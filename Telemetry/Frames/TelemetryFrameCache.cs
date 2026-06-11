using System;
using MozaPlugin.Protocol;

namespace MozaPlugin.Telemetry.Frames
{
    /// <summary>
    /// Pre-cached and per-call frame construction for <see cref="TelemetrySender"/>:
    /// the static keepalive / parity-poll / LED-poll frames, the per-start cached
    /// enable/mode/sequence/heartbeat frames, the display probe builders (live
    /// <see cref="TelemetrySender.TargetDeviceId"/> read), and the lazy per-page
    /// 7C:27/7C:23 display-config cache. All Send*/TickEmit* emission stays in
    /// the sender; this class only builds and caches bytes.
    /// </summary>
    internal sealed class TelemetryFrameCache
    {
        private readonly TelemetrySender _sender;

        internal TelemetryFrameCache(TelemetrySender sender)
        {
            _sender = sender;
        }

        // Pre-cached frames (built once per Start, reused every tick)
        private byte[] _cachedEnableFrame = null!;
        private byte[] _cachedModeFrame = null!;
        private byte[] _cachedSequenceFrame = null!;
        private byte[][] _cachedHeartbeatFrames = null!;

        internal byte[] EnableFrame => _cachedEnableFrame;
        internal byte[] ModeFrame => _cachedModeFrame;
        internal byte[][] HeartbeatFrames => _cachedHeartbeatFrames;

        // Group 0x43 N=1 device-ping frames sent ~1 Hz. Static — device IDs and
        // checksums never vary, so the byte[]s outlive any sender instance.
        internal static readonly byte[] DashKeepaliveFrameDash = BuildKeepaliveFrame(MozaProtocol.DeviceDash);
        internal static readonly byte[] DashKeepaliveFrame15 = BuildKeepaliveFrame(0x15);
        internal static readonly byte[] DashKeepaliveFrameWheel = BuildKeepaliveFrame(MozaProtocol.DeviceWheel);
        // CM2 standalone dashboard pings its bridge/main at 0x12; the dash/15/wheel
        // trio above never reaches CM2's expected target.
        internal static readonly byte[] DashKeepaliveFrameMain = BuildKeepaliveFrame(MozaProtocol.DeviceMain);

        // Peripheral output-poll frames — wire-parity polls (handbrake/pedals).
        // Cadence: presence ~22 Hz, handbrake-output ~10 Hz, pedals ~7 Hz.
        internal static readonly byte[] HandbrakePresenceFrame = BuildShortFrame(0x5A, 0x1B, new byte[] { 0x00 });
        internal static readonly byte[] HandbrakeOutputFrame   = BuildShortFrame(0x5D, 0x1B, new byte[] { 0x01, 0x00, 0x00 });
        internal static readonly byte[] PedalThrottleOutFrame  = BuildShortFrame(0x25, 0x19, new byte[] { 0x01, 0x00, 0x00 });
        internal static readonly byte[] PedalBrakeOutFrame     = BuildShortFrame(0x25, 0x19, new byte[] { 0x02, 0x00, 0x00 });
        internal static readonly byte[] PedalClutchOutFrame    = BuildShortFrame(0x25, 0x19, new byte[] { 0x03, 0x00, 0x00 });

