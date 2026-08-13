#!/usr/bin/env python3
"""Determine whether the FSR1 per-type header anchors (B1,B2) are wheel-reported
(negotiated) or PitHouse-invented. Scans the b2h (wheel->host) stream for frames
that carry the distinctive per-type anchor pairs, and dumps the wheel's non-ack
b2h frames (handshake/config) for inspection.

Usage: tools/fsr1_anchor_source.py <capture.pcapng> [max_frames]
"""
from __future__ import annotations
import sys, struct, collections
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(REPO / "usb-capture"))
from extract_moza_frames import (  # noqa: E402
    PCAPNG_BLOCK_EPB, iter_pcapng_blocks, parse_usbpcap_payload, scan_moza_frames,
)

# distinctive (b1,b2) anchor pairs seen in h2b, per type
ANCHORS = {0x01:(0x0b,0x88), 0x03:(0x27,0xfe), 0x04:(0x02,0x40),
           0x06:(0x05,0x08), 0x09:(0x00,0x48), 0x0e:(0x18,0x01)}

def main() -> int:
    path = Path(sys.argv[1])
    max_frames = int(sys.argv[2]) if len(sys.argv) > 2 else 0
    b2h_groups = collections.Counter()
    b2h_samples = collections.defaultdict(list)
    anchor_hits = collections.Counter()   # which anchor pairs appear in ANY b2h frame
    n = 0
    for btype, body in iter_pcapng_blocks(path.read_bytes()):
        if btype != PCAPNG_BLOCK_EPB:
            continue
        cap_len = struct.unpack_from("<I", body, 12)[0]
        pkt = body[20:20 + cap_len]
        transfer, endpoint, _, payload = parse_usbpcap_payload(pkt)
        if transfer != 0x03 or not payload:
            continue
        if not (endpoint & 0x80):     # only b2h (wheel -> host)
            continue
        for frame in scan_moza_frames(payload):
            if len(frame) < 4 or frame[0] != 0x7E:
                continue
            n += 1
            if max_frames and n > max_frames:
                return _dump(b2h_groups, b2h_samples, anchor_hits)
            grp, dev = frame[2], frame[3]
            b2h_groups[(grp, dev)] += 1
            # record a few non-trivial (len>4) samples per group
            if frame[1] > 4 and len(b2h_samples[(grp, dev)]) < 4:
                b2h_samples[(grp, dev)].append(frame.hex(" "))
            # does this frame contain any distinctive anchor pair as consecutive bytes?
            for t, (a, b) in ANCHORS.items():
                for i in range(len(frame) - 1):
                    if frame[i] == a and frame[i+1] == b:
                        anchor_hits[(t, a, b)] += 1
                        break
    return _dump(b2h_groups, b2h_samples, anchor_hits)

def _dump(groups, samples, hits):
    print("\n=== b2h (wheel->host) groups ===")
    for (g, d), c in groups.most_common(12):
        print(f"  0x{g:02x}/0x{d:02x}: {c}")
    print("\n=== b2h non-ack samples (handshake/config candidates) ===")
    for k, ss in list(samples.items())[:10]:
        print(f"  0x{k[0]:02x}/0x{k[1]:02x}:")
        for s in ss:
            print(f"     {s}")
    print("\n=== do the h2b anchor pairs appear anywhere in b2h? (negotiated?) ===")
    if not hits:
        print("  NONE — anchors never appear in wheel->host traffic => PitHouse-side, not wheel-reported")
    for (t, a, b), c in hits.most_common():
        print(f"  type 0x{t:02x} anchor {a:02x} {b:02x}: seen in {c} b2h frames")
    return 0

if __name__ == "__main__":
    raise SystemExit(main())
