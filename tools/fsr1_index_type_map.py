#!/usr/bin/env python3
"""Derive the FSR1 (group 0x42, dev 0x17) page-index -> record-type map from a
PitHouse capture, streaming so large files don't need full conversion.

Correlates the wheel's active page (from firmware-debug "Param 6 Written: N"
b2h text, group 0x0e, and/or host g32/0x81 select commands) with the record
type PitHouse streams (h2b group 0x42, dev 0x17) while that page is active.

Usage: tools/fsr1_index_type_map.py <capture.pcapng> [max_frames]
"""
from __future__ import annotations
import re, sys, struct, collections
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(REPO / "usb-capture"))
from extract_moza_frames import (  # noqa: E402
    PCAPNG_BLOCK_EPB, iter_pcapng_blocks, parse_usbpcap_payload, scan_moza_frames,
)

def main() -> int:
    path = Path(sys.argv[1])
    max_frames = int(sys.argv[2]) if len(sys.argv) > 2 else 0
    cur_idx = None
    # index -> Counter(record_type) counted only in STEADY state (same type held >=STEADY frames)
    idx_types: dict[int, collections.Counter] = collections.defaultdict(collections.Counter)
    idx_order = []          # first-seen order of indices
    STEADY = 15
    last_type = None
    run = 0
    n = 0
    raw = path.read_bytes()
    for btype, body in iter_pcapng_blocks(raw):
        if btype != PCAPNG_BLOCK_EPB:
            continue
        cap_len = struct.unpack_from("<I", body, 12)[0]
        pkt = body[20 : 20 + cap_len]
        transfer, endpoint, _, payload = parse_usbpcap_payload(pkt)
        if transfer != 0x03 or not payload:
            continue
        direction = "b2h" if (endpoint & 0x80) else "h2b"
        for frame in scan_moza_frames(payload):
            n += 1
            if max_frames and n > max_frames:
                _dump(idx_types, idx_order); return 0
            if len(frame) < 5 or frame[0] != 0x7E:
                continue
            grp, dev = frame[2], frame[3]
            # active page from firmware-debug text (b2h) or host select (h2b)
            txt = bytes(b if 32 <= b < 127 else 46 for b in frame).decode("ascii", "replace")
            m = re.search(r"Param 6 Written: *(\d+)", txt)
            if m:
                cur_idx = int(m.group(1)); last_type = None; run = 0
                if cur_idx not in idx_order:
                    idx_order.append(cur_idx)
                continue
            if grp in (0x32, 0x81) and direction == "h2b" and len(frame) >= 6:
                cur_idx = frame[4]; last_type = None; run = 0
                if cur_idx not in idx_order:
                    idx_order.append(cur_idx)
                continue
            # streamed FSR1 dashboard record — count only once the same type has been
            # held for STEADY consecutive frames (filters page-transition bleed).
            if grp == 0x42 and dev == 0x17 and direction == "h2b" and cur_idx is not None:
                t = frame[4]
                if t == 0x0d:
                    continue  # background cache, never a page's primary
                if t == last_type:
                    run += 1
                else:
                    last_type = t; run = 1
                if run >= STEADY:
                    idx_types[cur_idx][t] += 1
        if n and n % 200000 == 0:
            print(f"... {n} frames, indices seen: {sorted(idx_types)}", file=sys.stderr)
    _dump(idx_types, idx_order)
    return 0

def _dump(idx_types, idx_order):
    print("\n=== FSR1 page index -> streamed record type(s) (ground truth) ===")
    for idx in sorted(idx_types):
        tallies = idx_types[idx].most_common()
        shown = "  ".join(f"0x{t:02x}:{c}" for t, c in tallies)
        primary = [f"0x{t:02x}" for t, c in tallies if c > 0.02 * tallies[0][1]]
        print(f"  index {idx:2d} -> {{ {', '.join(primary)} }}    (counts: {shown})")

if __name__ == "__main__":
    raise SystemExit(main())
