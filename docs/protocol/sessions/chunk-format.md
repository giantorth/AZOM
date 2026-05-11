### Chunk format

Each `7c:00` data field contains one chunk:

```
session(1)  type(1)  seq_lo(1)  seq_hi(1)  payload(≤58)
```

| Field | Size | Description |
|-------|------|-------------|
| session | 1 | Session ID — pre-assigned, multiple concurrent |
| type | 1 | `0x01` = data, `0x00` = control/end marker, `0x81` = session channel open (device-initiated) |
| seq | 2 LE | Sequence number (monotonic within session) |
| payload | ≤58 | Net data per chunk; **all data chunks have 4-byte CRC-32 trailer** |

Net payload per full data chunk: **54 bytes** (58 minus 4-byte CRC). All data chunks include CRC-32 trailer, including final chunk.

### CRC algorithm

**Standard CRC-32** (ISO 3309 / ITU-T V.42, same as zlib/Ethernet/gzip/PNG):
- Polynomial: `0x04C11DB7` (reflected), init `0xFFFFFFFF`, xor-out `0xFFFFFFFF`
- Stored **little-endian** in 4-byte trailer
- Covers only **54-byte payload data** (excludes session/type/seq header)
- Per-chunk (not cumulative)
- Computable via `zlib.crc32(payload_bytes)` or `System.IO.Hashing.Crc32`

### Acknowledgments

`fc:00` with 3 bytes: `session(1) + ack_seq(2 LE)`. Session ID in ack identifies **ack sender's** session, not data sender's. Linked session pairs (e.g. 0x03↔0x0A) use cross-session acks.

**Session-open ACK must echo host's open_seq.** When host sends type=0x81 session open with `seq_lo:seq_hi`, wheel's `fc:00` ack must carry same seq value. Pithouse maintains monotonic port counter incrementing on each disconnect/reconnect; if wheel always replies with `ack_seq=0`, Pithouse treats as stale and retries endlessly (observed: 552 retries over 2.5 minutes). Counter starts at 1 on first power-on but increments across sessions.

**Inbound data chunks must be acked with the specific received seq, not a running max.** Verified 2026-05-09: when our handler tracked the highest seen seq and acked that running max, the wheel — which evidently keys its retransmit-suppression on per-seq acks — kept re-pushing earlier seqs every ~1 s indefinitely (e.g. `seq=5..14` retransmitted on a 20 s cadence after we'd already advanced our running max to 21). Each chunk should `SendSessionAck(session, seq)` with the literal seq just received. See `2026-05-09-acks-dedup-and-catalog-persistence.md`.

**Wheel retransmits must be deduped by seq before being fed to a parser.** The wheel re-pushes any unacked chunk on a ~1 s cadence; chunks routinely arrive 2-3× before our ack lands. Parsers that buffer-and-walk (`ChannelCatalogParser`, `TileServerStateParser`, the inbound side of any size-prefixed TLV stream) must track per-session highest seen seq and drop retransmits, otherwise duplicated bytes misalign the size-prefix walk and mid-stream records parse as garbage.

### Session data chunk CRC — 4 bytes LE

**Verified 2026-04-24, re-verified 2026-05-10 against 524 wire-trace files (227,497 / 227,713 chunks matched 4-byte; 0 / 227,713 matched 3-byte).** Each session `7c:00` data chunk carries a **4-byte CRC32-LE** trailer over the net body.

Full chunk wire layout: 6-byte `7c:00:sess:01:seq_lo:seq_hi` + 54-byte net data + 4-byte CRC32-LE = 64-byte payload = 69-byte frame (with `7e/N/group/device/cksum` framing, `N = 0x40`). The final chunk of a message is shorter; it still carries a 4-byte CRC over its (smaller) net data.

Reference computation (read-only validation against any capture):

```python
import zlib
# raw = bytes after framing strip: [resp_group, resp_dev, 7c, 00, sess, type, seq_lo, seq_hi, chunkPayload]
chunk = raw[8:]                                   # chunkPayload only
calc  = zlib.crc32(chunk[:-4])                    # CRC over net data
wire  = int.from_bytes(chunk[-4:], 'little')      # last 4 bytes LE
assert calc == wire
```

Sim chunking (`chunk_session_payload`, `_chunk_catalog_message`) and all chunk-CRC-aware ingestion paths (`UploadTracker.feed`, `PitHouseUploadReassembler.add`) use 4-byte CRC. `chunk_session_payload` exposes a `crc_bytes` knob for future firmware variants but defaults to 4.

#### Tautology trap when "verifying" alternative CRC widths

Two distinct false-positives have produced "the CRC is N bytes wide" claims that fail in production. Both were the result of a verification script computing the canonical 4-byte CRC under a different label:

| Bogus form | Why it appears to "match" 100% |
|---|---|
| `crc32(cp[:-3]) & 0xFFFFFF  ==  cp[-3:].le` while reading b2h JSONL bytes that did **not** include a wire checksum trailer | Treating the JSONL hex as if it has a trailing checksum byte (it doesn't — see [`../../tools/moza_trace.py:68-70`](../../tools/moza_trace.py) — b2h is `[resp_group, resp_dev, payload]` with no checksum) shifts `chunk[:-3]` onto the canonical `chunk[:-4]`. The compare against `chunk[-3:]` is just the bottom 3 bytes of the same 4-byte CRC LE. Looks like "3-byte CRC verified". |
| `crc32(cp[:-4]) & 0xFFFFFF  ==  cp[-4:-1].le` | Truncates both sides of the canonical test to 24 bits. Always matches when the 4-byte test matches. |

A "3-byte CRC" verification only proves the protocol is 3 bytes wide if it can also be shown to **fail** when the chunk is truncated by one byte at the front (or one byte is added at the back). The production code in `Telemetry/TelemetrySender.cs` does NOT make these off-by-one mistakes when reading from a real receive buffer — it reads `chunkPayload` at the correct offset, so a "3-byte CRC" branch in production rejects every real chunk (`crcRejects` counter grows until it's caught and reverted).

If you genuinely suspect a firmware variant uses a different CRC width, the only convincing test is: capture from that variant, decode framing exactly as the read loop does (post-stuff, post-checksum-strip), and confirm that **4-byte CRC fails** on >99% of chunks. The 216 / 227,713 "4-byte mismatch" rate observed in the historical trace archive is the floor — corrupt/truncated frames produce that level of noise even on a healthy 4-byte link.
