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
| `PluginsData/Common/Devices/<InstanceId>/settings.json` | One device instance's saved settings | — |
| `PluginsData/DevicesDesccriptorCache.json` | Template metadata cache | — |
| `DevicesLogos/` | Device images by GUID (PNG files) | — |

#### Device instance settings on disk

`index.json` is only a roster — `{ "Instances": [ { "InstanceId", "DeviceTypeName" } ], "LastActiveDevice", "ForceIndentedSaving" }`. Each instance's actual state lives in its own folder, named by the location SimHub itself computes:

```csharp
public string GetSettingsPath() =>          // DeviceInstance, 9.12.0
    "PluginsData\\Common\\Devices\\" + (RootInstance?.InstanceId ?? InstanceId);
```

```jsonc
{ "InstanceId": "…", "DeviceTypeID": "<descriptor GUID>_UserProject",
  "Settings":          { "LEDS": { … }, "Haptics": { … } },   // keyed by CompositeCode
  "ExtensionSettings": { "MozaBaseDeviceExtension": { … } },  // per DeviceExtension
  "DeviceTypeName": "MOZA R16", "Enabled": true, … }
```

Two consequences worth knowing:

- **`Settings` is keyed by `CompositeCode`, but only for a composed device.** `CompositeDeviceInstance.GetSettings` builds `Dictionary<CompositeCode, JToken>` over its children, and `SetSettings` splits it back the same way, calling `LoadDefaultSettings()` on any child the dictionary does not mention. A device added as a standalone root serializes its own settings **directly**, with no wrapper — so code reading a saved blob has to handle both shapes.
- **A ShakeIt haptics device carries its whole effect tree here**, not in a shared profile store: `Settings.Haptics.Profiles[].EffectsContainers[]` plus `activeProfileId`, `LastGameProfiles`, `GlobalGain` and `DeviceControlsSettings`. Both `ShakeItV3DeviceInstance<,>` (code-registered) and `StandardProtocolMotorsDeviceExtension` (declarative) serialize it identically as `JToken.FromObject(shakeITV3PluginBase.Settings)` and load it by reconstructing `ShakeITV3PluginDevice` from the token, so the blob transfers verbatim between the two paths. `DeviceInstance.SetSettings(JToken, bool)` is `public abstract` on the version-stable base type, so no reflection is needed to drive that — but both implementations wrap their body in `Application.Current.Dispatcher.Invoke`, which **deadlocks if called from the data thread**. Marshal with `BeginInvoke`.
- **`LedModuleDevice.SetSettings` indexes `dictionary["ledModuleSettings"]` unconditionally** and throws `KeyNotFoundException` when it is absent. Validate a hand-supplied blob before handing it over.

