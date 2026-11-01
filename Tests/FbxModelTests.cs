using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

// Step 6a of the native FBX reader: editor/FbxImport.cs and editor/FbxModel.cs on documents built in memory in the shapes of
// tools/fbx-drill/fbx_model_fixtures.py. The expected values are Blender 5.1's own (its FBX import + glTF export of those
// scenes, read back through GlbReader, 2026-11-01); the bit-level claim on every file is tools/fbx_model_drill.sh's.
public class FbxModelTests
{
    // ---- builders -------------------------------------------------------------------------------------------------
    static FbxNode N(string name, params object[] props) { var n = new FbxNode { Name = name }; n.Properties.AddRange(props); return n; }
    static FbxNode With(FbxNode n, params FbxNode[] children) { n.Children.AddRange(children); return n; }
    static FbxNode P(string name, string kind, string sub, params object[] values) { var p = N("P", name, kind, sub, ""); p.Properties.AddRange(values); return p; }
    static FbxNode V(string name, double x, double y, double z) => P(name, "Vector3D", "Vector", x, y, z);
    static FbxNode Props70(params FbxNode[] ps) => With(N("Properties70"), ps);
    static FbxNode Object(string cls, long id, string name, string sub, params FbxNode[] children) => With(N(cls, id, name + "\0\u0001" + cls, sub), children);
    static FbxNode C(long src, long dst) => N("C", "OO", src, dst);
    static FbxNode Model(long id, string name, params FbxNode[] props) => props.Length > 0 ? Object("Model", id, name, "Mesh", Props70(props)) : Object("Model", id, name, "Mesh");
    static FbxNode Material(long id, string name) => Object("Material", id, name, "", N("ShadingModel", "phong"));
    static FbxNode Layer(string kind, string data, string mapping, string reference, object values, string name = "", object index = null, string indexName = null)
    {
        var l = With(N("LayerElement" + kind, 0), N("Name", name), N("MappingInformationType", mapping), N("ReferenceInformationType", reference), N(data, values));
        if (index != null) l.Children.Add(N(indexName, index));
        return l;
    }
    static FbxNode Geometry(long id, string name, double[] vertices, int[][] faces, int[] edges = null, params FbxNode[] layers)
    {
        var idx = new List<int>();
        foreach (var f in faces) { idx.AddRange(f.Take(f.Length - 1)); idx.Add(~f[f.Length - 1]); }
        var children = new List<FbxNode> { N("Vertices", vertices), N("PolygonVertexIndex", idx.ToArray()) };
        if (edges != null) children.Add(N("Edges", edges));
        children.Add(N("GeometryVersion", 124)); children.AddRange(layers);
        return Object("Geometry", id, name, "Mesh", children.ToArray());
    }
    static List<FbxNode> Header(int up = 1, int upSign = 1, int front = 2, int frontSign = 1, int coord = 0, double unit = 1.0)
    {
        var settings = Props70(P("UpAxis", "int", "Integer", up), P("UpAxisSign", "int", "Integer", upSign), P("FrontAxis", "int", "Integer", front), P("FrontAxisSign", "int", "Integer", frontSign),
            P("CoordAxis", "int", "Integer", coord), P("CoordAxisSign", "int", "Integer", 1), P("UnitScaleFactor", "double", "Number", unit));
        var template = With(N("PropertyTemplate", "FbxNode"), Props70(P("Lcl Translation", "Lcl Translation", "", 0.0, 0.0, 0.0), P("Lcl Rotation", "Lcl Rotation", "", 0.0, 0.0, 0.0),
            P("Lcl Scaling", "Lcl Scaling", "", 1.0, 1.0, 1.0), P("RotationActive", "bool", "", 0), P("RotationOrder", "enum", "", 0)));
        return new List<FbxNode> { With(N("FBXHeaderExtension"), N("FBXHeaderVersion", 1003), N("FBXVersion", 7400), N("Creator", "tests")),
            With(N("GlobalSettings"), N("Version", 1000), settings), With(N("Definitions"), With(N("ObjectType", "Model"), template)) };
    }
    static FbxImport.Scene Scene(FbxNode[] objects, FbxNode[] connections, List<FbxNode> header = null)
    {
        var d = new FbxDocument { Version = 7400 };
        d.Nodes.AddRange(header ?? Header());
        d.Nodes.Add(With(N("Objects"), objects)); d.Nodes.Add(With(N("Connections"), connections));
        return FbxImport.Build(FbxScene.Read(d));
    }
    static string Bits(double d) => BitConverter.ToUInt64(BitConverter.GetBytes(d), 0).ToString("x16");
    static string Bits(double[] v) => string.Join(",", v.Select(Bits));
    static readonly double[] CubeV = { -1, -1, -1, 1, -1, -1, 1, 1, -1, -1, 1, -1, -1, -1, 1, 1, -1, 1, 1, 1, 1, -1, 1, 1 };
    static readonly int[][] CubeF = { new[] { 0, 1, 2, 3 }, new[] { 4, 7, 6, 5 }, new[] { 0, 4, 5, 1 }, new[] { 1, 5, 6, 2 }, new[] { 2, 6, 7, 3 }, new[] { 3, 7, 4, 0 } };
    static double[] FaceNormals(double[] v, int[][] faces)
    {
        var o = new List<double>();
        foreach (var f in faces)
        {
            double nx = 0, ny = 0, nz = 0; int prev = f[f.Length - 1];
            foreach (int c in f) { nx += (v[3 * prev + 1] - v[3 * c + 1]) * (v[3 * prev + 2] + v[3 * c + 2]); ny += (v[3 * prev + 2] - v[3 * c + 2]) * (v[3 * prev] + v[3 * c]); nz += (v[3 * prev] - v[3 * c]) * (v[3 * prev + 1] + v[3 * c + 1]); prev = c; }
            double n = Math.Sqrt(nx * nx + ny * ny + nz * nz); if (n == 0) n = 1;
            foreach (int c in f) { o.Add(nx / n); o.Add(ny / n); o.Add(nz / n); }
        }
        return o.ToArray();
    }

