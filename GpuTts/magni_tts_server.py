"""
MagniReader GPU voice server.

Runs one high-quality neural TTS model on an NVIDIA GPU and serves it to
MagniReader over a local HTTP connection (127.0.0.1 only).

    python magni_tts_server.py --engine omnivoice --voices <dir> [--port 0]

On start-up it prints one JSON line to stdout once the model is loaded:
    {"event": "ready", "port": 51234, "engine": "...", "gpu": "...", "load_ms": ...}
or, if it cannot run:
    {"event": "error", "message": "..."}

Endpoints (all need the header  X-Magni-Token: <MAGNI_TTS_TOKEN env var>):
    GET  /health          -> JSON status (engine, gpu, VRAM)
    GET  /voices          -> JSON list of reference voices found in --voices
    POST /synth           -> body JSON {"text", "lang": "en"|"es", "voice", "speed"}
                             reply: raw float32 little-endian mono PCM at 24 kHz
                             headers X-Gen-Ms, X-Audio-Ms
    POST /shutdown

Every engine clones the accent of a reference clip (a .wav plus a .txt with its
exact transcript). MagniReader ships a Castilian Spanish clip and an English one;
users can add their own clips to the voices folder.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import threading
import time
import traceback
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

OUT_SR = 24000  # MagniReader plays everything at 24 kHz (same as Kokoro)


# stdout carries only our JSON events. The model libraries print to stdout too, so
# everything else is sent to stderr (MagniReader's log).
_PROTOCOL = sys.stdout
sys.stdout = sys.stderr


def emit(obj: dict) -> None:
    _PROTOCOL.write(json.dumps(obj, ensure_ascii=False) + "\n")
    _PROTOCOL.flush()


def log(msg: str) -> None:
    sys.stderr.write(f"[{time.strftime('%H:%M:%S')}] {msg}\n")
    sys.stderr.flush()


# ─── Reference voices ────────────────────────────────────────────

class Voice:
    def __init__(self, wav: Path):
        self.id = wav.stem
        self.wav = str(wav)
        txt = wav.with_suffix(".txt")
        self.text = txt.read_text(encoding="utf-8").strip() if txt.exists() else ""
        meta_path = wav.with_suffix(".json")
        meta = json.loads(meta_path.read_text(encoding="utf-8")) if meta_path.exists() else {}
        self.lang = meta.get("lang") or ("es" if self.id.startswith("es") else "en")
        self.name = meta.get("name") or self.id
        self.meta = meta

    def to_json(self) -> dict:
        return {"id": self.id, "lang": self.lang, "name": self.name,
                "hasTranscript": bool(self.text), **{k: v for k, v in self.meta.items() if k not in ("lang", "name")}}


def scan_voices(dirs: list[str]) -> dict[str, Voice]:
    voices: dict[str, Voice] = {}
    for d in dirs:
        p = Path(d)
        if not p.is_dir():
            continue
        for wav in sorted(p.glob("*.wav")):
            try:
                v = Voice(wav)
                voices[v.id] = v
            except Exception as ex:  # a broken user file must not stop the server
                log(f"Skipping voice {wav}: {ex}")
    return voices


# ─── Engines ─────────────────────────────────────────────────────

def to_out_sr(audio, sr: int):
    import numpy as np
    audio = np.asarray(audio, dtype=np.float32).reshape(-1)
    if sr != OUT_SR:
        import librosa
        audio = librosa.resample(audio, orig_sr=sr, target_sr=OUT_SR).astype(np.float32)
    return audio


def time_stretch(audio, speed: float):
    """For engines without a speed control. Phase-vocoder; fine for 0.75..1.5x."""
    if abs(speed - 1.0) < 0.02:
        return audio
    import librosa
    import numpy as np
    return librosa.effects.time_stretch(audio, rate=speed).astype(np.float32)


class WordAligner:
    """
    Finds when each written word starts in the generated audio, so MagniReader's word
    highlight follows the voice. Uses the MMS forced-alignment model (CTC, ~300M
    parameters, multilingual, letters a-z) that ships with torchaudio. Words are the
    same as MagniReader's: whitespace-separated runs that contain a letter or digit.
    """

    def __init__(self):
        import torch
        import torchaudio
        bundle = torchaudio.pipelines.MMS_FA
        self.model = bundle.get_model(with_star=False).to("cuda").eval()
        self.dict = bundle.get_dict(star=None)
        self.sr = bundle.sample_rate
        self.torch = torch
        self.F = torchaudio.functional

    @staticmethod
    def words(text: str) -> list[str]:
        return [w for w in text.split() if any(c.isalnum() for c in w)]

    def _letters(self, word: str) -> str:
        import unicodedata
        w = unicodedata.normalize("NFD", word.lower())
        return "".join(c for c in w if c in self.dict and c not in ("-", "|"))

    def starts(self, audio24k, text: str):
        torch, F = self.torch, self.F
        words = self.words(text)
        if not words:
            return []
        letters = [self._letters(w) for w in words]
        keep = [i for i, l in enumerate(letters) if l]
        if not keep:
            return None
        wav = torch.from_numpy(audio24k).to("cuda").unsqueeze(0)
        wav = F.resample(wav, OUT_SR, self.sr)
        with torch.inference_mode():
            emission, _ = self.model(wav)
        targets = [self.dict[c] for i in keep for c in letters[i]]
        if emission.size(1) < len(targets) * 1.1:
            return None
        ali, scores = F.forced_align(emission, torch.tensor([targets], dtype=torch.int32, device="cuda"), blank=0)
        spans = F.merge_tokens(ali[0], scores[0].exp())
        sec_per_frame = wav.size(1) / emission.size(1) / self.sr
        starts: list[float | None] = [None] * len(words)
        k = 0
        for i in keep:
            starts[i] = spans[k].start * sec_per_frame
            k += len(letters[i])
        # Words with nothing alignable (numbers, symbols): between their neighbours
        dur = len(audio24k) / OUT_SR
        for i in range(len(starts)):
            if starts[i] is None:
                prev = next((starts[j] for j in range(i - 1, -1, -1) if starts[j] is not None), 0.0)
                nxt = next((starts[j] for j in range(i + 1, len(starts)) if starts[j] is not None), dur)
                starts[i] = (prev + nxt) / 2
        return [max(0.0, s - 0.03) for s in starts]  # a hair early feels in sync


class Engine:
    name = "?"
    native_speed = False

    def load(self) -> None: ...

    def prepare(self, voice: Voice) -> None:
        """Pre-compute anything reusable for a voice (called once per voice)."""

    def synth(self, text: str, lang: str, voice: Voice, speed: float):
        """Returns (float32 numpy audio, sample_rate)."""
        raise NotImplementedError


class OmniVoiceEngine(Engine):
    name = "omnivoice"
    native_speed = True

    def load(self):
        import torch
        from omnivoice import OmniVoice
        self.model = OmniVoice.from_pretrained("k2-fsa/OmniVoice", device_map="cuda", dtype=torch.float16)
        self.prompts = {}
        self.num_step = int(os.environ.get("MAGNI_OMNI_STEPS", "16"))

    def prepare(self, voice):
        if voice.id not in self.prompts:
            self.prompts[voice.id] = self.model.create_voice_clone_prompt(
                ref_audio=voice.wav, ref_text=voice.text or None)

    def synth(self, text, lang, voice, speed):
        self.prepare(voice)
        audios = self.model.generate(
            text=text, language=lang, voice_clone_prompt=self.prompts[voice.id],
            speed=speed if abs(speed - 1) > 0.01 else None, num_step=self.num_step)
        return audios[0], self.model.sampling_rate


class Qwen3Engine(Engine):
    """
    Qwen3-TTS Base (voice cloning) through faster-qwen3-tts, which captures both
    decoders as CUDA graphs. The stock qwen-tts package runs ~3x slower than real time
    on Windows (a Python-level generate() call per audio frame); this one is real time.
    """

    def __init__(self, size="0.6B"):
        self.size = size
        self.name = f"qwen3-{size.lower()}"

    def load(self):
        from faster_qwen3_tts import FasterQwen3TTS
        self.model = FasterQwen3TTS.from_pretrained(f"Qwen/Qwen3-TTS-12Hz-{self.size}-Base")

    def synth(self, text, lang, voice, speed):
        wavs, sr = self.model.generate_voice_clone(
            text=text, language="Spanish" if lang == "es" else "English",
            ref_audio=voice.wav, ref_text=voice.text, xvec_only=not voice.text,
            append_silence=False)
        return wavs[0], sr


ENGINES = {
    "omnivoice": OmniVoiceEngine,
    "qwen3-0.6b": lambda: Qwen3Engine("0.6B"),
}


# ─── HTTP server ─────────────────────────────────────────────────

class State:
    engine: Engine
    voices: dict[str, Voice]
    voice_dirs: list[str]
    token: str
    gpu_lock = threading.Lock()  # one synthesis at a time on the GPU
    aligner = None  # WordAligner, or None if it could not load
    gpu_name = ""


def gpu_info() -> dict:
    import torch
    free, total = torch.cuda.mem_get_info()
    return {
        "gpu": torch.cuda.get_device_name(0),
        "vramTotalMb": total // 2**20,
        "vramUsedMb": (total - free) // 2**20,
        "torchAllocatedMb": torch.cuda.memory_allocated() // 2**20,
        "torchPeakMb": torch.cuda.max_memory_allocated() // 2**20,
        "torchReservedMb": torch.cuda.memory_reserved() // 2**20,
    }


class Handler(BaseHTTPRequestHandler):
    server_version = "MagniTTS/1"

    def log_message(self, fmt, *args):  # keep stderr quiet
        pass

    def _auth(self) -> bool:
        if State.token and self.headers.get("X-Magni-Token") != State.token:
            self.send_error(403)
            return False
        return True

    def _json(self, obj, code=200):
        data = json.dumps(obj, ensure_ascii=False).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        if not self._auth():
            return
        if self.path == "/health":
            self._json({"engine": State.engine.name, **gpu_info()})
        elif self.path == "/voices":
            State.voices = scan_voices(State.voice_dirs)
            self._json([v.to_json() for v in State.voices.values()])
        else:
            self.send_error(404)

    def do_POST(self):
        if not self._auth():
            return
        if self.path == "/shutdown":
            self._json({"ok": True})
            threading.Thread(target=self.server.shutdown, daemon=True).start()
            return
        if self.path != "/synth":
            self.send_error(404)
            return
        try:
            n = int(self.headers.get("Content-Length", "0"))
            req = json.loads(self.rfile.read(n).decode("utf-8"))
            text = str(req.get("text", "")).strip()
            lang = "es" if str(req.get("lang", "en")).lower().startswith("es") else "en"
            speed = float(req.get("speed", 1.0))
            speed = min(2.0, max(0.5, speed))
            voice = State.voices.get(req.get("voice") or "")
            if voice is None:
                State.voices = scan_voices(State.voice_dirs)
                voice = State.voices.get(req.get("voice") or "") or next(
                    (v for v in State.voices.values() if v.lang == lang), None)
            if voice is None:
                self._json({"error": "No reference voice for this language"}, 400)
                return
            if not text:
                self._json({"error": "Empty text"}, 400)
                return

            import numpy as np
            t0 = time.perf_counter()
            times = None
            with State.gpu_lock:
                audio, sr = State.engine.synth(text, lang, voice, speed)
                audio = to_out_sr(audio, sr)
                if not State.engine.native_speed:
                    audio = time_stretch(audio, speed)
                gen_ms = (time.perf_counter() - t0) * 1000
                if State.aligner is not None and req.get("align", True):
                    try:
                        times = State.aligner.starts(audio, text)
                    except Exception:
                        log("Word alignment failed: " + traceback.format_exc())
            align_ms = (time.perf_counter() - t0) * 1000 - gen_ms
            data = np.ascontiguousarray(audio, dtype="<f4").tobytes()
            self.send_response(200)
            self.send_header("Content-Type", "application/octet-stream")
            self.send_header("Content-Length", str(len(data)))
            self.send_header("X-Gen-Ms", f"{gen_ms:.1f}")
            self.send_header("X-Audio-Ms", f"{len(audio) * 1000 / OUT_SR:.1f}")
            self.send_header("X-Voice", voice.id)
            self.send_header("X-Align-Ms", f"{align_ms:.1f}")
            if times is not None:
                self.send_header("X-Word-Times", ",".join(f"{t:.3f}" for t in times))
            self.end_headers()
            self.wfile.write(data)
        except Exception as ex:
            log(traceback.format_exc())
            try:
                self._json({"error": f"{type(ex).__name__}: {ex}"}, 500)
            except Exception:
                pass


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--engine", required=True, choices=sorted(ENGINES))
    ap.add_argument("--voices", action="append", default=[], help="folder with <id>.wav + <id>.txt (repeatable)")
    ap.add_argument("--port", type=int, default=0)
    ap.add_argument("--parent-pid", type=int, default=0, help="exit when this process exits")
    args = ap.parse_args()

    State.token = os.environ.get("MAGNI_TTS_TOKEN", "")
    State.voice_dirs = args.voices

    if args.parent_pid:
        threading.Thread(target=watch_parent, args=(args.parent_pid,), daemon=True).start()

    try:
        import torch
        if not torch.cuda.is_available():
            emit({"event": "error", "message": "No CUDA GPU is available to PyTorch."})
            return 2
        t0 = time.perf_counter()
        engine = ENGINES[args.engine]()
        engine.load()
        State.engine = engine
        State.voices = scan_voices(args.voices)
        if os.environ.get("MAGNI_ALIGN", "1") != "0":
            try:
                State.aligner = WordAligner()
            except Exception:
                log("Word aligner unavailable; MagniReader will estimate word timings: " + traceback.format_exc())
        # Pre-compute the voice prompts and warm the GPU kernels up
        for v in State.voices.values():
            try:
                engine.prepare(v)
            except Exception:
                log(f"Could not prepare voice {v.id}:\n{traceback.format_exc()}")
        for lang in ("en", "es"):
            v = next((x for x in State.voices.values() if x.lang == lang), None)
            if v:
                a, sr = engine.synth("Hola." if lang == "es" else "Hello.", lang, v, 1.0)
                if State.aligner is not None:
                    State.aligner.starts(to_out_sr(a, sr), "Hola." if lang == "es" else "Hello.")
        load_ms = (time.perf_counter() - t0) * 1000
    except Exception as ex:
        log(traceback.format_exc())
        emit({"event": "error", "message": f"{type(ex).__name__}: {ex}"})
        return 1

    httpd = ThreadingHTTPServer(("127.0.0.1", args.port), Handler)
    emit({"event": "ready", "port": httpd.server_address[1], "engine": engine.name,
          "load_ms": round(load_ms), **gpu_info()})
    httpd.serve_forever()
    return 0


def watch_parent(pid: int):
    """Exit when MagniReader exits (so the model never stays in VRAM on its own)."""
    import ctypes
    SYNCHRONIZE = 0x00100000
    k32 = ctypes.windll.kernel32
    h = k32.OpenProcess(SYNCHRONIZE, False, pid)
    if not h:
        os._exit(0)
    k32.WaitForSingleObject(h, 0xFFFFFFFF)
    os._exit(0)


if __name__ == "__main__":
    sys.exit(main() or 0)
