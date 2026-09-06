using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using GameReaderCommon.Enums;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SimHub.Plugins.DataPlugins.ShakeItV3.Device.MotorsWithFrequency;
using SimHub.Plugins.DataPlugins.ShakeItV3.Settings;

namespace MozaPlugin.Devices.Haptics
{
    /// <summary>
    /// The plugin's ShakeIt effect defaults for the wheelbase Haptics section:
    /// every stock effect off, and only oscillator 1 selected (the base sums its
    /// three oscillators into one actuator, so enabling all three triples the
    /// output). Shipped as data rather than code so the feel can be retuned by
    /// editing JSON, and reused by both delivery paths:
    ///
    /// * <see cref="Deploy"/> writes them to
    ///   <c>ShakeIt/EffectsDefaults/MozaWheelbaseLfe/</c>, which is SimHub's own
    ///   hook — <c>LoadDefaultPlatformSettings</c> reads that folder on Add effect
    ///   and Reset effect, keyed by the provider's DefaultSettingsKey.
    /// * <see cref="ApplyTo"/> populates a container directly, for the 24-effect
    ///   profile SimHub seeds during device construction — that happens before the
    ///   plugin's provider is installed, so it is still keyed to SimHub's stock
    ///   "SimagicReactors" defaults and the folder above is never consulted for it.
    /// </summary>
    internal static class MozaLfeEffectDefaults
    {
        public const string SettingsKey = "MozaWheelbaseLfe";

        /// <summary>
        /// Applies to EVERY effect container, whatever its type. This is the plugin's
        /// policy — all effects off, oscillator 1 only — expressed as data rather than
        /// code. A per-type file layers tuning on top of it.
        ///
        /// Not a ContainerType, so it never collides with a per-type lookup, and the
        /// leading underscore keeps it out of SimHub's own
        /// LoadDefaultPlatformSettings path (which looks for &lt;ContainerType&gt;.json).
        /// </summary>
        private const string BaselineKey = "_Baseline";

        private const string ResourcePrefix = "MozaPlugin.ShakeItDefaults.MozaWheelbaseLfe.";

        private static readonly Dictionary<string, string> Cache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static bool _loaded;

        /// <summary>Write the shipped defaults into SimHub's EffectsDefaults folder. Idempotent; skips files whose bytes already match.</summary>
        public static void Deploy()
        {
            try
            {
                var dir = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "ShakeIt", "EffectsDefaults", SettingsKey);
                Directory.CreateDirectory(dir);

                int written = 0;
                foreach (var kvp in All())
                {
                    var path = Path.Combine(dir, kvp.Key + ".json");
                    if (File.Exists(path) && string.Equals(File.ReadAllText(path), kvp.Value, StringComparison.Ordinal))
                        continue;
                    File.WriteAllText(path, kvp.Value);
                    written++;
                }

                if (written > 0)
                    MozaLog.Info($"[AZOM] Deployed {written} wheelbase LFE effect default(s) to ShakeIt/EffectsDefaults/{SettingsKey}");
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[AZOM] Could not deploy wheelbase LFE effect defaults: {ex.Message}");
            }
        }

        /// <summary>
        /// Apply the plugin's defaults to a live effect container: the baseline for
        /// every container, then this type's own file if one ships.
        ///
        /// Authoritative, not an override — the baseline lands on every container
        /// regardless of type, so whatever SimHub seeded the profile with (its stock
        /// "SimagicReactors" set, which enables every channel) is fully replaced
        /// rather than inherited-where-we-happen-to-have-a-file.
        /// </summary>
        /// <returns>True when anything was applied.</returns>
        public static bool ApplyTo(object container, string containerType)
        {
            if (container == null) return false;

            bool applied = ApplyFile(container, BaselineKey);
            if (!string.IsNullOrEmpty(containerType))
                applied |= ApplyFile(container, containerType);
            return applied;
        }

