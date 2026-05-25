### Display sub-device response table (wrapped in 0x43)

Display sub-device identity responses are routed through the main wheel's 0x43 group. The **wrapped response** arrives as `0xC3 0x71 [inner_response_byte] [inner_payload...]` where the inner byte is the toggled-group response of the original identity probe (0x02 → 0x82, 0x04 → 0x84, etc.). Parser must unwrap the outer 0x43/C3 frame and then decode the inner response as if it were a top-level identity reply.

Observed wrapped responses (from live sim capture, 2026-04-22; matches [`wheel-probe-sequence.md`](wheel-probe-sequence.md) inner shapes):

| Inner response | Example payload | Meaning |
|----------------|-----------------|---------|
| `0x89 00 01` | presence reply | sub-device count = 1 |
| `0x82 02` | product type = 2 | |
| `0x84 01 02 08 06` | device type reply | byte 2 = `0x08` = display |
| `0x85 01 02 00 00` | capabilities | display has no caps |
| `0x86 <12B>` | hardware ID | 12-byte STM32 MCU UID for the display controller |
| `0x87 0x01 "<ASCII>"` | model name | `"Display"` |
| `0x88 0x01 "<ASCII>"` | HW version | e.g. `RS21-W08-HW SM-D` |
| `0x8F 0x01 "<ASCII>"` | FW version | e.g. `RS21-W08-HW SM-D` |
| `0x90 0x00 "<ASCII>"` | serial number | |
| `0x91 0x04 0x01` | identity-11 | |

Plugin mapping: `MozaResponseParser.ParseDisplayIdentity()` decodes each inner response and returns a `ParsedResponse` with a `display-*` command name (`display-model-name`, `display-hw-version`, etc.). `MozaData` stores them in `Display*` fields distinct from the base wheel's identity fields.

### Display sub-device (inside VGS wheel)

During dashboard upload, Pithouse runs same probe against **Display** sub-module inside wheel (routed via `0x43` frames). Distinct identity:

| Field | VGS (wheel) | Display (sub-module) |
|-------|-------------|---------------------|
| Model (0x07) | `VGS` | `Display` |
| HW version (0x08/01) | `RS21-W08-HW SM-C` | `RS21-W08-HW SM-D` |
| HW revision (0x08/02) | `U-V12` | `U-V14` |
| Caps (0x05) | `01 02 1f 01` | `01 02 00 00` |
| Type (0x04) byte 2 | `04` | `08` |
| Serial | (differs) | (differs) |

SM-C/SM-D suffix distinguishes main controller from display controller. Display has no capability flags.

**Timing — cold start (wheel attached at host start):** Pithouse probes Display at ~t=9.97s — AFTER telemetry starts (t=9.88). On a cold start the display is already powered up when the host begins probing, and the response arrives within ~100 ms. Not a prerequisite for telemetry on this path.

**Timing — hot-attach (wheel attached to running host):** The display sub-device boots **substantially later than the wheel MCU**. Verified W17 capture 2026-05-25: wheel MCU answered `wheel-telemetry-mode` reads at t=0 (the moment the wheel was physically clipped onto the wheelbase); the display sub-device did not respond to the `0x43` identity probe burst until t≈20 s. During the intervening window the wheel ignores host session-open frames (`7c:00 type=0x81` on sess=0x01/0x02) — the wheel acks them on the wire only after the display has finished booting. A host that fires the session pipeline on wheel-MCU detection alone (before the display is up) gets neither fc:00 acks nor a channel-catalog push, and the dashboard layout renders locally on the wheel with every channel stuck at zero until SimHub restarts.

**Plugin gate:** `DashboardBindingCoordinator.StartTelemetryIfReady` defers `TelemetrySender.Start()` until `MozaPlugin.IsDisplayDetected == true` for any wheel where `WheelModelInfo.HasDisplay != false`. `DeviceProber`'s `display-model-name` case calls `StartTelemetryIfReady` again once the identity response decodes, so the deferral is self-recovering. `PollStatus` re-issues `SendDisplayProbe()` every 5 s while the wheel is detected but the display isn't, so a probe lost to USB jitter doesn't permanently wedge the gate. Screenless wheels (`HasDisplay == false`) skip the gate entirely via `ShouldDriveDashboard()`. Standalone CM2 dashboards skip it too — they ARE the dashboard target, no separate sub-device boot.

**Plugin probe sequence** (from `moza-startup.json` 2026-04-12):

| Step | Frame | Response | Description |
|------|-------|----------|-------------|
| 1 | `7E 01 43 17 00 [cs]` | `80` | Heartbeat/ping |
| 2 | `7E 01 43 17 09 [cs]` | `89 00 01` | Presence check (1 sub-device) |
| 3 | `7E 05 43 17 04 00 00 00 00 [cs]` | `84 01 02 08 06` | Hardware ID |
| 4 | `7E 01 43 17 06 [cs]` | `86` + 13 bytes | Serial number |
| 5 | `7E 02 43 17 02 00 [cs]` | `82 02` | Product type |
| 6 | `7E 05 43 17 05 00 00 00 00 [cs]` | (version data) | Firmware query |
| 7 | `7E 02 43 17 07 01 [cs]` | `87 01 "Display"` | **Model name** |
| 8 | `7E 02 43 17 0F 01 [cs]` | `8F 01 "RS21-W08-HW SM-D"` | FW version part 1 |
| 9 | `7E 02 43 17 08 01 [cs]` | `88 01 "RS21-W08-HW SM-D"` | HW version part 1 |
| 10 | `7E 02 43 17 0F 02 [cs]` | `8F 02 "U-V14"` | FW version part 2 |

Plugin sends steps 1-10 during preamble. `0x87` response with model "Display" sets `DisplayDetected=true`, gates dashboard telemetry features in UI — wheels without screen (e.g. CS V2.1) won't respond.
