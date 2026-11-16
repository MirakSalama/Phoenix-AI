# Phoenix AI — Local chat on AMD XDNA 1

**Local AI that puts every chip to work.**

Phoenix AI is an experimental **local AI chat and voice app for Windows** built around the **first-generation AMD XDNA 1 NPU** in Ryzen AI Phoenix and Hawk Point laptops. It started as a way to put an overlooked, “orphaned” laptop NPU to work.

**Try it on Windows x64.** [Download the portable preview](https://github.com/MirakSalama/Phoenix-AI/releases/tag/v0.1.0-preview.1) and follow the [first-run instructions](dist/README.md). Download chat and voice models separately using the included helper. [Share your hardware and test results](https://github.com/MirakSalama/Phoenix-AI/discussions/4).

## AMD Ryzen AI and XDNA 1 support

- **Local LLM chat on the NPU.** Phoenix runs LFM2.5 230M on AMD XDNA 1 using custom IRON and XRT programs.
- **Local voice.** Parakeet handles speech-to-text with an XDNA 1 encoder or a CPU fallback. Kokoro handles text-to-speech on the CPU.
- **CPU and GPU workers.** Phoenix can combine work across the NPU, CPU, integrated GPU, and discrete GPU in one turn. Phoenix Breath shows how much room remains in each active worker’s context.

The experiment reports document the model, backend, and test hardware for each run. See [AMD’s Ryzen AI support notes](https://ryzenai.docs.amd.com/en/latest/relnotes.html) for supported processor and driver combinations.

## Experiments

The [experiment index](experiments/README.md) includes runnable probes, measurements, and notes from the original NPU bring-up through four-device scheduling. Recorded results include failed and inconclusive runs. Measurements apply to the listed hardware, models, and workloads.

- [All-NPU text generation](experiments/010_x8_engine.md) and [Parakeet dictation](experiments/012_all_npu_encoder.md)
- [Shared GPU sessions](experiments/013_shared_sessions.md) and [four-device parallel and relay work](experiments/014_collaborative_workers.md)
- [Four-worker workbook verification](experiments/022_workbook_verification.md), including the test files and prompt
- [Early desktop benchmark comparison](Bench_old_vs_new.png) across the four models and four devices

## Build and run

The Windows UI lives in `apps/npu_chat_winui`. Python runners, custom NPU kernels, benchmarks, and their setup notes live under `experiments`. `Run-Phoenix-AI.bat` starts the development app once its local runtime and base model are set up. See [NPU chat setup](experiments/010_handoff.md) for the development environment and [portable packaging](scripts/package-phoenix.ps1) for the Windows release layout.

Model weights, SDKs, built runtimes, caches, and local user data are kept outside the source snapshot. The [model download helper](scripts/portable/Downloadables.md) installs starter chat and voice assets and optional models. The portable preview includes the compiled XDNA 1 programs for LFM2.5 230M. Other models need matching NPU programs and checkpoints to run on the NPU.

In **Models & Tools → Manage Models**, use **Check for updates** to compare chat models, D1, Parakeet, and Kokoro against publisher metadata. Phoenix reports differences and flags voice or NPU updates that need a new compatible bundle. Checking versions leaves installed files in place.

## Attribution

The early AMD NPU experiments and NPU Dictate code build on [Ryzen AI NPU Lab](https://github.com/AbhiramJayasankar/ryzenai-npu-lab/tree/a9531b8). Its copyright and license are preserved in [upstream notices](LICENSES/ryzenai-npu-lab.txt). See the [third-party notices](scripts/portable/Third-party%20notices.md) for packaged runtime and model dependencies.
