using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace SimHubInterfaceTest
{
    internal struct Instruction
    {
        public int Offset;
        public OpCode Op;
        public int Token;
    }

    /// <summary>Minimal IL reader over <see cref="MethodBody.GetILAsByteArray"/>.</summary>
    internal static class Il
    {
        private static readonly OpCode[] OneByte = new OpCode[0x100];
        private static readonly OpCode[] TwoByte = new OpCode[0x100];

        static Il()
        {
            foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                var op = (OpCode)f.GetValue(null);
                ushort v = unchecked((ushort)op.Value);
                if (v < 0x100) OneByte[v] = op;
                else if ((v & 0xFF00) == 0xFE00) TwoByte[v & 0xFF] = op;
            }
        }

        public static IEnumerable<Instruction> Decode(byte[] il)
        {
            int pos = 0;
            while (pos < il.Length)
            {
                int offset = pos;
                OpCode op = il[pos] == 0xFE ? TwoByte[il[pos + 1]] : OneByte[il[pos]];
                pos += op.Size;
                int token = 0;
                switch (op.OperandType)
                {
                    case OperandType.InlineNone: break;
                    case OperandType.ShortInlineBrTarget:
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineVar: pos += 1; break;
                    case OperandType.InlineVar: pos += 2; break;
                    case OperandType.InlineI8:
                    case OperandType.InlineR: pos += 8; break;
                    case OperandType.InlineSwitch: pos += 4 + 4 * BitConverter.ToInt32(il, pos); break;
                    case OperandType.InlineField:
                    case OperandType.InlineMethod:
                    case OperandType.InlineSig:
                    case OperandType.InlineString:
                    case OperandType.InlineTok:
                    case OperandType.InlineType:
                        token = BitConverter.ToInt32(il, pos);
                        pos += 4;
                        break;
                    default: pos += 4; break;   // InlineBrTarget, InlineI, ShortInlineR
                }
                yield return new Instruction { Offset = offset, Op = op, Token = token };
            }
        }

        public static MethodBase ResolveMethod(MethodBase context, int token)
        {
            try
            {
                return context.Module.ResolveMethod(token, TypeArgs(context), MethodArgs(context));
            }
            catch { return null; }
        }

        public static Type ResolveType(MethodBase context, int token)
        {
            try
            {
                return context.Module.ResolveType(token, TypeArgs(context), MethodArgs(context));
            }
            catch { return null; }
        }

        private static Type[] TypeArgs(MethodBase m)
            => m.DeclaringType != null && m.DeclaringType.IsGenericType ? m.DeclaringType.GetGenericArguments() : null;

        private static Type[] MethodArgs(MethodBase m)
            => m is MethodInfo mi && mi.IsGenericMethod ? mi.GetGenericArguments() : null;

        private static bool IsCall(OpCode op) => op == OpCodes.Call || op == OpCodes.Callvirt || op == OpCodes.Newobj;

        /// <summary>Every string literal loaded in <paramref name="method"/>.</summary>
        public static IEnumerable<string> StringLiterals(MethodBase method)
        {
            var il = method?.GetMethodBody()?.GetILAsByteArray();
            if (il == null) yield break;
            foreach (var ins in Decode(il))
            {
                if (ins.Op != OpCodes.Ldstr) continue;
                string s = null;
                try { s = method.Module.ResolveString(ins.Token); } catch { }
                if (s != null) yield return s;
            }
        }

        /// <summary>
        /// True when <paramref name="method"/> takes a monitor on its own type —
        /// <c>lock (typeof(T))</c> or <c>lock (GetType())</c>.
        /// </summary>
        public static bool LocksOnOwnType(MethodBase method)
        {
            var il = method.GetMethodBody()?.GetILAsByteArray();
            if (il == null) return false;
            bool typeofSelf = false, enter = false;
            Type pendingToken = null;
            OpCode previous = OpCodes.Nop;
            foreach (var ins in Decode(il))
            {
                if (ins.Op == OpCodes.Ldtoken)
                {
                    pendingToken = ResolveType(method, ins.Token);
                    previous = ins.Op;
                    continue;
                }
                if (IsCall(ins.Op))
                {
                    var target = ResolveMethod(method, ins.Token);
                    if (target != null)
                    {
                        if (target.DeclaringType == typeof(Type) && target.Name == "GetTypeFromHandle"
                            && pendingToken == method.DeclaringType)
                            typeofSelf = true;
                        if (target.DeclaringType == typeof(object) && target.Name == "GetType"
                            && previous == OpCodes.Ldarg_0)
                            typeofSelf = true;
                        if (target.DeclaringType == typeof(System.Threading.Monitor) && target.Name == "Enter")
                            enter = true;
                    }
                }
                pendingToken = null;
                previous = ins.Op;
            }
            return typeofSelf && enter;
        }
    }

    /// <summary>
    /// Every string literal the plugin passes as the member/type NAME into a reflection
    /// lookup, keyed by literal, with the methods it appears in. A small evaluation-stack
    /// simulation pins the literal to the name parameter, so a <c>typeof(X)</c> or other
    /// call in a later argument doesn't hide it. Names built at run time (concatenation,
    /// interpolation) are invisible here by construction.
    /// </summary>
    internal static class ReflectionLiterals
    {
        private static readonly HashSet<string> TypeLookups = new HashSet<string>(StringComparer.Ordinal)
        {
            "GetField", "GetProperty", "GetMethod", "GetEvent", "GetMember", "GetNestedType",
            "GetInterface", "InvokeMember", "GetType",
        };

        private static readonly HashSet<string> RuntimeExtensionLookups = new HashSet<string>(StringComparer.Ordinal)
        {
            "GetRuntimeField", "GetRuntimeProperty", "GetRuntimeMethod", "GetRuntimeEvent",
        };

        /// <summary>
        /// Plugin helpers that forward a name into reflection (Type full name, method
        /// name). Their callers' literals are the lookups.
        /// </summary>
        public static readonly (string Type, string Method)[] PluginSinks =
        {
            ("MozaPlugin.Devices.Haptics.MozaBaseHapticsBridge", "GetProp"),
        };

        public static Dictionary<string, SortedSet<string>> Collect(IEnumerable<Type> types)
        {
            var found = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            const BindingFlags declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                                          | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var type in types)
            {
                IEnumerable<MethodBase> methods;
                try { methods = type.GetMethods(declared).Cast<MethodBase>().Concat(type.GetConstructors(declared)).ToList(); }
                catch { continue; }
                foreach (var m in methods)
                {
                    MethodBody body;
                    try { body = m.GetMethodBody(); } catch { continue; }
                    if (body == null) continue;
                    Scan(m, body, found);
                }
            }
            return found;
        }

        private static void Scan(MethodBase method, MethodBody body, Dictionary<string, SortedSet<string>> found)
        {
            // Handler entry: catch/filter blocks start with the exception on the stack.
            var handlerEntry = new Dictionary<int, bool>();
            foreach (var c in body.ExceptionHandlingClauses)
            {
                bool pushesException = c.Flags == ExceptionHandlingClauseOptions.Clause
                                       || c.Flags == ExceptionHandlingClauseOptions.Filter;
                handlerEntry[c.HandlerOffset] = pushesException;
                if (c.Flags == ExceptionHandlingClauseOptions.Filter) handlerEntry[c.FilterOffset] = true;
            }

            var stack = new List<string>();   // null = not a known string literal
            foreach (var ins in Il.Decode(body.GetILAsByteArray()))
            {
                if (handlerEntry.TryGetValue(ins.Offset, out bool withException))
                {
                    stack.Clear();
                    if (withException) stack.Add(null);
                }

                var op = ins.Op;
                if (op == OpCodes.Ldstr)
                {
                    string s = null;
                    try { s = method.Module.ResolveString(ins.Token); } catch { }
                    stack.Add(s);
                    continue;
                }
                if (op == OpCodes.Dup)
                {
                    stack.Add(stack.Count > 0 ? stack[stack.Count - 1] : null);
                    continue;
                }
                if (op == OpCodes.Call || op == OpCodes.Callvirt || op == OpCodes.Newobj)
                {
                    var target = Il.ResolveMethod(method, ins.Token);
                    if (target == null) { stack.Clear(); continue; }
                    bool hasThis = op != OpCodes.Newobj && !target.IsStatic;
                    var pars = target.GetParameters();
                    var args = Pop(stack, pars.Length + (hasThis ? 1 : 0));
                    int nameParam = NameParameter(target, pars);
                    if (nameParam >= 0)
                    {
                        string literal = args[nameParam + (hasThis ? 1 : 0)];
                        if (literal != null)
                        {
                            if (!found.TryGetValue(literal, out var sites))
                                found[literal] = sites = new SortedSet<string>(StringComparer.Ordinal);
                            sites.Add(Runner.Name(method));
                        }
                    }
                    if (op == OpCodes.Newobj || (target is MethodInfo mi && mi.ReturnType != typeof(void)))
                        stack.Add(null);
                    continue;
                }
                if (op == OpCodes.Calli || op == OpCodes.Ret || op == OpCodes.Throw || op == OpCodes.Rethrow
                    || op == OpCodes.Leave || op == OpCodes.Leave_S || op == OpCodes.Endfinally || op == OpCodes.Endfilter)
                {
                    stack.Clear();
                    continue;
                }
                Pop(stack, PopCount(op.StackBehaviourPop));
                for (int i = PushCount(op.StackBehaviourPush); i > 0; i--) stack.Add(null);
            }
        }

        // Index of the name argument when `target` is a reflection lookup, else -1.
        private static int NameParameter(MethodBase target, ParameterInfo[] pars)
        {
            var declaring = target.DeclaringType;
            if (declaring == null) return -1;
            bool sink =
                ((declaring == typeof(Type) || declaring == typeof(Assembly) || declaring == typeof(Module))
                 && TypeLookups.Contains(target.Name))
                || (declaring == typeof(RuntimeReflectionExtensions) && RuntimeExtensionLookups.Contains(target.Name))
                || PluginSinks.Any(s => s.Type == declaring.FullName && s.Method == target.Name);
            if (!sink) return -1;
            for (int i = 0; i < pars.Length; i++)
                if (pars[i].ParameterType == typeof(string)) return i;
            return -1;
        }

        private static string[] Pop(List<string> stack, int count)
        {
            var popped = new string[count];
            for (int i = count - 1; i >= 0; i--)
            {
                if (stack.Count == 0) break;
                popped[i] = stack[stack.Count - 1];
                stack.RemoveAt(stack.Count - 1);
            }
            return popped;
        }

        private static int PopCount(StackBehaviour b)
        {
            switch (b)
            {
                case StackBehaviour.Pop0: return 0;
                case StackBehaviour.Pop1:
                case StackBehaviour.Popi:
                case StackBehaviour.Popref: return 1;
                case StackBehaviour.Pop1_pop1:
                case StackBehaviour.Popi_pop1:
                case StackBehaviour.Popi_popi:
                case StackBehaviour.Popi_popi8:
                case StackBehaviour.Popi_popr4:
                case StackBehaviour.Popi_popr8:
                case StackBehaviour.Popref_pop1:
                case StackBehaviour.Popref_popi: return 2;
                case StackBehaviour.Popi_popi_popi:
                case StackBehaviour.Popref_popi_popi:
                case StackBehaviour.Popref_popi_popi8:
                case StackBehaviour.Popref_popi_popr4:
                case StackBehaviour.Popref_popi_popr8:
                case StackBehaviour.Popref_popi_popref:
                case StackBehaviour.Popref_popi_pop1: return 3;
                default: return 0;
            }
        }

        private static int PushCount(StackBehaviour b)
        {
            switch (b)
            {
                case StackBehaviour.Push0: return 0;
                case StackBehaviour.Push1_push1: return 2;
                case StackBehaviour.Push1:
                case StackBehaviour.Pushi:
                case StackBehaviour.Pushi8:
                case StackBehaviour.Pushr4:
                case StackBehaviour.Pushr8:
                case StackBehaviour.Pushref: return 1;
                default: return 0;
            }
        }
    }
}
