"""Compare independent, collaborative and relay inference on Phoenix's four devices."""

import argparse
from concurrent.futures import ThreadPoolExecutor
from contextlib import ExitStack, contextmanager
from dataclasses import dataclass
from datetime import datetime, timezone
from itertools import permutations
import json
from math import log, isfinite
import os
from pathlib import Path
from queue import Empty, Queue
import secrets
import socket
from statistics import median
import subprocess
import sys
from tempfile import TemporaryFile
from threading import Thread
from time import monotonic, sleep
from urllib.error import URLError
from urllib.request import ProxyHandler, Request, build_opener


ROOT = Path(__file__).resolve().parents[1]
BUDGET = 192
LABELS = "ABCD"
ROLES = {
    "facts": "Extract the relevant facts, inputs, and constraints. Point out easily missed details.",
    "solve": "Work out the solution and show only the important calculations or logic.",
    "check": "Check the arithmetic, logic and boundary cases. Flag mistakes rather than repeating the answer.",
    "alternate": "Try a genuinely different approach. Identify the strongest alternative and how to rule it out.",
    "final": "Reconcile the evidence and write a short reasoned conclusion. Verify claims against the question.",
}
TASKS = (
    ("cost", "A repair uses four brackets at $18 each and three fasteners at $7 each. A $10 credit is applied. What is the final cost?", "$83", ("$73", "$93", "$81")),
    ("time", "A job starts at 09:20. Setup takes 35 minutes, the required pause takes 10 minutes, and the last stage takes 25 minutes. When does it finish?", "10:30", ("10:20", "10:25", "10:35")),
    ("stock", "You need six kits with four batteries each. You have nine batteries and can buy only packs of five. What is the fewest packs to buy?", "3", ("2", "4", "5")),
    ("ordering", "P comes before Q. R comes after Q. S comes before P. Which ordering satisfies every rule?", "S P Q R", ("P S Q R", "S P R Q", "S R P Q")),
    ("pricing", "Plan A charges $20 plus $1.40 per unit. Plan B charges $32 plus $0.80 per unit. At 30 units, which plan is cheaper, and by how much?", "B by $6", ("A by $6", "B by $12", "Both cost the same")),
    ("policy", "A return qualifies if it has a receipt and is at most 14 days old, or if the item is defective and at most 30 days old. This item is defective, 20 days old, and has no receipt. Is it eligible?", "Yes, under the defective-item rule", ("No, because it has no receipt", "No, because it exceeds 14 days", "Only if both rules are satisfied")),
    ("code", "Start with x = 2. For each n in [3, 4, 5], add n to x if n is odd, otherwise double x. What is x after the loop?", "15", ("14", "19", "23")),
    ("capacity", "Room A seats 8 and is free after 14:30. B seats 6 and is free after 14:00. C seats 12 and is free after 15:00. Seven people need a room at 14:45. Which room works?", "A only", ("B only", "C only", "A and C")),
    ("percentage", "One batch contains 20 parts with two defects. A second batch contains 80 parts with four defects. What percentage of all parts are defective?", "6%", ("7.5%", "10%", "15%")),
    ("transfer", "Copying 90 MB at a constant 3 MB per second takes an extra 8 seconds of setup. How many seconds does the whole operation take?", "38", ("30", "33", "35")),
    ("threshold", "Free shipping applies to orders of at least $50 after discounts and before tax. A $55 order gets a $7 discount and adds $4 tax. Does it get free shipping?", "No, because the qualifying amount is $48", ("Yes, because the total is $52", "Yes, because the original subtotal was $55", "No, because tax is always excluded from orders")),
    ("units", "A 2.4-liter tank is initially one-quarter full. Add 750 mL and then use 300 mL. How much liquid remains?", "1.05 L", ("0.45 L", "0.90 L", "1.35 L")),
)


@dataclass(frozen=True)
class Target:
    lane: str
    model: str
    adapter: str
    description: str


