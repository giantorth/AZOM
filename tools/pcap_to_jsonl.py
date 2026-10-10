#!/usr/bin/env python3
"""Extract MOZA wire frames from a USBPcap pcapng into JSONL.

Output records are compatible with tools/moza_trace.py (load_trace):
    {"t": <epoch_seconds>, "dir": "h2b"|"b2h", "hex": "<frame bytes hex>"}

Streams the capture through tools/usbpcap.py (no whole-file read) and keeps
only checksum-valid, unstuffed frames (tools/moza_wire.py). Per-frame epoch
timestamps let the output be cross-correlated with paired UDP captures
(tools/correlate_coap_serial.py) or SimHub replays (tools/simhub_replay.py).

Usage:
    tools/pcap_to_jsonl.py <capture.pcapng> <output.jsonl>                 # every device
    tools/pcap_to_jsonl.py <capture.pcapng> <output.jsonl> --device 5      # one USB address
    tools/pcap_to_jsonl.py <capture.pcapng> <output.jsonl> --device auto   # the busiest MOZA device
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from moza_wire import scan_moza_frames_checked  # noqa: E402
from usbpcap import MOZA_VID, TRANSFER_BULK, device_descriptor_ids, iter_usb_packets  # noqa: E402


def pick_moza_device(capture: str) -> int | None:
    counts: dict[int, int] = {}
    ids: dict[int, tuple[int, int]] = {}
    for pkt in iter_usb_packets(capture):
        if pkt.transfer == TRANSFER_BULK and pkt.payload:
            counts[pkt.device] = counts.get(pkt.device, 0) + sum(1 for _ in scan_moza_frames_checked(pkt.payload))
            continue
        d = device_descriptor_ids(pkt)
        if d is not None:
            ids[pkt.device] = d
    moza = [(n, dev) for dev, n in counts.items() if ids.get(dev, (None,))[0] == MOZA_VID and n > 0]
    if moza:
        return max(moza)[1]
    if counts and not ids:
        return max(counts.items(), key=lambda x: x[1])[0]
    return None


def extract(capture: str, output: str, device: int | None) -> int:
    count = 0
    with open(output, "w") as fh:
        for pkt in iter_usb_packets(capture):
            if pkt.transfer != TRANSFER_BULK or not pkt.payload:
                continue
            if device is not None and pkt.device != device:
                continue
            direction = "b2h" if pkt.is_in else "h2b"
            for frame in scan_moza_frames_checked(pkt.payload):
                fh.write(json.dumps({"t": pkt.t, "dir": direction, "hex": frame.hex()}))
                fh.write("\n")
                count += 1
    return count


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("capture")
    ap.add_argument("output")
    ap.add_argument("--device", default=None, help="USBPcap device address, 'auto', or omit for all")
    args = ap.parse_args()
    device: int | None
    if args.device is None:
        device = None
    elif args.device == "auto":
        device = pick_moza_device(args.capture)
        if device is None:
            print("no MOZA device with frames found", file=sys.stderr)
            return 2
        print(f"selected device {device}", file=sys.stderr)
    else:
        device = int(args.device, 0)
    n = extract(args.capture, args.output, device)
    print(f"wrote {n} frames to {args.output}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
