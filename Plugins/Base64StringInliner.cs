using System;
using System.Collections.Generic;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using Deobfuscator.Core;

namespace Deobfuscator.Plugins
{
    public sealed class Base64StringInliner
    {
        private enum WrapperKind
        {
            Bytes,
            Text
        }

        private readonly ModuleDef _mod;
        private readonly Emulator _emu;
        private readonly Dictionary<MethodDef, WrapperKind> _base64Wrappers = new();
        private readonly Dictionary<MethodDef, IMethod> _encodingProxies = new();
        private readonly Dictionary<MethodDef, Encoding> _encodingCache = new();

        public int Wrappers;
        public int EncodingProxies;
        public int Inlined;
        public int EncodingSites;
        public int Normalized;
        public int Failures;
        public readonly List<string> Notes = new();

        public Base64StringInliner(ModuleDef mod)
        {
            _mod = mod;
            _emu = new Emulator(mod);
            _emu.SeedStatics();
        }

        public void Run()
        {
            DetectEncodingProxies();
            RedirectEncodingProxies();
            DetectBase64Wrappers();
            NormalizeWrapperBodies();
            FoldCallSites();
        }

        private void DetectEncodingProxies()
        {
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var m in IlUtil.AllMethods(_mod))
                {
                    if (_encodingProxies.ContainsKey(m)) continue;
                    if (!m.HasBody || m.HasThis || !m.IsStatic) continue;
                    if (m.Parameters.Count != 0) continue;
                    if (m.ReturnType.FullName != "System.Text.Encoding") continue;
                    if (m.Body.Instructions.Count > 16) continue;

                    var direct = ResolveEncodingGetterBody(m);
                    if (direct == null) continue;

                    _encodingProxies[m] = direct;
                    EncodingProxies++;
                    changed = true;
                }
            }
        }

        private IMethod ResolveEncodingGetterBody(MethodDef m)
        {
            var ins = m.Body.Instructions;
            IMethod onlyCall = null;
            bool sawRet = false;

            foreach (var instr in ins)
            {
                switch (instr.OpCode.Code)
                {
                    case Code.Nop:
                        break;
                    case Code.Ret:
                        sawRet = true;
                        break;
                    case Code.Call:
                        if (sawRet || onlyCall != null) return null;
                        onlyCall = instr.Operand as IMethod;
                        if (onlyCall == null) return null;
                        break;
                    default:
                        return null;
                }
            }

            if (onlyCall == null) return null;

            if (onlyCall.DeclaringType?.FullName == "System.Text.Encoding" &&
                onlyCall.Name.String.StartsWith("get_", StringComparison.Ordinal))
                return onlyCall;

            if (onlyCall.ResolveMethodDef() is MethodDef callee &&
                _encodingProxies.TryGetValue(callee, out var direct))
                return direct;

            return null;
        }

        private void RedirectEncodingProxies()
        {
            if (_encodingProxies.Count == 0) return;

            foreach (var m in IlUtil.AllMethods(_mod))
            {
                if (!m.HasBody) continue;
                foreach (var instr in m.Body.Instructions)
                {
                    if (instr.OpCode.Code != Code.Call && instr.OpCode.Code != Code.Callvirt) continue;
                    if (instr.Operand is not MethodDef md) continue;
                    if (!_encodingProxies.TryGetValue(md, out var direct)) continue;

                    instr.OpCode = OpCodes.Call;
                    instr.Operand = direct;
                    EncodingSites++;
                }
            }
        }

        private void DetectBase64Wrappers()
        {
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var m in IlUtil.AllMethods(_mod))
                {
                    if (_base64Wrappers.ContainsKey(m)) continue;
                    if (!m.HasBody || m.HasThis || !m.IsStatic) continue;
                    if (m.Parameters.Count != 1) continue;
                    if (m.Parameters[0].Type.FullName != "System.String") continue;

                    WrapperKind kind;
                    if (m.ReturnType.FullName == "System.Byte[]") kind = WrapperKind.Bytes;
                    else if (m.ReturnType.FullName == "System.String") kind = WrapperKind.Text;
                    else continue;

                    var ins = m.Body.Instructions;
                    if (ins.Count > 200) continue;

                    bool hitsBase64 = false;
                    foreach (var instr in ins)
                    {
                        if (instr.OpCode.Code != Code.Call && instr.OpCode.Code != Code.Callvirt) continue;
                        if (instr.Operand is not IMethod im) continue;

                        if (IsConvertFromBase64(im))
                        {
                            hitsBase64 = true;
                            break;
                        }

                        if (im.ResolveMethodDef() is MethodDef callee &&
                            _base64Wrappers.ContainsKey(callee))
                        {
                            hitsBase64 = true;
                            break;
                        }
                    }

                    if (!hitsBase64) continue;

                    _base64Wrappers[m] = kind;
                    Wrappers++;
                    changed = true;
                }
            }
        }

        private static bool IsConvertFromBase64(IMethod im)
        {
            return im.Name == "FromBase64String" &&
                   im.DeclaringType?.FullName == "System.Convert" &&
                   (im.MethodSig?.Params.Count ?? 1) == 1;
        }

        private void NormalizeWrapperBodies()
        {
            IMethod fromBase64 = null;
            foreach (var m in IlUtil.AllMethods(_mod))
            {
                if (!m.HasBody) continue;
                foreach (var instr in m.Body.Instructions)
                {
                    if ((instr.OpCode.Code == Code.Call || instr.OpCode.Code == Code.Callvirt) &&
                        instr.Operand is IMethod im && IsConvertFromBase64(im))
                    {
                        fromBase64 = im;
                        break;
                    }
                }
                if (fromBase64 != null) break;
            }
            if (fromBase64 == null) return;

            foreach (var entry in _base64Wrappers)
            {
                if (entry.Value != WrapperKind.Bytes) continue;

                var m = entry.Key;
                if (!m.HasBody) continue;

                var all = m.Body.Instructions;
                var real = new List<Instruction>(all.Count);
                foreach (var instr in all)
                    if (instr.OpCode.Code != Code.Nop)
                        real.Add(instr);

                if (real.Count != 3) continue;
                if (!IsLdarg(real[0], 0)) continue;
                if (real[1].OpCode.Code != Code.Call) continue;
                if (real[1].Operand is not IMethod called) continue;
                if (real[2].OpCode.Code != Code.Ret) continue;
                if (IsConvertFromBase64(called)) continue;

                if (called.ResolveMethodDef() is not MethodDef callee ||
                    !_base64Wrappers.TryGetValue(callee, out var kind) ||
                    kind != WrapperKind.Bytes)
                    continue;

                var body = m.Body;
                body.Instructions.Clear();
                body.ExceptionHandlers.Clear();
                body.Variables.Clear();
                body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
                body.Instructions.Add(Instruction.Create(OpCodes.Call, fromBase64));
                body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                body.MaxStack = 2;
                body.KeepOldMaxStack = true;
                body.InitLocals = false;
                Normalized++;
            }
        }

        private void FoldCallSites()
        {
            foreach (var m in IlUtil.AllMethods(_mod))
            {
                if (!m.HasBody) continue;
                var ins = m.Body.Instructions;
                var targets = IlUtil.CollectTargets(m);
                var localStrings = new Dictionary<int, (string Value, int LdstrIndex)>();
                var localEncodings = new Dictionary<int, Encoding>();

                for (int i = 0; i < ins.Count; i++)
                {
                    bool isTarget = targets.Contains(ins[i]);
                    if (isTarget)
                    {
                        localStrings.Clear();
                        localEncodings.Clear();
                    }

                    int local = LocalOf(ins[i]);
                    if (local >= 0)
                    {
                        if (IsStoreLocal(ins[i]))
                        {
                            int p = PrevNonNop(ins, i);
                            if (!isTarget && p >= 0 && ins[p].OpCode.Code == Code.Ldstr && ins[p].Operand is string stored)
                                localStrings[local] = (stored, p);
                            else
                                localStrings.Remove(local);

                            if (!isTarget && p >= 0 &&
                                (ins[p].OpCode.Code == Code.Call || ins[p].OpCode.Code == Code.Callvirt) &&
                                ins[p].Operand is IMethod pm &&
                                pm.MethodSig?.RetType.FullName == "System.Text.Encoding")
                                localEncodings[local] = ResolveEncoding(ins[p], localEncodings);
                            else
                                localEncodings.Remove(local);
                        }
                        else if (!IsLoadLocal(ins[i]))
                        {
                            localStrings.Remove(local);
                            localEncodings.Remove(local);
                        }
                    }

                    var call = ins[i];
                    if (call.OpCode.Code != Code.Call) continue;
                    if (call.Operand is not IMethod callee) continue;

                    bool isDirect = IsConvertFromBase64(callee);
                    MethodDef wrapper = null;
                    WrapperKind kind = WrapperKind.Bytes;

                    if (!isDirect)
                    {
                        wrapper = callee.ResolveMethodDef();
                        if (wrapper == null || !_base64Wrappers.TryGetValue(wrapper, out kind))
                            continue;
                    }

                    if (!TryStringArg(ins, i, localStrings, out int argIdx, out int pushIdx, out string b64))
                        continue;

                    if (!isDirect && kind == WrapperKind.Text)
                    {
                        if (!_emu.TryInvoke(wrapper, new object[] { b64 }, out var r) || r is not string s)
                        {
                            Fail();
                            continue;
                        }

                        Nop(ins[argIdx]);
                        call.OpCode = OpCodes.Ldstr;
                        call.Operand = s;
                        Inlined++;
                        continue;
                    }

                    int gsIdx = NextNonNop(ins, i);
                    if (gsIdx < 0) continue;
                    var getString = ins[gsIdx];
                    if (getString.OpCode.Code != Code.Call && getString.OpCode.Code != Code.Callvirt) continue;
                    if (getString.Operand is not IMethod gm) continue;
                    if (gm.Name != "GetString" || gm.DeclaringType?.FullName != "System.Text.Encoding") continue;
                    if ((gm.MethodSig?.Params.Count ?? 0) != 1) continue;

                    int encIdx = PrevNonNop(ins, argIdx);
                    if (encIdx < 0 || !IsEncodingPush(ins[encIdx]))
                    {
                        encIdx = PrevNonNop(ins, pushIdx);
                        if (encIdx < 0 || !IsEncodingPush(ins[encIdx])) continue;
                    }

                    byte[] bytes;
                    if (isDirect)
                    {
                        try { bytes = Convert.FromBase64String(b64); }
                        catch { Fail(); continue; }
                    }
                    else if (!_emu.TryInvoke(wrapper, new object[] { b64 }, out var br) || br is not byte[] b)
                    {
                        Fail();
                        continue;
                    }
                    else
                    {
                        bytes = b;
                    }

                    Encoding enc;
                    try { enc = ResolveEncoding(ins[encIdx], localEncodings) ?? Encoding.UTF8; }
                    catch { Fail(); continue; }

                    string text;
                    try { text = enc.GetString(bytes); }
                    catch { Fail(); continue; }

                    Nop(ins[encIdx]);
                    Nop(ins[argIdx]);
                    Nop(call);
                    getString.OpCode = OpCodes.Ldstr;
                    getString.Operand = text;
                    Inlined++;
                }
            }
        }

        private Encoding ResolveEncoding(Instruction producer, Dictionary<int, Encoding> localEncodings = null)
        {
            if (producer.OpCode.Code != Code.Call && producer.OpCode.Code != Code.Callvirt)
            {
                if (localEncodings != null)
                {
                    int local = LocalOf(producer);
                    if (local >= 0 && localEncodings.TryGetValue(local, out var known))
                        return known;
                }
                return Encoding.UTF8;
            }

            if (producer.Operand is not IMethod im) return Encoding.UTF8;

            switch (im.Name.String)
            {
                case "get_UTF8": return Encoding.UTF8;
                case "get_ASCII": return Encoding.ASCII;
                case "get_Unicode": return Encoding.Unicode;
                case "get_BigEndianUnicode": return Encoding.BigEndianUnicode;
                case "get_UTF32": return Encoding.UTF32;
                case "get_Default": return Encoding.Default;
            }

            if (im.ResolveMethodDef() is MethodDef md && md.Parameters.Count == 0)
            {
                if (_encodingCache.TryGetValue(md, out var cached)) return cached;
                if (_emu.TryInvoke(md, Array.Empty<object>(), out var r) && r is Encoding enc)
                {
                    _encodingCache[md] = enc;
                    return enc;
                }
            }

            return Encoding.UTF8;
        }

        private static bool IsEncodingPush(Instruction instr)
        {
            switch (instr.OpCode.Code)
            {
                case Code.Ldsfld:
                case Code.Ldloc:
                case Code.Ldloc_S:
                case Code.Ldloc_0:
                case Code.Ldloc_1:
                case Code.Ldloc_2:
                case Code.Ldloc_3:
                case Code.Ldarg:
                case Code.Ldarg_S:
                case Code.Ldarg_0:
                case Code.Ldarg_1:
                case Code.Ldarg_2:
                case Code.Ldarg_3:
                    return true;

                case Code.Call:
                case Code.Callvirt:
                    return instr.Operand is IMethod im &&
                           (im.MethodSig?.Params.Count ?? 1) == 0 &&
                           (im.Name.String.StartsWith("get_", StringComparison.Ordinal) ||
                            im.DeclaringType?.FullName == "System.Text.Encoding" ||
                            im.MethodSig?.RetType.FullName == "System.Text.Encoding");

                default:
                    return false;
            }
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

        private static bool IsLoadLocal(Instruction i)
        {
            switch (i.OpCode.Code)
            {
                case Code.Ldloc:
                case Code.Ldloc_S:
                case Code.Ldloc_0:
                case Code.Ldloc_1:
                case Code.Ldloc_2:
                case Code.Ldloc_3:
                    return true;
                default: return false;
            }
        }

        private static bool IsStoreLocal(Instruction i)
        {
            switch (i.OpCode.Code)
            {
                case Code.Stloc:
                case Code.Stloc_S:
                case Code.Stloc_0:
                case Code.Stloc_1:
                case Code.Stloc_2:
                case Code.Stloc_3:
                    return true;
                default: return false;
            }
        }

        private static int LocalOf(Instruction i)
        {
            switch (i.OpCode.Code)
            {
                case Code.Ldloc_0:
                case Code.Stloc_0: return 0;
                case Code.Ldloc_1:
                case Code.Stloc_1: return 1;
                case Code.Ldloc_2:
                case Code.Stloc_2: return 2;
                case Code.Ldloc_3:
                case Code.Stloc_3: return 3;
                case Code.Ldloc:
                case Code.Ldloc_S:
                case Code.Stloc:
                case Code.Stloc_S:
                case Code.Ldloca:
                case Code.Ldloca_S: return i.Operand is Local l ? l.Index : -1;
                default: return -1;
            }
        }

        private static bool TryStringArg(IList<Instruction> ins, int callIdx,
            Dictionary<int, (string Value, int LdstrIndex)> localStrings,
            out int argIdx, out int pushIdx, out string value)
        {
            argIdx = PrevNonNop(ins, callIdx);
            pushIdx = argIdx;
            value = null;
            if (argIdx < 0) return false;

            var arg = ins[argIdx];
            if (arg.OpCode.Code == Code.Ldstr)
            {
                value = arg.Operand as string;
                return value != null;
            }

            if (IsLoadLocal(arg))
            {
                int local = LocalOf(arg);
                if (local >= 0 && localStrings.TryGetValue(local, out var known))
                {
                    value = known.Value;
                    pushIdx = known.LdstrIndex;
                    return true;
                }
            }

            return false;
        }

        private static int PrevNonNop(IList<Instruction> ins, int index)
        {
            for (int i = index - 1; i >= 0; i--)
                if (ins[i].OpCode.Code != Code.Nop)
                    return i;
            return -1;
        }

        private static int NextNonNop(IList<Instruction> ins, int index)
        {
            for (int i = index + 1; i < ins.Count; i++)
                if (ins[i].OpCode.Code != Code.Nop)
                    return i;
            return -1;
        }

        private static void Nop(Instruction i)
        {
            i.OpCode = OpCodes.Nop;
            i.Operand = null;
        }

        private void Fail()
        {
            Failures++;
        }
    }
}
