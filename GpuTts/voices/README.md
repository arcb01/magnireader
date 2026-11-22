# GPU voice reference clips

The GPU voice models clone the voice and **accent** of a short reference clip, so these clips
decide that the Spanish voice sounds Castilian (Spain) and the English one sounds American.
Each clip is a `.wav` with a `.txt` holding its exact transcript and a `.json` with its
language, display name and source.

| Clip | Language | Source | License |
| --- | --- | --- | --- |
| `es_castilian_m2` (default) | Spanish (Spain), neutral male | Synthetic: OmniVoice voice design, `"male, middle-aged, very low pitch"`, seed 34 | Made with OmniVoice (model CC BY-NC 4.0) |
| `en_us_m1` (default) | English (US), neutral male | Synthetic: OmniVoice voice design, `"male, middle-aged, moderate pitch, american accent"`, seed 8 | Made with OmniVoice (model CC BY-NC 4.0) |
| `es_castilian_m1` | Spanish (Spain) | [CSS10 Spanish](https://github.com/Kyubyong/css10) (LibriVox recording of Benito Pérez Galdós, *19 de marzo y el 2 de mayo*), clip `19demarzo_0008` | CC0 1.0 (dataset), public domain (LibriVox) |
| `en_us_f1` | English (US) | [LJ Speech](https://keithito.com/LJ-Speech-Dataset/) clip `LJ001-0009` (LibriVox recording) | Public domain |

The neutral male clips were picked from 12–16 voice-design candidates each (`GpuTts/bench/design_voices.py`)
for a male pitch (~105–115 Hz), words read correctly, and, for Spanish, [θ] in all six places Spain
uses it (*hace, ciudad, once, diez, empieza, plaza*). Cloned by OmniVoice and Qwen3 0.6B, they keep
the Castilian [θ] (6 of 6 in the benchmark text).

The Castilian audiobook clip was checked with a phoneme recognizer
(`facebook/wav2vec2-xlsr-53-espeak-cv-ft`): the speaker says *conciencia* and *disciplina*
with [θ] (distinción), which Latin American Spanish replaces with [s].

Users can add their own clips in `%APPDATA%\MagniReader\gpu-voices`: a 5–15 second `.wav`
of one speaker plus a `.txt` with the same name holding exactly what is said. Names that
start with `es` are Spanish, others English (or set `"lang"` in a `.json`).
