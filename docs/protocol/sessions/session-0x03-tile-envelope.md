### Session `0x03` tile-server envelope (12-byte variant)

Session `0x03` carries tile-server map metadata blobs (rally / nav-style
mini-maps consumed by older firmware displays). It uses a different
12-byte wrapper than the 9-byte envelope on sessions `0x09`/`0x0a`.

> **2025-11 firmware path.** On 2026-04+ KS Pro firmware, session `0x03` is
> reserved-but-unused (open frames + zero keepalives only). Tile-server
> traffic was relocated to session `0x04` (host → dev) and session `0x0B`
> (dev → host). See [`lifecycle.md` § Concurrent session map](lifecycle.md).

### Wire envelope

Reassembled message body (after stripping per-chunk CRC32-LE trailers):

```
FF 01 00 [comp_sz+4 u32 LE] FF 00 [uncomp_sz u24 BE] [zlib stream]
```

| Offset | Size | Field | Notes |
|--------|------|-------|-------|
| 0 | 1 | `0xFF` | Sentinel (same byte used for FF-prefix sub-msgs on sessions 0x01/0x04) |
| 1 | 1 | `0x01` | Sub-msg index (constant) |
| 2 | 1 | `0x00` | Tag (constant) |
| 3 | 4 | comp_sz + 4 (LE u32) | Compressed length, plus 4 — same `+4` convention as sessions `0x09`/`0x0a` |
| 7 | 1 | `0xFF` | Separator (constant) |
| 8 | 1 | `0x00` | Tag (constant) |
| 9 | 3 | uncomp_sz (BE u24) | **Big-endian, 3 bytes** — note the unusual mixed endianness vs `comp_sz` |
| 12 | N | zlib stream | `78 9c` standard deflate |

### Worked examples

**Small blob** (247-byte zlib, 775-byte JSON):

```
FF 01 00   FB 00 00 00      FF 00   00 03 07     78 9c …
           (comp+4 = 251)            (uncomp = 775, BE)
```

**Large blob** (1165-byte zlib, 6301-byte JSON):

```
FF 01 00   91 04 00 00      FF 00   00 18 9D     78 9c …
           (comp+4 = 1169)           (uncomp = 6301, BE)
```

### Decompressed payload

JSON object with map state, e.g.:

```json
{"map":{"ats":"...","ets2":"..."},"root":"...","version":N}
```

Plugin builder:
[`Telemetry/TileServerStateBuilder.BuildEnvelope()`](../../../Telemetry/TileServer/TileServerStateBuilder.cs).
Plugin parser:
[`Telemetry/TileServer/TileServerStateParser.cs`](../../../Telemetry/TileServer/TileServerStateParser.cs).

### Why this envelope is distinct

The `+4` budget on `comp_sz` matches sessions `0x09`/`0x0a`, but the BE u24
uncompressed-size field and the `FF 01 00 / FF 00` tag layout are unique to
session `0x03`. The plugin's `SessionDataReassembler` first tries the 9-byte
sessions-09/0a layout, falls back to magic-scan for `78 9c`, and ultimately
relies on `TileServerStateParser` to recognise the `FF 01 00` lead bytes —
see [`../plugin/reassembly-fallback.md`](../plugin/reassembly-fallback.md).