def fixtures(count):
    for i, (name, question, answer, distractors) in enumerate(TASKS[:count]):
        correct = (i * 3 + 1) % 4
        options = list(distractors)
        options.insert(correct, answer)
        text = question + "\n" + "\n".join(f"{label}. {choice}" for label, choice in zip(LABELS, options))
        yield name, text, LABELS[correct]


def local_port():
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def terminate(process):
    if process.poll() is None:
        process.terminate()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait()


@contextmanager
def decision_server(server, model):
    port, key = local_port(), secrets.token_urlsafe(24)
    base = f"http://127.0.0.1:{port}"
    opener = build_opener(ProxyHandler({}))
    headers = {"Authorization": f"Bearer {key}", "Content-Type": "application/json"}

    def request(path, body=None, timeout=8):
        data = json.dumps(body).encode() if body is not None else None
        with opener.open(Request(base + path, data, headers), timeout=timeout) as response:
            return json.load(response)

    with TemporaryFile() as errors:
        process = subprocess.Popen([
            str(server), "--model", str(model), "--device", "none", "--n-gpu-layers", "0",
            "--ctx-size", "4096", "--batch-size", "4096", "--ubatch-size", "4096", "--parallel", "1",
            "--host", "127.0.0.1", "--port", str(port), "--api-key", key, "--offline", "--no-ui",
        ], stdin=subprocess.DEVNULL, stdout=errors, stderr=errors, creationflags=subprocess.CREATE_NO_WINDOW)
        try:
            until = monotonic() + 15
            while monotonic() < until and process.poll() is None:
                try:
                    if request("/health", timeout=1).get("status") == "ok":
                        break
                except (URLError, TimeoutError):
                    pass
                sleep(0.12)
            else:
                raise RuntimeError("d1 did not become ready.")
            yield request
        finally:
            terminate(process)


def role_plan(request, question, targets, mode):
    roles = ["facts", "check"] if mode == "pilot" else ["facts", "solve", "check"]
    if mode == "collaborate":
        roles.append("alternate")
    roles.append("final")
    descriptions = {target.lane: target.description for target in targets}
    questions = {
        role: {
            "type": "choice",
            "instructions": f"Which available worker best handles this role? {ROLES[role]}",
            "criteria": {lane: desc for lane, desc in descriptions.items() if role != "final" or lane != "NPU"},
        }
        for role in roles
    }
    answer = request("/v1/systemone", {"state": question, "questions": questions})["answers"]
    probabilities = {role: answer[role]["probabilities"] for role in roles}
    lanes = [target.lane for target in targets]
    # Parallel drafts occupy distinct lanes. Their writer may reuse a lane after the barrier.
    unique_roles = roles if mode == "relay" else roles[:-1]
    assignments = permutations(lanes, len(unique_roles))
    candidates = (dict(zip(unique_roles, allocation)) for allocation in assignments)
    if mode != "relay":
        candidates = ({**plan, "final": final} for plan in candidates for final in probabilities["final"])

    def score(plan):
        return sum(log(max(1e-9, probabilities[role].get(lane, 0))) for role, lane in plan.items())

    selected = max((plan for plan in candidates if plan["final"] in probabilities["final"]), key=score)
    return {"lanes": selected, "probabilities": probabilities}


