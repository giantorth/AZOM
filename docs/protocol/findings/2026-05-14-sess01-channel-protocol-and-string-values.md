# Findings 2026-05-14 — sess=0x01 channel-protocol and string-value transport

Bridge-side decode of a live PitHouse session against Simple Rally Mini Dash
revealed a typed sub-msg protocol on **session 0x01** that carries the wheel's
channel catalog, the host's tier-def (subscription), and out-of-band string-
channel value pushes. This is **distinct from** (and runs in parallel with) the
sess=0x02 FF-record path documented elsewhere.

Capture: `sim/logs/bridge-20260514-204307.jsonl` (237,305 frames, 965 s). Loaded
dashboard: Simple Rally Mini Dash, source: AC, tracks observed: Imola →
ks_laguna_seca (`TrackId` channel changing live).

> **Topical canonical home for stable facts**:
> [`../sessions/session-0x01-channel-protocol.md`](../sessions/session-0x01-channel-protocol.md).
> This findings doc retains the discovery path, raw byte tables, and reasoning.

## The protocol in one frame

```
7e 1f 43 17                — frame: 0x7e, len=31, grp=0x43, dev=0x17
7c 00 01 01 6d 00          — chunk header: 7c00, sess=0x01, type=0x01, seq=0x006d
05 10 00 00 00             — sub-msg header: type=0x05, size_LE u32 = 16
07 8e                      — body: channel_idx=0x07, flag=0x80|14
6b 73 5f 6c 61 67 75 6e 61 5f 73 65 63 61    — body: ASCII "ks_laguna_seca"
a5 5c 10 a1                — chunk CRC32-LE
f5                         — frame checksum
```

**Sub-msg framing** on sess=0x01 is `[type:u8][size_LE u32][body:size]` — note
this is a **5-byte header with u32 size**, not the 6-byte `[type][size_LE u16][pad:3]`
header used by the upload sub-msgs on sess=0x05. (Functionally equivalent for
small payloads — `[type][size_LE u16][pad 00 00 00]` and `[type][size_LE u32]`
share the same byte layout when size < 65 536 — but the wheel parses these
streams with different framing semantics, so document them separately.)

## Inventory of sub-msg types observed

| Type | Dir | Bodies seen | Meaning |
|------|-----|-------------|---------|
| 0x00 | h2b | 5× size=1 (body `00` or `01`) | Ack / end-of-burst marker |
| 0x01 | h2b | 7× sizes 97/113/17 | **Tier-def (subscription)** — see below |
| 0x03 | b2h | 2× size=4 (body `01 00 00 00`) | Wheel handshake response |
| 0x04 | b2h | 9× sizes 17–51 | **Catalog URL announcement** — see below |
| 0x05 | h2b | 63× sizes 7/16 | **String value push** — see below |
| 0x06 | h2b 4× / b2h 5× | size=4 (u32 LE counter) | Seq-ack |
| 0x07 | h2b | 1× size=4 (body `02 00 00 00`) | Version / init handshake (value=2) |

The capture's full 965-second run involved exactly one dashboard load and one
track change — so the population above represents one steady-state session
plus its handshake.

## Type=0x04 — Catalog URL announcement (b2h)

Wheel announces every channel its loaded dashboard references, one record per
channel:

```
[type=0x04] [size_LE u32 = url_len + 1] [channel_idx u8 (starts at 1)] [URL ASCII, no NUL]
```

Stream-order positions assign the per-dashboard `channel_idx`. Observed for
Simple Rally Mini Dash:

| idx | URL |
|-----|-----|
| 1 | `v1/gameData/CarSettings_CurrentDisplayedRPMPercent` |
| 2 | `v1/gameData/CurrentLapTime` |
| 3 | `v1/gameData/EngineStarted` |
| 4 | `v1/gameData/Gear` |
| 5 | `v1/gameData/SpeedKmh` |
| 6 | `v1/gameData/SpeedMph` |
| 7 | `v1/gameData/TrackId` |
| 8 | `v1/gameData/TrackLength` |
| 9 | `v1/gameData/patch/TrackPositionPercent` |

The Simple Rally Mini Dash mzdash references 12 channels (`CarCoordinates01/02/03`
plus the 9 above). The wheel did **not** announce `CarCoordinates0[123]` —
they're referenced by widgets but the wheel chose to omit them from this run's
catalog. Cause not yet investigated (likely conditionally bound widgets or
inactive view).

## Type=0x01 — Host tier-def (subscription)

Per-tier record carrying compression code + bit width for each channel
included in that tier:

```
[type=0x01] [size_LE u32] [seq u8 = revision counter] [N × 16-byte channel record]

channel record (16 B):
[channel_idx u32 LE] [compression_code u32 LE] [bit_width u32 LE] [reserved u32 LE = 0]
```

