# Changelog

All notable changes to this project. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses [Semantic Versioning](https://semver.org/).

## [1.3.0] - 2026-11-21

An icon, and a way back after closing the strip.

### Added
- An app icon: a coral sun and cloud on a teal tile, shown on the exe and the Start menu shortcut (`src/app.ico`, made by `assets/make_icon.py`).
- A **Taskbar Glance** Start menu shortcut, made by the installer and removed by the uninstaller, to reopen the strip by itself after closing it (search "Taskbar Glance").

### Fixed
- The strip could vanish behind the taskbar and stay hidden until its next redraw: when Windows dropped its owner, the taskbar was drawn over it after opening Start or clicking the taskbar. The strip now re-owns itself and moves back on top on every foreground change.
- Switching displays (for example "Second screen only") left the strip where the old taskbar was; it now re-attaches to the taskbar once the display change has settled.
- Opening the strip while it is already running (the Start menu shortcut) did nothing; it now brings the running strip back.

## [1.2.0] - 2026-11-17

Updates without the command line.

### Added
- **Check for updates** in the right-click menu, which also shows the version you have. If a newer release exists it asks, downloads the exe, checks it against the release's SHA-256, swaps it in and restarts the strip; settings are kept. It runs only when you click it: there is no background check.

### Changed
- A release is published automatically when a merge to `main` carries a version that has no release yet; pushing a tag by hand is no longer needed.

## [1.1.0] - 2026-11-16

The next calendar event, next to the weather.

### Added
- A calendar segment with the next event: `15:30 · Dentist`, `Tomorrow · Ana's birthday`, `Thu 10:00 · Standup`, `Now · Standup`. It appears once `calendars` in `glance.json` has an iCal address, such as Google Calendar's "Secret address in iCal format" (no account, no sign-in); several calendars are merged.
- Hover popup: this month as a grid with today and the days that have events marked, then the events of the next `calendarDays` (14) days, `calendarRows` (8) at most. Click opens Google Calendar, or `calendarUrl`.
- An own iCal reader: repeating events (daily, weekly, monthly, yearly; interval, count, until, weekday, day of the month, month and position rules), skipped, moved and cancelled occurrences, all-day and multi-day events, and time zones from the file itself or from Windows.
- One download per calendar every `calendarRefreshMinutes` (15), kept for the next start in `calendar.json` (titles and times only), with the same retries as the weather. The text changes on its own at the moment an event starts or ends, without polling.
- Settings `showCalendar`, `calendars`, `calendarUrl`, `calendarDays`, `calendarRows`, `calendarTitleLength`, `calendarRefreshMinutes`; "Open calendar" in the right-click menu, where "Refresh now" refreshes both.
- `--render` draws the calendar too (`caltip`, `ics=`, `tz=`...), with made-up calendars in `docs/render.py` and 27 new checks in `tests/check.py`.

### Changed
- Memory is handed back more thoroughly a few seconds after each refresh, so the calendar adds nothing to the idle footprint (about 5 MB private).
- With `showWeather: false` the right-click menu no longer offers "Open forecast".

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
