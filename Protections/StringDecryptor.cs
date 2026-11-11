using System;
using System.Collections.Generic;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using Deobfuscator.Core;

namespace Deobfuscator.Protections
{
    public sealed class StringDecryptor
    {
        private readonly ModuleDef _mod;
        private readonly Emulator _emu;

        public int Decryptors;
        public int Sites;
        public int Failures;
        public string FirstFailureMethod;
        public string FirstFailureReason;
        public readonly Dictionary<string, int> FailureReasons = new();
        public readonly Dictionary<string, int> FailByMethod = new();

        public StringDecryptor(ModuleDef mod)
        {
            _mod = mod;
            _emu = new Emulator(mod);
            _emu.SeedStatics();
        }

        public void Run()
        {
            var map = new Dictionary<MethodDef, int>();
            foreach (var m in IlUtil.AllMethods(_mod))
            {
                if (!m.HasBody || m.HasThis) continue;
                if (m.ReturnType.FullName != "System.String") continue;
                if (m.Parameters.Count < 1 || m.Parameters.Count > 4) continue;
                if (!m.Parameters.Any(p => p.Type.FullName == "System.Int32" ||
                                          p.Type.FullName == "System.UInt32" ||
                                          p.Type.FullName == "System.Int64")) continue;
                var ins = m.Body.Instructions;
                if (ins.Count == 0 || ins.Count > 1500) continue;
                bool interesting = false;
                foreach (var i in ins)
                    if (i.OpCode == OpCodes.Xor || i.OpCode == OpCodes.Ldelem_U1 || i.OpCode == OpCodes.Ldelem_I1 ||
                        i.OpCode == OpCodes.Ldelem_U2 || i.OpCode == OpCodes.Call)
                    { interesting = true; break; }
                if (!interesting) continue;
                map[m] = m.Parameters.Count;
            }
            Decryptors = map.Count;

            foreach (var t in _mod.GetTypes())
                foreach (var m in t.Methods)
                {
                    if (!m.HasBody) continue;
                    var ins = m.Body.Instructions;
                    for (int i = 0; i < ins.Count; i++)
                    {
                        var instr = ins[i];
                        if (instr.OpCode.Code != Code.Call && instr.OpCode.Code != Code.Callvirt) continue;
                        if (instr.Operand is not MethodDef md) continue;
                        if (!map.TryGetValue(md, out int pc)) continue;

                        if (!TryConstArgs(ins, i, pc, out var args)) continue;

                        if (_emu.TryInvoke(md, args, out var r) && r is string s)
                        {
                            for (int k = 1; k <= pc; k++) ins[i - k].OpCode = OpCodes.Nop;
                            ins[i].OpCode = OpCodes.Ldstr;
                            ins[i].Operand = s;
                            Sites++;
                        }
                        else
                        {
                            Failures++;
                            FirstFailureMethod ??= md.FullName;
                            FirstFailureReason ??= _emu.LastError;
                            var key = _emu.LastError ?? "unknown";
                            FailureReasons[key] = FailureReasons.TryGetValue(key, out var c) ? c + 1 : 1;
                            var mk = md.FullName;
                            FailByMethod[mk] = FailByMethod.TryGetValue(mk, out var c2) ? c2 + 1 : 1;
                        }
                    }
                }
        }

        private static bool TryConstArgs(IList<Instruction> ins, int callIndex, int pc, out object[] args)
        {
            args = new object[pc];
            if (callIndex < pc) return false;
            for (int k = 0; k < pc; k++)
            {
                var a = ins[callIndex - pc + k];
                if (!ConstProducer(a, out var v)) return false;
                args[k] = v;
            }
            return true;
        }

        private static bool ConstProducer(Instruction i, out object v)
        {
            v = null;
            switch (i.OpCode.Code)
            {
                case Code.Ldnull: v = null; return true;
                case Code.Ldstr: v = (string)i.Operand; return true;
                case Code.Ldc_I4_M1: v = -1; return true;
                case Code.Ldc_I4_0: v = 0; return true;
                case Code.Ldc_I4_1: v = 1; return true;
                case Code.Ldc_I4_2: v = 2; return true;
                case Code.Ldc_I4_3: v = 3; return true;
                case Code.Ldc_I4_4: v = 4; return true;
                case Code.Ldc_I4_5: v = 5; return true;
                case Code.Ldc_I4_6: v = 6; return true;
                case Code.Ldc_I4_7: v = 7; return true;
                case Code.Ldc_I4_8: v = 8; return true;
                case Code.Ldc_I4: v = (int)i.Operand; return true;
                case Code.Ldc_I4_S: v = (int)(sbyte)i.Operand; return true;
                case Code.Ldc_I8: v = (long)i.Operand; return true;
                case Code.Ldc_R4: v = (float)i.Operand; return true;
                case Code.Ldc_R8: v = (double)i.Operand; return true;
                case Code.Ldtoken: v = i.Operand; return true;
                default: return false;
            }
        }
    }
}
