using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using MozaPlugin.Protocol;
using MozaPlugin.Resources;
using Newtonsoft.Json.Linq;

namespace MozaPlugin.Devices.Extensions
{
    /// <summary>
    /// Writes SimHub device definitions (<c>device.json</c>) into
    /// <c>DevicesDefinitions/User/&lt;DeviceName&gt;/</c> at runtime when a
    /// MOZA device is detected. Two sources: embedded resources (dashboards,
    /// base ambient strip) and a generated JSON tree (new-protocol wheels,
    /// where the LED/button layout depends on the detected model).
    ///
    /// Each method returns <c>true</c> when a file was actually written
    /// (fresh deploy or stale-rewrite); the caller is expected to flip its
    /// "restart SimHub" flag on a true result.
    /// </summary>
    internal static class DeviceDefinitionDeployer
    {
        private const string DashCm2Resource = "MozaPlugin.Devices.DashCm2.device.json";
        private const string DashCm1Resource = "MozaPlugin.Devices.DashCm1.device.json";
        private const string BaseAmbientResource = "MozaPlugin.Devices.WheelBase.device.json";
        private const string DashCm2DeviceName = "MOZA CM2 Racing Dash";
        private const string DashCm2ProductName = "CM2 Racing Dash";
        private const string DashCm1DeviceName = "MOZA CM1 Racing Dash";
        private const string DashCm1ProductName = "CM1 Racing Dash";
        private const string BaseAmbientDeviceName = "MOZA Wheel Base";

        // 0x0006 (R9 wheelbase) is the most common documented PID. The prior
        // 0x0004 placeholder doesn't match any known device. Used only when
        // registry discovery returns no PID (probe path under Wine).
        private const string FallbackPid = "0x0006";

        // SimHub renders a device profile's picture from a thumbnail.png sitting
        // next to device.json (PictureWrapper, [JsonIgnore] — not a device.json
        // field). Art is embedded keyed by firmware model prefix (generated wheels)
        // or by an explicit template key (dashes/base); see the csproj glob.
        private const string ThumbnailFileName = "thumbnail.png";
        private const string ThumbnailResourcePrefix = "MozaPlugin.Devices.Thumbnails.";

        // Template-based definitions (the CM1/CM2 dashes, base ambient, old-proto
        // wheel) deploy via DeployFromResource and have no firmware model prefix,
        // so their art is keyed by an explicit thumbnail name. Extend as art
        // arrives for the others.
        private const string DashCm2ThumbnailKey = "CM2";
        private const string DashCm1ThumbnailKey = "CM1";
        // The shared "MOZA Wheel Base" definition covers the whole ambient-strip
        // family (R21/R25/R27); the R21 render stands in for all of them for now.
        // Named by the model whose art it is, so per-model routing can be added
        // later without renaming (add R25U etc. keyed on the detected base).
        private const string BaseAmbientThumbnailKey = "R21U";

        // Device name → thumbnail key for the template-based definitions that ship
        // art. Drives RefreshDeployedThumbnails' startup top-up; the per-detection
        // DeployFromResource callers pass the key directly.
        private static readonly (string DeviceName, string ThumbnailKey)[] TemplateThumbnails =
        {
            (DashCm2DeviceName, DashCm2ThumbnailKey),
            (DashCm1DeviceName, DashCm1ThumbnailKey),
            (BaseAmbientDeviceName, BaseAmbientThumbnailKey),
        };

        // Content version of the dynamically generated wheel device.json. Bump
        // when the generated body changes in a way that should re-deploy over an
        // already-written file whose LED/button/knob counts are unchanged — e.g.
        // localizing the knob-section TitleOverride. The staleness check in
        // DeployGeneratedWheelDefinition rewrites any file with an older
        // SchemaVersion. v2: localized "Knob Indicators" TitleOverride. v3:
        // RPM-only wheels (ES, bare CS) no longer emit a phantom button physical
        // LED (10 RPM was counted as 11) and disable the buttons-backlight section.
        private const int GeneratedWheelSchemaVersion = 3;

        /// <summary>
        /// Deploy a dynamically generated device definition for a new-protocol wheel.
        /// Uses WheelModelInfo for button count (defaults for unknown models) and
        /// deterministic GUIDs for device identity.
        /// Called once when the wheel model name is first received from firmware.
        /// </summary>
        public static bool DeployForModel(string modelName, string? discoveredPid)
        {
            var prefix = WheelModelInfo.ExtractPrefix(modelName);
            var friendlyName = WheelModelInfo.GetFriendlyName(prefix);
            var guid = MozaDeviceConstants.ResolveWheelGuid(prefix);
            var modelInfo = WheelModelInfo.FromModelName(modelName);
            var deviceName = "MOZA " + friendlyName;

            return DeployGeneratedWheelDefinition(
                deviceName, guid, friendlyName,
                modelInfo.RpmLedCount, modelInfo.HasFlagLeds,
                modelInfo.ButtonLedCount, modelInfo.KnobCount,
                modelInfo.BrowSegmentSize,
                discoveredPid, prefix);
        }

