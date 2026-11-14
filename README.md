<p align="center">
  <img src="assets/magnireader.svg" alt="MagniReader logo" width="160" height="160">
</p>

<h1 align="center">MagniReader</h1>

<p align="center">
  <strong>Click any text on Windows. Hear it read aloud.</strong><br>
  A lightweight, always-ready screen reader with natural voices, word-by-word highlighting and AI screen descriptions.
</p>

<p align="center">
  <a href="https://github.com/arcb01/magnireader/actions/workflows/build.yml"><img src="https://img.shields.io/github/actions/workflow/status/arcb01/magnireader/build.yml?branch=main&label=build" alt="Build status"></a>
  <a href="https://github.com/arcb01/magnireader/releases/latest"><img src="https://img.shields.io/github/v/release/arcb01/magnireader?label=release" alt="Latest release"></a>
  <img src="https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D6?logo=windows" alt="Windows 10 and 11">
  <img src="https://img.shields.io/badge/.NET-8-512BD4?logo=dotnet" alt=".NET 8">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-AGPL--3.0-blue" alt="License: AGPL-3.0"></a>
</p>

<p align="center">
  <a href="https://github.com/arcb01/magnireader/releases/latest"><strong>Download</strong></a> ·
  <a href="#quick-start">Quick start</a> ·
  <a href="#features">Features</a> ·
  <a href="#voices">Voices</a> ·
  <a href="#vision-analysis">Vision</a> ·
  <a href="#build-from-source">Build from source</a>
</p>

---

MagniReader reads text from any Windows app the moment you click it. It pulls the text straight from the UI through Windows UI Automation (no OCR, no waiting), detects the language, and speaks it with an offline neural voice or any Windows voice, highlighting each word as it goes. When there is no text to grab, snap an area of the screen and an AI model describes it for you.

It is plain C# / .NET 8 / WPF: no Electron, no Python, and no cloud service needed for reading.

## Install

