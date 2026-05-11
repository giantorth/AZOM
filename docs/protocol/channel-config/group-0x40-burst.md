## Channel configuration burst (group 0x40, post-upload or on connect)

After dashboard file transfer (or on wheel connect without upload), Pit House sends burst of `0x40` commands configuring channel layout. Same burst used both contexts; CS V2.1 (no screen) receives same channel config as VGS (built-in screen). Channel indices and response values are dashboard-specific.

| Cmd | Data | Purpose |
|-----|------|---------|
| `09:00` | (none) | Begin/reset channel config |
| `1e:00` | `CC 00 00` | Enable channel CC on page 0 — wheel responds `CC XXXX` (stored, e.g. `01f4`=500, `03e8`=1000, `0bb8`=3000) |
| `1e:01` | `CC 00 00` | Enable channel CC on page 1 |
| `1e:03` | `CC 00 00` | Enable channel CC on page 3 (page 2 unused) |
| `1c:00`/`1c:01` | `00` | Page configuration |
| `1d:00`/`1d:01` | `00` | Page configuration |
| `28:00` | `00` | Query active dashboard mode (wheel retains across power cycles) |
| `28:01` | `00` | Query active page number |
| `28:02` | `01 00` | Set multi-channel telemetry mode (01=multi, 00=RPM only) |
| `1b:00`/`1b:01` | `FF value` | Brightness per page (value `64`=100%) |
| `1f:00`/`1f:01` | `FF idx 00 00 00` | LED color read per index (`idx`=`0a`–`0f` observed) |
| `27:00`–`27:03` | `00/01 XX YY ZZ` | **Per-page dashboard binding fingerprint** (read via 0x40, set via 0x3F — see § 27:NN dashboard-switch below) |
| `29:00` | `00` | Display settings (TBD) |
| `2a:03` | `00` | Display settings (TBD) |
| various | — | Other display settings (`0a`, `0b`, `05`, `20`, `21`, `24`, etc.) |

Wheel `0x0e` debug log confirms channel config writes EEPROM: `"Table 2, Param 47 Written: 7614374"`.

**Cold-connect (no dashboard upload):** captured during CS → VGS swap in `cs-to-vgs-wheel.ndjson`. Pit House runs full identity probe then same channel configuration burst — no `7c:00` file transfer or `configJson()` RPC. Does not ask wheel which dashboard is active; pushes channel layout from internal state. `0xc0/13:00` response `00 ff ff` during setup may indicate "no active dashboard" or default state.

**Implication:** burst appears required on each wheel connection before telemetry frames accepted. Sending `7d:23` to fresh wheel without first sending `0x40` channel enables and `28:02 data=0100` may not work.

### Page × channel matrix

PitHouse cold-attach capture (R5 base, W17 wheel, 2026-04-29) emitted `1E` enables across **3 pages × 5 channels = 15 combos**:

```
pages    used: 0x00, 0x01, 0x03      (skips 0x02)
channels used: 0x02, 0x03, 0x04, 0x05, 0x06   per page
```

**Pre-2026-04-29 plugin behaviour (`TelemetrySender.SendChannelConfig()`):**
```csharp
for (int page = 0; page <= 1; page++)
    for (byte cc = 2; cc <= 5; cc++)
        _connection.Send(BuildChannelEnableFrame((byte)page, cc));   // 8 combos only
```

Plugin missed `(0,6) (1,6) (3,2) (3,3) (3,4) (3,5) (3,6)` — page 2 entirely, channel 6 on every page. Patched 2026-04-29 to enumerate `pages {0,1,3} × channels {2..6}` matching PitHouse.

Likely meaning of `page` and `channel`:
- `page` = update-rate / tier bucket. 3 pages may correspond to the 3 known `package_level` values `30 / 500 / 2000` ms (see [`../../Telemetry/DashboardProfileStore.cs`]).
- `channel` = sub-stream slot within bucket. 5 slots per page → 15 max simultaneous wheel-bound telemetry channels.

