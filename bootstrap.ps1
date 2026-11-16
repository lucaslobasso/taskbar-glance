# bootstrap.ps1: one-line install of Taskbar Glance from the latest release. No .NET SDK, no git.
#
#   irm https://raw.githubusercontent.com/lucaslobasso/taskbar-glance/main/bootstrap.ps1 | iex
#
# With options (-City only applies when there are no settings yet):
#   & ([scriptblock]::Create((irm https://raw.githubusercontent.com/lucaslobasso/taskbar-glance/main/bootstrap.ps1))) -City "Buenos Aires" -NoAutostart
#
# Uninstall the same way (-Purge also deletes settings and the log):
#   & ([scriptblock]::Create((irm https://raw.githubusercontent.com/lucaslobasso/taskbar-glance/main/bootstrap.ps1))) -Uninstall
#
# Downloads TaskbarGlance.exe from the latest GitHub release, checks it against the release's SHA-256 and installs it
# to %LOCALAPPDATA%\TaskbarGlance with autostart.
param(
    [string]$City = '',
    [switch]$Uninstall,
    [switch]$Purge,
    [switch]$NoAutostart,
    [string]$Tag  # a specific release, e.g. v1.0.0 (default: the latest)
)

$ErrorActionPreference = 'Stop'
$repo = 'lucaslobasso/taskbar-glance'
$headers = @{ 'User-Agent' = 'taskbar-glance-bootstrap' }
$work = Join-Path ([IO.Path]::GetTempPath()) ('taskbar-glance-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $work | Out-Null
try {
    # The shared install functions come from the same version of the repo as the exe.
    $api = if ($Tag) { "https://api.github.com/repos/$repo/releases/tags/$Tag" } else { "https://api.github.com/repos/$repo/releases/latest" }
    $release = Invoke-RestMethod $api -Headers $headers
    $lib = Join-Path $work 'install-lib.ps1'
    Invoke-WebRequest "https://raw.githubusercontent.com/$repo/$($release.tag_name)/install-lib.ps1" -OutFile $lib -Headers $headers -UseBasicParsing
    . $lib

    if ($Uninstall) { Uninstall-Glance -Purge:$Purge; return }

    $asset = $release.assets | Where-Object name -EQ 'TaskbarGlance.exe'
    $sum = $release.assets | Where-Object name -EQ 'TaskbarGlance.exe.sha256'
    if (-not $asset) { throw "Release $($release.tag_name) has no TaskbarGlance.exe." }
    Write-Host "Downloading Taskbar Glance $($release.tag_name)..."
    $exe = Join-Path $work 'TaskbarGlance.exe'
    Invoke-WebRequest $asset.browser_download_url -OutFile $exe -Headers $headers -UseBasicParsing
    if ($sum) {
        $sumFile = Join-Path $work 'sum.txt'
        Invoke-WebRequest $sum.browser_download_url -OutFile $sumFile -Headers $headers -UseBasicParsing
        $want = ((Get-Content $sumFile -Raw).Trim() -split '\s+')[0]
        $got = (Get-FileHash $exe -Algorithm SHA256).Hash
        if ($got -ne $want) { throw "Checksum mismatch for TaskbarGlance.exe (expected $want, got $got). Nothing was installed." }
    }
    Unblock-File $exe
    Install-GlanceExe $exe -City $City -NoAutostart:$NoAutostart
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
