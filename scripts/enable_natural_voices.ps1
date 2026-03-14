# ──────────────────────────────────────────────────────────────
#  MagniReader Natural Voice Enabler
#  
#  Registers Natural voice AppX packages (Elvira, Guy, etc.)
#  into the Speech_OneCore registry so they become available
#  to the WinRT SpeechSynthesis API.
#
#  Run ONCE as Administrator:
#    Right-click PowerShell → "Run as administrator"
#    Then: .\enable_natural_voices.ps1
# ──────────────────────────────────────────────────────────────

#Requires -RunAsAdministrator

Write-Host ""
Write-Host "  ╔══════════════════════════════════════════╗" -ForegroundColor Yellow
Write-Host "  ║   MagniReader Natural Voice Enabler      ║" -ForegroundColor Yellow
Write-Host "  ╚══════════════════════════════════════════╝" -ForegroundColor Yellow
Write-Host ""

$onecorePath = "HKLM:\SOFTWARE\Microsoft\Speech_OneCore\Voices\Tokens"
$enabled = 0
$skipped = 0

# Find all Natural voice AppX packages
$voicePackages = Get-AppxPackage -Name "MicrosoftWindows.Voice.*"

if (-not $voicePackages) {
    Write-Host "  No Natural voice packages found." -ForegroundColor Red
    Write-Host "  Install them: Settings → Accessibility → Narrator → Add natural voices" -ForegroundColor Gray
    Read-Host "`n  Press Enter to exit"
    exit 1
}

Write-Host "  Found $($voicePackages.Count) Natural voice packages:" -ForegroundColor Cyan
Write-Host ""

foreach ($pkg in $voicePackages) {
    $tokensFile = Join-Path $pkg.InstallLocation "Tokens.xml"
    
    if (-not (Test-Path $tokensFile)) {
        Write-Host "    ⚠ $($pkg.Name) - no Tokens.xml found" -ForegroundColor Gray
        continue
    }
    
    # Parse the Tokens.xml to get voice registration info
    $xml = [xml](Get-Content $tokensFile)
    $token = $xml.Tokens.Category.Token
    
    if (-not $token) {
        Write-Host "    ⚠ $($pkg.Name) - no token data" -ForegroundColor Gray
        continue
    }
    
    $tokenName = $token.name
    $voiceDisplayName = ($token.String | Where-Object { $_.name -eq "" }).value
    $clsid = ($token.String | Where-Object { $_.name -eq "CLSID" }).value
    $langDataPath = ($token.String | Where-Object { $_.name -eq "LangDataPath" }).value
    $voicePath = ($token.String | Where-Object { $_.name -eq "VoicePath" }).value
    
    # Replace [INSTALLDIR] with actual path
    $installDir = $pkg.InstallLocation + "\"
    $langDataPath = $langDataPath -replace '\[INSTALLDIR\]', $installDir
    $voicePath = $voicePath -replace '\[INSTALLDIR\]', $installDir
    
    # Check if already registered
    $existingKey = Get-Item "$onecorePath\$tokenName" -ErrorAction SilentlyContinue
    if ($existingKey) {
        Write-Host "    ✅ $voiceDisplayName (already enabled)" -ForegroundColor Green
        $skipped++
        continue
    }
    
    Write-Host "    🔓 Enabling: $voiceDisplayName..." -ForegroundColor Yellow -NoNewline
    
    try {
        # Create the OneCore token
        $keyPath = "$onecorePath\$tokenName"
        New-Item -Path $keyPath -Force | Out-Null
        
        # Set default value (display name)
        Set-ItemProperty -Path $keyPath -Name '(Default)' -Value $voiceDisplayName
        
        # Set CLSID for the Neural TTS engine
        Set-ItemProperty -Path $keyPath -Name 'CLSID' -Value $clsid
        
        # Set paths
        Set-ItemProperty -Path $keyPath -Name 'LangDataPath' -Value $langDataPath
        Set-ItemProperty -Path $keyPath -Name 'VoicePath' -Value $voicePath
        
        # Create Attributes subkey
        $attrsPath = "$keyPath\Attributes"
        New-Item -Path $attrsPath -Force | Out-Null
        
        foreach ($attr in $token.Attribute) {
            Set-ItemProperty -Path $attrsPath -Name $attr.name -Value $attr.value
        }
        
        Write-Host " Done!" -ForegroundColor Green
        $enabled++
    }
    catch {
        Write-Host " FAILED: $($_.Exception.Message)" -ForegroundColor Red
    }
}

Write-Host ""
Write-Host "  ─────────────────────────────────────────" -ForegroundColor Gray
Write-Host "  Enabled: $enabled new Natural voices" -ForegroundColor $(if ($enabled -gt 0) { "Green" } else { "Gray" })
Write-Host "  Already had: $skipped voices" -ForegroundColor Gray
Write-Host ""

if ($enabled -gt 0) {
    Write-Host "  ✅ Done! Restart MagniReader to use Natural voices." -ForegroundColor Green
    Write-Host "  ⭐ Natural voices will automatically be selected as primary!" -ForegroundColor Yellow
} else {
    Write-Host "  All Natural voices were already enabled." -ForegroundColor Green
}

Write-Host ""
Read-Host "  Press Enter to exit"
