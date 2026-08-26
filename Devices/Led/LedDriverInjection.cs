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

        // StandardProtocolConnectionDevice.Manager is a getter-only auto-property,
        // so the swap goes through its compiler-generated backing field. Resolved
        // by name against the live instance's type: the declaring type only exists
        // on SimHub 9.12+, and this file must stay loadable on older builds.
        private const string ManagerBackingField = "<Manager>k__BackingField";

        /// <summary>
        /// Replace a StandardProtocolConnectionDevice's manager. Returns the manager
        /// that was installed (for restore at End), or null when the field could not
        /// be resolved.
        /// </summary>
        public static object? SwapConnectionManager(object connectionDevice, object manager)
        {
            var field = connectionDevice.GetType().GetField(
                ManagerBackingField, BindingFlags.NonPublic | BindingFlags.Instance);
            if (field == null)
            {
                MozaLog.Warn("[AZOM] Could not find the connection device's Manager backing field");
                return null;
            }

            var previous = field.GetValue(connectionDevice);
            field.SetValue(connectionDevice, manager);
            return previous;
        }

        /// <summary>Restore a connection device's original manager, but only if ours is still installed.</summary>
        public static void RestoreConnectionManager(object? connectionDevice, object? ours, object? original)
        {
            if (connectionDevice == null || ours == null || original == null) return;
            try
            {
                var field = connectionDevice.GetType().GetField(
                    ManagerBackingField, BindingFlags.NonPublic | BindingFlags.Instance);
                if (field == null) return;
                if (ReferenceEquals(field.GetValue(connectionDevice), ours))
                    field.SetValue(connectionDevice, original);
            }
            catch (Exception ex) { MozaLog.Debug($"[AZOM] Connection manager restore: {ex.Message}"); }
        }
    }
}
