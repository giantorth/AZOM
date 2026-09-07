# SimHub Control Mapper Native Integration Research

Research date: 2026-05-20

Target SimHub API version inspected locally:

- `SimHub.Plugins.dll` from the local SimHub install
- Assembly version observed during reflection: `1.0.9631.22016`

## Summary

There is no public SimHub plugin API that lets this plugin register a first-class
Control Mapper source controller from arbitrary plugin-managed button state.

The public API supports:

- Registering regular SimHub plugin inputs through `PluginManager.AddInput`.
- Triggering those inputs through `PluginManager.TriggerInputPress` and
  `TriggerInputRelease`.
- Triggering Control Mapper roles through `ControlMapperInterface`.

Those paths are useful for SimHub actions and role triggering, but they do not
make a plugin-managed device appear in Control Mapper's "Add source controller"
flow.

## How Control Mapper Source Controllers Work

The inspected Control Mapper internals are centered around:

- `ControlMapperPlugin`
- `ControlMapperPluginSettings`
- `RemapperWorker`
- `ControllerDescription`
- `ControllerSourceMapping`
- `ControllerState`

`RemapperWorker` uses SharpDX DirectInput controller discovery and builds
`ControllerDescription` objects from physical or virtual game controllers. That
matches the visible SimHub behavior: source controllers are real DirectInput
devices, vJoy devices, or SimHub's own flashed bridge device.

## Fanatec / Simucube-Style Wheel Recognition

SimHub contains an internal variant mechanism:

- Public interface:
  `SimHub.Plugins.OutputPlugins.ControlRemapper.Variants.IVariantProvider`
- Internal helper:
  `VariantHelper`
- Built-in providers:
  `FanatecVariantProvider`
  `SimucubeVariantProvider`

The interface is small:

```csharp
string GetVariant(int vendorid, int productid);
```

This appears to be how SimHub distinguishes some wheels that are connected
through a base but still show as the same Windows controller. The variant is
applied to a `ControllerDescription`, rather than creating a brand-new source
controller from plugin state.

## Possible MOZA Native Direction

The closest native path would be a MOZA variant provider:

1. Detect the MOZA wheelbase DirectInput controller by vendor/product ID.
2. Return the current MOZA SDK wheel identity as the variant.
3. Let Control Mapper's existing "Recognize individual wheels" behavior split
   mappings per current wheel variant.

This would be much closer to how SimHub handles Fanatec and Simucube.

The registration surface for variant providers is not public.

`VariantHelper` owns a private `VariantProviders` list. There is no discovered
public method on `PluginManager`, `ControlMapperPlugin`, or `ControlMapperPluginSettings`
to register another provider.

An prototype implementation will reflect into the active
`ControlMapperPlugin`, find its private `remapperWorker`, find the private
`variantHelper`, and append a custom provider to `VariantProviders`. 
## Plugin Adapter (as built — `ControlMapper/`)

`Integration/MozaVariantProvider.cs` + `Integration/ControlMapperBridge.cs` implement the
variant-provider direction above so the Add Source Controller dropdown shows each MOZA wheel
under its friendly name (CS Pro / KS Pro / KS / FSR V2 / …) and `SharpHelper.AquireController`'s
per-mapping variant gate dispatches input only to the saved mapping whose stored Variant matches
the currently-attached wheel.

### MozaVariantProvider

- Implements `IVariantProvider.GetVariant(int vid, int pid)`: reads `MozaData.WheelModelName`,
  extracts the prefix via `WheelModelInfo.ExtractPrefix`, and returns
  `WheelModelInfo.GetFriendlyName(prefix)` when `vid == MozaProtocol.VendorId` and the PID
  resolves to `MozaUsbIds.IsWheelbasePid` or `IsHubPid`; null otherwise.
- **Old-protocol (ES/ESX) wheels** are base-proxied at dev `0x13` and report the wheelbase's
  identity, not their own — there is no serial way to read the wheel's model (see
  [`protocol/identity/known-wheel-models.md`](protocol/identity/known-wheel-models.md) § ES wheel
  identity caveat). The resolver short-circuits to the fixed variant `"ES"` whenever
  `MozaPlugin.IsOldWheelDetected` is set, instead of leaking the base name (e.g.
  `"R5 Black # MOT-1"`) as the variant.
- The variant computation lives in one canonical method, `MozaVariantProvider.ComputeCurrentVariant()`,
  shared by `GetVariant`, `Poll`, and `ControlMapperBridge`'s auto-create / detach paths.
