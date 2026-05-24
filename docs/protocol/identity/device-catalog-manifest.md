# CoAP device-catalog manifest

PitHouse's SDK CoAP server exposes the connected hardware to clients
(iRacing, third-party tools) via two endpoints:

- `GET /MOZARacing/ProductDevice` → CBOR array of 16-char lowercase-hex
  device IDs (one entry per advertised device).
- `GET /MOZARacing/ProductDevice/<id>` → CBOR map manifest for that
  device, seven fields in fixed order.

Capture-verified 2026-05-23 from `iracing-pithouse-udp.pcapng`
(localhost UDP 40266↔55356).

> **Related:** PitHouse exposes a **second** external API on port 40288 —
> plain CBOR-over-UDP without the CoAP wrapper — used by tools that don't
> link the MOZA SDK DLL (RallySimFans / RBR is the first confirmed
> consumer). See [`../pithouse-udp/README.md`](../pithouse-udp/README.md).
> Both protocols reach the same wheelbase EEPROM cells through the same
> `HardwareApplier` commands.

## Per-device manifest fields (fixed order)

| Field | CBOR type | Width | Notes |
|---|---|---|---|
| `appVersion` | text | variable | Firmware version, e.g. `1.2.9.16`. Empty if unknown. |
| `hardwareVersion` | text | variable | HW revision string, e.g. `RS21-D05-HW BM-CU-V10`. |
| `id` | text | 16 chars | Lower-case hex. Synthesised — see derivation below. |
| `mcuUid` | text | 12 chars | First 6 bytes of the STM32 12-byte UID, lower-case hex. |
| `parentId` | text | 12 chars | First 12 chars of the parent device's `mcuUid` (NOT the parent's 16-char `id`). Motor self-references. |
| `productName` | text | variable | Friendly name, e.g. `R5 Black # MOT-1-V03`. |
| `productType` | text | variable | One of six fixed strings — see table below. |

Field order matters: PitHouse emits them in the order above and clients
that compare manifests against cached copies will diff on reordering.
The plugin's [`Sdk/DeviceCatalog.cs`](../../../Sdk/DeviceCatalog.cs)
`ToCborEntries` method matches the order byte-for-byte.

## Product types (wire vocabulary)

| `productType` | Source device | Notes |
|---|---|---|
| `Motor` | dev `0x13` base | Bus root. `parentId` self-references (same 12-char prefix as its own `mcuUid`). |
| `Wheel Base` | dev `0x13` base (alias of Motor) | Same physical MCU as Motor — same `mcuUid`, same `productName`, different `id`. PitHouse synthesises distinct IDs for the two aliases; the plugin matches by adding ASCII suffixes to the SHA-1 input (`"motor"` / `"wheelbase"`). |
| `Steering Wheel` | dev `0x17` wheel | `parentId` = Motor's `mcuUid` prefix. |
| `Display` | dev within wheel (display sub-device) | `parentId` = Motor's `mcuUid` prefix. Wire string is `Display`, **not** `Display Screen` (the pre-2026-05-23 SDK guess). |
| `Pedals` | dev `0x19` pedals | Not yet probed by the plugin — placeholder for future work when `PedalsMcuUid` parsing lands. |
| `Handbrake` | dev `0x1B` handbrake | Not yet probed by the plugin — placeholder for future work when `HandbrakeMcuUid` parsing lands. |

Related: [`hub-base-cascade.md`](hub-base-cascade.md) — dev `0x12` and
dev `0x13` answer the same probe cascade with byte-identical values on
R9/R12 firmware. The Motor / Wheel Base manifest split here is at the
SDK-catalog layer, not the wire-identity layer.

## Device-ID derivation

PitHouse's exact algorithm is not yet known. The plugin uses:

- `id = SHA-1(mcuUid)[0..8]` (16 hex chars) for single-device entries
  (Steering Wheel, Display).
- `id = SHA-1(mcuUid || ASCII suffix)[0..8]` for the Motor / Wheel Base
  pair, where the suffix is `"motor"` / `"wheelbase"` respectively. This
  yields distinct IDs from the same physical MCU UID without inventing
  a second UID.

The plugin's IDs are NOT byte-compatible with PitHouse's (PitHouse uses
a different derivation we haven't decoded). iRacing only requires that
IDs are unique within a session and resolve correctly to a manifest;
both properties hold under SHA-1 derivation.

## ES-wheel topology

ES wheels put the wheel and base on the same physical MCU at dev
`0x13`. The plugin's base-identity probes and wheel-identity probes
then read the same UID. When `BaseMcuUid` is byte-equal to
`WheelMcuUid`, [`DeviceCatalog.IsEsWheelTopology`](../../../Sdk/DeviceCatalog.cs)
returns `true` and the catalogue suppresses the Motor and Wheel Base
entries — only the Steering Wheel is advertised.

## Cold-start sequence (iRacing client)

When iRacing connects to a running CoAP server, the observed sequence
is (from frames 5972–6316 of `coldstart-iracing-pithouse.pcapng`):

1. `GET /MOZARacing/ProductDevice` (Observe register, MID seq).
2. Response: CBOR array of six device IDs.
3. For each ID: `GET /MOZARacing/ProductDevice/<id>` (one per device,
   serial).
4. Response: CBOR manifest map per device.
5. `POST /MOZARacing/SdkState` — capability probe. The wheel
   intentionally returns `4.04 Not Found`; this is **expected** and
   load-bearing.
6. For the active (primary) device: GETs for `/LimitAngle`,
   `/FfbStrength`, then one-shot POSTs for `/Feedforward`,
   `/HighFrequencyTorque`, `/SetMotorRunState` (partner-API capability
   probes — see
   [`../devices/wheelbase-0x13.md`](../devices/wheelbase-0x13.md) for
   the serial-layer mapping of those three).
7. Steady-state polling.

If the device list is empty, or if a manifest has empty
`productName` / `productType`, iRacing stops after step 4 and never
issues steps 5–7. The plugin's failure mode in the pre-fix state
(2026-05-23) was step 4 returning a single `Display` entry with empty
metadata — iRacing then degraded to 3-second CoAP pings for the rest
of the session.
