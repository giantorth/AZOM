#!/usr/bin/env python3
"""Estimate the wheel-display clock offset in a bug bundle's device-display-log.txt
and map selected display-log lines onto host time.

Each pull batch is stamped with host receive time (second resolution); every line
carries the display's own wall-clock stamp. A line can't be logged after it was
received, so offset = wheel - host >= max(wheel_ts - host_recv) over all lines.
The newest line in each batch tightens that bound.

usage: displaylog_clock.py <bundle files dir> [regex ...]
"""
import re
import sys
from datetime import datetime
from pathlib import Path

LINE = re.compile(r"^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d) \[wheel\] \[\w{3} (\w{3})\s+(\d+) (\d\d:\d\d:\d\d) (\d{4})\]"
                  r"[^\]]*\](.*)$")
MS = re.compile(r"^\[(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3})\]")


def parse(path):
    out = []
    for raw in path.read_text(encoding="utf-8", errors="replace").splitlines():
        m = LINE.match(raw)
        if not m:
            continue
        host = datetime.strptime(m.group(1), "%Y-%m-%d %H:%M:%S")
        wheel = datetime.strptime(f"{m.group(5)} {m.group(2)} {m.group(3)} {m.group(4)}", "%Y %b %d %H:%M:%S")
        body = m.group(6)
        ms = MS.match(body)
        if ms:
            wheel = datetime.strptime(ms.group(1), "%Y-%m-%d %H:%M:%S.%f")
        out.append((host, wheel, body))
    return out


def main():
    d = Path(sys.argv[1])
    pats = [re.compile(p) for p in sys.argv[2:]]
    rows = parse(d / "device-display-log.txt")
    if not rows:
        print("no parsable lines")
        return
    # offset lower bound: wheel - host can't be less than any line's (wheel - recv)
    lo = max((w - h).total_seconds() for h, w, _ in rows)
    # per batch: gap between newest line and the receive stamp, i.e. how stale the batch head is
    batches = {}
    for h, w, _ in rows:
        batches[h] = max(batches.get(h, w), w)
    gaps = sorted(((h - w).total_seconds() + lo) for h, w in batches.items())
    print(f"offset (wheel - host) >= {lo:+.3f}s; batch-head slack min={gaps[0]:.1f}s "
          f"median={gaps[len(gaps)//2]:.1f}s over {len(gaps)} batches")
    print(f"first line wheel={rows[0][1]}  last={rows[-1][1]}")
    for h, w, body in rows:
        if any(p.search(body) for p in pats):
            est = w.timestamp() - lo
            print(f"host<= {datetime.fromtimestamp(est).strftime('%H:%M:%S.%f')[:-3]}  wheel={w.strftime('%H:%M:%S')}  {body.strip()[:110]}")


if __name__ == "__main__":
    main()