        private static bool ApplyFile(object container, string key)
        {
            if (!All().TryGetValue(key, out var json)) return false;

            // ContainerId and Description identify the live effect — SimHub's own
            // SaveEffectSettings strips them for exactly this reason. Version is
            // file metadata, not container state.
            var doc = JObject.Parse(json);
            doc.Remove("ContainerId");
            doc.Remove("Description");
            doc.Remove("Version");

            // SettingsStore is applied by hand below, NOT by PopulateObject.
            // AbstractSettingsStore.Settings is a List<>, and Newtonsoft's default
            // ObjectCreationHandling.Auto APPENDS to an existing collection rather
            // than replacing it — so on a container SimHub already seeded, our
            // activation settings landed as a second entry while GetSettings<T>()
            // returns .OfType<T>().FirstOrDefault(), i.e. the stock one. The effect
            // came up disabled (a plain property) but still drove all three motors.
            var store = doc["SettingsStore"];
            doc.Remove("SettingsStore");

            JsonConvert.PopulateObject(doc.ToString(), container);
            ApplyChannelActivation(container, store);
            return true;
        }

        /// <summary>
        /// Write the file's channel activation onto the container's live
        /// <see cref="DeviceChannelActivationSettings"/> — the instance
        /// <c>GetSettings&lt;T&gt;()</c> hands to the tone mixer and the checkbox UI.
        /// Every type on this path is public, so no reflection and no serializer
        /// semantics are involved.
        /// </summary>
        private static void ApplyChannelActivation(object container, JToken? settingsStore)
        {
            try
            {
                var wanted = settingsStore?["Settings"]?
                    .FirstOrDefault(t => (string?)t["TypeName"] == nameof(DeviceChannelActivationSettings))?["Channels"] as JObject;
                if (wanted == null) return;

                if (!(container.GetType().GetProperty("SettingsStore")?.GetValue(container) is AbstractSettingsStore store))
                    return;

                var activation = store.GetSettings<DeviceChannelActivationSettings>();

                foreach (var placementEntry in wanted.Properties())
                {
                    if (!Enum.TryParse<FFBPlacement>(placementEntry.Name, ignoreCase: true, out var placement))
                        continue;
                    if (!(placementEntry.Value["Channels"] is JObject channels))
                        continue;

                    if (!activation.Channels.TryGetValue(placement, out var pca) || pca == null)
                    {
                        pca = new PlacementChannelsActivation();
                        activation.Channels[placement] = pca;
                    }

                    foreach (var channelEntry in channels.Properties())
                    {
                        if (!int.TryParse(channelEntry.Name, out int index)) continue;
                        bool enabled = channelEntry.Value["IsEnabled"]?.Value<bool>() ?? false;

                        if (pca.Channels.TryGetValue(index, out var existing) && existing != null)
                            existing.IsEnabled = enabled;      // in place: the UI binds to this object
                        else
                            pca.Channels[index] = new ChannelActivation { IsEnabled = enabled };
                    }
                }
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[AZOM] Could not apply LFE channel activation defaults: {ex.Message}");
            }
        }

        private static Dictionary<string, string> All()
        {
            if (_loaded) return Cache;
            _loaded = true;
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                foreach (var name in assembly.GetManifestResourceNames())
                {
                    if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal)) continue;
                    if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;

                    using (var stream = assembly.GetManifestResourceStream(name))
                    {
                        if (stream == null) continue;
                        using (var reader = new StreamReader(stream))
                        {
                            var key = name.Substring(ResourcePrefix.Length);
                            key = key.Substring(0, key.Length - ".json".Length);
                            Cache[key] = reader.ReadToEnd();
                        }
                    }
                }
                MozaLog.Debug($"[AZOM] Loaded {Cache.Count} embedded wheelbase LFE effect default(s)");
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[AZOM] Could not load embedded wheelbase LFE effect defaults: {ex.Message}");
            }
            return Cache;
        }
    }
}
