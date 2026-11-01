using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

// The C# side of tools/fbx_model_drill.sh (step 6a of the native FBX reader): for every FBX file, FbxModel's HafModel against
// the GLB Blender wrote of the same file (fbx_to_glb.py: import, export), compared on what 6a models - the node tree (names,
// parents, children order, mesh references, the written translation / rotation / scale as bits), the meshes (names, the
// primitives' material NAMES, vertex counts, positions, normals, UV sets and indices as bits) and the material names in
// order. Images, textures, samplers and material fields are 6b's. Exit 1 on any difference; a named fallback is a SKIP row.
static class FbxModelDrill
{
    static int Main(string[] args)
    {
        if (args.Length < 1) { Console.Error.WriteLine("usage: FbxModelDrill <file.fbx> <blender.glb> [...]  |  --dump <file.fbx>"); return 2; }
        if (args[0] == "--dump")
        {
            var scene = FbxImport.Read(args[1]);
            if (scene.Fallback != null) { Console.WriteLine("FALLBACK " + scene.Fallback); return 0; }
            var m = FbxModel.Build(scene);
            Console.WriteLine(Describe(m));
            return 0;
        }
        if (args[0] == "--probe")
        {
            var scene = FbxImport.Read(args[1]);
            if (scene.Fallback != null) { Console.WriteLine("FALLBACK " + scene.Fallback); return 0; }
            string M4(float[] m) => string.Join(" ", m.Select(FBits));
            foreach (var ob in scene.Objects)
            {
                Console.WriteLine("OBJ '" + ob.Name + "' parent " + (ob.Parent != null ? "'" + ob.Parent.Name + "'" : "None"));
                Console.WriteLine("  loc " + string.Join(" ", ob.Loc.Select(FBits)) + " eul " + string.Join(" ", ob.Eul.Select(FBits)) + " size " + string.Join(" ", ob.Size.Select(FBits)));
                Console.WriteLine("  base " + M4(VehicleProbe.ToColumnMajor(ob.HelperBase))); Console.WriteLine("  geom " + M4(VehicleProbe.ToColumnMajor(ob.HelperGeom))); Console.WriteLine("  matrix " + M4(VehicleProbe.ToColumnMajor(ob.HelperMatrix))); Console.WriteLine("  lclrot " + M4(VehicleProbe.ToColumnMajor(ob.HelperLclRot)));
                Console.WriteLine("  basis " + M4(VehicleProbe.ToColumnMajor(ob.Basis)));
                Console.WriteLine("  world " + M4(ob.World));
            }
            Console.WriteLine("GLOBAL " + M4(VehicleProbe.ToColumnMajor(scene.GlobalMatrix)));
            return 0;
        }
        int failed = 0, compared = 0, skipped = 0;
        for (int i = 0; i + 1 < args.Length; i += 2)
        {
            string fbx = args[i], glb = args[i + 1];
            try
            {
                var scene = FbxImport.Read(fbx);
                if (scene.Fallback != null) { Console.WriteLine("SKIP " + fbx + " : " + scene.Fallback); skipped++; continue; }
                var mine = FbxModel.Build(scene);
                var theirs = GlbReader.Read(glb);
                string d = Compare(mine, theirs);
                if (d == null) { Console.WriteLine("PASS " + fbx + " : " + mine.Nodes.Count + " nodes, " + mine.Meshes.Count + " meshes, " + mine.Materials.Count + " materials" + (scene.Notes.Count > 0 ? " (notes: " + string.Join("; ", scene.Notes.Distinct().Take(3)) + ")" : "")); compared++; }
                else { Console.WriteLine("DIFF " + fbx + " : " + d); failed++; }
            }
            catch (Exception e) { Console.WriteLine("ERROR " + fbx + " : " + e.GetType().Name + ": " + e.Message); failed++; }
        }
        Console.WriteLine("RESULT compared=" + compared + " skipped=" + skipped + " failed=" + failed);
        return failed == 0 ? 0 : 1;
    }

