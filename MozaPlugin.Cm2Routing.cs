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
    }
}
