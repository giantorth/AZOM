using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

// Binary-compatibility checker: does a built MozaPlugin.dll still load under a
// given SimHub build?
//
// Compiling against one SimHub version bakes that version's signatures into the
// IL, so a host whose contract differs fails at *load* time — the compiler can't
// see it and neither can a run against a single version. SimHub 9.12.0 is the
// live example (see docs/DEVELOPMENT.md § Dependencies).
//
//   usage: simhub-compat <plugin.dll> <simhub-dir>
//
// <simhub-dir> is any directory holding that version's SimHub.Plugins.dll,
// BA63Driver.dll, SerialDash.dll, GameReaderCommon.dll and SimHub.Logging.dll —
// a SimHub installation, or libs/SimHub/ itself.
//
// Four checks, each a load-time failure the compiler does not catch:
//
//   MISSING MEMBER   a member reference the host no longer declares
//                    -> MissingMethodException / MissingFieldException
//   MISSING TYPE     a type reference the host no longer declares
//                    -> TypeLoadException
//   UNIMPLEMENTED    a host interface member nothing on our type matches
//                    -> TypeLoadException
//   NOT VIRTUAL      a matching method exists but is not public virtual, so it
//                    cannot fill an interface slot (ECMA-335 II.12.2). This is
//                    the trap a hand-written compat overload falls into: it
//                    compiles, reads correctly, and still fails to load.
//   STALE OVERRIDE   an explicit .override pinned to a member the host dropped
//                    -> TypeLoadException
//
// Exit code 0 when compatible, 1 when not, so it works as a CI gate across every
// supported SimHub version.

static class Program
{
    static readonly string[] HostAssemblies =
    {
        "SimHub.Plugins", "BA63Driver", "SerialDash", "GameReaderCommon", "SimHub.Logging",
    };

    internal static bool IsHostAssembly(string name) => HostAssemblies.Contains(name);

