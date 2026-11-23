<#
.SYNOPSIS
    Builds the offline MagniReader GPU voice pack that IT deploys per machine.

.DESCRIPTION
    Downloads everything the GPU voices need ONCE, on a machine that may reach the
    internet, into one folder (by default C:\Program Files\MagniReader GPU Voices):

        envs\<env>\python.exe   standalone Python 3.11 with PyTorch (CUDA) and the model's packages
        models\<model>\         the model weights from Hugging Face
        torch\                  the word-aligner weights (torchaudio MMS_FA)
        manifest.json           what MagniReader reads to find all of the above

    MagniReader finds the pack on its own and then runs the GPU voices from it
    offline: no downloads and no programs in user-writable folders, so AppLocker or
    WDAC rules that only allow Program Files are satisfied. Copy the finished folder
    to other PCs with any deployment tool; the pack doesn't depend on its location
    (point MagniReader at another folder with the GpuVoicePackDirectory policy).

    Needs PowerShell 7.2 or newer (pwsh). Run it as administrator when the output is under Program Files. Building needs
    about 15 GB of free disk per model plus ~5 GB of temporary space.
    See docs/gpu-voices-it.md for the whole procedure.

.PARAMETER Engine
    Models to include: omnivoice, qwen3-0.6b, or both. Default: omnivoice.
    Note: OmniVoice's weights are licensed CC BY-NC 4.0 (non-commercial). Qwen3-TTS is Apache 2.0.

.PARAMETER Output
    Where to build the pack. Default: C:\Program Files\MagniReader GPU Voices.

.PARAMETER AppDir
    A MagniReader folder that contains GpuTts\ (requirements, server, voices). Default: the
    installed per-machine app, else this repository.

.PARAMETER SignTemplate
    Optional signing command run for every unsigned .exe/.dll/.pyd in the pack and for the
    catalog, where {{file}} is the file, e.g.
    'signtool sign /sha1 <thumbprint> /fd sha256 /tr http://timestamp.digicert.com /td sha256 "{{file}}"'

.PARAMETER NoCatalog
    Skip the file catalog (MagniReaderGpuVoices.cat). The catalog lists the hash of every
    file; signed with your code-signing certificate, it lets WDAC trust the whole pack.

.PARAMETER SkipTest
    Don't load the model on this PC's GPU after building (use on a build machine without an NVIDIA GPU).

.EXAMPLE
    # On one PC with internet access, as administrator:
    .\build-gpu-pack.ps1 -Engine omnivoice
#>
#Requires -Version 7.2
[CmdletBinding()]
param(
    [ValidateSet('omnivoice', 'qwen3-0.6b')]
    [string[]]$Engine = @('omnivoice'),
    [string]$Output = (Join-Path $env:ProgramFiles 'MagniReader GPU Voices'),
    [string]$AppDir,
    [string]$SignTemplate,
    [switch]$NoCatalog,
    [switch]$SkipTest
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# Keep in step with Core\GpuVoiceSpeaker.cs
$UvVersion = '0.13.9'
$PythonVersion = '3.11'
$Engines = @{
    'omnivoice'  = @{ Env = 'omnivoice'; Repo = 'k2-fsa/OmniVoice'; Extra = @{ 'audio_tokenizer' = 'eustlb/higgs-audio-v2-tokenizer' } }
    'qwen3-0.6b' = @{ Env = 'qwen3'; Repo = 'Qwen/Qwen3-TTS-12Hz-0.6B-Base'; Extra = @{} }
}

function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }

function Invoke-Checked([string]$exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$([IO.Path]::GetFileName($exe)) failed (exit $LASTEXITCODE)" }
}

# ── Inputs ──────────────────────────────────────────────────────────
if (-not $AppDir) {
    $installed = Join-Path $env:ProgramFiles 'MagniReader\current'
    $AppDir = if (Test-Path (Join-Path $installed 'GpuTts')) { $installed } else { Split-Path $PSScriptRoot -Parent }
}
$GpuTts = Join-Path $AppDir 'GpuTts'
if (-not (Test-Path (Join-Path $GpuTts 'magni_tts_server.py'))) { throw "GpuTts folder not found in $AppDir. Pass -AppDir." }
Write-Host "MagniReader files: $GpuTts"
Write-Host "Pack folder:       $Output"

New-Item -ItemType Directory -Force $Output | Out-Null
$Work = Join-Path ([IO.Path]::GetTempPath()) 'magnireader-gpu-pack'
New-Item -ItemType Directory -Force $Work | Out-Null

