using System;
using System.Collections.Generic;
using System.Linq;
using MozaPlugin.Telemetry.Dashboard;

namespace MozaPlugin.Devices.WheelUi
{
    /// <summary>
    /// Builds <see cref="ChannelMappingRow"/> lists for the per-wheel
    /// channel-mapping DataGrid. Two source modes:
    ///   1. Active profile loaded → enumerate tiers + string channels.
    ///   2. No profile → fall back to wheel-advertised catalog URLs.
    /// </summary>
    internal static class ChannelMappingRowFactory
    {
        /// <summary>
        /// Result of a row build: the row list (may be empty/null) and a
        /// human-readable status string for the UI's status label.
        /// </summary>
        public readonly struct BuildResult
        {
            public readonly List<ChannelMappingRow>? Rows;
            public readonly string StatusText;
            public BuildResult(List<ChannelMappingRow>? rows, string status)
            {
                Rows = rows;
                StatusText = status;
            }
        }

        public static BuildResult Build(MozaPlugin plugin)
        {
            if (plugin == null) return new BuildResult(null, "");

            // Snapshot the SimHub property list once so all rows share the same
            // backing list (avoids N copies of a 500-entry list).
            var props = plugin.GetAllSimHubPropertyNames();

            // FSR V1 renders fixed-schema group-0x42 dashboards, not tier-def
            // channels — map its built-in dashboard FIELDS from the catalog.
            if (plugin.IsFsr1DisplayWheel)
                return BuildFromFsr1Catalog(plugin, props);

            var profile = plugin.TelemetrySender?.Profile;

            if (profile == null || profile.Tiers.Count == 0)
                return BuildFromCatalog(plugin, props);

            return BuildFromProfile(profile, props);
        }

        /// <summary>
        /// Build rows for the FSR V1's built-in dashboards (group-0x42 record types),
        /// grouped by dashboard. One row per user-mappable field; engine-flag anchor
        /// fields are protocol-filled and omitted. Each row carries the field's
        /// current mapping (user override or catalog default) + input scale range.
        /// </summary>
        private static BuildResult BuildFromFsr1Catalog(MozaPlugin plugin, IReadOnlyList<string> props)
        {
            var rows = new List<ChannelMappingRow>();
            foreach (var dash in Telemetry.Fsr1DashboardCatalog.LiveDashboards)
            {
                foreach (var f in dash.Fields)
                {
                    if (!f.IsUserMappable) continue;
                    var m = plugin.GetFsr1FieldMapping(dash.Key, f.FieldId);
                    bool direct = f.Kind == Telemetry.Fsr1FieldKind.Direct;
                    rows.Add(new ChannelMappingRow
                    {
                        AllProperties = props,
                        IsFsr1 = true,
                        RecordKey = dash.Key,
                        FieldId = f.FieldId,
                        Name = $"{dash.Label} · {f.Label}" + (f.Decoded ? "" : "  (raw)"),
                        Url = dash.Key + "/" + f.FieldId,
                        Compression = f.Encoding.ToString(),
                        CapabilityText = direct ? "direct value" : $"0–{f.OutputMax}",
                        InMin = m?.InMin ?? f.DefaultInMin,
                        InMax = m?.InMax ?? f.DefaultInMax,
                        SimHubProperty = m?.Property ?? f.DefaultProperty,
                    });
                }
            }
            string status = rows.Count == 0
                ? "(FSR V1: no mappable dashboard fields)"
                : $"(FSR V1: {rows.Count} dashboard fields across built-in dashboards)";
            return new BuildResult(rows, status);
        }

        private static BuildResult BuildFromCatalog(MozaPlugin plugin, IReadOnlyList<string> props)
        {
            var catalog = plugin.WheelChannelCatalogForDiagnostics;
            if (catalog == null || catalog.Count == 0)
            {
                return new BuildResult(null,
                    "(no dashboard loaded and wheel has not advertised a channel catalog)");
            }

            var rows = new List<ChannelMappingRow>();
            foreach (var url in catalog.OrderBy(u => u, StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(url)) continue;
                // AllProperties MUST be set before SimHubProperty so the
                // setter's filter step sees the full list. Object initializers
                // assign in source order — list AllProperties first.
                rows.Add(new ChannelMappingRow
                {
                    AllProperties = props,
                    Name = url,
                    Url = url,
                    PackageLevel = 0,
                    Compression = "uint32_t",
                    SimHubProperty = "",
                });
            }
            return new BuildResult(rows,
                $"(no dashboard loaded — showing {rows.Count} wheel-advertised channels)");
        }

        private static BuildResult BuildFromProfile(MultiStreamProfile profile, IReadOnlyList<string> props)
        {
            // Per-widget tier-def emits one tier per dashboard widget, so a
            // dashboard with 12 widgets binding 6 unique URLs surfaces 12
            // tier×channel pairs. The mapping grid is keyed by URL → SimHub
            // property, so collapse duplicates by URL (first occurrence wins).
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rows = new List<ChannelMappingRow>();
            foreach (var tier in profile.Tiers.OrderBy(t => t.PackageLevel))
            {
                foreach (var ch in tier.Channels.OrderBy(c => c.Url, StringComparer.OrdinalIgnoreCase))
                {
                    if (DashboardProfileStore.IsInternalChannel(ch.SimHubProperty)) continue;
                    if (!seen.Add(ch.Url)) continue;
                    rows.Add(new ChannelMappingRow
                    {
                        AllProperties = props,
                        Name = ch.Name,
                        Url = ch.Url,
                        PackageLevel = ch.PackageLevel,
                        Compression = ch.Compression,
                        SimHubProperty = ch.SimHubProperty ?? "",
                    });
                }
            }
            // String channels live on profile.StringChannels (sess=0x01 type=0x05
            // out-of-band transport), not on Tiers — separate pass so users can
            // override the URL → SimHub property defaults from StringChannelDefaults.
            foreach (var ch in profile.StringChannels
                         .OrderBy(c => c.Url, StringComparer.OrdinalIgnoreCase))
            {
                if (DashboardProfileStore.IsInternalChannel(ch.SimHubProperty)) continue;
                if (!seen.Add(ch.Url)) continue;
                rows.Add(new ChannelMappingRow
                {
                    AllProperties = props,
                    Name = ch.Name,
                    Url = ch.Url,
                    PackageLevel = ch.PackageLevel,
                    Compression = ch.Compression,  // "string"
                    SimHubProperty = ch.SimHubProperty ?? "",
                });
            }
            return new BuildResult(rows, "");
        }
    }
}
