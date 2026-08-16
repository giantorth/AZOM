### Upload protocol handshake sequence

> **2026-04+ firmware (current PitHouse).** Wheels: CSP on R9, KS Pro on R12. Capture: `latestcaps/pithouse-switch-list-delete-upload-reupload.pcapng`. See [`../FIRMWARE.md`](../FIRMWARE.md) for the firmware-era matrix.
>
> **⚠ 2026-07/08 firmware supersedes the cross-session-ack model below** — see
> § "2026-07/08 firmware: same-session acks + FT-ACT acquisition" immediately
> below. The W17 that produced the 2026-05 observations has since updated
> (fw 1.2.6.17 era) and no longer acks on sess=0x04 for uploads on other sessions.

### 2026-07/08 firmware: same-session acks + FT-ACT acquisition

Ground truth: two complete, successful PitHouse uploads against a real W17 (CS Pro)
on an R5 base — `moza-simulator/sim/logs/bridge-20260731-064830.jsonl`
(`/config/start.json`, 164 B payload, sess 0x06) and
`bridge-upload-groundtruth-20260816-071055.jsonl` (dashboard + 103 KB JPEG,
104,748 B payload, 26 rounds, sess 0x04). Everything below is byte-verified.

**Session acquisition (per upload, not per connect):**

1. Host sends the pair `7C 27 0F 80 05 00 03 00 FE 01` (PORT-OPEN 5,3) +
   `7C 23 46 80 06 00 04 00 FE 01` (FT-ACT portA=6, sessB=4). The FT-ACT port
   field is always `sessB + 2` (uploads (6,4); config file (8,6); downloads
   (0x0D,0x0B) / (0x0E,0x0C)).
2. The wheel device-inits session `sessB` ~25 ms later with a **fresh open-seq**
   (`7C 00 04 81 [seq=4] 04 00 FD 02`), even if that session was already
   device-inited at connect. The fresh open rebases the seq space.
3. Host fc:00-acks the device-init.

**Seq alignment (fresh open-seq S):** host data chunks start at **S + 3**;
wheel reply chunks start at **S + 1**. (Same invariant the 2026-06 W17 RE
found; now confirmed on a clean capture: devinit S=4 → metadata chunks 7..13,
ready-ack chunks 5..10.)

**Transfer flow — all sub-msgs on the ONE acquired session, both directions:**

1. type=0x02 metadata (320 B body: 2 B pad, two `0x8C` LOCAL TLVs, `0x10`+md5,
   `bytes_written=0` BE, `total_size` BE, `ff×4`, XOR).
2. Wheel type=0x01 ready-ack ~70 ms later (292 B body, echoes md5 + total,
   `bytes_written=0`).
3. Per round: host sends one type=0x03 content sub-msg (4092-byte deflate
   stride, 12-byte position envelope — unchanged from
   [`per-chunk-trailer.md`](per-chunk-trailer.md)), then **waits for the
   wheel's type=0x01 progress ack** (bytes_written advances by the stride).
   Round ack latency observed 7–40 s while the wheel writes/renders.
4. After the last round: wheel emits type=0x11 complete
   (`bytes_written == total_size`), host sends session CLOSE (seq = next data
   seq), wheel fc-acks and sends its own CLOSE.
5. Post-upload the wheel pushes refreshed configJson state (sess 0x09) and a
   state blob on its 0x03 notification session (fc-acked by the host as
   sess 0x05 — the linked pair the PORT-OPEN(5,3) established).

**Chunk-level flow control:** PitHouse blasts each round's ~82 chunks
unthrottled, immediately re-emits the tail, then go-back-N retransmits from
the first unacked seq every ~1.6 s until the wheel's cumulative fc:00 acks
catch up. The wheel dedups by seq.

**Cumulative acks cut BOTH ways.** The wheel treats the host's fc:00 acks
as cumulative too: acking a post-gap seq tells the wheel everything below
was received and it **permanently drops** the missing chunks from its
retransmit buffer. When wheel→host reply chunks are lost (Wine serial
contention), the host must keep acking the contiguous high-water seq — not
the raw received seq — or the gap becomes unrecoverable and the upload
completion deadlocks (observed 2026-08-16: 18 lost reply chunks, specific-
seq acks, wheel never retransmitted, `SubMsg2AckTimeout`). Plugin:
`WheelUploadCoordinator.GetInboundAckSeq`.

