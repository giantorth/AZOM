using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MozaPlugin.Devices;

namespace MozaPlugin.Sdk
{
    /// <summary>
    /// Synthesises the MOZA-SDK device catalogue served at
    /// <c>/MOZARacing/ProductDevice</c> and
    /// <c>/MOZARacing/ProductDevice/&lt;id&gt;</c>. Source data is the
    /// existing identity fields the plugin collects from PitHouse-parity
    /// probes (group 0x06/0x07/0x08/0x09/0x0F/0x11) targeted at the wheel
    /// (dev 0x17) and base (dev 0x13).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Wire format from the 2026-05-23 PitHouse capture
    /// (<c>iracing-pithouse-udp.pcapng</c>): the device-list is a CBOR
    /// array of 16-char lowercase-hex IDs, one per device. Each
    /// per-device manifest is a CBOR map with seven fields in fixed
    /// order: <c>appVersion</c>, <c>hardwareVersion</c>, <c>id</c>,
    /// <c>mcuUid</c>, <c>parentId</c>, <c>productName</c>,
    /// <c>productType</c>. <c>mcuUid</c> is 12 chars (first 6 bytes of
    /// the 12-byte STM32 UID, lower-case hex). <c>parentId</c> is also
    /// 12 chars — the first 12 chars of the parent device's
    /// <c>mcuUid</c>, NOT the parent's 16-char <c>id</c>. The Motor
    /// device sits at the topology root and every other device's
    /// <c>parentId</c> points at its <c>mcuUid</c>.
    /// </para>
    /// <para>
    /// Product types match the wire vocabulary verbatim:
    /// <c>Motor</c>, <c>Wheel Base</c>, <c>Steering Wheel</c>,
    /// <c>Pedals</c>, <c>Handbrake</c>, <c>Display</c>. The capture
    /// promotes Motor as a distinct device from Wheel Base even though
    /// both carry the same <c>productName</c> on R5/R9/R12/R21/R25
    /// boards (they likely share an MCU and the firmware exposes the
    /// hub aspect as a sibling alias). We synthesise both entries from
    /// the same <c>BaseMcuUid</c>; the IDs differ because the SHA-1
    /// truncation is over <c>mcuUid + suffix</c> rather than the bare
    /// UID — see <see cref="DeriveDeviceIdWithSuffix"/>.
    /// </para>
    /// <para>
    /// ES wheels (where the wheel and base are one physical device on
    /// dev 0x13) are handled by <see cref="IsEsWheelTopology"/>: when
    /// <c>BaseMcuUid</c> and <c>WheelMcuUid</c> are byte-equal, we
    /// emit only the wheel manifest and skip the Motor / Wheel Base
    /// synthesis (no separate base device exists).
    /// </para>
    /// </remarks>
    public sealed class DeviceCatalog
    {
        /// <summary>Placeholder parentId for top-of-tree devices (Motor).</summary>
        public const string RootParentId = "000000000000";

        /// <summary>Width of a manifest device ID in hex characters.</summary>
        private const int IdHexLength = 16;

        /// <summary>Width of the manifest <c>mcuUid</c> and <c>parentId</c> fields in hex characters (6 bytes of the STM32 UID).</summary>
        public const int McuUidHexLength = 12;

        private readonly MozaData _data;

        public DeviceCatalog(MozaData data)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
        }

        /// <summary>
        /// Enumerates hex IDs for every currently-identifiable MOZA device.
        /// Order matches the cold-start capture: Motor, Wheel Base, Steering
        /// Wheel, Pedals, Handbrake, Display. Returns an empty list when no
        /// device has yet supplied an MCU UID — the SDK then sees an empty
        /// device list, the same shape PitHouse returns between hardware
        /// disconnects.
        /// </summary>
        public IReadOnlyList<string> EnumerateDeviceIds()
        {
            var list = new List<string>();
            foreach (var manifest in BuildManifests())
                list.Add(manifest.Id);
            return list;
        }

