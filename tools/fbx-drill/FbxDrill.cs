using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

// The C# side of tools/fbx_drill.sh: editor/FbxDocument.cs's dump of every record, in the format tools/fbx-drill/
// fbx_nodes.py prints (its header says it), so the two readers are compared row for row.
//
//   FbxDrill.exe <file.fbx> ...          the dumps, files separated by '=== <path>'; a file that does not read prints
//                                        'ERROR <message>' under its header and the exit status is 1
//   FbxDrill.exe --time <file.fbx> ...   per file: 'TIME <ms> <records> <path>' (the cost of the format layer alone)
static class FbxDrill
{
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--reject")
        {
            int failed = 0;
            for (int i = 1; i < args.Length; i++)
            {
                try { FbxDocument.Read(args[i]); Console.WriteLine("FAIL accepted malformed FBX: " + args[i]); failed++; }
                catch (InvalidDataException) { Console.WriteLine("REFUSED " + args[i]); }
                catch (Exception e) { Console.WriteLine("FAIL unnamed refusal: " + e); failed++; }
            }
            return failed == 0 && args.Length > 1 ? 0 : 1;
        }
        if (args.Length > 0 && (args[0] == "--scene" || args[0] == "--scene-full"))
        {
            // the object layer (editor/FbxScene.cs) in tools/fbx-drill/ufbx/ufbxdump.c's format: step 2's comparison
            bool fullScene = args[0] == "--scene-full";
            int src = 0;
            var so = new StringBuilder();
            for (int i = 1; i < args.Length; i++)
            {
                so.Append("=== ").Append(args[i]).Append('\n');
                try { FbxSceneDump.Dump(FbxScene.Read(args[i]), so, fullScene); }
                catch (Exception e) { so.Append("ERROR ").Append(e.Message).Append('\n'); src = 1; }
            }
            var sout = Console.OpenStandardOutput();
            var sbytes = new UTF8Encoding(false).GetBytes(so.ToString());
            sout.Write(sbytes, 0, sbytes.Length);
            return src;
        }
        bool time = args.Length > 0 && args[0] == "--time";
        int rc = 0;
        var sha = SHA1.Create();
        var o = new StringBuilder();
        for (int a = time ? 1 : 0; a < args.Length; a++)
        {
            string path = args[a];
            if (time)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try { var d = FbxDocument.Read(path); sw.Stop(); o.Append("TIME ").Append(sw.ElapsedMilliseconds).Append(' ').Append(Count(d.Nodes)).Append(' ').Append(path).Append('\n'); }
                catch (Exception e) { o.Append("ERROR ").Append(path).Append(": ").Append(e.Message).Append('\n'); rc = 1; }
                continue;
            }
            o.Append("=== ").Append(path).Append('\n');
            try
            {
                var doc = FbxDocument.Read(path);
                o.Append("version ").Append(doc.Version).Append('\n');
                int n = 0;
                foreach (var node in doc.Nodes) Dump(node, 0, o, sha, ref n);
                o.Append("records ").Append(n).Append('\n');
            }
            catch (Exception e) { o.Append("ERROR ").Append(e.Message).Append('\n'); rc = 1; }
        }
        var stdout = Console.OpenStandardOutput();
        var bytes = new UTF8Encoding(false).GetBytes(o.ToString());
        stdout.Write(bytes, 0, bytes.Length);
        return rc;
    }

    static int Count(List<FbxNode> nodes) { int n = 0; foreach (var x in nodes) n += 1 + Count(x.Children); return n; }

    static void Dump(FbxNode node, int depth, StringBuilder o, SHA1 sha, ref int n)
    {
        n++;
        o.Append(depth).Append(' ').Append(node.Name).Append(" | ");
        foreach (var p in node.Properties) o.Append(Code(p));
        o.Append(" | ");
        bool first = true;
        foreach (var p in node.Properties) { if (!first) o.Append(' '); first = false; o.Append(Value(p, sha)); }
        o.Append('\n');
        foreach (var c in node.Children) Dump(c, depth + 1, o, sha, ref n);
    }

    static char Code(object p)
    {
        switch (p)
        {
            case sbyte _: return 'Z'; case short _: return 'Y'; case byte _: return 'C'; case bool _: return 'B'; case int _: return 'I'; case float _: return 'F'; case double _: return 'D'; case long _: return 'L';
            case string _: return 'S'; case FbxRaw _: return 'R';
            case float[] _: return 'f'; case double[] _: return 'd'; case long[] _: return 'l'; case int[] _: return 'i'; case bool[] _: return 'b'; case byte[] _: return 'c';
            default: return '?';
        }
    }

    static string Hex(byte[] b) { var s = new StringBuilder(b.Length * 2); foreach (var x in b) s.Append(x.ToString("x2")); return s.ToString(); }

    static string Value(object p, SHA1 sha)
    {
        switch (p)
        {
            case sbyte z: return z.ToString();
            case byte character: return character.ToString();
            case short y: return y.ToString();
            case bool c: return c ? "1" : "0";
            case int i: return i.ToString();
            case long l: return l.ToString();
            case float f: return BitConverter.ToUInt32(BitConverter.GetBytes(f), 0).ToString("x8");
            case double d: return BitConverter.ToUInt64(BitConverter.GetBytes(d), 0).ToString("x16");
            case string s: { var raw = Encoding.UTF8.GetBytes(s); return "S:" + raw.Length + ":" + Hex(sha.ComputeHash(raw)); }
            case FbxRaw r: return "R:" + r.Bytes.Length + ":" + Hex(sha.ComputeHash(r.Bytes));
            case float[] a: { var raw = new byte[a.Length * 4]; Buffer.BlockCopy(a, 0, raw, 0, raw.Length); return "f[" + a.Length + "]:" + Hex(sha.ComputeHash(raw)); }
            case double[] a: { var raw = new byte[a.Length * 8]; Buffer.BlockCopy(a, 0, raw, 0, raw.Length); return "d[" + a.Length + "]:" + Hex(sha.ComputeHash(raw)); }
            case long[] a: { var raw = new byte[a.Length * 8]; Buffer.BlockCopy(a, 0, raw, 0, raw.Length); return "l[" + a.Length + "]:" + Hex(sha.ComputeHash(raw)); }
            case int[] a: { var raw = new byte[a.Length * 4]; Buffer.BlockCopy(a, 0, raw, 0, raw.Length); return "i[" + a.Length + "]:" + Hex(sha.ComputeHash(raw)); }
            case byte[] a: return "c[" + a.Length + "]:" + Hex(sha.ComputeHash(a));
            case bool[] a: { var raw = new byte[a.Length]; for (int i = 0; i < a.Length; i++) raw[i] = a[i] ? (byte)1 : (byte)0; return "b[" + a.Length + "]:" + Hex(sha.ComputeHash(raw)); }
            default: return "?";
        }
    }
}
