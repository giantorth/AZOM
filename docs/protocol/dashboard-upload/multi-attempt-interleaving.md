### Multi-attempt upload interleaving in the buffer

PitHouse retransmits cause chunks from different upload attempts to coexist
in the session buffer. Each attempt has its own chunk0 (chunk_offset=0).
Continuation chunks from one attempt do **not** cleanly continue another
attempt's deflate stream — they belong to a different zlib instance and
must not be glued across attempt boundaries.

### Why this happens

`type=0x03` sub-msgs carry the file content. PitHouse will resend earlier
sub-msgs (with the same TLV path block, same MD5, same flags) when the
device's `bytes_written` ack lags or arrives out of order. The retransmits
are byte-identical at the prefix (offset 0..278) — only the per-chunk
position envelope at body[279:291] distinguishes them. Consecutive type=0x03
chunks in the buffer are therefore *not* guaranteed to belong to the same
attempt or to extend the same deflate stream.

### Sim parsing strategy

`_parse_upload_6b` in [`sim/wheel_sim.py`](https://github.com/giantorth/moza-simulator)
implements a greedy walk that filters across attempt boundaries:

| Step | Operation | Purpose |
|------|-----------|---------|
| 1 | Walk buffer with the 6B-header validator (`type ∈ {01, 02, 03, 11}`, `pad == 00 00 00`, stride matches `6 + size_LE`) | Skip frames that aren't valid sub-msgs (e.g. interleaved unrelated chunks) |
| 2 | Find chunk0 = first `type=0x03` where `body[279:283] == 0` AND `78 9c` magic present in `body[291:]` | Lock onto a deflate-stream start |
| 3 | Initialize `zlib.decompressobj()` with `body0[zoff:]` (zoff = location of `78 9c` magic) | Begin streaming decode |
| 4 | For each remaining `type=0x03`: confirm `body[279:283]` matches the expected next `chunk_offset` (= `chunk_offset_so_far + chunkStride`) and `body[283:287]` matches the locked `total_compressed_size`; feed `body[291 : 291 + this_chunk_deflate_size]` into the decompressor | Reject continuation candidates that belong to a different attempt |
| 5 | Repeat step 4 until `chunk_offset + this_chunk_deflate_size == total_compressed_size` OR `decompressobj.eof` | Bounded by the envelope's declared total |

### Per-chunk header layout

Continuation chunks carry the same shared TLV envelope as chunk0 in the
first 279 bytes, then the 12-byte per-chunk position envelope, then the
deflate slice:

| Offset | Size | Meaning |
|--------|------|---------|
| 0–278 | 279 | Shared TLV envelope (LOCAL `0x8C` path + REMOTE `0x70` path + flag `0x10` + MD5) — identical in every chunk of one attempt |
| 279–290 | 12 | Per-chunk position envelope (u32 BE × 3): `chunk_offset`, `total_compressed_size`, `this_chunk_deflate_size`. See [`per-chunk-trailer.md`](per-chunk-trailer.md). |
| 291 | varies | chunk0: 320-byte uncompressed bundle preamble (file table), then `78 9c` zlib magic at body[~611] (varies with file count / path lengths), then deflate stream. chunks 1+: raw deflate continuation starts at body[291] directly. |

See [`per-chunk-trailer.md`](per-chunk-trailer.md) for the full
continuation-chunk byte map.

### Why envelope-driven matching beats "longest extension"

An earlier implementation matched continuations greedily by trying every
offset in `[280, 1500)` and picking the one that produced the longest
clean decompression extension. With the position envelope now decoded
(`body[279:283]` is the chunk_offset, and continuation deflate always
starts at body[291]), the wheel doesn't have to guess — it can directly
verify that an incoming chunk's `chunk_offset` equals the next expected
offset for the locked attempt, and reject mismatches. Greedy
longest-extension still works as a fallback for fuzzed / corrupt
traffic, but the envelope-driven check is O(1) and unambiguous.

### Verified outcomes

- 62 KB session 0x09 buffer with 14 `type=0x03` chunks (mostly
  retransmits) → 82 KB decoded mzdash JSON.
- Decoded JSON root keys: `name='JDM Gauge Style 02'`, `version='1.1.1'`,
  `type='Window.qml'` — confirms reassembly preserved structure.
- 2026-05-15: 4-chunk Simple Rally Mini Dash upload (15269 bytes
  compressed → 341089 bytes uncompressed bundle) decoded byte-exact;
  PNG content matched on-disk file byte-for-byte, mzdash matched
  except for two PitHouse-regenerated fields (`lastModified`,
  `window.GUID`). See [`per-chunk-trailer.md`](per-chunk-trailer.md)
  §"Decode-side verification".

### Edge case: partial uploads

When PitHouse aborts mid-flight (window-close, profile switch), the
envelope-driven walk stops at the last chunk whose `chunk_offset`
matches the expected continuation. The resulting partial mzdash is
still written to the virtual FS — better to keep partial structure
than nothing.
