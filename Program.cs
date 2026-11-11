using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;
using Deobfuscator.Core;
using Deobfuscator.Plugins;
using Deobfuscator.Protections;

ConsoleUi.Init();
args = args.SkipWhile(a => a == "--").ToArray();

if (args.Length >= 3 && args[0] == "--dump")
{
    ConsoleUi.Banner();
    var sw = Stopwatch.StartNew();
    ConsoleUi.BeginTag("DUMP", "Inspecting module metadata");
    var dumpMod = ModuleDefMD.Load(args[1]);
    using (var w = File.CreateText(args[2]))
    {
        var moduleType = dumpMod.GlobalType;
        if (moduleType != null)
        {
            w.WriteLine("=== <Module> fields ===");
            foreach (var f in moduleType.Fields)
                w.WriteLine($"  {(f.HasFieldRVA ? "RVA" : "   ")} {f.FieldType.FullName} {f.Name} initlen={(f.InitialValue?.Length ?? 0)}");
            w.WriteLine("=== cctor init patterns ===");
            var cctor = moduleType.FindStaticConstructor();
            if (cctor != null && cctor.HasBody)
                foreach (var i in cctor.Body.Instructions)
                    if ((i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) && i.Operand is IMethod im)
                        w.WriteLine($"  {i.Offset:X4}: {im.DeclaringType?.FullName}::{im.Name}");
        }
        foreach (var m in dumpMod.GetTypes().SelectMany(t => t.Methods))
        {
            if (!m.HasBody || m.ReturnType.FullName != "System.String") continue;
            if (m.Parameters.Count < 1 || m.Parameters.Count > 4) continue;
            if (!m.Parameters.Any(p => p.Type.FullName == "System.Int32")) continue;
            w.WriteLine("=== " + m.FullName + "  params=" + string.Join(",", m.Parameters.Select(p => p.Type.FullName)));
            foreach (var i in m.Body.Instructions)
                w.WriteLine($"  {i.Offset:X4}: {i.OpCode.Name,-12} {i.Operand}");
            if (w.BaseStream.Length > 500000) { w.WriteLine("...truncated"); break; }
        }
    }
    ConsoleUi.EndStep(true, $"wrote {Path.GetFileName(args[2])}", sw.ElapsedMilliseconds);
    return;
}

if (args.Length >= 3 && args[0] == "--dumpstr")
{
    ConsoleUi.Banner();
    var sw = Stopwatch.StartNew();
    ConsoleUi.BeginTag("STR ", "Extracting string literals");
    var sm = ModuleDefMD.Load(args[1]);
    int dumped = 0;
    using (var w = File.CreateText(args[2]))
    {
        var set = new SortedSet<string>();
        foreach (var m in sm.GetTypes().SelectMany(t => t.Methods))
            if (m.HasBody)
                foreach (var i in m.Body.Instructions)
                    if (i.OpCode == OpCodes.Ldstr && i.Operand is string s && !string.IsNullOrWhiteSpace(s))
                        set.Add(s);
        dumped = set.Count;
        foreach (var s in set) w.WriteLine(s);
    }
    ConsoleUi.EndStep(true, $"{dumped} strings -> {Path.GetFileName(args[2])}", sw.ElapsedMilliseconds);
    return;
}

if (args.Length >= 3 && args[0] == "--b64")
{
    ConsoleUi.Banner();
    var sw = Stopwatch.StartNew();
    ConsoleUi.BeginTag("B64 ", "Running Base64 string inliner");
    var b64Mod = ModuleDefMD.Load(args[1]);
    var pass = new Base64StringInliner(b64Mod);
    pass.Run();
    var b64Opts = new ModuleWriterOptions(b64Mod)
    {
        MetadataOptions = { Flags = MetadataFlags.PreserveAll | MetadataFlags.KeepOldMaxStack },
        Logger = DummyLogger.NoThrowInstance,
    };
    b64Mod.Write(args[2], b64Opts);
    ConsoleUi.EndStep(pass.Failures == 0, $"{pass.Inlined} inlined ({pass.Wrappers} wrappers, {pass.EncodingProxies} enc proxies)", sw.ElapsedMilliseconds);
    ConsoleUi.SummaryBox("BASE64 INLINER SUMMARY", new[]
    {
        ("Wrappers Detected", pass.Wrappers.ToString(), ConsoleColor.White),
        ("Wrappers Normalized", pass.Normalized.ToString(), ConsoleColor.White),
        ("Encoding Proxies Removed", pass.EncodingProxies.ToString(), ConsoleColor.White),
        ("Strings Decrypted", pass.Inlined.ToString(), ConsoleColor.Green),
        ("Encoding Sites Rewritten", pass.EncodingSites.ToString(), ConsoleColor.White),
        ("Failures", pass.Failures.ToString(), pass.Failures == 0 ? ConsoleColor.Green : ConsoleColor.Red),
    });
    return;
}