# Downloads go through the system proxy and trust the Windows certificate store
$env:UV_SYSTEM_CERTS = '1'
$env:UV_CACHE_DIR = Join-Path $Work 'uv-cache'
$env:UV_LINK_MODE = 'copy'
$env:HF_HOME = Join-Path $Work 'hf'
$env:HF_HUB_DISABLE_TELEMETRY = '1'
$env:PYTHONUTF8 = '1'
$env:PYTHONNOUSERSITE = '1'
Remove-Item Env:PYTHONPATH, Env:PYTHONHOME, Env:VIRTUAL_ENV -ErrorAction SilentlyContinue
if (-not $env:HTTPS_PROXY) {
    try {
        $target = [Uri]'https://pypi.org/simple/'
        $proxy = [System.Net.WebRequest]::GetSystemWebProxy().GetProxy($target)
        if ($proxy -and $proxy.Host -ne $target.Host) {
            $env:HTTPS_PROXY = $env:HTTP_PROXY = "$($proxy.Scheme)://$($proxy.Authority)"
            Write-Host "Using the system proxy $env:HTTPS_PROXY"
        }
    } catch { }
}

# ── uv and Python ───────────────────────────────────────────────────
Step "uv $UvVersion"
$uv = Join-Path $Work "uv-$UvVersion\uv.exe"
if (-not (Test-Path $uv)) {
    $zip = Join-Path $Work 'uv.zip'
    Invoke-WebRequest "https://github.com/astral-sh/uv/releases/download/$UvVersion/uv-x86_64-pc-windows-msvc.zip" -OutFile $zip -UseBasicParsing
    Expand-Archive $zip -DestinationPath (Split-Path $uv) -Force
    Remove-Item $zip
}

Step "Python $PythonVersion"
$pyRoot = Join-Path $Work 'python'
Invoke-Checked $uv @('python', 'install', $PythonVersion, '--install-dir', $pyRoot)
$basePython = Get-ChildItem $pyRoot -Directory |
    Where-Object { $_.Name -match "^cpython-$([regex]::Escape($PythonVersion))\.\d+-windows-x86_64" -and (Test-Path (Join-Path $_.FullName 'python.exe')) } |
    Sort-Object Name -Descending | Select-Object -First 1
if (-not $basePython) { throw "uv did not install Python $PythonVersion under $pyRoot" }

$manifestPath = Join-Path $Output 'manifest.json'
$manifest = if (Test-Path $manifestPath) { Get-Content $manifestPath -Raw | ConvertFrom-Json -AsHashtable } else { @{} }
if (-not $manifest.engines) { $manifest.engines = @{} }
$torchHome = Join-Path $Output 'torch'

foreach ($id in $Engine) {
    $info = $Engines[$id]
    $envDir = Join-Path $Output "envs\$($info.Env)"
    $python = Join-Path $envDir 'python.exe'
    $requirements = Join-Path $GpuTts "requirements\$($info.Env).txt"
    $modelDir = Join-Path $Output ("models\" + ($info.Repo -split '/')[-1])

    # A copy of the standalone Python is the environment: no venv, so the folder can move.
    Step "$id`: Python environment in $envDir"
    if (-not (Test-Path $python)) {
        New-Item -ItemType Directory -Force $envDir | Out-Null
        robocopy $basePython.FullName $envDir /E /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "Copying Python failed (robocopy $LASTEXITCODE)" }
    }

    Step "$id`: PyTorch (CUDA) and the model's packages (several GB)"
    Invoke-Checked $uv @('pip', 'install', '--python', $python, '--break-system-packages',
        '-r', $requirements, '--index-strategy', 'unsafe-best-match')

    Step "$id`: model weights ($($info.Repo))"
    $download = @"
import sys
try:
    import truststore; truststore.inject_into_ssl()
except Exception:
    pass
from huggingface_hub import snapshot_download
repo, dest = sys.argv[1], sys.argv[2]
snapshot_download(repo, local_dir=dest)
"@
    $dl = Join-Path $Work 'download.py'
    Set-Content -Path $dl -Value $download -Encoding utf8
    Invoke-Checked $python @($dl, $info.Repo, $modelDir)
    foreach ($sub in $info.Extra.Keys) {
        $subDir = Join-Path $modelDir $sub
        if (-not (Test-Path (Join-Path $subDir 'config.json'))) { Invoke-Checked $python @($dl, $info.Extra[$sub], $subDir) }
    }

    Step "$id`: word-aligner weights"
    $env:TORCH_HOME = $torchHome
    Invoke-Checked $python @('-c', 'import truststore; truststore.inject_into_ssl(); import torchaudio; torchaudio.pipelines.MMS_FA.get_model(with_star=False)')
    Remove-Item Env:TORCH_HOME

    # Users can't write to Program Files, so compile the .pyc files now (faster start-up).
    Step "$id`: precompiling Python files"
    & $python -m compileall -q -j 0 (Join-Path $envDir 'Lib') | Out-Null

    $reqText = (Get-Content $requirements -Raw) -replace "`r", ''
    $sha = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($reqText)))
    $manifest.engines[$id] = [ordered]@{
        python = [IO.Path]::GetRelativePath($Output, $python)
        model = [IO.Path]::GetRelativePath($Output, $modelDir)
        requirementsSha256 = $sha
    }
}

