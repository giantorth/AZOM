# USB Vendor / Product IDs

All MOZA Racing devices on the host USB bus share Vendor ID **`0x346E`**
(Gudsen Technology). The plugin uses the Product ID to decide which
connection class (if any) is allowed to claim the COM port — so a CRP
pedal set never sees wheelbase probe traffic and the AB9 shifter never
sees base/hub frames.

Canonical C# source: [`../../../Protocol/MozaUsbIds.cs`](../../../Protocol/MozaUsbIds.cs).
Update both this table and the inventory dictionary together; they are
expected to stay in sync.

## Inventory

| PID      | Category    | Devices                       | Confidence  |
|----------|-------------|-------------------------------|-------------|
| `0x0000` | Wheelbase   | R16, R21                      | confirmed   |
| `0x0001` | Pedals      | CRP, CRP2                     | confirmed   |
| `0x0002` | Wheelbase   | R9                            | confirmed   |
| `0x0003` | Pedals      | SRP                           | confirmed   |
| `0x0004` | Wheelbase   | R5                            | confirmed   |
| `0x0005` | Wheelbase   | R3                            | unconfirmed |
| `0x0006` | Wheelbase   | R12, R12v2                    | confirmed   |
| `0x0008` | MBooster    | mBooster Pedals               | confirmed   |
| `0x0011` | Pedals      | CRP2 (variant)                | confirmed   |
| `0x0012` | Wheelbase   | R9 (variant)                  | confirmed   |
| `0x0016` | Wheelbase   | R12 (variant)                 | confirmed   |
| `0x001E` | Shifter     | HGP                           | unconfirmed |
| `0x001F` | Handbrake   | HBP                           | unconfirmed |
| `0x0020` | Hub         | Universal HUB                 | confirmed   |
| `0x0023` | Shifter     | SGP                           | confirmed   |
| `0x0024` | Stalks      | MOZA Stalks                   | confirmed   |
| `0x0025` | Dashboard   | CM2 Racing Dash               | confirmed   |
| `0x1000` | Ab9         | AB9 active shifter            | confirmed   |
| `0x1002` | Ab9         | AB6 active shifter            | confirmed   |

PID `0x0006` is reported by Windows as USB string `"MOZA R12 Base"`
(see [`../../../usb-capture/USB-device-tree-view-infos.txt`](../../../usb-capture/USB-device-tree-view-infos.txt)).
PID `0x0002` is verified as R9 from the user's hardware inventory.

Some devices enumerate a `+0x0010` high-nibble **variant** PID (same
device class, a different firmware/hardware revision): `0x0002` R9 →
`0x0012`, `0x0006` R12 → `0x0016`, `0x0001` CRP2 → `0x0011`. The R12
(`0x0016`) and CRP2 (`0x0011`) variants were reported from a user with
an R12 base on `0x0016` and CRP-2 pedals on `0x0011`; before they were
registered, both unknown PIDs fell into the wheelbase/AB9 "unknown PID"
probe fallback, so the pedal port received wheelbase probe traffic and
base detection was ambiguous.
PID `0x1002` is the **AB6** active shifter, from a user diagnostics bundle
whose host enumerated it alongside an R21 (`0x0000`). The device names
itself: its HID product string is `MOZA AB6 FFB Base`, so the PID↔device
mapping is confirmed the same way `0x0006` is. What is *not* yet verified
is the CDC protocol — it is registered in category `Ab9` and driven by that
lane on the assumption of AB9 parity, and no AB6 capture exists (the
reporter had active-shifter detection disabled, so the port was never
opened). See [`ab9-shifter.md`](ab9-shifter.md) § AB6 sibling.
PID `0x0020` is verified as the Universal HUB from a user diagnostics
bundle: the host enumerates only the hub's CDC composite (no wheelbase
PID), so any wheel attached behind the hub (e.g. KS Pro) reaches the
plugin through the hub's serial pipe rather than a separate wheelbase
CDC device.

## Categories