if (args.Length < 2)
{
    ConsoleUi.PrintUsage();
    return;
}

var totalSw = Stopwatch.StartNew();
var stepSw = new Stopwatch();
var inputInfo = new FileInfo(args[0]);

ConsoleUi.Banner();
ConsoleUi.SectionHeader("TARGET");
ConsoleUi.InfoRow("Input", $"{Path.GetFileName(args[0])} ({(inputInfo.Exists ? ConsoleUi.FormatBytes(inputInfo.Length) : "?")})");
ConsoleUi.InfoRow("Output", Path.GetFileName(args[1]));

var mod = ModuleDefMD.Load(args[0]);
var devirt = new InxDevirtualizer(mod);
var resName = devirt.FindInitResourceName();
var resEntry = resName != null ? mod.Resources.Find(resName) as EmbeddedResource : null;
var resDisplay = resEntry != null
    ? $"\"{resName}\" ({ConsoleUi.FormatBytes(resEntry.Length)})"
    : (string.IsNullOrEmpty(resName) ? "resolved" : $"\"{resName}\"");
ConsoleUi.InfoRow("Assembly", mod.Assembly?.Name?.String ?? mod.Name.String);
ConsoleUi.InfoRow("Inx Store", resDisplay, ConsoleColor.Cyan);

if (mod.Assembly != null && mod.Name != mod.Assembly.Name)
{
    ConsoleUi.InfoRow("Module Fix", $"{mod.Name} -> {mod.Assembly.Name}", ConsoleColor.Yellow);
    mod.Name = mod.Assembly.Name;
}

Console.WriteLine();
ConsoleUi.SectionHeader("PIPELINE");

const int TotalSteps = 7;

stepSw.Restart();
ConsoleUi.BeginStep(1, TotalSteps, "Devirtualizing Inx methods");
devirt.Run();
ConsoleUi.EndStep(devirt.Failed == 0, $"{devirt.Devirtualized}/{devirt.Stubs} restored ({devirt.Failed} failed)", stepSw.ElapsedMilliseconds);
foreach (var e in devirt.Errors.Take(10))
    ConsoleUi.SubNote(e, ConsoleColor.Red);

stepSw.Restart();
ConsoleUi.BeginStep(2, TotalSteps, "Removing control-flow junk");
var junk = new JunkRemover(mod);
junk.Run();
int totalJunk = junk.Prefixes + junk.FallsThrough + junk.Nops + junk.DeadCode;
ConsoleUi.EndStep(true, $"{totalJunk} stripped (pref:{junk.Prefixes} br:{junk.FallsThrough} nop:{junk.Nops} dead:{junk.DeadCode})", stepSw.ElapsedMilliseconds);

stepSw.Restart();
ConsoleUi.BeginStep(3, TotalSteps, "Removing constant & fwd proxies");
var proxies = new ProxyRemover(mod);
proxies.Run();
int totalMainProxies = proxies.ConstantProxies + proxies.ForwardingProxies;
int totalMainSites = proxies.ConstantSites + proxies.ForwardingSites;
ConsoleUi.EndStep(true, $"{totalMainProxies} proxies ({totalMainSites} sites)", stepSw.ElapsedMilliseconds);

stepSw.Restart();
ConsoleUi.BeginStep(4, TotalSteps, "Decrypting static strings");
var strings = new StringDecryptor(mod);
strings.Run();
ConsoleUi.EndStep(strings.Failures == 0, $"{strings.Sites} decrypted ({strings.Decryptors} helpers, {strings.Failures} failed)", stepSw.ElapsedMilliseconds);
if (strings.FirstFailureMethod != null)
    ConsoleUi.SubNote($"first failure: {strings.FirstFailureMethod} :: {strings.FirstFailureReason}");
foreach (var kv in strings.FailureReasons.OrderByDescending(x => x.Value).Take(4))
    ConsoleUi.SubNote($"fail[{kv.Value}] {kv.Key}");

stepSw.Restart();
ConsoleUi.BeginStep(5, TotalSteps, "Inlining XOR string scramblers");
var xoring = new XoringStringInliner(mod);
xoring.Run();
ConsoleUi.EndStep(true, $"{xoring.Inlined} decrypted", stepSw.ElapsedMilliseconds);

