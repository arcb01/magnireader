<#
.SYNOPSIS
    Lists every executable file a MagniReader release ships, with its Authenticode signature.

.DESCRIPTION
    Checks Setup.exe, the MSI, and every .exe/.dll inside the full update package
    (the files that end up in the install folder). Prints a table, adds it to the
    GitHub job summary when run in Actions, and with -RequireSigned fails if any
    file is unsigned or has a broken signature.

    Run it locally on a Releases folder made by `vpk pack`:
        ./scripts/check-signatures.ps1 -ReleaseDir Releases

.PARAMETER ReleaseDir
    The folder vpk pack wrote to (default: Releases).

.PARAMETER Version
    The version to check (default: the newest full package in the folder, which may
    also hold the previous release vpk downloaded for delta updates).

.PARAMETER RequireSigned
    Exit with an error if anything is not validly signed.
#>
param(
    [string]$ReleaseDir = 'Releases',
    [string]$Version,
    [switch]$RequireSigned
)

$ErrorActionPreference = 'Stop'
$peExtensions = @('.exe', '.dll', '.msi', '.sys', '.node', '.pyd')

$pattern = if ($Version) { "*-$Version-full.nupkg" } else { '*-full.nupkg' }
$full = Get-ChildItem $ReleaseDir -Filter $pattern | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $full) { throw "No *-full.nupkg in $ReleaseDir. Run vpk pack first." }

$tmp = Join-Path ([IO.Path]::GetTempPath()) "magnireader-sigcheck-$PID"
if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
New-Item -ItemType Directory $tmp | Out-Null
try {
    # A .nupkg is a zip; the app's files are under lib/app/.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($full.FullName, $tmp)
    $appDir = Join-Path $tmp 'lib/app'
    if (-not (Test-Path $appDir)) { $appDir = $tmp }

    $files = @()
    $files += Get-ChildItem $ReleaseDir -File | Where-Object { $_.Extension -in '.exe', '.msi' } |
        ForEach-Object { [pscustomobject]@{ Name = $_.Name; Path = $_.FullName } }
    $files += Get-ChildItem $appDir -Recurse -File | Where-Object { $_.Extension -in $peExtensions } |
        ForEach-Object { [pscustomobject]@{ Name = [IO.Path]::GetRelativePath($appDir, $_.FullName); Path = $_.FullName } }

    $rows = foreach ($f in $files) {
        $sig = Get-AuthenticodeSignature -FilePath $f.Path
        $publisher = if ($sig.SignerCertificate) { $sig.SignerCertificate.GetNameInfo('SimpleName', $false) } else { '' }
        [pscustomobject]@{ File = $f.Name; Status = [string]$sig.Status; Publisher = $publisher }
    }
    $rows = $rows | Sort-Object @{ Expression = { $_.Status -eq 'Valid' } }, File

    $rows | Format-Table -AutoSize | Out-String -Width 200 | Write-Host

    $bad = @($rows | Where-Object { $_.Status -ne 'Valid' })
    $total = @($rows).Count
    Write-Host "$($total - $bad.Count) of $total files are signed."

    if ($env:GITHUB_STEP_SUMMARY) {
        $md = @("### Code signatures", "", "$($total - $bad.Count) of $total files are signed.", "",
                "| File | Signature | Publisher |", "|---|---|---|")
        $md += $rows | ForEach-Object { "| ``$($_.File)`` | $($_.Status) | $($_.Publisher) |" }
        $md -join "`n" | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8
    }

    if ($RequireSigned -and $bad.Count -gt 0) {
        $bad | ForEach-Object { Write-Host "::error::Not signed: $($_.File) ($($_.Status))" }
        throw "$($bad.Count) file(s) are not signed."
    }
}
finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