### 28:00/28:01/28:02 details

| Wire | Name (rs21_parameter.db) | Purpose |
|------|--------------------------|---------|
| `28:00 data=00` | `WheelGetCfg_GetMultiFunctionSwitch` | Query active dashboard mode. Wheel retains last loaded dashboard across disconnections. |
| `28:01 data=00` | `WheelGetCfg_GetMultiFunctionNum` | Query active page number |
| `28:02 data=01:00` | `WheelGetCfg_GetMultiFunctionLeft` | Set multi-channel telemetry mode (01=multi, 00=RPM only) |

Read-then-write pattern: Pithouse sends 28:00 and 28:01 (read state), then 28:02 (set mode) during burst. Wheel responds `00:00` to `28:02 data=01:00` — normal behavior, not failure.

**Normal operation:** `28 02 01 00` continues polling ~3.4 Hz to maintain multi-channel mode.

### 28:00/28:01 response format — wheel's current dashboard / page

The wheel's reply to `28:00` and `28:01` is **the wheel's authoritative answer to "which dashboard / page are you currently showing?"**, surviving plugin restart and SimHub reload (the wheel retains the last loaded dashboard across power cycles). This is the only positive signal of active-dashboard state at startup — the configJson state push on session 0x09 lists *installed* dashboards but does not single out an active one (see [`../dashboard-upload/config-rpc-session-09.md`](../dashboard-upload/config-rpc-session-09.md)).

Wire form (response group = request group + 0x80, device nibble-swapped):

```
host → wheel  : 7e 04 40 17 28 00 00 00 [chk]      # 28:00 read
wheel → host  : 7e 04 c0 71 28 00 [slot] [chk]     # response, slot = 1 byte
host → wheel  : 7e 04 40 17 28 01 00 00 [chk]      # 28:01 read
wheel → host  : 7e 05 c0 71 28 01 [page_lo] [page_hi] [chk]   # response, 2 bytes LE
```

Interpretation:

| Field | Encoding | Meaning |
|-------|----------|---------|
| `28:00 [slot]` | `u8` | Index into the wheel's `configJsonList` (alphabetical order of installed dashboards). `0x00` = first entry, `0x01` = second, etc. Matches the slot value the host sends in `kind=4` FF-records on session 0x02. |
| `28:01 [page_lo, page_hi]` | `u16 LE` | Active page within the currently-loaded dashboard. `0x0000` = page 0. Multi-page dashboards (with widget pagination) use this to identify which page is being rendered. |

Example from a 2025-11 firmware (CS Pro W17) where the wheel was showing `Core`:

```
28:00 response = 00          → slot 0 → configJsonList[0] = "Core"
28:01 response = 00 00       → page 0
```

The 11-entry configJsonList for that wheel was `Core, Grids, Mono, Nebula, Pulse, Rally V1, Rally V2, Rally V3, Rally V4, Rally V5, Rally V6` (alphabetical), so `slot=0` resolves to `"Core"`.

**Use case — auto-detect active dashboard at startup.** A host can resolve the wheel's current dashboard by:
1. Reading the configJson state push on session 0x09 (carries `configJsonList: [...]`).
2. Reading `28:00` (returns the slot index into that list).
3. `configJsonList[28:00.slot]` = dashboard name currently being rendered.

This avoids the "host sends tier-def for dashboard A, wheel is rendering dashboard B, channels don't bind" desync after plugin restart. Plugin should auto-select the same dashboard on reconnect rather than reverting to its last saved profile.

**Not currently parsed by the SimHub plugin.** As of 2026-05-10 the plugin sends both reads during the channel-config burst (`TelemetrySender.cs:3866-3867`) and the responses arrive in the wire trace, but the bytes are stored raw in `MozaData` (visible in the Diagnostics tab as `wheel 28:xx raw:`) without being parsed into a structured field. A future patch should resolve `28:00.slot → configJsonList[slot]` and surface it as `WheelDashboardState.ActiveDashboardName` for the plugin to gate its `RestartForSwitch` / catalog-resync logic on.

