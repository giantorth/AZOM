using System;
using System.Collections.Generic;
using MozaPlugin.Protocol;
using MozaPlugin.Telemetry;
using MozaPlugin.Devices.Extensions;

namespace MozaPlugin.Devices
{
    /// <summary>
    /// Decodes command responses and flips per-device detection flags on first sight.
    /// </summary>
    internal sealed class DeviceProber
    {
        // Per-device settings read commands. Sent after the corresponding
        // device is detected, rather than blasting all commands on connect.

        internal static readonly string[] BaseSettingsReadCommands = new[]
        {
            "base-limit", "base-max-angle", "base-ffb-strength", "main-get-interpolation", "base-torque", "base-speed",
            "base-damper", "base-friction", "base-inertia", "base-spring",
            "base-protection", "base-natural-inertia",
            "base-speed-damping", "base-speed-damping-point",
            "base-soft-limit-stiffness", "base-soft-limit-retain",
            "base-ffb-reverse", "base-temp-strategy", "base-gearshift-vibration",
            "main-get-work-mode", "main-get-led-status",
            "main-get-damper-gain", "main-get-friction-gain",
            "main-get-inertia-gain", "main-get-spring-gain",
            "main-get-ble-mode",
            "base-equalizer1", "base-equalizer2", "base-equalizer3",
            "base-equalizer4", "base-equalizer5", "base-equalizer6",
            "base-road-sensitivity",
            "base-ffb-curve-y1", "base-ffb-curve-y2", "base-ffb-curve-y3", "base-ffb-curve-y4", "base-ffb-curve-y5",
        };

        // 10-band EQ additions — read only after the base-fw-version reply
        // confirms support (old firmware never answers these; reading them in
        // the main sweep would burn PendingResponseTracker retry budget).
        internal static readonly string[] BaseEq10ReadCommands = new[]
        {
            "base-equalizer7", "base-equalizer8", "base-equalizer9", "base-equalizer10",
        };

        /// <summary>
        /// Per-wheel reads that don't depend on which LEDs are present. Sent at
        /// new-protocol detection regardless of the wheel model. LED-related
        /// reads (per-zone modes, per-zone brightness, per-LED colors, LED
        /// group probes) are deferred to <see cref="BuildNewWheelLedReadCommands"/>
        /// once <see cref="WheelModelInfo"/> has resolved — this stops the
        /// plugin hammering wheels (e.g. the original CS, which has only RPM
        /// LEDs) with reads for buttons / flags / knobs they can't service.
        /// PendingResponseTracker burns its retry budget on those timeouts.
        /// </summary>
        internal static readonly string[] NewWheelCoreReadCommands = new[]
        {
            "wheel-telemetry-mode",
            // Input modes — paddles/clutch/stick exist on every new-protocol wheel.
            // Knob input modes (wheel-knob-mode, wheel-knob-signal-modeN) are
            // deferred to BuildNewWheelLedReadCommands: they go only to models we
            // have positively identified, so the param-fragile rims never see them.
            //
            // Sleep-light reads (wheel-idle-mode/timeout/speed/color) are
            // deliberately NOT here. This list is issued at first wheel-detect,
            // before wheel-model-name resolves, so it cannot be gated on
            // capability. The legacy bare-"CS" wheel (HasSleepLight=false) does
            // not implement those parameters — reading them drives its firmware
            // into a Table 8 read-fail storm that makes it intermittently stop
            // answering presence polls (the plugin then resets it ~every 20 s in
            // a loop). The idle reads are deferred to
            // BuildNewWheelLedReadCommands, gated on WheelModelInfo.HasSleepLight,
            // once the model is known.
            "wheel-paddles-mode", "wheel-clutch-point", "wheel-stick-mode",
        };

        /// <summary>
        /// Build the LED-related per-wheel read list filtered by the wheel's
        /// actual LED layout. Reads omitted for LED groups the wheel doesn't
        /// have keep PendingResponseTracker from churning through retries on
        /// every cold-start of a wheel like the original "CS" (10 RPM LEDs
        /// only) or any future wheel registered in <see cref="WheelModelInfo.KnownModels"/>.
        ///
        /// For wheels mapped to <see cref="WheelModelInfo.Default"/> (firmware
        /// model name not in KnownModels) the LED-group probes still go out so
        /// we can detect Single / Rotary / Ambient groups we don't know about
        /// statically.
        /// </summary>
        internal static string[] BuildNewWheelLedReadCommands(WheelModelInfo? info, bool isFsr1 = false)
        {
            info ??= WheelModelInfo.Default;
            var cmds = new List<string>();

            // FSR V1 (RS21-D03): read NOTHING back. This old display rim is the
            // most param-fragile wheel we support — its store wedges into a
            // permanent "Failed to Read Parameter" storm that only a power-cycle
            // clears (see wheel-0x17.md § Param-store wedge). PitHouse never reads
            // its LED settings at all, while this batch is our heaviest read burst
            // (measured 87 per-LED colour reads on group 0x40 cmd 0x1F in one
            // session). Nothing needs them: the profile is the source of truth for
            // every colour/brightness we write, so the only loss is seeding the UI
            // pickers from the wheel's stored values on a fresh profile.
            if (isFsr1) return System.Array.Empty<string>();

            // Bare RPM-only rims (the legacy "CS": RPM LEDs, no buttons / knobs /
            // flags / sleep light) have essentially no readable LED settings, and
            // every group-param read we issue is for a new-protocol param this old
            // firmware doesn't implement — which storms its Table 8 param manager
            // ("Failed to Read Parameter" sweep → dead identity → re-detect loop).
            // PitHouse never reads these from this wheel. We drive its RPM LEDs by
            // writing colours directly; nothing needs reading back. Read nothing.
            if (info.RpmLedCount > 0 && info.ButtonLedCount == 0 && info.KnobCount == 0
                && !info.HasFlagLeds && !info.HasSleepLight)
            {
                return System.Array.Empty<string>();
            }

            // Sleep-light (idle breathing) settings — read only on wheels that
            // implement the feature. Captured into MozaData and seeded into the
            // per-wheel-page WheelSleepByPageGuid bundle via
            // SeedSleepBundleFromResponse. Deferred here (rather than the
            // model-blind NewWheelCoreReadCommands) so they can be gated on
            // HasSleepLight: the legacy bare-"CS" wheel lacks these parameters,
            // and reading them triggers a Table 8 read-fail storm in its
            // firmware that makes it periodically stop answering presence polls.
            // The matching idle-*-interval commands are write-only on the wire
            // (RxGroup=0xFF) so they're not read here; idle-speed (0x22) is
            // readable and is.
            if (info.HasSleepLight)
            {
                cmds.Add("wheel-idle-mode");
                cmds.Add("wheel-idle-timeout");
                cmds.Add("wheel-idle-speed");
                cmds.Add("wheel-idle-color");
            }

            // Per-zone LED modes + brightness + idle effect, gated on whether
            // the wheel actually has that zone. The idle-EFFECT reads (cmd 0x1d)
            // are additionally gated on HasSleepLight: they are idle/standby
            // animations the bare-"CS" rim lacks, and reading them — like writing
            // them — storms its Table 8 param manager. (Same family as the
            // sleep-light reads below; every wheel with idle effects has
            // HasSleepLight=true.)
            if (info.RpmLedCount > 0)
            {
                cmds.Add("wheel-rpm-brightness");
                if (info.HasSleepLight)
                    cmds.Add("wheel-telemetry-idle-effect");
            }
            if (info.ButtonLedCount > 0)
            {
                cmds.Add("wheel-buttons-led-mode");
                cmds.Add("wheel-buttons-brightness");
                if (info.HasSleepLight)
                    cmds.Add("wheel-buttons-idle-effect");
            }
            if (info.KnobCount > 0)
            {
                cmds.Add("wheel-knob-led-mode");
                if (info.HasSleepLight)
                    cmds.Add("wheel-knob-idle-effect");
            }

            // Knob INPUT config (encoder signal mode: BUTTON vs KNOB). Deliberately not
            // gated on KnobCount: that is the knob-LED capability, and most rims have
            // rotary encoders with no configurable knob LEDs (KnobCount == 0) — gating
            // these on it hid the signal-mode selector on every one of them. Issued for
            // any POSITIVELY IDENTIFIED model; the two early returns above already keep
            // the param-fragile rims out (FSR V1 reads nothing, and so does the bare
            // "CS" shape), and an unidentified model stays out for the same reason the
            // extended LED-group probes below do.
            //
            // A catalogued WheelModelInfo.KnobEncoderCount is authoritative and bounds
            // the sweep exactly — matching PitHouse, which reads 2a [N] only up to the
            // rim's real count (N=0..3 on a 4-encoder wheel; see
            // docs/protocol/findings/2026-04-28-wheel-catalog-read.md). An uncatalogued
            // model sweeps all five and lets the answers drive the UI. That discovery
            // over-reports — firmware answers every index whether or not the encoder
            // exists (KS: five answers, three knobs) — so it is a stopgap that keeps the
            // selector reachable on an unmeasured rim, not a substitute for the count.
            // A catalogued 0 means "confirmed no configurable encoders" and skips both.
            if (!ReferenceEquals(info, WheelModelInfo.Default) && info.KnobEncoderCount != 0)
            {
                cmds.Add("wheel-knob-mode");
                int sweep = info.KnobEncoderCount > 0 ? System.Math.Min(info.KnobEncoderCount, 5) : 5;
                for (int i = 0; i < sweep; i++)
                    cmds.Add($"wheel-knob-signal-mode{i}");
            }

            // Per-LED color reads, capped at the LED count this wheel reports.
            for (int i = 1; i <= info.RpmLedCount; i++)
                cmds.Add($"wheel-rpm-color{i}");
            for (int i = 1; i <= info.ButtonLedCount; i++)
                cmds.Add($"wheel-button-color{i}");
            if (info.HasFlagLeds)
            {
                for (int i = 1; i <= 6; i++)
                    cmds.Add($"wheel-flag-color{i}");
            }

            // Extended LED group probes (Single/Rotary/Ambient). Sent ONLY for
            // models we've positively identified as having the group — never for
            // an unknown model. Blind-probing Single/Ambient/knob-brightness on a
            // wheel we can't identify is exactly the "send reads to a rim that
            // doesn't implement the param" pattern that storms the legacy bare-"CS"
            // firmware (Table 8 read-fail → dead identity). A genuinely-new wheel
            // gets these once it's added to WheelModelInfo.KnownModels; until then
            // we stay quiet rather than risk wedging it. wheel-knob-brightness is
            // still read on known knob wheels (lights the Rotary-group presence
            // flag used by knob-ring writes).
            //
            // Knob palettes are seeded here like the RPM/button colours above (once,
            // at detect) so _data holds the wheel's stored Active + ring colours before
            // the Knobs tab is opened. PitHouse sweeps cmd 0x27 ROLE0 per knob within
            // ~3 s of connect (docs/protocol/findings/2026-05-10-knob-led-cmd27.md).
            // Single paced sweep, not a poll: a ~1 Hz ring poll (cmd 0x1F) produced
            // "Unexpected cmd: 31" firmware warnings on the Universal Hub path.
            if (info.KnobCount > 0)
            {
                cmds.Add("wheel-knob-brightness");
                int knobs = System.Math.Min(info.KnobCount, MozaData.WheelKnobMax);
                for (int k = 1; k <= knobs; k++)
                    cmds.Add($"wheel-knob{k}-active-color");
                if (info.KnobRingLeds != null)
                {
                    int ring = System.Math.Min(info.KnobRingLedTotal, MozaData.KnobRingLedMax);
                    for (int i = 1; i <= ring; i++)
                        cmds.Add($"wheel-knob-bg-color{i}");
                }
            }

            return cmds.ToArray();
        }

