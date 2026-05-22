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
3. On each tag-0x06 END marker encountered in the byte stream, commit:
   snapshot `_pendingIdxs` into `_lastCommittedIdxs`, rebuild
   `_liveCatalog` as `_catalog` masked to empty strings outside the
   committed set, clear `_pendingIdxs`. Dedup: if the new committed set
   equals `_lastCommittedIdxs`, skip the publish + log.
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
