# 2026-04-29 — Initial dashboard sync (cold attach, dev `0x17` integrated display)

Captured live via the USBIP passthrough bridge (`sim/bridge.py`) on real hardware: R9 base + W17 wheel with integrated formula-style dashboard. Bridge log: `sim/logs/bridge-20260429-152157.jsonl`. Sync was actively running during capture (~14 % progress on PitHouse-side at time of analysis), so this doc covers the **cold-attach burst and steady-state catalog stream**, not the full upload tail.

> Scope: dev `0x17` integrated wheel-display sub-device responses tunneled through base 0x71 in the wrapped `0xC3` envelope. Standalone MDD (dev `0x14`) is a different device — see [`../settings/dashboard-0x14.md`](../settings/dashboard-0x14.md).

## Trigger

PitHouse repeatedly probes group `0x43` against three device IDs at ~1 Hz until one answers:

```
7E 01 43 14 00 [cs]    # standalone MDD
7E 01 43 15 00 [cs]    # ?
7E 01 43 17 00 [cs]    # integrated wheel-display
```

Before re-seat: zero responses on any of these — base never forwarded the dashboard probe. After re-seat (wheel + dashboard physically reconnected): `0x43/0x17` started receiving `0xC3/0x71 80` ack within one probe interval. `0x14` and `0x15` continued silent (no MDD attached on this rig).

## Phase 1 — present-ack

```
b2h  7E 01 C3 71 80 [cs]
```

Single byte `0x80` payload. Means: "wheel-display present on `0x17`, ready for full probe." Repeats roughly once per second as keepalive throughout the session.

## Phase 2 — wrapped identity burst (b2h, group `0xC3`, dev `0x71`)

PitHouse runs the standard wheel-probe sequence (see [`../identity/wheel-probe-sequence.md`](../identity/wheel-probe-sequence.md) and [`../identity/display-sub-device.md`](../identity/display-sub-device.md)) against `0x43/0x17`. Each response is wrapped: outer frame `7E [N] C3 71 [inner_cmd|0x80] [slot] [data...] [cs]`.

Observed inner records for this hardware (W17 wheel-integrated dashboard, RGB variant):

| Inner | Slot | Bytes (hex) | Decoded |
|-------|------|-------------|---------|
| `89` | `00` | `01` | presence count = 1 sub-device |
| `82` | `02` | — | product type marker |
| `84` | `01` | `02 11 06` | hw-type triplet — **byte 2 = `0x11`**, new value beyond the documented `0x04` (wheel) / `0x08` (display); likely RGB-display variant |
| `85` | `01` | `02 00 00` | capabilities = none |
| `86` | `8a` | `e5 d0 86 b2 fc ad 74 86 db e2 08` | 12-byte hardware-ID (STM32 UID + framing) |
| `87` | `01` | `"W17 Display"` (16 B null-padded) | model name |
| `88` | `01` | `"RS21-W17-HW RGB-"` (16 B null-padded) | HW version part 1 |
| `88` | `02` | `"DU-V11"` (null-padded) | HW revision |
| `8F` | `01` | `"RS21-W17-HW RGB-"` (16 B null-padded) | FW version part 1 — note: **same string as HW part 1** for this build |
| `8F` | `02` | `"DU-V11"` (null-padded) | FW version part 2 — same as HW rev |
| `90` | `00` | `"ZjHh2CULKQ7GH573"` | serial half 1 |
| `90` | `01` | `"XoUZzSk3wTdJfkaY"` | serial half 2 |
| `91` | `04` | `00` | identity-11 (semantics unknown; cf. wheel-probe doc says 2-byte response — here only 1 byte after slot) |

**New observations vs existing docs:**
- `0x84` byte 2 = `0x11` is undocumented. Existing tables list `0x04` (wheel) and `0x08` (display) only. RGB-capable integrated displays appear to use `0x11`.
- `0x88/01` and `0x8F/01` carry the **same** ASCII string (`RS21-W17-HW RGB-`) on this build, contrary to display-sub-device.md where they are usually distinct (`SM-D` vs `MC-SW` etc.).
- `0x91/04` is single-byte `0x00` here, not 2 bytes.