        /// <summary>Outcome of <see cref="DeployAllKnown"/>: files written vs attempted.</summary>
        internal readonly struct DeployAllResult
        {
            public readonly int Written;
            public readonly int Total;

            public DeployAllResult(int written, int total)
            {
                Written = written;
                Total = total;
            }
        }

        /// <summary>
        /// Force-rewrite every device definition the plugin knows how to emit:
        /// one generated wheel definition per <see cref="WheelModelInfo.KnownModels"/>
        /// entry (artwork included), plus the base ambient strip and the CM1/CM2
        /// dashes. Unlike the lazy per-detection paths this
        /// ignores the staleness checks — the user asked for a redeploy, so an
        /// existing file that merely parses is still replaced (that is the repair
        /// case). Complements <see cref="RefreshDeployedThumbnails"/>, which tops up
        /// art but only for definitions already on disk.
        ///
        /// Every definition is stamped with <paramref name="wheelbasePid"/>: a
        /// wheel/base LED device is reached over its host composite's HID
        /// interface, so SimHub matches it on the WHEELBASE's PID, not on
        /// anything the rim reports. Deploying for a wheel the user does not own
        /// is harmless — an unused entry in SimHub's device list.
        /// </summary>
        public static DeployAllResult DeployAllKnown(string wheelbasePid, string dashboardPid)
        {
            int written = 0;
            int total = 0;
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (prefix, friendlyName, info) in WheelModelInfo.KnownModels)
            {
                var deviceName = "MOZA " + friendlyName;

                // Distinct prefixes can share a friendly name, hence a device
                // directory ("GS V2P" and the bare "GS" are both "MOZA GS V2 Pro").
                // Write each directory once; ResolveIdentityFor settles which
                // prefix's GUID it carries.
                if (!seenNames.Add(deviceName))
                    continue;

                total++;
                if (DeployGeneratedWheelDefinition(
                        deviceName, ResolveIdentityFor(deviceName, prefix), friendlyName,
                        info.RpmLedCount, info.HasFlagLeds, info.ButtonLedCount, info.KnobCount,
                        info.BrowSegmentSize, wheelbasePid, prefix, force: true))
                    written++;
            }

            var resources = new (string DeviceName, string Resource, string Guid, string Pid, string? ThumbnailKey)[]
            {
                (BaseAmbientDeviceName, BaseAmbientResource, MozaDeviceConstants.BaseAmbientGuid,   wheelbasePid, BaseAmbientThumbnailKey),
                (DashCm1DeviceName,     DashCm1Resource,     MozaDeviceConstants.DashCm1Guid,       wheelbasePid, DashCm1ThumbnailKey),
                (DashCm2DeviceName,     DashCm2Resource,     MozaDeviceConstants.DashCm2Guid,       dashboardPid, DashCm2ThumbnailKey),
            };

            foreach (var (deviceName, resource, guid, pid, thumbnailKey) in resources)
            {
                total++;
                // The ambient strip is the one templated definition whose LED count
                // varies by base model; the dashes are fixed-geometry.
                int? ledCount = string.Equals(deviceName, BaseAmbientDeviceName, StringComparison.Ordinal)
                    ? BaseModelInfo.TotalLeds(MozaPlugin.Instance?.Data?.BaseModelName)
                    : (int?)null;
                if (DeployFromResource(deviceName, resource, pid, guid, force: true, thumbnailKey: thumbnailKey,
                        telemetryLedCount: ledCount))
                    written++;
            }

            MozaLog.Info(
                $"[AZOM] Redeployed all device definitions: {written}/{total} written " +
                $"(wheelbase pid={wheelbasePid}, dash pid={dashboardPid}; restart SimHub to pick them up)");
            return new DeployAllResult(written, total);
        }

