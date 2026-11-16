<#
.SYNOPSIS
  Stops Taskbar Glance and removes the autostart entry and the exe.
.PARAMETER Purge
  Also delete the settings (glance.json), the saved forecast and the log.
#>
param([switch]$Purge)
. (Join-Path $PSScriptRoot 'install-lib.ps1')
Uninstall-Glance -Purge:$Purge
