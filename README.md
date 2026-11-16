# Phoenix AI

**Local AI that puts every chip to work.**

Phoenix AI is an experimental local Windows assistant for AMD Phoenix and Hawk Point laptops. It combines NPU chat and dictation with local speech synthesis and can schedule language-model work on the CPU, NPU, iGPU, and dGPU in the same turn.

## Experiments

The [experiment index](experiments/README.md) includes runnable probes, measurements, and notes from the original NPU bring-up through four-device scheduling. Recorded results include failed and inconclusive runs. Measurements apply to the listed hardware, models, and workloads.

- [All-NPU text generation](experiments/010_x8_engine.md) and [Parakeet dictation](experiments/012_all_npu_encoder.md)
- [Shared GPU sessions](experiments/013_shared_sessions.md) and [four-device parallel and relay work](experiments/014_collaborative_workers.md)
- [Four-worker workbook verification](experiments/022_workbook_verification.md), including the test files and prompt
- [Early desktop benchmark comparison](Bench_old_vs_new.png) across the four models and four devices

## Build and run

The Windows UI lives in `apps/npu_chat_winui`. Python runners, custom NPU kernels, benchmarks, and their setup notes live under `experiments`. `Run-Phoenix-AI.bat` starts the development app once its local runtime and base model are set up. See [NPU chat setup](experiments/010_handoff.md) for the development environment and [portable packaging](scripts/package-phoenix.ps1) for the Windows release layout.

Model weights, SDKs, built runtimes, caches, and local user data are kept outside the source snapshot. The current [model download helper](scripts/portable/Downloadables.md) covers optional CPU and GPU models. Matching AMD NPU programs, base weights, and speech assets must be prepared separately.

In **Models & Tools → Manage Models**, use **Check for updates** to compare chat models, D1, Parakeet, and Kokoro against publisher metadata. Phoenix reports differences and flags voice or NPU updates that need a new compatible bundle. Checking versions leaves installed files in place.

## Attribution

The early AMD NPU experiments and NPU Dictate code build on [Ryzen AI NPU Lab](https://github.com/AbhiramJayasankar/ryzenai-npu-lab/tree/a9531b8). Its copyright and license are preserved in [upstream notices](LICENSES/ryzenai-npu-lab.txt). See the [third-party notices](scripts/portable/Third-party%20notices.md) for packaged runtime and model dependencies.
