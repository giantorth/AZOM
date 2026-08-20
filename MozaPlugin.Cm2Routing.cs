using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows.Media;
using GameReaderCommon;
using SimHub.Plugins;
using MozaPlugin.Devices;
using MozaPlugin.Devices.StalksTruckSim;
using MozaPlugin.Hardware;
using MozaPlugin.Protocol;
using MozaPlugin.Resources;
using MozaPlugin.Settings;
using MozaPlugin.Telemetry;
using MozaPlugin.Telemetry.Dashboard;
using MozaPlugin.Telemetry.Era;
using MozaPlugin.Telemetry.Frames;
using MozaPlugin.Telemetry.TileServer;
using MozaPlugin.UI.UpdateCheck;
using Timer = System.Timers.Timer;

namespace MozaPlugin
{
    public partial class MozaPlugin
    {

        /// <summary>
        /// Set to true when a new device definition is deployed at runtime.
        /// The plugin settings panel shows a restart notice when this is true.
        /// </summary>
        internal volatile bool DeviceDefinitionDeployed;

        /// <summary>
        /// UTC timestamp of the last <see cref="Init"/> call. The UI hint-builder
        /// uses this as a settling reference so banners ("profile not added",
        /// "port in use") don't flash during the first few seconds of plugin
        /// startup before discovery and probe responses have arrived.
        /// </summary>
        internal DateTime StartupUtc { get; private set; } = DateTime.UtcNow;

        /// <summary>
        /// True when a standalone-USB dashboard (CM2 = 0x0025) is connected on
        /// its own dedicated port. Lets dashboard detection flip on USB PID
        /// alone, without waiting for a wheelbase relay or wheel-side ack.
        /// </summary>
        private bool IsStandaloneDashboardUsbConnection => DashboardUsbConnected;

        internal bool IsDashDetected =>
            DetectionState.DashDetected || IsStandaloneDashboardUsbConnection;

        /// <summary>
        /// True when a dash is present at all — on its own USB cable OR bridged through
        /// the primary pipe — independent of whether a wheel (and what kind) is attached.
        /// This is the "a dash exists, so manage it" predicate, distinct from the
        /// retired "should the MAIN sender drive the CM2" routing question: the CM2 is
        /// always driven by the dedicated <see cref="_cm2Sender"/> now. Used for UI tab
        /// visibility, CM2 meter-config gating, and diagnostics.
        ///
        /// The bridge is whatever owns the primary pipe — a wheelbase OR a Universal Hub.
        /// This deliberately does NOT require <c>BaseDetected</c>: that clause predated
        /// hub-only support and made a hub-bridged dash invisible to every consumer here,
        /// which collapsed the dash page's Dashboard tab (its telemetry-enable toggle with
        /// it) on a hub-only rig — bundle MGXWJ3YH. A bridged dash of unknown class may
        /// still turn out to be a CM1; <see cref="DashIsCm1"/> is the discriminated answer
        /// and gates the CM2-specific meter config.
        /// </summary>
        internal bool IsCm2Present =>
            DashboardUsbConnected
            || (_connection?.IsConnected == true
                && DetectionState.DashDetected);

        /// <summary>
        /// Wire dev_id of the CM2: a standalone-USB CM2 bridges as 0x12 (DeviceMain on
        /// its own pipe); a CM2 behind the wheelbase is the meter at 0x14 (DeviceDash).
        /// The <see cref="_cm2Sender"/>'s <c>TargetDeviceId</c> equals this; the CM2 LED
        /// writes and meter-config commands route here.
        /// </summary>
        internal byte Cm2TargetDeviceId =>
            DashboardUsbConnected ? MozaProtocol.DeviceMain : MozaProtocol.DeviceDash;

        /// <summary>
        /// An external display wired through the primary pipe — base OR hub — as the dash
        /// sub-device at 0x14, rather than a standalone-USB CM2. DECOUPLED: this is a pure
        /// "bus dash present" predicate — independent of the wheel's screen — since the
        /// CM2 is always driven by the dedicated <see cref="_cm2Sender"/> regardless of
        /// the wheel. Used by detection (probe the dash at 0x14) and the CM2 meter-config
        /// re-assert. Equivalent to <c>IsCm2Present &amp;&amp; !DashboardUsbConnected</c>.
        /// Says nothing about CM2-vs-CM1 — that is <see cref="DashIsCm1"/>'s answer.
        /// </summary>
        internal bool IsCm2BehindBaseCandidate =>
            IsCm2Present && !DashboardUsbConnected;


        /// <summary>
        /// Push the dashboard's live RPM LED bitmask (dash-send-telemetry,
        /// group 0x41 / FD DE) to the active dashboard sink, routed by connection
        /// path so the frame reaches the right device on the right pipe:
        ///   • standalone-USB CM2 → dedicated dashboard pipe, dev 0x12
        ///   • CM2 behind the wheelbase → main pipe, dev 0x14
        ///   • base-bridged dash (e.g. CM1) → main pipe, dev 0x14
        /// Called from <see cref="Devices.MozaDashLedDeviceManager"/> per frame.
        /// </summary>
        internal bool WriteDashLedBitmask(int bitmask)
        {
            // Stream lane (latest-wins, coalescing) — keep the per-frame CM2 LED
            // bitmask off the throttled one-shot FIFO so a shared-bus value stream
            // can't starve it. Idempotent end-state, safe to coalesce. Routed to the
            // CM2's connection + device (Cm2TargetDeviceId: 0x12 USB / 0x14 bus) —
            // the same place the dedicated _cm2Sender lives.
            if (DashboardUsbConnected)
                return _dashboardManager.WriteSettingForDeviceStream(
                    "dash-send-telemetry", Cm2TargetDeviceId, bitmask, Protocol.StreamKind.DashRpmBitmask);
            return _deviceManager.WriteSettingForDeviceStream(
                "dash-send-telemetry", Cm2TargetDeviceId, bitmask, Protocol.StreamKind.DashRpmBitmask);
        }

