#!/usr/bin/env python3
"""Extract embedded Qt resources from a PE/ELF binary or a standalone .rcc.

Qt compiles :/... resources into the binary as a qt_resource_data blob whose entries
are either raw or zlib-compressed. Rather than parsing the resource tree (whose layout
differs across Qt versions and needs the section base address), this brute-force scans
for zlib streams and keeps the ones that inflate to something useful. That is enough to
recover the JSON/QML schemas MOZA ships inside Dashboard Studio.

  usage: qt-rcc-extract.py <binary> <out-dir> [--min-size 64] [--ext json,qml,txt]

Each recovered blob is written as <out-dir>/<offset>.<detected-ext>.
"""
import argparse
import json
import os
import re
import sys
import zlib

ZLIB_HEADERS = (b'\x78\x01', b'\x78\x5e', b'\x78\x9c', b'\x78\xda')


def sniff_extension(data):
    """Guess a file extension from the decompressed bytes."""
    head = data.lstrip()[:400]
    if head[:1] in (b'{', b'['):
        try:
            json.loads(data.decode('utf-8'))
            return 'json'
        except (UnicodeDecodeError, ValueError):
            return 'maybe-json'
    if head.startswith(b'\x89PNG'):
        return 'png'
    if re.search(rb'^\s*import\s+QtQuick', head, re.M):
        return 'qml'
    try:
        text = data.decode('utf-8')
    except UnicodeDecodeError:
        return None
    printable = sum(c.isprintable() or c in '\r\n\t' for c in text)
    return 'txt' if printable / max(1, len(text)) > 0.95 else None


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('binary')
    parser.add_argument('out_dir')
    parser.add_argument('--min-size', type=int, default=64)
    parser.add_argument('--ext', default='json,qml,txt,maybe-json',
                        help='comma-separated extensions to keep')
    args = parser.parse_args()

    keep = set(args.ext.split(','))
    with open(args.binary, 'rb') as fh:
        blob = fh.read()
    print(f'{len(blob)} bytes from {args.binary}')

    os.makedirs(args.out_dir, exist_ok=True)

    offsets = []
    for header in ZLIB_HEADERS:
        start = 0
        while True:
            at = blob.find(header, start)
            if at < 0:
                break
            offsets.append(at)
            start = at + 1
    offsets.sort()
    print(f'{len(offsets)} candidate zlib headers')

    written = 0
    seen = set()
    for at in offsets:
        obj = zlib.decompressobj()
        try:
            data = obj.decompress(blob[at:], 64 << 20)
        except zlib.error:
            continue
        if len(data) < args.min_size:
            continue

        digest = hash(data)
        if digest in seen:
            continue

        ext = sniff_extension(data)
        if ext is None or ext not in keep:
            continue

        seen.add(digest)
        path = os.path.join(args.out_dir, f'{at:08x}.{ext}')
        with open(path, 'wb') as out:
            out.write(data)
        written += 1
        print(f'  0x{at:08x}  {len(data):9d}  {ext}')

    print(f'{written} resources written to {args.out_dir}')
    return 0 if written else 1


if __name__ == '__main__':
    sys.exit(main())
