#!/usr/bin/env python3
"""Per-second timeline of live LED frames in an AZOM serial capture.

Answers "did the LED stream stop, and for how long" — the question bundle
2X7HPMMS turned on. A gap longer than ~1 s in the wheel or CM2 rows means the
firmware dropped host LED ownership and the rim fell back to its stored idle
effect.

Counts transmitted frames per wall-clock second:

  tel17     group 0x43 -> wheel (session keepalive; NOT an LED frame, shown so a
            quiet LED row can be told from a quiet link)
  rpmCol    3F 19 00   wheel RPM colour chunks
  rpmLit    of those, chunks carrying at least one non-black LED
  rpmMask   3F 1A 00   wheel RPM active/window bitmask
  btnCol    3F 19 01   wheel button colour chunks
  btnMask   3F 1A 01   wheel button bitmask
  knobCol   3F 19 03   wheel knob-ring colour chunks
  knobMask  3F 1A 03   wheel knob bitmask
  cm2       group 0x32 -> dash, any subcommand

Usage:
    tools/led-timeline.py <capture.txt> [--gaps]

--gaps prints only seconds with no wheel LED frame at all, which is the fast
read when checking a keepalive fix.

Input is the bundle's serial-capture-startup.txt / serial-capture-rolling.txt,
i.e. lines of "<date> <time> <dir> <label> <hex bytes...>".
"""
import argparse
import sys
from collections import OrderedDict, defaultdict

WHEEL_LED = {
    (0x19, 0x00): "rpmCol",
    (0x1A, 0x00): "rpmMask",
    (0x19, 0x01): "btnCol",
    (0x1A, 0x01): "btnMask",
    (0x19, 0x03): "knobCol",
    (0x1A, 0x03): "knobMask",
}

COLUMNS = ["tel17", "rpmCol", "rpmLit", "rpmMask",
           "btnCol", "btnMask", "knobCol", "knobMask", "cm2"]

# Any of these present in a second means the wheel was still being fed.
WHEEL_FEED = ["rpmCol", "rpmMask", "btnCol", "btnMask", "knobCol", "knobMask"]


def parse(path):
    """Yield (second, direction, frame_bytes) for each parsable capture line."""
    with open(path, encoding="utf-8", errors="replace") as handle:
        for line in handle:
            if line.startswith("#") or not line.strip():
                continue
            parts = line.split()
            if len(parts) < 6:
                continue
            second = parts[0] + " " + parts[1][:8]
            direction = parts[2]
            try:
                frame = bytes(int(token, 16) for token in parts[4:])
            except ValueError:
                continue
            yield second, direction, frame


def classify(frame):
    """Map a frame to a column name, or None when it isn't LED/keepalive traffic."""
    if len(frame) < 6 or frame[0] != 0x7E:
        return None
    group, dev = frame[2], frame[3]
    if group == 0x3F and dev == 0x17:
        return WHEEL_LED.get((frame[4], frame[5]))
    if group == 0x32 and dev == 0x14:
        return "cm2"
    if group == 0x43 and dev == 0x17:
        return "tel17"
    return None


def chunk_is_lit(frame):
    """True when a colour chunk carries a non-black LED. Records are idx,R,G,B."""
    payload = frame[6:-1]
    for i in range(0, len(payload) - 3, 4):
        if payload[i + 1] or payload[i + 2] or payload[i + 3]:
            return True
    return False


def build(path):
    per_second = OrderedDict()
    for second, direction, frame in parse(path):
        if direction != "T":
            continue
        column = classify(frame)
        if column is None:
            continue
        row = per_second.setdefault(second, defaultdict(int))
        row[column] += 1
        if column == "rpmCol" and chunk_is_lit(frame):
            row["rpmLit"] += 1
        if column == "rpmMask":
            row["rpmMaskVal"] = frame[6:-1].hex()
    return per_second


def print_table(per_second):
    print("second               " + " ".join(f"{c:>8}" for c in COLUMNS) + "  rpmMaskVal")
    for second, row in per_second.items():
        cells = " ".join(f"{row.get(c, 0):>8}" for c in COLUMNS)
        print(f"{second}  {cells}  {row.get('rpmMaskVal', '-')}")


def print_gaps(per_second):
    run_start = None
    previous = None
    printed = False
    for second, row in per_second.items():
        fed = any(row.get(c, 0) for c in WHEEL_FEED)
        if not fed and run_start is None:
            run_start = second
        elif fed and run_start is not None:
            print(f"no wheel LED frames: {run_start} .. {previous}")
            printed = True
            run_start = None
        previous = second
    if run_start is not None:
        print(f"no wheel LED frames: {run_start} .. {previous} (to end of capture)")
        printed = True
    if not printed:
        print("no gaps: every second in the capture carried at least one wheel LED frame")


def main():
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("capture", help="serial-capture-*.txt from a bug-report bundle")
    parser.add_argument("--gaps", action="store_true",
                        help="print only the seconds with no wheel LED frame")
    args = parser.parse_args()

    per_second = build(args.capture)
    if not per_second:
        print("no LED or wheel-keepalive frames found in this capture")
        return 1
    if args.gaps:
        print_gaps(per_second)
    else:
        print_table(per_second)
    return 0


if __name__ == "__main__":
    sys.exit(main())
