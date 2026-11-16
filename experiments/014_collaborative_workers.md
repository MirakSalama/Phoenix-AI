# Experiment 014 — Four-device collaboration and relay

This experiment uses the installed Phoenix chat models and caps inference at four concurrent workers. d1 Omni 600M Q8 runs locally on the CPU before the chat workers start. Its structured role scores pick an assignment for each question. Relay roles use distinct devices. Parallel drafts use distinct devices and finish before synthesis.

| Lane | Same-model team | Mixed-model team |
|---|---|---|
| CPU | LFM2.5 230M | LFM2.5 350M |
| Phoenix NPU | LFM2.5 230M | LFM2.5 230M |
| Radeon iGPU | LFM2.5 230M | LFM3 1.7B |
| RTX 4050 dGPU | LFM2.5 230M | Gemma 4 E2B |

The NPU uses the portable Phoenix BF16 runtime through the native chat bridge. The other lanes use the local llama.cpp runtime and GGUF weights, with Vulkan on the two GPUs. The same-model round compares the same checkpoint family across different precisions and backends. CPU and GPU use Q4_K_M for both LFM2.5 checkpoints, QAD Q4_0 for LFM3, and Q4_0 for Gemma.

## Method

- **Independent jobs**. Four different prompts, one per device, each with a 96-output-token limit. The same jobs run serially and in parallel twice. The raw report includes submission and completion intervals for each worker.
- **Solo and teams**. Twelve multiple-choice fixtures cover arithmetic, ordering, eligibility, capacity, and simple code. Each workflow has a combined ceiling of 192 output tokens, including the verdict. Actual token counts vary because models can stop early.
- **Parallel collaboration**. Workers draft facts, a solution, a check, and an alternate approach independently. The final writer gets all four drafts and the original question.
- **Relay**. Facts, solution, check, and synthesis run in order. The next worker sees the original task and earlier notes. The final writer sees every handoff.
- **Scoring**. The writer selects a letter through the same constrained decoding step for every arm. This run restricts the writer to CPU or GPU backends, which support that shared scorer. Times for resident workers include inference and handoffs after model warm-up. d1 planning and model startup happen outside those times. Staged runs record per-task compute and the wall time for each full arm separately.

[Experiment script](014_collaborative_workers.py) · [Raw results](014_collaborative_workers_results.json)

## Results

Both rounds ran with all four models resident. The first same-model attempt staged one worker at a time because its 1,305 MiB usable host budget fell below the 1,715 MiB estimate. Its results remain under the staged section of the raw report. The resident rerun had a 4,164 MiB usable host budget. The resident mixed round had 5,429 MiB against an estimated 4,519 MiB.

| Independent batch | Tokens | Serial | Parallel | Speedup | Peak requests in flight |
|---|---:|---:|---:|---:|---:|
| Same model on all four devices | 384 | 3.00 s | 1.73 s | 1.74× | 4 |
| Mixed models on all four devices | 384 | 5.42 s | 2.52 s | 2.15× | 4 |

Each timing is the median of two batches. The measured overlap is at the request level. Every independent job generated all 96 requested tokens in both modes.

| Reasoning run | Correct | Median reply time | Median output tokens across stages |
|---|---:|---:|---:|
| Same model · LFM2.5 230M solo on dGPU | 3/12 | 0.27 s | 82 |
| Same model · four parallel drafts and synthesis | 2/12 | 0.96 s | 143 |
| Same model · four-device relay | 2/12 | 1.24 s | 103.5 |
| Mixed · Gemma E2B solo on dGPU | 8/12 | 2.14 s | 178 |
| Mixed · LFM3 1.7B solo on iGPU | 11/12 | 3.51 s | 178 |
| Mixed · four parallel drafts and synthesis | 2/12 | 1.70 s | 150 |
| Mixed · four-device relay | 3/12 | 3.07 s | 118.5 |
| Pilot · Gemma E2B solo on dGPU | 8/12 | 2.14 s | 178 |
| Pilot · LFM2.5 230M NPU and Gemma E2B dGPU | 5/12 | 1.59 s | 128 |

All resident parallel reasoning drafts overlapped within their respective cases. All relay stages ran in order on separate lanes. The synthesis and verdict still use the same worker after the draft or relay stages. Two Gemma solo cases in the mixed run took about 17 and 25 seconds, so the median omits substantial tail latency.

The two-model pilot repaired one case and lost four that Gemma got right on its own. In three losses the NPU checker proposed a wrong result. d1 selected LFM3 to write only one of 12 mixed parallel answers and three of 12 relay answers, although it was the best solo model on these fixtures. These results combine writer choice, the division of the token budget, and the content of handoffs. They cannot isolate the effect of each.

The independent workload is the strongest candidate for a four-worker scheduler. For reasoning, a follow-up should keep the best measured solo model as final writer and change only whether it receives verifiable evidence. Compare that against the same writer answering directly and give the final stage the same budget in both arms.

The worker processes exited after both resident runs. Windows showed about 5.2 GiB free after the final run.

## Reproduce

From the Phoenix AI project folder with the existing portable models and runtimes:

```powershell
& '.\dist\Phoenix-AI-Portable-win-x64\App\runtime\python\python.exe' -X utf8 '.\experiments\014_collaborative_workers.py' --output '.\cache\014_rerun.json'
```

Use --phases same or --phases mixed for one team, and add --bursts-only for just the independent workload. If the Windows host-memory estimate cannot admit all four models, the reasoning test loads one lane at a time and skips the independent-job speed comparison.
