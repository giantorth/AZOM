using System;
using System.Collections.Generic;

namespace MozaPlugin.Sdk
{
    /// <summary>
    /// Maps CoAP Uri-Path values to <see cref="CoapResourceHandler"/> instances.
    /// Two URI shapes are supported:
    /// <list type="bullet">
    ///   <item><description>Literal — <c>/MOZARacing/ProductDevice</c>, <c>/MOZARacing/SdkState</c>. Match by exact string.</description></item>
    ///   <item><description>Templated — <c>/MOZARacing/ProductDevice/{id}/FfbStrength</c>. The <c>{id}</c> placeholder is matched against
    ///     the live <see cref="DeviceCatalog"/> at lookup time; unknown IDs fail to resolve so observable garbage URIs from a
    ///     bad client surface as 4.04 rather than reaching a handler.</description></item>
    /// </list>
    /// Per-property suffixes after <c>{id}</c> are extracted as the
    /// <c>propertyName</c> out parameter — concrete handlers do not need to
    /// re-parse the URI. Thread-safe: all mutators and lookups serialize on a
    /// single lock so resource bindings can be added after the server is
    /// running (e.g. when a wheel reconnects and a new device-scoped handler
    /// must come online).
    /// </summary>
    public sealed class CoapResourceRegistry
    {
        /// <summary>
        /// Reserved placeholder string used in templated URI suffixes. A URI
        /// segment exactly equal to this is treated as the device-ID slot.
        /// </summary>
        public const string IdPlaceholder = "{id}";

        private readonly object _gate = new object();
        private readonly DeviceCatalog _catalog;

        // We index by the schema string the caller registered (e.g.
        // "/MOZARacing/ProductDevice/{id}/FfbStrength"). Resolve walks the
        // segments of the incoming URI against each registered schema in
        // insertion order (deterministic for diagnostics).
        private readonly List<(string Schema, string[] Segments, CoapResourceHandler Handler)> _bindings
            = new List<(string, string[], CoapResourceHandler)>();

        public CoapResourceRegistry(DeviceCatalog catalog)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        }

        /// <summary>
        /// Register <paramref name="handler"/> for the URI <paramref name="uriSuffix"/>.
        /// The suffix MUST begin with a slash and may contain at most one
        /// <c>{id}</c> placeholder segment. Re-binding an existing schema
        /// replaces the handler (allows hot-swap as devices come and go).
        /// </summary>
        public void Bind(string uriSuffix, CoapResourceHandler handler)
        {
            if (uriSuffix == null) throw new ArgumentNullException(nameof(uriSuffix));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            if (!uriSuffix.StartsWith("/", StringComparison.Ordinal))
                throw new ArgumentException("uriSuffix must begin with '/'.", nameof(uriSuffix));

            var segments = SplitSegments(uriSuffix);
            int placeholderCount = 0;
            for (int i = 0; i < segments.Length; i++)
            {
                if (string.Equals(segments[i], IdPlaceholder, StringComparison.Ordinal))
                    placeholderCount++;
            }
            if (placeholderCount > 1)
                throw new ArgumentException(
                    $"uriSuffix '{uriSuffix}' contains more than one {IdPlaceholder} placeholder; only one is supported.",
                    nameof(uriSuffix));

            lock (_gate)
            {
                for (int i = 0; i < _bindings.Count; i++)
                {
                    if (string.Equals(_bindings[i].Schema, uriSuffix, StringComparison.Ordinal))
                    {
                        _bindings[i] = (uriSuffix, segments, handler);
                        return;
                    }
                }
                _bindings.Add((uriSuffix, segments, handler));
            }
        }

