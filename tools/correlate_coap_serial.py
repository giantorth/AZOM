#!/usr/bin/env python3
"""Correlate CoAP requests in a UDP pcapng with serial frames in a paired pcapng.

Given a paired capture (CoAP-over-localhost-UDP on one side, USB-CDC serial on
the other, taken at the same wall-clock time), find every CoAP request whose
Uri-Path matches a substring, then report the serial frames PitHouse (or our
plugin) emitted in the window around each request.

Periodic noise is filtered by default (heartbeats, value-frame stream, parity
polls) so the rare frames that are actually a reaction to the CoAP request
surface clearly. Use --no-filter to see everything.

Usage:
    tools/correlate_coap_serial.py <udp.pcapng> <serial.pcapng> <uri_substring>
        [--window-ms 500] [--coap-ports 40266,55356,59339] [--no-filter]
        [--serial-jsonl <cached.jsonl>]

Examples:
    # Map Feedforward POST to its serial reaction
    tools/correlate_coap_serial.py \\
        captures/iracing-pithouse-udp.pcapng \\
        captures/iracing-pithouse-serial.pcapng \\
        Feedforward
"""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
from collections import Counter
from pathlib import Path
from tempfile import NamedTemporaryFile

REPO_ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(REPO_ROOT / "tools"))

from pcap_to_jsonl import extract as extract_serial  # type: ignore  # noqa: E402

# Frame shapes considered "background noise" — periodic frames that fire
# regardless of any CoAP request. Anything not in this set is potential signal.
# Each entry: (direction, group_hex, device_hex, cmd_hex_prefix). cmd is the
# first byte of the MOZA payload (frame[4] of the 7E-framed bytes — same offset
# for both directions). For b2h frames, response group = request group | 0x80
# and device nibbles are swapped; both forms are listed.
NOISE_SHAPES = {
    # h2b periodic
    ("h2b", "0x41", "0x17", "fd"),  # base heartbeat
    ("h2b", "0x2d", "0x13", "f5"),  # pedals heartbeat
    ("h2b", "0x5a", "0x1b", "00"),  # parity poll (32 Hz)
    ("h2b", "0x5d", "0x1b", "01"),
    ("h2b", "0xda", "0xb1", "00"),
    ("h2b", "0xdd", "0xb1", "01"),
    ("h2b", "0x25", "0x19", "01"),  # LED widget polls (3-frame burst)
    ("h2b", "0x25", "0x19", "02"),
    ("h2b", "0x25", "0x19", "03"),
    ("h2b", "0xa5", "0x91", "01"),
    ("h2b", "0xa5", "0x91", "02"),
    ("h2b", "0xa5", "0x91", "03"),
    ("h2b", "0xc3", "0x71", "7c"),  # value-frame stream
    ("h2b", "0xc3", "0x71", "fc"),
    ("h2b", "0x43", "0x17", "7d"),  # value-frame request
    ("h2b", "0x43", "0x17", "7c"),
    ("h2b", "0x43", "0x17", "fc"),
    # b2h periodic responses (resp_group = req_group | 0x80, dev nibble-swapped)
    ("b2h", "0xc1", "0x71", "fd"),  # 0x41/0x17 echo
    ("b2h", "0xad", "0x31", ""),    # 0x2d/0x13 echo (zero-length heartbeat ack)
    ("b2h", "0xad", "0x31", "00"),
    ("b2h", "0xda", "0xb1", "00"),  # parity poll echoes
    ("b2h", "0xdd", "0xb1", "01"),
    ("b2h", "0xa5", "0x91", "01"),  # LED widget echoes
    ("b2h", "0xa5", "0x91", "02"),
    ("b2h", "0xa5", "0x91", "03"),
    ("b2h", "0xc3", "0x71", "7d"),  # value-frame responses
    ("b2h", "0xc3", "0x71", "7c"),
    ("b2h", "0xc3", "0x71", "fc"),
}


