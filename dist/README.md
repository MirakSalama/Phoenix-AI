# Phoenix AI portable preview

[Download for Windows x64](https://github.com/MirakSalama/Phoenix-AI/releases/tag/v0.1.0-preview.1).

1. Extract the Windows ZIP to a writable folder. Keep **Phoenix AI.exe**, **App**, and **Models** together.
2. In **Models**, open **Get-Model.cmd** and select **LFM2.5 230M chat**. Confirm the listed files to install the starter chat model.
3. For voice, run the helper again for **Parakeet dictation** and **Kokoro speech output**. These are separate downloads.
4. Open **Phoenix AI.exe**.

The preview includes CPU and Vulkan GPU runtimes. The NPU path requires first-generation AMD XDNA in a Ryzen AI Phoenix or Hawk Point system and a compatible AMD NPU driver.

The executable is unsigned. The archive contains no model weights or personal data. Model files come from the sources shown by the helper and are checked against their expected sizes and SHA-256 hashes.

Report your processor, Windows version, NPU driver version, and what worked or failed in the [testing discussion](https://github.com/MirakSalama/Phoenix-AI/discussions/4).