Phase-2 burst completes within ~150 ms once the wheel acks.

## Phase 3 — direct device-info broadcast (b2h, groups `0x82`–`0x91` as outer group)

In parallel with the wrapped phase-2 response, **multiple downstream devices** broadcast the same field set on their own outer group IDs (no `0xC3` envelope). Each device fires `0x82..0x91` as its own group, with `dev` = nibble-swap of canonical dev ID:

| dev (response) | dev (canonical) | role | identifying strings observed |
|----------------|-----------------|------|------------------------------|
| `0x71` | `0x17` | wheel + integrated dash | `"W17"`, `"RS21-W17-MC SW"`, `"RS21-W17-HW SM-C"`, serial `2XKAX/4G2sOgxaqP...` |
| `0x91` | `0x19` | base motor | `"R5 Black # MOT-1..."`, `"RS21-D05-MC WB"`, `"RS21-D05-HW PM-C"` |
| `0xB1` | `0x1B` | hub / handbrake | `"HB # S01"`, `"RS21-S01-MC HB"`, `"RS21-S01-HW HB-C"` |
| `0x21` | `0x12` | (motor mirror?) | same identifiers as `0x91` |
| `0x31` | `0x13` | (hub mirror?) | similar to `0x91` |

Two transports run simultaneously: the wrapped `0xC3/0x71` envelope (responses to PitHouse probes against `0x17`) **and** the un-wrapped `0x82..0x91` group broadcasts from each downstream device. Same field ids (`84`,`85`,`86`,`87`,`88`,`8F`,`90`,`91`,`89`,`82`) but values differ per device.

Worth verifying whether PitHouse also probes `0x19`/`0x1B` directly or whether these broadcasts are unsolicited.

## Phase 4 — `7c 00` catalog records (b2h, group `0xC3`, dev `0x71`, payload prefix `7c 00`)

Once identity is published, the dashboard streams a **two-slot catalog** over the same `0xC3/0x71` channel. Format (preliminary):

```
7c 00 [slot:1] [page:1] [idx:1] [kind:u16 LE] [length:u32 LE] [data:length] [crc32:u32 LE]
```

### Slot `01` — device-state records

Echoes the device-info already in phase 2/3 in catalog form:

| idx | kind | data | notes |
|-----|------|------|-------|
| `05` | `0007` | `04 8a e5 d0 86 b2 fc ad 74 86 db e2 …` | repeats the 12-byte HW-ID from `0x86` |
| `06` | `0001` | `61 00 00 00 cb 4e d8 78` | unknown |
| `07` | `0500` | `d8 5c bc a6 b4` | unknown |
| `08` | `040a` | `00 00 00 00 00 00 00 6a 59 25 2a` | unknown |
| `09` | `0006` | `f0 65 47 3e aa` | unknown |
| `0a..` | `0000` | empty | placeholder/clear records |

### Slot `02` — telemetry-property catalog (the actual sync payload)

Each record advertises a SimHub-style property the dashboard wants:

| idx | kind | length | data |
|-----|------|--------|------|
| `06` | `00ff` | 0 | sentinel (`ff 00 00 00 ff`) |
| `07` | `0003` | 4 | `01 00 00 00` |
| `08` | `0004` | 17 | `\x01v1/gameData/Gear` |
| `09` | `0004` | 19 | `\x02v1/gameData/MaxRpm` |
| `0a` | `0004` | 16 | `\x03v1/gameData/Rpm` |
| `0b..0d` | `0006` | 4 | `00 00 00 03` (numeric value entries) |

Property names are length-prefixed by a 1-byte tag (`\x01`, `\x02`, …) — the **slot id PitHouse will use to push that property's value**. After the catalog drains, PitHouse replies with `h2b 0x43/0x17 7c 00 …` records of the same shape (host → dashboard config push), at >1700 frames in the first 30 s of capture, ramping the live-telemetry binding.