        /// <summary>
        /// Resolve <paramref name="fullUri"/> against the registered schemas.
        /// Returns the matching handler with the extracted device-ID slot
        /// (when the schema contained one) and the property-name suffix (the
        /// segment immediately following <c>{id}</c>, if any).
        /// </summary>
        /// <returns>
        /// The handler if a schema matched AND any required device-ID is in
        /// the catalog; <c>null</c> otherwise.
        /// </returns>
        public CoapResourceHandler? Resolve(string fullUri, out string? deviceId, out string? propertyName)
        {
            deviceId = null;
            propertyName = null;
            if (string.IsNullOrEmpty(fullUri)) return null;
            if (!fullUri.StartsWith("/", StringComparison.Ordinal)) return null;

            var requestSegments = SplitSegments(fullUri);

            // Snapshot under the lock so we can match without holding it
            // (handlers below may want to call back into other methods on
            // this registry without re-entering the gate). The list is
            // small (≤ dozens of entries) so the copy is cheap.
            (string Schema, string[] Segments, CoapResourceHandler Handler)[] snapshot;
            lock (_gate)
            {
                snapshot = _bindings.ToArray();
            }

            for (int b = 0; b < snapshot.Length; b++)
            {
                var entry = snapshot[b];
                if (entry.Segments.Length != requestSegments.Length) continue;

                string? candidateId = null;
                bool matched = true;
                for (int i = 0; i < entry.Segments.Length; i++)
                {
                    if (string.Equals(entry.Segments[i], IdPlaceholder, StringComparison.Ordinal))
                    {
                        candidateId = requestSegments[i];
                    }
                    else if (!string.Equals(entry.Segments[i], requestSegments[i], StringComparison.Ordinal))
                    {
                        matched = false;
                        break;
                    }
                }
                if (!matched) continue;

                // Validate the candidate ID is in the catalogue. Unknown IDs
                // surface as 4.04 from the listener rather than reaching a
                // handler that would have to re-validate.
                if (candidateId != null && !CatalogContainsId(candidateId))
                    continue;

                deviceId = candidateId;
                propertyName = ExtractPropertyName(entry.Segments, requestSegments);
                return entry.Handler;
            }
            return null;
        }

        /// <summary>Enumerate currently-bound URI schemas for diagnostics.</summary>
        public IEnumerable<string> KnownUriSuffixes
        {
            get
            {
                lock (_gate)
                {
                    var copy = new string[_bindings.Count];
                    for (int i = 0; i < _bindings.Count; i++)
                        copy[i] = _bindings[i].Schema;
                    return copy;
                }
            }
        }

        /// <summary>Number of bindings currently registered. Diagnostic only.</summary>
        public int Count
        {
            get { lock (_gate) return _bindings.Count; }
        }

        // ----- helpers -----

        /// <summary>
        /// Split a URI on '/' into non-empty segments. Leading/trailing
        /// slashes and consecutive slashes are tolerated. Used both at bind
        /// time (against the schema) and at resolve time (against the
        /// incoming Uri-Path).
        /// </summary>
        internal static string[] SplitSegments(string uri)
        {
            // Strip a single leading slash, then split. Empty segments are
            // dropped so trailing slashes don't break matching.
            string trimmed = uri;
            if (trimmed.StartsWith("/", StringComparison.Ordinal))
                trimmed = trimmed.Substring(1);
            if (trimmed.Length == 0) return Array.Empty<string>();
            var parts = trimmed.Split('/');
            var list = new List<string>(parts.Length);
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length > 0) list.Add(parts[i]);
            }
            return list.ToArray();
        }

        /// <summary>
        /// Extract the property-name slot — the segment that follows the
        /// <c>{id}</c> placeholder. For schemas without a placeholder, returns
        /// the LAST segment (so <c>/MOZARacing/SdkState</c> reports
        /// propertyName="SdkState"). For schemas where the placeholder is the
        /// trailing segment (the manifest URI), returns null.
        /// </summary>
        private static string? ExtractPropertyName(string[] schemaSegments, string[] requestSegments)
        {
            for (int i = 0; i < schemaSegments.Length; i++)
            {
                if (string.Equals(schemaSegments[i], IdPlaceholder, StringComparison.Ordinal))
                {
                    int next = i + 1;
                    if (next >= requestSegments.Length) return null;
                    return requestSegments[next];
                }
            }
            if (requestSegments.Length == 0) return null;
            return requestSegments[requestSegments.Length - 1];
        }

        private bool CatalogContainsId(string id)
        {
            // DeviceCatalog returns a freshly-built list; iterate case-
            // insensitively because manifest IDs are lowercase but a caller
            // could send mixed case.
            var ids = _catalog.EnumerateDeviceIds();
            for (int i = 0; i < ids.Count; i++)
            {
                if (string.Equals(ids[i], id, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
