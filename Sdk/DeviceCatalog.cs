using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MozaPlugin.Devices;

namespace MozaPlugin.Sdk
{
    /// <summary>
    /// Synthesises the MOZA-SDK device catalogue served at
    /// <c>/MOZARacing/ProductDevice</c> and
    /// <c>/MOZARacing/ProductDevice/&lt;id&gt;</c>. Source data is the
    /// existing identity fields the plugin already collects from
    /// PitHouse-parity probes (group 0x06/0x07/0x09/0x11 + display
    /// equivalents).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The live capture
    /// (<c>~/Downloads/test-connection-moza-iracing.pcapng.gz</c>) showed
    /// 16-char-lowercase-hex device IDs. PitHouse's exact derivation is
    /// not yet known; this implementation uses
    /// <c>SHA-1(McuUid)[0..16]</c> as a first iteration that is stable per
    /// physical wheel and trivially regenerable. If the vendor SDK rejects
    /// our IDs because it expects a different hash, only this class needs
    /// to change.
    /// </para>
    /// <para>
    /// Field names mirror the CBOR map keys from the capture exactly —
    /// <c>appVersion</c>, <c>hardwareVersion</c>, <c>id</c>, <c>mcuUid</c>,
    /// <c>parentId</c>, <c>productName</c>, <c>productType</c> — and
    /// <see cref="ToCborEntries"/> emits them in the same order PitHouse
    /// does so byte-output can be compared verbatim against capture
    /// fixtures.
    /// </para>
    /// </remarks>
    public sealed class DeviceCatalog
    {
        /// <summary>Placeholder parentId for top-of-tree devices.</summary>
        public const string RootParentId = "0000000000000000";

        /// <summary>Width of a manifest device ID in hex characters.</summary>
        private const int IdHexLength = 16;

        /// <summary>Width of the manifest <c>mcuUid</c> field in hex characters (6 bytes of the 12-byte STM32 UID, matching capture).</summary>
        private const int McuUidHexLength = 12;

        private readonly MozaData _data;

        public DeviceCatalog(MozaData data)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
        }

