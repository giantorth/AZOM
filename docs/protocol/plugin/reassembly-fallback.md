### Reassembly fallback

`SessionDataReassembler.TryDecompress` decompresses incoming session-data
streams using a tiered strategy because each session has its own envelope
layout — see the table in
[`../sessions/compressed-0x09-0x0a.md`](../sessions/compressed-0x09-0x0a.md).

### Strategy

```
TryDecompress()                       (no args; snapshots the internal _buffer)
    ├── 1. inline offset-envelope decode when buf[0] == 0x00
    │       (9-byte envelope: flags(1) + comp_sz+4 LE(4) + uncomp_sz LE(4))
    │       ↳ correct for sessions 0x09 and 0x0a
    │
    └── 2. TryDecompressByMagic (scan for 78 9c / 78 da)
            ↳ catches sessions 0x03, 0x04 (unknown prefix lengths)
            ↳ catches edge cases where embedded 0x7E bytes shifted alignment
```

Session 0x03 tile-server parsing is **not** part of the reassembler — it is a
separate path owned by `TelemetrySender` (`_tileServerParser` →
`TileServerStateParser`), invoked independently.

| Step | What it tries | When it succeeds |
|------|---------------|------------------|
| 1. Offset (inline) | When `buf[0] == 0x00`, read 9 bytes at start: `flags(1) + comp_sz+4 LE(4) + uncomp_sz LE(4)`, decompress remainder | Sessions 0x09/0x0a, well-formed envelopes |
| 2. Magic scan | Walk every byte position; on `78 9c` or `78 da`, trial `zlib.decompress`; accept if produces ≥1 byte | Sessions 0x03 / 0x04 root-dir / any session with unrecognized prefix; mzdash JSON pushes where embedded `0x7E` desyncs the offset reader |

The magic-scan fallback is what kept the plugin parsing session 0x04
directory listings correctly even before sim's envelope was matched to the
real-wheel format (2026-04-22). It mirrors the `_scan` helper in
[`sim/wheel_sim.py`](../../../sim/wheel_sim.py).

### Why the offset path can fail

- **Session uses a different prefix.** Session 0x04 has a 53-byte prefix;
  session 0x03 a 12-byte wrapper. Reading `comp_sz+4` from offset 1
  produces nonsense.
- **Embedded `0x7E` shifts alignment.** When chunked mzdash JSON contains
  literal `0x7E` bytes that the wire layer has escape-doubled, an
  off-by-one in chunk-strip can leave the decoded body misaligned. The
  magic scan rediscovers the zlib stream regardless.

### Plugin source

[`Telemetry/Sessions/SessionDataReassembler.cs`](../../../Telemetry/Sessions/SessionDataReassembler.cs).
The reassembler accepts chunked frames (one per `7c:00` data chunk),
strips per-chunk CRC32-LE trailers, concatenates net payloads, and runs
`TryDecompress` on the assembled buffer.