        internal static readonly string[] OldWheelSettingsReadCommands = new[]
        {
            "wheel-rpm-indicator-mode", "wheel-get-rpm-display-mode",
            "wheel-stick-mode",
            "wheel-old-rpm-brightness",
            "wheel-old-rpm-color1", "wheel-old-rpm-color2", "wheel-old-rpm-color3",
            "wheel-old-rpm-color4", "wheel-old-rpm-color5", "wheel-old-rpm-color6",
            "wheel-old-rpm-color7", "wheel-old-rpm-color8", "wheel-old-rpm-color9",
            "wheel-old-rpm-color10",
        };

        internal static readonly string[] BaseAmbientReadCommands = new[]
        {
            "base-ambient-brightness",
            "base-ambient-standby-mode",
            "base-ambient-indicator-state",
            "base-ambient-sleep-mode",
            "base-ambient-sleep-timeout",
            "base-ambient-startup-color",
            "base-ambient-shutdown-color",
            "base-ambient-sleep-breath-interval",
            "base-ambient-standby-interval-mode2",
            "base-ambient-standby-interval-mode3",
            "base-ambient-standby-interval-mode4",
            "base-ambient-standby-interval-mode5",
        };

        /// <summary>
        /// Per-LED palette reads for a base with <paramref name="ledsPerStrip"/> LEDs
        /// per strip: both idle palettes (standby modes 1 and 2) plus the sleep
        /// palette, across both strips. 6 reads per LED, so 36 on a 6-LED base and
        /// 54 on a 9-LED one. Modes 3–5 have no palette and are not read.
        /// </summary>
        internal static string[] BaseAmbientPerLedReadCommands(int ledsPerStrip)
        {
            var list = new List<string>(ledsPerStrip * 6);
            for (int strip = 0; strip < 2; strip++)
            {
                for (int mode = 1; mode <= 2; mode++)
                    for (int led = 0; led < ledsPerStrip; led++)
                        list.Add($"base-ambient-led-color-strip{strip}-mode{mode}-led{led}");

                for (int led = 0; led < ledsPerStrip; led++)
                    list.Add($"base-ambient-sleep-led-color-strip{strip}-led{led}");
            }
            return list.ToArray();
        }

        internal static readonly string[] HandbrakeSettingsReadCommands = new[]
        {
            "handbrake-direction", "handbrake-min", "handbrake-max",
            "handbrake-mode", "handbrake-button-threshold",
            "handbrake-y1", "handbrake-y2", "handbrake-y3", "handbrake-y4", "handbrake-y5",
        };

        // Per-model settings read once a relayed shifter's model is resolved (the
        // standalone lane reads its own per-model list via StandalonePeripheralDescriptor).
        // The SGP list adds its LED commands; the HGP has none.
        internal static readonly string[] HgpSettingsReadCommands = new[]
        {
            "shifter-direction", "shifter-paddle-sync", "shifter-hid-mode", "shifter-apply-mode",
        };
        internal static readonly string[] SgpSettingsReadCommands = new[]
        {
            "shifter-direction", "shifter-paddle-sync", "shifter-hid-mode", "shifter-apply-mode",
            "shifter-brightness", "shifter-colors",
        };

        internal static readonly string[] PedalsSettingsReadCommands = new[]
        {
            "pedals-throttle-dir", "pedals-throttle-min", "pedals-throttle-max",
            "pedals-brake-dir", "pedals-brake-min", "pedals-brake-max", "pedals-brake-angle-ratio",
            "pedals-clutch-dir", "pedals-clutch-min", "pedals-clutch-max",
            "pedals-throttle-y1", "pedals-throttle-y2", "pedals-throttle-y3", "pedals-throttle-y4", "pedals-throttle-y5",
            "pedals-brake-y1", "pedals-brake-y2", "pedals-brake-y3", "pedals-brake-y4", "pedals-brake-y5",
            "pedals-clutch-y1", "pedals-clutch-y2", "pedals-clutch-y3", "pedals-clutch-y4", "pedals-clutch-y5",
        };

        internal static readonly string[] HubReadCommands = new[]
        {
            "hub-base-power", "hub-port1-power", "hub-port2-power", "hub-port3-power",
            "hub-pedals1-power", "hub-pedals2-power", "hub-pedals3-power",
        };

        private readonly MozaPlugin _plugin;
        private readonly MozaSerialConnection _connection;
        private readonly MozaDeviceManager _deviceManager;
        private readonly MozaData _data;
        private readonly DeviceDetectionState _detectionState;
        // True for the primary (base/hub) prober, which drives the singular
        // TelemetrySender; false for the dedicated Universal Hub prober, which
        // only enumerates peripherals and must not touch the primary sender's
        // heartbeat mask.
        private readonly bool _drivesTelemetry;

        public DeviceProber(
            MozaPlugin plugin,
            MozaSerialConnection connection,
            MozaDeviceManager deviceManager,
            MozaData data,
            DeviceDetectionState detectionState,
            bool drivesTelemetry = true)
        {
            _plugin = plugin;
            _connection = connection;
            _deviceManager = deviceManager;
            _data = data;
            _detectionState = detectionState;
            _drivesTelemetry = drivesTelemetry;
        }

        /// <summary>
        /// Lock the responding wheel id on BOTH the device manager (per-instance)
        /// and the detection bag (survives a persistent-wire plugin reload). The
        /// manager's default is 0x17, so a wheel that answers elsewhere — ES on the
        /// base bus 0x13, or 0x15 — loses every "wheel"-class read/write on the next
        /// Init unless the reload can restore the id it locked here.
        /// <see cref="_drivesTelemetry"/> keeps the hub / base-aux probers from
        /// publishing an id for a pipe that isn't the wheel's.
        /// </summary>
        private void LockWheelId(byte deviceId)
        {
            _deviceManager.LockWheelId(deviceId);
            if (_drivesTelemetry)
                _detectionState.LastKnownWheelDeviceId = deviceId;
        }

        /// <summary>
        /// Log a device identity/capability echo, suppressing verbatim repeats.
        /// These values are constants of the attached hardware — they only change
        /// on a hot-swap — but the read commands are re-issued on every detection
        /// pass, so a redetect storm re-emitted the whole block once per pass
        /// (measured: 9 full display blocks in 178 s, and 25–74 % of every
        /// diagnostics bundle was exact-duplicate lines). Keyed per pipe so the
        /// primary / hub / base-aux probers don't suppress each other, and
        /// <see cref="MozaLog.DebugIfChanged"/> still re-emits every 5 min so a
        /// bundle pulled hours in shows current state.
        /// </summary>
        private void DebugIdentity(string field, string message) =>
            MozaLog.DebugIfChanged($"{_connection.CaptureLabel}:probe:{field}", message);

