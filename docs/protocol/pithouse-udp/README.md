# PitHouse UDP control protocol (port 40288)

PitHouse exposes a **second** external API alongside the CoAP-on-UDP-40266
SDK that iRacing uses. This one is plain CBOR-over-UDP (no CoAP wrapper),
default port 40288, used today by the RallySimFans launcher (RBR) and
expected to be the standard control surface for other wheel-config tools
that don't go through the official MOZA SDK DLL. Same underlying
wheelbase EEPROM cells, different protocol skin.

Verified 2026-05-23 by decompiling `RSF_Launcher/RSFControlConfig/RSFControlConfig.dll`
(`ControlConfig.SteeringWheels.Moza_Generic`); plugin-side implementation
lives at [`Sdk/PitHouseUdp/MozaControlUdpServer.cs`](../../../Sdk/PitHouseUdp/MozaControlUdpServer.cs).

## How it differs from the CoAP SDK

| | CoAP SDK | PitHouse UDP control |
|---|---|---|
| **Doc home** | [`../identity/device-catalog-manifest.md`](../identity/device-catalog-manifest.md) | (this file) |
| **Port** | 40266 | **40288** |
| **Framing** | CoAP (CON/NON, options, tokens, Observe) | Plain UDP datagrams |
| **Addressing** | `/MOZARacing/ProductDevice/<id>/<field>` | Top-level `{Head, Payload}` map with `PacketId` discriminator |
| **Field vocabulary** | `LimitAngle`, `GameMaximumAngle` | `MotSetSteer_LimitAngle`, `MotSetSteer_MaximumAngle`, `MotGetSteer_*` |
| **Reply path** | Same UDP flow (CoAP token correlation) | Client passes its own listen port in `Head.ReplyPort`; server replies to that |
| **Device targeting** | 16-char hashed device id in URI | None — assumed single base |
| **Encoding scale** | Mostly direct ASCII text for scalar reads | CBOR unsigned int |

Both protocols write the same underlying wheelbase EEPROM cells through
the standard `HardwareApplier` commands (`base-limit` grp 0x29 cmd 0x01;
`base-max-angle` grp 0x29 cmd 0x17). PitHouse-internal state is shared
between the two interfaces by virtue of both reaching the same physical
wheel.

## Envelope

Every datagram is a CBOR map at the top level:

```cbor
{
  "Head":    { "PacketId": int, "Version": "X.Y.Z", "ReplyPort": int? },
  "Payload": object | array | absent
}
```

| Field | Type | Notes |
|---|---|---|
| `Head.PacketId` | unsigned int | Operation discriminator — see table below |
| `Head.Version` | text | Per-PacketId version tag (e.g. `"1.0.0"` for write, `"1.0.4"` for read). Plugin currently accepts any version string. |
| `Head.ReplyPort` | unsigned int? | Present on read-style requests; absent on fire-and-forget writes. Server replies to `OriginalSender.IP : ReplyPort` (NOT the source port). RSF treats `ReplyPort=0` (local bind failure) as "abort"; plugin honours the same convention. |
| `Payload` | varies | Map for writes (named fields → values), array of field-name strings for reads, absent for some PacketIds. Handler-specific. |

## PacketId catalog

| PacketId | Direction | Purpose | Payload shape | Reply | First observed |
|---|---|---|---|---|---|
| **3** | C→S | Write steer lock | `{"MotSetSteer_MaximumAngle": deg, "MotSetSteer_LimitAngle": deg}` | None (fire-and-forget) | RSF launcher |
| **4** | C→S | Read steer lock | `["MotGetSteer_MaximumAngle", "MotGetSteer_LimitAngle"]` (array of field-name strings) | `{"Payload": {"MotGetSteer_MaximumAngle": int, "MotGetSteer_LimitAngle": int}}` to `ReplyPort` | RSF launcher |