        /// <summary>
        /// Identity to stamp into <paramref name="deviceName"/>'s definition during a
        /// bulk redeploy: the GUID already on disk when it belongs to a prefix sharing
        /// this device directory, otherwise <paramref name="prefix"/>'s own GUID.
        ///
        /// A redeploy must refresh a definition's BODY without re-keying its IDENTITY.
        /// Two firmware variants can share a directory under different GUIDs ("GS V2P"
        /// and the bare "GS" are both "MOZA GS V2 Pro"), and only the connected wheel
        /// says which is right — <see cref="MozaLedDeviceManager.IsModelConnected"/>
        /// matches the wheel's reported model against the prefix the DEFINITION's GUID
        /// resolves to, so stamping "GS V2P" over a bare-"GS" user's file would leave
        /// their wheel's LEDs dark and orphan the SimHub device instance keyed to the
        /// old GUID. Whatever the per-detection path wrote was chosen against the real
        /// wheel, so it wins over this path's first-wins guess.
        /// </summary>
        private static string ResolveIdentityFor(string deviceName, string prefix)
        {
            var fallback = MozaDeviceConstants.ResolveWheelGuid(prefix);
            try
            {
                var path = DeviceJsonPath(deviceName);
                if (!File.Exists(path))
                    return fallback;

                string? existingGuid = JObject.Parse(File.ReadAllText(path))
                    .SelectToken("DescriptorUniqueId")?.Value<string>();
                if (string.IsNullOrEmpty(existingGuid))
                    return fallback;

                // Only honour a GUID that maps back to a model prefix landing in THIS
                // directory; anything else (unregistered, generic, old-proto marker) is
                // not an identity for this device and must not be carried forward.
                var mapped = MozaDeviceConstants.GetWheelModelPrefix(existingGuid!);
                if (string.IsNullOrEmpty(mapped))
                    return fallback;

                if (!string.Equals("MOZA " + WheelModelInfo.GetFriendlyName(mapped!), deviceName,
                        StringComparison.Ordinal))
                    return fallback;

                if (!string.Equals(existingGuid, fallback, StringComparison.OrdinalIgnoreCase))
                    MozaLog.Debug(
                        $"[AZOM] Redeploy: keeping existing identity {existingGuid} (prefix={mapped}) " +
                        $"for '{deviceName}' rather than re-keying to {fallback} (prefix={prefix})");
                return existingGuid!;
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[AZOM] Could not read existing identity for '{deviceName}': {ex.Message}");
                return fallback;
            }
        }