        /// <summary>
        /// First-sight detection cascade for the dashboard sub-device. Called
        /// from the data-response path (parser case "dash-rpm-indicator-mode")
        /// and from the empty-presence-probe path
        /// (<see cref="MozaPlugin.OnPresenceProbeAck"/>). Idempotent — only the
        /// first call does work.
        /// </summary>
        public void MarkDashDetected()
        {
            if (_detectionState.DashDetected) return;
            _detectionState.DashDetected = true;

            // A dash reached through the primary pipe (base or hub) is the meter at
            // 0x14 (0x12 is the base main). PitHouse cm2.pcapng drives this CM2's
            // session + telemetry on 0x14. Deploy the CM2 profile PROVISIONALLY and
            // probe display identity at 0x14 — the class (CM2 vs CM1) isn't known
            // yet; DualDisplayCoordinator.TickCm1Discriminator decides, and swaps the
            // definition via LatchDashAsCm1 if this turns out to be a CM1.
            bool bridgedDash = _plugin.IsCm2BehindBaseCandidate;
            if (bridgedDash)
                _deviceManager.SendDisplayProbe(MozaProtocol.DeviceDash);

            if (DeviceDefinitionDeployer.DeployDashboard(_connection.DiscoveredPid))
                _plugin.DeviceDefinitionDeployed = true;
            _plugin.HardwareApplier.ApplyDashToHardware(_plugin.Settings?.ProfileStore?.CurrentProfile);
            MozaLog.Info(bridgedDash
                ? "[AZOM] Dashboard detected (bridged dash at 0x14 — provisionally deployed the CM2 profile, probing display identity)"
                : "[AZOM] Dashboard detected");

            // DECOUPLED: a bus CM2 is driven by the dedicated _cm2Sender, never the
            // main sender — so EnsureCm2Pipeline below (not ApplyTelemetrySettings/
            // StartTelemetryIfReady) brings it up. This holds for ANY wheel: a wheel
            // with its own screen (main sender drives 0x17, _cm2Sender drives the CM2),
            // a screenless wheel, or no wheel — _cm2Sender owns the CM2 in all cases,
            // and the CM1 discriminator now runs unconditionally for a bus dash.
            try { _plugin.EnsureCm2Pipeline(); }
            catch (Exception ex) { MozaLog.Debug($"[AZOM] EnsureCm2Pipeline on dash-detect skipped: {ex.Message}"); }
        }

        /// <summary>First-sight detection cascade for the handbrake sub-device.
        /// <paramref name="issueReads"/> false skips the settings-read cascade —
        /// used by a standalone pipe that has confirmed presence (connect or PID)
        /// but not yet that the device answers our binary protocol, so doomed
        /// reads don't spam the pending tracker.</summary>
        public void MarkHandbrakeDetected(bool issueReads = true)
        {
            if (_detectionState.HandbrakeDetected)
            {
                // Flag rode a persistent-wire reload; End() cleared the owner.
                if (_detectionState.HandbrakeOwner == null) _detectionState.HandbrakeOwner = _deviceManager;
                return;
            }
            // Record the owning pipe BEFORE flipping the flag so HardwareApplier
            // (which reads flag-then-owner) never sees detected==true paired with
            // a null/stale owner. First responder across the base + hub pipes wins.
            _detectionState.HandbrakeOwner = _deviceManager;
            _detectionState.HandbrakeDetected = true;
            _plugin.HardwareApplier.ApplyHandbrakeToHardware(_plugin.Settings?.ProfileStore?.CurrentProfile);
            if (issueReads)
                _deviceManager.ReadSettings(HandbrakeSettingsReadCommands);
            MozaLog.Info("[AZOM] Handbrake detected");
        }

        /// <summary>First-sight detection cascade for the pedals sub-device.
        /// See <see cref="MarkHandbrakeDetected"/> for <paramref name="issueReads"/>.</summary>
        public void MarkPedalsDetected(bool issueReads = true)
        {
            if (_detectionState.PedalsDetected)
            {
                // Flag rode a persistent-wire reload; End() cleared the owner.
                if (_detectionState.PedalsOwner == null) _detectionState.PedalsOwner = _deviceManager;
                // The registry and its routed probes are per instance and were
                // disposed with the prior one, so the routed lane has to be
                // re-interrogated even though the flag says "already detected".
                // Idempotent per owning pipe (ProbeRoutedMBooster's ContainsKey guard).
                _plugin.ProbeRoutedMBooster(_deviceManager);
                return;
            }
            // Owner first, then flag (see MarkHandbrakeDetected). The owning
            // MozaDeviceManager is this prober's — base pipe for the primary
            // prober, hub pipe for the dedicated hub prober.
            _detectionState.PedalsOwner = _deviceManager;
            _detectionState.PedalsDetected = true;
            _plugin.HardwareApplier.ApplyPedalsToHardware(_plugin.Settings?.ProfileStore?.CurrentProfile);
            if (issueReads)
                _deviceManager.ReadSettings(PedalsSettingsReadCommands);
            MozaLog.Info("[AZOM] Pedals detected");
            // The pedal device on this pipe may be an mBooster (RJ45 hookup
            // instead of USB) — probe its identity at dev 0x19 and, if it is
            // one, a routed mBooster lane gets registered over this pipe.
            // Pit House fully supports this topology; routing is by sub-device
            // id, same as any other relayed peripheral.
            _plugin.ProbeRoutedMBooster(_deviceManager);
        }

        /// <summary>First-sight detection cascade for the HGP/SGP shifter.
        /// See <see cref="MarkHandbrakeDetected"/> for <paramref name="issueReads"/>.
        /// The standalone-USB lane passes <c>issueReads:false</c> and issues its own
        /// per-model read list (incl. SGP LED commands) from the controller — this
        /// path's list is the common non-LED subset used on a base/hub-relayed pipe.</summary>
        // HGP and SGP are independent devices — each has its own flag + owner so both
        // can be attached at once (each on its own USB port). Owner first, then flag
        // (see MarkHandbrakeDetected). issueReads: relay resolution calls with true so
        // the resolved model's settings populate; the standalone lane latches at connect
        // with false and issues its own per-model read list from the controller.
        public void MarkHgpDetected(bool issueReads = true)
        {
            if (_detectionState.HgpDetected)
            {
                if (_detectionState.HgpOwner == null) _detectionState.HgpOwner = _deviceManager;
                return;
            }
            _detectionState.HgpOwner = _deviceManager;
            _detectionState.HgpDetected = true;
            _plugin.HardwareApplier.ApplyHgpToHardware(_plugin.Settings?.ProfileStore?.CurrentProfile);
            if (issueReads) _deviceManager.ReadSettings(HgpSettingsReadCommands);
            MozaLog.Info("[AZOM] HGP shifter detected");
        }

        public void MarkSgpDetected(bool issueReads = true)
        {
            if (_detectionState.SgpDetected)
            {
                if (_detectionState.SgpOwner == null) _detectionState.SgpOwner = _deviceManager;
                return;
            }
            _detectionState.SgpOwner = _deviceManager;
            _detectionState.SgpDetected = true;
            _plugin.HardwareApplier.ApplySgpToHardware(_plugin.Settings?.ProfileStore?.CurrentProfile);
            if (issueReads) _deviceManager.ReadSettings(SgpSettingsReadCommands);
            MozaLog.Info("[AZOM] SGP shifter detected");
        }

        /// <summary>A base/hub-relayed shifter (single 0x1A bus, no PID) can't be told
        /// apart at first sight, so probe the generic device-type identity — the ONE
        /// signal measured to differ (see <see cref="HgpDeviceType"/>). The settings
        /// block does NOT discriminate: an HGP behind an R5 answered every group 0x51
        /// read including brightness and colors, and acked writes to both (bundle
        /// 32ZD7KHW) — see docs/protocol/devices/shifter-0x1A.md § Telling HGP from SGP.
        /// The name/hw-version reads are exploratory and untracked; they cost two frames
        /// and would retire the device-type magic value if 0x1A self-describes.
        /// No-op once THIS pipe's model is latched — a shifter detected elsewhere (e.g.
        /// a standalone-USB HGP) says nothing about what's behind this base/hub and must
        /// not suppress the probe.</summary>
        public void ProbeRelayedShifter()
        {
            if (_detectionState.ShifterModelForOwner(_deviceManager) != ShifterModelKind.Unknown) return;
            _relayShifterProbeRounds++;
            _deviceManager.ReadSetting("shifter-device-type");
            // Exploratory, so only the first couple of rounds (one repeat in case the
            // first pair is dropped) — repeating a probe nothing depends on would just
            // add two frames per PollStatus tick.
            if (_relayShifterProbeRounds <= 2)
                _deviceManager.SendNameIdentityProbe(MozaProtocol.DeviceHPattern);
            // Liveness read, NOT identification — see the shifter-brightness case in
            // DetectDevices. It has to be a command whose reply doesn't re-enter this
            // method (shifter-direction does, via its own case), and both models answer
            // it, so it is the evidence the fallback below keys off.
            _deviceManager.ReadSetting("shifter-brightness");
            // Fallback for firmware that answers the settings block but not group 0x04:
            // once the device-type read has gone unanswered across this many probe rounds
            // (PollStatus re-fires the presence probe every tick while the model is
            // Unknown), resolve by elimination off any settings answer. Without it such a
            // shifter would show no tab at all — the pre-2026-07 behaviour was to always
            // show one. Deliberately NOT the first-round default: group 0x04 at 0x1A is
            // answered by both bases seen so far (R5 here, R12 per base-fw-version-b), so
            // the normal path is the identity reply, not this. Attempted once: if the latch
            // doesn't land (another pipe already owns the SGP), re-logging it every tick
            // adds nothing, and the owner gate above lets this pipe resolve normally if
            // that other shifter goes away.
            if (!_relayShifterFallbackTried
                && _relayShifterProbeRounds > RelayShifterDeviceTypeGraceRounds
                && _relayShifterAnsweredSettingsRead)
            {
                _relayShifterFallbackTried = true;
                MozaLog.Info("[AZOM] Relayed shifter answered settings reads but never the " +
                    $"group 0x04 device-type after {_relayShifterProbeRounds} probe rounds — " +
                    "resolving as SGP by elimination");
                MarkSgpDetected();
            }
        }

