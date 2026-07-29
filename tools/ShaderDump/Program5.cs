// Pass 5 debug: list type ids present and dump the field layout of one Shader asset.
using System;
using System.Linq;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

class Program5
{
    static void Main5Old(string[] args)
    {
        string path = args[0];
        var mgr = new AssetsManager();
        var bun = mgr.LoadBundleFile(path, true);
        var afile = mgr.LoadAssetsFileFromBundle(bun, 0, false);
        var groups = afile.file.AssetInfos.GroupBy(i => i.TypeId).OrderByDescending(g => g.Count());
        foreach (var g in groups.Take(20)) Console.WriteLine($"typeId {g.Key}: {g.Count()} assets");
        var sh = afile.file.AssetInfos.FirstOrDefault(i => i.TypeId == 48);
        if (sh != null)
        {
            var bf = mgr.GetBaseField(afile, sh);
            void Walk(AssetTypeValueField f, int d)
            {
                if (d > 2) return;
                Console.WriteLine($"{new string(' ', d * 2)}{f.TypeName} {f.FieldName} (kids {f.Children.Count})");
                foreach (var c in f.Children.Take(30)) Walk(c, d + 1);
            }
            Walk(bf, 0);
        }
        else Console.WriteLine("no Shader assets in this file");
    }
}