        private static string DeviceJsonPath(string deviceName) =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "DevicesDefinitions", "User", deviceName, "device.json");

        /// <summary>
        /// The PID every definition should carry: the primary pipe's live PID —
        /// the wheelbase, or the hub when the wheel answers there — then a registry
        /// walk, then <see cref="FallbackPid"/>.
        ///
        /// Never null, deliberately. <c>DiscoveredPid</c> is only set when the port
        /// came from registry discovery; the serial-probe connect path (the norm
        /// under Wine, where the registry walk finds nothing) leaves it null on a
        /// perfectly live connection. The per-detection deploys stamp FallbackPid in
        /// exactly that case, so this must too — a redeploy has to reproduce what
        /// detection writes, not second-guess it.
        /// </summary>
        public static string ResolveWheelbasePid(MozaSerialConnection? primary)
        {
            // Prefer the live value verbatim — it is exactly what the lazy
            // per-detection deploys stamp, so a redeploy stays consistent with
            // them (including the hub-only topology, where the primary legitimately
            // sits on the hub's PID).
            var live = primary?.DiscoveredPid;
            if (!string.IsNullOrEmpty(live))
                return live!;

            var ports = MozaPortDiscovery.Instance.Enumerate();
            foreach (var port in ports)
                if (port.Category == MozaDeviceCategory.Wheelbase)
                    return FormatPid(port.Pid);

            // Hub second: it only owns the wheel's HID when there is no base.
            foreach (var port in ports)
                if (port.Category == MozaDeviceCategory.Hub)
                    return FormatPid(port.Pid);

            return FallbackPid;
        }

        /// <summary>
        /// The PID for the CM2 definition, which is topology-dependent: a
        /// standalone-USB CM2 enumerates on its own <c>0x0025</c>, while a
        /// bus-bridged one is reached through the base and carries the base's PID.
        /// Prefer a real 0x0025 port when one is present so a redeploy can't stamp
        /// the base's PID over a working standalone-CM2 definition.
        /// </summary>
        public static string ResolveDashboardPid(string wheelbasePid)
        {
            foreach (var port in MozaPortDiscovery.Instance.Enumerate())
                if (port.Category == MozaDeviceCategory.Dashboard)
                    return MozaUsbIds.PidDashboardCm2;

            return wheelbasePid;
        }

        private static string FormatPid(ushort pid) =>
            "0x" + pid.ToString("X4", CultureInfo.InvariantCulture);

        /// <summary>
        /// Deploy the embedded CM2 dashboard device definition. The only
        /// standalone dashboard PID is the CM2's 0x0025, and a bus-bridged dash
        /// is either a CM2 or a CM1 (the latter via <see cref="DeployCm1Dashboard"/>),
        /// so every <c>DeployDashboard</c> target is the CM2 template.
        /// </summary>
        public static bool DeployDashboard(string? discoveredPid)
            => DeployFromResource(DashCm2DeviceName, DashCm2Resource, discoveredPid, MozaDeviceConstants.DashCm2Guid,
                thumbnailKey: DashCm2ThumbnailKey);

        /// <summary>
        /// Deploy the CM1 base-bridged dash definition (its own GUID, distinct
        /// from CM2/legacy). Called once the CM1 discriminator confirms a
        /// bus-bridged dash speaks group-0x35 rather than tier-def. Returns true
        /// if a definition was written (SimHub restart required to pick it up).
        /// </summary>
        public static bool DeployCm1Dashboard(string? discoveredPid)
            => DeployFromResource(DashCm1DeviceName, DashCm1Resource, discoveredPid, MozaDeviceConstants.DashCm1Guid,
                thumbnailKey: DashCm1ThumbnailKey);

        /// <summary>
        /// Remove the CM2 dash definition that <see cref="DeployDashboard"/> wrote
        /// speculatively for a bus-bridged dash, once that dash turns out to be a
        /// CM1. Guarded so a REAL standalone-USB CM2 (PID 0x0025) is never removed
        /// — only the base-bridged speculative copy (whose PID is the base's) is
        /// deleted. This is the duplicate-entry fix; it does NOT make CM1/CM2
        /// mutually exclusive (both templates remain embedded and deployable).
        /// </summary>
        public static bool RemoveSpeculativeCm2Dashboard()
        {
            try
            {
                var simHubDir = AppDomain.CurrentDomain.BaseDirectory;
                var deviceDir = Path.Combine(simHubDir, "DevicesDefinitions", "User", DashCm2DeviceName);
                var deviceJsonPath = Path.Combine(deviceDir, "device.json");
                if (!File.Exists(deviceJsonPath)) return false;

                // Only remove the base-bridged speculative copy. A genuine USB CM2
                // carries PID 0x0025; leave that one alone.
                string? existingPid = JObject.Parse(File.ReadAllText(deviceJsonPath))
                    .SelectToken("HardwareInterface.HardwareInterface.DeviceDetection.Pid")
                    ?.Value<string>();
                if (string.Equals(existingPid, MozaUsbIds.PidDashboardCm2, StringComparison.OrdinalIgnoreCase))
                    return false;

                Directory.Delete(deviceDir, recursive: true);
                MozaLog.Info($"[AZOM] Removed speculative CM2 dash definition (this dash is a CM1; restart SimHub to drop the stale entry)");
                return true;
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[AZOM] Could not remove speculative CM2 dash definition: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Deploy the embedded "Wheel Base" device definition exposing the
        /// 18-LED ambient telemetry strip. Caller must have already verified
        /// the connected base actually has the strip (via the
        /// base-ambient-brightness read probe in MozaPlugin) — bases without
        /// it (R9/R12) should never see this file deployed.
        /// </summary>
        /// <summary>
        /// Deploy the wheelbase ambient-strip definition. <paramref name="baseModelName"/>
        /// is the firmware model string (group 0x07 @ dev 0x12); it selects the LED
        /// count, since strip length is per model and the device exposes no geometry
        /// register. An empty/unknown name falls back to the 9-per-strip layout.
        /// </summary>
        public static bool DeployBaseAmbient(string? discoveredPid, string? baseModelName = null)
            => DeployFromResource(BaseAmbientDeviceName, BaseAmbientResource, discoveredPid, MozaDeviceConstants.BaseAmbientGuid,
                thumbnailKey: BaseAmbientThumbnailKey,
                telemetryLedCount: BaseModelInfo.TotalLeds(baseModelName));

        // Rewrite the ambient definition's LED geometry: the logical telemetry LED
        // count, the physical mapping's RepeatCount, and the length of the physical
        // Items array (one real mapping plus empty placeholders, one per LED).
        private static string PatchTelemetryLedCount(string json, int ledCount, string deviceName)
        {
            try
            {
                var doc = JObject.Parse(json);

                var logical = doc.SelectToken("LedsFeature.LogicalTelemetryLeds") as JObject;
                if (logical != null)
                    logical["LedCount"] = ledCount;

                if (doc.SelectToken("LedsFeature.PhysicalLedsMappings.Items") is JArray items && items.Count > 0)
                {
                    if (items[0] is JObject first && first["RepeatCount"] != null)
                        first["RepeatCount"] = ledCount;

                    while (items.Count > ledCount)
                        items.RemoveAt(items.Count - 1);
                    while (items.Count < ledCount)
                        items.Add(new JObject());
                }

                MozaLog.Debug($"[AZOM] Patched {deviceName} telemetry LED count to {ledCount}");
                return doc.ToString();
            }
            catch (Exception ex)
            {
                // A parse failure here must not block deployment — ship the
                // template unpatched rather than no definition at all.
                MozaLog.Warn($"[AZOM] Could not patch LED count for '{deviceName}', using template as-is: {ex.Message}");
                return json;
            }
        }

        private static bool DeployGeneratedWheelDefinition(string deviceName, string guid, string productName,
            int rpmCount, bool hasFlagLeds, int buttonCount, int knobCount, int browSegmentSize, string? discoveredPid,
            string modelPrefix, bool force = false)
        {
            try
            {
                var simHubDir = AppDomain.CurrentDomain.BaseDirectory;
                var userDefsDir = Path.Combine(simHubDir, "DevicesDefinitions", "User");
                var deviceDir = Path.Combine(userDefsDir, deviceName);
                var deviceJsonPath = Path.Combine(deviceDir, "device.json");

                int expectedTelemetryCount = rpmCount + (hasFlagLeds ? 6 : 0);
                bool fileExists = File.Exists(deviceJsonPath);

                if (fileExists && !force)
                {
                    bool stale;

                    // Compare existing LogicalTelemetryLeds.LedCount + LogicalButtonsSection.Items
                    // against expected. Mismatch = layout changed in a plugin update; rewrite.
                    try
                    {
                        var existing = JObject.Parse(File.ReadAllText(deviceJsonPath));
                        int existingLed = existing.SelectToken("LedsFeature.LogicalTelemetryLeds.LedCount")?.Value<int>() ?? -1;
                        int existingButtons = (existing.SelectToken("LedsFeature.LogicalButtonsSection.Items") as JArray)?.Count ?? -1;
                        int existingExtra = existing.SelectToken("LedsFeature.LogicalExtraSection.LedCount")?.Value<int>() ?? 0;
                        int existingSchema = existing.SelectToken("SchemaVersion")?.Value<int>() ?? 0;
                        string? existingKnobTitle = existing
                            .SelectToken("LedsFeature.LogicalExtraSection.TitleOverride")?.Value<string>();
                        bool? existingIndividual = existing
                            .SelectToken("LedsFeature.IsIndividualLedsSectionEnabled")?.Value<bool>();
                        string? existingDescriptorId = existing
                            .SelectToken("DescriptorUniqueId")?.Value<string>();
                        bool expectedIndividual = buttonCount > 0 || knobCount > 0;
                        stale = existingLed != expectedTelemetryCount
                            || existingButtons != buttonCount
                            || existingExtra != knobCount
                            // Identity drifted from the one THIS wheel's model prefix
                            // resolves to. Two firmware variants can share a device
                            // directory under different GUIDs ("GS V2P" vs bare "GS"),
                            // and only the connected wheel settles which is correct —
                            // so the detected model re-keys a file that a guess (a bulk
                            // redeploy with no wheel attached) stamped with the other
                            // variant's GUID. Without this the layout fields match and
                            // the wrong identity would stick, leaving the wheel's LEDs
                            // dark (see MozaLedDeviceManager.IsModelConnected).
                            || !string.Equals(existingDescriptorId, guid, StringComparison.OrdinalIgnoreCase)
                            // Content-version bump (e.g. localized TitleOverride) forces a
                            // one-time rewrite for users whose file predates the change.
                            || existingSchema < GeneratedWheelSchemaVersion
                            // Individual-LEDs flag drifted (RPM-only wheels must have it
                            // off). Targeted: only files with the wrong value rewrite, so
                            // button/knob wheels aren't needlessly re-deployed.
                            || (existingIndividual.HasValue && existingIndividual.Value != expectedIndividual)
                            // Knob-section label drifted from the current UI culture's
                            // translation — re-deploy so it matches (handles a SimHub
                            // language change after the file was first written).
                            || (knobCount > 0 && !string.Equals(
                                    existingKnobTitle, Strings.DeviceDef_KnobIndicators, StringComparison.Ordinal));
                    }
                    catch (Exception parseEx)
                    {
                        MozaLog.Warn(
                            $"[AZOM] Could not parse existing device.json for '{deviceName}', rewriting: {parseEx.Message}");
                        stale = true;
                    }

                    if (!stale)
                    {
                        // Definition is current, but the artwork may still be
                        // missing (added by a plugin update, or user-deleted).
                        EnsureThumbnail(deviceDir, modelPrefix);
                        return false;
                    }
                }

                Directory.CreateDirectory(deviceDir);

                // Registry-based discovery always populates DiscoveredPid when
                // we successfully connected. Fallback only matters if the file
                // is generated before the first connect.
                var pid = discoveredPid ?? FallbackPid;
                var json = GenerateWheelDeviceJson(guid, productName, rpmCount, hasFlagLeds, buttonCount, knobCount, browSegmentSize, pid);
                File.WriteAllText(deviceJsonPath, json);
                EnsureThumbnail(deviceDir, modelPrefix);

                string action = fileExists ? "Refreshed" : "Deployed";
                MozaLog.Debug(
                    $"[AZOM] {action} device definition: {deviceName} " +
                    $"(guid={guid}, telemetryLeds={expectedTelemetryCount}, rpm={rpmCount}, flags={hasFlagLeds}, " +
                    $"buttons={buttonCount}, knobs={knobCount}, brow={browSegmentSize}, pid={pid}, restart SimHub to pick up changes)");
                return true;
            }
            catch (Exception ex)
            {
                MozaLog.Error($"[AZOM] Error deploying device definition '{deviceName}': {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Top up artwork for wheel definitions that are already on disk.
        /// <see cref="DeployForModel"/> only ever reaches the wheel that is
        /// currently attached, so definitions left behind by other wheels the
        /// user has owned would stay picture-less until that wheel is plugged
        /// back in. Called once at Init.
        ///
        /// Only writes into directories that already exist — it never creates a
        /// definition for a wheel the user has not actually had connected, and
        /// never rewrites device.json. Cosmetic, so it reports nothing and never
        /// asks for a SimHub restart.
        /// </summary>
        public static void RefreshDeployedThumbnails()
        {
            try
            {
                var userDefsDir = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "DevicesDefinitions", "User");
                if (!Directory.Exists(userDefsDir))
                    return;

                foreach (var (prefix, friendlyName, _) in WheelModelInfo.KnownModels)
                {
                    var deviceDir = Path.Combine(userDefsDir, "MOZA " + friendlyName);
                    if (File.Exists(Path.Combine(deviceDir, "device.json")))
                        EnsureThumbnail(deviceDir, prefix);
                }

                // Template-based definitions (dashes/base) that ship art — keyed by
                // device name, not a firmware prefix.
                foreach (var (deviceName, thumbnailKey) in TemplateThumbnails)
                {
                    var deviceDir = Path.Combine(userDefsDir, deviceName);
                    if (File.Exists(Path.Combine(deviceDir, "device.json")))
                        EnsureThumbnail(deviceDir, thumbnailKey);
                }
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[AZOM] Could not refresh deployed device thumbnails: {ex.Message}");
            }
        }

        /// <summary>
        /// Write the model's product render to <c>thumbnail.png</c> beside its
        /// device.json — SimHub renders the device-profile picture from that
        /// sidecar. No-op when no art ships for the model (most of them), or
        /// when the file already matches. Cosmetic only: it never throws into
        /// the deploy path and never flips the caller's "restart SimHub" result,
        /// since the picture appears on the next SimHub start regardless.
        /// </summary>
        private static void EnsureThumbnail(string deviceDir, string thumbnailKey)
        {
            try
            {
                if (string.IsNullOrEmpty(thumbnailKey))
                    return;

                var assembly = Assembly.GetExecutingAssembly();
                using (var stream = assembly.GetManifestResourceStream(ThumbnailResourcePrefix + thumbnailKey + ".png"))
                {
                    if (stream == null)
                        return;

                    var bytes = new byte[stream.Length];
                    int read = 0;
                    while (read < bytes.Length)
                    {
                        int n = stream.Read(bytes, read, bytes.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }

                    var thumbnailPath = Path.Combine(deviceDir, ThumbnailFileName);
                    if (File.Exists(thumbnailPath) && BytesEqual(File.ReadAllBytes(thumbnailPath), bytes))
                        return;

                    Directory.CreateDirectory(deviceDir);
                    File.WriteAllBytes(thumbnailPath, bytes);
                    MozaLog.Debug($"[AZOM] Wrote device thumbnail for {thumbnailKey}: {thumbnailPath}");
                }
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[AZOM] Could not write device thumbnail for '{thumbnailKey}': {ex.Message}");
            }
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                    return false;
            }
            return true;
        }

        private static string GenerateWheelDeviceJson(string guid, string productName, int rpmCount, bool hasFlagLeds, int buttonCount, int knobCount, int browSegmentSize, string pid)
        {
            var physItems = new JArray();

            // Telemetry LEDs: single contiguous sequence. When the wheel has flag LEDs
            // they are 3-on-each-side of the RPM strip, so SimHub sees (rpmCount + 6)
            // LEDs as one logical run: [flag 1..3][rpm 1..N][flag 4..6]. Skipped
            // for a button-only wheel (0 RPM LEDs) — no phantom RepeatCount=0 item.
            int telemetryCount = rpmCount + (hasFlagLeds ? 6 : 0);
            if (telemetryCount > 0)
            {
                physItems.Add(new JObject
                {
                    ["SourceRole"] = 1,
                    ["SourceIndex"] = 0,
                    ["RepeatCount"] = telemetryCount,
                    ["RepeatMode"] = 1
                });
                for (int i = 1; i < telemetryCount; i++)
                    physItems.Add(new JObject());
            }

            // Button LEDs: buttonCount slots. Gated on buttonCount > 0 — an
            // RPM-only wheel (ES, bare CS) must NOT emit a button header item, or
            // SimHub counts a phantom physical LED (e.g. 10 RPM → 11 total).
            if (buttonCount > 0)
            {
                physItems.Add(new JObject
                {
                    ["SourceRole"] = 2,
                    ["SourceIndex"] = 0,
                    ["RepeatCount"] = buttonCount,
                    ["RepeatMode"] = 1
                });
                for (int i = 1; i < buttonCount; i++)
                    physItems.Add(new JObject());
            }

            // Knob indicator LEDs (Extra/encoders channel): one per rotary knob
            if (knobCount > 0)
            {
                physItems.Add(new JObject
                {
                    ["SourceRole"] = 3,
                    ["SourceIndex"] = 0,
                    ["RepeatCount"] = knobCount,
                    ["RepeatMode"] = 1
                });
                for (int i = 1; i < knobCount; i++)
                    physItems.Add(new JObject());
            }

            var buttonItems = new JArray();
            for (int i = 0; i < buttonCount; i++)
            {
                buttonItems.Add(new JObject
                {
                    ["Left"] = 20,
                    ["Top"] = 20,
                    ["Width"] = 40
                });
            }

            var device = new JObject
            {
                ["DescriptorUniqueId"] = guid,
                ["SchemaVersion"] = GeneratedWheelSchemaVersion,
                ["MinimumSimHubVersion"] = "9.11.8",
                ["DeviceDescription"] = new JObject
                {
                    ["BrandName"] = "MOZA",
                    ["ProductName"] = productName
                },
                ["LedsFeature"] = new JObject
                {
                    // Individual-LEDs section is only meaningful when the wheel has
                    // addressable button and/or knob LEDs beyond the RPM strip. For
                    // RPM-only wheels (e.g. ES, bare CS) it adds a useless editor
                    // section, so disable it — matching the hand-authored old-proto
                    // template.
                    ["IsIndividualLedsSectionEnabled"] = buttonCount > 0 || knobCount > 0,
                    ["PhysicalLedsMappings"] = new JObject { ["Items"] = physItems },
                    ["LogicalTelemetryLeds"] = new JObject
                    {
                        ["LedCount"] = telemetryCount,
                        ["Segments"] = BuildTelemetrySegments(hasFlagLeds, browSegmentSize),
                        // Off for button-only wheels (no RPM/flag LEDs).
                        ["IsEnabled"] = telemetryCount > 0
                    },
                    ["LogicalButtonsSection"] = new JObject
                    {
                        ["IsButtonEditorEnabled"] = false,
                        ["Items"] = buttonItems,
                        // Disabled for RPM-only wheels so the "enable buttons
                        // backlight" section doesn't show with zero buttons.
                        ["IsEnabled"] = buttonCount > 0
                    },
                    ["LogicalExtraSection"] = knobCount > 0
                        ? new JObject
                        {
                            ["LedCount"] = knobCount,
                            ["TitleOverride"] = Strings.DeviceDef_KnobIndicators,
                            ["IsEnabled"] = true
                        }
                        : new JObject { ["IsEnabled"] = false },
                    ["IsEnabled"] = true
                },
                ["HardwareInterface"] = new JObject
                {
                    ["HardwareInterface"] = new JObject
                    {
                        ["TypeName"] = "LedsStandardHIDProtocol",
                        ["IsSerialNumberPickerEnabled"] = false,
                        ["HIDUsagePage"] = "0xFF00",
                        ["HIDUsage"] = "0x77",
                        ["HIDReportId"] = "0x68",
                        ["HIDReportSize"] = 64,
                        ["DeviceDetection"] = new JObject
                        {
                            ["Vid"] = "0x346E",
                            ["Pid"] = pid
                        }
                    }
                },
                ["IsLocked"] = true
            };

            return device.ToString(Newtonsoft.Json.Formatting.Indented);
        }

        // SimHub interprets a single Segments entry of Size N as a 3-LED brow
        // region carved from the LogicalTelemetryLeds strip. Both the legacy
        // hasFlagLeds path (extra 6 LEDs total, 3 per side) and the in-band
        // brow path (segment carved from the existing strip) use the same
        // single-entry representation; they only differ in whether the LED
        // count includes the segment. Brow size wins when both are present.
        private static JArray BuildTelemetrySegments(bool hasFlagLeds, int browSegmentSize)
        {
            int size = browSegmentSize > 0 ? browSegmentSize : (hasFlagLeds ? 3 : 0);
            if (size <= 0)
                return new JArray();
            return new JArray(new JObject { ["Size"] = size });
        }

        private static bool DeployFromResource(string deviceName, string resourceName, string? discoveredPid, string expectedDescriptorId,
            bool force = false, string? thumbnailKey = null, int? telemetryLedCount = null)
        {
            try
            {
                var simHubDir = AppDomain.CurrentDomain.BaseDirectory;
                var userDefsDir = Path.Combine(simHubDir, "DevicesDefinitions", "User");
                var deviceDir = Path.Combine(userDefsDir, deviceName);
                var deviceJsonPath = Path.Combine(deviceDir, "device.json");

                bool fileExists = File.Exists(deviceJsonPath);

                if (fileExists && !force)
                {
                    bool stale;

                    // Template content version. A bump here forces a rewrite of an
                    // already-deployed definition whose GUID/PID/ProductName are
                    // unchanged but whose body changed (e.g. CM2 SchemaVersion 1→2
                    // dropping the individual-LEDs section and 10/6 LED layout).
                    int templateSchema = ReadResourceSchemaVersion(resourceName);

                    // Compare existing PID + DescriptorUniqueId against expected.
                    // PID mismatch covers user moving between hardware variants;
                    // DescriptorUniqueId mismatch covers the plugin shipping a new
                    // template for the same PID under a different GUID.
                    try
                    {
                        var existing = JObject.Parse(File.ReadAllText(deviceJsonPath));
                        string? existingPid = existing
                            .SelectToken("HardwareInterface.HardwareInterface.DeviceDetection.Pid")
                            ?.Value<string>();
                        string? existingDescriptorId = existing
                            .SelectToken("DescriptorUniqueId")
                            ?.Value<string>();
                        string expectedPid = discoveredPid ?? FallbackPid;
                        stale =
                            !string.Equals(existingPid, expectedPid, StringComparison.OrdinalIgnoreCase)
                            || !string.Equals(existingDescriptorId, expectedDescriptorId, StringComparison.OrdinalIgnoreCase);

                        // CM-dash guard: a user-renamed JSON (ProductName changed
                        // from the shipped "CM2 Racing Dash" / "CM1 Racing Dash")
                        // signals manual intervention; rewrite to the shipped
                        // template.
                        string? expectedProduct =
                            string.Equals(deviceName, DashCm2DeviceName, StringComparison.Ordinal) ? DashCm2ProductName
                            : string.Equals(deviceName, DashCm1DeviceName, StringComparison.Ordinal) ? DashCm1ProductName
                            : null;
                        if (!stale && expectedProduct != null)
                        {
                            string? productName = existing
                                .SelectToken("DeviceDescription.ProductName")
                                ?.Value<string>();
                            if (!string.Equals(productName, expectedProduct, StringComparison.Ordinal))
                                stale = true;
                        }

                        // Content-version guard: a newer template SchemaVersion than
                        // the deployed file means the shipped definition body changed
                        // without a GUID/PID/ProductName change. Missing field = 0.
                        if (!stale)
                        {
                            int existingSchema = existing.SelectToken("SchemaVersion")?.Value<int>() ?? 0;
                            if (existingSchema < templateSchema)
                                stale = true;
                        }

                        // Geometry guard: the base ambient definition's LED count is
                        // patched per base model (6 vs 9 LEDs per strip), so a file
                        // written for a different base — or by a plugin build that
                        // hardcoded 18 — has to be rewritten.
                        if (!stale && telemetryLedCount.HasValue)
                        {
                            int existingLeds = existing
                                .SelectToken("LedsFeature.LogicalTelemetryLeds.LedCount")?.Value<int>() ?? -1;
                            if (existingLeds != telemetryLedCount.Value)
                                stale = true;
                        }
                    }
                    catch (Exception parseEx)
                    {
                        MozaLog.Warn(
                            $"[AZOM] Could not parse existing device.json for '{deviceName}', rewriting: {parseEx.Message}");
                        stale = true;
                    }

                    if (!stale)
                    {
                        // Definition is current; the artwork may still be missing
                        // (added by a plugin update, or user-deleted).
                        if (thumbnailKey != null)
                            EnsureThumbnail(deviceDir, thumbnailKey);
                        return false;
                    }
                }

                var assembly = Assembly.GetExecutingAssembly();
                using (var stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                    {
                        MozaLog.Warn($"[AZOM] Embedded resource not found: {resourceName}");
                        return false;
                    }

                    Directory.CreateDirectory(deviceDir);

                    // Read the template JSON and patch the PID if we discovered one
                    string json;
                    using (var reader = new StreamReader(stream))
                    {
                        json = reader.ReadToEnd();
                    }

                    if (discoveredPid != null)
                    {
                        json = json.Replace("__DETECT_PID__", discoveredPid);
                        MozaLog.Debug($"[AZOM] Patched device PID to {discoveredPid} for {deviceName}");
                    }
                    else
                    {
                        json = json.Replace("__DETECT_PID__", FallbackPid);
                        MozaLog.Debug($"[AZOM] No PID discovered, using fallback {FallbackPid} for {deviceName}");
                    }

                    if (telemetryLedCount.HasValue)
                        json = PatchTelemetryLedCount(json, telemetryLedCount.Value, deviceName);

                    File.WriteAllText(deviceJsonPath, json);
                }

                if (thumbnailKey != null)
                    EnsureThumbnail(deviceDir, thumbnailKey);

                string action = fileExists ? "Refreshed" : "Deployed";
                MozaLog.Info($"[AZOM] {action} device definition: {deviceName} (restart SimHub to add it)");
                return true;
            }
            catch (Exception ex)
            {
                MozaLog.Error($"[AZOM] Error deploying device definition '{deviceName}': {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Read the <c>SchemaVersion</c> of an embedded device-definition template.
        /// Defaults to 1 if the resource is missing/unparseable so a deployed file
        /// with no SchemaVersion (treated as 0) still refreshes once.
        /// </summary>
        private static int ReadResourceSchemaVersion(string resourceName)
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                using (var stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream == null) return 1;
                    using (var reader = new StreamReader(stream))
                    {
                        var template = JObject.Parse(reader.ReadToEnd());
                        return template.SelectToken("SchemaVersion")?.Value<int>() ?? 1;
                    }
                }
            }
            catch
            {
                return 1;
            }
        }
    }
}