        // The HGP's grp-0x04 device-type reply, measured 2026-08-21 on a base-relayed HGP
        // (ES + R5, bundle 32ZD7KHW): `84 a1 01 02 08 01`. A relayed SGP's value has never
        // been measured, so this is a positive HGP match only — see
        // docs/protocol/open-questions.md § Relayed HGP/SGP discriminator. The standalone
        // lane doesn't need it (PID 0x001E / 0x0023 settle the model).
        private static readonly byte[] HgpDeviceType = { 0x01, 0x02, 0x08, 0x01 };

        // How many ProbeRelayedShifter rounds to wait for a group-0x04 answer before
        // falling back to elimination. Rounds are driven by the PollStatus presence probe
        // (~5 s apart), so this is a handful of seconds, not a race with the first reply.
        private const int RelayShifterDeviceTypeGraceRounds = 3;
        private int _relayShifterProbeRounds;
        // Set by any group-0x51 settings answer from this pipe's shifter. Evidence that a
        // shifter is there and talking — NOT evidence of which model, which is exactly the
        // conflation that made a relayed HGP report as an SGP.
        private bool _relayShifterAnsweredSettingsRead;
        // Edge guard so the elimination fallback logs + latches at most once per pipe.
        // NOT a "stop probing" latch: the owner gate at the top of ProbeRelayedShifter is
        // the only thing that ends the probe, so a pipe whose shifter is replaced still
        // re-resolves once the flags clear.
        private bool _relayShifterFallbackTried;

