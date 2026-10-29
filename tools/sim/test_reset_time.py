"""Reset layout regression: compare incremental updates against a fresh frame.

Run: firmware/.pio-venv/Scripts/python.exe tools/sim/test_reset_time.py
Requires pillow and freetype-py. Saves an optional preview via --preview PATH.
"""
import argparse
from datetime import datetime, timezone
from pathlib import Path
import re
import unittest

from PIL import Image, ImageChops
from sim_screen import Sim, text_width

PREVIEW = None

class ResetLayoutTests(unittest.TestCase):
    def test_incremental_switches_clear_both_date_lines(self):
        cl = dict(status="working", p5=100, pw=99, r5=299, rw=10019)
        cx = dict(status="idle", pw=100, rw=10019)
        epoch = int(datetime(2026, 12, 31, 15, 45, tzinfo=timezone.utc).timestamp())
        sim = Sim()
        frames = []
        for i, (mode, minutes, clock) in enumerate([
            ("countdown", 10019, epoch),
            ("datetime", 10019, epoch),
            ("datetime", 45, epoch),       # New year in UTC+8
            ("datetime", 0, epoch),
            ("datetime", -1, epoch),      # Long strings -> unknown
            ("datetime", 45, None),       # Legacy bridge without a clock
            ("datetime", 45, epoch),      # Clock recovers
            ("countdown", 45, epoch),     # Two lines -> one short line
        ]):
            state = dict(cl, r5=minutes, rw=minutes)
            codex = dict(cx, rw=minutes)
            options = dict(reset_format=mode, epoch=clock, utc_offset_min=480)
            actual = sim.draw(state, codex, force=i == 0, **options)
            if mode == "datetime" and minutes == 45 and clock:
                self.assertEqual(sim.cc.t1, "01/01\n00:30")
            expected = Sim().draw(state, codex, force=True, **options)
            self.assertIsNone(ImageChops.difference(actual, expected).getbbox(),
                              f"Stale pixels after {mode}, minutes={minutes}")
            frames.append(actual)
        if PREVIEW:
            sheet = Image.new("RGB", (240 * 4, 240 * 2))
            for i, frame in enumerate(frames):
                sheet.paste(frame, ((i % 4) * 240, (i // 4) * 240))
            path = Path(PREVIEW)
            path.parent.mkdir(parents=True, exist_ok=True)
            sheet.save(path)

    def test_card_dates_fit_clear_region(self):
        for text in ["12/31", "01/01", "23:59", "00:00", "6d 23h"]:
            self.assertLessEqual(text_width(text, 13), 58, text)

    def test_fixed_deadline_stays_on_same_date_across_poll_updates(self):
        at = datetime(2027, 1, 1, 0, 30, 29, tzinfo=timezone.utc).timestamp()
        cl = dict(status="idle", p5=25, pw=50, r5=30, rw=30, at5=at, atw=at)
        cx = dict(status="idle", pw=50, rw=30, atw=at)
        sim = Sim()
        before = sim.draw(cl, cx, True, "datetime", epoch=at - 1850, utc_offset_min=-60)
        after = sim.draw(dict(cl, r5=29, rw=29), dict(cx, rw=29),
                         reset_format="datetime", epoch=at - 1790, utc_offset_min=-60)
        self.assertEqual(sim.cc.t1, "12/31\n23:30")
        self.assertIsNone(ImageChops.difference(before, after).getbbox())

    def test_pet_text_fits_inside_ring_and_above_sprite(self):
        source = (Path(__file__).resolve().parents[2] / "firmware/src/main.cpp").read_text(encoding="utf-8")
        widths = dict((c, int(w)) for c, w in re.findall(r"\{'(.)', (\d+), \{", source))

        def width(text, pitch, radius):
            return sum((widths.get(c.upper(), 3) - 1) * pitch + 2 * radius + 1 + pitch
                       for c in text) - pitch

        for text in ["4h59m", "23h59m", "7d", "24h", "12/31", "23:59", "-"]:
            self.assertLessEqual(width(text, 2, 0), 64, text)
        self.assertLess(46 + 6 * 2 + 1, 60)  # Date/time bottom precedes sprite
        for center in [116, 194]:
            self.assertGreaterEqual(center - 32, 80)
            self.assertLessEqual(center + 32, 226)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--preview")
    args, rest = parser.parse_known_args()
    PREVIEW = args.preview
    unittest.main(argv=[__file__, *rest])
