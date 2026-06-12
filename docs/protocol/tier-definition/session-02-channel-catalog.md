### Session `0x02` — channel catalog (wheel → host)

The wheel advertises which telemetry channels it can decode by streaming
a TLV-encoded **channel catalog** on session `0x02` immediately after
session opens. The host uses this list to filter its outgoing tier
definition (drop channels the wheel doesn't know about) and to present
the user with a per-wheel channel list in the SimHub UI.

### TLV stream layout

```
[0xff]                                              — sentinel / reset marker
[0x03] [04 00 00 00] [01 00 00 00]                 — config param (value=1, constant)
[0x04] [size: u32 LE] [ch_index: u8] [url: ASCII]  — per-channel entry (repeated)
[0x06] [04 00 00 00] [total_channels: u32 LE]      — end marker with channel count
```

| Tag | Field | Notes |
|-----|-------|-------|
| `0xff` | sentinel | Single-byte reset marker; signals "channel catalog stream begins" |
| `0x03` | config param | 4-byte length + 4-byte LE u32 value. Always `1` from wheel (see [`tag-03-config-param.md`](tag-03-config-param.md)) |
| `0x04` | channel entry | 4-byte length + 1-byte channel index + UTF-8 ASCII URL. Length covers index byte + URL bytes (no terminator) |
| `0x06` | end marker | 4-byte length (always `04`) + 4-byte LE u32 total channel count |

### Channel entry shape

Each `0x04` entry encodes one channel:

```
[0x04] [size_LE: u32] [ch_idx: u8] [url: ASCII bytes]
                                   └ no NUL terminator; URL length = size - 1
```

URLs follow the `v1/gameData/...` namespace (see
[`../telemetry/channels.md`](../telemetry/channels.md) § Namespace
distribution). Examples: `v1/gameData/Rpm`, `v1/gameData/Brake`,
`v1/gameData/CurrentLapTime`.

### URL encoding forms

The wheel can emit a URL body in any of six shapes to save bytes on the
wire. The plugin's `ChannelCatalogParser` expands each to the full
`v1/gameData/...` form before storing in `_catalog`. Any URL form the
parser doesn't recognise gets dropped (the plausibility check
short-circuits and increments `sPlausReject` in the per-pass stats), so
adding a new prefix the wheel uses requires touching the parser too —
not just the docs.

**A dropped record doesn't skip its own bytes — it shreds what follows.**
On a `sPlausReject` reject the parser advances a single byte (`i++`) and
re-scans, so a *contiguous block* of unrecognised records mis-frames the
**valid** records after them, not just itself. A CS-Pro track-map dash
emits 64 `\l`/`\L` Location records in one burst; before those codes were
handled (2026-06-07) the 64 rejects walked the buffer one byte at a time
and corrupted the real channels that followed, collapsing the catalog to a
minimal set (symptom: dashboard/test-mode could only drive a handful of
channels). Handling every abbreviation the wheel emits is therefore
load-bearing for the whole catalog, not just the dropped records.

| Wire bytes (URL body) | Form | Expansion | Counter |
|---|---|---|---|
| `76 31 2F …` (`v1/…`) | literal | URL as-is, but the embedded code `\s` (`5C 73`) → `preset/` is expanded (see `\s` note below) | `sFull` |
| `01 …` | `0x01` prefix | `"v1/gameData/" + rest` | `sPrefix` |
| `5C 31 …` (`\1…`) | `\1` abbreviation | `"v1/gameData/" + rest`, with `\t` → `TyreTemp`, `\P` → `TyrePressure`, `\b` → `BrakeTemp` (inferred, see below), `{FL}`/`{FR}`/`{RL}`/`{RR}` → `FrontLeft`/`FrontRight`/`RearLeft`/`RearRight` placeholder expansion | `sAbbr` |
| `5C 70 …` (`\p…`) | `\p` abbreviation | `"v1/gameData/patch/" + rest` — used for the `patch/*` channels documented in [`../telemetry/channels.md`](../telemetry/channels.md) (`patch/TrackPositionPercent`, `patch/TrackName`, `patch/DisplayTrackName`, `patch/GameName`, etc.) | `sAbbr` |
| `5C 6C …` (`\l…`) | `\l` abbreviation | `"v1/gameData/patch/Location_" + rest` — the track-map node ring; `rest` is the ASCII decimal index, so `\l0`…`\l62` → `patch/Location_0`…`patch/Location_62`. Filtered out of the subscription by `IsRadarTrackMapChannel` unless radar/track-map channels are enabled. | `sAbbr` |
| `5C 4C` (`\L`) | `\L` abbreviation | `"v1/gameData/patch/Location"` — the track-map base channel; 2-byte body, no `rest` suffix. | `sAbbr` |

