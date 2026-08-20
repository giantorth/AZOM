using MozaPlugin.Devices;
using MozaPlugin.Protocol;

namespace MozaPlugin.Hardware
{
    /// <summary>
    /// CM2 dashboard write routing. Moved here from MozaPlugin: this class owns
    /// every hardware-side write, and it was already the heaviest consumer of
    /// these helpers (all the cm2-* meter-config writes in ApplyDashToHardware).
    ///
    /// <para>Topology decides the pipe and the device id every time. A
    /// standalone-USB CM2 lives on the dedicated <c>_dashboardManager</c>
    /// connection at device 0x12; a CM2 behind the wheelbase is reached over the
    /// main <c>_deviceManager</c> pipe at device 0x14. A name-based
    /// <c>_deviceManager.WriteSetting</c> would land on the wheelbase's own 0x12
    /// (the base main, which drops group 0x32) and never reach a USB CM2 — hence
    /// the explicit ForDevice routing throughout.</para>
    /// </summary>
    internal sealed partial class HardwareApplier
    {
        // Both read live off the plugin: the CM2 can appear, move between pipes,
        // or disappear at runtime, so neither can be cached at construction.
        private bool DashboardUsbConnected => _plugin.DashboardUsbConnected;
        private byte Cm2TargetDeviceId => _plugin.Cm2TargetDeviceId;

        /// <summary>
        /// Push the CM2's RPM LED bitmask. Routed to the CM2's connection +
        /// device — the same place the dedicated _cm2Sender lives:
        ///   • standalone-USB CM2 → dedicated pipe, dev 0x12
        ///   • base-bridged dash (e.g. CM1) → main pipe, dev 0x14
        /// Called from <see cref="Devices.Led.MozaDashLedDeviceManager"/> per frame.
        /// </summary>
        internal bool WriteDashLedBitmask(int bitmask)
        {
            // Stream lane (latest-wins, coalescing) — keep the per-frame CM2 LED
            // bitmask off the throttled one-shot FIFO so a shared-bus value stream
            // can't starve it. Idempotent end-state, safe to coalesce.
            if (DashboardUsbConnected)
                return _dashboardManager.WriteSettingForDeviceStream(
                    "dash-send-telemetry", Cm2TargetDeviceId, bitmask, StreamKind.DashRpmBitmask);
            return _deviceManager.WriteSettingForDeviceStream(
                "dash-send-telemetry", Cm2TargetDeviceId, bitmask, StreamKind.DashRpmBitmask);
        }

        /// <summary>
        /// Push the CM2's 6 flag-LED colours as the live dash-flag-colors array
        /// (group 0x32 cmd 08 00, 6×RGB, black = off). PitHouse drives the bus
        /// CM2's flag LEDs exactly this way — streamed per frame, the firmware
        /// lights each non-black flag (verified cm2t.pcapng).
        /// </summary>
        internal bool WriteDashFlagColors(byte[] rgb18)
        {
            if (DashboardUsbConnected)
                return _dashboardManager.WriteArrayForDeviceStream(
                    "dash-flag-colors", Cm2TargetDeviceId, rgb18, StreamKind.DashFlagColors);
            return _deviceManager.WriteArrayForDeviceStream(
                "dash-flag-colors", Cm2TargetDeviceId, rgb18, StreamKind.DashFlagColors);
        }

