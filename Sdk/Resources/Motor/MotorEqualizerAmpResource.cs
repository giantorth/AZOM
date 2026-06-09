using System;
using System.Collections.Generic;
using MozaPlugin.Hardware;
using MozaPlugin.Sdk.Cbor;

namespace MozaPlugin.Sdk.Resources.Motor
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/EqualizerAmp</c>. The
    /// 6-band FFB equalizer is bundled into a single CBOR map keyed by the
    /// nominal centre frequency of each band — keys come from the captured
    /// PitHouse response verbatim (note the awkward "_5" in 7.5/22.5).
    /// Values map 1:1 onto <see cref="MozaData.Equalizer1"/>..<c>Equalizer6</c>
    /// and the <c>base-equalizer1</c>..<c>base-equalizer6</c> commands.
    /// </summary>
    /// <remarks>
    /// Band → key → MozaData / command mapping (verified against existing
    /// MozaCommandDatabase entries):
    /// <list type="bullet">
    ///   <item><description>1: <c>EqualizerAmp7_5</c>  → <c>Equalizer1</c> / <c>base-equalizer1</c></description></item>
    ///   <item><description>2: <c>EqualizerAmp13</c>   → <c>Equalizer2</c> / <c>base-equalizer2</c></description></item>
    ///   <item><description>3: <c>EqualizerAmp22_5</c> → <c>Equalizer3</c> / <c>base-equalizer3</c></description></item>
    ///   <item><description>4: <c>EqualizerAmp39</c>   → <c>Equalizer4</c> / <c>base-equalizer4</c></description></item>
    ///   <item><description>5: <c>EqualizerAmp55</c>   → <c>Equalizer5</c> / <c>base-equalizer5</c></description></item>
    ///   <item><description>6: <c>EqualizerAmp100</c>  → <c>Equalizer6</c> / <c>base-equalizer6</c></description></item>
    /// </list>
    /// </remarks>
    internal sealed class MotorEqualizerAmpResource : CoapResourceHandler
    {
        // Ordered (key, MozaData reader, base command) tuples. Index = band-1.
        // Static so the array is built exactly once per process.
        private static readonly (string Key, Func<MozaData, int> Read, string Command)[] Bands =
        {
            ("EqualizerAmp7_5",  d => d.Equalizer1, "base-equalizer1"),
            ("EqualizerAmp13",   d => d.Equalizer2, "base-equalizer2"),
            ("EqualizerAmp22_5", d => d.Equalizer3, "base-equalizer3"),
            ("EqualizerAmp39",   d => d.Equalizer4, "base-equalizer4"),
            ("EqualizerAmp55",   d => d.Equalizer5, "base-equalizer5"),
            ("EqualizerAmp100",  d => d.Equalizer6, "base-equalizer6"),
        };

        private readonly MozaData _data;
        private readonly HardwareApplier _hardware;

        public MotorEqualizerAmpResource(MozaData data, HardwareApplier hardware)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
            _hardware = hardware ?? throw new ArgumentNullException(nameof(hardware));
        }

        public override CoapResourceResponse HandleGet(CoapResourceRequest req)
        {
            // Emit in band-1..6 order (preserving the captured PitHouse shape).
            var entries = new List<KeyValuePair<string, object>>(Bands.Length);
            for (int i = 0; i < Bands.Length; i++)
            {
                entries.Add(new KeyValuePair<string, object>(Bands[i].Key, Bands[i].Read(_data)));
            }
            return CoapResourceResponse.Content(CborWriter.WriteMap(entries), PayloadCodec.CFCbor);
        }

        public override CoapResourceResponse HandlePost(CoapResourceRequest req)
        {
            if (!req.HasPayload)
                return CoapResourceResponse.BadRequest("EqualizerAmp POST requires CBOR map body");

            Dictionary<string, int> map;
            try
            {
                map = CborReader.ReadMapStringToInt(req.Payload);
            }
            catch (CborFormatException ex)
            {
                MozaLog.Warn($"[AZOM.Sdk] EqualizerAmp POST malformed CBOR: {ex.Message}");
                return CoapResourceResponse.BadRequest("malformed CBOR: " + ex.Message);
            }

            for (int i = 0; i < Bands.Length; i++)
            {
                if (map.TryGetValue(Bands[i].Key, out int value))
                    _hardware.WriteIfBaseConnected(Bands[i].Command, value);
            }
            return CoapResourceResponse.Valid();
        }
    }
}