| Category    | Connection class                                                          | Probe target                                                          |
|-------------|---------------------------------------------------------------------------|-----------------------------------------------------------------------|
| `Wheelbase` | [`MozaSerialConnection`](../../../Protocol/MozaSerialConnection.cs) wired in [`MozaPlugin.cs`](../../../MozaPlugin.cs) | `MozaProbeTarget.BaseAndHub` — base probe (group `0x2B`) + hub probe (group `0x64`) |
| `Ab9`       | [`MozaAb9DeviceManager`](../../../Devices/MozaAb9DeviceManager.cs) — one shared lane for the AB9 (`0x1000`) and AB6 (`0x1002`); with both attached only the first-enumerated one is claimed | `MozaProbeTarget.Ab9` — identity probe (group `0x09` dev `0x12`, accepts `0x89` response) |
| `MBooster`  | [`MBoosterDeviceController`](../../../Devices/MBooster/MBoosterDeviceController.cs) (multi-device under [`MozaMBoosterRegistry`](../../../Devices/MBooster/MozaMBoosterRegistry.cs)) | `MozaProbeTarget.MBooster` — registry-only by design; mBooster has no application handshake (see [`mbooster.md`](mbooster.md)) so probe fallback is a no-op |
| `Pedals`    | Pedals (`dev 0x19`) are enumerated as a sub-device on whichever CDC pipe carries them — the wheelbase pipe (base-attached) or the dedicated `Hub` pipe (hub-attached, e.g. a base model with no pedal port). The owning pipe is recorded in `DeviceDetectionState.PedalsOwner` so settings reads + calibration writes route to it. (Pedals are never opened as their OWN dedicated CDC connection.) | *(no dedicated probe target — found via presence probe `0x00 0x19` on the base and hub pipes)* |
| `Shifter`   | Standalone-USB HGP (`0x001E`) / SGP (`0x0023`): a dedicated [`StandalonePeripheralController`](../../../Devices/StandalonePeripheralController.cs) lane per port (registry-only), addressed as root `0x12`. A shifter behind a base/hub is instead a `0x1A` sub-device on that pipe (found by presence probe `0x00 0x1A` on the base + hub pipes, recorded in `DeviceDetectionState.ShifterOwner`). | `MozaProbeTarget.ShifterOnly` (dormant; probe fallback force-disabled) |
| `Handbrake` | Same model as `Pedals`: handbrake (`dev 0x1B`) is enumerated on whichever pipe carries it (base or hub), with the owning pipe recorded in `DeviceDetectionState.HandbrakeOwner` for routed reads/writes. | *(no dedicated probe target — presence probe `0x00 0x1B` on the base and hub pipes)* |
| `Stalks`    | *(none — recognised so neither wheelbase nor AB9 probes it; the plugin reads it over **HID only**, see [`../../../Protocol/MozaHidReader.cs`](../../../Protocol/MozaHidReader.cs))* | *(none — but the device **does** have a CDC surface: identity groups, heartbeat, `0x0E`, and a ~1 Hz group `0x4C` read. See [`../open-questions.md`](../open-questions.md) § Group `0x4C`)* |
| `Hub`       | **Hub-only (no base):** the primary `Wheelbase` `MozaSerialConnection` claims the hub port (its filter admits hub PIDs) and runs the full wheel/session/telemetry pipeline there. **Base + hub both present:** the base stays the primary (the registry walk prefers a `Wheelbase`-category port) and a dedicated [`MozaHubDeviceManager`](../../../Devices/MozaHubDeviceManager.cs) claims the hub port to enumerate its peripherals (pedals/handbrake/port-power) in parallel. The `_activePorts` guard stops the dedicated connection from re-opening a hub the primary already holds. | Primary: `MozaProbeTarget.BaseAndHub`. Dedicated: `MozaProbeTarget.HubOnly` (single `0x64` hub probe), registry-only (probe fallback force-disabled). The post-session `0xE4` reply from `hub-port1-power` calls `MarkHubDetected()` to set `HubProbeSucceeded` for [`TelemetrySender`](../../../Telemetry/TelemetrySender.cs)'s 5-slot enumeration burst (primary pipe only). |
| `Dashboard` | Dedicated `MozaSerialConnection` filtered to dashboard PIDs only (`0x0025`), separate from the wheelbase connection so a standalone CM2 works alongside a base | Registry direct-claims the dashboard port by PID (no probe scan); screen telemetry / config writes address `dev_id=0x12` (CM2 bridge/main). A CM2 *behind* a wheelbase has no own port and is the dash sub-device at `dev_id=0x14` on the wheelbase connection. |
| `Unknown`   | Both `Wheelbase` and `Ab9` connections accept unknown PIDs as fallback    | Each runs its own probe; the first matching response wins             |

