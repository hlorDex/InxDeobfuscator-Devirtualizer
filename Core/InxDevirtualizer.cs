using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace Deobfuscator.Core
{
    public sealed class InxDevirtualizer
    {
        private readonly ModuleDefMD _mod;
        private readonly byte[] _bres;
        private readonly OpCode[] _oneByte = new OpCode[256];
        private readonly OpCode[] _twoByte = new OpCode[256];
        private MethodDef _xoring;

        public int Stubs;
        public int Devirtualized;
        public int Failed;
        public readonly List<string> Errors = new List<string>();

        public InxDevirtualizer(ModuleDefMD mod, string resourceName = null)
        {
            _mod = mod;
            BuildOpCodes();
            var name = resourceName ?? FindInitResourceName();
            var er = mod.Resources.Find(name) as EmbeddedResource
                     ?? throw new InvalidOperationException($"Inx resource '{name}' not found");
            _bres = InxRuntime.ExclusiveOr(er.CreateReader().ToArray());
            _xoring = FindXoringMethod();
        }

        private void BuildOpCodes()
        {
            foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (f.FieldType != typeof(OpCode)) continue;
                var op = (OpCode)f.GetValue(null);
                ushort v = (ushort)op.Value;
                if (v <= 0xFF) _oneByte[(byte)v] = op;
                else _twoByte[(byte)v] = op;
            }
        }

        public string FindInitResourceName()
        {
            foreach (var t in _mod.GetTypes())
                foreach (var m in t.Methods)
                {
                    if (!m.HasBody) continue;
                    var ins = m.Body.Instructions;
                    for (int i = 1; i < ins.Count; i++)
                        if ((ins[i].OpCode == OpCodes.Call || ins[i].OpCode == OpCodes.Callvirt) &&
                            ins[i].Operand is IMethod im && im.Name == "Init" &&
                            (im.DeclaringType?.Name?.String.Contains("Class") ?? false) &&
                            ins[i - 1].OpCode == OpCodes.Ldstr)
                            return (string)ins[i - 1].Operand;
                }
            return null;
        }

        private MethodDef FindXoringMethod()
        {
            foreach (var t in _mod.GetTypes())
                foreach (var m in t.Methods)
                    if (m.Name == "Xoring" && m.IsStatic && m.Parameters.Count == 2 &&
                        m.ReturnType.FullName == "System.String")
                        return m;
            return null;
        }

        public void Run()
        {
            var stubs = new List<MethodDef>();
            foreach (var t in _mod.GetTypes())
                foreach (var m in t.Methods)
                {
                    if (!m.HasBody) continue;
                    foreach (var i in m.Body.Instructions)
                        if (i.OpCode == OpCodes.Call && i.Operand is IMethod im &&
                            im.Name == "Execute" && (im.DeclaringType?.FullName?.Contains("ConversionBack") ?? false))
                        { stubs.Add(m); break; }
                }
            Stubs = stubs.Count;
            foreach (var m in stubs)
            {
                try { if (Devirtualize(m)) Devirtualized++; else Failed++; }
                catch (Exception ex) { Failed++; Errors.Add($"{m.FullName}: {ex.Message}"); }
            }
        }

        private bool Devirtualize(MethodDef m)
        {
            var ins = m.Body.Instructions;
            int e = -1;
            for (int i = 0; i < ins.Count; i++)
                if (ins[i].OpCode == OpCodes.Call && ins[i].Operand is IMethod im &&
                    im.Name == "Execute" && (im.DeclaringType?.FullName?.Contains("ConversionBack") ?? false))
                { e = i; break; }
            if (e < 0) return false;

            var lds = new List<string>();
            for (int j = e - 1; j >= 0 && lds.Count < 6; j--)
                if (ins[j].OpCode == OpCodes.Ldstr && ins[j].Operand is string s) lds.Insert(0, s);
            if (lds.Count < 6) return false;

            int skip = int.Parse(InxRuntime.XoringStub(lds[0], lds[1]));
            int take = int.Parse(InxRuntime.XoringStub(lds[4], lds[5]));

            byte[] raw = RawIl(m);
            byte[] slice = new byte[take];
            Array.Copy(_bres, skip, slice, 0, take);
            slice = InxRuntime.DecryptorXor(slice, raw);
            byte[] key = InxRuntime.KeyXor(InxRuntime.Md5(m.Name), InxRuntime.Key2);
            byte[] plain = InxRuntime.RijndaelDecrypt(key, slice);

            var locals = m.Body.Variables.ToList();
            var body = Parse(plain, locals);
            body.InitLocals = m.Body.InitLocals || locals.Count > 0;
            body.KeepOldMaxStack = false;
            body.MaxStack = (ushort)Math.Min(4096, Math.Max(8, body.Instructions.Count + 8));
            m.Body = body;
            m.Body.UpdateInstructionOffsets();
            return true;
        }

        private byte[] RawIl(MethodDef m)
        {
            var r = _mod.Metadata.PEImage.CreateReader(m.RVA);
            byte b0 = r.ReadByte();
            int codeSize, codeOff;
            if ((b0 & 3) == 2) { codeSize = b0 >> 2; codeOff = 1; }
            else { r.Position = 0; ushort f = r.ReadUInt16(); r.ReadUInt16(); codeSize = r.ReadInt32(); codeOff = (f >> 12) * 4; }
            r.Position = (uint)codeOff;
            return r.ReadBytes(codeSize);
        }

        private CilBody Parse(byte[] plain, List<Local> locals)
        {
            var br = new BinaryReader(new MemoryStream(plain));
            var body = new CilBody();
            foreach (var l in locals) body.Variables.Add(l);

            int excCount = br.ReadInt32();
            var rawHandlers = new List<(int tryStart, int tryEnd, int handlerStart, int handlerEnd, int filterStart, int type, uint catchTok)>();
            for (int i = 0; i < excCount; i++)
            {
                int catchTok = br.ReadInt32();
                int filterStart = br.ReadInt32();
                int handlerEnd = br.ReadInt32();
                int handlerStart = br.ReadInt32();
                int type = br.ReadByte();
                int tryEnd = br.ReadInt32();
                int tryStart = br.ReadInt32();
                rawHandlers.Add((tryStart, tryEnd, handlerStart, handlerEnd, filterStart, type, (uint)catchTok));
            }

            int instrCount = br.ReadInt32();
            var instrs = new List<Instruction>(instrCount);
            for (int i = 0; i < instrCount; i++) instrs.Add(new Instruction(OpCodes.Nop));

            for (int k = 0; k < instrCount; k++)
            {
                short sv = br.ReadInt16();
                int num4 = sv;
                OpCode op = (num4 >= 0 && num4 < 256) ? _oneByte[num4] : _twoByte[(byte)(num4 | 65024)];
                byte opType = br.ReadByte();
                object operand = ReadOperand(opType, br, instrs, body.Variables);
                instrs[k].OpCode = op;
                instrs[k].Operand = operand;
            }

            foreach (var h in rawHandlers)
            {
                var eh = new ExceptionHandler(MapHandlerType(h.type))
                {
                    TryStart = instrs[h.tryStart],
                    TryEnd = instrs[h.tryEnd],
                    HandlerStart = instrs[h.handlerStart],
                    HandlerEnd = instrs[h.handlerEnd],
                    CatchType = h.catchTok == 0xFFFFFFFF ? null : _mod.ResolveToken(h.catchTok) as ITypeDefOrRef,
                };
                if (h.filterStart >= 0 && h.filterStart < instrs.Count) eh.FilterStart = instrs[h.filterStart];
                body.ExceptionHandlers.Add(eh);
            }

            foreach (var ins in instrs) body.Instructions.Add(ins);
            return body;
        }

        private static ExceptionHandlerType MapHandlerType(int t) => t switch
        {
            1 => ExceptionHandlerType.Catch,
            2 => ExceptionHandlerType.Filter,
            3 => ExceptionHandlerType.Finally,
            5 => ExceptionHandlerType.Finally,
            _ => ExceptionHandlerType.Catch,
        };

        private object ReadOperand(byte opType, BinaryReader br, List<Instruction> instrs, IList<Local> locals)
        {
            switch (opType)
            {
                case 0: return null;
                case 1: return _mod.ResolveToken(br.ReadInt32());
                case 2: return br.ReadString();
                case 3: return br.ReadInt32();
                case 4:
                case 12:
                    {
                        int num = br.ReadInt32();
                        byte flag = br.ReadByte();
                        if (flag == 0) return (num >= 0 && num < locals.Count) ? (object)locals[num] : num;
                        return num;
                    }
                case 5: return _mod.ResolveToken(br.ReadInt32());
                case 6: return _mod.ResolveToken(br.ReadInt32());
                case 7: return instrs[br.ReadInt32()];
                case 8: return (int)br.ReadByte();
                case 9:
                    {
                        int n = br.ReadInt32();
                        var arr = new Instruction[n];
                        for (int i = 0; i < n; i++) arr[i] = instrs[br.ReadInt32()];
                        return arr;
                    }
                case 10: return instrs[br.ReadInt32()];
                case 11:
                    {
                        int tok = br.ReadInt32();
                        br.ReadByte();
                        return _mod.ResolveToken(tok);
                    }
                case 13: return BitConverter.ToSingle(br.ReadBytes(4), 0);
                case 14: return br.ReadDouble();
                case 15: return br.ReadInt64();
                default: throw new Exception("Unknown opType " + opType);
            }
        }
    }
}