        /// <summary>
        /// Returns the manifest for a single device ID, or <c>null</c> when
        /// the ID is not currently in the catalogue. ID lookup is
        /// case-insensitive but the canonical form is lowercase hex.
        /// </summary>
        public DeviceManifest? GetManifest(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var manifest in BuildManifests())
            {
                if (string.Equals(manifest.Id, id, StringComparison.OrdinalIgnoreCase))
                    return manifest;
            }
            return null;
        }

        /// <summary>
        /// Returns the manifest entries in the EXACT order PitHouse emits
        /// them on the wire: <c>appVersion</c>, <c>hardwareVersion</c>,
        /// <c>id</c>, <c>mcuUid</c>, <c>parentId</c>, <c>productName</c>,
        /// <c>productType</c>. Feed straight into
        /// <c>MozaPlugin.Sdk.Cbor.CborWriter.WriteMap(...)</c> to produce a
        /// byte-identical replay of the capture's manifest frames.
        /// </summary>
        public IReadOnlyList<KeyValuePair<string, object>> ToCborEntries(DeviceManifest m)
        {
            if (m == null) throw new ArgumentNullException(nameof(m));
            return new[]
            {
                new KeyValuePair<string, object>("appVersion",      m.AppVersion ?? string.Empty),
                new KeyValuePair<string, object>("hardwareVersion", m.HardwareVersion ?? string.Empty),
                new KeyValuePair<string, object>("id",              m.Id ?? string.Empty),
                new KeyValuePair<string, object>("mcuUid",          m.McuUid ?? string.Empty),
                new KeyValuePair<string, object>("parentId",        m.ParentId ?? string.Empty),
                new KeyValuePair<string, object>("productName",     m.ProductName ?? string.Empty),
                new KeyValuePair<string, object>("productType",     m.ProductType ?? string.Empty),
            };
        }

        // --- Manifest synthesis --------------------------------------------

        /// <summary>
        /// Build every currently-derivable manifest in a deterministic
        /// topological order: Motor (root) → Wheel Base → Steering Wheel →
        /// Pedals → Handbrake → Display. All non-Motor devices point their
        /// <c>parentId</c> at the Motor's 12-char <c>mcuUid</c> prefix.
        /// </summary>
        private List<DeviceManifest> BuildManifests()
        {
            var list = new List<DeviceManifest>();

            byte[] baseUid = _data.BaseMcuUid ?? Array.Empty<byte>();
            byte[] wheelUid = _data.WheelMcuUid ?? Array.Empty<byte>();
            byte[] displayUid = _data.DisplayMcuUid ?? Array.Empty<byte>();

            bool haveBase = baseUid.Length > 0;
            bool haveWheel = wheelUid.Length > 0;
            bool haveDisplay = displayUid.Length > 0;

            // ES-wheel topology: wheel and base are the same physical MCU on
            // dev 0x13 (probes echoed the wheel's identity into Base*).
            // Suppress the Motor / Wheel Base entries to avoid advertising
            // ghost devices to iRacing.
            bool esTopology = IsEsWheelTopology(baseUid, wheelUid);

            // Motor parent prefix: the 12-char mcuUid every child device
            // points at. Falls back to RootParentId when the base UID isn't
            // available yet (the wheel-only fallback for capture parity with
            // the previous behaviour — empty manifest list until probes
            // resolve is fine, but iRacing has been observed to bail when
            // parentId is a 16-char zero string, so prefer the 12-char form).
            string motorParentPrefix = haveBase && !esTopology
                ? FormatMcuUid(baseUid)
                : RootParentId;

            // ---- Motor (root) -------------------------------------------------
            if (haveBase && !esTopology)
            {
                string motorId = DeriveDeviceIdWithSuffix(baseUid, "motor");
                string motorHardware = !string.IsNullOrEmpty(_data.BaseHwSubVersion)
                    ? _data.BaseHwSubVersion
                    : (_data.BaseHwVersion ?? string.Empty);
                string motorApp = _data.BaseSwVersion ?? string.Empty;
                string motorName = !string.IsNullOrEmpty(_data.BaseModelName)
                    ? _data.BaseModelName
                    : "Motor";

                list.Add(new DeviceManifest(
                    appVersion:      motorApp,
                    hardwareVersion: motorHardware,
                    id:              motorId,
                    mcuUid:          FormatMcuUid(baseUid),
                    parentId:        motorParentPrefix,   // self-referential at the root
                    productName:     motorName,
                    productType:     "Motor"));

                // ---- Wheel Base (sibling alias) -------------------------------
                // Same physical MCU, different SDK manifest entry. ID
                // derivation differs from Motor's via the suffix so the two
                // entries get distinct 16-char IDs without needing a second
                // MCU UID. Same productName per capture.
                list.Add(new DeviceManifest(
                    appVersion:      motorApp,
                    hardwareVersion: motorHardware,
                    id:              DeriveDeviceIdWithSuffix(baseUid, "wheelbase"),
                    mcuUid:          FormatMcuUid(baseUid),
                    parentId:        motorParentPrefix,
                    productName:     motorName,
                    productType:     "Wheel Base"));
            }

            // ---- Steering Wheel ---------------------------------------------
            string? wheelId = null;
            if (haveWheel)
            {
                wheelId = DeriveDeviceId(wheelUid);
                string modelPrefix = WheelModelInfo.ExtractPrefix(_data.WheelModelName ?? string.Empty);
                string productName = ResolveWheelProductName(modelPrefix);
                string hardwareVersion = !string.IsNullOrEmpty(_data.WheelHwSubVersion)
                    ? _data.WheelHwSubVersion
                    : (_data.WheelHwVersion ?? string.Empty);
                string appVersion = _data.WheelSwVersion ?? string.Empty;

                list.Add(new DeviceManifest(
                    appVersion:      appVersion,
                    hardwareVersion: hardwareVersion,
                    id:              wheelId,
                    mcuUid:          FormatMcuUid(wheelUid),
                    parentId:        motorParentPrefix,
                    productName:     productName,
                    productType:     "Steering Wheel"));
            }

            // ---- Display ----------------------------------------------------
            // Capture taxonomy uses "Display" — NOT "Display Screen" as the
            // pre-2026-05-23 SDK assumed. The wheel-attached display is a
            // sibling of the Steering Wheel under the Motor root, not a
            // child of the wheel. ParentId points at the Motor mcuUid
            // prefix, matching the PitHouse capture.
            if (haveDisplay)
            {
                string displayId = DeriveDeviceId(displayUid);
                string displayHw = _data.DisplayHwVersion ?? string.Empty;
                string displayApp = _data.DisplaySwVersion ?? string.Empty;
                string displayName = string.IsNullOrEmpty(_data.DisplayModelName)
                    ? string.Empty
                    : _data.DisplayModelName;

                list.Add(new DeviceManifest(
                    appVersion:      displayApp,
                    hardwareVersion: displayHw,
                    id:              displayId,
                    mcuUid:          FormatMcuUid(displayUid),
                    parentId:        motorParentPrefix,
                    productName:     displayName,
                    productType:     "Display"));
            }

            // Pedals and Handbrake — deferred. MozaData has no
            // PedalsMcuUid / HandbrakeMcuUid fields yet and the plugin
            // doesn't issue identity probes for them. When those fields
            // land, add manifests here in this same shape: parentId =
            // motorParentPrefix, productType = "Pedals" / "Handbrake".

            return list;
        }

        // --- ID derivation -------------------------------------------------

        /// <summary>
        /// Derive a 16-char lowercase-hex device ID from a raw MCU UID.
        /// SHA-1 truncated to 16 hex chars: stable for a given physical
        /// device and trivially regenerable from the same UID input.
        /// </summary>
        public static string DeriveDeviceId(byte[] mcuUid)
        {
            if (mcuUid == null || mcuUid.Length == 0)
                throw new ArgumentException("mcuUid must be non-empty.", nameof(mcuUid));
            using (var sha1 = SHA1.Create())
            {
                byte[] hash = sha1.ComputeHash(mcuUid);
                return ToHexLower(hash, 0, 8);
            }
        }

        /// <summary>
        /// Derive a 16-char ID from <paramref name="mcuUid"/> qualified by
        /// <paramref name="suffix"/>. Lets us synthesise the Motor and Wheel
        /// Base manifest entries (which share an MCU UID) with distinct IDs
        /// without inventing a second MCU UID. The suffix becomes part of
        /// the SHA-1 input so two callers passing the same suffix always
        /// get the same ID.
        /// </summary>
        public static string DeriveDeviceIdWithSuffix(byte[] mcuUid, string suffix)
        {
            if (mcuUid == null || mcuUid.Length == 0)
                throw new ArgumentException("mcuUid must be non-empty.", nameof(mcuUid));
            if (string.IsNullOrEmpty(suffix))
                throw new ArgumentException("suffix must be non-empty.", nameof(suffix));
            using (var sha1 = SHA1.Create())
            {
                byte[] suffixBytes = Encoding.ASCII.GetBytes(suffix);
                byte[] combined = new byte[mcuUid.Length + suffixBytes.Length];
                Buffer.BlockCopy(mcuUid, 0, combined, 0, mcuUid.Length);
                Buffer.BlockCopy(suffixBytes, 0, combined, mcuUid.Length, suffixBytes.Length);
                byte[] hash = sha1.ComputeHash(combined);
                return ToHexLower(hash, 0, 8);
            }
        }

        /// <summary>
        /// Format the manifest's <c>mcuUid</c> / <c>parentId</c> field — first
        /// 6 bytes of the 12-byte STM32 UID as 12 lowercase-hex characters,
        /// matching the PitHouse capture exactly. If fewer than 6 bytes are
        /// available, emit whatever bytes we have (the manifest length will be
        /// shorter than the wire form but still parseable).
        /// </summary>
        public static string FormatMcuUid(byte[] mcuUid)
        {
            if (mcuUid == null || mcuUid.Length == 0) return string.Empty;
            int bytesToEmit = Math.Min(mcuUid.Length, McuUidHexLength / 2);
            return ToHexLower(mcuUid, 0, bytesToEmit);
        }

        /// <summary>
        /// ES wheels share device 0x13 between the wheel and the base —
        /// our base-* identity probes and wheel-* probes both end up
        /// reading the same physical MCU UID. When BaseMcuUid and WheelMcuUid
        /// are byte-equal (and both populated), treat the topology as
        /// ES — synthesise only the Steering Wheel manifest and skip the
        /// Motor / Wheel Base ghosts.
        /// </summary>
        public static bool IsEsWheelTopology(byte[]? baseUid, byte[]? wheelUid)
        {
            if (baseUid == null || wheelUid == null) return false;
            if (baseUid.Length == 0 || wheelUid.Length == 0) return false;
            if (baseUid.Length != wheelUid.Length) return false;
            return baseUid.SequenceEqual(wheelUid);
        }

        // --- Product-name mapping -----------------------------------------

        /// <summary>
        /// Map a wheel model-name prefix (firmware-reported, e.g.
        /// <c>"W18"</c>) to the SDK-side friendly product name. Falls back
        /// to the prefix itself for unknown wheels.
        /// </summary>
        public static string ResolveWheelProductName(string modelPrefix)
        {
            if (string.IsNullOrEmpty(modelPrefix)) return string.Empty;
            return WheelModelInfo.GetFriendlyName(modelPrefix);
        }

        // --- Hex helpers --------------------------------------------------

        private static string ToHexLower(byte[] bytes, int offset, int count)
        {
            if (bytes == null) return string.Empty;
            if (offset < 0 || count < 0 || offset + count > bytes.Length)
                throw new ArgumentOutOfRangeException(nameof(count));
            var sb = new StringBuilder(count * 2);
            for (int i = 0; i < count; i++)
                sb.Append(bytes[offset + i].ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }

    /// <summary>
    /// Immutable view of a single MOZA device's manifest, mirroring the
    /// CBOR shape PitHouse emits at
    /// <c>/MOZARacing/ProductDevice/&lt;id&gt;</c>. Field names are the
    /// CBOR map keys verbatim from the live capture.
    /// </summary>
    public sealed class DeviceManifest
    {
        public string AppVersion { get; }
        public string HardwareVersion { get; }
        public string Id { get; }
        public string McuUid { get; }
        public string ParentId { get; }
        public string ProductName { get; }
        public string ProductType { get; }

        public DeviceManifest(
            string appVersion,
            string hardwareVersion,
            string id,
            string mcuUid,
            string parentId,
            string productName,
            string productType)
        {
            AppVersion = appVersion ?? string.Empty;
            HardwareVersion = hardwareVersion ?? string.Empty;
            Id = id ?? string.Empty;
            McuUid = mcuUid ?? string.Empty;
            ParentId = parentId ?? string.Empty;
            ProductName = productName ?? string.Empty;
            ProductType = productType ?? string.Empty;
        }
    }
}
