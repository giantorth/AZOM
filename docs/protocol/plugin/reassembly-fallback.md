### Reassembly fallback

`SessionDataReassembler.TryDecompress` decompresses incoming session-data
streams. Each session has its own envelope layout — see the table in
[`../sessions/compressed-0x09-0x0a.md`](../sessions/compressed-0x09-0x0a.md) —
so the decoder locates the zlib stream by its magic and only then decides how
to bound and verify it.

### Strategy

```
TryDecompress()                       (no args; works on the internal buffer under its lock)
    scan for 78 9c / 78 da (at most 16 candidates)
    ├── 1. 9-byte envelope directly before the magic
    │       (flags=0x00(1) + comp_sz+4 LE(4) + uncomp_sz LE(4), sizes plausible)
    │       ↳ sessions 0x09 and 0x0a
    │       ↳ wait until all comp_sz−4 zlib bytes are buffered, then inflate
    │         exactly that span; uncomp_sz AND Adler-32 must match
    │
    └── 2. no envelope there
            ↳ inflate from the magic and accept only if the output's Adler-32
              sits in the buffer after the stream start
            ↳ sessions 0x03, 0x04 (unknown prefix lengths)
```

| Step | What it tries | When it succeeds |
|------|---------------|------------------|
| 1. Enveloped | Envelope read from the 9 bytes before the magic; flag must be `0x00` and the sizes within deflate's ratio limits. No inflate until the buffer holds `comp_sz−4` zlib bytes; the exact span must inflate to `uncomp_sz` bytes with a matching Adler-32 trailer | Sessions 0x09/0x0a, complete well-formed messages |
| 2. Trailer search | Inflate from the magic, then look for the output's Adler-32 (BE) in the buffered bytes after it | Sessions 0x03 / 0x04 root-dir / any session with an unrecognized prefix — only once the whole stream, trailer included, has arrived |

`DeflateStream` returns partial output on truncated input, so a bare inflate
cannot tell a fragment from a message; the Adler-32 check is what makes a
result a complete message. An enveloped candidate whose bytes are all present
but that fails both checks is discarded (with the bytes before it) and never
retried.

**One message at a time.** A successful decode returns the first complete
message and removes it plus everything before it. Bytes after it stay
buffered — the wheel packs the next message's head into the same chunk — so
the caller simply calls `TryDecompress` again after the next chunk. Once the
buffer drains empty, seq tracking resets as after `Clear()` (`HighWaterSeq`
back to -1, so the next burst's first chunk is accepted at any seq). Callers
therefore read the ack seq (`HighWaterSeq`) **before** decoding.

Session 0x03 tile-server parsing is **not** part of the reassembler — it is a
separate path owned by `TelemetrySender` (`_tileServerParser` →
`TileServerStateParser`), invoked independently.

`SessionDataReassembler.TryDecompressByMagic` still exists for callers that
hold a whole blob already (`DashboardDownloader`, `DeviceLogParser`): it
accepts the first magic hit that inflates to ≥1 byte, with no completeness
check, and `TryDecompress` does not use it.

### Why the envelope gate can't be the only path

- **Session uses a different prefix.** Session 0x04 has a 53-byte prefix;
  session 0x03 a 12-byte wrapper. The 9 bytes before their magic don't parse
  as a plausible envelope (the 0x04 prefix has big-endian sizes there), so
  they take the trailer search.
- **Bytes that only look like an envelope.** If an envelope-shaped prefix
  holds sizes that don't verify, the same candidate is retried with the
  trailer search before being discarded.

### Plugin source

[`Telemetry/Sessions/SessionDataReassembler.cs`](../../../Telemetry/Sessions/SessionDataReassembler.cs).
The reassembler accepts chunked frames (one per `7c:00` data chunk),
strips per-chunk CRC32-LE trailers, concatenates net payloads, and callers run
`TryDecompress` after each accepted chunk.