    static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: compat <plugin.dll> <hostDir>");
            return 2;
        }
        var pluginPath = args[0];
        var hostDir = args[1];

        var host = new HostIndex(hostDir);
        using var fs = File.OpenRead(pluginPath);
        using var pe = new PEReader(fs);
        var md = pe.GetMetadataReader();

        var problems = new List<string>();

        // ---- 1. every MemberReference into a host assembly must resolve ----
        foreach (var handle in md.MemberReferences)
        {
            var mr = md.GetMemberReference(handle);
            if (mr.Parent.Kind != HandleKind.TypeReference) continue;
            var tr = md.GetTypeReference((TypeReferenceHandle)mr.Parent);
            var asm = ScopeAssembly(md, tr);
            if (asm == null || !HostAssemblies.Contains(asm)) continue;

            string typeName = TypeRefName(md, tr);
            string memberName = md.GetString(mr.Name);
            var prov = new SigProvider(md);
            string sig;
            if (mr.GetKind() == MemberReferenceKind.Method)
            {
                var ms = mr.DecodeMethodSignature(prov, null);
                sig = $"{ms.ReturnType} {memberName}({string.Join(", ", ms.ParameterTypes)})";
            }
            else
            {
                sig = $"{mr.DecodeFieldSignature(prov, null)} {memberName}";
            }

            if (!host.HasMember(typeName, sig))
                problems.Add($"MISSING MEMBER  {typeName}::{sig}");
        }

        // ---- 2. every host TypeReference must exist ----
        foreach (var handle in md.TypeReferences)
        {
            var tr = md.GetTypeReference(handle);
            var asm = ScopeAssembly(md, tr);
            if (asm == null || !HostAssemblies.Contains(asm)) continue;
            string typeName = TypeRefName(md, tr);
            if (!host.HasType(typeName))
                problems.Add($"MISSING TYPE    {typeName}");
        }

        // ---- 3. every host interface we implement must be fully implemented ----
        foreach (var tdh in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(tdh);
            string implName = FullName(md, td);

            // Methods declared on this type, by full signature. Members inherited
            // from a host base class (e.g. ProfileBase<,>) can't be signature-matched
            // reliably through generic substitution, so those are matched by name.
            // Only public *virtual* methods can fill an interface slot (ECMA-335 II.12.2)
            // — a plain public method with a matching signature does not, which is how a
            // compat overload silently fails to satisfy an older host's interface.
            var ourMethods = new HashSet<string>(StringComparer.Ordinal);
            var ourNonVirtual = new HashSet<string>(StringComparer.Ordinal);
            foreach (var mh in td.GetMethods())
            {
                var m = md.GetMethodDefinition(mh);
                var prov = new SigProvider(md);
                var ms = m.DecodeSignature(prov, null);
                string sig = $"{ms.ReturnType} {md.GetString(m.Name)}({string.Join(", ", ms.ParameterTypes)})";
                bool isVirtual = (m.Attributes & System.Reflection.MethodAttributes.Virtual) != 0;
                bool isPublic = (m.Attributes & System.Reflection.MethodAttributes.MemberAccessMask)
                                == System.Reflection.MethodAttributes.Public;
                if (isVirtual && isPublic) ourMethods.Add(sig);
                else ourNonVirtual.Add(sig);
            }
            var inheritedNames = InheritedMethodNames(md, td, host);

            foreach (var iih in td.GetInterfaceImplementations())
            {
                var ii = md.GetInterfaceImplementation(iih);
                if (ii.Interface.Kind != HandleKind.TypeReference) continue;
                var itr = md.GetTypeReference((TypeReferenceHandle)ii.Interface);
                var asm = ScopeAssembly(md, itr);
                if (asm == null || !HostAssemblies.Contains(asm)) continue;

                string ifaceName = TypeRefName(md, itr);
                foreach (var need in host.InterfaceMembers(ifaceName))
                {
                    if (ourMethods.Contains(need)) continue;
                    if (ourNonVirtual.Contains(need))
                    {
                        problems.Add($"NOT VIRTUAL     {implName} : {ifaceName}::{need}");
                        continue;
                    }
                    if (inheritedNames.Contains(MethodName(need))) continue;
                    problems.Add($"UNIMPLEMENTED   {implName} : {ifaceName}::{need}");
                }
            }
        }

        // ---- 4. explicit MethodImpl records must target a member the host declares ----
        // An implicit interface implementation is bound by the runtime, but an explicit
        // .override pins one signature at compile time and fails type load if the host's
        // interface no longer declares it.
        foreach (var tdh in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(tdh);
            foreach (var mih in td.GetMethodImplementations())
            {
                var mi = md.GetMethodImplementation(mih);
                if (mi.MethodDeclaration.Kind != HandleKind.MemberReference) continue;
                var mr = md.GetMemberReference((MemberReferenceHandle)mi.MethodDeclaration);
                if (mr.Parent.Kind != HandleKind.TypeReference) continue;
                var tr = md.GetTypeReference((TypeReferenceHandle)mr.Parent);
                var asm = ScopeAssembly(md, tr);
                if (asm == null || !HostAssemblies.Contains(asm)) continue;

                string typeName = TypeRefName(md, tr);
                var ms = mr.DecodeMethodSignature(new SigProvider(md), null);
                string sig = $"{ms.ReturnType} {md.GetString(mr.Name)}({string.Join(", ", ms.ParameterTypes)})";
                if (!host.HasMember(typeName, sig))
                    problems.Add($"STALE OVERRIDE  {FullName(md, td)} -> {typeName}::{sig}");
            }
        }

        foreach (var p in problems.Distinct().OrderBy(x => x, StringComparer.Ordinal))
            Console.WriteLine(p);
        Console.WriteLine(problems.Count == 0
            ? $"OK — {Path.GetFileName(pluginPath)} is binary-compatible with {hostDir}"
            : $"{problems.Distinct().Count()} problem(s)");
        return problems.Count == 0 ? 0 : 1;
    }

    // "Ret Name(args)" -> "Name"
    static string MethodName(string sig)
    {
        int paren = sig.IndexOf('(');
        if (paren < 0) return sig;
        int space = sig.LastIndexOf(' ', paren);
        return space < 0 ? sig.Substring(0, paren) : sig.Substring(space + 1, paren - space - 1);
    }

    // Method names reachable through the base chain, following base types into the
    // plugin assembly and into the host assemblies.
    static HashSet<string> InheritedMethodNames(MetadataReader md, TypeDefinition td, HostIndex host)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var baseHandle = td.BaseType;
        int guard = 0;
        while (!baseHandle.IsNil && guard++ < 32)
        {
            if (baseHandle.Kind == HandleKind.TypeDefinition)
            {
                var b = md.GetTypeDefinition((TypeDefinitionHandle)baseHandle);
                foreach (var mh in b.GetMethods())
                    names.Add(md.GetString(md.GetMethodDefinition(mh).Name));
                baseHandle = b.BaseType;
                continue;
            }

            string baseName = baseHandle.Kind switch
            {
                HandleKind.TypeReference => TypeRefName(md, md.GetTypeReference((TypeReferenceHandle)baseHandle)),
                HandleKind.TypeSpecification => StripGenerics(
                    md.GetTypeSpecification((TypeSpecificationHandle)baseHandle).DecodeSignature(new SigProvider(md), null)),
                _ => null,
            };
            if (baseName == null) break;
            host.CollectMethodNames(baseName, names);
            break;
        }
        return names;
    }

    static string StripGenerics(string name)
    {
        int lt = name.IndexOf('<');
        return lt < 0 ? name : name.Substring(0, lt);
    }

    static string ScopeAssembly(MetadataReader md, TypeReference tr)
    {
        switch (tr.ResolutionScope.Kind)
        {
            case HandleKind.AssemblyReference:
                var ar = md.GetAssemblyReference((AssemblyReferenceHandle)tr.ResolutionScope);
                return md.GetString(ar.Name);
            case HandleKind.TypeReference:
                return ScopeAssembly(md, md.GetTypeReference((TypeReferenceHandle)tr.ResolutionScope));
            default:
                return null;
        }
    }

    static string TypeRefName(MetadataReader md, TypeReference tr)
    {
        string name = md.GetString(tr.Name);
        if (tr.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            var outer = md.GetTypeReference((TypeReferenceHandle)tr.ResolutionScope);
            return TypeRefName(md, outer) + "+" + name;
        }
        string ns = md.GetString(tr.Namespace);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    static string FullName(MetadataReader md, TypeDefinition td)
    {
        string name = md.GetString(td.Name);
        var decl = td.GetDeclaringType();
        if (!decl.IsNil)
            return FullName(md, md.GetTypeDefinition(decl)) + "+" + name;
        string ns = md.GetString(td.Namespace);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }
}