> The exhaustive telemetry-property list is much longer than what this snapshot shows — at the time of capture the upload was at ~14 % per the PitHouse UI.

## Cross-references

- [`../identity/wheel-probe-sequence.md`](../identity/wheel-probe-sequence.md) — base probe order + inner response shapes
- [`../identity/display-sub-device.md`](../identity/display-sub-device.md) — wrapped-envelope mechanics
- [`2026-04-24-firmware-upload-path.md`](2026-04-24-firmware-upload-path.md) — `7c 23` semantics (related but distinct from `7c 00`)
- [`2026-04-29-session-01-property-push.md`](2026-04-29-session-01-property-push.md) — host-side property push (`ff` records on session 0x01)

## Active-dashboard select channel — `0x40/0x17 28 xx`

> **Correction added 2026-04-29 evening:** the per-switch `7c 00` bursts catalogued below are layout / journal records being paged into a ring buffer on the wheel, **not** the "select active dashboard" command itself. The actual select sits on a separate channel.

The plugin's `TelemetrySender.cs:2543-2549` documents the get-side:

```
// 28:00 = WheelGetCfg_GetMultiFunctionSwitch — query active dashboard mode
// 28:01 = WheelGetCfg_GetMultiFunctionNum   — query active page number
// 28:02 = (set) telemetry channel mode: 01=multi-channel, 00=RPM only
_connection.Send(BuildGroup40Frame3(0x28, 0x00, 0x00));
_connection.Send(BuildGroup40Frame3(0x28, 0x01, 0x00));
```

Live capture confirms heavy h2b `0x40/0x17 28 00 00` and `0x40/0x17 28 01 00` polling whenever PitHouse is connected (single-byte gets), with `0xC0/0x71 28 NN VV` responses carrying the current value. `28 02 NN 00` writes flip telemetry-mode flags but don't carry a dashboard ID.

Page configure / activate uses `0x43/0x17 7c 27` and `7c 23` (`TelemetrySender.cs:2706-2715`):

```
configFrame   = 7E 0A 43 17 7C 27 0F 80 b2 00 b4 00 FE 01 00 [cs]
activateFrame = 7E 0A 43 17 7C 23 46 80 ab2 00 ab4 00 FE 01 00 [cs]
configFrame2  = 7E 06 43 17 7C 27 0F 00 z 00 00 [cs]
where b2 = 0x05 + 2*page, b4 = 0x03 + 2*page, z = 0x06 + 2*page,
      ab2 = 0x07 + 2*page, ab4 = 0x05 + 2*page.
```

These are emitted per page when `EnsureDisplayConfigCache` runs. The 256-byte response space in `0xC0/0x71 28 00 NN`'s second byte during transitions (observed `00, 01, 0a, 0b, 12, 19, 50, 88, 8e, a6, b7, ff` across captures) does not appear to be a stable dashboard ID — value floats during/after switches. Likely a slot index into the journal ring.

## Per-switch `7c 00` write profile (layout-bank journal, NOT select)

User triggered repeated dashboard switches; bridge counter was reset between switches to isolate per-switch profile. Initial 60 s "back→front→back" window analysis was conflated with the in-progress initial sync upload; the per-switch traffic isolated below is what each individual switch actually generates after the initial sync settled.

### Per-switch profile (counter reset between each)

Captured per-dashboard `h2b 0x43/0x17 7c 00` record writes:

| switch | slot 01 idx range | slot 02 idx range | other slots | total frames |
|--------|------------------|-------------------|-------------|--------------|
| Core (1st) | `4c..61` (22 idxs) | `99..aa` (18) | 03/08/09 small | ~50 |
| Grids | `8f..a4` (22) | `8b..a6` (28) | 03/06/08/09 | ~107 |
| Core (2nd) | `a7..c3` (29) | `af..d4` (38) | 03/06/08/09 | 118 |
| Grids→Grids→Mono | `c6..e3` (30) | `fa..ff,00..15` (wrap!) | 03/06/08/09 | mixed |
| Nebula | (slot 02 streaming during, switch burst at +45.84/+46.85s with 13 slot-01 records) | | | |
| Rally V6 | two bursts at +73.39/+74.39s, 8 slot-01 records each | | | |
| Rally V5 | (within same window above) | | | |

