// DistrictFrameHeadlessTest.cs - the district's draw-time frame, as a Bake Tests row (2026-11-02). The static Factory bakes
// every model in the baker's Z-up frame (one frame since 2026-09-12; the unit prefab stands its mesh up with a 270 degree
// X turn), and the game draws a district FxMesh by the import angles the asset carries. The District Factory writes
// DistrictBaker.DrawAngles into that asset: the registry's legacy import angles, then the frame rotation that stands the
// baker's frame up, then Facing. Until this date the window wrote Facing and the legacy angles alone and took the Factory's
// mesh as Y-up, so every district baked after the frame unified drew on its side (the breeder reactor's first bake since
// August). This row bakes a box authored in the baker's frame - 4 across, 2 fore and aft, 1 tall, its floor above the
// origin and its centre off the tile - through the real BakeFxMesh (the leveling, the DistrictMesh and FxMesh assets) with
// the angles the window would write, reads the FxMesh back and measures the box AS THE GAME DRAWS IT (the asset's own
// angles on the stored vertices): it must stand 1 tall on its 4x2 footprint, floor on the tile, centred; Facing 90 turns
// it about the vertical; a legacy roll about the baker's up turns it the same way. Composition also checks a separate
// asymmetric part and its golden-angle copy: their geometry, normals and tangents must stand in the same frame, with
// their own yaw/placement independent of the base's Facing and legacy angles. Throwaway assets under
// BakeSmokeTest.PREFIX, removed afterwards. SKIP without the SDK's FxMesh type (a project without the game's assemblies).
using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

public static class DistrictFrameHeadlessTest
{
    const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    const float Tol = 1e-3f;

    public static BakeTestSection RunSection()
    {
        var s = new BakeTestSection { title = "the district frame: a baker-frame box through BakeFxMesh with the window's draw angles, measured as the game draws it" };
        var body = new System.Text.StringBuilder();
        bool sdk = AppDomain.CurrentDomain.GetAssemblies().Any(a => { try { return a.GetType("Amplitude.Graphics.Fx.FxMesh") != null; } catch { return false; } });
        if (!sdk) { s.skip++; s.body = "SKIP: the SDK's Amplitude.Graphics.Fx.FxMesh type is not loaded - BakeFxMesh has nothing to write"; return s; }
        string name = BakeSmokeTest.PREFIX + "districtframe";
        var box = Box(new Vector3(1f, 2f, 3f), new Vector3(4f, 2f, 1f));   // centre (1, 2, 3), extents 4 across, 2 fore/aft, 1 tall (the baker's z)
        try
        {
            ComposeCase(s, body, box, 0f, Vector3.zero, 0f);
            ComposeCase(s, body, box, 90f, Vector3.zero, 90f);
            ComposeCase(s, body, box, 37f, new Vector3(15f, 20f, 10f), 23f);
            Case(s, body, box, name, 0f, Vector3.zero, new Vector3(4f, 1f, 2f), "Rotation 0, Facing 0: stands 1 tall on its 4x2 footprint");
            Case(s, body, box, name, 90f, Vector3.zero, new Vector3(2f, 1f, 4f), "Facing 90: turned about the vertical, still 1 tall");
            Case(s, body, box, name, 0f, new Vector3(0f, 0f, 90f), new Vector3(2f, 1f, 4f), "a legacy roll about the baker's up (import angles 0,0,90) is a turn, not a tip");
        }
        finally
        {
            foreach (var suffix in new[] { "_FxMesh", "_DistrictMesh" }) AssetDatabase.DeleteAsset("Assets/Resources/" + name + suffix + ".asset");
            UnityEngine.Object.DestroyImmediate(box);
        }
        body.Insert(0, $"{s.pass + s.fail} district frame cases; {s.pass} drawn as the window predicts\n");
        s.body = body.ToString();
        return s;
    }

