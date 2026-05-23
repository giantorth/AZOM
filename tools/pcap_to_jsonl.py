#!/usr/bin/env python3
"""Extract MOZA wire frames from a USBPcap pcapng into JSONL.

Output records are compatible with tools/moza_trace.py (load_trace):
    {"t": <epoch_seconds>, "dir": "h2b"|"b2h", "hex": "<frame bytes hex>"}

Reuses the pcapng / USBPcap / MOZA scanner primitives from
usb-capture/extract_moza_frames.py to avoid duplicating the parser. Adds
per-frame epoch timestamps so the output can be cross-correlated with paired
UDP captures (see tools/correlate_coap_serial.py).

Usage:
    tools/pcap_to_jsonl.py <capture.pcapng> <output.jsonl>
"""
from __future__ import annotations

import json
import struct
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(REPO_ROOT / "usb-capture"))

from extract_moza_frames import (  # type: ignore  # noqa: E402
    PCAPNG_BLOCK_EPB,
    PCAPNG_BLOCK_IDB,
    iter_pcapng_blocks,
    parse_usbpcap_payload,
    scan_moza_frames,
)


def _parse_idb_tsresol(body: bytes) -> int:
    """Return the IDB's if_tsresol value (default 6 = microseconds)."""
    # IDB body: u16 linktype, u16 reserved, u32 snaplen, options...
    off = 8
    while off + 4 <= len(body):
        code, length = struct.unpack_from("<HH", body, off)
        off += 4
        if code == 0:  # opt_endofopt
            break
        if code == 9 and length >= 1:  # if_tsresol
            return body[off]
        off += length + ((4 - (length % 4)) % 4)
    return 6


def _ticks_per_second(tsresol: int) -> int:
    if tsresol & 0x80:
        # MSB set -> base-2 exponent
        return 1 << (tsresol & 0x7F)
    return 10 ** tsresol


def _epb_timestamp(body: bytes, ticks_per_sec: int) -> float:
    # EPB body: u32 interface_id, u32 ts_high, u32 ts_low, ...
    ts_high, ts_low = struct.unpack_from("<II", body, 4)
    ticks = (ts_high << 32) | ts_low
    return ticks / ticks_per_sec


def extract(capture: Path, output: Path) -> int:
    raw = capture.read_bytes()

    # Pass 1: find IDB to learn timestamp resolution.
    tsresol = 6
    for btype, body in iter_pcapng_blocks(raw):
        if btype == PCAPNG_BLOCK_IDB:
            tsresol = _parse_idb_tsresol(body)
            break
    ticks_per_sec = _ticks_per_second(tsresol)

    # Pass 2: walk EPBs, extract bulk-transfer MOZA frames with timestamps.
    count = 0
    with output.open("w") as fh:
        for btype, body in iter_pcapng_blocks(raw):
            if btype != PCAPNG_BLOCK_EPB:
                continue
            t = _epb_timestamp(body, ticks_per_sec)
            cap_len = struct.unpack_from("<I", body, 12)[0]
            pkt = body[20 : 20 + cap_len]
            transfer, endpoint, _, payload = parse_usbpcap_payload(pkt)
            if transfer != 0x03 or not payload:
                continue
            direction = "b2h" if (endpoint & 0x80) else "h2b"
            for frame in scan_moza_frames(payload):
                fh.write(json.dumps({
                    "t": t,
                    "dir": direction,
                    "hex": frame.hex(),
                }))
                fh.write("\n")
                count += 1
    return count


def main() -> int:
    if len(sys.argv) != 3:
        print(f"Usage: {sys.argv[0]} <input.pcapng> <output.jsonl>", file=sys.stderr)
        return 1
    n = extract(Path(sys.argv[1]), Path(sys.argv[2]))
    print(f"wrote {n} frames to {sys.argv[2]}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
