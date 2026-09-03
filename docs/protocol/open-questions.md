## Open questions

-
- **Relayed HGP/SGP discriminator — what does a base/hub-relayed SGP answer on group `0x04`?** A relayed shifter has no PID and both models share bus id `0x1A`, EEPROM table 9, and the whole `0x51`/`0x52` settings block — a relayed **HGP** answers brightness and colors reads/writes exactly like an SGP (measured, bundle `32ZD7KHW`, see [`devices/shifter-0x1A.md`](devices/shifter-0x1A.md) § Telling HGP from SGP). The one signal that could differ is the group `0x04` device-type reply, measured at `01 02 08 01` for that HGP and **never measured for a relayed SGP**. Until an SGP bundle lands, "dev-type `08 01` ⇒ HGP" is a positive HGP match that does not prove the SGP differs. Both mitigations now ship: `ProbeRelayedShifter` reads model-name (`0x07`) and hw-version (`0x08`) at `0x1A` and logs whatever comes back (a relayed pedal set at `0x19` answers those groups — see [`identity/pedal-0x19.md`](identity/pedal-0x19.md); whether `0x1A` does is the open part), and the raw device-type plus the HGP/SGP verdict is logged on every relayed shifter connect, so the next SGP bundle answers this on arrival. A third, untried option: probe device-type on the **standalone** SGP lane (PID `0x0023`, model already known from the PID) — it would give the first SGP reading, though at root id `0x12` rather than relayed `0x1A`.

- **EEPROM direct access** — group 10 protocol found in rs21_parameter.db but never observed in USB captures; needs live verification.

- **Partner-API teardown on iRacing exit** — Plugin replicates PitHouse's iRacing-engage path (`base-feedforward` grp 0x2A cmd 0x40, `base-high-freq-torque` grp 0x2A cmd 0x41, `base-motor-run-state` grp 0x2C cmd 0x01 — see `Sdk/Resources/Motor/Motor{Feedforward,HighFrequencyTorque,SetMotorRunState}Resource.cs`). These writes persist to EEPROM (Tables 11 and 5 per firmware `param_manage.c` log echoes). **Unverified:** does PitHouse re-write these to a "disabled" value when iRacing exits or PitHouse disengages? If yes, the wheel stays in iRacing mode after a non-iRacing sim takes over, and the plugin needs a matching teardown path. Need a capture of iRacing-exit → 30 s idle (or PitHouse close) to see if a parallel CoAP DELETE / write-zero occurs. The 2026-05-23 capture ran for 215 s post-POST with no teardown write observed but did not include an exit event.

- **PitHouse UDP control protocol — additional PacketIds.** Plugin implements PacketId 3 (SteerLock write) and PacketId 4 (SteerLock read) on the plain-CBOR UDP control surface at port 40288 — see [`pithouse-udp/README.md`](pithouse-udp/README.md). These are the only IDs the RallySimFans launcher uses. PitHouse almost certainly handles others (FFB strength write, profile switching, calibration triggers, possibly volume / chime / LED commands) on the same port. The plugin's `MozaControlUdpServer` logs unknown PacketIds at sample 1/60. **Capture-driven follow-up:** one PitHouse session with the configuration UI exercised end-to-end (rotation, FFB, profile load, calibrate) would surface the full PacketId catalog. Extension on our side is one handler class per ID; no listener changes needed.

- **PitHouse UDP control — `Version` field semantics.** PacketId 3 (write) carries `"1.0.0"`; PacketId 4 (read) carries `"1.0.4"`. Plugin currently accepts any string. Open whether PitHouse rejects unknown versions or treats the field as advisory — affects whether we need to pin allowed versions per-PacketId or accept everything.

- **PitHouse UDP control — settings.ini write-out.** Real PitHouse persists its UDP port to `%USERPROFILE%\Documents\Moza Pit House\settings.ini` `[Application] udpPort`, which third-party clients (RSF observed) read to discover non-default ports. Our plugin defaults to 40288 but does not write the file — so a user with `MozaPluginSettings.ControlUdpPort` set to anything else would be unreachable by clients that only do file-based discovery. Decision needed before shipping a non-default port: either write the file (clean PitHouse-replacement behaviour) or document the port override requirement out-of-band.

