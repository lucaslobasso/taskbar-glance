#!/usr/bin/env python3
"""Checks what the strip and its popups say for made-up forecasts and calendars:  python tests/check.py

Uses the app's `--render text` mode with the scenarios of docs/render.py: no window, no settings, no network.
"""
from __future__ import annotations

import os
import sys
import tempfile

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'docs'))
import render  # noqa: E402

# (scene, extra arguments, lines that must appear, lines that must not)
CASES = [
    ('clear', [], ['strip: Clear day, "24°", badge none', 'Header: Buenos Aires | 24° Clear [Clear day]',
                   'Row: Today | 14:00 |', 'Row: Tomorrow | 08:00 |', 'Note: updated 5 min ago'], ['Warn:', '| 08:00 | 15° | 5% [Clear day]\nRow: Today']),
    ('clear', ['unit=F'], ['strip: Clear day, "75°", badge none'], []),
    ('rain-later', [], ['strip: PartlyCloudy day, "23°", badge rain', 'Warn: Rain from 17:00 [badge rain]',
                        'Row: Today | 14:00 |', 'Row:  | 20:00 | 19° | 70% [Rain night]'], ['Today | 08:00']),
    ('rain-later', ['hours=6,12,18'], ['Row: Today | 18:00 |', 'Row: Tomorrow | 06:00 |', 'Row:  | 12:00 |'], ['14:00']),
    # no rain code, only a 60% chance: it depends on rainWarnPercent
    ('maybe-rain', [], ['badge rain', 'Warn: Rain from 17:00'], []),
    ('maybe-rain', ['warn=80'], ['badge none'], ['Warn:']),
    ('thunder-later', [], ['badge thunder', 'Warn: Rain from 16:00, thunderstorm from 18:00 [badge thunder]'], []),
    ('raining', [], ['strip: Rain day, "16°", badge none'], ['Warn:']),
    ('snow', [], ['strip: Snow day, "-2°", badge none'], ['Warn:']),
    ('thunder', [], ['strip: Thunder day, "21°", badge none'], ['Warn:']),
    ('night', [], ['strip: Clear night, "14°", badge none', 'Row: Tomorrow | 08:00 |'], ['Row: Today']),
    # too old for the last reading: the forecast for this hour (15:00) is shown instead
    ('stale', [], ['strip: Clear day, "24°", badge rain', 'Note: updated 7 h ago'], []),
    ('nocity', [], ['strip: PartlyCloudy day, "Set city", badge none', 'Header: No city set'], []),
    ('notfound', [], ['strip: no icon, "City not found", badge none', 'Header: City "Atlantis" not found'], []),

    # the calendar. Monday 16 November, 13:10 in Buenos Aires: the standup is over, the dentist is next
    ('cal', [], ['strip: PartlyCloudy day, "23°", badge rain | no icon, "Today 15:30 · Dentist", badge none',
                 'Header: November 2026 | Mon 16', 'WeekHead: Mo | Tu | We | Th | Fr | Sa | Su',
                 'Week:  |  |  |  |  |  | 1\n', 'Week: 2 | 3 | 4 | 5 | 6 | 7 | 8 [events 2,3,4,5,6]',
                 'Week: 9 | 10 | 11 | 12 | 13 | 14 | 15 [events 9,10,11,12,13]',
                 'Week: 16 | 17 | 18 | 19 | 20 | 21 | 22 [today 16] [events 16,17,18,19,20,21,22]',
                 'Week: 23 | 24 | 25 | 26 | 27 | 28 | 29 [events 23,24,25,26,27]', 'Week: 30 |  |  |  |  |  |  [events 30]',
                 'Event: Today | 15:30 – 16:15 | Dentist',
                 "Event: Tomorrow | All day | Ana's birthday\nEvent:  | 19:00 – 20:00 | Gym\n",
                 'Event: Wed 18 Nov | 11:00 – 12:00 | Call with New York',         # 09:00 in New York, winter time
                 'Event: Thu 19 Nov | 12:30 – 13:30 | Lunch, with Marta and the whole team\nEvent:  | 18:00 – 19:00 | Gym (early)',
                 'Event: Fri 20 Nov | 10:00 – 10:15 | Standup', 'Event: Sat 21 Nov | All day | Trip', 'Note: +7 more', 'Note: updated 5 min ago'],
     ['Cancelled', 'Alarm', 'not this one', '| 10:00 – 10:15 | Standup\nEvent: Thu', 'Course', 'Old class', 'Thu 19 Nov | 19:00']),
    ('cal', ['rows=30'], ['Event: Fri 27 Nov | 10:00 – 10:15 | Standup\nEvent:  | 20:00 – 23:00 | Board games',
                          'Event: Mon 30 Nov | 10:00 – 10:15 | Standup\nNote: updated'], ['more', 'Dec']),
    ('cal', ['days=3', 'rows=30'], ['Event: Thu 19 Nov |'], ['Fri 20 Nov']),
    ('cal', ['title=5'], ['no icon, "Today 15:30 · Dent…"'], []),
    ('cal', ['week=sunday'], ['WeekHead: Su | Mo | Tu | We | Th | Fr | Sa', 'Week: 1 | 2 | 3 | 4 | 5 | 6 | 7 [events 2,3,4,5,6]',
                              'Week: 15 | 16 | 17 | 18 | 19 | 20 | 21 [today 16]'], []),
    ('cal', ['now=2026-11-16T15:45'], ['no icon, "Now · Dentist"', 'Event: Today | 15:30 – 16:15 | Dentist'], []),
    ('cal', ['now=2026-11-16T17:00'], ['no icon, "Tomorrow · Ana\'s birthday"', 'Event: Tomorrow | All day'], ['Dentist']),
    ('cal', ['now=2026-11-17T09:00'], ['no icon, "Today 19:00 · Gym"', "Event: Today | All day | Ana's birthday", '[today 17]'], []),
    ('cal', ['now=2026-11-17T21:00'], ['no icon, "Today · Ana\'s birthday"'], []),
    ('cal', ['now=2026-11-19T20:00'], ['no icon, "Tomorrow 10:00 · Standup"'], ['Event: Today', 'Gym (early)']),
    ('cal', ['now=2026-11-22T10:00'], ['no icon, "Today · Trip"', 'Event: Today | All day | Trip'], []),
    ('cal', ['now=2026-11-28T10:00'], ['no icon, "Mon 10:00 · Standup"'], []),
    ('cal', ['now=2026-11-07T10:00', 'days=30', 'rows=40'], ['no icon, "Mon 08:00 · Old class"', 'Event: Tue 17 Nov | All day',
                                                                'Event: Thu 12 Nov | 09:00 – 11:00 | Course\nEvent:  | 19:00 – 20:00 | Gym\nEvent: Fri 13'],
     ['Old class\nEvent:  | 10:00 – 10:15 | Standup\nEvent: Tue 17']),   # the class ended on the 9th, the course after 3 days
    ('cal', ['now=2026-10-20T23:00', 'days=30', 'rows=40'], ["Event: Tue 17 Nov | All day | Ana's birthday", 'Header: October 2026 | Tue 20'], []),
    # the same calendar on a PC in UTC
    ('cal', ['tz=0', 'now=2026-11-16T16:10'], ['no icon, "Today 18:30 · Dentist"', 'Event: Wed 18 Nov | 14:00 – 15:00 | Call with New York',
                                                 'Event: Fri 20 Nov | 13:00 – 13:15 | Standup'], []),
    ('cal', ['noweather'], ['strip: no icon, "Today 15:30 · Dentist", badge none'], []),
    # Monday 6 July, 08:00: one of each repeat rule; New York is on summer time (09:00 there = 10:00 here)
    ('cal-rules', [], ['no icon, "Today 10:00 · NY sync"', 'Header: July 2026 | Mon 6',
                       'Event: Today | until 18:00 | Conference\nEvent:  | 10:00 – 11:00 | NY sync\n',
                       'Event: Tomorrow | 07:00 | Every 3 days\nEvent:  | 12:00 – 13:00 | Biweekly\n',
                       'Event: Wed 8 Jul | 09:30 – 10:00 | Floating\nEvent:  | 10:00 – 10:30 | Outlook call\n',
                       'Event: Thu 9 Jul | All day | Summer fair\nEvent:  | 12:00 – 13:30 | Workshop\n',
                       'Event: Fri 10 Jul | 07:00 | Every 3 days\nEvent:  | 20:00 – 22:00 | Movie\n',
                       'Event: Sun 12 Jul | 20:00 – 22:00 | Movie\n',
                       'Event: Mon 13 Jul | 07:00 | Every 3 days\nEvent:  | 10:00 – 11:00 | NY sync\nEvent: Wed 15 Jul | 13:00 – 14:00 | Madrid call\nEvent: Mon 20 Jul',
                       'Event: Tue 21 Jul | 12:00 – 13:00 | Biweekly', 'Event: Fri 31 Jul | All day | Payday\nEvent:  | 17:00 – 17:30 | Workday review',
                       'Event: Mon 31 Aug | All day | Payday\nEvent:  | 10:00 – 11:00 | NY sync\nEvent:  | 17:00 – 17:30 | Workday review',
                       'Event: Tue 1 Sep | 12:00 – 13:00 | Biweekly\nNote: updated'],
     ['Leap day', 'Tue 14 Jul', 'Thu 16 Jul', 'more']),
    ('cal-rules', ['now=2026-03-09T08:00'], ['no icon, "Today 10:00 · NY sync"'], []),   # the day after New York's clocks changed
    ('cal-rules', ['now=2026-03-02T08:00'], ['no icon, "Today 11:00 · NY sync"'], []),
    ('cal-empty', [], ['no icon, "No events"', 'Note: Nothing in the next 14 days', 'Header: November 2026 | Mon 16'], ['Event:', '[events']),
    ('cal-loading', [], ['no icon, "…"', 'Note: Loading...', 'Week: 16 | 17 |'], ['Event:']),
    ('cal-failed', [], ['no icon, "–"', "Note: Can't reach the calendar; trying again soon."], ['Event:']),
    ('clear', [], [], ['calendar:', 'badge none | ']),   # no calendar set: no segment
]


def main() -> int:
    failed = 0
    with tempfile.TemporaryDirectory(prefix='glance-check-') as tmp:
        exe = render.build(tmp)
        for scene, extra, want, unwanted in CASES:
            text = open(render.run(exe, tmp, 'text', scene, extra), encoding='utf-8').read()
            problems = [f'missing: {w}' for w in want if w not in text] + [f'unexpected: {u}' for u in unwanted if u in text]
            print(('FAIL' if problems else 'ok  '), scene, ' '.join(extra))
            if problems:
                failed += 1
                print('   ' + '\n   '.join(problems))
                print('   --- got ---\n   ' + text.replace('\n', '\n   '))
    print(f'{len(CASES) - failed}/{len(CASES)} passed')
    return 1 if failed else 0


if __name__ == '__main__':
    sys.exit(main())
