using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Deobfuscator.Core
{
    public static class ConsoleUi
    {
        private const int BoxWidth = 78;

        public static void Init()
        {
            try
            {
                Console.OutputEncoding = Encoding.UTF8;
            }
            catch
            {
            }
        }

        public static void Banner()
        {
            var top = "╔" + new string('═', BoxWidth - 2) + "╗";
            var bot = "╚" + new string('═', BoxWidth - 2) + "╝";
            WriteColored(top + "\n", ConsoleColor.DarkCyan);
            WriteCenteredLine("InxDeobfuscator & Devirtualizer", ConsoleColor.Cyan);
            WriteCenteredLine(".NET 9.0  |  dnlib 4.5.0  |  ConversionBack Cleaner", ConsoleColor.DarkGray);
            WriteColored(bot + "\n\n", ConsoleColor.DarkCyan);
        }

        public static void PrintUsage()
        {
            Banner();
            WriteColored("  USAGE\n", ConsoleColor.Yellow);
            WriteColored("    Deobfuscator <protected.exe> <output.exe>\n", ConsoleColor.White);
            WriteColored("    Deobfuscator --b64 <protected.exe> <output.exe>\n", ConsoleColor.Gray);
            WriteColored("    Deobfuscator --dump <input.exe> <dump.txt>\n", ConsoleColor.Gray);
            WriteColored("    Deobfuscator --dumpstr <input.exe> <strings.txt>\n\n", ConsoleColor.Gray);

            WriteColored("  MODES\n", ConsoleColor.Yellow);
            WriteKeyValue("    (default)", "Full devirtualization, junk, proxy & string pipeline");
            WriteKeyValue("    --b64", "Run only Base64 string inliner & encoding proxy remover");
            WriteKeyValue("    --dump", "Dump <Module> fields, .cctor calls & string decryptors");
            WriteKeyValue("    --dumpstr", "Dump all non-whitespace ldstr literals");
            Console.WriteLine();
        }

        public static void SectionHeader(string title)
        {
            WriteColored($"  {title}\n", ConsoleColor.Yellow);
        }

        public static void InfoRow(string key, string value, ConsoleColor valueColor = ConsoleColor.White)
        {
            WriteColored($"    {key,-12}: ", ConsoleColor.DarkGray);
            WriteColored(value + "\n", valueColor);
        }

        public static void BeginStep(int index, int total, string label)
        {
            BeginLine($"[{index}/{total}]", ConsoleColor.Cyan, label);
        }

        public static void BeginTag(string tag, string label)
        {
            BeginLine($"[{tag.Trim()}]", ConsoleColor.Magenta, label);
        }

        private static void BeginLine(string tag, ConsoleColor tagColor, string label)
        {
            const int totalPrefixWidth = 49;
            string prefix = $"  {tag} ";
            int padWidth = Math.Max(10, totalPrefixWidth - prefix.Length);
            WriteColored(prefix, tagColor);
            var text = label.Length > padWidth ? label[..padWidth] : label.PadRight(padWidth);
            WriteColored(text, ConsoleColor.Gray);
        }

        public static void EndStep(bool ok, string detail, long elapsedMs)
        {
            WriteColored(ok ? "OK   " : "WARN ", ok ? ConsoleColor.Green : ConsoleColor.Yellow);
            WriteColored(detail, ConsoleColor.White);
            WriteColored($"  [{elapsedMs} ms]\n", ConsoleColor.DarkGray);
        }

        public static void SubNote(string text, ConsoleColor color = ConsoleColor.DarkYellow)
        {
            WriteColored($"          -> {text}\n", color);
        }

        public static void SummaryBox(string title, IReadOnlyList<(string Label, string Value, ConsoleColor Color)> rows)
        {
            const int leftW = 34;
            const int rightW = BoxWidth - leftW - 5;

            Console.WriteLine();
            WriteColored("┌" + new string('─', BoxWidth - 2) + "┐\n", ConsoleColor.DarkCyan);
            WriteBoxCentered(title, ConsoleColor.Cyan);
            WriteColored("├" + new string('─', leftW + 2) + "┬" + new string('─', rightW + 2) + "┤\n", ConsoleColor.DarkCyan);

            foreach (var (label, value, color) in rows)
            {
                var l = TruncatePad(label, leftW);
                var v = TruncatePad(value, rightW);
                WriteColored("│ ", ConsoleColor.DarkCyan);
                WriteColored(l, ConsoleColor.Gray);
                WriteColored(" │ ", ConsoleColor.DarkCyan);
                WriteColored(v, color);
                WriteColored(" │\n", ConsoleColor.DarkCyan);
            }

            WriteColored("└" + new string('─', leftW + 2) + "┴" + new string('─', rightW + 2) + "┘\n", ConsoleColor.DarkCyan);
        }

        public static void PrintTopMethods(IReadOnlyList<(int Count, string FullName)> methods)
        {
            Console.WriteLine();
            WriteColored("  LARGEST RECOVERED METHODS\n", ConsoleColor.Yellow);
            for (int i = 0; i < methods.Count; i++)
            {
                var (count, name) = methods[i];
                WriteColored($"    {i + 1,2}. ", ConsoleColor.DarkGray);
                WriteColored($"{count,5} IL  ", ConsoleColor.Cyan);
                WriteColored(name + "\n", ConsoleColor.White);
            }
            Console.WriteLine();
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024.0):F2} MB";
        }

        private static void WriteKeyValue(string key, string value)
        {
            WriteColored($"  {key,-15}", ConsoleColor.Cyan);
            WriteColored(value + "\n", ConsoleColor.Gray);
        }

        private static void WriteCenteredLine(string text, ConsoleColor color)
        {
            int inner = BoxWidth - 2;
            if (text.Length > inner) text = text[..inner];
            int left = (inner - text.Length) / 2;
            int right = inner - text.Length - left;
            WriteColored("║", ConsoleColor.DarkCyan);
            Console.Write(new string(' ', left));
            WriteColored(text, color);
            Console.Write(new string(' ', right));
            WriteColored("║\n", ConsoleColor.DarkCyan);
        }

        private static void WriteBoxCentered(string text, ConsoleColor color)
        {
            int inner = BoxWidth - 2;
            if (text.Length > inner) text = text[..inner];
            int left = (inner - text.Length) / 2;
            int right = inner - text.Length - left;
            WriteColored("│", ConsoleColor.DarkCyan);
            Console.Write(new string(' ', left));
            WriteColored(text, color);
            Console.Write(new string(' ', right));
            WriteColored("│\n", ConsoleColor.DarkCyan);
        }

        private static string TruncatePad(string text, int width)
        {
            if (text.Length > width) return text[..(width - 3)] + "...";
            return text.PadRight(width);
        }

        private static void WriteColored(string text, ConsoleColor color)
        {
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.Write(text);
            Console.ForegroundColor = prev;
        }
    }
}