- Exposes a `public event EventHandler VariantChanged` matching the Fanatec/Simucube convention;
  fired from `Poll()` when the resolved variant string transitions.

### ControlMapperBridge

- Walks the reflection chain (`ControlMapperPlugin → remapperWorker → variantHelper →
  VariantProviders`) defensively — every step that fails calls `LogGiveUp("…")` once and disables
  the bridge for the session rather than throwing.
- Lazy-materializes the provider list via `VariantHelper.Start()` when the user hasn't yet enabled
  Control Mapper's "Recognize Individual Wheels" toggle (without which `VariantHelper.GetVariant`
  returns null for every query, killing every mapping).
- After registering, calls `RemapperWorker.UpdateControllerList()` once so any wheel already
  plugged in at SimHub launch gets re-keyed with the MOZA variant on the first pass instead of
  waiting for the wheel-attach `VariantChanged` to fire it later.

### Workarounds (keyed off the `ControllerMappings.CollectionChanged` subscription)

1. **Detach shared Description on `Add`** — `set_ControllerDescription` stores by reference and
   `UpdateOrAdd` into `UnmappedControllers` uses a ControllerID-only predicate whose `CopyFrom`
   updater mutates the shared description on every `UpdateControllerList` tick, so saved mappings
   inherit Variant rewrites whenever the wheel changes. The fix clones the new mapping's
   `ControllerDescription` (`Activator.CreateInstance` + `CopyFrom`) and stamps `Variant` with the
   currently-detected wheel before SimHub's next tick sees it.
2. **Deduplicate double-adds** (`DeduplicateMozaMapping`) — the MOZA base can enumerate under two
   DirectInput interface paths (one with a USB serial, one synthesized — observed under Wine), so
   it appears twice in "Add Source Controller" and the user can map the same physical wheelbase for
   the same wheel more than once; both mappings then acquire the device and double-process its
   input. On `Add`, after the Variant is stamped, the bridge checks for an existing MOZA
   wheelbase/hub mapping with the same **VID+PID+Variant** and removes the just-added duplicate.
   Distinct variants on the same base (e.g. `CS Pro` + `KS`) are intentional per-wheel mappings and
   are kept. The removal is deferred via `Application.Current.Dispatcher.BeginInvoke` because
   `ObservableCollection` throws on re-entrant mutation inside its own `CollectionChanged`.

The bridge does **not** auto-create per-variant mappings. The user adds each MOZA source
controller through SimHub's own "Add Source Controller" flow (which runs `AddController` →
`ControllerMappings.Add` → `ControlMapperPluginSettings.UpdateControllerList()`, the canonical
async refresh that binds the device and marks it connected). The provider supplies the `Variant`
string and `AquireController`'s per-variant gate dispatches input to the matching mapping.

> **Removed (2026-06):** an earlier `AutoCreateVariantMappingIfNeeded` synthesized a per-variant
> mapping each tick when the attached wheel had no slot, to work around SimHub hiding a wheelbase
> from the dropdown once one mapping referenced its `ControllerID`. It was removed at the user's
> direction: on MOZA hardware that enumerates the base as **two** DirectInput devices, the
> wheelbase still appears in the dropdown, so manual add already works — and the synthesized
> mapping showed up as an unwanted extra entry that SimHub never marked `Available` (stuck
> "unplugged"). If a single-DirectInput-device base ever needs a programmatic second-wheel mapping
> again, replicate SimHub's add path exactly: `ControllerMappings.Add` then the **async**
> `ControlMapperPluginSettings.UpdateControllerList()` — never a synchronous
> `RemapperWorker.UpdateControllerList()` from inside the `Add`'s `CollectionChanged` (re-entrant,
> leaves the mapping un-acquired).

### Wiring

- `MozaPlugin` holds a `ControlMapper.ControlMapperBridge?` field, constructed in `Init` only when
  `MozaPluginSettings.EnableControlMapperVariants` is true (hidden setting, default true, flippable
  via JSON file edit if a future SimHub assembly change breaks the reflection chain).
- Registration tries immediately and retries up to ~50 ticks (~0.8 s at 60 Hz) in `DataUpdate`
  for slow `ControlMapperPlugin` load order; `Poll()` runs every `DataUpdate` tick; `Unregister()`
  removes the provider in `End` so plugin reload without SimHub restart doesn't leave a dead entry
  in `VariantHelper.VariantProviders`.
- Logging keeps Info-level reserved for significant lifecycle/action events; verbose per-tick
  diagnostics go to Debug.
