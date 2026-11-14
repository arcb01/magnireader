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

## Install

1. Download **`MagniReader-win-Setup.exe`** from the [latest release](https://github.com/arcb01/magnireader/releases/latest).
2. Run it. No admin rights needed: MagniReader installs for your user, adds Start menu and desktop shortcuts, and starts with its control bar and tray icon.
3. Optional: right-click the tray icon → **Settings → 🚀 App → Launch MagniReader when Windows starts**.

Installed copies update themselves: a new release is downloaded in the background and installed the next time MagniReader starts (or right-click the tray icon → **Restart to update**). Uninstall from Windows **Settings → Apps**. Your settings and the Kokoro model in `%APPDATA%\MagniReader` are kept across updates.

> Windows SmartScreen may warn about an unrecognized app because the installer isn't code-signed. Click **More info → Run anyway**.

## Quick start (from source)

**Prerequisites:** [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) on Windows 10/11.

```bash
git clone https://github.com/YOUR_USERNAME/magnireader.git
cd magnireader
dotnet run
```

The app starts with a small **control bar** at the top of the screen, plus an icon in the **system tray** (near the clock).

To run it without the SDK, publish once (see [Building](#building)) and start `MagniReader.exe` from the publish folder. You can pin that exe to Start or the taskbar.

## Control bar

A compact, always-on-top toolbar in the spirit of the Windows Magnifier toolbar. It never takes focus away from the app you're reading, so you can use it mid-document.

| Button | What it does |
|---|---|
| ▶ Read | Click it, then click any text to read it. No Ctrl+Alt needed. Esc cancels. |
| ■ Stop | Stops reading. |
| − Speed + | Reading speed, -10 to +10 (same as the slider in Settings). |
| Windows / Kokoro | Switches voice engine. Kokoro needs its model downloaded in Settings first. |
| 📷 | Describe a screen area with AI (same as `Ctrl+Alt+S`). |
| ⚙ | Opens Settings. |
| — | Hides the bar to the tray. Double-click the tray icon to bring it back. |
| ✕ | Exits MagniReader. |

Drag the bar by its "MagniReader" label. It remembers where you put it, and whether it was hidden.

## Reading controls

While a text is being read, a small strip of controls sits on top of the highlighted box: **⏪ 10** (back 10 seconds), **⏸ / ▶** (pause / continue), **10 ⏩** (forward 10 seconds) and **■** (stop). Clicking them doesn't stop the reading or take focus from the app you're reading. Very short texts (a button, a heading) don't get the strip.

- Back stops at the start of the current paragraph. Forward past its end moves on to the next one.
- The word box follows the jump right away.
- **Kokoro** and Windows voices played through Windows' own player jump exactly 10 seconds.
- **SAPI voices** (e.g. Natural voices through NaturalVoiceSAPIAdapter) can't jump inside their audio, so speech restarts at the word estimated to be 10 seconds away. The jump is close to 10 seconds, not exact.
- Other apps' audio comes back up while paused.

## Usage

| Action | How |
|---|---|
| Read text | `Ctrl+Alt+Click` on any text |
| Analyze screen area | `Ctrl+Alt+S` → drag to select |
| Read without Ctrl+Alt | Control bar ▶, then click the text |
| Pause, back / forward 10 s | Controls on top of the box being read |
| Stop reading | Left-click anywhere, `Escape`, or ■ on the bar |
| Settings | ⚙ on the control bar, or right-click tray icon → Settings |
| Show the control bar | Double-click the tray icon |
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
- **App** — launch at Windows startup, automatic updates, check for updates now, current version

## Building

```bash
# Run from source
dotnet run

# Build standalone executable
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true

# Output: bin/Release/net8.0-windows10.0.17763.0/publish/MagniReader.exe
```

## Releasing

Releases are built by GitHub Actions ([`.github/workflows/release.yml`](.github/workflows/release.yml)) with [Velopack](https://velopack.io):

```bash
git tag v1.1.0
git push origin v1.1.0
```

The workflow publishes a self-contained build, packs `MagniReader-win-Setup.exe`, a portable zip and full + delta update packages, and attaches them to a new GitHub Release. Installed copies pick it up within a few hours. You can also run the workflow by hand from the **Actions** tab and type the version.

Updates only run in the installed app. When you `dotnet run` from source, the update check is skipped.

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
│   ├── ControlBarWindow.xaml    # Compact always-on-top control bar
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
