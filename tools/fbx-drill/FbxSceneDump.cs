using System;
using System.Collections.Generic;
using System.Text;

// The C# side of the OBJECT-LAYER comparison of tools/fbx_drill.sh: editor/FbxScene.cs's dump of a file in exactly
// the format tools/fbx-drill/ufbx/ufbxdump.c prints from ufbx (the reference reader): settings, counts, every node
// with its local transform and matrices as bits, every mesh with its faces and vertex streams as FNV-1a 64 hashes
// over the bits of their values, every material. Equal text is the claim.
static class FbxSceneDump
{
    static ulong fnv;
    static void HashReset() => fnv = 14695981039346656037UL;
    static void HashBytes(byte[] b) { foreach (var x in b) { fnv ^= x; fnv *= 1099511628211UL; } }
    static void HashDouble(double d) => HashBytes(BitConverter.GetBytes(d));
    static void HashU32(uint v) => HashBytes(BitConverter.GetBytes(v));
    static string Bits(double d) => BitConverter.ToUInt64(BitConverter.GetBytes(d), 0).ToString("x16");

    public static void Dump(FbxScene s, StringBuilder o, bool full)
    {
        int textures = 0, skins = 0, clusters = 0, bones = 0, stacks = 0;
        foreach (var ob in s.Objects)
        {
            if (ob.IsTexture) textures++;
            else if (ob.Class == "Deformer" && ob.SubType == "Skin") skins++;
            else if (ob.Class == "Deformer" && ob.SubType == "Cluster") clusters++;
            else if (ob.Class == "NodeAttribute" && (ob.SubType == "LimbNode" || ob.SubType == "Limb" || ob.SubType == "Root")) bones++;
            else if (ob.Class == "AnimationStack") stacks++;
        }
        o.Append("settings up=").Append(s.UpAxis).Append(" front=").Append(s.FrontAxis).Append(" right=").Append(s.RightAxis)
         .Append(" unit=").Append(Bits(s.UnitMeters)).Append(" fps=").Append(Bits(s.FramesPerSecond)).Append(" version=").Append(s.Version).Append('\n');
        o.Append("counts nodes=").Append(s.Nodes.Count).Append(" meshes=").Append(s.Meshes.Count).Append(" materials=").Append(s.Materials.Count)
         .Append(" textures=").Append(textures).Append(" skins=").Append(skins).Append(" clusters=").Append(clusters).Append(" bones=").Append(bones).Append(" anim_stacks=").Append(stacks).Append('\n');
        for (int i = 0; i < s.Nodes.Count; i++)
        {
            var n = s.Nodes[i];
            o.Append("node ").Append(i).Append(" \"").Append(n.Name).Append("\" parent=").Append(n.Parent != null ? n.Parent.Index : -1)
             .Append(" attrib=").Append(n.AttribType).Append(" mesh=").Append(n.Mesh != null ? n.Mesh.Index : -1)
             .Append(" inherit=").Append(n.InheritMode).Append(" order=").Append(n.RotationOrder).Append(" rotspace=").Append(n.UseRotationSpace ? 1 : 0)
             .Append(" geomxf=").Append(n.HasGeometryTransform ? 1 : 0).Append(" visible=").Append(n.Visible ? 1 : 0).Append('\n');
            var t = n.LocalTransform;
            o.Append("  local t=").Append(Bits(t.tx)).Append(',').Append(Bits(t.ty)).Append(',').Append(Bits(t.tz))
             .Append(" r=").Append(Bits(t.qx)).Append(',').Append(Bits(t.qy)).Append(',').Append(Bits(t.qz)).Append(',').Append(Bits(t.qw))
             .Append(" s=").Append(Bits(t.sx)).Append(',').Append(Bits(t.sy)).Append(',').Append(Bits(t.sz)).Append('\n');
            Matrix("n2p", n.NodeToParent, o); Matrix("n2w", n.NodeToWorld, o);
            if (n.HasGeometryTransform) Matrix("g2n", n.GeometryToNode, o);
        }
        for (int i = 0; i < s.Meshes.Count; i++)
        {
            var m = s.Meshes[i];
            o.Append("mesh ").Append(i).Append(" \"").Append(m.Name).Append("\" vertices=").Append(m.NumVertices).Append(" faces=").Append(m.NumFaces)
             .Append(" indices=").Append(m.NumIndices).Append(" triangles=").Append(m.NumTriangles).Append(" max_face=").Append(m.MaxFaceTriangles)
             .Append(" materials=").Append(m.Materials.Count).Append(" instances=").Append(m.Instances.Count).Append('\n');
            HashReset();
            for (int f = 0; f < m.NumFaces; f++)
            {
                HashU32((uint)m.FaceSize[f]);
                for (int k = 0; k < m.FaceSize[f]; k++) HashU32((uint)m.VertexIndices[m.FaceBegin[f] + k]);
                if (full)
                {
                    o.Append("  face ").Append(f).Append(" n=").Append(m.FaceSize[f]).Append(" mat=").Append(m.FaceMaterial.Length > 0 ? m.FaceMaterial[f] : 0).Append(" idx=");
                    for (int k = 0; k < m.FaceSize[f]; k++) o.Append(k > 0 ? "," : "").Append(m.VertexIndices[m.FaceBegin[f] + k]);
                    o.Append('\n');
                }
            }
            o.Append("  faces hash=").Append(fnv.ToString("x16")).Append('\n');
            HashReset(); foreach (var fm in m.FaceMaterial) HashU32((uint)fm);
            o.Append("  face_material count=").Append(m.FaceMaterial.Length).Append(" hash=").Append(fnv.ToString("x16")).Append('\n');
            HashReset();
            for (int v = 0; v < m.NumVertices; v++)
            {
                HashDouble(m.Vertices[v * 3]); HashDouble(m.Vertices[v * 3 + 1]); HashDouble(m.Vertices[v * 3 + 2]);
                if (full) o.Append("  vertex ").Append(v).Append(' ').Append(Bits(m.Vertices[v * 3])).Append(' ').Append(Bits(m.Vertices[v * 3 + 1])).Append(' ').Append(Bits(m.Vertices[v * 3 + 2])).Append('\n');
            }
            o.Append("  vertices hash=").Append(fnv.ToString("x16")).Append('\n');
            // step 3: every face through FbxTriangulate (the quad rule, the n-gon ear clipping); corner indices in order
            HashReset();
            var tris = m.Triangles;
            foreach (var t in tris) HashU32((uint)t);
            if (full) { int at = 0; for (int f = 0; f < m.NumFaces; f++) for (int t = 0; t < Math.Max(0, m.FaceSize[f] - 2); t++, at += 3) o.Append("  tri ").Append(f).Append(' ').Append(tris[at]).Append(',').Append(tris[at + 1]).Append(',').Append(tris[at + 2]).Append('\n'); }
            o.Append("  triangles count=").Append(tris.Length / 3).Append(" hash=").Append(fnv.ToString("x16")).Append('\n');
            Stream("position", m.Position, m.NumIndices, o, full);
            Stream("normal", m.Normal, m.NumIndices, o, full);
            for (int u = 0; u < m.UvSets.Count; u++) { o.Append("  uvset ").Append(u).Append(" \"").Append(m.UvSets[u].Name).Append("\"\n"); Stream("uv" + u, m.UvSets[u], m.NumIndices, o, full); }
            for (int c = 0; c < m.ColorSets.Count; c++) { o.Append("  colorset ").Append(c).Append(" \"").Append(m.ColorSets[c].Name).Append("\"\n"); Stream("color" + c, m.ColorSets[c], m.NumIndices, o, full); }
            Stream("tangent", m.Tangent, m.NumIndices, o, full);
            Stream("bitangent", m.Bitangent, m.NumIndices, o, full);
            for (int k = 0; k < m.Materials.Count; k++) o.Append("  slot ").Append(k).Append(" material=").Append(MaterialIndex(s, m.Materials[k])).Append('\n');
        }
        for (int i = 0; i < s.Materials.Count; i++)
        {
            var mat = s.Materials[i];
            o.Append("material ").Append(i).Append(" \"").Append(mat.Name).Append("\" shader=").Append(mat.ShaderType).Append(" shading=\"").Append(mat.ShadingModel).Append('"')
             .Append(" diffuse=").Append(Bits(mat.Diffuse[0])).Append(',').Append(Bits(mat.Diffuse[1])).Append(',').Append(Bits(mat.Diffuse[2]))
             .Append(" textures=").Append(mat.Textures.Count).Append('\n');
            foreach (var (prop, _, file) in mat.Textures) o.Append("  texture \"").Append(prop).Append("\" -> \"").Append(file).Append("\"\n");
        }
    }

    static int MaterialIndex(FbxScene s, FbxObject o) { for (int i = 0; i < s.Materials.Count; i++) if (s.Materials[i].Object == o) return i; return -1; }

    static void Matrix(string tag, double[] m, StringBuilder o)
    {
        o.Append("  ").Append(tag);
        for (int i = 0; i < 12; i++) o.Append(' ').Append(Bits(m[i]));
        o.Append('\n');
    }

    static void Stream(string tag, FbxVertexStream st, int numIndices, StringBuilder o, bool full)
    {
        if (!st.Exists) { o.Append("  ").Append(tag).Append(" none\n"); return; }
        HashReset();
        for (int i = 0; i < numIndices; i++)
        {
            if (full) o.Append("  ").Append(tag).Append(' ').Append(i);
            for (int c = 0; c < st.Components; c++) { double v = st.Get(i, c); HashDouble(v); if (full) o.Append(' ').Append(Bits(v)); }
            if (full) o.Append('\n');
        }
        o.Append("  ").Append(tag).Append(" values=").Append(st.ValueCount).Append(" indices=").Append(st.Indices.Length).Append(" unique=").Append(st.UniquePerVertex ? "1" : "0").Append(" hash=").Append(fnv.ToString("x16")).Append('\n');
    }
}
