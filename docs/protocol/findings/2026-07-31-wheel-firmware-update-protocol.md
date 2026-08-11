# Wheel firmware update — serial-bus flash protocol (groups `0x15`–`0x18`)

Live capture of a real PitHouse **wheel firmware update** through the bridge:
RS21-W17-MC SW wheel (fw 1.2.7.7 before update) on RS21-D05 base
(fw 1.2.10.10), `sim/logs/bridge-20260731-064830.jsonl`, update initiated
t≈1785512824. First observation of the previously-undocumented device
groups `0x15 0x16 0x17 0x18` — a dedicated flash-transfer protocol,
distinct from the dashboard file-transfer path
([`2026-04-24-firmware-upload-path.md`](2026-04-24-firmware-upload-path.md)).

Update completed successfully: wheel version reply changed
`01 02 07 07` → **`01 02 09 07`** ~4 s after the final commit, with **no
CDC re-enumeration at completion** (the wheel reboots behind the base's
internal bus). PitHouse displays the new version as **1.2.7.9**
(user-confirmed) — the wheel follows the same wire→display mapping as
the base (wire `[major, minor, build, patch]`, display swaps the last
two bytes; see [`../devices/wheelbase-0x13.md`](../devices/wheelbase-0x13.md)
§ Firmware detection), so 1.2.7.7 was wire `01 02 07 07`. Totals: 47,167 bulk frames ≈ 2.74 MB raw streamed
(incl. retransmits) over ~536 s (t=903→1439), 37 digest exchanges,
0 bad checksums through the bridge.

## Kickoff

- t=1785512824…827: **3.1 s bus silence + CDC reconnect** (bridge
  `reconnect_count` 0→1) — the serial device re-enumerated when the
  update started. (Whether PitHouse re-opened the port or the base
  re-enumerated is not distinguishable from this capture.)
- t≈828–830: full **identity re-sweep** — version (`0x04`/`0x05`) and
  device-type (`0x02`) probes to every device (`0x13 0x17 0x19 0x1B`),
  identity-string reply cascade. Wheel still reports `84 71 01 02 07 07`
  (1.2.7.7) at this point.
- t=902.9: first `0x15` status poll (reply `0000`), bulk stream starts
  ~100 ms later.

## Transfer protocol (all addressed to dev `0x17`, replies dev `0x71`)

| Group | Dir | Payload | Role |
|-------|-----|---------|------|
| `0x16` | h2b | `[off:u16 BE] [58 data bytes]` (60-byte payload, 65-byte frame) | Bulk image data. `off` = byte offset **mod 65536** within the current block, striding 58/frame. Tail frames are shorter (observed 2+52 bytes, len `0x36`) |
| `0x15` | h2b poll | `00 00` | Progress poll. Reply `0x95` = received-byte count mod 65536 for the current block; after a NACK it reports the resume offset |
| `0x96` | b2h push | `[off:u16 BE] 01` | **NACK / resume request** — wheel names the offset it wants the stream resumed from; host rewinds to exactly that offset (observed `0F 68 01` → host restarts at `0F 68`). Sent unsolicited, repeated until honored |
| `0x18` | h2b | `01` | End-of-block commit/state command. Reply `0x98` = `02` |
| `0x17` | h2b | 16 zero bytes | Digest request. Reply `0x97` = **16-byte digest** of the received block (MD5-sized; algorithm unverified) — per-block integrity check before the next block starts |

## Block cycle

The image is streamed in **blocks of ≈138 KB** (u16 offset and byte
counter wrap mod 65536 twice per block; end-of-block counter reading
`0x1CB9` = 7353 ≡ 138,425 mod 65536). Per block:

1. Stream `0x16` frames from `off=0000`, 58 bytes each, in dense bursts.
2. Host polls `0x15` alongside; wheel may push `0x96` NACKs → host
   rewinds mid-block (also observed: last ~11-frame window re-sent
   verbatim at block end).
3. `0x18 01` → reply `02`.
4. `0x17` (16×`00`) → 16-byte digest reply (host may re-request; identical
   digest returned).
5. Next block restarts at `off=0000` after a ~1.6 s pause.

8 blocks (~1.09 MB streamed incl. retransmits) in the first ~5.5 min —
effective throughput ≈ 3.4 KB/s, the pace set by the wheel's flash
writes (stream stalls while `0x95` catches up, not by host pacing).

## Concurrent traffic during transfer

- Group `0x06` requests to **dev `0x1F`** (payloads `1E`, `1F`) every
  ~500 ms — dev `0x1F` and group 6's role here undecoded; not seen
  outside the update flow at this cadence.
- Group `0x43` 1-byte keepalives (`06`) to devs `0x14 0x15 0x17`.
- Identity-string heartbeat replies (`0x86`…) keep cycling ~1 Hz.
- Normal settings polling (wheel `0x40` sweeps etc.) **stops** for the
  duration.

## Completion sequence

After the last image block's normal commit + digest
(t=1438.5–1438.65, digest `c1202480…`):

1. **`0x19 00`** → reply `0x99 02` — first appearance of group `0x19`;
   finalize command.
2. `0x18 02` → `02` (argument `02`, not the per-block `01`).
3. A host burst of group-`0x0E` frames with incrementing payloads
   (`00 00 01`…`00 00 0A`) to devs `0x19` and `0x13`, answered on `0x8E`
   with 7-byte records — a log/status readout; purpose undecoded.
4. **Final 32-byte record** streamed as one `0x16` frame at `off=0000`
   (len `0x22` = 2+32) — a small trailer written after the image body
   (boot/activation header pattern), then its own `0x18 01` commit and
   digest (`d9c5fe4f…`).
5. `0x19 00` → `02` again, then a **digest-table walk**: repeated
   `0x18 02` → `0x17` pairs, the wheel returning a *different* digest
   each read — 13 reads, 12 distinct values, including byte-identical
   matches for earlier per-block digests (`bcf7a8ea…`, `c5b377e8…`,
   `fef0e600…`) — i.e. `0x18 02` advances an index into the wheel's
   stored digest table and `0x17` reads the entry; the host verifies
   every block against its manifest before triggering the apply. The
   walk ends when the first digest repeats (table wrap).
6. ~0.5 s later the host polls the wheel version (group `0x04`,
   4-zero-byte payload) — **first two probes unanswered** (wheel
   rebooting/applying), third answered at t=1442.86 with the **new
   version `84 71 01 02 09 07`** (was `01 02 07 07`). No bus gap, no
   re-enumeration — only the update-initiation reconnect at the start.

After the version confirmation the bus stayed in the update-mode idle
pattern (group-6 dev-`0x1F` polls, `0x43` keepalives, identity
heartbeats) — normal settings polling had **not** resumed as of the end
of the observation window (~3.7 min post-update; PitHouse presumably
still on the update screen).
