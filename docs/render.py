#!/usr/bin/env python3
"""Redraws the README pictures:  python docs/render.py

Builds the app (dotnet build, no AOT needed) and runs its hidden `--render` mode, which draws the strip and its
popups with the app's own drawing code for made-up forecasts and calendars: no window opens, no settings are read and
nothing is fetched. Needs the .NET SDK and `pip install pillow`. tests/check.py reuses the scenarios below.
"""
from __future__ import annotations

import json
import math
import os
import re
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


def ics(*lines: str) -> str:
    """An iCalendar file the way a server sends it: CRLF line ends, the zones it uses described in VTIMEZONE blocks."""
    head = [
        'BEGIN:VCALENDAR', 'VERSION:2.0', 'PRODID:-//taskbar-glance//made-up//EN',
        'BEGIN:VTIMEZONE', 'TZID:America/Argentina/Buenos_Aires', 'BEGIN:STANDARD', 'TZOFFSETFROM:-0300',
        'TZOFFSETTO:-0300', 'TZNAME:-03', 'DTSTART:19700101T000000', 'END:STANDARD', 'END:VTIMEZONE',
        'BEGIN:VTIMEZONE', 'TZID:America/New_York',
        'BEGIN:DAYLIGHT', 'TZOFFSETFROM:-0500', 'TZOFFSETTO:-0400', 'TZNAME:EDT', 'DTSTART:19700308T020000',
        'RRULE:FREQ=YEARLY;BYMONTH=3;BYDAY=2SU', 'END:DAYLIGHT',
        'BEGIN:STANDARD', 'TZOFFSETFROM:-0400', 'TZOFFSETTO:-0500', 'TZNAME:EST', 'DTSTART:19701101T020000',
        'RRULE:FREQ=YEARLY;BYMONTH=11;BYDAY=1SU', 'END:STANDARD', 'END:VTIMEZONE',
    ]
    return '\r\n'.join(head + list(lines) + ['END:VCALENDAR', ''])


def event(uid: str, title: str, *props: str) -> list[str]:
    return ['BEGIN:VEVENT', f'UID:{uid}@made-up', f'SUMMARY:{title}', *props, 'END:VEVENT']


BA = 'TZID=America/Argentina/Buenos_Aires'
NY = 'TZID=America/New_York'

