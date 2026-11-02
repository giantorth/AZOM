#!/usr/bin/env python3
"""Correlate the display's 'request_syn_t: invalid data size' log errors with
host session open/close frames on the wire, per bug bundle.

Display-log times are mapped to host time with the lower-bound clock offset
from displaylog_clock.py (so mapped times are upper bounds, typically 0-6 s late).

With --summary, only bundles whose pulls kept up with the log (median batch-head
slack < 10 s, so the offset bound is tight) are scored: each SYN error is matched
to the nearest host close frame on the wire.

usage: syn_error_vs_wire.py [--summary] <bundle files dir> [...]
"""
import re
import sys
from datetime import datetime
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from displaylog_clock import parse  # noqa: E402

EVENTS = re.compile(r"request_syn_t|SERVERCONNECTED|TileMap\] client created|stod")
# host session open (type 0x81) / close (type 0x00) on group 0x43 dev 0x17, and device-initiated opens
WIRE = re.compile(r"^(\S+ \S+) ([TR])\s+\S+\s+(?:7e \S\S )?(?:43 17|c3 71) 7c 00 (\S\S) (81|00) ")


def wire_events(d):
    out = []
    for name in ("serial-capture-startup.txt", "serial-capture-rolling.txt"):
        p = d / name
        if not p.exists():
            continue
        for line in p.read_text(encoding="utf-8", errors="replace").splitlines():
            m = WIRE.match(line)
            if m:
                ts = datetime.strptime(m.group(1), "%Y-%m-%d %H:%M:%S.%f")
                kind = "open" if m.group(4) == "81" else "close"
                who = "host" if m.group(2) == "T" else "dev"
                out.append((ts, f"WIRE {who} {kind} sess=0x{m.group(3)}"))
    return out


def summary(d, rows, lo):
    batches = {}
    for h, w, _ in rows:
        batches[h] = max(batches.get(h, w), w)
    slack = sorted((h - w).total_seconds() + lo for h, w in batches.items())
    if slack[len(slack) // 2] >= 10:
        return
    errs = [w.timestamp() - lo for h, w, b in rows if "request_syn_t" in b]
    closes = [ts.timestamp() for ts, t in wire_events(d) if t.startswith("WIRE host close")]
    if not errs or not closes:
        return
    t0, t1 = min(closes) - 60, max(ts.timestamp() for ts, _ in wire_events(d)) + 60
    errs = [e for e in errs if t0 <= e <= t1]  # only errors inside the capture window
    if not errs:
        return
    # mapped times are upper bounds: the matching close should precede by 0..~3 s
    near = sum(1 for e in errs if any(-1.5 <= e - c <= 4 for c in closes))
    print(f"{d.parent.name}: {len(errs)} SYN errors in capture window, {near} within 4 s after a host close, "
          f"{len(closes)} host closes")


def main():
    args = sys.argv[1:]
    want_summary = "--summary" in args
    for arg in [a for a in args if a != "--summary"]:
        d = Path(arg)
        if not (d / "device-display-log.txt").exists():
            continue
        rows = parse(d / "device-display-log.txt")
        if not rows:
            continue
        lo = max((w - h).total_seconds() for h, w, _ in rows)
        if want_summary:
            summary(d, rows, lo)
            continue
        ev = [(datetime.fromtimestamp(w.timestamp() - lo), "DISP " + re.sub(r"\s+", " ", b.strip())[:70])
              for h, w, b in rows if EVENTS.search(b)]
        ev += wire_events(d)
        if not ev:
            continue
        span = (rows[0][1], rows[-1][1])
        print(f"=== {d.parent.name}  display log covers wheel {span[0]:%H:%M:%S}..{span[1]:%H:%M:%S} (host ~{datetime.fromtimestamp(span[0].timestamp()-lo):%H:%M:%S}..)")
        prev = None
        for ts, text in sorted(ev):
            # collapse repeats
            if text == prev:
                continue
            prev = text
            print(f"  {ts:%H:%M:%S.%f}"[:-3] + f"  {text}")


if __name__ == "__main__":
    main()
