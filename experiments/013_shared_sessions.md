# Experiment 013 — Shared model sessions on one GPU

One process loaded the official LFM2.5 230M Q4_K_M GGUF (153,406,304 bytes) on the RTX 4050 Laptop GPU through Vulkan. The run used NVIDIA driver 621.14 and llama.cpp build 12189 (`80d2c0c2c`). The server selected only that Vulkan adapter and requested offload of all model layers. [Model source](https://huggingface.co/LiquidAI/LFM2.5-230M-GGUF) · [Server documentation](https://github.com/ggml-org/llama.cpp/blob/b12189/tools/server/README.md)

Each configuration used a fresh server, continuous batching, 2,048 context tokens per slot, and separate slot state. After a short warm-up, the first workload submitted 16 jobs with 96 generated tokens each. The second workload repeated the same prompts and counted the same 1,536 output tokens with prompt reuse disabled. A separate set of 16 grammar-constrained multiple-choice questions checked answer stability. Configuration order rotated across three rounds.

| Slots | Warm tokens/s, aggregate | Warm batch | Median job time | Peak dedicated GPU memory |
|---:|---:|---:|---:|---:|
| 1 | 419.5 | 3.66 s | 230 ms | 216 MiB |
| 2 | 664.2 | 2.31 s | 293 ms | 241 MiB |
| 4 | 1,085.5 | 1.41 s | 354 ms | 289 MiB |
| 8 | 1,317.6 | 1.17 s | 581 ms | 382 MiB |

The table shows medians across three runs. GPU memory is the sampled peak dedicated allocation for the server process. Raw results also include peak process working set, sampled shared GPU memory, first-token timings, and the first workload timings. Slot polling observed all requested slots active in every warm run. [Raw results](013_shared_sessions_results.json)

Eight slots finished the warm batch 3.14× as fast as one. Four slots offered most of that gain with lower per-job latency. An initial pilot saw an approximately 8.5-second delay to the first token in the first eight-slot batch. The later three full runs had no comparable stall, so cold performance is less established. [Initial pilot results](013_shared_sessions_smoke.json)

All twelve runs gave the same answers on the choice set and scored 6/16. This verifies that the slot count did not change the answers on these fixtures. Answer quality for a team that shares evidence needs a separate test. The fixed-token benchmark forces generation past EOS to keep the work constant.

Try four slots for short interactive batches and eight for bulk independent jobs in a follow-up test with representative prompts. Compare a single answer with a small team that independently gathers evidence and checks it before synthesis.

## Reproduce

From the Phoenix AI project folder with the existing local model and runtime installed:

```powershell
python .\experiments\013_shared_sessions.py
```

Use `--device Vulkan1` to pin an adapter explicitly. Use `--output` to save a new report alongside the recorded run.
