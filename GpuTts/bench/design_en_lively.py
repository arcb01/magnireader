import json, sys
sys.stdout.reconfigure(encoding="utf-8")
import numpy as np, torch, librosa, soundfile as sf
from pathlib import Path
from omnivoice import OmniVoice
from transformers import Wav2Vec2ForCTC, Wav2Vec2Processor
out = Path(sys.argv[1]); out.mkdir(parents=True, exist_ok=True)
TEXT = "Guess what? The new library downtown finally opened today, and honestly, it's even better than I expected!"
model = OmniVoice.from_pretrained("k2-fsa/OmniVoice", device_map="cuda", dtype=torch.float16)
proc = Wav2Vec2Processor.from_pretrained("facebook/wav2vec2-xlsr-53-espeak-cv-ft")
asr = Wav2Vec2ForCTC.from_pretrained("facebook/wav2vec2-xlsr-53-espeak-cv-ft").to("cuda").eval()
res = []
for instruct in ("male, young adult, moderate pitch, american accent", "male, middle-aged, moderate pitch, american accent"):
    for seed in range(16):
        torch.manual_seed(seed)
        a = model.generate(text=TEXT, language="en", instruct=instruct, num_step=32)[0]
        y = librosa.resample(a, orig_sr=model.sampling_rate, target_sr=16000)
        f0, voiced, _ = librosa.pyin(y, fmin=60, fmax=350, sr=16000)
        f = f0[~np.isnan(f0)]
        semis = 12 * np.log2(f / np.median(f))
        with torch.inference_mode():
            p = proc.batch_decode(asr(torch.tensor(y, device="cuda")[None]).logits.argmax(-1))[0]
        r = {"instruct": instruct.split(",")[1].strip(), "seed": seed, "f0": round(float(np.median(f))),
             "range_st": round(float(np.percentile(semis, 95) - np.percentile(semis, 5)), 1),
             "dur": round(len(a) / model.sampling_rate, 2), "phones": p}
        name = f"en_{'young' if 'young' in instruct else 'mid'}_{seed}.wav"
        sf.write(out / name, a, model.sampling_rate); r["file"] = name
        res.append(r)
        print(json.dumps({k: v for k, v in r.items() if k != "phones"}), flush=True)
(out / "lively.json").write_text(json.dumps(res, ensure_ascii=False, indent=1), encoding="utf-8")
