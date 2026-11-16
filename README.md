# Taskbar Glance

The weather in the Windows 11 taskbar, next to the tray: current temperature, an icon for the sky, and a mark when
rain or a thunderstorm is announced for later today. The next Google Calendar event is planned as a second segment.

![The strip next to the tray](docs/strip-rain-later.png)

![The icons](docs/icons-dark.png)

It is one small native exe (about 1.6 MB, about 4 MB of memory). It does not poll: it sleeps until a timer, the
mouse or a settings change wakes it, and makes one small request to [Open-Meteo](https://open-meteo.com) every
30 minutes. No account and no API key.

## Requirements

- Windows 11 (the taskbar at the bottom).
- To build: the .NET 11 SDK and the Visual Studio "Desktop development with C++" workload.

## Install

```powershell
git clone https://github.com/lucaslobasso/taskbar-glance
cd taskbar-glance
.\install.ps1 -City "Buenos Aires"
```

This builds the exe, copies it to `%LOCALAPPDATA%\TaskbarGlance`, starts it and makes it start with Windows.
If you leave `-City` out, the strip says "Set city": click it, fill in `"city"` in the file that opens and save.

## Use

| Action | What happens |
|---|---|
| Look | Icon, temperature, and `!` when rain (amber) or a thunderstorm (red) comes later today |
| Hover | Now, what is coming, and the forecast at 08:00, 14:00 and 20:00 for the rest of today and tomorrow |
| Click | Opens the forecast page (`weatherUrl`, or a web search for the city's weather) |
| Right-click | Refresh now, °C / °F, left or right side, start with Windows, open the settings file, exit |

![The popup](docs/tip-thunder-later.png)

## Settings

`%LOCALAPPDATA%\TaskbarGlance\glance.json`. Changes apply as soon as the file is saved.

| Setting | Default | Meaning |
|---|---|---|
| `city` | `""` | City name. Add a region or country to pick between namesakes: `"La Plata, Argentina"` |
| `units` | `"C"` | `"C"` or `"F"` |
| `weatherUrl` | `""` | Page opened on click. Empty = a web search for "weather" plus the city |
| `forecastHours` | `[8, 14, 20]` | Hours shown in the popup |
| `rainWarnPercent` | `50` | Chance of rain that counts as "rain later today" |
| `refreshMinutes` | `30` | Time between requests (10 to 720) |
| `position` | `"right"` | `"right"` (next to the tray) or `"left"` (the taskbar's left edge) |
| `offsetX` | `0` | Extra distance from that edge, in pixels at 100% scaling |
| `showWeather` | `true` | Show the weather segment |

The app adds a `location` block with the coordinates it found for the city. Edit its `latitude` and `longitude`
for an exact spot.

## How it works

- A layered window owned by the taskbar, drawn only when its content, position, scaling or theme changes. It hides
  while a fullscreen app is in front.
- The icons are drawn from shapes in code, so there are no image files and they stay sharp at any scaling.
- One request brings the current reading and two days of hourly forecast. It is saved, so a restart shows it at
  once. Offline, it retries after 1, 5 and then every 15 minutes, and shows the forecast for the current hour
  instead of an old reading.
- What leaves your PC: the city name (once, to find its coordinates) and those coordinates, to Open-Meteo.

## Uninstall

```powershell
.\uninstall.ps1          # add -Purge to delete the settings too
```

## License

MIT. Weather data by [Open-Meteo](https://open-meteo.com) (CC BY 4.0).