`seq` increments by 1 every time PitHouse re-sends a tier-def (any tier). The
two tier-defs alternate: fast tier (size=113, 7 channels), slow tier
(size=17, 1 channel). Initial transmission used a smaller fast tier (size=97,
6 channels) before `patch/TrackPositionPercent` came online.

Decoded — **all compression codes match `Data/Telemetry.json` exactly**:

| seq | size | Channels (idx → comp / width) |
|-----|------|-------------------------------|
| 0x00 (initial fast) | 97  | 1:0x0E/10, 2:0x07/32, 3:0x00/1, 4:0x0D/5, 5:0x0F/16, 6:0x0F/16 |
| 0x01,0x03,0x05,… (steady-state fast) | 113 | above + 9:0x0E/10 (`patch/TrackPositionPercent`) |
| 0x02,0x04,0x06,… (slow) | 17  | 8:0x07/32 (`TrackLength`) |

| comp_code | name (Telemetry.json) | width |
|-----------|-----------------------|-------|
| 0x00 | bool | 1 |
| 0x07 | float | 32 |
| 0x0D | int30 | 5 |
| 0x0E | percent_1 | 10 |
| 0x0F | float_6000_1 | 16 |

These codes match the existing
[`Telemetry/Frames/CompressionTable.cs`](../../../Telemetry/Frames/CompressionTable.cs)
registry. **No new compression code is introduced** by this finding — the
existing table is correct and complete for bit-packed channels.

### Critical: idx=7 (TrackId) is NOT in any tier-def

`TrackId` has `compression: "string"` in Telemetry.json. **It does not appear
in either tier-def record.** Strings are deliberately omitted from the bit-
packed subscription system and carried separately via type=0x05 records
(below). String channels do not need an entry in
[`Telemetry/Frames/CompressionTable.cs`](../../../Telemetry/Frames/CompressionTable.cs);
they aren't bit-packed at all.

## Type=0x05 — String value push (h2b)

Out-of-band string-channel value record:

```
[type=0x05] [size_LE u32 = 2 + strlen] [channel_idx u8] [flag u8 = 0x80 | strlen] [ASCII strlen bytes, no NUL]
```

Notes:

- ~~ASCII only, no NUL terminator. UTF-16 encoding is **not** used on this path.~~
  **Superseded 2026-05-15**: the encoding is **UTF-8**, not ASCII. The
  `imola` / `ks_laguna_seca` captures used here are pure ASCII, which is a
  UTF-8 subset, so the wire bytes don't distinguish the two — but a live
  test with `áéíñçüöß°` on the CS Pro confirmed the wheel decodes multi-byte
  UTF-8 sequences and renders the correct glyphs. UTF-16 remains ruled out.
  See [`../sessions/session-0x01-channel-protocol.md`](../sessions/session-0x01-channel-protocol.md#type0x05--string-value-push-h2b).
- `flag = 0x80 | strlen` — the 0x80 bit is a "string value" flag; low 7 bits
  redundantly carry the length. Max practical string is 127 bytes, matching
  the `range: "1~100 character"` declarations on string-typed Telemetry.json
  channels.
- Records are self-contained — channel_idx + length both appear in the body
  so the wheel can resync mid-stream.

### Verified examples

| Frame seq | Wire body | Decoded |
|-----------|-----------|---------|
| `0x67` | `05 07 00 00 00  07 85  69 6d 6f 6c 61` | idx=7, len=5, "imola" |
| `0x68`/`0x69`/`0x6a`/… | same | retransmits |
| `0x6d` | `05 10 00 00 00  07 8e  6b 73 5f 6c 61 67 75 6e 61 5f 73 65 63 61` | idx=7, len=14, "ks_laguna_seca" |

### Cadence

Observed deltas between successive type=0x05 records for idx=7 (only string
channel in this dashboard): n=62, min=8.13 s, mean=14.76 s, max=305.04 s.

So PitHouse does **not** re-send on a strict 2 s cadence even though
`TrackId.package_level = 2000` in Telemetry.json. Pattern is closer to:

- Burst (~3–6 retransmits) when value first arrives or changes
- Long silence (10–30 s) at steady state
- Re-send if the host receives some kind of "are you still there" signal (not
  yet decoded — might be tied to the type=0x06 ack-by-seq exchanges)

### What about the other 22 string-typed channels in Telemetry.json?

23 channels have `compression: "string"`; only `TrackId` was active this run
because no game was actually running — PitHouse was idle except for the
in-PitHouse track display. With AC running, expect `patch/TrackName`,
`patch/DisplayTrackName`, `CarModel`, `CarClass`, `SessionTypeName`, etc. to
also stream type=0x05 records.

## Type=0x06 — Seq-ack (both directions)

`[type=0x06] [size_LE u32 = 4] [seq u32 LE]`

The seq value tracks the tier-def revision counter. Wheel emits type=0x06
acks (b2h) to acknowledge it received tier-def revision N; host emits
type=0x06 (h2b) to acknowledge the wheel's last catalog announcement. Not
fully decoded — observed counters: h2b `0`, `0`, `0`, `0`; b2h `9` (matches
the 9 catalog records sent).

## Type=0x07 — Init / version (h2b, once at session start)

`[type=0x07] [size_LE u32 = 4] [02 00 00 00]` — version = 2, sent as the very
first byte of the h2b sess=0x01 stream right after the wheel completes its
catalog announcement burst.

## Type=0x00 — End / ack marker (h2b)

`[type=0x00] [size_LE u32 = 1] [body=0x00 or 0x01]` — appears between bursts.
Possibly an end-of-frame marker or acknowledgement byte. Low diagnostic value;
needs more captures to characterise.

## Type=0x03 — Wheel handshake response (b2h)

`[type=0x03] [size_LE u32 = 4] [01 00 00 00]` — sent twice early in the b2h
stream. Probably acknowledges the host's `type=0x07` init (version 2 →
"accepted, value 1"). Not yet fully characterised.