    static string Bits(double d) => BitConverter.ToUInt64(BitConverter.GetBytes(d), 0).ToString("x16");
    static string FBits(float f) => BitConverter.ToUInt32(BitConverter.GetBytes(f), 0).ToString("x8");

    /// <summary>The first difference on the 6a fields, or null.</summary>
    static string Compare(HafModel a, HafModel b)
    {
        if (a.Nodes.Count != b.Nodes.Count) return $"nodes: {a.Nodes.Count} vs Blender's {b.Nodes.Count} ({Names(a.Nodes.Select(n => n.Name))} vs {Names(b.Nodes.Select(n => n.Name))})";
        if (a.Meshes.Count != b.Meshes.Count) return $"meshes: {a.Meshes.Count} vs Blender's {b.Meshes.Count}";
        if (a.Materials.Count != b.Materials.Count) return $"materials: {a.Materials.Count} vs Blender's {b.Materials.Count} ({Names(a.Materials.Select(m => m.Name))} vs {Names(b.Materials.Select(m => m.Name))})";
        for (int i = 0; i < a.Materials.Count; i++) if (a.Materials[i].Name != b.Materials[i].Name) return $"material {i}: '{a.Materials[i].Name}' vs '{b.Materials[i].Name}'";
        if (a.Scenes.Count != b.Scenes.Count || a.Scene != b.Scene) return "scenes";
        if (!a.Scenes[0].Nodes.SequenceEqual(b.Scenes[0].Nodes)) return $"scene roots: [{string.Join(",", a.Scenes[0].Nodes)}] vs [{string.Join(",", b.Scenes[0].Nodes)}]";
        for (int i = 0; i < a.Nodes.Count; i++)
        {
            var x = a.Nodes[i]; var y = b.Nodes[i];
            if (x.Name != y.Name) return $"node {i}: name '{x.Name}' vs '{y.Name}'";
            if (x.Parent != y.Parent) return $"node {i} '{x.Name}': parent {x.Parent} vs {y.Parent}";
            if (!x.Children.SequenceEqual(y.Children)) return $"node {i} '{x.Name}': children [{string.Join(",", x.Children)}] vs [{string.Join(",", y.Children)}]";
            if (x.Mesh != y.Mesh) return $"node {i} '{x.Name}': mesh {x.Mesh} vs {y.Mesh}";
            if (x.HasMatrix != y.HasMatrix) return $"node {i} '{x.Name}': matrix";
            string t = Trs(x.Translation, y.Translation, "translation", 3); if (t != null) return $"node {i} '{x.Name}': {t}";
            t = Trs(x.Rotation, y.Rotation, "rotation", 4); if (t != null) return $"node {i} '{x.Name}': {t}";
            t = Trs(x.Scale, y.Scale, "scale", 3); if (t != null) return $"node {i} '{x.Name}': {t}";
        }
        for (int i = 0; i < a.Meshes.Count; i++)
        {
            var x = a.Meshes[i]; var y = b.Meshes[i];
            if (x.Name != y.Name) return $"mesh {i}: name '{x.Name}' vs '{y.Name}'";
            if (x.Primitives.Count != y.Primitives.Count) return $"mesh {i} '{x.Name}': {x.Primitives.Count} primitives vs {y.Primitives.Count}";
            for (int p = 0; p < x.Primitives.Count; p++)
            {
                var u = x.Primitives[p]; var v = y.Primitives[p];
                string where = $"mesh {i} '{x.Name}' primitive {p}";
                string mu = u.Material >= 0 ? a.Materials[u.Material].Name : "", mv = v.Material >= 0 ? b.Materials[v.Material].Name : "";
                if (mu != mv) return $"{where}: material '{mu}' vs '{mv}'";
                if (u.VertexCount != v.VertexCount) return $"{where}: {u.VertexCount} vertices vs {v.VertexCount}";
                string d = Floats(u.Positions, v.Positions, "positions", 3); if (d != null) return where + ": " + d;
                d = Floats(u.Normals, v.Normals, "normals", 3); if (d != null) return where + ": " + d;
                d = Floats(u.Uv0, v.Uv0, "uv0", 2); if (d != null) return where + ": " + d;
                d = Floats(u.Uv1, v.Uv1, "uv1", 2); if (d != null) return where + ": " + d;
                int um = u.UvMore?.Count ?? 0, vm = v.UvMore?.Count ?? 0;
                if (um != vm) return $"{where}: {um + 2} uv sets vs {vm + 2}";
                for (int k = 0; k < um; k++) { d = Floats(u.UvMore[k], v.UvMore[k], "uv" + (k + 2), 2); if (d != null) return where + ": " + d; }
                if ((u.Indices == null) != (v.Indices == null)) return where + ": indexed vs not";
                if (u.Indices != null)
                {
                    if (u.Indices.Length != v.Indices.Length) return $"{where}: {u.Indices.Length} indices vs {v.Indices.Length}";
                    for (int k = 0; k < u.Indices.Length; k++) if (u.Indices[k] != v.Indices[k]) return $"{where}: index {k}: {u.Indices[k]} vs {v.Indices[k]}";
                }
            }
        }
        return null;
    }

