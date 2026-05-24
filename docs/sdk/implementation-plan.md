# MOZA SDK Emulation — Implementation Plan & Status

Companion doc to `feasibility.md`, `api-inventory.md`, `native-io-model.md`, `csharp-wrapper.md`. Tracks the SDK-emulation feature from initial design through current status. Not a dated findings file — update in place as the work progresses.

## Status at a glance (2026-05-20)

- **Phase 1 code-complete.** All 7 implementation streams shipped + deployed to all five SimHub instances via `/deploy-all`. Build clean (0 errors). 12 self-test suites pass under real .NET Framework 4.8 in a Proton prefix.
- **End-to-end with the vendor SDK does NOT work yet under Wine.** The SDK returns `NODEVICES` from every settings call and our plugin's CoAP server receives no traffic from it — even with the master toggle on, real MOZA hardware attached, and the bound port free. Original investigation (2026-05-20, plugin bound to 5683) narrowed the cause to "SDK bails out of `installMozaSDK` very early in this environment, before any network or HID activity." Since then the plugin has been moved to UDP 40266 to match the SDK's hardcoded target (see B-1) but the Wine behavior gap (B-2) remains the open item.
- **Independent CoAP-server validation: ✅** A Linux-side hand-crafted CoAP client GET against our running plugin returned a well-formed 2.05 Content + CBOR array (empty device list, correct for no-hardware-on-this-test-box). Wire format and parser are sound.

## Context

`docs/sdk/feasibility.md` documents how the vendor MOZA SDK (`MOZA_SDK.dll`) talks to the wheel: every settings call rides **CoAP-over-UDP-localhost** to a PitHouse-hosted server; HID input rides plain Win32 HID; FFB effects ride DirectInput8 to the wheel's HID-FFB endpoint. None of those three channels touches the CDC-serial pipe the plugin owns.

The shim approach is therefore *not* a `MOZA_SDK.dll` replacement. The plugin stands up a CoAP server that mirrors PitHouse's URI tree, and a stub `MOZA Pit House.exe` keeps `installMozaSDK()` from spawning the real PitHouse (which would seize the CDC pipe and kill the plugin's connection per `feedback_plugin_pithouse_exclusive`).

User decisions captured during planning:
- **Phase 1 scope:** full URI tree fan-out in the first cut.
- **DirectInput FFB effects:** out of scope (SDK drives those direct-to-wheel; no plugin involvement).
- **Managed C# shim (`MOZA_API_CSharp.dll` drop-in):** Phase 2.
- **PitHouse stub strategy:** process-name impersonation, with the entire SDK feature behind a master ON/OFF toggle. While ON, the stub stays running continuously.

## Wire-format facts (from the captured PitHouse↔iRacing CoAP session)

Source capture: `~/Downloads/test-connection-moza-iracing.pcapng.gz` and identical copy at `usb-capture/iRacing/network-test-connection-moza-iracing.pcapng.gz`. The capture window starts at t=0 with PitHouse and the SDK consumer **already running** — so it shows steady-state, not the install handshake. Discovery still not visible on the wire.

**Transport.** UDP loopback. PitHouse's CoAP server listens on **40266** — not the well-known 5683. Initially this looked ephemeral (and the SDK's source port at 59378 in this capture is in fact ephemeral, as any client's would be), but subsequent disassembly of `MOZA_SDK.dll` shows the destination port is hardcoded: `0x9D4A` is loaded into `dx` at the libcoap address-setup call in both the 1.0.1.8 build and iRacing's customized variant. The SDK does not discover it; PitHouse must bind 40266 or the SDK won't reach the server. (This is why the literal isn't found by string search of the binary — it's a u16 register immediate, not an ASCII string.)

**URI tree** — three-level hierarchy:

```
/MOZARacing/ProductDevice                                  → CBOR array of device IDs
/MOZARacing/ProductDevice/<8-byte-hex-id>                  → CBOR device manifest (map)
/MOZARacing/ProductDevice/<8-byte-hex-id>/<Property>       → scalar (ASCII text) or CBOR (structured)
/MOZARacing/SdkState                                        → POST-only lifecycle/keepalive
```

