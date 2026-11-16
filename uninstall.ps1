<#
.SYNOPSIS
  Stops Taskbar Glance and removes the autostart entry and the exe.
.PARAMETER Purge
  Also delete the settings (glance.json), the saved forecast and the log.
#>
param([switch]$Purge)

$dest = Join-Path $env:LOCALAPPDATA 'TaskbarGlance'
$exe = Join-Path $dest 'TaskbarGlance.exe'
Get-Process TaskbarGlance -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
Start-Sleep -Milliseconds 300
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name TaskbarGlance -ErrorAction SilentlyContinue
if ($Purge) { Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue }
else { Remove-Item $exe -Force -ErrorAction SilentlyContinue }
Write-Host 'Taskbar Glance removed.' -ForegroundColor Green
