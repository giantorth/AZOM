using System;
using System.Collections.Generic;

namespace MozaPlugin.Settings
{
    /// <summary>Which kind of device page a profile store belongs to.</summary>
    public enum DeviceProfileKind
    {
        Wheel = 0,
        Base = 1,
        Dash = 2,
    }

    /// <summary>
    /// Every device page's own profile store, keyed by the page GUID (wheel / base:
    /// the model's registry GUID; dash: <c>MozaPlugin.Cm2PageGuid</c> /
    /// <c>MozaPlugin.Cm1PageGuid</c>). Serialized inside <see cref="MozaPluginSettings"/>
    /// so a migration and its sentinel land in one save.
    ///
    /// The dicts are copy-on-write: the save debounce serializes them off-thread, so
    /// a new store is added by swapping in a new dict (see <c>DeviceProfileCoordinator</c>),
    /// never by mutating in place.
    /// </summary>
    public sealed class DeviceProfileRegistry
    {
        public Dictionary<Guid, MozaWheelProfileStore> Wheels { get; set; }
            = new Dictionary<Guid, MozaWheelProfileStore>();

        public Dictionary<Guid, MozaBaseDeviceProfileStore> Bases { get; set; }
            = new Dictionary<Guid, MozaBaseDeviceProfileStore>();

        public Dictionary<Guid, MozaDashProfileStore> Dashes { get; set; }
            = new Dictionary<Guid, MozaDashProfileStore>();
    }

    /// <summary>Deep-copy helpers shared by the device profiles and the migration.</summary>
    internal static class DeviceProfileCopy
    {
        internal static int[]? Arr(int[]? a) => a != null ? (int[])a.Clone() : null;

        internal static Dictionary<string, Dictionary<string, string>> Mappings(
            Dictionary<string, Dictionary<string, string>>? src)
        {
            var dst = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            if (src == null) return dst;
            foreach (var kv in src)
                if (kv.Value != null)
                    dst[kv.Key] = new Dictionary<string, string>(kv.Value, StringComparer.OrdinalIgnoreCase);
            return dst;
        }

        internal static Dictionary<string, Fsr1FieldMapping> FieldMap(Dictionary<string, Fsr1FieldMapping>? src)
        {
            var dst = new Dictionary<string, Fsr1FieldMapping>(StringComparer.OrdinalIgnoreCase);
            if (src == null) return dst;
            foreach (var kv in src)
                if (kv.Value != null) dst[kv.Key] = kv.Value.Clone();
            return dst;
        }

        internal static Dictionary<string, Dictionary<string, Fsr1FieldMapping>> Fsr1(
            Dictionary<string, Dictionary<string, Fsr1FieldMapping>>? src)
        {
            var dst = new Dictionary<string, Dictionary<string, Fsr1FieldMapping>>(StringComparer.OrdinalIgnoreCase);
            if (src == null) return dst;
            foreach (var kv in src)
                if (kv.Value != null) dst[kv.Key] = FieldMap(kv.Value);
            return dst;
        }
    }
}
