using System.Collections.Generic;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using Deobfuscator.Core;

namespace Deobfuscator.Protections
{
    public sealed class SimpleProxyRemover
    {
        private readonly struct ProxyTarget
        {
            public readonly Code Op;
            public readonly IMethod Method;

            public ProxyTarget(Code op, IMethod method)
            {
                Op = op;
                Method = method;
            }
        }

        private readonly ModuleDef _mod;

        public int Proxies;
        public int Sites;
        public int Removed;

        public SimpleProxyRemover(ModuleDef mod)
        {
            _mod = mod;
        }

        public void Run()
        {
            var accessors = CollectPropertyAndEventAccessors();
            var raw = Detect(accessors);
            var resolved = ResolveChains(raw);
            Proxies = resolved.Count;
            if (resolved.Count == 0) return;

            RewriteCallSites(resolved);
            RemoveDeadProxies(resolved);
        }

        private HashSet<MethodDef> CollectPropertyAndEventAccessors()
        {
            var set = new HashSet<MethodDef>();
            foreach (var t in _mod.GetTypes())
            {
                foreach (var p in t.Properties)
                {
                    if (p.GetMethod != null) set.Add(p.GetMethod);
                    if (p.SetMethod != null) set.Add(p.SetMethod);
                    foreach (var om in p.OtherMethods) set.Add(om);
                }
                foreach (var e in t.Events)
                {
                    if (e.AddMethod != null) set.Add(e.AddMethod);
                    if (e.RemoveMethod != null) set.Add(e.RemoveMethod);
                    if (e.InvokeMethod != null) set.Add(e.InvokeMethod);
                    foreach (var om in e.OtherMethods) set.Add(om);
                }
            }
            return set;
        }

        private Dictionary<MethodDef, ProxyTarget> Detect(HashSet<MethodDef> accessors)
        {
            var map = new Dictionary<MethodDef, ProxyTarget>();
            foreach (var t in _mod.GetTypes())
            {
                if (t.HasGenericParameters) continue;
                foreach (var m in t.Methods)
                {
                    if (accessors.Contains(m)) continue;
                    if (TryGetProxyTarget(t, m, out var target))
                        map[m] = target;
                }
            }
            return map;
        }

        private bool TryGetProxyTarget(TypeDef declaringType, MethodDef m, out ProxyTarget target)
        {
            target = default;
            if (!m.HasBody || m.Body.ExceptionHandlers.Count > 0) return false;
            if (m.IsConstructor || m.IsStaticConstructor || m.IsVirtual || m.IsAbstract) return false;
            if (m.HasGenericParameters || m == _mod.EntryPoint || m.HasImplMap || m.ExportInfo != null) return false;

            var ins = new List<Instruction>(m.Body.Instructions.Count);
            foreach (var instr in m.Body.Instructions)
                if (instr.OpCode.Code != Code.Nop)
                    ins.Add(instr);

            int expectedArgs = m.Parameters.Count + (m.HasThis ? 1 : 0);
            if (ins.Count != expectedArgs + 2) return false;
            if (ins[^1].OpCode.Code != Code.Ret) return false;

            for (int k = 0; k < expectedArgs; k++)
                if (!IsLdarg(ins[k], k)) return false;

            var call = ins[expectedArgs];
            var cc = call.OpCode.Code;
            if (cc != Code.Call && cc != Code.Callvirt && cc != Code.Newobj) return false;
            if (call.Operand is not IMethod tgt || tgt.MethodSig == null) return false;
            if (tgt is MethodSpec || tgt.DeclaringType is TypeSpec) return false;
            if (tgt.ResolveMethodDef() == m) return false;

            int tgtArgs = tgt.MethodSig.Params.Count + (tgt.MethodSig.HasThis && cc != Code.Newobj ? 1 : 0);
            if (tgtArgs != expectedArgs) return false;

            if (cc == Code.Newobj)
            {
                if (m.HasThis || tgt.Name != ".ctor" || !tgt.MethodSig.HasThis) return false;
                if (m.ReturnType.FullName != tgt.DeclaringType?.FullName) return false;
            }
            else if (m.ReturnType.FullName != tgt.MethodSig.RetType.FullName)
            {
                return false;
            }

            target = new ProxyTarget(cc, tgt);
            return true;
        }

