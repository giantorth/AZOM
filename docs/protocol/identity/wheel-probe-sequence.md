## Device identity & probes

> **Probe form differs by firmware** — 2026-04+ uses short-form probes only; older firmware uses sub-byte variants (`08:01`, `10:00`, etc.). See [`../FIRMWARE.md`](../FIRMWARE.md) for the firmware-era matrix.

### Wheel connection probe sequence

When wheel detected, Pithouse queries device 0x17 for identity. All identity strings are 16-byte null-padded ASCII.

Observed probe order (`connect-wheel-start-game.json`): 0x09, 0x04, 0x06, 0x02, 0x05, 0x07, 0x0F, 0x11, 0x08, 0x10.

| Group | Cmd ID | Response | Notes |
|-------|--------|----------|-------|
| 0x09 | — (n=0) | 2 bytes (e.g. `00 01`) | **Presence/ready check** — sent first. Response may indicate sub-device count |
| 0x02 | — | 1 byte (e.g. `0x02`) | Possibly protocol version |
| 0x04 | `0x00` + 3 zero bytes | 4 bytes, per-model | VGS: `01 02 04 06`; Display sub-device: `01 02 08 06`. Byte 2 may encode device type (0x04=wheel, 0x08=display) |
| 0x05 | `0x00` + 3 zero bytes | 4 bytes, per-model | Capability flags? VGS: `01 02 1f 01`; CS V2.1: `01 02 26 00`; Display: `01 02 00 00` |
| 0x06 | — (n=0) | 12 bytes | Hardware identifier. VGS: `be 49 30 02 14 71 35 04 30 30 33 37` |
| 0x07 | `0x01` | 16-byte string | **Model name** — `VGS`, `CS V2.1` |
| 0x08 | `0x01` | 16-byte string | **HW version** — `RS21-W08-HW SM-C` |
| 0x08 | `0x02` | 16-byte string | **HW revision** — `U-V12`, `U-V02` |
| 0x0F | `0x01` | 16-byte string | **FW version** — `RS21-W08-MC SW` |
| 0x10 | `0x00` | 16-byte string | **Serial number, first half** |
| 0x10 | `0x01` | 16-byte string | **Serial number, second half** |
| 0x11 | `0x04` | 2 bytes | Unknown |

Full serial = two halves concatenated (32 ASCII chars).

### ES wheel — identity at device 0x18

ES wheels are **silent at `0x17`**. Their identity is read with the same probe
shapes re-targeted at **`0x18`** (the ES wheel is a module of the wheelbase MCU):
`0x07/01 → "ES"`, `0x08/01 → "RS21-D05-HW SM-C"`, `0x0F/01 → "RS21-D05-MC WB"`,
`0x06 → <UID shared with the base>`. See
[`known-wheel-models.md`](known-wheel-models.md) § ES wheel identity for the full
device-id map.

## Plugin wheel-detection flow (new vs old/ES protocol)

The plugin locks onto a wheel from the first telemetry/RPM response, then resolves
its model. New-protocol wheels resolve from `0x17`; ES wheels resolve from `0x18`.
Both share one model-name hot-swap path.

```
                    ┌──────────────────────────────────────┐
                    │ Base detected (base-mcu-temp @ 0x13)  │
                    │  → base-* identity probes @ 0x13      │  (motor name, "…BM-C")
                    └──────────────────┬───────────────────┘
                                       │  ProbeWheelDetection: telemetry-mode +
                                       │  rpm-value1 to wheel ids {0x17,0x15,0x13}
                       ┌───────────────┴────────────────┐
                       │                                 │
       wheel-telemetry-mode (grp 64) reply      wheel-rpm-value1 reply,
       on 0x17  →  NEW PROTOCOL                  no telemetry-mode  →  OLD / ES
                       │                                 │
        NewWheelDetected=true                  OldWheelDetected=true
        LockWheelId(0x17)                      LockWheelId(responder)
                       │                                 │
        Identity reads @ 0x17:                 Identity reads:
          wheel-model-name, sw, hw,              • wheel-* @ locked id
          serial, PitHouse probe                  (returns BASE name on ES)
                       │                          • es-wheel-* @ 0x18  ◄── NEW
                       │                          • OldWheelSettingsReadCommands
                       │                          • DeployOldProtoWheel (fallback)
                       │                                 │
        case "wheel-model-name"               case "es-wheel-model-name"  ◄── NEW
        (gated NewWheelDetected)              (model = "ES" from 0x18)
                       │                                 │
                       └────────────┬────────────────────┘
                                    ▼
                 DetectWheelModelHotSwap(model)   ── shared ──
                    model changed?  ─► yes ─► ResetWheelDetection (re-detect)
                                    │ no (first sight)
                                    ▼
                 LastKnownWheelModel = model
                 WheelModelInfo = FromModelName(model)
                    │                         │
            new: SendDisplayProbe        ES: HasDisplay=false ⇒ no display probe,
                 (if HasDisplay!=false)      no session/telemetry pipeline
                 LED reads, telemetry start
                    │                         │
                 DeployForModel(model) ─► device.json + GUID
                    │                         │
            "MOZA <model>"              "MOZA ES"  (was: generic old-proto only)
                                    │
                                    ▼
                 ApplyProfile  → per-wheel page settings bind by model GUID
```

**Hot-swap triggers (both protocols):** model-name change (above), responder
device-id change, or `WheelMissThreshold` poll misses. PollStatus re-reads
`wheel-model-name` for new wheels and additionally `es-wheel-model-name` for
old/ES wheels, so a rim swap to a different model is caught either way.

**Why ES uses 0x18, not the locked-id read:** on ES the locked wheel id sits in
the base's id space, so a `wheel-model-name` read returns the *base/motor* name
(`"R5 Black # MOT-1"`). Only the `0x18` `es-wheel-model-name` read yields the
real wheel model (`"ES"`), so that is the authoritative source for ES.
