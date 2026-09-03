using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using BA63Driver.Mapper;

namespace MozaPlugin.Devices.Led
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
    ///
    /// The constructor is invoked late-bound via <see cref="ConstructorInfo.Invoke(object[])"/>
    /// rather than through a compiled expression tree. Both bind by parameter name and
    /// degrade the same way, but <c>Expression.Compile()</c> emits IL at runtime, and a
    /// plugin DLL that generates code trips Defender's ML heuristics (Wacatac.H!ml) —
    /// see docs/DEVELOPMENT.md. Cost is one boxed argument array per state build, on the
    /// LED display path only.
    /// </summary>
    internal static class SimHubLedCompat
    {
        /// <summary>
        /// Empty <c>overrideState</c> channel, for hosts whose Display contract has none.
        /// Shared so the legacy overload doesn't allocate a delegate per frame.
        /// </summary>
        internal static readonly Func<Color[]> NoOverrides = () => Array.Empty<Color>();

        // Our ten logical arguments, in the order CreateState takes them. The map below
        // is expressed as indices into this list.
        private static readonly string[] OurArgNames =
        {
            "ledsState", "buttonsState", "encodersState", "matrixState", "rawState",
            "overrideState", "rpmBrightness", "buttonsBrightness", "encodersBrightness",
            "matrixBrightness",
        };

        private static readonly ConstructorInfo s_ctor;

        // One entry per host constructor parameter: an index into OurArgNames, or -1 to
        // take the constant in s_fallbacks at the same position.
        private static readonly int[] s_argMap;
        private static readonly object?[] s_fallbacks;

        static SimHubLedCompat()
        {
            // Widest public constructor: the one carrying every channel the host knows.
            s_ctor = typeof(LedDeviceState)
                .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .OrderByDescending(c => c.GetParameters().Length)
                .First();

            var ours = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < OurArgNames.Length; i++) ours[OurArgNames[i]] = i;

            var pars = s_ctor.GetParameters();
            s_argMap = new int[pars.Length];
            s_fallbacks = new object?[pars.Length];

            var ourTypes = new[]
            {
                typeof(Color[]), typeof(Color[]), typeof(Color[]), typeof(Color[]),
                typeof(Color[]), typeof(Color[]), typeof(double), typeof(double),
                typeof(double), typeof(double),
            };

            for (int i = 0; i < pars.Length; i++)
            {
                var p = pars[i];
                if (p.Name != null && ours.TryGetValue(p.Name, out int ourIdx)
                    && ourTypes[ourIdx] == p.ParameterType)
                {
                    s_argMap[i] = ourIdx;
                    continue;
                }

                // A parameter we don't recognise takes its own declared default, so a
                // future constructor change degrades to "that channel is empty" rather
                // than failing to load.
                s_argMap[i] = -1;
                s_fallbacks[i] = p.HasDefaultValue
                    ? p.DefaultValue
                    : DefaultOf(p.ParameterType);
            }
        }

        // Boxed default for any type, without emitting code: a fresh one-element array
        // is zero-initialised, so reading element 0 boxes default(T).
        private static object? DefaultOf(Type t)
            => t.IsValueType ? Array.CreateInstance(t, 1).GetValue(0) : null;

        /// <summary>
        /// Build a <see cref="LedDeviceState"/> against whatever constructor the loaded
        /// BA63Driver declares. Arguments the host's constructor doesn't take are dropped.
        /// </summary>
        internal static LedDeviceState CreateState(
            Color[] ledsState, Color[] buttonsState, Color[] encodersState,
            Color[] matrixState, Color[] rawState, Color[] overrideState,
            double rpmBrightness = 1.0, double buttonsBrightness = 1.0,
            double encodersBrightness = 1.0, double matrixBrightness = 1.0)
        {
            var map = s_argMap;
            var args = new object?[map.Length];
            for (int i = 0; i < map.Length; i++)
            {
                switch (map[i])
                {
                    case 0: args[i] = ledsState; break;
                    case 1: args[i] = buttonsState; break;
                    case 2: args[i] = encodersState; break;
                    case 3: args[i] = matrixState; break;
                    case 4: args[i] = rawState; break;
                    case 5: args[i] = overrideState; break;
                    case 6: args[i] = rpmBrightness; break;
                    case 7: args[i] = buttonsBrightness; break;
                    case 8: args[i] = encodersBrightness; break;
                    case 9: args[i] = matrixBrightness; break;
                    default: args[i] = s_fallbacks[i]; break;
                }
            }

            return (LedDeviceState)s_ctor.Invoke(args);
        }
    }
}
