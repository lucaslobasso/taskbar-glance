<#
.SYNOPSIS
  Builds Taskbar Glance from source (Native AOT) and installs it to %LOCALAPPDATA%\TaskbarGlance, starting with Windows.
.PARAMETER City
  City for the weather, e.g. "Buenos Aires" or "La Plata, Argentina". Only used when there are no settings yet;
  later, edit glance.json (right-click the strip > Open settings file).
.PARAMETER NoAutostart
  Don't add the HKCU Run entry (you can still toggle it from the strip's right-click menu).
.NOTES
  Needs the .NET 11 SDK and the Visual Studio "Desktop development with C++" workload (MSVC linker + Windows SDK)
  for Native AOT. Run from the repo root: .\install.ps1 -City "Buenos Aires"
  Works in Windows PowerShell 5.1 and PowerShell 7.
#>
param([string]$City = '', [switch]$NoAutostart)
$ErrorActionPreference = 'Stop'

$dest = Join-Path $env:LOCALAPPDATA 'TaskbarGlance'
$exe = Join-Path $dest 'TaskbarGlance.exe'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$out = Join-Path ([IO.Path]::GetTempPath()) 'taskbar-glance-publish'

Write-Host 'Building (Native AOT)...'
dotnet publish (Join-Path $PSScriptRoot 'src\TaskbarGlance.csproj') -c Release -o $out --nologo | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Build failed. Native AOT needs the VS "Desktop development with C++" workload.' }

# only the installed copy: a test copy started from elsewhere keeps running
Get-Process TaskbarGlance -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
Start-Sleep -Milliseconds 300
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item (Join-Path $out 'TaskbarGlance.exe') $exe -Force
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue

$settings = Join-Path $dest 'glance.json'
if ($City -and -not (Test-Path $settings)) {
    $json = @{ city = $City } | ConvertTo-Json
    [IO.File]::WriteAllText($settings, $json) # UTF-8 without BOM; the app fills in the other settings
}

if ($NoAutostart) { Remove-ItemProperty $runKey -Name TaskbarGlance -ErrorAction SilentlyContinue }
else { Set-ItemProperty $runKey -Name TaskbarGlance -Value "`"$exe`"" }
Start-Process $exe
$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "Installed $exe ($size MB) and started it." -ForegroundColor Green
if (-not (Test-Path $settings) -or -not $City) { Write-Host 'If the strip says "Set city": click it, fill in "city" in glance.json and save.' }