## Relationship to sess=0x02 (existing tier-def documentation)

Existing docs under [`../tier-definition/`](../tier-definition/) and
[`../sessions/session-0x02-ff-init.md`](../sessions/session-0x02-ff-init.md)
describe tier-defs going on **sess=0x02 with FF-record envelopes** (kind=2/7/
8/11 init, kind=4 dashboard switch, kind=9 periodic, kind=14/15 device log
pull).

This capture shows **sess=0x02 was ALSO active during the same session** —
the histogram counted FF-record kinds 2/9/11/14/15 + a kind=8 catalog upload
of 12,736 decompressed bytes. So sess=0x02 and sess=0x01 carry **different
slices of the protocol in parallel**, not alternative encodings of the same
thing:

| Channel | Carries |
|---------|---------|
| sess=0x01 (this finding) | Wheel's per-dashboard channel catalog (idx → URL), host's tier-def (idx → comp / width), string-channel values |
| sess=0x02 (existing docs) | FF-init handshake (nonce / FFB property catalog / wheel-state), kind=8 "master" channel catalog (all channels PitHouse knows about, ID → name), and the kind=14/15 device log pull |
| group 0x43 (`7d:23`) | Bit-packed value frames for the channels declared in the sess=0x01 tier-def |

**The plugin's existing tier-def code targets sess=0x02 with FF records.** This
finding shows PitHouse uses sess=0x01 with a typed sub-msg framing instead.
Whether the wheel accepts both (per-firmware tolerance) or only one (silent
rejection of the other) needs verification — see follow-up section.

## Implications

### For the plugin

1. **`Data/Telemetry.json` string channels currently dropped from tier-defs
   should stay dropped from the bit-packed tier system** — that's correct.
   They were dropped for the wrong reason (no `CompressionTable` entry); the
   right reason is that they go on a separate transport.
2. **Plugin needs a sess=0x01 catalog parser** to learn the per-dashboard idx
   assignments. The catalog parser at
   [`Telemetry/Frames/ChannelCatalogParser.cs`](../../../Telemetry/Frames/ChannelCatalogParser.cs)
   (or wherever the existing kind=8 parser lives) needs a sibling for the
   sess=0x01 type=0x04 stream.
3. **Plugin needs a type=0x05 string-value emitter** on sess=0x01. Per-
   channel cadence honours `pkg_level` (likely 2000 ms minimum) with extra
   re-sends on value change.
4. **Whether to switch the existing tier-def path from sess=0x02-FF to
   sess=0x01-type=0x01** is a separate question. The wheel was clearly
   accepting both in this capture (FFB properties on sess=0x02 + tier-defs on
   sess=0x01), so the answer may be "do both" — but verify.

### For Simple Rally test-mode failure

The user-reported "Simple Rally doesn't accept test mode" likely traces to
the plugin currently:
- Dropping `TrackId` from BuildMultiStreamProfile (silent skip on string)
- Not emitting any type=0x05 records on sess=0x01
- Hence the `TrackId` widget on the wheel display never receives a value

Fix is implementation tasks #13–#15 in the in-flight task list.

## Tooling

Analysis scripts (one-shots, not promoted to `tools/`):
- `/tmp/catalog_decode.py` — reassembles per-session streams and walks the
  type=`u8`/size=`u32 LE` records
- `/tmp/decode_tierdef.py` — parses type=0x01 bodies as 16-byte channel
  records and cross-references against Telemetry.json
- `/tmp/sess01_full.py` — full inventory of every sub-msg type per direction
  with cadence stats

Promoting one of these (probably `decode_tierdef.py` + the catalog walker) to
`tools/bridge-sess01-decode` is reasonable next step but not done yet.
