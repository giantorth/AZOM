namespace MozaPlugin.Devices.Extensions
{
    /// <summary>
    /// Device-definition deployment for the FCS farming panel: six button LEDs on
    /// the generated wheel layout (no telemetry run, no knobs).
    /// </summary>
    internal static partial class DeviceDefinitionDeployer
    {
        internal const string FcsDeviceName = "MOZA Farming Control System";
        internal const int FcsButtonLedCount = 6;
        // Embedded art under DeviceTemplates/Thumbnails; EnsureThumbnail skips a missing key.
        private const string FcsThumbnailKey = "FCS";

        /// <summary>Write (or refresh) the FCS definition once the panel is detected.
        /// The PID is the FCS's own (it is its own USB device).</summary>
        public static bool DeployForFcs(string? discoveredPid, bool force = false)
            => DeployGeneratedWheelDefinition(
                FcsDeviceName, MozaDeviceConstants.FcsGuid, "Farming Control System",
                rpmCount: 0, hasFlagLeds: false, buttonCount: FcsButtonLedCount, knobCount: 0,
                browSegmentSize: 0, discoveredPid ?? "0x0030", FcsThumbnailKey, force);

        /// <summary>Bulk-redeploy path; only with an FCS attached.</summary>
        private static (int Written, int Total) RedeployAllFcs()
        {
            var plugin = MozaPlugin.Instance;
            if (plugin == null || !plugin.IsFcsDetected) return (0, 0);
            return (DeployForFcs("0x0030", force: true) ? 1 : 0, 1);
        }
    }
}
