#!/usr/bin/env python3
"""Redraws the README pictures:  python docs/render.py

Builds the app (dotnet build, no AOT needed) and runs its hidden `--render` mode, which draws the strip and its
popup with the app's own drawing code for made-up forecasts: no window opens, no settings are read and nothing is
fetched. Needs the .NET SDK and `pip install pillow`. tests/check.py reuses the scenarios below.
"""
from __future__ import annotations

import json
import math
import os
import struct
import subprocess
import sys
import tempfile

DOCS = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(DOCS)
TASKBAR = {'dark': (0x1c, 0x1c, 0x1c), 'light': (0xee, 0xee, 0xee)}
DAY = '2026-11-16'
NEXT = '2026-11-17'


def forecast(cur: tuple[float, int, int], events: list[tuple[int, int, int, int]] = (), base: int = 1) -> dict:
    """An Open-Meteo style answer for two days. cur = (temperature, weather code, is_day).
    events = (first hour, last hour, weather code, chance of rain), hours counted 0..47 from midnight today."""
    times, temps, codes, pops, days = [], [], [], [], []
    for i in range(48):
        h = i % 24
        times.append(f'{DAY if i < 24 else NEXT}T{h:02d}:00')
        temps.append(round(17 + 7 * math.sin((h - 9) / 24 * 2 * math.pi), 1))
        code, pop = base, 5
        for first, last, c, p in events:
            if first <= i <= last:
                code, pop = c, p
        codes.append(code)
        pops.append(pop)
        days.append(1 if 7 <= h < 20 else 0)
    return {
        'utc_offset_seconds': -10800,
        'current': {'time': f'{DAY}T12:00', 'temperature_2m': cur[0], 'weather_code': cur[1], 'is_day': cur[2]},
        'hourly': {'time': times, 'temperature_2m': temps, 'weather_code': codes, 'precipitation_probability': pops, 'is_day': days},
    }


# name -> (forecast, render arguments)
SCENES = {
    'clear': (forecast((24, 0, 1)), [f'now={DAY}T10:30']),
    'rain-later': (forecast((23, 2, 1), [(17, 20, 61, 70), (30, 34, 3, 20)]), [f'now={DAY}T13:10']),
    'thunder-later': (forecast((27, 3, 1), [(16, 17, 63, 75), (18, 21, 95, 85), (38, 44, 80, 60)]), [f'now={DAY}T12:10']),
    'maybe-rain': (forecast((22, 3, 1), [(17, 20, 3, 60)], base=3), [f'now={DAY}T13:10']),
    'raining': (forecast((16, 63, 1), [(8, 13, 63, 90)]), [f'now={DAY}T09:20']),
    'night': (forecast((14, 0, 0)), [f'now={DAY}T22:40']),
    'night-cloudy': (forecast((13, 2, 0), base=2), [f'now={DAY}T21:10']),
    'cloudy': (forecast((18, 3, 1), base=3), [f'now={DAY}T11:00']),
    'fog': (forecast((9, 45, 1), base=45), [f'now={DAY}T08:30']),
    'snow': (forecast((-2, 73, 1), [(0, 20, 73, 80)]), [f'now={DAY}T11:00']),
    'thunder': (forecast((21, 95, 1), [(10, 14, 95, 90)]), [f'now={DAY}T11:00']),
    'stale': (forecast((23, 2, 1), [(17, 20, 61, 70)]), [f'now={DAY}T15:10', 'age=400']),
    'nocity': (None, ['nocity']),
    'notfound': (None, ['notfound', 'city=Atlantis']),
}

# file name -> (what, scene, extra arguments)
SHOTS = {
    'strip-rain-later.png': ('strip', 'rain-later', []),
    'strip-thunder-later.png': ('strip', 'thunder-later', []),
    'strip-light.png': ('strip', 'rain-later', ['light']),
    'strip-fahrenheit.png': ('strip', 'clear', ['unit=F']),
    'strip-nocity.png': ('strip', 'nocity', []),
    'tip-rain-later.png': ('tip', 'rain-later', []),
    'tip-thunder-later.png': ('tip', 'thunder-later', []),
    'tip-light.png': ('tip', 'rain-later', ['light']),
}
ICON_SHEET = ['clear', 'night', 'rain-later', 'night-cloudy', 'cloudy', 'fog', 'raining', 'snow', 'thunder']


def run(exe: str, tmp: str, what: str, scene: str, extra: list[str]) -> str:
    """Runs --render for a scene; returns the path of what it wrote."""
    data, args = SCENES[scene]
    out = os.path.join(tmp, f'{what}-{scene}-{"-".join(extra) or "x"}.out')
    cmd = [exe, '--render', what, out, *args, *extra]
    if data is not None:
        path = os.path.join(tmp, scene + '.json')
        with open(path, 'w') as f:
            json.dump(data, f)
        cmd.append('data=' + path)
    subprocess.run(cmd, check=True)
    return out


def to_image(raw: str, light: bool, margin: bool = True):
    from PIL import Image
    data = open(raw, 'rb').read()
    w, h = struct.unpack('<ii', data[:8])
    src = Image.frombuffer('RGBA', (w, h), data[8:], 'raw', 'BGRA', 0, 1)
    bg = TASKBAR['light' if light else 'dark']
    m = h // 3 if margin else 0
    img = Image.new('RGB', (w + 2 * m, h), bg)
    r, g, b, a = src.split()
    a = a.point(lambda v: 0 if v <= 1 else v)  # alpha 1 = the invisible, clickable background
    # premultiplied: out = src + bg * (1 - alpha)
    back = Image.new('RGB', (w, h), bg)
    black = Image.new('RGB', (w, h), (0, 0, 0))
    faded = Image.composite(black, back, a)
    from PIL import ImageChops
    img.paste(ImageChops.add(faded, Image.merge('RGB', (r, g, b))), (m, 0))
    return img


def build(tmp: str) -> str:
    out = os.path.join(tmp, 'build')
    subprocess.run(['dotnet', 'build', os.path.join(REPO, 'src', 'TaskbarGlance.csproj'), '-c', 'Release', '-o', out,
                    '-v', 'quiet', '-nologo'], check=True)
    return os.path.join(out, 'TaskbarGlance.exe')


def main() -> int:
    from PIL import Image
    with tempfile.TemporaryDirectory(prefix='glance-render-') as tmp:
        exe = build(tmp)
        for name, (what, scene, extra) in SHOTS.items():
            light = 'light' in extra
            to_image(run(exe, tmp, what, scene, extra), light, margin=what == 'strip').save(os.path.join(DOCS, name))
            print('wrote', name)
        for theme in ('dark', 'light'):
            extra = ['light'] if theme == 'light' else []
            parts = [to_image(run(exe, tmp, 'strip', s, extra + ['dpi=192']), theme == 'light', margin=False) for s in ICON_SHEET]
            sheet = Image.new('RGB', (sum(p.width for p in parts), parts[0].height), TASKBAR[theme])
            x = 0
            for p in parts:
                sheet.paste(p, (x, 0))
                x += p.width
            sheet.save(os.path.join(DOCS, f'icons-{theme}.png'))
            print('wrote', f'icons-{theme}.png')
    return 0


if __name__ == '__main__':
    sys.exit(main())
