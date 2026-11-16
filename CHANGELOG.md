# Changelog

All notable changes to this project. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses [Semantic Versioning](https://semver.org/).

## [1.0.0] - 2026-11-16

First release: the weather in the Windows 11 taskbar.

### Added
- The strip: a sky icon (day and night variants of sun, partly cloudy, cloud, fog, rain, snow, thunder) and the temperature in °C or °F, drawn from shapes in code.
- An amber `!` when rain is announced for later today, a red one for a thunderstorm (`rainWarnPercent` sets the chance that counts).
- Hover popup: the current weather, the warning, and the forecast at the hours in `forecastHours` (default 08:00, 14:00, 20:00) for the rest of today and tomorrow, with the chance of rain.
- Click opens `weatherUrl` (or a web search for the city's weather); right-click menu for refresh, units, side, start with Windows and the settings file.
- Data from Open-Meteo through WinHTTP: one request every `refreshMinutes` (30), the city looked up once, the forecast saved for the next start, retries after 1, 5 and 15 minutes, refresh after sleep.
- `position` and `offsetX`; the strip hides while a full-screen app is in front. Claude Taskbar 1.2 and newer docks outside it.
- One-line install from a GitHub release (`bootstrap.ps1`, checked against the release's SHA-256), `install.ps1` / `uninstall.ps1` for source builds, releases built by CI on a `v*` tag.
- Hidden `--render` mode used by `docs/render.py` (README pictures) and `tests/check.py`.
