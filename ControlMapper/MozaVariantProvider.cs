using System;
using MozaPlugin.Devices;
using MozaPlugin.Protocol;
using SimHub.Plugins.OutputPlugins.ControlRemapper.Variants;

namespace MozaPlugin.ControlMapper
{
    /// <summary>
    /// Reports the currently-attached MOZA wheel model as an
    /// <see cref="IVariantProvider"/> variant string, so SimHub's Control
    /// Mapper can key per-wheel button mappings off
    /// <c>(VID, PID, Variant)</c> instead of <c>(VID, PID)</c> alone.
    ///
    /// Every MOZA wheel connects through one wheelbase USB endpoint, so
    /// Windows / DirectInput see them all as the same controller — without
    /// this provider, swapping from (say) CS Pro to KS Pro would have the
    /// new wheel inherit the previous wheel's button mappings. This is the
    /// same problem Fanatec and Simucube solve via their own bundled
    /// providers in <c>SimHub.Plugins.dll</c>.
    ///
    /// Mirrors the Fanatec / Simucube provider convention: implements
    /// <see cref="IVariantProvider.GetVariant"/> and exposes a public
    /// <c>VariantChanged</c> event of type <see cref="EventHandler"/> that
    /// <c>VariantHelper</c> subscribes to (via reflection by name) inside
    /// <c>RemapperWorker.UpdateVariantProviders</c>. Firing the event on
    /// wheel hot-swap is what makes Control Mapper re-enumerate controllers
    /// without the user having to manually rescan.
    /// </summary>
    public class MozaVariantProvider : IVariantProvider
    {
        /// <summary>
        /// Fired by <see cref="Poll"/> when the resolved variant string
        /// changes (wheel attached, detached, or hot-swapped). SimHub's
        /// <c>VariantHelper</c> subscribes to this event by reflecting on
        /// the field name.
        /// </summary>
        public event EventHandler? VariantChanged;

        private string? _lastVariant;

        public string? GetVariant(int vendorid, int productid)
        {
            if (vendorid != MozaProtocol.VendorId) return null;
            ushort pid = unchecked((ushort)productid);
            if (!MozaUsbIds.IsWheelbasePid(pid) && !MozaUsbIds.IsHubPid(pid))
                return null;
            return ComputeCurrentVariant();
        }

        /// <summary>
        /// Canonical wheel-variant resolution from live plugin state, shared by
        /// <see cref="GetVariant"/>, <see cref="Poll"/>, and
        /// <see cref="ControlMapperBridge"/>'s auto-create / detach paths.
        /// </summary>
        internal static string? ComputeCurrentVariant()
        {
            var plugin = MozaPlugin.Instance;
            if (plugin == null) return null;
            // ES/ESX (old-protocol) wheels are base-proxied at dev 0x13 and report
            // the wheelbase's identity, not their own — no serial way to read the
            // wheel's model. Treat any old-protocol wheel as the "ES" variant
            // (a fixed identity key, not localized — same as the friendly names).
            if (plugin.IsOldWheelDetected) return "ES";
            string model = plugin.Data?.WheelModelName ?? string.Empty;
            if (string.IsNullOrEmpty(model)) return null;
            string prefix = WheelModelInfo.ExtractPrefix(model);
            if (string.IsNullOrEmpty(prefix)) return null;
            return WheelModelInfo.GetFriendlyName(prefix);
        }

        /// <summary>
        /// Compare the freshly-resolved variant against the cached value and
        /// fire <see cref="VariantChanged"/> when they differ. Called once
        /// per <see cref="MozaPlugin.DataUpdate"/> tick via
        /// <see cref="ControlMapperBridge.Poll"/>.
        /// </summary>
        internal void Poll()
        {
            string? current = ComputeCurrentVariant();
            if (current == _lastVariant) return;

            string before = _lastVariant ?? "<none>";
            string after = current ?? "<none>";
            _lastVariant = current;

            MozaLog.Debug(
                $"[AZOM] ControlMapper variant changed: '{before}' -> '{after}'");

            try { VariantChanged?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex)
            {
                MozaLog.Warn(
                    $"[AZOM] ControlMapper VariantChanged subscriber threw: {ex.Message}");
            }
        }
    }
}
