# taskbar-glance

Native AOT C# (net11.0, raw Win32 P/Invoke, no WinForms/WPF), built from the same skeleton as `claude-taskbar`.
Footprint is a feature: no polling loops, no UI frameworks, no reflection-based JSON, no `HttpClient` (WinHTTP via
P/Invoke in `Net.cs`). One UI thread; each fetch runs on a thread that lives for that request and posts
`Strip.WM_FETCHED` (weather) or `Strip.WM_CALFETCHED` (calendar) back.

Two segments (`Strip` kinds): 0 = weather (`Weather.cs`), 1 = calendar (`Calendar.cs` on top of the iCal reader
`Ical.cs`). `Tooltip.cs` builds either popup from typed lines.

## Build / run
- `dotnet publish src/TaskbarGlance.csproj -c Release -o out` (needs VS "Desktop development with C++" for the linker).
- `.\install.ps1 [-City "Name"]` builds + installs to `%LOCALAPPDATA%\TaskbarGlance` + HKCU Run + starts it.
  `bootstrap.ps1` is the one-line install of a release; both share `install-lib.ps1`. Keep them PowerShell 5.1-compatible.
- Release: bump `<Version>` in the csproj, add a CHANGELOG entry, merge, push tag `vX.Y.Z` (release.yml builds the exe +
  .sha256 and publishes the release that bootstrap.ps1 downloads; the notes come from the CHANGELOG section).
- `python tests/check.py` checks what the strip and popups say for made-up forecasts and calendars;
  `python docs/render.py` redraws the README pictures. Both use the hidden `--render strip|tip|caltip|text` mode (no
  window, no settings, no network) and the scenarios in `docs/render.py` (`SCENES`, and `CALENDARS` for the .ics text).
  A scene's arguments can be overridden per case (`now=`, `tz=`, `days=`, `rows=`...): the later one wins.
- `nuget.config` clears machine feeds on purpose (a broken local VS feed broke restore).

## Testing without touching the installed copy
- `GLANCE_DIR=<folder>` moves settings, saved forecast and log there; such a copy may run next to the installed one.
- `GLANCE_OFFLINE=1` never fetches and re-reads `weather.json` (an Open-Meteo answer) when it changes. The calendars
  are then the `*.ics` files in that folder.
- `GLANCE_DEBUG=1` logs renders and fetches to `glance.log`; `GLANCE_DEBUG_TIP=weather|calendar` opens that popup at
  startup.
- Only one `GLANCE_DIR` copy runs at a time (they share one mutex): stop the previous test copy first, by its path, so
  the installed one keeps running.
- The strip hides while a full-screen app is in front (the log's start line says `fullscreen=True`): nothing to
  screenshot then; check the log and `calendar.json` instead.
- Screenshots need layered windows: Pillow `ImageGrab.grab(include_layered_windows=True)`.

## Gotchas
- The strip must be WS_EX_TOPMOST *and* owned by `Shell_TrayWnd`, or the taskbar draws over it.
- Do not set `WINHTTP_OPTION_DECOMPRESSION`: with it `WinHttpReadData` failed with E_ABORT on Open-Meteo's answers.
- All times are the city's local time (`utc_offset_seconds` from Open-Meteo), not the PC's.
- Temperatures are stored in °C; °F is converted at display time, so changing units never needs a request.
- `Shell.OpenFile` starts Notepad explicitly: the registered app for `.json` (here VS Code) opened the file behind
  everything else, so clicking "Set city" looked dead. `GLANCE_DEBUG_OPEN=settings` runs it at startup.
- Next to Claude Taskbar: Glance owns the anchored edge. `Strip.NotifyNeighbors` posts the registered message
  `TaskbarGlance.Moved` to the `ClaudeTaskbarStrip` window whenever our rect or visibility changes; Claude Taskbar
  (1.2+) finds the `TaskbarGlanceStrip` window and docks outward of it.
- `Gfx` blends into premultiplied alpha for the strip and onto an opaque background for the popup; "erase" differs.
- Calendar times are the PC's local time (`Calendar.Now`); the weather's are the city's. In `--render`, `now=` sets
  both and `tz=` (minutes east of UTC) is the PC's zone, so the checks give the same answer on any machine.
- Time zones: with invariant globalization .NET on Windows cannot resolve IANA names ("Europe/Madrid"), checked on the
  AOT exe. `Ical` therefore uses the file's own VTIMEZONE blocks (Google always sends them); a zone that is only
  named goes through Windows names, then `icu.dll`'s `ucal_getWindowsTimeZoneID`, then the PC's zone.
- Calendar addresses are secrets: never log them or an exception message that could quote them. The log says
  "calendar 2"; `calendar.json` keys the saved events by a hash of the address.
- `Calendar.ScheduleTick` sets one timer for the next moment the text changes (an event of today starts or ends, or
  midnight). Anything that changes the events or the clock must call it again.
- The trim (`T_TRIM`) uses an aggressive, compacting GC: with a plain `GC.Collect()` private memory stayed at 6 to 8 MB
  after calendar downloads; now it returns to about 5 MB.

## Not done in the calendar
- RRULE parts `BYWEEKNO`, `BYYEARDAY`, `BYHOUR`... and `FREQ=HOURLY` or faster (such an event counts once); RDATE periods.
- An event you declined still shows: the iCal file does not say which attendee you are.
- The clock is always 24-hour; event locations are not read.
