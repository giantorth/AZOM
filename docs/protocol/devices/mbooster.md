# Moza mBooster Pedals

Vibration-motor pedal product on USB-CDC PID `0x0008`. The mBooster is a
single analog pedal with a built-in vibration motor — the user picks
whether the pedal serves as throttle, brake, or clutch in the plugin
UI, and the motor is driven by host-rendered effects (ABS, Lockup,
Threshold, Engine) per the documented protocol.

**Multi-device:** the plugin supports more than one mBooster on the
same host concurrently (one each for throttle / brake / clutch is the
canonical layout). Each unit gets its own [`MozaSerialConnection`](../../../Protocol/MozaSerialConnection.cs)
under [`MBoosterDeviceController`](../../../Devices/MBoosterDeviceController.cs);
all controllers are owned by [`MozaMBoosterRegistry`](../../../Devices/MozaMBoosterRegistry.cs).

## Reference protocol

The user-supplied protocol note in
[`../../MozamBooster — Protocol Note.md`](../../MozamBooster%20—%20Protocol%20Note.md)
is the authoritative wire-format reference. It includes verified
known-good frames against real hardware captures + the host-side
synthesizer formulas the plugin reproduces verbatim (see
[`MBoosterEffectSynthesizer.cs`](../../../Devices/MBoosterEffectSynthesizer.cs)).

The plugin-side implementation diverges from the protocol note in only
two ways:

1. **No firmware-version handshake** — the note says there is none.
2. **No probe fallback** — the registry-driven discovery is the only
   path. The serial probe fallback under `MozaProbeTarget.MBooster`
   returns null because mBooster device id `0x12` collides with
   wheelbase Main + AB9 Main, so writing a discovery probe at every
   COM port is high-risk for non-mBooster peripherals.

## USB identification

| Field           | Value                                       |
|-----------------|---------------------------------------------|
| Vendor ID       | `0x346E` (Gudsen / Moza)                    |
| Product ID      | `0x0008`                                    |
| Category        | `MozaDeviceCategory.MBooster`               |
| HID match       | VID+PID (no name regex — see [`MozaHidReader.cs`](../../../Protocol/MozaHidReader.cs)) |
| Baud rate       | 115200                                      |
| Stable identity | USB device instance segment from the registry walk; fallback to the device instance ID surfaced by `HidDevice.DevicePath` — see `MBoosterDeviceController.Identity` |

## Frame shapes

mBooster uses the same Moza wire framing as the wheelbase
(`7E LEN GRP DEV PAYLOAD CHK` with `data_len` excluding group + device
and the wire-aware checksum that compensates for `0x7E` byte stuffing).
[`MozaProtocol`](../../../Protocol/MozaProtocol.cs)'s checksum +
stuffing routines handle all framing.

### Motor write — cmd `0xb1`

Built inline by [`MozaMBoosterProtocol.BuildMotorFrame`](../../../Protocol/MozaMBoosterProtocol.cs).
14 bytes pre-stuffing.

```
7e  09  24  12   b1  EF  EN  00   P1  FH  FL  AH  AL   CK
                 │   │   │   │    │   └─┴─freq u16 BE
                 │   │   │   │    └ param1 (1..255)
                 │   │   │   └ pad (0x00)
                 │   │   └ enable (0 = off, 1 = on)
                 │   └ effect type (1..4)
                 └ cmd id (0xb1)
```

**Effect IDs** (enum [`MBoosterEffectId`](../../../Protocol/MozaMBoosterProtocol.cs)):

| ID  | Name      | ParamK | Trigger condition (host-side, doc § 4) |
|-----|-----------|--------|----------------------------------------|
| `1` | ABS       | 2000   | `absActive > 0.1` from SimHub          |
| `2` | Lockup    | 2640   | Heavy brake (>0.8) + wheels < 30 % of vehicle speed (fallback: brake > 0.9 when wheel speed unavailable) |
| `3` | Threshold | 3080   | Rising edge on brake > 0.6; release at < 0.3 (hysteresis) |
| `4` | Engine    | 1000   | `rpm > 0.8 × idleRpm` — runs continuously |

**Known-good frames** (verified against the protocol note's hardware
captures — diff against these in a `SerialTrafficCapture` export to
confirm wire correctness):

```
ABS on, 22Hz, amp=0x08e8:  7e 09 24 12 b1 01 01 00 5a 1c 28 08 e8 0b
ABS off:                   7e 09 24 12 b1 01 00 00 00 00 00 00 00 7c
Lockup on, 55Hz, ramp 0:   7e 09 24 12 b1 02 01 00 30 46 66 00 00 5a
Lockup off:                7e 09 24 12 b1 02 00 00 00 00 00 00 00 7d
Engine on, 10Hz, amp=0x020c: 7e 09 24 12 b1 04 01 00 64 0c cc 02 0c ca
Engine off:                7e 09 24 12 b1 04 00 00 00 00 00 00 00 7f
```

### Keepalive

Degenerate 0-payload frame targeting device 0x12 — `7e 00 00 12 9d`.
Built by [`MozaMBoosterProtocol.BuildKeepalive`](../../../Protocol/MozaMBoosterProtocol.cs).
Emitted every ~500 ms from `MBoosterEffectWorker` regardless of
effect state. Stops being sent → motor eventually drops connection
state and may stop responding to writes.

### Disable