        /// <summary>Resolve a base/hub-relayed shifter's model from the generic
        /// device-type identity reply — the authoritative discriminator on a lane with no
        /// PID. Logs the raw reply either way so a support bundle always carries the
        /// evidence. A match latches HGP; anything else latches SGP by elimination, since
        /// 0x1A is the shifter's exclusive bus id and there are only two passive models.
        /// No-op once THIS pipe's model is latched.</summary>
        private void ResolveRelayedShifterModelFromDeviceType()
        {
            var dt = _data.RelayShifterDeviceType;
            if (dt == null || dt.Length == 0) return;
            bool isHgp = BytesEqual(dt, HgpDeviceType);
            MozaLog.Info($"[AZOM] Shifter device-type reply = [{System.BitConverter.ToString(dt)}] " +
                $"(HGP/SGP identity discriminator; relayed lane) → {(isHgp ? "HGP" : "SGP")}");
            if (_detectionState.ShifterModelForOwner(_deviceManager) != ShifterModelKind.Unknown) return;
            if (isHgp) MarkHgpDetected();
            else MarkSgpDetected();
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>
        /// Validates a firmware model-name string per
        /// docs/how-to-query-device-type.md §5 ("Device-name validation"): reject
        /// empty names, non-printable bytes, and the known non-device replies
        /// (OK / BUSY / ERROR / ERR). Gates model-name-driven detection and model
        /// resolution so a stray status reply can't be mistaken for a wheel.
        /// </summary>
        private static bool IsValidWheelModelName(string? name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (char c in name!)
                if (c < 0x20 || c > 0x7E) return false;
            switch (name.ToUpperInvariant())
            {
                case "OK":
                case "BUSY":
                case "ERROR":
                case "ERR":
                    return false;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Wheel hot-swap detection by model-name change. Returns true (and triggers
        /// a re-detect) when a different wheel model is now reporting than the one
        /// last seen. Shared by the new-protocol (0x17) and ES (0x18) identity paths.
        /// </summary>
        private bool DetectWheelModelHotSwap(string currentModel)
        {
            if (!string.IsNullOrEmpty(_detectionState.LastKnownWheelModel) &&
                _detectionState.LastKnownWheelModel != currentModel)
            {
                _plugin.ResetWheelDetection(
                    $"Wheel model changed from '{_detectionState.LastKnownWheelModel}' " +
                    $"to '{currentModel}' — hot-swap detected");
                return true;
            }
            return false;
        }

        /// <summary>
        /// Re-trigger telemetry start once the display sub-device's identity
        /// has answered. The probe replies field-by-field, and on some wheels
        /// (W17 / CS Pro) the model-name field comes back EMPTY while
        /// HW/SW/MCU-UID populate. <see cref="MozaPlugin.IsDisplayDetected"/>
        /// — and therefore both the <c>StartTelemetryIfReady</c> display gate
        /// and the PollStatus display-wedge watchdog — rises on ANY of those
        /// fields, so the pipeline-start re-trigger must fire from every
        /// identity handler, not just model-name. Otherwise an empty-model-name
        /// wheel flips IsDisplayDetected true ~1 ms after the start gate last
        /// deferred, and nothing re-invokes the start: telemetry sits dead
        /// until an unrelated user action pokes it (CS-Pro bundle 2026-06-13).
        /// Idempotent: ClearDisplayWedgeRecovery is a flag clear and
        /// StartTelemetryIfReady no-ops once the sender is running.
        /// </summary>
        private void NoteDisplayIdentityReady()
        {
            if (!_plugin.IsDisplayDetected) return;
            // Display is responsive — a future wheel hot-swap that wedges
            // should get its own recovery attempt.
            _plugin.ClearDisplayWedgeRecovery();
            _plugin.StartTelemetryIfReady();
        }

        /// <summary>
        /// Ask for the numeric base firmware version (dev 0x12, group 0x04) — the
        /// sole gate for the wheelbase LFE effects and the 10-band EQ
        /// (<see cref="MozaData.BaseSupportsLfe"/>). Three shots because a silent
        /// base disables both outright: the canonical 0x12 request PitHouse sends,
        /// the same request in its zero-length form, and the same query at dev
        /// 0x13. An R12 (RS21-D07) on LFE-capable firmware answers none of them at
        /// 0x12 in the len-4 form — see MozaCommandDatabase.
        ///
        /// <para>Called at base detect and, while the version is still unknown,
        /// re-called from <c>PollStatusCore</c> — the detect-time burst rides the
        /// <c>BaseAmbientProbed</c> latch, so without the retry a base that drops
        /// all three replies stays LFE-dead for the whole session with nothing
        /// re-asking.</para>
        /// </summary>
        /// <param name="via">Pipe to ask on. Null → this prober's own manager.
        /// The poll-tick retry passes <c>DetectionState.BaseOwner</c> so a base
        /// sitting on the dedicated base-aux pipe (post base→hub migration) is
        /// asked there rather than on the now-hub-bound primary.</param>
        internal void SendBaseFwVersionProbes(MozaDeviceManager? via = null)
        {
            var dm = via ?? _deviceManager;
            dm.ReadSetting("base-fw-version");
            dm.SendBaseFwVersionShortProbe();
            dm.ReadSetting("base-fw-version-b");
        }

        /// <summary>Base identity reads (dev 0x13 direct) plus the firmware probes.
        /// Issued at first-sight detection and re-issued by the persistent-wire
        /// reload repair in DetectDevices — MozaData is per instance, so a reload
        /// starts with these fields blank while the detection latches ride the bag.
        /// See DeviceDetectionState.ResetBase for what blank identity costs.</summary>
        internal void SendBaseIdentityProbes()
        {
            _deviceManager.ReadSetting("base-model-name");
            _deviceManager.ReadSetting("base-sw-version");
            _deviceManager.ReadSetting("base-hw-version");
            _deviceManager.ReadSetting("base-hw-sub");
            _deviceManager.ReadSetting("base-mcu-uid");
            _deviceManager.ReadSetting("base-identity-11");
            SendBaseFwVersionProbes();
        }

        /// <summary>
        /// Write (or refresh, or remove) this wheelbase's SimHub device definition.
        /// Called from both capability replies — the ambient probe and the firmware
        /// version — because either can be the one that changes the answer, and the
        /// deployer's staleness check makes repeats free.
        /// </summary>
        private void DeployBaseDefinition(bool ambientDetected)
        {
            // Unknown until the firmware version answers. The ambient probe replies
            // first, so passing plain false there stripped HapticsFeature and the
            // version reply put it straight back — two writes and a restart banner
            // on every boot.
            bool? wantHaptics = _data.BaseFwVersion != 0
                ? _plugin.WheelbaseWantsShakeItHaptics
                : (bool?)null;

            if (DeviceDefinitionDeployer.DeployForBaseModel(
                    _data.BaseModelName, _connection.DiscoveredPid,
                    ambientDetected, wantHaptics))
                _plugin.DeviceDefinitionDeployed = true;
        }

        /// <summary>
        /// Auto-detect connected devices based on response commands.
        /// First sight of a known response flips the matching detection flag
        /// and queues per-device settings reads + Apply*ToHardware.
        /// </summary>
        public void DetectDevices(string commandName, int value, byte deviceId)
        {
            // wheel-mcu-uid / display-mcu-uid responses parse to negative int32
            // (BE-encoded 0xBE prefix), but UpdateFromArray has already stored
            // the raw 12 bytes — log before the `value < 0` guard.
            if (commandName == "wheel-mcu-uid" && _data.WheelMcuUid.Length > 0)
            {
                MozaLog.Debug(
                    $"[AZOM] Wheel MCU UID ({_data.WheelMcuUid.Length}B): " +
                    MozaLog.RedactBytesHex(_data.WheelMcuUid));
                return;
            }
            if (commandName == "display-mcu-uid" && _data.DisplayMcuUid.Length > 0)
            {
                MozaLog.Debug(
                    $"[AZOM] Display MCU UID ({_data.DisplayMcuUid.Length}B): " +
                    MozaLog.RedactBytesHex(_data.DisplayMcuUid));
                // MCU UID alone satisfies IsDisplayDetected — re-trigger the
                // deferred telemetry start (empty-model-name wheels rely on it).
                NoteDisplayIdentityReady();
                return;
            }

            if (value < 0) return;

            // TelemetrySender's heartbeat mask: only ping detected devices.
            // Only the primary prober drives the singular sender; the dedicated
            // hub prober enumerates peripherals on its own pipe and must not
            // toggle heartbeats on the primary (base) pipe.
            if (_drivesTelemetry)
            {
                var sender = _plugin.TelemetrySender;
                if (deviceId >= 18 && deviceId <= 30 && sender != null)
                    sender.DetectedDeviceMask |= (1 << (deviceId - 18));
            }

            // Base flag rode a persistent-wire reload; End() cleared the owner —
            // the first pipe to answer the base probe re-points it. A null owner is
            // also the once-per-reload trigger for re-reading everything the
            // first-sight cascade below would have fetched: that block is gated on
            // !BaseDetected, so on a reload it never runs, yet MozaData is fresh and
            // starts blank. Left alone, base settings, base identity, the ambient
            // block and the 10-band EQ values stay empty for the whole instance —
            // and blank identity empties the SDK DeviceCatalog, making every
            // device-scoped CoAP URI answer 4.04 (see DeviceDetectionState.ResetBase).
            // BaseDetected deliberately stays TRUE: clearing it would make the
            // reload's ApplyProfile a no-op and drop FFB strength / steering limit
            // until the cascade re-ran.
            if (commandName == "base-mcu-temp" && _detectionState.BaseDetected
                && _detectionState.BaseOwner == null)
            {
                _detectionState.BaseOwner = _deviceManager;

                // Re-open the capability latches that gate the two switch cases
                // below (neither is nested under !BaseDetected, so re-eliciting
                // their replies is enough to re-run the ambient and EQ10 re-reads).
                _detectionState.BaseAmbientLedSupported = false;
                _detectionState.BaseEq10Probed = false;
                _detectionState.BaseFwVersionLogged = false;
                // A prior instance that exhausted the retry budget would otherwise
                // leave TickBaseFwVersionRetry gated off forever, so BaseSupportsLfe
                // could never re-resolve and LFE + the 10-band EQ stayed disabled.
                _detectionState.BaseFwVersionProbeRetries = 0;

                _deviceManager.ReadSettings(BaseSettingsReadCommands);
                _deviceManager.ReadSetting("main-model-name");
                _deviceManager.ReadSetting("main-model-name-b");
                // Re-runs the ambient block (its own re-reads + definition deploy).
                _deviceManager.ReadSetting("base-ambient-brightness");
                // Identity + fw; the fw reply re-runs the deferred EQ10 apply/read.
                SendBaseIdentityProbes();
                MozaLog.Debug("[AZOM] Base rode a persistent-wire reload — re-reading settings, identity and capabilities");
            }

            // Base detection — IsBaseConnected was just set by UpdateFromCommand;
            // re-apply the profile so base settings get pushed.
            if (commandName == "base-mcu-temp" && !_detectionState.BaseDetected)
            {
                // Owner first, then flag (mirrors MarkPedalsDetected). The owning
                // MozaDeviceManager is this prober's pipe — the primary (base) in
                // the normal case, or the dedicated base-aux pipe after a base→hub
                // migration. HardwareApplier routes base FFB/ambient writes here.
                _detectionState.BaseOwner = _deviceManager;
                _detectionState.BaseDetected = true;
                MozaLog.Info("[AZOM] Base detected");
                // Writes queue first, reads after — device processes FIFO so
                // read responses reflect the values we just wrote.
                var profile = _plugin.Settings.ProfileStore.CurrentProfile;
                if (profile != null)
                    _plugin.ApplyProfile(profile);
                _deviceManager.ReadSettings(BaseSettingsReadCommands);

                // Capability probe for the wheelbase ambient strip — R21/R25/R27
                // family replies on group 0xA2; R9/R12 silently drop the read.
                // Reply is handled in the "base-ambient-brightness" case and
                // gates the wheelbase device definition's LED section.
                if (!_detectionState.BaseAmbientProbed)
                {
                    _detectionState.BaseAmbientProbed = true;
                    // Model name FIRST, ambient capability second. The device
                    // answers FIFO, so this ordering is what makes
                    // MozaData.BaseModelName populated by the time the ambient
                    // reply lands — and the ambient reply is what deploys the
                    // SimHub device definition, whose LED count depends on the
                    // model (6 LEDs/strip on R16 Ultra vs 9 elsewhere). Probe
                    // the other way round and the definition is written with
                    // the fallback geometry. See BaseModelInfo.
                    _deviceManager.ReadSetting("main-model-name");
                    _deviceManager.ReadSetting("main-model-name-b");
                    _deviceManager.ReadSetting("base-ambient-brightness");
                    // Base-identity probes (dev 0x13 direct). Populates
                    // MozaData.BaseMcuUid / BaseSwVersion / BaseHwVersion /
                    // BaseHwSubVersion / BaseModelName / BaseIdentity11 so
                    // DeviceCatalog can synthesise the Motor + Wheel Base
                    // manifest entries iRacing requires. PitHouse capture
                    // 2026-05-23 issues the same probes at cold-start.
                    //
                    // ES-wheel caveat: on ES wheels device 0x13 *is* the
                    // wheel, so these probes shadow wheel-model-name and the
                    // base-* handlers populate Base* fields with the wheel's
                    // identity. DeviceCatalog guards against acting on that
                    // mis-attribution by checking _deviceManager.WheelDeviceId
                    // before synthesising Motor / Wheel Base manifest entries.
                    // We still issue the probes here because wheel detection
                    // (and hence WheelDeviceId lock-in) happens *after* base
                    // detection in the typical R5/R9/R12/R21/R25 flow — the
                    // probes have to fly before we know whether to skip them.
                    SendBaseIdentityProbes();
                }
            }

            switch (commandName)
            {
                case "dash-rpm-indicator-mode":
                    MarkDashDetected();
                    break;

                case "base-ambient-brightness":
                    if (!_detectionState.BaseAmbientLedSupported)
                    {
                        _detectionState.BaseAmbientLedSupported = true;
                        DeployBaseDefinition(ambientDetected: true);
                        _plugin.HardwareApplier.ApplyBaseAmbientToHardware(_plugin.Settings?.ProfileStore?.CurrentProfile);
                        _deviceManager.ReadSettings(BaseAmbientReadCommands);
                        // Per-LED palettes, sized to the detected strip length so a
                        // 6-LED base never asks for LEDs 6..8.
                        int ledsPerStrip = _data.ResolvedAmbientLedsPerStrip;
                        _deviceManager.ReadSettings(BaseAmbientPerLedReadCommands(ledsPerStrip));
                        MozaLog.Info(
                            $"[AZOM] Base ambient LEDs detected (model='{(string.IsNullOrEmpty(_data.BaseModelName) ? "unknown" : _data.BaseModelName)}', "
                            + $"{ledsPerStrip} LEDs/strip)");
                    }
                    break;

                case "base-fw-version":
                case "base-fw-version-b":
                    // The reply's packed version is always positive (major byte
                    // < 0x80), so it clears the value guard above. Logged once per
                    // detection: it's the only LFE gate, and without it a silent
                    // base is indistinguishable from an old one in a bug report.
                    if (!_detectionState.BaseFwVersionLogged)
                    {
                        _detectionState.BaseFwVersionLogged = true;
                        MozaLog.Info(
                            $"[AZOM] Base firmware {_data.BaseFwVersionText} " +
                            $"(LFE effects {(_data.BaseSupportsLfe ? "supported" : "unsupported, needs >= 1.2.10.10")}) " +
                            $"via {commandName}");
                    }
                    // LFE support is only known now, and it decides whether the
                    // device definition carries a HapticsFeature block. This is
                    // also the ONLY deploy trigger for a base with no ambient
                    // strip — nothing else fires for an R5/R9/R12.
                    DeployBaseDefinition(ambientDetected: _detectionState.BaseAmbientLedSupported);

                    // Deferred equalizer7-10 apply+read: the main base sweep runs
                    // before the firmware version is known, and old firmware never
                    // answers these registers. Writes queue before reads so the
                    // read-backs reflect the profile values just applied.
                    if (_data.BaseSupportsEq10 && !_detectionState.BaseEq10Probed)
                    {
                        _detectionState.BaseEq10Probed = true;
                        _plugin.HardwareApplier.ApplyBaseToHardware(_plugin.Settings?.ProfileStore?.CurrentProfile);
                        _deviceManager.ReadSettings(BaseEq10ReadCommands);
                    }
                    break;

                case "wheel-telemetry-mode":
                    if (!_detectionState.NewWheelDetected && !_detectionState.OldWheelDetected)
                    {
                        _detectionState.NewWheelDetected = true;
                        // Stamp first-detect time for the display-wedge watchdog
                        // (PollStatus bounds the post-detect display-boot wait).
                        _plugin.NoteWheelDetected();
                        LockWheelId(deviceId);
                        // Don't apply here — page GUID isn't resolvable until
                        // wheel-model-name arrives. Apply runs in the
                        // wheel-model-name case below.
                        _deviceManager.ReadSetting("wheel-model-name");
                        _deviceManager.ReadSetting("wheel-sw-version");
                        _deviceManager.ReadSetting("wheel-hw-version");
                        _deviceManager.ReadSetting("wheel-serial-a");
                        _deviceManager.ReadSetting("wheel-serial-b");
                        // PitHouse's 12-frame identity handshake.
                        _deviceManager.SendPithouseIdentityProbe(deviceId);
                        // Display sub-device probe is deferred to the
                        // wheel-model-name handler below — sending 11 frames on
                        // the dashboard session group (0x43 dev=0x17) to a
                        // screenless wheel appears to put its command parser
                        // into a half-engaged state where settings reads start
                        // timing out. By waiting for the model-name response we
                        // can skip the probe entirely when WheelModelInfo says
                        // the wheel has no display.
                        //
                        // Send the LED-layout-independent reads now. The
                        // model-aware LED reads (per-zone modes, brightness,
                        // per-LED colors, group probes) are kicked off in the
                        // wheel-model-name handler below, once WheelModelInfo
                        // is resolved.
                        _deviceManager.ReadSettingsPaced(NewWheelCoreReadCommands);
                        MozaLog.Info($"[AZOM] New-protocol wheel detected on ID {deviceId}");
                        // Telemetry start is deferred until wheel-model-name responds;
                        // ShouldDriveDashboard() needs WheelModelInfo to decide.
                    }
                    else if (deviceId != _deviceManager.WheelDeviceId)
                    {
                        _plugin.ResetWheelDetection(
                            $"New wheel responded on ID {deviceId} (was locked on " +
                            $"{_deviceManager.WheelDeviceId}) — hot-swap detected");
                    }
                    break;

                case "wheel-model-name":
                    // Only the wheel ids may drive wheel identity. The parser's
                    // device hints already keep base/ES/shifter/pedals/handbrake
                    // replies out of the wheel-* bucket, but this case both
                    // DETECTS and HOT-SWAPS, so a single mis-routed reply costs a
                    // full detection reset (+ a PendingResponseTracker wipe). A
                    // relayed pedal set answering the shared group-0x07 probe used
                    // to land here as model 'SRP' and reset a healthy 'KS' wheel —
                    // see the pedals/handbrake hints in MozaResponseParser.
                    if (deviceId != MozaProtocol.DeviceWheel && deviceId != MozaProtocol.DeviceWheel15)
                        break;

                    // A valid model-name reply (the doc's canonical group-0x07
                    // probe, ProbeWheelDetection) can itself trigger new-protocol
                    // detection — this covers a wheel that answers the identity
                    // group but not the telemetry-mode / rpm-value1 groups the
                    // normal detection cascade keys off. Gated to the new-protocol
                    // wheel ids (0x17, 0x15); the base (0x13) resolves as
                    // base-model-name and ES (0x18) as es-wheel-model-name, so
                    // neither reaches this case. Mirrors the wheel-telemetry-mode
                    // bring-up minus the model-name read (already in hand).
                    if (!_detectionState.NewWheelDetected && !_detectionState.OldWheelDetected
                        && IsValidWheelModelName(_data.WheelModelName))
                    {
                        _detectionState.NewWheelDetected = true;
                        _plugin.NoteWheelDetected();
                        LockWheelId(deviceId);
                        _deviceManager.ReadSetting("wheel-sw-version");
                        _deviceManager.ReadSetting("wheel-hw-version");
                        // FSR V1 never answers serial-a/b (0x10/00,01) or the
                        // 0x09/02/04/05/06/11 probe frames — verified unanswered even
                        // on a HEALTHY session (Jul-31 bundle, zero param failures).
                        // Each unanswered read is a param-manager miss on the wheel
                        // whose store wedges, so skip them once we know the model.
                        if (!_plugin.IsFsr1DisplayWheel)
                        {
                            _deviceManager.ReadSetting("wheel-serial-a");
                            _deviceManager.ReadSetting("wheel-serial-b");
                            _deviceManager.SendPithouseIdentityProbe(deviceId);
                        }
                        _deviceManager.ReadSettingsPaced(NewWheelCoreReadCommands);
                        MozaLog.Info($"[AZOM] New-protocol wheel detected via model-name probe on ID {deviceId}");
                        // Fall through to the resolution block below.
                    }

                    // New-protocol (0x17) wheels resolve here. ES wheels are
                    // handled in the es-wheel-model-name case (their real model
                    // comes from module id 0x18; the locked-id read on ES returns
                    // the base/motor name, so we never resolve from it here).
                    if (_detectionState.NewWheelDetected)
                    {
                        var currentModel = _data.WheelModelName;
                        if (!IsValidWheelModelName(currentModel))
                            break;

                        if (DetectWheelModelHotSwap(currentModel))
                            break;

                        // First-sight: resolve LED layout and deploy device defs.
                        if (string.IsNullOrEmpty(_detectionState.LastKnownWheelModel))
                        {
                            _detectionState.LastKnownWheelModel = currentModel;
                            _plugin.WheelModelInfo = WheelModelInfo.FromModelName(currentModel);
                            var info = _plugin.WheelModelInfo;
                            MozaLog.Debug(
                                $"[AZOM] Wheel model: {currentModel} " +
                                $"(rpm={info!.RpmLedCount}, buttons={info.ButtonLedCount}, flags={info.HasFlagLeds}, knobs={info.KnobCount})");
                            // Display sub-device probe — deferred from the
                            // initial wheel-detection site so we can skip it
                            // entirely for known-no-display wheels. The probe
                            // sends 11 frames on the dashboard session group
                            // (0x43 dev=0x17); screenless wheels appear to
                            // interpret those as dashboard-pipeline traffic and
                            // stop servicing settings reads. For unknown wheels
                            // (HasDisplay==null) the probe still runs so the UI
                            // can light the dashboard section when a display
                            // sub-device responds.
                            if (info.HasDisplay != false)
                                _deviceManager.SendDisplayProbe();
                            // Now that WheelModelInfo is resolved, send the
                            // LED-group-filtered reads. Skipping reads for LEDs
                            // the wheel doesn't have keeps PendingResponseTracker
                            // from churning on inevitable timeouts. Suppressed while
                            // the wheel's param manager is storming — this batch is
                            // the heaviest read burst we emit, and re-detect loops
                            // feeding it into a failing param store is the documented
                            // wedge path (wheel-0x17.md § Table 8 storm).
                            if (_plugin.FirmwareDebugLogForDiagnostics.ParamStormActive)
                                MozaLog.Warn("[AZOM] Skipping wheel LED-capability read batch — param-store storm active.");
                            else
                                _deviceManager.ReadSettingsPaced(
                                    BuildNewWheelLedReadCommands(info, _plugin.IsFsr1DisplayWheel));
                            if (DeviceDefinitionDeployer.DeployForModel(currentModel, _connection.DiscoveredPid))
                                _plugin.DeviceDefinitionDeployed = true;

                            // First-sight wheel detect — fire the FULL ApplyProfile,
                            // not just ApplyWheelToHardware. The Init-time
                            // AutoApplyProfileOnLaunch call at MozaPlugin.InitProfileSystem
                            // fired when no wheel was detected yet, so all the
                            // per-section gates (NewWheelDetected, DashDetected, ...)
                            // were false and nothing wrote. Now that the wheel is
                            // up, re-run the full profile apply so colors / brightness
                            // / modes / dash / base etc. all land on hardware. The
                            // call is idempotent: Apply*ToHardware reads from the
                            // profile + overlay, not from device-modified _data
                            // state, so re-firing after the Init-time no-op pass
                            // produces the same writes regardless of how much of
                            // _data has been populated by intervening read responses.
                            var initialProfile = _plugin.Settings?.ProfileStore?.CurrentProfile;
                            if (initialProfile != null)
                                _plugin.ApplyProfile(initialProfile);

                            // Auto-load the dashboard library: this wheel's
                            // configured mzdash folder plus MOZA Dashboard
                            // Studio's own project root (see
                            // MozaPlugin.ReloadDashboardLibrary).
                            var ovFolder = _plugin.ActiveTelemetryMzdashFolder;
                            MozaLog.Debug("[AZOM] Loading dashboard library from: "
                                + string.Join(", ", _plugin.DashboardLibraryFolders(ovFolder)));
                            _plugin.ReloadDashboardLibrary(ovFolder);

                            try { _plugin.ApplyTelemetrySettings(); }
                            catch (Exception ex)
                            {
                                MozaLog.Warn($"[AZOM] ApplyTelemetrySettings after wheel-model-name failed: {ex.Message}");
                            }

                            // Wheel hot-swap path: the saved profile's dashboard
                            // preference (TelemetryDashboardKey) needs to be
                            // re-asserted against the freshly-attached wheel.
                            // Without this the wheel sits on whatever slot it
                            // boots to (typically its persisted "last-used"
                            // dashboard, NOT the host's saved choice), so the
                            // host's tier-def emissions target one slot while
                            // the wheel renders another. ApplyProfile already
                            // does this on game-switch; queue the same retry
                            // path here so PollStatus's TickPendingDashboardRetry
                            // picks it up once configJson state arrives (~200 ms
                            // post-detect on healthy connect, can be later on
                            // hot-attach).
                            _plugin.RequestSavedDashboardReapply();

                            // ShouldDriveDashboard now has real input — make the keep/skip decision.
                            _plugin.StartTelemetryIfReady();
                        }
                    }
                    else
                    {
                        DebugIdentity("wheel-model-misrouted",
                            $"[AZOM] Wheel model (mis-routed locked-id read): {_data.WheelModelName}");
                        // A valid model name from a new-protocol-only id while the
                        // session is classified old-protocol names the wheel behind
                        // the firmware advisory (real ES wheels reply here from the
                        // base id 0x13 with the base/motor name, so they never match).
                        if (_detectionState.OldWheelDetected
                            && (deviceId == MozaProtocol.DeviceWheel || deviceId == MozaProtocol.DeviceWheel15)
                            && IsValidWheelModelName(_data.WheelModelName)
                            && !string.Equals(_detectionState.NewWheelActingOldModel, _data.WheelModelName, StringComparison.Ordinal))
                        {
                            _detectionState.NewWheelActingOldModel = _data.WheelModelName;
                            _detectionState.NewWheelActingOldProtocol = true;
                            MozaLog.Info(
                                $"[AZOM] {_data.WheelModelName} is a new-protocol wheel but answered like an " +
                                "old-protocol one — firmware update recommended");
                        }
                    }
                    break;

                case "es-wheel-model-name":
                    {
                        // ES (old-protocol) wheel identity from module id 0x18.
                        // MozaData filled Wheel* from the 0x18 responses (0x17 is
                        // silent on ES), so this gives the ES wheel a real model
                        // ("ES"). Resolve it, detect a rim hot-swap by model change
                        // (shared with the 0x17 path), and deploy the model-specific
                        // definition so the ES wheel gets a proper per-wheel page
                        // identity instead of only the generic old-proto device.
                        var esModel = _data.WheelModelName;
                        if (!IsValidWheelModelName(esModel))
                            break;
                        if (DetectWheelModelHotSwap(esModel))
                            break;
                        if (string.IsNullOrEmpty(_detectionState.LastKnownWheelModel))
                        {
                            _detectionState.LastKnownWheelModel = esModel;
                            _plugin.WheelModelInfo = WheelModelInfo.FromModelName(esModel);
                            var info = _plugin.WheelModelInfo;
                            MozaLog.Info(
                                $"[AZOM] ES wheel model resolved: {esModel} " +
                                $"(rpm={info!.RpmLedCount}, buttons={info.ButtonLedCount}, hw={_data.WheelHwVersion})");
                            if (DeviceDefinitionDeployer.DeployForModel(esModel, _connection.DiscoveredPid))
                                _plugin.DeviceDefinitionDeployed = true;
                            // Re-apply the profile now that the wheel page-GUID
                            // resolves — ES LED colours / brightness / indicator
                            // mode bind to the right per-wheel page overlay.
                            var prof = _plugin.Settings?.ProfileStore?.CurrentProfile;
                            if (prof != null)
                                _plugin.ApplyProfile(prof);
                        }
                    }
                    break;

                case "wheel-sw-version":
                    DebugIdentity("wheel-fw", $"[AZOM] Wheel FW: {_data.WheelSwVersion}");
                    break;

                case "wheel-serial-b":
                    if (!string.IsNullOrEmpty(_data.WheelSerialNumber))
                        DebugIdentity("wheel-serial",
                            $"[AZOM] Wheel serial: {MozaLog.RedactId(_data.WheelSerialNumber)}");
                    break;

                case "wheel-hw-sub":
                    if (!string.IsNullOrEmpty(_data.WheelHwSubVersion))
                        DebugIdentity("wheel-hw-sub", $"[AZOM] Wheel HW sub: {_data.WheelHwSubVersion}");
                    break;

                case "wheel-mcu-uid":
                    if (_data.WheelMcuUid.Length > 0)
                        DebugIdentity("wheel-mcu-uid",
                            $"[AZOM] Wheel MCU UID ({_data.WheelMcuUid.Length}B): " +
                            MozaLog.RedactBytesHex(_data.WheelMcuUid));
                    break;

                case "wheel-device-type":
                    if (_data.WheelDeviceType.Length > 0)
                        DebugIdentity("wheel-device-type",
                            $"[AZOM] Wheel device type: {BitConverter.ToString(_data.WheelDeviceType)}");
                    break;

                case "wheel-capabilities":
                    if (_data.WheelCapabilities.Length > 0)
                        DebugIdentity("wheel-capabilities",
                            $"[AZOM] Wheel capabilities: {BitConverter.ToString(_data.WheelCapabilities)}");
                    break;

                case "wheel-presence":
                    DebugIdentity("wheel-presence",
                        $"[AZOM] Wheel presence/ready: sub_device_count={_data.WheelSubDeviceCount}");
                    break;

                case "wheel-device-presence":
                    DebugIdentity("wheel-device-presence",
                        $"[AZOM] Wheel device presence byte: 0x{_data.WheelDevicePresence:X2}");
                    break;

                case "wheel-identity-11":
                    if (_data.WheelIdentity11.Length > 0)
                        DebugIdentity("wheel-identity-11",
                            $"[AZOM] Wheel identity-11: {BitConverter.ToString(_data.WheelIdentity11)}");
                    break;

                case "display-model-name":
                    if (!string.IsNullOrEmpty(_data.DisplayModelName))
                    {
                        DebugIdentity("display-model", $"[AZOM] Display model: {_data.DisplayModelName}");
                        // Bridged CM2 confirmed by display identity. The CM2 itself is
                        // driven by the dedicated _cm2Sender at 0x14 (EnsureCm2Pipeline,
                        // already running) — NOT the main sender. This block only
                        // re-asserts the CM2 meter LED config now that the dash is
                        // confirmed and the profile is loaded.
                        //
                        // Skipped for a discriminated CM1: the DeployDashboard below
                        // would re-write the speculative CM2 definition that
                        // LatchDashAsCm1 deleted (any later display re-probe resurrects
                        // the wrong device entry), and the cm2-* meter registers the
                        // re-assert pushes don't exist on a CM1.
                        if (_plugin.IsCm2BehindBaseCandidate && !_plugin.DashIsCm1)
                        {
                            MozaLog.Info($"[AZOM] Bridged CM2 display confirmed: {_data.DisplayModelName} — re-asserting CM2 meter config");
                            if (DeviceDefinitionDeployer.DeployDashboard(_connection.DiscoveredPid))
                                _plugin.DeviceDefinitionDeployed = true;
                            // Push the CM2 meter LED config (modes/thresholds/colors,
                            // group 0x32) now that the base-bridged CM2 is CONFIRMED.
                            // The earlier MarkDashDetected apply races ahead of this —
                            // it runs before the profile is loaded and before
                            // IsCm2BehindBaseCandidate is true, so ApplyCm2DashboardConfig
                            // never fires there. Without this re-apply the meter is never
                            // put into telemetry LED mode and its RPM/flag LEDs stay dark
                            // (KS+CM2 bundle 2026-06-06: zero group-0x32 frames on the wire).
                            try { _plugin.HardwareApplier.ApplyDashToHardware(_plugin.Settings?.ProfileStore?.CurrentProfile); }
                            catch (Exception ex) { MozaLog.Debug($"[AZOM] CM2-on-base ApplyDashToHardware skipped: {ex.Message}"); }
                        }
                        // Re-arm the wedge-recovery one-shot now that we know
                        // a display is responsive — a future wheel hot-swap
                        // that wedges should get its own recovery attempt.
                        _plugin.ClearDisplayWedgeRecovery();
                        // Wheels not in KnownModels (HasDisplay==null) get their
                        // authoritative "has display" signal from this probe;
                        // trigger StartTelemetryIfReady so the fallback path
                        // (HasDisplay==null → IsDisplayDetected) actually starts.
                        _plugin.StartTelemetryIfReady();
                    }
                    break;
                case "display-hw-version":
                    if (!string.IsNullOrEmpty(_data.DisplayHwVersion))
                    {
                        DebugIdentity("display-hw", $"[AZOM] Display HW: {_data.DisplayHwVersion}");
                        // HW version alone satisfies IsDisplayDetected — re-trigger
                        // the deferred start for empty-model-name wheels (W17).
                        NoteDisplayIdentityReady();
                    }
                    break;
                case "display-sw-version":
                    if (!string.IsNullOrEmpty(_data.DisplaySwVersion))
                    {
                        DebugIdentity("display-fw", $"[AZOM] Display FW: {_data.DisplaySwVersion}");
                        // SW version alone satisfies IsDisplayDetected — re-trigger
                        // the deferred start for empty-model-name wheels (W17).
                        NoteDisplayIdentityReady();
                    }
                    break;
                case "display-serial":
                    if (!string.IsNullOrEmpty(_data.DisplaySerialNumber))
                        DebugIdentity("display-serial",
                            $"[AZOM] Display serial: {MozaLog.RedactId(_data.DisplaySerialNumber)}");
                    break;
                case "display-presence":
                    DebugIdentity("display-presence",
                        $"[AZOM] Display presence/ready: sub_device_count={_data.DisplaySubDeviceCount}");
                    break;
                case "display-device-presence":
                    DebugIdentity("display-device-presence",
                        $"[AZOM] Display device presence byte: 0x{_data.DisplayDevicePresence:X2}");
                    break;
                case "display-device-type":
                    if (_data.DisplayDeviceType.Length > 0)
                        DebugIdentity("display-device-type",
                            $"[AZOM] Display device type: {BitConverter.ToString(_data.DisplayDeviceType)}");
                    break;
                case "display-capabilities":
                    if (_data.DisplayCapabilities.Length > 0)
                        DebugIdentity("display-capabilities",
                            $"[AZOM] Display capabilities: {BitConverter.ToString(_data.DisplayCapabilities)}");
                    break;
                case "display-identity-11":
                    if (_data.DisplayIdentity11.Length > 0)
                        DebugIdentity("display-identity-11",
                            $"[AZOM] Display identity-11: {BitConverter.ToString(_data.DisplayIdentity11)}");
                    break;
                case "display-mcu-uid":
                    // Already logged before the value<0 guard at the top.
                    break;

                case "wheel-rpm-value1":
                    if (!_detectionState.NewWheelDetected && !_detectionState.OldWheelDetected)
                    {
                        _detectionState.OldWheelDetected = true;
                        // Stamp first-detect time (mirror of new-protocol path).
                        // WheelModelInfo stays null for old-protocol wheels (the
                        // wheel-model-name resolve below is gated on NewWheelDetected),
                        // so the display-wedge watchdog is gated to NewWheelDetected
                        // only and never runs for old wheels; the timestamp is cheap
                        // and keeps both branches symmetric.
                        _plugin.NoteWheelDetected();
                        LockWheelId(deviceId);
                        _plugin.HardwareApplier.ApplyWheelToHardware(_plugin.Settings?.ProfileStore?.CurrentProfile);
                        _deviceManager.ReadSetting("wheel-model-name");
                        _deviceManager.ReadSetting("wheel-sw-version");
                        _deviceManager.ReadSetting("wheel-hw-version");
                        _deviceManager.ReadSetting("wheel-serial-a");
                        _deviceManager.ReadSetting("wheel-serial-b");
                        // ES wheel identity lives at the wheel's own module id 0x18.
                        // The locked-id wheel-model-name above returns the BASE/motor
                        // name on ES (0x13 is the base), so probe 0x18 directly to
                        // learn the real model ("ES"). 0x18 is silent on a non-ES old
                        // wheel, so these are a no-op there (and modern 0x17 wheels
                        // never reach this branch). Handled in the es-wheel-model-name
                        // case, which deploys the model-specific definition.
                        _deviceManager.ReadSetting("es-wheel-model-name");
                        _deviceManager.ReadSetting("es-wheel-hw-version");
                        _deviceManager.ReadSetting("es-wheel-sw-version");
                        _deviceManager.ReadSetting("es-wheel-mcu-uid");
                        _deviceManager.SendPithouseIdentityProbe(deviceId);
                        _deviceManager.ReadSettingsPaced(OldWheelSettingsReadCommands);
                        // Device definition is deferred: an ES/ESX wheel deploys its
                        // model-specific definition (e.g. "MOZA ES") from the
                        // es-wheel-model-name case once id 0x18 answers. A wheel that
                        // never resolves a model gets no definition — the generic
                        // old-proto fallback was retired (no such wheel reaches it).
                        MozaLog.Info($"[AZOM] Old-protocol wheel detected on ID {deviceId}");
                        // 0x17/0x15 are new-protocol-only ids — a wheel answering the
                        // settings group there but classifying old-protocol is a
                        // current-generation wheel on legacy firmware (seen on W13/FSR
                        // V2: no telemetry-mode reply, rpm-value1 answers). Surface the
                        // firmware-update banner; the model name enriches it once the
                        // wheel-model-name read below answers.
                        if (deviceId == MozaProtocol.DeviceWheel || deviceId == MozaProtocol.DeviceWheel15)
                        {
                            _detectionState.NewWheelActingOldProtocol = true;
                            MozaLog.Info(
                                $"[AZOM] Wheel on new-protocol ID {deviceId} classified old-protocol — " +
                                "firmware update recommended");
                        }
                        _plugin.StartTelemetryIfReady();
                    }
                    else if (deviceId != _deviceManager.WheelDeviceId)
                    {
                        _plugin.ResetWheelDetection(
                            $"New wheel responded on ID {deviceId} (was locked on " +
                            $"{_deviceManager.WheelDeviceId}) — hot-swap detected");
                    }
                    break;

                case "handbrake-direction":
                    MarkHandbrakeDetected();
                    break;

                case "pedals-throttle-dir":
                    MarkPedalsDetected();
                    break;

                // First evidence of a base/hub-relayed shifter — probe the model
                // resolvers. No-op on the standalone lane (already latched by PID).
                case "shifter-direction":
                    _relayShifterAnsweredSettingsRead = true;
                    ProbeRelayedShifter();
                    break;

                // shifter-type (grp 0x51 cmd 0x02). Logged on every connect-time read:
                // the {0,1} → {H-pattern, sequential} polarity is unconfirmed on real
                // hardware, so support bundles from healthy and affected shifters are
                // how it gets pinned down (v1.5.1 flipped some HGPs via this setting).
                case "shifter-apply-mode":
                    MozaLog.Info($"[AZOM] Shifter-type (apply-mode) = {value} " +
                        $"({_detectionState.ShifterModelForOwner(_deviceManager)} lane, dev {deviceId})");
                    break;

                // A brightness answer is NOT an SGP identification — a relayed HGP answers
                // it too (0x00), and acks brightness/colors writes, because it stores the
                // same EEPROM table-9 params with no LEDs wired to them (bundle 32ZD7KHW).
                // Latching SGP here is what reported that HGP as an SGP. It counts only as
                // "a shifter on this pipe is answering settings reads", the fallback
                // evidence in ProbeRelayedShifter; the model comes from device-type. The
                // value itself is stored by the owner-aware TryUpdateShifter on the inbound
                // path, and re-read by the per-model list once the model latches.
                case "shifter-brightness":
                    _relayShifterAnsweredSettingsRead = true;
                    break;

                // Generic device-type identity reply from a relayed shifter — the
                // authoritative HGP/SGP discriminator on a lane with no PID.
                case "shifter-device-type":
                    ResolveRelayedShifterModelFromDeviceType();
                    break;

                // Exploratory identity reads fired alongside the device-type probe. Logged
                // raw: a self-describing name string at 0x1A would replace the device-type
                // magic value as the discriminator (docs/protocol/open-questions.md
                // § Relayed HGP/SGP discriminator). Nothing depends on them yet.
                case "shifter-model-name":
                    MozaLog.Info($"[AZOM] Shifter model-name reply = \"{_data.RelayShifterModelName}\" (relayed lane)");
                    break;
                case "shifter-hw-version":
                    MozaLog.Info($"[AZOM] Shifter hw-version reply = \"{_data.RelayShifterHwVersion}\" (relayed lane)");
                    break;

                case "hub-port1-power":
                    if (!_detectionState.HubDetected)
                    {
                        _detectionState.HubDetected = true;
                        _deviceManager.ReadSettings(HubReadCommands);
                        // Mirror to the connection so TelemetrySender's hub-only
                        // 5-slot enumeration burst still fires for hub-attached
                        // wheels. With registry-based discovery we don't probe at
                        // port-discovery time; first 0xE4 hub reply is the trigger.
                        try { _connection.MarkHubDetected(); } catch { }
                        MozaLog.Info("[AZOM] Universal Hub detected");
                    }
                    break;
            }
        }
    }
}