**Post-upload enable.** The uploaded dash lands in `disabledManager` and
does not appear in the wheel's picker until the host re-sends its
`configJson()` library list including the new name — the list is the
wheel's enable authority and slot table; see
[`config-rpc-session-09.md`](config-rpc-session-09.md) § "library list".

**Behavioral rules that differ from 2026-05:**

- **Acks ride the upload session itself.** No sess=0x04 cross-session ack
  channel, no host-side `7C 00 04 81 0E 00…` session-open, no dir-listing
  probe at upload time. The 2026-06 "ack-session port pairing" blocker is
  gone with this firmware.
- **Staging path is bare `/_moza_filetransfer_md5_<hex>`** in the content
  sub-msgs' `0x70` REMOTE TLV (no `/tmp`, no `/home/root`).
- **No 7C:23 frames of any kind during the transfer.** PitHouse's ~1 Hz
  display cadence drops to occasional `7C 27` pairs only. Sending a 7C:23
  dashboard-activate mid-upload re-targets the wheel's FT state machine and
  degrades the acks to the degenerate `total=0` form (observed with the
  plugin 2026-08-16).
- **Image bundle entries keep their original extension** — the content-addressed
  store accepts non-PNG rasters verbatim (`/home/moza/resource/images/MD5/<md5>.jpg`
  observed).
- If the staging file for the declared md5 already exists wheel-side, the
  wheel can emit type=0x01 ready + type=0x11 complete **immediately after the
  metadata**, before any content is sent (observed for a repeated
  `start.json` upload).

**Session-role convention:** odd sessions (0x05/0x07/0x09…) are the display /
page sessions (the per-page display-config triple FT-activates them); even
sessions carry file transfers (uploads 0x04, config files 0x06, downloads
0x0B/0x0C). An upload targeting an odd display session gets no device-init —
the wheel won't re-init a session the display owns.

Small-file flow (2025-11 firmware, `pithouse-switch-list-delete-upload-reupload.pcapng`, ~1.9KB mzdash):

1. Session 0x04 already open (from device_init). Host sends type=0x08 dir-listing probe with `/home/root`.
2. Wheel replies type=0x0a with populated directory listing.
3. Host sends `7c 23 46 80 08 00 06 00 fe 01` (session-open request, port=6).
4. Wheel emits device-initiated session-open for session 0x06 (`7c 00 06 81 06 00 06 00 fd 02`).
5. Host sends type=0x02 metadata on session 0x06 (316B for small file, 320B seen for 500KB).
6. Wheel replies type=0x01 ready-ack on session 0x06 (290B, echoes both path TLVs + md5 + size, bytes_written=0).
7. Host sends type=0x03 content on session 0x06 (one sub-msg, ~2192B for 1902B mzdash, contains zlib stream).
8. Wheel replies type=0x11 complete-ack (290B, bytes_written == total_size).
9. Host sends session_end `7c 00 06 00 ...`. Wheel sends session_end.

**Large-file flow (2026-04 firmware, observed 2026-04-24, ~500KB dashboard):** PitHouse splits the upload into many type=0x03 sub-msgs. Sim must emit a per-round progress ack or PitHouse stalls.

1–6. Same as small-file flow.
7. Host sends FIRST type=0x03 content sub-msg (size_field=4384, full path TLVs + 8B `compressed_header` + zlib data starting with `78 9c` magic).
8. Wheel emits type=0x01 progress ack with `bytes_written = decompressed_bytes_so_far`.
9. Host sends NEXT type=0x03 sub-msg (same paths echoed + raw deflate continuation, no `78 9c` magic at the same fixed offset within the msg).
10. Steps 8–9 repeat per round.
11. Once full zlib stream is reassembled and reaches deflate EOF, wheel emits type=0x11 complete-ack with `bytes_written = total_size`.
12. Host + wheel exchange session_end.