sealed class HostIndex
{
    readonly Dictionary<string, HashSet<string>> _members = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<string>> _ifaceMembers = new(StringComparer.Ordinal);
    readonly Dictionary<string, HashSet<string>> _methodNames = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _baseOf = new(StringComparer.Ordinal);
    readonly HashSet<string> _types = new(StringComparer.Ordinal);

    public HostIndex(string dir)
    {
        // Index only the host assemblies. A SimHub installation holds hundreds of
        // DLLs, and indexing them all lets an unrelated assembly's same-named type
        // shadow the real one.
        foreach (var file in Directory.GetFiles(dir, "*.dll")
                     .Where(f => Program.IsHostAssembly(Path.GetFileNameWithoutExtension(f))))
        {
            using var fs = File.OpenRead(file);
            PEReader pe;
            try { pe = new PEReader(fs); if (!pe.HasMetadata) continue; }
            catch { continue; }
            var md = pe.GetMetadataReader();
            foreach (var tdh in md.TypeDefinitions)
            {
                var td = md.GetTypeDefinition(tdh);
                string full = Full(md, td);
                _types.Add(full);
                var set = new HashSet<string>(StringComparer.Ordinal);
                var names = new HashSet<string>(StringComparer.Ordinal);
                var iface = new List<string>();
                bool isInterface = (td.Attributes & System.Reflection.TypeAttributes.Interface) != 0;

                if (!td.BaseType.IsNil)
                {
                    string bn = td.BaseType.Kind switch
                    {
                        HandleKind.TypeDefinition => Full(md, md.GetTypeDefinition((TypeDefinitionHandle)td.BaseType)),
                        HandleKind.TypeReference => RefName(md, md.GetTypeReference((TypeReferenceHandle)td.BaseType)),
                        HandleKind.TypeSpecification => Strip(
                            md.GetTypeSpecification((TypeSpecificationHandle)td.BaseType).DecodeSignature(new SigProvider(md), null)),
                        _ => null,
                    };
                    if (bn != null) _baseOf[full] = bn;
                }

                foreach (var mh in td.GetMethods())
                {
                    var m = md.GetMethodDefinition(mh);
                    var prov = new SigProvider(md);
                    MethodSignature<string> ms;
                    try { ms = m.DecodeSignature(prov, null); } catch { continue; }
                    string sig = $"{ms.ReturnType} {md.GetString(m.Name)}({string.Join(", ", ms.ParameterTypes)})";
                    set.Add(sig);
                    names.Add(md.GetString(m.Name));
                    if (isInterface && (m.Attributes & System.Reflection.MethodAttributes.Abstract) != 0)
                        iface.Add(sig);
                }
                foreach (var fh in td.GetFields())
                {
                    var f = md.GetFieldDefinition(fh);
                    var prov = new SigProvider(md);
                    try { set.Add($"{f.DecodeSignature(prov, null)} {md.GetString(f.Name)}"); } catch { }
                }
                _members[full] = set;
                _methodNames[full] = names;
                if (isInterface) _ifaceMembers[full] = iface;
            }
            pe.Dispose();
        }
    }

