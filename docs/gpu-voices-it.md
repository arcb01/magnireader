# GPU voices on managed PCs (guide for IT)

MagniReader's optional GPU voices are neural text-to-speech models that run on an NVIDIA GPU. They need Python, PyTorch with CUDA, and a model of a few GB, about 12 GB in total.

When a user clicks **Install** in Settings, MagniReader downloads all of that into the user's own `%LOCALAPPDATA%` folder and runs it from there. Many company policies block that:

- **AppLocker, WDAC or Software Restriction Policies** refuse to run `python.exe` from the user profile. The install then fails with *"Failed to query Python interpreter … Access is denied (os error 5)"*. This is the usual cause, and it has nothing to do with the firewall: the downloads themselves worked.
- **A proxy or firewall** blocks the download hosts.

The simplest fix is option A: you build the GPU voice pack once and install it under Program Files, like any other application.

## Option A (recommended): install the offline GPU voice pack

The pack is one folder, by default `C:\Program Files\MagniReader GPU Voices`. It contains a private Python with PyTorch, the model weights and the word-highlight model. When MagniReader finds it, it runs the GPU voices from there, fully offline:

- nothing is downloaded on the users' PCs
- nothing runs from a user-writable folder, so default AppLocker and WDAC rules allow it
- standard users can't change it

### 1. Build the pack (once)

Use one Windows PC that can reach the internet. It doesn't need a GPU, but with one the script also test-loads the model. You need **PowerShell 7.2+** (`pwsh`), about 15 GB free per model, and administrator rights.

```powershell
# From a MagniReader checkout, or with the installed app's files (-AppDir):
pwsh -File scripts\build-gpu-pack.ps1 -Engine omnivoice
```

- `-Engine omnivoice` or `-Engine qwen3-0.6b` (or both). Each model is about 10–12 GB.
- `-Output D:\Packs\MagniReader GPU Voices` builds somewhere else.
- `-SkipTest` skips the GPU test on a build PC without an NVIDIA GPU.

