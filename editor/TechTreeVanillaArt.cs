// TechTreeVanillaArt.cs (HAF editor) — the game's OWN card images for the Tech Tree viewer.
//
// A technology's card image is a reference (an Amplitude.Framework.Guid) in its TechnologyUIMapper. A mod's own image
// is a file in the project and TechTreeData resolves it there. The game's images are not in the project: they are
// packed into texture atlases inside the game's UI bundle,
//
//     <game>/AssetBundles/Mercury.Data.UI/mercury.data.ui.assetbundle
//
// with one index asset, UIAtlas.asset (Amplitude.UI.Atlas.UITextureAtlas): atlasEntries[] = { Guid, Index,
// Coordinates, WidthHeight } and outputEntries[] = { Name, ... }, where Index picks the output (the atlas texture, by
// file name) and Coordinates is the image's rectangle in it, 0..1, measured from the texture's TOP.
//
// Checked outside Unity on 2026-11-21 (the bundle read with UnityPy): 4,440 entries and 193 outputs; World-Wide Web's
// reference is entry -> output 110, "UI_Technology_Era6_00.png" (2048 x 2048), 360 x 360 px, and cropping that
// rectangle from the top gives the game's picture (from the bottom it gives part of another one).
//
// The bundle is 1.2 GB (LZ4 chunks, so opening it reads only its directory). It is opened for one read and closed
// again each time (Unload(false) keeps what was loaded), so the modding tools are never refused it because this
// window holds it; a bundle that something else has open already is used as it is and left open.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

static class TechTreeVanillaArt
{
    public struct Region
    {
        public string Output;      // the atlas texture's file name, e.g. "UI_Technology_Era6_00.png"
        public Rect Uv;            // the image's rectangle in it, in Unity's texture coordinates (from the BOTTOM)
        public int Width, Height;  // the image's size in pixels
    }

    internal const string AtlasFolder = "Assets/Data/[UI]/Atlases/UIAtlasBuilder.Generated/";
    internal const string AtlasPath = AtlasFolder + "UIAtlas.asset";

    // Seam for a drill: the game's folder is found through the registry's config folder.
    internal static Func<string> BundlePathImpl = () =>
        Path.Combine(Path.GetFullPath(Path.Combine(ModelRegistry.ConfigDir, "..", "..")), "AssetBundles", "Mercury.Data.UI", "mercury.data.ui.assetbundle");

    static Dictionary<string, Region> regions;   // Unity GUID text of an image -> where it is; null until read
    static string problem;                       // why the game's images cannot be shown; null = not tried, "" = fine
    static readonly Dictionary<string, Texture2D> atlases = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
    static bool cleanupHooked;

    /// <summary>Why the game's images are not shown ("" when they are, or when nothing asked for one yet).</summary>
    public static string Problem => problem ?? "";

    /// <summary>The atlas rectangle an Amplitude image holds, turned into Unity's texture coordinates (v runs up).</summary>
    internal static Rect UvOf(Rect coordinates) =>
        new Rect(coordinates.x, 1f - coordinates.y - coordinates.height, coordinates.width, coordinates.height);

    public static bool TryRegion(string unityGuid, out Region region)
    {
        region = default(Region);
        if (string.IsNullOrEmpty(unityGuid)) return false;
        EnsureIndex();
        return regions != null && regions.TryGetValue(unityGuid, out region);
    }

    /// <summary>The atlas texture of that name, loaded on first use; null when it cannot be loaded (said once).</summary>
    public static Texture2D Atlas(string output)
    {
        if (string.IsNullOrEmpty(output)) return null;
        if (atlases.TryGetValue(output, out var tex)) return tex;
        try { tex = WithBundle(b => b.LoadAsset<Texture2D>(AtlasFolder + output)); }
        catch (Exception e) { Debug.LogWarning("[TechTree] the game's atlas '" + output + "' could not be loaded (its cards show without artwork): " + e.Message); }
        atlases[output] = tex;
        HookCleanup();
        return tex;
    }

    /// <summary>The Reload button: try again what failed (the game was closed, the bundle is readable now, ...).</summary>
    public static void RetryFailed()
    {
        if (!string.IsNullOrEmpty(problem)) { problem = null; regions = null; }
        var gone = new List<string>();
        foreach (var kv in atlases) if (kv.Value == null) gone.Add(kv.Key);
        foreach (var k in gone) atlases.Remove(k);
    }