Additional PacketIds almost certainly exist — see [open questions](#open-questions)
below. Extending the protocol on the plugin side means adding one
`IPitHousePacketHandler` class in `Sdk/PitHouseUdp/Handlers/` and one
`RegisterHandler` call in `MozaControlUdpServer`'s constructor.

## Steer-lock write (PacketId 3)

Request:
```cbor
{
  "Head":    { "PacketId": 3, "Version": "1.0.0" },
  "Payload": {
    "MotSetSteer_MaximumAngle": <degrees>,
    "MotSetSteer_LimitAngle":   <degrees>
  }
}
```

- Both fields are degrees, integer.
- RSF clamps to 90–2000° per model; the plugin re-clamps in
  `SteerLockWriteHandler` so out-of-range values don't reach the
  wheelbase.
- Either field can be absent; only the present ones are forwarded.
- No reply — even errors are silent. Diagnostics surface via the
  plugin's debug log.

## Steer-lock read (PacketId 4)

Request:
```cbor
{
  "Head":    { "PacketId": 4, "Version": "1.0.4", "ReplyPort": <local-udp-port> },
  "Payload": [ "MotGetSteer_MaximumAngle", "MotGetSteer_LimitAngle" ]
}
```

Reply (UDP to `OriginalSender.IP : ReplyPort`, 5-second timeout client-side):
```cbor
{
  "Payload": {
    "MotGetSteer_MaximumAngle": <int>,
    "MotGetSteer_LimitAngle":   <int>
  }
}
```

The `Payload` array in the request lists which fields the client wants
back. RSF requests both; the plugin always returns both, even if the
client asked for only one. (RSF parses by name, so over-replying is
harmless.)

## Port discovery

The default port is `40288` but PitHouse persists the active value to:

```
%USERPROFILE%\Documents\Moza Pit House\settings.ini
[Application]
udpPort=40288
```

Third-party clients (RSF observed; presumably others) read that file to
discover the live port. The plugin's `MozaControlUdpServer` accepts a
port override at construction (`MozaPluginSettings.ControlUdpPort`,
default 40288); it does **not** currently write the settings.ini so
third-party clients reading it would see whatever a real PitHouse
install left there. Adding the write would make non-default ports
discoverable to RSF; tracked in [open questions](#open-questions).

## Known clients

| Client | What it uses | Source |
|---|---|---|
| **RallySimFans launcher** (Richard Burns Rally) | PacketId 3 + 4 (set + read steer lock for per-car ratio) | `RSFControlConfig.dll` → `ControlConfig.SteeringWheels.Moza_Generic` (decompiled) |
| **(Open)** | Other wheel-config tools likely use this surface too | Worth a survey before extending the PacketId catalog |

## Plugin implementation

| Concern | Where |
|---|---|
| Listener / dispatcher | [`Sdk/PitHouseUdp/MozaControlUdpServer.cs`](../../../Sdk/PitHouseUdp/MozaControlUdpServer.cs) |
| Envelope DTO | [`Sdk/PitHouseUdp/PitHousePacket.cs`](../../../Sdk/PitHouseUdp/PitHousePacket.cs) |
| Handler interface + reply context | [`Sdk/PitHouseUdp/IPitHousePacketHandler.cs`](../../../Sdk/PitHouseUdp/IPitHousePacketHandler.cs) |
| PacketId 3 handler (write) | [`Sdk/PitHouseUdp/Handlers/SteerLockWriteHandler.cs`](../../../Sdk/PitHouseUdp/Handlers/SteerLockWriteHandler.cs) |
| PacketId 4 handler (read) | [`Sdk/PitHouseUdp/Handlers/SteerLockReadHandler.cs`](../../../Sdk/PitHouseUdp/Handlers/SteerLockReadHandler.cs) |
| Lifecycle wiring | `MozaPlugin.Init` / `MozaPlugin.End` (started/stopped alongside the CoAP server under the `SdkEmulationEnabled` gate) |
| Port setting | `MozaPluginSettings.ControlUdpPort` (default 40288) |

Receive loop mirrors `MozaSdkCoapServer`: dedicated thread, loopback-only
bind, 200 ms receive timeout for clean `_stopRequested` polling, joined
with 1 s timeout on `Stop`.

## Open questions

These are tracked in [`../open-questions.md`](../open-questions.md) so they
don't get lost — capture-driven follow-up worth doing the next time real
PitHouse is observed handling these requests:

1. **Other PacketIds.** RSF only uses 3 and 4. PitHouse almost certainly
   handles FFB strength, profile switching, calibration triggers, etc.
   on the same port. One capture of PitHouse running with the
   configuration UI active would surface them.
2. **`Version` semantics.** Writes carry `"1.0.0"`, reads carry `"1.0.4"`.
   Does PitHouse reject unknown versions, or is the field advisory? The
   plugin currently accepts any string.
3. **`ReplyPort=0` behaviour.** RSF aborts its read when its local bind
   returns port 0; PitHouse-side reaction is untested. The plugin's
   `PitHouseReplyContext.SendReply` drops with a debug log.
4. **Settings.ini write.** Real PitHouse writes
   `%USERPROFILE%\Documents\Moza Pit House\settings.ini` so clients can
   discover the port. Our plugin doesn't. Adding the write makes
   non-default port overrides discoverable.
5. **Multi-base topology.** This protocol has no device-id field. If a
   user attaches two wheelbases, both will service the same write —
   probably wrong. Needs survey before any multi-base shipping.

## Comparison to the CoAP SDK

The two surfaces serve different consumers and have different
ergonomics:

- **CoAP SDK (40266)** — designed for high-frequency, observe-capable
  property reads. Used by iRacing for its torque pipeline and capability
  probes (Feedforward / HighFrequencyTorque / SetMotorRunState — see
  [`../devices/wheelbase-0x13.md`](../devices/wheelbase-0x13.md) §
  Groups 0x2A / 0x2C). Multi-device aware via device-id-keyed URIs.
- **PitHouse UDP control (40288)** — designed for one-shot config
  reads/writes from external wheel-config tools that don't link the
  MOZA SDK DLL. Single-base, named-field, no observe.

For the same logical operation (e.g. set wheel rotation lock), the
plugin routes both protocols through the same `HardwareApplier`
command, so the EEPROM state stays consistent regardless of which API
the client picked.
