using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

// SimHub interface test: does a built MozaPlugin.dll still work against a given SimHub
// installation?
//
//   simhub-interface-test --simhub <dir> --plugin <MozaPlugin.dll> [--contract]
//                         [--label <text>] [--report <file.md>] [--expect-version <x.y.z>]
//
// <dir> must be a full SimHub installation, not libs/SimHub: the plugin is loaded in an
// AppDomain rooted there, under SimHubWPF.exe.config, so it binds exactly as it does
// inside SimHub. Two passes, both run by the real CLR rather than re-derived from
// metadata:
//
//   binary    every plugin type is loaded and every method JIT-compiled. Catches what
//             the compiler can't see across SimHub versions: TypeLoadException (a host
//             interface member we don't implement), MissingMethodException /
//             MissingFieldException (a host member we call that is gone), and missing
//             host types or assemblies.
//   contract  (--contract) every member the plugin reaches by NAME through reflection
//             is resolved against the host with the plugin's own binding flags, see
//             Contract.cs. An IL scan of the plugin then reconciles every reflection
//             literal in the DLL against the checks, so a new lookup can't go
//             untested. Only meaningful for a DLL built from this source tree.
//
// Exit code: 0 pass (warnings allowed), 1 failures, 2 usage or setup error.

namespace SimHubInterfaceTest
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            string simhub = null, plugin = null, report = null, label = null, expect = null;
            bool contract = false;
            for (int i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
                try
                {
                    switch (args[i])
                    {
                        case "--simhub": simhub = Next(); break;
                        case "--plugin": plugin = Next(); break;
                        case "--report": report = Next(); break;
                        case "--label": label = Next(); break;
                        case "--expect-version": expect = Next(); break;
                        case "--contract": contract = true; break;
                        default: return Usage($"unknown argument '{args[i]}'");
                    }
                }
                catch (ArgumentException e) { return Usage(e.Message); }
            }
            if (simhub == null || plugin == null) return Usage("--simhub and --plugin are required");

            simhub = Path.GetFullPath(simhub);
            plugin = Path.GetFullPath(plugin);
            if (!File.Exists(Path.Combine(simhub, "SimHub.Plugins.dll")))
                return Setup($"'{simhub}' holds no SimHub.Plugins.dll — point --simhub at a SimHub installation");
            if (!File.Exists(plugin))
                return Setup($"plugin '{plugin}' not found");

            // Not Console.OutputEncoding: setting it throws when stdout is redirected (Wine).
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
            label ??= Path.GetFileName(plugin);

            string hostVersion;
            Result result;
            var domain = CreateHostDomain(simhub);
            try
            {
                var runner = (Runner)domain.CreateInstanceFromAndUnwrap(
                    typeof(Runner).Assembly.Location, typeof(Runner).FullName);

                hostVersion = runner.HostVersion(simhub);
                Console.WriteLine($"== {label}: {plugin}");
                Console.WriteLine($"   SimHub {hostVersion} at {simhub}");
                if (expect != null && !SameVersion(hostVersion, expect))
                    return Setup($"'{simhub}' is SimHub {hostVersion}, expected {expect}");

                result = runner.Run(plugin, contract);
            }
            catch (Exception e)
            {
                return Setup($"harness crashed: {e}");
            }
            finally
            {
                try { AppDomain.Unload(domain); } catch { /* process exit tears it down anyway */ }
            }

            foreach (var f in result.Findings)
                Console.WriteLine($"{f.Severity,-4}  [{f.Area}] {f.Text}");
            string verdict = result.Failed ? "FAIL" : "PASS";
            Console.WriteLine(
                $"{verdict}: {result.TypesLoaded} types loaded, {result.MethodsPrepared} methods JIT-compiled " +
                $"({result.MethodsSkipped} generic skipped)" +
                (contract ? $", {result.ContractChecks} contract checks" : "") +
                $" — {result.Count("FAIL")} failure(s), {result.Count("WARN")} warning(s)");

            if (report != null)
                File.AppendAllText(report, Markdown(label, plugin, hostVersion, contract, result), new UTF8Encoding(false));

            return result.Failed ? 1 : 0;
        }

        // The plugin and its references load in an AppDomain rooted at the SimHub
        // folder and configured by SimHub's own exe.config, so assembly probing and
        // binding redirects match a real SimHub process.
        private static AppDomain CreateHostDomain(string simhub)
        {
            string config = Path.Combine(simhub, "SimHubWPF.exe.config");
            var setup = new AppDomainSetup
            {
                ApplicationBase = simhub.EndsWith("\\") || simhub.EndsWith("/") ? simhub : simhub + Path.DirectorySeparatorChar,
                ConfigurationFile = File.Exists(config) ? config : null,
            };
            if (setup.ConfigurationFile == null)
                Console.WriteLine("   note: no SimHubWPF.exe.config — running without SimHub's binding redirects");
            return AppDomain.CreateDomain("simhub-host", null, setup);
        }

        private static bool SameVersion(string actual, string expected)
        {
            expected = expected.TrimStart('v', 'V');
            if (!Version.TryParse(actual, out var a) || !Version.TryParse(expected, out var e)) return actual == expected;
            int Part(int p) => p < 0 ? 0 : p;
            return a.Major == e.Major && a.Minor == e.Minor && Part(a.Build) == Part(e.Build)
                && (e.Revision < 0 || Part(a.Revision) == e.Revision);
        }

        // GitHub renders this in the update PR body; keep failure lists bounded.
        private static string Markdown(string label, string plugin, string hostVersion, bool contract, Result r)
        {
            const int MaxLines = 40;
            var sb = new StringBuilder();
            sb.AppendLine($"#### {(r.Failed ? "FAIL" : "PASS")} — {label} on SimHub {hostVersion}");
            sb.AppendLine();
            sb.AppendLine($"- Plugin: `{Path.GetFileName(plugin)}` {r.PluginVersion}");
            sb.AppendLine($"- Binary: {r.TypesLoaded} types loaded, {r.MethodsPrepared} methods JIT-compiled " +
                          $"({r.MethodsSkipped} generic skipped), {r.Count("FAIL", "Binary")} failure(s)");
            if (contract)
                sb.AppendLine($"- Reflection contract: {r.ContractChecks} checks, " +
                              $"{r.Findings.Count(f => f.Severity == "FAIL" && f.Area != "Binary")} failure(s), {r.Count("WARN")} warning(s)");

            var issues = r.Findings.Where(f => f.Severity == "FAIL" || f.Severity == "WARN").ToList();
            if (issues.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("```");
                foreach (var f in issues.Take(MaxLines))
                    sb.AppendLine($"{f.Severity,-4}  [{f.Area}] {f.Text}");
                if (issues.Count > MaxLines)
                    sb.AppendLine($"… {issues.Count - MaxLines} more (see the workflow log)");
                sb.AppendLine("```");
            }
            sb.AppendLine();
            return sb.ToString();
        }

        private static int Usage(string error)
        {
            Console.Error.WriteLine($"simhub-interface-test: {error}");
            Console.Error.WriteLine(
                "usage: simhub-interface-test --simhub <dir> --plugin <MozaPlugin.dll> [--contract] " +
                "[--label <text>] [--report <file.md>] [--expect-version <x.y.z>]");
            return 2;
        }

        private static int Setup(string error)
        {
            Console.Error.WriteLine($"simhub-interface-test: {error}");
            return 2;
        }
    }

    [Serializable]
    internal sealed class Finding
    {
        public string Severity;
        public string Area;
        public string Text;
    }

    [Serializable]
    internal sealed class Result
    {
        public readonly List<Finding> Findings = new List<Finding>();
        public string PluginVersion = "";
        public int TypesLoaded;
        public int MethodsPrepared;
        public int MethodsSkipped;
        public int ContractChecks;

        public bool Failed => Findings.Any(f => f.Severity == "FAIL");

        public int Count(string severity, string area = null)
            => Findings.Count(f => f.Severity == severity && (area == null || f.Area == area));

        public void Add(string severity, string area, string text)
        {
            // Loader exceptions and JIT failures repeat per caller; one line each is enough.
            if (Findings.Any(f => f.Severity == severity && f.Area == area && f.Text == text)) return;
            Findings.Add(new Finding { Severity = severity, Area = area, Text = text });
        }
    }
}
