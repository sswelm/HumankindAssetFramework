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
        int fails = 0, runs = 0, objects = 0, prims = 0, declined = 0, expectedFailures = 0, writtenRuns = 0, leftRuns = 0; long verts = 0;
        // the Factory's converter and a folder to run it in (arguments 2 and 3); without them the written stage is skipped, said
        string converter = args.Length > 2 && File.Exists(args[1]) ? args[1] : null, workDir = args.Length > 2 ? args[2] : null;
        Console.WriteLine(converter != null ? "CONVERTER\t" + converter : "CONVERTER\tnone: the written files are NOT compared");
        var declinedWhy = new Dictionary<string, int>(); var leftToBlender = new SortedDictionary<string, int>(); var leftFiles = new HashSet<string>();
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
                // the PRODUCTION prep (BlenderPrep.Prepare) does the work; `diagnose` keeps it going past a reason to fall
                // back, so everything it can lay out is still compared
                var prep = BlenderPrep.Prepare(m, target, diagnose: true, names: names);
                if (prep.World != null) bworld = prep.World;
                long myTotal = prep.SourceTriangles;
                var problems = new List<string>(); string outcome = "";
                if (myTotal != total) problems.Add($"triangle total C# {myTotal} vs Blender {total}");
                float ratio = prep.Ratio;
                var written = GlbReader.Read(outGlb);
                var nodeByName = new Dictionary<string, int>();
                for (int i = 0; i < written.Nodes.Count; i++) if (written.Nodes[i].Mesh >= 0 && !nodeByName.ContainsKey(written.Nodes[i].Name)) nodeByName[written.Nodes[i].Name] = i;
                int okObjects = 0, okPrims = 0;
                var prepared = new List<(string name, int armature, BlenderExport.Skin skin, List<string> joints, List<BlenderExport.Primitive> primitives)>();
                var materialsOfMesh = prep.MaterialsOfMesh;   // per mesh node: the Blender material of each primitive written, in order (null for the empty slot)
                var neutralArmatures = prep.NeutralArmatures;
                int declinedBefore = declined;
                foreach (var run in prep.Objects)
                {
                    if (run.Declined != null) { declined++; declinedWhy[run.Declined] = declinedWhy.TryGetValue(run.Declined, out int c) ? c + 1 : 1; continue; }
                    var r = run.Reduced; var mine = run.Primitives; var skinLayout = run.Skin;
                    Cover(m, r, BlenderExport.Validated(r), mine, cover);
                    if (skinLayout != null)
                    {
                        cover["a skinned object"]++;
                        if (mine.Count > 0 && mine[0].NeutralBone) cover["a vertex without a bone (the neutral bone)"]++;
                        if (r.DefNr != null && r.DefNr.Any(l => l != null && l.Count > 4)) cover["a vertex of more than four groups"]++;
                        bool identity = true; for (int i = 0; i < 16; i++) if (skinLayout.ArmatureWorld[i] != (i % 5 == 0 ? 1f : 0f)) identity = false;
                        if (!identity) cover["an armature that is not at the identity"]++;
                    }
                    prepared.Add((run.Name, skinLayout != null ? run.Armature : -1, skinLayout, run.JointNames, mine));
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
                else Structure(m, names, bworld, written, problems, cover, prep.Tree, neutralArmatures, materialsOfMesh, leftToBlender);
                // every other reason the production prep names for leaving the file to Blender (the tree's own are counted above)
                foreach (var reason in prep.Reasons)
                {
                    if (reason.StartsWith("'") || (prep.Tree != null && prep.Tree.Problems.Contains(reason))) continue;
                    string kind = reason.Split(':')[0];
                    leftToBlender[kind] = leftToBlender.TryGetValue(kind, out int kc) ? kc + 1 : 1;
                    if (cover.ContainsKey("left to Blender: " + kind)) cover["left to Blender: " + kind]++;
                }
                // the WRITTEN file: what the Factory's converter makes of it against what it makes of Blender's, byte for byte
                if (prep.Model != null && problems.Count == 0 && converter != null)
                {
                    // the file as WRITTEN and read back - not the model in memory: the writer is part of what is judged
                    string myGlb = Path.Combine(workDir, "in_cs", "model.glb");
                    Directory.CreateDirectory(Path.GetDirectoryName(myGlb));
                    GlbWriter.Write(prep.Model, myGlb);
                    var reread = GlbReader.Read(myGlb);
                    var hits = new List<string>();
                    string d = Assembled(reread, written) ?? Materials(reread, written, myGlb, outGlb, hits) ?? Written(myGlb, outGlb, converter, workDir, hits, reread.Skins.Count > 0);
                    if (d != null) problems.Add("written: " + d);
                    else
                    {
                        // counted only now: every comparison of this run held
                        outcome = "written, and the converter reads it as it reads Blender's";
                        writtenRuns++; cover["a written file the converter reads as it reads Blender's"]++;
                        foreach (var h in hits) cover[h]++;
                        foreach (var note in prep.Notes) if (cover.ContainsKey("material: " + note)) cover["material: " + note]++; else problems.Add("the prep notes a material rule the drill has no row for: " + note);
                    }
                }
                else if (prep.Model == null && prep.Fallback == null) problems.Add("the prep gave neither a file nor a reason");
                else if (prep.Model == null && problems.Count == 0)
                {
                    // a reason that claims something about Blender's file is held to it: the converter must refuse that file
                    if (prep.Reasons.Any(x => x.StartsWith("factor-range:")) && converter != null)
                    {
                        string refused = Convert(converter, outGlb, Path.Combine(workDir, "out_range"), 0);
                        if (refused == null) problems.Add("left to Blender for a factor outside 0..1, yet the converter reads Blender's file");
                        else cover["left to Blender: factor-range (the converter refuses Blender's file too)"]++;
                    }
                    if (problems.Count == 0) { outcome = "LEFT TO BLENDER: " + prep.Fallback; leftRuns++; }
                }
                else if (problems.Count == 0) problems.Add("a prepared file that was not judged (no converter)");
                if (problems.Count > 0) { fails++; Console.WriteLine($"FAIL {shortKey} {tag}: " + string.Join("; ", problems.Take(4)) + (problems.Count > 4 ? $"; ... {problems.Count - 4} more" : "")); }
                else
                {
                    Console.WriteLine($"PASS {shortKey} {tag}: {okObjects} objects, {okPrims} primitives equal (ratio {ratio:R}); {outcome}");
                    // a file left to Blender, by name and reason, once per file (the script prints these: a count alone does not say WHICH)
                    if (prep.Model == null && leftFiles.Add(key)) Console.WriteLine($"LEFT {shortKey}: {prep.Fallback}");
                }
            }
            catch (Exception e) { fails++; Console.WriteLine($"FAIL {shortKey} {tag}: {e.GetType().Name}: {e.Message}"); }
        }
        foreach (var kv in declinedWhy) Console.WriteLine($"NOTE {kv.Value} object runs not compared: {kv.Key}");
        foreach (var kv in leftToBlender) Console.WriteLine($"NOTE {kv.Value} runs whose structure is left to Blender: {kv.Key}");
        // a rule no compared object exercised was not held to Blender by this run: the script fails on a zero it expects filled
        foreach (var kv in cover) Console.WriteLine($"COVER {kv.Value} {kv.Key}");
        // every run ends one way: failed, known to fail in prep_model.py, written and judged, or left to Blender by name
        if (runs != fails + expectedFailures + writtenRuns + leftRuns) { Console.WriteLine($"FAIL the runs do not add up: {runs} runs, {fails} failed + {expectedFailures} prep failures + {writtenRuns} written + {leftRuns} left to Blender"); fails++; }
        Console.WriteLine($"TOTAL runs {runs} failed {fails} objects {objects} primitives {prims} vertices {verts} declined {declined} prepfails {expectedFailures} written {writtenRuns} left {leftRuns}");
        return fails == 0 ? 0 : 1;
    }

    static readonly string[] CoverKeys =
    {
        "a node's mesh index compared", "a written file the converter reads as it reads Blender's", "a material's base colour compared (factor, image)", "material: a MASK alpha written as 0", "material: a pbrMetallicRoughness object beside a default base colour", "material: no pbrMetallicRoughness object on either side", "material: a base colour image under a colour factor", "material: unlit", "material: specular-glossiness", "left to Blender: jpeg-alpha", "left to Blender: factor-range (the converter refuses Blender's file too)", "material: a blended alpha kept", "material: a base colour image, byte for byte", "written: a skinned file", "written: several materials (an .mtl and an albedo each)", "written: a base colour image", "written: a flat material (a swatch from its factor)",
"left to Blender: an animated file", "left to Blender: an object under a bone", "a skin's joint indices compared", "two nodes of one armature sharing a skin", "two armatures, a skin each", "a primitive's material index compared", "a faceless skinned object compared", "left to Blender: an object of the file is declined", "left to Blender: material-uv", "left to Blender: camera-children", "left to Blender: lights", "a camera left out", "an object hung from a node that bears its parent bone's name",
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
    static void Structure(HafModel m, BlenderNames.Result names, float[][] bworld, HafModel written, List<string> problems, SortedDictionary<string, long> cover, BlenderExportTree.Result tree, ISet<int> neutralArmatures, Dictionary<int, List<string>> materialsOfMesh, SortedDictionary<string, int> leftToBlender)
    {
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
            // the mesh INDEX: every object's mesh is written once, in the order the serializer reaches them (MeshVisitOrder)
            if (n.HasMesh && n.Object != null)
            {
                int expectedMesh = tree.MeshVisitOrder.IndexOf(n.Object.MeshNode);
                if (expectedMesh != w.Mesh) { problems.Add($"structure: node {i} '{n.Name}' mesh index {expectedMesh} here vs {w.Mesh} written"); shown++; continue; }
                cover["a node's mesh index compared"]++;
            }
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
        // each primitive's material: its INDEX in the written list. The list above holds names and order, the mesh stage the
        // vertex data; neither holds which primitive got which material (review of PR #129: the same hole as the skins')
        for (int i = 0; i < written.Nodes.Count && shown < 4; i++)
        {
            var n = tree.Nodes[i]; var w = written.Nodes[i];
            if (!n.HasMesh || n.Object == null || w.Mesh < 0 || !materialsOfMesh.TryGetValue(n.Object.MeshNode, out var mats)) continue;
            var theirs = written.Meshes[w.Mesh].Primitives.Where(p => p.Mode == 4).ToList();
            if (theirs.Count != mats.Count) continue;   // the mesh stage's to report
            bool same = true;
            for (int k = 0; k < mats.Count && same; k++)
            {
                int expected = mats[k] == null ? -1 : tree.Materials.IndexOf(mats[k]);
                if (expected != theirs[k].Material) { problems.Add($"structure: node '{w.Name}' primitive {k} material index {expected} ('{mats[k]}') here vs {theirs[k].Material} written"); shown++; same = false; }
            }
            if (same && mats.Count > 0) cover["a primitive's material index compared"]++;
        }
        // The source armature fixes the joint indices: names and bind matrices can coincide across armatures. The tree
        // holds each skinned node's joints (BlenderExportTree.Node.SkinJoints) - the rule the writer will use
        var skinOfArmature = new Dictionary<int, int>();   // armature glTF node -> the written skin its first node used
        for (int i = 0; i < written.Nodes.Count && shown < 4; i++)
        {
            var w = written.Nodes[i];
            var n = tree.Nodes[i];
            if (n.SkinJoints == null)
            {
                if (w.Skin >= 0) { problems.Add("structure: node '" + w.Name + "' has an unexpected skin"); shown++; }
                continue;
            }
            if (w.Skin < 0 || w.Skin >= written.Skins.Count) { problems.Add("structure: node '" + w.Name + "' is missing its skin"); shown++; continue; }
            var expectedJoints = n.SkinJoints;
            var sk = written.Skins[w.Skin];
            if (!expectedJoints.SequenceEqual(sk.Joints))
            { problems.Add("structure: skin of '" + w.Name + "' joint indices [" + string.Join(",", expectedJoints) + "] here vs [" + string.Join(",", sk.Joints) + "] written"); shown++; continue; }
            cover["a skin's joint indices compared"]++;
            // one skin per armature: the same index for every node of it, another for another armature's
            if (skinOfArmature.TryGetValue(n.SkinArmature, out int shared))
            {
                if (shared != w.Skin) { problems.Add($"structure: node '{w.Name}' uses skin {w.Skin}, another node of its armature skin {shared}"); shown++; continue; }
                cover["two nodes of one armature sharing a skin"]++;
            }
            else
            {
                if (skinOfArmature.ContainsValue(w.Skin)) { problems.Add($"structure: node '{w.Name}' uses skin {w.Skin}, which a node of another armature uses"); shown++; continue; }
                if (skinOfArmature.Count > 0) cover["two armatures, a skin each"]++;
                skinOfArmature[n.SkinArmature] = w.Skin;
            }
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

    /// <summary>What the converter reads of a material, held to Blender's file directly - the converter itself shows a
    /// factor only where there is no texture, and an image only in its faithful mode: the base colour factor as float32
    /// and the base colour image's bytes (or that there is none).</summary>
    static string Materials(HafModel mine, HafModel theirs, string myGlb, string theirGlb, List<string> hits)
    {
        if (mine.Materials.Count != theirs.Materials.Count) return $"{mine.Materials.Count} materials here, {theirs.Materials.Count} in Blender's";
        // whether a material HAS a pbrMetallicRoughness object decides the converter's swatch (white with one, grey without):
        // read off the two files' JSON, not inferred from values
        bool[] pa = PbrObjects(myGlb), pb = PbrObjects(theirGlb);
        for (int i = 0; i < mine.Materials.Count; i++)
        {
            HafMaterial a = mine.Materials[i], b = theirs.Materials[i];
            // the DOUBLES: Blender writes a float32's exact value, and so must this (a consumer that reads floats could not tell)
            for (int k = 0; k < 4; k++)
                if (!SameDouble(a.BaseColorFactor[k], b.BaseColorFactor[k]))
                    return $"material {i} '{a.Name}' base colour factor [{string.Join(",", a.BaseColorFactor.Select(x => x.ToString("R")))}] here vs [{string.Join(",", b.BaseColorFactor.Select(x => x.ToString("R")))}] in Blender's ({a.AlphaMode}, cutoff {a.AlphaCutoff:R}, texture {a.BaseColorTexture >= 0})";
            if (pa[i] != pb[i]) return $"material {i} '{a.Name}' {(pa[i] ? "has a" : "has no")} pbrMetallicRoughness object here (metallic {a.MetallicFactor:R}, roughness {a.RoughnessFactor:R}), Blender's {(pb[i] ? "has one" : "has none")} (metallic {b.MetallicFactor:R}, roughness {b.RoughnessFactor:R})";
            // metallic and roughness are not the converter's to read, but they make the object: held to Blender's as float32
            if (a.MetallicFactor != b.MetallicFactor || a.RoughnessFactor != b.RoughnessFactor) return $"material {i} '{a.Name}' metallic {a.MetallicFactor:R}, roughness {a.RoughnessFactor:R} here vs {b.MetallicFactor:R}, {b.RoughnessFactor:R} in Blender's";
            bool flat = a.BaseColorTexture < 0 && a.BaseColorFactor.All(x => x == 1);
            if (pa[i] && flat) hits.Add("material: a pbrMetallicRoughness object beside a default base colour");
            if (!pa[i]) hits.Add("material: no pbrMetallicRoughness object on either side");
            byte[] ia = a.BaseColorTexture >= 0 ? mine.Images[mine.Textures[a.BaseColorTexture].Source].Bytes : null, ib = b.BaseColorTexture >= 0 ? theirs.Images[theirs.Textures[b.BaseColorTexture].Source].Bytes : null;
            if ((ia == null) != (ib == null)) return $"material {i} '{a.Name}' {(ia == null ? "has no base colour image here, one in Blender's" : "has a base colour image here, none in Blender's")}";
            if (ia != null && !ia.SequenceEqual(ib)) return $"material {i} '{a.Name}' base colour image differs ({ia.Length} bytes here, {ib.Length} in Blender's)";
            bool plain = a.BaseColorTexture < 0;
            if (a.AlphaMode == "MASK" && plain && a.BaseColorFactor[3] == 0) hits.Add("material: a MASK alpha written as 0");
            if (a.AlphaMode != "OPAQUE" && a.AlphaMode != "MASK" && a.BaseColorFactor[3] != 1) hits.Add("material: a blended alpha kept");
            if (ia != null) hits.Add("material: a base colour image, byte for byte");
            if (ia != null && (a.BaseColorFactor[0] != 1 || a.BaseColorFactor[1] != 1 || a.BaseColorFactor[2] != 1)) hits.Add("material: a base colour image under a colour factor");
        }
        if (mine.Materials.Count > 0) hits.Add("a material's base colour compared (factor, image)");
        return null;
    }

    /// <summary>Two JSON numbers as the reader parsed them: equal, or within two units in the last place of a double. The
    /// reader's JSON parser is not correctly rounded: Blender writes a float32's double in Python's shortest form
    /// (0.4829860329627991), the writer here in 17 digits (0.48298603296279907) - one double, parsed an ulp apart
    /// (the Cobra, 2026-10-08; Python reads both alike). A value left in double differs at 1e-8 and is still caught.</summary>
    static bool SameDouble(double a, double b) => a == b || Math.Abs(a - b) <= 4.5e-16 * Math.Max(Math.Abs(a), Math.Abs(b));
    static bool SameDoubles(double[] a, double[] b) { if (a.Length != b.Length) return false; for (int i = 0; i < a.Length; i++) if (!SameDouble(a[i], b[i])) return false; return true; }

    /// <summary>Per material of a GLB: does its JSON have a pbrMetallicRoughness object.</summary>
    static bool[] PbrObjects(string glb)
    {
        var bytes = File.ReadAllBytes(glb);
        int n = BitConverter.ToInt32(bytes, 12);
        var root = Newtonsoft.Json.Linq.JObject.Parse(Encoding.UTF8.GetString(bytes, 20, n));
        return (root["materials"] as Newtonsoft.Json.Linq.JArray ?? new Newtonsoft.Json.Linq.JArray()).Select(x => x["pbrMetallicRoughness"] != null).ToArray();
    }

    /// <summary>The assembled model against Blender's file, field by field, for what the converter does not show: the
    /// scene's roots (it walks the node list), and - so that a difference is named here and not as an OBJ line - every
    /// node's name, children, mesh, skin and transform as the JSON doubles, every skin's joints and inverse bind
    /// matrices.</summary>
    static string Assembled(HafModel mine, HafModel theirs)
    {
        if (mine.Scenes.Count != theirs.Scenes.Count || mine.Scene != theirs.Scene) return $"{mine.Scenes.Count} scenes (default {mine.Scene}) here, {theirs.Scenes.Count} ({theirs.Scene}) in Blender's";
        for (int i = 0; i < mine.Scenes.Count; i++) if (!mine.Scenes[i].Nodes.SequenceEqual(theirs.Scenes[i].Nodes)) return $"scene {i} roots [{string.Join(",", mine.Scenes[i].Nodes)}] here, [{string.Join(",", theirs.Scenes[i].Nodes)}] in Blender's";
        if (mine.Nodes.Count != theirs.Nodes.Count) return $"{mine.Nodes.Count} nodes here, {theirs.Nodes.Count} in Blender's";
        for (int i = 0; i < mine.Nodes.Count; i++)
        {
            HafNode a = mine.Nodes[i], b = theirs.Nodes[i];
            if (a.Name != b.Name || !a.Children.SequenceEqual(b.Children) || a.Mesh != b.Mesh || a.Skin != b.Skin || b.HasMatrix) return $"node {i} '{a.Name}' (children {a.Children.Count}, mesh {a.Mesh}, skin {a.Skin}) here, '{b.Name}' (children {b.Children.Count}, mesh {b.Mesh}, skin {b.Skin}) in Blender's";
            if (!SameDoubles(a.Translation, b.Translation) || !SameDoubles(a.Rotation, b.Rotation) || !SameDoubles(a.Scale, b.Scale)) return $"node {i} '{a.Name}' transform [{string.Join(",", a.Translation.Concat(a.Rotation).Concat(a.Scale).Select(x => x.ToString("R")))}] here vs [{string.Join(",", b.Translation.Concat(b.Rotation).Concat(b.Scale).Select(x => x.ToString("R")))}] in Blender's";
        }
        if (mine.Skins.Count != theirs.Skins.Count) return $"{mine.Skins.Count} skins here, {theirs.Skins.Count} in Blender's";
        for (int i = 0; i < mine.Skins.Count; i++)
        {
            HafSkin a = mine.Skins[i], b = theirs.Skins[i];
            if (!a.Joints.SequenceEqual(b.Joints)) return $"skin {i} joints differ from Blender's";
            if (b.InverseBindMatrices == null || !a.InverseBindMatrices.SequenceEqual(b.InverseBindMatrices)) return $"skin {i} inverse bind matrices differ from Blender's";
        }
        if (mine.Meshes.Count != theirs.Meshes.Count) return $"{mine.Meshes.Count} meshes here, {theirs.Meshes.Count} in Blender's";
        return null;
    }

    /// <summary>The prepared model written, and the converter run on it and on Blender's file - in the faithful mode
    /// (grid 0: every vertex, an .mtl and an albedo per material) and at the Factory's default grid - with the two output
    /// folders compared file by file, byte for byte. Both inputs carry the same file name: the OBJ's first line has it.</summary>
    static string Written(string myGlb, string blenderGlb, string converter, string work, List<string> hits, bool skinned)
    {
        string inA = Path.GetDirectoryName(myGlb), inB = Path.Combine(work, "in_bl");
        Directory.CreateDirectory(inB);
        File.Copy(blenderGlb, Path.Combine(inB, "model.glb"), true);
        foreach (int grid in new[] { 0, 140 })
        {
            string outA = Path.Combine(work, "out_cs"), outB = Path.Combine(work, "out_bl");
            foreach (var d in new[] { outA, outB }) { if (Directory.Exists(d)) Directory.Delete(d, true); Directory.CreateDirectory(d); }
            string ea = Convert(converter, Path.Combine(inA, "model.glb"), outA, grid), eb = Convert(converter, Path.Combine(inB, "model.glb"), outB, grid);
            if (eb != null) return $"the converter fails on Blender's own file (grid {grid}): {eb}";
            if (ea != null) return $"the converter fails on the written file (grid {grid}): {ea}";
            var fa = Directory.GetFiles(outA).Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var fb = Directory.GetFiles(outB).Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (!fa.SequenceEqual(fb)) return $"grid {grid}: the converter wrote [{string.Join(",", fa)}] from this file, [{string.Join(",", fb)}] from Blender's";
            foreach (var f in fa)
            {
                byte[] a = File.ReadAllBytes(Path.Combine(outA, f)), b = File.ReadAllBytes(Path.Combine(outB, f));
                if (a.SequenceEqual(b)) continue;
                if (f.EndsWith(".obj") || f.EndsWith(".mtl"))
                {
                    string[] la = File.ReadAllLines(Path.Combine(outA, f)), lb = File.ReadAllLines(Path.Combine(outB, f));
                    int at = 0; while (at < la.Length && at < lb.Length && la[at] == lb[at]) at++;
                    int diff = 0; for (int i = 0; i < Math.Min(la.Length, lb.Length); i++) if (la[i] != lb[i]) diff++;
                    return $"grid {grid}: {f} differs in {diff} of {la.Length} lines ({lb.Length} from Blender's), first at line {at + 1}: '{(at < la.Length ? la[at] : "<end>")}' here vs '{(at < lb.Length ? lb[at] : "<end>")}'";
                }
                return $"grid {grid}: {f} differs ({a.Length} bytes here, {b.Length} from Blender's)";
            }
            if (grid == 0)
            {
                if (skinned) hits.Add("written: a skinned file");
                if (fa.Any(f => f.EndsWith(".mtl"))) hits.Add("written: several materials (an .mtl and an albedo each)");
                if (fa.Any(f => f.EndsWith(".png") || f.EndsWith(".jpg") || f.EndsWith(".webp"))) hits.Add("written: a base colour image");
                if (fa.Any(f => f.EndsWith(".tga"))) hits.Add("written: a flat material (a swatch from its factor)");
            }
        }
        return null;
    }

    static string Convert(string converter, string glb, string outDir, int grid)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(converter, $"\"{glb}\" \"{outDir}\" model {grid}") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        using (var p = System.Diagnostics.Process.Start(psi))
        {
            var err = p.StandardError.ReadToEndAsync(); p.StandardOutput.ReadToEnd(); p.WaitForExit();
            return p.ExitCode == 0 ? null : "exit " + p.ExitCode + " " + err.Result.Trim().Split('\n').LastOrDefault();
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
