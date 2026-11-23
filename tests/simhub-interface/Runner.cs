using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace SimHubInterfaceTest
{
    /// <summary>Runs inside the SimHub-rooted AppDomain (see Program.RunIsolated).</summary>
    internal sealed class Runner : MarshalByRefObject
    {
        private const BindingFlags Declared =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
            | BindingFlags.DeclaredOnly;

        public Runner()
        {
            // This assembly arrived through LoadFrom, which a later by-name bind (remoting,
            // serialisation) won't see; hand back the loaded copy.
            AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
            {
                var name = new AssemblyName(e.Name).Name;
                return AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
            };
        }

        /// <summary>
        /// SimHub's release version. Its PE version resources are fixed (1.x.0.0); the
        /// real string is a literal in <c>SimHubWPF.AppVersion</c>'s static initialiser,
        /// read from the IL so no SimHub code runs.
        /// </summary>
        public string HostVersion(string simhub)
        {
            try
            {
                var exe = Assembly.LoadFrom(Path.Combine(simhub, "SimHubWPF.exe"));
                var found = Il.StringLiterals(exe.GetType("SimHubWPF.AppVersion", false)?.TypeInitializer)
                    .Where(s => System.Text.RegularExpressions.Regex.IsMatch(s, @"^\d+\.\d+\.\d+(\.\d+)?$"))
                    .Distinct()
                    .ToList();
                if (found.Count == 1) return found[0];
                return found.Count == 0 ? "unknown" : $"unknown (candidates {string.Join(", ", found)})";
            }
            catch (Exception e)
            {
                return $"unknown ({Describe(e)})";
            }
        }

        public Result Run(string pluginPath, bool contract)
        {
            var result = new Result();

            Assembly plugin;
            try
            {
                // LoadFile, not LoadFrom: LoadFrom hands back a same-identity copy from the
                // probing path, and a local SimHub folder usually has a deployed MozaPlugin.dll.
                plugin = Assembly.LoadFile(pluginPath);
            }
            catch (Exception e)
            {
                result.Add("FAIL", "Binary", $"could not load {pluginPath}: {Describe(e)}");
                return result;
            }
            result.PluginVersion = plugin.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                                   ?? plugin.GetName().Version.ToString();

            var types = LoadTypes(plugin, result);
            PrepareMethods(types, result);

            if (contract)
            {
                try
                {
                    new Contract(plugin, types, result).Run();
                }
                catch (Exception e)
                {
                    result.Add("FAIL", "Contract", $"contract run aborted: {Describe(e)}");
                }
            }

            var stray = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a != plugin && a.GetName().Name == plugin.GetName().Name)
                .Select(a => a.Location).ToList();
            foreach (var path in stray)
                result.Add("WARN", "Binary", $"a second {plugin.GetName().Name} was bound from {path} — results may mix two builds");

            return result;
        }

        // Assembly.GetTypes loads every type, which is where the CLR verifies interface
        // implementations against the host's interfaces.
        private static Type[] LoadTypes(Assembly plugin, Result result)
        {
            Type[] types;
            try
            {
                types = plugin.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types.Where(t => t != null).ToArray();
                foreach (var le in e.LoaderExceptions.Where(x => x != null))
                    result.Add("FAIL", "Binary", $"type load: {Describe(le)}");
            }
            result.TypesLoaded = types.Length;
            return types;
        }

        // JIT-compiling a method resolves every member and type it references, so a host
        // member that is gone surfaces here even if the method never runs in a test.
        private static void PrepareMethods(Type[] types, Result result)
        {
            var closedBy = ClosedInstantiations(types);
            // A type that failed to load fails every method that touches it again;
            // those repeats add nothing to the type-load line already reported.
            var typeLoadMessages = new HashSet<string>(
                result.Findings.Where(f => f.Area == "Binary").Select(f => f.Text), StringComparer.Ordinal);
            int followOns = 0;

            foreach (var type in types)
            {
                Type[] classArgs = Type.EmptyTypes;
                if (type.IsGenericTypeDefinition)
                {
                    classArgs = closedBy.TryGetValue(type, out var known)
                        ? known
                        : type.GetGenericArguments().Select(_ => typeof(object)).ToArray();
                    if (!Instantiable(() => type.MakeGenericType(classArgs)))
                    {
                        result.MethodsSkipped += Methods(type).Count();
                        continue;
                    }
                }

                foreach (var method in Methods(type))
                {
                    var methodArgs = Type.EmptyTypes;
                    if (method.IsGenericMethodDefinition)
                    {
                        var mi = (MethodInfo)method;
                        methodArgs = mi.GetGenericArguments().Select(_ => typeof(object)).ToArray();
                        if (!Instantiable(() => (type.IsGenericTypeDefinition
                                ? (MethodInfo)MethodBase.GetMethodFromHandle(mi.MethodHandle, type.MakeGenericType(classArgs).TypeHandle)
                                : mi).MakeGenericMethod(methodArgs)))
                        {
                            result.MethodsSkipped++;
                            continue;
                        }
                    }

                    try
                    {
                        var inst = classArgs.Concat(methodArgs).Select(a => a.TypeHandle).ToArray();
                        if (inst.Length == 0) RuntimeHelpers.PrepareMethod(method.MethodHandle);
                        else RuntimeHelpers.PrepareMethod(method.MethodHandle, inst);
                        result.MethodsPrepared++;
                    }
                    catch (Exception e)
                    {
                        if (typeLoadMessages.Contains($"type load: {Describe(e)}")) followOns++;
                        else result.Add("FAIL", "Binary", $"JIT {Name(method)}: {Describe(e)}");
                    }
                }
            }
            if (followOns > 0)
                result.Add("INFO", "Binary", $"{followOns} more method(s) fail to JIT only because of the type-load failures above");
        }

        private static IEnumerable<MethodBase> Methods(Type type)
            => type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared))
                .Where(m => !m.IsAbstract
                            && (m.Attributes & MethodAttributes.PinvokeImpl) == 0
                            && (m.MethodImplementationFlags & (MethodImplAttributes.InternalCall | MethodImplAttributes.Runtime)) == 0);

        // A generic plugin type is compiled with the arguments the plugin itself closes
        // it over (e.g. a store's base class), so constrained parameters still JIT.
        private static Dictionary<Type, Type[]> ClosedInstantiations(Type[] types)
        {
            var map = new Dictionary<Type, Type[]>();
            void Note(Type t)
            {
                if (t == null || !t.IsGenericType || t.IsGenericTypeDefinition || t.ContainsGenericParameters) return;
                var def = t.GetGenericTypeDefinition();
                if (!map.ContainsKey(def)) map[def] = t.GetGenericArguments();
            }
            foreach (var t in types)
            {
                try
                {
                    for (var b = t.BaseType; b != null; b = b.BaseType) Note(b);
                    foreach (var i in t.GetInterfaces()) Note(i);
                    foreach (var f in t.GetFields(Declared)) Note(f.FieldType);
                }
                catch { /* a type that fails here already failed to load */ }
            }
            return map;
        }

        private static bool Instantiable(Func<object> make)
        {
            try { make(); return true; }
            catch (ArgumentException) { return false; }   // constraint violation
        }

        internal static string Name(MethodBase m) => $"{m.DeclaringType?.FullName}::{m.Name}";

        internal static string Describe(Exception e)
        {
            while ((e is TargetInvocationException || e is TypeInitializationException) && e.InnerException != null)
                e = e.InnerException;
            string msg = e.Message.Replace("\r", " ").Replace("\n", " ");
            if (e is FileNotFoundException fnf && !string.IsNullOrEmpty(fnf.FileName) && !msg.Contains(fnf.FileName))
                msg += $" ({fnf.FileName})";
            return $"{e.GetType().Name}: {msg}";
        }
    }
}