    static string Full(MetadataReader md, TypeDefinition td)
    {
        string name = md.GetString(td.Name);
        var decl = td.GetDeclaringType();
        if (!decl.IsNil) return Full(md, md.GetTypeDefinition(decl)) + "+" + name;
        string ns = md.GetString(td.Namespace);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    static string RefName(MetadataReader md, TypeReference tr)
    {
        string name = md.GetString(tr.Name);
        if (tr.ResolutionScope.Kind == HandleKind.TypeReference)
            return RefName(md, md.GetTypeReference((TypeReferenceHandle)tr.ResolutionScope)) + "+" + name;
        string ns = md.GetString(tr.Namespace);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    static string Strip(string name)
    {
        int lt = name.IndexOf('<');
        return lt < 0 ? name : name.Substring(0, lt);
    }

    // Every method name on `type` and its base chain, for name-only inheritance checks.
    public void CollectMethodNames(string type, HashSet<string> into)
    {
        int guard = 0;
        while (type != null && guard++ < 32)
        {
            if (_methodNames.TryGetValue(type, out var names))
                foreach (var n in names) into.Add(n);
            _baseOf.TryGetValue(type, out type);
        }
    }

    public bool HasType(string type) => _types.Contains(type);

    // Members are looked up on the declaring type only when we know it; a miss on
    // an unknown type is not reported (it may live in an assembly outside the set).
    public bool HasMember(string type, string sig)
    {
        if (!_members.TryGetValue(type, out var set)) return true;
        if (set.Contains(sig)) return true;
        // walk nothing else: inherited members are resolved by the runtime, so a
        // miss here is only meaningful when the type genuinely declares no match.
        return false;
    }

    public IEnumerable<string> InterfaceMembers(string iface)
        => _ifaceMembers.TryGetValue(iface, out var l) ? l : Enumerable.Empty<string>();
}

sealed class SigProvider : ISignatureTypeProvider<string, object>
{
    readonly MetadataReader _md;
    public SigProvider(MetadataReader md) => _md = md;

    public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', shape.Rank - 1) + "]";
    public string GetByReferenceType(string elementType) => elementType + "&";
    public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
        => genericType + "<" + string.Join(",", typeArguments) + ">";
    public string GetGenericMethodParameter(object genericContext, int index) => "!!" + index;
    public string GetGenericTypeParameter(object genericContext, int index) => "!" + index;
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
    public string GetPinnedType(string elementType) => elementType;
    public string GetPointerType(string elementType) => elementType + "*";
    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
    public string GetSZArrayType(string elementType) => elementType + "[]";
    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        var td = reader.GetTypeDefinition(handle);
        string name = reader.GetString(td.Name);
        var decl = td.GetDeclaringType();
        if (!decl.IsNil) return GetTypeFromDefinition(reader, decl, 0) + "+" + name;
        string ns = reader.GetString(td.Namespace);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }
    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var tr = reader.GetTypeReference(handle);
        string name = reader.GetString(tr.Name);
        if (tr.ResolutionScope.Kind == HandleKind.TypeReference)
            return GetTypeFromReference(reader, (TypeReferenceHandle)tr.ResolutionScope, 0) + "+" + name;
        string ns = reader.GetString(tr.Namespace);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }
    public string GetTypeFromSpecification(MetadataReader reader, object genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
        => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
}
