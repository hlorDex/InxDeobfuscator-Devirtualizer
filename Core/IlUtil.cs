using System;
using System.Collections.Generic;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace Deobfuscator.Core
{
    public static class IlUtil
    {
        public static IEnumerable<MethodDef> AllMethods(ModuleDef mod)
        {
            foreach (var t in mod.GetTypes())
                foreach (var m in t.Methods)
                    yield return m;
        }

        public static int Remove(MethodDef m, Func<Instruction, bool> remove)
        {
            var body = m.Body;
            var ins = body.Instructions;
            int count = ins.Count;
            if (count == 0) return 0;

            var pos = new Dictionary<Instruction, int>(count);
            for (int i = 0; i < count; i++) pos[ins[i]] = i;

            var rm = new bool[count];
            int removed = 0;
            for (int i = 0; i < count; i++)
                if (remove(ins[i])) { rm[i] = true; removed++; }
            if (removed == 0) return 0;

            var next = new Instruction[count];
            Instruction nxt = null;
            for (int i = count - 1; i >= 0; i--)
            {
                next[i] = nxt;
                if (!rm[i]) nxt = ins[i];
            }

            Instruction Map(Instruction t) => (t != null && pos.TryGetValue(t, out int p) && rm[p]) ? next[p] : t;

            for (int i = 0; i < count; i++)
            {
                if (rm[i]) continue;
                var o = ins[i].Operand;
                if (o is Instruction ti)
                {
                    ins[i].Operand = Map(ti);
                }
                else if (o is IList<Instruction> sw)
                {
                    for (int s = 0; s < sw.Count; s++) sw[s] = Map(sw[s]);
                }
            }

            foreach (var eh in body.ExceptionHandlers)
            {
                eh.TryStart = Map(eh.TryStart);
                eh.TryEnd = Map(eh.TryEnd);
                eh.HandlerStart = Map(eh.HandlerStart);
                eh.HandlerEnd = Map(eh.HandlerEnd);
                eh.FilterStart = Map(eh.FilterStart);
            }

            for (int i = count - 1; i >= 0; i--)
                if (rm[i]) ins.RemoveAt(i);

            return removed;
        }

        public static HashSet<Instruction> CollectTargets(MethodDef m)
        {
            var t = new HashSet<Instruction>();
            var body = m.Body;
            foreach (var i in body.Instructions)
            {
                if (i.Operand is Instruction ti) t.Add(ti);
                else if (i.Operand is IList<Instruction> sw)
                    foreach (var s in sw) t.Add(s);
            }
            foreach (var eh in body.ExceptionHandlers)
            {
                if (eh.TryStart != null) t.Add(eh.TryStart);
                if (eh.TryEnd != null) t.Add(eh.TryEnd);
                if (eh.HandlerStart != null) t.Add(eh.HandlerStart);
                if (eh.HandlerEnd != null) t.Add(eh.HandlerEnd);
                if (eh.FilterStart != null) t.Add(eh.FilterStart);
            }
            return t;
        }

        public static Instruction? ConstantToInstruction(object value)
        {
            switch (value)
            {
                case null: return Instruction.Create(OpCodes.Ldnull);
                case string s: return Instruction.Create(OpCodes.Ldstr, s);
                case bool b: return Instruction.CreateLdcI4(b ? 1 : 0);
                case int i: return Instruction.CreateLdcI4(i);
                case uint u: return Instruction.CreateLdcI4(unchecked((int)u));
                case short sh: return Instruction.CreateLdcI4(sh);
                case ushort us: return Instruction.CreateLdcI4(us);
                case sbyte sb: return Instruction.CreateLdcI4(sb);
                case byte by: return Instruction.CreateLdcI4(by);
                case char c: return Instruction.CreateLdcI4(c);
                case long l: return Instruction.Create(OpCodes.Ldc_I8, l);
                case ulong ul: return Instruction.Create(OpCodes.Ldc_I8, unchecked((long)ul));
                case float f: return Instruction.Create(OpCodes.Ldc_R4, f);
                case double d: return Instruction.Create(OpCodes.Ldc_R8, d);
                default: return null;
            }
        }
    }
}
