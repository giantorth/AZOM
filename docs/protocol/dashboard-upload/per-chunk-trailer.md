### Per-chunk metadata trailer (continuation chunks)

> **2026-04+ firmware.** Continuation-chunk format. Re-decoded
> 2026-05-15 against `sim/logs/bridge-20260514-170002.jsonl` upload #2
> (Simple Rally Mini Dash, 4 type=0x03 sub-msgs, clean — no retries).
> See [`../FIRMWARE.md`](../FIRMWARE.md) for the firmware-era matrix.
> Decompression independently verified byte-exact 2026-05-15 — see
> §"Decode-side verification" below.

Each type=0x03 sub-msg body has a shared TLV envelope in its first 279
bytes, then a 12-byte per-chunk position envelope, then the deflate
payload starting at body[291].

## Layout

| Body offset | Bytes | Meaning |
|-------------|-------|---------|
| 0 | 1 | always `00` (body pad byte 0) |
| 1 | 1 | always `00` (body pad byte 1) |
| 2–145 | 144 | LOCAL TLV `0x8C` — Windows source path UTF-16LE (`C:/Users/<user>/AppData/Local/Temp/_moza_filetransfer_tmp_<unix_ms>`, NUL-terminated) |
| 146–261 | 116 | REMOTE TLV `0x70` — wheel staging path UTF-16LE (`/_moza_filetransfer_md5_<md5hex>`, NUL-terminated) |
| 262 | 1 | flag `0x10` |
| 263–278 | 16 | MD5 of the bundle's combined content |
| **279–290** | 12 | **per-chunk position envelope** (differs across chunks; see below) |
| 291+ | varies | chunk 0: bundle metadata header (uncompressed file table) + zlib magic `78 9c` + start of deflate stream. chunks 1+: raw deflate continuation. |

Bytes 0–278 are the **shared envelope** — byte-identical across every
type=0x03 sub-msg of one upload attempt. Empirically, bytes 279–280
(high u16 of `chunk_offset` u32 BE) are also `00 00` for any
`total_compressed < 65536`, so the longest-common-prefix probe lands
at byte 281 for typical small uploads. For uploads >64 KB compressed,
divergence would start at byte 279.

## Per-chunk position envelope (body[279:291], 12 bytes)

Decoded structure (three u32 BE fields, contiguous):

```
body[279:283]  chunk_offset             (u32 BE)
body[283:287]  total_compressed_size    (u32 BE)
body[287:291]  this_chunk_deflate_size  (u32 BE)
```

Verified for upload #2's 4 chunks (`total_compressed = 0x3BA5 = 15269`,
`chunkStride = 0x0FFC = 4092`):

| Chunk | body[279:283] chunk_offset BE | body[283:287] total_size BE | body[287:291] this_chunk_size BE |
|------:|:------------------------------|:----------------------------|:---------------------------------|
| 0 | `00 00 00 00` (= 0)            | `00 00 3b a5` (= 15269)       | `00 00 0f fc` (= 4092)             |
| 1 | `00 00 0f fc` (= 4092)         | `00 00 3b a5`                 | `00 00 0f fc`                      |
| 2 | `00 00 1f f8` (= 8184)         | `00 00 3b a5`                 | `00 00 0f fc`                      |
| 3 | `00 00 2f f4` (= 12276)        | `00 00 3b a5`                 | `00 00 0b b1` (= 2993, last chunk) |

- `chunk_offset = chunk_index × chunkStride` (4092 here, varies per
  upload — set by the host).