## Discovery path

`MozaPortDiscovery` walks the registry under
```
HKLM\SYSTEM\CurrentControlSet\Enum\USB\VID_346E&PID_xxxx&MI_00\<instance>\Device Parameters\PortName
```
filtering to instances whose `Service` value is `usbser` (the standard
Windows CDC ACM driver). Live COM ports are cross-checked against
`SerialPort.GetPortNames()` so ghost registry entries are dropped.
Implementation: [`../../../Protocol/MozaPortDiscovery.cs`](../../../Protocol/MozaPortDiscovery.cs) — VID at line 32, driver filter at line 194.

The same filter applies to the serial-probe fallback in
[`MozaSerialConnection`](../../../Protocol/MozaSerialConnection.cs) that
runs when the registry didn't already hand out a matching port
(Wine/Proton without USB enumeration, missing driver, fresh Windows
install, or a partially-enumerated bus where some MOZA devices register
and others don't). The fallback also consults the registry per port:
ports the registry already pinned to a non-matching MOZA PID are
skipped without serial bytes, and ports pinned to a matching PID are
claimed directly. Only unclassified ports actually receive probe
writes. Both paths use the per-connection PID filter lambdas in
`MozaPlugin.cs` and `MozaAb9DeviceManager.cs`.

## Hub-on-wheelbase-pipe

The Universal HUB (`0x0020`) is admitted by the wheelbase pipe's PID
filter even though it is its own category. Two reasons:

* `MozaProbeTarget.BaseAndHub` includes a dedicated hub probe (group
  `0x64`, dev `0x12`, cmd `0x03`); the wheelbase pipe is the only place
  in the codebase that knows how to talk to a hub.
* Wheels like the KS Pro reach the host through the Universal HUB's
  CDC pipe rather than a separate wheelbase USB device — when a user
  has hub-attached hardware, the hub's COM port is the *only* MOZA port
  the registry sees, so dropping it leaves the plugin with nothing to
  talk to.

`MozaPortDiscovery` registry-claims hub ports without re-probing; the
post-session `0xE4` reply from `hub-port1-power` then calls
`MozaSerialConnection.MarkHubDetected()` and `TelemetrySender` fires
its 5-slot enumeration burst.

## Unknown-PID fallback

When a Moza-VID device with a PID not in the inventory shows up, both
the wheelbase and AB9 connections accept it as a probe candidate. Each
runs its protocol-specific identity probe and only the matching one
holds the port. The AB9 probe pre-flights with a base-disambiguation
check so it cannot mis-claim a wheelbase port.

`MozaPortDiscovery` logs a single Info-level line the first time it
sees each unknown PID per process lifetime:
```
[Moza] Unknown Moza PID 0xXXXX on COMn — not in usb-ids inventory.
Will be probed with every known protocol; please report so
docs/protocol/devices/usb-ids.md can be updated.
```

## Legacy constant name caveat

[`MozaUsbIds.cs`](../../../Protocol/MozaUsbIds.cs) defines
`PidWheelbaseR9 = "0x0006"` and `PidWheelbaseR12 = "0x0002"`. The
R9/R12 suffixes are **mis-labelled** relative to the hardware mapping
in the table above (R9 is `0x0002`, R12 is `0x0006`). Renaming would
churn diffs without changing behaviour because `IsWheelbasePid` accepts
both PIDs unconditionally. The names are preserved as-is for diff
hygiene; new code should prefer `MozaUsbIds.Categorize(pid)` or the
neutral constants (`PidWheelbaseR16R21`, `PidWheelbaseR5`,
`PidWheelbaseR3`).