    // Parts use their own Facing in drawn space. The entry's legacy angles/Facing rotate the base only;
    // converting the composed result back to storage must cancel them for every part and copy.
    static void ComposeCase(BakeTestSection s, System.Text.StringBuilder body, Mesh box, float facing, Vector3 legacy, float partFacing)
    {
        BakeTestRunnerWindow.Progress.ThrowIfCancelled();
        var centre = new Vector3(-4f, 7f, 9f);
        var size = new Vector3(1f, 2f, 3f);
        var part = Box(centre, size);
        var offset = new Vector3(5f, 0.5f, -6f);
        var copy = new Vector3(-4f, 1f, 7f);
        var entryOffset = new Vector3(0.25f, 1f, -0.75f);
        Mesh merged = null;
        Texture2D atlas = null, normal = null, rough = null;
        string label = $"composed part and copy: entry Facing {facing}, legacy {legacy}, part Facing {partFacing}";
        try
        {
            var R = Quaternion.Euler(DistrictBaker.DrawAngles(facing, legacy));
            merged = DistrictBaker.ComposeDistrict(new DistrictBaker.ComposeSource { mesh = box },
                new System.Collections.Generic.List<DistrictBaker.ComposeSource> {
                    new DistrictBaker.ComposeSource { mesh = part, facing = partFacing, posOffset = offset,
                        copies = new System.Collections.Generic.List<Vector3> { copy } }
                }, R, 1024, entryOffset, out atlas, out normal, out rough);
            var vs = merged.vertices; var ns = merged.normals; var ts = merged.tangents;
            var pv = part.vertices; var pn = part.normals; var pt = part.tangents;
            for (int placement = 0; placement < 2; placement++)
            {
                var yaw = Quaternion.Euler(0f, partFacing + (placement == 0 ? 0f : 137.5f), 0f);
                var off = (placement == 0 ? offset : copy) + entryOffset;
                for (int i = 0; i < pv.Length; i++)
                {
                    int at = box.vertexCount + placement * pv.Length + i;
                    // Independent Z-up -> Y-up mapping: (x, y, z) -> (x, z, -y), then ground/centre, yaw and place.
                    var local = new Vector3(pv[i].x - centre.x, pv[i].z - (centre.z - size.z * 0.5f), -(pv[i].y - centre.y));
                    var expected = yaw * local + off;
                    var expectedNormal = yaw * new Vector3(pn[i].x, pn[i].z, -pn[i].y);
                    var expectedTangent = yaw * new Vector3(pt[i].x, pt[i].z, -pt[i].y);
                    if ((R * vs[at] - expected).magnitude > Tol)
                        throw new Exception($"placement {placement} vertex {i}: drawn {R * vs[at]}, expected {expected}");
                    if ((R * ns[at] - expectedNormal).magnitude > Tol)
                        throw new Exception($"placement {placement} normal {i} has the wrong frame");
                    if ((R * new Vector3(ts[at].x, ts[at].y, ts[at].z) - expectedTangent).magnitude > Tol || ts[at].w != pt[i].w)
                        throw new Exception($"placement {placement} tangent {i} has the wrong frame or handedness");
                }
            }
            s.pass++; body.AppendLine($"PASS: {label} - upright, grounded, placed, normals and tangents in the same frame");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { s.fail++; body.AppendLine($"FAIL: {label}: {ex.GetBaseException().GetType().Name}: {ex.GetBaseException().Message}"); }
        finally
        {
            foreach (var o in new UnityEngine.Object[] { merged, part, atlas, normal, rough })
                if (o != null) UnityEngine.Object.DestroyImmediate(o);
        }
    }

    static void Case(BakeTestSection s, System.Text.StringBuilder body, Mesh box, string name, float facing, Vector3 importAngles, Vector3 expect, string label)
    {
        BakeTestRunnerWindow.Progress.ThrowIfCancelled();
        try
        {
            var angles = DistrictBaker.DrawAngles(facing, importAngles);
            string guid = DistrictBaker.BakeFxMesh(box, name, angles, out string path, levelOnGround: true);
            if (guid == null || path == null) { s.fail++; body.AppendLine($"FAIL: {label}: BakeFxMesh wrote nothing (see the console)"); return; }
            var fx = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            if (fx == null) { s.fail++; body.AppendLine($"FAIL: {label}: the FxMesh asset {path} does not load back"); return; }
            var t = fx.GetType();
            var mesh = t.GetField("mesh", BF)?.GetValue(fx) as Mesh;
            var iaField = t.GetField("importAngles", BF);
            if (mesh == null || iaField == null) { s.fail++; body.AppendLine($"FAIL: {label}: the FxMesh has no mesh or importAngles field ({t.FullName})"); return; }
            var ia = (Vector3)iaField.GetValue(fx);
            if ((ia - angles).magnitude > Tol) { s.fail++; body.AppendLine($"FAIL: {label}: the asset carries import angles {ia}, the window wrote {angles}"); return; }
            // the game's draw: the stored vertices under the asset's own angles
            var R = Quaternion.Euler(ia);
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue); var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            foreach (var v in mesh.vertices) { var d = R * v; min = Vector3.Min(min, d); max = Vector3.Max(max, d); }
            var size = max - min; var centre = (min + max) * 0.5f;
            string drawn = $"drawn {size.x:0.###} across, {size.y:0.###} tall, {size.z:0.###} deep; floor y={min.y:0.###}, centre x={centre.x:0.###} z={centre.z:0.###}";
            if ((size - expect).magnitude > Tol) { s.fail++; body.AppendLine($"FAIL: {label}: expected {expect.x}x{expect.y}x{expect.z} (across x tall x deep), {drawn}"); return; }
            if (Mathf.Abs(min.y) > Tol) { s.fail++; body.AppendLine($"FAIL: {label}: the floor is not on the tile - {drawn}"); return; }
            if (Mathf.Abs(centre.x) > Tol || Mathf.Abs(centre.z) > Tol) { s.fail++; body.AppendLine($"FAIL: {label}: the footprint is not centred - {drawn}"); return; }
            s.pass++; body.AppendLine($"PASS: {label} - {drawn}; import angles {ia}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { s.fail++; body.AppendLine($"FAIL: {label}: {ex.GetBaseException().GetType().Name}: {ex.GetBaseException().Message}"); }
    }

    /// <summary>An axis-aligned box: centre c, full size e, 8 vertices, 12 triangles wound outward, normals and a UV layer.</summary>
    static Mesh Box(Vector3 c, Vector3 e)
    {
        var h = e * 0.5f;
        var v = new Vector3[8]; int k = 0;
        for (int x = 0; x < 2; x++) for (int y = 0; y < 2; y++) for (int z = 0; z < 2; z++)
            v[k++] = new Vector3(c.x + (x == 0 ? -h.x : h.x), c.y + (y == 0 ? -h.y : h.y), c.z + (z == 0 ? -h.z : h.z));
        // vertex index = x*4 + y*2 + z
        int[] tris =
        {
            0, 1, 3, 0, 3, 2,   // -x
            4, 6, 7, 4, 7, 5,   // +x
            0, 4, 5, 0, 5, 1,   // -y
            2, 3, 7, 2, 7, 6,   // +y
            0, 2, 6, 0, 6, 4,   // -z
            1, 5, 7, 1, 7, 3,   // +z
        };
        var m = new Mesh { name = "districtframe_box" };
        m.SetVertices(v);
        m.SetTriangles(tris, 0);
        m.SetUVs(0, v.Select(p => new Vector2(p.x, p.y)).ToArray());
        m.SetTangents(v.Select(_ => new Vector4(1f, 0.3f, 0.7f, -1f)).ToArray());
        m.RecalculateNormals();
        m.RecalculateBounds();
        return m;
    }
}
