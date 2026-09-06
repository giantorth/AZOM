"""Shared loader/decoder for AB9 sim-bridge JSONL files.

Format per line (sim/logs/ab9-*.jsonl):
  {"t": <epoch>, "dir": "h2b"|"b2h", "len": N, "hex": "<raw frame>"}

Wire frame layout (Moza common): 7E LEN GRP DEV [PAYLOAD x LEN] CHK
For Group 0x20 (FFB), payload[0..1] is a 2-byte sub-command; payload[2..]
is the sub-cmd's own arguments.

Usage:
    from ab9_session import iter_frames, AFrame
"""
from __future__ import annotations

import json
import os
import struct
from dataclasses import dataclass
from pathlib import Path
from typing import Iterator, Optional

# See moza_bridge.BRIDGE_DIR — capture dir is caller-supplied.
SIM_LOG_DIR = Path(os.environ.get("MOZA_TRACE_DIR") or Path.cwd())


@dataclass
class AFrame:
    t: float            # absolute epoch timestamp
    t_rel: float        # seconds relative to trace start
    dir: str            # 'h2b' or 'b2h'
    grp: int            # wire group (response bit included for b2h)
    dev: int            # wire device id (nibble-swapped for b2h)
    payload: bytes      # bytes between DEV and CHK
    raw: bytes          # full frame bytes
    line_no: int        # 1-indexed source line

    @property
    def is_ffb(self) -> bool:
        """True if this is a Group 0x20 (FFB) frame in either direction."""
        return self.grp in (0x20, 0xA0)

    @property
    def is_h2b(self) -> bool:
        return self.dir == "h2b"

    @property
    def is_b2h(self) -> bool:
        return self.dir == "b2h"

    @property
    def sub_cmd(self) -> tuple[int, int]:
        """Return (sub_hi, sub_lo) for Group 0x20 frames, else (-1, -1)."""
        if not self.is_ffb or len(self.payload) < 2:
            return (-1, -1)
        return (self.payload[0], self.payload[1])

    @property
    def sub_args(self) -> bytes:
        """Bytes following the 2-byte sub-cmd in an FFB frame."""
        if not self.is_ffb or len(self.payload) < 2:
            return b""
        return self.payload[2:]

    def hex_payload(self) -> str:
        return self.payload.hex()


def _decode_hex(hex_str: str) -> tuple[int, int, bytes, bytes]:
    """Decode a wire frame hex string. Returns (grp, dev, payload, raw).

    Returns (0, 0, b"", b"") if the frame is malformed (too short / bad start).
    The caller decides whether to drop such frames.
    """
    raw = bytes.fromhex(hex_str)
    if len(raw) < 5 or raw[0] != 0x7E:
        return (0, 0, b"", raw)
    plen = raw[1]
    # 7E LEN GRP DEV [PAYLOAD x LEN] CHK → total = 5 + plen
    if len(raw) != 5 + plen:
        return (0, 0, b"", raw)
    grp = raw[2]
    dev = raw[3]
    payload = raw[4:4 + plen]
    return (grp, dev, payload, raw)


def iter_frames(path: str | Path, max_frames: int = 0) -> Iterator[AFrame]:
    """Stream AFrames from a sim JSONL file.

    ``max_frames`` of 0 means read all. The first frame's timestamp anchors
    ``t_rel`` for the rest of the stream.
    """
    t0: Optional[float] = None
    with open(path) as fh:
        for idx, line in enumerate(fh, start=1):
            if max_frames and idx > max_frames:
                break
            line = line.strip()
            if not line:
                continue
            try:
                obj = json.loads(line)
            except json.JSONDecodeError:
                continue
            t = obj["t"]
            if t0 is None:
                t0 = t
            hex_str = obj["hex"]
            grp, dev, payload, raw = _decode_hex(hex_str)
            yield AFrame(
                t=t,
                t_rel=t - t0,
                dir=obj["dir"],
                grp=grp,
                dev=dev,
                payload=payload,
                raw=raw,
                line_no=idx,
            )


def resolve_log(arg: Optional[str] = None) -> Path:
    """Resolve a sim/logs path. If ``arg`` is None, returns the most-recent
    ``ab9-*.jsonl``. If ``arg`` is a path, returns it. Otherwise, searches
    sim/logs for files matching ``*arg*``.
    """
    if arg is None:
        candidates = sorted(SIM_LOG_DIR.glob("ab9-*.jsonl"),
                            key=lambda p: p.stat().st_mtime, reverse=True)
        if candidates:
            return candidates[0]
        raise FileNotFoundError(f"No ab9-*.jsonl in {SIM_LOG_DIR}")
    p = Path(arg)
    if p.exists():
        return p
    candidates = sorted(SIM_LOG_DIR.glob(f"*{arg}*"),
                        key=lambda x: x.stat().st_mtime, reverse=True)
    if candidates:
        return candidates[0]
    raise FileNotFoundError(f"No sim log matching '{arg}' in {SIM_LOG_DIR}")


# Labels for the default 1..6 effect allocation. The second byte is a
# session-assigned effect index, so a capture whose alloc order differs shifts
# these — see docs/protocol/devices/ab9-shifter.md.
SUB_NAMES = {
    (0x07, -1): "ffb-alloc",
    (0x0E, -1): "ffb-init",
    (0x13, -1): "ffb-commit",
    (0x0A, 0x01): "shift-rumble-mag",
    (0x0A, 0x05): "vib-stream",
    (0x0B, 0x02): "engine-pulse-on",
    (0x0B, 0x03): "engine-pulse-off",
    (0x0D, 0x02): "start-pulse-on",
    (0x0D, 0x03): "start-pulse-off",
    (0x0D, 0x05): "start-vib",
    (0x08, 0x04): "engage-force-mag",
    (0x08, 0x06): "neutral-force-mag",
}


def sub_label(sub_hi: int, sub_lo: int) -> str:
    """Human-readable label for a Group-0x20 (sub_hi, sub_lo) pair.

    Falls back to a `(sub_hi, sub_lo)` lookup with wildcard `-1` for sub_lo
    when only the high byte distinguishes a sub-cmd family (0x07/0x0E/0x13).
    """
    if (sub_hi, sub_lo) in SUB_NAMES:
        return SUB_NAMES[(sub_hi, sub_lo)]
    if (sub_hi, -1) in SUB_NAMES:
        return SUB_NAMES[(sub_hi, -1)]
    return f"sub_{sub_hi:02x}_{sub_lo:02x}"
