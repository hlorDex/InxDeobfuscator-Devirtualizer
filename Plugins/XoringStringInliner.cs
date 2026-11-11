using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace Deobfuscator.Plugins
{
    public sealed class XoringStringInliner
    {
        private readonly ModuleDef _mod;
        private readonly Dictionary<MethodDef, int> _scramblers = new();

        public int Inlined;

        public XoringStringInliner(ModuleDef mod) { _mod = mod; }

        public void Run()
        {
            Detect();
            foreach (var t in _mod.GetTypes())
                foreach (var m in t.Methods)
                {
                    if (!m.HasBody) continue;
                    var ins = m.Body.Instructions;
                    for (int i = 0; i < ins.Count; i++)
                    {
                        if (ins[i].OpCode != OpCodes.Call || ins[i].Operand is not MethodDef md) continue;
                        if (!_scramblers.TryGetValue(md, out int pc)) continue;

                        var args = new string[pc];
                        bool ok = true;
                        for (int k = 0; k < pc; k++)
                        {
                            var a = ins[i - 1 - k];
                            if (a.OpCode == OpCodes.Ldstr && a.Operand is string s) args[pc - 1 - k] = s;
                            else { ok = false; break; }
                        }
                        if (!ok) continue;

                        string result = pc == 2 ? InxRuntime_XoringStub(args[0], args[1]) : InxRuntime_Xoring1(args[0]);
                        for (int k = 0; k < pc; k++) ins[i - 1 - k].OpCode = OpCodes.Nop;
                        ins[i].OpCode = OpCodes.Ldstr;
                        ins[i].Operand = result;
                        Inlined++;
                    }
                }
        }

        private void Detect()
        {
            foreach (var t in _mod.GetTypes())
                foreach (var m in t.Methods)
                {
                    if (!m.HasBody) continue;
                    if (m.ReturnType.FullName != "System.String") continue;
                    if (m.Parameters.Count < 1 || m.Parameters.Count > 2) continue;
                    if (m.Parameters.Any(p => p.Type.FullName != "System.String")) continue;
                    var ins = m.Body.Instructions;
                    if (ins.Count > 80) continue;
                    if (!ins.Any(x => x.OpCode == OpCodes.Xor)) continue;
                    _scramblers[m] = m.Parameters.Count;
                }
        }

        private static string InxRuntime_XoringStub(string a, string b)
        {
            byte[] ba = Encoding.UTF8.GetBytes(a), bb = Encoding.UTF8.GetBytes(b);
            int n = Math.Min(ba.Length, bb.Length);
            var sb = new StringBuilder();
            for (int i = 0; i < n; i++) sb.Append((char)(ba[i] ^ bb[i]));
            if (a.Length > n) sb.Append(a.Substring(n));
            return sb.ToString();
        }

        private static string InxRuntime_Xoring1(string input)
        {
            var sb = new StringBuilder(input.Length);
            foreach (char c in input) sb.Append((char)(c ^ '\u0639'));
            return sb.ToString();
        }
    }
}
