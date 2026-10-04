// Run with Unity -batchmode -nographics -executeMethod VehicleProbePreviewHeadlessTest.Run.
// These invariants need Unity's actual Mesh/GameObject runtime; the kernel's arithmetic is covered by xUnit.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static class VehicleProbePreviewHeadlessTest
{
    // The Protected Cruiser probe crashed in Unity's Mono weak-table cache (2026-10-04). Exercise the replacement
    // under collection between stages, and verify that a second probe reads edited indices instead of stale vertices.
    static void CheckProbeCache()
    {
        var model = new HafModel(); var mesh = new HafMesh();
        var primitive = new HafPrimitive { VertexCount = 6,
            Positions = new float[] { 0,0,0, 1,0,0, 0,1,0, 10,0,0, 11,0,0, 10,1,0 }, Indices = new[] { 0,1,2 } };
        mesh.Primitives.Add(primitive); model.Meshes.Add(mesh);
        model.Nodes.Add(new HafNode { Name = "First", Mesh = 0 });
        model.Nodes.Add(new HafNode { Name = "Second", Mesh = 0 });
        for (int run = 0; run < 4; run++)
        {
            bool moved = (run & 1) != 0;
            primitive.Indices = moved ? new[] { 3,4,5 } : new[] { 0,1,2 };
            var result = VehicleProbe.Run(new VehicleProbe.Input { Model = model, Progress = _ =>
            { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); } });
            Require(result.Parts.Count == 2 && result.Parts.All(p => p.Verts == 3 && p.Center[0] == (moved ? 10.5 : 0.5)),
                "probe cache lifetime under collection");
        }
    }

    public static void Run()
    {
        var assets = new List<UnityEngine.Object>();
        try
        {
            var m = new HafModel(); var mesh = new HafMesh(); const float n = 0.70710677f;
            mesh.Primitives.Add(new HafPrimitive { VertexCount = 3, Material = 0,
                Positions = new float[] { 0,0,0, 1,0,1, 0,1,0 }, Normals = new[] { -n,0,n, -n,0,n, -n,0,n } });
            m.Meshes.Add(mesh); m.Materials.Add(new HafMaterial());
            m.Nodes.Add(new HafNode { Name = "Panel", Mesh = 0, Scale = new double[] { 2,1,1 } });
            var result = VehicleProbe.Run(m);
            var root = VehicleProbePreview.Build(result, new[] { m }, null, assets);
            var built = root.GetComponentInChildren<MeshFilter>().sharedMesh;
            // World vertices measured in Unity from vehicle_rig.py's exported FBX of this same panel.
            Require((built.vertices[1] - new Vector3(-2, 0, 1)).sqrMagnitude < 1e-8f, "FBX preview frame");
            Require((built.vertices[2] - new Vector3(0, 1, 0)).sqrMagnitude < 1e-8f, "preview up axis");
            var tangent = (built.vertices[1] - built.vertices[0]).normalized;
            Require(Mathf.Abs(Vector3.Dot(tangent, built.normals[0])) < 0.0002f, "scaled surface normal");
            var ids = new HashSet<int>(assets.Select(a => a.GetInstanceID()));
            Clear(assets);
            Require(!Resources.FindObjectsOfTypeAll<UnityEngine.Object>().Any(a => ids.Contains(a.GetInstanceID())), "preview asset cleanup");

            CheckProbeCache();
            CheckMaterials(assets);
            CheckGate(assets);
            Clear(assets);

            int rootsBefore = Resources.FindObjectsOfTypeAll<GameObject>().Count(g => g.name == "__vehicleProbePreview");
            m.Materials[0].BaseColorFactor = new float[0]; // inject a failure after the hierarchy and mesh exist
            bool failed = false;
            try { VehicleProbePreview.Build(result, new[] { m }, null, assets); }
            catch (IndexOutOfRangeException) { failed = true; }
            Require(failed, "failure injection");
            Clear(assets); // the same ownership contract used by VehicleLabWindow.ProbeInProcess's catch
            Require(Resources.FindObjectsOfTypeAll<GameObject>().Count(g => g.name == "__vehicleProbePreview") == rootsBefore,
                "partial preview cleanup");
            Debug.Log("PASS — vehicle probe preview: FBX frame, surface normals, material colours and alpha modes, root names, winding gate, complete and partial asset cleanup");
            EditorApplication.Exit(0);
        }
        catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
        finally { Clear(assets); }
    }

    static void CheckMaterials(List<UnityEngine.Object> assets)
    {
        var m = new HafModel();
        var hm = new HafMaterial { BaseColorFactor = new[] { 0.1f, 0.2f, 0.8f, 0.25f }, AlphaMode = "BLEND" };
        m.Materials.Add(hm);
        Material Make(float tint = 1f) => ModelPreview.MaterialFor(m, 0, true, Shader.Find("Standard"),
            new Dictionary<int, Texture2D>(), new Dictionary<int, Material>(), assets, tint);
        var blend = Make();
        // The sRGB encoding of the factor: what Unity's FBX importer made of the Blender probe FBX's linear colours, read off the
        // project's import cache (Library/Artifacts, Salegs Revenge probe: 0.137255 -> 0.4062, 0.8 -> 0.90633) on 2026-10-03.
        Require(Mathf.Abs(blend.color.r - 0.34919f) < 0.00002f && Mathf.Abs(blend.color.g - 0.48453f) < 0.00002f &&
            Mathf.Abs(blend.color.b - 0.90633f) < 0.00002f, "FBX material colour");
        Require(blend.color.a == 0.25f && blend.GetFloat("_Mode") == 3f && blend.renderQueue == 3000 &&
            blend.GetInt("_SrcBlend") == 1 && blend.GetInt("_DstBlend") == 10 && blend.GetInt("_ZWrite") == 0 &&
            blend.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON"), "FBX transparent material");
        var bright = Make(2f);
        Require(Mathf.Abs(bright.color.r - blend.color.r * 2f) < 0.00001f && bright.color.a == blend.color.a,
            "brightness leaves alpha unchanged");
        hm.AlphaMode = "MASK"; hm.AlphaCutoff = 0.35f;
        var mask = Make();
        Require(mask.color.a == 0.25f && mask.GetFloat("_Mode") == 1f && mask.renderQueue == 2450 &&
            mask.GetInt("_ZWrite") == 1 && mask.GetFloat("_Cutoff") == 0.35f &&
            mask.IsKeywordEnabled("_ALPHATEST_ON") && !mask.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON"), "cutout material");
        hm.AlphaMode = "OPAQUE";
        var opaque = Make();
        Require(opaque.color.a == 1f && opaque.GetFloat("_Mode") == 0f && opaque.GetInt("_ZWrite") == 1 &&
            !opaque.IsKeywordEnabled("_ALPHATEST_ON") && !opaque.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON"), "opaque ignores file alpha");
    }

    static void CheckGate(List<UnityEngine.Object> assets)
    {
        var imported = GameObject.CreatePrimitive(PrimitiveType.Cube); assets.Add(imported);
        imported.name = "box_probe";
        var mesh = UnityEngine.Object.Instantiate(imported.GetComponent<MeshFilter>().sharedMesh); assets.Add(mesh);
        mesh.name = "Box"; imported.GetComponent<MeshFilter>().sharedMesh = mesh;
        var fbx = VehicleProbePreviewGateTest.Parts(imported, imported: true);
        Require(fbx.ContainsKey("Box") && !fbx.ContainsKey("box_probe"), "FBX root uses mesh part name");
        Require(VehicleProbePreviewGateTest.Parts(imported).ContainsKey("box_probe"), "in-process object names retained");
        Require(fbx["Box"].facing != 0, "closed box facing judged");
        Require(VehicleProbePreviewGateTest.WithinMismatchTolerance(1, 0), "unchanged single part passes");
        var tri = mesh.triangles;
        for (int i = 0; i < tri.Length; i += 3) { int swap = tri[i + 1]; tri[i + 1] = tri[i + 2]; tri[i + 2] = swap; }
        mesh.triangles = tri;
        var reversed = VehicleProbePreviewGateTest.Parts(imported, imported: true);
        Require(reversed["Box"].facing == -fbx["Box"].facing, "reversed faces detected");
        Require(!VehicleProbePreviewGateTest.WithinMismatchTolerance(1, 1), "one inverted part fails");
        Require(!VehicleProbePreviewGateTest.WithinMismatchTolerance(19, 1), "one mismatch above five percent fails");
        Require(VehicleProbePreviewGateTest.WithinMismatchTolerance(20, 1), "five percent stale parts allowed");
        Require(!VehicleProbePreviewGateTest.WithinMismatchTolerance(20, 2), "more than five percent fails");
        Require(!VehicleProbePreviewGateTest.WithinMismatchTolerance(0, 0), "no matching parts fails");
    }

    static void Require(bool value, string name) { if (!value) throw new InvalidOperationException("Vehicle probe preview: " + name); }
    static void Clear(List<UnityEngine.Object> assets)
    { foreach (var asset in assets) if (asset != null) UnityEngine.Object.DestroyImmediate(asset); assets.Clear(); }
}