# name -> calendar file. The scenes of the same name say what time it is (the PC is in Buenos Aires: tz=-180).
CALENDARS = {
    # a week in November as most calendars look: repeating meetings, a birthday, one moved and one skipped occurrence
    'cal': ics(
        *event('standup', 'Standup', f'DTSTART;{BA}:20260105T100000', f'DTEND;{BA}:20260105T101500',
               'RRULE:FREQ=WEEKLY;BYDAY=MO,WE,FR', f'EXDATE;{BA}:20261118T100000'),
        *event('dentist', 'Dentist', 'DTSTART:20261116T183000Z', 'DTEND:20261116T191500Z',
               'BEGIN:VALARM', 'ACTION:EMAIL', 'SUMMARY:Alarm', 'TRIGGER:-PT30M', 'END:VALARM'),
        *event('birthday', "Ana's birthday", 'DTSTART;VALUE=DATE:20201117', 'DTEND;VALUE=DATE:20201118', 'RRULE:FREQ=YEARLY'),
        *event('ny', 'Call with New York', f'DTSTART;{NY}:20261118T090000', f'DTEND;{NY}:20261118T100000'),
        *event('games', 'Board games', f'DTSTART;{BA}:20260130T200000', f'DTEND;{BA}:20260130T230000', 'RRULE:FREQ=MONTHLY;BYDAY=-1FR'),
        *event('gym', 'Gym', f'DTSTART;{BA}:20260901T190000', f'DTEND;{BA}:20260901T200000', 'RRULE:FREQ=WEEKLY;BYDAY=TU,TH'),
        *event('gym', 'Gym (early)', f'DTSTART;{BA}:20261119T180000', f'DTEND;{BA}:20261119T190000', f'RECURRENCE-ID;{BA}:20261119T190000'),
        *event('gone', 'Cancelled thing', f'DTSTART;{BA}:20261116T140000', f'DTEND;{BA}:20261116T150000', 'STATUS:CANCELLED'),
        *event('course', 'Course', f'DTSTART;{BA}:20261110T090000', f'DTEND;{BA}:20261110T110000', 'RRULE:FREQ=DAILY;COUNT=3'),
        *event('class', 'Old class', f'DTSTART;{BA}:20260105T080000', f'DTEND;{BA}:20260105T090000',
               'RRULE:FREQ=WEEKLY;BYDAY=MO;UNTIL=20261109T235959Z'),
        *event('trip', 'Trip', 'DTSTART;VALUE=DATE:20261121', 'DTEND;VALUE=DATE:20261124'),
        # a long line folded the way the format asks for, with an escaped comma
        *event('lunch', 'Lunch\\, with Marta and\r\n  the whole team', f'DTSTART;{BA}:20261119T123000', f'DTEND;{BA}:20261119T133000',
               'DESCRIPTION:SUMMARY:not this one'),
    ),
    # one of each rule the reader knows, seen from a Monday in July (New York is on summer time then)
    'cal-rules': ics(
        *event('ny', 'NY sync', f'DTSTART;{NY}:20260105T090000', f'DTEND;{NY}:20260105T100000', 'RRULE:FREQ=WEEKLY;BYDAY=MO'),
        *event('pay', 'Payday', 'DTSTART;VALUE=DATE:20260131', 'RRULE:FREQ=MONTHLY;BYMONTHDAY=-1'),
        *event('review', 'Workday review', f'DTSTART;{BA}:20260130T170000', f'DTEND;{BA}:20260130T173000',
               'RRULE:FREQ=MONTHLY;BYDAY=MO,TU,WE,TH,FR;BYSETPOS=-1'),
        *event('biweekly', 'Biweekly', f'DTSTART;{BA}:20260106T120000', f'DTEND;{BA}:20260106T130000', 'RRULE:FREQ=WEEKLY;INTERVAL=2;BYDAY=TU'),
        *event('leap', 'Leap day', 'DTSTART;VALUE=DATE:20240229', 'RRULE:FREQ=YEARLY'),
        *event('fair', 'Summer fair', 'DTSTART;VALUE=DATE:20250710', 'RRULE:FREQ=YEARLY;BYMONTH=7;BYDAY=2TH'),
        *event('three', 'Every 3 days', f'DTSTART;{BA}:20260704T070000', 'RRULE:FREQ=DAILY;INTERVAL=3;COUNT=4'),
        *event('floating', 'Floating', 'DTSTART:20260708T093000', 'DTEND:20260708T100000'),
        *event('outlook', 'Outlook call', 'DTSTART;TZID="Eastern Standard Time":20260708T090000', 'DTEND;TZID="Eastern Standard Time":20260708T093000'),
        *event('workshop', 'Workshop', 'DTSTART:20260709T150000Z', 'DURATION:PT1H30M'),
        *event('movie', 'Movie', f'DTSTART;{BA}:20260710T200000', f'DTEND;{BA}:20260710T220000', f'RDATE;{BA}:20260712T200000'),
        *event('conf', 'Conference', f'DTSTART;{BA}:20260705T090000', f'DTEND;{BA}:20260706T180000'),
        # a zone the file names but does not describe: Windows is asked (18:00 in Madrid's summer = 13:00 in Buenos Aires)
        *event('madrid', 'Madrid call', 'DTSTART;TZID=Europe/Madrid:20260715T180000', 'DTEND;TZID=Europe/Madrid:20260715T190000'),
    ),
    'cal-empty': ics(*event('old', 'Long ago', 'DTSTART:20260105T120000Z', 'DTEND:20260105T130000Z')),
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
    'cal': (forecast((23, 2, 1), [(17, 20, 61, 70), (30, 34, 3, 20)]), [f'now={DAY}T13:10', 'tz=-180']),
    'cal-rules': (forecast((12, 0, 1)), ['now=2026-07-06T08:00', 'tz=-180', 'days=60', 'rows=30']),
    'cal-empty': (forecast((24, 0, 1)), [f'now={DAY}T10:30', 'tz=-180']),
    'cal-loading': (forecast((24, 0, 1)), [f'now={DAY}T10:30', 'cal=loading']),
    'cal-failed': (forecast((24, 0, 1)), [f'now={DAY}T10:30', 'cal=failed']),
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
    'strip-calendar.png': ('strip', 'cal', []),
    'strip-calendar-light.png': ('strip', 'cal', ['light']),
    'strip-calendar-only.png': ('strip', 'cal', ['noweather', f'now={DAY}T17:00']),
    'tip-calendar.png': ('caltip', 'cal', []),
    'tip-calendar-light.png': ('caltip', 'cal', ['light']),
}
ICON_SHEET = ['clear', 'night', 'rain-later', 'night-cloudy', 'cloudy', 'fog', 'raining', 'snow', 'thunder']


def run(exe: str, tmp: str, what: str, scene: str, extra: list[str]) -> str:
    """Runs --render for a scene; returns the path of what it wrote."""
    data, args = SCENES[scene]
    out = os.path.join(tmp, f'{what}-{scene}-{re.sub("[^A-Za-z0-9=,]", "_", "-".join(extra)) or "x"}.out')
    cmd = [exe, '--render', what, out, *args, *extra]  # of two equal arguments the later one wins
    if scene in CALENDARS:
        path = os.path.join(tmp, scene + '.ics')
        with open(path, 'w', encoding='utf-8', newline='') as f:
            f.write(CALENDARS[scene])
        cmd.append('ics=' + path)
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
