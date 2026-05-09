"""Shared loader and decoder for MOZA wire-trace JSONL files.

Usage from other tools:
    from moza_trace import load_trace, decode_frame
"""
import json
import struct
from dataclasses import dataclass, field
from pathlib import Path
from typing import Optional

LOGS_DIR = Path.home() / ".local/share/Steam/steamapps/compatdata/2825720939/pfx/drive_c/Program Files (x86)/SimHub/Logs"


@dataclass
class Frame:
    t: float          # seconds relative to trace start
    dir: str          # 'h2b' or 'b2h'
    raw: bytes
    group: int = 0
    dev: int = 0
    # Session-layer fields (populated for group 0x43/0xC3)
    session: int = -1
    stype: int = -1   # 0x81=open, 0x00=close, 0x01=data
    seq: int = -1
    port: int = -1
    # FF record fields (populated for data chunks containing FF records)
    ff_kind: int = -1
    ff_size: int = -1
    # Value frame fields (populated for 7D:23 frames)
    vf_flag: int = -1
    vf_data: bytes = field(default_factory=bytes)
    # FC ack fields
    fc_cmd: int = -1
    fc_session: int = -1
    fc_seq: int = -1

    @property
    def is_session(self) -> bool:
        return self.group in (0x43, 0xC3)

    @property
    def is_value_frame(self) -> bool:
        return self.vf_flag >= 0

    @property
    def is_ff_record(self) -> bool:
        return self.ff_kind >= 0

    @property
    def is_open(self) -> bool:
        return self.stype == 0x81

    @property
    def is_close(self) -> bool:
        return self.stype == 0x00

    @property
    def is_data(self) -> bool:
        return self.stype == 0x01


def decode_frame(t: float, direction: str, raw: bytes) -> Frame:
    f = Frame(t=t, dir=direction, raw=raw)
    if len(raw) < 5:
        return f

    # h2b: [0x7E][N][group][dev][cmd...][chk]
    # b2h: [resp_group][resp_dev][payload...]  (resp_group = group | 0x80)
    if direction == 'b2h':
        f.group = raw[0]
        f.dev = raw[1]
        # Session-layer b2h: resp_group=0xC3, resp_dev=0x71
        if raw[0] == 0xC3 and len(raw) >= 7:
            if raw[2] == 0xFC and raw[3] == 0x00 and len(raw) >= 7:
                f.fc_cmd = raw[3]
                f.fc_session = raw[4]
                f.fc_seq = raw[5] | (raw[6] << 8)
            elif raw[2] == 0x7C and raw[3] == 0x00 and len(raw) >= 8:
                f.session = raw[4]
                f.stype = raw[5]
                if f.stype == 0x81 and len(raw) > 7:
                    f.port = raw[6] | (raw[7] << 8)
                elif f.stype == 0x01 and len(raw) > 7:
                    f.seq = raw[6] | (raw[7] << 8)
                    payload = raw[8:]
                    if len(payload) > 13 and payload[0] == 0xFF:
                        f.ff_size = struct.unpack_from('<I', payload, 1)[0]
                        f.ff_kind = struct.unpack_from('<I', payload, 9)[0]
                elif f.stype == 0x00 and len(raw) > 7:
                    f.seq = raw[6] | (raw[7] << 8)
        return f

    # h2b path
    f.group = raw[2]
    f.dev = raw[3]

    if f.group == 0x43 and len(raw) >= 8:
        if raw[4] == 0x7C and raw[5] == 0x00:
            f.session = raw[6]
            f.stype = raw[7]
            if f.stype == 0x81 and len(raw) > 9:
                f.port = raw[8] | (raw[9] << 8)
            elif f.stype == 0x01 and len(raw) > 9:
                f.seq = raw[8] | (raw[9] << 8)
                payload = raw[10:-5] if len(raw) > 15 else raw[10:]
                if len(payload) > 13 and payload[0] == 0xFF:
                    f.ff_size = struct.unpack_from('<I', payload, 1)[0]
                    f.ff_kind = struct.unpack_from('<I', payload, 9)[0]
            elif f.stype == 0x00 and len(raw) > 9:
                f.seq = raw[8] | (raw[9] << 8)

        elif raw[4] == 0x7D and raw[5] == 0x23 and len(raw) >= 12:
            f.vf_flag = raw[10]
            f.vf_data = raw[12:-1] if len(raw) > 13 else raw[12:]

        elif raw[4] == 0xFC and raw[5] == 0x00 and len(raw) >= 9:
            f.fc_cmd = raw[5]
            f.fc_session = raw[6]
            f.fc_seq = raw[7] | (raw[8] << 8)

    return f


def load_trace(path: str | Path, max_lines: int = 0) -> list[Frame]:
    frames: list[Frame] = []
    t0: Optional[float] = None
    with open(path) as fh:
        for i, line in enumerate(fh):
            if max_lines > 0 and i >= max_lines:
                break
            obj = json.loads(line.strip())
            t = obj["t"]
            if t0 is None:
                t0 = t
            raw = bytes.fromhex(obj["hex"])
            frames.append(decode_frame(t - t0, obj["dir"], raw))
    return frames


def latest_trace() -> Path:
    traces = sorted(LOGS_DIR.glob("moza-wire-*.jsonl"), key=lambda p: p.stat().st_mtime, reverse=True)
    for p in traces:
        if p.stat().st_size > 0:
            return p
    raise FileNotFoundError(f"No non-empty trace files in {LOGS_DIR}")


def resolve_trace(arg: Optional[str]) -> Path:
    if arg is None or arg == "latest":
        return latest_trace()
    p = Path(arg)
    if p.exists():
        return p
    candidates = sorted(LOGS_DIR.glob(f"*{arg}*"), key=lambda x: x.stat().st_mtime, reverse=True)
    if candidates:
        return candidates[0]
    raise FileNotFoundError(f"No trace matching '{arg}'")
