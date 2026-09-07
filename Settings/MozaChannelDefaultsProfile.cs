using System;
using System.Collections.Generic;
using System.Windows.Controls;
using SimHub.Plugins.ProfilesCommon;

namespace MozaPlugin.Settings
{
    /// <summary>
    /// One named set of master channel defaults — the per-channel SimHub-property
    /// mapping the master channel mapper edits.
    ///
    /// <para>Rides SimHub's profile system (<see cref="MozaChannelDefaultsStore"/>) but
    /// is a SEPARATE store from <see cref="MozaProfile"/>: these profiles are created,
    /// named, switched and exported entirely from the master mapper's own selector and
    /// have nothing to do with the device profiles on the Options tab. Two independent
    /// lists rather than one shared one, so a user can pair any channel-default set
    /// with any device profile without the two having to line up.</para>
    ///
    /// <para>Resolution order for a channel URL: per-dashboard override
    /// (<see cref="MozaProfile.TelemetryChannelMappings"/>) → THIS → Telemetry.json's
    /// <c>simhub_property</c> → <c>StringChannelDefaults</c>.</para>
    /// </summary>
    public class MozaChannelDefaultsProfile
        : ProfileBase<MozaChannelDefaultsProfile, MozaChannelDefaultsStore>,
          IProfile, IProfile<MozaChannelDefaultsProfile, MozaChannelDefaultsStore>
    {
        // No inline editor — the master mapper dialog IS this profile's editor.
        public override Control ProfileContentControl => null!;

        /// <summary>
        /// Channel URL (e.g. <c>"v1/gameData/Rpm"</c>) → SimHub property path or
        /// formula. Absent URL = that channel keeps its Telemetry.json default; an
        /// entry replaces the property AND forces the channel's scale to 1 (see
        /// <c>DashboardProfileStore.ResolveDefaultBinding</c>).
        ///
        /// <para>Newtonsoft replaces this instance on load and drops the comparer, so
        /// every reader re-normalises to OrdinalIgnoreCase rather than indexing it
        /// directly — <c>DashboardProfileStore.SetDefaultOverrides</c> for the runtime
        /// snapshot, <c>MasterChannelMapperDialog.BuildRows</c> for the UI.</para>
        /// </summary>
        public Dictionary<string, string> Mappings { get; set; }
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Deep copy — SimHub's Clone goes through here, and a shared dict
        /// reference would make the clone edit its source.</summary>
        public override void CopyProfilePropertiesFrom(MozaChannelDefaultsProfile p)
        {
            if (p == null) return;
            Mappings = p.Mappings != null
                ? new Dictionary<string, string>(p.Mappings, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