### 27:NN per-page binding state (host→wheel write on group 0x3F)

**NOTE:** earlier draft of this section claimed `3F 27:NN` is THE
dashboard-switch trigger. Subsequent capture analysis (same pcap) found a
separate **FF-record on session 0x02** that's a stronger candidate for the
primary switch signal — see [`../findings/2026-04-30-dashboard-switch-3f27.md`](../findings/2026-04-30-dashboard-switch-3f27.md). The `3F 27:NN`
writes documented here may be a secondary per-page state update rather than
the actual switch trigger. **Both paths are UNTESTED from the plugin side.**
Wire format below is verified from capture; behaviour against live wheel
from plugin replay not confirmed.

Wire format observed in `wireshark/csp/startup, change knob colors, change dash several times, delete dash.pcapng`. CSP firmware writes per-page binding via group `0x3F` (wheel write), not `0x40` (read). Symmetric pair:

```
read  : 7e 03 40 17 27 [page] 00            host→wheel    (group 0x40)
reply : 7e 06 c0 71 27 [page] [4-byte data] wheel→host    (group 0xc0, dev 0x71 = nibble-swap of 0x17)
write : 7e 06 3f 17 27 [page] [4-byte data] host→wheel    (group 0x3F)
```

`page` = 0..3. `4-byte data` = opaque dashboard fingerprint, format `[flag:1] [3-byte fingerprint]`. Flag byte:
- `0x00` — primary fingerprint (active state)
- `0x01` — alternate fingerprint (cached / counter state — semantics TBD)

Wheel poll responses oscillate between `00 XX YY ZZ` and `01 XX YY ZZ` for the same page across consecutive `27:NN` reads — wheel returns both states alternately.

**Captured switch sequence** (from `startup,change knob colors,...pcapng`):
```
t=49.57s   page 3 ← 00 f6 b4 99    (initial bind at startup)
t=49.59s   page 1 ← 00 c6 e1 9b
t=49.60s   page 0 ← 00 f3 79 a1
t=49.61s   page 2 ← 00 9b a7 eb
t=49.88s   page 2 ← 01 11 47 e6    (alternate-state set)
t=150.57s  page 2 ← 00 ff 64 00    ← user switched dashboard for page 2
t=166.78s  page 3 ← 00 5f 97 ff    ← user switched dashboard for page 3
```

**Fingerprint origin (unknown):** the 24-bit fingerprint does NOT match any field in the session 0x09 configJson state (dashboard `id`, `dirName`, `title`, `hash`). Tried MD5/SHA1/SHA256/CRC32 of those fields and of mzdash file bytes — no match. Likely a wheel-internal opaque ID assigned at upload time, or a custom hash algorithm (FNV/Murmur/proprietary).

**Plugin implications (all UNTESTED — verify with live wheel before trusting):**

- **Detect** active-dashboard changes by polling `27:00..27:03` on group 0x40. Stash last-known fingerprint per page; non-match indicates change of some kind.
- **Drive a switch via `3F 27:NN`** — would require a pre-recorded fingerprint→dashboard mapping. Fingerprint is wheel-assigned, not derivable from mzdash content. NOT the recommended path; try the FF-record on session 0x02 first ([`../findings/2026-04-30-dashboard-switch-3f27.md`](../findings/2026-04-30-dashboard-switch-3f27.md)).
- **Doc correction**: [`../../usb-capture/payload-09-state-re.md`](../../../usb-capture/payload-09-state-re.md) line 167 said "zero `3F:28` write frames anywhere" — accurate but wrong cmd; real activity on cmd `27`, not `28`. The `3F 27:NN` writes here are likely a per-page state update; the actual switch trigger is the FF-record on session 0x02.