**Idx ranges advance monotonically and wrap at `ff→00`.** Slot 02 idx wrap was observed live (`fa, fb, fc, fd, fe, ff, 00, 01, 02 …`). The bank is a 256-entry-per-slot ring buffer keyed by an 8-bit cyclic transaction sequence number.

### Conflated baseline (early observation, kept for reference)

First (pre-reset) 60 s window in same bridge log produced this **non-heartbeat** traffic profile (heartbeat groups `0x40 0x5A 0x5D 0x25 0xC0 0xDA 0xDD 0xA5 0x8E 0x0E 0x9F 0x9E` filtered out except where listed). This count includes the tail of the initial-sync upload, so absolute numbers are inflated:

| dir | grp/dev | cmd | count | role |
|-----|---------|-----|-------|------|
| h2b | `0x43/0x17` | `7c 00` | 512 | dashboard-config record writes |
| b2h | `0xC3/0x71` | `7c 00` | 517 | echo / response |
| h2b | `0x43/0x17` | `fc 00` | 256 | host ack frames |
| b2h | `0xC3/0x71` | `fc 00` | 194 | base ack frames |
| h2b | `0x43/0x14` `0x15` `0x17` | `00` | ~54 each | normal probe heartbeats continue |
| b2h | `0x0E/0x71` | `05 ..` | ~25 | post-switch debug log dump (see below) |

### h2b `0x43/0x17 7c 00` record-bank layout (switch payload)

Decoded `7c 00 [slot:1] [page:1] [idx:1] [kind:u16 LE] [length:u32 LE] [data:length] [crc32:u32 LE]`. During the switch:

| slot | page | idx range | unique idxs | frames | observation |
|------|------|-----------|-------------|--------|-------------|
| `01` | `01` | `00..ff` | 176 | 395 | **main bank** — 256-entry dashboard layout, most idxs touched at least once, hot ones written multiple times |
| `02` | `01` | `00..ff` | 66 | 78 | tier-2 / property-binding bank |
| `03` | `01` | `77..7f` | 9 | 11 | small extension bank (8 entries, ~contiguous) |
| `06` | `01` | `39..3d` | 5 | 5 | tiny block |
| `08` | `01` | `3e..47` | 10 | 12 | tiny block |
| `09` | `01` | `35..3b` | 7 | 11 | tiny block |

**Implication (revised):** the slot `01/01` bank is a 256-entry **ring-buffer journal**, not a "current dashboard layout" array. Each switch appends fresh entries at the cursor's current position (then advances), wrapping at `ff→00`. Idx is a u8 transaction-sequence id, not a stable layout-slot id. The "select active dashboard" pointer lives elsewhere — see `0x40/0x17 28 xx` section above and `0x43/0x17 7c 27 / 7c 23` page activate frames in `TelemetrySender.cs:2706-2715`.

Records inside the journal carry a sub-table prefix in their body's first byte (`01..` for table 1, `02..` for table 2). The kind=`0106` 4-byte u32 records (`da010000`, `ea010000`, `f1010000`, …) are a **monotonic write counter**, not a stable dashboard ID — observed across switches:

| switch event | kind=0106 value (LE u32) |
|--------------|--------------------------|
| Nebula 1st burst | 0x000001da = 474 |
| Nebula 2nd burst | 0x000001ea = 490 |
| Rally V6 1st burst | 0x000001ea = 490 |
| Rally V6 2nd burst | 0x000001f1 = 497 |