    static void EnsureIndex()
    {
        if (regions != null || problem != null) return;
        try
        {
            regions = WithBundle(b => ReadAtlas(b.LoadAsset(AtlasPath)));
            problem = "";
        }
        catch (Exception e)
        {
            problem = e.Message;
            Debug.LogWarning("[TechTree] the game's own card images are not shown: " + e.Message);
        }
    }

    static T WithBundle<T>(Func<AssetBundle, T> use)
    {
        AssetBundle bundle = null; bool mine = false;
        foreach (var b in AssetBundle.GetAllLoadedAssetBundles())
            if (b != null && b.Contains(AtlasPath)) { bundle = b; break; }
        if (bundle == null)
        {
            string path = BundlePathImpl();
            if (!File.Exists(path)) throw new FileNotFoundException("the game's UI bundle is not at '" + path + "'");
            bundle = AssetBundle.LoadFromFile(path);
            if (bundle == null) throw new InvalidOperationException("Unity could not open the game's UI bundle '" + path + "'");
            mine = true;
        }
        try { return use(bundle); }
        finally { if (mine) bundle.Unload(false); }
    }

    // By reflection: the index's type lives in the game's Amplitude.UI.dll, which a modding project carries.
    static Dictionary<string, Region> ReadAtlas(UnityEngine.Object atlas)
    {
        if (atlas == null) throw new InvalidOperationException("the game's UI bundle holds no '" + AtlasPath + "'");
        const BindingFlags ALL = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var entries = atlas.GetType().GetField("atlasEntries", ALL)?.GetValue(atlas) as Array;
        var outputs = atlas.GetType().GetField("outputEntries", ALL)?.GetValue(atlas) as Array;
        if (entries == null || outputs == null)
            throw new InvalidOperationException("the game's atlas index loaded as " + atlas.GetType().FullName + " without its entries (is the game's Amplitude.UI.dll in this project?)");

        var names = new string[outputs.Length];
        FieldInfo fName = null;
        for (int i = 0; i < outputs.Length; i++)
        {
            object o = outputs.GetValue(i);
            if (fName == null) fName = o.GetType().GetField("Name", ALL);
            names[i] = fName?.GetValue(o) as string ?? "";
        }

        var map = new Dictionary<string, Region>(entries.Length, StringComparer.Ordinal);
        FieldInfo fGuid = null, fIndex = null, fCoords = null, fSize = null, fa = null, fb = null, fc = null, fd = null;
        foreach (object e in entries)
        {
            if (fGuid == null)
            {
                var et = e.GetType();
                fGuid = et.GetField("Guid", ALL); fIndex = et.GetField("Index", ALL); fCoords = et.GetField("Coordinates", ALL); fSize = et.GetField("WidthHeight", ALL);
                if (fGuid == null || fIndex == null || fCoords == null || fSize == null)
                    throw new InvalidOperationException("the game's atlas entries no longer have Guid / Index / Coordinates / WidthHeight");
                var gt = fGuid.FieldType;
                fa = gt.GetField("a", ALL); fb = gt.GetField("b", ALL); fc = gt.GetField("c", ALL); fd = gt.GetField("d", ALL);
                if (fa == null || fb == null || fc == null || fd == null)
                    throw new InvalidOperationException("the game's Guid no longer has the four ints a, b, c, d");
            }
            object g = fGuid.GetValue(e);
            int index = (int)fIndex.GetValue(e);
            if (index < 0 || index >= names.Length) continue;
            var size = (Vector2Int)fSize.GetValue(e);
            string key = TechTreeData.AmplitudeGuidToUnity((int)fa.GetValue(g), (int)fb.GetValue(g), (int)fc.GetValue(g), (int)fd.GetValue(g));
            map[key] = new Region { Output = names[index], Uv = UvOf((Rect)fCoords.GetValue(e)), Width = size.x, Height = size.y };
        }
        return map;
    }

    // The atlas textures outlive the closed bundle and belong to nobody: free them before the scripts reload.
    static void HookCleanup()
    {
        if (cleanupHooked) return;
        cleanupHooked = true;
        AssemblyReloadEvents.beforeAssemblyReload += () =>
        {
            foreach (var tex in atlases.Values) if (tex != null) Resources.UnloadAsset(tex);
            atlases.Clear();
        };
    }
}
