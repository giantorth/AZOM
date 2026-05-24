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
| `0x001E` | Shifter     | HGP                           | unconfirmed |
| `0x001F` | Handbrake   | HBP                           | unconfirmed |
| `0x0020` | Hub         | Universal HUB                 | confirmed   |
| `0x0025` | Dashboard   | CM2 Racing Dash               | confirmed   |
| `0x1000` | Ab9         | AB9 active shifter            | confirmed   |

PID `0x0006` is reported by Windows as USB string `"MOZA R12 Base"`
(see [`../../../usb-capture/USB-device-tree-view-infos.txt`](../../../usb-capture/USB-device-tree-view-infos.txt)).
PID `0x0002` is verified as R9 from the user's hardware inventory.
PID `0x0020` is verified as the Universal HUB from a user diagnostics
bundle: the host enumerates only the hub's CDC composite (no wheelbase
PID), so any wheel attached behind the hub (e.g. KS Pro) reaches the
plugin through the hub's serial pipe rather than a separate wheelbase
CDC device.

## Categories

| Category    | Connection class                                                          | Probe target                                                          |
|-------------|---------------------------------------------------------------------------|-----------------------------------------------------------------------|
| `Wheelbase` | [`MozaSerialConnection`](../../../Protocol/MozaSerialConnection.cs) wired in [`MozaPlugin.cs`](../../../MozaPlugin.cs) | `MozaProbeTarget.BaseAndHub` — base probe (group `0x2B`) + hub probe (group `0x64`) |
| `Ab9`       | [`MozaAb9DeviceManager`](../../../Devices/MozaAb9DeviceManager.cs)        | `MozaProbeTarget.Ab9` — identity probe (group `0x09` dev `0x12`, accepts `0x89` response) |
| `MBooster`  | [`MBoosterDeviceController`](../../../Devices/MBoosterDeviceController.cs) (multi-device under [`MozaMBoosterRegistry`](../../../Devices/MozaMBoosterRegistry.cs)) | `MozaProbeTarget.MBooster` — registry-only by design; mBooster has no application handshake (see [`mbooster.md`](mbooster.md)) so probe fallback is a no-op |
| `Pedals`    | *(none — plugin currently does not open pedals over the CDC pipe)*        | *(none — wheelbase filter skips this category)*                       |
| `Shifter`   | *(none yet — placeholder for HGP/sequential CDC traffic)*                 | *(none)*                                                              |
| `Handbrake` | *(none yet — placeholder for HBP CDC traffic)*                            | *(none)*                                                              |
| `Hub`       | Same `MozaSerialConnection` instance as `Wheelbase` (the wheelbase filter admits hub PIDs) | `MozaProbeTarget.BaseAndHub` — registry direct-claims the hub port without re-probing; the post-session `0xE4` reply from `hub-port1-power` calls `MarkHubDetected()` to set `HubProbeSucceeded` for [`TelemetrySender`](../../../Telemetry/TelemetrySender.cs)'s 5-slot enumeration burst |
| `Dashboard` | Same `MozaSerialConnection` instance as `Wheelbase` (the wheelbase filter admits dashboard PIDs) | Registry direct-claims the dashboard port; dashboard presence is confirmed from the USB PID itself (no wheelbase relay required), and screen telemetry / config writes address `dev_id=0x12` (CM2 bridge/main) instead of `0x14` |
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
