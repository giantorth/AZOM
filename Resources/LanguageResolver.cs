using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace MozaPlugin.Resources
{
    /// <summary>
    /// Resolves the effective UI culture for the plugin.
    ///
    /// Precedence (highest to lowest):
    ///  1. <c>preferred</c> argument (the user's explicit pick from the
    ///     in-plugin language ComboBox). Null/empty/whitespace/"auto" means
    ///     "no override; use the auto chain below".
    ///  2. SimHub's own UI language, read from
    ///     <c>&lt;install&gt;/PluginsData/GlobalSimhubSettings.json</c>
    ///     (<c>Culture</c> field). Users expect a language pick in SimHub
    ///     Settings to apply to plugin panes too.
    ///  3. The OS UI culture (<see cref="CultureInfo.CurrentUICulture"/>).
    ///  4. English as final fallback.
    ///
    /// Each candidate culture walks its parent chain looking for a match in
    /// <see cref="SupportedCultures"/>, so "es-MX" → "es" and "fr-CA" → "fr".
    /// A candidate that doesn't resolve to anything supported falls through to
    /// the next step rather than locking the plugin into an unsupported
    /// language.
    ///
    /// Called once from <c>MozaPlugin.Init</c> and again from
    /// <c>MozaPlugin.GetWPFSettingsControl</c> (because the WPF UI thread
    /// predates plugin Init and would otherwise keep its own
    /// <c>CurrentUICulture</c>).
    /// </summary>
    internal static class LanguageResolver
    {
        // Locales we ship a Strings.<lang>.resx satellite for. The neutral
        // ("en") resources live in the main assembly; the rest are satellites
        // under <install>/<culture>/MozaPlugin.resources.dll. Adding a new
        // language means: drop Strings.<lang>.resx in Resources/, append the
        // culture code here, add an EmbeddedResource entry in MozaPlugin.csproj,
        // and add a DisplayNames entry below.

        public static readonly IReadOnlyList<string> SupportedCultures = new[] { "en", "de", "es", "fr", "ru", "vi", "zh-Hans" };

        // Names shown in the in-plugin language ComboBox. Each language is
        // named in its own tongue so a user who can't read the current UI can
        // still recognise their option.
        public static readonly IReadOnlyDictionary<string, string> DisplayNames =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "en", "English" },
                { "de", "Deutsch" },
                { "es", "Español" },
                { "fr", "Français" },
                { "ru", "Русский" },
                { "vi", "Tiếng Việt" },
                { "zh-Hans", "简体中文" },
            };

        /// <summary>
        /// Resolves the culture, given an optional user-picked preference.
        /// </summary>
        /// <param name="preferred">
        /// User's explicit choice from the in-plugin picker (BCP-47 like "es"
        /// or "fr"). Null, empty, whitespace, or "auto" disables the override
        /// and falls back to the auto chain (SimHub → OS → en).
        /// </param>
        public static CultureInfo Resolve(string? preferred = null)
        {
            // 1) Explicit user pick.
            if (!string.IsNullOrWhiteSpace(preferred)
                && !string.Equals(preferred, "auto", StringComparison.OrdinalIgnoreCase))
            {
                var picked = WalkChainForSupported(preferred!);
                if (picked != null) return picked;
                // If the user saved an unknown/unsupported value (e.g. they
                // tested a code we later removed), don't lock them out — fall
                // through to the auto chain.
            }

            // 2) SimHub's UI language.
            var simhubCulture = TryReadSimHubCulture();
            if (!string.IsNullOrWhiteSpace(simhubCulture))
            {
                var matched = WalkChainForSupported(simhubCulture!);
                if (matched != null) return matched;
            }

            // 3) OS UI culture.
            var osMatched = WalkChainForSupported(CultureInfo.CurrentUICulture.Name);
            if (osMatched != null) return osMatched;

            // 4) English.
            return new CultureInfo("en");
        }

        /// <summary>
        /// Reads the <c>Culture</c> field out of SimHub's global settings JSON.
        /// Returns null if the file is missing, unreadable, or the field is absent.
        /// </summary>
        private static string? TryReadSimHubCulture()
        {
            try
            {
                // Plugin DLLs load from the SimHub install dir, so
                // AppDomain.BaseDirectory points at it. The settings file lives
                // under PluginsData/.
                var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                                        "PluginsData", "GlobalSimhubSettings.json");
                if (!File.Exists(path)) return null;
                var json = File.ReadAllText(path);
                // Lightweight extraction so we don't drag Newtonsoft into a path
                // that runs before the rest of plugin initialisation.
                var m = Regex.Match(json, "\"Culture\"\\s*:\\s*\"([^\"]+)\"");
                return m.Success ? m.Groups[1].Value : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Walks the parent chain of <paramref name="name"/> looking for a
        /// match against <see cref="SupportedCultures"/>. So "es-MX" resolves
        /// to "es"; "ja-JP" returns null (we don't ship Japanese).
        /// </summary>
        private static CultureInfo? WalkChainForSupported(string name)
        {
            CultureInfo? c;
            try { c = new CultureInfo(name); }
            catch (CultureNotFoundException) { return null; }

            while (c != null && !string.IsNullOrEmpty(c.Name))
            {
                if (SupportedCultures.Any(s => string.Equals(s, c.Name, StringComparison.OrdinalIgnoreCase)))
                    return new CultureInfo(c.Name);
                if (c.Equals(CultureInfo.InvariantCulture)) break;
                c = c.Parent;
            }
            return null;
        }
    }
}