- **PitHouse UDP control — `ReplyPort=0` behaviour.** RSF's client treats local-bind-port-0 as "abort the read" (it never sends the request in that case). PitHouse's server-side behaviour when it does receive a request with `ReplyPort=0` is untested. The plugin's `PitHouseReplyContext.SendReply` drops with a debug log; if PitHouse instead replies to the source port, we may need to match that.

- **PitHouse UDP control — multi-base topology.** The protocol envelope has no device-id field — implicit assumption is one wheelbase per machine. RSF only ever talks to a single base. If a user attaches two bases, both servicing the same write is almost certainly wrong; needs a survey of real PitHouse behaviour before any multi-base shipping.

- **Group `0x21` — one-shot main probe, reply is a constant.** Host → dev `0x12` on the wheelbase pipe (PID `0x0006`), exactly **once per PitHouse connect**, then never again:

  ```
  7E 01 21 12 03 [chk]              host → main
  7E 05 A1 21 03 AA 55 01 90 [chk]  main → host
  ```

  Byte-identical in every PitHouse startup capture checked (`12-04-26/moza-startup`, `12-04-26-2/moza-startup-1`, `-2`, `ksp/putOnWheelAndOpenPitHouse`, `fh6/pithouse-start`, `fh6/pithouse-start-low-fps-at-start`) — 6/6, one request each, always the same 4 value bytes. Fires consistently **0.6–1.7 s after the first `0x07` model-name probe**, i.e. inside the identity phase:

  | capture | first `0x07` | `0x21` | first session open |
  |---|---|---|---|
  | `moza-startup` | 8.67 s | 9.30 s | 8.76 s |
  | `moza-startup-1` | 8.41 s | 9.05 s | 8.50 s |
  | `putOnWheelAndOpenPitHouse` | 16.29 s | 17.20 s | 26.86 s |
  | `fh6/pithouse-start` | 18.04 s | 19.72 s | *(none in capture)* |

  Note it is **not** ordered relative to the session opens — it lands ~0.5 s after them on the `12-04-26*` rigs and ~9.7 s before them on the KS Pro rig. So it tracks the identity sweep, not session setup. Absent from every `simhub-*` capture, and the plugin has no group-`0x21` command anywhere — so this is PitHouse-only today.

  `AA 55` is the classic sync/magic pair, so the reply reads like a fixed capability or protocol-generation token rather than a per-unit value — but all six captures resolve to a PID `0x0006` (R12) base, so "constant" may just mean "constant for this base family". Open: (a) does the value vary by base model / firmware, (b) does the base gate anything on the read, (c) is the plugin's omission harmless? The plugin has shipped without it, so it is not required for telemetry — but it is a cheap parity gap to close if (b) turns out to matter. Resolving needs the same one-shot read on a non-R12 base, and one PitHouse connect where the probe is blocked (sim-side) to see whether engagement still happens.

