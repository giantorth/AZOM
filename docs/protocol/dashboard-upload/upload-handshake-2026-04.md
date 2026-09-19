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

### Host emit rate: leave the wheel room to answer

The link is half-duplex 115200 (11520 B/s). `WriteBudget.TargetBytesPerWindow`
calls 11000 B/s "100 %". Session-data frames on the upload path measure
**67 wire bytes**, so the emit loop's per-frame delay sets the outbound rate
directly:

| `InterFrameDelayMs` | outbound | % of 11000 B/s budget | + ~750 B/s inbound = % of wire |
|---|---|---|---|
| 6 | 11167 B/s | 101.5 % | 103 % |
| 8 | 8375 B/s | 76 % | 79 % |

6 ms oversubscribes the wire, and its in-code justification ("~64 wire bytes per
chunk → 10.7 kB/s, ~85 % of budget") was arithmetic that never held. Measured
over the 30 s upload window in bug bundle **8RDM91JG**: `43 17 7c 00` traffic
alone ran 10720 B/s (96.5 % of all outbound), total outbound 11111 B/s = 101 %
of budget, combined with inbound 11861 B/s = **103 % of the wire**.

A saturated half-duplex link crowds out the wheel's own b2h chunks, and the
losses show up as reassembler forward gaps and wheel window re-bursts. Both
observed stalls sat pinned at 97-105 % budget for the whole transfer:
8RDM91JG hung at 20 % (`bw=49104/244879`), **C4KX4GKK** — a different rig,
native Windows, KS Pro, no LED traffic at all — timed out at 95.7 %
(`last bw=233244 total=243630`) after 681 write-budget warnings.

Useful throughput at 6 ms was ~2 kB/s of acked payload against 10.7 kB/s
pushed: a 5:1 waste ratio that is retransmit churn. Pacing slower moves more.

### A backwards seq on the upload session is a re-burst, not a new burst

On a forward gap the wheel **re-bursts its whole unacked window**, not just the
missing chunk. So during an upload a b2h seq *below* the reassembler's
high-water mark is routine. `SessionDataReassembler.Insert` classifies any
`seq < _lastSeq` as a new burst: it clears the buffer and resets the seq.

For the zlib dir-listing that buffer was designed for, that is correct. For the
6-byte ack sub-msg stream it is **fatal**, because the clear lands
mid-sub-msg:

* the buffer then begins partway into a sub-msg body;
* `WalkAckSubMsgs`' three-pad-byte (`00 00 00`) check fails at offset 0;
* the walk offset was reset to 0 as well, so it fails identically on every
  later chunk — the ack stream is **permanently desynchronised**.

Ground truth, 8RDM91JG:

```
07:48:04.397  Upload ack type=0x01 bw=49104 total=244879   ← last ack ever parsed
07:48:04.825  Reassembler seq restart (sess=0x04 upload): got seq=81,
              last was 82; clearing 3874B buffer (assuming new burst)
07:49:09.949  Reassembler forward gap: got seq=184, expected 182
```

The wheel never stopped talking. Counting `7c 00 04 01` inbound chunks in the
capture: 74 before the restart (23 s), then **103 (67 s) + 189 (127 s) = 292
further reply chunks, ~17.8 KB — from which not one ack sub-msg was parsed**.
`_ackProgress` never set again, so the completion wait simply spun to timeout.
Because a mis-aligned offset can also land on a zero run inside a body, the
walker may instead parse *garbage* zero-size sub-msgs rather than stall — worse,
not better.

Fix: before feeding a chunk to either ack reassembler during an in-flight
upload, drop it when `seq <= HighWaterSeq` (`WheelUploadCoordinator.IsUploadRetransmit`).
The bytes are already buffered, so the walker keeps its alignment and the caller
still acks — `GetInboundAckSeq` re-affirms the high-water mark, which is what
tells the wheel to move past its re-burst. Bound the check to a window
(1024 chunks, vs the wheel's ~765-chunk receive window) so a genuine wheel-side
session restart or a u16 seq wrap still reaches the restart path instead of
being dropped forever. Only while `IsUploadInFlight`: outside an upload this
same buffer carries dir-listing blobs whose bursts legitimately restart low.

A forward gap is unaffected and still recovers through `GapDetected` +
cumulative ack — which is exactly why C4KX4GKK, whose stream only ever hit
forward gaps, reached 95.7 % while 8RDM91JG died at 20 %.

### One upload attempt at a time

Every per-attempt field on `WheelUploadCoordinator` is single-instance:
`_acquireTarget`, `_acquireOpenSeq`, `_outboundSeq`, the four wait events, and
`_isUploadInFlight`. Nothing serialised `RunBackgroundUpload`, and two callers
queue it straight onto the thread pool — `TriggerManualUpload` (a second click
on the Files tab) and `QueueBackgroundUploadIfReady` (a reconnect landing
mid-transfer). Overlapping runs therefore clobber each other's session
acquisition, share one outbound seq counter, and each one's `finally` clears
`_isUploadInFlight` out from under the other.

Signature in bundle **C4KX4GKK** — three attempts against one live transfer,
the two newcomers dying `NoFtSession` ~15 s in (5 FT-ACT attempts x 3 s) while
the live one kept acking:

```
11:58:11.281  Uploading dashboard ... token=0x403F9B85   ← live, acking to bw=167772
12:07:04.968  Uploading dashboard ... token=0x400FEC4E
12:07:19.970  Dashboard upload: NoFtSession                ← 15 s, never acquired
12:07:27.796  Uploading dashboard ... token=0x400E9FF6
12:07:42.797  Dashboard upload: NoFtSession
```

`RunBackgroundUpload` now takes an `Interlocked` claim and rejects a second
concurrent call, returning **without** firing `UploadCompleted` — that event is
what hands the LEDs back, and a spurious one would end the stand-down
mid-transfer.

### Group-0 live RPM frames PERSIST; they do not lapse on their own

The unified keepalive in `MozaLedDeviceManager` is built on "the firmware
renders live LEDs only WHILE their bitmask is fed; stop feeding and the section
reverts to its stored/idle render", with a 0.75 s interval because ownership
drops 1000 ms after the last feed. That is **verified for the knob rings**
(group 3) — the observation it was written from was a ring reverting to stored
colours ~0.7x/s when the feed landed late.

It does **not** hold for the RPM strip (group 0). Ground truth, bundle
**NS9G817J** (W17 / CS Pro):

```
08:26:14.111  Session 0x04 sub-msg 2 complete-ack (bytes_written=244879 total=244879)
08:26:14.632  Upload progress bar released      ← host stops feeding group 0
08:27:40      capture ends — the amber fill is STILL LIT on the rim
```

Across those 85 s the capture carries **zero** `3f 17 19 00` (colour chunk) and
zero `3f 17 1a 00` (active+window bitmask) frames — the only `3f 17 19 01`
traffic is group 1, the button LEDs. Nothing repainted the strip and nothing
needed to: the wheel simply held the last frame it was given.

Consequence for any code that drives group 0: **going quiet does not turn the
LEDs off.** The live pipeline already accounts for this without saying so — its
`rpmChanged` path emits an explicit all-black colour frame plus `active=0` over
the full window on a lit -> off transition, then goes quiet. Anything else that
paints the strip (`UploadProgressLedBar`) has to blank it the same way when it
stands down, and one all-black frame is the proven form. It must stay one-shot:
re-sending all-black on a cadence pins the wheel in live-render mode and blocks
its firmware sleep light.

The upside is that group 0 needs no keepalive at all, so the progress meter runs
at 1 Hz with every frame carrying a change, rather than paying a sub-1 s
re-feed on a link an upload is already saturating.

### `bytes_written` is not a progress counter

The ack sub-msg trailer's `bytes_written:u32 BE` looks like a byte counter and
sometimes behaves like one, but it is **not** monotone from zero and must not be
used to drive a progress display. Two uploads of a byte-identical payload
(md5 `75f037d4…`, `total_size` 244879) on the same W17, 38 minutes apart:

| bundle | ready-ack (sub-msg 1) | status | subsequent acks |
|---|---|---|---|
| 8RDM91JG | `bw=0 total=244879` | `0x1D` | type=0x01, genuine 4092-byte steps |
| NS9G817J | `bw=244879 total=244879` | `0x2C` | type=0x11, `bw=total` for the whole transfer |

NS9G817J's ready-ack landed **0.2 s** after the upload started, before a single
type=0x03 content sub-msg had been emitted, already reporting the full size.
It was a fresh single attempt — no earlier attempt in the session, and the
preceding one (8RDM91JG, a different session) had stalled at `bw=49104`, so the
wheel did not hold a complete staging file either. The status byte differs
between the two shapes (`0x1D` vs `0x2C`), which is the only correlate observed
so far; what selects the shape is not established.

Consequence: anything that shows transfer progress must use the **host emit
fraction** — type=0x03 content sub-msgs handed to the wire out of the total the
payload was chunked into (`WheelUploadCoordinator.UploadProgress`). That is
always monotone from 0, always meaningful, and stalls exactly when the emit loop
stalls in its backlog drain. Both the Files tab percentage and the RPM-bar
progress meter read it.

`bw`/`total` remains the right answer to a different question — "does the wheel
have all the bytes" — which is how the Files tab still decides
complete-vs-stopped, and how the coordinator recognises the
complete-via-progress firmware variant.

### Outbound flow control: retransmit at the ack frontier, and never drop

The wheel's `fc:00 [session] [ack_seq:u16 LE]` is a **cumulative** ack, so two
rules follow for the host side of an upload. Both were being broken, and
together they deadlocked bundle **8MKDKT7R** (KS Pro) for six minutes.

Note when parsing captures: the inbound ack frame carries a `c3 71` prefix
before `fc 00`, so a matcher anchored at byte 0 sees no acks at all.

**Observed state over the last 5 minutes of that transfer:**

| | |
|---|---|
| wheel's sess-0x04 ack_seq | **frozen at 1391** — 3986 of 4094 acks repeat it |
| host's highest sent seq | **2473** |
| chunks sent past the ack point | **1082** |
| distinct frames / transmissions | 1255 / 26174 → 20.9x each, **24.7x amplification** |

The wheel was waiting for seq 1392. It never arrived, so its cumulative ack
could not move, so everything from 1393 up was discarded on receipt.

**Rule 1 — retransmit only at the frontier.** Re-offering the whole unacked
span is not just wasted wire, it starves the one chunk that would unblock the
transfer: 6.3 kB/s, 64 % of a half-duplex link, spent on frames the wheel was
guaranteed to discard. `SessionRetransmitter.HoldSession` confines a held
session's retransmits to `HeldRetransmitWindow` (8) seqs above its lowest
unacked chunk. Modelled against the state above: 1136 frames instead of 37650,
**33x less wire**, and seqs 1392-1399 get all of it.

**Rule 2 — never drop an unacked chunk on a reliable stream.**
`DueRetransmits` evicts at `maxRetries` (30). For a cumulative-ack stream an
eviction is not recovery, it is a *permanent* deadlock: seq 1392 was dropped
after 30 attempts, after which no future traffic could ever advance the wheel's
ack. A held session is exempt from both that eviction and the `MaxQueueSize`
LRU; the transfer's own stall timeout is what gives up, and it reports a
failure instead of hanging.

**And `QueueSize` is not a window signal.** The upload's pacing loop used queue
depth for liveness — but depth falls both when the wheel acks *and* when the
retransmitter gives up. Every eviction therefore re-armed the 90 s stall
deadline, so the abort never fired, and the same false "window drained" reading
let the emit loop run 1082 chunks past the wheel. `AckedChunkCount` is
monotonic and advanced **only** by a genuine ack — never by eviction, never by
`DropSession` — and is what the pacing loop watches now.

Finally: the upload path never called `DropSession` on exit (Session09 and
`DashboardDownloader` both do), so a finished attempt's unacked chunks kept
retransmitting into a session nobody was reading. It does now.

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