**Session number is dynamic.** Earlier docs hardcoded session `0x05` / `0x06`; in fresh 2026-04 PitHouse runs we have observed `0x07` carrying the upload (the `7c:23` trigger from the host picked port 7). Sim now treats any session in `0x04..0x0a` as a candidate file-transfer session and gates by buffer content (presence of a type=0x02 sub-msg).

**Device-side reply seq is independent from the host's seq counter on the same session.** Real wheel starts its file-transfer reply seq at `port + 1` (e.g. port 6 → first wheel→host data chunk at seq `0x07`). Sim previously reused the host's `_upload_next_seq` counter, which on a port-6 upload started replies at the host's last seq + 1 (≈ `0x11`); PitHouse silently dropped those out-of-window chunks. Fixed via `_ft_reply_next_seq[session]` initialised to `port + 1`.

**Wheel-side ack session ≠ host upload session (verified 2026-05-14).** When the host uploads on `sess=0x05` (or any session in the 0x05..0x09 dynamic range), the wheel acks on **`sess=0x04`** — both fc:00 chunk acks AND the type=0x01 progress + type=0x11 complete sub-msgs land on b2h sess=0x04. Verified across two consecutive uploads (ETS2-ATS, Simple Rally Mini Dash) in `sim/logs/bridge-20260514-170002.jsonl`: b2h sess=0x05 was 0–25 frames total across both uploads while b2h sess=0x04 carried 5+1 type=0x01/0x11 sub-msgs per upload at the expected per-round cadence (one type=0x01 per host type=0x03 sub-msg, then one type=0x11 once deflate EOF reached).

This is the **same "linked session pairs" pattern** [`../sessions/chunk-format.md`](../sessions/chunk-format.md) calls out for the 0x03↔0x0A pair. For upload, the pair is `host_upload_session ↔ wheel_session_0x04`.

The ack sub-msg body echoes the REMOTE staging path as a `0x70` TLV (UTF-16LE `/_moza_filetransfer_md5_<hex>`) — even though the host's outbound metadata never carried a REMOTE TLV (current PitHouse uses two LOCAL `0x8C` TLVs only). The wheel derives the staging path from the host-declared MD5 and echoes it back as part of its progress + complete acks.

**Plugin implementation gap.** `WheelUploadCoordinator.NoteInboundChunk` filters by `session == ActiveSession`. For uploads on host session 0x05, `ActiveSession = 0x05` so b2h sess=0x04 acks are dropped on the floor — the coordinator's `_subMsg1Response` / `_subMsg2Response` wait events never fire from real wheel replies, and the wire-format-fallback path (Legacy ↔ New) can misfire when the new format actually worked. Fix: feed b2h sess=0x04 sub-msgs into the coordinator alongside the upload session's traffic, walk them with the 6-byte sub-msg parser, and fire the ack events on the observed type=0x01 / type=0x11 boundaries.

**Per-round progress ack.** For each new type=0x03 round detected in the buffer, sim emits another type=0x01 with the latest `bytes_written` value. Without this, PitHouse halts the upload after the first round (the protocol behaves like a per-round flow-control credit). `_ft_rounds_acked[session]` tracks how many rounds have been acked so duplicate keepalive timers don't re-fire on the same round.

**Stuck-state recovery.** Sim reload (`mcp__wheel-sim-windows__sim_reload` then `sim_start`) appears to Windows as a USB disconnect → reconnect, forcing PitHouse to drop its cached "upload in progress" state and re-handshake cleanly. Required after any PitHouse retry-loop wedges (UI stuck on "resources syncing" with no wire activity).

### 1-byte XOR status after `ff*4` sentinel (not a 4-byte trailer)

**Resolved 2026-04-24.** The "4-byte trailer" chased in earlier revisions of this section was a misread. Only **1 byte** follows the `ff ff ff ff` sentinel. That byte is an **8-bit XOR checksum** over the body bytes — specifically, XOR of every byte from the first TLV marker through the final `ff` of the sentinel, producing a single byte appended as the message terminator. The 3 bytes that visually "looked like part of the trailer" in capture hex dumps were actually 3 of the 4 bytes of the chunk's 4-byte CRC32 — the last CRC byte and the frame checksum were getting silently dropped by a buggy capture-extract helper, making a 4+1 = 5-byte tail look like a 4-byte trailer.

