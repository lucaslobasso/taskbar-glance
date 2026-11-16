<#
.SYNOPSIS
  Builds Taskbar Glance from source (Native AOT) and installs it to %LOCALAPPDATA%\TaskbarGlance, starting with Windows.
  Most people want the one-line install of a release instead (see the README); this is for contributors.
.PARAMETER City
  City for the weather, e.g. "Buenos Aires" or "La Plata, Argentina". Only used when there are no settings yet;
  later, edit glance.json (right-click the strip > Open settings file).
.PARAMETER NoAutostart
  Don't add the HKCU Run entry (you can still toggle it from the strip's right-click menu).
.NOTES
  Needs the .NET 11 SDK and the Visual Studio "Desktop development with C++" workload (MSVC linker + Windows SDK)
  for Native AOT. Run from the repo root: .\install.ps1 -City "Buenos Aires"
#>
param([string]$City = '', [switch]$NoAutostart)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'install-lib.ps1')

$out = Join-Path ([IO.Path]::GetTempPath()) 'taskbar-glance-publish'
Write-Host 'Building (Native AOT)...'
dotnet publish (Join-Path $PSScriptRoot 'src\TaskbarGlance.csproj') -c Release -o $out --nologo | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Build failed. Native AOT needs the VS "Desktop development with C++" workload.' }

Install-GlanceExe (Join-Path $out 'TaskbarGlance.exe') -City $City -NoAutostart:$NoAutostart
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
