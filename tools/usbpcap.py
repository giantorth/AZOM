#!/usr/bin/env python3
"""Streaming reader for USBPcap pcapng captures.

Never loads the whole file: the race captures of the motion platform are 3-16 GB.
Yields raw pcapng blocks (so a filtered copy can be written verbatim) and parsed
USB packets with the USBPcap device address, which the older helper in
usb-capture/extract_moza_frames.py dropped.

Usage from other tools:
    from usbpcap import iter_usb_packets, UsbPacket, TRANSFER_BULK
    for pkt in iter_usb_packets(path):
        if pkt.transfer == TRANSFER_BULK and pkt.device == 5:
            ...
"""
from __future__ import annotations

import struct
from dataclasses import dataclass
from typing import Iterator, Optional, Tuple

BLOCK_SHB = 0x0A0D0D0A
BLOCK_IDB = 0x00000001
BLOCK_EPB = 0x00000006

TRANSFER_ISOCH = 0x00
TRANSFER_INTERRUPT = 0x01
TRANSFER_CONTROL = 0x02
TRANSFER_BULK = 0x03

MOZA_VID = 0x346E


@dataclass
class UsbPacket:
    t: float            # epoch seconds (pcapng timestamp)
    bus: int
    device: int         # USBPcap device address
    endpoint: int       # bit 7 set = IN (device -> host)
    transfer: int       # TRANSFER_* constant
    payload: bytes      # data after the USBPcap header, clipped to data_length

    @property
    def is_in(self) -> bool:
        return bool(self.endpoint & 0x80)


def iter_blocks(path: str) -> Iterator[Tuple[int, bytes]]:
    """Yield (block_type, full_block_bytes) without reading the whole file."""
    with open(path, "rb", buffering=8 * 1024 * 1024) as fh:
        while True:
            hdr = fh.read(8)
            if len(hdr) < 8:
                return
            btype, blen = struct.unpack("<II", hdr)
            if blen < 12:
                return
            rest = fh.read(blen - 8)
            if len(rest) < blen - 8:
                return
            yield btype, hdr + rest


def idb_tsresol(block: bytes) -> int:
    """if_tsresol option of an IDB block (default 6 = microseconds)."""
    body = block[8:-4]
    off = 8  # u16 linktype, u16 reserved, u32 snaplen
    while off + 4 <= len(body):
        code, length = struct.unpack_from("<HH", body, off)
        off += 4
        if code == 0:
            break
        if code == 9 and length >= 1:
            return body[off]
        off += length + ((4 - (length % 4)) % 4)
    return 6


def ticks_per_second(tsresol: int) -> int:
    if tsresol & 0x80:
        return 1 << (tsresol & 0x7F)
    return 10 ** tsresol


def epb_fields(block: bytes) -> Tuple[int, bytes]:
    """(timestamp_ticks, packet_bytes) of an EPB block."""
    body = block[8:-4]
    # iface u32 @0, ts_high @4, ts_low @8, cap_len @12, pkt_len @16, data @20
    ts_high, ts_low = struct.unpack_from("<II", body, 4)
    cap_len = struct.unpack_from("<I", body, 12)[0]
    return (ts_high << 32) | ts_low, body[20 : 20 + cap_len]


def parse_usbpcap(pkt: bytes, t: float) -> Optional[UsbPacket]:
    """Parse the USBPcap pseudo-header. Returns None for malformed packets.

    Header (little-endian): u16 header_length, u64 irp_id, u32 usbd_status,
    u16 urb_function, u8 irp_info, u16 bus, u16 device, u8 endpoint,
    u8 transfer, u32 data_length, [transfer-specific bytes...].
    """
    if len(pkt) < 27:
        return None
    hdr_len = struct.unpack_from("<H", pkt, 0)[0]
    if hdr_len < 27 or hdr_len > len(pkt):
        return None
    bus = struct.unpack_from("<H", pkt, 17)[0]
    device = struct.unpack_from("<H", pkt, 19)[0]
    endpoint = pkt[21]
    transfer = pkt[22]
    data_length = struct.unpack_from("<I", pkt, 23)[0]
    payload = pkt[hdr_len:]
    if len(payload) > data_length:
        payload = payload[:data_length]
    return UsbPacket(t, bus, device, endpoint, transfer, payload)


def iter_usb_packets(path: str) -> Iterator[UsbPacket]:
    tps = 10 ** 6
    for btype, block in iter_blocks(path):
        if btype == BLOCK_IDB:
            tps = ticks_per_second(idb_tsresol(block))
            continue
        if btype != BLOCK_EPB:
            continue
        ticks, pkt = epb_fields(block)
        parsed = parse_usbpcap(pkt, ticks / tps)
        if parsed is not None:
            yield parsed


def device_descriptor_ids(pkt: UsbPacket) -> Optional[Tuple[int, int]]:
    """(idVendor, idProduct) when the packet carries a USB device descriptor.

    The GET_DESCRIPTOR(DEVICE) data stage is a control IN transfer whose payload
    starts with bLength=0x12, bDescriptorType=0x01; idVendor/idProduct are the
    little-endian u16 at offsets 8 and 10.
    """
    if pkt.transfer != TRANSFER_CONTROL or not pkt.is_in:
        return None
    p = pkt.payload
    if len(p) < 18 or p[0] != 0x12 or p[1] != 0x01:
        return None
    return struct.unpack_from("<HH", p, 8)
