using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Display
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/DisplayScreenScreenBrightness</c>.
    /// SDK <c>DisplayScreenScreenBrightness</c> (0..100) — backlight level of
    /// the standalone display sub-device.
    /// </summary>
    /// <remarks>
    /// <para>
    /// GET reads <see cref="MozaData.DashDisplayBrightness"/> which the
    /// plugin already populates from the dash settings round.
    /// </para>
    /// <para>
    /// POST is a partial gap: the plugin DOES write display brightness on
    /// the wire — via <c>MozaTelemetrySender.SendDashDisplayBrightness</c>,
    /// invoked from
    /// <see cref="HardwareApplier.ApplyDashToHardware"/> — but the wire
    /// shape is bespoke and is NOT registered in <c>MozaCommandDatabase</c>.
    /// <see cref="HardwareApplier.WriteIfDashDetected"/> only dispatches
    /// commands that are in the DB, so we cannot route this POST through
    /// the standard helper without first adding a real command entry.
    /// Returns 4.05 with a one-shot WARN until a backing command is added;
    /// GET still serves the cached value for clients that only read.
    /// </para>
    /// </remarks>
    internal sealed class DisplayScreenScreenBrightnessResource : DisplayScalarResource
    {
        public DisplayScreenScreenBrightnessResource(MozaData data, HardwareApplier hardware)
            : base(data, hardware,
                "DisplayScreenScreenBrightness",
                d => d.DashDisplayBrightness,
                commandName: null)
        {
        }
    }
}