class NpuWorker:
    def __init__(self, python, portable):
        self.events = Queue()
        self.request_id = 0
        self.errors = TemporaryFile()
        env = os.environ.copy()
        env.update(
            PHOENIX_NPU_PROGRAMS=str(portable / "App/cache/phoenix-portable-programs"),
            NPU_CHAT_X8_MODEL="lfm25-230m",
        )
        try:
            self.process = subprocess.Popen(
                [str(python), "-X", "utf8", str(ROOT / "experiments/winui_bridge.py"), "--benchmark"],
                stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=self.errors, text=True, encoding="utf-8",
                env=env, creationflags=subprocess.CREATE_NO_WINDOW)
        except BaseException:
            self.errors.close()
            raise
        Thread(target=self.read, daemon=True).start()
        try:
            ready = self.wait("ready", None)
            if not ready.get("npu"):
                raise RuntimeError("Phoenix NPU did not initialize.")
        except BaseException:
            self.close()
            raise

    def read(self):
        try:
            for line in self.process.stdout:
                try:
                    self.events.put(json.loads(line))
                except json.JSONDecodeError:
                    continue
        finally:
            self.events.put(None)

    def wait(self, expected, request_id):
        until = monotonic() + 90
        while monotonic() < until:
            try:
                event = self.events.get(timeout=max(0, until - monotonic()))
            except Empty:
                break
            if event is None:
                raise RuntimeError("The NPU worker exited.")
            if event["kind"] == "fatal" or event["kind"] == "error" and event.get("id") == request_id:
                raise RuntimeError(event["text"])
            if event["kind"] == expected and (request_id is None or event.get("id") == request_id):
                return event
        raise TimeoutError(f"NPU worker timed out waiting for {expected}.")

    def respond(self, messages, limit):
        self.request_id += 1
        self.process.stdin.write(json.dumps({"kind": "chat", "id": self.request_id,
                                            "messages": messages, "max_tokens": limit}) + "\n")
        self.process.stdin.flush()
        return self.wait("reply", self.request_id)

    def close(self):
        terminate(self.process)
        self.process.stdin.close()
        self.process.stdout.close()
        self.errors.close()


def message(prompt):
    return [{"role": "user", "content": prompt}]


def run_stage(workers, lane, role, question, limit, notes=""):
    prompt = f"{ROLES[role]} Be concise.\n\nTask:\n{question}"
    if notes:
        prompt += "\n\nEarlier workers' notes. Check them against the task before using them.\n" + notes
    start = monotonic()
    result = workers[lane].respond(message(prompt), limit)
    return {"lane": lane, "role": role, "budget": limit, "start": start, "end": monotonic(),
            "output_tokens": result["generated_tokens"], "input_tokens": result["prompt_tokens"],
            "notes": result["response"]}


def verdict(worker, question, notes):
    prompt = f"{question}\n\nReasoning to consider and check:\n{notes}\nChoose the best answer. Reply with its letter only."
    start = monotonic()
    with worker.request("/v1/chat/completions", {
        "messages": message(prompt), "max_tokens": 16, "temperature": 0, "cache_prompt": False,
        "grammar": "root ::= [ABCD]", "reasoning_effort": "none",
        "chat_template_kwargs": {"enable_thinking": False},
    }) as response:
        result = json.load(response)
    answer = (result["choices"][0]["message"].get("content") or "").strip()
    return answer, {
        "role": "verdict", "budget": 16, "start": start, "end": monotonic(),
        "output_tokens": result["usage"]["completion_tokens"], "input_tokens": result["usage"]["prompt_tokens"],
    }


def notes_from(stages):
    return "\n".join(f"{stage['role']} ({stage['lane']}): {stage['notes'][:640]}" for stage in stages)


def measure(workers, question, correct, mode, plan):
    start = monotonic()
    lane = plan["final"]
    if mode == "solo":
        stages = [run_stage(workers, lane, "final", question, 176)]
    elif mode == "relay":
        stages = []
        for role in ("facts", "solve", "check"):
            stages.append(run_stage(workers, plan[role], role, question, 40, notes_from(stages[-2:])))
        stages.append(run_stage(workers, lane, "final", question, 56, notes_from(stages)))
    else:
        draft_roles = [role for role in plan if role != "final"]
        with ThreadPoolExecutor(max_workers=len(draft_roles)) as pool:
            pending = [pool.submit(run_stage, workers, plan[role], role, question, 56 if mode == "pilot" else 32)
                       for role in draft_roles]
            stages = [task.result() for task in pending]
        stages.append(run_stage(workers, lane, "final", question, 64 if mode == "pilot" else 48, notes_from(stages)))
    answer, vote = verdict(workers[lane], question, stages[-1]["notes"])
    stages.append({"lane": lane, **vote})
    return result_row(answer, correct, stages, start, monotonic() - start)


def result_row(answer, correct, stages, start, elapsed):
    assert sum(stage["budget"] for stage in stages) == BUDGET
    return {"answer": answer, "expected": correct, "correct": answer == correct, "valid": answer in list(LABELS),
            "seconds": elapsed, "output_tokens": sum(stage["output_tokens"] for stage in stages),
            "stages": [{**stage, "start": stage["start"] - start, "end": stage["end"] - start} for stage in stages]}


