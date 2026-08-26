# SimHub Plugin API Reference

Notes on SimHub's plugin API gathered from decompiling `SimHub.Plugins.dll` and building the MOZA plugin. SimHub does not publish official plugin docs, so this serves as a working reference.

## Plugin Interfaces

A plugin class implements one or more interfaces and is decorated with metadata attributes:

```csharp
[PluginDescription("...")]
[PluginAuthor("...")]
[PluginName("...")]
public class MyPlugin : IPlugin, IDataPlugin, IWPFSettingsV2
```

### IPlugin

Core lifecycle — every plugin implements this.

| Member | Description |
|--------|-------------|
| `PluginManager PluginManager { set; }` | Injected by SimHub before `Init` |
| `string LeftMenuTitle { get; }` | Label shown in SimHub's left nav |
| `ImageSource PictureIcon { get; }` | Icon for the nav (nullable) |
| `void Init(PluginManager pluginManager)` | Called once at startup |
| `void End(PluginManager pluginManager)` | Called on shutdown |

### IDataPlugin

Adds a per-frame callback driven by the game loop.

| Member | Description |
|--------|-------------|
| `void DataUpdate(PluginManager pluginManager, ref GameData data)` | Called every frame. `data.GameRunning`, `data.NewData.Rpms`, `data.NewData.MaxRpm`, flags, etc. |

### IWPFSettingsV2

Provides a settings UI shown in SimHub's plugin pane.

| Member | Description |
|--------|-------------|
| `Control GetWPFSettingsControl(PluginManager pluginManager)` | Return a WPF `UserControl` |

## Settings Persistence

SimHub provides JSON-based settings persistence via extension methods on `IPlugin`:

```csharp
// Read (deserializes from SimHub's settings directory, or creates default)
_settings = this.ReadCommonSettings<MySettings>("key", () => new MySettings());

// Write
this.SaveCommonSettings("key", _settings);
```

The settings object can be any serializable class. Newtonsoft.Json is used for serialization.

## Properties and Actions

Plugins can expose named properties (readable from dashboards/other plugins) and actions (triggerable from input mappings):

```csharp
// Properties — lambda evaluated each frame
this.AttachDelegate("MyPlugin.SomeValue", () => _data.SomeValue);

// Actions — triggered by user-bound buttons/keys
this.AddAction("MyPlugin.DoSomething", (a, b) => { ... });
```

## Logging

```csharp
SimHub.Logging.Current.Info("message");
SimHub.Logging.Current.Error("message");
```

Writes to SimHub's log file.

## Formula Engine (NCalcEngineBase) Thread Safety

`SimHub.Plugins.OutputPlugins.Dash.TemplatingCommon.NCalcEngineBase` (implements `IFormulaEngine`) is the engine behind dashboard formulas; the plugin reuses it for NCalc channel mappings (see [`ncalc-channel-mapping.md`](ncalc-channel-mapping.md)). Verified by decompiling `SimHub.Plugins.dll` (ilspycmd): **one instance is NOT safe for concurrent evaluation.** The evaluation path mutates unsynchronized per-instance state:

- `VariableStack` — a plain `HashSet<string>` `Add`/`Remove`d around **every** `[property]` variable resolution (its recursion guard). Two concurrent evaluations on one instance can corrupt it or trip spurious recursion detection.
- The stateful dashboard functions — `blink()`, `changed()`, `increasing()`/`decreasing()`, `minimum()`/`maximum()`, `scroll()`, `inertia()` — read-modify-write plain `Dictionary`s keyed by expression.
- `rand` — a `System.Random` (not thread-safe; concurrent use can wedge it to all-zero output).

The engine does lock where SimHub expects cross-thread access (`CacheLock` around the expression caches; the shared result caches are concurrent types), but evaluation itself assumes a single caller. SimHub's own usage matches: engine instances are created per consumer context (an `instanceCount` static tracks them), not shared across threads.

**Consequence for plugins:** serialize all evaluation on a given instance (a lock around `ParseValueOrDefault`), and give independent consumer threads their own instances rather than sharing one — construction is cheap (`new NCalcEngineBase()` binds to `PluginManager.Instance` internally). Side effect worth knowing: the stateful functions keep per-instance state, so two engines evaluating the same `blink(...)` expression advance independent timers.

## Application Lifecycle (Restart / Exit)

`PluginManager` exposes a supported hook for asking SimHub to exit — and optionally relaunch itself. This is the mechanism a plugin uses to restart SimHub after an in-app self-update so the freshly-swapped DLL gets loaded. Verified by decompiling `SimHub.Plugins.dll` (`SimHub.Plugins.PluginManager`):

```csharp
// public instance method — no reflection needed
public void RequestApplicationExit(bool restart);

// public getter (private setter); true once teardown has begun
public bool IsApplicationExiting { get; }
```

`RequestApplicationExit` decompiled:

```csharp
public void RequestApplicationExit(bool restart)
{
    if (IsInitialized)   // private field; set true after plugins load
    {
        Logging.Current.Info("Application exit requested from " + new StackTrace());
        this.ApplicationExitRequested?.Invoke(this, restart);   // internal event
    }
}
```

- `restart: true` → SimHub exits **and relaunches**.
- `restart: false` → plain exit.

The call raises the **internal** `ApplicationExitRequested` event carrying the `restart` flag; the SimHub WPF shell (`SimHubWPF.exe` — not in `SimHub.Plugins.dll`) subscribes and performs the actual teardown and (when `restart` is true) relaunch. The method is a no-op until `PluginManager.IsInitialized` is true, so call it after `Init` has run (e.g. from a UI action), not during early startup.

SimHub also ships an "Automatic restart" user setting (the strings `HasAutomaticRestartEnabled` and "Automatic restart delay (seconds):" are present in the assembly), but its owning type and gating are not on `PluginManager` and were not reverse-engineered — `RequestApplicationExit(true)` is the load-bearing call and works regardless. (Note: `RequestReload` is a method on `DevicesPlugin`, not `PluginManager`; it reloads device/dashboard definitions without a full process restart.)

The MOZA plugin calls `RequestApplicationExit(true)` from `MozaPlugin.RestartSimHub()`, wired to the "Restart SimHub" button the update banner shows after an in-app update is installed.

## Profile System (`SimHub.Plugins.ProfilesCommon`)

SimHub has a built-in per-game profile system. Plugins provide a profile data class and a store; SimHub handles switching profiles when the active game changes.

### Core Types

**`ProfileBase<TProfile, TSettings>`** — Base class for a profile. Subclass and add your settings properties.

| Member | Description |
|--------|-------------|
| `string Name { get; set; }` | Profile display name |
| `string DisplayName { get; }` | Formatted name (includes game info) |
| `Control ProfileContentControl { get; }` | Optional WPF control for editing profile fields. Return `null` if not needed. |
| `void CopyProfilePropertiesFrom(TProfile p)` | Deep-copy all settings from another profile (used by clone) |

**`ProfileSettingsBase<TProfile, TSettings>`** — Base class for the profile store. Manages the collection of profiles and current selection.

| Member | Description |
|--------|-------------|
| `List<TProfile> Profiles` | All profiles |
| `TProfile CurrentProfile { get; set; }` | Active profile |
| `ObservableCollection<TProfile> SortedProfiles` | Sorted/observable, used by UI bindings |
| `ProfileSwitchingMode ProfileSwitchingMode` | How profiles switch on game change |
| `string FileFilter` | File dialog filter for import/export (e.g. `"My profile (*.myprofile)\|*.myprofile"`) |
| `void Init()` | Call during plugin init. Reads `PluginManager.Instance.GameName` and selects the matching profile. |
| `void AddProfile(TProfile p)` | Add a new profile |
| `event EventHandler CurrentProfileChanged` | Fires when the active profile changes (game switch or manual) |
| `void InitProfile(TProfile p)` | Override to run setup on deserialized profiles |

**`ProfileSwitchingMode`** — Enum controlling auto-switch behavior:
- `Disabled` — Manual only
- `LastUsedPerGame` — Remember last profile per game
- `BestMatch` — SimHub picks the closest match

**`IProfileSettings` / `IProfileSettings<TProfile>`** — Interfaces implemented by `ProfileSettingsBase`. Required by the UI controls.

### Wiring Up Profiles

```csharp
// In Init():
var store = _settings.ProfileStore;
if (store.Profiles.Count == 0)
    store.Profiles.Add(new MyProfile { Name = "Default" });
store.Init();  // reads current game, selects profile
store.CurrentProfileChanged += OnProfileChanged;

// Apply initial profile
if (store.CurrentProfile != null)
    ApplyProfile(store.CurrentProfile);
```

The store is typically a property on your settings class so it's persisted alongside other settings via `SaveCommonSettings`.

### Profile UI Controls

SimHub provides ready-made WPF controls for profile management. These live in `SimHub.Plugins.ProfilesCommon` (assembly `SimHub.Plugins`).

**`ProfileCombobox`** — Styled dropdown showing all profiles with game icons.

```xml
xmlns:profilescommon="clr-namespace:SimHub.Plugins.ProfilesCommon;assembly=SimHub.Plugins"

<profilescommon:ProfileCombobox ProfileSettings="{Binding MyProfileStore}" />
```

| Property | Type | Description |
|----------|------|-------------|
| `ProfileSettings` | `IProfileSettings` (DependencyProperty) | The profile store to bind to |

Internally renders a MahApps `MetroComboBox` bound to `ProfileSettings.SortedProfiles` with `SelectedItem` bound to `ProfileSettings.CurrentProfile`.

**`ProfileList`** — Complete profile management bar: dropdown + Profiles manager / Edit / Clone / New buttons.

```xml
<profilescommon:ProfileList DataContext="{Binding MyProfileStore}" />
```

| Property | Type | Description |
|----------|------|-------------|
| `AdditionalActionButtons` | `object` | Slot for extra buttons (content property) |
| `RightContent` | `object` | Slot for content on the right side |

The `ProfileList` internally creates a `ProfileCombobox` and wires it to the `DataContext`. It also creates a `ProfileHandler` that provides click handlers for New/Clone/Edit/Manage.

**`ProfilesManager<TProfile, TSettings>`** — Modal dialog for full profile management (import/export, drag-drop, reorder, profile switching mode).