    [Fact]
    public void Polygons_of_every_kind_tessellate_and_lay_out_as_blender_writes_them()
    {
        double[] v = { 0, 0, 0, 2, 0, 0, 1, 2, 0, 3, 0, 0, 5, 0, 0, 5, 2, 0, 3, 2, 0, 6, 0, 0, 8, 1, 0, 10, 0, 0, 8, 3, 0, 9, 0, 0, 11, 0, 0, 12, 1.5, 0, 10, 3, 0, 8.5, 1.5, 0, 13, 0, 0, 16, 0, 0, 16, 1, 0, 14, 1, 0, 14, 3, 0, 13, 3, 0 };
        int[][] f = { new[] { 0, 1, 2 }, new[] { 3, 4, 5, 6 }, new[] { 7, 8, 9, 10 }, new[] { 11, 12, 13, 14, 15 }, new[] { 16, 17, 18, 19, 20, 21 } };
        var layers = new[] { Layer("Material", "Materials", "ByPolygon", "IndexToDirect", new[] { 0, 1, 0, 1, 0 }),
            Layer("UV", "UV", "ByPolygonVertex", "IndexToDirect", new[] { 0.0, 0.0, 1.0, 0.0, 1.0, 1.0, 0.0, 1.0, 0.5, 0.5 }, "map1", new[] { 0, 1, 2, 0, 1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3, 4, 0, 1, 2, 3, 4, 1 }, "UVIndex"),
            Layer("Normal", "Normals", "ByPolygonVertex", "Direct", FaceNormals(v, f)), Layer("Smoothing", "Smoothing", "ByPolygon", "Direct", new[] { 0, 1, 0, 0, 0 }) };
        var s = Scene(new[] { Model(1, "Polygons"), Geometry(30, "PolygonsGeo", v, f, null, layers), Material(40, "MatA"), Material(41, "MatB") }, new[] { C(1, 0), C(30, 1), C(40, 1), C(41, 1) });
        Assert.Null(s.Fallback);
        var mesh = s.Meshes[0];
        var tris = FbxModel.Tessellate(mesh);
        Assert.Equal(1 + 2 + 2 + 3 + 4, tris.Count);
        Assert.Equal((3, 4, 5), (tris[1].C0, tris[1].C1, tris[1].C2)); Assert.Equal((3, 5, 6), (tris[2].C0, tris[2].C1, tris[2].C2));   // the convex quad along 0-2
        Assert.Equal((7, 8, 10), (tris[3].C0, tris[3].C1, tris[3].C2)); Assert.Equal((8, 9, 10), (tris[4].C0, tris[4].C1, tris[4].C2));  // the arrowhead quad flips to 1-3
        var m = FbxModel.Build(s);
        Assert.Equal(new[] { "MatA", "MatB" }, m.Materials.Select(x => x.Name).ToArray());
        Assert.Equal(2, m.Meshes[0].Primitives.Count);
        Assert.Equal((0, 13, 21), (m.Meshes[0].Primitives[0].Material, m.Meshes[0].Primitives[0].VertexCount, m.Meshes[0].Primitives[0].Indices.Length));   // slot 0: the triangle, the folded quad, the L
        Assert.Equal((1, 9, 15), (m.Meshes[0].Primitives[1].Material, m.Meshes[0].Primitives[1].VertexCount, m.Meshes[0].Primitives[1].Indices.Length));    // slot 1: the quad and the pentagon
        Assert.NotNull(m.Meshes[0].Primitives[0].Uv0);
        Assert.Equal("3fe6a09e80000000,0000000000000000,0000000000000000,3fe6a09e80000000", Bits(m.Nodes[0].Rotation));   // the Y-up file turned Z-up and back: +90 about X
        Assert.Equal("3f847ae140000000", Bits(m.Nodes[0].Scale[0]));   // centimetres: 0.01
    }