$manifest.format = 1
$manifest.torchHome = 'torch'
$manifest.built = (Get-Date).ToString('o')
$manifest.python = $basePython.Name
$manifest | ConvertTo-Json -Depth 5 | Set-Content -Path $manifestPath -Encoding utf8
Write-Host "Wrote $manifestPath"

# ── Test on this PC's GPU, fully offline ────────────────────────────
if (-not $SkipTest -and (Get-Command nvidia-smi -ErrorAction SilentlyContinue)) {
    foreach ($id in $Engine) {
        Step "$id`: test load on the GPU (offline)"
        $e = $manifest.engines[$id]
        $psi = [Diagnostics.ProcessStartInfo]::new((Join-Path $Output $e.python))
        foreach ($a in @('-u', (Join-Path $GpuTts 'magni_tts_server.py'), '--engine', $id, '--voices', (Join-Path $GpuTts 'voices'))) { $psi.ArgumentList.Add($a) }
        $psi.UseShellExecute = $false; $psi.RedirectStandardOutput = $true
        $psi.Environment['MAGNI_MODEL_PATH'] = Join-Path $Output $e.model
        $psi.Environment['TORCH_HOME'] = $torchHome
        $psi.Environment['HF_HUB_OFFLINE'] = '1'; $psi.Environment['TRANSFORMERS_OFFLINE'] = '1'
        $psi.Environment['HF_HOME'] = Join-Path $Work 'hf-test'
        $psi.Environment['MAGNI_TTS_CACHE'] = Join-Path $Work 'cache'
        $psi.Environment['MAGNI_TTS_TOKEN'] = 'test'
        $p = [Diagnostics.Process]::Start($psi)
        try {
            $line = $null
            while (-not $p.HasExited -and -not ($line -like '{*')) { $line = $p.StandardOutput.ReadLine() }
            if ($line -notlike '*"ready"*') { throw "The $id server did not start: $line" }
            Write-Host "OK: $line" -ForegroundColor Green
        } finally { if (-not $p.HasExited) { $p.Kill($true) } }
    }
} elseif (-not $SkipTest) {
    Write-Warning 'No NVIDIA GPU on this PC: the pack was built but not test-loaded.'
}

# ── Signing hooks ───────────────────────────────────────────────────
if ($SignTemplate) {
    Step 'Signing unsigned programs and libraries'
    $files = Get-ChildItem $Output -Recurse -File -Include *.exe, *.dll, *.pyd |
        Where-Object { (Get-AuthenticodeSignature $_.FullName).Status -eq 'NotSigned' }
    Write-Host "$($files.Count) files to sign"
    foreach ($f in $files) {
        cmd /c ($SignTemplate.Replace('{{file}}', $f.FullName))
        if ($LASTEXITCODE -ne 0) { throw "Signing failed for $($f.FullName)" }
    }
}
if (-not $NoCatalog) {
    Step 'File catalog'
    $cat = Join-Path $Output 'MagniReaderGpuVoices.cat'
    if (Test-Path $cat) { Remove-Item $cat }
    New-FileCatalog -Path $Output -CatalogFilePath $cat -CatalogVersion 2.0 | Out-Null
    if ($SignTemplate) { cmd /c ($SignTemplate.Replace('{{file}}', $cat)); if ($LASTEXITCODE -ne 0) { throw 'Signing the catalog failed' } }
    Write-Host "Wrote $cat"
}

$size = (Get-ChildItem $Output -Recurse -File | Measure-Object Length -Sum).Sum / 1GB
Write-Host ("`nDone: {0:N1} GB in {1}" -f $size, $Output) -ForegroundColor Green
Write-Host "Temporary files in $Work can be deleted."
