// Run with Unity -batchmode -nographics -executeMethod VehicleProbePreviewHeadlessTest.Run.
// These invariants need Unity's actual Mesh/GameObject runtime; the kernel's arithmetic is covered by xUnit.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static class VehicleProbePreviewHeadlessTest
{
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

            int rootsBefore = Resources.FindObjectsOfTypeAll<GameObject>().Count(g => g.name == "__vehicleProbePreview");
            m.Materials[0].BaseColorFactor = new float[0]; // inject a failure after the hierarchy and mesh exist
            bool failed = false;
            try { VehicleProbePreview.Build(result, new[] { m }, null, assets); }
            catch (IndexOutOfRangeException) { failed = true; }
            Require(failed, "failure injection");
            Clear(assets); // the same ownership contract used by VehicleLabWindow.ProbeInProcess's catch
            Require(Resources.FindObjectsOfTypeAll<GameObject>().Count(g => g.name == "__vehicleProbePreview") == rootsBefore,
                "partial preview cleanup");
            Debug.Log("PASS — vehicle probe preview: FBX frame, surface normals, complete and partial asset cleanup");
            EditorApplication.Exit(0);
        }
        catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
        finally { Clear(assets); }
    }

    static void Require(bool value, string name) { if (!value) throw new InvalidOperationException("Vehicle probe preview: " + name); }
    static void Clear(List<UnityEngine.Object> assets)
    { foreach (var asset in assets) if (asset != null) UnityEngine.Object.DestroyImmediate(asset); assets.Clear(); }
}
