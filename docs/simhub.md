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
- **NOT** persuade the Add Source Controller UI to offer the wheelbase a second time. The Add dropdown is sourced from `UnmappedControllers` filtered by ControllerID — once any saved mapping references that GUID, the device is hidden, regardless of variant. ✗

Workaround: programmatically build a new `ControllerSourceMapping` (clone an existing MOZA mapping's description, stamp `Variant` to the current wheel, reassign) and add it to `ControlMapperPluginSettings.ControllerMappings` from inside the plugin's data loop — bypass the UI add entirely.

The shipping plugin **already does this** in `ControlMapperBridge`: `AutoCreateVariantMappingIfNeeded()` auto-creates a per-variant mapping each tick when the attached wheel has no slot (guarded by a once-per-session `_autoCreatedVariants` set so a user-deleted mapping does not reappear), and `DetachMozaDescription()` deep-clones shared `Description` references on `Add` so SimHub's by-reference `CopyFrom` updater can't rewrite a saved mapping's Variant on a later wheel change.

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
