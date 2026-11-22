"""Castilian check: Spain Spanish pronounces c/z as [θ] (distinción); Latin American
Spanish says [s] (seseo). A phoneme recognizer counts the [θ] it hears.

    python accent_check.py file1.wav file2.wav ...
"""
import sys
sys.stdout.reconfigure(encoding="utf-8")
import librosa
import torch
from transformers import Wav2Vec2ForCTC, Wav2Vec2Processor

MODEL = "facebook/wav2vec2-xlsr-53-espeak-cv-ft"
proc = Wav2Vec2Processor.from_pretrained(MODEL)
model = Wav2Vec2ForCTC.from_pretrained(MODEL).eval()
for path in sys.argv[1:]:
    wav, _ = librosa.load(path, sr=16000)
    with torch.inference_mode():
        logits = model(torch.tensor(wav)[None]).logits
    ids = logits.argmax(-1)[0]
    phones = proc.batch_decode(ids[None])[0]
    print(f"{path}\n  θ count: {phones.count('θ')}   s count: {phones.count('s')}\n  {phones}\n", flush=True)