- **Group `0x4C` — MOZA Stalks has a live CDC protocol; `usb-ids.md` said it had none.** PID `0x0024` (`Stalks`) enumerates its own CDC pipe and answers a full protocol surface at dev `0x12`. Confirmed by resolving the USB device address of every group-`0x4C` frame back to its `idProduct` — `0x346E:0x0024` in all five captures that carry the group (`AB9/all_gears`, `AB9/1-N`, `fh6/pithouse-start`, `fh6/pithouse-start-low-fps-at-start`, `fh6/controller-wheel-controller-pithouse-running`), across two rigs. In `all_gears` the Stalks is a third MOZA device alongside the R12 base (`0x0006`) and the AB9 (`0x1000`), each on its own address.

  The device self-identifies through the standard identity groups:

  | Probe | Reply |
  |-------|-------|
  | `0x07` model-name | `S07` |
  | `0x08` hw-version | `RS21-S07-HW BM-C` |
  | `0x08` hw-revision | `U-V10` |
  | `0x0F` sw-version | `RS21-S07-MC SW` |
  | `0x04` dev-type | `01 02 07 01` (DT_2 DT_3 = `07 01`) |
  | `0x05` capabilities | `01 02 4F 00` |
  | `0x09` presence | `00 08` |
  | `0x11` identity-11 | `04 01` |
  | `0x10` serial | 32 ASCII chars |

  Beyond identity, the only traffic is heartbeat (`0x00`/`0x80`), group `0x0E` (param/debug), and a **~1 Hz read on group `0x4C`**:

  ```
  7E 05 4C 12 07 00 00 00 00 [chk]   host → stalks
  7E 03 CC 21 07 00 00 [chk]          stalks → host
  ```

  Always cmd `0x07` with a 4-byte zero payload, always a 2-byte zero reply — across all 81 polls (26 / 20 / 15 / 12 / 8) in those five captures. It runs for essentially the whole capture (`controller-wheel-controller-pithouse-running`: 0.06 s → 25.0 s of a 25.4 s capture) or from the moment PitHouse engages (`pithouse-start`: 21.1 s → 36.6 s of 39.7 s).

  Three things are open. (1) **What `0x4C`/`07` reads** — the value has never been observed non-zero, so it is probably a state/status word that only moves when a stalk is actuated; a capture with the stalks physically operated while PitHouse is open would settle it. (2) **Whether the poll is load-bearing.** The plugin reads the Stalks over **HID only** (`Protocol/MozaHidReader.cs` — a 28-button joystick feeding `MozaData.StalksButtonStates` and the truck-sim keyboard feature) and never opens its CDC pipe, so if the `0x4C` poll is a keepalive the plugin is relying on the device not needing one. It has not caused reported problems, which is weak evidence that it is optional. (3) **`0x09` presence returns `00 08`** where a wheel with one Display sub-device returns `00 01` — if that field really is a sub-device count, the Stalks exposes 8 of them, which nothing else in the docs accounts for.

  Also needs folding into the canonical pages once decided: [`devices/usb-ids.md`](devices/usb-ids.md) describes the `Stalks` category as *"no CDC traffic yet"* (now known false — corrected inline), and [`identity/dev-type-table.md`](identity/dev-type-table.md) has no row for a non-wheel dev-type, so `07 01` / `S07` is unrecorded.

- **Group `0x5A` — handbrake presence poll, 20-byte reply undecoded.** The plugin emits this itself (`Telemetry/Frames/TelemetryFrameCache.cs` `HandbrakePresenceFrame`, dispatched from `TelemetrySender.Tick.cs`) but it appears in no device table:

  ```
  7E 01 5A 1B 00 [chk]                      host → handbrake
  7E 14 DA B1 00 × 20 [chk]                 handbrake → host   (N = 0x14 = 20)
  ```

  Verified on `soft-restart.calibrate-paddles.interpolation-…deadzone-…` with a handbrake physically attached (dev `0x1B` answers `0x00` presence heartbeats 391×): 5418 requests, 5415 replies, paired 1:1 with the documented `0x5D` output poll (5414 requests) — matching `TickEmitPeripheralPolls`, which fires presence at phase 0 and output at phase `slow/5` of the same ~1 Hz cycle. (The comment above the frame table in `TelemetryFrameCache.cs` claims "presence ~22 Hz, handbrake-output ~10 Hz"; the tick code and the wire both say ~1 Hz each, so that comment is stale.) **Every reply body byte is zero in the entire capture** — 20 zero bytes, 5415 times.

  So the request works and the device answers, but nothing is known about what the 20 bytes carry. `0x5A` also sits *below* the documented handbrake block (`0x5B` settings read / `0x5C` write / `0x5D` output / `0x5E` calibration — see [`devices/handbrake-0x1B.md`](devices/handbrake-0x1B.md)), which is the one gap in an otherwise contiguous per-device group quartet, so it may not be a handbrake group at all — it could be a generic per-device block that only the handbrake happens to be polled on. Open: (a) does any byte ever become non-zero (pull the handbrake during a capture), (b) is `0x5A` accepted by other device ids (`0x19` pedals, `0x1A` shifter) — a cheap read sweep would answer this, (c) what is the plugin's poll actually for, given the reply is discarded? If it is genuinely a presence probe then the reply body is irrelevant and this is purely a documentation gap; if it carries state, the plugin is throwing data away. Caveat before deleting it as dead weight: apparently-useless parity polls on this bus have repeatedly turned out to keep a device engaged at idle, so establish what it does before removing it.

- **Non-MOZA CDC traffic can false-positive as a Moza frame.** In `AB9/all_gears` a single frame on USB device address 22 (`0x1532:0x0D06`, a Razer device) parsed as group `0x4D` and is the capture's only checksum failure. Rate is 1 frame in 7365 and it is flagged, but any tooling that trusts `0x7E [N]` framing without a checksum gate on a multi-vendor capture will pick up noise like this. Not a protocol question — recorded so the stray `0x4D` is not chased as a real group.

