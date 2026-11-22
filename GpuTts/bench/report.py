"""Builds the benchmark table (markdown) from results/*.json."""
import json, statistics, sys
from pathlib import Path

res = Path(sys.argv[1])
order = ["omnivoice", "qwen3-0.6b", "chatterbox", "qwen3-1.7b"]
names = {"omnivoice": "OmniVoice", "chatterbox": "Chatterbox V3 + es-ES", "qwen3-0.6b": "Qwen3-TTS 0.6B Base",
         "qwen3-1.7b": "Qwen3-TTS 1.7B Base", "voxcpm2": "VoxCPM2"}
accent = json.loads((res / "accent.json").read_text()) if (res / "accent.json").exists() else {}
rows = []
for e in order:
    f = res / f"{e}.json"
    if not f.exists():
        continue
    d = json.loads(f.read_text())
    def med(lang, k):
        return statistics.median(r[k] for r in d["runs"][lang])
    rows.append((names[e], med("en", "ttfa_s"), med("es", "ttfa_s"), med("en", "rtf"), med("es", "rtf"),
                 d["vram"]["torch_peak_mb"], d["vram"]["gpu_used_delta_mb"], d["load_ms"] / 1000, accent.get(e, "")))
print("| Model | First audio EN | First audio ES | RTF EN | RTF ES | VRAM peak (PyTorch) | VRAM used (whole process) | Load + warm-up | Castilian [θ] heard |")
print("|---|---|---|---|---|---|---|---|---|")
for r in rows:
    print(f"| {r[0]} | {r[1]:.2f} s | {r[2]:.2f} s | {r[3]:.2f} | {r[4]:.2f} | {r[5]/1024:.1f} GB | {r[6]/1024:.1f} GB | {r[7]:.0f} s | {r[8]} |")