Device IDs are 16-char lowercase hex strings (8 bytes; derived from MCU UID + parent ID hash). The capture exposes three devices:
- `2b563ef6bab2373b` — `productName=KS`, `productType=Steering Wheel`, `hardwareVersion=RS21-W04-HW SM-CU-V04B`, `appVersion=1.2.7.2`, `mcuUid=350e75ef7e7b`, `parentId=210e35dbdef4`.
- `3ba09ee5a15befd5` — the wheelbase (carries motor properties: `FfbStrength`, `LimitAngle`, `Feedforward`, `HighFrequencyTorque`, `SetMotorRunState`).
- `f4261ce19b2fdcae` — third device (likely dashboard/screen).

**Per-device manifest CBOR** — schema confirmed verbatim from the capture:
```cbor
{ "appVersion":      "1.2.7.2",
  "hardwareVersion": "RS21-W04-HW SM-CU-V04B",
  "id":              "2b563ef6bab2373b",
  "mcuUid":          "350e75ef7e7b",
  "parentId":        "210e35dbdef4",
  "productName":     "KS",
  "productType":     "Steering Wheel" }
```

**Payload encodings:**

| Direction | Type | Content-Format | Encoding |
|---|---|---|---|
| GET response | Device list, manifests, pair/map int | `application/cbor` | CBOR array of strings or CBOR map |
| GET response | Scalar int (e.g. `/FfbStrength`) | `application/octet-stream` | **ASCII decimal text** (`"100"` for value 100) |
| POST request body | Scalar int write (e.g. `/Feedforward`, `/SdkState`) | `application/octet-stream` | **4-byte little-endian** (e.g. `64 01 00 00` = 0x164 = 356; `/SdkState` `01 00 00 00` = 1) |
| POST response | Acknowledgement | `application/octet-stream` | Empty body, 2.03 Valid code |

The asymmetry (ASCII text on read, binary on write) is unusual but matches byte-for-byte across multiple frames.

**CoAP Observe (RFC 7641)** is in use. The first GET on `/MOZARacing/ProductDevice` carries `Observe: Register (0)` — iRacing subscribes for change notifications.

**Quirks worth replicating:**
- `/MOZARacing/SdkState` returns 4.04 Not Found despite iRacing posting it repeatedly. Phase 1 reproduces this verbatim.
- `Feedforward` / `HighFrequencyTorque` / `SetMotorRunState` are NOT in the public 1.0.1.8 SDK headers (`MOZA_SDK.zip` and `MOZA_SDK(1).zip` are byte-identical — confirmed via `cmp`). They look like a MOZA–iRacing partnership API for direct torque injection. Real-game iRacing support requires implementing these end-to-end. Phase 1 accept-and-log.

## Phase 1 architecture (as shipped)

```
MozaPlugin/
├── Sdk/                                          ← new namespace
│   ├── MozaSdkCoapServer.cs                      UDP listener + dispatch + Observe registry
│   ├── Coap/
│   │   ├── CoapMessage.cs                        RFC 7252 encoder/decoder
│   │   ├── CoapOption.cs                         Uri-Path / Content-Format / Observe / Token / Block options
│   │   ├── CoapCode.cs                           2.05 Content / 2.03 Valid / 4.04 / 4.05 / 5.00
│   │   ├── CoapToken.cs                          MID + token generators
│   │   └── ObserveRegistry.cs                    Per-(endpoint, token, URI) subscription map
│   ├── Cbor/
│   │   ├── CborWriter.cs                         RFC 8949 subset
│   │   └── CborReader.cs                         RFC 8949 subset
│   ├── CoapResourceRegistry.cs                   URI → handler map with {id} placeholder
│   ├── CoapResourceHandler.cs                    Abstract base + Request/Response structs
│   ├── PitHouseStubManager.cs                    Spawn / supervise / kill the stub child via JobObject
│   ├── PayloadCodec.cs                           Scalar ASCII text vs. 4-byte LE binary helpers
│   ├── DeviceCatalog.cs                          Synthesises /ProductDevice list + manifests from MozaData
│   ├── Resources/
│   │   ├── Discovery/                            DeviceList, DeviceManifest
│   │   ├── Lifecycle/                            SdkState (4.04), SoftReboot, CenterWheel
│   │   ├── Motor/                                FfbStrength, PeakTorque, LimitAngle, EqualizerAmp, …, Feedforward, HighFrequencyTorque, SetMotorRunState
│   │   ├── Wheel/                                ShiftIndicator*, ScreenBrightness, KnobMode, …
│   │   ├── Display/                              DisplayScreenSpeedUnit, …
│   │   ├── Pedal/                                NonLinear, OutDir, Calibrate*
│   │   ├── Handbrake/
│   │   └── Shifter/                              AutoBlip* — gap returns (PITHOUSENOTREADY / NOINSTALLSDK)
│   ├── ResourceBindings.cs                       Central registration
│   └── PitHouseUdp/                              ← second PitHouse external API (port 40288, plain CBOR-over-UDP)
│       ├── MozaControlUdpServer.cs               Listener + dispatcher (mirrors MozaSdkCoapServer lifecycle)
│       ├── PitHousePacket.cs                     Envelope DTO: {Head: {PacketId, Version, ReplyPort?}, Payload}
│       ├── IPitHousePacketHandler.cs             Handler interface + PitHouseReplyContext
│       └── Handlers/
│           ├── SteerLockWriteHandler.cs          PacketId 3 — MotSetSteer_LimitAngle / MaximumAngle → base-{limit,max-angle}
│           └── SteerLockReadHandler.cs           PacketId 4 — MotGetSteer_* read-back via Head.ReplyPort
├── UI/SettingsControl.xaml + .Sdk.cs             "Third-party SDK" tab
├── UI/MozaPluginSettings.cs                      SdkEmulationEnabled / SdkCoapPort / SdkBindLoopbackOnly / ControlUdpPort
└── MozaPlugin.csproj                             Embed PitHouseStub.exe as resource

PitHouseStub/                                     ← sibling project
├── PitHouseStub.csproj                           net48, OutputType=Exe, AssemblyName="MOZA Pit House"
└── Program.cs                                    Idle stub (Phase 1 release)
```