The folder survives orphaning: SimHub deletes it only when the user removes the device, so settings belonging to a `DeviceTypeID` that no longer resolves stay readable indefinitely. That is what makes a plugin-side migration off a retired device identity possible — see [`docs/DEVELOPMENT.md`](DEVELOPMENT.md#device-extensions-devices) § Migrating off the pre-1.6 wheelbase devices.

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

The interface also declares `event EventHandler VariantChanged`. `VariantHelper.Start()` subscribes to it — but **only for the three providers it creates itself** (Simucube, Fanatec, Simagic); a provider appended to the list afterwards is never subscribed, so its `VariantChanged` reaches nobody. A late-registered provider has to request the re-enumeration itself through the public `ControlMapperPluginSettings.UpdateControllerList()`.

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

**Lazy-initialization gotcha**: `VariantProviders` is null until `VariantHelper.Start()` runs. `RemapperWorker.UpdateVariantProviders()` is called from the constructor **and on every `ProcessControllers` tick (~3 ms)**, gated on the user toggle:

```csharp
RemapperWorker.UpdateVariantProviders() {
    if (settings.RecognizeIndiviualWheels)
        variantHelper.Start();   // no-op once the list exists; otherwise creates it, adds Simucube + Fanatec + Simagic, subscribes to THEIR VariantChanged
    else
        variantHelper.Stop();    // unsubscribes, disposes the providers and sets VariantProviders = null
}
```

Both methods lock on `typeof(VariantHelper)`. `OutputMode == Disabled` also calls `Stop()` every tick. So with the toggle off (or output disabled) the list — and anything a plugin appended to it — is gone, and when the toggle comes back `Start()` builds a fresh list without the plugin's provider. Every `GetVariant` call returns null while the toggle is off, so `AquireController`'s variant check (below) fails on every saved mapping that carries a Variant, while a mapping whose Variant is null keeps working. The MOZA bridge therefore never stamps a Variant while the toggle is off, re-inserts its provider (under the same lock) when it sees a list it isn't in, and reports the state in the diagnostics dump. SimHub's UI label for the toggle is "Recognize supported wheels as individual controllers".

`RemapperWorker.UpdateControllerList` is wired into `variantHelper.VariantChanged` in `RemapperWorker.ctor`, and `VariantHelper` forwards its bundled providers' events there. A provider appended later is not subscribed; the bridge calls `ControlMapperPluginSettings.UpdateControllerList()` (public, `Task.Run` → `RemapperWorker.UpdateControllerList`) from its own `VariantChanged` handler instead.

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

// The list is null while the toggle is off (Stop() nulls it) — don't call Start()
// yourself: the next ProcessControllers tick would Stop() it again and discard your
// provider. Insert under SimHub's own lock and re-check about once a second from Poll().
lock (vh.GetType()) {
    if (providersField.GetValue(vh) is IList providers && !providers.Contains(myProvider))
        providers.Add(myProvider);
}

// Start() early-returns once the list exists, so it will NOT subscribe to your
// VariantChanged. Ask for the re-enumeration yourself — on registration and on every
// variant change — through the public async entry point (Task.Run inside SimHub):
object settings = cmType.GetField("controlMapperPluginSettings").GetValue(cmInstance);
settings.GetType().GetMethod("UpdateControllerList", Type.EmptyTypes).Invoke(settings, null);
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
bool AquireController(DirectInput directInput, ControllerSourceMapping mapping, VariantHelper helper, Action onConnect, …) {
    if (mapping.ControllerState.Device == null) {
        if (mapping.Description.IsVJoySimHubDriven) { SetAsUnplugged(mapping); return false; }
        if (mapping.IsEnabled && mapping.ControllerState.Available) {
            // Debouncer(5000): true once every 5 s → an unacquired mapping is retried every 5 s
            if (mapping.ControllerState.AcquireDebouncer.Debounce()) {
                try {
                    // Ordinal, case-sensitive; null != "KS Pro"
                    if (mapping.Description.Variant != helper.GetVariant(vid, pid)) { SetAsUnplugged(mapping); return false; }
                    mapping.ControllerState.Device = CreateJoystick(directInput, mapping.Description.ControllerID); // NonExclusive | Background
                    mapping.ControllerState.ControllerStatus = ControllerStatus.Acquired;
                    onConnect?.Invoke();
                } catch (Exception ex) {
                    mapping.ControllerState.ControllerStatus = ControllerStatus.Error;
                    Logging.Current.Error("Error while acquiring device " + ex);
                }
            }
        } else SetAsUnplugged(mapping);
    } else {
        // Already acquired: drop it when disabled, or when the live variant no longer matches (ToLower compare)
        if (!mapping.IsEnabled) { dispose; Device = null; }
        else if (mapping.Description.Variant?.ToLower() != helper.GetVariant(vid, pid)?.ToLower()) {
            dispose; Device = null; SetAsUnplugged(mapping); return false;
        }
    }
    return mapping.ControllerState.Device != null;
}

void SetAsUnplugged(ControllerSourceMapping mapping) {
    mapping.ControllerState.ControllerStatus = ControllerStatus.Unplugged;   // enum: None, Unplugged, Acquired, Disabled, Error
}
```

`ControllerState.IsConnected` is `ControllerStatus == Acquired`; that — not `Available` — is what the UI renders as connected. `UpdateControllerList` calls `AcquireDebouncer.ResetDebounce()` on every Available mapping, so a re-enumeration also forces an immediate acquire attempt.

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

## Motion Plugin — Custom Outputs

How a plugin can supply its own motion-platform output to SimHub's **Motion** plugin (the
licensed motion feature — `IsSimHubMotionLicenceInstalled` / `…Valid` live in `SimHub.Plugins.dll`).
Decompiled with `ilspycmd` 10.1.1 from a SimHub **9.12.8** install on 2026-10-07. None of this is a
documented API: type names, member signatures and even the namespace casing can change between
releases. Recorded here as reference material only — whether the MOZA plugin should integrate the
HMA150 this way is an open decision (the platform's own game mode takes *raw telemetry*, not
per-actuator positions — see [`protocol/motion/game-stream-0x22.md`](protocol/motion/game-stream-0x22.md)).

### Assembly and namespaces

- **`SimHub.Plugins.Motion.dll`** (~9 MB) — **not vendored in `libs/SimHub/`**; `SimHub.Plugins.dll`
  holds no Motion types. A plugin building against it would copy the DLL in with `Private=false`
  like the others.
- Namespaces are inconsistently cased: `SimHub.Plugins.Motion.{Contracts, Outputs,
  Outputs.ActuatorOrdering, Outputs.GenericCommon, Outputs.GenericSerialV2, Geometry}` and
  `Simhub.Plugins.Motion.{Models, Settings}` (lower-case *h*).
- Plugin class `SimHub.Plugins.Motion.MotionPlugin`, `[PluginName("Motion")]`.

### Pipeline — telemetry → effects → geometry → outputs

```
SimHub data thread   MotionPlugin.DataUpdate(ref GameData)
                       → MotionInputData (sanitised telemetry; OrientationPitchDegrees = −OrientationPitch)
                       → MotionWorkerBase.SetCurrentGameState(data)          // stores a reference only
"MotionUpdateThread"  MotionWorkerSleep: ProcessCore(); Thread.Sleep(2)      // ~2–3 ms, independent of game rate
                       → per MotionDevice: Update(dt, snapshot)
                           → EffectsProcessor.Process → GroupedMotionState {Primary, Secondary}   (DOFs in deg / mm / %)
                           → GetMotionState (overrides: simulated / quick-test / OpenXR / e-stop / suspend; 2 s transitions)
                           → GeometryAggregator.UpdateCore → CompositeGeometryResult {Primary, Secondary GeometryResult}
                           → Output.Update(result) or Output.KeepAlive(result)   // Output is always a ControllersAggregatorOutput
                               → one OutputWorker thread per enabled sub-output ("Motion <name>"):
                                 ApplyState → sub.Update / sub.KeepAlive; Thread.Sleep(sub.RefreshRateMs)
                                   → GetAxisValue(result, channelIdx, …) → wire format
```

- `MotionPlugin` is `[NoAutoEnablePlugin]`, `Priority => -9999`, implements `IPlugin, IDataPlugin,
  IWPFSettingsV2, IRTDataPlugin`; `Init` calls `GameManager.SetHighPrecision()`, loads
  `GeneralSettingsV3` (falls back to / saves as `GeneralSettingsV2`), applies each device's
  `StartMode`, creates the worker (`MotionWorkerMultimedia` and `UseMultimediaTimer` exist but
  nothing reads them) and a 16 ms UI `DispatcherTimer`.
- **`MotionInputData`** (built only while the game runs, is not paused and the car is on track —
  otherwise defaults): `GameConnected`, `InCar`, `GamePaused`, `GameProcessDetected`; orientation
  yaw/pitch/roll (`*Sanitized` when `UseSanitizedValuesForMotion`), pitch/roll/yaw velocities,
  `LocalVelocity{Forward,Lateral,Upward}`, `Acceleration{Sway,Surge,Heave}` (m/s²),
  `SuspensionVelocityMs[4]` (× `SuspensionVelocityMsMotionRatio`), `WheelSlip`, `WheelsSpeed`,
  `WheelsRPS`, `WheelsOnKerbs`, `GroundSpeed` (km/h), pit-limiter fields, `DirectTractionLoss`,
  `ABSActive`/`TCActive`, `IsInPit`/`IsInPitLane`, RPM/MaxRPM, pedals, `Gear`,
  `GearChangeFromInputs` (dynamic button `MotionGearChange`), flight ground contacts,
  `WorldCoordinates`, track-profiler pitch range, `Belt*`/`Unfiltered*` acceleration copies.
- **`MotionDevice`** = one platform setup: `Output` (always a `ControllersAggregatorOutput`, JSON
  `OutputEx`, N `AggregatedOutputs`), `Geometry` + optional `SecondaryGeometry`, `Profiles`
  (`MotionEffectsProfile`: `Effects`, `ProfileSettings`, `ProfilePresets`, `FineBoosterSettings`),
  `PowerSettings`, `IdleManager`, `EffectsProcessor`, motion-compensation objects, `WitMotionManager`,
  `WindMotionAdapter`, simulated/quick-test sources. `Devices` is unbounded but only
  `CurrentDevice` (`SelectedDeviceId`) is active; the others are forced disabled.
- **Device update order**: inactive → return; welcome layer showing → disabled; assignation
  callback set → only that runs; invalid → disabled; motion-compensation + WitMotion init;
  **shutdown** (disabled / disposing while started): 2 s transition to zero, then `Output.Stop()`
  once parked + 500 ms; **idle** (`IdleManager.ShouldBeIdling`): 2 s to zero, then
  `Output.KeepAlive(last)` every tick (wind still flows); leaving idle: 2 s +
  `AfterConnectMotionDelay`; sensor calibration; **normal**: `EffectsProcessor.Process` →
  `GetMotionState` → `StartOutput` (after `DisconnectReconnectDelay`, state `Cooldown` meanwhile) →
  `GeometryAggregator.UpdateCore` when `Output.IsReadyForLiveMotionData()` → `Output.Update`.
- `GetMotionState` override precedence (last wins): remote simulated → simulated → quick test →
  OpenXR CorEstimator pose → **output failure or `IsSafetyStopped()` freezes the last pose**
  (`SafetyStopped`) → `IsSuspended` zero → `ForceSuspend` zero → not ready zero. A `StateKind`
  change starts a 2 s transition (max(`AfterConnectMotionDelay`, 4 s) around an e-stop).
- `MotionDeviceState`: 0 Disabled, 1 Cooldown, 2 Active, 3 EStop, 4 Idle, 5 GoingToIdle,
  6 Failure, 7 Stopping, 8 Suspended, 9 NotFound, 10 Starting, 11 InvalidConfiguration,
  12 NotConfigured, 13 Calibrating, 14 GoingToCalibration. `OutputConnectionState`: 0 Disconnected,
  1 Connected, 2 NotFound, 3 Failure. `SafetyState { None = 0, EStopped = 1 }`.
- **Per-output worker** (`ControllersAggregator/OutputWorker`): the aggregator never touches
  hardware; each enabled sub-output gets a dedicated `Thread` (Normal priority) looping
  `ApplyState(state); Thread.Sleep(Output.RefreshRateMs)` — plain sleep, latest-state only, frames
  drop or repeat freely; an exception sets `ConnectionState = Failure`.
- **`MotionOutputBase` lifecycle**: `Start(isAssignationMode)` → `ApplySettings` (running settings
  are a JSON clone) → `StartInternal` → `AfterStarting`. `Update`: `CheckIdleStop`; once:
  `BeforeStartingMotion` then **`Unpark`** (a `DirectActuatorTransition` from park to live positions
  over `ParkDuration`, fed through pseudo-roles `500+idx`); `isReadyForLiveMotionData = true`;
  `BoostActuators`; `UpdateInternal`; `LastUpdatePositions`. `KeepAlive`: with `AllowIdling`
  (default true) or in assignation mode → `CheckMotionStop` (**park**: transition to park
  positions, `BeforeStoppingMotion`) → `CheckIdleStart` (`BeforeStartingIdle`) →
  `KeepAliveInternal`; otherwise it just calls `Update` with the zero pose. `Stop`: park,
  `CheckIdleStop`, `BeforeStopping`, `StopInternal`, `ConnectionState = Disconnected`.

### Effects

Per tick `EffectsProcessor.Process` (`Core/EffectsProcessor.cs`): safety checks (|surge| > 500 m/s²
artefact → 800 ms soft transition; angular crash |Δpitch|+|Δroll| > `CrashDetectionAnglesThreshold`;
telemetry gap > 100 ms → soft transition), gear-change / flight-touchdown artefact removal,
acceleration crash detection (absolute or delta), `LowSpeedDampener` (blends accelerations and
pitch to a 500 ms average below `LowSpeedAccelerationsDampeningSpeed` 15 km/h, near the pit
limiter, flight on ground); then for every effect `IsAvailableOnCurrentPlatform &&
AvailableOnCurrentGame`: `gain = categoryGain(effect.MotionCategory) × categoryGain(MotionGlobal)`
(haptics and belts use their own category only), `effect.GetMotionState(...)`, and every
`MotionEffectComponent.OutputValueWithGain` is **summed** into `GroupedMotionState.AddToAxis(target,
MotionAxis, value)`; yaw normalised to ±180; settings-change transition; `GlobalSmoothing`;
crash transition (`CrashFilterPreset` −2 → 1.75 … 1 → 0.75); state transition. A state signature
(`Live,connected,InCar,paused,<car>,teleportation,gamestatereset,pitlane,pit`) change triggers a soft
transition; teleport = > 10 m jump (50 m flight).

Inside an effect (`MotionEffectBase.GetMotionState` → `GetComponents`): source value → washout →
dynamic-range compression → `FilterBasicProgressiveAcceleration` → `FilterBasicSmoothing`
(`Smoothing` 0–100, sigmoid EMA) → `FilterBasicInOutScaling` (`clamp(value/Input, −1, 1) × Output`,
or a soft knee with `SoftBounds`/`SoftBoundRange`) → `CorrectionCurve` (`GammaFilter`: `GammaValue`,
`Threshold`, `MinimumValue`, `DeadZoneSmoothing`, `Negate`) → `× Gain/100 × kindGain`; disabled or
muted-by-isolation → 0. Common JSON keys: `Gain`, `IsEnabled`, `ScalingRange {Input, Output}`
(telemetry units → deg / mm / %), `SeparateScalingRange`, `Smoothing`, `SoftBounds`,
`SoftBoundRange`, `EnableDynamicRangeCompression`, `AccelerationFilter {…}`, `WashoutSettings
{EnableWashout, EnableAdapativeWashout, Soft/Strong/ExtremeValue {BaseWashout},
WashoutMultiplierMode}`, `CorrectionCurve {GammaFilter, IsEnabled}`, `ReverseDirection`,
`ForwardWhenBraking`, `OutwardForces`, `PerceptualForces`. Haptic effects add
`ChildOuputSettingsStore.ChildOutputSettings[] {DisplayName, IsEnabled, OutputValue, MotionTarget,
ChildOutputCategory}` (one per destination), speed boost (`EnableSpeedBoost`, `BoostSpeed`,
`BoostValue`) and pulse shape (`PulseDuration`, `UseAdvancedShape`, `Advanced{PulseDuration,
RampUpDuration, RampDownDuration}`). Traction-loss effects add `TractionLossRange`, `VelocityRange`,
`OutputRange`, `TractionLossMode`, controlled-velocity settings, `YawRateSensibility`, `Bias`.

Registry `MotionDevice.GetEffectsTypes(gameFamily)`; P = pre-enabled, R/F = racing/flight only.
Target is Primary unless noted; `Secondary*Effect` twins drive the secondary geometry.

| Effect | UI name | Category | Input | Output `MotionAxis` | Needs capability |
|--------|---------|----------|-------|---------------------|------------------|
| `PitchEffect` (P) / `SecondaryPitchEffect` | Pitch | PrimaryPlatform | `OrientationPitchDegrees` (+ track-profiler adjust) | `PitchDegrees` | `Pitch` |
| `PitchRateEffect` (P, F) | Pitch rate | PrimaryPlatform | pitch velocity | `PitchDegrees` | `PitchChangeVelocity` |
| `RollEffect` (P) / `RollRateEffect` (P, F) | Roll / Roll rate | PrimaryPlatform | roll (`ContinuousAngleTracker`) / roll velocity | `RollDegrees` | `Roll` / `RollChangeVelocity` |
| `SurgeEffect` (P) | Surge to pitch | PrimaryPlatform | `AccelerationSurge` (`ForwardWhenBraking`) | `PitchDegrees` | `Surge` |
| `Surge6DofsEffect` (P) | Surge | PrimaryPlatform | `AccelerationSurge` | `SurgeMm` (geometries that accept it) | `Surge` |
| `SwayEffect` (P) / `Sway6DofsEffect` (P) | Sway to roll / Sway | PrimaryPlatform | `AccelerationSway` (`OutwardForces`) | `RollDegrees` / `SwayMm` | `Sway` |
| `HeaveEffect` (P) | Heave | PrimaryPlatform | `AccelerationHeave` | `HeaveMm` | `Heave` |
| `Yaw6DofsEffect` (P) | Yaw / Yaw (Continuous) | PrimaryPlatform | yaw via `ContinuousYawTracker` | `YawDegrees` | `Yaw` |
| `TractionLossEffect` / `TractionLoss6DofsEffect` | Rear traction loss to roll / to yaw | PrimaryPlatform | TL angle (below) | `RollDegrees` / `YawDegrees` | `LocalVelocity` or `DirectTractionLoss` |
| `SuspensionEffect` | Suspensions | Haptic | `SuspensionVelocityMs[4]` (0 below 0.5 km/h) | corner axes, `ExtraHeaveMm`, `ExtraPitchDegrees` ((FL+FR−RL−RR)/2), `ExtraRollDegrees`, belt centre | `SuspensionVelocity(Partial)` |
| `TractionLossHapticsEffect` | Traction loss | Haptic | lateral/forward velocity, 25–45 Hz oscillator (`Sensitivity`, `SignalGrain`, `SideFocus`) | corners L/R, belt, dedicated heave/roll | `LocalVelocity` |
| `WingsLoadHapticsEffect` (F) / `FlightTouchdownHapticsEffect` (F) | Wings load / Touch down | Haptic | √((2·pitchRate)² + (rollRate/2)²) / ground-contact increments → pulse | corners, belt, dedicated axes / `HeaveMm`, belt | `YawChangeVelocity` / `FlightGroundContactsCount` |
| `RumbleStripsEffect` (R) | Rumble strips | Haptic | `WheelsOnKerbs[4]`, speed factor (5–100 km/h)³ | corners/heave, belt, dedicated heave/roll | `WheelsOnKerbs` |
| `EngineEffect` | Engine vibration | Haptic | RPM, MaxRPM, throttle, surge; `EngineEffectShape` Legacy/V2, `StopAtSpeedKmh`, `V2_*` | `HeaveMm`, belt, dedicated heave/pitch/roll | `RPM` |
| `GearChangeEffect` (R) | Gear change | Haptic | gear change → pulse | `PitchDegrees`, `HeaveMm`, `SurgeMm`, secondary, `ExtraSurge*`, belt, dedicated | `Gear` |
| `ABSEffect` (R) | ABS trigger | Haptic | `ABSActive` → up to 3 pulses (`PulseManager`) | `CornerFrontLeftMm`/`CornerFrontRightMm`, belt, dedicated heave/pitch | `ABSActive` |
| `AudioEffect` | Audio to haptics | Haptic | WASAPI loopback, biquad low-pass (`LowPassFrequency`, `PreAmp`) | corners L/R, belt, dedicated axes | — |
| `ExtraAxis{Heave, Pitch, HeaveToPitch, PitchRate, SurgeToPitch, Roll, RollRate, SwayToRoll, TractionLossToRoll, Sway, Surge, SurgeSecondary}Effect` (P) | dedicated-axis variants | `MotionExtraAxis*` | the same inputs | `ExtraHeaveMm`, `ExtraPitchDegrees`, `ExtraRollDegrees`, `ExtraSwayMm`, `ExtraSurgeMm`, `ExtraSurgeSecondaryMm` | — |
| `ExtraAxis{TractionLoss, TractionLossSway, TractionLossYaw}Effect` (P) | single TL table | MotionExtraAxisTractionLoss | TL angle / sway / continuous yaw | `ExtraTLDegrees` | — |
| `ExtraAxisFrontRear{TractionLoss, TractionLossSway, TractionLossYaw}Effect` (P) | front + rear TL | MotionExtraAxisTractionLoss | TL angle (`Bias` splits range) / sway / yaw | `ExtraTLFrontMm` (negated), `ExtraTLRearMm` | — |
| `ExtraAxisBelt{Surge, Sway, SingleSway, Heave}Effect` (P) | belt tensioner | BeltTensioner | `BeltAcceleration*` (`NegativeTorqueMode`, `ActivateOnlyOnBraking`, `BrakingLevelPercent`) | `ExtraBeltTensioner{Center, Left, Right}Percent` | — |

TL angle (`TractionLossEffectHelper.GetTLAngle`): direct mode = `−DirectTractionLoss` when the game
exposes it; `TractionLoss` mode = acos-based angle between local velocity and forward, blended with
yaw rate at low speed (`YawRateSensibility`), faded in over 5–30 km/h; velocity mode =
`LocalVelocityLateral` faded over 5–30 km/h; then washout, DRC and an optional "controlled
velocity" `SignalFollower`. `SpinAndLockHapticsEffect` has a defaults file but is never registered.

**Defaults**: `Motion/EffectsDefaults/{Racing|Flight}/<EffectClass>.json` is the serialised settings
object (flat keys above + `SettingsVersion`); `MotionEffectBase.LoadDefaultSettings(family)` applies
hard-coded defaults, then `EffectsDefaultsOverrides/<Family>/<Type>.json` (user "save as default"),
else the factory file, via `JObject.Parse(...).Populate(Settings)` (unknown keys ignored — the
legacy `MaxInputRange` keys in `PitchRateEffect.json` are dead). Many `Secondary*`/`ExtraAxis*`
effects reuse their parent's file (`LoadDefaultsFromType()`). Defaults apply only when an effect is
first added (`AddIfMissing`) or on `ResetSettings`. **Profiles**: `MotionDevice :
ProfileSettingsBase<MotionEffectsProfile, MotionDevice>` (`SimHub.Plugins.ProfilesCommon`, see
[Profile System](#profile-system-simhubpluginsprofilescommon)) with `FilterByGameFamily`; each
profile has a `TargetGameFamily`, `ProfilePresets` (`ActivePreset`, per-effect `PresetSettings`), and
`ProfileSettings`: `GlobalSmoothing`, `CategoryGains`/`SecondaryCategoryGains`, crash filter,
artefact removal, low-speed / pit-limiter / on-ground dampening, `UseSanitizedValuesForMotion`,
`CenterOfRotationOffset`, `ActuatorStrokeLimitAndOffsetOverride`.

### Geometries

**DOF inputs** (`Simhub.Plugins.Motion.Models.MotionAxis`, carried in `MotionState`, physical
units — degrees, millimetres, percent): 0 `YawDegrees`, 1 `PitchDegrees`, 2 `RollDegrees`,
3 `HeaveMm`, 4 `SurgeMm`, 5 `SwayMm`, 6–9 `Corner{FrontLeft,FrontRight,RearLeft,RearRight}Mm`,
10/11 `Side{Left,Right}Mm`, 12 `ExtraSwayMm`, 13 `ExtraSurgeMm`, 14 `ExtraTLDegrees`,
15/16 `ExtraTL{Front,Rear}Mm`, 17–19 `ExtraBeltTensioner{Left,Right,Center}Percent`,
20–22 `ExtraWind{Left,Right,Center}Percent`, 23 `ExtraSurgeSecondaryMm`, 24 `ExtraHeaveMm`,
25 `ExtraPitchDegrees`, 26 `ExtraRollDegrees`, 100/101 `Dummy1/2`. Directions: pitch Rear/Front,
roll Left/Right, heave Down/Up, surge Backward/Forward.

A device geometry is an `ICompositeGeometry` (`CompositeGeometry<TBase>`): one **base geometry**
plus extra axes (`Axis: ObservableCollection<IMotionExtraAxis>`). `UpdateCore`: extra axes
`Premix`, base `UpdateCore`, extra axes `UpdateCore` merged with `GeometryResult.MergeWith`
(same-role positions add). Base geometries reset the DOFs they do not support.

| Composite (picker name) | Base class | Inputs | Output roles |
|-------------------------|-----------|--------|--------------|
| `GenericGeometry3Dof4Linear` "3 DOFs / 4 corners" | `Geometry3Dof4Linear` | pitch, roll, heave, 4 corners | `LinearCorner{FL,FR,RL,RR}` (1–4) |
| `GenericGeometry3Dof4Linear2F1R` / `…1F2R` | `Geometry3Dof4Linear2F1R` / `1F2R` | same, rears or fronts averaged | FL, FR, `LinearRearCenter` / `LinearFrontCenter`, RL, RR |
| `GenericGeometry2Dof2Linear` "2 DOFs / Left right (Seat mover)" | `Geometry2Dof2Linear` | pitch, roll, side L/R, heave | `LinearSide{Left,Right}` (31/32) |
| `GenericGeometry6DofsRotary` / `…6DofsLinear` "6DOFs Rotary / Linear Hexapod" | `Geometry6DOFsStewartRotary` (crank + rod) / `…StewartLinear` | yaw, pitch, roll, heave, sway, surge, corners | `SixDOFsHexapod*` (51–56) |
| `GenericGeometryFourDofsLinear` "4DOFs Linear" | `GeometryFourDofsLinear` | yaw (coupled from a rear A-frame), pitch, roll, heave, corners | `FourDofsLinearCorner*` (33–36) |
| `GenericGeometryYawVRBypass` "Yaw VR 1/2/3 motion" | `GeometryYawVRBypass` | yaw, pitch, roll, corners | **none** — output reads `GeometryResult.MotionState` (pose); forces `YawVROutput` |
| `GenericGeometry6DOFsBypass` "6DOFs Bypass" | `Geometry6DOFsBypass` | 6 DOF + corners folded into a pose | **none** — pose only (use `<PoseAxis:…>` tokens) |
| `GenericGeometryNull` "Add-ons only" | `NullGeometry` | — | extra axes only |
| `GenericGeometry2DOFSeatMover{Linear,Rotary}` (not in picker) | `Geometry2DOFSeatMover*` | pitch, roll, side L/R, heave | 31/32 |

Extra axes (`Geometry.ExtraAxis.*`): `SurgeAxis` / `SurgeAxisSecondary` / `SwayAxis` / `HeaveAxis`
(mm ÷ stroke/2, speed-limited by `ActuatorsSpikeFilterMmPerSecond`), `PitchAxis` / `RollAxis`
(deg ÷ `AxisRangeHalfDegrees`), `TLSingleAxis` (deg ÷ `MaximumAngle`/2 → `SingleTractionLoss`),
`TLFrontRearAxis` (→ `Front/RearTractionLoss`), `BeltSingleAxis` / `BeltDualAxis` /
`PTABeltDualAxis` (positive input `CenterPosition`(+`CenterOffset`) → `PullLimit`, negative →
`ReleaseLimit`, `SplineFilter`, `KeepParked`), `WindAxis{Center,LeftRight,Triple}` (0..100 % ×
`MaxPercent` → −1..+1, fed by `WindMotionAdapter`, not effects).

**3 DOF / 4 corners solve** (`Geometry3Dof4Linear.Update` + `Core/Geometry3Dof4LinearHelper`):
stroke-limit transition and heave/pitch/roll offsets → reset sway/yaw/surge, `AxisLimitingSettings`
down-scalers, angular spike limiter (`ActuatorsSpikeFilterDegreesPerSecond` 50 °/s) → inverse
kinematics: corners at (±`RigLength`/2, ±`RigWidth`/2) (optional `RigWidthFront`), rotate by
pitch/roll about `CenterOfRotation{Front,Right}Offset`, translate by heave, leg extension by
floor-plane projection → `ActiveDownScaler`: when max |leg| exceeds the stroke (limit, safety
`ActuatorSafetyRangePercent` 20 %, prediction 5 %) a binary search scales the whole pose down →
`Position = Offset(0, stroke, leg) × 2 − 1` (**−1 fully retracted, 0 mid-stroke, +1 fully
extended**) + corner haptics `mm / (stroke/2)` → clamp ±1, `ActuatorsSpikeFilterMmPerSecond` (500)
→ `SetAxisPosition(role, range = ActuatorsStroke, Mm, pos, acceptBoost: true)`. Defaults:
`RigLength` 1000, `RigWidth` 600, `ActuatorsStroke` 150; capacities `PitchCapacity = 90 −
atan((L/2 + |COR|)/(strokeLimit/2))`, `HeaveCapacity = strokeLimit/2` feed the effects' bounds.
Hexapod / 4 DOF / seat movers use `SixDofInput` and per-actuator `IActuator` models (linear or
rotary crank) and report `PrimaryOverflowPercent = (1/LastScale − 1) × 100`.

**Results**: `ActuatorPosition { Position (−1..+1, 0 = centre; belts/wind −1 = released/off),
BoostedPosition?, Range (stroke mm / degrees / 100 %), Unit (None, Mm, Degrees, Percent),
CrankLength, CanBeBoosted }`; `GeometryResult { Axis, UnscaledAxisCenter, MotionState (final pose),
PrimaryOverflowPercent, PitchOverflowPercent, RollOverflowPercent, spike-limiter counters,
MotionCompensation* fields, PrimaryAxisStates, BeltAxisStates }` with `SetAxisPosition`,
`GetAxisValueOrDefault`, `GetBoostedAxisValueOrDefault`, `Mix`, `MergeWith`;
`CompositeGeometryResult { PrimaryGeometryResult, SecondaryGeometryResult }` with
`GetTarget(MotionTarget)`. **Secondary** = `MotionTarget.Secondary`, a second geometry stacked on
the primary (the picker only creates it as a `GenericGeometry2Dof2Linear` seat mover when
`HasSeatMover`); `Secondary*` effects, `Global.SecondaryPlatform.Gain` and roles assigned with
`MotionTarget = Secondary` (`LinearSideLeft/Right`) drive it. **Boost** = the "Motion detail
amplifier" (`MotionOutputBase.BoostActuators`, `Outputs.Booster/FineMotionAssist`,
`FineBoosterSettings { IsEnabled, SmallMovementBoost 3, MaximumBoostedVelocity 500,
ReturnSpeedScale 1.5, AllowedDriftFactor 2, Gain 100 (0..300) }`): amplifies small slow movements
of `CanBeBoosted` positions (a velocity-dependent high-pass), faded out within `EdgeSafetyMargin`
0.1 of the ends, written to `BoostedPosition`; only outputs calling `GetAxisValue(…,
boostAllowed: true)` see it.

`GetAxisValue` exactly: `k = (Roles[idx]?.RangeLimit ?? 100) / 100`; a `(ActuatorRole)(500+idx)`
entry returns its raw `Position` (park/unpark transitions); `(100+idx)` returns `Position × k`
(identification test); no assignment → `defaultUnassignedValue` (0 = mid-stroke); else the boosted
or plain value for `(MotionTarget, Role)` × k, negated when `ReverseDirection`. **No clamping** here
(geometries clamp earlier). Park position = `ParkPosition/50 − 1` (negated when reversed), used only
when `UseParkPositionEx`; `UseParkPositionsFromRoles` takes the role's `DefaultParkPosition` (−1 for
lift roles, 0 for surge/TL/sway/wind).

### Actuator ordering and the assignation dialog

`ActuatorOrderingSettings`: serialised `Roles` (auto-resized to `MaxActuators` with `DefaultRoles`),
`ConfigurationDone` (default true; `ConfigurationIncomplete = !ConfigurationDone && MaxActuators > 0`
blocks start with "Actuator order settings have not been configured."), `UseParkPosition`,
`ParkDuration` (5000 ms); not serialised `TestingAmplitude` 2 %, `BeltTestingAmplitude` 10 %,
`TestingFrequency` 2 Hz. `TryLoadDefaultMapping` auto-assigns role groups present in the geometry
(4-corner order RL, FL, FR, RR; hexapod order; TL + surge). `ActuatorRoleAssignment { Role,
MotionTarget, ReverseDirection, RangeLimit % (100), ParkPosition % (50), ParkPositionFromRole,
ManualPosition, Identify, IdentificationMode (None, LeftDown, UpRight, Absolute, All),
OutputNumber, RoleMissing }`.

`ActuatorOrderingIdentificationDialog`: sets a no-op `IdentificationCallback` (suspends the normal
device update), calls `output.Start(isAssignationMode: true)` on that one output with its own
`OutputWorker(keepAlive: true)`, then feeds the selected channel through `(ActuatorRole)(100+idx)`:
`None` = sine × `TestingAmplitude`, `LeftDown`/`UpRight` = `(lfo ∓ 1) × amplitude`, `Absolute` =
`ManualPosition/50` (all negated when reversed; belts use `BeltTestingAmplitude`); switches to
`keepAlive: false` once unparked; the user picks a role per channel from `AvailableRoles`;
`OkAsync` rejects duplicates and missing primary roles and runs `TestAll` for direction-sensitive
roles. Start-time check (`IMotionOutputExtensions.GetOutputErrors`): with
`PowerSettings.CheckAxisAssignment` every geometry role must be mapped by some active controller;
`CheckAxisAssignmentRelax` (default) lets a whole `ComponentGroup` idle unmapped.

### Power, idle, safety

`PowerSettings` (per device): `StartMode` (`Last`; Off forces disabled, AlwaysOn enables when the
configuration is valid — applied only at Init), `UseIdleTimeout` (true), `IdleTimeoutSeconds` (30),
`IdleDetectionMode` (0 = a running game process counts as activity; 1 = connected or paused;
2 = connected and not paused), `StartInIdle` (true), `AfterConnectMotionDelay` (5 s; also floors
e-stop recovery at 4 s), `DisconnectReconnectDelay` (0 s cooldown before restart),
`ShowForceOnlineButton`, `CheckAxisAssignment`/`…Relax`, `UseSafetyDelays` (display text only),
`IsInitialized`. Activity is also kept alive by `ForceSuspend`, the OpenXR receiver, simulated /
remote / quick-test sources; `ForceOnline` or sensor calibration cancel idle.

Idle: 2 s to zero, then `Output.KeepAlive` every tick; sub-outputs with `AllowIdling` park once
and run `KeepAliveInternal` (output-specific heartbeat or hold), otherwise they keep receiving
`Update` with the zero pose; leaving idle unparks and ramps over 2 s + `AfterConnectMotionDelay`.
`IsEnabled` is the on/off switch (`Enable()`/`Disable()` = zero, park, `Stop()`); `IsSuspended`
(`Global.SuspendMotion`) and `ForceSuspend` hold a zero pose with outputs live. **E-stop**: an
output raising `SafetyState.EStopped` (Thanos hardware e-stop) or `ConnectionState = Failure`
freezes the last pose, shows `EStop`/`Failure`, can play `Sounds\Motion-Error.mp3`, recovers over
≥ 4 s. "Overflow" is only the geometry down-scaling percentage (`Global.Geometry*Overflow`).

### Licence gating

`SimHub.Licensing.GlobalLicenseManager` (`HasActiveMotionLicence`, `MotionTrialActive`,
`StartMotionTrial`, …) — the **only functional gate** is in `MotionWorkerBase.ProcessCore`:
`if (!haslicence && !MotionTrialActive()) shared = new MotionInputData();`. Unlicensed, the whole
pipeline still runs on **empty telemetry**: outputs start, connect, unpark, park and idle; manual,
simulated, quick-test and assignation sources still move the platform; game-driven motion stays
neutral. The trial runs only while live, on track, enabled and past the welcome layer
(`MotionPluginSettings.ShowLayer` forces disabled until `Enable()`).

### Feedback inputs and motion compensation

- **WitMotion IMU** (`SimHub.WitMotion.dll` → `WitmotionImuReader`; `MotionCompensation/WitMotionManager`,
  one per device): serial 115200, 100 Hz; `SerialPort`, `MountingMode`, `YawCorrection`,
  `YawDriftCorrectionEnabled`, `ZeroOrientation`. Not used for actuator control — it feeds VR
  motion compensation (`SixDofsMotionCompensation.Update`: `EnablePhysicalSensor`,
  `PhysicalFactor`, `PhysicalSmoothing`, `PhysicalGain`); "Calibrate sensor" parks at zero then
  `CalibrateFlat` / `CaptureZeroOrientation`. The COM port is reserved from SimHub's scanner.
- Motion-compensation outputs: a 6-DOF rig pose to the memory-mapped file `Local\motionRigPose`;
  OpenXR-MotionCompensation telemetry to `Local\OXRMC_Telemetry` (the OpenXR `CorEstimator` can
  also be an input pose source). `OpenVRMotionCompensation` exists but `MotionCompensations =
  { OpenXRMC, OpenXRMC }` lists OpenXR twice, so OpenVR never updates (apparent bug).
- `YawVROutput` reads tracker yaw/pitch/roll, temperatures and battery back over TCP/UDP; with
  `UseYawVRForMotionCompensation` the angles become `MotionCompensationExternal{Yaw,Pitch,Roll}`.
- Apart from the SCN DLL and YawVR, **no actuator-position feedback path exists**; outputs are
  open-loop.
- Other sources: `SimulatedMotionStateAggregator` (manual/auto motion from the UI),
  `QuickMotionTest`, `RemoteSimulatedMotionStateAggregator.Enable(id)/KeepAlive(id)` (2 s timeout;
  no caller in the DLL — `ApiEnabled`/`ApiToken` suggest the web API), `WindMotionAdapter`.

### Exported properties and actions (plugin "MotionPlugin")

Properties: `Global.GeometryPrimaryOverflow`, `Global.GeometrySecondaryOverflow`,
`Global.DeviceState`, `Global.MotionEnabled`, `Global.ActiveMotionSetup`, `Global.MotionSuspended`;
`Global.{MotionOrientations | PrimaryPlatform | SecondaryPlatform | Sway | Surge | SurgeSecondary |
Heave | Pitch | Roll | Haptics | BeltTensioner | TractionLoss}.Gain`;
`Global.MotionDetailAmplifier.{Enabled, Gain}`, `Global.GlobalMotionSmoothing`,
`Global.ActiveProfilePreset`, `Global.BeltTensioner.{KeepParked, Offset}`,
`Global.TrackProfiler.*`, `YawVR.{TemperatureRoll, TemperaturePitch, TemperatureYaw,
TemperatureMax}`, `MotionCompensationComponent.<FilterCode>.{Enabled, TargetScale,
TargetSmoothing, SpeedLimiter*}` (+ `Sensor*`, "Smooothing" spelled with three o's), per effect
`<EffectClass>.{Available, Enabled, IsIsolated, IsMuted, Gain, Smoothing, EnableWashout, Washout}`,
dynamic `YawDiff`. **No per-actuator position property exists.**

Actions: `Global.{ToggleMotionEnabled, EnableMotion, DisableMotion, SuspendMotion,
CalibratePhysicalSensor, OpenEffectsCompactView}`, `Global.BeltTensioner.{Set/Disable/Toggle}KeepParked`
/ `{Increment/Decrement/Reset}Offset`, `Global.*.{Increment,Decrement}Gain`,
`Global.MotionDetailAmplifier.{Toggle, IncrementGain, DecrementGain}`,
`Global.GlobalMotionSmoothing.{Increment, Decrement}`, `Global.{Next,Previous}ProfilePreset`,
`Global.ToggleTrackProfiler`, `Recording.MarkMotionTelemetryRecord`,
`Filters.ToggleCrashFilterSoundFeedback`, `QuickTest.Toggle{Roll,Pitch}Test`, per effect
`<Effect>.{ToggleState, ToggleIsolated, Increment/DecrementGain, Increment/DecrementSmoothing}`,
dynamic button `MotionGearChange`.

### The output contract (all public)

```csharp
// SimHub.Plugins.Motion.Contracts
[JsonConverter(typeof(AbstractConverterAllowNull<IMotionOutput>))]
public interface IMotionOutput : IDisposable, IAbstractSerialize
{
    void Start(bool isAssignationMode = false);
    void Stop();
    void Update(CompositeGeometryResult geometryResult);
    void KeepAlive(CompositeGeometryResult geometryResult);
    bool IsConnected { get; }
    OutputConnectionState ConnectionState { get; }
    Control SettingsControl { get; }            // WPF
    int RefreshRateMs { get; }
    void DataUpdate(PluginManager pluginManager, GameData data);
    IEnumerable<string> GetReservedSerialPorts();
    Guid OutputId { get; }
}

public abstract class MotionOutputBase<TSettings> : IMotionOutput<TSettings>
    where TSettings : OutputSettingsBase
{
    public abstract string Name { get; }
    public abstract string PreviewIcon { get; }
    public abstract Control SettingsControl { get; }
    protected abstract IEnumerable<string> GetOutputConfigurationErrorsInternal(bool ignoreAxisAssignment);
    protected abstract void StartInternal();
    public abstract void StopInternal();
    protected abstract void KeepAliveInternal(CompositeGeometryResult geometryResult);
    protected abstract void UpdateInternal(CompositeGeometryResult geometryResult, TSettings currentSettings);

    public virtual int RefreshRateMs => 2;      // worker thread sleeps this between Update calls
    // virtual hooks: BeforeStartingMotion(geo), BeforeStoppingMotion(), BeforeStartingIdle(),
    //                BeforeStoppingIdle(), Park(), Unpark()
    public virtual double GetAxisValue(CompositeGeometryResult geometryResult, int idx,
        TSettings currentSettings, bool boostAllowed = false, double defaultUnassignedValue = 0.0);
    protected void ProcessCommunicationError(Exception ex);
    public string TypeName => GetType().Name;   // the key the loader matches on
}

public abstract class SerialOutputBase<TSettings> : MotionOutputBase<TSettings>
    where TSettings : OutputSettingsBase, ISerialPortSettings
{   // owns `protected ISerialPort SerialPort`; BeforeSerialOpen/AfterSerialOpen hooks;
    // implements StartInternal/StopInternal/KeepAliveInternal/GetReservedSerialPorts
}
// GenericSerialOutputV2 : SerialOutputBase<GenericSerialOutputSettingsV2> is the reference implementation.
```

Settings chain: `Simhub.Plugins.Motion.Models.OutputSettingsBase` →
`ActuatorOrderingSettingsBase(bool allowParkPosition, int actuatorsCount = 8) : OutputSettingsBase,
IActuatorOrderingSettings` → `GenericSerialOutputSettingsV2 : ActuatorOrderingSettingsBase,
ISerialPortSettings`. The output is created with `Activator.CreateInstance` and then
`serializer.Populate`d, so the settings object must exist from the constructor / field
initializer and persist with `[JsonObject(MemberSerialization.OptIn)]` + `[JsonProperty]`.

### What an output receives per tick

`Update` gets a `CompositeGeometryResult { PrimaryGeometryResult, SecondaryGeometryResult }`, each a
`GeometryResult` with `IReadOnlyDictionary<ActuatorRole, ActuatorPosition> Axis` (`ActuatorPosition
{ double Position; double? BoostedPosition; Range }`). These are **per-actuator values after
SimHub's geometry / DOF mixing**, not raw pitch/roll/heave. `GetAxisValue(geo, channelIdx,
Settings, boostAllowed: true)` resolves the channel's assigned role, applies `RangeLimit` and
`ReverseDirection` and returns the geometry's **−1..+1** position (0 = mid-stroke; it does not clamp
— see "Geometries" for the exact code); `GenericSerialOutputV2.UpdateInternal` simply formats
that per channel. The per-output worker thread runs every `RefreshRateMs` (2 ms default,
~500 Hz; the generic outputs keep 2 ms while `UseLegacyDelay` is on, otherwise the shortest
UpdateCommand delay).

MotionPlugin publishes only `Global.*` (DeviceState, MotionEnabled, overflow, gains, belt, track
profiler), `MotionCompensationComponent.*`, per-effect `*.Gain` / `*.Enabled` and YawVR
temperatures — **no per-actuator or `Axis1`-style properties**, so deriving from the base class is
the only way to get the mixed values.

### Roles and axis assignment

`ActuatorOrderingSettings.Roles[i].Role` is the `ActuatorRole` of output channel *i* (`Axis{i+1}`),
`0` = `None` (unused — not "auto"); the full enum with values is at the end of the hardware-presets
subsection below. `RangeLimit` (default 100) and `ParkPosition` (default 50) are stored per role;
`MaxActuatorsEx` is on `ActuatorOrderingSettingsEditable`. The SimFeedback sample preset ships every
role as 0 with `ConfigurationDone: false`, so the user maps channels in the UI.
`AxisFormat { Unset = 0, Binary = 1, NumberString = 2, HexString = 3 }` and `AxisResolution` (bits)
drive the generic template tokens — see "Generic template language" below.

### Registration — the picker is closed, the loader is open

A plugin cannot add an entry to the hard-coded picker list, but every saved or imported controller
is materialised by `TypeName` through a resolver that scans plugin DLLs too (details under "How the
controller list is built"). Net effect: a `public class MyOutput : SerialOutputBase<MySettings>` (or
`MotionOutputBase<…>` for a non-serial transport; parameterless constructor; short name not
clashing with a built-in) plus a `Motion\Presets\<name>.shmotioncontroller` =
`{"Output": {"TypeName": "MyOutput", "CustomName": "…", …}}` (**`CustomName` required**) appears
under *Presets*, and the same file imports through the import dialog. Folder layout:
`Motion/Presets/` (empty by default), `Motion/MotionHardwarePresets/*/config.shmotionoutput`,
`Motion/EffectsDefaults/{Racing,Flight}/`, `Motion/EffectsDefaultsOverrides/`; saved setups under
`PluginsData/Common/MotionInterfaces/` and `MotionPlugin.GeneralSettingsV2.json`.

### Power settings (`StartMode`, `IdleDetectionMode`, …)

```csharp
public enum Simhub.Plugins.Motion.Models.OutputStartMode { Off, Last, AlwaysOn }
public enum Simhub.Plugins.Motion.Models.IdleDetection
{ GameConnectOrPausedAndProcess, GameConnectedOrPaused, GameConnectedNotPaused }
```

Current builds keep these on `MotionDevice.PowerSettings` (`StartMode`, `UseIdleTimeout`,
`IdleTimeoutSeconds`, `StartInIdle`, `IdleDetectionMode`, `AfterConnectMotionDelay`,
`DisconnectReconnectDelay`, …), not on the output settings. The flat fields in an old
`.shmotioncontroller` (as in the SimFeedback sample) are legacy: they land in
`OutputSettingsBase`'s `[JsonExtensionData] internal JObject ExtParams` and are copied into
`PowerSettings` once, if it has not been set up yet.

### How the controller list is built (`SimHub.Plugins.Motion.UI.OutputPicker`)

Verified by decompiling `SimHub.Plugins.Motion.dll` (9.12.8) in full — 997 files — and
`AbstractConverter<,>` out of `SimHub.Plugins.dll`.

- The picker constructor hard-codes three lists, each shown **sorted by `Name`**:
  *Generic* — `GenericSerialOutputV2`, `GenericUDPOutputV2`, `DummyOutput`;
  *Standard* (source order) — `DynamicXDX2UltraOutput`, `ThermaltakeGM53DofsOutput`,
  `Cammus3DofsOutput`, `TrakRacer3DOFMotionSystemOutput`, `RaceBearMotion4XOutput`,
  `ThanosAMCControllerOutput`, `Thanos4UControllerOutput`, `VNMControllerOutput`,
  `Motion4simControllerOutput`, `PTActuatorsCANOutput`, `DIYSimHubBeltTensionerOutput`,
  `SMC3OutputH6P6`, `SMC3OutputV2H3P3H2P2`, `SMC3OutputV2H4`, `SMC3OutputNJMotionEvoLegacy`,
  `SMC3OutputV2Single`, `SCNOutput`; *Legacy* — `ThanosAMCOpenHardwareControllerOutput`,
  `ThanosAMCMDBOXControllerOutput`.
- *Presets*: every `Motion\Presets\**\*.shmotioncontroller` deserialised as
  `Simhub.Plugins.Motion.Settings.MotionControllerSettings { IMotionOutput Output }`; rejected when
  `Output` is a `ControllersAggregatorOutput` or `CustomName` is empty. The shipped folder is
  empty; the UI links to github.com/SHWotever/SimHubMotionPresets. "Import from file" takes the
  same format (`ImportType = Imported`).
- **Resolution of `TypeName`** — `SimHub.Plugins.SettingsBuilderModule.AbstractConverter<T, UDefault>`
  (decompiled): a static `PluginFinder.GetResolver(…, typeof(T)).GetPlugins()` is filtered to
  non-abstract types assignable to `IMotionOutput`, grouped by `Name.ToLowerInvariant()`; `ReadJson`
  looks up `TypeName`/`typeName` lower-cased, falls back to `UDefault` when it is concrete, else
  null when `AllowNull`, then `Activator.CreateInstance(type)` + `serializer.Populate(...)`. The
  finder is the same assembly scan that discovers `IDeviceExtensionFilter` /
  `IDeviceDescriptorsRegistry` implementations in plugin DLLs, so a public non-abstract
  `IMotionOutput` in a plugin assembly is resolvable by its short class name. Not in the picker,
  but reachable through a preset or import file.
- Outputs that are never in the picker: `YawVROutput` and `PTActuatorsBeltForceOutput` are
  *forced* by their geometry (`GeometryYawVRBypass.GetForcedOutputType()`,
  `PTABeltDualAxis.GetForcedOutputType()` → `MotionDevice` does `Activator.CreateInstance`);
  `ControllersAggregatorOutput` ("Multiple controllers") is created internally and runs each child
  on its own `OutputWorker` thread; the legacy `GenericSerialOutput`, `GenericUDPOutput`,
  `SMC3Output`, `SimFeedbackOpenSFXOutput` (its `StartInternal` throws "deprecated"),
  `DMoverControllerOutput`, `SCNHyperAxisOutput` exist only so old saved JSON still loads.
- Serialised members of an output: `TypeName`, `OutputId`, `CustomName`, `CustomNamePattern`,
  `ShowOriginalName`, `Comments`, `ImportType`, `AutomaticOrigin`, `ReviewManager`, `Settings`.
  `Settings.ActuatorOrderingSettings.Roles[] = { Role, MotionTarget, ReverseDirection, RangeLimit,
  ParkPosition }` plus `ConfigurationDone`, `UseParkPosition`, `ParkDuration`, `MaxActuatorsEx`.

### Shared output conventions

- Every output receives per-channel values in **[−1, +1]** from `GetAxisValue` (applies
  `RangeLimit/100` and `ReverseDirection`; role `500+idx` is a raw test value).
- Lifecycle: `StartInternal` → `AfterStarting`; first `Update`: `BeforeStartingMotion` → `Unpark`
  (ramp to live position) → `UpdateInternal` every tick; going idle: `Park` → `BeforeStoppingMotion`
  → `KeepAliveInternal` loop (`BeforeStartingIdle` / `BeforeStoppingIdle`); stop: `BeforeStopping`
  → `StopInternal`.
- `SerialOutputBase<T>` opens the port at `BaudRate` (default 250000) with RTS = DTR = true and a
  5 s write timeout; `KeepAliveInternal` writes a zero-length buffer every 500 ms. Its **default
  frame** is `FF FF` + `ProtocolAxis` (8) × uint16 **big-endian** + `0A 0D`, with
  `ConvertTo16Bits(v) = clamp((v/2 + 0.5) × 65536, 0, 65535)` — centre `0x8000`.

### Built-in output controllers (9.12.8)

No code exists for Qubic, ProSimu, Next Level Racing, Sigma Integrale, MotionAlpha, Simucube or
MOZA; FlyPT appears only in the OpenVR/OpenXR motion-compensation UI; DOF Reality is a set of
SMC3 (Arduino) wrappers; VeroMotion, Novus and eRacing-Lab exist only as hardware presets over the
PT-Actuator and Thanos outputs.

| Class | UI name | Transport | Channels / roles | Rate | Per-tick frame and handshake |
|-------|---------|-----------|------------------|------|------------------------------|
| `GenericSerialOutputV2` | Generic serial output | serial, user baud (default 115200), RTS default on, DTR off, `AfterOpenDelay` | 10, user-assigned | 2 ms unless `UseLegacyDelay` off | user template (below) |
| `GenericUDPOutputV2` | Generic UDP output | UDP `TargetIPAddress:TargetPort` (127.0.0.1:11000), optional ping check | 10 | same | one datagram per command, no reply wait |
| `DummyOutput` | Testing virtual output | none (WPF window) | 10 | 2 | shows Axis1..10; can simulate E-stop / failure / not-found |
| `DynamicXDX2UltraOutput` | DynamicX DX2 Ultra | serial 921600 8N1, RTS/DTR off | 2 fixed: `LinearSideLeft`, `LinearSideRight` | 10 | `54 00 02 [A1 BE16] [A2 BE16] 56` |
| `ThermaltakeGM53DofsOutput` / `TrakRacer3DOFMotionSystemOutput` / `RaceBearMotion4XOutput` (all `GM53DofsOutput`) | Thermaltake GM5 3DOF / Trak Racer 3DOF / Race Bear Motion 4X | serial 115200 8N1, RTS on | 4 fixed: FR, RR, RL, FL; park at bottom | 2 (≥ 4 ms between frames) | `"HA"` + 4 × BE16, no terminator |
| `Cammus3DofsOutput` | Cammus Dynamic Racing Simulator | serial 115200 8N1 | 4 fixed: FL, FR, RL, RR | 2 | `61 62 [A1][A2][A3][A4] 63 64`, **8-bit** per axis |
| `ThanosAMCControllerOutput` | Thanos AMC Controller (AASD15A) | serial 250000, FTDI VID `0x0403` filter | 7, user-assigned | 2 | `FF FF` + 8 × BE16 + `0A 0D`; text handshake `RQM` → `AMC…`/`Thanos…` version, `C:<type>`, `CMD56`, spike filters `spv15..19`, enhancements (fw ≥ 2.26.8): `CMD60/66/65/63/70/64/62V`; async `SH:E-stopped:`, `SH:Active:`, `SH:Park_Done:` |
| `Thanos4UControllerOutput` | Thanos Thanos4U Controller | same | 4 (`T4UM`/`T4US`), enhancements fw ≥ 1.02.9 | 2 | same 20-byte frame + handshake |
| `ThanosAMCOpenHardwareControllerOutput` / `ThanosAMCMDBOXControllerOutput` (legacy) | Thanos Open Hardware / AMC MDBOX | serial 250000 | 7 | `UpdateIntervalMs` (2) | plain `FF FF` + 8 × BE16 + `0A 0D`, no handshake |
| `VNMControllerOutput` | VNM Motion Controller | serial 250000, RTS/DTR on | 9, user-assigned | 2 | `FF FF` + 9 × BE16 + `0A 0D` |
| `Motion4simControllerOutput` | Motion4Sim Servo Motion Controller | serial 250000 | 8 | 2 | `FF FF` + 8 × **uint24 BE** + `0A 0D`; optional `F9 F9 80 00 00 00 0A 0D` calibrate-online at start |
| `DIYSimHubBeltTensionerOutput` | SimHub DIY Belt Tensioner | serial 250000 | 2 (`BeltLeft`, `BeltRight`) | 2 | `FF FF 01` + 2 × BE16 + `0A 0D`; start: `FF FF 0E` version, `FF FF 0A` motor count, speed `FF FF 02`, accel `FF FF 03` |
| `SMC3OutputV2Single` | SMC3 controller | serial 500000 (`Smc3Controller`), DTR = reset, forced for STM VID `0x0483` | 3, user-assigned | `Settings.RefreshRateMs` (10) | three 5-byte packets `[A hi lo][B hi lo][C hi lo]`; position 511 + v·511 (10-bit) or 2047 + v·2047 (12-bit fw); handshake `[ver]` → `[v hi lo]`, `[ena]`, PID `[D..O hi lo]`, 1 s zero-byte keepalive |
| `SMC3OutputV2H3P3H2P2` | DofReality M2 / MP2 / H2 / P2 / H3 / P3 (SMC3) | same | 3 fixed: `LinearSideLeft`, `LinearSideRight`(rev), `SingleTractionLoss`; PID defaults Kp130 Ki3 Kd4 Ks5 | 10 | same |
| `SMC3OutputV2H4` | DofReality H4 / P4 (SMC3) | same | 4 fixed: RR(rev), RL(rev), TL(rev), `LinearFrontCenter`(rev) | 10 | `[A][B][C]` + `[Y hi lo]` |
| `SMC3OutputH6P6` | DofReality H6 / P6 (SMC3) | **two** serial ports (`SerialPortL`/`SerialPortR`) | 6 hexapod roles, `RangeLimit` 95 | 10 | `[A][B][C]` per board; `[v6d][v6D]` reads the board side to auto-assign ports |
| `SMC3OutputNJMotionEvoLegacy` | NJMotion Evo (SMC3) | same | 2: side left/right(rev) | 10 | `[A][B][C]` |
| `SCNOutput` (+ hidden `SCNHyperAxisOutput`) | Dyadic Systems SCN5 / SCN6 | vendor DLL `TMBSCOM.dll` (copied to `%TEMP%` and `LoadLibrary`'d), `\\.\COMx` 115200 | 2 axes | 2 | `fn_move_abs(axis, centre + centre·v·SafeRange%)`; homing at start / before motion / stop; wire format hidden in the DLL |
| `PTActuatorsCANOutput` | PT-Actuator CAN Controller | vendor DLL `PT_MOTOR_DLL.dll` (P/Invoke, `Serial_OpenPort(n, 115200)`) | up to 15 CAN ids; default RL, FL, FR, RR, RearTL, Surge, FrontTL(rev), BeltL/R | `RefreshIntervalMs` 1–50 (5) | per motor `PP_Abs_move` / `PP_Abs_move_enhance` with pos = (v+1)/2 · 10000; `LED_Set`; E-stop poll; start: online check, versions, `CMD_Enable_motor` ×15, homing `HM_Set`/`HM_start_motor`, `PP_Set(vel, acc, dec)`; stop `PP_stop_motor`, `Serial_ClosePort` |
| `PTActuatorsBeltForceOutput` (forced) | PT-Actuator CAN BeltForce | same DLL | motors 7/8 only (belts) | 5 | same |
| `YawVROutput` (forced by `GeometryYawVRBypass`) | Yaw VR | UDP 50010 (pose) + TCP 50020 (control); discovery by UDP broadcast `YAW_CALLING` | pose bypass, no actuators (YAW1/2/3) | 5 | ASCII `Y[yyy.yyy]P[ppp.ppp]R[rrr.rrr]` (roll and yaw negated); TCP `0x30` + UDP port + `"SimHub"`, `0xD4`, `0xF6`, `0xA1` start / `0xA2` stop / `0xA3` exit; 1 s status timer `E5 E4 B0 F6`; tracker feedback `Y[..]P[..]R[..]U[..]`; optional shared memory `YawVRGEFile` |
| `ControllersAggregatorOutput` | Multiple controllers | — | fans out to children | children's own | passes the geometry to every child worker |
| `DMoverControllerOutput` (hidden) | D-MOVER (DM-H3) | serial 1 500 000 | 4: FL, FR, RL, RR | 2 | `66 CC 00` + 17-byte payload + sum-checksum per motor, CANopen SDO setup at open; position scaling looks saturated at \|v\| ≈ 0.02 (apparent bug) |
| `SimFeedbackOpenSFXOutput` (deprecated) | OpenSFX SimFeedback AC-Servo | serial 460800 | 4 | 2 | `StartInternal` throws; the shipped `.shmotioncontroller` preset for SimFeedback uses `GenericSerialOutputV2` instead (`3,<CommandCounter>,<Axis1,string,-4096,4096>,…;` at 250000 baud, `10;` → `10,Arduino ready`, `14,<Setting,…>;`, keepalive `15;`, stop `7;`) |

`SimHub.WitMotion.dll` is an **input**: `MotionCompensation.WitMotionManager` reads a WitMotion IMU
over serial (115200, 100 Hz) for VR motion compensation; it is not an output.

### Generic template language (`GenericSerialOutputV2` / `GenericUDPOutputV2`)

Source: `SimHub.Plugins.Motion.Outputs.GenericCommon/GenericCommand.cs` (`UpdateRegEx`,
`PartFactory`). Text between tokens is sent as Latin-1 bytes; there are **no escape sequences**
(write `<10>`/`<13>` or `<0x0A>`/`<0x0D>` for CR/LF) and **no checksum/CRC token**. A token that
fails to parse is sent literally.

| Token | Where | Encoding |
|-------|-------|----------|
| `<AxisN>` (`N` = 1–9 digits, trailing `a`/`b` ignored — `<Axis10>` cannot be written) | Update / IdleUpdate only | `u = clamp((v+1)/2 · (2^R − 1))`, `R` = `AxisResolution` bits (default **8**); `AxisFormat` `Binary` → ⌈R/8⌉ bytes big-endian, `NumberString` → decimal ASCII, `HexString` → upper-case hex without leading zeros |
| `<Left>` / `<Right>` | same | Axis1 / Axis2 shortcuts |
| `<AxisN,string,min,max>` | same | decimal ASCII of (int) v mapped −1..1 → min..max; always a string |
| `<Actuator,Role>` (`ActuatorRole` name) | same | like `<AxisN>` but reads the geometry role directly (bypasses slot reverse/range); the factory indexes `array[2]` of a 2-part split — likely throws |
| `<ActuatorPosition:N\|map=a,b\|clamp=a,b\|format=F\|stringformat=S\|round=n\|littleendian>` | same | slot N through `FormatConverter.EncodeValue` (default Float, big-endian, 3 decimals) |
| `<PoseAxis:[Primary\|Secondary:]Axis\|rev\|convert=Unit\|clamp\|map=s0,s1,d0,d1\|format\|stringformat\|round\|littleendian\|bigendian>` | same | platform pose (`MotionAxis`: `YawDegrees`, `PitchDegrees`, `RollDegrees`, `HeaveMm`, `SurgeMm`, `SwayMm`, corners, `Extra*`, belts, wind) with unit conversion (`UnitKind`: Degrees, Radians, CentiDegrees, Millimeters, Meters, Percent, PercentZeroToOne, m/s, cm/s, m/s², °/s, rad/s) — the token to use with a 6-DOF pose-bypass geometry |
| `<Pose:…>` | same | v1 of the above (ints big-endian, floats native little-endian, no clamp) |
| `<NNN>` / `<0xHH>` | any phase | one raw byte |
| `<CommandCounter>` | any | per-token counter from 1, decimal ASCII |
| `<Setting,Name,Fmt>` | any | `string.Format("{0:Fmt}", Settings.Name)` from the protocol's `SettingsBuilder` (user NCalc settings). The editor's "insert setting" button writes `<Settings,…>` (plural), which does not match — sent literally |
| `<Setting:Name\|map\|clamp\|format\|stringformat\|round\|littleendian>` | any | numeric setting through `FormatConverter` |

`PartFormat`: `String, Float, Double, Uint8, UInt16, UInt24, UInt32, Int8, Int16, Int24, Int32`
(clamped to range); `StringFormatKind`: `None, String, AlwaysSignedString`; strings use `"0.###"`.

`GenericProtocolDefinitionV2`: `AxisResolution` (bits, default 8), `AxisFormat` (`Binary` default),
`UseLegacyDelay` (default **true**), `ShowAdvancedPhases`, `SettingsBuilder`, and the command lists
`StartCommands`, `UpdateCommands`, `StopCommands`, `ConnectedCommands`, `DisconnectCommands`,
`IdleStartCommands`, `IdleUpdateCommands`, `IdleStopCommands`. `GenericCommand { Command,
CommandDelay }`; `GenericCommandWithResponse` adds `MustWaitForMessage`, `WaitForMessage` (also a
template), `WaitForDelay` (timeout, 5000 ms) — serial only, `ReadExisting` until the text appears,
timeout = failure + port closed.

Phase semantics: `StartCommands` run in `BeforeStartingMotion` (at the first live update, before
unpark — not at port open); `UpdateCommands` every tick, each sent when `now − lastSent ≥
CommandDelay` (or always when `CommandDelay == RefreshRateMs`); `StopCommands` in
`BeforeStoppingMotion` after `Park`, with axis values reading 0; with `ShowAdvancedPhases`,
`ConnectedCommands` in `AfterStarting`, `DisconnectCommands` in `BeforeStopping`, `IdleStart` /
`IdleStop` on idle transitions, `IdleUpdateCommands` in `KeepAliveInternal`. `RefreshRateMs` is
2 ms while `UseLegacyDelay` is on; otherwise `max(1, min CommandDelay)` of the active update
list (vendor wrappers on `GenericSerialOutputV2Base`: the single update command's delay, else 2).

### Hardware presets (`Motion\MotionHardwarePresets\<dir>\`)

Read by `MotionPresets.MotionHardwarePresetsProvider.GetPresets()` for the setup wizard's
"Accessories" page (not the output picker). `preset.json = { Name, Brand, Comment, ParentPreset,
IsGenericPreset, DebugOnly }`; `config.shmotionoutput = { Geometry, SecondaryGeometry, Output }`
(a child without a config inherits its parent's config and logo); `logo.png`. **Every preset wraps
exactly one child output in a `ControllersAggregatorOutput`.** 25 folders ship in 9.12.8:

| Preset | Geometry (+ extra axes) | Child output | Roles in slot order |
|--------|-------------------------|--------------|---------------------|
| DOFReality H2 / P2 (parent `DOFReality_2DOFs`) | `Geometry2Dof2Linear` | `SMC3OutputV2H3P3H2P2` | 31 SideLeft, 32 SideRight (rev); `RefreshRateMs` 10 |
| DOFReality H3 / P3 (parent `_3DOFs`) | `Geometry2Dof2Linear` + `TLSingleAxis` | `SMC3OutputV2H3P3H2P2` | 31, 32 (rev), 6 SingleTractionLoss |
| DOFReality H4 / P4 | `Geometry3Dof4Linear1F2R` + `TLSingleAxis` | `SMC3OutputV2H4` | 4 RR (rev), 3 RL (rev), 6 TL (rev), 13 FrontCenter (rev) |
| DOFReality H6 / P6 (parent `_6DOFs`) | `Geometry6DOFsStewartRotary` (rod 560, crank 90) | `SMC3OutputH6P6` | 56 (rev), 55, 54 (rev), 51, 52 (rev), 53 — all `RangeLimit` 95 |
| Cammus 3DOFs | `Geometry3Dof4Linear` (stroke 80) | `Cammus3DofsOutput` | 1, 2, 3, 4 |
| DynamicX DX2 Ultra | `Geometry2Dof2Linear` | `DynamicXDX2UltraOutput` | 31, 32 |
| Thermaltake GM5 / Trak Racer 3DOF / RaceBear Motion 4X 100 & 150 | `Geometry3Dof4Linear` | the matching `GM53DofsOutput` subclass | 2, 4, 3, 1; `UseParkPosition` |
| eRacing-Lab RS MINI / RS MEGA 4U, Novus XMotion | `Geometry3Dof4Linear` | `Thanos4UControllerOutput` | 3, 1, 2, 4 |
| eRacing-Lab RS MEGA Plus | same | `ThanosAMCControllerOutput` | 3, 1, 2, 4 |
| VeroMotion Champion GT 100 mm / GTR 150 mm | `Geometry3Dof4Linear` (stroke 100 / 150) | `PTActuatorsCANOutput` | 3, 1, 2, 4 (CAN ids 1–4), `RefreshIntervalMs` 5 |
| VeroMotion Legend GT / GTR | `Geometry3Dof4Linear` + `SurgeAxis` + `TLFrontRearAxis` | `PTActuatorsCANOutput` | 3, 1, 2, 4, 8 RearTL (rev), 5 Surge, 7 FrontTL |
| VeroMotion BeltForce (`DebugOnly`) | `NullGeometry` + `PTABeltDualAxis` | `PTActuatorsBeltForceOutput` | 21, 22 |
| Yaw VR | `GeometryYawVRBypass` | `YawVROutput` | — (pose bypass) |
| Sample | — | — | `preset.json` only |

`ActuatorRole` (`Outputs.ActuatorOrdering`): 1–4 `LinearCornerFrontLeft/FrontRight/RearLeft/RearRight`;
5 `SurgeAxis`; 6/7/8 `SingleTractionLoss`/`FrontTractionLoss`/`RearTractionLoss`; 9 `SwayAxis`;
10 `SurgeAxisSecondary`; 11 `HeaveAxis`; 12 `PitchAxis`; 13/14 `LinearFrontCenter`/`LinearRearCenter`;
15 `RollAxis`; 21–23 `BeltLeft/Right/Center`; 31/32 `LinearSideLeft/Right`; 33–36
`FourDofsLinearCorner*`; 51–56 `SixDOFsHexapod` FrontRight, MiddleRight, RearRight, RearLeft,
MiddleLeft, FrontLeft; 61–63 `WindLeft/Center/Right`; 100+ / 500+ test axes.

## MahApps Metro

SimHub's UI is built on [MahApps.Metro](https://mahapps.com/). Plugin UIs can use MahApps controls (`MetroComboBox`, `ToggleSwitch`, etc.) for consistent styling. The assemblies are already loaded by SimHub at runtime.