Verified across every `type=0x01/0x02/0x03/0x08/0x0a/0x11` message with clean chunk CRCs in:

- `latestcaps/pithouse-switch-list-delete-upload-reupload.pcapng` (both files, both directions)
- `09-04-26/dash-upload.pcapng` (legacy session 0x04 path)
- `12-04-26-2/moza-startup-1.pcapng` (handshake/telemetry)

For every message, `status == xor_over_body_bytes`. Example (file2 `type=0x01` ready-ack, 2025-11 capture): body XOR = `0x2e`, last byte on wire = `0x2e`.

**Message layout** (confirmed, replaces earlier speculation):

```
[type:1] [size_LE:u16] [pad:3]             — 6-byte header (size is u16 LE, not u32)
[pad:2 = 00 00]                            — body begins here
[LOCAL path TLV  #1]                       — 0x8A/0x8C 0x00 + UTF-16LE + 00 00  (firmware-dependent)
[LOCAL path TLV  #2 OR REMOTE path TLV]    — see firmware notes below
[flag:1 = 0x10]
[md5:16]
[bytes_written:u32 BE]
[total_size:u32 BE]
[ff ff ff ff]                              — sentinel
[status:1]                                 — XOR(every body byte above)
```

`size_LE` counts every byte after the first 6 (i.e. `msg_len = size + 6`). The two `00 00` bytes that follow the 6-byte header look like they could be extra header pad, but they are part of the body and contribute to the XOR (as zeros they are no-ops).

**Second TLV firmware variance:**

| Firmware | Second TLV | Content |
|----------|------------|---------|
| 2025-11 (PCAP captures) | `0x70 0x00` REMOTE | Wheel-side staging path `/home/root/_moza_filetransfer_md5_<md5hex>` (`UTF-16LE NUL-term`) |
| 2026-04+ (PCAP captures, retained for legacy parity) | `0x70 0x00` REMOTE | Same shape as 2025-11 |
| 2026-05+ (current PitHouse, bridge capture `sim/logs/bridge-20260514-170002.jsonl`) | `0x8C 0x00` LOCAL | **Identical duplicate** of TLV #1 (same Windows source path, no REMOTE path at all in metadata) |

On 2026-05+ PitHouse the wheel-side staging path
(`/tmp/_moza_filetransfer_md5_<md5>`) appears only **inside the
type=0x03 content body** (the deflate stream's pre-zlib path TLV
block), not in the type=0x02 metadata. The metadata now carries only
the source Windows path, repeated twice. Reason for the duplication
is unknown — likely a fixed-slot artifact retained from when the
second slot was REMOTE.

**XOR status verified on 2026-05-14 bridge capture
(`sim/logs/bridge-20260514-170002.jsonl`).** Upload #2's type=0x02
metadata body[319] = `0x4B`; `XOR(body[0..318]) = 0x4B`. Confirmed
bit-exact across all 2144 session-data chunks of upload #2 (every
per-chunk CRC32-LE matched as well).

**Sim impact.** The pre-2026-04-24 sim emitted a 4-byte `ff ff ff ff` trailer — 3 bytes longer than the real wheel — and set `size_LE = body_len` assuming an 8-byte header. Both errors compounded: PitHouse parsed size field, walked N bytes into the body, and its internal state machine's `next_message_offset` pointer landed 3 bytes past the real message end, at which point the sentinel scan / status XOR check failed and PitHouse sat on "resources syncing" waiting for a message it would never recognise. `build_file_transfer_response` in `sim/wheel_sim.py` now emits a 1-byte XOR status byte and sets `size = body_len + 2`.

### Session data chunk CRC — 4 bytes LE

