using System.Collections.Generic;
using MozaPlugin.Telemetry.Dashboard;

namespace MozaPlugin.UI.DjsonImport
{
    /// <summary>
    /// Adapts the plugin's channel catalog to the conversion pipeline.
    ///
    /// <para>This is the <b>only</b> file in <c>UI/DjsonImport</c> that touches plugin
    /// internals, which is what keeps everything else runnable from a plain console host
    /// over the whole SimHub template corpus.</para>
    /// </summary>
    public static class PluginChannelCatalog
    {
        /// <summary>Every channel Telemetry.json declares, with its pristine default
        /// binding (never a per-dashboard or master-mapper override — the converter must
        /// resolve against the shipped catalog, not one user's remapping).</summary>
        public static IEnumerable<ChannelRow> Rows(DashboardProfileStore store)
        {
            foreach (var entry in store.EnumerateTelemetryChannels())
                yield return new ChannelRow(entry.Url, entry.DefaultProperty);
        }

        /// <summary>A converter wired to the plugin's catalog.</summary>
        public static DjsonConverter CreateConverter(DashboardProfileStore? store = null)
            => new DjsonConverter(new ChannelResolver(Rows(store ?? new DashboardProfileStore())));
    }
}