        /// <summary>
        /// Enumerates hex IDs for every currently-identifiable MOZA device.
        /// Order is deterministic: wheelbase (if cached), wheel, display,
        /// pedals, handbrake. Returns an empty list when no device has yet
        /// supplied an MCU UID — the SDK then sees an empty device list,
        /// which is the same shape PitHouse would return between hardware
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
        /// topological order so callers and tests see stable ID ordering.
        /// </summary>
        private List<DeviceManifest> BuildManifests()
        {
            var list = new List<DeviceManifest>();

            // The plugin currently has no captured BaseMcuUid field (there
            // is no `base-mcu-uid` parser branch in MozaData). Without a
            // base UID we cannot synthesise a deterministic wheelbase ID,
            // so the wheelbase is omitted from the catalogue and the
            // wheel's parentId falls back to RootParentId. When a future
            // change adds BaseMcuUid + a base-presence flag, drop the
            // wheelbase manifest in here ahead of the wheel and rewire
            // wheelParentId to the base's Id.
            string wheelParentId = RootParentId;

            string? wheelId = null;
            byte[] wheelUid = _data.WheelMcuUid;
            if (wheelUid != null && wheelUid.Length > 0)
            {
                wheelId = DeriveDeviceId(wheelUid);
                string modelPrefix = WheelModelInfo.ExtractPrefix(_data.WheelModelName ?? string.Empty);
                string productName = ResolveWheelProductName(modelPrefix);
                // Prefer WheelHwSubVersion when populated — in the capture
                // the hardwareVersion field carried the long "RS21-W04-HW
                // SM-CU-V04B" string that the plugin's wheel-hw-sub probe
                // stores in WheelHwSubVersion, not the short
                // WheelHwVersion byte string. Fall back to WheelHwVersion
                // when sub-version isn't populated.
                string hardwareVersion = !string.IsNullOrEmpty(_data.WheelHwSubVersion)
                    ? _data.WheelHwSubVersion
                    : (_data.WheelHwVersion ?? string.Empty);
                // The capture's manifest field is named "appVersion" and
                // carries firmware-version strings like "1.2.7.2". MozaData
                // exposes that as WheelSwVersion (software/firmware
                // version reported by the wheel — group 0x07 cmd 0x02).
                // No separate WheelAppVersion field exists today.
                string appVersion = _data.WheelSwVersion ?? string.Empty;

                list.Add(new DeviceManifest(
                    appVersion:      appVersion,
                    hardwareVersion: hardwareVersion,
                    id:              wheelId,
                    mcuUid:          FormatMcuUid(wheelUid),
                    parentId:        wheelParentId,
                    productName:     productName,
                    productType:     "Steering Wheel"));
            }

            byte[] displayUid = _data.DisplayMcuUid;
            if (displayUid != null && displayUid.Length > 0)
            {
                string displayId = DeriveDeviceId(displayUid);
                string displayParent = wheelId ?? RootParentId;
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
                    parentId:        displayParent,
                    productName:     displayName,
                    productType:     "Display Screen"));
            }

            // Pedals and handbrake: no MCU UID captured today
            // (MozaData has no Pedals*McuUid / Handbrake*McuUid fields).
            // Per the streaming-spec contract, devices without a populated
            // MCU UID are not enumerated. When those fields appear in
            // MozaData later, add Pedals + Handbrake manifests here.

            return list;
        }

        // --- ID derivation -------------------------------------------------

        /// <summary>
        /// Derive a 16-char lowercase-hex device ID from a raw MCU UID.
        /// SHA-1 truncated to 16 hex chars: stable for a given physical
        /// wheel and trivially regenerable from the same UID input. If
        /// real PitHouse uses a different hash (the capture does not
        /// reveal which), only this method needs to change.
        /// </summary>
        public static string DeriveDeviceId(byte[] mcuUid)
        {
            if (mcuUid == null || mcuUid.Length == 0)
                throw new ArgumentException("mcuUid must be non-empty.", nameof(mcuUid));
            using (var sha1 = SHA1.Create())
            {
                byte[] hash = sha1.ComputeHash(mcuUid);
                // 16 lowercase hex chars = first 8 bytes of the digest.
                return ToHexLower(hash, 0, 8);
            }
        }

        /// <summary>
        /// Format the manifest's <c>mcuUid</c> field. The capture showed a
        /// 12-char string (e.g. <c>350e75ef7e7b</c>) which is the first 6
        /// bytes of the 12-byte STM32 UID; the trailing 6 bytes were not
        /// included. We replicate that truncation when enough bytes are
        /// present, otherwise emit whatever bytes we have.
        /// </summary>
        public static string FormatMcuUid(byte[] mcuUid)
        {
            if (mcuUid == null || mcuUid.Length == 0) return string.Empty;
            int bytesToEmit = Math.Min(mcuUid.Length, McuUidHexLength / 2);
            return ToHexLower(mcuUid, 0, bytesToEmit);
        }

        // --- Product-name mapping -----------------------------------------

        /// <summary>
        /// Map a wheel model-name prefix (firmware-reported, e.g.
        /// <c>"W18"</c>) to the SDK-side friendly product name. Falls back
        /// to the prefix itself for unknown wheels.
        /// </summary>
        /// <remarks>
        /// The capture's manifest had <c>productName=KS</c> but the
        /// firmware reports <c>W18</c> for the same physical wheel.
        /// <see cref="WheelModelInfo.GetFriendlyName"/> returns
        /// <c>"KS Pro"</c>, so we follow the SDK convention rather than
        /// the capture's truncation. A post-Phase-1 verification will
        /// confirm which form the vendor SDK actually checks against.
        /// </remarks>
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
