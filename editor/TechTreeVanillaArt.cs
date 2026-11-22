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

    // Seam for a drill. The game's folder is two up from the registry's config folder (<game>/BepInEx/config); when
    // that folder was overridden to somewhere else, the install the registry can detect by itself is tried next.
    internal static Func<string> BundlePathImpl = () =>
    {
        string first = null;
        foreach (string config in new[] { ModelRegistry.ConfigDir, ModelRegistry.AutoDetectConfigDir() })
        {
            if (string.IsNullOrEmpty(config)) continue;
            string path = Path.Combine(Path.GetFullPath(Path.Combine(config, "..", "..")), "AssetBundles", "Mercury.Data.UI", "mercury.data.ui.assetbundle");
            if (first == null) first = path;
            if (File.Exists(path)) return path;
        }
        return first ?? "";
    };

    static Dictionary<string, Region> regions;   // Unity GUID text of an image -> where it is; null until read
    static string problem;                       // why the game's images cannot be shown; null = not tried, "" = fine
    static readonly Dictionary<string, Texture2D> atlases = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
    // the atlas textures this class took out of a bundle it opened ITSELF: only those are its own to unload
    static readonly HashSet<Texture2D> ownTextures = new HashSet<Texture2D>();
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
        if (atlases.TryGetValue(output, out var tex))
        {
            // A failed load is remembered as a true null and stays until Reload. A texture that WAS loaded and has been
            // destroyed since (whoever holds the bundle unloaded it) is loaded again.
            if (tex != null || ReferenceEquals(tex, null)) return tex;
            atlases.Remove(output); ownTextures.Remove(tex);
        }
        tex = null; bool own = false; string why = null;
        try { tex = WithBundle(b => b.LoadAsset<Texture2D>(AtlasFolder + output), out own); }
        catch (Exception e) { why = e.Message; }
        if (tex == null)
            Debug.LogWarning("[TechTree] the game's atlas '" + output + "' could not be loaded (its cards show without artwork; Reload tries again): " + (why ?? "the bundle holds no such texture"));
        else if (own) ownTextures.Add(tex);
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
        gone.Clear();
        foreach (var kv in icons) if (kv.Value == null) gone.Add(kv.Key);
        foreach (var k in gone) icons.Remove(k);
    }

    // ── One small texture per ICON ────────────────────────────────────────────
    // A card's unlock icons are 46 units high and drawn at a fraction of that. Two things make the atlas itself a
    // poor source for them (review 2026-11-22, measured):
    //  - The pictograms of bonuses, resources and actions sit in ALPHA-ONLY atlases (Alpha8: a distance field in the
    //    alpha, RGB 0 in every pixel). The game draws them with a distance-field shader; drawn as stored they are
    //    black, blurred shapes (the user's screenshot of the first version).
    //  - The colour icons' atlases have no mip levels, so a 212 px icon drawn at 20 px is grainy beside the
    //    project's own icons, which have them.
    // So each icon is copied once into a texture of its own, with mip levels: a pictogram as a WHITE shape whose
    // alpha is the field ramped around its contour at 0.5 (the reviewer's verified recipe: twice the field's size,
    // slope 16; a hard threshold loses thin strokes, the raw field is a glow), a colour icon as it is.
    static readonly Dictionary<string, Texture2D> icons = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
    internal const int IconMax = 128;        // a colour icon's copy is at most this large
    internal const float FieldSlope = 16f;   // how steep the pictogram's edge is made (per unit of alpha, at twice the field's size)

    /// <summary>The icon for that image reference as a texture of its own; null when the image is not one of the game's or cannot be read.</summary>
    public static Texture2D Icon(string unityGuid)
    {
        if (string.IsNullOrEmpty(unityGuid)) return null;
        if (icons.TryGetValue(unityGuid, out var icon))
        {
            if (icon != null || ReferenceEquals(icon, null)) return icon;   // a remembered failure stays until Reload; a destroyed one is made again
            icons.Remove(unityGuid);
        }
        icon = null;
        if (TryRegion(unityGuid, out var region))
        {
            var atlas = Atlas(region.Output);
            if (atlas != null)
            {
                try { icon = Bake(atlas, region.Uv, region.Width, region.Height, atlas.format == TextureFormat.Alpha8); }
                catch (Exception e) { Debug.LogWarning("[TechTree] the game's icon in '" + region.Output + "' could not be copied (it is not shown; Reload tries again): " + e.Message); }
            }
        }
        icons[unityGuid] = icon;
        HookCleanup();
        return icon;
    }

    /// <summary>
    /// Copies the rectangle `uv` of `atlas` into a new texture with mip levels. `field` = the source is a distance
    /// field in the alpha channel: the copy is white, at twice the size, with the alpha ramped around 0.5.
    /// Safe inside OnGUI: the render target that was active is put back (without that, the rest of the window's
    /// draw is lost; executed by the reviewer).
    /// </summary>
    internal static Texture2D Bake(Texture atlas, Rect uv, int width, int height, bool field)
    {
        int w = Mathf.Max(1, width), h = Mathf.Max(1, height);
        if (field) { w *= 2; h *= 2; }
        else if (Mathf.Max(w, h) > IconMax) { float k = IconMax / (float)Mathf.Max(w, h); w = Mathf.Max(1, Mathf.RoundToInt(w * k)); h = Mathf.Max(1, Mathf.RoundToInt(h * k)); }
        // the copy keeps the source's bytes: the target and the copy are of the source's colour space, so a project
        // in linear colour space converts on the way in and back on the way out (ENC's is gamma: no conversion at all)
        bool srgb = !field && UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsSRGBFormat(atlas.graphicsFormat);
        var previous = RenderTexture.active;
        var target = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
        try
        {
            Graphics.Blit(atlas, target, new Vector2(uv.width, uv.height), new Vector2(uv.x, uv.y));
            RenderTexture.active = target;
            var copy = new Texture2D(w, h, TextureFormat.RGBA32, true, !srgb) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp };
            copy.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
            if (field)
            {
                var pixels = copy.GetPixels32();
                for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(255, 255, 255, FieldAlpha(pixels[i].a));
                copy.SetPixels32(pixels);
            }
            copy.Apply(true, true);
            return copy;
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(target);
        }
    }

    /// <summary>A distance field's value as the shape's alpha: 0.5 is the contour, the ramp around it FieldSlope steep.</summary>
    internal static byte FieldAlpha(byte field) =>
        (byte)Mathf.RoundToInt(255f * Mathf.Clamp01((field / 255f - 0.5f) * FieldSlope + 0.5f));

    static void EnsureIndex()
    {
        if (regions != null || problem != null) return;
        try
        {
            regions = WithBundle(b =>
            {
                var atlas = b.LoadAsset(AtlasPath);
                // in the bundle, yet nothing loaded: the index's script type is missing, not the asset
                if (atlas == null && b.Contains(AtlasPath))
                    throw new InvalidOperationException("the game's atlas index is in its UI bundle but did not load (is the game's Amplitude.UI.dll in this project, and did Unity load it?)");
                return ReadAtlas(atlas);
            }, out _);
            problem = "";
        }
        catch (Exception e)
        {
            problem = e.Message;
            Debug.LogWarning("[TechTree] the game's own card images are not shown: " + e.Message);
        }
    }

    // `own` = this call opened the bundle itself (and closed it again); false = something else had it mounted.
    static T WithBundle<T>(Func<AssetBundle, T> use, out bool own)
    {
        own = false;
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
        own = mine;
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

    // The textures out of a bundle this class opened and closed outlive it and belong to nobody: free them before the
    // scripts reload. A texture out of a bundle something else has mounted is that holder's, and is left alone.
    static void HookCleanup()
    {
        if (cleanupHooked) return;
        cleanupHooked = true;
        AssemblyReloadEvents.beforeAssemblyReload += () =>
        {
            foreach (var tex in ownTextures) if (tex != null) Resources.UnloadAsset(tex);
            ownTextures.Clear(); atlases.Clear();
            foreach (var icon in icons.Values) if (icon != null) UnityEngine.Object.DestroyImmediate(icon);   // this class made them
            icons.Clear();
        };
    }
}