The script downloads from the hosts in the [allowlist](#hosts-to-allow) below. It uses the Windows proxy settings and certificate store, so TLS-inspecting proxies work.

> **Licenses:** OmniVoice's model weights are **CC BY-NC 4.0, non-commercial use only**. Using them at work may not be allowed. **Qwen3-TTS (`qwen3-0.6b`) is Apache 2.0** and is fine for commercial use. Choose `-Engine qwen3-0.6b` unless your use is non-commercial.

### 2. Deploy it

Copy the finished folder to the same path on every PC that has MagniReader. Any tool works:

- **Intune**: package the folder with an install command such as `robocopy . "%ProgramFiles%\MagniReader GPU Voices" /E` (Win32 app, System context). Use detection on `manifest.json`.
- **SCCM / ConfigMgr**: an application or package with the same robocopy command.
- **Manually**: copy the folder as administrator.

MagniReader picks it up the next time Settings opens. Settings → Speech → GPU voices then says *"installed by your organization"*. The PC needs an NVIDIA GPU with 6 GB or more and driver 570 or newer.

### 3. Optional policies

Set these under `HKLM\SOFTWARE\Policies\MagniReader`, through Group Policy Preferences → Registry or an Intune configuration profile:

| Value | Type | Effect |
|---|---|---|
| `GpuVoicePackDirectory` | String | Use a pack in another folder, e.g. `D:\Apps\MagniReader GPU Voices` |
| `DisableGpuVoiceDownload` | DWORD = 1 | Hide the Install button, so users only get the pack you deploy |
| `DisableUpdates` | DWORD = 1 | Turn off MagniReader's own updates (see [enterprise.md](enterprise.md)) |

### 4. Code signing and WDAC

The pack contains thousands of third-party files: Python, PyTorch, CUDA libraries and `.pyd` modules. Some are signed by their publishers, such as NVIDIA's CUDA DLLs, and many aren't. Under WDAC, choose one of these:

- **Catalog (recommended):** the script writes `MagniReaderGpuVoices.cat`, which lists the hash of every file. Sign it with your organization's code-signing certificate and add it to your WDAC catalog trust, or pass `-SignTemplate` and the script signs it for you.
- **Sign the files:** `-SignTemplate 'signtool sign /sha1 <thumbprint> /fd sha256 /tr <timestamp URL> /td sha256 "{{file}}"'` signs every unsigned `.exe`, `.dll` and `.pyd`, then the catalog. This takes a while.
- **Path rule:** allow `%ProgramFiles%\MagniReader GPU Voices\*`, which standard users can't write to. AppLocker's default rules already allow it.

### Updating the pack

The pack records which package versions it was built for. If a MagniReader update changes them, Settings shows a warning, and you rebuild and redeploy the pack. Most MagniReader updates don't change them.

### Limitations

- Voice clips that users add must include their `.txt` transcript. Without one, the model would need to download a speech recognizer, which an offline pack can't do.
- Logs are written to `%LOCALAPPDATA%\MagniReader\gpu-tts\logs` and a small speech cache to `%LOCALAPPDATA%\MagniReader\gpu-tts\cache`. These are data files, not programs.

## Option B: allow the per-user install

Use this only if you'd rather not deploy a pack. You need **both** of these.

### Programs to allow

All of these run from the user's profile, which is why default rules block them:

| Program | Path |
|---|---|
| uv (Python installer) | `%LOCALAPPDATA%\MagniReader\gpu-tts\uv\uv.exe` |
| Python | `%LOCALAPPDATA%\MagniReader\gpu-tts\python\cpython-3.11.*\python.exe` |
| Python (per model) | `%LOCALAPPDATA%\MagniReader\gpu-tts\envs\<model>\Scripts\python.exe` |
| Python modules and CUDA DLLs | `*.pyd` and `*.dll` under `%LOCALAPPDATA%\MagniReader\gpu-tts\` (if DLL rules are enforced) |

That means an AppLocker path rule for `%LOCALAPPDATA%\MagniReader\gpu-tts\*` (Executable and DLL rules), or the WDAC equivalent. These files aren't signed by MagniReader, and many aren't signed at all, so publisher rules can't cover them.

MagniReader also runs `nvidia-smi.exe` from `C:\Windows\System32` to detect the GPU. It's signed by NVIDIA and normally allowed.

### Hosts to allow

All connections are outbound HTTPS (TCP 443) and happen only while installing. Once installed, the GPU voices run offline.

| Host | Used for |
|---|---|
| `github.com`, `release-assets.githubusercontent.com`, `objects.githubusercontent.com` | uv and the Python runtime (python-build-standalone) |
| `pypi.org`, `files.pythonhosted.org` | Python packages |
| `download.pytorch.org` | PyTorch with CUDA (about 3 GB), and the word-highlight model |
| `huggingface.co`, `*.hf.co` (e.g. `cdn-lfs.hf.co`, `cas-bridge.xethub.hf.co`, `transfer.xethub.hf.co`) | Model weights |

- **Proxy:** MagniReader passes the Windows proxy settings, including PAC scripts, on to the installer. The installer can't authenticate to a proxy with NTLM or Kerberos, so allow these hosts without authentication, or use option A.
- **TLS inspection:** the installer and Python use the Windows certificate store, so your inspection root certificate works if it's deployed there.
- **Firewall:** no inbound rule is needed. MagniReader talks to its voice server only over `127.0.0.1` (loopback), on a random port, with a random access token.

### What gets written

| Folder | Contents |
|---|---|
| `%LOCALAPPDATA%\MagniReader\gpu-tts` | uv, Python, package cache, model weights (`hf`), word-highlight model (`torch`), logs, speech cache (about 12 GB per model) |
| `%APPDATA%\MagniReader\gpu-voices` | The user's own voice clips |