- **Group 0x09 semantics** — presence/ready check sent first during probe. Response `00 01` may indicate sub-device count (VGS has 1 Display sub-device). Needs verification with other wheel models.

- **Group 0x28 / 0x29 purpose** — group 0x28 queries base for per-device parameters (values 450, 1000 seen); group 0x29 sets base parameter (value 1100). Possibly FFB or calibration related.

- **Session-0x01 `ff` push: `kind` field semantics** — host→wheel property pushes (see [`findings/2026-04-29-session-01-property-push.md`](findings/2026-04-29-session-01-property-push.md)) carry a `kind:u32` whose full table is still incomplete: brightness uses `kind=1`, standby `kind=10` (u64 ms). The "brightness=100 baseline uses kind=14" reading was wrong — `kind=14`/`kind=15` are the device log pull, resolved 2026-08-07 in [`sessions/session-0x02-ff-init.md`](sessions/session-0x02-ff-init.md) § Device log pull. Still open: whether `kind` is a property-id table, a value-encoding selector, or both; and what the RPM/flag-colour and indicator-mode pushes use. Also: are there pushes with `size > 12` (strings, colour triplets, multi-field structs)?

- **Sess=0x02 init kind=8 / kind=11 per-session content** — Canonical doc: [`sessions/session-0x02-ff-init.md`](sessions/session-0x02-ff-init.md). The wheel requires `kind=2 + kind=7 + kind=8 + kind=11` on session 0x02 before it will honour `kind=4` dashboard-switch records. Plugin currently emits only `kind=2 + kind=7`, so dashboard switches are silently ignored. Verified 2026-05-13: shipping the captured `Resources/sess02_init_kind{8,11}_pithouse.bin` bytes verbatim on every `StartInner` cycle **locks the wheel** within a handful of restart cycles (W17 / CS Pro hardware, required physical power-cycle). The records carry session-bound state (kind=2 offsets 12..15 vary per capture; kind=8 body size grows monotonically across a single PitHouse session). Re-enabling kind=8/11 emission requires decoding the inner structure and building per-session-correct bodies — see [`sessions/session-0x02-ff-init.md`](sessions/session-0x02-ff-init.md) for the required-work list and [`findings/2026-05-07-sess02-ff-kinds-reference.md`](findings/2026-05-07-sess02-ff-kinds-reference.md) for the partial body decode.

- ~~**Dashboard switch wire signal**~~ — VERIFIED 2026-04-30/05-01. Primary: FF-record on session 0x02, 25-byte payload. Slot = **0-based** index into `configJsonList` (alphabetical names from session 0x09), **NOT** `enableManager.dashboards`. Verified against live wheel: slot→dashboard mapping confirmed correct. PitHouse re-sends tier-def ~800ms later (no preamble, just enable/tier/end records). Secondary: `0x3F 0x17 27:[page]` per-page fingerprint write (state sync, not the switch trigger). See [`findings/2026-04-30-dashboard-switch-3f27.md`](findings/2026-04-30-dashboard-switch-3f27.md). Remaining work: channel catalog uses `\x01` prefix post-switch (parser expanded to handle both forms); tier-def timing needs ~800ms delay after FF-record for correct catalog indices.

- ~~**Tier-flag → package_level mapping inverted between plugin paths**~~ — RESOLVED 2026-04-29. Type02 firmware uses **flag=2 = fastest tier**, NOT flag=0. Wheel binds widgets to the highest flag value. Plugin's `TelemetrySender.Profile` setter now expands single-pkg_level profiles to 3 tiers and assigns the fastest pkg_level to tier 2 (flag=2). Confirmed live: Nebula widgets render value updates only when fast-tick frames carry flag=2.

- ~~**Telemetry-flag count vs package_level count**~~ — RESOLVED 2026-04-30. PitHouse multi-pkg-level dashboards emit `4 broadcasts × N sub-tiers per broadcast`, where each broadcast's sub-tiers cover all `package_level` rates; flag bytes increment monotonically across all sub-tiers. Single-pkg-level dashboards use 3 broadcasts × 1 sub-tier. Plugin formula: `broadcasts = (subCount == 1) ? 3 : max(4, subCount + 1)`. See [`tier-definition/version-2-compact-vgs.md`](tier-definition/version-2-compact-vgs.md) for the full pattern.

