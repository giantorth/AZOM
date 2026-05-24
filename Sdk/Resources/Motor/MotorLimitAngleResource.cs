using System;
using System.Collections.Generic;
using MozaPlugin.Hardware;
using MozaPlugin.Sdk.Cbor;

namespace MozaPlugin.Sdk.Resources.Motor
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/LimitAngle</c>. This is
    /// the only motor URI that bundles two int parameters into a single
    /// CBOR map — both the in-game soft maximum (<c>GameMaximumAngle</c>)
    /// and the hardware-enforced rotation limit (<c>LimitAngle</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// GET response shape (captured from PitHouse):
    /// <c>{"GameMaximumAngle": int, "LimitAngle": int}</c>. Key order is
    /// preserved because some CoAP clients sniff map keys positionally; we
    /// emit via <see cref="CborWriter.WriteMap(IReadOnlyList{KeyValuePair{string,object}})"/>
    /// which honours list order rather than using the dictionary overload
    /// (which is hash-order in .NET Framework 4.8).
    /// </para>
    /// <para>
    /// POST consumes the same shape and writes <c>base-max-angle</c> +
    /// <c>base-limit</c>. Missing keys are tolerated as "leave that value
    /// alone" — the underlying writes are gated on
    /// <see cref="HardwareApplier.WriteIfBaseConnected"/>'s &lt; 0 sentinel
    /// when we pass -1.
    /// </para>
    /// </remarks>
    internal sealed class MotorLimitAngleResource : CoapResourceHandler
    {
        private readonly MozaData _data;
        private readonly HardwareApplier _hardware;

        public MotorLimitAngleResource(MozaData data, HardwareApplier hardware)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
            _hardware = hardware ?? throw new ArgumentNullException(nameof(hardware));
        }

        public override CoapResourceResponse HandleGet(CoapResourceRequest req)
        {
            // Preserve the captured key order. WriteMap(KeyValuePair list)
            // emits in list order; the Dictionary overload would not. Wire
            // values are degrees; the wheelbase stores half-degrees in
            // _data.MaxAngle / _data.Limit so we multiply by 2 to convert
            // (same boundary conversion as Sdk/PitHouseUdp/Handlers/SteerLockReadHandler).
            var entries = new List<KeyValuePair<string, object>>
            {
                new KeyValuePair<string, object>("GameMaximumAngle", _data.MaxAngle * 2),
                new KeyValuePair<string, object>("LimitAngle", _data.Limit * 2),
            };
            byte[] payload = CborWriter.WriteMap(entries);
            return CoapResourceResponse.Content(payload, PayloadCodec.CFCbor);
        }

        public override CoapResourceResponse HandlePost(CoapResourceRequest req)
        {
            if (!req.HasPayload)
                return CoapResourceResponse.BadRequest("LimitAngle POST requires CBOR map body");

            Dictionary<string, int> map;
            try
            {
                map = CborReader.ReadMapStringToInt(req.Payload);
            }
            catch (CborFormatException ex)
            {
                MozaLog.Warn($"[Moza.Sdk] LimitAngle POST malformed CBOR: {ex.Message}");
                return CoapResourceResponse.BadRequest("malformed CBOR: " + ex.Message);
            }

            // Wire degrees → half-degree raw. The plugin's own slider does
            // the same divide before writing — see
            // UI/SettingsControl.xaml.cs RotationSlider_ValueChanged. Without
            // this conversion the wheel applies 2× the requested lock or
            // silently clamps, which manifests as "client wrote but the
            // wheel didn't change angle".
            if (map.TryGetValue("GameMaximumAngle", out int gameMax))
                _hardware.WriteIfBaseConnected("base-max-angle", gameMax / 2);
            if (map.TryGetValue("LimitAngle", out int limit))
                _hardware.WriteIfBaseConnected("base-limit", limit / 2);

            return CoapResourceResponse.Valid();
        }
    }
}
