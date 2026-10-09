// BlenderPrep.cs - the Factory's model prep without Blender (step 5 milestone d, part 4c): what editor/Tools~/prep_model.py
// does to a glTF source when asked to REDUCE - import, per-object Decimate COLLAPSE to a triangle budget, export - as one
// HafModel the GlbWriter writes. Every piece is a port held to Blender by tools/prep_drill.sh:
//   BlenderReduce      the importer's mesh and the collapse            BlenderExport      the exporter's mesh layout
//   BlenderExportTree  the node list, transforms, materials, skins     VehicleProbe.*     world matrices, the bone chain
// This file only assembles them. What the written file must equal is what the Factory's converter (baker/glbconv) reads:
// the nodes in order with their transforms, each mesh node's primitives (POSITION, NORMAL, TEXCOORD_0, JOINTS_0,
// WEIGHTS_0, indices, material), each skin's joints and inverse bind matrices, each material's name, base colour
// factor and base colour image. The drill runs the converter on this file and on Blender's and compares its whole
// output byte for byte.
// NOT Blender's, and not read by the converter: the meshes' names, COLOR_n as float RGBA (Blender writes shorts or
// RGB floats), the material properties beyond the base colour (copied from the source as they are; the alpha mode
// made one the schema knows), textures and
// images the written materials do not use (kept; Blender drops them), `extras`.
// A file this cannot prep as Blender does is NAMED (Result.Fallback) and left to Blender - never guessed:
//   no reduce asked (instanced meshes stay shared then: not modelled), several scenes (prep_model.py fails on some),
//   animations (Blender exports its posed import state), KHR_materials_variants (the importer keeps a slot per
//   primitive then), an unskinned mesh object named Icosphere... (prep_model.py removes it as the importer's bone
//   shape), an object the reduce declines (BlenderReduce.FallbackReason), a shape the tree names
//   (BlenderExportTree.Result.Problems), an object under a bone (pose evaluation), and what MaterialProblems names:
//   a material that comes out with a factor outside 0..1 (Blender clamps the colour only, and the converter refuses the
//   file - Blender's own too), a base colour image that is not a PNG or a JPEG, one whose file name's extension is not
//   its format's, a JPEG whose alpha is read (each written again by Blender's own encoder), and any image the writer
//   cannot embed.
// STRIP (part 4d): prep_model.py deletes every object whose name contains one of the given substrings, ignoring case,
// with all its descendants, before it reduces - the ratio is taken from what is left. Stripped() is that rule; the tree
// and the reduce leave those objects out. A strip WITHOUT a reduce is Blender's (no reduce asked, above).
// Called by UniversalBaker.PrepInProcess, with Blender's prep_model.py as the fallback for every file named here.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

public static class BlenderPrep
{
    public sealed class ObjectRun
    {
        public int Node; public string Name;
        public string Declined;                              // BlenderReduce.FallbackReason, or null
        public BlenderReduce.Result Reduced;
        public BlenderExport.Skin Skin;                      // the skinned layout's armature, or null
        public List<string> JointNames;                      // the armature's bones in creation order, or null
        public int Armature = -2;                            // the armature's glTF node (-1 the dummy root's), -2 when not skinned
        public List<BlenderExport.Primitive> Primitives;     // as written; empty for an object without faces
    }

    public sealed class Result
    {
        public HafModel Model;                               // the prepared file, or null when Fallback names why not
        public string Fallback;                              // the first reason this file is Blender's to prep, or null
        public readonly List<string> Reasons = new List<string>();   // every reason
        public BlenderNames.Result Names; public float[][] World;
        public readonly List<ObjectRun> Objects = new List<ObjectRun>();
        public BlenderExportTree.Result Tree;                // null when an object was declined
        public readonly Dictionary<int, List<string>> MaterialsOfMesh = new Dictionary<int, List<string>>();   // per mesh node: each written primitive's Blender material (null for none)
        public readonly HashSet<int> NeutralArmatures = new HashSet<int>();
        public long SourceTriangles, Triangles; public float Ratio;   // prep_model.py's "PREP reduce: tris <before> -> <after>" and its ratio
        public readonly List<string> Notes = new List<string>();   // the material rules this file took, each once (the drill's coverage rows)
        public List<string> StripSubstrings = new List<string>();  // the strip list as prep_model.py parses it (trimmed, lower case, no empties)
        public HashSet<string> Stripped = new HashSet<string>(StringComparer.Ordinal);   // the objects the strip removed, by Blender name
    }

