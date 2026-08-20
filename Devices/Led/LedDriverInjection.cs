using System;
using System.Reflection;
using SimHub.Plugins.OutputPlugins.GraphicalDash.LedModules;

namespace MozaPlugin.Devices.Led
{
    /// <summary>
    /// Reflection swap of SimHub's <c>LedModuleSettings.DeviceDriver</c> (the
    /// setter is protected). Captures the driver that was installed so the
    /// extension's End can put it back — otherwise SimHub keeps referencing
    /// (and Display()-calling) the plugin's closed driver after the extension
    /// is gone.
    /// </summary>
    internal static class LedDriverInjection
    {
        private static readonly PropertyInfo? Prop =
            typeof(LedModuleSettings).GetProperty(
                "DeviceDriver", BindingFlags.Public | BindingFlags.Instance);
        private static readonly MethodInfo? Setter = Prop?.GetSetMethod(nonPublic: true);

        public static bool CanInject => Setter != null;

        /// <summary>Install <paramref name="driver"/>; returns the driver that
        /// was installed before (for restore at End).</summary>
        public static object? Swap(LedModuleSettings settings, object driver)
        {
            var previous = Prop!.GetValue(settings);
            Setter!.Invoke(settings, new[] { driver });
            return previous;
        }

        /// <summary>Restore <paramref name="original"/>, but only if
        /// <paramref name="ours"/> is still the installed driver (a re-attach
        /// may already have swapped in a fresh one).</summary>
        public static void Restore(LedModuleSettings? settings, object? ours, object? original)
        {
            if (settings == null || Setter == null || ours == null) return;
            try
            {
                if (ReferenceEquals(Prop!.GetValue(settings), ours))
                    Setter.Invoke(settings, new[] { original });
            }
            catch (Exception ex) { MozaLog.Debug($"[AZOM] LED driver restore: {ex.Message}"); }
        }
    }
}
