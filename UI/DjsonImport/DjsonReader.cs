using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MozaPlugin.UI.DjsonImport
{
    /// <summary>
    /// Reads a SimHub <c>.djson</c> dashboard as a JSON tree.
    ///
    /// <para>Deliberately <see cref="JObject"/>-based rather than typed: the file uses
    /// Newtonsoft <c>$type</c> polymorphism over ~63 SimHub types whose shapes vary
    /// wildly, and only a subset of each item's properties is meaningful here.
    /// <c>TypeNameHandling</c> stays <b>off</b> — the <c>$type</c> strings name SimHub
    /// assembly types and must never be bound, only inspected as strings.</para>
    /// </summary>
    public static class DjsonReader
    {
        /// <summary>Load and parse. Returns a null root plus a message on failure.</summary>
        public static (JObject? root, string error) Load(string path)
        {
            try
            {
                // utf-8 with BOM detection: SimHub writes both.
                using var reader = new StreamReader(path, System.Text.Encoding.UTF8, true);
                using var json = new JsonTextReader(reader);
                var root = JObject.Load(json);
                return (root, "");
            }
            catch (Exception ex)
            {
                return (null, ex.Message);
            }
        }

        /// <summary>
        /// Enumerate a collection property, tolerating Newtonsoft's reference-preserving
        /// wrapper.
        ///
        /// <para><b>Load-bearing.</b> SimHub sometimes serialises a lazily-evaluated LINQ
        /// query where a concrete list was expected, which Newtonsoft writes as
        /// <c>{"$id":"…","$values":[…]}</c> with a
        /// <c>System.Linq.Enumerable+WhereEnumerableIterator</c> <c>$type</c>. Treating
        /// that object as "not an array" silently loses every screen in the file
        /// (e.g. <c>AIM GS-DASH/AIM PB.djson</c>).</para>
        /// </summary>
        public static IEnumerable<JObject> Items(JToken? token)
        {
            if (token == null) yield break;

            if (token is JObject wrapper)
            {
                token = wrapper["$values"];
                if (token == null) yield break;
            }

            if (token is not JArray arr) yield break;

            foreach (var child in arr)
                if (child is JObject obj)
                    yield return obj;
        }

        /// <summary>The discriminating part of an item's <c>$type</c> — everything after
        /// <c>GraphicalDash.</c> and before the assembly name, e.g. <c>Models.TextItem</c>,
        /// <c>Models.BuiltIn.GearText</c>, <c>Behaviors.Gauges.RPM</c>. Empty when the
        /// item carries no recognisable <c>$type</c>.</summary>
        public static string TypeName(JObject? item)
        {
            string raw = (string?)item?["$type"] ?? "";
            if (raw.Length == 0) return "";

            int comma = raw.IndexOf(',');
            string full = comma >= 0 ? raw.Substring(0, comma) : raw;

            const string marker = "GraphicalDash.";
            int at = full.IndexOf(marker, StringComparison.Ordinal);
            return at >= 0 ? full.Substring(at + marker.Length) : full;
        }

        /// <summary>The leaf of <see cref="TypeName"/> — <c>TextItem</c>, <c>GearText</c>.</summary>
        public static string ShortTypeName(JObject? item)
        {
            string t = TypeName(item);
            int dot = t.LastIndexOf('.');
            return dot >= 0 ? t.Substring(dot + 1) : t;
        }

        // ── Typed accessors ───────────────────────────────────────────────────
        // Invariant culture throughout: .djson is machine-written with '.' decimals
        // regardless of the authoring machine's locale.

        public static double Num(JObject? o, string name, double fallback = 0)
        {
            var t = o?[name];
            if (t == null || t.Type == JTokenType.Null) return fallback;
            if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float)
                return (double)t;
            return double.TryParse((string?)t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                ? d : fallback;
        }

        public static int Int(JObject? o, string name, int fallback = 0)
            => (int)Math.Round(Num(o, name, fallback));

        /// <summary>Read a string property. A property that holds an object or array —
        /// SimHub stores some colours as a serialised WPF brush rather than a hex string —
        /// yields the fallback instead of throwing; use <see cref="Obj"/> to inspect those.</summary>
        public static string Str(JObject? o, string name, string fallback = "")
        {
            var t = o?[name];
            if (t == null || t.Type == JTokenType.Null) return fallback;
            if (t is not JValue) return fallback;
            return (string?)t ?? fallback;
        }

        public static bool Bool(JObject? o, string name, bool fallback = false)
        {
            var t = o?[name];
            if (t == null || t.Type == JTokenType.Null) return fallback;
            if (t.Type == JTokenType.Boolean) return (bool)t;
            return bool.TryParse((string?)t, out var b) ? b : fallback;
        }

        public static JObject? Obj(JObject? o, string name) => o?[name] as JObject;

        /// <summary>True when the property is present and not JSON null.</summary>
        public static bool Has(JObject? o, string name)
        {
            var t = o?[name];
            return t != null && t.Type != JTokenType.Null;
        }
    }
}
