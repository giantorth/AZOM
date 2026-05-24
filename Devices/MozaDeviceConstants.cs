using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace MozaPlugin.Devices
{
    internal static class MozaDeviceConstants
    {
        /// <summary>
        /// DescriptorUniqueId GUIDs from the .shdp device definitions.
        /// These are permanent — changing them orphans existing user device instances.
        /// </summary>
        public const string DashGuid          = "c97a4d00-a66d-4e2f-a9b4-e7fc348dcc33";
        public const string DashCm2Guid       = "6a2d9b0f-8b1e-4d32-9c18-1f5ec8a81025";
        public const string WheelGenericGuid  = "ed153fcb-774d-4cea-97db-5f7096cd1099";
        public const string WheelOldProtoGuid = "5e70f006-ba71-4987-9e88-840d650b12ef";
        // Wheelbase ambient LED strip (18 LEDs across two physical 9-LED strips).
        // Deployed only for bases that respond to a base-ambient-brightness probe
        // (R21 / R25 / R27 family). R9 / R12 don't have this strip — no device.
        public const string BaseAmbientGuid   = "b8361c60-1bbd-4497-8cb4-af5df7db7251";

        /// <summary>Marker prefix returned by GetWheelModelPrefix for old-protocol devices.</summary>
        public const string OldProtocolMarker = "__old__";

        /// <summary>
        /// Fallback RPM LED count used pre-detection (before the wheel model name
        /// is known). Known-model code paths should prefer <see cref="WheelModelInfo.RpmLedCount"/>.
        /// </summary>
        public const int RpmLedCount = 10;
        public const int ButtonLedCount = 14;
        public const int FlagLedCount = 6;

        /// <summary>
        /// Backward-compatible GUIDs for wheel models that had static device templates.
        /// These must never change — existing users have device instances keyed by these GUIDs.
        /// </summary>
        private static readonly Dictionary<string, string> BackwardCompatGuids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "GS V2P",  "68b2eb89-043e-4e29-be9c-4045c9636124" },
            { "CS V2.1", "cd485bdb-934d-4d06-8224-d24fb1f82bd7" },
            { "W17",     "503269ba-fc50-44d4-9844-8800da5f9f10" },  // CS Pro (firmware reports "W17", was "CSP")
            { "W18",     "14c84064-a968-43b9-ab92-a02f512632ce" },  // KS Pro (firmware reports "W18")
            { "W13",     "c4f0cf35-e68c-4756-a04a-b2f8b5d6dbf3" },  // FSR V2 (firmware reports "W13", was "FSR2")
        };

        /// <summary>
        /// Fixed namespace UUID for generating deterministic wheel device GUIDs via UUID v5.
        /// Arbitrary but permanent — changing it would orphan any devices generated with it.
        /// </summary>
        private static readonly Guid WheelGuidNamespace =
            new Guid("a1b2c3d4-e5f6-4789-abcd-ef0123456789");

        /// <summary>
        /// Runtime registry mapping GUID → model prefix. Populated at startup from
        /// backward-compat map + UUID v5 generation for all known models + persistent file.
        /// </summary>
        private static readonly Dictionary<string, string> GuidToPrefix =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Reverse map: model prefix → GUID. Populated alongside GuidToPrefix.
        /// </summary>
        private static readonly Dictionary<string, string> PrefixToGuid =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static bool _registryInitialized;
        private static readonly object _registryLock = new object();
        private static string? _registryPath;

        // Save serialization. The registry lock guards only the in-memory dicts;
        // file I/O is pushed outside that lock so LED writes and hot-swap detection
        // (which also take _registryLock indirectly via Resolve/Get calls) aren't
        // blocked behind a synchronous disk write. _pendingSaveSnapshot holds the
        // latest snapshot any caller wants persisted; _saveLock serializes the
        // actual writer so two threads never interleave temp-file+rename steps.
        private static readonly object _saveLock = new object();
        private static Dictionary<string, string>? _pendingSaveSnapshot;

        /// <summary>
        /// Initialize the GUID registry. Call once from plugin Init.
        /// Populates the lookup tables from backward-compat GUIDs,
        /// UUID v5 generation for all WheelModelInfo.KnownModels,
        /// and any previously saved dynamic mappings from disk.
        /// </summary>
        public static void InitializeRegistry()
        {
            lock (_registryLock)
            {
                if (_registryInitialized) return;
                _registryInitialized = true;

                _registryPath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "DevicesDefinitions", "User", "moza-wheel-guids.json");

                // 1. Load persistent registry (may contain unknown-model GUIDs from prior runs)
                LoadRegistryFile();

                // 2. Register backward-compat GUIDs (overwrite any stale file entries)
                foreach (var kvp in BackwardCompatGuids)
                    Register(kvp.Key, kvp.Value);

                // 3. Register UUID v5 GUIDs for known models that don't have backward-compat GUIDs
                foreach (var (prefix, _, _) in WheelModelInfo.KnownModels)
                {
                    if (!PrefixToGuid.ContainsKey(prefix))
                        Register(prefix, GenerateUuidV5(prefix));
                }
            }
        }

        /// <summary>
        /// Get or create the device GUID for a wheel model prefix.
        /// For backward-compat models, returns the existing GUID.
        /// For new models, generates a deterministic UUID v5 and registers it.
        /// </summary>
        public static string ResolveWheelGuid(string modelPrefix)
        {
            string guid;
            Dictionary<string, string> snapshot;
            lock (_registryLock)
            {
                if (PrefixToGuid.TryGetValue(modelPrefix, out var existing))
                    return existing;

                // New model — generate, register, and snapshot the current map
                // while still holding the lock so the snapshot is internally consistent.
                guid = GenerateUuidV5(modelPrefix);
                Register(modelPrefix, guid);
                snapshot = new Dictionary<string, string>(PrefixToGuid, StringComparer.OrdinalIgnoreCase);
            }

            // Disk write happens outside the registry lock to avoid blocking LED
            // writes / hot-swap detection behind a synchronous WriteAllText+Move.
            EnqueueRegistrySave(snapshot);
            return guid;
        }

        /// <summary>
        /// Publish a snapshot to be persisted, then either run the writer (if no
        /// other thread is currently writing) or hand off to whoever holds the
        /// writer slot. The in-flight writer re-checks <see cref="_pendingSaveSnapshot"/>
        /// after each write, so a later, fresher snapshot always wins — and we
        /// never queue more than one follow-up write regardless of caller volume.
        /// </summary>
        private static void EnqueueRegistrySave(Dictionary<string, string> snapshot)
        {
            // Publish the latest snapshot first. Any in-flight writer will pick
            // this up on its post-write check; that's why we don't need to queue.
            Volatile.Write(ref _pendingSaveSnapshot, snapshot);

            // Try to become the writer. If another thread already holds the slot,
            // it will observe our published snapshot and write it for us.
            if (!Monitor.TryEnter(_saveLock))
                return;

            try
            {
                while (true)
                {
                    var pending = Interlocked.Exchange(ref _pendingSaveSnapshot, null);
                    if (pending == null) return;
                    WriteRegistrySnapshot(pending);
                }
            }
            finally
            {
                Monitor.Exit(_saveLock);
            }
        }

        /// <summary>
        /// Resolve the expected wheel model prefix from a SimHub DeviceTypeID.
        /// Returns null if the DeviceTypeID is not a known wheel device.
        /// Returns empty string for the generic new-protocol fallback.
        /// Returns a model prefix (e.g. "CSP") for model-specific devices.
        /// Returns <see cref="OldProtocolMarker"/> for old-protocol devices.
        /// </summary>
        public static string? GetWheelModelPrefix(string deviceTypeId)
        {
            if (string.IsNullOrEmpty(deviceTypeId))
                return null;

            // Generic new-protocol fallback
            if (Matches(deviceTypeId, WheelGenericGuid))
                return "";

            // Old-protocol device
            if (Matches(deviceTypeId, WheelOldProtoGuid))
                return OldProtocolMarker;

            // Dynamic registry lookup (covers backward-compat + UUID v5 + unknown models)
            foreach (var kvp in GuidToPrefix)
            {
                if (Matches(deviceTypeId, kvp.Key))
                    return kvp.Value;
            }

            return null;
        }

        /// <summary>Returns true if the DeviceTypeID is a known dashboard device (legacy SHDP or standalone CM2).</summary>
        public static bool IsDashDevice(string deviceTypeId) =>
            !string.IsNullOrEmpty(deviceTypeId)
            && (Matches(deviceTypeId, DashGuid) || Matches(deviceTypeId, DashCm2Guid));

        /// <summary>Returns true if the DeviceTypeID is the wheel base ambient LED device.</summary>
        public static bool IsBaseAmbientDevice(string deviceTypeId) =>
            !string.IsNullOrEmpty(deviceTypeId) && Matches(deviceTypeId, BaseAmbientGuid);

        /// <summary>Check if deviceTypeId matches an id exactly or as a prefix (for _UserProject/_Embedded suffixes).</summary>
        private static bool Matches(string deviceTypeId, string id) =>
            deviceTypeId.Equals(id, StringComparison.OrdinalIgnoreCase)
            || deviceTypeId.StartsWith(id + "_", StringComparison.OrdinalIgnoreCase);

        private static void Register(string prefix, string guid)
        {
            GuidToPrefix[guid] = prefix;
            PrefixToGuid[prefix] = guid;
        }

        /// <summary>
        /// Generate a deterministic UUID v5 from a model prefix string.
        /// </summary>
        private static string GenerateUuidV5(string modelPrefix)
        {
            byte[] namespaceBytes = WheelGuidNamespace.ToByteArray();
            // Convert .NET GUID (mixed-endian) to big-endian for RFC 4122 hashing
            SwapGuidEndian(namespaceBytes);

            byte[] nameBytes = Encoding.UTF8.GetBytes(modelPrefix);

            byte[] hash;
            using (var sha1 = SHA1.Create())
            {
                sha1.TransformBlock(namespaceBytes, 0, namespaceBytes.Length, null, 0);
                sha1.TransformFinalBlock(nameBytes, 0, nameBytes.Length);
                hash = sha1.Hash;
            }

            // Take first 16 bytes, set version (5) and variant (RFC 4122)
            byte[] guidBytes = new byte[16];
            Array.Copy(hash, guidBytes, 16);
            guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x50); // Version 5
            guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80); // Variant 10xx

            // Convert back to .NET mixed-endian
            SwapGuidEndian(guidBytes);

            return new Guid(guidBytes).ToString();
        }

        /// <summary>
        /// Swap between .NET's mixed-endian GUID layout and big-endian RFC 4122 layout.
        /// .NET stores Data1 (4 bytes), Data2 (2 bytes), Data3 (2 bytes) as little-endian;
        /// RFC 4122 uses network byte order (big-endian) throughout.
        /// </summary>
        private static void SwapGuidEndian(byte[] bytes)
        {
            byte tmp;
            tmp = bytes[0]; bytes[0] = bytes[3]; bytes[3] = tmp;
            tmp = bytes[1]; bytes[1] = bytes[2]; bytes[2] = tmp;
            tmp = bytes[4]; bytes[4] = bytes[5]; bytes[5] = tmp;
            tmp = bytes[6]; bytes[6] = bytes[7]; bytes[7] = tmp;
        }

        private static void LoadRegistryFile()
        {
            try
            {
                if (_registryPath != null && File.Exists(_registryPath))
                {
                    var json = File.ReadAllText(_registryPath);
                    var map = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
                    if (map != null)
                    {
                        foreach (var kvp in map)
                            Register(kvp.Key, kvp.Value);
                    }
                }
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[Moza] Could not load wheel GUID registry: {ex.Message}");
            }
        }

        /// <summary>
        /// Persist the given snapshot to disk. Caller must hold <see cref="_saveLock"/>
        /// (via <see cref="EnqueueRegistrySave"/>) to guarantee no other thread is
        /// running the temp-file+rename sequence concurrently.
        /// </summary>
        private static void WriteRegistrySnapshot(Dictionary<string, string> snapshot)
        {
            try
            {
                if (_registryPath == null) return;

                var dir = Path.GetDirectoryName(_registryPath);
                if (dir != null)
                    Directory.CreateDirectory(dir);

                var json = JsonConvert.SerializeObject(snapshot, Formatting.Indented);

                // Atomic write: stage to a sibling .tmp first, then move into place.
                // A crash mid-WriteAllText would otherwise truncate the existing
                // registry to garbage and lose the GUID-to-prefix map on next load.
                var tmpPath = _registryPath + ".tmp";
                File.WriteAllText(tmpPath, json);
                if (File.Exists(_registryPath))
                    File.Delete(_registryPath);
                File.Move(tmpPath, _registryPath);
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[Moza] Could not save wheel GUID registry: {ex.Message}");
            }
        }
    }
}
