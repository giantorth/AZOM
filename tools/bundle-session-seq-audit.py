#!/usr/bin/env python3
"""Audit per-session inbound chunk sequencing in a bug-report bundle capture.

Reads a `serial-capture-*.txt` from a diagnostics bundle and, for every
catalog-bearing session, reports the wheel->host `7c 00 <sess> 01 <seq>` data
chunk stream: gaps, out-of-order arrivals, and — the part that matters for
catalog corruption — whether a chunk that would be REJECTED by
ChannelCatalogParser.AppendChunkIfNew's running-max dedup (`seq <= highest
seen`) was actually a first delivery rather than a retransmit.

Every such rejection is a permanent hole in the linear TLV byte stream the
catalog parser walks, which shows up as empty / duplicated / mis-indexed
channel URLs in the "Wheel channel catalog" diagnostics section.

Usage:
    tools/bundle-session-seq-audit.py <capture.txt> [<capture.txt> ...]
    tools/bundle-session-seq-audit.py bundle/serial-capture-rolling.txt
"""
import re
import sys
from collections import defaultdict

LINE = re.compile(r"(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d+) (T|R)\s+(\S+)\s+(.*)")


def audit(path):
    # session -> list of (timestamp, seq)
    inbound = defaultdict(list)
    with open(path, encoding="utf-8", errors="replace") as fh:
        for line in fh:
            m = LINE.match(line)
            if not m or m.group(2) != "R":
                continue
            b = m.group(4).split()
            # c3 <tgt> 7c 00 <sess> <type=01> <seq lo> <seq hi> …
            if len(b) < 8 or b[0] != "c3" or b[2] != "7c" or b[3] != "00":
                continue
            if b[5] != "01":
                continue
            sess = int(b[4], 16)
            seq = int(b[6], 16) | (int(b[7], 16) << 8)
            inbound[sess].append((m.group(1), seq))

    print(f"### {path}")
    if not inbound:
        print("  (no inbound type=01 session chunks)\n")
        return

    for sess in sorted(inbound):
        stream = inbound[sess]
        seen = set()
        highest = None
        appended = set()
        rejected_first_delivery = []   # (ts, seq) — real data thrown away
        rejected_retransmit = 0
        out_of_order = 0
        gaps = []

        for ts, seq in stream:
            first_delivery = seq not in seen
            seen.add(seq)
            if highest is None:
                highest = seq
                appended.add(seq)
                continue
            if seq <= highest:
                # AppendChunkIfNew drops this.
                if first_delivery:
                    rejected_first_delivery.append((ts, seq))
                    out_of_order += 1
                else:
                    rejected_retransmit += 1
                continue
            if seq > highest + 1:
                gaps.append((ts, highest + 1, seq - 1))
            highest = seq
            appended.add(seq)

        missing = sorted(s for lo_hi in gaps for s in range(lo_hi[1], lo_hi[2] + 1)
                         if s not in appended)
        print(f"  sess=0x{sess:02X}  chunks={len(stream)}  distinct={len(seen)}  "
              f"appended={len(appended)}")
        print(f"    retransmits correctly deduped : {rejected_retransmit}")
        print(f"    FIRST DELIVERIES thrown away  : {len(rejected_first_delivery)}"
              f"{'   <-- catalog corruption' if rejected_first_delivery else ''}")
        for ts, seq in rejected_first_delivery[:8]:
            print(f"        {ts}  seq=0x{seq:04X}")
        if len(rejected_first_delivery) > 8:
            print(f"        … {len(rejected_first_delivery) - 8} more")
        print(f"    forward gaps (never seen)     : {len(missing)}"
              f"{'   <-- lost on the wire' if missing else ''}")
        if missing:
            print("        " + " ".join(f"0x{s:04X}" for s in missing[:16])
                  + (" …" if len(missing) > 16 else ""))
    print()


if __name__ == "__main__":
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    for p in sys.argv[1:]:
        audit(p)
