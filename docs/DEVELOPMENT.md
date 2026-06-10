# Development Guide

SimHub plugin for MOZA Racing hardware providing two-way telemetry: streams game data (speed, RPM, gear, lap times, fuel, tyre wear, etc.) to the wheel dashboard display, drives wheel/dashboard RPM and flag LEDs, and allows configuring wheelbase settings. Also supports standalone USB dashboards (CM2 Racing Dash, PID `0x0025`) that connect without a wheelbase. Uses a custom binary serial protocol reverse-engineered from the [boxflat](https://github.com/Lawstorant/boxflat) project; wire-level protocol reference lives under [`docs/protocol/`](protocol/).

Contents:

1. [Building from Source](#building-from-source)
2. [Repository Map](#repository-map)
3. [Architecture](#architecture)
4. [Component Reference](#component-reference)
5. [Subsystem Deep-Dives](#subsystem-deep-dives)
6. [How-To Workflows](#how-to-workflows)
7. [Key Protocol Details](#key-protocol-details)
8. [Dependencies](#dependencies)

## Building from Source

The project targets .NET Framework 4.8 (x86) and uses the `Microsoft.NETFramework.ReferenceAssemblies.net48` NuGet package so it can cross-compile on Linux without Mono. The built DLL runs on Windows under SimHub.

### Building on Windows

Prerequisites: [VS Code](https://code.visualstudio.com/) with the [C# Dev Kit](https://marketplace.visualstudio.com/items?itemName=ms-dotnettools.csdevkit) extension, .NET SDK 8.0+ ([download](https://dotnet.microsoft.com/download)).

1. Open the project folder in VS Code.
2. Build from the terminal:

   ```
   dotnet build -c Release
   ```

3. Copy `bin/x86/Release/MozaPlugin.dll` into your SimHub installation directory. Or set the `SIMHUB_PATH` environment variable to have it copied automatically on build:

   ```
   set SIMHUB_PATH=C:\Program Files (x86)\SimHub
   dotnet build -c Release
   ```

   PowerShell:
   ```powershell
   $env:SIMHUB_PATH = "C:\Program Files (x86)\SimHub"
   dotnet build -c Release
   ```

4. Restart SimHub. The plugin appears under Settings > Plugins as "AZOM".

### Cross-Compiling on Linux

The .NET SDK can target .NET Framework 4.8 using the `Microsoft.NETFramework.ReferenceAssemblies.net48` NuGet package (already included in the `.csproj`).

1. Install the .NET SDK:

   ```bash
   # Arch Linux
   sudo pacman -S dotnet-sdk

   # Ubuntu/Debian
   sudo apt install dotnet-sdk-8.0

   # Fedora
   sudo dnf install dotnet-sdk-8.0
   ```

2. Build:

   ```bash
   dotnet build -c Release
   ```

3. Copy `bin/x86/Release/MozaPlugin.dll` to your Windows SimHub installation (scp, shared folder, USB drive, etc.) and restart SimHub.

Notes:

- The reference-assemblies package means you do **not** need Mono or Windows installed.
- SimHub DLLs in `libs/SimHub/` are reference-only (`Private=false`) and not copied to output.
- The build produces a single output DLL with no additional runtime dependencies to deploy (locales are embedded — see [i18n](#internationalization-i18n)).

### CI/CD

- **Build**: Every push to `main` and every PR is built automatically via GitHub Actions.
- **Dev pre-release**: Every push to `dev` builds a Release and publishes a GitHub pre-release (a per-commit `dev-<sha>` tag plus a rolling `dev-latest` tag) — this is where active-development builds come from.
- **Release**: Pushing a `v*` tag (e.g., `v0.2.0`) builds a Release, generates a changelog, and publishes a GitHub Release with the DLL (device definitions are embedded in the DLL).
- **SimHub dependency updates**: A daily workflow checks for new SimHub releases and creates a PR to update `libs/SimHub/`.

## Repository Map

| Path | Contents |
|---|---|
| `MozaPlugin.cs` | Plugin entry point / orchestrator (`IPlugin`, `IDataPlugin`, `IWPFSettingsV2`): Init/DataUpdate/End lifecycle, timers, serial message dispatch, PollStatus, collaborator construction, forwarder shims |
| `MozaData.cs` | Thread-safe data model (~80 volatile fields) for every device value + HID input positions; `UpdateFromCommand`/`UpdateFromArray` map parsed responses to fields |
| `MozaDeviceManager.cs` | High-level read/write API per connection: wheel ID cycling (23→21→19), paced read batches, presence probes |
| `SimHubRegistrar.cs` | SimHub `AZOM.*` property delegates + button-bindable actions (step/cycle/toggle) |
| `Protocol/` | Serial transport: `MozaSerialConnection` (threads, framing, 0x7E stuffing, write lanes), `MozaPortDiscovery` (registry walk), `MozaUsbIds` (PID inventory), `MozaCommandDatabase` (200+ commands), `MozaResponseParser`, `MozaProtocol` (constants/checksums), `MozaHidReader`, `PendingResponseTracker`, `WriteBudget`, `ConnectionFailure` |
| `Devices/` | Device detection + per-device managers and SimHub device extensions: `DeviceProber`, `DeviceDetectionState`, `ConnectionCoordinator`, `WheelModelInfo`, `MozaDeviceConstants`, AB9 / Hub / Dashboard / Base / mBooster / standalone-peripheral managers, wheel/dash/base extensions + LED managers + device settings controls, `DeviceDefinitionDeployer`, `WheelUi/` helpers |
| `Telemetry/` | Dashboard telemetry pipeline: `TelemetrySender` (orchestrator) + collaborators in subdirectories (see [Architecture](#architecture)); FSR1/CM1 display drivers; `DashboardBindingCoordinator`, `DualDisplayCoordinator`, `Fsr1Cm1MappingCoordinator`, `SimHubPropertyResolver`, `ChannelCatalogParser`, `ConfigJsonClient`, `PropertyPushQueue` |
| `Telemetry/Frames/` | Frame building: `TierDefinitionBuilder`/`TierDefinitionEmitter`, `TelemetryFrameBuilder`, `TelemetryFrameCache`, `TelemetryEncoder`, `TelemetryBitWriter`, `GameDataSnapshot`, `StringValueBuilder`, `PropertyCoercion` |
| `Telemetry/Sessions/` | Session layer: `SessionLifecycle` (open/close state machine), `SessionRegistry`, `SessionDispatcher`, `SessionDataReassembler`, `RpcCallChannel` |
| `Telemetry/Dashboard/` | Dashboard library + upload/download: `DashboardProfileStore`, `DashboardCache`, `WheelUploadCoordinator`, `DashboardDownloader`, `FileTransferBuilder`, `WheelStateParser`/`WheelDashboardState` |
| `Telemetry/Lifecycle/` | Pipeline lifecycle state machines: `SilenceGate`, `HotSwitchCoordinator`, `RecoveryDispatcher`, `CatalogResyncProbe`, `PostSwitchCatalogConvergence` |
| `Telemetry/Inbound/` | `TelemetryInboundDispatcher` — inbound 0xC3 routing (acks, device-init, per-session data) |
| `Telemetry/Watchdog/` | `DisplayWatchdog` — unified content-aware engagement verdict + close-storm backstop + sess=0x09/configJson transmit nudges |
| `Telemetry/Display/`, `Era/`, `TestMode/`, `TileServer/`, `Protocol/` | `WheelSlotTracker`; era policy (`MozaWheelEra`/`EraPolicy`); test-signal generator/catalog; tile-server state build/parse; `CompressionTable` |
| `Hardware/` | `HardwareApplier` — every hardware-side write path (`Apply*ToHardware`, detection-gated `WriteIf*` family) |
| `Settings/` | `SettingsMigrator` (schema v0→v9), `ProfileCoordinator` (persistence + profile system + per-wheel-page accessors) |
| `UI/` | Plugin settings pane (`SettingsControl` + partials), profile model (`MozaProfile`/`MozaProfileStore`/`MozaPluginSettings`), status hints, diagnostics text/bundle, custom controls (`UI/Controls/`), update check (`UI/UpdateCheck/`) |
| `Sdk/` | Third-party SDK emulation: CoAP server (`MozaSdkCoapServer`), `CoapStubManager` (PitHouse-impersonation child process), `PitHouseUdp/` control server, CBOR codec. `CoapStub/` (separate console project) is the stub executable |
| `Diagnostics/` | `MozaLog` (ring-buffered log wrapper), `SerialTrafficCapture` (frame ring + JSONL wire-trace sink), `SessionRetransmitter`, `FirmwareDebugLog` |
| `ControlMapper/` | SimHub Control Mapper variant-provider integration — see [`docs/controlmapper.md`](controlmapper.md) |
| `Resources/` | i18n: `Strings.resx` + 9 locale variants, hand-edited `Strings.Designer.cs`, `LanguageResolver` |
| `DeviceTemplates/` | Embedded SimHub Device Builder `device.json` definitions, deployed lazily on first detection |
| `Data/` | `Telemetry.json` — 400+ channel definitions (URL, compression, package_level, default `simhub_property`/`simhub_scale`) |
| `Themes/` | WPF theme dictionaries (`MozaTheme`, `MozaIcons`, `Generic.xaml`) |
| `docs/` | This guide, protocol reference (`docs/protocol/`), SimHub internals notes (`simhub.md`), capture workflow (`usb-capture.md`) |
| `tools/` | Reusable wire-trace / capture analysis scripts (`moza_trace.py`, `tierdef-decode`, `cm1-0x35-decode`, `fsr1-*`, `wire-*`, …) |
| `sim/` | Python wheel emulator + USB-gadget bridge rig (out of plugin scope; has its own README) |
| `libs/SimHub/` | Reference-only SimHub DLLs, auto-updated by CI |

### Partial-class splits

- **`UI/SettingsControl`** (plugin pane): `SettingsControl.xaml.cs` (main: tab refresh tick, base/pedals/options handlers, diagnostics), `.UpdateBanner.cs` (status-hint banners, update notifications, restart-required flow), `.Redesign.cs` (custom-control initialization/theming), `.Sdk.cs` (SDK tab handlers), `.ImportProfile.cs` (profile import dialog).
- **`Devices/MozaWheelSettingsControl`** (per-wheel device page): `.xaml.cs` (main refresh tick, telemetry section, RPM/Buttons/Flag swatches), `.Inputs.cs` (live paddles/buttons display + input-mode handlers), `.Knobs.cs` (knob ring grid + signal-mode editor). The dashboard combo / channel mapper / upload + file-inventory sections live in the shared `Devices/WheelUi/DashboardManagementControl` (+ its `.Files.cs` partial), hosted by both the wheel and dash pages.

## Architecture

### Collaborator pattern

The two orchestrators — `MozaPlugin.cs` and `Telemetry/TelemetrySender.cs` — delegate behavior to focused collaborator classes (5-PR refactor 2026-05-18, extended by the 2026-06 god-class split). Conventions:

- Collaborators are standalone `internal sealed class`es (never partial classes). The constructor takes the orchestrator back-reference (`MozaPlugin _plugin` / `TelemetrySender _sender`) plus directly-injected *stable* per-Init dependencies (`MozaData`, `DeviceDetectionState`, managers, probers).
- Cross-collaborator state access uses `internal` promotion of orchestrator fields rather than partial-class splits — explicit boundaries at the cost of field plumbing.
- The orchestrator keeps 1-line forwarder shims wherever external callers exist, so call sites in UI/device files don't churn.
- **Injection hazards** — two fields are replaced at runtime and must never be captured as constructor deps:
  - `MozaPlugin._settings` is replaced by `ClearSettings()` → collaborators read `_plugin.Settings` live.
  - `TelemetrySender._connection` is replaced by `Rebind()` (CM2 standalone repoint) → sender collaborators read `_sender.ConnectionRef` live.

`MozaPlugin` collaborators (constructed in `Init`):

| Class | Owns |
|---|---|
| `Devices/DeviceProber.cs` | Detection response dispatch + per-device read batches; two-phase wheel reads (core reads at detect, LED reads after model resolves so absent hardware isn't hammered); idempotent `Mark*Detected` helpers; secondary instances (`drivesTelemetry:false`) serve the hub/base-aux/standalone-peripheral pipes |
| `Devices/DeviceDetectionState.cs` | Volatile per-device detection flags shared across poll/UI/serial/telemetry threads; survives game-switch reloads via the persistent-wire bag |
| `Devices/ConnectionCoordinator.cs` | Primary connect + AB9/CM2/hub/base-aux dedicated lanes, base↔hub primary migration state machine, hub/base pipe polling + inbound scoping |
| `Hardware/HardwareApplier.cs` | Every hardware write path: `ApplyProfileHardware`, the per-device `Apply*ToHardware` methods, detection-gated `WriteIf*`/`WriteColorIf*`/`WriteArrayIf*` family, owner-routed pedal/handbrake writes, model-capability LED gating |
| `Settings/ProfileCoordinator.cs` | Settings persistence (debounced save, clear/reset), profile-store init/subscription, `ApplyProfile`, the per-wheel-page accessor family (`ActiveTelemetry*`, overlay, sleep/idle bundles, era), wheel-reported seed methods |
| `Settings/SettingsMigrator.cs` | Schema v0→v9 migration + profile baseline seeding |
| `Telemetry/DashboardBindingCoordinator.cs` | Dashboard binding: telemetry settings push, kind=4 emission for profile-driven switches, wheel-initiated switch handling, lifecycle gates, pending-apply retry |
| `Telemetry/DualDisplayCoordinator.cs` | CM2/CM1 dual-display pipelines: `EnsureCm2Pipeline`, the CM1 discriminator, FSR1/CM1 driver start/stop |
| `Telemetry/Fsr1Cm1MappingCoordinator.cs` | FSR1/CM1 field mappings + active dashboard index store + `Table 7 Param 6` page-report follow |
| `Telemetry/SimHubPropertyResolver.cs` | `ResolveAsDouble`/`AsString`, `@internal/` channels, property-name enumeration |
| `SimHubRegistrar.cs` | `AZOM.*` property delegates (live state reads at invoke time) + action registration |
| `Devices/Ab9EngineVibrationWorker.cs` | The 91 Hz host-rendered AB9 engine-vibration loop |
| `ControlMapper/ControlMapperBridge.cs` | Control Mapper variant-provider registration + workarounds — see [`docs/controlmapper.md`](controlmapper.md) |

`TelemetrySender` collaborators (constructed in its ctor):

| Class | Owns |
|---|---|
| `Sessions/SessionLifecycle.cs` | Session open/close state machine (`ProbeAndOpenSessions` incl. the cold-start wide close + CS-Pro 20 s extended wait), session-control frame builders (open/close/ack/prime/end), fc:00 ack latch, gap-aware contiguous-ack tracking |
| `Frames/TelemetryFrameCache.cs` | Cached enable/mode/sequence/heartbeat frames, static keepalive + parity-poll + LED-poll frames, display probe builders, lazy per-page 7C:27/7C:23 display-config cache |
| `Frames/TierDefinitionEmitter.cs` | `SendTierDefinition` + blind retransmit for firmware that doesn't ack sess=0x01 |
| `Inbound/TelemetryInboundDispatcher.cs` | Inbound 0xC3 routing keyed on `TargetDeviceIdSwapped`: fc:00 acks, type=0x81 device-init, per-session type=0x01 dispatch |
| `Watchdog/DisplayWatchdog.cs` | Unified content-aware engagement verdict: "engaged" requires positive proof (catalog + configJson state), never inbound filler; the wheel-reported slot is authoritative and a slot mismatch never restarts. Plus the wheel-CLOSE storm backstop, sess=0x09 prime retry, configJson gap retransmit nudges, and restart/park escalation via `RecoveryDispatcher` |
| `Display/WheelSlotTracker.cs` | `MaybeUpdateWheelReportedSlot` with strict type-04 validation (decode by wheel family — W13 field B, everything else field A) |
| `Lifecycle/SilenceGate.cs` | Stop→Start ~11 s host-silence gate + post-switch UI cooldown (statics, survive plugin recycle) |
| `Lifecycle/HotSwitchCoordinator.cs` | Hot-renegotiation burst state machine (arm/pace/emit decisions) |
| `Lifecycle/CatalogResyncProbe.cs`, `Lifecycle/PostSwitchCatalogConvergence.cs`, `Lifecycle/RecoveryDispatcher.cs` | Catalog re-sync probe throttle; post-switch convergence nudges; restart escalation |
| `PropertyPushQueue.cs` | Brightness-blanking coalescing via per-(session,kind) seq supersedence |
| `Dashboard/WheelUploadCoordinator.cs`, `Dashboard/DashboardDownloader.cs` | mzdash upload session lifecycle / download path (upload scaffolding is being finished — do not delete) |
| `Sessions/RpcCallChannel.cs` | Session 0x0a JSON RPC calls/replies |

What deliberately stays in the orchestrators: `MozaPlugin`'s Init/End lifecycle, `OnMessageReceived` dispatch, and `PollStatus` heartbeat; `TelemetrySender`'s `StartInner` orchestration, per-tick loop (`OnTimerElapsedInner` + `TickEmit*`), `Stop()`, and the profile/catalog lifecycle (`Profile` setter, `ApplySubscription`, `MaybeSwapProfileForCatalog`).

### Threading model

- **Threads in play:** SimHub UI/dispatcher thread (WPF handlers, `GetWPFSettingsControl`), SimHub data thread (`DataUpdate`, ~60 Hz), serial read thread per connection (`OnMessageReceived`, inbound dispatcher), serial write thread per connection, `System.Timers.Timer` ThreadPool callbacks (PollStatus 5 s, retry 250 ms, reconnect 5 s, telemetry tick ~30 ms, FSR1/CM1 driver ticks), background `StartInner`, the AB9 91 Hz worker, and mBooster 50 Hz workers.
- **Conventions:** `volatile` for single-field flags; `Interlocked` for counters and `long` timestamps (the project targets x86, so `Interlocked.Read`/`Exchange` on `long` is load-bearing for atomicity — never replace with a plain read or wrap in a lock); copy-on-write sets for read-mostly collections; leaf locks only (`_session01SeqLock`/`_session02SeqLock`/`_session09SeqLock` guard outbound seq read-modify-writes, `_sdkLifecycleGate` serializes SDK start/stop, the save-debounce lock guards lazy timer creation) — no lock nesting anywhere.
- **Hard rule:** never add a lock around fields the serial read thread touches on its ack path — a prior watchdog lock stalled the read thread on Tick→ack and deadlocked telemetry. Use `Interlocked`/`volatile` instead.
- Re-entry guards: the telemetry tick (`_tickInProgress`), `TryConnect` (`_connectingFlag` CAS), `Start()` (SemaphoreSlim + per-run CancellationTokenSource supersession).
- Shutdown: `MozaPlugin.IsShuttingDown` (static volatile) short-circuits in-flight callbacks; `End()` stops timers → detaches events → tears down I/O in dependency order; `CleanupPartialInit` mirrors it for failed Init.

### Connection topology

One `MozaSerialConnection` per USB CDC pipe, each with its own read/write threads, `PendingResponseTracker`, and `CaptureLabel`:

| Pipe | Owner | Claims | Role |
|---|---|---|---|
| Primary | `MozaPlugin._connection` (persistent static across game-switch reloads) | Wheelbase / Hub / unknown PIDs; CM2 PID when no dedicated dash port | Wheel + base + session/telemetry pipeline |
| AB9 | `MozaAb9DeviceManager` | PID `0x1000` | Shifter config + FFB streaming |
| Dashboard | `MozaDashboardDeviceManager` | PID `0x0025` (CM2) | Standalone-USB CM2 |
| Hub | `MozaHubDeviceManager` | PID `0x0020` | Universal Hub peripherals when a base is also present |
| Base-aux | `MozaBaseDeviceManager` | freed base port | Base telemetry after a base→hub primary migration |
| Standalone peripherals | `MozaStandalonePeripheralRegistry` | pedals/handbrake PIDs | Config/calibration for direct-attached peripherals |
| mBooster | `MozaMBoosterRegistry` | PID `0x0008` (per device) | Vibration-motor effects + calibration |

`Devices/ConnectionCoordinator.cs` owns connect/reconnect for the primary + dedicated lanes and the two self-heal migrations: base→hub (broken base — wheel answered on the hub after a 15 s wheel-less grace) and hub→base (wrong latch order — a wheelbase port freed up while the primary sits on the hub). Peripheral ownership (`DetectionState.PedalsOwner`/`HandbrakeOwner`/`BaseOwner`) records which pipe's device manager answered first; `HardwareApplier` routes writes through the owner. The **persistent wire** (`s_persistentConnection`/`s_persistentTelemetrySender`/`s_persistentDetectionState` statics) survives SimHub's plugin reload on game switch so the wheel never sees the ~10–14 s sess=0x09 settle; an `AppDomain.ProcessExit` hook closes sessions 0x01/0x02/0x03 on real exit so the wheel doesn't carry stale session state into the next launch.

## Component Reference

### Logging & Diagnostics (`Diagnostics/`)

- `MozaLog` — static wrapper around `SimHub.Logging.Current`; every `[Moza]` line also lands in a 5 000-entry in-process ring buffer (`Snapshot()` feeds the diagnostics export, sidestepping SimHub's flush cadence and per-version log paths).
- `SerialTrafficCapture` — singleton ring buffer (200 000 entries, oldest-drop) of timestamped TX/RX frames across all live connections, distinguished by `CaptureLabel`. `StartFileSink(path)` additionally writes a bridge-format JSONL (`{t, dir, hex, len}`, compatible with `tools/moza_trace.py` consumers) to `SimHub/Logs/moza-wire-<timestamp>.jsonl` for the whole session — toggled by `MozaPluginSettings.EnableWireTraceFileSink`, one fresh file per Init.
- `SessionRetransmitter` — per-chunk retransmit queue with exponential backoff until fc:00 acks drain it.
- `FirmwareDebugLog` — ring buffer of unsolicited group-0x0E firmware log lines for the Diagnostics tab; cleared per connection.

### Plugin entry point (`MozaPlugin.cs`)

- Implements `IPlugin`/`IDataPlugin`/`IWPFSettingsV2`. `Init` constructs the connection stack + collaborators (reusing the persistent wire when alive), `DataUpdate` fans game data out to every sender/driver/worker, `End` tears down in dependency order while optionally keeping the persistent wire alive.
- Reload safety: `Init` is try/catch-wrapped with `CleanupPartialInit()` mirroring `End()`; `Instance` is published only after all resources are wired; property delegates are null-guarded; `OnMessageReceived`/`PollStatus` short-circuit on `IsShuttingDown`.
- `OnMessageReceived` (serial read thread): captures firmware-debug 0x0E lines (also wheel-alive evidence + FSR1/CM1 page-report + rim attach/detach parsing), filters session/control frames the telemetry dispatcher owns, routes presence-probe ACKs to `OnPresenceProbeAck`, then parses via `MozaResponseParser` → `MozaData` → `DeviceProber.DetectDevices`.
- `PollStatus` (5 s): hub/base-aux polls, dual-display ticks, wheel hot-swap miss counter + PitHouse-parity wheel maintenance (presence probe, param poll, 0x43 keepalive, model recheck), presence probes for undetected devices, display re-probe + 60 s display-wedge watchdog (one-shot forced reconnect), knob-ring capability read, hub port-power polls.
- `CheckGearshiftEvent`/`CheckAb9GearshiftEvent` (per `DataUpdate`): debounced gearshift vibration triggers; neutral transitions suppressed by default.
- Button-bindable actions + `AZOM.*` properties live in `SimHubRegistrar.cs`; the user-facing action list is in [README.md § SimHub Actions](../README.md#simhub-actions).

### Serial protocol layer (`Protocol/`)

- `MozaSerialConnection` — port discovery, background read/write threads, frame assembly, full 0x7E byte stuffing both directions, classified open-failure surface (`ConnectionFailure`: AccessDenied/PortVanished/… consumed by the UI hint builder). Two write lanes (see [Key Protocol Details](#key-protocol-details)). Registry discovery is primary; the legacy serial probe is an automatic fallback for unclassified ports only, scoped by `MozaProbeTarget` (`BaseAndHub`/`Ab9`/`HubOnly`/`PedalsOnly`/`HandbrakeOnly`/`MBooster`) and hard-disableable via `DisableSerialProbeFallback`.
- `MozaPortDiscovery` — Windows registry walk of `HKLM\SYSTEM\...\Enum\USB\VID_346E&PID_*` returning `(PortName, VID, PID, FriendlyName, Category)` per MOZA composite; cross-references `SerialPort.GetPortNames()` to drop ghosts; logs unknown PIDs once per process.
- `MozaUsbIds` — the PID inventory and category routing (single source of truth, mirrored in [`docs/protocol/devices/usb-ids.md`](protocol/devices/usb-ids.md)).
- `MozaCommandDatabase` — 200+ command definitions (identity probes, settings, LED matrices, AB9/CM2/mBooster blocks).
- `MozaResponseParser` — bit-7 toggle + nibble-swap + wildcard matching; `busHint` disambiguates shared dev id 0x12 (base main vs AB9 vs mBooster); unwraps display sub-device identity; silently drops session control frames.
- `MozaProtocol` — constants + the two checksum helpers. Production code uses `CalculateWireChecksum()` (raw sum + `count(0x7E in body) × 0x7E`) on both send and verify — see [`docs/protocol/wire/checksum.md`](protocol/wire/checksum.md). Also `WheelEchoPrefixes`/`IsWheelEcho` for write-echo keepalive detection.
- `MozaHidReader` — HidSharp-based physical-input reader (steering/pedals/paddles/handbrake/buttons) enumerated by VID 0x346E + PID category; powers UI live-input bars and the `AZOM.*` input properties with no game running; mBooster axes route through the registry.

### Device management (`Devices/`, root managers)

- `MozaDeviceManager` — per-connection read/write API: wheel ID cycling, `ReadSettingsPaced` for large bursts, untracked `SendPresenceProbe` empty probes (absent devices cost one 5-byte frame per tick instead of a 3-retry storm), injectable `PendingResponseTracker` for per-pipe retransmit.
- `MozaAb9DeviceManager` — AB9 identity probe cascade, stored-setting reads (group 0x1E) vs writes (0x1F), FFB session-init handshake, engine-vibration/pulse/trigger/low-rate streaming frames, gear-shift intensity config. Wire decode: [`docs/protocol/devices/ab9-shifter.md`](protocol/devices/ab9-shifter.md). The host-rendered 91 Hz engine-vibration loop lives in `Ab9EngineVibrationWorker` (PitHouse-replicating sub-stream set; period formula `K ≈ 3.95e11 / (rpm × freq_hz)`).
- `MozaHubDeviceManager` — dedicated Universal Hub lane when a base is also present; peripherals enumerate in parallel with first-responder ownership routing.
- `MozaStandalonePeripheralRegistry` / `StandalonePeripheralController` — one descriptor-driven lane per direct-attached pedal set / handbrake (config/calibration only; axes stay HID). Standalone shifters (HGP/SGP) have no settings surface and are deliberately not claimed.
- `MozaMBoosterRegistry` / `MBoosterDeviceController` / `MBoosterEffectWorker` — multi-device mBooster support: registry discovery, per-device 50 Hz host-rendered effects (ABS/Lockup/Threshold/Engine per the protocol note, engine capped at 10 %), role-based axis merge into throttle/brake/clutch, experimental calibration surface. See [`docs/protocol/devices/mbooster.md`](protocol/devices/mbooster.md).
- `DeviceProber` — see [Architecture](#architecture). Wheel reads are two-phase (core at detect, LED reads after `wheel-model-name` resolves, capped by `WheelModelInfo` capabilities) so wheels are never hammered with reads for hardware they don't have.

### Data model (`MozaData.cs`)

- ~80 volatile fields covering connection flags, identity (wheel/display/base, incl. PitHouse-style extended identity + MCU UIDs), temps, settings values, HID positions, button states, LED color arrays.
- `IsConnected` = any MOZA device confirmed on the bus (base, hub, or standalone dashboard) — the "can I send commands?" guard. `IsBaseConnected` is the narrower base-feature flag.
- `UpdateFromCommand`/`UpdateFromArray` map parsed responses to fields with per-branch length checks; `ClearWheelIdentity` resets on hot-swap.

### Telemetry pipeline (`Telemetry/`)

`TelemetrySender` drives the multi-phase startup matching PitHouse's observed sequence:

1. **Session opens** (`Sessions/SessionLifecycle.cs`) — close stale sessions (0x01..0x03 on warm reload, wide 0x01..0x0A on cold start — CS-Pro/KS-Pro silently swallow fresh opens over stale state), then open 0x01 (mgmt) + 0x02 (telem/FlagByte) with ack waits; slow-bring-up wheels get a 20 s sliced extended wait keyed on the sess=0x09 device-init "wheel ready" signal.
2. **Device-initiated session intake** — the wheel opens 0x04..0x0A on its own side; each type=0x81 is acked and routed (`SessionRegistry`/`SessionDispatcher`).
3. **configJson RPC (sess=0x09)** — `ConfigJsonClient` parses the wheel's dashboard state blob; the sender replies once with the canonical library list. Watchdogs in `DisplayWatchdog` re-prime on gaps.
4. **Catalog quiet wait + tier definition** — `ChannelCatalogParser` assembles the wheel's channel-URL catalog; `TierDefinitionEmitter`/`TierDefinitionBuilder` intersect the active `MultiStreamProfile` with the catalog and emit the tier def (era-dependent V2 compact or V0 URL encoding; the END-marker echo rule is load-bearing — see [`protocol/tier-definition/`](protocol/tier-definition/)). The tier-def always rides sess 0x01 with FF/control records on the mirror 0x02 (a cold-start catalog arriving on 0x02 does not move it).
5. **Active tick loop** (~30 ms) — per-tier `7d:23` value frames, string channels (type=0x05 on sess=0x01, 2 s keepalive floor), enable + sequence counter, peripheral parity polls + LED state polls (load-bearing wheel-engagement keepalives — see `Frames/TelemetryFrameCache.cs` comments), widget polls, retransmit drain, slow path (~1 Hz: 0x43 keepalives, mode frame, display config, 28x polls, sess=0x09 keepalive).

Key supporting pieces:

- `TelemetryFrameBuilder` / `TelemetryEncoder` / `TelemetryBitWriter` / `GameDataSnapshot` — bit-packed value-frame assembly; `Telemetry/Protocol/CompressionTable.cs` is the canonical compression-code map.
- `DashboardProfileStore` — parses `.mzdash` files, seeds channel mappings from `Data/Telemetry.json`, produces stable dashboard keys, and synthesizes catalog-only profiles via `BuildProfileFromCatalog`.
- **Catalog-only mode**: with no mzdash folder configured, `MaybeSwapProfileForCatalog` synthesizes a `"WheelCatalog"` profile from `ChannelCatalogParser.LiveCatalog` once the wheel commits a tier-def generation, re-synthesizing when the catalog count or END marker advances; mzdash profiles are never replaced.
- `ChannelCatalogParser` — per-session buffers with seq dedup + CRC32 validation, four URL encoding forms (full, `0x01` prefix, `\1`/`\p` abbreviations, back-references), live-set tracking per END-marker generation. Full details: [`protocol/tier-definition/session-02-channel-catalog.md`](protocol/tier-definition/session-02-channel-catalog.md).
- Dashboard upload/download (`Dashboard/`): `FileTransferBuilder` (session 0x04 wire format), `WheelUploadCoordinator` (upload state machine + wire-format auto-fallback + skip-if-unchanged MD5), `DashboardDownloader`. The upload UI is currently hidden; scaffolding stays.
- `TileServerStateBuilder`/`Parser` — session 0x03 host→wheel tile-server blob (ATS/ETS2); inbound parser dormant.
- Recovery: `DisplayWatchdog` (unified engagement verdict — restart only on confirmed content absence past the 20 s grace; wheel-initiated CLOSE storms fast-escalate; sess=0x09 prime retries and configJson gap nudges are transmit plumbing, not verdicts), `RecoveryDispatcher` (30 s debounce, 3-restarts-per-window cap, park-on-exhaustion), `SilenceGate` (the ~11 s Stop→reopen host-silence rule the wheel's sess=0x09 interlock requires).
- Standalone display drivers: `Fsr1DisplayDriver` (group-0x42 fixed-schema push for FSR V1) and `Cm1DisplayDriver` (group-0x35 keyed float stream) — own timers, dash-lane stream slots, run concurrently with the tier-def sender.

### UI (`UI/` + device settings controls)

- Plugin pane tabs: Base, Wheel, Handbrake, Pedals, AB9 Shifter, mBooster, Hub, Options, SDK, About (diagnostics + serial capture). Device-page controls live under `Devices/` and are connection-gated by their LED driver's `IsConnected()`.
- 500 ms refresh tick + `_suppressEvents` guard against feedback loops; live-input sections poll HID at 30 Hz.
- `StatusHintBuilder.Build(plugin, nowUtc)` — pure function returning banner hints (port locked by another app, device definition deployed → restart, device-profile-not-added per device type), diff-cached so unchanged ticks don't rebuild the visual tree.
- Diagnostics tab: identity dump, wheel dashboard state, session state, bandwidth (`WriteBudget` window + monotonic peak), wire-error counters, CRC reject counters, serial capture start/stop + ZIP bundle export (`DiagnosticsBundleWriter`: manifest + serial capture + diagnostics text + `MozaLog` snapshot).
- UI render reads from saved state (overlay/bundles), not `_data` — `_data` mirrors transient device responses and drifts (see `MozaWheelSettingsControl.MergeOverlayIntoData`).
- Custom WPF control library (`UI/Controls/` + `Themes/`): `SectionCard`, `SegmentedControl`, `OffOnToggle`, `PaletteStrip` (+ `ColorPickerDialog` custom chip), `KnobRingViz`, `BandwidthSparkline`, `ConnectionPill`, `MozaCurveEditor` (also the FFB equalizer via style), `SteeringArc`, `TemperatureGraph`. Styles in `Themes/Generic.xaml`, tokens in `Themes/MozaTheme.xaml`, icons in `Themes/MozaIcons.xaml`.

### Profile system (`UI/MozaProfile.cs`, `UI/MozaProfileStore.cs`, `UI/MozaPluginSettings.cs`)

Per-game configuration snapshots on SimHub's `ProfileBase`. State is split across four storage tiers:

- **Plugin-global** (`MozaPluginSettings` flat fields): connection toggles, last ports, probe-fallback opt-out, update-check state, etc.
- **Per-wheel-page** (`MozaPluginSettings` dicts keyed by SimHub page GUID): mzdash folder, telemetry enabled, era, sleep bundle, idle bundle (`*ByPageGuid`).
- **Per-(profile × wheel-page)** (`MozaProfile.WheelOverridesByPageGuid`): wheel LED/mode/brightness/colors/input modes + the per-game dashboard pick (`TelemetryProfileName`/`TelemetryMzdashPath`). Sentinel `-1`/null = fall through to baseline.
- **Per-game baseline** (`MozaProfile` directly): motor/FFB/handbrake/pedals, dash + base-ambient settings, gearshift tuning, `Ab9Settings`, `TelemetryDashboardKey`, `TelemetryChannelMappings` (profile × page × dashboard-key × URL → property path).

`MozaProfile.CaptureFromCurrent` captures only device-read-sourced state; UI handlers write overlays/profiles directly so a partial device read can't clobber user edits. The `ActiveTelemetry*` accessors on `MozaPlugin` (backed by `Settings/ProfileCoordinator.cs`) resolve the current wheel's page GUID and read/write the right tier. UI handlers go through the `WriteIf*` helpers so slider drags while disconnected persist without queueing writes. Migration history (schema v2→v9) is documented in [Settings storage and migration](#settings-storage-and-migration).

### Device extensions (`Devices/`)

- `MozaDeviceExtensionFilter` routes SimHub devices by `DescriptorUniqueId` GUID to the wheel / dash / base-ambient extensions; `MozaDeviceConstants` owns the GUID↔model-prefix registry (persisted with write-temp-then-Move).
- `WheelModelInfo` — per-model LED layout descriptor (RPM/button/flag/knob counts, `bool? HasDisplay`) resolved from the firmware model name. `HasDisplay` gates all dashboard-related traffic via `ShouldDriveDashboard()` — screenless wheels must never see the display probe burst or session pipeline (drives them into a settings-read-timeout storm). The display-detected gate additionally defers telemetry start until the (slow-booting, ~20 s on CS Pro) display sub-device answers, with a 60 s wedge watchdog forcing one reconnect.
- LED managers (`MozaLedDeviceManager` wheel, `MozaDashLedDeviceManager`, `MozaBaseLedDeviceManager`, CM1/CM2 paths): virtual `ILedDeviceManager`s injected into SimHub's LED module; model-aware index remapping, windowed `active+window` bitmasks, per-frame color chunks with palette-hash dedup, flag-LED routing to the meter sub-device, "default during telemetry" button override. See [`docs/protocol/leds/color-commands.md`](protocol/leds/color-commands.md).
- Extension settings DTOs (`Moza{Wheel,Dash,Base}ExtensionSettings`) apply into the profile/overlay tiers; the wheel DTO drain is one-shot (`WheelExtensionDrained`) so stale device JSON can't clobber plugin settings.
- `DeviceTemplates/` definitions deploy lazily on first detection; per-model wheel definitions are generated from `WheelModelInfo` and rewritten when a model's layout changes.

### SDK emulation (`Sdk/`)

- `MozaSdkCoapServer` (CoAP-over-UDP, port 40266) + `PitHouseUdp.MozaControlUdpServer` (plain UDP CBOR, port 40288) expose wheel state/config to third-party tools; both hold refs into `MozaData`/`HardwareApplier` and are per-instance.
- `CoapStubManager` spawns the `MOZA Pit House.exe` impersonation stub (separate `CoapStub/` project) with a registry redirect; the manager is process-persistent across plugin reloads because Wine intermittently hangs its teardown path — `TryStop(ms)` bounds every stop call. Lifecycle is serialized by `_sdkLifecycleGate` and driven from both Init and the live UI toggles.

## Subsystem Deep-Dives

### Standalone dashboard pipeline (CM2)

The MOZA CM2 Racing Dash (USB PID `0x0025`) is a standalone USB dashboard with no wheelbase — the full dashboard pipeline (screen telemetry, dashboard library + kind=4 switch, stored RPM colors, meter-mode + threshold writes) runs against it. CM2 has 16 physical RPM LEDs (no buttons, no separate flag strip).

- **Detection.** `ConnectionCoordinator.MarkStandaloneDashboardDetectedFromUsb(reason)` (idempotent; wired at Init for persistent-wire reuse and at TryConnect/TryConnectDashboard) flips detection on USB PID alone, deploys the CM2 device.json, applies the dash profile, and starts the sender. `MozaData.IsDashboardConnected` is a third truth source for `IsConnected`.
- **Telemetry retargeting.** `TelemetrySender.TargetDeviceId` (default wheel 0x17) is set to `MozaProtocol.DeviceMain` (0x12) for a standalone CM2 / `DeviceDash` (0x14) for a bus-bridged CM2 by `DashboardBindingCoordinator.ApplyTelemetrySettings` via `plugin.PreferredStandaloneDashboardTargetDeviceId`. The setter invalidates the display-config cache and rebuilds per-tier frame builders; the dev id is threaded through every session/control/display frame. `TelemetryInboundDispatcher` keys on `TargetDeviceIdSwapped` with a wide 0x21/0x41/0x71 fan-in in standalone mode. `ResolveAutoPolicy` pins Era2026 for standalone targets.
- **Meter-config + live LED surface.** The `cm2-*` command block (write group 0x32, dev 0x12) covers brightness, modes, thresholds, and 16 stored colors; `HardwareApplier.ApplyCm2DashboardConfig` programs it. Live per-frame RPM LEDs reuse the wheel RPM-bar commands retargeted to dev 0x12 via `WriteSettingForDevice`/`WriteArrayForDevice` (working hypothesis — unverified against real CM2 captures; fall back to firmware-driven LEDs if flat).
- **Device template.** `DashCm2Guid` is distinct from the legacy dash GUID; `DeviceDefinitionDeployer.DeployDashboard(pid)` routes CM2 PID → CM2 template and compares GUID + PID + product name on existing files.
- `.mzdash` upload to CM2 storage is out of scope; the upload scaffolding remains untouched.

### Concurrent dual-screen pipelines (wheel screen + CM2/CM1 dash)

A user may drive **both** a wheel screen and a separate dash concurrently, each with its own dashboard + channel mappings, via dedicated lanes on the shared wheelbase connection:

- **Stream-slot lanes** (`MozaSerialConnection`, 32 slots): wheel pipeline at slot-base 0, AB9/mBooster 11–17, the second dash pipeline at slot-base 18. `TelemetrySender.StreamSlotBase` offsets every periodic frame; `ClearStreamSlots(from,count)` wipes one lane on stop. Co-resident senders set `SharesConnection` + `StrictInboundFilter` so each consumes only its own device's 0xC3 replies.
- **Wheel lane:** the tier-def `_telemetrySender` (dev 0x17), or `Fsr1DisplayDriver` for an FSR1.
- **Dash lane:** `MozaPlugin._cm2Sender` (a second `TelemetrySender`) driven at the CM2 bridge/main dev 0x12 in both topologies (bus-bridged at slot-base 18, own-USB at slot-base 0). PitHouse drives a bus CM2's LED config (0x32) and telemetry (0x43) entirely at dev 0x12 fire-and-forget; 0x14 only carries blind management. Orchestrated by `Telemetry/DualDisplayCoordinator.EnsureCm2Pipeline()` (gated on `ActiveTelemetryEnabled && wheelHasOwnScreen && (busCm2 || usbCm2)`), with the saved-dashboard re-assert in `TickCm2DashboardReassert`. CM2 mappings are keyed under `Cm2PageGuid`/`Cm2DashKey`, independent of the wheel's.
- **UI:** `DashboardManagementControl` is parameterized by `IsCm2Target` so the dash page routes combo/mappings/switches to the CM2 sender + CM2 keys.

### CM1 base-bridged dash (group-0x35 driver + discriminator)

The **CM1** does not speak tier-def — it is driven by a flat keyed value stream on group 0x35 (`<2-byte key><BE float32>` records), with the same 0x32/0x81 switch + `Table 7 Param 6` page-report family as the FSR1. Wire decode: [`protocol/devices/dash-0x14.md`](protocol/devices/dash-0x14.md) § "CM1 Racing Dash"; emitter verified byte-exact against `FSR1_CM1.pcapng`.

- **Driver:** `Telemetry/Cm1DisplayDriver.cs` (~50 ms tick, dash-lane slots) streams the flat `Cm1DashboardCatalog` field set via `Cm1DisplayEmitter`.
- **Discriminator** (`DualDisplayCoordinator.TickCm1Discriminator`): an unknown bridged dash first gets the tier-def `_cm2Sender` with its engagement watchdog suppressed; a 0x8E param-read answer fast-latches CM1 after a 5 s settle, otherwise the 25 s no-catalog timeout latches it. Latching persists `DashIsCm1`, deploys the CM1 device definition, stops the tier-def sender, and starts the CM1 driver. A real CM2 (catalog arrives) drops the suppress flag.
- **Mapping/UI:** flat field mappings under `Cm1PageGuid` (`MozaProfile.Cm1FieldMappings`), page index in `Cm1ActiveDashboardByGuid`; the dash page switches to CM1 mode rows. Field semantics are best-effort — the catalog ships blank defaults the user assigns.

### FSR V1 (group `0x42`) display wheel — as built

The FSR V1 (model-name `FSR`, hw `RS21-D03*`) uses a fundamentally different transport: the host pushes pre-computed display field values as fixed-schema group-0x42 records at ~28 Hz; there is no catalog, no tier-def, no session 0x02. Full wire decode: [`protocol/devices/wheel-0x17.md`](protocol/devices/wheel-0x17.md) § Group 0x42. Distinct from FSR V2 (`W13`), which is a standard tier-def wheel.

- **Detection & routing:** `WheelModelInfo.KnownModels` entry `("FSR", "FSR V1", …, hasDisplay: false)` deliberately keeps the tier-def pipeline/display probe/wedge watchdog off; `MozaPlugin.IsFsr1DisplayWheel` is the routing flag. The push runs in the standalone `Telemetry/Fsr1DisplayDriver` (own ~35 ms timer) so a dash pipeline can run concurrently; `DualDisplayCoordinator.StartFsr1DriverIfNeeded()` starts/stops it.
- **Catalog/emitter:** `Fsr1DashboardCatalog.cs` (per record type: field defs with offsets/encoding/capability/default property + the partial page-index → record-type map) and `Fsr1DisplayEmitter.cs` (startup declaration sweep, live records, 0x43 keepalive, the `g32/81` select command) — byte-exact-verified against captures.
- **Switching, both directions:** host→wheel via group 0x32 cmd 0x81 BE32 page index 0..18 (dropdown → `SetActiveFsr1Index(idx, sendToWheel:true)`, drained by the driver); wheel→host via the `Table 7, Param 6 Written: <idx>` firmware log (HID combo switches included), parsed by `Fsr1Cm1MappingCoordinator.TryFollowFsr1DashboardLog` so the plugin auto-follows.
- **User mapping:** per wheel-GUID → record-key → field in `MozaProfile.Fsr1DashboardMappings` with per-field input-scale min/max, edited in the standard channel mapper (`ChannelMappingRowFactory.BuildFromFsr1Catalog`).
- **Open items:** 5 of 19 page indices confirmed; field semantics for record types `06/09/0d/0e` decoded structurally but unnamed (exposed as raw slots); `b1`/`b2` meaning. Remaining unknowns must come from captures — do not fill a field on a guess. Tools: `tools/fsr1-0x42-extract`, `tools/fsr1-field-decode`, `tools/fsr1-hid-decode`.

### Dashboard switch state machine

`DashboardBindingCoordinator.ApplyTelemetryDashboardFromProfile(MozaProfile)` is the single entry point for binding the wheel's displayed dashboard to the current game profile's saved pick. It fires from `ApplyProfile` and from `PollStatus`'s retry loop. Returns `true` once resolved, `false` to defer.

**Inputs.** The saved `TelemetryDashboardKey` (`wheel:<id>` / `file:<filename>:<sha1-8>` / `builtin:<name>`) resolves to a target name, then to a target slot in the wheel's `ConfigJsonList`. All three key kinds funnel through the same slot lookup.

**Readiness gate (defer when):** sender null; sender not Active (kind=4 before preamble completes is silently dropped); `IsInSilenceCooldown` (a prior kind=4 is inside the silence window); wheel state null/empty (the `_cachedLastState` static fallback covers plugin reloads). Retries ride the coordinator's lock-guarded `_pending` record (5 min deadline, 30 s warn cadence); the defer reason surfaces in the UI status label via `PendingDashboardApplyDescription`.

**Apply path:** if the wheel is already on the target slot (`WheelReportedSlot` — wire-level ground truth from the wheel's own b2h type-04 records — or the reload-surviving `LastEmittedKind4Slot`), no wire action. Otherwise emit kind=4 via `OnDashboardSwitched(slot)`; with `EnableHotRenegotiation` (default) there is no Stop+Start — the tick handler emits a paced multi-emission tier-def burst echoing the wheel's END marker (see [`protocol/tier-definition/handshake.md`](protocol/tier-definition/handshake.md) § In-game dashboard switch).

**Wheel-initiated switches:** the wheel emits its own kind=4 with the new slot; `WheelSlotTracker` detects it, arms the hot-reneg burst without re-emitting, and raises `WheelInitiatedSwitch` → the coordinator resolves slot→name, updates `ActiveTelemetryProfileName`, re-applies settings, and raises `DashboardSelectionChanged` for the UI.

**Catalog re-sync probe:** when tier-def building finds unbound catalog channels (incomplete cold-start advertisement), a single kind=4 to the current slot is scheduled (800 ms deferred, ~8 s throttle via `Lifecycle/CatalogResyncProbe`) to nudge the wheel into re-advertising.

**Cold-start session pairing:** tier-def stays pinned to sess 0x01 / FF records to 0x02 regardless of which session the cold-start catalog landed on — following the catalog put the tier-def on 0x02 where CS-Pro never binds it.

**Silence gates** (`Lifecycle/SilenceGate`, static timestamps): `MarkStopped` enforces ~11 s of host silence between Stop and the next open (the wheel's sess=0x09 interlock — empirically load-bearing even on cold start); `MarkSwitchEmitted` drives the UI cooldown (200 ms hot / 11 s legacy).

**Reset semantics:** `LastEmittedKind4Slot` (static) and `ConfigJsonClient._cachedLastState` (static) survive plugin reload and are cleared on wheel hot-swap via `ResetBindingTracking`; `_lastAppliedDashboardKey` short-circuits repeated applies per instance.

**Observed timings:** game switch ~5–15 s (cold-start preamble + apply); in-game switch ~1–2 s (hot-reneg); legacy non-hot path ~35 s (the 11 s gates are the wheel's interlock and can't be shortened).

**Diagnostic tools** (`tools/`): `wire-dashboard-switches`, `wire-sess-lifecycle`, `wire-kind4-response`.

### Internationalization (i18n)

User-visible strings live in `Resources/Strings.resx` (English neutral/master) plus per-culture variants for de, el, es, fr, it, ko, nb, ru, vi, and zh-Hans. XAML uses `{x:Static res:Strings.<Key>}`; C# uses `MozaPlugin.Resources.Strings.<Key>`. The strongly-typed accessor `Resources/Strings.Designer.cs` is hand-edited (one line per key), not generated.

**Single-DLL deployment.** Every locale is embedded directly inside `MozaPlugin.dll` — no satellite assemblies. The csproj sets `<WithCulture>false</WithCulture>` per non-neutral resx with explicit `ManifestResourceName` keys; `Strings.Designer.cs` builds a BCP-47-keyed `ResourceManager` dictionary and `Get(key)` walks `Thread.CurrentUICulture`'s parent chain (passing `InvariantCulture` to each `GetString` so no satellite lookup happens), falling back to English.

`Resources/LanguageResolver.cs` resolves the culture at `Init` and `GetWPFSettingsControl`: explicit picker pref → SimHub's own `Culture` (from `GlobalSimhubSettings.json`) → OS culture → English. The UI thread's culture is reassigned inside `GetWPFSettingsControl` before constructing `SettingsControl` (x:Static evaluates at parse time).

**Adding a new key:** add the master entry in `Strings.resx`, a matching `<data>` line in **every** other `Strings.*.resx`, and a one-line property in `Strings.Designer.cs`. All three in the same change — a missing resx entry returns the key string at runtime; a missing Designer property fails XAML compile.

**Adding a new language:** (1) copy `Strings.resx` to `Strings.<culture>.resx` and translate; (2) add the `<EmbeddedResource>` entry to the csproj matching the existing pattern; (3) add the culture to `SupportedCultures` + `DisplayNames` in `LanguageResolver.cs`; (4) add the `_byCulture` row in `Strings.Designer.cs`. The Options-tab picker enumerates `SupportedCultures` automatically.

## How-To Workflows

### Adding new device settings

When adding a new setting that is written to the device, it must also be saved/restored with the profile system. Pick the storage tier first — see the four-tier classification in [Profile system](#profile-system-uimozaprofilecs-uimozaprofilestorecs-uimozapluginsettingscs) — then walk:

1. **`Protocol/MozaCommandDatabase.cs`** — add the command definition (name, device, read/write groups, command ID, payload size, type).
2. **`MozaDeviceManager.cs`** — add the device type mapping in `GetDeviceId()` if it's a new device.
3. **`Protocol/MozaProtocol.cs`** — add device ID / group constants if needed.
4. **`MozaData.cs`** — add volatile field(s) and `UpdateFromCommand` case(s).
5. **`Devices/DeviceProber.cs`** — add to the appropriate per-device read array so it's read after detection; add detection logic in `DetectDevices()` if needed. Push the new value through the matching `Apply*ToHardware` method in `Hardware/HardwareApplier.cs` (sentinel-guard the write: `if (value >= 0) …`). Wheel-overlay fields source from `Eff(overlay?.X ?? -1, profile.X)`; profile-level from `profile.X`; per-wheel-page from the matching `*ByPageGuid` dict.
6. **Storage** by tier:
   - **Per-game baseline** → property on `MozaProfile`, copy in `CopyProfilePropertiesFrom()`. Only add a `CaptureFromCurrent()` line if the value flows from device reads; UI-edited fields are written by handlers directly and capture would clobber them.
   - **Per-(profile × wheel-page)** → property on `WheelOverride`, copy in `WheelOverride.Clone()`. No capture.
   - **Per-wheel-page** → a `Dictionary<Guid, …>ByPageGuid` on `MozaPluginSettings` + a `MozaPlugin` accessor resolving the current page GUID + a `SettingsMigrator` step draining any legacy fields; bump `SettingsSchemaVersion`.
   - **Plugin-global** → property on `MozaPluginSettings`.
7. **XAML** — add UI controls to `SettingsControl.xaml` or the matching device settings control.
8. **UI handler** by tier:
   - Profile-level: `_plugin.UpdateActiveProfile(p => p.X = val)` → `WriteIf<Device>` → `SaveSettings()`.
   - Wheel-overlay: `_plugin.UpdateActiveWheelOverlay(o => o.X = val)` → `WriteIfWheelDetected` → `SaveSettings()`.
   - Per-wheel-page: `_plugin.ActiveXxx = val;` → `WriteIf<Device>` → `SaveSettings()`.
   - Plugin-global: `_plugin.Settings.X = val;` → `SaveSettings()`.
   - Colors via `WriteColorIf<Device>`, arrays via `WriteArrayIfWheelDetected`. `SaveSettings()` is debounced (500 ms); update `_data.X` too so the next refresh tick shows the value before the device echo.

Every setting that writes to the device on UI change must round-trip through profiles or the per-wheel-page dicts — a transient-only field is lost on game/profile switch.

**Host-rendered settings** (e.g. AB9 engine vibration) skip steps 1–4 entirely: no command-DB entry, no `MozaData` field, no probe — just the profile property + UI, with the periodic worker reading the profile on its next tick. **One-shot host-side config writes that ARE device-persisted but bypass the command DB** (e.g. AB9 gear-shift intensity) follow 6–8 plus an explicit `Send*` call in both the UI handler and `ApplyAb9ToHardware`.

### Settings storage and migration

`MozaPluginSettings.SettingsSchemaVersion` gates `Settings/SettingsMigrator.MigrateToSchemaV2`, which runs every step forward in one pass:

- v2: legacy UID-keyed slots → `WheelOverridesByPageGuid` / `TelemetryChannelMappings`.
- v3: flat telemetry fields → overlays.
- v4/v5/v6: mzdash folder / telemetry-enabled / era lifted off the overlay into the per-wheel-page dicts.
- v7: per-profile baseline reseed (repair pass).
- v8/v9: sleep and idle bundles lifted into `WheelSleepByPageGuid` / `WheelIdleByPageGuid`.

`WheelOverride.LegacyJsonFields` (`[JsonExtensionData(WriteData = false)]`) preserves removed JSON keys through deserialization so each step can drain them. Use `MozaDeviceConstants.ResolveWheelGuid(prefix)` to map a model prefix to a page GUID — never hard-code GUIDs. The device-JSON → plugin-settings drain is one-shot, gated by `WheelExtensionDrained` with fill-only semantics, because SimHub doesn't reliably re-serialize device JSON before shutdown.

### Adding a new telemetry channel

Most new channels only need step 1 — set `simhub_property` (and optional `simhub_scale`) in `Data/Telemetry.json` and the resolver pulls the value via `PluginManager.GetPropertyValue`. Steps 2–4 are only required when the channel should also be readable via the legacy `SimHubField` snapshot path.

1. **`Data/Telemetry.json`** — ensure URL, compression, package_level; add `simhub_property` for a default mapping and `simhub_scale` when units differ (e.g. `0.01` for 0–100 → 0–1, `57.2957795` for radians → degrees). `simhub_property: "@internal/<key>"` locks the channel to a plugin-computed value (add the `case` in `SimHubPropertyResolver`'s internal-channel switch).
2. *(optional)* `Telemetry/Dashboard/DashboardProfileStore.cs` — URL suffix → `SimHubField` in `UrlFieldMap`.
3. *(optional)* `Telemetry/Dashboard/DashboardProfile.cs` — extend the `SimHubField` enum.
4. *(optional)* `Telemetry/Frames/GameDataSnapshot.cs` — field + `FromStatusData()` + `GetField()` case.

### String-typed channels (out-of-band on sess=0x01)

Channels declared `compression: "string"` in `Telemetry.json` (23 total: `TrackId`, `CarModel`, `SessionTypeName`, etc.) do **not** bit-pack into value frames. They ride `type=0x05` sub-msgs on session 0x01: `[type=0x05][size_LE u32 = 2+strlen][channel_idx u8][0x80|strlen u8][ASCII]`. Wire reference: [`protocol/sessions/session-0x01-channel-protocol.md`](protocol/sessions/session-0x01-channel-protocol.md).

- `DashboardProfileStore` routes string-compression URLs to `MultiStreamProfile.StringChannels` — a `string` compression must **never** get a `CompressionTable` entry (a bit-packed slot the firmware refuses to bind).
- `ChannelCatalogParser.FindIdxByUrl(url)` is the authoritative idx source — the wheel re-indexes per dashboard; never hardcode idx values.
- `StringValueBuilder.Build(idx, value)` is byte-exact-verified; max strlen 127.
- `TelemetrySender.TickEmitStringValues()` emits on change with a 2 s keepalive floor, chunked through the standard session path on `_session01OutboundSeq`.
- Test mode reads `TestSignal.StringValue` directly. **Live SimHub property wiring is NOT yet implemented** — see the `TODO(2026-05-15)` in `TickEmitStringValues`.

## Key Protocol Details

The canonical wire reference is [`docs/protocol/`](protocol/). Load-bearing facts for plugin work:

- Message format: `[0x7E] [length] [request_group] [device_id] [command_id...] [payload...] [checksum]`. Responses: toggle bit 7 of the group, swap nibbles of the device id, match command id (0xFF wildcards). Multi-byte integers big-endian; floats byte-reversed. Reads use a zero-filled payload of the declared width (some wheels drop non-zero-payload reads).
- **0x7E byte stuffing**: every body 0x7E is doubled on the wire, both directions (`MozaProtocol.StuffFrame` / the read loop's collapse). The checksum must count the duplicated bytes — use `CalculateWireChecksum()` everywhere; the raw variant silently drops ~20% of zlib-bearing chunks. See [`docs/protocol/wire/checksum.md`](protocol/wire/checksum.md).
- **Two write lanes** in `MozaSerialConnection`: the **one-shot FIFO** (session traffic, settings writes, probes — ordered, paced 4 ms between consecutive one-shots, `WriteBudget` token-bucket extends the gate under pressure, never drops) and **stream slots** (periodic telemetry — one slot per `StreamKind`, latest-wins `Interlocked.Exchange` coalescing, unpaced, never bandwidth-gated). `WriteLoop` drains one-shots first, then sweeps slots; frames go out as a single pooled stuffed `_port.Write`. `SendPriority` jumps acks ahead of tier-def bursts (the wheel times out sessions whose acks lag ~1 s). `FlushPendingWrites()` drops both lanes + the OS buffer.
- **Read path**: a bulk-read thread polls `BytesToRead` at 2 ms and pulls whatever's available — Wine's blocking `Read(buf, 0, n)` does NOT return early, so the guard is load-bearing; per-byte `ReadByte()` starved the reader under Wine.
- Session close frames (type=0x00 end marker) carry a 6-byte payload with `len=6` — a shorter payload advertised as 6 makes the wheel over-read and kill the read stream.
- Session lifecycle, chunk format, tier-def encodings, catalog protocol, configJson schema, dashboard upload: see [`protocol/sessions/`](protocol/sessions/), [`protocol/tier-definition/`](protocol/tier-definition/), [`protocol/dashboard-upload/`](protocol/dashboard-upload/).

## Dependencies

- **NuGet:** `Microsoft.NETFramework.ReferenceAssemblies.net48`, `Newtonsoft.Json`, `log4net`.
- **Runtime (Windows only):** `Microsoft.Win32.Registry` (in `mscorlib`) — used by `MozaPortDiscovery`. The serial-probe fallback consults the registry per port and is hard-disableable via `DisableSerialProbeFallback`.
- **SimHub DLLs** (`libs/SimHub/`, reference-only, not packaged): `SimHub.Plugins.dll`, `GameReaderCommon.dll`, `SimHub.Logging.dll`, `SerialDash.dll`, `BA63Driver.dll`, `HidSharp.dll`. A daily GitHub Actions workflow creates PRs when new SimHub versions release.

**Important:** the SimHub DLLs in `libs/SimHub/` must match the runtime SimHub version — the PluginSdk ships older DLLs missing newer interface members, causing `TypeLoadException` at runtime. Always update from an actual SimHub installation.
