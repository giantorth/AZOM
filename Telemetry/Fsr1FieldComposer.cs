using System.Collections.Generic;

namespace MozaPlugin.Telemetry
{
    /// <summary>
    /// Single merge point for the FSR1 field set: the static catalog fields plus any
    /// per-profile synthetic split fields (<see cref="Fsr1SyntheticField"/>). The driver,
    /// emitter, channel-mapping UI, field probe, and viz all enumerate fields through here
    /// so they agree on what exists. When the synthetic dict is empty for a record, the
    /// catalog field array is returned unchanged (byte-for-byte unchanged behaviour).
    /// </summary>
    internal static class Fsr1FieldComposer
    {
        /// <summary>
        /// The effective field list for <paramref name="dash"/>: catalog fields followed by
        /// one synthesised <see cref="Fsr1FieldDef"/> per stored synthetic field. Order does
        /// not matter — every consumer sorts by Start.
        /// </summary>
        internal static IReadOnlyList<Fsr1FieldDef> FieldsFor(MozaPlugin? plugin, Fsr1Dashboard dash)
        {
            if (dash == null) return System.Array.Empty<Fsr1FieldDef>();
            var synth = plugin?.GetSyntheticFields(dash.Key);
            if (synth == null || synth.Count == 0)
                return dash.Fields;

            var list = new List<Fsr1FieldDef>(dash.Fields.Length + synth.Count);
            list.AddRange(dash.Fields);
            foreach (var s in synth)
            {
                if (s == null) continue;
                list.Add(Synthesise(s));
            }
            return list;
        }

        /// <summary>
        /// Resolve a single field by id within a record: catalog first, then synthetic.
        /// Returns null when neither has it.
        /// </summary>
        internal static Fsr1FieldDef? FindField(MozaPlugin? plugin, string recordKey, string fieldId)
        {
            if (string.IsNullOrEmpty(recordKey) || string.IsNullOrEmpty(fieldId)) return null;

            var dash = Fsr1DashboardCatalog.ByKey(recordKey);
            if (dash != null)
                foreach (var f in dash.Fields)
                    if (f.FieldId == fieldId) return f;

            var synth = plugin?.GetSyntheticFields(recordKey);
            if (synth != null)
                foreach (var s in synth)
                    if (s != null && s.FieldId == fieldId) return Synthesise(s);

            return null;
        }

        /// <summary>True when <paramref name="fieldId"/> names a synthetic field on the record.</summary>
        internal static bool IsSynthetic(MozaPlugin? plugin, string recordKey, string fieldId)
        {
            var synth = plugin?.GetSyntheticFields(recordKey);
            if (synth == null) return false;
            foreach (var s in synth)
                if (s != null && s.FieldId == fieldId) return true;
            return false;
        }

        /// <summary>
        /// Build a catalog-shaped <see cref="Fsr1FieldDef"/> from a stored synthetic field.
        /// Offsets/encoding mirror its explicit mapping span; the value path is Direct (the
        /// span carries the byte width, Scale/Bias carry precision). The actual wire layout
        /// is still resolved through <see cref="Fsr1DashboardCatalog.ResolveLayout"/> against
        /// the inline mapping, so these defaults only seed the span.
        /// </summary>
        private static Fsr1FieldDef Synthesise(Fsr1SyntheticField s)
        {
            var m = s.Mapping ?? new Fsr1FieldMapping();
            int start = m.StartOffset ?? 5;
            int end = m.EndOffset ?? start;
            if (end < start) end = start;
            int width = end - start + 1;
            if (width > 3) width = 3;

            var offsets = new int[width];
            for (int i = 0; i < width; i++) offsets[i] = start + i;

            Fsr1Encoding enc = width switch
            {
                1 => Fsr1Encoding.U8,
                2 => (m.LittleEndian ?? false) ? Fsr1Encoding.U16_LE : Fsr1Encoding.U16_BE,
                _ => Fsr1Encoding.U24_BE,
            };

            return new Fsr1FieldDef
            {
                FieldId = s.FieldId,
                Label = string.IsNullOrEmpty(s.Label) ? s.FieldId : s.Label,
                Offsets = offsets,
                Encoding = enc,
                Kind = Fsr1FieldKind.Direct,
                DefaultProperty = m.Property ?? "",
                Decoded = true,
                FullScale = 0,
            };
        }
    }
}
