using System;
using System.Collections.Generic;
using System.Linq;
using MozaPlugin.Devices;
using MozaPlugin.Devices.MBooster;

namespace MozaPlugin.Hardware
{
    /// <summary>
    /// mBooster pedal calibration writes. Moved here from MozaPlugin to sit
    /// with the other Apply*ToHardware methods; this class owns every
    /// hardware-side write. Depends only on its arguments, so it needs
    /// nothing from the plugin instance.
    /// </summary>
    internal sealed partial class HardwareApplier
    {
        /// <summary>
        /// Push calibration values (direction / min / max / curve) for one
        /// mBooster to its device. Sentinel-guarded — values left at -1 (or
        /// null array) are skipped, so a fresh profile with no overrides
        /// produces zero hardware writes. Per protocol note § 6 these
        /// commands are "likely but unverified" on mBooster firmware.
        /// </summary>
        internal void ApplyMBoosterToHardware(MBoosterDeviceController controller, MBoosterDeviceSettings s)
        {
            if (controller == null || s == null || !controller.IsConnected) return;

            // Route Direction / Min / Max / output-curve to the command slot
            // matching the pedal's ROLE. This used to be hardcoded to the
            // "throttle" slot, which is wrong for a mBooster used as a brake or
            // clutch (and for a chain whose master pedal isn't the throttle) —
            // the calibration silently landed on the wrong pedal's command.
            // The role is the master pedal's (axis 0): ResolveAxisRole gives the
            // legacy Role for a single unit or the chain default/override
            // otherwise. Per-pedal calibration for the OTHER chained pedals is a
            // follow-up (needs a per-pedal settings UI); this fixes the routing
            // for the pedal the current single calibration set configures.
            int axisCount = controller.AxisCount > 0 ? controller.AxisCount : 1;
            // Apply EACH hosted pedal's calibration to its role-specific command.
            // Pedal 0 (master) keeps its calibration in the flat fields (the
            // existing UI); the additional chained pedals (axes 1+) store theirs
            // in s.Pedals[axis]. An unconfigured pedal (all -1 / null) writes
            // nothing. Once connectivity is known, phantom axes (no pedal
            // wired) are skipped, and a lane's sole connected pedal falls back
            // to the flat fields when it has no per-pedal entry — see
            // MBoosterDeviceController.SoleConnectedAxis.
            var connectedAxes = controller.ConnectedAxes;
            int soleAxis = controller.SoleConnectedAxis();
            for (int axis = 0; axis < axisCount && axis < global::MozaPlugin.Devices.MBooster.MBoosterDeviceController.MaxAxes; axis++)
            {
                if (connectedAxes != null && (axis >= connectedAxes.Length || !connectedAxes[axis]))
                    continue;
                var role = global::MozaPlugin.Devices.MBooster.MozaMBoosterRegistry.ResolveAxisRole(s, axis, axisCount);
                string? prefix =
                    role == global::MozaPlugin.Devices.MBooster.MBoosterRole.Throttle ? "throttle"
                    : role == global::MozaPlugin.Devices.MBooster.MBoosterRole.Brake ? "brake"
                    : role == global::MozaPlugin.Devices.MBooster.MBoosterRole.Clutch ? "clutch"
                    : null;
                if (prefix == null) continue;

                // This pedal's full config: master flat fields (axis 0) or its
                // per-pedal entry. An unconfigured chained pedal writes nothing.
                global::MozaPlugin.Devices.MBooster.IMBoosterPedalConfig cfg;
                if (axis == 0) cfg = s;
                else if (s.Pedals != null && s.Pedals.TryGetValue(axis, out var p) && p != null) cfg = p;
                else if (axis == soleAxis) cfg = s;
                else continue;

                // Every per-pedal calibration here is a PHYSICAL setting stored
                // on that pedal's own mBooster unit (confirmed on hardware: each
                // unit reports only its own pedal's calibration, under its own
                // role register). Address it by the pedal's ROLE through the
                // calibration-derived chain map (same as the effects — see
                // MBoosterEffectWorker.TargetDevice), NOT the raw HID axis: the
                // motor/config device id follows the chain plug position, which
                // doesn't match the HID axis order, so an axis-index device
                // sends these writes to the wrong physical pedal. Falls back to
                // the axis mapping (0x12 for a standalone) until the map resolves.
                int roleIdx = role == global::MozaPlugin.Devices.MBooster.MBoosterRole.Throttle ? 0
                            : role == global::MozaPlugin.Devices.MBooster.MBoosterRole.Brake ? 1
                            : role == global::MozaPlugin.Devices.MBooster.MBoosterRole.Clutch ? 2 : -1;
                byte dev = controller.MotorDeviceForRole(roleIdx, axis);

                if (cfg.Direction >= 0) controller.SendIntWrite($"mbooster-{prefix}-dir", cfg.Direction, dev);
                if (cfg.Min >= 0) controller.SendIntWrite($"mbooster-{prefix}-min", cfg.Min, dev);
                if (cfg.Max >= 0) controller.SendIntWrite($"mbooster-{prefix}-max", cfg.Max, dev);
                // CurveY/CurveX (Sim Input Mapping output curve) are NOT
                // pushed here — purely host-side now, no wire command at
                // all (see MozaMBoosterRegistry.EvaluateCurveArbitraryX and
                // docs/protocol/devices/mbooster.md "Sim Input Mapping").
                // Travel / End Stop / Natural Friction / Segmented Damping are
                // load-cell + motor Pedal Feel features living on brake-named
                // SINGLETON cmdIds (0x84/0x85, 0xB2, 0xAE, 0xB7) with no
                // per-pedal selector, so they can only ever configure the pedal
                // that owns that hardware. Pushing them from a PASSIVE pedal's
                // stored config doesn't configure that pedal — it overwrites the
                // active pedal's registers (bundle KY3HK4QP: the passive
                // throttle's 3.8/35.9mm is what the brake unit committed as
                // Params 48/49). The UI hides these controls for a passive pedal;
                // this stops values saved before that gate existed from still
                // being replayed on every connect.
                bool ownsPedalFeelHardware = controller.IsAxisMotorized(axis);
                if (ownsPedalFeelHardware && cfg.TravelStartMm >= 0)
                {
                    controller.SendIntWrite("mbooster-brake-travel-start",
                        global::MozaPlugin.Protocol.MozaMBoosterProtocol.EncodeTravelMm(cfg.TravelStartMm), dev);
                }
                if (ownsPedalFeelHardware && cfg.TravelEndMm >= 0)
                {
                    controller.SendIntWrite("mbooster-brake-travel-end",
                        global::MozaPlugin.Protocol.MozaMBoosterProtocol.EncodeTravelMm(cfg.TravelEndMm), dev);
                }
                if (ownsPedalFeelHardware && cfg.EndstopFrontStiffness >= 0)
                {
                    controller.SendIntWrite("mbooster-brake-endstop-front",
                        global::MozaPlugin.Protocol.MozaMBoosterProtocol.EncodeEndstopStiffness(cfg.EndstopFrontStiffness), dev);
                }
                if (ownsPedalFeelHardware && cfg.EndstopEndStiffness >= 0)
                {
                    controller.SendIntWrite("mbooster-brake-endstop-end",
                        global::MozaPlugin.Protocol.MozaMBoosterProtocol.EncodeEndstopStiffness(cfg.EndstopEndStiffness), dev);
                }
                // NaturalFrictionEnabled == false forces the pushed value to
                // 0% regardless of NaturalFrictionPct — same convention as
                // SegmentedDampingSettings.DampingEnabled below — so it also
                // has to fire on an otherwise-untouched profile once the
                // feature has been explicitly switched off.
                if (ownsPedalFeelHardware && (cfg.NaturalFrictionPct >= 0 || !cfg.NaturalFrictionEnabled))
                {
                    float frictionPct = cfg.NaturalFrictionEnabled ? cfg.NaturalFrictionPct : 0f;
                    int frictionRaw = global::MozaPlugin.Protocol.MozaMBoosterProtocol.EncodeFrictionPct(frictionPct);
                    controller.SendIntWrite("mbooster-brake-friction-0", frictionRaw, dev);
                    controller.SendIntWrite("mbooster-brake-friction-1", frictionRaw, dev);
                }
                // Segmented Damping (both "When Pressed" and "When
                // Released" — see cfg.SegmentedDamping). One wire command
                // carries the whole feature's state at once, so a fresh
                // profile with no override on EITHER side still sends
                // nothing here (guarded like every other calibration write
                // above); once ANY field on either side is set — or the
                // feature has been switched off via DampingEnabled — the
                // frame is filled out using factory defaults for whichever
                // side still has no override. DampingEnabled == false forces
                // every segment field to 0%, same as PushSegmentedDamping in
                // UI/SettingsControl.xaml.cs.
                var sd = ownsPedalFeelHardware ? cfg.SegmentedDamping : null;
                if (sd != null && (!sd.DampingEnabled || sd.Divider1Pressed >= 0 || sd.Divider2Pressed >= 0
                    || sd.Seg1Pressed >= 0 || sd.Seg2Pressed >= 0 || sd.Seg3Pressed >= 0
                    || sd.Divider1Released >= 0 || sd.Divider2Released >= 0
                    || sd.Seg1Released >= 0 || sd.Seg2Released >= 0 || sd.Seg3Released >= 0))
                {
                    bool sdEnabled = sd.DampingEnabled;
                    var c = global::MozaPlugin.Devices.MBooster.MBoosterUiConstants.SegDampSegDefaultPct;
                    var frame = global::MozaPlugin.Protocol.MozaMBoosterProtocol.BuildSegmentedDampingFrame(
                        sd.Divider1Pressed >= 0 ? sd.Divider1Pressed : global::MozaPlugin.Devices.MBooster.MBoosterUiConstants.SegDampDivider1PressedDefaultPct,
                        sd.Divider2Pressed >= 0 ? sd.Divider2Pressed : global::MozaPlugin.Devices.MBooster.MBoosterUiConstants.SegDampDivider2PressedDefaultPct,
                        sd.Divider1Released >= 0 ? sd.Divider1Released : global::MozaPlugin.Devices.MBooster.MBoosterUiConstants.SegDampDivider1ReleasedDefaultPct,
                        sd.Divider2Released >= 0 ? sd.Divider2Released : global::MozaPlugin.Devices.MBooster.MBoosterUiConstants.SegDampDivider2ReleasedDefaultPct,
                        !sdEnabled ? 0 : sd.Seg1Pressed >= 0 ? sd.Seg1Pressed : c,
                        !sdEnabled ? 0 : sd.Seg1Released >= 0 ? sd.Seg1Released : c,
                        !sdEnabled ? 0 : sd.Seg2Pressed >= 0 ? sd.Seg2Pressed : c,
                        !sdEnabled ? 0 : sd.Seg2Released >= 0 ? sd.Seg2Released : c,
                        !sdEnabled ? 0 : sd.Seg3Pressed >= 0 ? sd.Seg3Pressed : c,
                        !sdEnabled ? 0 : sd.Seg3Released >= 0 ? sd.Seg3Released : c,
                        dev);
                    controller.SendOneShot(frame);
                }
                if (role == global::MozaPlugin.Devices.MBooster.MBoosterRole.Brake)
                {
                    if (cfg.SensorOutputRatioPct >= 0)
                    {
                        controller.SendFloatWrite("mbooster-brake-angle-ratio", cfg.SensorOutputRatioPct, dev);
                    }
                    if (cfg.MaxThresholdKg >= 0)
                    {
                        controller.SendIntWrite("mbooster-brake-threshold",
                            global::MozaPlugin.Protocol.MozaMBoosterProtocol.EncodeThresholdKg(cfg.MaxThresholdKg), dev);
                    }
                }

                // Deadzone / Max Force / Pedal Feel curve — CONFIRMED real
                // hardware calibration (see
                // MBoosterDeviceController.PushFeelCurveResync). Fresh
                // profile with none set sends nothing, same guarantee as
                // every other calibration write here. Once ANY of the three
                // is set, the whole 8-value family is pushed together (the
                // device has no partial-update form for it), using the
                // pedal's own sane "off" default for whichever side has no
                // override — 0kg deadzone, 200kg max force, and the curve's
                // own default Linear shape (MozaMBoosterRegistry
                // .FeelCurveFractions) for an uncustomized curve.
                bool curveCustomized = (cfg.InputCurveY != null
                    && cfg.InputCurveY.Length == global::MozaPlugin.Devices.MBooster.MBoosterUiConstants.PedalFeelNodeCount)
                    || (cfg.InputCurveX != null
                    && cfg.InputCurveX.Length == global::MozaPlugin.Devices.MBooster.MBoosterUiConstants.PedalFeelNodeCount);
                if (ownsPedalFeelHardware && (cfg.DeadzoneKg >= 0 || cfg.MaxForceKg >= 0 || curveCustomized))
                {
                    double dz = cfg.DeadzoneKg >= 0 ? cfg.DeadzoneKg : 0;
                    double mf = cfg.MaxForceKg >= 0 ? cfg.MaxForceKg : 200;
                    controller.PushFeelCurveResync(dz, mf, cfg.InputCurveY, cfg.InputCurveX, dev);
                }
            }
        }
    }
}
