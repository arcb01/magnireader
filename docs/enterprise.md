# Deploying MagniReader in an organization

Every release has two installers:

| File | Installs to | Who it's for | Updates |
|---|---|---|---|
| `MagniReader-win-Setup.exe` | `%LocalAppData%\MagniReader` (per user, no admin) | Individuals | Automatic, from GitHub Releases |
| `MagniReader-win.msi` | `C:\Program Files\MagniReader` (per machine, admin) | IT: Intune, Group Policy, SCCM | Deployed by IT |

Use the MSI on managed PCs. Strict policies often block the Setup.exe install, because AppLocker's and WDAC's default rules don't let programs run from folders a user can write to, such as `%LocalAppData%`. The MSI puts MagniReader in Program Files, which standard users can't change.

## Installing the MSI

Silent install for all users:

```cmd
msiexec /i MagniReader-win.msi /qn /l*v "%TEMP%\MagniReader-install.log"
```

Install to another folder:

```cmd
msiexec /i MagniReader-win.msi /qn VELOPACK_INSTALLDIR="D:\Apps\MagniReader"
```

Silent uninstall:

```cmd
msiexec /x MagniReader-win.msi /qn
```

- **Intune**: add it as a *Line-of-business app* (upload the `.msi`), or wrap it as a *Windows app (Win32)* with the commands above. Install behavior: *System*.
- **Group Policy**: *Computer Configuration → Policies → Software Settings → Software installation*, assigned.
- **SCCM / ConfigMgr**: a Windows Installer deployment type with the same commands.

The MSI adds Start menu and desktop shortcuts for everyone. Silent installs don't start the app. Each user's settings stay in `%APPDATA%\MagniReader`.

**To upgrade**, deploy the newer MSI. It replaces the installed version and keeps users' settings.

## Updates

On a per-machine install, MagniReader doesn't update itself, because standard users can't write to Program Files and would only get an admin prompt. Settings → App shows "Updates are managed by your organization", and IT rolls out new versions as MSIs.

You can also set this with a policy (Group Policy Preferences → Registry, or an Intune configuration profile):

| Key | Value | Effect |
|---|---|---|
| `HKLM\SOFTWARE\Policies\MagniReader` | `DisableUpdates` (DWORD) = `1` | No update checks, for any install (also Setup.exe installs) |
| `HKLM\SOFTWARE\Policies\MagniReader` | `DisableUpdates` (DWORD) = `0` | Allow update checks on a per-machine install (users then need admin rights to apply them) |

Without the value, per-user installs update themselves and per-machine installs don't.

## AppLocker and WDAC

Once releases are code-signed, allow MagniReader with a **publisher rule** for its certificate. It then keeps working after every update, unlike a hash rule. Ask the developer for the publisher name and certificate details.

Signing covers every program file in the install folder (see the list below). Third-party files that are already signed keep their original publisher, such as Microsoft for the .NET runtime, so your rules need to allow those publishers too. Most organizations already do.

The MSI also contains a small installer helper DLL from Velopack that runs only during install and uninstall. It isn't signed separately, but it is inside the signed MSI. If your WDAC policy checks the DLLs that `msiexec` loads, allow it by hash or install the MSI from a managed deployment tool.

## GPU voices

The optional GPU voices (Settings → Speech → GPU voices) download Python, PyTorch and uv into `%LOCALAPPDATA%\MagniReader\gpu-tts` and run them from there. Those are third-party programs that MagniReader can't sign, and they live in a user-writable folder, so strict AppLocker or WDAC policies will block them. For managed PCs there's an offline **GPU voice pack** that IT builds once and installs under Program Files. Nothing is downloaded or run from user folders. See [gpu-voices-it.md](gpu-voices-it.md), which also has the firewall/proxy allowlist if you'd rather allow the per-user install.

## What gets signed

The release workflow (`.github/workflows/release.yml`) signs with `vpk` once signing secrets are set. vpk signs every unsigned `.exe` and `.dll` in the package, plus `Setup.exe` and the MSI, and leaves files that are already signed alone. After packing, `scripts/check-signatures.ps1` lists every file with its signature in the job summary, and fails the release if signing is set up but something was missed.

**Signed by us** (they ship unsigned):

| File | From |
|---|---|
| `MagniReader.exe`, `MagniReader.dll` | This repository |
| `MagniReader.exe` launcher in the install root, `Update.exe` | Velopack (start-up stub and updater) |
| `MagniReader-win-Setup.exe`, `MagniReader-win.msi` | Velopack installers |
| `Velopack.dll` | Velopack |
| `Hardcodet.NotifyIcon.Wpf.dll` | Tray icon |
| `KokoroSharp.dll`, `MisakiSharp.dll`, `NumSharp.dll` | Kokoro voices |
| `NAudio.dll`, `NAudio.Core.dll`, `NAudio.Asio.dll`, `NAudio.Midi.dll`, `NAudio.Wasapi.dll`, `NAudio.WinMM.dll`, `NAudio.WinForms.dll` | Audio playback |
| `OpenTK.Audio.OpenAL.dll`, `OpenTK.Core.dll`, `OpenTK.Mathematics.dll` | KokoroSharp dependency |

**Already signed by their publisher** (left as they are):

- The .NET 8 and WPF runtime (the release is self-contained): Microsoft.
- `onnxruntime.dll`, `onnxruntime_providers_shared.dll`, `Microsoft.ML.OnnxRuntime.dll`: Microsoft.
- `System.Speech.dll`, `System.Drawing.Common.dll`, `System.Numerics.Tensors.dll`, `Microsoft.Bcl.Numerics.dll`, `System.Security.Cryptography.ProtectedData.dll`, `Microsoft.Windows.SDK.NET.dll`, `WinRT.Runtime.dll`: Microsoft.
- `Newtonsoft.Json.dll`: Newtonsoft (.NET Foundation).

**Downloaded at run time, not ours to sign:**

- `kokoro.onnx`, the Kokoro model, into `%APPDATA%\MagniReader\kokoro`. It's a data file, not a program, so code-integrity policies don't apply to it.
- GPU voices: `uv.exe`, a Python 3.11 runtime, PyTorch with its CUDA DLLs, the model packages and the model weights, all into `%LOCALAPPDATA%\MagniReader\gpu-tts`. See [GPU voices](#gpu-voices).

The workflow's job summary is the exact list for each release. If a new dependency adds a file, it shows up there.