Discovery of `\l`/`\L` (2026-06-07, wire-verified): a CS-Pro track-map dash
advertises its node ring as `\l0`…`\l62` (`5C 6C` + ASCII decimal) plus a
base `\L` (`5C 4C`) — 64 records in one sess-0x02 burst. The pre-2026-06-07
plausibility check accepted only `\1` and `\p` as `5C` abbreviations, so all
64 were `sPlausReject`-ed; and because each reject does `i++` (not a
full-record skip), the block mis-framed the valid channel records that
followed it (see the cascade note above the table). These channels are
track-map data, filtered from the subscription by `IsRadarTrackMapChannel`
unless radar is enabled — so decoding them mainly keeps the parser aligned
through the burst; the visible payoff is the *other* channels no longer
getting shredded.

Discovery of `\b` (2026-05-26, inferred not directly observed): issue #43
user's diagnostics bundle showed 4 corrupted catalog entries
`v1/gameData/\bRearRight`, `\bFrontRight`, `\bRearLeft`, `\bFrontLeft` at
adjacent indices to the corresponding `TyreTemp*` and `TyrePressure*`
channels for the same tire corners. Same user's earlier capture had those
channels as full-text `BrakeTempFrontLeft/Right/RearLeft/Right`, confirming
the channel family exists on the wheel side. The wheel had emitted the
abbreviated form `\1\b{XX}` and the parser left `\b` (5C 62) as literal
bytes because no replacement was registered, producing the rendered
`\bXxxx` output. Added `\b` → `BrakeTemp` to the Replace chain by
analogy with `\t` (TyreTemp) and `\P` (TyrePressure) — same tire-corner
instrumentation family, same encoding pattern. **No PH bridge capture
contains either the literal `BrakeTemp` text or the `5C 62` byte
sequence**, so this expansion is pattern-inferred rather than wire-verified.
If a future PH capture shows `\b` expanding to a different token,
revisit. The previous `\p` discovery follows.

Discovery of `\p` (2026-05-22): on a Simple Rally Mini Dash post-switch
emission, idx 4 carried URL body `5C 70 54 72 61 63 6B 50 6F 73 69 74 69 6F 6E 50 65 72 63 65 6E 74`
(`\pTrackPositionPercent`). The parser's pre-2026-05-22 plausibility check
accepted only `v1/`, `0x01`, and `\1` prefixes, so this entry was rejected
as garbage, idx 4 never entered `_pendingIdxs`, and the dashboard's
track-completion-% slot vanished from every catalog-synthesised tier-def
the host pushed back. Symptom: the wheel showed the layout but the track
position slot rendered as zero. Adding `\p` recognition with the
`v1/gameData/patch/` expansion fixed it without touching anything else.

Discovery of `\s` (2026-06-03, wire-verified both ways): the `v1/preset/*`
namespace (`TimeStamp`, `CurrentTorque`, `SteeringWheelAngle`) is abbreviated
**embedded inside a literal `v1/`** rather than as a whole-prefix code like
`\1`/`\p`. The wheel emits `\s` (`5C 73`) for the `preset/` path segment: URL
body `76 31 2F 5C 73 …` = `v1/` + `\s` + suffix. Unlike `\1`/`\p`, the body
starts with `v1/`, so it passes plausibility via the literal branch — but that
branch did no expansion, storing `v1/\sTimeStamp` verbatim. Symptom: the
channel-mapping UI showed `\sTimeStamp`, and the verbatim URL did not match the
`v1/preset/TimeStamp` key in `Data/Telemetry.json`, so catalog synth fell to
the heuristic fallback and the host fed the wheel a constant 0 (breaking the
`(tt - lastTt) < 1200` ms flash-on-change logic in community dashboards).
**Wire-verified, not inferred**: the same `TimeStamp` channel appears as the
full literal `v1/preset/TimeStamp` (moza-wire `20260602-212424`, idx 35) AND as
`v1/\sTimeStamp` (`20260602-184935`, idx 2), so `\s` → `preset/` reproduces the
known URL exactly. Fix: expand `\s` → `preset/` in the literal branch of
`ChannelCatalogParser`.