First switch frame seen:
```
1777501940.143  h2b  7c 00 02 01 e7  01 ff  0c 00 00 00  97 f4 bc be 04 00 00 00 06 00 00 00 00 00  48 38 11 db
                       slot page idx  kind   length=12   data (12 bytes)                            crc32
```
Slot `02 01` idx `e7`, kind `0xff01` (sentinel/clear?), 12-byte payload `97 f4 bc be 04 00 00 00 06 00 00 00`, crc `db113848`.

### Post-switch debug log burst (b2h `0x0E/0x71`, `0x0E/0x21`)

After bank upload completes, base/wheel/pedal MCUs spew `[INFO]` log lines (kind `05` = log channel) including:
- `Wheel Uart is connected`
- `Heartbeat Log End`
- `JoystickMode:2`, `PaddleMode:2`, `RotaryMode 0:0`, `IndicatorType:0`
- `AxisDuty:50%`, `ButtonNoneProc:100ms`, `ConductionTime[L:2 R:2]ms`
- `MCU temp : 37.00000 (°C)`
- `Throttle/Brake/Clutch calibrate theta` lines, `Sensor direction:`, `Output direction:`
- `device connected: wheel_…`
- `Mean Loss Rate : 0.00000`

Looks like a diagnostic-state echo printed by firmware whenever the dashboard config changes — useful for cross-referencing applied settings vs sent records.

### Telemetry stream signature (for filtering)

Live telemetry (game running) adds these high-rate channels — useful to filter out when isolating switch traffic:

| dir | grp/dev | cmd | rate notes |
|-----|---------|-----|------------|
| h2b | `0x2D/0x13` | `f5 31` | game tick A |
| h2b | `0x41/0x17` | `fd de` | game tick B |
| h2b | `0x43/0x17` | `7d 23` | runtime tier values (matches `7c 23` activate-frame docs) |
| h2b | `0x3F/0x17` | `1a 00`, `19 00` | wheel-state ticks |
| h2b | `0x1F/0x12` | `4f 08..4f 0b` | LED bar updates |

## Correction (2026-04-29 evening): record format misread

Throughout the earlier sections of this finding I parsed `7c 00 SS PP NN MM …` as `session_data { slot, page, idx, kind, length, data, crc }`. That alignment is wrong. The frame layout is the **standard SerialStream chunk** documented in [`../sessions/chunk-format.md`](../sessions/chunk-format.md):

```
7c 00  [session:1] [type:1]  [seq_lo:1] [seq_hi:1]  [payload:≤58]  [crc32_LE:4]
```

So my labels remap as:
- "slot" → **session ID** (`01..0a` are concurrent SerialStream sessions, not bank slots)
- "page" → **type byte** (`01`=data, `00`=control/end, `81`=open)
- "idx" → **seq_lo** (lower byte of the per-session monotonic seq)
- "kind first byte" → **seq_hi** (upper byte of seq; advances when seq_lo wraps)

Implications:
- The "u8 cyclic counter that wraps `ff→00`" I observed is just `seq_lo` rolling while `seq_hi` is constant within the window. Per-session seq is a u16, not u8.
- The "kind family `06XX / 04ff / 0001`" classification is **invalid** — those bytes were `seq_hi + payload[0]` from chunks of advancing seq, not record-type tags.
- Application-level record content lives in the per-chunk `payload` field; widget/binding/value records are reassembled by the receiver per session.
- Counts of "unique kind values" across baseline-vs-spam are still meaningful as a coarse busy-ness signal (more chunks pushed = more activity), but the inferred record taxonomy isn't.

**Re-decode path:** parse chunks per `chunk-format.md` (strip `[session][type][seq][crc]`), reassemble per (session, type=01) seq stream, then decode the application-level records inside. The reassembly is what `Telemetry/Sessions/SessionDataReassembler.cs` already does for plugin-side ingestion — apply the same to the captured JSONL log to get the real record kinds.

The bigger wins below (game-start handshake, channel-enable matrix, RPM-bar LED stream, in-game vs idle switch rate-spike signature, telemetry stream tags) are independent of this misread and remain valid.

## Diff-confirmed switch model (V6→V5 vs V5→V4 isolated captures)

