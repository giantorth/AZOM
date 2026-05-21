using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk
{
    /// <summary>
    /// Central entry point that wires every resource handler into a
    /// <see cref="CoapResourceRegistry"/>. Stream 7's UDP listener calls
    /// <see cref="RegisterAll"/> once at startup; subsequent reconnects do
    /// not need to re-register because device IDs are resolved against the
    /// live catalog at lookup time.
    /// </summary>
    /// <remarks>
    /// Phase 6a registers only Discovery + Lifecycle here. The TODO comments
    /// below are load-bearing — they tell future agents EXACTLY where to add
    /// their <c>Register</c> call so the wiring stays in one place.
    /// </remarks>
    internal static class ResourceBindings
    {
        /// <summary>
        /// Bind every Phase-6 resource into <paramref name="r"/>.
        /// </summary>
        public static void RegisterAll(CoapResourceRegistry r, DeviceCatalog catalog, MozaData data, HardwareApplier hw)
        {
            Resources.Discovery.DiscoveryBindings.Register(r, catalog, data);
            Resources.Lifecycle.LifecycleBindings.Register(r, catalog, data, hw);
            Resources.Motor.MotorBindings.Register(r, data, hw);
            Resources.Wheel.WheelBindings.Register(r, catalog, data, hw);
            Resources.Display.DisplayBindings.Register(r, catalog, data, hw);
            Resources.Pedal.PedalBindings.Register(r, data, hw);
            Resources.Handbrake.HandbrakeBindings.Register(r, data, hw);
            Resources.Shifter.ShifterBindings.Register(r, data, hw);
        }
    }
}