Same opcode with `enable = 0` and all params zeroed.
[`MozaMBoosterProtocol.BuildDisableFrame(effect)`](../../../Protocol/MozaMBoosterProtocol.cs).
Sent at every effect deactivation edge AND for all four effect IDs on
controller dispose, otherwise the last-active waveform can latch on
the motor after the port closes.

## Stream lane

All motor frames go through a single per-connection lane:

```
StreamKind.MBoosterEffect = 17
```

Per protocol note § 4 the worker emits at most one motor frame per tick
(~50 Hz), so a single coalesced lane is sufficient — the latest-wins
behaviour on writer lag is the same property the doc relies on when it
says "the motor plays the instantaneous amplitude you send".

Keepalives go via the one-shot FIFO (`MozaSerialConnection.Send`) so
they aren't coalesced.

## Effect synthesis

[`MBoosterEffectSynthesizer.cs`](../../../Devices/MBoosterEffectSynthesizer.cs)
reproduces protocol note § 4 verbatim:

| Effect    | Waveform                                                    |
|-----------|-------------------------------------------------------------|
| ABS       | `wave = 0.9 + 0.1 * sin(phase); amp = wave * intensity`      |
| Lockup    | `ramp = clamp(elapsed / 0.5, 0, 1); amp = ramp * intensity`  |
| Threshold | 5 Hz envelope: 20 ms full + 120 ms 80 % + 60 ms gap          |
| Engine    | `wave = 0.5 + 0.5 * sin(phase); amp = wave * intensity`      |

Engine intensity is clamped to 10 % at apply time (doc § 4 default
`engineScale = 0.01`, clamped to `[0, 0.1]`) — engine runs
continuously and would dominate the other effects without this cap.

## Calibration surface (experimental)

The protocol note marks the pedal-config command surface (group 35
read, group 36 write) as "likely but unverified" on mBooster firmware.
The plugin ships the full surface in
[`MozaCommandDatabase.cs`](../../../Protocol/MozaCommandDatabase.cs)
under the `mbooster-*` prefix anyway — the user opted in. The UI's
Calibration expander surfaces this as experimental with a yellow
warning.

| Command                       | Group (R/W) | CmdId | Bytes | Type  |
|-------------------------------|-------------|-------|-------|-------|
| `mbooster-throttle-dir/min/max` | 35 / 36   | 1/2/3 | 2     | int   |
| `mbooster-brake-dir/min/max`    | 35 / 36   | 4/5/6 | 2     | int   |
| `mbooster-clutch-dir/min/max`   | 35 / 36   | 7/8/9 | 2     | int   |
| `mbooster-{throttle,brake,clutch}-y1..y5` | 35 / 36 | 14-29 | 4 | float |
| `mbooster-{throttle,brake,clutch}-output` | 37 / —  | 1/2/3 | 2 | int   |
| `mbooster-brake-angle-ratio`    | 35 / 36   | 26    | 4     | float |

All targeted at device id `0x12` on the mBooster's own CDC port. The
plugin's [`MozaResponseParser`](../../../Protocol/MozaResponseParser.cs)
disambiguates from wheelbase Main / AB9 Main via the
`busHint: "mbooster"` argument set in `MBoosterDeviceController.OnConnectionMessage`.

## HID identity reconciliation

Open hardware question — first-launch behaviour will tell us:

The registry walk in [`MozaPortDiscovery`](../../../Protocol/MozaPortDiscovery.cs)
surfaces an `InstanceId` per CDC composite (e.g. `a&399b951f&0&0000`).
HidSharp's `HidDevice.DevicePath` contains a similar parent-USB
instance segment between the second and third `#` separators (e.g.
`\\?\HID#VID_346E&PID_0008&MI_02#a&399b951f&0&0002#{4d1e55b2-...}`).
The plugin's `MozaHidReader.ExtractUsbParentInstance` extracts that
segment as the HID-side identity.

If on real hardware these two identities don't match for the same
physical device, per-device settings won't pair correctly across the
HID and CDC stacks. The fallback path is port-name proximity (CDC's
COM port + HID's parent instance share a registry parent). Logs at
detect time show both identities so the user can report a mismatch.

## Source-of-truth files in this repo

- Protocol primitives — [`Protocol/MozaMBoosterProtocol.cs`](../../../Protocol/MozaMBoosterProtocol.cs)
- Effect synthesis — [`Devices/MBoosterEffectSynthesizer.cs`](../../../Devices/MBoosterEffectSynthesizer.cs)
- Settings types — [`Devices/MBoosterTypes.cs`](../../../Devices/MBoosterTypes.cs)
- Per-device controller — [`Devices/MBoosterDeviceController.cs`](../../../Devices/MBoosterDeviceController.cs)
- 50 Hz effect worker — [`Devices/MBoosterEffectWorker.cs`](../../../Devices/MBoosterEffectWorker.cs)
- Multi-device registry — [`Devices/MozaMBoosterRegistry.cs`](../../../Devices/MozaMBoosterRegistry.cs)
- HID extension — [`Protocol/MozaHidReader.cs`](../../../Protocol/MozaHidReader.cs) (`MozaHidClass.MBooster` path)
- Profile storage — [`UI/MozaProfile.cs`](../../../UI/MozaProfile.cs) (`MBoosterSettings` dict)
- UI tab — [`UI/SettingsControl.xaml`](../../../UI/SettingsControl.xaml) (`MBoosterTab`) + handlers in `SettingsControl.xaml.cs` under "mBooster tab — multi-device"