def discover_coap_ports(udp_pcap: Path) -> list[tuple[int, int]]:
    """Find localhost UDP port pairs that look like CoAP (non-mDNS / non-SSDP)."""
    out = subprocess.run(
        ["tshark", "-r", str(udp_pcap), "-Y", "udp",
         "-T", "fields", "-e", "ip.src", "-e", "udp.srcport",
         "-e", "ip.dst", "-e", "udp.dstport"],
        capture_output=True, text=True, timeout=120,
    )
    pairs: set[tuple[int, int]] = set()
    for line in out.stdout.splitlines():
        parts = line.split("\t")
        if len(parts) < 4:
            continue
        src_ip, src_port, dst_ip, dst_port = parts
        if not (src_ip.startswith("127.") and dst_ip.startswith("127.")):
            continue
        try:
            sp, dp = int(src_port), int(dst_port)
        except ValueError:
            continue
        if sp in (5353, 1900) or dp in (5353, 1900):
            continue
        pairs.add((min(sp, dp), max(sp, dp)))
    return sorted(pairs)


def extract_coap_posts(udp_pcap: Path, ports: list[int], uri_substring: str) -> list[dict]:
    """Return list of {frame, t, src, dst, uri, payload_hex} for matching POSTs."""
    decode_args = []
    for p in ports:
        decode_args += ["-d", f"udp.port=={p},coap"]
    proc = subprocess.run(
        ["tshark", "-r", str(udp_pcap), *decode_args,
         "-T", "fields", "-e", "frame.number", "-e", "frame.time_epoch",
         "-e", "udp.srcport", "-e", "udp.dstport",
         "-e", "coap.code", "-e", "coap.opt.uri_path"],
        capture_output=True, text=True, timeout=180,
    )
    posts: list[dict] = []
    for line in proc.stdout.splitlines():
        parts = line.split("\t")
        if len(parts) < 6 or not parts[4]:
            continue
        try:
            code = int(parts[4])
            t = float(parts[1])
        except ValueError:
            continue
        if code != 2:  # CoAP POST
            continue
        uri = parts[5].replace(",", "/")
        if uri_substring not in uri:
            continue
        posts.append({
            "frame": int(parts[0]),
            "t": t,
            "src": parts[2], "dst": parts[3],
            "uri": "/" + uri,
            "payload_hex": _extract_coap_payload_hex(udp_pcap, ports, int(parts[0])),
        })
    return posts


def _extract_coap_payload_hex(udp_pcap: Path, ports: list[int], frame_no: int) -> str:
    """Pull the 4-byte (or whatever) octet-stream payload of a specific CoAP frame."""
    decode_args = []
    for p in ports:
        decode_args += ["-d", f"udp.port=={p},coap"]
    proc = subprocess.run(
        ["tshark", "-r", str(udp_pcap), *decode_args,
         "-Y", f"frame.number=={frame_no}", "-x"],
        capture_output=True, text=True, timeout=30,
    )
    bytes_seen: list[int] = []
    for line in proc.stdout.splitlines():
        if not line or not line[0].isdigit() and line[0] not in "abcdefABCDEF":
            continue
        # tshark -x lines: "0000  AA BB CC ...   <ascii>"
        parts = line.split()
        if not parts or len(parts[0]) != 4:
            continue
        for tok in parts[1:]:
            if len(tok) == 2:
                try:
                    bytes_seen.append(int(tok, 16))
                except ValueError:
                    break
            else:
                break
    # CoAP payload follows the 0xFF marker; find the last occurrence
    if 0xFF in bytes_seen:
        idx = len(bytes_seen) - 1 - bytes_seen[::-1].index(0xFF)
        return bytes(bytes_seen[idx + 1:]).hex()
    return ""


def load_serial_jsonl(path: Path) -> list[dict]:
    return [json.loads(line) for line in path.open()]