        private static Dictionary<MethodDef, ProxyTarget> ResolveChains(Dictionary<MethodDef, ProxyTarget> raw)
        {
            var resolved = new Dictionary<MethodDef, ProxyTarget>(raw.Count);
            foreach (var kv in raw)
            {
                var cur = kv.Value;
                var visited = new HashSet<MethodDef> { kv.Key };
                bool cycle = false;

                while (cur.Method.ResolveMethodDef() is MethodDef nextMd && raw.TryGetValue(nextMd, out var next))
                {
                    if (!visited.Add(nextMd))
                    {
                        cycle = true;
                        break;
                    }
                    cur = next;
                }

                if (!cycle)
                    resolved[kv.Key] = cur;
            }
            return resolved;
        }

        private void RewriteCallSites(Dictionary<MethodDef, ProxyTarget> resolved)
        {
            foreach (var t in _mod.GetTypes())
            {
                foreach (var m in t.Methods)
                {
                    if (!m.HasBody) continue;
                    foreach (var instr in m.Body.Instructions)
                    {
                        if (instr.OpCode.Code != Code.Call && instr.OpCode.Code != Code.Callvirt) continue;
                        if (instr.Operand is not IMethod im) continue;
                        if (im.ResolveMethodDef() is not MethodDef md) continue;
                        if (!resolved.TryGetValue(md, out var target)) continue;

                        if (target.Method.ResolveMethodDef() is MethodDef tgtDef && !IsAccessible(tgtDef, t))
                            continue;

                        instr.OpCode = ToOpCode(target.Op);
                        instr.Operand = target.Method;
                        Sites++;
                    }
                }
            }
        }

        private void RemoveDeadProxies(Dictionary<MethodDef, ProxyTarget> resolved)
        {
            var referenced = CollectReferencedMethods();
            foreach (var t in _mod.GetTypes())
            {
                for (int i = t.Methods.Count - 1; i >= 0; i--)
                {
                    var m = t.Methods[i];
                    if (resolved.ContainsKey(m) && !referenced.Contains(m))
                    {
                        t.Methods.RemoveAt(i);
                        Removed++;
                    }
                }
            }
        }

        private HashSet<MethodDef> CollectReferencedMethods()
        {
            var referenced = new HashSet<MethodDef>();
            if (_mod.EntryPoint != null) referenced.Add(_mod.EntryPoint);

            foreach (var t in _mod.GetTypes())
            {
                foreach (var p in t.Properties)
                {
                    if (p.GetMethod != null) referenced.Add(p.GetMethod);
                    if (p.SetMethod != null) referenced.Add(p.SetMethod);
                    foreach (var om in p.OtherMethods) referenced.Add(om);
                }
                foreach (var e in t.Events)
                {
                    if (e.AddMethod != null) referenced.Add(e.AddMethod);
                    if (e.RemoveMethod != null) referenced.Add(e.RemoveMethod);
                    if (e.InvokeMethod != null) referenced.Add(e.InvokeMethod);
                    foreach (var om in e.OtherMethods) referenced.Add(om);
                }
                foreach (var m in t.Methods)
                {
                    foreach (var ov in m.Overrides)
                        if (ov.MethodBody?.ResolveMethodDef() is MethodDef bm)
                            referenced.Add(bm);

                    if (!m.HasBody) continue;
                    foreach (var instr in m.Body.Instructions)
                        if (instr.Operand is IMethod im && im.ResolveMethodDef() is MethodDef rmd)
                            referenced.Add(rmd);
                }
            }
            return referenced;
        }

        private static bool IsAccessible(MethodDef tgt, TypeDef fromType)
        {
            if (tgt.IsPublic || tgt.IsAssembly || tgt.IsFamilyOrAssembly) return true;
            for (var cur = fromType; cur != null; cur = cur.DeclaringType)
                if (cur == tgt.DeclaringType)
                    return true;
            return false;
        }

        private static bool IsLdarg(Instruction i, int index)
        {
            switch (i.OpCode.Code)
            {
                case Code.Ldarg_0: return index == 0;
                case Code.Ldarg_1: return index == 1;
                case Code.Ldarg_2: return index == 2;
                case Code.Ldarg_3: return index == 3;
                case Code.Ldarg:
                case Code.Ldarg_S: return i.Operand is Parameter p && p.Index == index;
                default: return false;
            }
        }

        private static OpCode ToOpCode(Code code) => code switch
        {
            Code.Newobj => OpCodes.Newobj,
            Code.Callvirt => OpCodes.Callvirt,
            _ => OpCodes.Call
        };
    }
}
