#!/usr/bin/env python3
"""Probe the FSR1 (group 0x42, dev 0x17) frame header bytes in a capture, streaming.
For each record type, show the distribution of the 4 header bytes after the type byte
(frame[5..8]) and the frame length — to determine whether B1/B2 are a fixed per-type
anchor or vary (data / counter). Also dumps a few raw frames per type.

Usage: tools/fsr1_header_probe.py <capture.pcapng> [max_frames]
"""
from __future__ import annotations
import sys, struct, collections
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(REPO / "usb-capture"))
from extract_moza_frames import (  # noqa: E402
    PCAPNG_BLOCK_EPB, iter_pcapng_blocks, parse_usbpcap_payload, scan_moza_frames,
)

def main() -> int:
    path = Path(sys.argv[1])
    max_frames = int(sys.argv[2]) if len(sys.argv) > 2 else 0
    hdr = collections.defaultdict(collections.Counter)  # type -> Counter((b1,b2,p3,p4,len))
    samples = collections.defaultdict(list)
    n = 0
    for btype, body in iter_pcapng_blocks(path.read_bytes()):
        if btype != PCAPNG_BLOCK_EPB:
            continue
        cap_len = struct.unpack_from("<I", body, 12)[0]
        pkt = body[20:20 + cap_len]
        transfer, endpoint, _, payload = parse_usbpcap_payload(pkt)
        if transfer != 0x03 or not payload:
            continue
        if endpoint & 0x80:      # b2h — only host->wheel streams the dashboard records
            continue
        for frame in scan_moza_frames(payload):
            if len(frame) < 9 or frame[0] != 0x7E or frame[2] != 0x42 or frame[3] != 0x17:
                continue
            n += 1
            if max_frames and n > max_frames:
                return _dump(hdr, samples)
            t = frame[4]
            key = (frame[5], frame[6], frame[7], frame[8], frame[1])
            hdr[t][key] += 1
            if len(samples[t]) < 3:
                samples[t].append(frame.hex(" "))
    return _dump(hdr, samples)

def _dump(hdr, samples):
    print("\n=== FSR1 header-byte distribution per record type (h2b, group 0x42/dev 0x17) ===")
    for t in sorted(hdr):
        combos = hdr[t].most_common()
        print(f"\ntype 0x{t:02x}: {len(combos)} distinct header combos, {sum(hdr[t].values())} frames")
        for (b1, b2, p3, p4, ln), c in combos[:6]:
            print(f"    B1={b1:02x} B2={b2:02x} pad={p3:02x} {p4:02x} len={ln:02x}  x{c}")
        for s in samples[t][:1]:
            print(f"    e.g. {s}")
    return 0

if __name__ == "__main__":
    raise SystemExit(main())