**`v1/preset/*` is a wheel-internal namespace — the host must NOT send it
(corrected 2026-06-12).** The wheel firmware supplies `preset/` values
(`TimeStamp`, `CurrentTorque`, `SteeringWheelAngle`) to its own dashboard
engine from internal state (its clock, torque output, steering-encoder
angle). Wire-verified against a single-channel `preset/TimeStamp` test
dashboard (`sim/logs/bridge-timestamp-pithouse.jsonl`, PitHouse↔real wheel):
PitHouse's tier-def subscribes to **no** `comp=7`/float channel and sends
**zero** `7d:23` value frames, yet the wheel renders a live timestamp
counting toward zero — proving the value is wheel-sourced. Earlier builds
mapped `v1/preset/TimeStamp` to the plugin-computed `@internal/TimeStamp`
clock and *subscribed* to it; that **overrode** the wheel's correct internal
value with a large positive ms count, garbling the display and breaking the
`(tt - lastTt)` flash-on-change logic. `DashboardProfileStore.IsWheelInternalPresetChannel`
now drops the whole `preset/` namespace from every subscription so the wheel
fills it itself, matching PitHouse. (The constant-0 symptom in the `\s`
history above was a *different* bug — a garbled URL that still subscribed;
the fix is to not subscribe at all.)

### Channel indexing

- Index `0` is **reserved for padding** — sent on session 0x01
  device-description but never re-used here on session 0x02.
