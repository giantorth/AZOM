using System;
using System.Collections.Generic;
using MozaPlugin.Devices;
using MozaPlugin.Protocol;

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
                var port = connection.LastFailure.PortName ?? "the MOZA COM port";
                list.Add(new StatusHint(
                    StatusHintKind.PortLockedByOtherApp,
                    "MOZA hardware detected but the port is in use",
                    $"Found a MOZA device on {port}, but the port is held by another application. " +
                    $"Close MOZA PitHouse (or anything else talking to the wheelbase) and click Refresh."));
            }

            // Rule 2: DeviceDefinitionDeployed (existing behaviour, kept verbatim)
            if (plugin.DeviceDefinitionDeployed)
            {
                list.Add(new StatusHint(
                    StatusHintKind.DeviceDefinitionDeployed,
                    "Restart SimHub",
                    "New device definitions were deployed. Restart SimHub to add them under Devices."));
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
                    list.Add(new StatusHint(
                        StatusHintKind.ProfileNotAddedDash,
                        "MOZA Dashboard not added in SimHub",
                        "A MOZA dashboard is connected, but the device hasn't been added in SimHub. " +
                        "Open Devices > Add device > MOZA Dashboard."));
                }

                // Rule 4: ProfileNotAddedBaseAmbient
                if (detection.BaseAmbientLedSupported && !plugin.BaseAmbientDeviceExtensionActive)
                {
                    list.Add(new StatusHint(
                        StatusHintKind.ProfileNotAddedBaseAmbient,
                        "MOZA Wheel Base not added in SimHub",
                        "This wheelbase has an ambient LED strip, but 'MOZA Wheel Base' hasn't been added " +
                        "in SimHub. Open Devices > Add device > MOZA Wheel Base to drive the strip."));
                }

                // Rule 5: ProfileNotAddedWheel
                // Old-protocol wheels share a single template ("MOZA Old Protocol Wheel"),
                // so model-prefix lookup is bypassed in that path; we fall back to the
                // global DeviceExtensionActive flag, which the old-proto extension also
                // toggles when its Init fires.
                bool wheelDetected = detection.NewWheelDetected || detection.OldWheelDetected;
                var model = detection.LastKnownWheelModel;
                if (wheelDetected && !string.IsNullOrEmpty(model))
                {
                    bool active;
                    string friendly;
                    if (detection.OldWheelDetected)
                    {
                        active = plugin.DeviceExtensionActive;
                        friendly = "Old Protocol Wheel";
                    }
                    else
                    {
                        active = plugin.IsModelSpecificExtensionActive(model);
                        var prefix = WheelModelInfo.ExtractPrefix(model);
                        friendly = WheelModelInfo.GetFriendlyName(prefix);
                    }

                    if (!active)
                    {
                        list.Add(new StatusHint(
                            StatusHintKind.ProfileNotAddedWheel,
                            $"MOZA {friendly} not added in SimHub",
                            $"Wheel '{model}' is connected, but 'MOZA {friendly}' hasn't been added in " +
                            $"SimHub. Open Devices > Add device > MOZA {friendly}.",
                            relatedModel: model));
                    }
                }
            }

            return list;
        }
    }
}
