# 🔊 MagniReader

**Click on any text. Hear it read aloud. Snap any area. Get an AI description.**

MagniReader is a lightweight Windows screen reader that reads text aloud from any application. It uses Windows UI Automation for instant text extraction (no OCR), detects the language automatically, and speaks with high-quality Neural voices. It can also capture any screen area and describe it using AI vision models (Gemini or OpenAI).

A fast, always-available screen reader with AI-powered visual analysis.

---

## Highlights

- **Instant text extraction** — reads directly from the UI layer via Windows UI Automation. No OCR, no delays, works in any app.
- **Automatic language detection** — detects 18+ languages using trigram analysis and selects the right voice automatically.
- **Neural voice support** — integrates with [NaturalVoiceSAPIAdapter](https://github.com/gexgd0419/NaturalVoiceSAPIAdapter) for Microsoft Edge Neural voices (free, high quality).
- **Continuous reading** — doesn't stop at one sentence. Reads through paragraphs and following text elements seamlessly.
- **Visual overlay** — dims the screen, highlights the text being read, and shows a progress fill as reading advances.
- **Regional voice preference** — automatically selects the right accent for your locale (es-ES vs es-MX, en-US vs en-GB).
- **🔍 Screenshot analysis** — capture any screen area with `Ctrl+Alt+S` and get an AI-powered description via Gemini or OpenAI. Quick summary first, detailed analysis on demand.
- **Lightweight** — pure C# / .NET 8 / WPF. No Electron, no Python, no cloud dependencies for core operation.

## Quick start

**Prerequisites:** [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) on Windows 10/11.

```bash
git clone https://github.com/YOUR_USERNAME/magnireader.git
cd magnireader
dotnet run
```

The app starts silently in your **system tray** (near the clock). That's it.

## Usage

| Action | How |
|---|---|
| Read text | `Ctrl+Alt+Click` on any text |
| Analyze screen area | `Ctrl+Alt+S` → drag to select |
| Stop reading | Left-click anywhere or `Escape` |
| Settings | Right-click tray icon → Settings |
| Exit | Right-click tray icon → Exit |

## How it works

```
             Ctrl+Alt+Click
                  │
                  ▼
    ┌──────────────────────────┐
    │    UI Automation API     │ ← extracts text + bounding rect
    │   (TextExtractor.cs)     │   from any Windows application
    └────────────┬─────────────┘
                 │
                 ▼
    ┌──────────────────────────┐
    │   Language Detection     │ ← trigram analysis on extracted text
    │  (LanguageDetector.cs)   │   supports en, es, fr, de, it, pt...
    └────────────┬─────────────┘
                 │
                 ▼
    ┌──────────────────────────┐
    │     Voice Selection      │ ← picks the best voice for the
    │   (VoiceManager.cs)      │   detected language + region
    └────────────┬─────────────┘
                 │
           ┌─────┴─────┐
           ▼           ▼
    ┌────────────┐ ┌────────────┐
    │   SAPI5    │ │   WinRT    │ ← Neural voices via SAPI5,
    │  (Neural)  │ │ (Standard) │   standard voices via WinRT
    └────────────┘ └────────────┘
```

## 🗣️ Voice setup

MagniReader works out of the box with the built-in Windows voices (David, Zira, etc.), but they sound robotic.

### Unlock Neural voices (recommended)

Windows has high-quality Neural voices built in, but Microsoft restricts them to system apps (Narrator, Edge). A community adapter unlocks them for any app:

1. **Install Natural voices** (if you haven't already):
   - Settings → Accessibility → Narrator → Add natural voices
   - Or: Settings → Time & Language → Speech → Add voices

2. **Install [NaturalVoiceSAPIAdapter](https://github.com/gexgd0419/NaturalVoiceSAPIAdapter)**:
   - Download from [Releases](https://github.com/gexgd0419/NaturalVoiceSAPIAdapter/releases)
   - Run `Installer.exe` — install both 32-bit and 64-bit versions
   - Enable **"Microsoft Edge online voices"** (free, uses Edge's Read Aloud API)
   - Optionally enable **"Narrator natural voices"** for offline support

3. Restart MagniReader — Neural voices appear in Settings and are used automatically.

> **Note:** Edge Online voices require an internet connection. They use the same free API as Edge's Read Aloud feature. No API key or subscription needed.

## 🔍 Vision analysis

Capture any area of your screen and get an AI description of what's shown.

1. **Setup**: Go to Settings → Vision, select a provider (Gemini or OpenAI), and paste your API key
   - Get a free Gemini key at [aistudio.google.com/apikey](https://aistudio.google.com/apikey)
   - Use the **Test API Key** button to verify it works
2. **Capture**: Press `Ctrl+Alt+S` — a crosshair overlay appears
3. **Select**: Drag to select the area you want analyzed
4. **Result**: A quick summary appears and is read aloud
5. **Details**: Click **🔎 More Details** for a thorough, comprehensive analysis

The initial response is a concise 2-3 sentence summary. The detailed follow-up transcribes all visible text, describes UI elements, layout, and context.

## Settings

Right-click the tray icon → Settings to access:

- **Speech** — rate, volume, overlay dim opacity, highlight theme
- **Voices** — drag to reorder voice priority per language, preview any voice
- **Vision** — select AI provider (Gemini/OpenAI), enter API key, test connectivity

## Building

```bash
# Run from source
dotnet run

# Build standalone executable
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true

# Output: bin/Release/net8.0-windows10.0.17763.0/win-x64/publish/MagniReader.exe
```

## Project structure

```
magnireader/
├── App.xaml.cs                  # Entry point, orchestrator, reading queue
├── Core/
│   ├── TextExtractor.cs         # UI Automation text extraction + continuous reading
│   ├── LanguageDetector.cs      # Trigram-based language identification
│   ├── VoiceManager.cs          # Voice discovery (WinRT + SAPI5 + registry)
│   ├── VoiceEnabler.cs          # OneCore voice registration (admin)
│   ├── SpeechEngine.cs          # Dual-engine speech synthesis (WinRT + SAPI5)
│   └── VisionAnalyzer.cs        # Gemini / OpenAI vision API integration
├── UI/
│   ├── OverlayWindow.xaml       # Reading overlay with progress fill
│   ├── SnipWindow.xaml          # Screen capture selection overlay
│   ├── VisionResultWindow.xaml  # AI analysis result display
│   └── SettingsWindow.xaml      # Voice + vision configuration panel
├── Input/
│   └── InputManager.cs          # Global hotkeys + cancel detection
├── Infrastructure/
│   ├── SettingsStore.cs         # JSON settings persistence
│   └── CrashLogger.cs          # Crash reports + runtime diagnostics logging
├── Helpers/
│   └── NativeInterop.cs         # Win32 P/Invoke
└── scripts/                     # Standalone admin tools + source assets
```

## Technical details

- **Text extraction**: Windows UI Automation (`TextPattern`, `ValuePattern`, `Name` property). URL text is filtered out automatically.
- **Language detection**: Character trigram frequency profiles compared via distance scoring. Falls back to system UI language for short text.
- **Voice engine**: Dual-path synthesis — SAPI5 for NaturalVoiceSAPIAdapter voices (Neural quality), WinRT `SpeechSynthesizer` for standard voices.
- **Voice selection**: Prefers Neural voices → matches regional variant to system locale → falls back to Multilingual voices → falls back to any available voice.
- **DPI awareness**: PerMonitorV2. UI Automation coordinates are converted from physical pixels to WPF DIPs.
- **Continuous reading**: Extracts the clicked element plus up to 10 following siblings. Reads them sequentially, moving the overlay highlight to each element.
- **Vision analysis**: Two-phase approach — quick summary prompt first, detailed analysis on demand. Supports Gemini (model fallback chain: 2.0-flash → 1.5-flash → pro-vision) and OpenAI (gpt-4o-mini). API key validated via models endpoint before use.

## Requirements

- **Windows 10/11**
- **.NET 8 SDK** (for building from source)
- **NaturalVoiceSAPIAdapter** (optional, for Neural voices)
- **Gemini or OpenAI API key** (optional, for vision analysis)

## License

This project is licensed under the [GNU Affero General Public License v3.0](LICENSE) (AGPL-3.0).

MagniReader is built to advance accessibility for everyone. You are free to use, modify, and distribute this software — but any derivative work must also be open-sourced under the same license. This ensures that improvements to accessibility tools remain available to the community.
