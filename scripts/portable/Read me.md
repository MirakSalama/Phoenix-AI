# Phoenix AI for Windows x64

Open **Phoenix AI.exe** beside **App** and **Models**. Keep the three items together in a writable location. The flat runtime opens from **PhoenixAI.exe**.

LFM2.5 230M is the included chat model. Auto prefers the compatible AMD XDNA 1 NPU, then a Vulkan GPU, then the CPU. NPU acceleration requires the AMD driver for Ryzen 7040/8040 Phoenix or Hawk Point. The precompiled programs are included.

Parakeet TDT v2 handles English speech input. It uses the NPU encoder on supported hardware and has an included CPU encoder for other PCs. Kokoro synthesizes replies locally and includes both voice choices, pace, and pitch controls. Chat and both voice models work offline.

Find the CPU-based **Decision router** at the top of **Settings**. Open **Models → Get-Model.cmd** to download or import optional models. The flat runtime keeps this script under **Downloadables**. Chats, keys, memory, and voice preferences are kept under the Windows user's local app data folder.