        /// <summary>
        /// Push a single RPM LED's colour to the dash's live indicator-colour
        /// register (wire 0B 00). Routed/named per topology like the bitmask:
        /// standalone-USB CM2 → cm2-indicator-color on 0x12, behind-base CM2 →
        /// dash-rpm-color on 0x14. <paramref name="index"/> is 0-based.
        /// </summary>
        internal bool WriteDashRpmColor(int index, byte r, byte g, byte b)
        {
            var rgb = new byte[] { r, g, b };
            // One coalescing stream slot per RPM index (DashRpmColor0..9) bounds the
            // per-frame SyncRpmColors write-amplifier (up to 10 writes/frame) and
            // keeps it off the throttled one-shot lane. index is 0-based, 0..9.
            var slot = (StreamKind)((int)StreamKind.DashRpmColor0 + index);
            bool inRange = index >= 0
                && (int)slot <= (int)StreamKind.DashRpmColor9;
            if (DashboardUsbConnected)
                return inRange
                    ? _dashboardManager.WriteArrayForDeviceStream(
                        $"cm2-indicator-color{index + 1}", Cm2TargetDeviceId, rgb, slot)
                    : _dashboardManager.WriteArrayForDevice(
                        $"cm2-indicator-color{index + 1}", Cm2TargetDeviceId, rgb);

            return inRange
                ? _deviceManager.WriteArrayForDeviceStream($"dash-rpm-color{index + 1}", Cm2TargetDeviceId, rgb, slot)
                : _deviceManager.WriteArrayForDevice($"dash-rpm-color{index + 1}", Cm2TargetDeviceId, rgb);
        }

        /// <summary>New-firmware CM2 live LED colour chunk (group 0x32 cmd 13 00,
        /// idx/R/G/B records) addressed to the CM2. Rides the same coalescing slots
        /// the legacy per-LED colour path used (DashRpmColor0+).</summary>
        internal bool WriteCm2LiveLedColorChunk(byte[] chunk, int chunkIdx)
        {
            var slot = (StreamKind)((int)StreamKind.DashRpmColor0 + chunkIdx);
            bool inRange = chunkIdx >= 0 && (int)slot <= (int)StreamKind.DashRpmColor9;
            if (DashboardUsbConnected)
                return inRange
                    ? _dashboardManager.WriteArrayForDeviceStream("cm2-live-colors", Cm2TargetDeviceId, chunk, slot)
                    : _dashboardManager.WriteArrayForDevice("cm2-live-colors", Cm2TargetDeviceId, chunk);
            return inRange
                ? _deviceManager.WriteArrayForDeviceStream("cm2-live-colors", Cm2TargetDeviceId, chunk, slot)
                : _deviceManager.WriteArrayForDevice("cm2-live-colors", Cm2TargetDeviceId, chunk);
        }

        /// <summary>New-firmware CM2 live LED bitmask (group 0x32 cmd 14 00, 8-byte
        /// active(u32 LE) + window(u32 LE) form) addressed to the CM2.</summary>
        internal bool WriteCm2LiveLedBitmask(byte[] activeWindow8)
        {
            if (DashboardUsbConnected)
                return _dashboardManager.WriteArrayForDeviceStream(
                    "cm2-live-bitmask", Cm2TargetDeviceId, activeWindow8, StreamKind.DashRpmBitmask);
            return _deviceManager.WriteArrayForDeviceStream(
                "cm2-live-bitmask", Cm2TargetDeviceId, activeWindow8, StreamKind.DashRpmBitmask);
        }

        /// <summary>
        /// Route a one-shot CM2 meter-config write (group 0x32: modes, thresholds,
        /// indicator brightness, stored idle colours) to the CM2's OWN pipe + device.
        /// These mirror the per-frame WriteDash* LED routing.
        /// </summary>
        internal bool WriteCm2Config(string commandName, int value) =>
            DashboardUsbConnected
                ? _dashboardManager.WriteSettingForDevice(commandName, Cm2TargetDeviceId, value)
                : _deviceManager.WriteSettingForDevice(commandName, Cm2TargetDeviceId, value);

        internal bool WriteCm2Config(string commandName, byte[] payload) =>
            DashboardUsbConnected
                ? _dashboardManager.WriteArrayForDevice(commandName, Cm2TargetDeviceId, payload)
                : _deviceManager.WriteArrayForDevice(commandName, Cm2TargetDeviceId, payload);
    }
}
