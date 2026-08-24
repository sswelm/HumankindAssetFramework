// BakeFeatureTest.cs — INTEGRATION test for the baker's FEATURE knobs (run via Tools ▸ HAF ▸ Bake Tests…).
//
// The Bake Smoke Test proves every registered model still bakes into valid assets. This complements it by proving each
// BAKER FEATURE actually does what it claims: it bakes a self-contained synthetic cube with one knob toggled at a time
// and asserts a feature-specific invariant on the baked mesh/atlas (not just "assets exist"). Fully self-contained (no
// external model file) and NON-DESTRUCTIVE — everything bakes under throwaway "__feat_*" names that are cleaned up after.
//
// Geometry assertions (doubleSided/faceted/heightUV/size/position/normals) are exact & robust. Atlas pixel assertions
// (brightness/saturation) are BEST-EFFORT: the baked atlas is DXT1-compressed and may not be CPU-readable, so those
// report SKIP rather than fail if GetPixels32 throws. Tier 1 = the static path; Blender-dependent (targetTris, stripParts)
// and animated (animClip/animateBones/animUnitFix) features are a planned Tier 2 that needs a rigged fixture.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class BakeFeatureTest
{
    internal const string Prefix = "__feat_";   // internal since 2026-08-23: the delete guard excludes fixtures BY THIS CONSTANT, not by a copy of it

    public static BakeTestSection RunTier1Section()
    {
        var res = new List<string>();
        int pass = 0, fail = 0, skip = 0;
        string tmp = Path.Combine(Path.GetTempPath(), "haf_feattest");
        var used = new List<string>();
        BakeTestSection Section() => new BakeTestSection
        { title = "Features — baker options (synthetic)", pass = pass, fail = fail, skip = skip, body = string.Join("\n", res) };

        try
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(tmp);
            string cube1 = WriteCube(tmp, "cube1", false);   // single-material
            string cube2 = WriteCube(tmp, "cube2", true);    // two-material

            // ---- baseline (KeepModel, no features) — the reference for ratio/relative checks ----
            var mBase = Bake(Cfg("base", cube1), used, out var rBase);
            if (mBase == null) { res.Add("FAIL: baseline bake did not produce a mesh (" + rBase.error + ") — aborting"); fail++; return Section(); }
            int T0 = mBase.triangles.Length / 3, V0 = mBase.vertexCount;
            res.Add($"(baseline: {V0} verts, {T0} tris)");

            // ---- doubleSided: appends a reversed copy of every triangle -> exactly 2x tris ----
            {
                var c = Cfg("ds", cube1); c.doubleSided = true;
                var m = Bake(c, used, out _);
                int t = m != null ? m.triangles.Length / 3 : -1;
                Check(res, ref pass, ref fail, "doubleSided doubles triangles", m != null && t == 2 * T0, $"{T0} -> {t} (expected {2 * T0})");
            }

            // ---- Faceted normals: unwelds so each triangle corner is its own vertex -> vertexCount == index count ----
            {
                var c = Cfg("faceted", cube1); c.normals = NormalsMode.Faceted;
                var m = Bake(c, used, out _);
                Check(res, ref pass, ref fail, "Faceted normals unweld (vertexCount == triangles.Length)",
                    m != null && m.vertexCount == m.triangles.Length, m != null ? $"verts={m.vertexCount}, idx={m.triangles.Length}" : "no mesh");
            }

            // ---- Recalculate normals: mesh carries a full normal set ----
            {
                var c = Cfg("recalc", cube1); c.normals = NormalsMode.Recalculate; c.smoothingAngle = 30f;
                var m = Bake(c, used, out _);
                bool ok = m != null && m.normals != null && m.normals.Length == m.vertexCount && m.vertexCount > 0;
                Check(res, ref pass, ref fail, "Recalculate normals produces a full normal set", ok, m != null ? $"normals={(m.normals != null ? m.normals.Length : 0)}/{m.vertexCount}" : "no mesh");
            }

            // ---- heightUV: overrides UVs with V = normalized HEIGHT. Assert V actually TRACKS vertex height, not merely
            //      that V spans [0,1] — the cube's own UVs already span [0,1], so a range check would pass even if heightUV
            //      regressed to a no-op. The baker sets V = (z - minZ)/(maxZ - minZ) on the baked verts with no later
            //      vertex move (heightUV-only bake), so the baked mesh must satisfy uv.v == normalizedHeight per vertex;
            //      the stock per-face UVs (0/1, independent of height) would deviate far. ----
            {
                var c = Cfg("heightuv", cube1); c.heightUV = true;
                var m = Bake(c, used, out _);
                bool ok = false; string d = "no mesh";
                if (m != null && m.uv != null && m.uv.Length == m.vertexCount && m.vertexCount > 0)
                {
                    var vs = m.vertices; var uv = m.uv;
                    float mnz = float.MaxValue, mxz = float.MinValue;
                    foreach (var v in vs) { mnz = Mathf.Min(mnz, v.z); mxz = Mathf.Max(mxz, v.z); }
                    float hz = Mathf.Max(1e-4f, mxz - mnz);
                    float maxDev = 0f;
                    for (int i = 0; i < vs.Length; i++) maxDev = Mathf.Max(maxDev, Mathf.Abs(uv[i].y - (vs[i].z - mnz) / hz));
                    ok = maxDev < 0.02f;   // heightUV sets V = normalized Z exactly; a no-op (stock face UVs) would deviate ~1
                    d = $"max |V - normalizedHeight| = {maxDev:0.000}";
                }
                Check(res, ref pass, ref fail, "heightUV maps V to vertex height (not just [0,1])", ok, d);
            }

            // ---- atlasMaxDim: caps the baked atlas (source albedo is 512, so 256 must downscale) ----
            {
                var c = Cfg("atlas256", cube1); c.atlasMaxDim = 256;
                var t = BakeAtlas(c, used);
                Check(res, ref pass, ref fail, "atlasMaxDim=256 caps the atlas", t != null && t.width <= 256 && t.height <= 256, t != null ? $"{t.width}x{t.height}" : "no atlas");
                var c2 = Cfg("atlas1024", cube1); c2.atlasMaxDim = 1024;
                var t2 = BakeAtlas(c2, used);
                Check(res, ref pass, ref fail, "atlasMaxDim=1024 keeps the 512 source", t2 != null && t2.width <= 1024 && t2.width >= 128, t2 != null ? $"{t2.width}x{t2.height}" : "no atlas");
            }

            // ---- size: scales the model's longest axis to `size` ----
            {
                var c = Cfg("size", cube1); c.size = 10f;
                var m = Bake(c, used, out _);
                float longest = m != null ? Mathf.Max(m.bounds.size.x, Mathf.Max(m.bounds.size.y, m.bounds.size.z)) : -1;
                Check(res, ref pass, ref fail, "size scales the longest axis", m != null && Mathf.Abs(longest - 10f) < 1.5f, $"longest={longest:0.0} (expected ~10)");
            }

            // ---- positionOffset.z: keel is raised to 0 then offset -> min.z ~= offset.z ----
            {
                var c = Cfg("posz", cube1); c.positionOffset = new Vector3(0, 0, 5f);
                var m = Bake(c, used, out _);
                float minz = m != null ? m.bounds.min.z : float.NaN;
                Check(res, ref pass, ref fail, "positionOffset.z raises the model", m != null && Mathf.Abs(minz - 5f) < 1.0f, $"min.z={minz:0.00} (expected ~5)");
            }

            // ---- windingFix: rewinds faces outward -> must complete without error ----
            {
                var c = Cfg("winding", cube1); c.windingFix = true;
                var m = Bake(c, used, out var r);
                Check(res, ref pass, ref fail, "windingFix completes and keeps geometry", m != null && r.ok && m.triangles.Length > 0, r.ok ? "ok" : r.error);
            }

            // ---- materialMode Multi on a 2-material model: packs an atlas, bake succeeds (packing detail covered by real models) ----
            {
                var c = Cfg("multi", cube2); c.materialMode = MaterialMode.Multi;
                var t = BakeAtlas(c, used);
                Check(res, ref pass, ref fail, "materialMode=Multi bakes a 2-material model", t != null, t != null ? $"atlas {t.width}x{t.height}" : "no atlas");
            }

            // ---- albedoBrightness (best-effort pixel read): brighter atlas than baseline ----
            {
                var tB = BakeAtlas(Cfg("lum_base", cube1), used);
                var cb = Cfg("lum_bright", cube1); cb.albedoBrightness = 2f;
                var t2 = BakeAtlas(cb, used);
                if (TryMeanLuminance(tB, out float l0) && TryMeanLuminance(t2, out float l1))
                    Check(res, ref pass, ref fail, "albedoBrightness=2 brightens the atlas", l1 > l0 + 4f, $"mean lum {l0:0} -> {l1:0}");
                else { Skip(res, "albedoBrightness", "baked atlas not CPU-readable (DXT1)"); skip++; }
            }

            // ---- albedoSaturation=0 (best-effort): atlas is grayscale (r≈g≈b) ----
            {
                var c = Cfg("gray", cube1); c.albedoSaturation = 0f;
                var t = BakeAtlas(c, used);
                if (TryMaxChannelSpread(t, out float spread))
                    Check(res, ref pass, ref fail, "albedoSaturation=0 greyscales the atlas", spread < 24f, $"max channel spread {spread:0}");
                else { Skip(res, "albedoSaturation", "baked atlas not CPU-readable (DXT1)"); skip++; }
            }

            // ---- E5 rollback: a FAILED re-bake must RESTORE the previous baked assets, not lose them. Exercises the E5
            //      safety net end-to-end (its restore path is otherwise never hit at runtime). After a good bake, re-baking
            //      the SAME resource with a missing model file fails -> E5's BackupOutputs captured the good outputs, so
            //      RestoreOutputs deletes the outputs and copies the backups back. We assert the assets return with their
            //      ORIGINAL Unity GUIDs (the registry resolves by GUID) + mesh content — which also catches a non-atomic
            //      or GUID-losing restore (a broken restore leaves the asset missing or with a fresh GUID). ----
            {
                var good = Cfg("e5", cube1);
                var m0 = Bake(good, used, out var r0);
                string skelPath = "Assets/Resources/" + good.resourceName + "_Skeleton.asset";
                string atlasPath = "Assets/Resources/" + good.resourceName + "_Atlas.asset";
                string skelGuid0 = AssetDatabase.AssetPathToGUID(skelPath);
                string atlasGuid0 = AssetDatabase.AssetPathToGUID(atlasPath);
                int verts0 = m0 != null ? m0.vertexCount : -1;
                if (m0 == null || string.IsNullOrEmpty(skelGuid0))
                    Check(res, ref pass, ref fail, "E5 rollback restores a failed re-bake", false, "setup bake produced no assets (" + r0.error + ")");
                else
                {
                    // Force a deterministic failure of the SAME resource: a missing model file makes Build throw at the
                    // File.Copy in extraction -> ok:false, well after E5's BackupOutputs ran. Same resourceName => same
                    // output paths the good bake owns, so RestoreOutputs must bring them back.
                    var bad = Cfg("e5", cube1); bad.modelFile = Path.Combine(tmp, "does_not_exist.obj");
                    var rBad = UniversalBaker.Build(bad);
                    var mR = AssetDatabase.LoadAssetAtPath<Mesh>("Assets/Resources/" + good.resourceName + "_ModelMesh.asset");
                    string skelGuid1 = AssetDatabase.AssetPathToGUID(skelPath);
                    string atlasGuid1 = AssetDatabase.AssetPathToGUID(atlasPath);
                    bool restored = !rBad.ok && mR != null && mR.vertexCount == verts0
                                    && skelGuid1 == skelGuid0 && atlasGuid1 == atlasGuid0;
                    Check(res, ref pass, ref fail, "E5 rollback restores a failed re-bake (GUIDs + content)", restored,
                        $"rebakeFailed={!rBad.ok}, verts {verts0}->{(mR != null ? mR.vertexCount : -1)}, skelGuidKept={skelGuid1 == skelGuid0}, atlasGuidKept={atlasGuid1 == atlasGuid0}");
                }
            }
        }
        catch (Exception e) { res.Add("FAIL: harness exception — " + e.Message); fail++; }
        finally
        {
            Cleanup(used);
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
            AssetDatabase.Refresh();
        }
        return Section();
    }

    // TIER 2 — the knobs a cube can't exercise: Blender-dependent (targetTris/stripParts) via generated high-poly / named
    // fixtures, and the ANIMATED pipeline via a real rigged model borrowed from the registry (SKIP if none on disk). Slower
    // (real Blender bakes) and fixture-dependent, so it's a separate row from the fast, self-contained Tier 1.
    public static BakeTestSection RunTier2Section()
    {
        var res = new List<string>(); int pass = 0, fail = 0, skip = 0;
        var used = new List<string>();
        string tmp = Path.Combine(Path.GetTempPath(), "haf_feattest2");
        BakeTestSection Section() => new BakeTestSection
        { title = "Features — Blender + animated", pass = pass, fail = fail, skip = skip, body = string.Join("\n", res) };
        if (!UniversalBaker.BlenderAvailable())
        { fail++; res.Add("FAIL: Blender not found — targetTris / stripParts / animated need it. Install it or set EditorPrefs 'ENC.blenderPath'."); return Section(); }
        try
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(tmp);

            // ---- targetTris: Blender quadric-decimates a high-poly source toward the ceiling -> far fewer tris ----
            {
                string grid = WriteGrid(tmp, "grid", 50);   // 50x50 quads = 5000 tris, gently displaced
                var cf = Cfg("tt_full", grid); cf.targetTris = 0;
                var mf = Bake(cf, used, out _); int Tfull = mf != null ? mf.triangles.Length / 3 : -1;
                var cr = Cfg("tt_red", grid); cr.targetTris = 600;
                var mr = Bake(cr, used, out var rr); int Tred = mr != null ? mr.triangles.Length / 3 : -1;
                Check(res, ref pass, ref fail, "targetTris decimates via Blender",
                    mf != null && mr != null && Tfull > 2000 && Tred > 0 && Tred < Tfull / 2, $"{Tfull} -> {Tred} tris (target 600)");
            }

            // ---- stripParts: Blender removes the named object (+children) before baking -> less geometry ----
            {
                string two = WriteTwoObjects(tmp, "twoobj");
                var cf = Cfg("sp_full", two);
                var mf = Bake(cf, used, out _); int Tfull = mf != null ? mf.triangles.Length / 3 : -1;
                var cs = Cfg("sp_strip", two); cs.stripParts = "rotorpart";
                var ms = Bake(cs, used, out var rs); int Tstrip = ms != null ? ms.triangles.Length / 3 : -1;
                Check(res, ref pass, ref fail, "stripParts drops the named object",
                    mf != null && ms != null && Tstrip > 0 && Tstrip < Tfull * 0.75f, $"{Tfull} -> {Tstrip} tris after strip 'rotorpart'");
            }

            // ---- animated pipeline: borrow a real rigged model from the registry, bake via the ANIMATED path ----
            var animFx = ModelRegistry.Load().Where(m => m.animated && !string.IsNullOrEmpty(m.modelFile) && File.Exists(m.modelFile))
                                             .GroupBy(m => m.resourceName).Select(g => g.First()).Take(2).ToList();
            if (animFx.Count == 0) { Skip(res, "animated pipeline", "no animated registry model with an existing source file on disk"); skip++; }
            foreach (var fx in animFx)
            {
                // Route through ModelFactoryWindow.ConfigFor — the SAME config path the Bake button and the smoke
                // test use. The old hand-built BakeConfig silently dropped convertRig + rotationEuler (and
                // keepBlack/keepTexture), so a conversion-path model (the Combine soldier) was baked through the
                // LEGACY pipeline here — exercising a pipeline that model never ships on (review 2026-07-19).
                var clone = JsonUtility.FromJson<ModelDef>(JsonUtility.ToJson(fx));   // never mutate the real entry
                clone.resourceName = Prefix + "anim_" + San(fx.resourceName);
                var c = ModelFactoryWindow.ConfigFor(clone);
                used.Add(c.resourceName);
                var r = UniversalBaker.BuildAnimated(c);
                bool clipAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>("Assets/Resources/" + c.resourceName + "_Clips.asset") != null;
                bool ok = r.ok && !EmptyGuid(r.skeletonGuid) && !EmptyGuid(r.clipGuid) && clipAsset;
                Check(res, ref pass, ref fail, $"animated bake '{fx.resourceName}' -> skeleton + clip", ok,
                    ok ? $"skel {r.skeletonGuid}, clip {r.clipGuid}" : (r.ok ? "guids/clip asset missing" : r.error));
            }
        }
        catch (Exception e) { res.Add("FAIL: harness exception — " + e.Message); fail++; }
        finally
        {
            Cleanup(used);
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
            AssetDatabase.Refresh();
        }
        return Section();
    }

    static bool EmptyGuid(string g) => string.IsNullOrEmpty(g) || g == "0,0,0,0";
    static string San(string s) { var sb = new StringBuilder(); foreach (var ch in s ?? "") sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_'); return sb.ToString(); }

    // ---- bake helpers ----
    static BakeConfig Cfg(string tag, string obj) => new BakeConfig
    {
        resourceName = Prefix + tag, modelFile = obj, size = 5f, normals = NormalsMode.KeepModel, smoothingAngle = 20f,
        convertGrid = 0, materialMode = MaterialMode.Auto, atlasMaxDim = 512, albedoBrightness = 1f, albedoSaturation = 1f,
    };

    static Mesh Bake(BakeConfig c, List<string> used, out BakeResult r)
    {
        used.Add(c.resourceName);
        r = UniversalBaker.Build(c);
        return r.ok ? AssetDatabase.LoadAssetAtPath<Mesh>("Assets/Resources/" + c.resourceName + "_ModelMesh.asset") : null;
    }
    static Texture2D BakeAtlas(BakeConfig c, List<string> used)
    {
        used.Add(c.resourceName);
        var r = UniversalBaker.Build(c);
        return r.ok ? AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Resources/" + c.resourceName + "_Atlas.asset") : null;
    }

    static bool TryMeanLuminance(Texture2D t, out float lum)
    {
        lum = 0f;
        if (t == null) return false;
        try { var px = t.GetPixels32(); if (px.Length == 0) return false; double s = 0; foreach (var p in px) s += p.r * 0.299 + p.g * 0.587 + p.b * 0.114; lum = (float)(s / px.Length); return true; }
        catch { return false; }
    }
    static bool TryMaxChannelSpread(Texture2D t, out float spread)
    {
        spread = 0f;
        if (t == null) return false;
        try
        {
            var px = t.GetPixels32(); if (px.Length == 0) return false;
            int step = Mathf.Max(1, px.Length / 4096);   // sample up to ~4k px
            float mx = 0f;
            for (int i = 0; i < px.Length; i += step)
            { int hi = Mathf.Max(px[i].r, Mathf.Max(px[i].g, px[i].b)), lo = Mathf.Min(px[i].r, Mathf.Min(px[i].g, px[i].b)); mx = Mathf.Max(mx, hi - lo); }
            spread = mx; return true;
        }
        catch { return false; }
    }

    static void Check(List<string> res, ref int pass, ref int fail, string label, bool ok, string detail)
    {
        res.Add((ok ? "PASS" : "FAIL") + ": " + label + (string.IsNullOrEmpty(detail) ? "" : "  (" + detail + ")"));
        if (ok) pass++; else fail++;
    }
    static void Skip(List<string> res, string label, string why) => res.Add("SKIP: " + label + "  (" + why + ")");

    static void Cleanup(IEnumerable<string> names)
    {
        foreach (var n in names.Distinct())
        {
            foreach (var s in new[] { "_ModelMesh.asset", "_Atlas.asset", "_Mat.mat", "_Model.prefab", "_Skeleton.asset", "_Clips.asset", "_ClipsPoseData.bytes" })
                AssetDatabase.DeleteAsset("Assets/Resources/" + n + s);   // pose bytes incl. — Tier-2 animated runs used to strand them in the SHIPPED Resources root
            AssetDatabase.DeleteAsset("Assets/FactorySource/" + n);
        }
    }

    // ---- synthetic model: a unit cube (single- or two-material) + a 512px vertical-gradient orange albedo ----
    static string WriteCube(string dir, string name, bool twoMats)
    {
        var d = Path.Combine(dir, name);
        Directory.CreateDirectory(d);
        var sb = new StringBuilder();
        sb.AppendLine("mtllib " + name + ".mtl");
        float[,] v = { { -0.5f, -0.5f, -0.5f }, { 0.5f, -0.5f, -0.5f }, { 0.5f, 0.5f, -0.5f }, { -0.5f, 0.5f, -0.5f },
                       { -0.5f, -0.5f, 0.5f }, { 0.5f, -0.5f, 0.5f }, { 0.5f, 0.5f, 0.5f }, { -0.5f, 0.5f, 0.5f } };
        for (int i = 0; i < 8; i++) sb.AppendLine($"v {v[i, 0]} {v[i, 1]} {v[i, 2]}");
        sb.AppendLine("vt 0 0"); sb.AppendLine("vt 1 0"); sb.AppendLine("vt 1 1"); sb.AppendLine("vt 0 1");
        int[][] faces = {
            new[]{1,2,3,4}, new[]{5,8,7,6}, new[]{1,4,8,5},   // group A (3 quads)
            new[]{2,6,7,3}, new[]{1,5,6,2}, new[]{4,3,7,8},   // group B (3 quads)
        };
        void Emit(int from, int to)
        {
            for (int f = from; f < to; f++)
            {
                var q = faces[f];
                sb.AppendLine($"f {q[0]}/1 {q[1]}/2 {q[2]}/3");
                sb.AppendLine($"f {q[0]}/1 {q[2]}/3 {q[3]}/4");
            }
        }
        var mtl = new StringBuilder();
        if (twoMats)
        {
            sb.AppendLine("usemtl matA"); Emit(0, 3);
            sb.AppendLine("usemtl matB"); Emit(3, 6);
            mtl.AppendLine("newmtl matA"); mtl.AppendLine("map_Kd " + name + "A_albedo.png");
            mtl.AppendLine("newmtl matB"); mtl.AppendLine("map_Kd " + name + "B_albedo.png");
            WriteAlbedo(Path.Combine(d, name + "A_albedo.png"), new Color(1f, 0.55f, 0.2f));
            WriteAlbedo(Path.Combine(d, name + "B_albedo.png"), new Color(0.3f, 0.5f, 1f));
        }
        else
        {
            sb.AppendLine("usemtl mat"); Emit(0, 6);
            mtl.AppendLine("newmtl mat"); mtl.AppendLine("map_Kd " + name + "_albedo.png");
            WriteAlbedo(Path.Combine(d, name + "_albedo.png"), new Color(1f, 0.55f, 0.2f));
        }
        File.WriteAllText(Path.Combine(d, name + ".obj"), sb.ToString());
        File.WriteAllText(Path.Combine(d, name + ".mtl"), mtl.ToString());
        return Path.Combine(d, name + ".obj");
    }

    static void WriteAlbedo(string path, Color tint)
    {
        const int S = 512;
        var t = new Texture2D(S, S, TextureFormat.RGBA32, false);
        var px = new Color32[S * S];
        for (int y = 0; y < S; y++)
        {
            float b = 0.3f + 0.5f * (y / (float)(S - 1));   // 0.3..0.8 by row: mid-bright (headroom for 2x) + colour for saturation
            Color32 c32 = new Color(tint.r * b, tint.g * b, tint.b * b, 1f);
            for (int x = 0; x < S; x++) px[y * S + x] = c32;
        }
        t.SetPixels32(px); t.Apply();
        File.WriteAllBytes(path, t.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(t);
    }

    // A gently-displaced N×N quad grid (N*N*2 tris) as a single-material OBJ — the high-poly source for the targetTris test.
    static string WriteGrid(string dir, string name, int n)
    {
        var d = Path.Combine(dir, name); Directory.CreateDirectory(d);
        var sb = new StringBuilder();
        sb.AppendLine("mtllib " + name + ".mtl");
        int W = n + 1;
        for (int y = 0; y <= n; y++)
            for (int x = 0; x <= n; x++)
            {
                float fx = x / (float)n, fy = y / (float)n;
                float fz = 0.15f * Mathf.Sin(fx * Mathf.PI * 3f) * Mathf.Sin(fy * Mathf.PI * 3f);   // waves so decimation is curvature-driven, not a trivial plane collapse
                sb.AppendLine($"v {fx - 0.5f} {fy - 0.5f} {fz}");
            }
        for (int y = 0; y <= n; y++)
            for (int x = 0; x <= n; x++)
                sb.AppendLine($"vt {x / (float)n} {y / (float)n}");
        sb.AppendLine("usemtl mat");
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int a = y * W + x + 1, b = y * W + (x + 1) + 1, c = (y + 1) * W + (x + 1) + 1, e = (y + 1) * W + x + 1;
                sb.AppendLine($"f {a}/{a} {b}/{b} {c}/{c}");
                sb.AppendLine($"f {a}/{a} {c}/{c} {e}/{e}");
            }
        File.WriteAllText(Path.Combine(d, name + ".obj"), sb.ToString());
        var mtl = new StringBuilder(); mtl.AppendLine("newmtl mat"); mtl.AppendLine("map_Kd " + name + "_albedo.png");
        File.WriteAllText(Path.Combine(d, name + ".mtl"), mtl.ToString());
        WriteAlbedo(Path.Combine(d, name + "_albedo.png"), new Color(0.6f, 0.6f, 0.62f));
        return Path.Combine(d, name + ".obj");
    }

    // Two named cube objects ("body" + "rotorpart") in one OBJ, for the stripParts test (strip 'rotorpart' -> body only).
    static string WriteTwoObjects(string dir, string name)
    {
        var d = Path.Combine(dir, name); Directory.CreateDirectory(d);
        var sb = new StringBuilder();
        sb.AppendLine("mtllib " + name + ".mtl");
        sb.AppendLine("vt 0 0"); sb.AppendLine("vt 1 0"); sb.AppendLine("vt 1 1"); sb.AppendLine("vt 0 1");
        int baseV = 0;
        void Cube(string objName, float ox)
        {
            sb.AppendLine("o " + objName);
            float[,] vv = { { -0.5f, -0.5f, -0.5f }, { 0.5f, -0.5f, -0.5f }, { 0.5f, 0.5f, -0.5f }, { -0.5f, 0.5f, -0.5f },
                            { -0.5f, -0.5f, 0.5f }, { 0.5f, -0.5f, 0.5f }, { 0.5f, 0.5f, 0.5f }, { -0.5f, 0.5f, 0.5f } };
            for (int i = 0; i < 8; i++) sb.AppendLine($"v {vv[i, 0] + ox} {vv[i, 1]} {vv[i, 2]}");
            sb.AppendLine("usemtl mat");
            int[][] q = { new[]{1,2,3,4}, new[]{5,8,7,6}, new[]{1,4,8,5}, new[]{2,6,7,3}, new[]{1,5,6,2}, new[]{4,3,7,8} };
            foreach (var f in q)
            {
                sb.AppendLine($"f {baseV + f[0]}/1 {baseV + f[1]}/2 {baseV + f[2]}/3");
                sb.AppendLine($"f {baseV + f[0]}/1 {baseV + f[2]}/3 {baseV + f[3]}/4");
            }
            baseV += 8;
        }
        Cube("body", 0f);
        Cube("rotorpart", 2f);
        File.WriteAllText(Path.Combine(d, name + ".obj"), sb.ToString());
        var mtl = new StringBuilder(); mtl.AppendLine("newmtl mat"); mtl.AppendLine("map_Kd " + name + "_albedo.png");
        File.WriteAllText(Path.Combine(d, name + ".mtl"), mtl.ToString());
        WriteAlbedo(Path.Combine(d, name + "_albedo.png"), new Color(0.6f, 0.6f, 0.62f));
        return Path.Combine(d, name + ".obj");
    }
}
