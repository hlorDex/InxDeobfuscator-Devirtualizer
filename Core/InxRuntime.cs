using System;
using System.Security.Cryptography;
using System.Text;

namespace Deobfuscator.Core
{
    public static class InxRuntime
    {
        public const string Key1 = "\u0623\u064b";
        public const string Key2 = "Class";

        public static byte[] ExclusiveOr(byte[] data)
        {
            var rand = new NetFxRandom(23546654);
            var result = new byte[data.Length];
            for (int i = 0; i < data.Length; i++)
                result[i] = (byte)(data[i] ^ (byte)rand.Next(0, 250));
            return result;
        }

        public static byte[] KeyXor(byte[] data, string password)
        {
            byte[] keys = Encoding.ASCII.GetBytes(password);
            var result = (byte[])data.Clone();
            for (int i = 0; i < result.Length; i++)
                result[i] ^= keys[i % keys.Length];
            return result;
        }

        public static byte[] DecryptorXor(byte[] data, byte[] key)
        {
            int datalen = data.Length, keylen = key.Length;
            var output = (byte[])data.Clone();
            if (keylen == 0) return output;
            int N1 = 12, N2 = 14, NS = 258;
            for (int I = 0; I < keylen; I++) NS += NS % (key[I] + 1);
            for (int I = 0; I < datalen; I++)
            {
                NS = key[I % keylen] + NS;
                N1 = (NS + 5) * (N1 & 255) + (N1 >> 8);
                N2 = (NS + 7) * (N2 & 255) + (N2 >> 8);
                NS = ((N1 << 8) + N2) & 255;
                output[I] = (byte)(output[I] ^ NS);
            }
            byte[] hk = Encoding.ASCII.GetBytes("HCP");
            for (int i = 0; i < datalen; i++) output[i] ^= hk[i % hk.Length];
            return output;
        }

        public static byte[] RijndaelDecrypt(byte[] key, byte[] message)
        {
            using var alg = new RijndaelManaged { Key = key, IV = key };
            using var ms = new System.IO.MemoryStream();
            using (var transform = alg.CreateDecryptor())
            using (var cs = new CryptoStream(ms, transform, CryptoStreamMode.Write))
            {
                cs.Write(message, 0, message.Length);
                cs.FlushFinalBlock();
            }
            return ms.ToArray();
        }

        public static byte[] Md5(string s) => MD5.Create().ComputeHash(Encoding.ASCII.GetBytes(s));

        public static string XoringStub(string a, string b)
        {
            byte[] ba = Encoding.UTF8.GetBytes(a), bb = Encoding.UTF8.GetBytes(b);
            int n = Math.Min(ba.Length, bb.Length);
            var sb = new StringBuilder();
            for (int i = 0; i < n; i++) sb.Append((char)(ba[i] ^ bb[i]));
            if (a.Length > n) sb.Append(a.Substring(n));
            return sb.ToString();
        }
    }
}
