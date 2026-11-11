using System;
using System.Collections.Generic;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using Deobfuscator.Core;

namespace Deobfuscator.Protections
{
    public sealed class ProxyRemover
    {
        private readonly ModuleDef _mod;
        private readonly Emulator _emu;

        public int ConstantProxies;
        public int ConstantSites;
        public int ForwardingProxies;
        public int ForwardingSites;
        public readonly List<string> Notes = new();

        public ProxyRemover(ModuleDef mod)
        {
            _mod = mod;
            _emu = new Emulator(mod);
            _emu.SeedStatics();
        }

        public void Run()
        {
            var constantMap = new Dictionary<MethodDef, object>();
            var forwardMap = new Dictionary<MethodDef, IMethod>();

            bool progress = true;
            var pending = new List<MethodDef>();
            foreach (var m in IlUtil.AllMethods(_mod))
            {
                if (!m.HasBody || m.Parameters.Count != 0 || m.HasThis) continue;
                if (m.ReturnType.FullName == "System.Void") continue;
                if (m.Body.Instructions.Count > 400) continue;
                pending.Add(m);
            }

            while (progress)
            {
                progress = false;
                for (int k = pending.Count - 1; k >= 0; k--)
                {
                    var m = pending[k];
                    if (constantMap.ContainsKey(m)) continue;
                    if (_emu.TryInvoke(m, Array.Empty<object>(), out var r) && r != null &&
                        IlUtil.ConstantToInstruction(r) != null)
                    {
                        constantMap[m] = r;
                        pending.RemoveAt(k);
                        progress = true;
                    }
                }
            }

            foreach (var m in IlUtil.AllMethods(_mod))
            {
                if (!m.HasBody || m.HasThis) continue;
                var fwd = DetectForwarder(m);
                if (fwd != null) forwardMap[m] = fwd;
            }

            ConstantProxies = constantMap.Count;
            ForwardingProxies = forwardMap.Count;

            foreach (var t in _mod.GetTypes())
                foreach (var m in t.Methods)
                {
                    if (!m.HasBody) continue;
                    var ins = m.Body.Instructions;
                    for (int i = 0; i < ins.Count; i++)
                    {
                        var instr = ins[i];
                        if ((instr.OpCode.Code != Code.Call && instr.OpCode.Code != Code.Callvirt) ||
                            instr.Operand is not MethodDef md) continue;

                        if (constantMap.TryGetValue(md, out var c))
                        {
                            var rep = IlUtil.ConstantToInstruction(c);
                            if (rep != null)
                            {
                                ins[i] = rep;
                                ConstantSites++;
                            }
                            continue;
                        }

                        if (forwardMap.TryGetValue(md, out var fwd))
                        {
                            if (instr.OpCode.Code == Code.Callvirt && !fwd.ResolveMethodDefHasThis())
                                instr.OpCode = OpCodes.Call;
                            instr.Operand = fwd;
                            ForwardingSites++;
                        }
                    }
                }
        }

        private static MethodDef DetectForwarder(MethodDef m)
        {
            if (!m.HasBody) return null;
            var ins = m.Body.Instructions;
            int n = m.Parameters.Count + (m.HasThis ? 1 : 0);
            if (n == 0 || ins.Count != n + 2) return null;
            for (int i = 0; i < n; i++)
                if (!IsLdarg(ins[i], i)) return null;
            var call = ins[n];
            if (call.OpCode.Code != Code.Call && call.OpCode.Code != Code.Callvirt) return null;
            if (ins[n + 1].OpCode.Code != Code.Ret) return null;
            if (call.Operand is not IMethod tgt) return null;
            if (tgt.MethodSig == null) return null;
            if (tgt.MethodSig.Params.Count != m.Parameters.Count) return null;
            if (tgt.MethodSig.HasThis != m.HasThis) return null;
            if (tgt.MethodSig.RetType.FullName != m.ReturnType.FullName) return null;
            return tgt.ResolveMethodDef();
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
    }

    internal static class ProxyExt
    {
        public static bool ResolveMethodDefHasThis(this IMethod m)
        {
            if (m.MethodSig != null) return m.MethodSig.HasThis;
            var md = m.ResolveMethodDef();
            return md != null && md.HasThis;
        }
    }
}
