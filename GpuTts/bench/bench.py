"""Benchmarks one engine through magni_tts_server.py, the way MagniReader uses it:
the text is split into sentence chunks and each chunk is one /synth request.

    python bench.py <engine> <venv python> <out dir>
"""
import json, os, re, secrets, subprocess, sys, time, urllib.request, wave, struct
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent  # the GpuTts folder
engine, py, out = sys.argv[1], sys.argv[2], Path(sys.argv[3])
out.mkdir(parents=True, exist_ok=True)

TEXTS = {
    "en": ("The library opens at nine in the morning. Visitors can borrow up to five books at a time, "
           "and the reading room on the second floor stays quiet all day. If you need help finding something, "
           "ask at the front desk and a librarian will show you where it is."),
    "es": ("La biblioteca abre a las nueve de la mañana. Los visitantes pueden llevarse hasta cinco libros a la vez, "
           "y la sala de lectura del segundo piso permanece en silencio todo el día. Si necesitas ayuda para encontrar algo, "
           "pregunta en el mostrador y un bibliotecario te indicará dónde está."),
}
VOICE = {"en": "en_us_f1", "es": "es_castilian_m1"}


def chunks(text, first_max=120, mx=260):
    # same idea as KokoroSpeaker.SplitIntoChunks: sentences, first one short
    sents = re.findall(r"[^.!?…]+[.!?…]*\s*", text)
    res = []
    for s in sents:
        s = s.strip()
        limit = first_max if not res else mx
        while len(s) > limit:
            cut = s.rfind(", ", 0, limit)
            cut = cut + 1 if cut > limit // 3 else s.rfind(" ", 0, limit)
            res.append(s[:cut].strip()); s = s[cut:].strip()
            limit = mx
        if s:
            res.append(s)
    return res


def gpu_used_mb():
    r = subprocess.run(["nvidia-smi", "--query-gpu=memory.used", "--format=csv,noheader,nounits"],
                       capture_output=True, text=True)
    return int(r.stdout.strip().splitlines()[0])


token = secrets.token_hex(16)
env = dict(os.environ, MAGNI_TTS_TOKEN=token, PYTHONUTF8="1", PYTHONIOENCODING="utf-8")
before = gpu_used_mb()
t0 = time.perf_counter()
proc = subprocess.Popen([py, "-u", str(REPO / "magni_tts_server.py"), "--engine", engine,
                         "--voices", str(REPO / "voices"), "--parent-pid", str(os.getpid())],
                        stdout=subprocess.PIPE, stderr=open(out / f"{engine}_server.log", "w", encoding="utf-8"),
                        env=env, text=True, encoding="utf-8")
ready = None
for line in proc.stdout:
    try:
        ev = json.loads(line)
    except ValueError:
        continue
    ready = ev
    break
startup_s = time.perf_counter() - t0
if not ready or ready.get("event") != "ready":
    print(json.dumps({"engine": engine, "error": ready}))
    sys.exit(1)
port = ready["port"]
base = f"http://127.0.0.1:{port}"


def call(path, body=None):
    req = urllib.request.Request(base + path, data=None if body is None else json.dumps(body).encode(),
                                 headers={"X-Magni-Token": token, "Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=600) as r:
        return r.read(), dict(r.headers)


def save(path, pcm_bytes):
    import array
    a = array.array("f"); a.frombytes(pcm_bytes)
    with wave.open(str(path), "wb") as w:
        w.setnchannels(1); w.setsampwidth(2); w.setframerate(24000)
        w.writeframes(b"".join(struct.pack("<h", max(-32767, min(32767, int(x * 32767)))) for x in a))


results = {"engine": engine, "startup_s": round(startup_s, 1), "load_ms": ready.get("load_ms"),
           "gpu": ready.get("gpu"), "runs": {}}
for lang, text in TEXTS.items():
    parts = chunks(text)
    runs = []
    for rep in range(3):
        t_start = time.perf_counter()
        ttfa = None; gen = 0.0; audio = 0.0; pcm = b""
        for i, c in enumerate(parts):
            ts = time.perf_counter()
            data, h = call("/synth", {"text": c, "lang": lang, "voice": VOICE[lang], "speed": 1.0})
            dt = time.perf_counter() - ts
            if ttfa is None:
                ttfa = time.perf_counter() - t_start
            gen += dt; audio += len(data) / 4 / 24000; pcm += data
        runs.append({"ttfa_s": round(ttfa, 3), "gen_s": round(gen, 3), "audio_s": round(audio, 2),
                     "rtf": round(gen / audio, 3)})
        if rep == 0:
            save(out / f"{engine}_{lang}.wav", pcm)
    results["runs"][lang] = runs
    print(engine, lang, runs, flush=True)

health = json.loads(call("/health")[0])
results["vram"] = {"torch_peak_mb": health["torchPeakMb"], "torch_reserved_mb": health["torchReservedMb"],
                   "gpu_used_delta_mb": gpu_used_mb() - before}
try:
    call("/shutdown", {})
except Exception:
    pass
proc.wait(timeout=30)
(out / f"{engine}.json").write_text(json.dumps(results, indent=1))
print(json.dumps(results))