def burst(workers, simultaneous):
    prompts = {
        "CPU": "Describe how to organize a small workshop.",
        "NPU": "Describe how to back up personal files.",
        "iGPU": "Describe how to label storage boxes.",
        "dGPU": "Describe how to plan a group picnic.",
    }

    def job(lane):
        start = monotonic()
        reply = workers[lane].respond(message(prompts[lane] + " Write twelve detailed tips."), 96)
        return {"lane": lane, "output_tokens": reply["generated_tokens"], "start": start, "end": monotonic()}

    start = monotonic()
    if simultaneous:
        with ThreadPoolExecutor(max_workers=4) as pool:
            jobs = list(pool.map(job, prompts))
    else:
        jobs = [job(lane) for lane in prompts]
    elapsed = monotonic() - start
    peak = max(sum(job["start"] <= at < job["end"] for job in jobs) for at in (job["start"] for job in jobs))
    return {"seconds": elapsed, "output_tokens": sum(job["output_tokens"] for job in jobs), "peak_in_flight": peak,
            "jobs": [{**job, "start": job["start"] - start, "end": job["end"] - start} for job in jobs]}


def save(path, report):
    pending = path.with_suffix(path.suffix + ".partial")
    pending.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    pending.replace(path)


def staged_measure(cases, mode, plans, targets, open_worker):
    """Preserve dependencies and evidence while admitting one resident worker at a time."""
    stages = {name: [] for name, _, _ in cases}
    answers = {}
    solo = mode in ("solo", "solo-iGPU")
    relay = mode == "relay"
    roles = ["final"] if solo else ["facts", "solve", "check"]
    if not solo:
        if not relay:
            roles.append("alternate")
        roles.append("final")
    for role in roles:
        for lane, target in targets.items():
            jobs = [(name, question) for name, question, _ in cases if plans[name][role] == lane]
            if not jobs:
                continue
            with open_worker(target) as worker:
                for name, question in jobs:
                    prior = stages[name]
                    notes = notes_from(prior if role == "final" else prior[-2:]) if relay or role == "final" else ""
                    limit = (176 if solo else 56 if relay else 48) if role == "final" else (40 if relay else 32)
                    result = run_stage({lane: worker}, lane, role, question, limit, notes)
                    prior.append(result)
                    if role == "final":
                        answer, vote = verdict(worker, question, result["notes"])
                        answers[name] = answer
                        prior.append({"lane": lane, **vote})
    for name, _, correct in cases:
        prior = stages[name]
        yield {"case": name, "mode": mode, "plan": plans[name], **result_row(
            answers[name], correct, prior, prior[0]["start"], sum(stage["end"] - stage["start"] for stage in prior))}


