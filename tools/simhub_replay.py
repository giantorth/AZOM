#!/usr/bin/env python3
"""Reader for SimHub session recordings (<stem>.telemetry.json + .jsonidx + .json.metadata).

On-disk format (SimHub 9.12, verified on iRacing recordings 2026-10):
    .telemetry.json      "FF01" header, then one record per frame:
                         [01][u32 LE length][raw deflate (zlib wbits=-15)] -> JSON object
    .telemetry.jsonidx   5-byte header, then 25-byte records:
                         [01][u32 LE file offset][12 bytes zero][f64 LE seconds since StartDate]
    .json.metadata       {"StartDate": ISO-8601 with offset, "EndDate", "CarModel", "TrackName",
                          "GameAndReader", ...}

A frame's shape is game-specific. iRacing frames are
    {"SessionDataInfoUpdate": int, "SessionDataRaw": "<YAML>", "Telemetry": "<JSON>"}
where Telemetry is a JSON *string* holding ~340 irsdk channels (Pitch, Roll, Yaw,
*Rate, *Accel, *shockDefl, RPM, Gear, Throttle, Brake, Speed, ...). Assetto Corsa
frames carry Graphics/Physics/... dicts directly. `iter_frames` unwraps nested JSON
strings so callers see dicts either way.

Usage from other tools:
    from simhub_replay import read_metadata, iter_frames, numeric_channels
    meta = read_metadata(stem)
    for fr in iter_frames(stem):
        fr.t_rel, fr.t_utc, fr.data            # seconds since start, epoch seconds, dict
        chans = numeric_channels(fr.data)      # {"Telemetry.Yaw": -1.0084, ...}

Timestamps: pcapng captures carry UTC epoch timestamps, so `t_utc` lines a frame up
with wire traffic directly (within the recorder's own latency).
"""
from __future__ import annotations

import json
import re
import struct
import zlib
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, Iterator, List, Optional, Tuple

INDEX_HEADER = 5
INDEX_RECORD = 25
RECORD_HEADER = 5   # [01][u32 len]


@dataclass
class ReplayMeta:
    start_utc: float      # epoch seconds
    end_utc: float
    car: str
    track: str
    game: str
    raw: Dict[str, Any]


@dataclass
class ReplayFrame:
    index: int
    t_rel: float          # seconds since StartDate (from the index)
    t_utc: float          # epoch seconds
    data: Dict[str, Any]


def _parse_iso(s: str) -> float:
    # .NET writes 7 fractional digits; datetime.fromisoformat wants <= 6.
    s = re.sub(r"(\.\d{6})\d+", r"\1", s)
    return datetime.fromisoformat(s).astimezone(timezone.utc).timestamp()


def read_metadata(stem: str) -> ReplayMeta:
    raw = json.loads(Path(stem + ".telemetry.json.metadata").read_text(encoding="utf-8"))
    return ReplayMeta(
        start_utc=_parse_iso(raw["StartDate"]),
        end_utc=_parse_iso(raw["EndDate"]),
        car=raw.get("CarModel") or "",
        track=raw.get("TrackName") or "",
        game=raw.get("GameAndReader") or "",
        raw=raw,
    )


def read_index(stem: str) -> List[Tuple[int, float]]:
    """[(file_offset, t_rel_seconds)] for every flagged index record."""
    ir = Path(stem + ".telemetry.jsonidx").read_bytes()
    out: List[Tuple[int, float]] = []
    p = INDEX_HEADER
    while p + INDEX_RECORD <= len(ir):
        if ir[p] == 1:
            off = struct.unpack_from("<I", ir, p + 1)[0]
            t_rel = struct.unpack_from("<d", ir, p + 17)[0]
            out.append((off, t_rel))
        p += INDEX_RECORD
    return out


def _unwrap(obj: Any) -> Any:
    """Parse string-valued members that are themselves JSON documents."""
    if isinstance(obj, dict):
        for k, v in list(obj.items()):
            if isinstance(v, str) and v[:1] in "{[":
                try:
                    obj[k] = json.loads(v)
                except ValueError:
                    pass
    return obj


def iter_frames(stem: str, unwrap: bool = True) -> Iterator[ReplayFrame]:
    meta = read_metadata(stem)
    idx = read_index(stem)
    with open(stem + ".telemetry.json", "rb") as fh:
        for i, (off, t_rel) in enumerate(idx):
            fh.seek(off)
            head = fh.read(RECORD_HEADER)
            if len(head) < RECORD_HEADER or head[0] != 1:
                continue
            length = struct.unpack_from("<I", head, 1)[0]
            blob = fh.read(length)
            try:
                data = json.loads(zlib.decompress(blob, -15))
            except (zlib.error, ValueError):
                continue
            if unwrap:
                data = _unwrap(data)
            yield ReplayFrame(i, t_rel, meta.start_utc + t_rel, data)


def numeric_channels(data: Dict[str, Any], prefix: str = "") -> Dict[str, float]:
    """Flatten numeric/bool leaves to {"Section.Key": float}; skips lists and $type tags."""
    out: Dict[str, float] = {}
    for k, v in data.items():
        if k == "$type":
            continue
        name = f"{prefix}{k}"
        if isinstance(v, bool):
            out[name] = 1.0 if v else 0.0
        elif isinstance(v, (int, float)):
            out[name] = float(v)
        elif isinstance(v, dict):
            out.update(numeric_channels(v, name + "."))
    return out


def channel_value(data: Dict[str, Any], path: str) -> Optional[Any]:
    cur: Any = data
    for part in path.split("."):
        if not isinstance(cur, dict) or part not in cur:
            return None
        cur = cur[part]
    return cur