After two clean back-to-back captures with counter-reset between (game off, no other UI activity), diff revealed:
- 1156 non-heartbeat events in V6→V5 (30 s)
- 2558 h2b events in V5→V4 (35 s); 225 unique writes vs the V6→V5 set
- The 225 "unique" writes are **same record kinds at advancing ring idxs**, NOT novel magic frames
- Many `7c 00 02 01 NN 06ff …` records repeat byte-identically across both switches (e.g. `06ff16000000 7ae15a5b 0900000000a4000000430001ffff0000d9d90a0a00006f807f46`) — those are shared widget records used by both dashboards

**Confirmed model (revised after baseline-vs-spam diff):**

1. Dashboard switch traffic **does** go through the base CDC bridge — confirmed by 30 s idle baseline vs 60 s spam-click diff (2026-04-29 evening capture, R5 base, real hardware):

   | metric | idle | spam-click | ratio |
   |--------|------|-----------|-------|
   | `h2b 0x43/0x17 7c 00` rate | 4.39/s | 9.69/s | 2.2× |
   | slot 01 page 01 records | 2.4/s | 7.6/s | 3.1× |
   | unique `(slot,kind,body)` tuples | (baseline) | +80 not in baseline | — |

2. Per-click signature: bursts of **slot 01 page 01 records with kind family `06XX`** (`0600 / 0601 / 0603 / 0605 / 0606 / 060a / 060b / 0611 / 0614 / 0621 / 0653`) plus slot 02 kind `0d ff`. These kinds do not appear at the same rate when the UI is idle — they fire on click.

3. There is no discrete "select dashboard N" magic frame; switch is a burst of widget-content records into the journal. The wheel must derive "active dashboard" implicitly from journal contents (by record version / CRC dedup).

4. Plugin polls `0x40/0x17 28 00 00` and `28 01 00` to read state. Captures show write counterpart `0x40/0x17 27 NN VV` (`27 00..27 03` with value 0, plus `27 02 01` enable) — that is the `ChannelMode` set, not dashboard select. Plugin code only emits `28 02 NN 00`; the `27` family appears in PitHouse capture but not in the plugin's own send path.

5. The `0xC0/0x71 28 00 NN` response value floats during/after switches — appears to be an internal slot/cursor index, not a stable dashboard ID.

6. **Earlier "no signal in spam" claim was wrong.** First diff (V6→V5 vs V5→V4) used too-narrow filtering and missed the burst delta. Idle baseline vs spam-click is the right comparison, and it cleanly separates the click-driven content.

## Game-start telemetry setup (live capture, R5 base, 2026-04-29 16:48)

When user starts a game in PitHouse, the following non-heartbeat traffic appears within 5 s of the first game-tick frame (`0x2D/0x13 f5 31 …` or `0x41/0x17 fd de …`):

```
+0.00  h2b 0x28/0x13 01 00 00            → b2h 0xA8/0x31 01 01 c2     # rate-param 1 = 0x01c2 (450)
+0.00  h2b 0x28/0x13 17 00 00            → b2h 0xA8/0x31 17 01 c2     # rate-param 2 = 450
+0.05  h2b 0x28/0x13 02 00 00            → b2h 0xA8/0x31 02 03 e8     # rate-param 3 = 0x03e8 (1000)
+0.30  h2b 0x2B/0x13 02 00 00            → b2h 0xAB/0x31 02 00 00     # hub set/ack
+0.70  h2b 0x43/0x17 7c 00 03 01 73 00 00 00 00 00   # slot-03 commit marker
+0.70  b2h 0xC3/0x71 7c 00 02 01 9d 04 00 00 00 00   # slot-02 ack
…
+1.20  h2b 0x43/0x17 7c 00 02 01 NN 04 ff …          # slot-02 widget records start streaming
+1.30  h2b 0x43/0x17 7d 23 32 …                      # tier-0x23 telemetry begins
+1.40  h2b 0x41/0x17 fd de 00 00 00 00 …             # FFB tick stream (~20 Hz)
+1.40  h2b 0x2D/0x13 f5 31 00 00 00 00 …             # game tick stream (~20 Hz)
+1.50  h2b 0x3F/0x17 1a NN MM …                      # RPM-bar LED stream begins
```

