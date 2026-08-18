using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using BA63Driver.Mapper;

namespace MozaPlugin.Devices
{
    /// <summary>
    /// Version shims that let one DLL drive SimHub's LED pipeline across host builds
    /// whose LED contract differs at the binary level.
    ///
    /// SimHub 9.12.0 added an <c>overrideState</c> colour layer (the dashboard "Device
    /// LEDs override" component): <c>ILedDeviceManager.Display</c> gained a sixth
    /// <c>Func&lt;Color[]&gt;</c> and <see cref="LedDeviceState"/>'s constructor gained a
    /// matching <c>Color[]</c>. Both changes are binary-breaking in *both* directions —
    /// a build compiled against either version fails on the other (TypeLoadException on
    /// the interface, MissingMethodException on the constructor).
    ///
    /// The interface half is handled in the managers themselves, which declare both
    /// <c>Display</c> overloads: implicit interface implementations are bound by the CLR
    /// at type-load time by name and signature, so each host binds the overload its own
    /// interface declares. The constructor half is handled here, because a direct
    /// <c>new</c> bakes one signature into the IL.
    /// </summary>
    internal static class SimHubLedCompat
    {
        /// <summary>
        /// Empty <c>overrideState</c> channel, for hosts whose Display contract has none.
        /// Shared so the legacy overload doesn't allocate a delegate per frame.
        /// </summary>
        internal static readonly Func<Color[]> NoOverrides = () => Array.Empty<Color>();

        private delegate LedDeviceState StateFactory(
            Color[] ledsState, Color[] buttonsState, Color[] encodersState,
            Color[] matrixState, Color[] rawState, Color[] overrideState,
            double rpmBrightness, double buttonsBrightness,
            double encodersBrightness, double matrixBrightness);

        private static readonly StateFactory s_create = BuildStateFactory();

        /// <summary>
        /// Build a <see cref="LedDeviceState"/> against whatever constructor the loaded
        /// BA63Driver declares. Arguments the host's constructor doesn't take are dropped.
        /// </summary>
        internal static LedDeviceState CreateState(
            Color[] ledsState, Color[] buttonsState, Color[] encodersState,
            Color[] matrixState, Color[] rawState, Color[] overrideState,
            double rpmBrightness = 1.0, double buttonsBrightness = 1.0,
            double encodersBrightness = 1.0, double matrixBrightness = 1.0)
            => s_create(ledsState, buttonsState, encodersState, matrixState, rawState,
                        overrideState, rpmBrightness, buttonsBrightness,
                        encodersBrightness, matrixBrightness);

        /// <summary>
        /// Compile a constructor call once, binding our arguments to the host's parameters
        /// by name. A parameter we don't recognise takes its own declared default (or
        /// <c>default(T)</c>), so a future constructor change degrades to "that channel is
        /// empty" rather than failing to load.
        /// </summary>
        private static StateFactory BuildStateFactory()
        {
            var ledsState = Expression.Parameter(typeof(Color[]), "ledsState");
            var buttonsState = Expression.Parameter(typeof(Color[]), "buttonsState");
            var encodersState = Expression.Parameter(typeof(Color[]), "encodersState");
            var matrixState = Expression.Parameter(typeof(Color[]), "matrixState");
            var rawState = Expression.Parameter(typeof(Color[]), "rawState");
            var overrideState = Expression.Parameter(typeof(Color[]), "overrideState");
            var rpmBrightness = Expression.Parameter(typeof(double), "rpmBrightness");
            var buttonsBrightness = Expression.Parameter(typeof(double), "buttonsBrightness");
            var encodersBrightness = Expression.Parameter(typeof(double), "encodersBrightness");
            var matrixBrightness = Expression.Parameter(typeof(double), "matrixBrightness");

            var ours = new ParameterExpression[]
            {
                ledsState, buttonsState, encodersState, matrixState, rawState, overrideState,
                rpmBrightness, buttonsBrightness, encodersBrightness, matrixBrightness,
            };
            var byName = ours.ToDictionary(p => p.Name!, StringComparer.Ordinal);

            // Widest public constructor: the one carrying every channel the host knows.
            var ctor = typeof(LedDeviceState)
                .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .OrderByDescending(c => c.GetParameters().Length)
                .First();

            var args = new List<Expression>();
            foreach (var p in ctor.GetParameters())
            {
                if (p.Name != null && byName.TryGetValue(p.Name, out var ours2)
                    && ours2.Type == p.ParameterType)
                {
                    args.Add(ours2);
                    continue;
                }

                object? fallback = p.HasDefaultValue ? p.DefaultValue
                    : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType)
                    : null;
                args.Add(Expression.Constant(fallback, p.ParameterType));
            }

            return Expression.Lambda<StateFactory>(Expression.New(ctor, args), ours).Compile();
        }
    }
}
