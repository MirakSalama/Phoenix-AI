# Optional models

Run **Get-Model.cmd**, choose a model, and review the file name, source, and full size before approving. Phoenix verifies the publisher checksum and size before making the file available. Restart Phoenix after installation.

| Model | Size | Publisher |
| --- | ---: | --- |
| LFM2.5 350M Q4_K_M | 229 MB | [Liquid AI](https://huggingface.co/LiquidAI/LFM2.5-350M-GGUF) |
| LFM3 1.7B QAD Q4_0 | 1.05 GB | [Liquid AI](https://huggingface.co/LiquidAI/LFM3-1.7B-GGUF) |
| Qwen 3.5 2B Q4_K_M | 1.27 GB | [LM Studio community conversion](https://huggingface.co/lmstudio-community/Qwen3.5-2B-GGUF) |
| Gemma 4 E2B Q4_0 (manual choice) | 2.84 GB | [GGML community conversion](https://huggingface.co/ggml-org/gemma-4-E2B-it-GGUF) |
| d1-omni-600M Q8_0 | 407 MB | [Liquid AI](https://huggingface.co/LiquidAI/d1-omni-600M-GGUF) |

These GGUF builds run on the CPU or a Vulkan GPU. The base package contains the XDNA 1 programs for LFM2.5 230M. Parakeet and Kokoro are included in the base package.

Phoenix uses d1-omni-600M on the CPU to choose between a local reply, document lookup, and web search. Find its toggle under Settings. The chat model is selected separately.

To reuse a matching file you already have, run for example:

    Get-Model.cmd -Model lfm3 -FromFile "D:\Models\LFM3-1.7B-QAD-Q4_0.gguf"