    [Fact]
    public void Transforms_pivots_orders_pre_post_rotations_and_the_euler_round_trip()
    {
        var cube = Geometry(30, "CubeGeo", CubeV, CubeF, null, Layer("Normal", "Normals", "ByPolygonVertex", "Direct", FaceNormals(CubeV, CubeF)));
        var objects = new[] { cube, Material(40, "Mat"),
            Model(1, "Root", V("Lcl Translation", 10, 20, 30), V("Lcl Rotation", 10, 20, 30), V("Lcl Scaling", 1, 2, 3)),
            Model(2, "b_child", V("Lcl Translation", 1, 0, 0), V("Lcl Rotation", 0, 45, 0), V("RotationPivot", 1, 1, 1), V("ScalingPivot", 2, 0, 0), V("RotationOffset", 0, 1, 0), V("ScalingOffset", 0, 0, 1), V("Lcl Scaling", 2, 2, 2)),
            Model(3, "A_child", V("Lcl Rotation", 30, 40, 50), P("RotationActive", "bool", "", 1), V("PreRotation", 90, 0, 0), V("PostRotation", 0, 0, 15), P("RotationOrder", "enum", "", 4)),
            Model(4, "a_child", V("Lcl Rotation", 30, 40, 50), P("RotationActive", "bool", "", 1), P("RotationOrder", "enum", "", 5), V("GeometricTranslation", 0, 5, 0), V("GeometricRotation", 0, 0, 90), V("GeometricScaling", 1, 1, 0.5)),
            Model(5, "grandchild", V("Lcl Translation", 0, 0, 7), V("Lcl Rotation", 30, 40, 50), P("RotationActive", "bool", "", 1), P("RotationOrder", "enum", "", 1)),
            Model(6, "order2", V("Lcl Rotation", 30, 40, 50), P("RotationActive", "bool", "", 1), P("RotationOrder", "enum", "", 2)),
            Model(7, "order3", V("Lcl Rotation", 30, 40, 50), P("RotationActive", "bool", "", 1), P("RotationOrder", "enum", "", 3)),
            Model(8, "inactive_pre", V("Lcl Rotation", 30, 40, 50), V("PreRotation", 90, 0, 0), P("RotationOrder", "enum", "", 5)) };
        var conns = new List<FbxNode> { C(1, 0), C(2, 1), C(3, 1), C(4, 1), C(5, 2), C(6, 0), C(7, 0), C(8, 0) };
        for (long n = 1; n <= 8; n++) { conns.Add(C(30, n)); conns.Add(C(40, n)); }
        var m = FbxModel.Build(Scene(objects, conns.ToArray()));
        // the exporter's order: members before their node, children by BLI_strcasecmp (A_child, a_child, b_child), roots in creation order
        Assert.Equal(new[] { "A_child", "a_child", "grandchild", "b_child", "Root", "order2", "order3", "inactive_pre" }, m.Nodes.Select(n => n.Name).ToArray());
        Assert.Equal(new[] { 0, 1, 3 }, m.Nodes[4].Children.ToArray());
        Assert.Equal("3febafba60000000,3fd37af920000000,bfa3574f60000000,3fd9632680000000", Bits(m.Nodes[0].Rotation));   // pre/post rotation, order ZXY
        Assert.Equal("c007791340000000,400de9c120000000,bff8d66500000000", Bits(m.Nodes[1].Translation));                   // the geometric transform folded into the matrix
        Assert.Equal("3ff0000000000000,3fe0000000000000,3ff0000000000000", Bits(m.Nodes[1].Scale));
        Assert.Equal("bfbf0ec800000000,4008f87600000000,bff0000080000000", Bits(m.Nodes[3].Translation));                   // pivots and offsets
        Assert.Equal("3fffffffc0000000,3fffffffa0000000,4000000000000000", Bits(m.Nodes[3].Scale));                         // a scale of 2 through the Euler round trip: float noise, as written
        Assert.Equal("3fe664db80000000,3fd3658120000000,3fa2193680000000,3fe4ab0ee0000000", Bits(m.Nodes[4].Rotation));   // the root's rotation under the global matrix
        Assert.Equal("3fe549f7a0000000,3fdde38820000000,3fc82f1ec0000000,3fe1a1d360000000", Bits(m.Nodes[5].Rotation));   // order YZX
        Assert.Equal("3fe549f7a0000000,3fdfee32e0000000,bfb1e3b900000000,3fe1a1d380000000", Bits(m.Nodes[7].Rotation));   // PreRotation ignored without RotationActive, ZYX
        Assert.All(m.Nodes, n => Assert.Equal(0, n.Mesh));   // one datablock, one material list: one glTF mesh
        Assert.Equal(24, m.Meshes[0].Primitives[0].VertexCount);   // six flat faces of four
    }

