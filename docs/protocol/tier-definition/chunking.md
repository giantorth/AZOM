### Chunking (both versions, both directions)

Tier-definition messages travel as standard SerialStream `7c:00` data
chunks. Both v0 (URL-subscription) and v2 (compact) layouts share the same
chunking rules in both directions.

### Chunk wire layout

Each chunk is one frame:

```
7E [N] 43 17 7C 00 [session] 01 [seq_lo] [seq_hi] [net_payload] [crc32 LE] [checksum]
```

| Offset | Size | Field | Notes |
|--------|------|-------|-------|
| 0 | 1 | `0x7E` | Frame start |
| 1 | 1 | `[N]` | Payload length (max `0x40` = 64) |
| 2 | 1 | `0x43` | TelemetrySendGroup |
| 3 | 1 | `0x17` | Device wheel |
| 4–5 | 2 | `7C 00` | SerialStream chunk header |
| 6 | 1 | `[session]` | Session byte (`0x01` mgmt, `0x02` telemetry) |
| 7 | 1 | `0x01` | Chunk type = data |
| 8–9 | 2 | seq (LE) | Monotonic per-session sequence |
| 10..N–4 | ≤54 | net payload | Slice of the application-layer message |
| N–3..N–1 | 4 | CRC32-LE | Standard ISO 3309 / zlib CRC over net payload only |
| –1 | 1 | frame checksum | Wire-level checksum over whole frame (see [`../wire/checksum.md`](../wire/checksum.md)) |

### Sizing constants

| Constant | Value | Source |
|----------|-------|--------|
| Max net payload per chunk | **54 bytes** (58 with CRC) | [`Telemetry/Frames/TierDefinitionBuilder.cs:185`](../../../Telemetry/Frames/TierDefinitionBuilder.cs) `MaxNetPerChunk` |
| Total chunk payload | up to 58 bytes | net + 4-byte CRC |
| Chunk frame size on wire | up to 64 bytes (`N = 0x40`) | net + CRC + 6-byte chunk header |
| Wire frame size | up to 69 bytes | adds `7E`, `[N]`, group, device, frame checksum |

### CRC32 over net payload

**ALL chunks have a 4-byte CRC-32 trailer**, including the final (short)
chunk of a message. Verified by computing CRC-32 of every chunk's net data
across `moza-startup-1.pcapng`, `moza-startup-2.pcapng`, and KS Pro
captures.

- Polynomial `0xEDB88320` (reflected `0x04C11DB7`)
- Init `0xFFFFFFFF`, xor-out `0xFFFFFFFF`
- Stored little-endian
- Covers **net payload only** (no session/type/seq header bytes)
- Per-chunk, not cumulative — each chunk is CRCd independently

Computable via `zlib.crc32()` (Python) or `System.IO.Hashing.Crc32`. Plugin
implementation: [`TierDefinitionBuilder.Crc32`](../../../Telemetry/Frames/TierDefinitionBuilder.cs).

### Sequence numbers

`seq` is a per-session 16-bit LE counter, incremented once per data chunk
sent in that direction. The peer ACKs cumulative seq via `fc:00`:

```
7E 05 43 17 FC 00 [session] [ack_seq_lo] [ack_seq_hi] [checksum]
```

`ack_seq` is the highest contiguous seq received plus 1 (next-expected
seq), Stop-and-Wait style.

### Reassembly

Receiver must:

1. Buffer chunks per (session, seq).
2. Strip per-chunk CRC32-LE trailer (last 4 bytes of net payload).
3. Concatenate trimmed payloads in seq order.
4. Hand the assembled stream to the application layer (TLV parser, zlib
   decompressor, etc.).

A previous revision of this section briefly claimed 3-byte CRC trailers;
that was an artifact of a buggy `extract_frames` helper that dropped the
last 2 bytes of each frame (real CRC's last byte + frame checksum). When
raw tshark output is inspected directly, every chunk's last 4 bytes match
`zlib.crc32(net)` LE exactly. See
[`../sessions/chunk-format.md`](../sessions/chunk-format.md) for the
re-verification trail.
