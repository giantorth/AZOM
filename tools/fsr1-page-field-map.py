#!/usr/bin/env python3
"""Per-page-index view of a PitHouse FSR1 group-0x42 stream.

For every active-page window (from the wheel's "Param 6 Written: N" firmware-debug
text and/or the host's g32/0x81 select) report, per streamed record type:
  * the b1/b2 sub-header histogram (which per-dashboard descriptor PitHouse uses
    on THAT page — the wheel gates records on it, so it is page-specific, not
    per-type),
  * the frame count,
  * per data-byte min/max/distinct/#changes so a field that is live on that page
    can be told apart from one the dashboard leaves at a constant.

Usage: tools/fsr1-page-field-map.py <capture.pcapng> [--index N] [--type 0xNN]
"""
from __future__ import annotations
import argparse, collections, re, struct, sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(REPO / "usb-capture"))
from extract_moza_frames import (  # noqa: E402
    PCAPNG_BLOCK_EPB, iter_pcapng_blocks, parse_usbpcap_payload, scan_moza_frames,
)


class ByteStat:
    __slots__ = ("lo", "hi", "distinct", "changes", "prev")

    def __init__(self):
        self.lo, self.hi = 255, 0
        self.distinct = set()
        self.changes = 0
        self.prev = None

    def add(self, v):
        if v < self.lo: self.lo = v
        if v > self.hi: self.hi = v
        if len(self.distinct) < 64: self.distinct.add(v)
        if self.prev is not None and v != self.prev: self.changes += 1
        self.prev = v


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("capture")
    ap.add_argument("--index", type=int, default=None)
    ap.add_argument("--type", default=None)
    args = ap.parse_args()
    want_type = int(args.type, 0) if args.type else None

    cur = None                      # current page index
    # (index, type) -> {"hdr": Counter, "n": int, "bytes": {off: ByteStat}}
    acc: dict[tuple, dict] = {}
    order = []

    def bucket(idx, t):
        k = (idx, t)
        if k not in acc:
            acc[k] = {"hdr": collections.Counter(), "n": 0,
                      "bytes": collections.defaultdict(ByteStat)}
            order.append(k)
        return acc[k]

    for btype, body in iter_pcapng_blocks(Path(args.capture).read_bytes()):
        if btype != PCAPNG_BLOCK_EPB:
            continue
        cap_len = struct.unpack_from("<I", body, 12)[0]
        pkt = body[20:20 + cap_len]
        transfer, endpoint, _, payload = parse_usbpcap_payload(pkt)
        if transfer != 0x03 or not payload:
            continue
        b2h = bool(endpoint & 0x80)
        for frame in scan_moza_frames(payload):
            if len(frame) < 5 or frame[0] != 0x7E:
                continue
            if b2h:
                txt = bytes(b if 32 <= b < 127 else 46 for b in frame).decode("ascii", "replace")
                m = re.search(r"Param 6 Written: *(\d+)", txt)
                if m:
                    cur = int(m.group(1))
                continue
            grp, dev = frame[2], frame[3]
            if grp == 0x32 and dev == 0x17 and len(frame) >= 9 and frame[4] == 0x81:
                cur = frame[8]
                continue
            if grp != 0x42 or dev != 0x17 or len(frame) < 8:
                continue
            t = frame[4]
            if want_type is not None and t != want_type:
                continue
            if args.index is not None and cur != args.index:
                continue
            b = bucket(cur, t)
            b["hdr"][(frame[5], frame[6])] += 1
            b["n"] += 1
            plen = frame[1]
            for off in range(5, plen):
                fi = 4 + off
                if fi < len(frame) - 1:
                    b["bytes"][off].add(frame[fi])

    for idx, t in order:
        d = acc[(idx, t)]
        if d["n"] < 5:
            continue
        hdrs = ", ".join(f"b1={h[0]:02x}/b2={h[1]:02x}×{c}" for h, c in d["hdr"].most_common(6))
        print(f"\n=== page index {idx}  type 0x{t:02x}  frames={d['n']} ===")
        print(f"  sub-header: {hdrs}")
        for off in sorted(d["bytes"]):
            s = d["bytes"][off]
            tag = "CONST" if s.changes == 0 else f"chg={s.changes}"
            vals = ""
            if len(s.distinct) <= 8:
                vals = "  vals=" + ",".join(f"{v:02x}" for v in sorted(s.distinct))
            print(f"   data[{off:2d}] lo={s.lo:3d} hi={s.hi:3d} distinct={len(s.distinct):3d} {tag}{vals}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
