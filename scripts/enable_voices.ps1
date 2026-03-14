# ──────────────────────────────────────────────────────────────
#  MagniReader Voice Enabler
#  
#  This script makes Windows OneCore voices (used by Narrator)
#  accessible to MagniReader's speech engine (SAPI5).
#  
#  Run this script ONCE as Administrator:
#    Right-click PowerShell → "Run as administrator"
#    Then: .\enable_voices.ps1
# ──────────────────────────────────────────────────────────────

#Requires -RunAsAdministrator

Write-Host ""
Write-Host "  ╔══════════════════════════════════════╗" -ForegroundColor Yellow
Write-Host "  ║   MagniReader Voice Enabler          ║" -ForegroundColor Yellow
Write-Host "  ╚══════════════════════════════════════╝" -ForegroundColor Yellow
Write-Host ""

$onecorePath = "HKLM:\SOFTWARE\Microsoft\Speech_OneCore\Voices\Tokens"
$sapiPath    = "HKLM:\SOFTWARE\Microsoft\Speech\Voices\Tokens"

# Check OneCore voices
$onecoreVoices = Get-ChildItem $onecorePath -ErrorAction SilentlyContinue
if (-not $onecoreVoices) {
    Write-Host "  No OneCore voices found." -ForegroundColor Red
    Write-Host "  Install voices: Settings → Time & Language → Speech" -ForegroundColor Gray
    exit 1
}

# Check existing SAPI5 voices
$sapiVoices = (Get-ChildItem $sapiPath -ErrorAction SilentlyContinue).PSChildName

Write-Host "  Found OneCore voices:" -ForegroundColor Cyan
$enabled = 0
$skipped = 0

foreach ($voice in $onecoreVoices) {
    $voiceName = (Get-ItemProperty $voice.PSPath -ErrorAction SilentlyContinue).'(default)'
    if (-not $voiceName) { $voiceName = $voice.PSChildName }
    
    $attrs = Get-ItemProperty "$($voice.PSPath)\Attributes" -ErrorAction SilentlyContinue
    $lang = if ($attrs.Language) { $attrs.Language } else { "unknown" }
    
    if ($sapiVoices -contains $voice.PSChildName) {
        Write-Host "    ✅ $voiceName (already enabled)" -ForegroundColor Green
        $skipped++
        continue
    }
    
    Write-Host "    🔓 Enabling: $voiceName..." -ForegroundColor Yellow -NoNewline
    
    try {
        # Create the SAPI5 token key
        $newKeyPath = "$sapiPath\$($voice.PSChildName)"
        New-Item -Path $newKeyPath -Force | Out-Null
        
        # Copy default value (display name)
        if ($voiceName) {
            Set-ItemProperty -Path $newKeyPath -Name '(Default)' -Value $voiceName
        }
        
        # Copy voice data paths
        $voiceProps = Get-ItemProperty $voice.PSPath -ErrorAction SilentlyContinue
        foreach ($prop in @('VoicePath', 'LangDataPath')) {
            if ($voiceProps.$prop) {
                Set-ItemProperty -Path $newKeyPath -Name $prop -Value $voiceProps.$prop
            }
        }
        
        # Set the CLSID for the OneCore TTS engine
        Set-ItemProperty -Path $newKeyPath -Name 'CLSID' -Value '{179F3D56-1B0B-42B2-A962-59B7EF59FE1B}'
        
        # Copy Attributes subkey (Language, Gender, Name, Age, etc.)
        if ($attrs) {
            $attrsPath = "$newKeyPath\Attributes"
            New-Item -Path $attrsPath -Force | Out-Null
            
            foreach ($prop in $attrs.PSObject.Properties) {
                if ($prop.Name -notmatch '^PS') {
                    Set-ItemProperty -Path $attrsPath -Name $prop.Name -Value $prop.Value -ErrorAction SilentlyContinue
                }
            }
        }
        
        Write-Host " Done!" -ForegroundColor Green
        $enabled++
    }
    catch {
        Write-Host " FAILED: $($_.Exception.Message)" -ForegroundColor Red
    }
}

Write-Host ""
Write-Host "  ─────────────────────────────────────" -ForegroundColor Gray
Write-Host "  Enabled: $enabled new voices" -ForegroundColor Green
Write-Host "  Already had: $skipped voices" -ForegroundColor Gray
Write-Host ""

if ($enabled -gt 0) {
    Write-Host "  ✅ Done! Restart MagniReader to use the new voices." -ForegroundColor Green
    Write-Host ""
    Write-Host "  TIP: For even better voices, download Natural voices:" -ForegroundColor Cyan
    Write-Host "    Settings → Accessibility → Narrator → Add natural voices" -ForegroundColor Gray
} else {
    Write-Host "  All OneCore voices were already enabled." -ForegroundColor Green
    Write-Host ""
    Write-Host "  TIP: Download Natural voices for the best quality:" -ForegroundColor Cyan
    Write-Host "    Settings → Accessibility → Narrator → Add natural voices" -ForegroundColor Gray
}

Write-Host ""
Read-Host "  Press Enter to exit"
