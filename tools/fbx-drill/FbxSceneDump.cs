using System;
using System.Collections.Generic;
using System.Linq;
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
        int textures = 0;
        foreach (var ob in s.Objects) if (ob.IsTexture) textures++;
        o.Append("settings up=").Append(s.UpAxis).Append(" front=").Append(s.FrontAxis).Append(" right=").Append(s.RightAxis)
         .Append(" unit=").Append(Bits(s.UnitMeters)).Append(" fps=").Append(Bits(s.FramesPerSecond)).Append(" version=").Append(s.Version)
         .Append(" exporter=").Append(s.Exporter).Append(" exporter_version=").Append(s.ExporterVersion).Append(" ktime=").Append(s.KTimeSecond).Append('\n');
        o.Append("counts nodes=").Append(s.Nodes.Count).Append(" meshes=").Append(s.Meshes.Count).Append(" materials=").Append(s.Materials.Count)
         .Append(" textures=").Append(textures).Append(" skins=").Append(s.Skins.Count).Append(" clusters=").Append(s.Clusters.Count).Append(" bones=").Append(s.Bones.Count)
         .Append(" poses=").Append(s.Poses.Count).Append(" anim_stacks=").Append(s.AnimStacks.Count).Append(" anim_layers=").Append(s.AnimLayers.Count)
         .Append(" anim_values=").Append(s.AnimValues.Count).Append(" anim_curves=").Append(s.AnimCurves.Count).Append('\n');
        for (int i = 0; i < s.Nodes.Count; i++)
        {
            var n = s.Nodes[i];
            o.Append("node ").Append(i).Append(" \"").Append(n.Name).Append("\" parent=").Append(n.Parent != null ? n.Parent.Index : -1)
             .Append(" attrib=").Append(n.AttribType).Append(" mesh=").Append(n.Mesh != null ? n.Mesh.Index : -1)
             .Append(" inherit=").Append(n.InheritMode).Append(" order=").Append(n.RotationOrder).Append(" rotspace=").Append(n.UseRotationSpace ? 1 : 0)
             .Append(" geomxf=").Append(n.HasGeometryTransform ? 1 : 0).Append(" visible=").Append(n.Visible ? 1 : 0)
             .Append(" bind_pose=").Append(n.BindPose != null ? n.BindPose.Index : -1).Append(" bone=").Append(n.Bone != null ? n.Bone.Index : -1).Append('\n');
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
            foreach (var sk in m.SkinDeformers) o.Append("  skin ").Append(sk.Index).Append('\n');
            foreach (var n in m.Instances) o.Append("  instance ").Append(n.Index).Append('\n');
        }
        for (int i = 0; i < s.Materials.Count; i++)
        {
            var mat = s.Materials[i];
            o.Append("material ").Append(i).Append(" \"").Append(mat.Name).Append("\" shader=").Append(mat.ShaderType).Append(" shading=\"").Append(mat.ShadingModel).Append('"')
             .Append(" diffuse=").Append(Bits(mat.Diffuse[0])).Append(',').Append(Bits(mat.Diffuse[1])).Append(',').Append(Bits(mat.Diffuse[2]))
             .Append(" textures=").Append(mat.Textures.Count).Append('\n');
            foreach (var (prop, _, file) in mat.Textures) o.Append("  texture \"").Append(prop).Append("\" -> \"").Append(file).Append("\"\n");
        }
        // step 4: bones, skins, clusters, poses in ufbxdump.c's lines
        foreach (var b in s.Bones)
            o.Append("bone ").Append(b.Index).Append(" \"").Append(b.Name).Append("\" root=").Append(b.IsRoot ? 1 : 0).Append(" radius=").Append(Bits(b.Radius))
             .Append(" rel=").Append(Bits(b.RelativeLength)).Append(" instances=").Append(b.Instances.Count).Append('\n');
        foreach (var sk in s.Skins)
        {
            o.Append("skin ").Append(sk.Index).Append(" \"").Append(sk.Name).Append("\" method=").Append(sk.SkinningMethod).Append(" clusters=").Append(sk.Clusters.Count)
             .Append(" vertices=").Append(sk.Vertices.Length).Append(" weights=").Append(sk.Weights.Length).Append(" max_weights=").Append(sk.MaxWeightsPerVertex).Append(" dq=").Append(sk.NumDqWeights).Append('\n');
            o.Append("  clusters"); foreach (var cl in sk.Clusters) o.Append(' ').Append(cl.Index); o.Append('\n');
            HashReset();
            for (int v = 0; v < sk.Vertices.Length; v++)
            {
                var sv = sk.Vertices[v]; HashU32((uint)sv.WeightBegin); HashU32((uint)sv.NumWeights); HashDouble(sv.DqWeight);
                if (full) o.Append("  sv ").Append(v).Append(" begin=").Append(sv.WeightBegin).Append(" n=").Append(sv.NumWeights).Append(" dq=").Append(Bits(sv.DqWeight)).Append('\n');
            }
            o.Append("  skin_vertices hash=").Append(fnv.ToString("x16")).Append('\n');
            HashReset();
            for (int w = 0; w < sk.Weights.Length; w++)
            {
                var sw = sk.Weights[w]; HashU32((uint)sw.ClusterIndex); HashDouble(sw.Weight);
                if (full) o.Append("  sw ").Append(w).Append(" cluster=").Append(sw.ClusterIndex).Append(" w=").Append(Bits(sw.Weight)).Append('\n');
            }
            o.Append("  skin_weights hash=").Append(fnv.ToString("x16")).Append('\n');
            HashReset();
            for (int d = 0; d < sk.NumDqWeights; d++) { HashU32(unchecked((uint)sk.DqVertices[d])); HashDouble(sk.DqWeights[d]); }
            o.Append("  dq hash=").Append(fnv.ToString("x16")).Append('\n');
        }
        foreach (var cl in s.Clusters)
        {
            o.Append("cluster ").Append(cl.Index).Append(" \"").Append(cl.Name).Append("\" bone=").Append(cl.BoneNode != null ? cl.BoneNode.Index : -1).Append(" num_weights=").Append(cl.NumWeights).Append('\n');
            HashReset();
            for (int k = 0; k < cl.NumWeights; k++)
            {
                HashU32(unchecked((uint)cl.Vertices[k])); HashDouble(cl.Weights[k]);
                if (full) o.Append("  cw ").Append(k).Append(' ').Append(unchecked((uint)cl.Vertices[k])).Append(' ').Append(Bits(cl.Weights[k])).Append('\n');
            }
            o.Append("  raw hash=").Append(fnv.ToString("x16")).Append('\n');
            Matrix("m2b", cl.MeshNodeToBone, o); Matrix("b2w", cl.BindToWorld, o); Matrix("g2b", cl.GeometryToBone, o); Matrix("g2w", cl.GeometryToWorld, o);
            var t = cl.GeometryToWorldTransform;
            o.Append("  g2wt t=").Append(Bits(t.tx)).Append(',').Append(Bits(t.ty)).Append(',').Append(Bits(t.tz))
             .Append(" r=").Append(Bits(t.qx)).Append(',').Append(Bits(t.qy)).Append(',').Append(Bits(t.qz)).Append(',').Append(Bits(t.qw))
             .Append(" s=").Append(Bits(t.sx)).Append(',').Append(Bits(t.sy)).Append(',').Append(Bits(t.sz)).Append('\n');
        }
        foreach (var p in s.Poses)
        {
            o.Append("pose ").Append(p.Index).Append(" \"").Append(p.Name).Append("\" bind=").Append(p.IsBindPose ? 1 : 0).Append(" bones=").Append(p.BonePoses.Count).Append('\n');
            foreach (var bp in p.BonePoses) { o.Append("  bone ").Append(bp.BoneNode.Index).Append('\n'); Matrix("b2w", bp.BoneToWorld, o); Matrix("b2p", bp.BoneToParent, o); }
        }
        Animation(s, o, full);
    }

    static int MaterialIndex(FbxScene s, FbxObject o) { for (int i = 0; i < s.Materials.Count; i++) if (s.Materials[i].Object == o) return i; return -1; }

    static string FBits(float f) => BitConverter.ToUInt32(BitConverter.GetBytes(f), 0).ToString("x8");
    static void HashFloat(float f) => HashBytes(BitConverter.GetBytes(f));
    static double[] SampleTimes(double begin, double end)
    {
        double span = end - begin;
        if (!(span > 0.0)) span = 1.0;
        var t = new double[13];
        t[0] = begin - span * 2.3;
        t[1] = begin - span * 0.5;
        for (int k = 0; k <= 8; k++) t[2 + k] = begin + span * (double)k / 8.0;
        t[11] = end + span * 0.5;
        t[12] = end + span * 2.3;
        return t;
    }

    /// <summary>Step 5, in ufbxdump.c's lines: stacks, layers (animated properties as a hash), values, curves (keys and thirteen
    /// samples as hashes), every node's transform at thirteen samples of each stack.</summary>
    static void Animation(FbxScene s, StringBuilder o, bool full)
    {
        foreach (var st in s.AnimStacks)
        {
            o.Append("anim_stack ").Append(st.Index).Append(" \"").Append(st.Name).Append("\" begin=").Append(Bits(st.TimeBegin)).Append(" end=").Append(Bits(st.TimeEnd)).Append(" layers=");
            for (int k = 0; k < st.Layers.Count; k++) o.Append(k > 0 ? "," : "").Append(st.Layers[k].Index);
            o.Append('\n');
        }
        foreach (var l in s.AnimLayers)
        {
            o.Append("anim_layer ").Append(l.Index).Append(" \"").Append(l.Name).Append("\" weight=").Append(Bits(l.Weight)).Append(" weight_anim=").Append(l.WeightIsAnimated ? 1 : 0)
             .Append(" blended=").Append(l.Blended ? 1 : 0).Append(" additive=").Append(l.Additive ? 1 : 0).Append(" compose_rot=").Append(l.ComposeRotation ? 1 : 0)
             .Append(" compose_scale=").Append(l.ComposeScale ? 1 : 0).Append(" values=").Append(l.Values.Count).Append(" props=").Append(l.AnimProps.Length).Append('\n');
            o.Append("  values"); foreach (var v in l.Values) o.Append(' ').Append(v.Index); o.Append('\n');
            var sorted = l.AnimProps.OrderBy(a => a.Element.ElementId).ThenBy(a => a.Key).ThenBy(a => a.PropName, StringComparer.Ordinal).ThenBy(a => a.Value.Index).ToList();
            HashReset();
            foreach (var ap in sorted)
            {
                HashU32((uint)ap.Element.ElementId); HashBytes(Encoding.UTF8.GetBytes(ap.PropName)); HashU32((uint)ap.Value.Index);
                if (full) o.Append("  aprop ").Append(ap.Element.ElementId).Append(" \"").Append(ap.PropName).Append("\" value=").Append(ap.Value.Index).Append('\n');
            }
            o.Append("  aprops hash=").Append(fnv.ToString("x16")).Append('\n');
        }
        foreach (var v in s.AnimValues)
            o.Append("anim_value ").Append(v.Index).Append(" \"").Append(v.Name).Append("\" default=").Append(Bits(v.DefaultValue[0])).Append(',').Append(Bits(v.DefaultValue[1])).Append(',').Append(Bits(v.DefaultValue[2]))
             .Append(" curves=").Append(v.Curves[0] != null ? v.Curves[0].Index : -1).Append(',').Append(v.Curves[1] != null ? v.Curves[1].Index : -1).Append(',').Append(v.Curves[2] != null ? v.Curves[2].Index : -1).Append('\n');
        foreach (var c in s.AnimCurves)
        {
            o.Append("anim_curve ").Append(c.Index).Append(" \"").Append(c.Name).Append("\" keys=").Append(c.Keyframes.Length).Append(" pre=").Append(c.PreMode).Append(',').Append(c.PreRepeat)
             .Append(" post=").Append(c.PostMode).Append(',').Append(c.PostRepeat).Append(" min=").Append(Bits(c.MinValue)).Append(" max=").Append(Bits(c.MaxValue))
             .Append(" tmin=").Append(Bits(c.MinTime)).Append(" tmax=").Append(Bits(c.MaxTime)).Append('\n');
            HashReset();
            for (int k = 0; k < c.Keyframes.Length; k++)
            {
                var key = c.Keyframes[k];
                HashDouble(key.Time); HashDouble(key.Value); HashU32((uint)key.Interpolation);
                HashFloat(key.LeftDx); HashFloat(key.LeftDy); HashFloat(key.RightDx); HashFloat(key.RightDy);
                if (full) o.Append("  key ").Append(k).Append(" t=").Append(Bits(key.Time)).Append(" v=").Append(Bits(key.Value)).Append(" i=").Append(key.Interpolation)
                           .Append(" l=").Append(FBits(key.LeftDx)).Append(',').Append(FBits(key.LeftDy)).Append(" r=").Append(FBits(key.RightDx)).Append(',').Append(FBits(key.RightDy)).Append('\n');
            }
            o.Append("  keys hash=").Append(fnv.ToString("x16")).Append('\n');
            var times = SampleTimes(c.MinTime, c.MaxTime);
            HashReset();
            for (int k = 0; k < 13; k++)
            {
                double value = FbxAnimation.EvaluateCurve(c, times[k], 0.0);
                HashDouble(value);
                if (full) o.Append("  sample ").Append(k).Append(" t=").Append(Bits(times[k])).Append(" v=").Append(Bits(value)).Append('\n');
            }
            o.Append("  samples hash=").Append(fnv.ToString("x16")).Append('\n');
        }
        foreach (var st in s.AnimStacks)
        {
            var times = SampleTimes(st.TimeBegin, st.TimeEnd);
            for (int j = 0; j < s.Nodes.Count; j++)
            {
                var n = s.Nodes[j];
                HashReset();
                for (int k = 0; k < 13; k++)
                {
                    var t = FbxAnimation.EvaluateTransform(st.Anim, n, times[k]);
                    HashDouble(t.tx); HashDouble(t.ty); HashDouble(t.tz);
                    HashDouble(t.qx); HashDouble(t.qy); HashDouble(t.qz); HashDouble(t.qw);
                    HashDouble(t.sx); HashDouble(t.sy); HashDouble(t.sz);
                    if (full) o.Append("  at ").Append(k).Append(" t=").Append(Bits(t.tx)).Append(',').Append(Bits(t.ty)).Append(',').Append(Bits(t.tz))
                               .Append(" r=").Append(Bits(t.qx)).Append(',').Append(Bits(t.qy)).Append(',').Append(Bits(t.qz)).Append(',').Append(Bits(t.qw))
                               .Append(" s=").Append(Bits(t.sx)).Append(',').Append(Bits(t.sy)).Append(',').Append(Bits(t.sz)).Append('\n');
                }
                o.Append("anim_eval stack=").Append(st.Index).Append(" node=").Append(j).Append(" hash=").Append(fnv.ToString("x16")).Append('\n');
            }
        }
    }

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