    /// <summary>prep_model.py's reduce on a glTF model. With `diagnose` the work goes on past a reason to fall back (the
    /// drill compares what it can); without, it stops at the first one.</summary>
    public static Result Prepare(HafModel m, long targetTris, bool diagnose = false, BlenderNames.Result names = null, int threads = 0, string strip = null, Action checkpoint = null)
    {
        checkpoint?.Invoke();
        var res = new Result { Names = names ?? BlenderNames.Compute(m) };
        names = res.Names;
        void Decline(string why) { res.Reasons.Add(why); if (res.Fallback == null) res.Fallback = why; }
        res.Stripped = Stripped(names, strip, out res.StripSubstrings, out string stripProblem);
        if (stripProblem != null) Decline(stripProblem);
        if (targetTris <= 0) Decline("no reduce asked: instanced meshes stay shared in Blender then, which is not modelled");
        // prep_model.py fails on such a file when an object lies outside the scene Blender makes active ("not in View
        // Layer": the scenes fixture), and works when none does (no_default_scene); which it is, is not modelled
        if (m.Scenes.Count > 1) Decline("a file of several scenes: which one Blender makes active, and whether prep_model.py then fails on an object outside it, is not modelled");
        if (m.Animations.Count > 0) Decline("an animated file: Blender exports its posed import state");
        // imp/mesh.py: with variant mappings the importer never merges a mesh's material slots, and it makes materials for
        // the variants alone (read, not measured: named and left)
        if (m.ExtensionsUsed.Contains("KHR_materials_variants")) Decline("variants: the file uses KHR_materials_variants, under which Blender's importer keeps a material slot per primitive");
        if (res.Fallback != null && !diagnose) return res;

        // the mesh objects in creation order, and the triangle total prep_model.py takes its ratio from
        var meshObjects = new List<(int node, string name)>();
        foreach (var (node, name) in names.MeshObjectsInOrder)
        {
            checkpoint?.Invoke();
            // prep_model.py purges "the importer's bone shape": every mesh object whose name starts with Icosphere and has
            // no vertex group - a real part of that name too, with its triangles out of the ratio and its children
            // unparented; an object without a vertex is a mesh object all the same
            int sk = m.Nodes[node].Skin;
            bool groups = sk >= 0 && sk < m.Skins.Count && m.Meshes[m.Nodes[node].Mesh].Primitives.Exists(p => p.Skinned);
            if (name.StartsWith("Icosphere", StringComparison.Ordinal) && !groups) Decline("icosphere: the object '" + name + "' is removed by prep_model.py (it takes every unskinned Icosphere... for the importer's bone shape)");
            if (res.Stripped.Contains(name)) continue;   // stripped: not reduced, not in the ratio
            var layout = BlenderMesh.FromGltf(m, m.Nodes[node].Mesh);
            if (layout.VertexCount == 0) continue;
            meshObjects.Add((node, name)); res.SourceTriangles += layout.Faces.Length / 3;
        }
        // prep_model.py stops when the strip leaves no mesh OBJECT ("no meshes to reduce"); with only vertex-less ones left
        // it would go on and export a file without a mesh - named here all the same, and Blender does what it does
        if (meshObjects.Count == 0) { Decline("no mesh to reduce"); return res; }
        res.Ratio = BlenderReduce.Ratio(Math.Max(1, targetTris), res.SourceTriangles);
        foreach (var (node, name) in meshObjects)
        {
            checkpoint?.Invoke();
            string why = BlenderReduce.FallbackReason(m, node);
            if (why != null) Decline("'" + name + "': " + why);
        }
        if (res.Fallback != null && !diagnose) return res;

        foreach (var (node, name) in meshObjects)
        {
            checkpoint?.Invoke();
            var run = new ObjectRun { Node = node, Name = name, Declined = BlenderReduce.FallbackReason(m, node) };
            res.Objects.Add(run);
            if (run.Declined != null) continue;
            // a skinned mesh hangs from its armature with no transform of its own: both matrices are the armature's.
            // The exported joints are the armature's bones in creation order; group i is the skin's joint i
            int skin = m.Nodes[node].Skin;
            if (skin >= 0 && skin < m.Skins.Count && m.Meshes[m.Nodes[node].Mesh].Primitives.Exists(p => p.Skinned))
            {
                int an = names.ArmatureNodeOfSkin[skin];
                if (res.World == null) res.World = VehicleProbe.BlenderWorldMatrices(m, null);
                var arma = an >= 0 ? res.World[an] : new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
                var place = new Dictionary<int, int>(); run.JointNames = new List<string>();
                foreach (int b in names.BoneNodesInOrder) if (names.ArmatureNodeOfBone[b] == an) { place[b] = place.Count; run.JointNames.Add(names.BoneOfJoint[b]); }
                run.Skin = new BlenderExport.Skin { ObjectWorld = arma, ArmatureWorld = arma, JointCount = place.Count, GroupJoint = m.Skins[skin].Joints.Select(j => place.TryGetValue(j, out int at) ? at : -1).ToArray() };
                run.Armature = an;
            }
            var r = BlenderReduce.Reduce(m, node, res.Ratio, names, threads, checkpoint);
            run.Reduced = r;
            run.Primitives = BlenderExport.MeshPrimitives(r, run.Skin);
            res.MaterialsOfMesh[node] = run.Primitives.Select(p => { var (mat, vc) = r.Slots[p.MaterialSlot]; return mat < 0 && !vc ? null : names.MaterialOf[(mat < 0 ? names.MeshDatablockNode[node] : -1, mat, vc)]; }).ToList();
            // Blender appends ONE neutral joint to an armature if any mesh of it has a vertex without a bone
            if (run.Skin != null && run.Primitives.Count > 0 && run.Primitives[0].NeutralBone) res.NeutralArmatures.Add(run.Armature);
            // as prep_model.py counts its "tris <before> -> <after>": the faces the modifier left, BEFORE the exporter's
            // validate drops a twin face or two (the written primitives hold a few fewer: 28,356 for Blender's 28,365
            // on a 3.6M-triangle ship, measured 2026-10-09 - the files were equal, the log line was not)
            res.Triangles += r.Faces.Length / 3;
        }
        if (res.Objects.Exists(o => o.Declined != null)) return res;   // the tree would not know the declined mesh's faces nor its materials

        checkpoint?.Invoke();
        if (res.World == null) res.World = VehicleProbe.BlenderWorldMatrices(m, null);
        var withFaces = new HashSet<int>(res.MaterialsOfMesh.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key));
        res.Tree = BlenderExportTree.Build(m, names, res.World, withFaces, res.NeutralArmatures, mn => res.MaterialsOfMesh.TryGetValue(mn, out var l) ? l : new List<string>(), stripped: res.Stripped);
        foreach (var p in res.Tree.Problems) Decline(p);
        var underBone = res.Tree.Nodes.FirstOrDefault(n => !n.TransformKnown);
        if (underBone != null) Decline("an object under a bone: '" + underBone.Name + "' - its transform needs Blender's pose evaluation");
        if (!res.Tree.Nodes.Exists(n => n.HasMesh)) Decline("no mesh left to export");
        foreach (var why in MaterialProblems(m, res)) Decline(why);
        if (res.Fallback != null) return res;
        checkpoint?.Invoke();
        res.Model = Assemble(m, res);
        checkpoint?.Invoke();
        return res;
    }

    // Python str.strip includes the ASCII information separators U+001C..U+001F; .NET Trim does not.
    // Use Python's whitespace set explicitly so the editor's Mono follows the same rule too.
    static readonly char[] PythonWhitespace = "\u0009\u000a\u000b\u000c\u000d\u001c\u001d\u001e\u001f\u0020\u0085\u00a0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u2028\u2029\u202f\u205f\u3000".ToCharArray();

    /// <summary>prep_model.py's strip: `subs = [s.strip().lower() for s in arg.split(",") if s.strip()]`, then every object
    /// whose `name.lower()` contains one of them, with its `children_recursive` - an object's children are the objects
    /// parented to it, those parented to one of an armature's bones included. Python's lower() and .NET's are the same
    /// on ASCII; a substring that is not ASCII, or a name with one of the two letters whose lower case IS ASCII in
    /// Python (the Kelvin sign, the dotted capital I), is named and left to Blender rather than matched differently.</summary>
    public static HashSet<string> Stripped(BlenderNames.Result names, string strip, out List<string> substrings, out string problem)
    {
        problem = null;
        substrings = (strip ?? "").Split(',').Select(s => s.Trim(PythonWhitespace)).Where(s => s.Length > 0).Select(s => s.ToLowerInvariant()).ToList();
        var gone = new HashSet<string>(StringComparer.Ordinal);
        if (substrings.Count == 0) return gone;
        if (substrings.Exists(s => s.Any(c => c > 127))) { problem = "strip: a strip substring that is not ASCII (Python's lower case and .NET's can differ there)"; return gone; }
        if (names.Objects.Exists(o => o.Name.IndexOf('\u212A') >= 0 || o.Name.IndexOf('\u0130') >= 0)) { problem = "strip: an object name with a letter whose lower case is ASCII in Python only"; return gone; }
        var children = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var o in names.Objects)
            if (o.Parent != null) { if (!children.TryGetValue(o.Parent, out var l)) children[o.Parent] = l = new List<string>(); l.Add(o.Name); }
        void Take(string name) { if (!gone.Add(name)) return; if (children.TryGetValue(name, out var kids)) foreach (var k in kids) Take(k); }
        foreach (var o in names.Objects)
        {
            string lower = o.Name.ToLowerInvariant();
            if (substrings.Exists(s => lower.Contains(s))) Take(o.Name);
        }
        return gone;
    }

    static string Mode(HafMaterial mat) => string.IsNullOrEmpty(mat.AlphaMode) ? "OPAQUE" : mat.AlphaMode;   // the importer: `alpha_mode or 'OPAQUE'`
    static bool Unlit(HafMaterial mat) => mat.ExtensionsJson != null && mat.ExtensionsJson.Contains("KHR_materials_unlit") && Newtonsoft.Json.Linq.JObject.Parse(mat.ExtensionsJson)["KHR_materials_unlit"] != null;
    static bool Png(byte[] b) => b != null && b.Length > 3 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47;
    static bool Jpeg(byte[] b) => b != null && b.Length > 2 && b[0] == 0xFF && b[1] == 0xD8;

    /// <summary>What the written materials and their images cannot be, or must not be, made to equal - named per material:
    /// - a factor outside 0..1 in what Blender writes (see the line below);
    /// - a base colour image the exporter does not pass through. It writes the ORIGINAL bytes only of an image it can
    ///   keep in its format: when the material's alpha is read from the texture (the importer wires it for every mode but
    ///   OPAQUE and a MASK at a cutoff of 0 or over 1) the image is written as PNG (material/image.py __gather_mime_type:
    ///   an "Alpha" socket forces it), so a JPEG is decoded and encoded again by Blender's own PNG writer - bytes this
    ///   cannot make (measured 2026-10-08: the Espana's 4,735-byte JPEG came out a 15,078-byte PNG). An image loaded from
    ///   a FILE takes its format from the file name, so a name whose extension is not its content's is encoded again
    ///   too; and an image that is neither PNG nor JPEG (WebP, KTX2, none) is not one the writer embeds.
    /// An unlit material's alpha goes to a Mix shader's factor, not an "Alpha" socket, so its JPEG would pass through:
    /// it is named all the same (an over-decline, on the safe side).</summary>
    static IEnumerable<string> MaterialProblems(HafModel m, Result res)
    {
        var sourceOf = new Dictionary<string, (int material, bool vertexColor)>();
        foreach (var kv in res.Names.MaterialOf) if (!sourceOf.ContainsKey(kv.Value)) sourceOf[kv.Value] = (kv.Key.material, kv.Key.vertexColor);
        foreach (var name in res.Tree.Materials)
        {
            var (src, vertexColor) = sourceOf[name];
            if (src < 0) continue;
            var mat = WrittenMaterial(m.Materials[src], vertexColor, null);
            // Blender writes these as they are (only the colour is clamped), and the converter's reader refuses a file
            // with one - measured on Blender's own output (2026-10-08): such a source does not bake today either
            if (mat.BaseColorFactor.Any(v => !(v >= 0 && v <= 1)) || !(mat.MetallicFactor >= 0f && mat.MetallicFactor <= 1f) || !(mat.RoughnessFactor >= 0f && mat.RoughnessFactor <= 1f))
                yield return $"factor-range: material '{name}' comes out of Blender with a factor outside 0..1 (it clamps the colour only), and the converter refuses such a file";
            if (mat.BaseColorTexture < 0) continue;
            int image = mat.BaseColorTexture < m.Textures.Count ? m.Textures[mat.BaseColorTexture].Source : -1;
            var im = image >= 0 && image < m.Images.Count ? m.Images[image] : null;
            bool png = Png(im?.Bytes), jpeg = Jpeg(im?.Bytes);
            if (!png && !jpeg) { yield return $"image-format: material '{name}' has a base colour texture whose image is neither a PNG nor a JPEG (or is not there)"; continue; }
            string uri = im.Uri ?? "";
            if (uri.Length > 0 && !uri.StartsWith("data:", StringComparison.Ordinal))
            {
                string ext = System.IO.Path.GetExtension(uri.Split('?')[0]).ToLowerInvariant();
                if (!(png ? ext == ".png" : ext == ".jpg" || ext == ".jpeg"))
                    yield return $"image-uri: material '{name}' has a base colour image '{uri}' whose name is not its format's: Blender writes it again in the name's format";
            }
            string mode = Mode(mat);
            bool alphaRead = mode != "OPAQUE" && !(mode == "MASK" && (mat.AlphaCutoff == 0 || mat.AlphaCutoff > 1));
            if (alphaRead && jpeg) yield return $"jpeg-alpha: material '{name}' reads its alpha from a JPEG: Blender writes that image again as PNG";
        }
        // every image is carried, used or not, and the writer embeds PNG and JPEG only
        for (int i = 0; i < m.Images.Count; i++)
            if (!Png(m.Images[i].Bytes) && !Jpeg(m.Images[i].Bytes)) { yield return $"image-format: image {i} '{m.Images[i].Name}' is neither a PNG nor a JPEG: the writer cannot embed it"; break; }
    }

    /// <summary>The file as the exporter lays it out: the tree's nodes, a mesh per mesh node in the order the serializer
    /// reaches them, one skin per armature, the materials at first use.</summary>
    static HafModel Assemble(HafModel m, Result res)
    {
        var tree = res.Tree; var names = res.Names;
        var o = new HafModel();
        // extensionsUsed is the writer's to make (from the payloads it carries); nothing is REQUIRED: Blender's exporter
        // marks no material extension required, and a required name the converter's reader does not know would make it
        // refuse this file alone
        // textures, images and samplers as the source has them, so every material's references hold
        foreach (var t in m.Textures) o.Textures.Add(new HafTexture { Name = t.Name, Source = t.Source, Sampler = t.Sampler });
        foreach (var im in m.Images) o.Images.Add(new HafImage { Name = im.Name, MimeType = im.MimeType, Uri = im.Uri, Bytes = im.Bytes });
        foreach (var s in m.Samplers) o.Samplers.Add(s);
        // materials: the Blender material of each name back to its glTF material (and whether it is the vertex-colour variant)
        var sourceOf = new Dictionary<string, (int material, bool vertexColor)>();
        foreach (var kv in names.MaterialOf) if (!sourceOf.ContainsKey(kv.Value)) sourceOf[kv.Value] = (kv.Key.material, kv.Key.vertexColor);
        foreach (var name in tree.Materials)
        {
            var (src, vertexColor) = sourceOf[name];
            HafMaterial mat;
            if (src >= 0) mat = WrittenMaterial(m.Materials[src], vertexColor, res.Notes);
            // the importer's material for a coloured primitive without one, as the exporter writes it (measured: no base
            // colour, metallic 0, roughness 0.5, double sided)
            else mat = new HafMaterial { MetallicFactor = 0f, RoughnessFactor = 0.5f, DoubleSided = true };
            mat.Name = name; mat.NameAbsent = false;
            o.Materials.Add(mat);
        }
        // meshes, in the order the serializer reaches them
        var runOfNode = res.Objects.ToDictionary(r => r.Node, r => r);
        foreach (int meshNode in tree.MeshVisitOrder)
        {
            var run = runOfNode[meshNode];
            var mesh = new HafMesh { Name = m.Meshes[m.Nodes[meshNode].Mesh].Name };
            var mats = res.MaterialsOfMesh[meshNode];
            for (int i = 0; i < run.Primitives.Count; i++)
            {
                var p = run.Primitives[i];
                var hp = new HafPrimitive { Mode = 4, VertexCount = p.VertexCount, Positions = p.Positions, Normals = p.Normals, Indices = p.Indices, Material = mats[i] == null ? -1 : tree.Materials.IndexOf(mats[i]) };
                if (p.Uv.Count > 0) hp.Uv0 = p.Uv[0];
                if (p.Uv.Count > 1) hp.Uv1 = p.Uv[1];
                if (p.Uv.Count > 2) hp.UvMore = p.Uv.Skip(2).ToList();
                if (p.Colors.Count > 0) hp.Colors = Rgba(p.Colors[0], p.VertexCount);
                if (p.Colors.Count > 1) hp.ColorMore = p.Colors.Skip(1).Select(c => Rgba(c, p.VertexCount)).ToList();
                if (p.Joints != null) { hp.Joints = p.Joints; hp.Weights = p.Weights; }
                mesh.Primitives.Add(hp);
            }
            o.Meshes.Add(mesh);
        }
        // nodes and skins
        var skinOfArmature = new Dictionary<int, int>();
        for (int i = 0; i < tree.Nodes.Count; i++)
        {
            var n = tree.Nodes[i];
            var hn = new HafNode { Name = n.Name, Parent = n.Parent };
            hn.Children.AddRange(n.Children);
            if (n.Translation != null) hn.Translation = n.Translation.Select(x => (double)x).ToArray();
            if (n.Rotation != null) hn.Rotation = n.Rotation.Select(x => (double)x).ToArray();
            if (n.Scale != null) hn.Scale = n.Scale.Select(x => (double)x).ToArray();
            if (n.HasMesh && n.Object != null) hn.Mesh = tree.MeshVisitOrder.IndexOf(n.Object.MeshNode);
            if (n.SkinJoints != null)
            {
                if (!skinOfArmature.TryGetValue(n.SkinArmature, out int si))
                {
                    si = o.Skins.Count; skinOfArmature[n.SkinArmature] = si;
                    var ibm = new double[16 * n.SkinJoints.Count];
                    for (int j = 0; j < n.SkinJoints.Count; j++) for (int k = 0; k < 16; k++) ibm[16 * j + k] = tree.Nodes[n.SkinJoints[j]].InverseBind[k];
                    o.Skins.Add(new HafSkin { Name = tree.Nodes[n.Parent].Name, Joints = n.SkinJoints.ToArray(), InverseBindMatrices = ibm });
                }
                hn.Skin = si;
            }
            o.Nodes.Add(hn);
        }
        // the skins no node uses, after the others (BlenderExportTree.Result.UnusedSkins)
        foreach (var (armature, name, joints) in tree.UnusedSkins)
        {
            var ibm = new double[16 * joints.Count];
            for (int j = 0; j < joints.Count; j++) for (int k = 0; k < 16; k++) ibm[16 * j + k] = tree.Nodes[joints[j]].InverseBind[k];
            o.Skins.Add(new HafSkin { Name = name, Joints = joints.ToArray(), InverseBindMatrices = ibm });
        }
        var scene = new HafScene { Name = "Scene" };
        scene.Nodes.AddRange(tree.SceneRoots);
        o.Scenes.Add(scene); o.Scene = 0;
        return o;
    }

    /// <summary>A source material as it comes back out of Blender, for what the converter reads of it (the rest is the
    /// source's own): specular-glossiness and unlit, the base colour factor, an alpha mode the schema knows.</summary>
    static HafMaterial WrittenMaterial(HafMaterial source, bool vertexColor, List<string> notes)
    {
        var mat = Copy(source);
        SpecularGlossiness(mat, notes);
        // exp/material/materials.py: an unlit material is written with metallic 0 and roughness 0.9 whatever the
        // source says - so it always has a pbrMetallicRoughness object (the converter's white swatch, not its grey)
        if (Unlit(mat)) { mat.MetallicFactor = 0f; mat.RoughnessFactor = 0.9f; mat.MetallicRoughnessTexture = -1; mat.MetallicRoughnessTexCoord = 0; if (notes != null && !notes.Contains("unlit")) notes.Add("unlit"); }
        mat.BaseColorFactor = WrittenBaseColour(mat, vertexColor, Unlit(mat));
        // an alpha mode the schema does not know makes the converter's reader refuse the whole file (measured: "").
        // The importer reads an empty one as OPAQUE and anything but OPAQUE and MASK as a blend; Blender writes
        // one of the three. WHICH of them it writes is not modelled beyond that (it drops MASK at a cutoff of 0
        // and BLEND at an alpha of 1, for one) - the converter does not read the mode
        string mode = Mode(mat);
        mat.AlphaMode = mode == "OPAQUE" || mode == "MASK" || mode == "BLEND" ? mode : "BLEND";
        return mat;
    }

    /// <summary>KHR_materials_pbrSpecularGlossiness: the importer takes the extension's diffuse colour and texture as the
    /// base colour (metallic 0, roughness 1 - glossiness), and the exporter writes a metallic-roughness material - the
    /// extension is gone from the file (measured on a Lab source, 2026-10-08: 22 materials of one ship). With
    /// KHR_materials_unlit beside it the importer takes the unlit path and never looks at it (imp/material.py): the
    /// extension goes, the core values stay. True when the diffuse values were taken.</summary>
    static bool SpecularGlossiness(HafMaterial mat, List<string> notes)
    {
        const string ext = "KHR_materials_pbrSpecularGlossiness";
        if (mat.ExtensionsJson == null || !mat.ExtensionsJson.Contains(ext)) return false;
        var all = Newtonsoft.Json.Linq.JObject.Parse(mat.ExtensionsJson);
        if (!(all[ext] is Newtonsoft.Json.Linq.JObject sg)) return false;
        bool taken = all["KHR_materials_unlit"] == null;
        if (taken)
        {
            mat.BaseColorFactor = sg["diffuseFactor"] is Newtonsoft.Json.Linq.JArray df && df.Count == 4 ? df.Select(x => (double)x).ToArray() : new double[] { 1, 1, 1, 1 };
            if (sg["diffuseTexture"] is Newtonsoft.Json.Linq.JObject dt) { mat.BaseColorTexture = (int)dt["index"]; mat.BaseColorTexCoord = dt["texCoord"] != null ? (int)dt["texCoord"] : 0; }
            else { mat.BaseColorTexture = -1; mat.BaseColorTexCoord = 0; }
            // the importer's Principled for it: metallic 0, roughness 1 - glossiness (the converter makes a WHITE swatch of a
            // material that has a pbrMetallicRoughness object and a grey one of a material without: the Teutonic's 'White')
            mat.MetallicFactor = 0f;
            mat.RoughnessFactor = (float)(1.0 - (sg["glossinessFactor"] != null ? (double)sg["glossinessFactor"] : 1.0));
            mat.MetallicRoughnessTexture = -1; mat.MetallicRoughnessTexCoord = 0;
            if (notes != null && !notes.Contains("specular-glossiness")) notes.Add("specular-glossiness");
        }
        all.Remove(ext);
        mat.ExtensionsJson = all.Count > 0 ? all.ToString(Newtonsoft.Json.Formatting.None) : null;
        return taken;
    }

    /// <summary>baseColorFactor as it comes back out of Blender: through the importer's nodes (pbrMetallicRoughness.py
    /// base_color) and the exporter's reading of them. The colour is the factor in float32. The alpha is 1 for an OPAQUE
    /// material; for a material with neither a base colour texture nor vertex colours the importer sets the socket itself
    /// - under MASK to 1 or 0 by `alpha >= cutoff`, compared as the JSON doubles; with a texture or vertex colours a MASK
    /// at a cutoff of 0 is opaque (1), over 1 discards everything (the socket at 0), and every other case multiplies by
    /// the factor's alpha in float32 (measured 2026-10-08 on the 24 materials of export_layout; the converter makes a
    /// flat material's swatch from these four). A colour outside 0..1 is CLAMPED by the exporter
    /// (pbr_metallic_roughness.py) - so [1.5, 1.5, 1.5] is white and the factor is left out of the file. An unlit
    /// material's colour keeps what is over 1 (unlit.py does not clamp) but a negative component still comes back 0
    /// (read as "not clamped at all", measured otherwise). Neither the alpha, nor metallic, nor roughness is clamped:
    /// those come back as they went in (all measured on the export_flat_alpha fixture; a self-review of PR #130
    /// found the first version declining every one of them for a clamp Blender does not do).</summary>
    static double[] WrittenBaseColour(HafMaterial mat, bool vertexColor, bool unlit)
    {
        var f = mat.BaseColorFactor ?? new double[] { 1, 1, 1, 1 };
        double alpha = f[3], cutoff = mat.AlphaCutoff;
        string mode = Mode(mat);
        double a;
        if (mode == "OPAQUE") a = 1;
        else if (mat.BaseColorTexture < 0 && !vertexColor) a = mode == "MASK" ? (alpha >= cutoff ? 1 : 0) : (float)alpha;
        else if (mode == "MASK" && cutoff == 0) a = 1;
        else if (mode == "MASK" && cutoff > 1) a = 0;
        else a = (float)alpha;
        float C(double v) { float x = (float)v; return x < 0f ? 0f : x > 1f && !unlit ? 1f : x; }
        return new double[] { C(f[0]), C(f[1]), C(f[2]), a };
    }

    /// <summary>A colour set as the model holds one: four floats per vertex.</summary>
    static float[] Rgba(BlenderExport.ColorSet c, int vertices)
    {
        var a = new float[4 * vertices];
        for (int v = 0; v < vertices; v++)
            for (int k = 0; k < 4; k++)
                a[4 * v + k] = c.Forced ? 1f : c.Alpha ? (float)(c.Shorts[4 * v + k] / 65535.0) : k < 3 ? c.Data[3 * v + k] : 1f;
        return a;
    }

    /// <summary>Every field of a material, arrays cloned: by reflection, so a field added to HafMaterial later is carried
    /// without a word here.</summary>
    static HafMaterial Copy(HafMaterial s)
    {
        var d = new HafMaterial();
        foreach (FieldInfo f in typeof(HafMaterial).GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            object v = f.GetValue(s);
            f.SetValue(d, v is Array arr ? arr.Clone() : v);
        }
        return d;
    }
}
