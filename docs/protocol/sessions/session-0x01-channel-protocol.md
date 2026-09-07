# Session 0x01 — channel protocol (typed sub-msg framing)

> **2026-04+ firmware, current PitHouse (verified 2026-05-14 bridge capture).**
> Sess=0x01 carries the wheel's per-dashboard channel catalog, the host's
> tier-def (subscription), and out-of-band non-bit-packed channel values
> (strings, in current observation; possibly other types in future captures).
> Co-exists with the sess=0x02 FF-record protocol described in
> [`session-0x02-ff-init.md`](session-0x02-ff-init.md). Discovery in
> [`../findings/2026-05-14-sess01-channel-protocol-and-string-values.md`](../findings/2026-05-14-sess01-channel-protocol-and-string-values.md).

## Sub-msg framing

Sess=0x01 data chunks carry **typed sub-msgs** packed end-to-end. Header is
5 bytes:

```
[type:u8] [size_LE u32] [body: size bytes]
```

Multiple sub-msgs pack tightly within and across `7c:00` data chunks — walk
by stride `5 + size`. Chunks themselves use the standard
[`chunk-format.md`](chunk-format.md): `7c 00 [sess=01] [type=01] [seq_lo]
[seq_hi]` + ≤54-byte net data + 4-byte CRC32-LE.

## Type registry

| Type | Dir | Purpose | Layout |
|------|-----|---------|--------|
| 0x00 | h2b | End/ack marker (?) | `[type][size=1][body:1 = 00 or 01]` |
| 0x01 | h2b | **Tier-def (subscription)** | `[type][size][seq u8][N × 16B channel records]` |
| 0x03 | b2h | Wheel handshake response | `[type][size=4][01 00 00 00]` |
| 0x04 | b2h | **Catalog URL announcement** | `[type][size = url_len+1][idx u8][URL ASCII]` |
| 0x05 | h2b | **String value push** | `[type][size = 2+strlen][idx u8][0x80\|strlen u8][UTF-8]` |
| 0x06 | both | Seq-ack | `[type][size=4][seq u32 LE]` |
| 0x07 | h2b | Init / version | `[type][size=4][02 00 00 00]` (version=2) |

Types 0x02, 0x08+ not yet observed on sess=0x01 in any capture.

## Type=0x04 — Catalog URL announcement (b2h)

The wheel announces its loaded dashboard's channel set to the host. One
record per channel, in increasing-idx order:

```
[type=0x04] [size_LE u32 = url_len + 1] [channel_idx u8] [URL ASCII, no NUL]
```

- `channel_idx` starts at 1, increments per channel, is per-dashboard (different
  dashboards get different idx → URL assignments).
- URLs are bare ASCII paths like `v1/gameData/SpeedKmh`. UTF-16 is not used
  on this channel.
- The wheel only announces channels its **widgets actively reference**. mzdash
  URLs that aren't bound to a rendered widget may be omitted (observed for
  `CarCoordinates0[123]` on Simple Rally Mini Dash).

## Type=0x01 — Tier-def / subscription (h2b)

The host declares which channels go in which tier with what compression code
and bit width:

```
[type=0x01] [size_LE u32] [seq u8] [N × 16-byte channel record]

channel record (16 B):
[channel_idx u32 LE] [compression_code u32 LE] [bit_width u32 LE] [reserved u32 LE = 0]
```

- `seq` increments by 1 every time the host re-sends a tier-def (any tier —
  fast and slow share one counter).
- Each tier corresponds to a `package_level` group from
  [`../../Data/Telemetry.json`](../../../Data/Telemetry.json) (30 ms fast tier,
  2000 ms slow tier, etc.). Tiers are sent as separate type=0x01 records.
- Compression codes match the
  [`Telemetry/Frames/CompressionTable.cs`](../../../Telemetry/Frames/CompressionTable.cs)
  registry: 0x00 bool, 0x07 float, 0x0D int30, 0x0E percent_1, 0x0F
  float_6000_1, etc.
- `channel_idx` MUST match the catalog announcement's idx for the same URL —
  the wheel keys value-frame routing by idx.

### String channels are deliberately omitted from tier-defs

Any channel with `compression: "string"` in Telemetry.json (`TrackId`,
`TrackName`, `CarModel`, `Flag_Name`, etc. — 23 channels total) **does not
appear in any type=0x01 tier-def**. Strings are transported out-of-band via
type=0x05 records (below), not bit-packed into the value frame.

The plugin's `CompressionTable.cs` does not need a `string` entry —
attempting to assign one would land the channel in a tier-def where the wheel
doesn't expect it.