stepSw.Restart();
ConsoleUi.BeginStep(6, TotalSteps, "Inlining Base64 string layers");
var b64 = new Base64StringInliner(mod);
b64.Run();
ConsoleUi.EndStep(b64.Failures == 0, $"{b64.Inlined} decrypted ({b64.Wrappers} wrappers, {b64.EncodingProxies} enc proxies)", stepSw.ElapsedMilliseconds);

stepSw.Restart();
ConsoleUi.BeginStep(7, TotalSteps, "Removing simple forwarding proxies");
var simpleProxies = new SimpleProxyRemover(mod);
simpleProxies.Run();
ConsoleUi.EndStep(true, $"{simpleProxies.Proxies} proxies ({simpleProxies.Sites} sites, {simpleProxies.Removed} removed)", stepSw.ElapsedMilliseconds);

stepSw.Restart();
ConsoleUi.BeginTag("SAVE", "Writing cleaned assembly");
WriteCleanedModule(mod, args[1]);
var outInfo = new FileInfo(args[1]);
ConsoleUi.EndStep(true, $"{Path.GetFileName(args[1])} ({ConsoleUi.FormatBytes(outInfo.Length)})", stepSw.ElapsedMilliseconds);

stepSw.Restart();
ConsoleUi.BeginTag("CHCK", "Verifying output integrity");
var check = ModuleDefMD.Load(args[1]);
int remaining = 0;
int bad = 0;
foreach (var t in check.GetTypes())
{
    foreach (var m in t.Methods)
    {
        if (!m.HasBody) continue;
        foreach (var i in m.Body.Instructions)
        {
            if (i.OpCode == OpCodes.Call && i.Operand is IMethod im && im.Name == "Execute")
                remaining++;
            bool needsOperand = i.OpCode.OperandType != OperandType.InlineNone;
            if (needsOperand && i.Operand == null && i.OpCode != OpCodes.Nop)
                bad++;
        }
    }
}
ConsoleUi.EndStep(remaining == 0 && bad == 0, $"Execute={remaining} missingOperands={bad}", stepSw.ElapsedMilliseconds);

totalSw.Stop();

int totalStringsDecrypted = strings.Sites + xoring.Inlined + b64.Inlined;
int totalProxiesHandled = totalMainProxies + b64.EncodingProxies + simpleProxies.Proxies;
int totalProxySites = totalMainSites + b64.EncodingSites + simpleProxies.Sites;

ConsoleUi.SummaryBox("DEOBFUSCATION SUMMARY", new[]
{
    ("Methods Devirtualized / Restored", $"{devirt.Devirtualized} / {devirt.Stubs} ({devirt.Failed} failed)", devirt.Failed == 0 ? ConsoleColor.Green : ConsoleColor.Yellow),
    ("Strings Decrypted (Total)", $"{totalStringsDecrypted} (Stat:{strings.Sites} XOR:{xoring.Inlined} B64:{b64.Inlined})", ConsoleColor.Green),
    ("Proxies Eliminated", $"{totalProxiesHandled} ({totalProxySites} sites, {simpleProxies.Removed} removed)", ConsoleColor.Cyan),
    ("Junk Instructions Stripped", $"{totalJunk} (Pref:{junk.Prefixes} Br:{junk.FallsThrough} Dead:{junk.DeadCode})", ConsoleColor.White),
    ("Remaining Inx::Execute Calls", remaining.ToString(), remaining == 0 ? ConsoleColor.Green : ConsoleColor.Red),
    ("Broken / Missing Operands", bad.ToString(), bad == 0 ? ConsoleColor.Green : ConsoleColor.Red),
    ("Total Elapsed", $"{totalSw.Elapsed.TotalSeconds:F2} s", ConsoleColor.White),
});

var topMethods = check.GetTypes()
    .SelectMany(t => t.Methods)
    .Where(m => m.HasBody)
    .OrderByDescending(m => m.Body.Instructions.Count)
    .Take(8)
    .Select(m => (m.Body.Instructions.Count, m.FullName))
    .ToList();

ConsoleUi.PrintTopMethods(topMethods);

static void WriteCleanedModule(ModuleDefMD mod, string outputPath)
{
    var opts = new ModuleWriterOptions(mod)
    {
        MetadataOptions = { Flags = MetadataFlags.PreserveAll | MetadataFlags.KeepOldMaxStack },
        Logger = DummyLogger.NoThrowInstance,
    };
    mod.Write(outputPath, opts);
}