    static string Names(IEnumerable<string> n) => "[" + string.Join(",", n.Take(12)) + "]";

    static string Trs(double[] x, double[] y, string what, int n)
    {
        for (int k = 0; k < n; k++) if (Bits(x[k]) != Bits(y[k])) return $"{what}[{k}] {Bits(x[k])} ({x[k]}) vs {Bits(y[k])} ({y[k]})";
        return null;
    }

    static string Floats(float[] x, float[] y, string what, int per)
    {
        if ((x == null) != (y == null)) return what + (x == null ? " missing here" : " missing in Blender's");
        if (x == null) return null;
        if (x.Length != y.Length) return $"{what}: {x.Length / per} values vs {y.Length / per}";
        for (int k = 0; k < x.Length; k++) if (FBits(x[k]) != FBits(y[k])) return $"{what} {k / per}[{k % per}]: {FBits(x[k])} ({x[k]}) vs {FBits(y[k])} ({y[k]})";
        return null;
    }

    static string Describe(HafModel m)
    {
        var sb = new StringBuilder();
        sb.Append("nodes ").Append(m.Nodes.Count).Append(" meshes ").Append(m.Meshes.Count).Append(" materials ").Append(m.Materials.Count).Append('\n');
        for (int i = 0; i < m.Nodes.Count; i++)
        {
            var n = m.Nodes[i];
            sb.Append("node ").Append(i).Append(" \"").Append(n.Name).Append("\" parent=").Append(n.Parent).Append(" mesh=").Append(n.Mesh).Append(" children=[").Append(string.Join(",", n.Children)).Append("] t=").Append(string.Join(",", n.Translation.Select(Bits)))
              .Append(" r=").Append(string.Join(",", n.Rotation.Select(Bits))).Append(" s=").Append(string.Join(",", n.Scale.Select(Bits))).Append('\n');
        }
        for (int i = 0; i < m.Meshes.Count; i++)
        {
            var me = m.Meshes[i];
            sb.Append("mesh ").Append(i).Append(" \"").Append(me.Name).Append("\" primitives=").Append(me.Primitives.Count);
            foreach (var p in me.Primitives) sb.Append(" [mat=").Append(p.Material).Append(" v=").Append(p.VertexCount).Append(" i=").Append(p.Indices?.Length ?? 0).Append(" uv=").Append((p.Uv0 != null ? 1 : 0) + (p.Uv1 != null ? 1 : 0) + (p.UvMore?.Count ?? 0)).Append(']');
            sb.Append('\n');
        }
        for (int i = 0; i < m.Materials.Count; i++) sb.Append("material ").Append(i).Append(" \"").Append(m.Materials[i].Name).Append("\"\n");
        return sb.ToString();
    }
}
