"""Generates candidate neutral male reference clips with OmniVoice voice design and scores
them: median pitch (male range) and, for Spanish, the Castilian [θ] count."""
import json, sys
sys.stdout.reconfigure(encoding="utf-8")
import numpy as np, torch, librosa, soundfile as sf
from pathlib import Path
from omnivoice import OmniVoice
from transformers import Wav2Vec2ForCTC, Wav2Vec2Processor

out = Path(sys.argv[1]); out.mkdir(parents=True, exist_ok=True)
TEXT = {
    "en": "The weather today is mild and clear, with a light breeze from the west and temperatures around twenty degrees.",
    "es": "Hoy hace un día tranquilo en la ciudad; a las once y diez empieza la reunión en la plaza, junto al ayuntamiento.",
}
EXPECTED_THETA = 6  # hace, ciudad, once, diez, empieza, plaza
model = OmniVoice.from_pretrained("k2-fsa/OmniVoice", device_map="cuda", dtype=torch.float16)
proc = Wav2Vec2Processor.from_pretrained("facebook/wav2vec2-xlsr-53-espeak-cv-ft")
asr = Wav2Vec2ForCTC.from_pretrained("facebook/wav2vec2-xlsr-53-espeak-cv-ft").to("cuda").eval()

def phones(wav, sr):
    y = librosa.resample(wav, orig_sr=sr, target_sr=16000)
    with torch.inference_mode():
        ids = asr(torch.tensor(y, device="cuda")[None]).logits.argmax(-1)
    return proc.batch_decode(ids)[0]

res = []
for lang, instruct in (("en", "male, middle-aged, moderate pitch, american accent"), ("es", "male, middle-aged, moderate pitch")):
    for seed in range(int(sys.argv[2]) if len(sys.argv) > 2 else 12):
        torch.manual_seed(seed)
        a = model.generate(text=TEXT[lang], language=lang, instruct=instruct, num_step=32)[0]
        sr = model.sampling_rate
        f0, _, _ = librosa.pyin(librosa.resample(a, orig_sr=sr, target_sr=16000), fmin=60, fmax=400, sr=16000)
        p = phones(a, sr)
        r = {"lang": lang, "seed": seed, "f0": round(float(np.nanmedian(f0))), "dur": round(len(a) / sr, 2),
             "theta": p.count("θ"), "s": p.count("s"), "phones": p}
        sf.write(out / f"{lang}_seed{seed}.wav", a, sr)
        res.append(r)
        print(json.dumps({k: v for k, v in r.items() if k != "phones"}), flush=True)
(out / "candidates.json").write_text(json.dumps(res, ensure_ascii=False, indent=1), encoding="utf-8")
