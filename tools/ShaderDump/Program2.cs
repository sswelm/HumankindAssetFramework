// Pass 2: parse the bundle's assets, find the ComputeShader (classId 72) named AmpliAnimation,
// and dump its serialized field tree so we can locate the compressed kernel blobs.
using System;
using System.IO;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

class Program2
{
    static void Dump(AssetTypeValueField f, TextWriter w, int depth)
    {
        string val = "";
        try
        {
            if (f.Value != null)
            {
                if (f.TemplateField.ValueType == AssetValueType.ByteArray)
                    val = $" <bytes[{f.AsByteArray.Length}]>";
                else
                {
                    string s = f.AsString;
                    val = " = " + (s.Length > 80 ? s.Substring(0, 80) + "..." : s);
                }
            }
        }
        catch { }
        w.WriteLine($"{new string(' ', depth * 2)}{f.TypeName} {f.FieldName}{val}  (children: {f.Children.Count})");
        int shown = 0;
        foreach (var c in f.Children)
        {
            if (shown++ > 12) { w.WriteLine($"{new string(' ', depth * 2 + 2)}... ({f.Children.Count - 12} more)"); break; }
            Dump(c, w, depth + 1);
        }
    }

    static void Main2Old(string[] args)
    {
        string path = args[0];
        var mgr = new AssetsManager();
        var bun = mgr.LoadBundleFile(path, true);
        var afile = mgr.LoadAssetsFileFromBundle(bun, 0, false);
        Console.WriteLine("assets: " + afile.file.AssetInfos.Count);
        foreach (var info in afile.file.AssetInfos)
        {
            if (info.TypeId != 72) continue;   // ComputeShader
            var bf = mgr.GetBaseField(afile, info);
            string name = bf["m_Name"].AsString;
            Console.WriteLine($"ComputeShader: {name} (pathId {info.PathId}, size {info.ByteSize})");
            if (name == "AmpliAnimation")
            {
                using var w = new StreamWriter("amplianimation_tree.txt");
                Dump(bf, w, 0);
                Console.WriteLine("tree -> amplianimation_tree.txt");
            }
        }
    }
}