        // LED state read polls (`0x40/0x17 1F 03 [group] 00 00 00 00`).
        // Group 1 (RPM bar) ~1 Hz; group 2 (Single) ~0.2 Hz.
        //
        // The 1F 03 [group] sub-cmd shape is not in the documented MozaCommandDatabase
        // read set and Universal HUB firmware logs an `Unexpected cmd: 31` per poll
        // (W17 CS-Pro observed ~8/min). The polls were deleted on 2026-05-27 for
        // exactly that reason — but the deletion regressed RPM-LED telemetry-mode
        // engagement on the GS V2 Pro (firmware variant reporting bare "GS"):
        // wheel-telemetry-rpm-colors / wheel-send-rpm-telemetry frames no longer
        // visibly light the RPM strip without these read polls in the keepalive
        // mix. See `project_parity_polls_load_bearing` memory — the wheel uses
        // the *requests* (not their responses) as the host-is-live heartbeat for
        // per-group telemetry engagement; ~1 Hz is enough. Restored 2026-05-28.
        // If the firmware-warning noise becomes a problem, replace with a
        // documented read-set shape rather than removing entirely.
        internal static readonly byte[] LedStatePollGroup1 = BuildShortFrame(0x40, 0x17, new byte[] { 0x1F, 0x03, 0x01, 0x00, 0x00, 0x00, 0x00 });
        internal static readonly byte[] LedStatePollGroup2 = BuildShortFrame(0x40, 0x17, new byte[] { 0x1F, 0x03, 0x02, 0x00, 0x00, 0x00, 0x00 });

        private static byte[] BuildShortFrame(byte group, byte dev, byte[] payload)
        {
            var frame = new byte[payload.Length + 5];
            frame[0] = MozaProtocol.MessageStart;
            frame[1] = (byte)payload.Length;
            frame[2] = group;
            frame[3] = dev;
            Array.Copy(payload, 0, frame, 4, payload.Length);
            frame[frame.Length - 1] = MozaProtocol.CalculateWireChecksum(frame, frame.Length - 1);
            return frame;
        }

        private static byte[] BuildKeepaliveFrame(byte dev)
        {
            var frame = new byte[] { MozaProtocol.MessageStart, 0x01, MozaProtocol.TelemetrySendGroup, dev, 0x00, 0x00 };
            frame[5] = MozaProtocol.CalculateWireChecksum(frame);
            return frame;
        }

        // ── Cached frame construction ───────────────────────────────────────

        internal void BuildCachedFrames()
        {
            _cachedModeFrame = BuildStaticFrame(new byte[] {
                MozaProtocol.MessageStart, 4,
                MozaProtocol.TelemetryModeGroup, MozaProtocol.DeviceWheel,
                0x28, 0x02, 0x01, 0x00 });

            _cachedEnableFrame = BuildStaticFrame(new byte[] {
                MozaProtocol.MessageStart, 6,
                MozaProtocol.BaseSendTelemetry, MozaProtocol.DeviceWheel,
                0xFD, 0xDE, 0x00, 0x00, 0x00, 0x00 });

            _cachedSequenceFrame = BuildStaticFrame(new byte[] {
                MozaProtocol.MessageStart, 6,
                0x2D, MozaProtocol.DeviceBase,
                0xF5, 0x31, 0x00, 0x00, 0x00, 0x00 });

            _cachedHeartbeatFrames = new byte[13][];
            for (int i = 0; i < 13; i++)
            {
                byte dev = (byte)(18 + i);
                var frame = new byte[] { MozaProtocol.MessageStart, 0x00, 0x00, dev, 0x00 };
                frame[4] = MozaProtocol.CalculateWireChecksum(frame);
                _cachedHeartbeatFrames[i] = frame;
            }
        }

        private static byte[] BuildStaticFrame(byte[] body)
        {
            var frame = new byte[body.Length + 1];
            Array.Copy(body, 0, frame, 0, body.Length);
            frame[frame.Length - 1] = MozaProtocol.CalculateWireChecksum(body);
            return frame;
        }

        private byte _sequenceCounter;

        internal void ResetSequenceCounter() => _sequenceCounter = 0;

        internal byte[] BuildSequenceCounterFrame()
        {
            byte seq = _sequenceCounter++;
            _cachedSequenceFrame[9] = seq;
            _cachedSequenceFrame[10] = MozaProtocol.CalculateWireChecksum(
                _cachedSequenceFrame, _cachedSequenceFrame.Length - 1);
            // Return a copy: the write queue holds a reference until the write thread
            // drains it, and we mutate _cachedSequenceFrame on the next tick.
            var copy = new byte[_cachedSequenceFrame.Length];
            Array.Copy(_cachedSequenceFrame, copy, copy.Length);
            return copy;
        }