    [Fact]
    public void Names_take_blender_s_pools_and_defaults()
    {
        string longName = new string('L', 70);
        var objects = new[] { Geometry(30, "", CubeV, CubeF), Geometry(31, longName, new double[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, new[] { new[] { 0, 1, 2 } }),
            Model(1, "Cube.001"), Model(2, "Cube"), Model(3, "Cube"), Model(4, longName), Model(5, "Shared"), Model(6, "Twice"), Model(7, "ParentA"), Model(8, "ParentB"), Material(40, "MatA"), Material(41, "MatB") };
        var conns = new[] { C(1, 0), C(2, 0), C(3, 0), C(4, 0), C(5, 0), C(7, 0), C(8, 0), C(6, 7), C(6, 8), C(30, 1), C(30, 2), C(30, 3), C(31, 4), C(30, 5), C(31, 6), C(40, 2), C(41, 5), C(40, 5) };
        var s = Scene(objects, conns);
        var m = FbxModel.Build(s);
        Assert.Equal(new[] { "Cube.001", "Cube", "Cube.002", new string('L', 55) + "_4e0c060", "Shared", "ParentA", "Twice", "ParentB" }, m.Nodes.Select(n => n.Name).ToArray());
        Assert.Equal(7, m.Nodes[6].Parent);   // connected to two parents: the LAST wins
        Assert.Equal(new[] { "Mesh", new string('L', 55) + "_4e0c060", "Mesh" }, m.Meshes.Select(x => x.Name).ToArray());   // an empty name is "Mesh"; a shared datablock with other slots is another glTF mesh
        Assert.Equal(new[] { 0, 0, 0, 1, 2, -1, 1, -1 }, m.Nodes.Select(n => n.Mesh).ToArray());
        Assert.Equal((0, 1), (m.Meshes[0].Primitives[0].Material, m.Meshes[2].Primitives[0].Material));   // Cube's MatA; Shared's slots MatB, MatA with every face in slot 0
        Assert.Equal(-1, m.Meshes[1].Primitives[0].Material);
    }

    [Fact]
    public void Degenerate_polygons_go_and_an_empty_mesh_leaves_its_node_without_one()
    {
        double[] v = { 0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0, 2, 0, 0 };
        int[][] f = { new[] { 0, 1, 2, 3 }, new[] { 0, 1, 1, 2 }, new[] { 3, 2, 1, 0 }, new[] { 1, 4 }, new[] { 1, 4, 2 } };
        var layers = new[] { Layer("Normal", "Normals", "AllSame", "IndexToDirect", new[] { 0.0, 0.0, 1.0 }), Layer("Material", "Materials", "AllSame", "IndexToDirect", new[] { 0 }),
            Layer("UV", "UV", "ByPolygonVertex", "IndexToDirect", new[] { 0.25, 0.75, 1.0, 0.0 }, "", new[] { 0, 1, 0, 1, 0, 1, 1, 0, 0, 1, 0, 1, 0, 1, 9, 0, 1 }, "UVIndex") };
        var s = Scene(new[] { Model(1, "Degenerate"), Geometry(30, "Deg", v, f, null, layers), Model(2, "NoFaces"), Geometry(31, "Points", new double[] { 0, 0, 0, 1, 1, 1 }, new int[0][]), Material(40, "Mat") },
            new[] { C(1, 0), C(2, 0), C(30, 1), C(31, 2), C(40, 1) });
        Assert.Equal(2, s.Meshes[0].FaceCount);   // the quad and the last triangle: a repeated vertex, a reversed twin and a two-corner polygon went
        Assert.Contains(s.Notes, n => n.Contains("indices out of bounds"));
        var m = FbxModel.Build(s);
        Assert.Equal((0, -1), (m.Nodes[0].Mesh, m.Nodes[1].Mesh));
        Assert.Equal((7, 9), (m.Meshes[0].Primitives[0].VertexCount, m.Meshes[0].Primitives[0].Indices.Length));
    }

    [Fact]
    public void Edges_and_by_edge_smoothing_make_sharp_edges()
    {
        var vnorm = new List<double>();
        for (int i = 0; i < 8; i++) { double n = Math.Sqrt(3.0); vnorm.Add(CubeV[3 * i] / n); vnorm.Add(CubeV[3 * i + 1] / n); vnorm.Add(CubeV[3 * i + 2] / n); }
        var layers = new[] { Layer("Smoothing", "Smoothing", "ByEdge", "Direct", new[] { 1, 0, 1, 1, 1, 1, 0, 1, 1, 1, 1, 1 }), Layer("Normal", "Normals", "ByVertice", "Direct", vnorm.ToArray()),
            Layer("UV", "UV", "ByVertice", "Direct", Enumerable.Range(0, 16).Select(i => 0.1 * i).ToArray(), "byvert") };
        var s = Scene(new[] { Model(1, "Cube"), Geometry(30, "CubeGeo", CubeV, CubeF, Enumerable.Range(0, 12).ToArray(), layers), Material(40, "Mat") }, new[] { C(1, 0), C(30, 1), C(40, 1) });
        var mesh = s.Meshes[0];
        Assert.NotNull(mesh.SharpEdges);
        Assert.Contains(VehicleProbe.SharpEdgeKey(1, 2), mesh.SharpEdges);   // the second edge of the first face (corner 1 -> 2)
        Assert.Contains(VehicleProbe.SharpEdgeKey(6, 5), mesh.SharpEdges);   // the seventh entry: corner 6 of face 1 (vertex 6) to corner 7 (vertex 5)
        Assert.Equal(2, mesh.SharpEdges.Count);
        Assert.NotNull(mesh.CustomNormal);
        var m = FbxModel.Build(s);
        Assert.Equal(8, m.Meshes[0].Primitives[0].VertexCount);   // normals and UVs per VERTEX: the sharp edges split the fans, but every corner of a vertex decodes the same normal - eight dots (Blender wrote 8)
    }

    [Fact]
    public void Axes_and_units_come_from_the_global_settings()
    {
        var tri = Geometry(30, "Tri", new double[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, new[] { new[] { 0, 1, 2 } }, null, Layer("Normal", "Normals", "ByPolygonVertex", "Direct", new[] { 0.0, 0, 1, 0, 0, 1, 0, 0, 1 }));
        var zup = FbxModel.Build(Scene(new[] { Model(1, "Tri", V("Lcl Translation", 1, 2, 3), V("Lcl Rotation", 0, 0, 30)), tri }, new[] { C(1, 0), C(30, 1) }, Header(up: 2, upSign: 1, front: 1, frontSign: -1, coord: 0, unit: 100.0)));
        Assert.Null(zup.Nodes[0].Scale.Length == 3 && zup.Nodes[0].Scale[0] == 1 && zup.Nodes[0].Scale[1] == 1 && zup.Nodes[0].Scale[2] == 1 ? null : "scale");   // metres: no scale written
        Assert.Equal((1.0, 3.0, -2.0), (zup.Nodes[0].Translation[0], zup.Nodes[0].Translation[1], zup.Nodes[0].Translation[2]));   // Z up in, Y up out
        var inches = FbxModel.Build(Scene(new[] { Model(1, "Tri", V("Lcl Translation", 1, 2, 3)), tri }, new[] { C(1, 0), C(30, 1) }, Header(unit: 2.54)));
        Assert.Equal((float)0.0254, (float)inches.Nodes[0].Scale[0]);
    }

    [Fact]
    public void Files_outside_the_sub_step_are_named_as_blender_s()
    {
        var bones = Scene(new[] { Model(1, "A"), Object("Model", 2, "Bone", "LimbNode") }, new[] { C(1, 0), C(2, 1) });
        Assert.StartsWith("bones", bones.Fallback);
        var anim = Scene(new[] { Model(1, "A"), Object("AnimationStack", 60, "Take", ""), Object("AnimationLayer", 61, "Base", ""), Object("AnimationCurveNode", 70, "T", "") }, new[] { C(1, 0), C(61, 60), C(70, 61) });
        Assert.StartsWith("animation", anim.Fallback);
        var emptyStack = Scene(new[] { Model(1, "A"), Object("AnimationStack", 60, "Take", ""), Object("AnimationLayer", 61, "Base", "") }, new[] { C(1, 0), C(61, 60) });
        Assert.Null(emptyStack.Fallback);   // a stack without curves makes no action
        var colours = Scene(new[] { Model(1, "A"), Geometry(30, "G", CubeV, CubeF, null, Layer("Color", "Colors", "ByPolygonVertex", "Direct", new double[96])) }, new[] { C(1, 0), C(30, 1) });
        Assert.Contains("colour", colours.Fallback);
        var d = new FbxDocument { Version = 7400 };
        d.Nodes.Add(With(N("Objects"), Model(1, "A"))); d.Nodes.Add(With(N("Connections"), C(1, 0)));
        Assert.Contains("GlobalSettings", FbxImport.Build(FbxScene.Read(d)).Fallback);
    }

    [Fact]
    public void Long_names_are_cut_with_a_sha1_tail_as_validate_blend_names_does()
    {
        Assert.Equal("short", FbxImport.ValidateBlendName("short"));
        string cut = FbxImport.ValidateBlendName(new string('L', 70));
        Assert.Equal(new string('L', 55) + "_4e0c060", cut);
        string accented = FbxImport.ValidateBlendName(new string('é', 40));   // 80 bytes: cut to 55 bytes, which splits a character: one less
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(accented) <= 63);
        Assert.EndsWith("_" + Sha1Prefix(new string('é', 40)), accented);
    }
    static string Sha1Prefix(string s) { using (var sha = System.Security.Cryptography.SHA1.Create()) return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s))).Replace("-", "").ToLowerInvariant().Substring(0, 7); }
}