- `total_compressed_size` is byte-identical across all chunks and
  equals the sum of `this_chunk_deflate_size` across all chunks
  (`4092 × 3 + 2993 = 15269` for upload #2).
- `this_chunk_deflate_size` is `chunkStride` for every chunk except
  the last, where it carries the residual count.
- Trailing bytes beyond `body[291 + this_chunk_deflate_size]` exist in
  the wire frame but are not part of the deflate stream. For upload #2,
  every body is padded to `body_len = 4384 = 291 + 4092 + 1` for
  chunks 0–2 (1 trailing byte), and chunk 3 has
  `body_len = 3285 = 291 + 2993 + 1`. **The 1 trailing byte is NOT a pad
  — it is the 8-bit XOR status over `body[:-1]`**, the same formula as
  the type=0x02 metadata trailer. Verified 27/27 against the 2026-08-16
  PitHouse ground-truth chunks; the wheel's `FileTransferClient`
  VALIDATES it and rejects the sub-msg with
  `Invalid data: Checksum mismatch-- 0 <xor> <len>` (display app log)
  when it is wrong — while still fc-acking the chunks, so the failure is
  silent at the session layer. Drop it when reassembling deflate.

## Chunk-0-only fields (body[291:body_end] for chunk 0)

After the 12-byte position envelope, chunk 0 carries the **bundle
metadata header** (uncompressed file table) followed by the zlib
stream. See [`sess05-bundle-contents.md`](sess05-bundle-contents.md)
§"Per-file metadata layout" for the full byte-exact field map. Summary
of upload #2 (file_count=2, dest_paths 158 B + 134 B):

```
body[291:295]: file_count          (u32 BE = 2)
body[295:295+N1]: file[0] metadata (byte_len + dest_path UTF-16BE + uncompressed_size)
body[X1:X1+N2]: file[1] metadata
body[X2:X2+4]: total_compressed_size (u32 BE)   ← matches body[283:287]
body[X2+4:X2+8]: total_uncompressed_size (u32 LE)
body[X2+8:]: zlib magic `78 9c` + start of zlib stream
```

For upload #2 the zlib magic landed at body[611], i.e. 320 bytes of
uncompressed bundle preamble + 2-byte zlib header. **Compute the
magic offset dynamically by walking the per-file table; do not
hardcode.**

## Continuation-chunk deflate offset

Chunks 1+ raw deflate continuation **starts at body[291]** (immediately
after the per-chunk position envelope). No per-file header in
continuations — the wheel splices `body[291 : 291 + this_chunk_deflate_size]`
from each continuation chunk directly into the running deflate stream.

## Decode-side verification (2026-05-15)

Reassembled the deflate stream from upload #2 using:

- `c0 = body0[291+320+2 : 291+4092]` (skip 320-byte bundle preamble + 2-byte zlib header, keep 3770 bytes of raw deflate)
- `c1 = body1[291 : 291+4092]` (4092 bytes raw deflate)
- `c2 = body2[291 : 291+4092]` (4092 bytes raw deflate)
- `c3 = body3[291 : 291+2993]` (2993 bytes raw deflate, includes trailing adler32)
- Total raw deflate input: 14947 bytes

Decompressed with `zlib.decompressobj(wbits=-15)` produced **341,089
bytes**:
- bytes 0..340,341 = decoded mzdash content (CRLF-normalized)
- bytes 340,342..341,088 = PNG (747 bytes, **byte-exact** vs on-disk
  `Resource/MD5/bd529011a002c03dc77b2f63b193b789.png`)

Diff vs on-disk `Simple Rally Mini Dash.mzdash` (CRLF-normalized,
340,348 bytes) shows only two field-level differences, both expected
version drift from PitHouse saving the dashboard between the capture
and the on-disk read:
- `"lastModified": 1778803603` (capture) vs `1774468256` (on-disk
  read taken later — PitHouse hadn't yet re-saved at capture time)
- `"window.GUID": "4Db1Pwrc03SHDnpUtw4hxPipDRDrlPvN"` (capture, 32 chars)
  vs `"{94a9ade1-d841-4579-a115-5f6ffef97005}"` (on-disk, 38 chars)
  — accounts for the exact 6-byte length difference

Byte-aligned decompression at `body[291]` is byte-exact for both
decode and upload-build. No bit-level shift is required.

## Plugin implementation impact

`Telemetry/Dashboard/FileTransferBuilder.cs:BuildFileContentChunked`
currently returns a single sub-msg. To match the protocol it needs to:

1. Build the full bundle body (`BuildFileContentBodyType02` extended for
   multi-file — see [`sess05-bundle-contents.md`](sess05-bundle-contents.md)).
2. Build the compressed payload = `[uncompressed file table] + [zlib-stream]`
   where the zlib stream is the concatenated file contents
   (CRLF-normalized mzdash + raw PNG bytes in dest_path order).
3. Split the compressed payload at chunkStride (4092) boundaries.
4. For each chunk, prepend the shared 279-byte envelope + 12-byte
   position envelope `[chunk_offset:u32 BE][total_compressed_size:u32 BE][this_chunk_deflate_size:u32 BE]`,
   yielding `body = shared_prefix(279) + position_envelope(12) + deflate_slice(stride or residual)`.
5. For the LAST chunk, set `this_chunk_deflate_size` to the actual
   remaining-bytes count (not chunkStride).
6. Append the XOR status byte (XOR of every preceding body byte) after
   the deflate slice so
   `body_len = 291 + this_chunk_deflate_size + 1` (matches capture).
7. Each chunk becomes a separate type=0x03 sub-msg via the standard
   6-byte sub-msg header (`[0x03][size_LE u16][3 pad bytes]`).