## Type=0x05 — String value push (h2b)

Self-contained per-string-channel value record:

```
[type=0x05] [size_LE u32 = 2 + strlen] [channel_idx u8] [flag u8 = 0x80 | strlen] [UTF-8 strlen bytes, no NUL]
```

- **Encoding: UTF-8.** Confirmed 2026-05-15 on CS Pro firmware against the test
  prefix `áéíñçüöß°` — the wheel's text widget rendered the actual accented
  glyphs from their UTF-8 byte sequences (`C3 A1`, `C3 A9`, …). ASCII fallback
  silently corrupts non-ASCII chars (turning `Viñedos` → `Vi?edos`) and
  Latin-1 sends raw high bytes that the wheel renders as missing-glyph
  placeholders (no font slot at 0xF1 alone). UTF-16 is **not** used. The
  PitHouse capture used as the original reference (`imola`/`ks_laguna_seca`)
  is pure ASCII, which decodes identically under UTF-8 — the earlier
  "ASCII only" wording was a sample-size artefact.
- `flag = 0x80 | strlen` — the 0x80 bit is the "string value" type flag;
  the low 7 bits redundantly carry the length. **Max strlen = 127 BYTES**,
  not characters; a value with multi-byte UTF-8 sequences holds correspondingly
  fewer codepoints (~42 CJK chars, ~63 European accented chars). Truncation
  is by byte and may split a multi-byte sequence; the wheel renders the
  trailing orphan as a placeholder.
- No NUL terminator. Length is authoritative from `size_LE` and from the
  `flag` byte's low 7 bits (they must agree: `flag & 0x7F == size - 2`).
- `channel_idx` is the idx from the catalog announcement (type=0x04).

### Cadence

Observed re-sending pattern (single TrackId-only sample):

- Initial burst on value-change: ~3–6 retransmits over 100–500 ms
- Steady-state silence: 10–30 s between sends when value isn't changing
- Min observed delta = 8.13 s, mean = 14.76 s, max = 305 s

So the `package_level: 2000` declaration in Telemetry.json sets a **minimum
cadence** floor, not a strict per-2-second beat. Plugins emitting type=0x05
should be event-driven (re-send on SimHub property change) with a 2 s
keep-alive floor.

## Type=0x06 — Seq-ack

Mutual acknowledgement of the per-direction sub-msg revision counter:

```
[type=0x06] [size_LE u32 = 4] [seq u32 LE]
```

- h2b type=0x06: host acks wheel's catalog announcement seq.
- b2h type=0x06: wheel acks host's tier-def revision seq.

Counter values observed: h2b 0/0/0/0, b2h 9 (= the 9 catalog records sent).
Use to gate retransmission — see [`../findings/2026-05-09-acks-dedup-and-catalog-persistence.md`](../findings/2026-05-09-acks-dedup-and-catalog-persistence.md)
for the analogous pattern on the sess=0x02 / configJson paths.

## Type=0x07 — Init (h2b)

`[type=0x07] [size_LE u32 = 4] [02 00 00 00]` — version=2. Sent once at the
start of the h2b sess=0x01 stream, right after the wheel completes its
catalog-announcement burst on b2h. Wheel's type=0x03 `[01 00 00 00]` is
the corresponding response.

## Type=0x00 — End/ack marker (h2b)

`[type=0x00] [size_LE u32 = 1] [body u8]` — appears between bursts. Body
value seen: 0x00 and 0x01. Diagnostic value low; needs more captures.

## Implementation notes for the plugin

1. **Catalog parser**: subscribe to b2h sess=0x01 chunks, walk type=0x04
   records, build URL → idx map per dashboard. Reset on each dashboard
   switch (the wheel re-announces).
2. **Tier-def emitter**: for each declared tier (fast / slow / etc.), pack
   non-string channels into a type=0x01 record with their idx + compression
   code + bit width. Send on sess=0x01. Increment shared seq counter on each
   re-send.
3. **String emitter**: subscribe to SimHub property changes for each string
   channel; on change, build type=0x05 record and send on sess=0x01. Emit
   a re-send at the channel's pkg_level cadence (2000 ms typical) even if
   value unchanged.
4. **Co-existence with sess=0x02 FF**: this sess=0x01 protocol does NOT
   replace the sess=0x02 FF-record handshake — the FFB property catalog
   (kind=11) and kind=8 master catalog still ride sess=0x02. The device log
   pull (kind=14 request / payload, kind=15 receipt) rides whichever session
   carries the FF-init handshake — sess=0x01 in the current-firmware capture,
   interleaved with the typed sub-msgs above. The two channels carry
   complementary information.
