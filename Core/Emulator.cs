using System;
using System.Collections.Generic;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace Deobfuscator.Core
{
    public sealed class Emulator
    {
        private sealed class EmuUnsupported : Exception
        {
            public EmuUnsupported(string m) : base(m) { }
        }

        private readonly ModuleDef _mod;
        private readonly Dictionary<IField, object> _statics = new();
        private readonly long _maxSteps;
        private long _steps;

        public string LastError;

        public Emulator(ModuleDef mod, long maxSteps = 5_000_000)
        {
            _mod = mod;
            _maxSteps = maxSteps;
        }

        public void ResetStatics() => _statics.Clear();

        public void SeedStatic(IField field, object value) => _statics[field] = value;

        public int SeedStatics()
        {
            int seeded = 0;
            foreach (var m in IlUtil.AllMethods(_mod))
            {
                if (!m.HasBody) continue;
                var ins = m.Body.Instructions;
                for (int k = 0; k < ins.Count; k++)
                {
                    if (ins[k].OpCode != OpCodes.Call || ins[k].Operand is not IMethod im) continue;
                    if (im.Name != "InitializeArray") continue;
                    if (im.DeclaringType?.FullName != "System.Runtime.CompilerServices.RuntimeHelpers") continue;

                    FieldDef rva = null;
                    for (int j = k - 1; j >= 0 && j >= k - 4; j--)
                        if (ins[j].OpCode == OpCodes.Ldtoken && ins[j].Operand is FieldDef fd) { rva = fd; break; }
                    if (rva?.InitialValue == null) continue;

                    for (int j = k + 1; j < ins.Count && j <= k + 8; j++)
                    {
                        if (ins[j].OpCode == OpCodes.Stsfld && ins[j].Operand is IField sf)
                        {
                            _statics[sf] = rva.InitialValue;
                            seeded++;
                            break;
                        }
                    }
                }
            }
            return seeded;
        }

        private readonly object Marker = new();

        private object ResStream(object asm, string resName)
        {
            if (resName == null) return new System.IO.MemoryStream();
            foreach (var r in _mod.Resources)
            {
                if (r.Name != resName) continue;
                if (r is EmbeddedResource er)
                {
                    var data = er.CreateReader().ToArray();
                    return new System.IO.MemoryStream(data);
                }
            }
            return new System.IO.MemoryStream();
        }

        public bool TryInvoke(MethodDef method, object[] args, out object result)
        {
            result = null;
            try
            {
                _steps = 0;
                result = Execute(method, args);
                return true;
            }
            catch (EmuUnsupported e) { LastError = e.Message; return false; }
            catch (Exception e) { LastError = e.GetType().Name + ": " + e.Message; return false; }
        }

        private object Execute(MethodDef method, object[] args)
        {
            if (!method.HasBody) throw new EmuUnsupported("no body");
            var body = method.Body;
            var ins = body.Instructions;
            if (ins.Count == 0) return null;

            var idx = new Dictionary<Instruction, int>(ins.Count);
            for (int i = 0; i < ins.Count; i++) idx[ins[i]] = i;

            var locals = new object[body.Variables.Count];
            var stack = new Stack<object>();
            int ip = 0;

            while (ip < ins.Count)
            {
                if (++_steps > _maxSteps) throw new EmuUnsupported("step limit");
                var instr = ins[ip];
                var code = instr.OpCode.Code;
                bool jumped = false;

                switch (code)
                {
                    case Code.Nop:
                    case Code.Break:
                    case Code.Unaligned:
                    case Code.Volatile:
                        break;

                    case Code.Ldarg_0: stack.Push(Arg(args, 0)); break;
                    case Code.Ldarg_1: stack.Push(Arg(args, 1)); break;
                    case Code.Ldarg_2: stack.Push(Arg(args, 2)); break;
                    case Code.Ldarg_3: stack.Push(Arg(args, 3)); break;
                    case Code.Ldarg: stack.Push(Arg(args, ArgIndex(instr, method))); break;
                    case Code.Ldarg_S: stack.Push(Arg(args, ArgIndex(instr, method))); break;
                    case Code.Ldarga:
                    case Code.Ldarga_S: stack.Push(new Ptr(new[] { Arg(args, ArgIndex(instr, method)) }, 0)); break;

                    case Code.Ldloc_0: stack.Push(locals[0]); break;
                    case Code.Ldloc_1: stack.Push(locals[1]); break;
                    case Code.Ldloc_2: stack.Push(locals[2]); break;
                    case Code.Ldloc_3: stack.Push(locals[3]); break;
                    case Code.Ldloc: stack.Push(locals[((Local)instr.Operand).Index]); break;
                    case Code.Ldloc_S: stack.Push(locals[((Local)instr.Operand).Index]); break;

                    case Code.Stloc_0: locals[0] = Pop(stack); break;
                    case Code.Stloc_1: locals[1] = Pop(stack); break;
                    case Code.Stloc_2: locals[2] = Pop(stack); break;
                    case Code.Stloc_3: locals[3] = Pop(stack); break;
                    case Code.Stloc: locals[((Local)instr.Operand).Index] = Pop(stack); break;
                    case Code.Stloc_S: locals[((Local)instr.Operand).Index] = Pop(stack); break;

                    case Code.Ldnull: stack.Push(null); break;
                    case Code.Ldc_I4_M1: stack.Push(-1); break;
                    case Code.Ldc_I4_0: stack.Push(0); break;
                    case Code.Ldc_I4_1: stack.Push(1); break;
                    case Code.Ldc_I4_2: stack.Push(2); break;
                    case Code.Ldc_I4_3: stack.Push(3); break;
                    case Code.Ldc_I4_4: stack.Push(4); break;
                    case Code.Ldc_I4_5: stack.Push(5); break;
                    case Code.Ldc_I4_6: stack.Push(6); break;
                    case Code.Ldc_I4_7: stack.Push(7); break;
                    case Code.Ldc_I4_8: stack.Push(8); break;
                    case Code.Ldc_I4: stack.Push((int)instr.Operand); break;
                    case Code.Ldc_I4_S: stack.Push((int)(sbyte)instr.Operand); break;
                    case Code.Ldc_I8: stack.Push((long)instr.Operand); break;
                    case Code.Ldc_R4: stack.Push((float)instr.Operand); break;
                    case Code.Ldc_R8: stack.Push((double)instr.Operand); break;
                    case Code.Ldstr: stack.Push((string)instr.Operand); break;

                    case Code.Dup: stack.Push(stack.Peek()); break;
                    case Code.Pop: Pop(stack); break;

                    case Code.Add: { var b = Pop(stack); var a = Pop(stack); stack.Push(AddVals(a, b)); break; }
                    case Code.Sub: { var b = Pop(stack); var a = Pop(stack); stack.Push(SubVals(a, b)); break; }
                    case Code.Mul: Bin(stack, (a, b) => IsF(a) || IsF(b) ? (object)(R8(a) * R8(b)) : IsL(a) || IsL(b) ? I8(a) * I8(b) : I4(a) * I4(b)); break;
                    case Code.Div: Bin(stack, (a, b) => IsF(a) || IsF(b) ? (object)(R8(a) / R8(b)) : IsL(a) || IsL(b) ? I8(a) / I8(b) : I4(a) / I4(b)); break;
                    case Code.Div_Un: Bin(stack, (a, b) => IsL(a) || IsL(b) ? (object)((long)(I8(a) / I8(b))) : (I4(a) / I4(b))); break;
                    case Code.Rem: Bin(stack, (a, b) => IsF(a) || IsF(b) ? (object)(R8(a) % R8(b)) : IsL(a) || IsL(b) ? I8(a) % I8(b) : I4(a) % I4(b)); break;
                    case Code.Rem_Un: Bin(stack, (a, b) => IsL(a) || IsL(b) ? (object)(I8(a) % I8(b)) : (I4(a) % I4(b))); break;
                    case Code.And: Bin(stack, (a, b) => IsL(a) || IsL(b) ? (object)(I8(a) & I8(b)) : I4(a) & I4(b)); break;
                    case Code.Or: Bin(stack, (a, b) => IsL(a) || IsL(b) ? (object)(I8(a) | I8(b)) : I4(a) | I4(b)); break;
                    case Code.Xor: Bin(stack, (a, b) => IsL(a) || IsL(b) ? (object)(I8(a) ^ I8(b)) : I4(a) ^ I4(b)); break;
                    case Code.Shl: Bin(stack, (a, b) => IsL(a) ? (object)(I8(a) << I4(b)) : I4(a) << (I4(b) & 31)); break;
                    case Code.Shr: Bin(stack, (a, b) => IsL(a) ? (object)(I8(a) >> I4(b)) : I4(a) >> (I4(b) & 31)); break;
                    case Code.Shr_Un: Bin(stack, (a, b) => IsL(a) ? (object)(I8(a) >> I4(b)) : (int)((uint)I4(a) >> (I4(b) & 31))); break;
                    case Code.Neg: Un(stack, a => IsF(a) ? (object)(-R8(a)) : IsL(a) ? (object)(-I8(a)) : -I4(a)); break;
                    case Code.Not: Un(stack, a => IsL(a) ? (object)(~I8(a)) : ~I4(a)); break;

                    case Code.Conv_I1: Un(stack, a => a is Ptr ? a : (sbyte)I4(a)); break;
                    case Code.Conv_I2: Un(stack, a => a is Ptr ? a : (short)I4(a)); break;
                    case Code.Conv_I4: Un(stack, a => a is Ptr ? a : (int)I8(a)); break;
                    case Code.Conv_I8: Un(stack, a => a is Ptr ? a : IsF(a) ? (long)R8(a) : I8(a)); break;
                    case Code.Conv_U1: Un(stack, a => a is Ptr ? a : (byte)I4(a)); break;
                    case Code.Conv_U2: Un(stack, a => a is Ptr ? a : (ushort)I4(a)); break;
                    case Code.Conv_U4: Un(stack, a => a is Ptr ? a : (uint)I8(a)); break;
                    case Code.Conv_U8: Un(stack, a => a is Ptr ? a : (ulong)I8(a)); break;
                    case Code.Conv_I: Un(stack, a => a is Ptr ? a : (long)I8(a)); break;
                    case Code.Conv_U: Un(stack, a => a is Ptr ? a : (long)I8(a)); break;
                    case Code.Conv_R4: Un(stack, a => a is Ptr ? a : (float)R8(a)); break;
                    case Code.Conv_R8: Un(stack, a => a is Ptr ? a : R8(a)); break;
                    case Code.Conv_R_Un: Un(stack, a => a is Ptr ? a : R8(a)); break;

                    case Code.Ceq: Cmp(stack, (a, b) => EqInt(a, b)); break;
                    case Code.Cgt: Cmp(stack, (a, b) => CmpNum(a, b) > 0); break;
                    case Code.Cgt_Un: Cmp(stack, (a, b) => CmpNum(a, b) > 0); break;
                    case Code.Clt: Cmp(stack, (a, b) => CmpNum(a, b) < 0); break;
                    case Code.Clt_Un: Cmp(stack, (a, b) => CmpNum(a, b) < 0); break;

                    case Code.Br:
                    case Code.Br_S: ip = idx[(Instruction)instr.Operand]; jumped = true; break;
                    case Code.Brtrue:
                    case Code.Brtrue_S: if (Truthy(Pop(stack))) { ip = idx[(Instruction)instr.Operand]; jumped = true; } break;
                    case Code.Brfalse:
                    case Code.Brfalse_S: if (!Truthy(Pop(stack))) { ip = idx[(Instruction)instr.Operand]; jumped = true; } break;
                    case Code.Beq:
                    case Code.Beq_S: { var b = Pop(stack); var a = Pop(stack); if (EqInt(a, b)) { ip = idx[(Instruction)instr.Operand]; jumped = true; } break; }
                    case Code.Bne_Un:
                    case Code.Bne_Un_S: { var b = Pop(stack); var a = Pop(stack); if (!EqInt(a, b)) { ip = idx[(Instruction)instr.Operand]; jumped = true; } break; }
                    case Code.Bgt:
                    case Code.Bgt_S:
                    case Code.Bgt_Un:
                    case Code.Bgt_Un_S: { var b = Pop(stack); var a = Pop(stack); if (CmpNum(a, b) > 0) { ip = idx[(Instruction)instr.Operand]; jumped = true; } break; }
                    case Code.Blt:
                    case Code.Blt_S:
                    case Code.Blt_Un:
                    case Code.Blt_Un_S: { var b = Pop(stack); var a = Pop(stack); if (CmpNum(a, b) < 0) { ip = idx[(Instruction)instr.Operand]; jumped = true; } break; }
                    case Code.Bge:
                    case Code.Bge_S:
                    case Code.Bge_Un:
                    case Code.Bge_Un_S: { var b = Pop(stack); var a = Pop(stack); if (CmpNum(a, b) >= 0) { ip = idx[(Instruction)instr.Operand]; jumped = true; } break; }
                    case Code.Ble:
                    case Code.Ble_S:
                    case Code.Ble_Un:
                    case Code.Ble_Un_S: { var b = Pop(stack); var a = Pop(stack); if (CmpNum(a, b) <= 0) { ip = idx[(Instruction)instr.Operand]; jumped = true; } break; }

                    case Code.Switch:
                        {
                            int v = I4(Pop(stack));
                            var targets = (IList<Instruction>)instr.Operand;
                            if (v >= 0 && v < targets.Count) { ip = idx[targets[v]]; jumped = true; }
                            break;
                        }

                    case Code.Ret:
                        if (method.ReturnType.FullName == "System.Void") return null;
                        return stack.Count > 0 ? Pop(stack) : null;

                    case Code.Newarr:
                        {
                            var et = ElemType((ITypeDefOrRef)instr.Operand);
                            int n = I4(Pop(stack));
                            stack.Push(Array.CreateInstance(et, n));
                            break;
                        }
                    case Code.Ldlen: stack.Push(((Array)Pop(stack)).Length); break;
                    case Code.Ldelema:
                        {
                            int i = I4(Pop(stack)); var a = (Array)Pop(stack); stack.Push(new Ptr(a, i));
                            break;
                        }
                    case Code.Ldelem_I1: case Code.Ldelem_U1: case Code.Ldelem_I2: case Code.Ldelem_U2:
                    case Code.Ldelem_I4: case Code.Ldelem_U4: case Code.Ldelem_I8: case Code.Ldelem_I:
                    case Code.Ldelem_R4: case Code.Ldelem_R8: case Code.Ldelem_Ref:
                        {
                            int i = I4(Pop(stack)); var a = (Array)Pop(stack); stack.Push(a.GetValue(i));
                            break;
                        }
                    case Code.Stelem_I1: case Code.Stelem_I2: case Code.Stelem_I4: case Code.Stelem_I8:
                    case Code.Stelem_I: case Code.Stelem_R4: case Code.Stelem_R8: case Code.Stelem_Ref:
                        {
                            var v = Pop(stack); int i = I4(Pop(stack)); var a = (Array)Pop(stack);
                            a.SetValue(Conv(v, a.GetType().GetElementType()), i);
                            break;
                        }

                    case Code.Ldind_I1: case Code.Ldind_U1: case Code.Ldind_I2: case Code.Ldind_U2:
                    case Code.Ldind_I4: case Code.Ldind_U4: case Code.Ldind_I8: case Code.Ldind_I:
                    case Code.Ldind_R4: case Code.Ldind_R8: case Code.Ldind_Ref:
                        stack.Push(IndRead(code, Pop(stack)));
                        break;
                    case Code.Stind_I1: case Code.Stind_I2: case Code.Stind_I4: case Code.Stind_I8:
                    case Code.Stind_R4: case Code.Stind_R8: case Code.Stind_Ref: case Code.Stind_I:
                        { var v = Pop(stack); IndWrite(code, Pop(stack), v); break; }
                    case Code.Cpblk:
                        { var size = I4(Pop(stack)); var src = Pop(stack); var dst = Pop(stack); CopyBlock(dst, src, size); break; }
                    case Code.Initblk:
                        { var size = I4(Pop(stack)); var val = I4(Pop(stack)); InitBlock(Pop(stack), val, size); break; }

                    case Code.Ldsfld:
                        stack.Push(LoadStatic((IField)instr.Operand));
                        break;
                    case Code.Ldsflda:
                        stack.Push(AddrOf((IField)instr.Operand));
                        break;
                    case Code.Stsfld:
                        _statics[(IField)instr.Operand] = Pop(stack);
                        break;

                    case Code.Ldtoken: stack.Push(instr.Operand); break;
                    case Code.Sizeof: stack.Push(SizeOf((ITypeDefOrRef)instr.Operand)); break;

                    case Code.Box:
                    case Code.Castclass:
                    case Code.Isinst:
                        break;
                    case Code.Unbox:
                    case Code.Unbox_Any:
                        break;

                    case Code.Newobj:
                        NewObj((IMethod)instr.Operand, stack);
                        break;

                    case Code.Call:
                    case Code.Callvirt:
                        InvokeCall((IMethod)instr.Operand, stack);
                        break;

                    default:
                        throw new EmuUnsupported("opcode " + code);
                }

                if (!jumped) ip++;
            }
            return null;
        }

        private void InvokeCall(IMethod m, Stack<object> stack)
        {
            var target = m.ResolveMethodDef();
            int pc = m.MethodSig != null ? m.MethodSig.Params.Count : 0;
            bool hasThis = m.MethodSig != null && m.MethodSig.HasThis;

            if (target != null && target.HasBody)
            {
                var a = new object[pc + (hasThis ? 1 : 0)];
                for (int i = pc - 1; i >= 0; i--) a[i + (hasThis ? 1 : 0)] = Pop(stack);
                if (hasThis) a[0] = Pop(stack);
                object r = Execute(target, a);
                if (target.ReturnType.FullName != "System.Void") stack.Push(r);
                return;
            }

            var args = new object[pc + (hasThis ? 1 : 0)];
            for (int i = pc - 1; i >= 0; i--) args[i + (hasThis ? 1 : 0)] = Pop(stack);
            if (hasThis) args[0] = Pop(stack);
            object res = Bcl(m, args, out bool isVoid);
            if (!isVoid) stack.Push(res);
        }

        private void NewObj(IMethod m, Stack<object> stack)
        {
            var target = m.ResolveMethodDef();
            int pc = m.MethodSig != null ? m.MethodSig.Params.Count : 0;
            var args = new object[pc];
            for (int i = pc - 1; i >= 0; i--) args[i] = Pop(stack);

            string dt = m.DeclaringType?.FullName ?? "";
            if (dt == "System.String")
            {
                if (pc == 1 && args[0] is char[] ca) stack.Push(new string(ca));
                else if (pc == 3 && args[0] is char[] c3) stack.Push(new string(c3, I4(args[1]), I4(args[2])));
                else if (pc == 1 && args[0] is char c) stack.Push(c.ToString());
                else if (pc == 2 && args[0] is char cc) stack.Push(new string(cc, I4(args[1])));
                else throw new EmuUnsupported("String ctor");
                return;
            }
            if (dt == "System.Exception")
            {
                stack.Push(new Exception(pc == 1 ? args[0] as string : null));
                return;
            }
            if (dt == "System.Object")
            {
                stack.Push(new object());
                return;
            }
            if (target != null && target.HasBody)
            {
                var instance = new object();
                var a = new object[pc + 1];
                a[0] = instance;
                Array.Copy(args, 0, a, 1, pc);
                Execute(target, a);
                stack.Push(instance);
                return;
            }
            throw new EmuUnsupported("newobj " + dt);
        }

        private object LoadStatic(IField field)
        {
            if (field.DeclaringType?.FullName == "System.String" && field.Name == "Empty") return "";
            if (field.DeclaringType?.FullName == "System.Int32" && field.Name == "MaxValue") return int.MaxValue;
            if (field.DeclaringType?.FullName == "System.Int32" && field.Name == "MinValue") return int.MinValue;
            if (_statics.TryGetValue(field, out var v)) return v;
            if (field is FieldDef fd && fd.HasFieldRVA && fd.InitialValue != null)
                return fd.InitialValue;
            return null;
        }

        private object AddrOf(IField field)
        {
            var v = LoadStatic(field);
            if (v is byte[] ba) return new Ptr(ba, 0);
            if (v is Array ar) return new Ptr(ar, 0);
            return new Ptr(new object[] { v }, 0);
        }

        private static object Materialize(FieldDef fd)
        {
            var t = ElemType(fd.FieldType.ToTypeDefOrRef());
            var arr = Array.CreateInstance(t, fd.InitialValue.Length / ElemSize(t));
            Buffer.BlockCopy(fd.InitialValue, 0, arr, 0, fd.InitialValue.Length);
            return arr;
        }

        private object Bcl(IMethod m, object[] a, out bool isVoid)
        {
            isVoid = false;
            string dt = m.DeclaringType?.FullName ?? "";
            string name = m.Name;
            try
            {
                switch (dt)
                {
                    case "System.Math":
                        switch (name)
                        {
                            case "Abs": return Abs(a[0]);
                            case "Min": return IsF(a[0]) || IsF(a[1]) ? Math.Min(R8(a[0]), R8(a[1])) : (IsL(a[0]) || IsL(a[1]) ? Math.Min(I8(a[0]), I8(a[1])) : Math.Min(I4(a[0]), I4(a[1])));
                            case "Max": return IsF(a[0]) || IsF(a[1]) ? Math.Max(R8(a[0]), R8(a[1])) : (IsL(a[0]) || IsL(a[1]) ? Math.Max(I8(a[0]), I8(a[1])) : Math.Max(I4(a[0]), I4(a[1])));
                            case "Floor": return Math.Floor(R8(a[0]));
                            case "Ceiling": return Math.Ceiling(R8(a[0]));
                            case "Round": return Math.Round(R8(a[0]));
                            case "Sqrt": return Math.Sqrt(R8(a[0]));
                            case "Log": return Math.Log(R8(a[0]));
                            case "Log10": return Math.Log10(R8(a[0]));
                            case "Pow": return Math.Pow(R8(a[0]), R8(a[1]));
                            case "Sin": return Math.Sin(R8(a[0]));
                            case "Cos": return Math.Cos(R8(a[0]));
                            case "Tan": return Math.Tan(R8(a[0]));
                        }
                        break;
                    case "System.Convert":
                        switch (name)
                        {
                            case "ToInt32": return IsF(a[0]) ? (int)R8(a[0]) : I4(a[0]);
                            case "ToInt64": return I8(a[0]);
                            case "ToByte": return (byte)I4(a[0]);
                            case "ToChar": return (char)I4(a[0]);
                            case "ToString": return a[0]?.ToString();
                            case "FromBase64String": return Convert.FromBase64String((string)a[0]);
                            case "ToBase64String": return Convert.ToBase64String((byte[])a[0]);
                        }
                        break;
                    case "System.BitConverter":
                        switch (name)
                        {
                            case "ToInt32": return BitConverter.ToInt32((byte[])a[0], I4(a[1]));
                            case "ToInt64": return BitConverter.ToInt64((byte[])a[0], I4(a[1]));
                            case "ToString": return BitConverter.ToString((byte[])a[0]);
                        }
                        break;
                    case "System.String":
                        switch (name)
                        {
                            case "Concat": return string.Concat(a.CastStringSafe());
                            case "Join": return string.Join(a[0] as string, (string[])a[1]);
                            case "get_Length": return ((string)a[0]).Length;
                            case "get_Chars": return (int)((string)a[0])[I4(a[1])];
                            case "ToCharArray": return ((string)a[0]).ToCharArray();
                            case "Substring": return a.Length == 2 ? ((string)a[0]).Substring(I4(a[1])) : ((string)a[0]).Substring(I4(a[1]), I4(a[2]));
                            case "IsNullOrEmpty": return string.IsNullOrEmpty(a[0] as string);
                            case "IsNullOrWhiteSpace": return string.IsNullOrWhiteSpace(a[0] as string);
                            case "Intern": return string.Intern(a[0] as string);
                            case "op_Equality": return (string)a[0] == (string)a[1];
                            case "op_Inequality": return (string)a[0] != (string)a[1];
                            case "GetHashCode": return ((string)a[0]).GetHashCode();
                            case "op_Addition": return (string)a[0] + (string)a[1];
                            case "ToString": return a[0]?.ToString();
                        }
                        break;
                    case "System.Reflection.Assembly":
                        if (name.StartsWith("Get") && name.EndsWith("Assembly")) return Marker;
                        if (name == "op_Equality") return ReferenceEquals(a[0], a[1]);
                        if (name == "op_Inequality") return !ReferenceEquals(a[0], a[1]);
                        if (name == "GetName") return Marker;
                        if (name == "get_FullName") return "Assembly";
                        if (name == "get_Location") return "Assembly.dll";
                        if (name == "GetManifestResourceStream") return ResStream(a[0], a[1] as string);
                        break;
                    case "System.Type":
                        if (name == "GetTypeFromHandle") return Marker;
                        if (name == "op_Equality") return ReferenceEquals(a[0], a[1]);
                        if (name == "op_Inequality") return !ReferenceEquals(a[0], a[1]);
                        if (name == "get_FullName") return "Type";
                        break;
                    case "System.Object":
                        if (name == "GetType") return Marker;
                        if (name == "ToString") return a[0]?.ToString();
                        if (name == "op_Equality") return ReferenceEquals(a[0], a[1]);
                        if (name == "op_Inequality") return !ReferenceEquals(a[0], a[1]);
                        if (name == "GetHashCode") return a[0]?.GetHashCode() ?? 0;
                        if (name == ".ctor") { isVoid = true; return null; }
                        break;
                    case "System.IO.Stream":
                        switch (name)
                        {
                            case "get_Length": return ((System.IO.MemoryStream)a[0]).Length;
                            case "get_Position": return ((System.IO.MemoryStream)a[0]).Position;
                            case "set_Position": ((System.IO.MemoryStream)a[0]).Position = I8(a[1]); isVoid = true; return null;
                            case "ReadByte": return ((System.IO.MemoryStream)a[0]).ReadByte();
                            case "Read": { var ms = (System.IO.MemoryStream)a[0]; int r = ms.Read((byte[])a[1], I4(a[2]), I4(a[3])); return r; }
                            case "Seek": return ((System.IO.MemoryStream)a[0]).Seek(I8(a[1]), (System.IO.SeekOrigin)I4(a[2]));
                        }
                        break;
                    case "System.IO.MemoryStream":
                        if (name == "GetBuffer") return ((System.IO.MemoryStream)a[0]).GetBuffer();
                        break;
                    case "System.IDisposable":
                        if (name == "Dispose") { isVoid = true; return null; }
                        break;
                    case "System.Char":
                        switch (name)
                        {
                            case "ToString": return ((char)I4(a[0])).ToString();
                            case "ToUpper": return char.ToUpper((char)I4(a[0]));
                            case "ToLower": return char.ToLower((char)I4(a[0]));
                        }
                        break;
                    case "System.Array":
                        switch (name)
                        {
                            case "get_Length": return ((Array)a[0]).Length;
                            case "Copy":
                                if (a.Length == 3) Array.Copy((Array)a[0], (Array)a[1], I4(a[2]));
                                else Array.Copy((Array)a[0], I4(a[1]), (Array)a[2], I4(a[3]), I4(a[4]));
                                isVoid = true; return null;
                            case "Reverse": Array.Reverse((Array)a[0]); isVoid = true; return null;
                        }
                        break;
                    case "System.Buffer":
                        if (name == "BlockCopy") { Buffer.BlockCopy((Array)a[0], I4(a[1]), (Array)a[2], I4(a[3]), I4(a[4])); isVoid = true; return null; }
                        break;
                    case "System.Text.Encoding":
                        if (name == "get_UTF8") return Encoding.UTF8;
                        if (name == "get_ASCII") return Encoding.ASCII;
                        if (name == "get_Unicode") return Encoding.Unicode;
                        if (name == "GetString") return ((Encoding)a[0]).GetString((byte[])a[1]);
                        if (name == "GetBytes") return ((Encoding)a[0]).GetBytes((string)a[1]);
                        break;
                    case "System.Runtime.CompilerServices.RuntimeHelpers":
                        if (name == "InitializeArray")
                        {
                            var arr = (Array)a[0];
                            if (a[1] is FieldDef fd && fd.InitialValue != null)
                                Buffer.BlockCopy(fd.InitialValue, 0, arr, 0, Math.Min(fd.InitialValue.Length, Buffer.ByteLength(arr)));
                            isVoid = true; return null;
                        }
                        break;
                }
            }
            catch (Exception e)
            {
                throw new EmuUnsupported("bcl " + dt + "::" + name + " -> " + e.Message);
            }
            throw new EmuUnsupported("bcl " + dt + "::" + name);
        }

        private static object Abs(object o) => IsF(o) ? (object)Math.Abs(R8(o)) : IsL(o) ? (object)Math.Abs(I8(o)) : Math.Abs(I4(o));

        private static Type ElemType(ITypeDefOrRef t)
        {
            switch (t?.FullName)
            {
                case "System.Byte": return typeof(byte);
                case "System.SByte": return typeof(sbyte);
                case "System.Int16": return typeof(short);
                case "System.UInt16": return typeof(ushort);
                case "System.Int32": return typeof(int);
                case "System.UInt32": return typeof(uint);
                case "System.Int64": return typeof(long);
                case "System.UInt64": return typeof(ulong);
                case "System.Single": return typeof(float);
                case "System.Double": return typeof(double);
                case "System.Char": return typeof(char);
                case "System.Boolean": return typeof(bool);
                case "System.String": return typeof(string);
                default: return typeof(object);
            }
        }

        private static int SizeOf(ITypeDefOrRef t)
        {
            switch (t?.FullName)
            {
                case "System.Byte": case "System.SByte": case "System.Boolean": return 1;
                case "System.Int16": case "System.UInt16": case "System.Char": return 2;
                case "System.Int32": case "System.UInt32": case "System.Single": return 4;
                case "System.Int64": case "System.UInt64": case "System.Double": return 8;
                default: return 8;
            }
        }

        private static int ElemSize(Type t) => t == typeof(byte) || t == typeof(sbyte) || t == typeof(bool) ? 1 : t == typeof(short) || t == typeof(ushort) || t == typeof(char) ? 2 : t == typeof(int) || t == typeof(uint) || t == typeof(float) ? 4 : 8;

        private static object Conv(object v, Type t)
        {
            if (v == null) return null;
            if (t == typeof(object) || t.IsInstanceOfType(v)) return v;
            try { return Convert.ChangeType(v, t); } catch { return v; }
        }

        private sealed class Ptr
        {
            public readonly Array Arr;
            public readonly int Off;
            public Ptr(Array a, int o) { Arr = a; Off = o; }
        }

        private static int ArgIndex(Instruction i, MethodDef m)
        {
            int th = m.HasThis ? 1 : 0;
            return i.Operand switch
            {
                Parameter p => p.Index + th,
                int n => n,
                byte b => b,
                sbyte s => s,
                uint u => (int)u,
                _ => 0
            };
        }

        private static object AddVals(object a, object b)
        {
            if (a is Ptr pa && b is not Ptr) return new Ptr(pa.Arr, pa.Off + I4(b));
            if (b is Ptr pb && a is not Ptr) return new Ptr(pb.Arr, pb.Off + I4(a));
            if (IsF(a) || IsF(b)) return R8(a) + R8(b);
            if (IsL(a) || IsL(b)) return I8(a) + I8(b);
            return I4(a) + I4(b);
        }

        private static object SubVals(object a, object b)
        {
            if (a is Ptr ps && b is not Ptr) return new Ptr(ps.Arr, ps.Off - I4(b));
            if (a is Ptr p1 && b is Ptr p2) return p1.Off - p2.Off;
            if (IsF(a) || IsF(b)) return R8(a) - R8(b);
            if (IsL(a) || IsL(b)) return I8(a) - I8(b);
            return I4(a) - I4(b);
        }

        private static object IndRead(Code c, object p)
        {
            if (p is not Ptr ptr) throw new EmuUnsupported("ldind on non-pointer");
            var arr = ptr.Arr;
            int off = ptr.Off;
            if (arr is byte[] b)
            {
                if (off < 0 || off >= b.Length) throw new EmuUnsupported("ldind out of range");
                switch (c)
                {
                    case Code.Ldind_U1: return b[off];
                    case Code.Ldind_I1: return (sbyte)b[off];
                    case Code.Ldind_U2: return BitConverter.ToUInt16(b, off);
                    case Code.Ldind_I2: return BitConverter.ToInt16(b, off);
                    case Code.Ldind_U4: return BitConverter.ToUInt32(b, off);
                    case Code.Ldind_I4: return BitConverter.ToInt32(b, off);
                    case Code.Ldind_I8: return BitConverter.ToInt64(b, off);
                    case Code.Ldind_R4: return BitConverter.ToSingle(b, off);
                    case Code.Ldind_R8: return BitConverter.ToDouble(b, off);
                    case Code.Ldind_I: return BitConverter.ToInt32(b, off);
                    case Code.Ldind_Ref: return b[off];
                }
            }
            var raw = arr.GetValue(off);
            switch (c)
            {
                case Code.Ldind_U1: return Convert.ToInt32(raw);
                case Code.Ldind_I1: return (sbyte)Convert.ToInt32(raw);
                case Code.Ldind_I2: return Convert.ToInt32(raw);
                case Code.Ldind_I4: return Convert.ToInt32(raw);
                case Code.Ldind_I8: return Convert.ToInt64(raw);
                case Code.Ldind_R4: return Convert.ToSingle(raw);
                case Code.Ldind_R8: return Convert.ToDouble(raw);
                default: return raw;
            }
        }

        private static void IndWrite(Code c, object p, object v)
        {
            if (p is not Ptr ptr) throw new EmuUnsupported("stind on non-pointer");
            var arr = ptr.Arr;
            int off = ptr.Off;
            if (arr is byte[] b)
            {
                if (off < 0 || off >= b.Length) throw new EmuUnsupported("stind out of range");
                switch (c)
                {
                    case Code.Stind_I1: b[off] = (byte)I4(v); return;
                    case Code.Stind_I2: BitConverter.GetBytes((short)I4(v)).CopyTo(b, off); return;
                    case Code.Stind_I4: BitConverter.GetBytes(I4(v)).CopyTo(b, off); return;
                    case Code.Stind_I8: BitConverter.GetBytes(I8(v)).CopyTo(b, off); return;
                    case Code.Stind_R4: BitConverter.GetBytes((float)R8(v)).CopyTo(b, off); return;
                    case Code.Stind_R8: BitConverter.GetBytes(R8(v)).CopyTo(b, off); return;
                    default: arr.SetValue(v, off); return;
                }
            }
            arr.SetValue(Conv(v, arr.GetType().GetElementType()), off);
        }

        private static void CopyBlock(object dstO, object srcO, int size)
        {
            if (dstO is not Ptr dst) throw new EmuUnsupported("cpblk dst non-pointer");
            if (srcO is not Ptr src) throw new EmuUnsupported("cpblk src non-pointer");
            if (dst.Arr is byte[] db && src.Arr is byte[] sb)
            {
                Buffer.BlockCopy(sb, src.Off, db, dst.Off, size);
                return;
            }
            for (int k = 0; k < size; k++)
                dst.Arr.SetValue(src.Arr.GetValue(src.Off + k), dst.Off + k);
        }

        private static void InitBlock(object p, int value, int size)
        {
            if (p is not Ptr ptr) throw new EmuUnsupported("initblk non-pointer");
            if (ptr.Arr is byte[] b)
            {
                for (int k = 0; k < size; k++)
                {
                    int i = ptr.Off + k;
                    if (i >= 0 && i < b.Length) b[i] = (byte)value;
                }
            }
        }

        private static object Arg(object[] args, int i)
        {
            if (i < 0 || i >= args.Length) throw new EmuUnsupported("arg out of range " + i);
            return args[i];
        }

        private static object Pop(Stack<object> s) => s.Count > 0 ? s.Pop() : throw new EmuUnsupported("stack underflow");

        private static void Bin(Stack<object> s, Func<object, object, object> f) { var b = Pop(s); var a = Pop(s); s.Push(f(a, b)); }
        private static void Un(Stack<object> s, Func<object, object> f) => s.Push(f(Pop(s)));
        private static void Cmp(Stack<object> s, Func<object, object, bool> f) { var b = Pop(s); var a = Pop(s); s.Push(f(a, b) ? 1 : 0); }

        private static bool IsF(object o) => o is double || o is float;
        private static bool IsL(object o) => o is long || o is ulong;
        private static int I4(object o) => o == null ? 0 : Convert.ToInt32(o);
        private static long I8(object o) => o == null ? 0 : Convert.ToInt64(o);
        private static double R8(object o) => o == null ? 0 : Convert.ToDouble(o);

        private static bool Truthy(object o) => o switch { null => false, bool b => b, int i => i != 0, long l => l != 0, _ => true };

        private static bool EqInt(object a, object b)
        {
            if (a == null || b == null) return a == null && b == null;
            if (a is string || b is string) return a?.ToString() == b?.ToString();
            if (IsF(a) || IsF(b)) return R8(a) == R8(b);
            if (IsL(a) || IsL(b)) return I8(a) == I8(b);
            return I4(a) == I4(b);
        }

        private static int CmpNum(object a, object b)
        {
            if (IsF(a) || IsF(b)) return R8(a).CompareTo(R8(b));
            if (IsL(a) || IsL(b)) return I8(a).CompareTo(I8(b));
            return I4(a).CompareTo(I4(b));
        }
    }

    internal static class EmuExt
    {
        public static object[] CastStringSafe(this object[] a)
        {
            var r = new object[a.Length];
            for (int i = 0; i < a.Length; i++) r[i] = a[i]?.ToString();
            return r;
        }
    }
}
