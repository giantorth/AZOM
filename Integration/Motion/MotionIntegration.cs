using System;
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

        public static bool IsAvailable => !s_broken;

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

        /// <summary>True while SimHub Motion has an HMA150 output started.</summary>
        public static bool AnyOutputStarted()
        {
            if (s_broken) return false;
            try { return AnyOutputStartedCore(); }
            catch (Exception ex) { Disable(ex); return false; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool AnyOutputStartedCore() => MozaHma150Output.AnyStarted;

        private static void Disable(Exception ex)
        {
            if (s_broken) return;
            s_broken = true;
            MozaLog.Warn($"[AZOM/Motion] SimHub Motion integration disabled — this SimHub version's "
                + $"motion API does not match: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