        // ── Display probe frames (live TargetDeviceId read) ─────────────────

        internal byte[] BuildDisplayFrame(byte cmd)
        {
            var frame = new byte[] { MozaProtocol.MessageStart, 0x01,
                MozaProtocol.TelemetrySendGroup, _sender.TargetDeviceId,
                cmd, 0x00 };
            frame[5] = MozaProtocol.CalculateWireChecksum(frame);
            return frame;
        }

        internal byte[] BuildDisplayFrameWithData(byte cmd, byte[] data)
        {
            var frame = new byte[4 + 1 + data.Length + 1]; // start+N+grp+dev + cmd + data + checksum
            frame[0] = MozaProtocol.MessageStart;
            frame[1] = (byte)(1 + data.Length); // N = cmd + data
            frame[2] = MozaProtocol.TelemetrySendGroup;
            frame[3] = _sender.TargetDeviceId;
            frame[4] = cmd;
            Array.Copy(data, 0, frame, 5, data.Length);
            frame[frame.Length - 1] = MozaProtocol.CalculateWireChecksum(frame);
            return frame;
        }

        // ── Display-config cache ────────────────────────────────────────────

        // Lazy per-page cache for the 7C:27/7C:23 display-config frames sent
        // ~1 Hz. Invalidated when the profile's page count changes; rebuilt on
        // first SendDisplayConfig() after that.
        private byte[][]? _cachedDisplayConfigFrames;
        private int _cachedDisplayConfigPageCount;

        /// <summary>Drop the per-page display-config cache (TargetDeviceId change).</summary>
        internal void InvalidateDisplayConfig()
        {
            _cachedDisplayConfigFrames = null;
            _cachedDisplayConfigPageCount = 0;
        }

        internal byte[][] GetDisplayConfigFrames(int pageCount)
        {
            EnsureDisplayConfigCache(pageCount);
            return _cachedDisplayConfigFrames!;
        }

        private void EnsureDisplayConfigCache(int pageCount)
        {
            if (_cachedDisplayConfigFrames != null && _cachedDisplayConfigPageCount == pageCount)
                return;

            var frames = new byte[pageCount * 3][];
            for (int page = 0; page < pageCount; page++)
            {
                byte b2 = (byte)(0x05 + 2 * page);
                byte b4 = (byte)(0x03 + 2 * page);
                byte z  = (byte)(0x06 + 2 * page);
                byte ab2 = (byte)(0x07 + 2 * page);
                byte ab4 = (byte)(0x05 + 2 * page);

                var configFrame = new byte[] { MozaProtocol.MessageStart, 0x0A, MozaProtocol.TelemetrySendGroup,
                    MozaProtocol.DeviceWheel, 0x7C, 0x27, 0x0F, 0x80, b2, 0x00, b4, 0x00, 0xFE, 0x01, 0x00 };
                configFrame[14] = MozaProtocol.CalculateWireChecksum(configFrame);

                var activateFrame = new byte[] { MozaProtocol.MessageStart, 0x0A, MozaProtocol.TelemetrySendGroup,
                    MozaProtocol.DeviceWheel, 0x7C, 0x23, 0x46, 0x80, ab2, 0x00, ab4, 0x00, 0xFE, 0x01, 0x00 };
                activateFrame[14] = MozaProtocol.CalculateWireChecksum(activateFrame);

                var configFrame2 = new byte[] { MozaProtocol.MessageStart, 0x06, MozaProtocol.TelemetrySendGroup,
                    MozaProtocol.DeviceWheel, 0x7C, 0x27, 0x0F, 0x00, z, 0x00, 0x00 };
                configFrame2[10] = MozaProtocol.CalculateWireChecksum(configFrame2);

                frames[page * 3 + 0] = configFrame;
                frames[page * 3 + 1] = activateFrame;
                frames[page * 3 + 2] = configFrame2;
            }

            _cachedDisplayConfigFrames = frames;
            _cachedDisplayConfigPageCount = pageCount;
        }
    }
}
