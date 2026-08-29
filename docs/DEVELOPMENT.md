# Development Guide

SimHub plugin for MOZA Racing hardware providing two-way telemetry: streams game data (speed, RPM, gear, lap times, fuel, tyre wear, etc.) to the wheel dashboard display, drives wheel/dashboard RPM and flag LEDs, and allows configuring wheelbase settings. Also supports standalone USB dashboards (CM2 Racing Dash, PID `0x0025`) that connect without a wheelbase. Uses a custom binary serial protocol reverse-engineered from the [boxflat](https://github.com/Lawstorant/boxflat) project; wire-level protocol reference lives under [`docs/protocol/`](protocol/).

### Key sources

Two directories are the canonical references for any protocol or wire-level work — read them before changing telemetry, session, or device-detection code:

- **[`docs/protocol/`](protocol/)** — the authoritative wire-level protocol reference. Start at [`docs/protocol/README.md`](protocol/README.md) (function-first layout, per-device command tables in [`devices/`](protocol/devices/), dated deep-dive journal in [`findings/`](protocol/findings/)). Read [`wire/`](protocol/wire/) first — the frame format, checksum, and 0x7E stuffing apply to **all** device traffic. The component reference and protocol sections below link into specific pages; this is their canonical home. Per the project convention, new protocol facts are written here, not duplicated into design docs or commit messages.
- **[`tools/`](../tools/)** — reusable Python wire-trace / capture-analysis scripts built during reverse-engineering (`moza_trace.py`, `trace-tools`, `tierdef-decode`, `cm1-0x35-decode`, `fsr1-*`, `wire-*`, `bridge-*`, …). They consume the bridge-format JSONL emitted by `SerialTrafficCapture.StartFileSink` (see [Logging & Diagnostics](#logging--diagnostics-diagnostics)). Reach for these when decoding a capture or verifying an emitter byte-exact against PitHouse traffic; the deep-dive sections below cite the specific tool for each subsystem.

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

- **Build**: Pushes to `main`/`dev` plus fork and draft PRs are compile-checked via GitHub Actions (same-repo non-draft PRs skip this — they get the full PR build below).
- **PR pre-releases**: Every commit pushed to an open same-repo PR builds a Release and publishes a GitHub pre-release tagged `pr-<N>-<sha7>` (version `X.Y.Z-pr.<N>.<sha7>` — the core comes from a `vX.Y.Z` in the PR title when present, else the next patch after the newest stable tag; asset `MozaPlugin_pr<N>_<sha7>.zip`) — this is where active-development builds come from. The newest 5 builds per PR are kept; closing or merging the PR deletes all of them (`pr-cleanup.yml`). The in-app updater lists each open PR as a selectable release channel. Bot-created PRs (SimHub bumps) don't trigger `pull_request` workflows — build those via the `PR Build` workflow's manual dispatch with the PR number.
- **Release**: Pushing a `v*` tag (e.g., `v0.2.0`) builds a Release and publishes a GitHub Release with the DLL (device definitions are embedded in the DLL). It also repoints the legacy `dev-latest` tag at the new stable so plugins still on the retired dev channel get offered the upgrade out.
- **SimHub dependency updates**: A daily workflow checks for new SimHub releases and creates a PR to update `libs/SimHub/`.

### Paths that can't move

Most of the tree can be rearranged freely — the csproj is SDK-style and globs its sources,
so moving a `.cs` or `.xaml` needs no project-file edit. These are the exceptions, and
several of them **fail silently at runtime rather than at build time**:

| Path | Why it's pinned |
|---|---|
| `Themes/Generic.xaml` | `Properties/AssemblyInfo.cs` declares `[assembly: ThemeInfo(…, SourceAssembly)]`, so WPF resolves `UI/Controls/` default styles from the resource URI `Themes/Generic.xaml`, derived from that file's path relative to the project dir. Move it and custom controls render unstyled with **no build error**. The same file is also the only source of `ComVisible` and the assembly `Guid` — it looks vestigial because `GenerateAssemblyInfo=false`, but it isn't. |
| `Themes/*` generally | 22 hardcoded `pack://application:,,,/MozaPlugin;component/Themes/…` URIs. |
| `UI/Controls/` namespace | Stays `MozaControls`, not `MozaPlugin.UI.Controls` — five XAML files hard-code `xmlns:ctrl="clr-namespace:MozaControls"`. |
| `Resources/Strings*.resx` | 13 `<EmbeddedResource Update="…">` entries with explicit `ManifestResourceName`. MSBuild does **not** error on a non-matching `Update`; the resx silently gets a default manifest name and `Strings.Designer.cs` fails at runtime. |
| `CoapStub/`, `tools/` | Pinned by `<DefaultItemExcludes>`. Move either and its `.cs` files get swept into the net48 compile — `tools/simhub-compat` is net9.0 and breaks the build. |
| `libs/`, `DeviceTemplates/`, `Resources/`, `Data/`, `Themes/` | Named literally in the CI `paths:` filters, which are duplicated in **four** places: `build.yml` (×2), `pr-build.yml`, and a shell `case` in `changed-code.yml`. Keep all four in sync or pushes stop triggering builds. |
| `MozaData.cs` | Stays at the repo root in `namespace MozaPlugin`. 110 files read it, and since every sub-namespace is a child of `MozaPlugin` they resolve it with no `using` at all. |

`obj/` caches a BAML tree, so an **incremental build silently misses a broken XAML type
reference**. Always `rm -rf obj bin` before trusting a build after moving or renaming XAML.

### Splitting a large class

There are no unit tests — no xunit/NUnit/MSTest anywhere (`Telemetry/TestMode/*` and
`DashboardTestPattern.cs` are runtime test-signal *features*). Verification is build, deploy,
hardware smoke test. That shapes which of the two splitting styles to reach for:

- **`<Type>.<Area>.cs` partial files** for ordering-sensitive orchestration — anything
  touching `Init`/`DataUpdate`/`End`, inbound message routing, or poll ticks, where
  construction order, teardown order and event-subscription windows are load-bearing. A
  partial split cannot change semantics.
- **`internal sealed class <Area>Coordinator`** for self-contained clusters, following
  `Devices/ConnectionCoordinator.cs`: constructor injection with `MozaPlugin plugin` first,
  constructed in `Init()`, with a comment at the field declaration pointing at the new file.

Note what the coordinator pattern does *not* do: coordinators hold `MozaPlugin _plugin` and
call back through it, so it relocates code without decoupling it. Prefer moving a member to
its rightful owner (e.g. a hardware write to `HardwareApplier`) over adding another
forwarding shim to `MozaPlugin`.

**Cautionary precedent:** extracting a `SessionLifecycleCoordinator` out of
`Telemetry/TelemetrySender.cs` hung the plugin on game switch and had to be reverted. If you
touch session lifecycle, test a game switch explicitly.

## Repository Map

| Path | Contents |
|---|---|
| `MozaPlugin.cs` + `MozaPlugin.*.cs` | Plugin entry point (`IPlugin`, `IDataPlugin`, `IWPFSettingsV2`), split into partials by concern — see [Partial-class splits](#partial-class-splits). The root `.cs` holds the class declaration, field block, `DataUpdate`, `End` and the accessor surface |
| `MozaData.cs` | Thread-safe data model (~200 volatile fields) for every device value + HID input positions; `UpdateFromCommand`/`UpdateFromArray` map parsed responses to fields. Stays at the root in `namespace MozaPlugin` because every sub-namespace reads it without a `using` |
| `Protocol/` | Serial transport only: `MozaSerialConnection` (threads, framing, 0x7E stuffing, write lanes), `MozaPortDiscovery` (device enumeration, registry + sysfs sources), `WineHost`/`LinuxUsbEnumerator`/`WineComNameResolver`/`WineDevicePathMozaPort`/`WineNativeExec` (Wine/Proton discovery + transport + native-exec), `MozaUsbIds` (PID inventory), `MozaCommandDatabase` (200+ commands), `MozaResponseParser`, `MozaProtocol` (constants/checksums), `MozaHidReader`, `PendingResponseTracker`, `WriteBudget`, `ConnectionFailure` |
| `Devices/` | Connection + detection: `DeviceProber`, `DeviceDetectionState`, `ConnectionCoordinator`, `MozaDeviceManager` (per-connection read/write API), the per-lane `Moza{Base,Hub,Dashboard,Ab9}DeviceManager`, standalone-peripheral registry/controller, `WheelModelInfo`, `MozaDeviceConstants`, `GearshiftDetector`, `StandbyCoordinator` |
| `Devices/MBooster/` | mBooster subsystem: `MBoosterDeviceController`, `MBoosterEffectWorker`, `MBoosterEffectSynthesizer`, `MBoosterTypes`, `MozaMBoosterRegistry` |
| `Devices/Led/` | SimHub LED integration: `Moza{,Dash,Base}LedDeviceManager`, `LedDriverInjection`, `SimHubLedCompat` |
| `Devices/Haptics/` | Host-rendered haptics loops: `Ab9EngineVibrationWorker`, `BaseLfeEffectWorker`, `EngineVibrationMath`; plus `MozaBaseHapticsBridge` (the wheelbase device's ShakeIt Haptics section) |
| `Devices/Extensions/` | SimHub device-extension plumbing: `Moza{Wheel,Dash,Base}DeviceExtension` + their `*ExtensionSettings`, `DeviceDefinitionDeployer`, `MozaDeviceExtensionFilter` |
| `Devices/Ui/` | Per-device WPF pages: `Moza{Wheel,Dash,Base}SettingsControl`, the shared `DashboardManagementControl` / `DashboardFilesControl`, channel-mapping rows, `WheelUiHelpers` |
| `Devices/StalksTruckSim/` | Truck-sim stalk controller + action mapping |
| `Telemetry/` | Dashboard telemetry pipeline: `TelemetrySender` (orchestrator, split into partials) + `DashboardBindingCoordinator`, `DualDisplayCoordinator`, `Fsr1Cm1MappingCoordinator`, `ChannelMappingCoordinator`, `SimHubPropertyResolver`, `RetryBackoff` |
| `Telemetry/Frames/` | Frame building: `TierDefinitionBuilder`/`TierDefinitionEmitter`, `TelemetryFrameBuilder`, `TelemetryFrameCache`, `TelemetryEncoder`, `CompressionTable`, `TelemetryBitWriter`, `GameDataSnapshot`, `StringValueBuilder`, `PropertyCoercion`, `TrackMapTransform`, `ChannelCatalogParser` |
| `Telemetry/Sessions/` | Session layer: `SessionLifecycle` (open/close state machine), `SessionRegistry`, `SessionDispatcher`, `SessionDataReassembler`, `RpcCallChannel`, `FfRecordStream` + `FfRecordReader`, `SessionPropertyPushBuilder`, `SessionRetransmitter`, `PropertyPushQueue` |
| `Telemetry/Dashboard/` | Dashboard library + upload/download: `DashboardProfileStore`, `DashboardCache`, `WheelUploadCoordinator`, `DashboardDownloader`, `FileTransferBuilder`, `ConfigJsonClient`, `WheelStateParser`/`WheelDashboardState` |
| `Telemetry/Display/` | Standalone (non-tier-def) display drivers: `Fsr1DisplayDriver`/`Emitter`/`DashboardCatalog`, `Cm1DisplayDriver`/`Emitter`/`DashboardCatalog`, `Fsr1VizSnapshot`, `WheelSlotTracker` |
| `Telemetry/Lifecycle/` | Pipeline lifecycle: `HotSwitchCoordinator`, `RecoveryDispatcher`, `CatalogResyncProbe`, `PostSwitchCatalogConvergence`, `SilenceGate`, `TelemetryInboundDispatcher` (inbound 0xC3 routing), `DisplayWatchdog` (engagement verdict + close-storm backstop) |
| `Telemetry/Era/`, `TestMode/`, `TileServer/` | Era policy (`MozaWheelEra`/`EraPolicy`, always resolved from the wheel — there is no user override); test-signal generator/catalog + the dashboard test pattern and switch auto-test; tile-server state build/parse |
| `Hardware/` | `HardwareApplier` (partials: core, `.Cm2`, `.MBooster`) — every hardware-side write path: `Apply*ToHardware`, the detection-gated `WriteIf*` family, CM2 write routing; plus `ProcessResponsivenessManager` (EcoQoS opt-out + timer resolution) |
| `Settings/` | The whole settings domain: `MozaPluginSettings`, `MozaProfile`, `MozaProfileStore`, `BaseSettingCatalog`, `ProfileCoordinator` (persistence + profile system + per-wheel-page accessors) |
| `Integration/` | SimHub-registration surfaces: `SimHubRegistrar` (`AZOM.*` properties + button actions), `ControlMapperBridge` + `MozaVariantProvider` (see [`docs/controlmapper.md`](controlmapper.md)) |
| `UI/` | Plugin settings pane (`SettingsControl` + partials), status hints, diagnostics text/bundle, `TemperatureHistory`, custom controls (`UI/Controls/`, `namespace MozaControls`), PitHouse import wizard (`UI/Import/`), update check (`UI/UpdateCheck/`) |
| `Sdk/` | Third-party SDK emulation: `SdkLifecycleCoordinator` (owns the servers + stub child process), CoAP server (`MozaSdkCoapServer`), `CoapStubManager`, `PitHouseUdp/` control server, CBOR codec, `Resources/` URI handlers. `CoapStub/` (separate console project) is the stub executable |
| `Diagnostics/` | `MozaLog` (ring-buffered log wrapper), `SerialTrafficCapture` (frame ring + JSONL wire-trace sink), `FirmwareDebugLog`, `DeviceLogStore`/`DeviceLogParser` (display application log), `CaptureRedactor`, `Fsr1ProbeTool` |
| `Resources/` | i18n: `Strings.resx` + 12 locale variants, hand-edited `Strings.Designer.cs`, `LanguageResolver` |
| `DeviceTemplates/` | Embedded SimHub Device Builder `device.json` definitions, deployed lazily on first detection; `Thumbnails/` holds the product renders (per-wheel + CM2 dash) deployed alongside them as `thumbnail.png` sidecars |
| `Data/` | `Telemetry.json` — 400+ channel definitions (URL, compression, package_level, default `simhub_property`/`simhub_scale`) |
| `Themes/` | WPF theme dictionaries (`MozaTheme`, `MozaIcons`, `Generic.xaml`). **Frozen path** — see [Paths that can't move](#paths-that-cant-move) |
| `docs/` | This guide, protocol reference (`docs/protocol/`), SimHub internals notes (`simhub.md`), capture workflow (`usb-capture.md`) |
| `tools/` | Reusable wire-trace / capture analysis scripts (`moza_trace.py`, `tierdef-decode`, `cm1-0x35-decode`, `fsr1-*`, `wire-*`, …). Capture dir comes from `MOZA_TRACE_DIR` |
| _(moved out)_ | The Python wheel/device emulator + USB-gadget bridge rig now lives in its own project: [giantorth/moza-simulator](https://github.com/giantorth/moza-simulator) |
| `libs/SimHub/` | Reference-only SimHub DLLs, auto-updated by CI |

### Partial-class splits

Three classes are large enough to be split across files. All three are one class —
partials share fields, so a member can move between these files freely without any
signature change. See [Splitting a large class](#splitting-a-large-class) for when to prefer a partial file over
extracting a collaborator.

- **`MozaPlugin`** (plugin entry point):

  | File | Owns |
  |---|---|
  | `MozaPlugin.cs` | class declaration, static + instance field block, `DataUpdate`, `End`, `GetWPFSettingsControl`, accessor surface |
  | `.Bootstrap.cs` | `Init` and `CleanupPartialInit` — construction and teardown ordering |
  | `.Polling.cs` | the four timer bodies and `PollStatusCore` |
  | `.Inbound.cs` | `OnMessageReceived`, presence-probe ACK routing, AB9 inbound |
  | `.Shutdown.cs` | `End`'s ordered teardown, `ProcessExit` handling |
  | `.HostHooks.cs` | power-mode changes, SerialDash port-scan veto |
  | `.DataUpdate.cs` | the per-tick game-data fan-out |
  | `.MBooster.cs` | mBooster device coordination (settings, serial/connectivity resolve, routed probes, axis-role healing) |
  | `.Cm2Routing.cs` | CM2 presence predicates + meter firmware-era detection |
  | `.ChannelMapping.cs` | property-resolver forwarders and the NCalc formula plumbing |
  | `.Diagnostics.cs` | display-running predicates + the read-only diagnostics surface |
  | `.Haptics.cs` | LFE test triggers + the ShakeIt channel bridge |
  | `.Shims.cs` | the remaining forwarder facade to extracted collaborators |

- **`Telemetry/TelemetrySender`** (telemetry orchestrator): `.cs` (fields, ctor, `Rebind`, `Dispose`, nested `TierState`), `.Lifecycle.cs` (`Start`/`StartInner`/`Stop`), `.Tick.cs` (the timer loop and per-tick emitters), `.CatalogSync.cs` (resync probe, subscription growth, post-switch convergence), `.Subscription.cs`, `.Profile.cs`, `.Sessions.cs`, `.DashCommands.cs`, `.Library.cs` (configJson + library sync), `.Session09.cs`, `.DeviceLog.cs`, `.Frames.cs`.

- **`Hardware/HardwareApplier`**: `.cs` (the `Apply*ToHardware` + `WriteIf*` families), `.Cm2.cs` (CM2 write routing), `.MBooster.cs` (mBooster calibration writes).


- **`UI/SettingsControl`** (plugin pane) — one partial per tab or concern; `SettingsControl.xaml.cs` itself holds only the fields, constructor and Loaded/Unloaded timer wiring:

  | File | Owns |
  |---|---|
  | `.xaml.cs` | fields, ctor, `OnLoadedStartTimers` / `OnUnloadedStopTimers`, `Instance` |
  | `.Refresh.cs` | the 500 ms refresh tick, `RefreshDisplay`, `RefreshBaseTab`, HID input displays |
  | `.BaseTab.cs` | base slider handlers, checkbox handlers, RPM range |
  | `.Lfe.cs` | wheelbase LFE effects (base fw ≥ 1.2.10.10) |
  | `.Ffb.cs` | FFB equalizer + FFB curve + Bluetooth/base calibration |
  | `.Handbrake.cs` | handbrake range, curve, calibration |
  | `.Pedals.cs` | pedals tab |
  | `.Hub.cs` | hub tab |
  | `.Options.cs` | options tab, profile system, telemetry toggles, connection toggle |
  | `.About.cs` | about-tab link handlers |
  | `.Ab9.cs` | AB9 active-shifter tab |
  | `.MBooster.cs` | mBooster tab (multi-device, multi-pedal row selection) |
  | `.Effects.cs` | custom effects (experimental) |
  | `.Calibration.cs` | calibration (experimental) |
  | `.SliderHelpers.cs` | generic slider-handler helpers + keyed slider value box |
  | `.Sdk.cs` | SDK tab handlers |
  | `.UpdateBanner.cs` | status-hint banners, update notifications, restart-required flow |
  | `.Redesign.cs` | custom-control initialization/theming |
  | `.BugReport.cs` | diagnostics bundle + bug-report upload |
  | `.Shifter.cs` / `.Stalks.cs` | HGP/SGP shifter tabs; truck-sim stalks |
  | `.ImportProfile.cs` | profile import dialog |

  The PitHouse preset wizard (`UI/Import/`) accepts both legacy raw-JSON presets and the ZIP-wrapped `.mzpreset` container PitHouse 1.4+ writes (a zip holding `preset.json` + `metadata.json`); `PitHousePresetArchive` unwraps it, detecting the container by content (ZIP magic) rather than extension, so both read paths (`PitHousePresetReader` + `PitHouseFolderScanner`) stay format-agnostic. `deviceType` picks the mapper — `PitHouseMotorMapper` (wheelbase → `MozaProfile`), and for `Pedals` one of two: `PitHousePedalsMapper` (mBooster — per-pedal settings rows, subject-role detection, retargetable via the "Apply to" combo) or `PitHouseCrpPedalsMapper` (CRP/CRP2/SRP — one device with all three pedals, calibration on `MozaProfile.Pedals*`, nothing to retarget). The wizard routes on the preset's own `devices` list, falling back to the mBooster path — see [`docs/protocol/devices/mbooster.md`](protocol/devices/mbooster.md#crp--crp2--srp-presets--the-passive-pedal-route).
- **`Devices/Ui/MozaWheelSettingsControl`** (per-wheel device page): `.xaml.cs` (main refresh tick, telemetry section, RPM/Buttons/Flag swatches), `.Inputs.cs` (live paddles/buttons display + input-mode handlers), `.Knobs.cs` (knob ring grid + signal-mode editor). The dashboard combo / channel mapper sections live in the shared `Devices/Ui/DashboardManagementControl` (Dashboard tab); the upload + on-wheel file-inventory (Enable/Delete) sections live in the sibling `Devices/Ui/DashboardFilesControl` (Files tab). Both are self-contained (own 500 ms refresh timer, Loaded/Unloaded-gated) and hosted by both the wheel and dash pages.

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
| `Settings/ProfileCoordinator.cs` | Settings persistence (debounced save, clear/reset), profile-store init/subscription, `ApplyProfile`, the per-wheel-page accessor family (`ActiveTelemetry*`, overlay, sleep/idle bundles), wheel-reported seed methods |
| `Telemetry/DashboardBindingCoordinator.cs` | Dashboard binding: telemetry settings push, kind=4 emission for profile-driven switches, wheel-initiated switch handling, lifecycle gates, pending-apply retry |
| `Telemetry/DualDisplayCoordinator.cs` | CM2/CM1 dual-display pipelines: `EnsureCm2Pipeline`, the CM1 discriminator, FSR1/CM1 driver start/stop |
| `Telemetry/Fsr1Cm1MappingCoordinator.cs` | FSR1/CM1 field mappings + active dashboard index store + `Table 7 Param 6` page-report follow |
| `Telemetry/SimHubPropertyResolver.cs` | `ResolveAsDouble`/`AsString`, `@internal/` channels, property-name enumeration |
| `Integration/SimHubRegistrar.cs` | `AZOM.*` property delegates (live state reads at invoke time) + action registration |
| `Devices/Haptics/Ab9EngineVibrationWorker.cs` | The 91 Hz host-rendered AB9 engine-vibration loop |
| `Devices/Haptics/BaseLfeEffectWorker.cs` | The 50 Hz host-rendered wheelbase LFE loop (complex gearshift / engine / ABS, cmd `0x2D/0x77`, fw ≥ 1.2.10.10) |
| `Integration/ControlMapperBridge.cs` | Control Mapper variant-provider registration + workarounds — see [`docs/controlmapper.md`](controlmapper.md) |

`TelemetrySender` collaborators (constructed in its ctor):

| Class | Owns |
|---|---|
| `Sessions/SessionLifecycle.cs` | Session open/close state machine (`ProbeAndOpenSessions` incl. the cold-start wide close + CS-Pro 20 s extended wait), session-control frame builders (open/close/ack/prime/end), fc:00 ack latch, gap-aware contiguous-ack tracking |
| `Frames/TelemetryFrameCache.cs` | Cached enable/mode/sequence/heartbeat frames, static keepalive + parity-poll + LED-poll frames, display probe builders, lazy per-page 7C:27/7C:23 display-config cache |
| `Frames/TierDefinitionEmitter.cs` | `SendTierDefinition` + blind retransmit for firmware that doesn't ack sess=0x01 |
| `Inbound/TelemetryInboundDispatcher.cs` | Inbound 0xC3 routing keyed on `TargetDeviceIdSwapped`: fc:00 acks, type=0x81 device-init, per-session type=0x01 dispatch |
| `Watchdog/DisplayWatchdog.cs` | Unified content-aware engagement verdict: "engaged" requires positive proof (catalog + configJson state), never inbound filler; the wheel-reported slot is authoritative and a slot mismatch never restarts. Plus the wheel-CLOSE storm backstop, sess=0x09 prime retry, configJson gap retransmit nudges, and restart/park escalation via `RecoveryDispatcher` |
| `Display/WheelSlotTracker.cs` | `MaybeUpdateWheelReportedSlot` with strict type-04 validation (decode by wheel family — W13 field B, everything else field A) |
| `Lifecycle/SilenceGate.cs` | Stop→Start ~11 s host-silence gate + post-switch UI cooldown (per-instance Interlocked timestamps; cross-reload survival rides the reused persistent main sender) |
| `Lifecycle/HotSwitchCoordinator.cs` | Hot-renegotiation burst state machine (arm/pace/emit decisions) |
| `Lifecycle/CatalogResyncProbe.cs`, `Lifecycle/PostSwitchCatalogConvergence.cs`, `Lifecycle/RecoveryDispatcher.cs` | Catalog re-sync probe throttle; post-switch convergence nudges; restart escalation |
| `PropertyPushQueue.cs` | Brightness-blanking coalescing via per-(session,kind) seq supersedence |
| `Dashboard/WheelUploadCoordinator.cs`, `Dashboard/DashboardDownloader.cs` | mzdash upload session lifecycle / download path (upload scaffolding is being finished — do not delete) |
| `Sessions/RpcCallChannel.cs` | Session 0x0a JSON RPC calls/replies |

What deliberately stays in the orchestrators: `MozaPlugin`'s Init/End lifecycle, `OnMessageReceived` dispatch, and `PollStatus` heartbeat; `TelemetrySender`'s `StartInner` orchestration, per-tick loop (`OnTimerElapsedInner` + `TickEmit*`), `Stop()`, and the profile/catalog lifecycle (`Profile` setter, `ApplySubscription`, `MaybeSwapProfileForCatalog`).

### Threading model

- **Threads in play:** SimHub UI/dispatcher thread (WPF handlers, `GetWPFSettingsControl`), SimHub data thread (`DataUpdate`, ~60 Hz), serial read thread per connection (`OnMessageReceived`, inbound dispatcher), serial write thread per connection, `System.Timers.Timer` ThreadPool callbacks (PollStatus 5 s, retry 250 ms, reconnect 5 s, telemetry tick ~30 ms, FSR1/CM1 driver ticks), background `StartInner`, the AB9 91 Hz worker, and mBooster 50 Hz workers.
- **Conventions:** `volatile` for single-field flags; `Interlocked` for counters and `long` timestamps (the project targets x86, so `Interlocked.Read`/`Exchange` on `long` is load-bearing for atomicity — never replace with a plain read or wrap in a lock); copy-on-write for read-mostly collections (station sets, `TelemetryChannelMappings`, the per-page sleep/idle bundle dicts — writers build fresh dicts and reference-swap; the serial-read/tick readers snapshot the reference once, never walk a mutating dict); leaf locks only (`_session01SeqLock`/`_session02SeqLock`/`_session09SeqLock` guard outbound seq read-modify-writes, `_profileSetLock` serializes the `Profile` setter across UI/read/tick threads, `GameDataSnapshot.s_carPosLock` guards the radar car-position statics shared by dual-display senders, `_sdkLifecycleGate` serializes SDK start/stop, the save-debounce lock guards lazy timer creation) — no lock nesting anywhere.
- **Formula engines:** SimHub's `NCalcEngineBase` is NOT safe for concurrent evaluation on one instance (see [`docs/simhub.md`](simhub.md) § Formula Engine) — every `NCalcExpressionEvaluator` serializes internally, and each consumer thread family gets its own instance: the resolver's shared one serves the telemetry tick + UI, and `MozaPlugin.CreateHapticsFormulaResolver()` hands the LFE worker and the mBooster registry private instances so 50 Hz haptics never queue behind telemetry evaluations.
- **Hard rule:** never add a lock around fields the serial read thread touches on its ack path — a prior watchdog lock stalled the read thread on Tick→ack and deadlocked telemetry. Use `Interlocked`/`volatile` instead.
- Re-entry guards: the telemetry tick (`_tickInProgress`), `TryConnect` (`_connectingFlag` CAS), the reconnect-timer tick (`_reconnectTickInProgress` CAS — a slow probe can outlast the 5 s interval), `Start()` (SemaphoreSlim + per-run CancellationTokenSource supersession). Serial I/O threads carry a per-open generation token (`MozaSerialConnection._ioGeneration`): `Disconnect` bumps it, so a thread that outlives its `Join(1000)` (syscall-wedged under Wine) exits on wake instead of re-attaching to the next open's port and queues.
- Shutdown: `MozaPlugin.IsShuttingDown` (static volatile) short-circuits in-flight callbacks; `End()` stops timers → detaches events → tears down I/O in dependency order; `CleanupPartialInit` mirrors it for failed Init.

### Connection topology

One `MozaSerialConnection` per USB CDC pipe, each with its own read/write threads, `PendingResponseTracker`, and `CaptureLabel`:

| Pipe | Owner | Claims | Role |
|---|---|---|---|
| Primary | `MozaPlugin._connection` (persistent static across game-switch reloads) | Wheelbase / Hub / unknown PIDs; CM2 PID when no dedicated dash port | Wheel + base + session/telemetry pipeline |
| AB9 | `MozaAb9DeviceManager` | PID `0x1000` (AB9) / `0x1002` (AB6) | Shifter config + FFB streaming |
| Dashboard | `MozaDashboardDeviceManager` | PID `0x0025` (CM2) | Standalone-USB CM2 |
| Hub | `MozaHubDeviceManager` | PID `0x0020` | Universal Hub peripherals when a base is also present |
| Base-aux | `MozaBaseDeviceManager` | freed base port | Base telemetry after a base→hub primary migration |
| Standalone peripherals | `MozaStandalonePeripheralRegistry` | pedals/handbrake PIDs | Config/calibration for direct-attached peripherals |
| mBooster | `MozaMBoosterRegistry` | PID `0x0008` (per device) | Vibration-motor effects + calibration |

`Devices/ConnectionCoordinator.cs` owns connect/reconnect for the primary + dedicated lanes and the two self-heal migrations: base→hub (broken base — wheel answered on the hub after a 15 s wheel-less grace) and hub→base (wrong latch order — a wheelbase port freed up while the primary sits on the hub). Peripheral ownership (`DetectionState.PedalsOwner`/`HandbrakeOwner`/`BaseOwner`) records which pipe's device manager answered first; `HardwareApplier` routes writes through the owner. The **persistent wire** (`s_persistentConnection`/`s_persistentTelemetrySender`/`s_persistentDetectionState` statics) survives SimHub's plugin reload on game switch so the wheel never sees the ~10–14 s sess=0x09 settle; an `AppDomain.ProcessExit` hook closes sessions 0x01/0x02/0x03 on real exit so the wheel doesn't carry stale session state into the next launch.

#### Host sleep/resume recovery

On host **sleep/resume** the wheel firmware power-cycles and silently tears down its display/telemetry sessions, but the host serial tty frequently stays `.IsOpen == true` (half-open). Two existing recovery paths both miss this case: the 5 s reconnect timer is gated on `!IsConnected` (still `true`), and `MozaSerialConnection`'s ~30 s half-open dead-tty detector (`ReadIdleDeadMs`, which force-closes a port that goes `BytesToRead==0` forever without throwing) only fires while the wheel is *silent* — a resuming wheel usually starts talking again immediately, resetting `_lastRxUtcTicks`. The net symptom: the sender keeps ticking value frames into sessions the wheel has already dropped → **blank display with nothing to trigger a rebuild**.

`MozaPlugin` subscribes to `Microsoft.Win32.SystemEvents.PowerModeChanged` (in `Init`; unsubscribed in `End()`/`CleanupPartialInit` via the `_powerModeHooked` gate — it is a **static** event, so a live subscription would leak the instance and double-fire across the game-switch reload). On `PowerModes.Resume`, `OnPowerModeChanged` bounces off the SystemEvents notification thread (`ThreadPool`) and calls **`MozaSerialConnection.ForceReconnect`** on the two display-bearing pipes — the primary (wheel) and the standalone-USB CM2. `ForceReconnect` closes the port and raises `Disconnected` *without* waiting for the I/O-error threshold (sharing the `_portFailureLogged` CAS with `HandleIoFailure` so it can't race a double-close, and keeping `_running` true so the I/O threads stay alive for `Connect()` to reopen). `Disconnected` drives the existing tested reset chain (`OnSerialDisconnected`/`OnDashboardDisconnected` → `ResetWheelDetection` → sender `Stop()`); the reconnect timer then reopens a fresh port and the session pipeline rebuilds cold. Config-only lanes (hub/base-aux/AB9/peripherals) are deliberately left to self-heal via the half-open detector — a stale config lane is benign. The subscription is `try`-wrapped because `SystemEvents` needs a message pump and can be absent under Wine/Proton (harmless — a Linux host's sleep doesn't raise Windows power events).

## Component Reference

### Logging & Diagnostics (`Diagnostics/`)

- `MozaLog` — static wrapper around `SimHub.Logging.Current`; every `[Moza]` line also lands in a 5 000-entry in-process ring buffer (`Snapshot()` feeds the diagnostics export, sidestepping SimHub's flush cadence and per-version log paths). The frame-rate Debug lines on the serial read thread (the WIRE session-chunk diag and the firmware-debug echo) are gated by `MozaLog.WireDebugEnabled` — default on, disable via `VerboseWireDebugLog: false` in the settings JSON (code-only, no UI).
- `SerialTrafficCapture` — singleton ring buffer (200 000 entries, oldest-drop) of timestamped TX/RX frames across all live connections, distinguished by `CaptureLabel`. `StartFileSink(path)` additionally writes a bridge-format JSONL (`{t, dir, hex, len}`, compatible with `tools/moza_trace.py` consumers) to `SimHub/Logs/moza-wire-<timestamp>.jsonl` for the whole session — toggled by `MozaPluginSettings.EnableWireTraceFileSink`, one fresh file per Init.
- `SessionRetransmitter` — per-chunk retransmit queue with exponential backoff until fc:00 acks drain it.
- `FirmwareDebugLog` — ring buffer of unsolicited group-0x0E firmware log lines for the Diagnostics tab; cleared per connection.
- `DeviceLogStore` + `DeviceLogParser` — the wheel **display's** own application log (a Linux `MOZADash` process: crash backtraces, memory samples, render state), *pulled* over the session layer rather than arriving unsolicited. `TelemetrySender.TickEmitDeviceLogPoll` sends an FF `kind=14` request for 100 lines ~1/min on the FF session; the device answers with a zlib'd UTF-16BE line list and an FF `kind=15` receipt tells it how many to drop. Distinct source from `FirmwareDebugLog` (base/wheel MCU chatter) and separately retained — one pull is 100 lines of several hundred bytes. Surfaces on the Diagnostics tab and as `device-display-log.txt` in the bundle; disable via `EnableDeviceLogPull: false`. Wire spec: [`docs/protocol/sessions/session-0x02-ff-init.md`](protocol/sessions/session-0x02-ff-init.md) § Device log pull.

### Plugin entry point (`MozaPlugin.cs`)

- Implements `IPlugin`/`IDataPlugin`/`IWPFSettingsV2`. `Init` constructs the connection stack + collaborators (reusing the persistent wire when alive), `DataUpdate` fans game data out to every sender/driver/worker, `End` tears down in dependency order while optionally keeping the persistent wire alive.
- Reload safety: `Init` is try/catch-wrapped with `CleanupPartialInit()` mirroring `End()`; `Instance` is published only after all resources are wired; property delegates are null-guarded; `OnMessageReceived`/`PollStatus` short-circuit on `IsShuttingDown`.
- `OnMessageReceived` (serial read thread): captures firmware-debug 0x0E lines (also wheel-alive evidence + FSR1/CM1 page-report + rim attach/detach parsing), filters session/control frames the telemetry dispatcher owns, routes presence-probe ACKs to `OnPresenceProbeAck`, then parses via `MozaResponseParser` → `MozaData` → `DeviceProber.DetectDevices`.
- `PollStatus` (5 s): hub/base-aux polls, dual-display ticks, wheel hot-swap miss counter + PitHouse-parity wheel maintenance (presence probe, param poll, 0x43 keepalive, model recheck), presence probes for undetected devices, display re-probe + 60 s display-wedge watchdog (one-shot forced reconnect), knob-ring capability read, hub port-power polls.
- `CheckGearshiftEvent`/`CheckAb9GearshiftEvent` (per `DataUpdate`): debounced gearshift vibration triggers; neutral transitions suppressed by default.
- Button-bindable actions + `AZOM.*` properties live in `Integration/SimHubRegistrar.cs`, most of them generated from the `Settings/BaseSettingCatalog.cs` table (one row per wheelbase setting → one property + four step actions, or three toggle actions); the user-facing action list is in [README.md § SimHub Actions](../README.md#simhub-actions). Adding a wheelbase setting to the SimHub surface is a one-row change — the row's display range and scale **must** match the corresponding Base-tab slider handler in `UI/SettingsControl.BaseTab.cs`, since both write the same parameter-store slot.

### Serial protocol layer (`Protocol/`)

- `MozaSerialConnection` — port discovery, background read/write threads (bound to a per-open generation token so a Join-timeout zombie can't re-attach to the next open — see [Threading model](#threading-model)), frame assembly, full 0x7E byte stuffing both directions, classified open-failure surface (`ConnectionFailure`: AccessDenied/PortVanished/… consumed by the UI hint builder). Two write lanes (see [Key Protocol Details](#key-protocol-details)). Registry discovery is primary; the legacy serial probe is an automatic fallback for unclassified ports only, scoped by `MozaProbeTarget` (`BaseAndHub`/`Ab9`/`HubOnly`/`PedalsOnly`/`HandbrakeOnly`/`MBooster`). Enumeration-only lanes (base-aux, hub, dashboard, standalone peripherals) hard-disable it by passing `disableProbeFallback: () => true` at construction; there is no user setting for it.
- `MozaPortDiscovery` — device enumeration behind one `PortInfo` shape (`PortName`, VID, PID, `FriendlyName`, `InstanceId`, `ContainerId`, `DevicePath`, `Serial`, `Category`), so every consumer — lane managers, the mBooster/standalone-peripheral registries, diagnostics, the Arduino-scan veto — is platform-agnostic. Two sources, picked by `WineHost.IsWine`, reported as `Source` (`Registry`/`Sysfs`/`None`) with `IsAuthoritative` = "a real source answered":
  - **Registry** (native Windows): walk of `HKLM\SYSTEM\...\Enum\USB\VID_346E&PID_*`, cross-referenced against `SerialPort.GetPortNames()` to drop ghosts. Lists only devices that bound `usbser`, so the serial probe can still add information and the per-port narrowing in `FindMozaPort` is unchanged.
  - **Sysfs** (Wine/Proton): `LinuxUsbEnumerator` walks `/sys` through Wine's unix drive. A Wine prefix has **no `Enum\USB` tree at all**, so the registry walk is dead there and the plugin used to fall back to a blind sweep that opened whatever else was on the bus. sysfs enumerates the USB bus itself, so it is complete — `FindMozaPort` never probes under it.
  - `DurableId(PortInfo)` (`VID:PID:serial`) is the per-unit key each lane persists beside its port name and tries first on reconnect; unlike a port name it survives a replug onto a different tty/COM.
- `WineHost` — latched Wine/Proton detection (`ntdll!wine_get_version`), host uname, the unix-drive root (the drive with both `/proc` and `/sys/class/tty`), and the prefix path via `kernel32!wine_get_unix_file_name`. Linux paths gate on `IsWine` **and** a non-null `UnixRoot`; Wine on macOS has no `/sys` and falls through to the COM-name path.
- `LinuxUsbEnumerator` — per MOZA CDC interface: VID/PID and interface number from `/sys/class/tty/<tty>/device/uevent` + `bInterfaceNumber` (the real descriptor field, not a `-if00` name guess), identity (serial, product, manufacturer, bus path) from the colon-free device entries under `/sys/bus/usb/devices`. Every path avoids `':'` — sysfs interface dirs are named `1-1.3:1.0`, and under SimHub's AppDomain .NET's `FileIOPermission` path emulation rejects a colon outside the drive with `NotSupportedException` (`File.ReadAllText` throws; `Directory.Exists` swallows it into a silent `false`). **Do not use `Path.GetFileName` to take a sysfs leaf name** — it treats `':'` as the *volume* separator, so `…\1-1.3:1.0` yields `"1.0"`, which silently defeats every colon-based filter here; use the local `LeafName` (splits on `\`/`/` only). Getting that wrong cost ~55 first-chance exceptions per enumeration (thousands per session, each with a formatted stack trace from SimHub's first-chance handler) and left the duplicate-VID+PID disambiguation — `TryResolveOwningDevice`, for e.g. a pair of mBoosters — permanently falling through to ordinal pairing. That one traversal is the only colon-bearing path in the file and reaches the filesystem through raw `GetFileAttributesW`: Wine resolves the path correctly, only the managed layer objects. Reads use `File.ReadAllText`, never `File.ReadAllBytes` — sysfs advertises 4096 bytes and returns fewer, which makes `ReadAllBytes` throw.
- `WineDevicePathMozaPort` — `IMozaPort` over raw `CreateFileW`/`SetCommState`/`ReadFile` on the unix device node (`Z:\dev\ttyACM2`). Under Wine the COM name is unknowable, so the connection opens the device path directly; `TryOpen` picks this implementation whenever `PortInfo.DevicePath` is non-empty and `SerialPortMozaPort` otherwise. Never call `SetupComm` on this path — it wedges Wine on a cold port. The ctor runs the **native cold-start warm-up** (`WineNativeExec` + `stty`) immediately before `CreateFileW`; this is the single choke point every open path funnels through, and a warm-up *timeout* refuses the open rather than entering Wine's comm-config unwarmed.
- `WineNativeExec` — runs a **native (non-Wine) Linux program** from inside the Wine process via `ntdll!__wine_unix_spawnvp` (the export `winebrowser` uses to reach `xdg-open`; `CreateProcess` refuses non-PE images and `start.exe /unix` only path-converts into `ShellExecuteEx`). `execvp` does the PATH lookup, so no shell is spawned. Two consumers, both things Wine structurally cannot do: the CDC-ACM warm-up above (a native open + termios completes `SET_LINE_CODING`, without which Wine's first comm-config IOCTL deadlocks the shared wineserver) and reaping stub orphans whose wineserver died — those reparent to init and are invisible to `Process.GetProcessesByName`. The blocking call runs on a thread joined with a timeout and **abandoned** on expiry (never aborted), and that thread owns and frees its own unmanaged argv. Design + verification: [`docs/linux-cold-start-fix.md`](linux-cold-start-fix.md).
- `WineComNameResolver` — **label only.** Wine's `<prefix>/dosdevices/comNN` symlink target is unreadable from inside the prefix (reparse tags need Wine's own `user.WINEREPARSE` xattr, `QueryDosDevice` answers `\Device\SerialN`, `GetFinalPathNameByHandle` would mean opening the tty). What works read-only is stat: Wine follows the symlink, so `comNN` reports the tty node's timestamps, and matching that tuple against a stat of `/dev/<tty>` identifies the pair without opening anything. Feeds the connection pill, the diagnostics dump and SimHub's Arduino-scan veto (which is COM-keyed, so without it SimHub would open the port we hold — wine ttys have no `O_EXCL`). An ambiguous match yields no label and nothing else changes.
- `MozaUsbIds` — the PID inventory and category routing (single source of truth, mirrored in [`docs/protocol/devices/usb-ids.md`](protocol/devices/usb-ids.md)).
- `MozaCommandDatabase` — 200+ command definitions (identity probes, settings, LED matrices, AB9/CM2/mBooster blocks).
- `MozaResponseParser` — bit-7 toggle + nibble-swap + wildcard matching; `busHint` disambiguates shared dev id 0x12 (base main vs AB9 vs mBooster); unwraps display sub-device identity; silently drops session control frames.
- `MozaProtocol` — constants + the two checksum helpers. Production code uses `CalculateWireChecksum()` (raw sum + `count(0x7E in body) × 0x7E`) on both send and verify — see [`docs/protocol/wire/checksum.md`](protocol/wire/checksum.md). Also `WheelEchoPrefixes`/`IsWheelEcho` for write-echo keepalive detection.
- `MozaHidReader` — HidSharp-based physical-input reader (steering/pedals/paddles/handbrake/buttons) enumerated by VID 0x346E + PID category; powers UI live-input bars and the `AZOM.*` input properties with no game running; mBooster axes route through the registry.

### Device management (`Devices/`, root managers)

- `MozaDeviceManager` — per-connection read/write API: wheel ID cycling, `ReadSettingsPaced` for large bursts, untracked `SendPresenceProbe` empty probes (absent devices cost one 5-byte frame per tick instead of a 3-retry storm), injectable `PendingResponseTracker` for per-pipe retransmit.
- `MozaAb9DeviceManager` — the active-shifter lane, shared by the **AB9** (PID `0x1000`) and **AB6** (PID `0x1002`): identity probe cascade, stored-setting reads (group 0x1E) vs writes (0x1F), FFB session-init handshake, engine-vibration/pulse/trigger/low-rate streaming frames, gear-shift intensity config. One manager, one `LastAb9Port`, one `MozaProfile.Ab9` block and one settings tab — with an AB9 *and* an AB6 attached only the first-enumerated one is claimed. The tab header, connect/detect log lines and diagnostics label resolve the model from `DiscoveredPid` via `MozaUsbIds.ActiveShifterShortName` (null PID under Wine/Proton probe discovery → the neutral `AB9/AB6`). AB6 protocol parity is assumed, not captured. Wire decode: [`docs/protocol/devices/ab9-shifter.md`](protocol/devices/ab9-shifter.md). The host-rendered 91 Hz engine-vibration loop lives in `Ab9EngineVibrationWorker` (PitHouse-replicating sub-stream set; period formula `K ≈ 3.95e11 / (rpm × freq_hz)`).
- `MozaHubDeviceManager` — dedicated Universal Hub lane when a base is also present; peripherals enumerate in parallel with first-responder ownership routing.
- `MozaStandalonePeripheralRegistry` / `StandalonePeripheralController` — one descriptor-driven lane per direct-attached pedal set / handbrake / **shifter** (config/calibration only; axes stay HID). On its own CDC pipe the peripheral is the root device, so each lane overrides its `MozaDeviceManager` to `main` (`0x12`) — the bus sub-device ids (pedals `0x19`, handbrake `0x1B`, shifter `0x1A`) only apply on a relayed pipe — and passes its command family as the parser's `busHint`. Each descriptor carries a `SettingsReadCommands` list, issued (paced, off the serial read thread) once the presence probe confirms the binary channel: that list is a dedicated pipe's **only** read path, because the shared `Mark*Detected` helpers latch on first sight and early-return, so the later `issueReads:true` call can never deliver them. Without it the tab renders `MozaData`'s placeholder defaults instead of the device's stored calibration. The HGP (`0x001E`) and SGP (`0x0023`) shifters have a settings surface (a "Shifter" tab): reverse-direction + paddle-sync for both, plus the SGP's 2 configurable LEDs (fixed 8-index palette) + brightness, and an HGP calibration routine. A base/hub-relayed shifter is detected as a `0x1A` sub-device on that pipe (`DeviceDetectionState.ShifterOwner`). The HGP auto-blip is host-side software with no wire command and is **not** implemented — see [`docs/protocol/devices/shifter-0x1A.md`](protocol/devices/shifter-0x1A.md).
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
5. **Active tick loop** (~30 ms) — per-tier `7d:23` value frames, string channels (type=0x05 on sess=0x01, polled ~4 Hz, emit on change with a 15 s keepalive floor), enable + sequence counter, peripheral parity polls + LED state polls (load-bearing wheel-engagement keepalives — see `Frames/TelemetryFrameCache.cs` comments), widget polls, retransmit drain, slow path (~1 Hz: 0x43 keepalives, mode frame, display config, 28x polls, sess=0x09 keepalive).

Key supporting pieces:

- `TelemetryFrameBuilder` / `TelemetryEncoder` / `TelemetryBitWriter` / `GameDataSnapshot` — bit-packed value-frame assembly; `Telemetry/Frames/CompressionTable.cs` is the canonical compression-code map.
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
- Diagnostics tab: identity dump, wheel dashboard state, session state, bandwidth (`WriteBudget` window + monotonic peak), wire-error counters, CRC reject counters, per-lane state for the dedicated standalone-peripheral pipes (`BuildStandalonePeripherals` — pedals/handbrake/HGP/SGP are their own connections, so the wheelbase/AB9/hub assignment slots never cover them and a pedals-only rig would otherwise read as a total detection failure), serial capture start/stop + ZIP bundle export (`DiagnosticsBundleWriter`: manifest + serial capture + diagnostics text + `MozaLog` snapshot + `upload-log.txt` when a report upload was attempted).
- Bug reports (`SettingsControl.BugReport.cs` + `UI/BugReport/`): the About tab's "Report a problem" card posts that same bundle to the Cloudflare Worker in [`worker/`](../worker/). `BugReportService` records every attempt into `BugReportUploadLog` — request size, HTTP status, the Cloudflare headers (`cf-ray`/`cf-mitigated` mark an edge/WAF block as opposed to a Worker rejection), response body, the full inner-exception chain, and on failure a DNS + system-proxy + plain-`GET /` probe (the Worker serves nothing at the root, so its answer there is an empty 404 with `cf-ray`; a non-empty body or any other status is interception). `DiagnosticsBundleWriter` pulls that log into the bundle on **both** paths, so a user whose uploads are refused can export by hand and the reason ships with the zip; the same record also goes to `MozaLog`, and the failure's short code (`HTTP 403`, `network: timeout`) is appended to the status line. The status line is a read-only `TextBox`, not a `TextBlock` — the ticket reference has to be selectable, and `CopyBugReportRefButton` copies it on its own. Reading the submitted reports is `worker/tools/bugreports.py` (`list` / `show` / `fetch -x` / `sync`), which downloads and unpacks bundles into `worker/bundles/<TICKET>/`; that directory, like the rest of `worker/`, is outside git, so see `worker/README.md` for the admin token and endpoint details.
- UI render reads from saved state (overlay/bundles), not `_data` — `_data` mirrors transient device responses and drifts (see `MozaWheelSettingsControl.MergeOverlayIntoData`).
- Custom WPF control library (`UI/Controls/` + `Themes/`): `SectionCard`, `SegmentedControl`, `OffOnToggle`, `PaletteStrip` (+ `ColorPickerDialog` custom chip), `KnobRingViz`, `BandwidthSparkline`, `ConnectionPill`, `MozaCurveEditor` (draggable-node output curve; `AllowHorizontalDrag` enables X-breakpoint edits — used by the wheelbase FFB output curve, where `LockLastNodeX` pins the last point at input=100 since the base has `base-ffb-curve-x1..x4` but no x5, and by the mBooster curve, which resamples its dragged X to fixed breakpoints host-side; also the FFB equalizer via style), `SteeringArc`, `TemperatureGraph`. Styles in `Themes/Generic.xaml`, tokens in `Themes/MozaTheme.xaml`, icons in `Themes/MozaIcons.xaml`.

### Profile system (`Settings/MozaProfile.cs`, `Settings/MozaProfileStore.cs`, `Settings/MozaPluginSettings.cs`)

Per-game configuration snapshots on SimHub's `ProfileBase`. State is split across four storage tiers:

- **Plugin-global** (`MozaPluginSettings` flat fields): connection toggles, last ports, probe-fallback opt-out, update-check state, etc.
- **Per-wheel-page** (`MozaPluginSettings` dicts keyed by SimHub page GUID): mzdash folder, telemetry enabled, sleep bundle, idle bundle (`*ByPageGuid`).
- **Per-(profile × wheel-page)** (`MozaProfile.WheelOverridesByPageGuid`): wheel LED/mode/brightness/colors/input modes + the per-game dashboard pick (`TelemetryProfileName`/`TelemetryMzdashPath`). Sentinel `-1`/null = fall through to baseline.
- **Per-game baseline** (`MozaProfile` directly): motor/FFB/handbrake/pedals, dash + base-ambient settings, gearshift tuning, `Ab9Settings`, `TelemetryDashboardKey`, `TelemetryChannelMappings` (profile × page × dashboard-key × URL → property path).

`MozaProfile.CaptureFromCurrent` captures only device-read-sourced state; UI handlers write overlays/profiles directly so a partial device read can't clobber user edits. The `ActiveTelemetry*` accessors on `MozaPlugin` (backed by `Settings/ProfileCoordinator.cs`) resolve the current wheel's page GUID and read/write the right tier. UI handlers go through the `WriteIf*` helpers so slider drags while disconnected persist without queueing writes. Migration history (schema v2→v9) is documented in [Settings storage and migration](#settings-storage-and-migration).

### Device extensions (`Devices/`)

- `MozaDeviceExtensionFilter` routes SimHub devices by `DescriptorUniqueId` GUID to the wheel / dash / wheelbase extensions; `MozaDeviceConstants` owns the GUID↔model-prefix registry (persisted with write-temp-then-Move). Wheelbases are keyed under a `base:` namespace in the same registry so a base token can't collide with a wheel prefix; `GetWheelModelPrefix` skips those rows and `GetBaseModelPrefix`/`IsBaseDevice` read them. The pre-per-model shared `BaseAmbientGuid` still matches, so an instance added under the old definition keeps routing until the user re-adds.
- `WheelModelInfo` — per-model LED layout descriptor (RPM/button/flag/knob counts, `bool? HasDisplay`) resolved from the firmware model name. `HasDisplay` gates all dashboard-related traffic via `ShouldDriveDashboard()` — screenless wheels must never see the display probe burst or session pipeline (drives them into a settings-read-timeout storm). The display-detected gate additionally defers telemetry start until the (slow-booting, ~20 s on CS Pro) display sub-device answers, with a 60 s wedge watchdog forcing one reconnect.
- LED managers (`MozaLedDeviceManager` wheel, `MozaDashLedDeviceManager`, `MozaBaseLedDeviceManager`, CM1/CM2 paths): virtual `ILedDeviceManager`s injected into SimHub's LED module via `LedDriverInjection` (which captures SimHub's original driver; the extensions' `End` restores it so SimHub never keeps Display()-calling a closed driver); model-aware index remapping, windowed `active+window` bitmasks, per-frame color chunks with palette-hash dedup, flag-LED routing to the meter sub-device, "default during telemetry" button override. See [`docs/protocol/leds/color-commands.md`](protocol/leds/color-commands.md).
- Extension settings DTOs (`Moza{Wheel,Dash,Base}ExtensionSettings`) apply into the profile/overlay tiers; the wheel DTO drain is one-shot (`WheelExtensionDrained`) so stale device JSON can't clobber plugin settings.
- `DeviceTemplates/` definitions deploy lazily on first detection; per-model wheel definitions are generated from `WheelModelInfo` and rewritten when a model's layout changes.
- **Per-model wheelbase definitions** (`DeployForBaseModel` → `GenerateBaseDeviceJson`) — one device per base model, named from the bare firmware token (`BaseModelInfo.ExtractPrefix("R16 Black # MOT-3-V01")` → `MOZA R16`). Firmware never reports the "Ultra" marketing suffix, so nothing derives it. The body carries `LedsFeature` only when the model has an ambient strip (`BaseModelInfo.KnownModels`, `LedsPerStrip == 0` = none) and `HapticsFeature` only when LFE firmware is present **and** `Settings.WheelbaseLfeSource == ShakeIt`. A base with neither gets **no definition at all** — a featureless descriptor builds an empty composite that sits permanently "scanning" — and a stale one is deleted. An unidentified base writes nothing either: the folder name *is* the model name. Deploy fires from both capability replies (`base-ambient-brightness` and `base-fw-version` in `DeviceProber`) plus the late `main-model-name` path in `MozaPlugin.Inbound.cs`; the staleness check (identity, schema, LED presence/count, haptics presence + motors + frequency band, PID) makes repeats free and is what re-deploys after a firmware upgrade adds LFE. A successful write removes the legacy shared `MOZA Wheel Base` folder.
- **Wheelbase LFE in ShakeIt** (`Devices/Haptics/MozaBaseHapticsBridge.cs`) — declaring `HapticsFeature` makes SimHub call `LedModuleDevice.DisablePrimary()` and add a `StandardProtocolConnectionDevice` that becomes the composite's only primary, so **the ambient LEDs stop unless that connection reports connected** (mechanism: [`docs/simhub.md`](simhub.md#device-builder-haptics-simhub-912)). `MozaBaseDeviceExtension.InjectDrivers` swaps its `Manager` for `MozaBaseConnectionManager`, whose `IsConnected()` is the *base pipe* state (broad, because it gates LEDs) and whose `GetDriverInstance()` returns `MozaBaseMotorsDriver` — that one gates on `IsBaseLfeHapticsReady` and forwards the three `MotorState`s to `PostShakeItLfeChannels`, so `BaseLfeEffectWorker` stays the single wire owner. Both swaps restore on `End`. The bridge types bind 9.12-era members and are only touched behind `MozaBaseHapticsBridge.IsSupported`, so an older SimHub degrades to "no haptics". The plugin's own LFE tab and ShakeIt would sum on the wire, so `Settings.WheelbaseLfeSource` picks exactly one: it gates the JSON, hides the LFE tab, drops `PostShakeItLfeChannels`, and silences the worker's own modes.
- **LFE channel defaults** — SimHub seeds a new haptics device's 24-effect profile with every channel enabled (its defaults key is hardcoded to `SimagicReactors`), which this base would sum into a 3x. `MozaWheelbaseLfeChannelsProvider` fixes user-added effects; the already-seeded profile is rewritten once per device instance by `MozaBaseHapticsBridge.NarrowStockChannelDefaults`, gated by `MozaBaseExtensionSettings.LfeChannelDefaultsNormalized` and matching only the exact all-on shape. Mechanism and the reflection traps: [`docs/simhub.md`](simhub.md#channel-activation-defaults--three-traps).
- Wheelbase LED devices are model-gated like wheels: `MozaBaseLedDeviceManager.ExpectedModelPrefix` keeps a leftover definition for another base model from driving the attached one with the wrong geometry (empty prefix = the legacy shared definition, always connected).
- **Device profile pictures** — SimHub renders a device profile's picture from a `thumbnail.png` sidecar next to `device.json`, not from a JSON field (mechanism + format spec: [`docs/simhub.md`](simhub.md#device-picture-thumbnailpng)). `DeviceTemplates/Thumbnails/<key>.png` holds the product renders, embedded by a csproj glob as `MozaPlugin.Devices.Thumbnails.<key>.png` and written out by `DeviceDefinitionDeployer.EnsureThumbnail`. The `<key>` is the **firmware model prefix** for generated wheel definitions (via `DeployForModel`), or an **explicit template key** for the resource-based definitions (dashes/base via `DeployFromResource` — e.g. the CM2 dash keys `CM2.png`, listed in `TemplateThumbnails`). Both deploy paths call `EnsureThumbnail` on the definition-is-current path as well as on a fresh write, so art reaches devices whose definition was deployed by an older plugin build; `RefreshDeployedThumbnails` (at Init) tops up every already-deployed folder — wheels and templated devices alike. It is idempotent (skips when the on-disk bytes match) and cosmetic: a thumbnail-only write never flips the "restart SimHub" result, since the picture appears at the next start regardless. Devices with no art (the lookup misses) simply get none. Add a wheel's art by dropping `<prefix>.png` in — no code change; wheels sharing a rim get a copy per prefix (e.g. `FSR.png` = `W13.png`, the identical-looking FSR V1/V2). A new templated-device picture also needs its key threaded through the matching `DeployFromResource` caller + `TemplateThumbnails`. Wheelbase art is keyed by model token through `BaseThumbnails` (`R21`→`R21U.png`, `R25`→`R25U.png`) since renders are named for the product, not the bare token. An `Unwired/` subfolder parks renders whose prefix isn't known yet; it is deliberately outside the (non-recursive) glob so nothing unresolvable ships in the DLL. Regenerate with `tools/make-device-thumbnail`, which enforces the 512-long-side / 8-bit PaletteAlpha spec.

### SDK emulation (`Sdk/`)

- `MozaSdkCoapServer` (CoAP-over-UDP, port 40266) + `PitHouseUdp.MozaControlUdpServer` (plain UDP CBOR, port 40288) expose wheel state/config to third-party tools; both hold refs into `MozaData`/`HardwareApplier` and are per-instance.
- `CoapStubManager` spawns the `MOZA Pit House.exe` impersonation stub (separate `CoapStub/` project) with a registry redirect; the manager is process-persistent across plugin reloads because Wine intermittently hangs its teardown path — `TryStop(ms)` bounds every stop call. Lifecycle is serialized by `_sdkLifecycleGate` and driven from both Init and the live UI toggles.

## Subsystem Deep-Dives

### Standalone dashboard pipeline (CM2)

The MOZA CM2 Racing Dash (USB PID `0x0025`) is a standalone USB dashboard with no wheelbase — the full dashboard pipeline (screen telemetry, dashboard library + kind=4 switch, stored RPM colors, meter-mode + threshold writes) runs against it. CM2 has 16 physical RPM LEDs (no buttons, no separate flag strip).

- **Detection.** `ConnectionCoordinator.MarkStandaloneDashboardDetectedFromUsb(reason)` (idempotent; wired at Init for persistent-wire reuse and at TryConnect/TryConnectDashboard) flips detection on USB PID alone, deploys the CM2 device.json, applies the dash profile, and starts the sender. `MozaData.IsDashboardConnected` is a third truth source for `IsConnected`. A **bridged** dash instead reaches the plugin as the 0x14 sub-device on the primary pipe, detected either by a `dash-rpm-indicator-mode` reply or by an empty presence-probe ACK (`DeviceProber.MarkDashDetected`) — and the bridge is whatever owns that pipe, a wheelbase **or** a Universal Hub. `IsCm2Present`/`IsCm2BehindBaseCandidate` therefore require only a live primary connection + `DashDetected`, never `BaseDetected`; a hub-only rig is a supported topology, and requiring a base there hid the dash from the UI entirely. `MarkDashDetected` deploys the CM2 definition **provisionally** — the class isn't known until the CM1 discriminator answers.
- **Telemetry retargeting.** `TelemetrySender.TargetDeviceId` (default wheel 0x17) is set to `MozaProtocol.DeviceMain` (0x12) for a standalone CM2 / `DeviceDash` (0x14) for a bus-bridged CM2 by `DashboardBindingCoordinator.ApplyTelemetrySettings` via `plugin.PreferredStandaloneDashboardTargetDeviceId`. The setter invalidates the display-config cache and rebuilds per-tier frame builders; the dev id is threaded through every session/control/display frame. `TelemetryInboundDispatcher` keys on `TargetDeviceIdSwapped` with a wide 0x21/0x41/0x71 fan-in in standalone mode. `ResolveAutoPolicy` pins Era2026 for standalone targets.
- **Meter-config + live LED surface.** The `cm2-*` command block (write group 0x32) covers brightness, modes, thresholds, and 16 stored colors; `HardwareApplier.ApplyCm2DashboardConfig` programs it. These commands address the `cm2-main` device type, which `MozaDeviceManager.GetDeviceId` resolves through `PreferredStandaloneDashboardTargetDeviceId` to the **same topology-dependent target as the telemetry — dev `0x14` for a base-bridged CM2, dev `0x12` for a standalone-USB CM2**. Live per-frame RPM LEDs reuse the wheel RPM-bar commands retargeted to that same device via `WriteSettingForDevice`/`WriteArrayForDevice` (working hypothesis — unverified against real CM2 captures; fall back to firmware-driven LEDs if flat).
- **Device template.** `DashCm2Guid` is distinct from the legacy dash GUID; `DeviceDefinitionDeployer.DeployDashboard(pid)` routes CM2 PID → CM2 template and compares GUID + PID + product name on existing files.
- `.mzdash` upload to CM2 storage is out of scope; the upload scaffolding remains untouched.

### Concurrent dual-screen pipelines (wheel screen + CM2/CM1 dash)

A user may drive **both** a wheel screen and a separate dash concurrently, each with its own dashboard + channel mappings, via dedicated lanes on the shared wheelbase connection:

- **Stream-slot lanes** (`MozaSerialConnection`, 32 slots): wheel pipeline at slot-base 0, AB9/mBooster 11–17, the second dash pipeline at slot-base 18. `TelemetrySender.StreamSlotBase` offsets every periodic frame; `ClearStreamSlots(from,count)` wipes one lane on stop. Co-resident senders set `SharesConnection` + `StrictInboundFilter` so each consumes only its own device's 0xC3 replies.
- **Wheel lane:** the tier-def `_telemetrySender` (dev 0x17), or `Fsr1DisplayDriver` for an FSR1.
- **Dash lane:** `MozaPlugin._cm2Sender` (a second `TelemetrySender`) — the wire target **varies by routing** (set in `DualDisplayCoordinator.EnsureCm2Pipeline` via `dev = usbCm2 ? DeviceMain : DeviceDash`): an own-USB CM2 is driven at the bridge/main dev `0x12` (slot-base 0); a bus-bridged CM2 is the meter at dev `0x14` (slot-base 18, coexisting with the wheel screen). PitHouse `cm2.pcapng` drives a bus CM2's session, LED config (0x32), and telemetry (0x43) entirely on dev `0x14` — `0x14` is what engages and answers (b2h session chunks), while `0x12` behind a base is the base main and never engages the session layer. Orchestrated by `Telemetry/DualDisplayCoordinator.EnsureCm2Pipeline()` (gated on `ActiveDashTelemetryEnabled && (busCm2 || usbCm2)` — wheel-independent, with a 12 s teardown dwell so a one-tick detection blip can't abort a live cold-start), with the saved-dashboard re-assert in `TickCm2DashboardReassert`. CM2 mappings are keyed under `Cm2PageGuid`/`Cm2DashKey`, independent of the wheel's. The enable gate is **dash-scoped**, not the wheel's: `ProfileCoordinator.ActiveDashTelemetryEnabled` resolves an explicit `Cm2PageGuid` entry → the wheel page's value while a wheel is identified (one shared toggle for wheel+dash rigs) → **on** when no wheel resolves at all, since a dash on a wheel-less rig is the only display and `TelemetryEnabledDefaultForNewWheels` is a wheel-keyed signal.
- **UI:** `DashboardManagementControl` is parameterized by `IsCm2Target` so the dash page routes combo/mappings/switches to the CM2 sender + CM2 keys.
- **Radar/track-map with both screens:** the car-position state (locations, stable radar slots, track folder, map bounds) is profile-independent — identical for both displays — so `GameDataSnapshot.PopulateCarPositionsShared` computes it once per game frame under `s_carPosLock` (keyed on the `StatusDataBase` reference; SimHub allocates a fresh one per `DataUpdate`) and both senders' ticks copy the cached result. Both screens render identical slot assignments, and the slot hold-timers advance once per frame regardless of sender count.

### CM1 base-bridged dash (group-0x35 driver + discriminator)

The **CM1** does not speak tier-def — it is driven by a flat keyed value stream on group 0x35 (`<2-byte key><BE float32>` records), with the same 0x32/0x81 switch + `Table 7 Param 6` page-report family as the FSR1. Wire decode: [`protocol/devices/dash-0x14.md`](protocol/devices/dash-0x14.md) § "CM1 Racing Dash"; emitter verified byte-exact against `FSR1_CM1.pcapng`.

- **Driver:** `Telemetry/Display/Cm1DisplayDriver.cs` (~50 ms tick, dash-lane slots) streams the flat `Cm1DashboardCatalog` field set via `Cm1DisplayEmitter`.
- **Discriminator** (`DualDisplayCoordinator.TickCm1Discriminator`, per PollStatus tick): classifies a bridged dash on **positive evidence only**. It re-probes the CM1-exclusive group-0x0E param register (`MozaDeviceManager.SendCm1ParamProbe`, untracked) ~1 Hz; a 0x8E answer (routed via `MozaPlugin.OnMessageReceived` → `NoteDashParamReadAnswered`) latches CM1 after a 5 s settle, and a tier-def catalog (`CatalogCount > 0`) proves CM2 and drops the `_cm2Sender`'s suppressed engagement watchdog. There is deliberately **no** no-catalog timeout — absence of a catalog equally describes a slow/starved CM2, and that fallback is what once mislabeled real CM2s; an unclassified dash just stays in discrimination. Latching deploys the CM1 device definition, deletes the speculative CM2 copy `MarkDashDetected` wrote (`RemoveSpeculativeCm2Dashboard`, guarded so a real USB 0x0025 CM2 is never deleted), stops any tier-def sender, and starts the CM1 driver. `DashIsCm1` is **session-only** — re-derived each boot, never persisted.
  - The decision is anchored on the **bridged dash's own presence**, not on a running pipeline: a live tier-def `_cm2Sender` only supplies the `CatalogCount` fast path (and while one is cold-starting the discriminator waits for it to reach Active before anchoring its clock). Identification is therefore independent of `ActiveDashTelemetryEnabled` and of any wheel — a dash whose lane the user turned off, or a hub-only / wheel-less rig, still classifies correctly and just doesn't stream. Gating identification on that sender is what left a hub-bridged CM1 permanently wearing the CM2 device definition (bundle MGXWJ3YH). The current state is reported on the Diagnostics tab's `Dash class:` line.
- **Mapping/UI:** flat field mappings under `Cm1PageGuid` (`MozaProfile.Cm1FieldMappings`), page index in `Cm1ActiveDashboardByGuid`; the dash page switches to CM1 mode rows. Field semantics are best-effort — the catalog ships blank defaults the user assigns.

### FSR V1 (group `0x42`) display wheel — as built

The FSR V1 (model-name `FSR`, hw `RS21-D03*`) uses a fundamentally different transport: the host pushes pre-computed display field values as fixed-schema group-0x42 records at ~28 Hz; there is no catalog, no tier-def, no session 0x02. Full wire decode: [`protocol/devices/wheel-0x17.md`](protocol/devices/wheel-0x17.md) § Group 0x42. Distinct from FSR V2 (`W13`), which is a standard tier-def wheel.

- **Detection & routing:** `WheelModelInfo.KnownModels` entry `("FSR", "FSR V1", …, hasDisplay: false)` deliberately keeps the tier-def pipeline/display probe/wedge watchdog off; `MozaPlugin.IsFsr1DisplayWheel` is the routing flag. The push runs in the standalone `Telemetry/Display/Fsr1DisplayDriver` (own ~35 ms timer) so a dash pipeline can run concurrently; `DualDisplayCoordinator.StartFsr1DriverIfNeeded()` starts/stops it.
- **Catalog/emitter:** `Fsr1DashboardCatalog.cs` (per record type: field defs with offsets/encoding/capability/default property + the partial page-index → record-type map) and `Fsr1DisplayEmitter.cs` (startup declaration sweep, live records, 0x43 keepalive, the `g32/81` select command) — byte-exact-verified against captures.
- **Switching, both directions:** host→wheel via group 0x32 cmd 0x81 BE32 page index 0..18 (dropdown → `SetActiveFsr1Index(idx, sendToWheel:true)`, drained by the driver); wheel→host via the `Table 7, Param 6 Written: <idx>` firmware log (HID combo switches included), parsed by `Fsr1Cm1MappingCoordinator.TryFollowFsr1DashboardLog` so the plugin auto-follows.
- **User mapping:** per wheel-GUID → record-key → field in `MozaProfile.Fsr1DashboardMappings` with per-field input-scale min/max, edited in the standard channel mapper (`ChannelMappingRowFactory.BuildFromFsr1Catalog`).
- **Open items:** 5 of 19 page indices confirmed; field semantics for record types `06/09/0d/0e` decoded structurally but unnamed (exposed as raw slots); `b1`/`b2` meaning. Remaining unknowns must come from captures — do not fill a field on a guess. Tools: `tools/fsr1-0x42-extract`, `tools/fsr1-field-decode`, `tools/fsr1-page-field-map.py` (per-page b1/b2 + per-byte variance), `tools/fsr1-hid-decode`.

### Dashboard switch state machine

`DashboardBindingCoordinator.ApplyTelemetryDashboardFromProfile(MozaProfile)` is the single entry point for binding the wheel's displayed dashboard to the current game profile's saved pick. It fires from `ApplyProfile` and from `PollStatus`'s retry loop. Returns `true` once resolved, `false` to defer.

**Inputs.** The saved `TelemetryDashboardKey` (`wheel:<id>` / `file:<filename>:<sha1-8>` / `builtin:<name>`) resolves to a target name, then to a target slot in the wheel's `ConfigJsonList`. All three key kinds funnel through the same slot lookup.

**Readiness gate (defer when):** sender null; sender not Active (kind=4 before preamble completes is silently dropped); `IsInSilenceCooldown` (a prior kind=4 is inside the silence window); wheel state null/empty (the `_cachedLastState` static fallback covers plugin reloads). Retries ride the coordinator's lock-guarded `_pending` record (5 min deadline, 30 s warn cadence); the defer reason surfaces in the UI status label via `PendingDashboardApplyDescription`.

**Apply path:** if the wheel is already on the target slot (`WheelReportedSlot` — wire-level ground truth from the wheel's own b2h type-04 records — or the reload-surviving `LastEmittedKind4Slot`), no wire action. Otherwise emit kind=4 via `OnDashboardSwitched(slot)`; with `EnableHotRenegotiation` (default) there is no Stop+Start — the tick handler emits a paced multi-emission tier-def burst echoing the wheel's END marker (see [`protocol/tier-definition/handshake.md`](protocol/tier-definition/handshake.md) § In-game dashboard switch).

**Wheel-initiated switches:** the wheel emits its own kind=4 with the new slot; `WheelSlotTracker` detects it, arms the hot-reneg burst without re-emitting, and raises `WheelInitiatedSwitch` → the coordinator resolves slot→name, updates `ActiveTelemetryProfileName`, re-applies settings, and raises `DashboardSelectionChanged` for the UI.

**Catalog re-sync probe:** when tier-def building finds unbound catalog channels (incomplete cold-start advertisement), a single kind=4 to the current slot is scheduled (800 ms deferred, ~8 s throttle via `Lifecycle/CatalogResyncProbe`) to nudge the wheel into re-advertising.

**Cold-start session pairing:** tier-def stays pinned to sess 0x01 / FF records to 0x02 regardless of which session the cold-start catalog landed on — following the catalog put the tier-def on 0x02 where CS-Pro never binds it.

**Silence gates** (`Lifecycle/SilenceGate`, per-instance Interlocked timestamps): `MarkStopped` enforces ~11 s of host silence between Stop and the next open (the wheel's sess=0x09 interlock — empirically load-bearing even on cold start); `MarkSwitchEmitted` drives the UI cooldown (200 ms hot / 11 s legacy).

**Reset semantics:** `LastEmittedKind4Slot` (static) and `ConfigJsonClient._cachedLastState` (static) survive plugin reload and are cleared on wheel hot-swap via `ResetBindingTracking`; `_lastAppliedDashboardKey` short-circuits repeated applies per instance.

**Observed timings:** game switch ~5–15 s (cold-start preamble + apply); in-game switch ~1–2 s (hot-reneg); legacy non-hot path ~35 s (the 11 s gates are the wheel's interlock and can't be shortened).

**Diagnostic tools** (`tools/`): `wire-dashboard-switches`, `wire-sess-lifecycle`, `wire-kind4-response`.

### Internationalization (i18n)

User-visible strings live in `Resources/Strings.resx` (English neutral/master) plus per-culture variants for de, el, es, fr, it, ko, nb, pt, ru, vi, and zh-Hans, and a joke "Bark" locale riding the Windows `qps-ploc` pseudo-locale slot (so `CultureInfo` accepts it without a custom-culture registration). XAML uses `{x:Static res:Strings.<Key>}`; C# uses `MozaPlugin.Resources.Strings.<Key>`. The strongly-typed accessor `Resources/Strings.Designer.cs` is hand-edited (one line per key), not generated.

**Single-DLL deployment.** Every locale is embedded directly inside `MozaPlugin.dll` — no satellite assemblies. The csproj sets `<WithCulture>false</WithCulture>` per non-neutral resx with explicit `ManifestResourceName` keys; `Strings.Designer.cs` builds a BCP-47-keyed `ResourceManager` dictionary and `Get(key)` walks `Thread.CurrentUICulture`'s parent chain (passing `InvariantCulture` to each `GetString` so no satellite lookup happens), falling back to English.

`Resources/LanguageResolver.cs` resolves the culture at `Init` and `GetWPFSettingsControl`: explicit picker pref → SimHub's own `Culture` (from `GlobalSimhubSettings.json`) → OS culture → English. The UI thread's culture is reassigned inside `GetWPFSettingsControl` before constructing `SettingsControl` (x:Static evaluates at parse time).

**Adding a new key:** add the master entry in `Strings.resx`, a matching `<data>` line in **every** other `Strings.*.resx`, and a one-line property in `Strings.Designer.cs`. All three in the same change — a missing resx entry returns the key string at runtime; a missing Designer property fails XAML compile.

**Adding a new language:** (1) copy `Strings.resx` to `Strings.<culture>.resx` and translate; (2) add the `<EmbeddedResource>` entry to the csproj matching the existing pattern; (3) add the culture to `SupportedCultures` + `DisplayNames` in `LanguageResolver.cs`; (4) add the `_byCulture` row in `Strings.Designer.cs`. The Options-tab picker enumerates `SupportedCultures` automatically.

## How-To Workflows

### Adding new device settings

When adding a new setting that is written to the device, it must also be saved/restored with the profile system. Pick the storage tier first — see the four-tier classification in [Profile system](#profile-system-uimozaprofilecs-uimozaprofilestorecs-uimozapluginsettingscs) — then walk:

1. **`Protocol/MozaCommandDatabase.cs`** — add the command definition (name, device, read/write groups, command ID, payload size, type).
2. **`Devices/MozaDeviceManager.cs`** — add the device type mapping in `GetDeviceId()` if it's a new device.
3. **`Protocol/MozaProtocol.cs`** — add device ID / group constants if needed.
4. **`MozaData.cs`** — add volatile field(s) and `UpdateFromCommand` case(s).
5. **`Devices/DeviceProber.cs`** — add to the appropriate per-device read array so it's read after detection; add detection logic in `DetectDevices()` if needed. Push the new value through the matching `Apply*ToHardware` method in `Hardware/HardwareApplier.cs` (sentinel-guard the write: `if (value >= 0) …`). Wheel-overlay fields source from `Eff(overlay?.X ?? -1, profile.X)`; profile-level from `profile.X`; per-wheel-page from the matching `*ByPageGuid` dict.
6. **Storage** by tier:
   - **Per-game baseline** → property on `MozaProfile`, copy in `CopyProfilePropertiesFrom()`. Only add a `CaptureFromCurrent()` line if the value flows from device reads; UI-edited fields are written by handlers directly and capture would clobber them.
   - **Per-(profile × wheel-page)** → property on `WheelOverride`, copy in `WheelOverride.Clone()`. No capture.
   - **Per-wheel-page** → a `Dictionary<Guid, …>ByPageGuid` on `MozaPluginSettings` + a `MozaPlugin` accessor resolving the current page GUID. Legacy-field draining is no longer needed — the historic v2–v9 migrator has been removed.
   - **Plugin-global** → property on `MozaPluginSettings`.
7. **XAML** — add UI controls to `SettingsControl.xaml` or the matching device settings control.
8. **UI handler** by tier:
   - Profile-level: `_plugin.UpdateActiveProfile(p => p.X = val)` → `WriteIf<Device>` → `SaveSettings()`.
   - Wheel-overlay: `_plugin.UpdateActiveWheelOverlay(o => o.X = val)` → `WriteIfWheelDetected` → `SaveSettings()`.
   - Per-wheel-page: `_plugin.ActiveXxx = val;` → `WriteIf<Device>` → `SaveSettings()`.
   - Plugin-global: `_plugin.Settings.X = val;` → `SaveSettings()`.
   - Colors via `WriteColorIf<Device>`, arrays via `WriteArrayIfWheelDetected`. `SaveSettings()` is debounced (500 ms); update `_data.X` too so the next refresh tick shows the value before the device echo.

Every setting that writes to the device on UI change must round-trip through profiles or the per-wheel-page dicts — a transient-only field is lost on game/profile switch.

**Host-rendered settings** (e.g. AB9 engine vibration, the wheelbase LFE effects in `Devices/Haptics/BaseLfeEffectWorker.cs`) skip steps 1–4 entirely: no command-DB entry, no `MozaData` field, no probe — just the profile property + UI, with the periodic worker reading the profile on its next tick. **One-shot host-side config writes that ARE device-persisted but bypass the command DB** (e.g. AB9 gear-shift intensity) follow 6–8 plus an explicit `Send*` call in both the UI handler and `ApplyAb9ToHardware`. New host-rendered *stream* lanes need a `StreamKind` slot in `Protocol/MozaSerialConnection.cs` — bump `StreamSlotCount` and add the member past the LED lanes (the static ctor asserts the regions fit).

### Settings storage and migration

Settings are split across `MozaPluginSettings` (plugin-global values plus the per-wheel-page `*ByPageGuid` dicts), `MozaProfile` (per-game baseline) and `WheelOverride` (per-profile × wheel-page).

**There is no schema-version counter and no migrator class.** The historic v2–v9 `SettingsMigrator` and the `[JsonExtensionData]` legacy-key mechanism it relied on have both been removed now that the destination dicts are the only storage. Migrations are written inline in `Init` (`MozaPlugin.cs`), each guarded by its own sentinel so it runs exactly once:

- the legacy Stable/Dev update-channel enum drains when `UpdateChannelId` is still empty (retired Dev users land on Stable with a cleared `LastSeen*` cache);
- the `VerboseWireDebugLog` default flip is gated by the dedicated `VerboseWireDebugLogDefaultMigrated` bool, so anyone who re-enables it afterwards keeps it.

Follow that pattern for new migrations: put the sentinel next to the setting it guards, drain in `Init`, and never read the legacy field again. A retired setting can also be dropped outright when nothing reads it any more — Newtonsoft ignores the orphaned key on the next load, which is how the per-wheel-page firmware-era pick (`WheelTelemetryEraByPageGuid`) was removed along with its UI selector.

Use `MozaDeviceConstants.ResolveWheelGuid(prefix)` to map a model prefix to a page GUID — never hard-code GUIDs. The device-JSON → plugin-settings drain is one-shot, gated by `WheelExtensionDrained` with fill-only semantics, because SimHub doesn't reliably re-serialize device JSON before shutdown.

### Adding a new telemetry channel

Most new channels only need step 1 — set `simhub_property` (and optional `simhub_scale`) in `Data/Telemetry.json` and the resolver pulls the value via `PluginManager.GetPropertyValue`. Steps 2–4 are only required when the channel should also be readable via the legacy `SimHubField` snapshot path.

1. **`Data/Telemetry.json`** — ensure URL, compression, package_level; add `simhub_property` for a default mapping and `simhub_scale` when units differ (e.g. `0.01` for 0–100 → 0–1, `57.2957795` for radians → degrees). `simhub_scale` belongs to the JSON's **own** property: a user mapping (per-dashboard override or the global master mapper) replaces the property and resets the channel's scale to 1, so an override supplies the value in the channel's wire unit and a differing source unit is converted with a formula (`[prop]*100`). Inheriting the JSON scale silently zeroed integer channels — `ErsState` is `uint3` (4-bit, truncating) with scale `0.01`, so every mapped strat value under 100 packed as 0 — and saturated the ×100 percent channels; `DashboardProfileStore.ResolveDefaultBinding` is where the two are resolved together. `simhub_property: "@internal/<key>"` locks the channel to a plugin-computed value (add the `case` in `SimHubPropertyResolver`'s internal-channel switch).
2. *(optional)* `Telemetry/Dashboard/DashboardProfileStore.cs` — URL suffix → `SimHubField` in `UrlFieldMap`.
3. *(optional)* `Telemetry/Dashboard/DashboardProfile.cs` — extend the `SimHubField` enum.
4. *(optional)* `Telemetry/Frames/GameDataSnapshot.cs` — field + `FromStatusData()` + `GetField()` case.

### String-typed channels (out-of-band on sess=0x01)

Channels declared `compression: "string"` in `Telemetry.json` (23 total: `TrackId`, `CarModel`, `SessionTypeName`, etc.) do **not** bit-pack into value frames. They ride `type=0x05` sub-msgs on session 0x01: `[type=0x05][size_LE u32 = 2+strlen][channel_idx u8][0x80|strlen u8][ASCII]`. Wire reference: [`protocol/sessions/session-0x01-channel-protocol.md`](protocol/sessions/session-0x01-channel-protocol.md).

- `DashboardProfileStore` routes string-compression URLs to `MultiStreamProfile.StringChannels` — a `string` compression must **never** get a `CompressionTable` entry (a bit-packed slot the firmware refuses to bind).
- `ChannelCatalogParser.FindIdxByUrl(url)` is the authoritative idx source — the wheel re-indexes per dashboard; never hardcode idx values.
- `StringValueBuilder.Build(idx, value)` is byte-exact-verified; max strlen 127.
- `TelemetrySender.TickEmitStringValues()` polls the string channels at ~4 Hz (their sources — TrackId, CarModel — change on the order of minutes) and emits on change with a 15 s keepalive floor, chunked through the standard session path on the tier-def session's seq.
- Test mode reads `TestSignal.StringValue` directly; game-running mode resolves the bound SimHub property live via `PropertyStringResolver` (see `ResolveStringChannelValue`).

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
- **Runtime (Windows only):** `Microsoft.Win32.Registry` (in `mscorlib`) — the `MozaPortDiscovery` registry source. Under Wine/Proton that source is empty and `LinuxUsbEnumerator` reads `/sys` through Wine's unix drive instead; neither needs a package reference. The serial-probe fallback is skipped entirely when the sysfs source is live, and per-lane hard-disabled by the enumeration-only managers.
- **SimHub DLLs** (`libs/SimHub/`, reference-only, not packaged): `SimHub.Plugins.dll`, `GameReaderCommon.dll`, `SimHub.Logging.dll`, `SerialDash.dll`, `BA63Driver.dll`, `HidSharp.dll`. A daily GitHub Actions workflow creates PRs when new SimHub versions release (those bot PRs don't trigger the PR Build workflow — dispatch it manually with the PR number if you need a test build).

**Important:** build against the newest supported SimHub — the PluginSdk ships older DLLs missing newer interface members, causing `TypeLoadException` at runtime. Always update `libs/SimHub/` from an actual SimHub installation (`libs/SimHub/VERSION` records which one).

**Running on older SimHub builds.** Compiling against the newest DLLs bakes that build's signatures into the IL, so a host whose contract differs fails at *load* time, not compile time — and the break runs in both directions. SimHub 9.12.0 is the live example: it added an `overrideState` colour layer (the dashboard "Device LEDs override" component), which changed `ILedDeviceManager.Display` (a sixth `Func<Color[]>`) and `LedDeviceState`'s constructor (a matching `Color[]`). Two shapes of fix, both in [`Devices/Led/SimHubLedCompat.cs`](../Devices/Led/SimHubLedCompat.cs) and its callers:

- **Interface members** — declare *both* overloads on the implementing class. Implicit interface implementations are bound by the CLR at type-load time by name and signature, so each host picks the overload its own interface declares. The compat overload must be marked `virtual` by hand: only a public **virtual** method can fill an interface slot (ECMA-335 II.12.2), and Roslyn marks only the overload matching the compile-time interface. A non-virtual compat overload compiles and looks right while the old host still throws `TypeLoadException`.
- **Constructors / concrete methods** — a direct `new` bakes one signature in, so build the call once through reflection and bind arguments to the host's parameters by name (`SimHubLedCompat.CreateState`). Parameters the host doesn't declare are dropped; ones we don't recognise take their own declared default.

Neither the compiler nor a run against a single SimHub version catches a regression here, so verify with [`tools/simhub-compat`](../tools/simhub-compat/) — it compares a built DLL's metadata references, interface implementations (including virtualness) and `MethodImpl` records against each version's DLL set, and exits non-zero on a mismatch:

```bash
tools/simhub-compat/simhub-compat bin/x86/Release/MozaPlugin.dll \
    libs/SimHub \
    "/path/to/an/older/SimHub"
```