**New finding — `0x28/0x13` rate-parameter group:**
- `0x28` = read on hub (dev 0x13). Response on `0xA8/0x31` (nibble-swap dev 0x13 → 0x31).
- Three indices observed: `01 → 0x01c2 (450)`, `17 → 0x01c2 (450)`, `02 → 0x03e8 (1000)`.
- Likely tier rate-parameter readouts. Values 450/1000 ms align with PitHouse `package_level` semantics. Plugin's `MozaProtocol.cs` doesn't yet decode this group — see open questions.

**`0x3F/0x17 1a NN MM …` decoded as RPM bar LED stream:**

Frame layout:
```
7E [N] 3F 17 1A 00 [mask:u16 LE] 00 00 [bar-config:u16 LE] 00 00 [cs]
```
- `mask` = which LEDs are lit. Observed progression with rising RPM: `0000 → 0008 → 0018 → 0038 → 0078 → f803 → f80f → f81f`. Bit `0x0008` = first LED, `0xf81f` = full 13-LED bar.
- `bar-config` = LED set / mode: `f81f` (13-LED standard), `ffff` (16-LED extended). Different dashboards use different configs, sometimes mixed.
- Variants `1a 03 00 00 …` (mode reset) and `1a 01 ff 00 …` (mode toggle) interleave.
- Cadence 30-100 Hz when RPM is changing.

**In-game dashboard switch signature** (different from idle switch):

Idle switch fires kinds `06XX` heavily; in-game switch fires kinds `0000 / 0001 / 0003 / 0005 / 0006 / 04ff` instead — these are **live widget instantiation records**:

| kind | inferred role |
|------|---------------|
| `0000` | widget instance with UUID (body `01 00 00 00 [u32 widget-id] [crc8]`) |
| `0001` | widget config payload, variable length |
| `0003` | layout/grid descriptor |
| `0005` / `0006` | binding/position refs (body `04 00 00 00 [u32 idx] [crc32]`) |
| `04ff` (slot 02) | live widget data update |

Plus heavy `0x40/0x17 1f 03 01` LED-state polling burst (rate doubles during transition) and accelerated heartbeats.

## `7d 23` runtime telemetry payload — observation

> **Canonical decode lives in [`../telemetry/live-stream.md`](../telemetry/live-stream.md).** That page documents the frame as `[N] 43 17 7D 23 32 00 23 32 [flag] 20 [data]` where `[flag]` selects a **tier** (one frame per `package_level` bucket) and `[data]` is **bit-packed channels in alphabetical order by URL**. My initial reading of `[flag]` as a per-property tag was wrong — flag is the tier index, and data unpacks via the tier-definition sent earlier on session 0x01/0x02.

Re-interpreting my 2026-04-29 capture under the correct model:

| flag | data len | # frames | meaning |
|------|----------|----------|---------|
| `0c` | 3 | 1489 | a tier with ~24 packed bits |
| `0d` | 2 | 15 | a tier with ~16 bits — value `0x2134`=8500 plausibly the RPM-only tier |
| `12` | 6 | 28 | tier with ~48 bits |
| `15` | 9 | 1078 | tier with ~72 bits — high-rate tier (≈package_level 30) |
| `19` | 9 | 797 | another high-rate tier |
| `1a` | 5 | 35 | tier with ~40 bits |
| `1b` | 4 | 18 | tier with ~32 bits |