```csharp
var manager = new ProfilesManager<MyProfile, MyStore>(store);
manager.ShowDialogWindow(parentControl);
```

Inherits from `SimHub.Plugins.UI.SHDialogContentBase`.

**`ProfileHandler<TProfile, TSettings>`** — Used internally by `ProfileList`. Provides `LoadProfile_Click`, `CloneProfile_Click`, `EditProfile_Click`, `NewProfile_Click` handlers.

## UI Utilities

**`SimHub.Plugins.UI.SHDialogContentBase`** — Base class for modal dialogs. Call `.ShowDialogWindow(parent)` to display.

## GameData Reference

Available in `DataUpdate` via `data.NewData` (type `GameReaderCommon.StatusDataBase`).

Check `data.GameRunning` and `data.NewData != null` before accessing.

**Core motion/telemetry:**

| Property | Type | Description |
|----------|------|-------------|
| `Rpms` | `double` | Current engine RPM |
| `FilteredRpms` | `double` | Smoothed RPM |
| `SpeedKmh` | `double` | Speed in km/h |
| `FilteredSpeedKmh` | `double` | Smoothed speed |
| `Gear` | `string` | **String**, not int. Values: `"R"` (reverse), `"N"` (neutral), `"1"`–`"N"` (gears). Cast with `int.TryParse()`. |
| `Throttle` | `double` | Throttle position 0–100 |
| `Brake` | `double` | Brake position 0–100 |
| `BestLapTime` | `TimeSpan` | Best lap time |
| `CurrentLapTime` | `TimeSpan` | Current lap time elapsed |
| `LastLapTime` | `TimeSpan` | Last completed lap time |
| `DeltaToSessionBest` | `double?` | Gap to session best in seconds (nullable) |
| `FuelPercent` | `double` | Fuel remaining 0–100% |
| `DRSEnabled` | `int` | **Int, not bool.** Nonzero = DRS active. |
| `ERSPercent` | `double` | ERS energy 0–100% |

**Tyre wear:**

| Property | Type | Description |
|----------|------|-------------|
| `TyreWearFrontLeft` | `double` | Tyre wear 0–100% |
| `TyreWearFrontRight` | `double` | |
| `TyreWearRearLeft` | `double` | |
| `TyreWearRearRight` | `double` | |

**Flags (nonzero = active):**

| Property | Type |
|----------|------|
| `Flag_Checkered` | `int` |
| `Flag_Black` | `int` |
| `Flag_Orange` | `int` |
| `Flag_Yellow` | `int` |
| `Flag_Blue` | `int` |
| `Flag_White` | `int` |
| `Flag_Green` | `int` |

**Gotchas:**
- `Gear` is a `string`, not `int`. `"R"` cannot be cast to int directly.
- `DRSEnabled` is `int`, not `bool`. Check `!= 0`.
- `DeltaToSessionBest` is nullable (`double?`). Use `?? 0.0`.

## PluginManager Properties

Plugins can read SimHub-wide properties via `pluginManager.GetPropertyValue("name")`. Returns `object`; cast or convert as needed. Available at startup unless noted.

| Property | Type | Description |
|----------|------|-------------|
| `DataCorePlugin.GameData.TemperatureUnit` | `string` | Global temperature unit preference (`"Celsius"` or `"Fahrenheit"`), configured at first launch |
| `DataCorePlugin.GameData.CarSettings_RPMShiftLight1` | `double` | Shift light zone 1 progress (0.0–1.0). Game-dependent; requires active session. |
| `DataCorePlugin.GameData.CarSettings_RPMShiftLight2` | `double` | Shift light zone 2 progress (0.0–1.0). Game-dependent; requires active session. |
| `DataCorePlugin.GameData.CarSettings_RPMRedLineReached` | `int` | Nonzero when RPM is at/above redline. Game-dependent; requires active session. |

Note: `ShiftLight1`/`ShiftLight2` are progress values within their respective shift zones (not absolute RPM). They map to the three-zone LED pattern common on steering wheels (e.g. 3 green + 4 red + 3 blue). `RedLineReached` triggers blink behavior.

## Device Extension System

SimHub has a device definition and extension system for hardware devices (LED controllers, button boxes, displays). Plugins can register device extensions that add tabs and behavior to devices in SimHub's "Devices" section.

### Device Templates (`.shdevicetemplate`)

A `.shdevicetemplate` is a ZIP file containing three files that registers a device type with SimHub:

- **`device.json`** — Device metadata and USB detection
- **`defaults.json`** — Default device settings
- **`picture.png`** — Device thumbnail

Templates are placed in `DevicesDefaults/StandardDevicesTemplatesUser/` (survives SimHub updates).

**`device.json` schema:**
```json
{
  "Brand": "Manufacturer Name",
  "Name": "Device Model",
  "DetectionDescriptor": {
    "IsValid": true,
    "iVID": 13422,
    "iPID": 4,
    "IgnoreForArduino": true
  },
  "StandardDeviceId": "UniqueDeviceId",
  "InheritedFrom": "D8415EF5-1052-451F-916F-B286531AD0FE",
  "IsDeprecated": false,
  "MaximumInstances": 1,
  "MinimumSimHubVersion": "9.5.0",
  "TemplateVersion": 1
}
```

Key fields:
- `iVID`/`iPID` — USB Vendor/Product ID in **decimal**. `iPID: 0` causes SimHub to mark `IsValid: false`, disabling auto-detection.
- `InheritedFrom` — **Required.** UUID of a base device type. Without it, device creation fails with NullReferenceException. Known base types:
  - `D8415EF5-1052-451F-916F-B286531AD0FE` — Simple LED device (MLD, Delta SL-20)
  - `4D631FFA-B696-4F4A-BF7C-A1F35621529D` — Dashboard/DDU device
  - `EC6EA501-35F4-4009-9E46-B46A79A04CC1` — Wheel/pedal device
- `StandardDeviceId` — String identifier used as `DeviceTypeID`. At runtime, may be suffixed with `_UserProject` or `_Embedded`.

**`defaults.json` schema:**

