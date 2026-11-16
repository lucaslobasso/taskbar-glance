# taskbar-glance

Native AOT C# (net11.0, raw Win32 P/Invoke, no WinForms/WPF), built from the same skeleton as `claude-taskbar`.
Footprint is a feature: no polling loops, no UI frameworks, no reflection-based JSON, no `HttpClient` (WinHTTP via
P/Invoke in `Net.cs`). One UI thread; each fetch runs on a thread that lives for that request and posts
`Strip.WM_FETCHED` back.

## Build / run
- `dotnet publish src/TaskbarGlance.csproj -c Release -o out` (needs VS "Desktop development with C++" for the linker).
- `.\install.ps1 [-City "Name"]` builds + installs to `%LOCALAPPDATA%\TaskbarGlance` + HKCU Run + starts it.
  Keep the scripts PowerShell 5.1-compatible.
- `python tests/check.py` checks what the strip and popup say for made-up forecasts; `python docs/render.py` redraws
  the README pictures. Both use the hidden `--render strip|tip|text` mode (no window, no settings, no network) and the
  scenarios in `docs/render.py`.
- `nuget.config` clears machine feeds on purpose (a broken local VS feed broke restore).

## Testing without touching the installed copy
- `GLANCE_DIR=<folder>` moves settings, saved forecast and log there; such a copy may run next to the installed one.
- `GLANCE_OFFLINE=1` never fetches and re-reads `weather.json` (an Open-Meteo answer) when it changes.
- `GLANCE_DEBUG=1` logs renders and fetches to `glance.log`; `GLANCE_DEBUG_TIP=weather` opens the popup at startup.
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

## Next: Google Calendar (phase 2)
Second segment (`Strip` kind 1) fed by secret iCal addresses (`calendarUrls`), own `Ical.cs` with recurrence
expansion, popup with month grid + agenda, click opens Google Calendar. Check TZID handling under invariant
globalization first.
