#!/usr/bin/env python3
"""Shared MOZA wire-frame helpers for capture analysis.

The frame writer byte-stuffs every 0x7E from body index 2 on (`7E` -> `7E 7E`,
see MozaProtocol.StuffFrame), so a naive `7E`-scan shifts every byte after a
0x7E payload byte and can resync mid-frame. That shows up in per-byte field
analysis as phantom extra values — it is how the FSR1 type-06 "gap" slot was
once read as capture-confirmed. Use :func:`scan_moza_frames_checked` for
anything that inspects payload bytes.
"""
from __future__ import annotations

from typing import Iterator

CHECKSUM_MAGIC = 0x0D          # MozaProtocol.MagicValue
FRAME_START = 0x7E


def wire_checksum(frame_no_csum: bytes) -> int:
    """Checksum of an UNSTUFFED frame minus its trailing checksum byte.

    Mirrors MozaProtocol.CalculateWireChecksum: magic + sum(bytes), with every
    0x7E from index 2 on counted twice (stuffing doubles it on the wire).
    """
    total = CHECKSUM_MAGIC + sum(frame_no_csum)
    for i in range(2, len(frame_no_csum)):
        if frame_no_csum[i] == FRAME_START:
            total += FRAME_START
    return total & 0xFF


def scan_moza_frames_checked(buf: bytes) -> Iterator[bytes]:
    """Yield UNSTUFFED, checksum-verified frames `7E N GRP DEV <payload> CSUM`."""
    i, n = 0, len(buf)
    while i < n:
        if buf[i] != FRAME_START:
            i += 1
            continue
        if i + 2 > n:
            break
        total = buf[i + 1] + 5          # 7E, len, grp, dev, payload..., csum
        out = bytearray(buf[i : i + 2])
        j = i + 2
        while len(out) < total and j < n:
            b = buf[j]
            out.append(b)
            j += 1
            if b == FRAME_START and j < n and buf[j] == FRAME_START:
                j += 1                  # stuffed pair -> one payload byte
        if len(out) < total:
            i += 1
            continue
        if wire_checksum(bytes(out[:-1])) == out[-1]:
            yield bytes(out)
            i = j
        else:
            i += 1