- Real channels start at index `1`.
- Indices are **1-based** and assigned in the wheel's **parse order from
  the currently-loaded mzdash** (the order `Telemetry.get(...)` /
  `v1/gameData/...` references appear in the dashboard's JSON), NOT
  alphabetically. The early-2026 alphabetic hypothesis was wrong: wire
  traces against dashboards with known non-alphabetic URL order confirm
  the wheel re-indexes per dashboard, in load order. A wheel may have
  channels indexed 1..N where N depends on the active dashboard's
  channel count.

### Observed catalogs

| Wheel | Channel count | Channels |
|-------|---------------|----------|
| VGS | 16 | BestLapTime, Brake, CurrentLapTime, DrsState, ErsState, FuelRemainder, GAP, Gear, LastLapTime, Rpm, SpeedKmh, Throttle, TyreWearFL, TyreWearFR, TyreWearRL, TyreWearRR |
| CSP | 20 | (VGS list +) ABSActive, ABSLevel, TCActive, TCLevel, TyrePressureFL, TyrePressureFR, TyrePressureRL, TyrePressureRR, TyreTempFL, TyreTempFR, TyreTempRL, TyreTempRR |

The catalog tells the host **what the currently-loaded dashboard
subscribes to**, not the union of all dashboards the wheel could ever
load. Switching dashboards changes the catalog the wheel advertises on
the next connect.

### Worked example: VGS BestLapTime entry

```
04                                — tag
14 00 00 00                       — size = 20 (= 1 byte index + 19 byte URL)
01                                — ch_index = 1
76 31 2f 67 61 6d 65 44 61 74     "v1/gameDat"
61 2f 42 65 73 74 4c 61 70 54     "a/BestLapT"
69 6d 65                          "ime"
```

(URL length: 19 bytes; total entry: 20 bytes after the 5-byte tag/length
prefix → 25-byte TLV entry on wire.)

### Back-references and END-marker generations

After the initial full-URL announcement, the wheel re-emits the catalog
on every dashboard switch (re-indexed) and **continuously as a
keepalive** for as long as the session is open. Two record forms appear:

- **Full URL records** — `[0x04] [size_LE] [ch_idx] [url bytes]` with
  `size > 1`. The wheel only emits these for idxs whose URL is being
  *declared* (initial announcement, switch with a changed channel,
  full-catalog refresh).
- **Back-reference records** — `[0x04] [04 00 00 00] [ch_idx]` with
  `size = 1` (just the idx byte, no URL bytes). The wheel emits these
  to refresh / re-acknowledge any idx it has ever announced. **Back-refs
  are emitted both as part of switch announcements (carry-over channels
  the new dash inherits) AND as part of pure keepalive** — they're
  indistinguishable on the wire.

The end-of-announcement boundary is the **tag-0x06 END marker** with a
u32 value (`[0x06] [04 00 00 00] [u32 LE value]`). The value bumps
monotonically per generation within a session and is the tier-def
version handshake — the host echoes the latest value in its own tier-def
emissions so the wheel treats them as the current generation rather than
duplicates. Keepalive END markers re-assert the current generation with
the same value; switch END markers bump to a new value.

In practice the wheel will emit **multiple END markers at the same
generation value within a single inbound buffer**: a first burst
declares the current dashboard's idx set (e.g. `{1..6}` after a switch
to a 6-channel layout), followed by one or more keepalive bursts that
re-affirm the FULL historical catalog mapping (`{1..N}` where N is
every idx the wheel has ever bound this session) terminated by another
END marker carrying the same value. Each same-`markerValue` burst is
the wheel maintaining its back-ref resolution table across past
dashboards, NOT extending the current dashboard's channel set — see
the [Live-set tracking](#live-set-tracking-which-idxs-are-in-the-current-dashboard)
rules for the parse-side handling.

### Live-set tracking ("which idxs are in the current dashboard?")

`Catalog` (the accumulated URL list) is not dash-specific — it preserves
every URL the wheel has ever announced this session so back-refs can
resolve. To know which idxs are in the **currently-loaded** dashboard
(needed for catalog-only profile synthesis and the channel-mapping UI),
the parser publishes a separate `LiveCatalog` view via these rules:

1. Walk records in byte order. Maintain `_pendingIdxs` (set of idxs
   touched since the last END marker boundary).
2. **Only full URL records add to `_pendingIdxs`.** Back-references are
   resolved into `_catalog` for URL lookup but do NOT contribute to
   the live set — they're ambiguous (keepalive vs. carry-over) and
   crediting them poisoned post-switch live sets with stale historical
   idxs in observed traces.
3. On each tag-0x06 END marker encountered in the byte stream, read the
   END marker's `markerValue` from bytes 5..8 at the parse loop's
   current position (NOT from `_lastWheelEndMarker` — that field is set
   by the pre-parse wire scan to the LATEST END value in the buffer and
   was wrong for every non-final END in a multi-END buffer; a buffer
   like `[urls A] END=109 [urls B] END=138` produced two commits that
   both saw `_lastWheelEndMarker = 138` and got misgrouped into a single
   generation). Then:
   - If `markerValue != _committedEndMarker` → **new generation**.
     Snapshot `_pendingIdxs` into `_lastCommittedIdxs`, rebuild
     `_liveCatalog` as `_catalog` masked to empty strings outside the
     committed set, advance `_committedEndMarker = markerValue`, clear
     `_pendingIdxs`. Dedup: if the new set equals the prior
     `_lastCommittedIdxs`, skip the publish + log.
   - If `markerValue == _committedEndMarker` → **same generation**, drop
     the burst. **First commit at a given `markerValue` is authoritative
     for the current dashboard.** Subsequent same-`markerValue` commits
     are protocol re-affirmation bursts (the wheel maintains its full
     historical catalog mapping across dashboards for back-ref
     resolution and re-asserts the full mapping in a follow-up burst
     within the same generation), and extending `_liveCatalog` with
     those idxs lets stale prior-dashboard idxs leak into the current
     dashboard's synth profile. Dropping them implicitly preserves the
     first commit's idxs against later *subset* re-emissions in the
     same generation too — the dropped-Gear case where the wheel emits
     `{1..9}` then `{1..3}` keepalives at the same `markerValue` would
     otherwise blank channels 4..9 if we overwrote on every commit.
4. Clear `_pendingIdxs` at the start of every parse pass. The buffer is
   append-only and re-walked each pass; carry-over state would let URLs
   from an uncommitted generation pollute the FIRST commit of the next
   pass.

This gives the host a clean "channels in the wheel's currently-loaded
dashboard" view for tier-def synthesis even when there's no local mzdash
to consult.

### Plugin consumption

[`TelemetrySender.WheelChannelCatalog`](../../../Telemetry/TelemetrySender.cs)
parses this stream during preamble and exposes the resulting URL list to
the UI. The list is also used by `FilterProfileToCatalog` to drop tier
entries whose URL doesn't appear in the wheel's advertised set, with
last-path-segment fallback (case-insensitive). See
[`../plugin/tier-impl.md`](../plugin/tier-impl.md).

Catalog-only profile synthesis (`TelemetrySender.MaybeSwapProfileForCatalog`,
fallback path when no local mzdash is configured) consumes
`ChannelCatalogParser.LiveCatalog` rather than `Catalog`, then builds a
`MultiStreamProfile` via `DashboardProfileStore.BuildProfileFromCatalog`.
Re-synthesis triggers on catalog count change OR `LastWheelEndMarker`
value change.

### Cross-references

- [`handshake.md`](handshake.md) — when this stream arrives in the
  bidirectional sequence
- [`version-0-url-csp.md`](version-0-url-csp.md) — host echoes this same
  TLV format back to CSP wheels as the v0 subscription confirmation
- [`version-2-compact-vgs.md`](version-2-compact-vgs.md) — VGS uses a
  different compact host response that encodes compression and bit
  widths instead of URLs