The stub binary is embedded inside `MozaPlugin.dll` as a manifest resource (`MozaPlugin.Sdk.PitHouseStub.exe`); on toggle-on, the plugin extracts it to `%LOCALAPPDATA%\SimHub\MozaPlugin\PitHouseStub\MOZA Pit House.exe`, creates a Win32 JobObject with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, spawns suspended, assigns to the JobObject, resumes.

### Implementation streams (all merged)

1. ✅ **CoAP wire layer** (`Sdk/Coap/*` — 6 files, ~1,045 LOC). 5/5 tests pass. Includes a stable insertion sort for repeated `Uri-Path` options (Mono/wine-mono regression — `List<T>.Sort` is unstable there and was scrambling URI segments).
2. ✅ **CBOR codec** (`Sdk/Cbor/*` — 3 files, ~850 LOC). 5/5 tests pass. Byte-exact against captured PitHouse frames.
3. ✅ **PitHouseStub project + manager.** JobObject lifecycle, embedded as resource, extracted on Start.
4. ✅ **DeviceCatalog.** 6/6 tests pass. IDs = SHA-1-first-16-hex of MCU UID (placeholder derivation — vendor's algorithm unknown).
5. ✅ **UI tab** (`SettingsControl.xaml` + `.Sdk.cs` + 3 new `MozaPluginSettings` props). Live status binding; 20-row rolling request log.
6. ✅ **Resource handlers** (~85 small files across the eight `Resources/*` subdirectories). All test suites pass.
7. ✅ **CoAP server + MozaPlugin lifecycle wiring.** 5/5 server tests pass.
8. ✅ **PitHouse UDP control server (2026-05-23)** — `Sdk/PitHouseUdp/*` (5 files, ~400 LOC). Second PitHouse-equivalent external API on port 40288, plain CBOR-over-UDP (no CoAP). Used by RallySimFans (RBR) to read/write steering lock; canonical protocol reference at [`../protocol/pithouse-udp/README.md`](../protocol/pithouse-udp/README.md). Starts and stops alongside the CoAP server under the same `SdkEmulationEnabled` gate. Independent bind failures (40288 conflict vs 40266 conflict) don't take the other server down. Extension point for new PacketIds is one handler class + one `RegisterHandler` call.

## Integration test results (Wine + real wheel, 2026-05-20)

> **Note on ports:** at the time of this investigation the plugin's CoAP server was bound to UDP 5683 (libcoap default). The port has since been moved to UDP 40266 to match the hardcoded `0x9D4A` immediate in `MOZA_SDK.dll` — see B-1 below. The 5683 references in this section are accurate-as-of-2026-05-20.

Test setup:
- Wine prefix `compatdata/2825720939` (SimHub's Proton prefix on this dev box).
- Real MOZA R5 Base attached (host `lsusb` shows `346e:0004 Gudsen MOZA R5 Base`; Wine's `system.reg` has `HID\VID_346E&PID_0004` entry).
- Plugin deployed and `SdkEmulationEnabled=true` in `MozaPluginSettings.json`.
- Probe binary `CoapProbe.exe` built — net48 x86, P/Invokes through the vendor `MOZA_API_CSharp.dll` → `MOZA_API_C.dll` → `MOZA_SDK.dll`. Source at `/tmp/moza-sdk-probe/`. Calls `installMozaSDK()`, sleeps 3s (per the vendor `sdk_api_test.cc` pattern), then `EnumShifterDevices`, `setMotorFfbStrength(50)`, `setMotorPeakTorque(75)`, `CenterWheel()`, and the matching getters.
- Diagnostic build of the stub (`PitHouseStub/Program.cs`) — logs args, every byte received on stdin, heartbeats, and shutdown signals to `%LOCALAPPDATA%\SimHub\MozaPlugin\PitHouseStub\stub-trace-<pid>-<stamp>.log`.

### What we observed

1. **Plugin's CoAP server binds 127.0.0.1:5683 cleanly** under wine (`SimHubWPF.exe` + `wineserver` both visible as owners via `ss -lnpu`). Verified independently via a Linux-side hand-crafted CoAP probe (Python `socket`) — `GET /MOZARacing/ProductDevice` returns a valid 2.05 ACK with content-format=60 (CBOR) and an empty array (`0x80`), matching expectations for "no MCU UID populated yet."

2. **`installMozaSDK()` returns in ~20-100ms** — fire-and-forget. Too fast to involve any network I/O. Disassembly confirms it only allocates the `CoapClient` singleton and dispatches an I/O thread; the heavy lifting is async.

3. **The SDK locates PitHouse via process-name walk.** Found OUR stub at `C:\users\steamuser\AppData\Local\SimHub\MozaPlugin\PitHouseStub\MOZA Pit House.exe`. Confirmed in two ways:
   - Plugin log shows `[Moza] PitHouse stub exited unexpectedly with code 0` at the same moment `installMozaSDK()` runs — the SDK `TerminateProcess`-d our stub.
   - A new instance of OUR stub re-appears immediately, with args `-m --sdk` (different from the plugin's no-args spawn).

4. **The `-m --sdk` flags are hardcoded in the SDK binary** — `strings` finds the literal string `-m --sdk` in both `MOZA_SDK.dll` x64 and the 32-bit equivalent. The SDK is unambiguously passing these to whatever it finds via process-walk.

5. **The diagnostic stub captured ZERO stdin bytes.** `Console.IsInputRedirected == False`. No pipe was attached to the spawned stub's stdin. The earlier hypothesis that the SDK communicates with PitHouse via stdio is ruled out (in this Wine environment at least).

6. **`strace -f -e trace=execve,socket,bind,connect,sendto,recvfrom,sendmsg,recvmsg,setsockopt,openat,readlink` on the probe's wine process for ~3 seconds covering the entire SDK init + 9 settings calls produced 41,442 syscall lines. Of those, ZERO are `AF_INET` socket creations.** All sockets are `AF_UNIX` to `wineserver` or `/tmp/.X11-unix/X0`. The SDK never created any UDP or TCP socket during the test.

7. **The SDK never opened `\AppData\Local\MOZA Pit House\CoapClient.log`** (libcoap's log path, which would be opened on first CoAP socket setup) — confirms the CoAP layer never reached the point of socket setup.

8. **Every settings call returned `NODEVICES`** — getters AND setters (per the SDK's enumCode.h: `NODEVICES = 2`). Even `CenterWheel()` returned `NODEVICES`. The SDK's internal device map is empty and every entry point short-circuits on it.

9. **Stopping SimHub (freeing 5683) made no difference.** The SDK still returned `NODEVICES` for everything, still made no inet sockets, still never wrote to the stub's stdin. So our plugin holding 5683 is not the bottleneck.

### Reading

The SDK is failing in `installMozaSDK` (or in the I/O worker it spawns) very early — well before any HID enumeration, network I/O, file I/O, or stdio activity that would be visible to us. Plausible causes:

- A Wine ntdll/kernel32 incompatibility silently bailing out of an early init step.
- A required Win32 facility that doesn't behave the same under Wine (e.g. a particular synchronization primitive, an NT event, a section object).
- A spawned-process readiness check on the SDK side that our dumb stub never satisfies (the SDK could be polling for some piece of evidence the *real* PitHouse provides on `-m --sdk` startup — a named event, a registry key write, a file in AppData — and giving up silently).

The user's hypothesis — *the SDK binds the discovery port and expects PitHouse to call it* — is plausible but not directly observed in this test (no `bind()` syscalls during the probe window). It would still fit if the bind happens lazily on first non-NODEVICES code path that we never trigger.

### What is now definitively ruled OUT as the SDK↔PitHouse discovery channel

Searched statically in `MOZA_SDK.dll` (both x64 and x86) plus dynamic Wine test:

- **mDNS / Bonjour / dns-sd / DNS-SD-over-multicast** — zero `mdns`/`bonjour`/`dns_sd`/`dnssd`/`_*._udp.local`/`_*._tcp.local`/`224.0.0.251`/`ff02::fb` strings. No `dnsapi.dll` import.
- **UPnP / SSDP / LLMNR / NetBIOS / SLP** — zero matching strings.
- **CoAP all-nodes multicast (RFC 7252 § 8)** — no `224.0.1.187`, no `FF0X:0:0:0:0:0:0:FD`. libcoap's multicast strings ARE present (statically linked) but the all-CoAP-nodes group address isn't, so the SDK isn't sending multicast discovery requests by default.
- **`coap://` URI parsing** — the literal scheme string is absent. The SDK builds CoAP addresses programmatically, not via libcoap's URI parser.
- **Registry value carrying a port** — per user direction. The registry key `Software\MOZA\PitHouse` IS read by the SDK (the imports for `RegOpenKeyExA`/`RegQueryValueExA` are present, and the path string is in `.rdata`) but it's used only for `installMozaSDK()`'s install-path lookup.
- **Shared memory / named pipes / COM / RPC / WinUSB** — ruled out by import-table inspection.
- **A hardcoded port literal as ASCII** — no `5683`/`5684`/`40266` etc. as text strings.
- **`stdio` pipes** — diagnostic stub confirmed `IsInputRedirected=False` and zero bytes received.

### What IS confirmed present

- **libcoap's `COAP_DEFAULT_PORT` (5683) is in the binary as a 16-bit integer literal** — 62 BE u16 occurrences + 31 LE u16 + 10 u32 LE. These come from libcoap itself (statically linked); the SDK does NOT target 5683. The actual target port is `0x9D4A` (40266), loaded into `dx` at the SDK's libcoap address-setup call — a separate u16 immediate, present in both the 1.0.1.8 build and iRacing's customized variant.
- **The string `localhost` is referenced** — but its only code xref is inside libcoap's URI-parser default-hostname fallback, not SDK setup code.
- **`installMozaSDK` is at RVA 0x9B320** in the x64 DLL. Disassembly: allocates 0x40 bytes (the `CoapClient` singleton), stores two .rdata string pointers (at 0x180336358 and 0x180336370 — likely host + URI prefix), constructs internal state, and dispatches a worker thread targeting the function at 0x180084f20 (almost certainly `CoapClient::sendinAndReceivingLoop` per the demangled symbol in `MOZA_SDK.lib`).
- **The SDK has only `CoapClient` class symbols, not `CoapServer`.** libcoap is statically linked for UDP only (TCP/TLS/WS/WSS strings all say "not supported"; no MOZA-branded PSK so DTLS is dormant).

## Remaining work

### Blocking — must complete before iRacing acceptance

- **(B-1) SDK ↔ PitHouse discovery channel.** ✅ **Resolved.** Disassembly of `MOZA_SDK.dll` shows the destination port is hardcoded — the immediate `0x9D4A` (40266) is loaded into `dx` at the libcoap address-setup call in both the 1.0.1.8 build and iRacing's customized variant. There is no discovery channel; PitHouse simply binds 40266 and the SDK targets it directly. No `Sdk/PitHousePortAdvertiser.cs` needed — the plugin's `MozaSdkCoapServer` binds 40266 by default (`MozaPluginSettings.SdkCoapPort`).

- **(B-2) Phase-1-blocking Wine behavior gap.** The SDK doesn't appear to do anything useful in Wine even with real hardware attached. Even the simplest path — `installMozaSDK()` then a setter — never reaches the network or HID layer. With B-1 resolved (plugin binds the right port), this is now the only thing keeping the Wine integration test from going green; worth understanding even if we ultimately target Windows users.

### Required for full Phase 1 functionality

- **(R-1) HidSharp / vendor-SDK HID coexistence.** `Devices/MozaHidReader.cs:148` uses `device.TryOpen(out HidStream)` with no explicit share-mode. HidSharp's Windows backend SHOULD default to `FILE_SHARE_READ|FILE_SHARE_WRITE` so the vendor SDK's `CreateFileW`+`ReadFile` HID-input path can coexist on the same wheel HID device, but unverified. Wine registry confirms the MOZA HID device IS exposed; not yet tested whether SDK and plugin can hold it simultaneously. Effort: 30 minutes of verification + 0-4 hours fallback if conflict.

- **(R-2) Device manifest validation.** Our synthesised `DeviceCatalog` IDs use SHA-1-first-16-hex of MCU UID; real PitHouse's algorithm might differ. The CoAP response will validate as well-formed CBOR, but the SDK might cache IDs from a previous PitHouse install and reject ours. Resolve same trace as B-1.

- **(R-3) `productName` confirmation.** Capture had `productName=KS` for a W18 wheel; our `WheelModelInfo` maps W18→"KS Pro". Verify which form vendor SDKs expect.

- **(R-4) Stream-7 receive-loop log spam.** `MozaSdkCoapServer.ReceiveLoop()` catches a `SocketException 0x274c` (WSAETIMEDOUT) every ~200ms from the UDP recv timeout and logs each one as a first-chance exception via SimHub's log4net `Error` channel. Functionally harmless but the log spam masks real errors. Fix: filter on `SocketError.TimedOut` and log at Debug, not Error. ~10 minutes.

### Known gaps (settings URIs that return 4.04/4.05 today)

These return-by-design URIs need either a `MozaData` field or a `MozaCommandDatabase` command before they go live. Each logs a one-time WARN at runtime. Adding them is the standard "Adding New Device Settings" workflow in `DEVELOPMENT.md`; ~30-60 minutes per URI once the firmware command is known.

- **Motor:** `RoadSensitivity` (has command, no MozaData field), `LimitWheelSpeed` (neither), `NaturalInertiaRatio` (neither).
- **Wheel:** `ClutchPaddleCombinePos`, `JoystickHatswitchMode`, `SpeedUnit`, `TemperatureUnit`, `ScreenBrightness` (wheel-screen), `ScreenCurrentUI`. Plus `ShiftIndicatorLightRpm` GET (POST works), `ScreenUIList` POST (GET returns empty map).
- **Display:** `DisplayScreenSpeedUnit`, `DisplayScreenTemperatureUnit`, `DisplayScreenScreenCurrentUI`, `DisplayScreenScreenBrightness` POST (GET live), `DisplayScreenScreenUIList` POST.
- **Display URI prefix** — agent chose `DisplayScreen*` prefix to avoid registry collision with wheel URIs; needs Windows verification of what real PitHouse uses. Documented as a TODO in `DisplayBindings.cs`.

### Out of scope for Phase 1 — tracked for follow-up iterations

- **Partner-API CDC reverse-engineering — DONE 2026-05-23.** `Feedforward`, `HighFrequencyTorque`, `SetMotorRunState` now forward to the wheelbase as serial writes via three new `MozaCommandDatabase` entries: `base-feedforward` (grp `0x2A` cmd `0x40`), `base-high-freq-torque` (grp `0x2A` cmd `0x41`), and `base-motor-run-state` (grp `0x2C` cmd `0x01`). See [`../protocol/devices/wheelbase-0x13.md`](../protocol/devices/wheelbase-0x13.md) § Groups 0x2A and 0x2C for the byte-level mapping (CoAP LE int32 → serial BE16 in the last two payload bytes). Encoding verified against paired UDP + CDC captures (`iracing-pithouse-{udp,serial}.pcapng`) using the new `tools/correlate_coap_serial.py` and `tools/pcap_to_jsonl.py` analysis utilities. iRacing posts each partner-API URI exactly once per session as a capability probe; firmware `[INFO]param_manage.c` log echoes confirm the wheel persists each write to EEPROM (Tables 11 and 5). **Open follow-up:** confirm whether PitHouse re-writes these cells to a disabled value on iRacing exit — tracked in [`../protocol/open-questions.md`](../protocol/open-questions.md) § "Partner-API teardown on iRacing exit".
- **Phase 2:** drop-in `MOZA_API_CSharp.dll` for managed C# consumers — bypasses CoAP entirely for in-process callers. Not blocking; vendor wrapper still works through our CoAP server.
- **Phase 3 / on-demand:** native `MOZA_API_C.dll` shim, `motorMoveTo` / `motorStopMove`, H-shifter auto-blip — only if specific consumers demand them.
- **Permanently out of scope:** DirectInput FFB effect translation (the SDK drives those direct-to-wheel via HID-FFB; plugin never sees them).

## Verification gates (for the final acceptance)

1. **Build (Linux, repeatable today).** `dotnet build -c Release` builds both `MozaPlugin` and `PitHouseStub`. Embedded resource verified in deployed DLL.
2. **Stub lifecycle.** Toggle SDK on; confirm `MOZA Pit House.exe` appears in Task Manager; flip off, confirm gone; flip on, close SimHub, confirm stub dies with SimHub.
3. **CoAP server liveness.** Hand-craft a CoAP `GET coap://127.0.0.1:40266/MOZARacing/ProductDevice` (Python `socket` is enough); verify response is 2.05 with CBOR array payload. ✅ **Done — confirmed working today.** (Originally verified against port 5683 during early development; port has since moved to 40266 to match the SDK's hardcoded target.)
4. **Per-device manifest.** `GET /MOZARacing/ProductDevice/<id>`; verify CBOR manifest matches the captured schema. *Blocked on real MCU UID data populating `MozaData` — needs the wheel actually detected by the plugin.*
5. **Scalar round-trip.** GET/POST `/…/FfbStrength`; verify ASCII text on read, 4-byte LE on write, wheel hardware change. *Blocked on B-2 (Wine SDK behavior gap).*
6. **CoAP Observe.** GET with `Observe: 0`; modify value via plugin UI; verify notification. *Blocked on B-2.*
7. **End-to-end against vendor SDK.** Run a tiny console app against our plugin with SDK toggle on. *Blocked on B-2.*
8. **iRacing release acceptance.** Connect cleanly, post `Feedforward` continuously without error; no FFB through partner-API path required for release. *Blocked on B-2.*
9. **CDC coexistence.** Telemetry / dashboard / LED still work while a vendor SDK consumer is active. *Blocked on B-2.*

## Suggested order of operations

1. **B-1 is resolved.** Port is hardcoded 40266 in the SDK binary; plugin already binds it. No advertiser needed. Focus shifts to B-2.
2. Resolve B-2 — get the SDK to actually do network/HID work in Wine (or move acceptance testing to a real Windows host).
3. Run vendor `ShifterTest.cs` / `sdk_api_test.cc` against our plugin → confirms gates 4-7.
4. Run iRacing → confirms gate 8 and partial 9.
5. Pick up R-1/R-2/R-3 (HID coexistence, ID derivation, productName) — likely resolved during step 3 trace review.
6. R-4 receive-loop log spam fix — independent, quick.
7. Resource-handler gap closure as needed.
8. Begin partner-API CDC reverse-engineering iteration.

## Reference artefacts in this repo

- `usb-capture/iRacing/network-test-connection-moza-iracing.pcapng.gz` — steady-state CoAP between SDK and PitHouse. Starts AFTER discovery; useful for wire-format reference, not for discovery.
- `usb-capture/iRacing/test-with360hz-{on,off}-2.pcapng.gz` — pure USB captures of the SDK's HID-FFB push to the wheel at 360Hz (device 1.24.3, endpoints 0x03/0x83). Starting evidence for the partner-API torque path.
- `Sdk/Coap/CoapWireTests.cs`, `Sdk/Cbor/CborTests.cs`, `Sdk/CoapResourceRegistryTests.cs`, `Sdk/MozaSdkCoapServerTests.cs`, and per-resource `*Tests.cs` — invoke via `SdkSelfTestRunner.RunAll()` (currently called manually; can be wired to a Diagnostics-tab button if helpful).
- `/tmp/moza-sdk-probe/` — the wine-side probe we built today (CoapProbe.exe + the staged vendor DLLs). Useful as the regression test for the Wine integration question once B-1 lands.