        /// <summary>
        /// Push the CM2's 6 flag-LED colours as the live dash-flag-colors array
        /// (group 0x32 cmd 08 00, 6×RGB, black = off). PitHouse drives the bus
        /// CM2's flag LEDs exactly this way — streamed per frame, the firmware
        /// lights each non-black flag (verified cm2t.pcapng). Routed to the same
        /// device/connection as the RPM bitmask (standalone-USB CM2 → 0x12 on the
        /// dedicated pipe, behind-base CM2 → 0x14 on the base).
        /// </summary>
        internal bool WriteDashFlagColors(byte[] rgb18)
        {
            if (DashboardUsbConnected)
                return _dashboardManager.WriteArrayForDeviceStream(
                    "dash-flag-colors", Cm2TargetDeviceId, rgb18, Protocol.StreamKind.DashFlagColors);
            return _deviceManager.WriteArrayForDeviceStream(
                "dash-flag-colors", Cm2TargetDeviceId, rgb18, Protocol.StreamKind.DashFlagColors);
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
            var slot = (Protocol.StreamKind)((int)Protocol.StreamKind.DashRpmColor0 + index);
            bool inRange = index >= 0
                && (int)slot <= (int)Protocol.StreamKind.DashRpmColor9;
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

        /// <summary>True when the CM2's meter firmware is the 2026-06 indicator
        /// stack that takes wheel-style group-0x3F live LED commands instead of
        /// the legacy 41 FD DE / 32 0B registers. Auto-detected + persisted; see
        /// <see cref="DetectCm2LedFirmwareEra"/>.</summary>
        internal bool Cm2HasNewLedFirmware => Settings?.Cm2NewLedFirmware ?? false;

        /// <summary>
        /// CM2 meter firmware era detection from the meter's 0x0E heartbeat text
        /// (src=0x41). The 2026-06 firmware rework replaced the autonomous
        /// threshold RPM ramp (RpmMode / RpmNumber[0~9] / RpmPercent[0~9]) with
        /// the wheel-style indicator-group stack (IndicatorMode / StandbyMode,
        /// meter_diag.c:89 → :88) and stopped honoring the legacy live LED
        /// registers. Both directions are detected so a firmware downgrade
        /// recovers too. Persisted because the heartbeat only arrives ~1/min —
        /// the next boot starts on the right LED path immediately.
        /// </summary>
        private void DetectCm2LedFirmwareEra(string text)
        {
            bool isNew;
            if (text.Contains("RpmNumber[") || text.Contains("RpmMode:")) isNew = false;
            else if (text.Contains("IndicatorMode:") || text.Contains("StandbyMode:")) isNew = true;
            else return;
            if (Settings == null || Settings.Cm2NewLedFirmware == isNew) return;
            Settings.Cm2NewLedFirmware = isNew;
            PersistSettings();
            MozaLog.Info("[AZOM] CM2 meter firmware era detected: " +
                         (isNew ? "indicator stack — wheel-style LED commands" : "legacy RPM ramp") +
                         " — dash LED path switched");
        }

        /// <summary>New-firmware CM2 live LED colour chunk (group 0x32 cmd 13 00,
        /// idx/R/G/B records) addressed to the CM2. Rides the same coalescing slots
        /// the legacy per-LED colour path used (DashRpmColor0+).</summary>
        internal bool WriteCm2LiveLedColorChunk(byte[] chunk, int chunkIdx)
        {
            var slot = (Protocol.StreamKind)((int)Protocol.StreamKind.DashRpmColor0 + chunkIdx);
            bool inRange = chunkIdx >= 0 && (int)slot <= (int)Protocol.StreamKind.DashRpmColor9;
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
                    "cm2-live-bitmask", Cm2TargetDeviceId, activeWindow8, Protocol.StreamKind.DashRpmBitmask);
            return _deviceManager.WriteArrayForDeviceStream(
                "cm2-live-bitmask", Cm2TargetDeviceId, activeWindow8, Protocol.StreamKind.DashRpmBitmask);
        }

        /// <summary>
        /// Route a one-shot CM2 meter-config write (group 0x32: modes, thresholds,
        /// indicator brightness, stored idle colours) to the CM2's OWN pipe + device.
        /// A standalone-USB CM2 lives on the dedicated _dashboardManager connection,
        /// NOT the wheelbase _deviceManager — so a name-based _deviceManager.WriteSetting
        /// would land on the wheelbase's own 0x12 (the base main, which drops group-0x32)
        /// and never reach the USB CM2. These mirror the per-frame WriteDash* LED routing.
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
