# Shared by install.ps1 (build from source), bootstrap.ps1 (one-line install of a release) and uninstall.ps1.
# Works in Windows PowerShell 5.1 and PowerShell 7.

$GlanceRepo = 'lucaslobasso/taskbar-glance'
$RunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

function Get-GlanceDir { Join-Path $env:LOCALAPPDATA 'TaskbarGlance' }

# Only the installed copy: a test copy started from elsewhere keeps running.
function Stop-Glance {
    $exe = Join-Path (Get-GlanceDir) 'TaskbarGlance.exe'
    Get-Process TaskbarGlance -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
    Start-Sleep -Milliseconds 300
}

# Copies the exe into place, writes the first settings, sets autostart and starts it.
function Install-GlanceExe([string]$Source, [string]$City = '', [switch]$NoAutostart) {
    $dest = Get-GlanceDir
    $exe = Join-Path $dest 'TaskbarGlance.exe'
    Stop-Glance
    New-Item -ItemType Directory -Force $dest | Out-Null
    Copy-Item $Source $exe -Force

    $settings = Join-Path $dest 'glance.json'
    if ($City -and -not (Test-Path $settings)) {
        $json = @{ city = $City } | ConvertTo-Json
        [IO.File]::WriteAllText($settings, $json) # UTF-8 without BOM; the app fills in the other settings
    }

    if ($NoAutostart) { Remove-ItemProperty $RunKey -Name TaskbarGlance -ErrorAction SilentlyContinue }
    else { Set-ItemProperty $RunKey -Name TaskbarGlance -Value "`"$exe`"" }
    Start-Process $exe
    $size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host "Installed $exe ($size MB) and started it." -ForegroundColor Green
    if (-not $NoAutostart) { Write-Host 'It starts with Windows (toggle in the right-click menu).' }
    if (-not (Test-Path $settings)) { Write-Host 'The strip says "Set city": click it, fill in "city" in glance.json and save.' }
}

function Uninstall-Glance([switch]$Purge) {
    Stop-Glance
    Remove-ItemProperty $RunKey -Name TaskbarGlance -ErrorAction SilentlyContinue
    $dest = Get-GlanceDir
    if ($Purge) { Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue }
    else { Remove-Item (Join-Path $dest 'TaskbarGlance.exe') -Force -ErrorAction SilentlyContinue }
    Write-Host 'Taskbar Glance removed.' -ForegroundColor Green
}