- **PitHouse per-dashboard sub-tier split source** — PitHouse's Grids tier-def splits 8 channels into 5+2+1 across `pkg_level` 30/500/2000 sub-tiers, but `Data/Telemetry.json` marks all 8 as pkg=30 only. PitHouse must source the split from somewhere outside `Telemetry.json` (likely embedded in PitHouse's own dashboard catalogs). Plugin currently uses `Telemetry.json` pkg_level grouping (8+12 split for Grids); wheel still renders successfully because tier-def is internally consistent and widget binding goes through channel idx, not flag/sub-tier position. Open whether PitHouse's split is just metadata or actually changes wheel-side decoding behaviour.

- **Type02 inferred compression codes** — partially RESOLVED 2026-08-14. A `(code, width)` an inferred entry made up is worse than merely undecoded: it takes the **whole tier** down. Type02 firmware logs `TelemetryClient::onSenderReceive::TelemetryBitPackageError:type size not match` once while building the tier's bit-package model, then silently discards every value frame for that tier — so healthy channels packed alongside the bad one go dark too. Bundles `5XR0GQDB` and `1HZ4ZRH7` (both W17/CS Pro): `TrackTemp&unit=F` at `0x12`/14 shares the pkg-2000 tier with `BestLapTime` and `LastLapTime`, and all three stayed blank while every other tier rendered.
  Decoding the byte-exact PitHouse capture set gives the definitive list of `(code, width)` pairs that firmware actually receives: `0x07`/32 (132×), `0x00`/1 (100×), `0x11`/14 (95×), `0x16`/12 (91×), `0x0D`/5 (58×), `0x0F`/16 (56×), `0x02`/8 (51×), `0x04`/16 (48×), `0x13`/5 (14×), `0x0E`/10 (4×). **`0x12` appears at no width at all**, and `0x13` appears only at width 5 — so `track_temp_1` (`0x12`/14), `brake_temp_1` (`0x12`/16) and `oil_pressure_1` (`0x13`/14) were all unsound, and `0x12` was additionally registered twice at two different widths. All three now emit as `float` (`0x07`/32), the documented fallback. `0x10`/`0x11` remain as before: capture-real but not *decoded* on Type02 (widgets read 0) — a milder failure that does not kill the tier.
  Still unverified, non-colliding, no field report: `0x15` (`float_600_2`), `0x17` (`float_001`), `0x14` (`uint3`/`uint8`), `0x03` (`uint15`), `0x01` (`uint8_t`), `0x08` (`int32_t`). Note `0x17`/10 and `0x14`/4 are absent from the capture set yet render correctly in both bundles, so "uncaptured" alone does not imply broken — a code registered at a width that contradicts a captured pair does.
  Residual ambiguity: in both bundles the dead tier was also the LAST tier, so "the last tier is dropped" is not fully excluded by field data alone. It is disfavoured — the firmware's complaint names a type/size rather than a flag, the plugin declares and emits all tiers with correct per-tier byte counts, and tier arming is per (page, channel) not per tier — but a PitHouse capture of a dashboard carrying a track/brake temp would close it.

- **Plugin Telemetry.json sparse SimHub mappings** — As of 2026-04-30, `Data/Telemetry.json` contains 454 channel sectors but only ~17 have non-empty `simhub_property` / `simhub_field`. ABS/TC + 8 tyre channels mapped 2026-04-30; ~437 sectors still rely on the heuristic fallback in `DashboardProfileStore.PickCompressionForUrl` for compression and have no live SimHub data binding. Backfilling these is mechanical (most match `DataCorePlugin.GameData.<URL_suffix>` 1:1); needs a sweep + verification per dashboard.

- ~~**Plugin V0/Type02/V2 dispatch**~~ — RESOLVED 2026-04-29. Type02 firmware uses the **V2 bit-packed `7d:23` path with LEGACY N convention (N = 8+data, NOT 10+data)**. Plugin's `useV0Values = ProtocolVersion == 0` check at `TelemetrySender.cs:2257` routes to V2; `type02NConvention=false` is hard-set in the Profile setter so frames have N=14 instead of N=16. Verified byte-identical to PitHouse 2026-04-29 nebula capture, wheel renders correctly. Earlier guess that Type02 needs V0 chunked path was wrong.