1. Download **`MagniReader-win-Setup.exe`** from the [latest release](https://github.com/arcb01/magnireader/releases/latest).
2. Run it. No admin rights needed: it installs for your user, adds Start menu and desktop shortcuts, and starts with its control bar and tray icon.
3. Optional: **Settings → App → Launch MagniReader when Windows starts**.

MagniReader keeps itself up to date. New releases download in the background and install the next time it starts, or right away with **Restart to update** in the tray menu. Your settings and the Kokoro model in `%APPDATA%\MagniReader` are kept across updates. Uninstall from Windows **Settings → Apps**.

> [!NOTE]
> The installer isn't code-signed yet, so Windows SmartScreen may warn about an unrecognized app. Click **More info → Run anyway**.

## Quick start

1. Hold **`Ctrl+Alt`** and **click** any text: a web page, a document, a button, a menu.
2. MagniReader dims the screen, highlights the text box and the word being spoken, and reads on through the following paragraphs.
3. **Left-click anywhere** or press **`Esc`** to stop.
4. Select text anywhere and press **`Ctrl+Alt+R`** to read just your selection.
5. Press **`Ctrl+Alt+S`** and drag over any area to get an AI description of it.

Prefer the mouse alone? Press **▶ Read** on the control bar, then click the text.

## Features

- **Instant text from any app.** Reads straight from the UI layer with Windows UI Automation. Hold `Ctrl+Alt` and the element under the cursor lights up before you click.
- **Word-by-word highlight.** The current word is boxed on screen as it is spoken, and the box follows every jump.
- **Continuous reading.** Carries on through the following paragraphs and elements instead of stopping after one.
- **Natural voices, offline.** Built-in [Kokoro](https://github.com/Lyrcaxis/KokoroSharp) neural voices for English and Spanish run locally on your CPU. Every installed Windows voice is available too.
- **Automatic language detection.** Picks the language of each text and the right voice and accent for it.
- **Reading controls.** Pause, go back or forward 10 seconds, and stop, from a small strip on top of the text being read.
- **Quieter background audio.** Music and videos turn down while MagniReader speaks and come back when it stops.
- **AI screen descriptions.** Snap any area and get a short summary read aloud, with a detailed description on demand (Gemini or OpenAI).
- **Control bar.** A compact always-on-top toolbar that never steals focus from the app you're reading.
- **Configurable shortcuts.** Record your own keys for reading, reading a selection and describing the screen.
- **Installer, auto-updates and launch at startup.** One setup file, updates from GitHub Releases, and an optional start with Windows.

## Usage

| Action | Default |
|---|---|
| Read text | `Ctrl+Alt+Click` on any text |
| Read selected text | Select it, then `Ctrl+Alt+R` |
| Read without the keyboard | **▶ Read** on the control bar, then click the text |
| Describe a screen area | `Ctrl+Alt+S`, then drag to select |
| Pause, back / forward 10 s | Strip on top of the text being read |
| Stop reading | Left-click anywhere, `Esc`, or **■** on the control bar |
| Settings | **⚙** on the control bar, or right-click the tray icon |
| Show the control bar | Double-click the tray icon |
| Exit | **✕** on the control bar, or right-click the tray icon → Exit |

The read, read-selection and describe shortcuts can be changed in **Settings → Shortcuts**. Stopping always works with `Esc` or a click.

### Control bar

| Button | What it does |
|---|---|
| ▶ Read | Click it, then click any text to read it. `Esc` cancels. |
| ■ Stop | Stops reading. |
| − Speed + | Reading speed, -10 to +10. |
| Windows / Kokoro | Switches the voice engine. Kokoro needs its model downloaded first. |
| 📷 | Describes a screen area with AI. |
| ⚙ | Opens Settings. |
| — | Hides the bar to the tray. Double-click the tray icon to bring it back. |
| ✕ | Exits MagniReader. |

Drag the bar by its label. It remembers where you put it and whether it was hidden.

### Reading controls

While a text is being read, a small strip sits on top of the highlighted box: **⏪ 10**, **⏸ / ▶**, **10 ⏩** and **■**. Clicking it doesn't interrupt the reading or take focus from your app. Very short texts, like a button or a heading, don't get the strip.

- Back stops at the start of the current paragraph. Forward past its end moves on to the next one.
- Kokoro and standard Windows voices jump exactly 10 seconds. SAPI voices (such as Natural voices through NaturalVoiceSAPIAdapter) restart at the word estimated to be 10 seconds away.
- Background audio comes back up while paused.

## Voices

Pick the engine in **Settings → Speech**, or switch it from the control bar.

### Kokoro (recommended)

Offline neural voices for English and Spanish that sound natural and need no account or internet once downloaded.

1. **Settings → Speech → Voice engine → Kokoro**.
2. Click **⬇ Download Kokoro model** (a one-time download, kept across updates).
3. Choose an English and a Spanish voice and press ▶ to preview them.

Other languages (Catalan, French, …) are read with Windows voices, and if Kokoro ever fails a Windows voice takes over.

### Windows voices

MagniReader works out of the box with every installed Windows voice. With the Windows engine selected, Settings lists the voices per language so you can preview them and reorder them with ▲ ▼. The first voice of each language is used.

<details>
<summary><strong>Unlock the Windows Natural voices</strong></summary>

Windows ships high-quality Natural voices but only lets system apps (Narrator, Edge) use them. A community adapter makes them available to any app:

1. Install Natural voices: **Settings → Accessibility → Narrator → Add natural voices**, or **Settings → Time & Language → Speech → Add voices**.
2. Install [NaturalVoiceSAPIAdapter](https://github.com/gexgd0419/NaturalVoiceSAPIAdapter/releases): run `Installer.exe`, install both the 32-bit and 64-bit versions, enable **Microsoft Edge online voices** (free, needs internet) and optionally **Narrator natural voices** (offline).
3. Restart MagniReader. The Natural voices appear in Settings and are preferred automatically.

</details>

## Vision analysis

Capture any area of the screen and hear what's on it.

1. **Settings → Vision**: choose **Gemini** or **OpenAI**, paste your API key and press **Test API Key**. A free Gemini key is available at [aistudio.google.com/apikey](https://aistudio.google.com/apikey).
2. Press **`Ctrl+Alt+S`** (or 📷 on the control bar) and drag over the area.
3. A 2–3 sentence summary appears and is read aloud.
4. Click **🔎 More Details** for a full description: all visible text, UI elements, layout and context.

Screenshots are sent only to the provider you choose, and only when you take one.

## Settings

| Tab | What's there |
|---|---|
| **Speech** | Voice engine, Kokoro voices and model download, voice priority per language (Windows engine), rate, volume, lowering other sounds |
| **Display** | How much to dim the rest of the screen, highlight theme |
| **Shortcuts** | Record the read, read-selection and describe-area shortcuts, or reset them |
| **Vision** | AI provider, API key, key test |
| **App** | Launch at startup, automatic updates, check for updates, installed version |

## How it works

```
        Ctrl+Alt+Click
              │
              ▼
   UI Automation text + bounds        TextExtractor
              │
              ▼
   Language detection                 LanguageDetector
              │
              ▼
   Voice selection                    VoiceManager
              │
      ┌───────┴────────┐
      ▼                ▼
   Kokoro (ONNX)   Windows voices     KokoroSpeaker / SpeechEngine
      └───────┬────────┘              (WinRT + SAPI5)
              ▼
   Word timings → on-screen highlight WordLocator + OverlayWindow
```

<details>
<summary><strong>Technical details</strong></summary>

- **Text extraction**: Windows UI Automation (`TextPattern`, `ValuePattern`, `Name`). Reads the clicked element plus the elements that follow it, moving the highlight to each one.
- **Language detection**: common-word counts first, then character trigram profiles. Falls back to the system UI language for very short text.
- **Speech**: Kokoro runs on ONNX Runtime (CPU) through KokoroSharp. Windows voices go through WinRT `SpeechSynthesizer`, or SAPI5 for adapter voices.
- **Voice selection**: prefers neural voices, then the regional variant matching your locale (es-ES vs es-MX, en-US vs en-GB), then multilingual voices, then any voice.
- **Word highlight**: each word's screen rectangle is found ahead of the speech with UI Automation text ranges, falling back to Windows OCR for labels and simple controls, then boxed as the engine reports it.
- **Audio ducking**: other apps' audio sessions are lowered through the Windows Core Audio API and restored afterwards, even after a crash.
- **DPI awareness**: Per-Monitor V2. UI Automation coordinates are converted from physical pixels to WPF DIPs.
- **Vision**: a quick summary prompt, then a detailed one on demand. Gemini (2.5 Pro, falling back to 2.5 Flash) or OpenAI (gpt-4o-mini).
- **Updates**: packaged with [Velopack](https://velopack.io), with full and delta packages served from GitHub Releases.

</details>

## Build from source

**Prerequisites:** Windows 10/11 and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
git clone https://github.com/arcb01/magnireader.git
cd magnireader
dotnet run
```

```powershell
# Standalone build
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
# Output: bin\Release\net8.0-windows10.0.17763.0\publish\MagniReader.exe
```

Updates only run in the installed app. When you `dotnet run` from source, the update check is skipped.

### Releasing

Releases are built by [`.github/workflows/release.yml`](.github/workflows/release.yml):

```powershell
git tag v1.0.0
git push origin v1.0.0
```

The workflow publishes a self-contained build, packs `MagniReader-win-Setup.exe`, a portable zip and full + delta update packages, and attaches them to a new GitHub Release. Installed copies pick it up within a few hours. You can also run the workflow by hand from the **Actions** tab.

### Project structure

```
magnireader/
├── Program.cs                    # Entry point, Velopack install/update hooks
├── App.xaml.cs                   # Orchestrator, tray icon, reading queue
├── Core/
│   ├── TextExtractor.cs          # UI Automation text extraction + continuous reading
│   ├── LanguageDetector.cs       # Language identification
│   ├── VoiceManager.cs           # Voice discovery (WinRT + SAPI5 + registry)
│   ├── VoiceEnabler.cs           # OneCore voice registration (admin)
│   ├── SpeechEngine.cs           # Speech playback, pause and seek
│   ├── KokoroSpeaker.cs          # Offline Kokoro neural voices
│   ├── WordLocator.cs            # Spoken word → screen rectangle
│   ├── AudioDucker.cs            # Lowers other apps' audio while reading
│   └── VisionAnalyzer.cs         # Gemini / OpenAI vision
├── UI/
│   ├── ControlBarWindow.xaml     # Always-on-top control bar
│   ├── OverlayWindow.xaml        # Dimmed overlay + text and word highlight
│   ├── PlaybackStripWindow.xaml  # Pause / back / forward / stop strip
│   ├── HoverHighlightWindow.xaml # Highlights the element under the cursor
│   ├── SnipWindow.xaml           # Screen area selection
│   ├── VisionResultWindow.xaml   # AI description display
│   └── SettingsWindow.xaml       # Settings
├── Input/
│   └── InputManager.cs           # Global shortcuts + cancel detection
├── Infrastructure/
│   ├── SettingsStore.cs          # JSON settings in %APPDATA%\MagniReader
│   ├── StartupManager.cs         # Launch at Windows startup
│   ├── UpdateService.cs          # Auto-updates from GitHub Releases
│   └── CrashLogger.cs            # Crash reports + diagnostics log
├── Helpers/
│   └── NativeInterop.cs          # Win32 P/Invoke
├── assets/                      # Logo (SVG + PNG)
└── scripts/                      # Standalone admin voice tools
```

## Requirements

- Windows 10 or 11 (x64)
- Optional: a Gemini or OpenAI API key for vision analysis
- Optional: [NaturalVoiceSAPIAdapter](https://github.com/gexgd0419/NaturalVoiceSAPIAdapter) for the Windows Natural voices
- To build: the .NET 8 SDK

## License

MagniReader is licensed under the [GNU Affero General Public License v3.0](LICENSE).

It is built to make reading on screen easier for everyone. You are free to use, modify and share it, and any derivative work must stay open source under the same license, so improvements to accessibility tools remain available to the community.