def shape_of(rec: dict) -> tuple[str, str, str, str]:
    """Return (direction, group, device, cmd_byte) for a 7E-framed MOZA frame.

    Both h2b and b2h frames on the USB CDC stream carry the same outer framing:
    7E [N] [group] [device] [N-byte payload] [checksum]. The first payload byte
    (frame[4]) is the cmd-id when N > 0; group has bit 7 set on responses;
    device has its nibbles swapped on responses. Zero-length frames (N=0) have
    no cmd byte — leave it blank so identical zero-payload heartbeats land in a
    single bucket.
    """
    raw = bytes.fromhex(rec["hex"])
    if len(raw) < 4:
        return (rec["dir"], "?", "?", "")
    n = raw[1]
    group, dev = raw[2], raw[3]
    cmd = f"{raw[4]:02x}" if n > 0 and len(raw) > 4 else ""
    return (rec["dir"], f"0x{group:02x}", f"0x{dev:02x}", cmd)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("udp_pcap", type=Path)
    ap.add_argument("serial_pcap", type=Path)
    ap.add_argument("uri_substring")
    ap.add_argument("--window-ms", type=int, default=500)
    ap.add_argument("--coap-ports", type=str, default=None,
                    help="Comma-separated UDP ports. Defaults to auto-discovery.")
    ap.add_argument("--no-filter", action="store_true",
                    help="Show all serial frames in the window, not just rare ones.")
    ap.add_argument("--serial-jsonl", type=Path, default=None,
                    help="Reuse a pre-extracted JSONL instead of re-extracting.")
    args = ap.parse_args()

    if args.coap_ports:
        ports = [int(x) for x in args.coap_ports.split(",") if x.strip()]
    else:
        pairs = discover_coap_ports(args.udp_pcap)
        if not pairs:
            print(f"no localhost CoAP UDP flows found in {args.udp_pcap}", file=sys.stderr)
            return 2
        ports = sorted({p for pair in pairs for p in pair})
        print(f"auto-discovered CoAP ports: {ports}", file=sys.stderr)

    posts = extract_coap_posts(args.udp_pcap, ports, args.uri_substring)
    if not posts:
        print(f"no CoAP POSTs matching {args.uri_substring!r} in {args.udp_pcap}", file=sys.stderr)
        return 1

    if args.serial_jsonl and args.serial_jsonl.exists():
        serial = load_serial_jsonl(args.serial_jsonl)
        print(f"loaded {len(serial)} cached serial frames from {args.serial_jsonl}", file=sys.stderr)
    else:
        tmp = NamedTemporaryFile(suffix=".jsonl", delete=False)
        tmp.close()
        n = extract_serial(args.serial_pcap, Path(tmp.name))
        print(f"extracted {n} serial frames to {tmp.name}", file=sys.stderr)
        serial = load_serial_jsonl(Path(tmp.name))

    serial.sort(key=lambda r: r["t"])
    window_s = args.window_ms / 1000.0

    print(f"\n== {len(posts)} CoAP POST(s) matching {args.uri_substring!r} ==\n")
    for post in posts:
        t = post["t"]
        lo, hi = t - window_s, t + window_s
        in_window = [r for r in serial if lo <= r["t"] <= hi]
        if args.no_filter:
            picked = in_window
        else:
            picked = [r for r in in_window if shape_of(r) not in NOISE_SHAPES]

        agg = Counter(shape_of(r) for r in picked)
        print(f"frame={post['frame']} t={t:.6f} src→dst={post['src']}→{post['dst']}")
        print(f"  uri={post['uri']}")
        print(f"  payload_hex={post['payload_hex']}  (LE int32={_le_int(post['payload_hex'])})")
        print(f"  serial frames in ±{args.window_ms}ms: total={len(in_window)} non-noise={len(picked)}")
        for (d, g, dev, cmd), n in agg.most_common(20):
            print(f"    {n:4d}  {d:3s} {g:>4s} {dev:>4s} cmd={cmd}")
        # Print up to 8 actual non-noise frames with their full hex and dt
        for r in picked[:8]:
            dt_ms = (r["t"] - t) * 1000
            print(f"    [{dt_ms:+8.2f}ms] {r['dir']:3s} {r['hex']}")
        print()

    return 0


def _le_int(hex_str: str) -> str:
    if not hex_str:
        return "?"
    try:
        b = bytes.fromhex(hex_str)
        if len(b) == 4:
            return str(int.from_bytes(b, "little", signed=True))
        return hex_str
    except ValueError:
        return hex_str


if __name__ == "__main__":
    sys.exit(main())
