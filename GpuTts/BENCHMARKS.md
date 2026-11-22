# GPU voice benchmarks

Measured on 22 Nov 2026 on an **NVIDIA GeForce RTX 3090 (24 GB, driver 610.88)**, Windows 11,
Python 3.11, through `magni_tts_server.py` exactly as MagniReader uses it: the text is split into
sentence chunks (first chunk ≤ 120 characters), each chunk is one `/synth` request, and the word
aligner runs on every chunk (OmniVoice and Qwen3 rows; it adds about 30–60 ms).

Test text: a three-sentence paragraph in English (~18 s of speech) and its Spanish translation
(~15 s). Each figure is the median of 3 runs after warm-up. Voices: the bundled reference clips
(`en_us_f1`, `es_castilian_m1`).

- **First audio**: time from the request until the first chunk's audio is ready, which is when
  MagniReader starts playing.
- **RTF** (real-time factor): generation time ÷ audio length. Below 1 is faster than real time;
  above 1 means gaps between sentences.
- **Castilian [θ] heard**: a phoneme recognizer (`facebook/wav2vec2-xlsr-53-espeak-cv-ft`) counts
  the [θ] sounds in the Spanish output. The text has 5 places where Spain says [θ] and Latin America
  says [s] (*cinco, vez, permanece, silencio, necesitas*).

| Model | First audio EN | First audio ES | RTF EN | RTF ES | VRAM peak (PyTorch) | VRAM used (whole process) | Castilian [θ] heard |
|---|---|---|---|---|---|---|---|
| **OmniVoice** (16 steps, fp16) | **1.03 s** | **1.04 s** | **0.18** | **0.22** | 5.4 GB | 6.2 GB | yes (6 of 5) |
| **Qwen3-TTS 0.6B Base** via faster-qwen3-tts (CUDA graphs) | 1.35 s | 1.10 s | 0.37 | 0.38 | 4.0 GB | 4.9 GB | yes (5 of 5) |
| Chatterbox Multilingual V3 + Spain-Spanish finetune | 3.46 s | 3.15 s | 1.09 | 1.09 | 7.3 GB | 7.7 GB | yes (6 of 5) |
| Qwen3-TTS 0.6B Base, stock `qwen-tts` | 10.3 s | 8.6 s | 3.38 | 2.91 | 3.7 GB | 4.7 GB | — |
| Qwen3-TTS 1.7B Base, stock `qwen-tts` | 11.0 s | 9.4 s | 3.43 | 3.60 | 4.4 GB | 5.4 GB | yes (5 of 5) |
| VoxCPM2 | — | — | — | — | — | — | crashed while loading inside the server; dropped |

**Fastest: OmniVoice** (about 5× faster than real time, first audio in ~1 s). Qwen3-TTS 0.6B is
second (about 2.7× faster than real time). Both are offered in MagniReader. The stock Qwen3-TTS
package runs one Hugging Face `generate()` call per audio frame and is ~3× *slower* than real time
on Windows; [faster-qwen3-tts](https://pypi.org/project/faster-qwen3-tts/) captures both decoders as
CUDA graphs and makes it ~9× faster. Chatterbox (Hugging Face Llama backend) is about real time, so
a reading would pause between sentences.

Cold start (server launch → ready, model already downloaded, measured through MagniReader's code):
OmniVoice 18.9 s, Qwen3 0.6B 25.2 s. During that time MagniReader reads with Kokoro or a Windows voice.

Install from scratch through Settings (uv + Python + PyTorch + model + GPU self-test, ~100 MB/s
connection): OmniVoice 4 min 49 s, Qwen3 0.6B 2 min 54 s (it reused the PyTorch download).

## Reproduce

```powershell
# one venv per model, from GpuTts\requirements\<env>.txt (see Core\GpuVoiceSpeaker.cs for the uv command)
python GpuTts\bench\bench.py omnivoice <venv>\Scripts\python.exe results
python GpuTts\bench\accent_check.py results\omnivoice_es.wav
python GpuTts\bench\report.py results
```
