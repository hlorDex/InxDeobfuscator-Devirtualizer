using System;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using Deobfuscator.Core;

namespace Deobfuscator.Protections
{
    public sealed class JunkRemover
    {
        private readonly ModuleDef _mod;

        public int Prefixes;
        public int FallsThrough;
        public int Nops;
        public int DeadCode;

        public JunkRemover(ModuleDef mod) { _mod = mod; }

        public void Run()
        {
            foreach (var m in IlUtil.AllMethods(_mod))
            {
                if (!m.HasBody || m.Body.Instructions.Count == 0) continue;
                var body = m.Body;

                for (int pass = 0; pass < 32; pass++)
                {
                    int before = body.Instructions.Count;

                    Prefixes += IlUtil.Remove(m, i =>
                        i.OpCode.Code == Code.Unaligned ||
                        i.OpCode.Code == Code.Volatile ||
                        i.OpCode.Code == Code.Readonly);

                    FallsThrough += IlUtil.Remove(m, i => IsBrToNext(m, i));

                    Nops += IlUtil.Remove(m, i => i.OpCode.Code == Code.Nop);

                    DeadCode += RemoveDeadCode(m);

                    if (body.Instructions.Count == before) break;
                }
            }
        }

        private static bool IsBrToNext(MethodDef m, Instruction cur)
        {
            if (cur.OpCode.Code != Code.Br && cur.OpCode.Code != Code.Br_S) return false;
            if (cur.Operand is not Instruction t) return false;
            var ins = m.Body.Instructions;
            int k = ins.IndexOf(cur);
            return k >= 0 && k + 1 < ins.Count && ins[k + 1] == t;
        }

        private static int RemoveDeadCode(MethodDef m)
        {
            var body = m.Body;
            var ins = body.Instructions;
            var targets = IlUtil.CollectTargets(m);
            var dead = new System.Collections.Generic.HashSet<Instruction>();
            for (int k = 0; k < ins.Count; k++)
            {
                var c = ins[k].OpCode.Code;
                bool terminal = c == Code.Br || c == Code.Br_S || c == Code.Ret ||
                                c == Code.Throw || c == Code.Rethrow ||
                                c == Code.Endfinally || c == Code.Endfilter;
                if (!terminal) continue;
                for (int j = k + 1; j < ins.Count; j++)
                {
                    if (targets.Contains(ins[j])) break;
                    dead.Add(ins[j]);
                }
            }
            if (dead.Count == 0) return 0;
            return IlUtil.Remove(m, i => dead.Contains(i));
        }
    }
}
