using System;
using System.Collections.Generic;
using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.PitHouseUdp.Handlers
{
    /// <summary>
    /// PacketId 3 — set steering lock. Body shape (observed from RSF):
    /// <code>
    ///   { "Head": { "PacketId": 3, "Version": "1.0.0" },
    ///     "Payload": { "MotSetSteer_MaximumAngle": &lt;deg&gt;,
    ///                  "MotSetSteer_LimitAngle":   &lt;deg&gt; } }
    /// </code>
    /// Both fields are degrees, clamped to the wheelbase model's
    /// supported range (RSF uses 90–2000° for every R-series base).
    /// PitHouse-style fire-and-forget — no reply expected.
    /// <para>
    /// Translation: both fields route to existing
    /// <see cref="HardwareApplier"/> commands that the CoAP server's
    /// <c>LimitAngle</c> resource already drives — <c>base-limit</c>
    /// (grp 0x29 cmd 0x01) and <c>base-max-angle</c> (grp 0x29 cmd
    /// 0x17). No new wheelbase-side surface needed; this is purely
    /// a second protocol skin over the same EEPROM cells.
    /// </para>
    /// </summary>
    internal sealed class SteerLockWriteHandler : IPitHousePacketHandler
    {
        public int PacketId => 3;
        public string Name => "SteerLock write";

        // Match RSF's clamp range. The wheelbase firmware rejects values
        // outside its physical capability anyway, but bounding here lets
        // us log a single sensible message instead of relying on the
        // wheel to silently refuse.
        private const int MinDegrees = 90;
        private const int MaxDegrees = 2000;

        private readonly MozaData _data;
        private readonly HardwareApplier _hardware;

        public SteerLockWriteHandler(MozaData data, HardwareApplier hardware)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
            _hardware = hardware ?? throw new ArgumentNullException(nameof(hardware));
        }

        public void Handle(PitHousePacket request, PitHouseReplyContext ctx)
        {
            if (request.Payload is not Dictionary<string, object> map)
            {
                ctx.Summary = $"ignored — payload is not a map ({request.Payload?.GetType().Name ?? "null"})";
                MozaLog.Warn($"[PitHouseUdp] PacketId 3: {ctx.Summary}");
                return;
            }

            int? maximumAngle = TryReadInt(map, "MotSetSteer_MaximumAngle");
            int? limitAngle = TryReadInt(map, "MotSetSteer_LimitAngle");

            if (maximumAngle == null && limitAngle == null)
            {
                ctx.Summary = "ignored — neither MaximumAngle nor LimitAngle present";
                MozaLog.Warn("[PitHouseUdp] PacketId 3: " + ctx.Summary);
                return;
            }

            int? appliedMaxDeg = maximumAngle.HasValue ? Clamp(maximumAngle.Value, "MaximumAngle") : null;
            int? appliedLimitDeg = limitAngle.HasValue ? Clamp(limitAngle.Value, "LimitAngle") : null;

            // CRITICAL UNIT CONVERSION: the wheelbase wire protocol stores
            // rotation in half-degree units (1 raw = 2°). RSF — and other
            // third-party tools that copied PitHouse's external API — pass
            // whole degrees. The plugin's own slider does the same divide
            // before writing (UI/SettingsControl.xaml.cs RotationSlider_ValueChanged:
            // `int raw = deg / 2`). Without this conversion the wheel
            // either clamps silently or applies twice the requested lock,
            // which manifests as "RSF wrote but nothing changed".
            int? appliedMaxRaw = appliedMaxDeg / 2;
            int? appliedLimitRaw = appliedLimitDeg / 2;

            // Snapshot once to keep the reported reason consistent with what
            // WriteIfBaseConnected actually saw. Avoids the race where the
            // flag flips between our check and the write call.
            bool baseConnected = _data.IsBaseConnected;

            if (!baseConnected)
            {
                // WriteIfBaseConnected would have silently no-op'd. Surface
                // that to the UI so the user doesn't think the wheel applied
                // a value it never received.
                ctx.Summary = $"SKIPPED — base not connected (would have set max={appliedMaxDeg?.ToString() ?? "-"}° limit={appliedLimitDeg?.ToString() ?? "-"}°)";
                MozaLog.Warn($"[PitHouseUdp] SteerLock write SKIPPED (base not connected) — requested max={appliedMaxDeg?.ToString() ?? "-"}° limit={appliedLimitDeg?.ToString() ?? "-"}° from={ctx.OriginalSender}");
                return;
            }

            // Apply each field independently — the wire allows either or both.
            if (appliedMaxRaw.HasValue) _hardware.WriteIfBaseConnected("base-max-angle", appliedMaxRaw.Value);
            if (appliedLimitRaw.HasValue) _hardware.WriteIfBaseConnected("base-limit", appliedLimitRaw.Value);

            string summary = $"applied max={appliedMaxDeg?.ToString() ?? "-"}° limit={appliedLimitDeg?.ToString() ?? "-"}° (raw {appliedMaxRaw?.ToString() ?? "-"}/{appliedLimitRaw?.ToString() ?? "-"})";
            ctx.Summary = summary;
            MozaLog.Debug($"[PitHouseUdp] SteerLock write {summary} from={ctx.OriginalSender}");
        }

        private static int? TryReadInt(Dictionary<string, object> map, string key)
        {
            if (!map.TryGetValue(key, out var raw)) return null;
            // CborReader returns int / uint / ulong depending on width.
            switch (raw)
            {
                case int i: return i;
                case uint u when u <= int.MaxValue: return (int)u;
                case ulong ul when ul <= int.MaxValue: return (int)ul;
                default:
                    MozaLog.Warn($"[PitHouseUdp] PacketId 3: '{key}' has unsupported value type '{raw?.GetType().Name ?? "null"}'");
                    return null;
            }
        }

        private static int Clamp(int value, string fieldDiagnostic)
        {
            if (value < MinDegrees)
            {
                MozaLog.Debug($"[PitHouseUdp] {fieldDiagnostic} {value}° below min — clamping to {MinDegrees}°");
                return MinDegrees;
            }
            if (value > MaxDegrees)
            {
                MozaLog.Debug($"[PitHouseUdp] {fieldDiagnostic} {value}° above max — clamping to {MaxDegrees}°");
                return MaxDegrees;
            }
            return value;
        }
    }
}
