# Third-party components

- Liquid AI's [LFM2.5 230M](https://huggingface.co/LiquidAI/LFM2.5-230M) and [Q4_K_M GGUF](https://huggingface.co/LiquidAI/LFM2.5-230M-GGUF) are available under the LFM Open License. Phoenix includes separate compiled XDNA 1 programs for the BF16 checkpoint.
- Liquid AI's [d1-omni-600M Q8_0](https://huggingface.co/LiquidAI/d1-omni-600M-GGUF) decision model is available under the LFM Open License.
- [NVIDIA Parakeet TDT 0.6B v2](https://huggingface.co/nvidia/parakeet-tdt-0.6b-v2) and [Ivan Tupakov's ONNX conversion](https://huggingface.co/istupakov/parakeet-tdt-0.6b-v2-onnx) are licensed under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/). The [XDNA 1 model bundle](https://github.com/AbhiramJayasankar/ryzenai-npu-lab/releases/tag/model-v1) splits the ONNX encoder and repacks its weights. The CPU encoder is the upstream INT8 ONNX conversion.
- [Kokoro-82M](https://huggingface.co/hexgrad/Kokoro-82M) by hexgrad and the [Kokoro ONNX conversion](https://github.com/thewh1teagle/kokoro-onnx) are Apache-2.0. The original model card lists the contributors and attributed training audio.
- [llama.cpp b12189](https://github.com/ggml-org/llama.cpp/releases/tag/b12189) is distributed under the [MIT license](https://github.com/ggml-org/llama.cpp/blob/b12189/LICENSE). Its bundled OpenMP runtime notice is alongside the binary.
- AMD XRT's Python extension is accompanied by **XRT-LICENSE.txt**. The kernel driver is supplied by the system.
- The Python distributions and Python packages carry their license and notice files in their runtime folders. Kokoro uses [eSpeak NG](https://github.com/espeak-ng/espeak-ng), available under GPL-3.0 or later, through [espeakng-loader](https://github.com/thewh1teagle/espeakng-loader). Those projects provide the corresponding source.
- The app includes the Microsoft .NET and Windows App SDK runtimes.
- [ZXing.Net](https://github.com/micjahn/ZXing.Net) generates QR codes locally. Its Apache-2.0 license is in **ZXing-LICENSE.txt**.
- pypdf 6.21.4 extracts selectable PDF text locally. The distribution’s BSD-3-Clause license is included inside **experiments/attachment-pypdf.zip**.
