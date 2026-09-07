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
        // Base model-name halves (dev 0x12 group 0x07 cmd 0x01 / 0x02), stitched
        // into MozaData.BaseModelName as they arrive. Held here because this
        // handler is the only thing that composes them.
        private volatile string? _baseModelChunk1;
        private volatile string? _baseModelChunk2;

        private void OnMessageReceived(byte[] data) => OnMessageReceived(data, fromDashboard: false);

        // fromDashboard: frame arrived on the standalone-USB dashboard pipe (CM2 on
        // its own port) rather than the wheelbase pipe. Both pipes share this handler
        // and both tag their own main MCU as raw 0x21, so device id alone can't tell
        // the R5 base from a standalone CM2.
        private void OnMessageReceived(byte[] data, bool fromDashboard)
        {
            // Shutdown guard: serial reader may deliver frames after End() begins.
            if (IsShuttingDown) return;

            // Firmware debug frames (raw wire group 0x0E, subtype 0x05) carry
            // unsolicited ASCII status / log lines from the wheel-bus firmware
            // (main bridge / wheel / display). They're not part of the
            // request/response protocol — capture for diagnostics visibility,
            // then short-circuit so MozaResponseParser doesn't waste cycles
            // trying to match them against the command database.
            if (data.Length >= 4
                && data[0] == MozaProtocol.FirmwareDebugGroup
                && data[2] == 0x05)
            {
                byte rawDeviceId = data[1];
                string text;
                try
                {
                    // Body is ASCII text starting at data[3]. Trim trailing
                    // newline / null padding so the ring buffer entries are
                    // compact and don't contain control chars that mess up
                    // the diagnostics text layout. Non-printable bytes
                    // become '?' under the ASCII decode's error replacement.
                    text = System.Text.Encoding.ASCII
                        .GetString(data, 3, data.Length - 3)
                        .TrimEnd('\n', '\r', '\0');
                }
                catch
                {
                    text = $"<{data.Length - 3} bytes>";
                }
                _firmwareDebugLog.Record(rawDeviceId, text);
                // The wheel streams these 0x0E logs (dev 0x71) continuously whenever it
                // is physically connected — they only stop on a real rim/link drop. So
                // they are positive "wheel is alive" evidence: count them against the
                // hot-swap miss counter. Marked unconditionally (not via the id-matched
                // MarkWheelResponse) because the 0x71 log channel identifies the wheel
                // by itself, regardless of whether it's locked on 23/21/19. Without
                // this, a wheel that keeps logging but stops answering the periodic
                // wheel-model-name poll (the FSR1 firmware does exactly this after
                // initial detection) trips the poll-miss watchdog and gets re-detected
                // on a ~20 s loop — a phantom "disconnection". A genuine disconnect
                // still fires the watchdog (the logs stop too). Verified against
                // "Disconnection issue.pcapng".
                if (rawDeviceId == 0x71)
                    _deviceManager.MarkWheelAlive();
                // FSR V1 reports its current dashboard/page index via this log on
                // every switch (incl. wheel-side HID combo): "Table 7, Param 6
                // Written: <N>". Parse it so the plugin follows wheel-initiated
                // switches. See docs/protocol/devices/wheel-0x17.md § Group 0x42.
                if (rawDeviceId == 0x71 && IsFsr1DisplayWheel)
                    _fsr1Cm1Mapping.TryFollowFsr1DashboardLog(text);
                // A CM1 base-bridged dash reports its current page via the byte-identical
                // "Table 7, Param 6 Written: N" log on dev 0x41 (0x14 swapped). Follow it.
                if (rawDeviceId == 0x41 && DashIsCm1)
                    _fsr1Cm1Mapping.TryFollowCm1DashboardLog(text);
                // CM2 meter heartbeat vocabulary identifies its firmware era (LED
                // command family) — see DetectCm2LedFirmwareEra. A base-bridged CM2
                // logs as the dash sub-device (raw 0x41) on the wheelbase pipe; a
                // standalone-USB CM2 logs as its own main MCU (raw 0x21) on the
                // dashboard pipe.
                if ((rawDeviceId == 0x41 && !DashIsCm1) || (fromDashboard && rawDeviceId == 0x21))
                    DetectCm2LedFirmwareEra(text);
                // The main bridge logs steering-wheel (rim) attach/detach edges
                // here as "steer_connected <N>" / "Gpw Wheel Disconnected". A rim
                // pull is NOT a USB/serial disconnect, so the poll-miss hot-swap
                // path never fires — this is the only signal that tears down the
                // stale cached identity/catalog. See TryHandleWheelConnectionLog.
                // Wheelbase pipe only: a standalone CM2's 0x21 logs are the dash's
                // own MCU, not the main bridge.
                if (rawDeviceId == 0x21 && !fromDashboard)
                    TryHandleWheelConnectionLog(text);
                if (MozaLog.WireDebugEnabled)
                    // NoRing: these arrive at ~1/s per device and are already
                    // retained in FirmwareDebugLog (its own ring, printed in the
                    // diagnostics dump) and in the wire trace. Mirroring them into
                    // MozaLog's ring as well cost 54 % of the bundle's log — the
                    // connect/handshake lines were long gone by report time.
                    MozaLog.DebugNoRing(
                        $"[AZOM] firmware-debug src={(rawDeviceId == 0x21 ? "main" : rawDeviceId == 0x71 ? "wheel" : rawDeviceId == 0xB1 ? "display" : $"0x{rawDeviceId:X2}")}: {text}");
                return;
            }
            // Other 0x0E variants we don't yet know how to decode — drop
            // silently (preserves prior behaviour for unknown subtypes).
            if (data.Length >= 1 && data[0] == MozaProtocol.FirmwareDebugGroup)
                return;

            // Filter SerialStream control frames (0xC3 / 7C/FC + 00) — session-
            // management chunks handled by TelemetrySender, not command responses.
            if (data.Length >= 4 && data[0] == MozaProtocol.SerialStreamRespGroup &&
                (data[2] == MozaProtocol.SerialStreamOpcodeData ||
                 data[2] == MozaProtocol.SerialStreamOpcodeCtrl) && data[3] == 0x00)
                return;

            // Filter wheel's 7c:23 dashboard-activate advertisements — informational,
            // absorbed by TelemetrySender.
            if (data.Length >= 4 && data[0] == MozaProtocol.SerialStreamRespGroup
                && data[2] == MozaProtocol.SerialStreamOpcodeData && data[3] == 0x23)
                return;

            // Filter group 0x40 channel-config burst echoes (1E XX, 28 XX).
            // Wheel returns stored EEPROM values; mark wheel-alive and swallow.
            if (data.Length >= 4 && data[0] == MozaProtocol.WheelChannelCfgRespGroup
                && data[1] == MozaProtocol.WheelDeviceIdSwapped
                && (data[2] == MozaProtocol.WheelCfgOpcodeChannelEnable ||
                    data[2] == MozaProtocol.WheelCfgOpcodeMultiFunction))
            {
                // Capture raw 28:00 / 28:01 reply bytes; semantics not yet
                // decoded — stored raw for offline correlation against game state.
                if (data.Length >= 6 && data[2] == MozaProtocol.WheelCfgOpcodeMultiFunction)
                {
                    if (data[3] == 0x00 && _data != null)
                    {
                        _data.Last28x00Byte5 = data[5];
                        _data.Last28x00ByteValid = true;
                        _data.Last28xReplyTickMs = Environment.TickCount;
                    }
                    else if (data[3] == 0x01 && _data != null)
                    {
                        _data.Last28x01Byte4 = data[4];
                        _data.Last28x01Byte5 = data[5];
                        _data.Last28x01BytesValid = true;
                        _data.Last28xReplyTickMs = Environment.TickCount;
                    }
                }
                _deviceManager.MarkWheelResponse(MozaProtocol.SwapNibbles(data[1]));
                return;
            }

            // Empty presence-probe ACK (PitHouse-style): host sent
            // `7e 00 00 dev_id chk`, device replied `7e 00 80 swap(dev_id) chk`.
            // The on-wire frame has been stripped of its 0x7e + length and
            // checksum by MozaSerialConnection, so `data` here is
            // {group=0x80, dev_id_swapped} — 2 bytes total. Route to the per-id
            // first-sight detection helper. SendPresenceProbe in PollStatus is
            // the only caller that emits empty probes today; the wheel itself
            // never spontaneously sends these.
            if (data.Length == 2 && data[0] == 0x80)
            {
                byte deviceId = MozaProtocol.SwapNibbles(data[1]);
                OnPresenceProbeAck(deviceId);
                return;
            }

            // CM1 param-read reply: the discriminator probe (SendCm1ParamProbe,
            // group 0x0E → dev 0x14) was answered with a group-0x8E frame from the
            // dash (dev 0x41). A tier-def CM2 doesn't answer it, so this is a
            // positive CM1 signal for TickCm1Discriminator's fast path. Not a
            // command-DB entry — flag and short-circuit.
            if (data.Length >= 2 && data[0] == 0x8E && data[1] == 0x41)
            {
                _dualDisplay?.NoteDashParamReadAnswered();
                return;
            }

            var result = MozaResponseParser.Parse(data);
            if (!result.HasValue)
            {
                // Known wheel write echoes with no command DB entry — treat as
                // keepalive from the wheel device id. See MozaProtocol.WheelEchoPrefixes.
                if (MozaProtocol.IsWheelEcho(data))
                {
                    _deviceManager.MarkWheelResponse(MozaProtocol.SwapNibbles(data[1]));
                    return;
                }

                // Any wheel-targeted response counts as "wheel is alive" even if
                // we can't decode the specific command. Prior behavior only
                // marked alive on parsed reads / known echo prefixes — wheel
                // read-responses outside those two paths (e.g. LED state poll
                // group 2 with payload prefix `1F 03 02`) were logged as
                // Unmatched and never reset PollStatus's miss counter. The
                // wheel kept answering at ~5 s cadence, but every response
                // looked like silence to the hot-swap detector, which
                // incorrectly tripped after 3 ticks (15 s) and triggered an
                // unnecessary Stop+silence-gate+restart cycle.
                if (data.Length >= 2)
                {
                    byte dev = MozaProtocol.SwapNibbles(data[1]);
                    if (dev == MozaProtocol.DeviceWheel)
                        _deviceManager.MarkWheelResponse(dev);
                }

                // Two dispatch threads (primary + dashboard pipe) reach here.
                int unmatched = System.Threading.Interlocked.Increment(ref _unmatched);
                if (unmatched <= 20 && data.Length >= 2)
                {
                    byte grp = MozaProtocol.ToggleBit7(data[0]);
                    byte dev = MozaProtocol.SwapNibbles(data[1]);
                    // BitConverter.ToString rejects startIndex == value.Length on
                    // .NET Framework even when length == 0, so guard the
                    // payload-only frames (e.g. bare `c0 71` wheel ACKs).
                    int showLen = Math.Min(data.Length - 2, 8);
                    string payload = showLen > 0
                        ? BitConverter.ToString(data, 2, showLen)
                        : "(empty)";
                    MozaLog.Debug(
                        $"[AZOM] Unmatched #{_unmatched}: rawGroup=0x{data[0]:X2} group=0x{grp:X2} " +
                        $"rawDev=0x{data[1]:X2} dev={dev} len={data.Length} " +
                        $"payload={payload}");
                }
                return;
            }

            var r = result.Value;

            // Ack the tracker that owns this pipe's reads — the dashboard lane
            // keeps its own so its retransmits go out on the CM2's port.
            if (fromDashboard)
                _dashboardManager?.PendingResponses.NoteResponse(r.Name);
            else
                PendingResponses.NoteResponse(r.Name);

            // Normalize stick-mode: old firmware sends 2-byte value (0 or 256),
            // new firmware sends 1-byte enum (0=none, 1=left, 2=right, 3=both).
            if (r.Name == "wheel-stick-mode")
            {
                if (r.PayloadLength <= 1)
                {
                    _data.WheelDualStickSupported = true;
                }
                else
                {
                    // Old 2-byte format: 0x0100 (256) = left D-pad on
                    r.IntValue = r.IntValue >= 256 ? 1 : 0;
                }
            }

            // Base model name (dev 0x12, group 0x07), arriving as two 16-byte
            // chunks. Needs "main"-typed commands: the parser hints every dev-0x12
            // reply as "main" and drops commands whose DeviceType differs, so the
            // wheel-typed command retargeted at 0x12 never matched and this field
            // stayed empty.
            if ((r.Name == "main-model-name" || r.Name == "main-model-name-b")
                && r.ArrayValue != null)
            {
                var chunk = MozaData.ParseNullTerminatedString(r.ArrayValue);
                if (r.Name == "main-model-name") _baseModelChunk1 = chunk;
                else _baseModelChunk2 = chunk;

                var baseName = ((_baseModelChunk1 ?? string.Empty) + (_baseModelChunk2 ?? string.Empty)).Trim();
                if (!string.IsNullOrEmpty(baseName) && _data.BaseModelName != baseName)
                {
                    _data.BaseModelName = baseName;
                    MozaLog.Debug($"[AZOM] Base identity: {baseName}");

                    // Latch the strip geometry as soon as the model is recognised.
                    // BaseModelName itself is blanked by ClearWheelIdentity on any
                    // rim swap or transient reconnect, so the emitter must not keep
                    // re-deriving from it.
                    if (Devices.BaseModelInfo.IsKnown(baseName))
                        _data.BaseAmbientLedsPerStrip = Devices.BaseModelInfo.LedsPerStrip(baseName);

                    // This string NAMES the device definition and selects its LED
                    // count, so nothing can be written until it lands. DeviceProber
                    // reads the model before the capability probes so it is normally
                    // known by then, but a dropped or reordered reply would leave the
                    // base with no definition at all — deploy once the real name
                    // arrives. Idempotent: the deployer's staleness check no-ops when
                    // the file already matches.
                    if (Devices.BaseModelInfo.IsKnown(baseName)
                        && (DetectionState.BaseAmbientLedSupported || _data.BaseSupportsLfe))
                    {
                        if (Devices.Extensions.DeviceDefinitionDeployer.DeployForBaseModel(
                                baseName, _deviceManager?.Connection?.DiscoveredPid,
                                DetectionState.BaseAmbientLedSupported,
                                _data.BaseFwVersion != 0 ? WheelbaseWantsShakeItHaptics : (bool?)null))
                            DeviceDefinitionDeployed = true;
                    }
                }
                return;
            }

            // Shifter replies share command names across HGP/SGP, so route a relayed
            // shifter's values into whichever model was detected on this pipe.
            if (!_data.TryUpdateShifter(DetectionState.ShifterModelForOwner(_deviceManager), r.Name, r.IntValue, r.ArrayValue))
            {
                _data.UpdateFromCommand(r.Name, r.IntValue);
                if (r.ArrayValue != null)
                    _data.UpdateFromArray(r.Name, r.ArrayValue);
            }

            // Persist wheel-reported sleep-bundle values so next launch reapplies them.
            _profileCoordinator.SeedSleepBundleFromResponse(r);

            // Prime the persistent-write cache from the wheel's own readback so an
            // apply whose values already match writes nothing to its flash. Only the
            // scalar flash-backed params are primed here — their write-path encoding is
            // the raw int, so device value and write value are directly comparable.
            // (idle-speed / idle-color pack mode+ms and RGB into composite keys; they
            // are left alone rather than risk a mis-encoded prime silently swallowing a
            // real user change.) See HardwareApplier.PrimeWheelCfgFromDevice.
            if (r.Name != null && r.IntValue >= 0 && TryPrimableWheelCfgKey(r.Name, out var primeKey))
                _hardwareApplier.PrimeWheelCfgFromDevice(primeKey, r.IntValue);

            // Extended LED group presence: any response from a group proves it exists.
            if (r.Name != null)
            {
                int g = -1;
                if (r.Name.StartsWith("wheel-single-",  StringComparison.Ordinal)) g = 2;
                else if (r.Name.StartsWith("wheel-knob-",    StringComparison.Ordinal)) g = 3;
                else if (r.Name.StartsWith("wheel-ambient-", StringComparison.Ordinal)) g = 4;
                if (g >= 2 && g <= 4 && DetectionState.TrySetWheelLedGroupPresent(g))
                    MozaLog.Debug($"[AZOM] Wheel LED group {g} detected");
            }

            _deviceManager.MarkWheelResponse(r.DeviceId);
            if (r.Name != null)
                _deviceProber.DetectDevices(r.Name, r.IntValue, r.DeviceId);
        }

        /// <summary>
        /// Dispatch an empty presence-probe ACK to the first-sight detection
        /// helper for the matching sub-device. Handles devices probed via
        /// <see cref="MozaDeviceManager.SendPresenceProbe"/> from PollStatus —
        /// dash / handbrake / pedals (first-sight detection) and the locked
        /// wheel (liveness heartbeat → <see cref="MozaDeviceManager.MarkWheelAlive"/>).
        /// Other device IDs (e.g. AB9, Booster) reach this path harmlessly and
        /// are ignored.
        /// </summary>
        private void OnPresenceProbeAck(byte deviceId)
        {
            switch (deviceId)
            {
                case MozaProtocol.DeviceWheel:
                    // Presence ACK from the locked wheel — the active liveness
                    // heartbeat that replaced the per-tick model-name read.
                    _deviceManager.MarkWheelAlive();
                    break;
                case MozaProtocol.DeviceDash:
                    _deviceProber.MarkDashDetected();
                    break;
                case MozaProtocol.DeviceHandbrake:
                    _deviceProber.MarkHandbrakeDetected();
                    break;
                case MozaProtocol.DevicePedals:
                    _deviceProber.MarkPedalsDetected();
                    break;
                // DeviceHPattern == DeviceSequential (0x1A). A relayed shifter has no
                // PID, so probe the model resolvers rather than latch a single flag.
                case MozaProtocol.DeviceHPattern:
                    _deviceProber.ProbeRelayedShifter();
                    break;
            }
        }

        /// <summary>
        /// Serial-message handler for the AB9 shifter's dedicated pipe. Marks the
        /// device as detected on the first parseable response, pushes saved
        /// profile settings once, and lets the AB9 settings UI snapshot the
        /// latest device-reported values via <see cref="MozaAb9DeviceManager"/>.
        /// </summary>
        private void OnAb9MessageReceived(byte[] data)
        {
            if (IsShuttingDown) return;
            if (data == null || data.Length < 2) return;

            // Filter firmware debug noise before parsing.
            if (data[0] == MozaProtocol.FirmwareDebugGroup) return;

            // Bus-hint "ab9" disambiguates from base-* commands (the AB9 main and
            // wheelbase main share device id 0x12 numerically — without the hint
            // the parser auto-tags as "base" and filters out every ab9-* match).
            var result = MozaResponseParser.Parse(data, busHint: "ab9");
            if (!result.HasValue) return;

            var r = result.Value;
            if (r.Name == null || !r.Name.StartsWith("ab9-", StringComparison.Ordinal))
                return;

            bool rising = !DetectionState.Ab9Detected;
            _ab9Manager.MarkDetected();
            // Push the FFB session-init handshake (alloc/init/commit) before any
            // hardware apply. Not gated on `rising`: the handshake is per port
            // session (the manager no-ops within one), and the detection latch is
            // process-wide state that can already be set when a re-enumerated
            // device comes back to an empty effect table.
            try { _ab9Manager.SendFfbInitSequence(); }
            catch (Exception ex) { MozaLog.Warn($"[AZOM/AB9] FFB init failed: {ex.Message}"); }
            if (rising)
            {
                DetectionState.Ab9Detected = true;
                _hardwareApplier.ApplyAb9ToHardware(_settings?.ProfileStore?.CurrentProfile);
            }

            // Status-probe + layout read-back values. Every other ab9-* name falls
            // through the switch untouched, so this stays a no-op for the ten
            // stored-setting reads the connect burst already issues.
            _data?.UpdateFromCommand(r.Name, r.IntValue);

            MozaLog.Debug($"[AZOM/AB9] {r.Name} = {r.IntValue}");
        }
    }
}
