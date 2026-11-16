#!/usr/bin/env python3
"""Checks what the strip and its popup say for made-up forecasts:  python tests/check.py

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
    ('clear', [], ['strip: Clear day, "24°", badge none', 'Header: Buenos Aires · 24° Clear [Clear day]',
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
