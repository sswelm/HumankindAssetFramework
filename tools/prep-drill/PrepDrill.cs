using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

// THE MODEL PREP IN C#, DRILLED AGAINST BLENDER'S (step 5 milestone d): blender_prep_many.py ran the real prep_model.py
// and wrote GLBs; this reads its PREP rows, reduces every mesh object of the SOURCE as prep_model does (BlenderReduce at
// prep's ratio), lays each out as the glTF exporter does (BlenderExport), and holds the result to the mesh Blender wrote
// for the node of the same name - per primitive: the vertex count, positions, normals, every UV and colour set, a
// skinned mesh's joints and weights (and its joint list, by name) and the indices, bit for bit. One line per file and run:
//   PASS <key> <tag>: <n> objects, <p> primitives equal            FAIL <key> <tag>: <the first differences>
// and a TOTAL line. An object the port declines (BlenderReduce.FallbackReason) is counted, named.
static class PrepDrill
{
    static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.WriteLine($"RUNTIME\t{(IntPtr.Size * 8)}-bit\ttrig {(BlenderTrig.Exact ? "exact" : "rounded")}\tcolour table {(BlenderColor.TableKnown ? "known" : "unknown")}");
        int fails = 0, runs = 0, objects = 0, prims = 0, declined = 0, expectedFailures = 0; long verts = 0;
        var declinedWhy = new Dictionary<string, int>(); var leftToBlender = new SortedDictionary<string, int>();
        var cover = new SortedDictionary<string, long>();
        foreach (var k in CoverKeys) cover[k] = 0;
        HafModel source = null; string sourcePath = null; BlenderNames.Result names = null; float[][] bworld = null;
        foreach (var line in File.ReadAllLines(args[0]))
        {
            var t = line.Split('\t');
            if (t.Length >= 6 && t[0] == "PREPFAIL")
            {
                // prep_model.py itself failed. Known: a file of several scenes ("Object ... is not in View Layer") - the
                // Factory's bake of such a file fails today; anything else is a finding
                runs++;
                string sk = string.Join("/", t[1].Split('/').Reverse().Take(2).Reverse());
                try
                {
                    var failedModel = GlbReader.Read(t[1]);
                    if (failedModel.Scenes.Count > 1 && t[5].Contains("View Layer")) { expectedFailures++; Console.WriteLine($"PASS {sk} {t[2]}: prep_model.py fails on a file of {failedModel.Scenes.Count} scenes, as known ({t[5]})"); }
                    else { fails++; Console.WriteLine($"FAIL {sk} {t[2]}: prep_model.py failed for a reason this drill does not know: {t[5]}"); }
                }
                catch (Exception e) { fails++; Console.WriteLine($"FAIL {sk} {t[2]}: {e.GetType().Name}: {e.Message}"); }
                continue;
            }
            if (t.Length < 6 || t[0] != "PREP") continue;
            string key = t[1], tag = t[2]; long total = long.Parse(t[3]), target = long.Parse(t[4]); string outGlb = t[5];
            string shortKey = string.Join("/", key.Split('/').Reverse().Take(2).Reverse());
            runs++;
            try
            {
                if (sourcePath != key) { source = GlbReader.Read(key); sourcePath = key; names = BlenderNames.Compute(source); bworld = null; GC.Collect(); }   // the key is the path, lower-cased
                var m = source;
                var meshObjects = new List<(int node, string name, int faces)>();
                long myTotal = 0;
                foreach (var (node, name) in names.MeshObjectsInOrder)
                {
                    var layout = BlenderMesh.FromGltf(m, m.Nodes[node].Mesh);
                    if (layout.VertexCount == 0) continue;
                    meshObjects.Add((node, name, layout.Faces.Length / 3)); myTotal += layout.Faces.Length / 3;
                }
                var problems = new List<string>();
                if (myTotal != total) problems.Add($"triangle total C# {myTotal} vs Blender {total}");
                float ratio = BlenderReduce.Ratio(target, myTotal);
                var written = GlbReader.Read(outGlb);
                var nodeByName = new Dictionary<string, int>();
                for (int i = 0; i < written.Nodes.Count; i++) if (written.Nodes[i].Mesh >= 0 && !nodeByName.ContainsKey(written.Nodes[i].Name)) nodeByName[written.Nodes[i].Name] = i;
                int okObjects = 0, okPrims = 0;
                var prepared = new List<(string name, int armature, BlenderExport.Skin skin, List<string> joints, List<BlenderExport.Primitive> primitives)>();
                var materialsOfMesh = new Dictionary<int, List<string>>();   // per mesh node: the Blender material of each primitive written, in order (null for the empty slot)
                var neutralArmatures = new HashSet<int>();
                int declinedBefore = declined;
                foreach (var (node, name, faces) in meshObjects)
                {
                    bool skinned = m.Nodes[node].Skin >= 0 && m.Nodes[node].Skin < m.Skins.Count && m.Meshes[m.Nodes[node].Mesh].Primitives.Exists(p => p.Skinned);
                    string why = BlenderReduce.FallbackReason(m, node);
                    if (why != null) { declined++; declinedWhy[why] = declinedWhy.TryGetValue(why, out int c) ? c + 1 : 1; continue; }
                    // a skinned mesh hangs from its armature with no transform of its own: both matrices are the armature's.
                    // The exported joints are the armature's bones in creation order; group i is the skin's joint i
                    BlenderExport.Skin skinLayout = null; List<string> jointNames = null;
                    if (skinned)
                    {
                        int si = m.Nodes[node].Skin, an = names.ArmatureNodeOfSkin[si];
                        if (bworld == null) bworld = VehicleProbe.BlenderWorldMatrices(m, null);
                        var arma = an >= 0 ? bworld[an] : new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
                        var place = new Dictionary<int, int>(); jointNames = new List<string>();
                        foreach (int b in names.BoneNodesInOrder) if (names.ArmatureNodeOfBone[b] == an) { place[b] = place.Count; jointNames.Add(names.BoneOfJoint[b]); }
                        skinLayout = new BlenderExport.Skin { ObjectWorld = arma, ArmatureWorld = arma, JointCount = place.Count, GroupJoint = m.Skins[si].Joints.Select(j => place.TryGetValue(j, out int at) ? at : -1).ToArray() };
                    }
                    var r = BlenderReduce.Reduce(m, node, ratio, names);
                    var mine = BlenderExport.MeshPrimitives(r, skinLayout);
                    materialsOfMesh[node] = mine.Select(p => { var (mat, vc) = r.Slots[p.MaterialSlot]; return mat < 0 && !vc ? null : names.MaterialOf[(mat < 0 ? names.MeshDatablockNode[node] : -1, mat, vc)]; }).ToList();
                    Cover(m, r, BlenderExport.Validated(r), mine, cover);
                    if (skinLayout != null)
                    {
                        cover["a skinned object"]++;
                        if (mine.Count > 0 && mine[0].NeutralBone) cover["a vertex without a bone (the neutral bone)"]++;
                        if (r.DefNr != null && r.DefNr.Any(l => l != null && l.Count > 4)) cover["a vertex of more than four groups"]++;
                        bool identity = true; for (int i = 0; i < 16; i++) if (skinLayout.ArmatureWorld[i] != (i % 5 == 0 ? 1f : 0f)) identity = false;
                        if (!identity) cover["an armature that is not at the identity"]++;
                    }
                    int armature = skinLayout != null ? names.ArmatureNodeOfSkin[m.Nodes[node].Skin] : -1;
                    if (skinLayout != null && mine.Count > 0 && mine[0].NeutralBone) neutralArmatures.Add(armature);
                    prepared.Add((name, armature, skinLayout, jointNames, mine));
                }
                // Blender appends one neutral joint to the shared armature if ANY exported mesh needs it.
                // Collect that requirement from our layouts before comparing any object's skin, so mesh order cannot matter.
                foreach (var (name, armature, skinLayout, jointNames, mine) in prepared)
                {
                    if (skinLayout != null && mine.Count > 0 && !mine[0].NeutralBone && neutralArmatures.Contains(armature))
                        cover["a weighted mesh sharing an armature with a neutral bone"]++;
                    if (mine.Count == 0)
                    {
                        // an object without faces (lines, points): the exporter writes its node WITHOUT a mesh
                        if (nodeByName.ContainsKey(name)) problems.Add($"{name}: no faces, yet Blender's file has a mesh node of that name"); else okObjects++;
                        continue;
                    }
                    if (!nodeByName.TryGetValue(name, out int wn)) { problems.Add($"{name}: no mesh node of that name in Blender's file"); continue; }
                    if (skinLayout != null)
                    {
                        // the joints Blender wrote, by name, against the armature's bones in creation order (+ the neutral bone)
                        int ws = written.Nodes[wn].Skin;
                        if (ws < 0 || ws >= written.Skins.Count) { problems.Add($"{name}: skinned here, no skin in Blender's file"); continue; }
                        var theirJoints = written.Skins[ws].Joints.Select(j => written.Nodes[j].Name).ToList();
                        var myJoints = new List<string>(jointNames); if (neutralArmatures.Contains(armature)) myJoints.Add("neutral_bone");
                        if (!theirJoints.SequenceEqual(myJoints)) { problems.Add($"{name}: joints [{string.Join(",", myJoints.Take(6))}...] ({myJoints.Count}) vs Blender's [{string.Join(",", theirJoints.Take(6))}...] ({theirJoints.Count})"); continue; }
                    }
                    else if (written.Nodes[wn].Skin >= 0) { problems.Add($"{name}: Blender's file skins it, the layout does not"); continue; }
                    var theirs = written.Meshes[written.Nodes[wn].Mesh].Primitives.Where(p => p.Mode == 4).ToList();
                    if (theirs.Count != mine.Count) { problems.Add($"{name}: {mine.Count} primitives vs Blender's {theirs.Count}"); continue; }
                    bool objectOk = true;
                    for (int i = 0; i < mine.Count && objectOk; i++)
                    {
                        string d = Differ(mine[i], theirs[i]);
                        if (d != null) { problems.Add($"{name} primitive {i}: {d}"); objectOk = false; }
                        else { okPrims++; verts += mine[i].VertexCount; }
                    }
                    if (objectOk) okObjects++;
                }
                objects += okObjects; prims += okPrims;
                // the file's structure: every node Blender wrote, by index - name, parent, transform (BlenderExportTree)
                if (bworld == null) bworld = VehicleProbe.BlenderWorldMatrices(m, null);
                // a file with a declined object is Blender's to prep as a whole: its structure is not predicted (the tree would
                // not know whether the declined mesh has faces, nor its materials)
                if (declined > declinedBefore) { leftToBlender["an object of the file is declined"] = leftToBlender.TryGetValue("an object of the file is declined", out int lc) ? lc + 1 : 1; cover["left to Blender: an object of the file is declined"]++; }
                else Structure(m, names, bworld, written, problems, cover, new HashSet<int>(materialsOfMesh.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key)), neutralArmatures, materialsOfMesh, leftToBlender);
                if (problems.Count > 0) { fails++; Console.WriteLine($"FAIL {shortKey} {tag}: " + string.Join("; ", problems.Take(4)) + (problems.Count > 4 ? $"; ... {problems.Count - 4} more" : "")); }
                else Console.WriteLine($"PASS {shortKey} {tag}: {okObjects} objects, {okPrims} primitives equal (ratio {ratio:R})");
            }
            catch (Exception e) { fails++; Console.WriteLine($"FAIL {shortKey} {tag}: {e.GetType().Name}: {e.Message}"); }
        }
        foreach (var kv in declinedWhy) Console.WriteLine($"NOTE {kv.Value} object runs not compared: {kv.Key}");
        foreach (var kv in leftToBlender) Console.WriteLine($"NOTE {kv.Value} runs whose structure is left to Blender: {kv.Key}");
        // a rule no compared object exercised was not held to Blender by this run: the script fails on a zero it expects filled
        foreach (var kv in cover) Console.WriteLine($"COVER {kv.Value} {kv.Key}");
        Console.WriteLine($"TOTAL runs {runs} failed {fails} objects {objects} primitives {prims} vertices {verts} declined {declined} prepfails {expectedFailures}");
        return fails == 0 ? 0 : 1;
    }

    static readonly string[] CoverKeys =
    {
        "a skin's joint indices compared", "a faceless skinned object compared", "left to Blender: an object of the file is declined", "left to Blender: material-uv", "left to Blender: camera-children", "left to Blender: lights", "a camera left out", "an object hung from a node that bears its parent bone's name",
        "bones: two or more root bones", "bones: a bone child nearer than 0.004 (no length taken from it)", "bones: a bone of length 1 (no bone child, no parent bone, at its parent's origin)", "bones: an edit bone of no length (its tail moved along Z)", "bones: an edit bone shorter than 1e-6 (its tail moved along the bone)",
        "bones: a skeleton that is not a joint, on a skin with inverse bind matrices", "bones: a bone almost along -Y (the series for 1 + y)", "bones: a bone along -Y (the mirrored matrix)", "a node list compared (name, parent, order)", "a node transform compared", "a joint transform compared", "a skin's inverse bind matrices compared", "a neutral bone compared", "a material list compared (names, order)", "an object under a bone (its transform is the next part's)", "an animated file (its transforms are Blender's posed state, not compared)",
        "normals from the file (custom normals)", "no normals (every face flat)", "a normal that rounds to zero, made up", "a zero normal on a fan that does not point up", "an object of several primitives",
        "an object without faces", "two or more UV sets", "COLOR_0 as RGB (the material's colour)", "COLOR_0 with alpha (a face without material)",
        "COLOR_0 forced (255s)", "two or more colour sets", "a colour layer on the vertices (point domain)", "a twin face the exporter's validate removes",
        "COLOR_0 with alpha (a coloured material whose alpha is wired)", "COLOR_0 as RGB (a coloured MASK material at a cutoff of 0 or over 1)",
        "a coloured alpha material after twin-face validation", "a MASK cutoff whose float rounding crosses a wiring boundary",
        "COLOR_0 with alpha wired through a base colour texture", "COLOR_0 with alpha on an unlit material",
        "COLOR_0 as RGB after a non-unit alpha factor rounds to one", "COLOR_0 with alpha when the factor is exactly one",
        "a weighted mesh sharing an armature with a neutral bone",
        "a skinned object", "a vertex without a bone (the neutral bone)", "a vertex of more than four groups", "an armature that is not at the identity",
    };

    /// <summary>The written file's node list against the tree the exporter builds: count, each node's name, parent and
    /// transform bits, the scene's roots. Bones' transforms wait for the bone part; an animated file's transforms are
    /// Blender's posed import state, which the tree does not model (the Factory preps static entries).</summary>
    static void Structure(HafModel m, BlenderNames.Result names, float[][] bworld, HafModel written, List<string> problems, SortedDictionary<string, long> cover, ISet<int> meshNodesWithFaces, ISet<int> neutralArmatures, Dictionary<int, List<string>> materialsOfMesh, SortedDictionary<string, int> leftToBlender)
    {
        var tree = BlenderExportTree.Build(m, names, bworld, meshNodesWithFaces, neutralArmatures, mn => materialsOfMesh.TryGetValue(mn, out var l) ? l : new List<string>());
        if (tree.Problems.Count > 0)
        {
            foreach (var p in tree.Problems)
            {
                leftToBlender[p] = leftToBlender.TryGetValue(p, out int c) ? c + 1 : 1;
                string tag = "left to Blender: " + p.Substring(0, p.IndexOf(':'));
                if (cover.ContainsKey(tag)) cover[tag]++;
            }
            return;
        }
        int problemsBefore = problems.Count;
        // the materials: the same names in the same order (the properties are the writer part's)
        var wmats = written.Materials.Select(x => x.Name).ToList();
        if (!wmats.SequenceEqual(tree.Materials)) problems.Add($"structure: materials [{string.Join(",", tree.Materials.Take(6))}] ({tree.Materials.Count}) here vs [{string.Join(",", wmats.Take(6))}] ({wmats.Count}) written");
        else if (tree.Materials.Count > 0) cover["a material list compared (names, order)"]++;
        if (written.Nodes.Count != tree.Nodes.Count) { problems.Add($"structure: {tree.Nodes.Count} nodes here vs {written.Nodes.Count} written [{string.Join(",", tree.Nodes.Select(n => n.Name).Take(8))}] vs [{string.Join(",", written.Nodes.Select(n => n.Name).Take(8))}]"); return; }
        var wparent = new int[written.Nodes.Count]; for (int i = 0; i < wparent.Length; i++) wparent[i] = -1;
        for (int i = 0; i < written.Nodes.Count; i++) foreach (int c in written.Nodes[i].Children) wparent[c] = i;
        bool animated = m.Animations.Count > 0;
        if (animated) cover["an animated file (its transforms are Blender's posed state, not compared)"]++;
        int shown = 0;
        for (int i = 0; i < tree.Nodes.Count && shown < 4; i++)
        {
            var n = tree.Nodes[i]; var w = written.Nodes[i];
            if (n.Name != w.Name) { problems.Add($"structure: node {i} is '{n.Name}' here, '{w.Name}' written"); shown++; continue; }
            if (n.Parent != wparent[i]) { problems.Add($"structure: node {i} '{n.Name}' under {n.Parent} here, {wparent[i]} written"); shown++; continue; }
            if (n.HasMesh != (w.Mesh >= 0)) { problems.Add($"structure: node {i} '{n.Name}' {(w.Mesh >= 0 ? "has a mesh in the file, none here" : "has a mesh here, none in the file")}"); shown++; continue; }
            // the children ARRAY, in order: the converter walks it (a parent per node alone does not hold the order)
            if (!n.Children.SequenceEqual(w.Children)) { problems.Add($"structure: node {i} '{n.Name}' children [{string.Join(",", n.Children)}] here vs [{string.Join(",", w.Children)}] written"); shown++; continue; }
            if (!n.HasMesh && n.Object != null && n.Object.Skin >= 0) cover["a faceless skinned object compared"]++;
            // an object under a parent that is not its glTF node's (a camera between them went), or under an OBJECT though it is parented to a bone
            if (n.Object != null && n.Parent >= 0 && tree.Nodes[n.Parent].Object != null)
            {
                if (n.Object.ParentBone != null) cover["an object hung from a node that bears its parent bone's name"]++;
            }
            if (!n.TransformKnown) { cover["an object under a bone (its transform is the next part's)"]++; continue; }
            if (animated && n.Object != null) continue;
            if (w.HasMatrix) { problems.Add($"structure: node {i} '{n.Name}' written with a matrix"); shown++; continue; }
            string d = Trs("translation", n.Translation, w.Translation, 0, 0, 0) ?? Trs("rotation", n.Rotation, w.Rotation, 0, 0, 0, 1) ?? Trs("scale", n.Scale, w.Scale, 1, 1, 1);
            if (d != null) { problems.Add($"structure: node {i} '{n.Name}' {d}"); shown++; continue; }
            cover["a node transform compared"]++;
            if (n.BoneNode >= 0) cover["a joint transform compared"]++;
            if (n.NeutralBone) cover["a neutral bone compared"]++;
        }
        // The source armature fixes the joint indices: names and bind matrices can coincide across armatures.
        var boneIndex = tree.Nodes.Select((n, i) => (n, i)).Where(x => x.n.BoneNode >= 0).ToDictionary(x => x.n.BoneNode, x => x.i);
        for (int i = 0; i < written.Nodes.Count && shown < 4; i++)
        {
            var w = written.Nodes[i];
            var n = tree.Nodes[i]; var o = n.Object;
            bool expectsSkin = n.HasMesh && o != null && o.Skin >= 0 && m.Meshes[m.Nodes[o.MeshNode].Mesh].Primitives.Exists(p => p.Skinned);
            if (!expectsSkin)
            {
                if (w.Skin >= 0) { problems.Add("structure: node '" + w.Name + "' has an unexpected skin"); shown++; }
                continue;
            }
            if (w.Skin < 0 || w.Skin >= written.Skins.Count) { problems.Add("structure: node '" + w.Name + "' is missing its skin"); shown++; continue; }
            int armature = names.ArmatureNodeOfSkin[o.Skin];
            var expectedJoints = names.BoneNodesInOrder.Where(b => names.ArmatureNodeOfBone[b] == armature).Select(b => boneIndex[b]).ToList();
            if (neutralArmatures.Contains(armature))
            {
                int armatureIndex = tree.Nodes.FindIndex(x => x.Object != null && x.Object.Kind == BlenderNames.ObjectKind.Armature && x.Object.GltfNode == armature);
                expectedJoints.Add(tree.Nodes.FindIndex(x => x.NeutralBone && x.ArmatureNode == armatureIndex));
            }
            var sk = written.Skins[w.Skin];
            if (!expectedJoints.SequenceEqual(sk.Joints))
            { problems.Add("structure: skin of '" + w.Name + "' joint indices [" + string.Join(",", expectedJoints) + "] here vs [" + string.Join(",", sk.Joints) + "] written"); shown++; continue; }
            cover["a skin's joint indices compared"]++;
            if (sk.Skeleton >= 0) { problems.Add("structure: skin of '" + w.Name + "' written with a skeleton (the exporter names none)"); shown++; }
            bool all = true;
            for (int j = 0; j < sk.Joints.Length && all; j++)
            {
                var jn = tree.Nodes[expectedJoints[j]];
                if (jn.InverseBind == null) { problems.Add("structure: skin of '" + w.Name + "' joint '" + jn.Name + "' inverse bind matrix not predicted"); shown++; all = false; break; }
                for (int k = 0; k < 16; k++)
                {
                    // glTF defaults an omitted accessor to identity; absence must still be compared.
                    float actual = sk.InverseBindMatrices == null ? (k % 5 == 0 ? 1f : 0f) : (float)sk.InverseBindMatrices[16 * j + k];
                    if (BitConverter.ToUInt32(BitConverter.GetBytes(jn.InverseBind[k]), 0) != BitConverter.ToUInt32(BitConverter.GetBytes(actual), 0))
                    { problems.Add("structure: skin of '" + w.Name + "' joint " + j + " '" + jn.Name + "' inverse bind matrix entry " + k + ": " + jn.InverseBind[k].ToString("R") + " here vs " + actual.ToString("R") + " written" + (sk.InverseBindMatrices == null ? " (implicit identity)" : "")); shown++; all = false; break; }
                }
            }
            if (all) cover["a skin's inverse bind matrices compared"]++;
        }
        if (!tree.SceneRoots.SequenceEqual(written.Scenes.Count > 0 ? written.Scenes[written.Scene < 0 ? 0 : written.Scene].Nodes : new List<int>()))
            problems.Add($"structure: scene roots [{string.Join(",", tree.SceneRoots)}] here vs [{string.Join(",", written.Scenes.Count > 0 ? written.Scenes[written.Scene < 0 ? 0 : written.Scene].Nodes : new List<int>())}] written");
        if (problems.Count == problemsBefore)
        {
            cover["a node list compared (name, parent, order)"]++;
            if (tree.CamerasLeftOut > 0) cover["a camera left out"]++;
            // a branch of the bone chain counts when the file that took it compared equal, joints and inverse bind matrices
            foreach (var note in tree.Notes) if (cover.ContainsKey("bones: " + note)) cover["bones: " + note]++; else problems.Add("structure: the bone chain notes a branch the drill has no row for: " + note);
        }
    }

    static string Trs(string what, float[] mine, double[] theirs, params double[] identity)
    {
        for (int k = 0; k < identity.Length; k++)
        {
            float a = mine != null ? mine[k] : (float)identity[k];
            if (BitConverter.ToUInt32(BitConverter.GetBytes(a), 0) != BitConverter.ToUInt32(BitConverter.GetBytes((float)theirs[k]), 0))
                return $"{what} [{string.Join(",", (mine ?? identity.Select(x => (float)x).ToArray()).Select(x => x.ToString("R")))}] here vs [{string.Join(",", theirs.Select(x => ((float)x).ToString("R")))}] written";
        }
        return null;
    }

    /// <summary>Which of the layout's rules this object exercised (counted per object run).</summary>
    static void Cover(HafModel model, BlenderReduce.Result reduced, BlenderReduce.Result r, List<BlenderExport.Primitive> mine, SortedDictionary<string, long> cover)
    {
        void Hit(string k, bool yes) { if (yes) cover[k]++; }
        Hit("a twin face the exporter's validate removes", r.Faces.Length < reduced.Faces.Length);
        Hit("an object without faces", mine.Count == 0);
        if (mine.Count == 0) return;
        Hit("normals from the file (custom normals)", r.CustomNormal != null);
        Hit("no normals (every face flat)", r.CustomNormal == null && r.FaceSharp.All(s => s));
        Hit("an object of several primitives", mine.Count > 1);
        Hit("two or more UV sets", r.Uv.Count > 1);
        var sets = mine[0].Colors;
        Hit("COLOR_0 as RGB (the material's colour)", sets.Count > 0 && !sets[0].Alpha);
        // the deciding slot's material: one with alpha splits by whether the importer wired the vertex alpha into it
        int deciding = -1;
        foreach (int slot in new SortedSet<int>(r.FaceMaterial)) { var (mat, vc) = r.Slots[slot]; if (vc) { deciding = slot; break; } if (mat < 0) break; }
        bool alphaMaterial = deciding >= 0 && r.Slots[deciding].material >= 0 && r.SlotAlpha[deciding].mode != "OPAQUE";
        Hit("COLOR_0 with alpha (a face without material)", sets.Count > 0 && sets[0].Alpha && !sets[0].Forced && !alphaMaterial);
        Hit("COLOR_0 forced (255s)", sets.Count > 0 && sets[0].Forced);
        Hit("two or more colour sets", sets.Count > 1);
        Hit("COLOR_0 with alpha (a coloured material whose alpha is wired)", alphaMaterial && sets.Count > 0 && sets[0].Alpha);
        Hit("COLOR_0 as RGB (a coloured MASK material at a cutoff of 0 or over 1)", alphaMaterial && r.SlotAlpha[deciding].mode == "MASK" && sets.Count > 0 && !sets[0].Alpha);
        Hit("a coloured alpha material after twin-face validation", alphaMaterial && r.Faces.Length < reduced.Faces.Length && sets.Count > 0 && sets[0].Alpha);
        bool roundedBoundary = false;
        if (alphaMaterial && r.SlotAlpha[deciding].mode == "MASK")
        {
            double cutoff = r.SlotAlpha[deciding].cutoff;
            roundedBoundary = (cutoff > 1 && (float)cutoff == 1f) || (cutoff != 0 && (float)cutoff == 0f);   // 1.00000001, 1e-50, -1e-50
        }
        Hit("a MASK cutoff whose float rounding crosses a wiring boundary", roundedBoundary);
        bool wired = alphaMaterial && sets.Count > 0 && sets[0].Alpha;
        bool blend = alphaMaterial && r.SlotAlpha[deciding].mode == "BLEND";
        double factor = alphaMaterial ? r.SlotAlpha[deciding].factor : 1.0;
        Hit("COLOR_0 as RGB after a non-unit alpha factor rounds to one", blend && factor != 1.0 && (float)factor == 1f && sets.Count > 0 && !sets[0].Alpha);
        Hit("COLOR_0 with alpha when the factor is exactly one", blend && factor == 1.0 && wired);
        Hit("COLOR_0 with alpha wired through a base colour texture", wired && model.Materials[r.Slots[deciding].material].BaseColorTexture >= 0);
        Hit("COLOR_0 with alpha on an unlit material", wired && (model.Materials[r.Slots[deciding].material].ExtensionsJson ?? "").Contains("KHR_materials_unlit"));
        Hit("a colour layer on the vertices (point domain)", r.Colors.Exists(c => c.point));
        // the corner normals once more, before the exporter's rounding: does any round to the zero vector
        int nc = r.Faces.Length; short[] d0 = null, d1 = null;
        if (r.CustomNormal != null) { d0 = new short[nc]; d1 = new short[nc]; for (int c = 0; c < nc; c++) { d0[c] = r.CustomNormal[2 * c]; d1[c] = r.CustomNormal[2 * c + 1]; } }
        var cn = VehicleProbe.BlenderCornerNormals(r.Positions, r.Faces, r.FaceSharp, d0, d1);
        // ... and is one of those on a fan whose OWN normal is not +Z: only there does "the exporter makes a zero up" differ
        // from "an invalid space keeps the fan's normal" (a triangle of no area has the normal +Z, so it cannot tell)
        bool zero = false, zeroAway = false; float[] fan = null;
        for (int c = 0; c < nc && !zeroAway; c++)
        {
            if (!(BlenderExport.Round4(cn[3 * c]) == 0f && BlenderExport.Round4(cn[3 * c + 1]) == 0f && BlenderExport.Round4(cn[3 * c + 2]) == 0f)) continue;
            zero = true;
            if (fan == null) fan = VehicleProbe.BlenderFanNormals(r.Positions, r.Faces, r.FaceSharp);
            zeroAway = !(BlenderExport.Round4(fan[3 * c]) == 0f && BlenderExport.Round4(fan[3 * c + 1]) == 0f && BlenderExport.Round4(fan[3 * c + 2]) == 1f)
                    && !(BlenderExport.Round4(fan[3 * c]) == 0f && BlenderExport.Round4(fan[3 * c + 1]) == 0f && BlenderExport.Round4(fan[3 * c + 2]) == 0f);
        }
        Hit("a normal that rounds to zero, made up", zero);
        Hit("a zero normal on a fan that does not point up", zeroAway);
    }

    static string Differ(BlenderExport.Primitive a, HafPrimitive b)
    {
        if (a.VertexCount != b.VertexCount) return $"{a.VertexCount} vertices vs Blender's {b.VertexCount}";
        string d;
        if ((d = Arr("positions", a.Positions, b.Positions, 3)) != null) return d;
        if ((d = Arr("normals", a.Normals, b.Normals, 3)) != null) return d;
        var uvs = new List<float[]>(); if (b.Uv0 != null) uvs.Add(b.Uv0); if (b.Uv1 != null) uvs.Add(b.Uv1); if (b.UvMore != null) uvs.AddRange(b.UvMore);
        if (uvs.Count != a.Uv.Count) return $"{a.Uv.Count} UV sets vs Blender's {uvs.Count}";
        for (int u = 0; u < uvs.Count; u++) if ((d = Arr("UV set " + u, a.Uv[u], uvs[u], 2)) != null) return d;
        // the colour sets as the reader decodes Blender's: RGB floats padded with alpha 1; a set with alpha arrives as
        // normalized shorts (short / 65535), the forced set as bytes of 255 (all ones)
        var theirColors = new List<float[]>(); if (b.Colors != null) theirColors.Add(b.Colors); if (b.ColorMore != null) theirColors.AddRange(b.ColorMore);
        if (theirColors.Count != a.Colors.Count) return $"{a.Colors.Count} colour sets vs Blender's {theirColors.Count}";
        for (int k = 0; k < a.Colors.Count; k++)
        {
            var set = a.Colors[k]; var expect = new float[4 * a.VertexCount];
            for (int i = 0; i < a.VertexCount; i++)
                for (int j = 0; j < 4; j++)
                    expect[4 * i + j] = set.Forced ? 1f : set.Alpha ? (float)(set.Shorts[4 * i + j] / 65535.0) : j < 3 ? set.Data[3 * i + j] : 1f;
            if ((d = Arr("colour set " + k, expect, theirColors[k], 4)) != null) return d;
        }
        if ((a.Joints != null) != (b.Joints != null)) return a.Joints != null ? "joints here, none in Blender's file" : "Blender wrote joints, the layout none";
        if (a.Joints != null)
        {
            if (b.Joints1 != null) return "Blender wrote a second set of joints";
            for (int i = 0; i < a.Joints.Length; i++) if (a.Joints[i] != b.Joints[i]) return $"joint {i % 4} of vertex {i / 4} is {a.Joints[i]} vs Blender's {b.Joints[i]} (weights {a.Weights[i]:R} vs {b.Weights[i]:R})";
            if ((d = Arr("weights", a.Weights, b.Weights, 4)) != null) return d;
        }
        int[] bi = b.Indices ?? Enumerable.Range(0, b.VertexCount).ToArray();
        if (a.Indices.Length != bi.Length) return $"{a.Indices.Length} indices vs Blender's {bi.Length}";
        for (int i = 0; i < bi.Length; i++) if (a.Indices[i] != bi[i]) return $"index {i} is {a.Indices[i]} vs Blender's {bi[i]}";
        return null;
    }

    static string Arr(string what, float[] a, float[] b, int per)
    {
        if (b == null) return what + ": Blender wrote none";
        if (a.Length != b.Length) return $"{what}: {a.Length / per} vs Blender's {b.Length / per}";
        int differ = 0, first = -1;
        for (int i = 0; i < a.Length; i++)
            if (BitConverter.ToUInt32(BitConverter.GetBytes(a[i]), 0) != BitConverter.ToUInt32(BitConverter.GetBytes(b[i]), 0)) { differ++; if (first < 0) first = i; }
        if (differ == 0) return null;
        int v = first / per;
        string Row(float[] x) => string.Join(" ", Enumerable.Range(0, per).Select(k => x[v * per + k].ToString("R")));
        return $"{what} differ in {differ} of {a.Length} values, first at vertex {v}: {Row(a)} vs Blender's {Row(b)}";
    }
}
