using System;
using System.Collections.Generic;
using MozaPlugin.Devices;
using MozaPlugin.Protocol;
using MozaPlugin.Resources;

namespace MozaPlugin.UI
{
    /// <summary>
    /// Pure function over current plugin state that produces the list of
    /// banners shown under the top bar. Called every 500 ms on the UI
    /// refresh tick; the caller diffs the result so unchanged sets don't
    /// rebuild the visual tree. Severity-sorted (port-locked first,
    /// restart notice second, profile-not-added last).
    /// </summary>
    internal static class StatusHintBuilder
    {
        // Allow at least 3 s of plugin lifetime before surfacing the
        // port-in-use banner. Combined with the >= 2 ConsecutiveOpenFailures
        // debounce, this prevents a single transient open failure during
        // plug-in from flashing a misleading banner.
        private static readonly TimeSpan PortLockedSettling = TimeSpan.FromSeconds(3);

        // Allow at least 5 s before surfacing profile-not-added banners.
        // SimHub instantiates device extensions shortly after the plugin
        // Init; this margin keeps the banners off during the cold-start
        // race between probe responses and extension Init calls.
        private static readonly TimeSpan ProfileNotAddedSettling = TimeSpan.FromSeconds(5);

        public static IReadOnlyList<StatusHint> Build(MozaPlugin plugin, DateTime nowUtc)
        {
            if (plugin == null) return Array.Empty<StatusHint>();

            var list = new List<StatusHint>();
            var elapsed = nowUtc - plugin.StartupUtc;
            var detection = plugin.DetectionState;
            var connection = plugin.Connection;
            var data = plugin.Data;

            // Rule 1: PortLockedByOtherApp
            // Hardware visible in the OS registry AND open keeps failing with
            // access-denied AND we're not connected. The double-failure debounce
            // (>= 2) prevents a single transient open failure from flashing the
            // banner.
            if (elapsed >= PortLockedSettling
                && connection != null
                && !(data?.IsConnected ?? false)
                && connection.ConsecutiveOpenFailures >= 2
                && connection.LastFailure.Kind == ConnectionFailureKind.AccessDenied
                && MozaPortDiscovery.Instance.Enumerate().Count >= 1)
            {
                var port = connection.LastFailure.PortName ?? Strings.Banner_PortFallbackName;
                list.Add(new StatusHint(
                    StatusHintKind.PortLockedByOtherApp,
                    Strings.Banner_PortLocked_Title,
                    string.Format(Strings.Banner_PortLocked_Body, port)));
            }

            // Rule 1b: PortVanished — registry saw the port but Open can't find it
            // (hot-unplug / Wine pty teardown). Distinct remediation from
            // access-denied: replug, not close-another-app.
            if (elapsed >= PortLockedSettling
                && connection != null
                && !(data?.IsConnected ?? false)
                && connection.ConsecutiveOpenFailures >= 2
                && connection.LastFailure.Kind == ConnectionFailureKind.PortVanished)
            {
                var port = connection.LastFailure.PortName ?? Strings.Banner_PortFallbackName;
                list.Add(new StatusHint(
                    StatusHintKind.PortVanished,
                    Strings.Banner_PortVanished_Title,
                    string.Format(Strings.Banner_PortVanished_Body, port)));
            }

            // Rule 1c: TelemetryParked — the recovery ladder exhausted its restart
            // budget (or hit a terminal park) and stopped the telemetry pipeline.
            // Surface WHY (the verbatim park reason) and how to retry.
            var sender = plugin.TelemetrySender;
            if (sender != null && sender.Phase == global::MozaPlugin.Telemetry.PipelinePhase.Parked)
            {
                var rec = sender.Recovery;
                string? reason = rec?.ParkReason;
                if (rec?.ParkIsDegraded ?? false)
                {
                    // Benign degraded state (e.g. screenless wheel) — calm wording,
                    // no "failure", no "toggle to retry" nag.
                    list.Add(new StatusHint(
                        StatusHintKind.TelemetryDegraded,
                        Strings.Banner_TelemetryDegraded_Title,
                        (string.IsNullOrEmpty(reason) ? "" : reason + " ")
                        + Strings.Banner_TelemetryDegraded_Body));
                }
                else
                {
                    list.Add(new StatusHint(
                        StatusHintKind.TelemetryParked,
                        Strings.Banner_TelemetryParked_Title,
                        (string.IsNullOrEmpty(reason) ? "" : reason + " ")
                        + Strings.Banner_TelemetryParked_Body));
                }
            }

            // Rule 2: DeviceDefinitionDeployed (existing behaviour, kept verbatim)
            if (plugin.DeviceDefinitionDeployed)
            {
                list.Add(new StatusHint(
                    StatusHintKind.DeviceDefinitionDeployed,
                    Strings.Banner_RestartSimHub_Title,
                    Strings.Banner_RestartSimHub_Body));
            }

            // Rules 3-5: profile-not-added. Suppressed when DeviceDefinitionDeployed
            // is true so we don't double-up "restart" and "add device" in the
            // same session — the user can't add anything until SimHub restarts.
            bool profileGate = elapsed >= ProfileNotAddedSettling
                            && !plugin.DeviceDefinitionDeployed
                            && (data?.IsConnected ?? false);

            if (profileGate)
            {
                // Rule 3: ProfileNotAddedDash
                if (detection.DashDetected && !plugin.DashDeviceExtensionActive)
                {
                    const string dashDeviceName = "MOZA CM2 Racing Dash";
                    list.Add(new StatusHint(
                        StatusHintKind.ProfileNotAddedDash,
                        string.Format(Strings.Banner_ProfileNotAdded_TitleFmt, dashDeviceName),
                        string.Format(Strings.Banner_ProfileNotAddedDash_Body, dashDeviceName)));
                }

                // Rule 4: ProfileNotAddedBaseAmbient
                if (detection.BaseAmbientLedSupported && !plugin.BaseAmbientDeviceExtensionActive)
                {
                    const string baseDeviceName = "MOZA Wheel Base";
                    list.Add(new StatusHint(
                        StatusHintKind.ProfileNotAddedBaseAmbient,
                        string.Format(Strings.Banner_ProfileNotAdded_TitleFmt, baseDeviceName),
                        string.Format(Strings.Banner_ProfileNotAddedBaseAmbient_Body, baseDeviceName)));
                }

                // Rule 5: ProfileNotAddedWheel
                // Old-protocol wheels share a single template ("MOZA Old Protocol Wheel"),
                // so model-prefix lookup is bypassed in that path; we fall back to the
                // global DeviceExtensionActive flag, which the old-proto extension also
                // toggles when its Init fires.
                bool wheelDetected = detection.NewWheelDetected || detection.OldWheelDetected;
                var model = detection.LastKnownWheelModel;
                // Reaching here with a resolved model means a model-specific
                // "MOZA <model>" device was deployed — for new-protocol wheels AND
                // for an identified old wheel like ES (id 0x18). Use the
                // model-specific extension + friendly name in both cases. A generic
                // model-less old wheel never gets here (its model is empty) and is
                // covered by the shared old-proto device's own status path.
                if (wheelDetected && !string.IsNullOrEmpty(model))
                {
                    bool active = plugin.IsModelSpecificExtensionActive(model);
                    var prefix = WheelModelInfo.ExtractPrefix(model);
                    string friendly = WheelModelInfo.GetFriendlyName(prefix);

                    if (!active)
                    {
                        var wheelDeviceName = "MOZA " + friendly;
                        list.Add(new StatusHint(
                            StatusHintKind.ProfileNotAddedWheel,
                            string.Format(Strings.Banner_ProfileNotAdded_TitleFmt, wheelDeviceName),
                            string.Format(Strings.Banner_ProfileNotAddedWheel_Body, wheelDeviceName, model),
                            relatedModel: model));
                    }
                }
            }

            return list;
        }
    }
}