Flag base on this connection appears to be ≈`0x0c` (vs PitHouse's typical `0x00`/`0x02`/`0x0a`/`0x13`); flag is a per-connection counter so the absolute value is not portable. Mapping each flag back to a `package_level` bucket and then to specific channels requires the tier-definition records that were sent on session `0x01/0x02` earlier — those records carry the channel list and bit widths per tier (see [`../tier-definition/`](../tier-definition/)).

**Next step to map flag → channels:** capture the tier-definition records emitted just before telemetry starts (group `0x43/0x17 7c:00` on session 0x01/0x02), parse them per [`../tier-definition/`](../tier-definition/) docs to enumerate channels per tier, then bit-unpack the `7d 23` payload accordingly.

## Telemetry-channel enable matrix

Plugin's `TelemetrySender.SendChannelConfig()` (now patched) emits `0x40/0x17 1E [page] [channel] 00 00` per (page,channel) combo. Captured PitHouse on cold attach (2026-04-29 16:41 capture, R5 base):

```
pages    used: 0x00, 0x01, 0x03      (skips 0x02)
channels used: 0x02, 0x03, 0x04, 0x05, 0x06   per page
combos        : 15 = 3 pages × 5 channels
```

Pre-patch plugin emitted only 8 combos (pages 0/1 × channels 2-5) — missing `(0,6) (1,6) (3,2) (3,3) (3,4) (3,5) (3,6)`. Post-patch matches PitHouse.

Likely meaning (unverified):
- `page` = update-rate bucket. PitHouse uses 3 buckets, perhaps mapping to the 3 known `package_level` values `30/500/2000` ms (see `Telemetry/Dashboard/DashboardProfileStore.cs:650`).
- `channel` = sub-stream slot within bucket. 5 slots per page.
- 15 total = max simultaneous wheel-bound telemetry channels.

### Property → (page,channel) mapping — open

Plugin's tier model (`MultiStreamProfile.Tiers`, sorted by `package_level`) doesn't expose `page` or `channel` indices in its public API — it builds frames by tier index. To predict which widget will be bound to which (page,channel), we need:

1. Decode the slot-02 PitHouse records (`\x01v1/gameData/Gear`, `\x02v1/gameData/MaxRpm`, …) — each carries a property-tag byte. Confirm whether the tag byte == channel index, and whether record's slot-id == page.
2. Cross-reference `Data/Telemetry.json` package_level groupings against observed page numbers (0/1/3).
3. Capture h2b traffic for one widget bound to a known property (e.g. RPM at known value 5000) and find which `0x43/0x17 7d 23 …` payload byte carries that value.

Until that's done, `TelemetrySender` can't predict the assignment — it just enables all 15 combos and lets the wheel decide. **Watch for widgets that still render blank after the patch:** that's evidence of a property bound to a slot the plugin's tier-model isn't filling.

## Open questions

- Decode the slot-01 records `06–09` (UUID/CRC pairs?).
- Confirm `kind` field width (u16 LE assumed; could be u8+pad).
- Identify the role of dev `0x21` and `0x31` — appear to be virtual mirrors of `0x19`/`0x1B`.
- `0x84/01 02 11 06`: is byte 2 (`0x11`) a stable RGB-display marker or a per-firmware version field?
- Capture a full sync to enumerate the complete `v1/gameData/*` property set.
- **Decode the `0xC0/0x71 28 00 NN` response byte semantics.** Observed values across switches: `00, 01, 0a, 0b, 12, 19, 50, 88, 8e, a6, b7, ff`. Plugin code only sets `28 02 01 00` (multi-channel) and `28 02 00 00`; the wheel-side response to `28 00` poll varies in ways not yet mapped to dashboard identity.
- **Find the actual "select dashboard N" write.** Plugin's `7c 27 / 7c 23` page-config and page-activate frames (`TelemetrySender.cs:2706-2715`) are candidates but were not visible in the bridge histogram tops during isolated single-switch captures — needs targeted capture with non-CSP build to compare.
- Map `mzdash` `lastModified` (e.g. `1763711166` = `0xbe 18 20 69` LE) into the wire — none of the 11 known timestamps from `Data/Dashes/*.mzdash` appeared verbatim in the bridge log; if dashboards are identified by hash rather than timestamp on the wire, find which hash is used.
