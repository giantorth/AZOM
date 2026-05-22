using System;
using System.Collections.Generic;

namespace MozaPlugin.UI
{
    /// <summary>
    /// Discriminator for the user-facing footgun banners surfaced under the
    /// top bar of the plugin settings panel. One <see cref="StatusHint"/>
    /// instance per active condition; multiple banners can stack.
    /// </summary>
    internal enum StatusHintKind
    {
        // Registry sees a MOZA device but every Open returns access-denied
        // (PitHouse or another app holds the port).
        PortLockedByOtherApp,
        // A fresh device.json was deployed this session; SimHub must restart
        // before it picks the file up.
        DeviceDefinitionDeployed,
        // Dashboard hardware detected over the wire, but "MOZA Dashboard"
        // hasn't been added under SimHub > Devices.
        ProfileNotAddedDash,
        // Wheelbase has an ambient LED strip but "MOZA Wheel Base" hasn't
        // been added under SimHub > Devices.
        ProfileNotAddedBaseAmbient,
        // A wheel was detected (new or old protocol) and its corresponding
        // model-specific device extension is not active in SimHub.
        ProfileNotAddedWheel,
    }

    /// <summary>
    /// View model for a single status banner. Pure data; equality is by all
    /// fields so the UI refresh tick can short-circuit unchanged renders.
    /// </summary>
    internal sealed class StatusHint : IEquatable<StatusHint>
    {
        public StatusHintKind Kind { get; }
        public string Title { get; }
        public string Body { get; }
        public string? RelatedModel { get; }

        public StatusHint(StatusHintKind kind, string title, string body, string? relatedModel = null)
        {
            Kind = kind;
            Title = title ?? string.Empty;
            Body = body ?? string.Empty;
            RelatedModel = relatedModel;
        }

        public bool Equals(StatusHint? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return Kind == other.Kind
                && string.Equals(Title, other.Title, StringComparison.Ordinal)
                && string.Equals(Body, other.Body, StringComparison.Ordinal)
                && string.Equals(RelatedModel, other.RelatedModel, StringComparison.Ordinal);
        }

        public override bool Equals(object? obj) => obj is StatusHint h && Equals(h);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = (int)Kind;
                hash = (hash * 397) ^ (Title?.GetHashCode() ?? 0);
                hash = (hash * 397) ^ (Body?.GetHashCode() ?? 0);
                hash = (hash * 397) ^ (RelatedModel?.GetHashCode() ?? 0);
                return hash;
            }
        }

        public static bool ListEquals(IReadOnlyList<StatusHint>? a, IReadOnlyList<StatusHint>? b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a is null || b is null) return false;
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (!a[i].Equals(b[i])) return false;
            }
            return true;
        }
    }
}