**Verified 2026-04-24 (again).** Each session `7c:00` data chunk carries a **4-byte CRC32-LE** trailer over the net body. A previous revision of this section briefly claimed 3 bytes; that claim was an artifact of a buggy `extract_frames` helper that dropped the last 2 bytes of each frame (real CRC's last byte + frame checksum). When raw tshark output is inspected directly every chunk's last 4 bytes match `zlib.crc32(net)` LE exactly.

Full chunk wire layout: 6-byte `7c:00:sess:01:seq_lo:seq_hi` + 54-byte net data + 4-byte CRC32-LE = 64-byte payload = 69-byte frame (with `7e/N/group/device/cksum` framing, `N = 0x40`). The final chunk of a message is shorter; it still carries a 4-byte CRC over its (smaller) net data.

Sim chunking (`chunk_session_payload`, `_chunk_catalog_message`) and all chunk-CRC-aware ingestion paths (`UploadTracker.feed`, `PitHouseUploadReassembler.add`) use 4-byte CRC. `chunk_session_payload` exposes a `crc_bytes` knob for future firmware variants but defaults to 4.

### Multi-round upload content (type=0x03) — zlib reassembly

> **NOTE 2026-04-24**: this section described an 8-byte sub-msg header. That interpretation worked on session 0x07 captures by accident (chunk-stride misalignment landed on valid LZ77 boundaries) but failed on larger uploads / session 0x09. The real header is 6 bytes; continuations have a per-chunk variable header before the deflate continuation. See [`6-byte-submsg-header.md`](6-byte-submsg-header.md) and [`per-chunk-trailer.md`](per-chunk-trailer.md) (continuation chunks) for the corrected layout. The legacy 8B-header parser is kept as a fallback in `_parse_upload` for older firmware, but new firmware should hit the 6B path.

Large dashboards (≥ ~10KB compressed) are split across many type=0x03 sub-msgs. Original (legacy 8B-header) interpretation:

```
[03] [size_LE:u32] [00 00 00]                  — 8B sub-msg header (LEGACY — actually 6B; trailing 2 zeros are body, see below)
[LOCAL TLV]                                    — 0x8c 0x00 + UTF-16LE Windows temp path + 00 00
[REMOTE TLV]                                   — 0x70 0x00 + UTF-16LE /_moza_filetransfer_md5_<hex> + 00 00
[0x10] [md5:16]
[reserved:4]
[token:4]
[compressed_header:8]                          — uncomp_sz BE + comp_sz LE (mixed endian)
[zlib_or_raw_deflate_chunk]                    — `78 9c` magic only on FIRST sub-msg; subsequent sub-msgs carry raw deflate continuation at the same byte offset
```

Observed `size_LE = 0x1120 = 4384` for every type=0x03 sub-msg in a 506KB upload on the user's 2026-04 PitHouse. Continuation deflate data starts at **body[291]** in every chunk (immediately after the 12-byte position envelope at body[279:291] — see [`per-chunk-trailer.md`](per-chunk-trailer.md)). For chunk 0 specifically, body[291:611] carries the uncompressed bundle preamble (file table) and `78 9c` zlib magic lands at body[611] for upload #2 (varies with file count / path lengths).

**Reassembly algorithm — legacy fallback** (`_parse_upload` in `sim/wheel_sim.py`):

1. Anchor on the LAST type=0x02 metadata marker in the session buffer (PitHouse may retry → stale type=0x02 / type=0x03 blocks earlier in the buffer must be skipped).
2. From the anchor, enumerate all following type=0x03 sub-msgs where `size_LE` is in the plausible 1000–10000 range.
3. In the first sub-msg, find the `78 9c` zlib magic → derive `zoff_in_msg`.
4. For each sub-msg (first and continuations), slice `buf[off + zoff_in_msg : off + 8 + size_LE]` and concatenate. This strips the (mistakenly-interpreted) 8B sub-msg header + path TLVs + md5 + tokens + compressed_header from every continuation.
5. Feed the concatenated deflate stream through `zlib.decompressobj()`. If `d.eof`, the upload is complete; else it was truncated but still yields partial bytes which sim writes to its virtual FS (better to store partial mzdash than nothing).

For new firmware always prefer the 6B-header path (`_parse_upload_6b`); see below.

**`_scan_file_transfer_paths` anchoring.** The metadata-field extractor (md5, total_size, local path) also anchors on the LAST type=0x02 boundary for the same reason — otherwise on retries the sim ends up building reply bodies that concatenate paths from the stale attempt with paths from the fresh attempt, inflating body length and shifting the size field.
