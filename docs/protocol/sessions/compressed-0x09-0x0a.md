### Compressed transfer envelope (sessions `0x09`, `0x0a`)

Sessions `0x09` (configJson state push) and `0x0a` (RPC) carry zlib-compressed
JSON payloads framed by a fixed 9-byte header that is **prepended once** to
the reassembled message body, before the chunked SerialStream layer.

The 9-byte header lives at the start of the **reassembled** application
message — i.e. after `7c:00` chunks are concatenated and per-chunk CRC
trailers stripped. Each chunk does NOT carry its own envelope.

```
flags(1)  comp_sz_plus_4(4 LE)  uncomp_sz(4 LE)  zlib_stream(...)
```

| Offset | Size | Field | Notes |
|--------|------|-------|-------|
| 0 | 1 | flags | `0x00` in every observed message |
| 1 | 4 | compressed length + 4 (LE u32) | Adds `+4` to the actual zlib stream length, presumed to budget the deflate Adler-32 trailer |
| 5 | 4 | uncompressed length (LE u32) | Decompressed payload byte count |
| 9 | N | zlib stream | Standard `78 9c` deflate (zlib magic) |

**Worked example** (RPC reset, `pithouse-switch-list-delete-upload-reupload.pcapng`,
2026-04-21):

```
00            (flags)
1d 00 00 00   (comp_sz+4 = 29 → 25 bytes of zlib data)
11 00 00 00   (uncomp_sz = 17 → JSON body length)
78 9c ...     (zlib stream begins)
```

Decompressed body: `{"completelyRemove()":"{...}","id":13}` (17 chars after
key/value match).

#### Reassembly procedure

1. Walk the incoming `7c:00` data chunks for the session in seq order.
2. Strip the 4-byte CRC32-LE trailer from each chunk's net payload.
3. Concatenate the trimmed payloads.
4. Read the 9-byte envelope from offset 0.
5. Pass `body[9 : 9 + (comp_sz_plus_4 - 4)]` to `zlib.decompress`.
6. Validate `len(decompressed) == uncomp_sz`.

Plugin: [`SessionDataReassembler.TryDecompress`](../../../Telemetry/Sessions/SessionDataReassembler.cs)
applies this layout first; fallback `TryDecompressByMagic` scans for `78 9c`
when the offset-based parser fails on a session that uses a different
envelope (sessions 0x03/0x04). See
[`../plugin/reassembly-fallback.md`](../plugin/reassembly-fallback.md).

#### Direction asymmetry

| Session | Direction | Use |
|---------|-----------|-----|
| `0x09` | device → host | configJson state push (Schema A snapshot at connect; Schema B deltas after FS mutation) |
| `0x09` | host → device | (older firmware) `configJson()` canonical library list; KS Pro fires this on `0x0A` instead |
| `0x0a` | host → device | RPC requests — `completelyRemove()`, reset (`()`), etc. (see [`session-0x0a-rpc.md`](session-0x0a-rpc.md)) |
| `0x0a` | device → host | RPC replies (mirror request key + same `id`) |

#### Distinct envelopes

Other sessions use different prefix layouts and **must not be parsed with
this 9-byte header**:

| Session | Envelope | Reference |
|---------|----------|-----------|
| `0x01` | `0xFF` + `inner_len(4 LE)` + `token(4 LE)` + body + CRC32(4) | [`../dashboard-upload/session-01-mgmt-rpc.md`](../dashboard-upload/session-01-mgmt-rpc.md) |
| `0x03` | 12-byte `FF 01 00 [comp+4 LE] FF 00 [uncomp BE u24]` | [`session-0x03-tile-envelope.md`](session-0x03-tile-envelope.md) |
| `0x04` | 53-byte prefix + zlib (2025-11 dir-listing) | [`../dashboard-upload/session-04-root-dir.md`](../dashboard-upload/session-04-root-dir.md) |
| `0x04`/`0x05`/`0x06`/`0x07` | 6-byte sub-msg headers (file transfer) | [`../dashboard-upload/upload-handshake-2026-04.md`](../dashboard-upload/upload-handshake-2026-04.md) |

One envelope per session; do not share parsers.
