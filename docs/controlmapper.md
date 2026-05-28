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