The correct structure depends on the `InheritedFrom` base type. **`DeviceTypeID` must be the base template GUID** (not the device's own `StandardDeviceId`) — using the device's own string ID causes `LedModuleDevice.SetSettings()` to throw `KeyNotFoundException` on every startup when a saved device instance exists.

For **D8415EF5** (simple LED wheel/bar devices):
```json
{
  "InstanceId": "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx",
  "DeviceTypeID": "D8415EF5-1052-451F-916F-B286531AD0FE",
  "Settings": {
    "ledModuleSettings": {
      "VID": 13422,
      "PID": 4,
      "Ledcount": 10,
      "ButtonsCount": 0,
      "IsEnabled": true,
      "_LEDsBrightness": 100.0
    },
    "leds": { },
    "buttons": { },
    "encoders": { },
    "matrix": { },
    "raw": { }
  }
}
```

For **4D631FFA** (dashboard/DDU devices):
```json
{
  "InstanceId": "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx",
  "DeviceTypeID": "4D631FFA-B696-4F4A-BF7C-A1F35621529D",
  "Settings": {
    "LCD": { },
    "LEDS": { }
  }
}
```

Key fields:
- `InstanceId` — A fixed GUID unique to this device type (generate once, embed in the template). Using `null` lets SimHub assign one, but a fixed GUID ensures stable identity across installs.
- `DeviceTypeID` — **Must be the base template GUID** (e.g. `D8415EF5-...`), NOT the `StandardDeviceId` string. Using the device's own string causes `LedModuleDevice.SetSettings()` to fail loading saved instances.
- `ledModuleSettings` — Hardware-level LED config. `VID`/`PID` identify the physical serial driver. For virtual devices with no real driver, omit `VID`/`PID`.
- `leds`, `buttons`, etc. — Empty objects in defaults; SimHub populates these when the user configures effects. They must be present as keys for `LedModuleDevice.SetSettings()` to resolve them correctly.

SimHub caches template metadata in `PluginsData/DevicesDesccriptorCache.json`. Delete the cache entry to force re-read after template changes.

### Device Builder Format (`.shdd` / `.shdp`)

SimHub 9.11+ includes a Device Builder that produces a newer format, distinct from `.shdevicetemplate`. The editable file is `.shdd`; the distributable (end-user installable) export is `.shdp`. Both are ZIP files containing a single `DeviceName/device.json`.

This format **does not use `InheritedFrom`** or `defaults.json` — it directly declares features, avoiding the `LedModuleDevice.SetSettings()` registry issue entirely.

**`device.json` schema:**
```json
{
  "DescriptorUniqueId": "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx",
  "SchemaVersion": 1,
  "MinimumSimHubVersion": "9.11.8",
  "DeviceDescription": {
    "BrandName": "Manufacturer",
    "ProductName": "Device Name"
  },
  "LedsFeature": {
    "IsIndividualLedsSectionEnabled": true,
    "PhysicalLedsMappings": {
      "Items": [
        { "SourceRole": 1, "SourceIndex": 0, "RepeatCount": 10, "RepeatMode": 1 },
        {}, {}, {}, {}, {}, {}, {}, {}, {},
        { "SourceRole": 2, "SourceIndex": 0, "RepeatCount": 13, "RepeatMode": 1 },
        {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}
      ]
    },
    "LogicalTelemetryLeds": { "LedCount": 10, "Segments": [], "IsEnabled": true },
    "LogicalButtonsSection": {
      "IsButtonEditorEnabled": false,
      "Items": [ ... ],
      "IsEnabled": true
    },
    "IsEnabled": true
  },
  "HardwareInterface": {
    "HardwareInterface": {
      "TypeName": "LedsStandardHIDProtocol",
      "HIDUsagePage": "0xFF00",
      "HIDUsage": "0x77",
      "HIDReportId": "0x68",
      "HIDReportSize": 64,
      "DeviceDetection": { "Vid": "0x346E", "Pid": "0x0004" }
    }
  }
}
```

Key fields:
- `DescriptorUniqueId` — Replaces `StandardDeviceId`. Used to match in `IDeviceExtensionFilter` (check `DeviceTypeID` for this GUID, possibly with `_UserProject` or `_Embedded` suffix).
- `SourceRole` in `PhysicalLedsMappings` — `1` = telemetry/RPM LEDs, `2` = button LEDs. Empty `{}` items fill the remaining slots in a repeated group.
- `TypeName` — Hardware communication protocol. `"LedsStandardHIDProtocol"` sends LED colors over HID reports. For virtual devices with no real HID hardware, use a placeholder `Vid`/`Pid` (e.g. `0x9999`) that won't match any real device; the HID path then stays idle while the virtual `ILedDeviceManager` injection handles the LED pipeline.
- `.shdp` files are installed via SimHub's device import UI (not copied to `StandardDevicesTemplatesUser/`).

#### Device picture (`thumbnail.png`)

The picture SimHub shows for a Device Builder profile is **not** a `device.json` field — it is a
`thumbnail.png` sidecar in the same folder as `device.json`. Decoded from `SimHub.Plugins.dll` 9.11.21:

```csharp
// DeviceDescription ctor
Thumbnail = new PictureWrapper(descriptor, "thumbnail.png", 512);   // filename, FitSize

// PictureWrapper
[JsonIgnore] public BitmapImage Picture =>          // <descriptor dir>/thumbnail.png
    File.Exists(f) ? Images.ByteToImage(File.ReadAllBytes(f)) : null;
```

- `DeviceDescription.Thumbnail` is `[JsonIgnore]`, so it never serializes into `device.json`. The
  path is `Path.Combine(DescriptorContent.Directory, "thumbnail.png")`, where `Directory` is
  assigned by the descriptor loader to the folder holding `device.json`.
- The Device Builder descriptor overrides `Icon` to return `Thumbnail.Picture` when present, so the
  sidecar **takes precedence** over the `DevicesLogos/` GUID-named fallback (which is
  CWD-relative and keyed on the `_UserProject`/`_Embedded`-suffixed `DeviceTypeID`).
  With neither, SimHub renders `DevicesLogos\nopicture.png`.
- **Format.** The builder's picture importer runs
  `WuQuantizer.OptimizetoPng(Images.FitImage(512, 512, …, Color.Transparent))` — fit *inside* a
  512×512 box (aspect preserved, long side becomes 512; **not** padded to a square), then
  palette-quantized. All 19 stock thumbnails under `DevicesDefinitions/Embedded/` are 512 on the
  long side, 8-bit PaletteAlpha, 13–72 KB.
- `ImageQualityAnalyzer.Analyze` backs a builder-UI quality warning: it wants a real alpha channel
  and a tight crop (it flags >3 % transparent margin as improperly cropped).
- `ExportToShdd` zips the whole folder (`SearchOption.AllDirectories`), so `thumbnail.png` travels
  inside `.shdd`/`.shdp` exports automatically.
- A sibling `buttoneditor.png` is the button-editor backdrop; only relevant with
  `IsButtonEditorEnabled: true`.

This plugin ships per-wheel art through this mechanism — see
[DEVELOPMENT.md](DEVELOPMENT.md#device-extensions-devices).

### IDeviceExtensionFilter

Tells SimHub which `DeviceExtension` to attach to which device type. SimHub discovers implementations via assembly scanning.

```csharp
using SimHub.Plugins.Devices;
using SimHub.Plugins.Devices.DeviceExtensions;

public class MyExtensionFilter : IDeviceExtensionFilter
{
    public IEnumerable<Type> GetExtensionsTypes(DeviceInstance device)
    {
        // DeviceTypeID may be suffixed at runtime (_UserProject, _Embedded)
        var typeId = device.DeviceDescriptor.DeviceTypeID ?? "";
        if (typeId == "MyDeviceId" || typeId.StartsWith("MyDeviceId_"))
            yield return typeof(MyDeviceExtension);
    }
}
```

### DeviceExtension

Abstract base class for adding a settings tab and behavior to a device.

```csharp
using SimHub.Plugins.Devices.DeviceExtensions;

internal class MyDeviceExtension : DeviceExtension
{
    // Tab title in the device's settings panel
    public override string ExtentionTabTitle => "My Tab";

    // Called when device is created or after game change
    public override void Init(PluginManager pluginManager) { }

    // Called on app exit or device deletion
    public override void End(PluginManager pluginManager) { }

    // Called every game loop
    public override void DataUpdate(PluginManager pluginManager, ref GameData data) { }

    // Called when no saved profile exists
    public override void LoadDefaultSettings() { }

    // Called to reload a saved device profile (game change, user action)
    public override void SetSettings(JToken settings, bool isDefault) { }

    // Called to export current settings for profile save
    public override JToken GetSettings() { return JToken.FromObject(settings); }

    // WPF control for the extension tab
    public override Control CreateSettingControl() { return new MyControl(); }

    // Actions available for button mapping
    public override IEnumerable<DynamicButtonAction> GetDynamicButtonActions() { yield break; }

    // The device this extension is attached to
    // DeviceInstance LinkedDevice { get; }
}
```

`SetSettings()`/`GetSettings()` are the per-game profile mechanism — SimHub calls `SetSettings()` on game change with the saved profile's JSON, and `GetSettings()` when saving.

### Accessing the LED Effects Engine

SimHub computes LED colors per-frame based on user-configured effects (RPM indicators, flags, speed limiter animations, etc.). For devices with proprietary protocols, the extension can read these computed colors and forward them.

**Architecture:**
```
Device Instance
  └── LedModuleDevice (sub-device, inherits from CompositableDeviceInstance)
        └── LedModuleSettings
              ├── RGBLedsDriver (effects engine)
              │     └── GetResult() → Color[]
              └── DeviceDriver (USB/HID connection — may be disconnected)
```

**Key classes:**

| Class | Namespace | Description |
|-------|-----------|-------------|
| `LedModuleDevice` | `SimHub.Plugins.OutputPlugins.GraphicalDash.LedModules` | Sub-device handling LEDs. Has `ledModuleSettings` field. |
| `LedModuleSettings` | Same namespace | Abstract class with `LedsDriver` property and `Display()` method. |
| `RGBLedsDriver` | `SimHub.Plugins.DataPlugins.RGBDriver` | Effects engine. `GetResult()` returns `System.Drawing.Color[]`. |
| `LedResult` | `SimHub.Plugins.DataPlugins.RGBDriver` | Sparse LED state: `Dictionary<int, Color>` indexed by position. Has `ToArray()`. |

**Accessing from a DeviceExtension:**

```csharp
using SimHub.Plugins.DataPlugins.RGBDriver;
using SimHub.Plugins.OutputPlugins.GraphicalDash.LedModules;

private RGBLedsDriver _ledsDriver;

public override void Init(PluginManager pluginManager)
{
    foreach (var instance in LinkedDevice.GetInstances())
    {
        if (instance is LedModuleDevice lmd)
        {
            _ledsDriver = lmd.ledModuleSettings?.LedsDriver;
            break;
        }
    }
}

public override void DataUpdate(PluginManager pluginManager, ref GameData data)
{
    Color[] colors = _ledsDriver?.GetResult();
    if (colors != null)
    {
        // Forward colors[0..N] to your device's proprietary protocol
    }
}
```

`GetResult()` applies brightness and returns the final `Color[]` array.

**Problem:** SimHub's effects UI is gated on the LED driver being "connected." If the built-in driver can't connect to the hardware (shows "searching device..."), the effects configuration is disabled. Polling `GetResult()` directly works but users can't configure effects. The solution is to inject a virtual `ILedDeviceManager`.

### ILedDeviceManager (Virtual Driver Injection)

SimHub's LED pipeline flows through `ILedDeviceManager` on `LedModuleSettings.DeviceDriver`. By replacing this with a custom implementation, you can:
1. Report as always-connected (enabling effects UI)
2. Receive computed LED colors directly in `Display()`
3. Forward them to proprietary hardware

**Interface** (namespace `SimHub.Plugins.OutputPlugins.GraphicalDash.PSE`, assembly `SimHub.Plugins.dll`):

```csharp
public interface ILedDeviceManager
{
    LedModuleSettings LedModuleSettings { get; set; }
    LedDeviceState LastState { get; }

    event EventHandler BeforeDisplay;
    event EventHandler AfterDisplay;
    event EventHandler OnConnect;
    event EventHandler OnError;
    event EventHandler OnDisconnect;

    void Display(Func<Color[]> leds, Func<Color[]> buttons, Func<Color[]> encoders,
                 Func<Color[]> matrix, Func<Color[]> rawState, bool forceRefresh,
                 Func<object> extraData = null,
                 double rpmBrightness = 1.0, double buttonsBrightness = 1.0,
                 double encodersBrightness = 1.0, double matrixBrightness = 1.0);

    bool IsConnected();
    string GetSerialNumber();
    string GetFirmwareVersion();
    object GetDriverInstance();
    void Close();
    void ResetDetection();
    void SerialPortCanBeScanned(object sender, SerialDashController.ScanArgs e);
    IPhysicalMapper GetPhysicalMapper();
    ILedDriverBase GetLedDriver();
}
```

**Required assemblies:**
| Assembly | Provides |
|----------|----------|
| `SimHub.Plugins.dll` | `ILedDeviceManager`, `LedModuleSettings`, `LedModuleDevice` |
| `BA63Driver.dll` | `LedDeviceState`, `IPhysicalMapper`, `NeutralLedsMapper`, `ILedDriverBase` |
| `SerialDash.dll` | `SerialDashController.ScanArgs` |

**`LedDeviceState`** (namespace `BA63Driver.Interfaces`):
```csharp
public class LedDeviceState
{
    public Color[] LedsState { get; }
    public Color[] ButtonsState { get; }
    public Color[] EncodersState { get; }
    public Color[] MatrixState { get; }
    public Color[] RawState { get; }
    public double RpmBrightness { get; }
    public double ButtonsBrightness { get; }
    public double EncodersBrightness { get; }
    public double MatrixBrightness { get; }
    public object ExtraData { get; set; }

    public LedDeviceState(Color[] leds, Color[] buttons, Color[] encoders,
        Color[] matrix, Color[] raw,
        double rpmBrightness = 1.0, double buttonsBrightness = 1.0,
        double encodersBrightness = 1.0, double matrixBrightness = 1.0);
}
```

**Virtual driver implementation pattern:**

```csharp
using BA63Driver.Interfaces;
using BA63Driver.Mapper;
using SimHub.Plugins.OutputPlugins.GraphicalDash.LedModules;
using SimHub.Plugins.OutputPlugins.GraphicalDash.PSE;

internal class VirtualLedDriver : ILedDeviceManager
{
    public LedModuleSettings LedModuleSettings { get; set; }
    public LedDeviceState LastState { get; private set; }

    // Events (required by interface, may not need to be fired)
    public event EventHandler BeforeDisplay;
    public event EventHandler AfterDisplay;
    public event EventHandler OnConnect;
    public event EventHandler OnError;
    public event EventHandler OnDisconnect;

    // Always report connected — this enables the effects UI
    public bool IsConnected() => true;

    public string GetSerialNumber() => "VIRTUAL";
    public string GetFirmwareVersion() => "1.0";
    public object GetDriverInstance() => this;
    public void Close() { }
    public void ResetDetection() { }
    public void SerialPortCanBeScanned(object sender, SerialDashController.ScanArgs e) { }
    public IPhysicalMapper GetPhysicalMapper() => new NeutralLedsMapper();
    public ILedDriverBase GetLedDriver() => null;

    public void Display(Func<Color[]> leds, Func<Color[]> buttons,
        Func<Color[]> encoders, Func<Color[]> matrix, Func<Color[]> rawState,
        bool forceRefresh, Func<object> extraData = null,
        double rpmBrightness = 1.0, double buttonsBrightness = 1.0,
        double encodersBrightness = 1.0, double matrixBrightness = 1.0)
    {
        var ledColors = leds?.Invoke() ?? Array.Empty<Color>();
        var buttonColors = buttons?.Invoke() ?? Array.Empty<Color>();
        var encoderColors = encoders?.Invoke() ?? Array.Empty<Color>();
        var matrixColors = matrix?.Invoke() ?? Array.Empty<Color>();
        var rawColors = rawState?.Invoke() ?? Array.Empty<Color>();

        // Store state (required — SimHub reads LastState for NCalc formulas)
        LastState = new LedDeviceState(ledColors, buttonColors, encoderColors,
            matrixColors, rawColors, rpmBrightness, buttonsBrightness,
            encodersBrightness, matrixBrightness);

        // Forward ledColors to your device here
    }
}
```

**Injecting the driver** (from a DeviceExtension):

The `DeviceDriver` setter on `LedModuleSettings` is `protected`, so reflection is needed:

```csharp
foreach (var instance in LinkedDevice.GetInstances())
{
    if (instance is LedModuleDevice lmd && lmd.ledModuleSettings != null)
    {
        var driver = new VirtualLedDriver();
        driver.LedModuleSettings = lmd.ledModuleSettings;

        var prop = typeof(LedModuleSettings).GetProperty("DeviceDriver",
            BindingFlags.Public | BindingFlags.Instance);
        prop?.GetSetMethod(nonPublic: true)?.Invoke(lmd.ledModuleSettings,
            new object[] { driver });
    }
}
```

After injection, SimHub's LEDs tab shows "Connected" and the full effects configuration UI is available. SimHub calls `Display()` every frame with the computed `Func<Color[]>` callbacks, which the virtual driver evaluates and forwards.

**Dynamic connection state:** `IsConnected()` can return a dynamic value (e.g. based on hardware detection) instead of always `true`. When the state changes, fire the `OnConnect` or `OnDisconnect` event so SimHub updates the LED pipeline. Without firing these events, SimHub may not notice the transition and will not resume `Display()` calls after a reconnection. The events should be fired from the device extension's `DataUpdate()` (called every frame regardless of connection state), not from `Display()` itself (which stops being called when disconnected). Internal state (cached bitmasks, brightness, wake-up flags) should be reset on disconnect so the device re-initializes cleanly on reconnect.

**`LedModuleSettings.Display()` internals:**
```csharp
bool exclusive = IndividualLEDsMode == IndividualLEDsMode.Exclusive && RawDriver != null;
DeviceDriver.Display(
    () => OverrideResult(exclusive ? new Color[0] : (LedsDriver?.GetResult(100.0) ?? new Color[0])),
    () => OverrideResult(!exclusive ? (ButtonsDriver?.GetResult(100.0) ?? new Color[0])
                                    : (UseButtonsDefaultColors ? ButtonsColorManager?.DefaultColors : null) ?? new Color[0]),
    () => OverrideResult(!exclusive ? (EncodersDriver?.GetResult(100.0) ?? new Color[0])
                                    : (UseButtonsDefaultColors ? EncodersColorManager?.DefaultColors : null) ?? new Color[0]),
    () => OverrideResult(MatrixDriver?.GetResult(...) ?? new Color[0]),
    () => OverrideResult(IndividualLEDsMode == IndividualLEDsMode.Disabled
                            ? new Color[0]
                            : (RawDriver?.GetResult(100.0, Color.Transparent) ?? new Color[0])),
    rpmBrightness: GetEffectiveLedsBrightness(),
    buttonsBrightness: GetEffectiveButtonsBrightness(),
    ...);
```

**`IndividualLEDsMode`** (enum in `SimHub.Plugins.OutputPlugins.GraphicalDash.LedModules`):
- `Disabled` — no individual-LED overrides. `rawState` callback returns `Color[0]`.
- `Combined` — both logical drivers (`LedsDriver`/`ButtonsDriver`/`EncodersDriver`) and `RawDriver` run. `rawState` returns the individual overrides; logical channels return their normal output. The device manager merges raw over logical.
- `Exclusive` ("Individual LEDs only" in the SimHub UI) — **only** `RawDriver` runs. SimHub forcibly passes `Color[0]` to the `leds` callback regardless of what `LedsDriver` would produce. The `buttons` and `encoders` callbacks return `ButtonsColorManager.DefaultColors` / `EncodersColorManager.DefaultColors` if `UseButtonsDefaultColors` is true, otherwise `Color[0]`. Only `rawState` carries effect output.

**Practical consequence for `ILedDeviceManager.Display()` implementations:** Do not early-return when `ledColors.Length == 0` or `encoderColors.Length == 0` before applying rawState. In Exclusive mode the logical channel is empty by design; the raw overrides must be merged first (typically by extending the empty channel array up to the device's physical LED count), then the per-channel processing fires off the merged array. If the raw merge is gated behind a non-empty check on the logical channel, individual-only effects silently never reach the hardware.

### Device Definition Locations

| Path | Description | Survives Update |
|------|-------------|-----------------|
| `DevicesDefinitions/Embedded/` | Built-in device definitions (binary `.def` files) | No |
| `DevicesDefinitions/User/` | User-created definitions | Yes |
| `DevicesDefaults/StandardDevicesTemplatesOffline/` | Built-in `.shdevicetemplate` files | No |
| `DevicesDefaults/StandardDevicesTemplatesUser/` | Custom `.shdevicetemplate` files | Yes |
| `DevicesDefaults/StandardDevicesTemplatesOnline/` | Downloaded templates | — |
| `DevicesDefaults/*.shdevice` | Instantiated device defaults (UUID-named JSON files) | — |
| `PluginsData/Common/Devices/index.json` | Active device instances | — |
| `PluginsData/DevicesDesccriptorCache.json` | Template metadata cache | — |
| `DevicesLogos/` | Device images by GUID (PNG files) | — |

### Gotchas and Practical Notes

**`LedModuleDevice.SetSettings()` KeyNotFoundException:** If a saved device instance exists in `PluginsData/Common/Devices/index.json` and SimHub throws `KeyNotFoundException` inside `LedModuleDevice.SetSettings()` on startup, the root cause is almost always `DeviceTypeID` in `defaults.json` being set to the device's own `StandardDeviceId` string instead of the base template GUID (e.g. `D8415EF5-1052-451F-916F-B286531AD0FE`). `LedModuleDevice.SetSettings()` uses that GUID to resolve handlers in an internal registry; an unrecognized string fails silently in loading but throws on the dictionary lookup inside `SetSettings()`. The error is non-fatal (device still connects) but LED effect profiles saved by the user fail to reload. Fix by using the base GUID as `DeviceTypeID`.

**Virtual driver injection timing:** Do not inject the `ILedDeviceManager` virtual driver in `DeviceExtension.Init()`. SimHub calls `Init()` before calling `LedModuleDevice.SetSettings()` during `LoadDevices()`. Injecting the driver first replaces the `DeviceDriver` reference that `SetSettings()` may need to resolve saved effect state. Defer injection to the first `DataUpdate()` call instead — by that point, `SetSettings()` has already run and the LED pipeline is ready to receive the virtual driver.

**Template deletion:** SimHub deletes the `.shdevicetemplate` file from `StandardDevicesTemplatesUser/` when a user removes the device. Plugins should re-deploy the template on startup or the device won't be available to re-add.

**Cache:** SimHub caches template metadata in `DevicesDesccriptorCache.json`. Templates are only read at startup. After deploying a changed template, either delete the cache entry or bump `TemplateVersion` in `device.json` to force re-read.

**DeviceTypeID format:** Embedded devices (`.def` files) use UUID-style DeviceTypeIDs with suffixes like `_UserProject` or `_Embedded` (e.g. `a5272f03-fc8b-4e03-a708-a6d192e450f6_UserProject`). Template-based devices use the `StandardDeviceId` string. The `IDeviceExtensionFilter` should match both the exact string and the suffixed variant.

**Instantiated devices:** All 170+ `.shdevice` files in `DevicesDefaults/` use UUID-based DeviceTypeIDs. These are the default settings for embedded device types, not user instances.

**Assembly version mismatch:** The `SimHub.Plugins.dll` shipped in the PluginSdk may be older than the runtime version. Interfaces can have additional members in newer versions. The runtime DLL throws `TypeLoadException` if an interface implementation is missing members. Always build against the actual runtime DLL, not the SDK copy. Key assemblies that may need updating:
- `SimHub.Plugins.dll` — core plugin/device API
- `BA63Driver.dll` — `LedDeviceState`, `IPhysicalMapper`, `ILedDriverBase`
- `SerialDash.dll` — `SerialDashController.ScanArgs`

**LED pipeline event:** `PluginManager.OnLedsUpdate` is an `internal static event` that fires after LED data is computed each frame. Not accessible from plugins without reflection. The `ILedDeviceManager.Display()` injection is the supported path.

## ShakeIt V3 — Custom Haptic Output Devices

How a plugin surfaces a proprietary-protocol device (wheelbase LFE channel, pedal vibration motors) as a ShakeIt output. Verified by decompiling `SimHub.Plugins.dll` **9.11.21** with ilspycmd (`ilspycmd -o <dir> -p libs/SimHub/SimHub.Plugins.dll -r libs/SimHub`). Namespace root is `SimHub.Plugins.DataPlugins.ShakeItV3` (**DataPlugins**, not OutputPlugins). Type/member names survive SimHub's obfuscation pass in this version, but they are not a public API contract — re-verify on SimHub updates.

### Architecture

One generic base plugin, two concrete plugins:

- `ShakeITV3PluginBase<T, SettingsType>` — public abstract, `where T : IOutputManager`.
- `ShakeITMotorsV3Plugin : ShakeITV3PluginBase<VibrationOutputManager, …>` — `[PluginName("ShakeIt Motors")]`.
- `ShakeITBSV3Plugin : ShakeITV3PluginBase<SoundOutputManager, …>` — `[PluginName("ShakeIt Bass Shakers")]`.

Per-frame flow (`ShakeITV3PluginBase.DataUpdate`, SimHub data thread, once per game-data tick):

1. Each `EffectsContainerBase` in the current per-game profile runs `ProcessEffects(data, …)`.
2. `currentProfile.CollectEffectiveEffects(EffectsCollected)` gathers active `EffectOutput`s.
3. Single handoff to the output layer: `settings.CurrentOutputManager.EffectUpdate(gameRunning, currentGain, EffectsCollected)` — `currentGain` is the profile global gain 0–100 (0 when muted).

The 10 Hz `DispatcherTimer` in the base is **UI preview only** (runs while the settings control is visible); all real output happens on the data-update tick. Effect *containers* are discovered by assembly scanning (`PluginFinder.GetResolver(…, typeof(EffectsContainerBase))`, filtered by `[ShakeItContainerMetadata]` + `OutputMode`/`DeviceTarget`), so custom effect types are third-party-extensible — that is the effect side, distinct from the output-device side below.

Two output-layer concepts that are easy to confuse:

- **`OutputBase`** (public abstract) — the *per-effect-container* output config: channel mapping, `ExportProperty`, `PropertyName`, `DisableOutput`. Not a device.
- **`IOutputManager`** / **`IDeviceOutputManager`** (public interfaces) — the device subsystem: `int EffectUpdate(bool gameinrace, double globalGain, IList<EffectOutput> effectOutputs)`, `IsConnected`, settings controls.

### The built-in output lists are closed

- **ShakeIt Motors** (`VibrationOutputManager`): the device list is a **fixed 11-element array literal** (Arduino, Fanatec, ForceFeel, GameTrix ×2, HSR, Simagic ×2, Conspit, ThreeDRap, VNM) and `AllocateOutput` is a hardcoded if/else chain keyed on a closed `DeviceType` enum. No "custom serial/UDP" member, no registry to append to. Injecting here would require runtime patching — don't.
- **ShakeIt Bass Shakers** (`SoundOutputManager`): FMOD audio only. It does not implement `IDeviceOutputManager` and has **no non-audio hook** — a serial device cannot participate short of presenting itself to the OS as an audio output. Use the Motors path.

### The extension surface: device-hosted "Haptics" devices

The supported path — how Simagic / Simsonn / Simucube / Conspit / Interhaptics pedals and rumble kits appear in ShakeIt. A device registered in SimHub's **Devices** section carries a full embedded ShakeIt Motors instance as a "Haptics" composite; its device settings page *is* the ShakeIt effects editor (per-game profiles, effect→channel mapping, controls).

**Discovery** (`SimHub.Plugins.Devices.DevicesPlugin.GetDeviceDescriptors`):

```csharp
foreach (Type plugin in PluginFinder.GetResolver("DevicesPlugin", typeof(IDeviceDescriptorsRegistry)).GetPlugins())
    foreach (DeviceDescriptor device2 in ((IDeviceDescriptorsRegistry)Activator.CreateInstance(plugin)).GetDevices())
        list.Add(device2);
```

`IDeviceDescriptorsRegistry` is **public** (`IEnumerable<DeviceDescriptor> GetDevices()`), and the resolver metadata-scans **all DLLs** including third-party plugins — the same mechanism that discovers `IDeviceExtensionFilter`. A public parameterless-constructible implementer in the plugin assembly is picked up automatically.

**`DeviceDescriptor`** (public) — key settable members: `string DeviceTypeID` (unique GUID, duplicates throw), `string Name`, `string Brand`, `int MaximumInstances` (must be > 0), `Func<DeviceInstance> Factory`, `List<USBRequest> DetectionDescriptors` (`new USBRequest(vid, pid)`, decimal ints — drives USB auto-detection), `Func<bool> Available`, `IEnumerable<Type> RequiredPlugins`.

**Built-in registry example** (`SimHub.Plugins.Devices.Regisry.SimsonnDevicesRegistry` — note SimHub's misspelled namespace):

```csharp
yield return new DeviceDescriptor
{
    DeviceTypeID = "BF62F95F-…", MaximumInstances = 5, Brand = Brands.BrandSimsonn,
    Name = "Simsonn VAM / VAM Pro (…)",
    Factory = () => new ShakeItV3DeviceInstance<MotorsWithFrequencyOutputManager,
        ShakeitSettingsMotorsWithFrequencyOutputManagerBase<MotorsWithFrequencyOutputManager,
            SimsonnWithFrequencyChannelsSettingsProvider>>(),
    DetectionDescriptors = new List<USBRequest> { new USBRequest(56829, 24593), … }
};
```

Everything vendor-specific is the innermost provider type; the rest is SimHub's generic machinery.

**`ShakeItV3DeviceInstance<TOutputManager, TSettings>`** (**internal**, `: CompositableDeviceInstance`, `CompositeCode/CompositeLabel = "Haptics"`) is thin glue (~100 lines, every member type public):

- Hosts a public `ShakeITV3PluginDevice<TOutputManager, TSettings>` created with `fromDevice: true`.
- `DataUpdate` forwards to the hosted plugin's `DataUpdate(pluginManager, ref data, ShouldBeRunning())`.
- `GetSettingsControls()` yields `ShakeItV3SettingsEffectsProfile` ("Effects"), `ShakeItDeviceControlsUI` ("Controls"), then the output manager's own controls — all public types.
- `SetSettings`/`LoadDefaultSettings` construct the hosted plugin on the WPF dispatcher; `GetSettings` serializes `Settings` to `JToken`.
- `GetDeviceState()` returns `Connected` only when `Output.IsConnected` — the provider's `IsConnected` drives the Devices-page connection state.

### Value handoff — the MotorsWithFrequency path

`MotorsOutputManagerBase` (public abstract, implements `IDeviceOutputManager`): `IsConnected => ShakeItChannelsInfoProvider?.IsConnected ?? false`; `AllowPropertiesExport => false`. `MotorsWithFrequencyOutputManagerBase` (public abstract) adds `OutputMode.MotorsWithFrequency` and turns each `EffectOutput` into a cached `IAudioRenderer`, then calls:

```csharp
protected abstract void UpdateOutput(bool gameinrace, double globalGain,
    IList<EffectOutput> effectOutputs, List<IAudioRenderer> renderers);
```

The **internal** concrete `MotorsWithFrequencyOutputManager` implements the tone mixer (~50 lines): per tone-renderer it builds `GenericTone { Frequency, Gain = globalGain/100 × toneGain × containerEffectiveGain/100, IsPrehemptive, TargetChannel }` for every enabled channel of the effect's placement, drops zero-gain tones, lets preemptive tones suppress all others, then groups by channel:

```csharp
// per channel: Gain = max(tone gains); Frequency = gain²-weighted average of tone frequencies
ShakeItChannelsInfoProvider.UpdateOutput(Dictionary<int, ChannelValue> values);

public class ChannelValue { public double Gain; public double Frequency; }   // Gain 0–1, Frequency Hz
```

Channel indices follow the order of `GetChannels()`. The provider interface (**public**) is the piece a plugin implements:

```csharp
public interface IShakeItChannelsInfoProvider   // SimHub.Plugins.DataPlugins.ShakeItV3.Device
{
    string DefaultSettingsKey { get; }
    bool IsConnected { get; }
    List<ChannelInformation> GetChannels(MotorsWithFrequencyOutputManagerBase manager);  // ChannelInformation = { string Name }
    ChannelActivation CreateDefaultActivationFor(FFBPlacement placement, MotorsWithFrequencyOutputManagerBase manager);
    void LoadDefaultPlatformSettings(EffectsContainerBase effectsContainerBase, ShakeItProfile shakeItProfile);
    void UpdateOutput(Dictionary<int, ChannelValue> values);   // the per-tick sink
    void Stop();
    FrequencyRange HardwareFrequencyRange();                   // ctor (int min, int max, bool hasFrequency) — ShakeIt clamps tones to it
    void SetSettings(ShakeItSettings shakeItSettings);
    IEnumerable<DeviceSettingControl> GetSettingsControls();
}
```

`MotorsWithFrequencyChannelsSettingsProvider<TSettings>` (public) is a reusable provider base, but it is welded to `IUSBGenericManagerSerial<MotorStates>` hardware managers (the Simagic-family pedal transports) — for a proprietary transport, implement `IShakeItChannelsInfoProvider` directly. `InterhapticsOutputManager` (public) is the cleanest all-public manager example (pushes tones to a named-pipe sink).

**Threading and construction caveats:**

- `UpdateOutput` arrives on **SimHub's data-update thread** every game tick (~60 Hz game-dependent). Serial writes from it must be non-blocking (coalescing stream slots, never a blocking port write).
- The output manager is instantiated by SimHub via `Activator.CreateInstance<T>()` (`ShakeItSettings<T>.CreateOutputManager`), and the provider via `new()` — **no constructor injection**. A provider reaches live plugin state (connection, detection flags) through a static/singleton bridge.
- Device-hosted instances (`FromDevice == true`) never export properties (see below) and skip the main plugin's left-nav UI — everything lives on the device page.

### Two integration approaches

**A — all public, zero reflection:** subclass `MotorsWithFrequencyOutputManagerBase` (copy the ~50-line tone mixer or model on `InterhapticsOutputManager`), pair it with plain `ShakeItDeviceSettings<T>`, and write a public clone of `ShakeItV3DeviceInstance` (its body is all-public types). Register via `IDeviceDescriptorsRegistry`. More copied code, immune to internal renames except at the interface level.

**B — reflection shortcut, maximum reuse:** implement only `IShakeItChannelsInfoProvider` (public, `new()`-constructible) and Activator-construct the internal generic so SimHub's mixer + channel-mapping UI are reused verbatim:

```csharp
var asm = typeof(SimHub.Plugins.Devices.DeviceDescriptor).Assembly;
var mgr = asm.GetType("SimHub.Plugins.DataPlugins.ShakeItV3.Device.MotorsWithFrequency.MotorsWithFrequencyOutputManager");
var settings = asm.GetType("SimHub.Plugins.DataPlugins.ShakeItV3.Device.ShakeitSettingsMotorsWithFrequencyOutputManagerBase`2")
    .MakeGenericType(mgr, typeof(MyChannelsProvider));
var inst = asm.GetType("SimHub.Plugins.DataPlugins.ShakeItV3.Device.ShakeItV3DeviceInstance`2")
    .MakeGenericType(mgr, settings);
Factory = () => (DeviceInstance)Activator.CreateInstance(inst, nonPublic: true);
```

Only two internal type names are load-bearing (`ShakeItV3DeviceInstance<,>`, `MotorsWithFrequencyOutputManager`) — far less surface than the Control Mapper reflection chain, but still version-fragile: guard every step and fail soft.

### Accessibility map

| Type | Accessibility |
|------|---------------|
| `IDeviceDescriptorsRegistry`, `DeviceDescriptor` (+`Factory`), `USBRequest` | public |
| `DeviceInstance`, `CompositableDeviceInstance`, `DeviceSettingControl` | public |
| `ShakeITV3PluginBase<,>`, `ShakeITV3PluginDevice<,>`, `ShakeItDeviceSettings<T>`, `ShakeitSettingsMotorsWithFrequencyOutputManagerBase<,>` | public |
| `IOutputManager`, `IDeviceOutputManager`, `IOutput`, `OutputManagerBase<>`, `OutputBase` | public |
| `MotorsOutputManagerBase`, `MotorsWithFrequencyOutputManagerBase` | public abstract |
| `IShakeItChannelsInfoProvider`, `IChannelsSettingsProvider`, `ChannelValue`, `ChannelInformation`, `FrequencyRange` | public |
| `MotorsWithFrequencyChannelsSettingsProvider<>` (USB-serial-welded), `InterhapticsOutputManager` | public |
| `ShakeItV3SettingsEffectsProfile`, `ShakeItDeviceControlsUI` | public |
| `ShakeItV3DeviceInstance<,>`, `MotorsWithFrequencyOutputManager`, `BasicMotorsDeviceOutputManager` | **internal** |
| `VibrationOutputManager` device list, `DeviceType` enum, `SoundOutputManager` | closed — not extensible |

### Read-only alternative: exported effect properties

`ShakeITV3PluginBase.ExportProperties` attaches per-effect delegates when `!FromDevice` (main plugins only — `AllowPropertiesExport` is true for `VibrationOutputManager`/`SoundOutputManager`, false for device-hosted managers). For each **enabled** container whose output has **`ExportProperty` checked** and a non-empty **`PropertyName`**:

```csharp
"Export." + i.Output.PropertyName + "." + j.Placement   // value: () => j.LastOutput
```

attached via `AttachDelegate(name, GetType(), func)`, so the full readable names are `ShakeITMotorsV3Plugin.Export.<PropertyName>.<Placement>` and `ShakeITBSV3Plugin.Export.<PropertyName>.<Placement>` (`<Placement>` is the effect's `FFBPlacement`, e.g. `FrontLeft`; value is the effect's `LastOutput` level, 0–100). `GlobalGain` and `IsMuted` are exported per plugin as well. Pollable via `PluginManager.GetPropertyValue` or usable directly in any NCalc formula field — a zero-integration bridge at the cost of per-effect user setup, level-only (no frequency), and no channel mapping.

### Device Builder haptics (SimHub 9.12+)

**Superseded finding.** Up to 9.11 the Device Builder feature set was LEDs + Screen only, and a
declarative device could never appear in ShakeIt. **9.12.0 added a haptics feature block**, so a
single `device.json` can now declare LEDs *and* motors. Verified by decompiling
`SimHub.Plugins.dll` 9.12.0 (`ilspycmd -o <dir> -p libs/SimHub/SimHub.Plugins.dll -r libs/SimHub`).

```csharp
[Flags] public enum DescriptorFeatures        // …Registry.DescriptorBuilder
{ None = 0, Screen = 2, LEDs = 4, Haptics = 8, Fans = 0x10, DataCommunication = 0x20 }

public class HapticsFeature : FeatureBase     // …DescriptorBuilder.Haptics
{
    public int MotorsCount;        // clamped to 1..8 by the device extension; ctor default 6
    public bool HasFrequency;      // ctor default true
    public int MinimumFrequency;   // ctor default 1     — ShakeIt clamps tones to [min, max]
    public int MaximumFrequency;   // ctor default 255
}
```

`DescriptorContent.ShouldSerializeHapticsFeature()` gates serialization on `IsEnabled`, so a
disabled feature block is **absent** from the JSON rather than present-and-false. The same is true
of `LedsFeature`, `ScreenFeature`, `FansFeature` and `DataCommunicationFeature` — absent means off,
and an omitted block round-trips cleanly.

JSON shape (SimHub's own round-trip output, MOZA wheelbase, 9.12.0):

```json
"LedsFeature": { "…": "…", "IsEnabled": true },
"HapticsFeature": {
  "MotorsCount": 3, "HasFrequency": true,
  "MinimumFrequency": 5, "MaximumFrequency": 200, "IsEnabled": true
},
"HardwareInterface": { "HardwareInterface": {
  "TypeName": "LedsStandardHIDProtocol",
  "HIDReportId": "0x68", "HIDFansReportId": "0x69", "HIDMotorsReportId": "0x6A",
  "HIDReportSize": 64, "DeviceDetection": { "Vid": "0x346E", "Pid": "0x00" } } },
"MinimumSimHubVersion": "9.12.0"
```

Two round-trip gotchas for a plugin that generates these files and compares them for staleness:

- SimHub **normalises the PID hex** — a written `"0x0000"` comes back as `"0x00"`. Compare PIDs by
  value, or an untouched definition reads as stale on every boot and the "restart SimHub" banner
  never clears.
- SimHub adds `LastModified` and drops disabled feature blocks. Compare semantic fields only.

#### The composite restructure — LEDs stop without a connected primary

This is the trap. `BuildDevice()` on the Device Builder descriptor rewires the composite as soon as
**either** Haptics or Fans is enabled:

```csharp
bool flag2 = FansFeature.IsEnabled || HapticsFeature.IsEnabled;
ledModuleDevice = new LedModuleDevice(...);
if (flag2) {
    ledModuleDevice.DisablePrimary();          // the LED module is no longer primary
    ledModuleDevice.UseSharedConnection();
    standardHidConnectionDevice = new StandardProtocolConnectionDevice(manager, vid, pid, …);
}
if (HapticsFeature.IsEnabled)
    standardProtocolMotorsDeviceExtension = new StandardProtocolMotorsDeviceExtension(
        () => standardHidConnectionDevice.GetRawDriverInstance() as IMotorsDriver,
        MotorsCount, HasFrequency, MinimumFrequency, MaximumFrequency);
```

`StandardProtocolConnectionDevice.IsPrimary => true` and it becomes the composite's **only**
primary. `CompositeDeviceInstance.DataUpdate` then splits primaries from the rest, and when a
primary is not `Connected` it sets `PrimaryDeviceMissing = true` on every non-primary sub-device →
`DeviceInstance.ShouldBeRunning()` returns false → `LedModuleDevice.DataUpdate` sets
`ledModuleSettings.IsEnabled = false`.

> **A plugin that adds `HapticsFeature` to an LED definition it drives through a virtual
> `ILedDeviceManager` must ALSO make the connection sub-device report connected, or the LEDs go
> dark.** A JSON-only change is not enough.

`StandardProtocolConnectionDevice.Manager` is a public getter-only auto-property, so the swap goes
through its `<Manager>k__BackingField` — the same reflection shape as the `LedModuleSettings.DeviceDriver`
injection above. One swap covers both concerns: the motors extension resolves its driver lazily via
`Manager.GetDriverInstance()`, so a manager whose `GetDriverInstance()` returns an `IMotorsDriver`
receives the mixed motor values directly.

Also implement **`IConnectableLedDeviceManager`** (`void EnsureConnected()`, one method) on the
replacement manager. Without it, `StandardProtocolConnectionDevice.DataUpdate` calls
`Manager.Display(6 × () => new Color[0], forceRefresh: false)` on **every tick**.

#### The motors interface

```csharp
public interface IMotorsDriver : IUSBDriver, IDriver, IDisposable   // BA63Driver.Interfaces
{ bool SendMotors(MotorStates states, bool forceRefresh); }
// IDriver: bool IsConnected; string SerialNumber; string FirmwareVersion; void Clear();

public class MotorStates { public MotorState[] States { get; } = new MotorState[8]; }
public struct MotorState { public int Frequency; public double Gain; }   // Gain 0..1
```

`StandardProtocolMotorsDeviceExtension` (`CompositeCode/CompositeLabel = "Haptics"`) hosts a full
`ShakeITV3PluginDevice` closed over `MotorsWithFrequencyOutputManager` +
`StandardProtocolMotorsChannelsSettingsProvider`, so the device page gains the standard ShakeIt
Effects / Controls tabs. Channels are named `"Motor 1".."Motor N"` and
`HardwareFrequencyRange()` comes from the JSON's min/max — the declarative path gives no way to
supply a custom `IShakeItChannelsInfoProvider`, so custom channel names and default activations do
not survive a move from a code-registered device to a declared one.

Its `GetDeviceState()` returns `Connected` only when the output manager is connected, which chains
through `StandardProtocolSharedMotorsManager.IsConnected()` → the `IMotorsDriver`'s `IsConnected`.
That state does **not** gate the LEDs — only the connection sub-device's does — so the motors
driver can carry a narrower capability gate than the connection manager.

**Alternative hook.** `StandardProtocolMotorsSharedDrivers` is a **public static** registry
(`Register(string key, Func<IMotorsDriver>)`) keyed by a GUID the extension generates into its
private `sharedDriverKey` field. Re-registering that key redirects the motors driver without
touching the connection device — but the key needs reflection to read, and it does nothing about
the connection-primary problem above, so the manager swap is the better single hook.

Haptics devices can still be code-registered via `IDeviceDescriptorsRegistry` (§ above); that path
is the only way to supply a custom channels provider.

#### Channel activation defaults — three traps

A declarative haptics device gets `StandardProtocolMotorsChannelsSettingsProvider`, and its
per-effect channel defaults come out **all channels enabled**. On hardware that sums its channels
into one actuator that is a silent multiplication. Three things make this harder to fix than it
looks, all verified against 9.12.0 on real hardware:

1. **The defaults key is hardcoded and not overridable.**
   `MotorsWithFrequencyChannelsSettingsProvider.DefaultSettingsKey => "SimagicReactors"` is a plain
   (non-virtual) property, and `StandardProtocolMotorsChannelsSettingsProvider` does not override
   it. So *every* Device-Builder haptics device inherits Simagic's curated defaults from
   `ShakeIt/EffectsDefaults/SimagicReactors/<ContainerType>.json`, which ship with SimHub and enable
   every channel. Swapping in a custom `IShakeItChannelsInfoProvider` changes the key for later
   `AddEffect` calls but does **not** retroactively touch a profile already seeded.

2. **Activations are materialized lazily, long after the containers exist.**
   A new device's 24-effect default profile is built during the motors sub-device's own
   `LoadDefaultSettings`, but each effect's `DeviceChannelActivationSettings` is created on demand
   — `MotorsWithFrequencyOutputManager.UpdateOutput` and the checkbox UI both call
   `PlacementChannelsActivation.Get` → `GetOrAdd` → `CreateDefaultActivationFor`. Measured on an
   R16: containers appear at t+0 ms, activations ~2 s later. Any pass that inspects the profile at
   device-construction time sees containers with an empty settings store and concludes, wrongly,
   that there is nothing to do.

3. **`AbstractSettingsStore.Settings` is a public FIELD, not a property.**
   ```csharp
   public class AbstractSettingsStore { public List<AbstractSettingsBase> Settings = new(); }
   ```
   `EffectsContainerBase.SettingsStore` *is* a property, so a property-only reflection walk resolves
   the store and then silently returns null for its contents — every effect looks like it has no
   activation settings at all. Any reflection helper used here must fall back to fields.

`LoadDefaultPlatformSettings` (the provider hook that *can* seed activations) only runs from
`ShakeItProfile.AddEffect` and `IContainerGroupExtensions.ResetEffect` — never on profile load, and
never for the stock default profile. So a custom provider fixes user-added effects only; the seeded
defaults have to be rewritten directly. The MOZA plugin does that once per device instance
(`MozaBaseHapticsBridge.NarrowStockChannelDefaults`), matching only the exact all-channels-on shape
so a deliberate multi-channel choice is never clobbered.

Note also that `MotorsOutputManagerBase.LoadDefaultPlatformSettings` computes an
`EffectsDefaultsOverrides/<key>` path and then discards it — both file probes use the
`EffectsDefaults` path. The overrides mechanism is dead in this version.

## Control Mapper Variant Providers

SimHub's Control Mapper supports a "variant" concept — a per-attached-wheel string that lets the same DirectInput controller track different button-mapping bundles. Fanatec and Simucube ship built-in providers (`FanatecVariantProvider`, `SimucubeVariantProvider`); the registration surface for third-party providers is **not public** and requires reflection. The IL findings below come from `SimHub.Plugins.dll` version `1.0.9631.22016`.

### Public surface

```csharp
namespace SimHub.Plugins.OutputPlugins.ControlRemapper.Variants;

public interface IVariantProvider
{
    string GetVariant(int vendorid, int productid);
}
```

By convention (matching `FanatecVariantProvider` / `SimucubeVariantProvider`), providers also expose a public `EventHandler` event named exactly **`VariantChanged`**. `VariantHelper` reflects on this event by name when it subscribes — fire the event when your detected variant changes and the helper triggers controller re-enumeration.

### The variant pipeline

```
ControlMapperPlugin (public — discoverable via PluginManager.GetPlugin<T>())
  └── remapperWorker        (RemapperWorker, private field)
        ├── settings              (ControlMapperPluginSettings, public field)
        │     └── RecognizeIndiviualWheels (bool, user toggle — note SimHub's typo)
        ├── variantHelper         (VariantHelper, private field)
        │     └── VariantProviders (List<IVariantProvider>, private field) ← REGISTRATION TARGET
        └── directInput           (SharpDX.DirectInput.DirectInput)
```

**`VariantHelper.GetVariant(int vid, int pid)`** decompiled:

```csharp
if (!Settings.RecognizeIndiviualWheels) return null;   // MASTER GATE
if (VariantProviders == null) return null;
return VariantProviders.Select(p => p.GetVariant(vid, pid)).FirstOrDefault(v => v != null);
```

**Lazy-initialization gotcha**: `VariantProviders` is null until `VariantHelper.Start()` runs. `Start()` is called from `RemapperWorker.UpdateVariantProviders()`, which gates on the user toggle:

```csharp
RemapperWorker.UpdateVariantProviders() {
    if (settings.RecognizeIndiviualWheels)
        variantHelper.Start();   // creates the list, adds Simucube + Fanatec, subscribes to providers' VariantChanged
    else
        variantHelper.Stop();    // unsubscribes
}
```

If the user has `RecognizeIndiviualWheels` off, the variant pipeline is dead — every `GetVariant` call returns null and `AquireController`'s variant check (below) fails on every saved mapping. **This is a hard prerequisite** to document for users. (The MOZA bridge's `TryRegister` deliberately calls `VariantHelper.Start()` to force-create the provider list and insert `MozaVariantProvider` even when the toggle is off, so enabling "Recognize individual wheels" takes effect immediately — the provider is already present and waiting; the toggle remains the master gate for `GetVariant` returning non-null.)

`RemapperWorker.UpdateControllerList` is wired into `variantHelper.VariantChanged` in `RemapperWorker.ctor`. So provider-side `VariantChanged` → `VariantHelper.VariantChanged` → `UpdateControllerList()` → controller re-enumeration.

### Registering a custom provider

There is no public API. The reflection chain (defensive: every step can fail if SimHub renames an internal):

```csharp
Assembly pmAsm = pluginManager.GetType().Assembly;
Type cmType = pmAsm.GetType("SimHub.Plugins.OutputPlugins.ControlRemapper.ControlMapperPlugin");

// PluginManager.GetPlugin<T>() — public, generic, no-arg
MethodInfo getPlugin = pluginManager.GetType().GetMethod(
    "GetPlugin", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
object cmInstance = getPlugin.MakeGenericMethod(cmType).Invoke(pluginManager, null);

object rw = cmType.GetField("remapperWorker", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(cmInstance);
object vh = rw.GetType().GetField("variantHelper", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(rw);

FieldInfo providersField = vh.GetType().GetField(
    "VariantProviders", BindingFlags.NonPublic | BindingFlags.Instance);

// Lazy-materialize the list if the user hasn't flipped RecognizeIndiviualWheels yet
if (providersField.GetValue(vh) == null) {
    vh.GetType().GetMethod("Start", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(vh, null);
}

((IList)providersField.GetValue(vh)).Add(myProvider);

// Refresh subscriptions — Start() unconditionally re-subscribes to every provider
// (yes, this double-subscribes the original Fanatec + Simucube providers; benign noise,
// not a correctness bug, since SimHub doesn't track subscription counts itself).
rw.GetType().GetMethod(
    "UpdateVariantProviders",
    BindingFlags.NonPublic | BindingFlags.Instance).Invoke(rw, null);

// Force an immediate re-enumeration so the new provider's variant lands on the first
// pass (controllers attached before our bridge registered are otherwise stamped with
// variant=null until the next VariantChanged fires).
rw.GetType().GetMethod(
    "UpdateControllerList",
    BindingFlags.NonPublic | BindingFlags.Instance).Invoke(rw, null);
```

### How variant flows through controller enumeration

`RemapperWorker.UpdateControllerList` walks `directInput.GetDevices()`. The per-device closure (`<>c__DisplayClass64_2.<UpdateControllerList>b__1`) does:

1. Build a fresh `ControllerDescription` via `ToControllerDescription`, which stamps `Variant = variantHelper.GetVariant(VID, PID)` — i.e. **our provider's current value**.

2. Run a **match cascade** against `settings.ControllerMappings` (a `FirstOrDefault` chain). All three predicates are **variant-aware**:

   | Predicate (compiler-generated) | Match condition |
   |--------------------------------|-----------------|
   | `b__2` (first FirstOrDefault) | `m.Description.InterfacePath == newDesc.InterfacePath && m.Description.Variant?.ToLower() == newDesc.Variant?.ToLower()` |
   | `b__3` (second) | `m.Description.ControllerID == deviceInstance.InstanceGuid && m.Description.Variant?.ToLower() == newDesc.Variant?.ToLower()` |
   | `b__10` (third, gated on `IsUniquePIDVID && MatchControllerOnPIDVID`) | `m.Description.VendorID == newDesc.VendorID && m.Description.ProductId == newDesc.ProductId && m.Description.Variant?.ToLower() == newDesc.Variant?.ToLower()` |

3. If matched: `match.ControllerDescription.CopyFrom(newDesc)` — updates the saved mapping's description fields in place (including `Variant`; idempotent when the values are already equal).

4. `UpdateOrAdd` into `settings.AvailableControllers` (`ObservableCollection<ControllerDescription>`). Predicate `b__4` is also variant-aware (ControllerID + Variant); updater `b__6` calls `existing.CopyFrom(newDesc)`.

5. **If the cascade returns null**: device is unmapped for the current variant. Add `deviceInstance.InstanceGuid` to a local `unmappedGuids` list, then `UpdateOrAdd` into `settings.UnmappedControllers`. **Predicate `b__7` is ControllerID-only** — no variant check — and updater `b__9` calls `existing.CopyFrom(newDesc)`. **This is the trap**, see below.

6. Add `deviceInstance.InstanceGuid` to `foundGuids` (a local list).

After the per-device loop:

```csharp
foreach (m in settings.ControllerMappings)
    m.ControllerState.Available = foundGuids.Contains(m.Description.ControllerID);
CleanCollection(settings.UnmappedControllers, unmappedGuids);     // strips entries whose ControllerID isn't in the GUID list
CleanCollection(settings.AvailableControllers, foundGuids);
```

So **`Available` is "is the DirectInput device currently plugged in"**, NOT "does this mapping's variant match." Mappings whose stored Variant doesn't match the live wheel still show `Available=true` if the wheelbase is connected. SimHub UI typically renders this as "online," which is misleading but isn't where input dispatch is gated.

### Input dispatch gating: `SharpHelper.AquireController`

The real per-variant gate. Called from `ProcessControllers` before any input is polled:

```csharp
bool AquireController(DirectInput directInput, ControllerSourceMapping mapping, VariantHelper helper, …) {
    if (mapping.ControllerState.Device != null) {
        if (!mapping.IsEnabled)        { cleanup; return false; }
        if (!mapping.ControllerState.Available) { cleanup; return false; }
        if (mapping.ControllerState.AcquireDebouncer.Debounce()) return false;

        // First variant check (when device already acquired):
        if (SharpHelper.GetCurrentVariant(mapping, helper) != mapping.Description.Variant) {
            SharpHelper.SetAsUnplugged(mapping);       // sets ControllerStatus = Disconnected
            return false;
        }
    } else {
        try {
            mapping.ControllerState.Device = SharpHelper.CreateJoystick(directInput, mapping.Description.ControllerID);
            // …onConnected.Invoke()…
        } catch { SharpHelper.SetAsUnplugged(mapping); return false; }

        // Second variant check (after acquire), ToLower-normalized:
        if (SharpHelper.GetCurrentVariant(mapping, helper)?.ToLower() != mapping.Description.Variant?.ToLower()) {
            mapping.ControllerState.Device.Dispose();
            mapping.ControllerState.Device = null;
            SharpHelper.SetAsUnplugged(mapping);
            return false;
        }
    }
    return mapping.ControllerState.Device != null;
}

// SharpHelper.GetCurrentVariant — calls our provider via the helper:
string GetCurrentVariant(ControllerSourceMapping mapping, VariantHelper helper) {
    return helper.GetVariant(mapping.Description.VendorID, mapping.Description.ProductId);
}

// SharpHelper.SetAsUnplugged — minimal:
void SetAsUnplugged(ControllerSourceMapping mapping) {
    mapping.ControllerState.ControllerStatus = ControllerStatus.Disconnected;
}
```

`ProcessControllers` short-circuits the input loop body on `AquireController` returning false, so **per-variant input dispatch works correctly** even when `Available` is variant-agnostic.

### Shared-reference pitfalls

**`ControllerSourceMapping.set_ControllerDescription`** stores its argument **by reference** — no clone:

```csharp
set_ControllerDescription(value) {
    if (Equals(this.<ControllerDescription>k__BackingField, value)) return;
    this.<ControllerDescription>k__BackingField = value;
    OnControllerDescriptionChanged();   // subscribes to the description's PropertyChanged
}
```

**`ControlMapperPluginSettings.AddController`** (the method "Add Source Controller" calls):

```csharp
void AddController(ControllerDescription description) {
    if (description == null) return;
    var csm = new ControllerSourceMapping();
    csm.ControllerDescription = description;     // SHARED reference
    this.ControllerMappings.Add(csm);            // fires CollectionChanged on the dispatcher
    UpdateControllerList();
}
```

The UI typically passes a description that lives in `settings.AvailableControllers` or `settings.UnmappedControllers`, so the new `ControllerSourceMapping` and the source collection now **share the same `ControllerDescription` instance**.

**The trap**: `UpdateOrAdd` into `UnmappedControllers` (b__7, ControllerID-only) calls `existing.CopyFrom(newDesc)` whenever the shared description happens to be re-found. The shared description's `Variant` gets overwritten with the live wheel's variant on every wheel change — and the saved mapping silently inherits the rewrite.

**Workaround when programmatically adding (or when intercepting `CollectionChanged`)**: deep-clone the description so the new mapping owns its own object:

```csharp
ControllerDescription clone = new ControllerDescription();
clone.CopyFrom(newCsm.ControllerDescription);
clone.Variant = currentDetectedVariant;   // what the user actually meant to add
newCsm.ControllerDescription = clone;
```

### Single-DirectInput-device hardware

SimHub's variant model implicitly assumes each variant maps to a distinct DirectInput `InstanceGuid` — Fanatec / Simucube wheels enumerate as separate Windows joystick devices when the user swaps wheels. For hardware like MOZA where the wheelbase keeps the same `InstanceGuid` regardless of which wheel is attached (wheel identity arrives via the serial protocol, not USB device-tree changes), the variant provider can still:

- **Disambiguate the display label** in Add Source Controller via `Description.Variant`. ✓
- **Gate input dispatch** via `AquireController`'s variant check. ✓
- **Offer the wheelbase in Add Source Controller** — hardware-dependent. The Add dropdown is sourced from `UnmappedControllers` filtered by ControllerID, so a single-DirectInput-device base is hidden once any saved mapping references that GUID. But MOZA bases that enumerate as **multiple** DirectInput devices (observed: two base entries, unlabeled) surface each distinct `InstanceGuid` separately, so the user can add the second wheel manually. ✓/✗ per hardware.

The plugin does **not** auto-create mappings. `ControlMapperBridge.DetachMozaDescription()` deep-clones shared `Description` references on `Add` (so SimHub's by-reference `CopyFrom` updater can't rewrite a saved mapping's Variant on a later wheel change), and the user adds each controller through the normal Add Source Controller flow.

> **Removed (2026-06):** `AutoCreateVariantMappingIfNeeded()` used to synthesize a per-variant mapping from the data loop for the single-DirectInput-device case. It was removed at the user's direction — on their multi-DirectInput-device base the dropdown already offers the wheel, and the synthesized mapping appeared as an unwanted extra entry stuck `Available=false` ("unplugged"). The hard-won constraints below stand if it's ever reintroduced.

**If you reintroduce a programmatic add, refresh via the settings entry point, not the worker.** `AddController` calls `ControlMapperPluginSettings.UpdateControllerList()` immediately after `ControllerMappings.Add`. That settings method defers via `Task.Run` → `OnUpdateControllerList` → `RemapperWorker.UpdateControllerList`, i.e. on a background thread *after* the `Add` completes. The refresh is the *only* place the variant match cascade binds a live device to a mapping (`ControllerState.Available`) and the only way `ProcessControllers` → `SharpHelper.AquireController` then reaches `ControllerStatus.Acquired` — which is both the UI "connected" state (`ControllerState.IsConnected => ControllerStatus == Acquired`) and the gate for input dispatch. A programmatic add bypassing `AddController` must call `ControlMapperPluginSettings.UpdateControllerList()` itself. Do **not** invoke `RemapperWorker.UpdateControllerList()` directly from inside the `Add`'s `CollectionChanged` handler: it runs synchronously on the UI thread, re-entrant inside the in-flight collection-change dispatch (its per-device `Dispatcher.Invoke` closures mutate `AvailableControllers`/`UnmappedControllers` mid-notification), and the new mapping silently never acquires.

Note also that `RemapperWorker` subscribes `UpdateControllerList` to `variantHelper.VariantChanged`, but `VariantHelper.Start()` only subscribes to the providers present when it first builds the list and early-returns on later calls — so a provider appended afterward (the MOZA one) is **not** wired to `VariantChanged` in this assembly version. Wheel swaps still work because `ProcessControllers` polls `GetVariant` every loop and `USBChangeDetectorService.DevicesChanged` (the wheel attach re-enumerating on USB) fires `UpdateControllerList`; do not rely on the provider's `VariantChanged` reaching SimHub.

### WPF dispatcher requirement

`ControlMapperPluginSettings.ControllerMappings` is an `ObservableCollection<ControllerSourceMapping>` bound to a WPF `CollectionView`. **All modifications must run on the UI dispatcher thread**. Background-thread `Add` throws:

```
This type of CollectionView does not support changes to its
SourceCollection from a thread different from the Dispatcher thread.
```

The exception fires AFTER the backing `List<T>` is mutated, so `Count` rises but the WPF view never sees the change notification — UI stays out of sync, and a subsequent UI-thread modification can throw too. Marshal via:

```csharp
var dispatcher = System.Windows.Application.Current?.Dispatcher;
if (dispatcher == null || dispatcher.CheckAccess())
    mappings.Add(csm);
else
    dispatcher.BeginInvoke(new Action(() => mappings.Add(csm)));
```

### Key types

| Type | Namespace | Public/Internal |
|------|-----------|------------------|
| `IVariantProvider` | `…ControlRemapper.Variants` | **public** |
| `VariantHelper` | `…ControlRemapper.Variants` | internal (constructor takes `ControlMapperPluginSettings`) |
| `ControlMapperPlugin` | `…ControlRemapper` | public |
| `RemapperWorker` | `…ControlRemapper` | public type, members internal/private |
| `SharpHelper` | `…ControlRemapper.Helpers` | internal (note: `Aquire`, not `Acquire`) |
| `ControllerSourceMapping` | `…ControlRemapper.Models` | public |
| `ControllerDescription` | `…ControlRemapper.Models` | public |
| `ControllerState` | `…ControlRemapper.Models` | public |
| `ControlMapperPluginSettings` | `…ControlRemapper.Models` | public |

## MahApps Metro

SimHub's UI is built on [MahApps.Metro](https://mahapps.com/). Plugin UIs can use MahApps controls (`MetroComboBox`, `ToggleSwitch`, etc.) for consistent styling. The assemblies are already loaded by SimHub at runtime.
