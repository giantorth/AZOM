using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using MozaPlugin.Diagnostics;
using SimHub.Plugins;

namespace MozaPlugin.Integration.Motion
{
    internal enum MotionSetupStatus
    {
        Unknown,
        MotionPluginDisabled,
        OutputNotAdded,
        OutputAdded,
    }

    /// <summary>
    /// The only entry point from plugin code into SimHub.Plugins.Motion types. That API
    /// is undocumented and can change in any SimHub release, so every call is guarded:
    /// the Motion-typed work lives in no-inline methods, which the JIT compiles only when
    /// called, inside a try. A load failure (TypeLoadException / MissingMethodException)
    /// is logged once and disables the integration; the rest of the plugin keeps working.
    /// </summary>
    internal static class MotionIntegration
    {
        private static volatile bool s_broken;

        /// <summary>Whether the active SimHub Motion setup contains an enabled HMA150 output.</summary>
        public static MotionSetupStatus GetSetupStatus(PluginManager? pluginManager)
        {
            if (s_broken || pluginManager == null) return MotionSetupStatus.Unknown;
            try { return GetSetupStatusCore(pluginManager); }
            catch (Exception ex) { Disable(ex); return MotionSetupStatus.Unknown; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static MotionSetupStatus GetSetupStatusCore(PluginManager pluginManager)
        {
            var motion = pluginManager.GetPlugin<SimHub.Plugins.Motion.MotionPlugin>();
            if (motion == null) return MotionSetupStatus.MotionPluginDisabled;
            var outputs = motion.MotionPluginSettings?.CurrentDevice?.Output?.AggregatedOutputs;
            if (outputs == null) return MotionSetupStatus.OutputNotAdded;
            return outputs.Any(o => o.IsEnabled && o.Output is MozaHma150Output)
                ? MotionSetupStatus.OutputAdded
                : MotionSetupStatus.OutputNotAdded;
        }

        /// <summary>
        /// SimHub's per-setup start mode defaults to "Last": a setup that was enabled when
        /// SimHub closed is enabled again at the next launch, and for the HMA150 that means the
        /// platform lifts to its ready position as soon as SimHub opens. Once per setup that
        /// has an enabled HMA150 output, switch "Last" to "Off" so motion only starts when the
        /// user presses Enable, like Motion Manager's Start. Setups already handled are kept
        /// in <paramref name="handledSetups"/>, so a user who sets it back keeps their choice.
        /// SimHub reads the start mode only at launch, so this takes effect from the next one.
        /// Returns true when <paramref name="handledSetups"/> changed (caller saves settings).
        /// </summary>
        public static bool DefaultStartModeToOff(PluginManager? pluginManager, ICollection<string>? handledSetups)
        {
            if (s_broken || pluginManager == null || handledSetups == null) return false;
            try { return DefaultStartModeToOffCore(pluginManager, handledSetups); }
            catch (Exception ex) { Disable(ex); return false; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool DefaultStartModeToOffCore(PluginManager pluginManager, ICollection<string> handledSetups)
        {
            var motion = pluginManager.GetPlugin<SimHub.Plugins.Motion.MotionPlugin>();
            var device = motion?.MotionPluginSettings?.CurrentDevice;
            var outputs = device?.Output?.AggregatedOutputs;
            if (device == null || outputs == null
                || !outputs.Any(o => o.IsEnabled && o.Output is MozaHma150Output))
                return false;

            string setupId = device.DeviceId.ToString();
            if (handledSetups.Contains(setupId)) return false;

            var power = device.PowerSettings;
            if (power == null) return false;
            if (power.StartMode == Simhub.Plugins.Motion.Models.OutputStartMode.Last)
            {
                power.StartMode = Simhub.Plugins.Motion.Models.OutputStartMode.Off;
                motion!.SaveSettings();
                MozaLog.Info("[AZOM/Motion] SimHub Motion start mode for the HMA150 setup changed from "
                    + "\"Last\" to \"Off\": motion no longer re-enables itself when SimHub starts");
            }
            handledSetups.Add(setupId);
            return true;
        }

        /// <summary>
        /// Whether SimHub's Manual control (or its automated test cycles) or the Hop-in position is the
        /// motion source of the active setup: <c>MotionDevice.SimulatedMotionStateAggregator.IsEnabled</c>
        /// or <c>MotionDevice.HopInActive</c>.
        /// </summary>
        public static bool IsManualControlActive(PluginManager? pluginManager)
        {
            if (s_broken || pluginManager == null) return false;
            try { return IsManualControlActiveCore(pluginManager); }
            catch (Exception ex) { Disable(ex); return false; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool IsManualControlActiveCore(PluginManager pluginManager)
        {
            var motion = pluginManager.GetPlugin<SimHub.Plugins.Motion.MotionPlugin>();
            var device = motion?.MotionPluginSettings?.CurrentDevice;
            if (device == null) return false;
            // The Hop-in position is a held pose like a Manual control setting; in game mode the
            // controller would ignore its heave (it only reacts to shock movement, not a steady offset).
            return device.SimulatedMotionStateAggregator?.IsEnabled == true || device.HopInActive;
        }

        private static void Disable(Exception ex)
        {
            if (s_broken) return;
            s_broken = true;
            MozaLog.Warn($"[AZOM/Motion] SimHub Motion integration disabled — this SimHub version's "
                + $"motion API does not match: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
