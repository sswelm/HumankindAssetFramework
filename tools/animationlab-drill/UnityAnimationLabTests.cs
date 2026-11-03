using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

// Hidden test windows bypass the registry-loading OnEnable; all assets and PNGs belong to the runner project.
public class AnimationLabTestWindow : AnimationLabWindow { void OnEnable() { } }

public static class UnityAnimationLabTests
{
    static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static readonly List<string> Rows = new List<string>();
    static int failed;
    static void Set(AnimationLabWindow w, string name, object value) => typeof(AnimationLabWindow).GetField(name, Private).SetValue(w, value);
    static T Get<T>(AnimationLabWindow w, string name) => (T)typeof(AnimationLabWindow).GetField(name, Private).GetValue(w);
    static object Call(AnimationLabWindow w, string name, params object[] args) => typeof(AnimationLabWindow).GetMethod(name, Private).Invoke(w, args);
    static void Check(string name, bool ok) { Rows.Add((ok ? "PASS " : "FAIL ") + name); if (!ok) failed++; }
    static Mesh Mesh(string name)
    {
        var m = new Mesh { name = name, vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 },
            uv = new[] { Vector2.zero, Vector2.right, Vector2.up }, bindposes = new[] { Matrix4x4.identity, Matrix4x4.Translate(Vector3.up) } };
        m.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 3).ToArray();
        return m;
    }
    // A multi-material rig as the Factory sees it: two submeshes (two material slots) over shared vertices. The Factory's
    // persisted clone MERGES them into one submesh in order (BuildMultiAtlasAndRemap), which is the shape every real
    // multi-material entry's clone has - the single-submesh cases above cannot see a check that rejects it.
    static Mesh TwoSubmeshMesh(string name)
    {
        var m = new Mesh { name = name, vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.one },
            uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one }, bindposes = new[] { Matrix4x4.identity, Matrix4x4.Translate(Vector3.up) } };
        m.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 4).ToArray();
        m.subMeshCount = 2; m.SetTriangles(new[] { 0, 1, 2 }, 0); m.SetTriangles(new[] { 1, 3, 2 }, 1);
        return m;
    }
    static void CloneCase(string defect)
    {
        string res = "Preview_" + defect, dir = "Assets/FactorySource/" + res;
        Directory.CreateDirectory(dir + "/anim"); AssetDatabase.Refresh();
        var source = defect.StartsWith("merged") ? TwoSubmeshMesh("source") : Mesh("source"); AssetDatabase.CreateAsset(source, dir + "/source.asset");
        var sub = UnityEngine.Object.Instantiate(source); sub.name = "UV clone";
        bool reject = defect != "valid" && defect != "pool_order" && defect != "merged";
        // the Factory's merge (in order) is the clone; the same triangles merged in another order are not this rig's clone
        if (defect == "merged") { sub.subMeshCount = 1; sub.SetTriangles(new[] { 0, 1, 2, 1, 3, 2 }, 0); }
        if (defect == "merged_reordered") { sub.subMeshCount = 1; sub.SetTriangles(new[] { 1, 3, 2, 0, 1, 2 }, 0); }
        if (defect == "weights") sub.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 1, weight0 = 1 }, 3).ToArray();
        if (defect == "geometry") sub.vertices = new[] { Vector3.zero, Vector3.right * 5, Vector3.up };
        if (defect == "topology") sub.triangles = new[] { 0, 2, 1 };
        if (defect == "unweighted") sub.boneWeights = new BoneWeight[0];
        if (defect == "bindposes" || defect == "pool_order") { var bp = sub.bindposes; bp[0] = Matrix4x4.Translate(Vector3.right); sub.bindposes = bp; }
        if (defect == "nonfinite") { var bp = sub.bindposes; bp[0][0] = float.NaN; sub.bindposes = bp; }
        AssetDatabase.CreateAsset(sub, UniversalBaker.PreviewMeshPath(dir, res, 0));
        var pool = new List<Mesh> { sub }; Mesh expected = reject ? source : sub;
        if (defect == "pool_order")
        {
            var valid = UnityEngine.Object.Instantiate(source); valid.uv = new[] { Vector2.one, Vector2.right, Vector2.up };
            AssetDatabase.CreateAsset(valid, UniversalBaker.PreviewMeshPath(dir, res, 1)); pool.Add(valid); expected = valid;
        }
        var go = new GameObject(res);
        for (int i = 0; i < 2; i++) { var bone = new GameObject("bone" + i); bone.transform.SetParent(go.transform, false); }
        var renderer = go.AddComponent<SkinnedMeshRenderer>(); renderer.sharedMesh = source;
        var mat = new Material(Shader.Find("Standard")) { name = res + "_PreviewMat" };
        AssetDatabase.CreateAsset(mat, dir + "/" + res + "_PreviewMat.mat");
        renderer.sharedMaterials = Enumerable.Repeat(mat, source.subMeshCount).ToArray();
        renderer.bones = new[] { go.transform.GetChild(0), go.transform.GetChild(1) };
        string path = dir + "/anim/" + res + "_anim.prefab";
        PrefabUtility.SaveAsPrefabAsset(go, path); UnityEngine.Object.DestroyImmediate(go);
        var container = new Mesh(); AssetDatabase.CreateAsset(container, dir + "/anim/_" + res + "_Clip_primary.asset");
        AssetDatabase.AddObjectToAsset(new AnimationClip { name = "Idle", legacy = true }, container); AssetDatabase.SaveAssets();
        var w = ScriptableObject.CreateInstance<AnimationLabTestWindow>();
        try
        {
            Set(w, "cur", new ModelDef { resourceName = res }); Set(w, "fitSkin", false);
            Call(w, "BuildAnimPreview", "anim");
            var inst = Get<GameObject>(w, "fitAnimInst");
            Check(defect + ": playing preview chooses compatible mesh", inst != null && inst.GetComponent<SkinnedMeshRenderer>().sharedMesh == expected);
            var playing = inst != null ? inst.GetComponent<SkinnedMeshRenderer>() : null;
            Check(defect + ": playing preview has one material per rendered submesh", playing != null && playing.sharedMaterials.Length == playing.sharedMesh.subMeshCount);
            Call(w, "LoadFitPreview", path, pool);
            var draws = Get<List<(Mesh, Material[], Matrix4x4)>>(w, "fitDraws");
            Check(defect + ": rest preview chooses compatible mesh", draws != null && draws.Count == 1 && draws[0].Item1 == expected);
            if (defect == "valid")
            {
                Get<ModelDef>(w, "cur").textureFile = Path.Combine(Application.dataPath, "absent.png"); Set(w, "fitSkin", true);
                Call(w, "LoadFitPreview", path, pool);
                var field = typeof(AnimationLabWindow).GetField("fitSkinNote", Private);
                Check("rest preview retains the missing-skin explanation for its UI", field != null && ((string)field.GetValue(w) ?? "").Contains("showing the bake atlas"));
            }
        }
        finally { UnityEngine.Object.DestroyImmediate(w); }
    }
    // Exercise the actual writer, not a hand-built approximation of its persisted mesh shape.
    static void FactoryCase()
    {
        const string res = "Preview_factory"; string dir = "Assets/FactorySource/" + res;
        Directory.CreateDirectory(dir + "/anim"); AssetDatabase.Refresh();
        var source = TwoSubmeshMesh("factory source");
        // The material islands have distinct vertices, as the Factory's per-material remapper requires.
        source.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.right * 2, Vector3.right * 3, new Vector3(2, 1, 0) };
        source.uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.zero, Vector2.right, Vector2.up };
        source.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 6).ToArray();
        source.SetTriangles(new[] { 3, 4, 5 }, 1); source.RecalculateNormals();
        AssetDatabase.CreateAsset(source, dir + "/source.asset");
        var go = new GameObject(res); var renderer = go.AddComponent<SkinnedMeshRenderer>(); renderer.sharedMesh = source;
        for (int i = 0; i < 2; i++) { var bone = new GameObject("bone" + i); bone.transform.SetParent(go.transform, false); }
        renderer.bones = new[] { go.transform.GetChild(0), go.transform.GetChild(1) };
        var albedos = new List<KeyValuePair<string, Texture2D>>(); var materials = new Material[2];
        for (int i = 0; i < 2; i++)
        {
            string name = i == 0 ? "factory_red" : "factory_blue";
            materials[i] = new Material(Shader.Find("Standard")) { name = name };
            AssetDatabase.CreateAsset(materials[i], dir + "/" + name + ".mat");
            var image = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            image.SetPixels(Enumerable.Repeat(i == 0 ? Color.red : Color.blue, 16).ToArray()); image.Apply();
            albedos.Add(new KeyValuePair<string, Texture2D>("mat" + i + "_" + name, image));
        }
        renderer.sharedMaterials = materials;
        string path = dir + "/anim/" + res + "_anim.prefab";
        PrefabUtility.SaveAsPrefabAsset(go, path);
        object[] args = { go, albedos, res, new BakeConfig { atlasMaxDim = 64, albedoBrightness = 1, albedoSaturation = 1, keepBlack = true }, null };
        var atlas = (Texture2D)typeof(UniversalBaker).GetMethod("BuildMultiAtlasAndRemap", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, args);
        var clone = ((List<Mesh>)args[4]).Single();
        Check("Factory writer merges a copy and retains the source topology", source.subMeshCount == 2 && clone.subMeshCount == 1 && renderer.sharedMaterials.Length == 1);
        var uv = clone.uv;
        Color32 red = atlas.GetPixelBilinear(uv[0].x, uv[0].y), blue = atlas.GetPixelBilinear(uv[3].x, uv[3].y);
        Check("Factory writer maps both material islands to their atlas colours", red.r == 255 && red.b == 0 && blue.r == 0 && blue.b == 255);
        AssetDatabase.CreateAsset(clone, UniversalBaker.PreviewMeshPath(dir, res, 0));
        AssetDatabase.CreateAsset(atlas, dir + "/atlas.asset");
        var body = new Material(Shader.Find("Standard")) { name = res + "_PreviewMat", mainTexture = atlas };
        AssetDatabase.CreateAsset(body, dir + "/" + res + "_PreviewMat.mat");
        // BuildFitPreview's rest prefab wears the atlas but still carries the rig mesh until LoadFitPreview substitutes it.
        renderer.sharedMesh = source; renderer.sharedMaterials = Enumerable.Repeat(body, source.subMeshCount).ToArray();
        string rest = dir + "/" + res + "_PropFit.prefab";
        PrefabUtility.SaveAsPrefabAsset(go, rest); UnityEngine.Object.DestroyImmediate(go);
        var container = new Mesh(); AssetDatabase.CreateAsset(container, dir + "/anim/_" + res + "_Clip_primary.asset");
        AssetDatabase.AddObjectToAsset(new AnimationClip { name = "Idle", legacy = true }, container); AssetDatabase.SaveAssets();
        var w = ScriptableObject.CreateInstance<AnimationLabTestWindow>();
        try
        {
            Set(w, "cur", new ModelDef { resourceName = res }); Set(w, "fitSkin", false);
            Call(w, "BuildAnimPreview", "anim");
            var playing = Get<GameObject>(w, "fitAnimInst").GetComponent<SkinnedMeshRenderer>();
            Check("Factory writer's persisted clone is worn by the playing preview", playing.sharedMesh == clone);
            Check("Factory writer's playing preview uses one atlas material", playing.sharedMaterials.Length == 1);
            Call(w, "LoadFitPreview", rest, new List<Mesh> { clone });
            var draws = Get<List<(Mesh, Material[], Matrix4x4)>>(w, "fitDraws");
            Check("Factory writer's persisted clone is worn by the rig rest preview", draws != null && draws.Count == 1 && draws[0].Item1 == clone);
        }
        finally { UnityEngine.Object.DestroyImmediate(w); }
    }
    static void SkinCache()
    {
        string png = Path.Combine(Application.dataPath, "skin.png");
        var t = new Texture2D(1, 1, TextureFormat.RGBA32, false); t.SetPixel(0, 0, new Color32(40, 80, 120, 190)); t.Apply();
        File.WriteAllBytes(png, t.EncodeToPNG()); UnityEngine.Object.DestroyImmediate(t);
        var body = new Material(Shader.Find("Standard"));
        var go = new GameObject("active clip renderer"); var r = go.AddComponent<MeshRenderer>();
        var w = ScriptableObject.CreateInstance<AnimationLabTestWindow>();
        try
        {
            var model = new ModelDef { textureFile = png, brightness = 1 }; Set(w, "cur", model); Set(w, "fitSkin", true);
            var first = (Material)Call(w, "PreviewBodyMaterial", body, null); r.sharedMaterial = first;
            model.tintR = 20;
            var second = (Material)Call(w, "PreviewBodyMaterial", body, null);
            Check("skin refresh keeps the active renderer's material alive", first != null && r.sharedMaterial == second);
            Check("skin refresh applies current adjustments", second != null && ((Texture2D)second.mainTexture).GetPixels32()[0].r == 60);
            model.tintR = 30;
            bool fallback = false;
            using (var locked = new FileStream(png, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                try { object[] args = { body, null }; fallback = (Material)Call(w, "PreviewBodyMaterial", args) == body && args[1] != null; }
                catch (TargetInvocationException) { }
            }
            Check("temporarily unreadable skin falls back with an explanation", fallback);
            Check("read fallback also restores the active renderer's atlas", r.sharedMaterial != null && r.sharedMaterial.mainTexture == body.mainTexture);
            var recovered = (Material)Call(w, "PreviewBodyMaterial", body, null);
            Check("skin recovers on the next refresh without replacing the active material", r.sharedMaterial == recovered && ((Texture2D)recovered.mainTexture).GetPixels32()[0].r == 70);
        }
        finally { UnityEngine.Object.DestroyImmediate(w); UnityEngine.Object.DestroyImmediate(go); UnityEngine.Object.DestroyImmediate(body); }
    }
    static void Pixels()
    {
        bool equal = true;
        for (int n = 20; n <= 400; n++)
        {
            float brightness = n / 100f; var lut = SkinAdjust.GammaLut(brightness);
            for (int v = 0; v < 256; v++)
            {
                byte expected = (byte)Mathf.Clamp(Mathf.RoundToInt(255f * Mathf.Pow(v / 255f, 1f / Mathf.Clamp(brightness, .2f, 4f))), 0, 255);
                if ((lut == null ? (byte)v : lut[v]) != expected) equal = false;
            }
        }
        Check("gamma LUT matches Unity's runtime math across 381 brightness values", equal);
        var src = new[] { new Color32(180, 110, 40, 19) }; var adjusted = SkinAdjust.ApplyTo(src, 1.5f, .85f, 50, 50, 0);
        var bytes = new byte[] { 180, 110, 40, 19 }; SkinAdjust.Apply(bytes, 1.5f, .85f, 50, 50, 0);
        Check("Color32 adapter preserves the source and alpha and matches the pure transform", src[0].r == 180 && adjusted[0].a == 19 && adjusted[0].r == bytes[0] && adjusted[0].g == bytes[1] && adjusted[0].b == bytes[2]);
    }
    public static void Run()
    {
        string[] args = Environment.GetCommandLineArgs(); string output = args[Array.IndexOf(args, "-hafPreviewOutput") + 1];
        try { foreach (string defect in new[] { "valid", "bindposes", "weights", "geometry", "topology", "unweighted", "pool_order", "nonfinite", "merged", "merged_reordered" }) CloneCase(defect); FactoryCase(); SkinCache(); Pixels(); }
        catch (Exception e) { Rows.Add("FAIL exception: " + e); failed++; }
        Rows.Add("TOTAL failed " + failed); File.WriteAllLines(output, Rows); EditorApplication.Exit(failed == 0 ? 0 : 1);
    }
}