def memory_fit(targets, server, model_root, model_files, devices, estimate):
    amounts = {}
    for target in targets:
        if target.lane == "NPU":
            # Portable XRT uses both mapped weights and the packed device buffers.
            amounts[target.lane] = 768
            continue
        path = model_root / target.model / model_files[target.model]
        placement = ["--model", str(path), "--device", target.adapter, "--split-mode", "none",
                     "--n-gpu-layers", "0" if target.lane == "CPU" else "all", "--fit", "off",
                     "--parallel", "1", "--batch-size", "512", "--ubatch-size", "512"]
        memory = estimate(server.with_name("llama-fit-params.exe"), placement, 2048)
        amounts[target.lane] = memory["Host"] + 96 + (memory[target.adapter] if target.lane == "iGPU" else 0)
        if target.lane == "dGPU" and memory[target.adapter] + 384 > devices[target.adapter]["FreeMiB"]:
            raise RuntimeError("The dGPU lacks dedicated memory for the requested model.")
    return amounts


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cases", type=int, default=len(TASKS), choices=range(1, len(TASKS) + 1))
    parser.add_argument("--phases", nargs="+", choices=("pilot", "same", "mixed"), default=["pilot", "same", "mixed"])
    parser.add_argument("--output", type=Path, default=Path(__file__).with_name("014_collaborative_workers_results.json"))
    parser.add_argument("--resume", action="store_true", help="Keep completed phases already in the output.")
    parser.add_argument("--bursts-only", action="store_true",
                        help="Measure four independent jobs serially and in parallel when all models fit.")
    args = parser.parse_args()
    portable = ROOT / "dist/Phoenix-AI-Portable-win-x64"
    python = portable / "App/runtime/python/python.exe"
    model_root = portable / "Models"
    os.environ["PHOENIX_MODEL_DIR"] = str(model_root)
    from gguf_context import estimate, host_budget
    from llama_vulkan_chat import MODELS, SERVER, VulkanChat, discover_devices

    devices = discover_devices()
    igpu = max((gpu for gpu in devices if gpu["Integrated"]), key=lambda gpu: gpu["FreeMiB"])
    dgpu = max((gpu for gpu in devices if not gpu["Integrated"]), key=lambda gpu: gpu["FreeMiB"])
    d1 = model_root / "d1-omni-600m/d1-omni-600M-Q8_0.gguf"
    if not python.is_file() or not (args.bursts_only or d1.is_file()):
        parser.error("The installed portable Python runtime and d1 weights are required.")
    cases = list(fixtures(args.cases))
    same = [
        Target("CPU", "lfm25-230m", "none", "LFM2.5 230M on CPU. Short extraction and simple calculations."),
        Target("NPU", "lfm25-230m", "NPU", "LFM2.5 230M on NPU. Fast short extraction and routine tasks."),
        Target("iGPU", "lfm25-230m", igpu["Id"], "LFM2.5 230M on Radeon. Brief checking and alternate approaches."),
        Target("dGPU", "lfm25-230m", dgpu["Id"], "LFM2.5 230M on RTX 4050. Fast generation and final synthesis."),
    ]
    mixed = [
        Target("CPU", "lfm25-350m", "none", "LFM2.5 350M on CPU. Compact model for fact extraction and straightforward checking."),
        same[1],
        Target("iGPU", "lfm3-1.7b", igpu["Id"], "LFM3 1.7B on Radeon. Stronger technical reasoning and independent checks."),
        Target("dGPU", "gemma4-e2b", dgpu["Id"], "Gemma 4 E2B on RTX 4050. Larger model for reasoning, checking or final synthesis."),
    ]
    groups = {"pilot": [same[1], mixed[3]], "same": same, "mixed": mixed}
    report = {"started_utc": datetime.now(timezone.utc).isoformat(), "output_budget": BUDGET,
              "devices": devices, "decision_model": d1.name, "cases": len(cases), "phases": {}}
    if args.resume:
        previous = json.loads(args.output.read_text(encoding="utf-8"))
        if previous["cases"] != len(cases) or previous["output_budget"] != BUDGET or previous["decision_model"] != d1.name:
            parser.error("The previous report used different fixtures, token budget or router.")
        report = previous

    @contextmanager
    def open_worker(target):
        started = monotonic()
        worker = NpuWorker(python, portable) if target.lane == "NPU" else VulkanChat(
            target.model, target.adapter, target.lane, context_limit=2048)
        try:
            worker.respond(message("Say hello."), 8)
            print(f"{target.lane}/{target.model} ready in {monotonic() - started:.1f}s", flush=True)
            yield worker
        finally:
            worker.close()

    for phase in args.phases:
        if args.bursts_only:
            if phase == "pilot":
                continue
            targets = groups[phase]
            estimates = memory_fit(targets, SERVER, model_root, MODELS, {gpu["Id"]: gpu for gpu in devices}, estimate)
            headroom = host_budget(False)
            entry = {"targets": [vars(target) for target in targets], "host_budget_mib": headroom,
                     "estimated_host_mib": estimates, "bursts": []}
            report.setdefault("independent_bursts", {})[phase] = entry
            if headroom < sum(estimates.values()):
                entry["skipped"] = "Insufficient host memory for four resident workers."
                print(f"{phase}: need about {sum(estimates.values())} MiB, have {headroom} MiB", flush=True)
            else:
                with ExitStack() as stack:
                    workers = {target.lane: stack.enter_context(open_worker(target)) for target in targets}
                    entry["bursts"] = [{"simultaneous": concurrent, **burst(workers, concurrent)}
                                       for concurrent in (False, True, True, False)]
                for result in entry["bursts"]:
                    print(f"{phase}: {'parallel' if result['simultaneous'] else 'serial'}, "
                          f"{result['seconds']:.2f}s, peak {result['peak_in_flight']} in flight", flush=True)
            save(args.output, report)
            continue
        previous = report["phases"].get(phase)
        if previous and len(previous["runs"]) == len(cases) * {"pilot": 2, "same": 3, "mixed": 4}[phase]:
            continue
        targets = groups[phase]
        print(f"{phase}: d1 assigning roles to {', '.join(f'{t.model}/{t.lane}' for t in targets)}", flush=True)
        workflows = ["pilot"] if phase == "pilot" else ["collaborate", "relay"]
        with decision_server(SERVER, d1) as route:
            plans = {mode: {name: role_plan(route, question, targets, mode) for name, question, _ in cases}
                     for mode in workflows}
        estimates = memory_fit(targets, SERVER, model_root, MODELS, {gpu["Id"]: gpu for gpu in devices}, estimate)
        headroom = host_budget(False)
        resident = phase == "pilot" or headroom >= sum(estimates.values())
        phase_result = {"targets": [vars(target) for target in targets], "plans": plans, "runs": [], "bursts": [],
                        "residency": "concurrent" if resident else "staged",
                        "host_budget_mib": headroom, "estimated_host_mib": estimates}
        report["phases"][phase] = phase_result
        save(args.output, report)
        if resident:
            with ExitStack() as stack:
                workers = {target.lane: stack.enter_context(open_worker(target)) for target in targets}
                if phase != "pilot":
                    phase_result["bursts"] = [{"simultaneous": concurrent, **burst(workers, concurrent)}
                                               for concurrent in (False, True, True, False)]
                    save(args.output, report)
                for index, (name, question, correct) in enumerate(cases):
                    arms = [("solo", {"final": "dGPU"})] + [(mode, plans[mode][name]["lanes"]) for mode in workflows]
                    if phase == "mixed":
                        arms.insert(1, ("solo-iGPU", {"final": "iGPU"}))
                    offset = index % len(arms)
                    for arm, plan in arms[offset:] + arms[:offset]:
                        result = measure(workers, question, correct, "solo" if arm == "solo-iGPU" else arm, plan)
                        phase_result["runs"].append({"case": name, "mode": arm, "plan": plan, **result})
                        print(f"{phase}/{arm} {name}: {result['answer']} vs {correct}, {result['seconds']:.2f}s", flush=True)
                        save(args.output, report)
        else:
            print(f"{phase}: need approximately {sum(estimates.values())} MiB host budget, have {headroom} MiB. "
                  "Running the same reasoning workflows one resident model at a time.", flush=True)
            phase_result["arm_wall_seconds"] = {}
            arms = ["solo", "solo-iGPU", *workflows] if phase == "mixed" else ["solo", *workflows]
            for arm in arms:
                started = monotonic()
                selection = ({name: {"final": "iGPU" if arm == "solo-iGPU" else "dGPU"} for name, _, _ in cases}
                             if arm.startswith("solo") else {name: plan["lanes"] for name, plan in plans[arm].items()})
                phase_result["runs"].extend(staged_measure(
                    cases, arm, selection, {target.lane: target for target in targets}, open_worker))
                phase_result["arm_wall_seconds"][arm] = monotonic() - started
                save(args.output, report)
        for arm in dict.fromkeys(row["mode"] for row in phase_result["runs"]):
            rows = [row for row in phase_result["runs"] if row["mode"] == arm]
            print(f"{phase}/{arm}: {sum(row['correct'] for row in rows)}/{len(rows)}, "
                  f"median {median(row['seconds'] for row in rows):.2f}s", flush=True)


if __name__ == "__main__":
    main()
