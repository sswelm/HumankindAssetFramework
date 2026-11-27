// GlbDisconnectedParts.Unseen.cs - UNSEEN FACES (2026-11-27, Saleg's Revenge: "most of the guns bodies are still inside
// that are not visible").
//
// A ship modelled for close-ups carries its insides: gun bodies under the deck, the inner skin of the hull, frames and
// decks no one looks into. Measured on the Saleg's Revenge after a careful cut (C:/tmp/haf-build/hidden): of the faces
// the game would draw, 73 % showed their front to no camera above the horizon, and most of them were joined to faces
// that do show. The Factory bakes the unit to a fixed triangle budget, so every one of those faces took detail from a
// face that shows. A split cannot take them (they are joined to what shows), and the Vehicle Lab's visibility probe
// judges a whole part. With the rule below and its rim, the pass leaves out 51 % of that ship's still faces in 43 s
// (the drill under UnseenDefaultRimRings).
//
// So the fuse, when asked, leaves out every face that no camera of the game can see from EITHER side:
//
//   * THE CAMERAS ARE THE HIDDEN TWINS' (GlbDisconnectedParts.Covered.cs): straight above, and rings at 65, 40 and 15
//     degrees above the horizon; a larger face is asked from more directions and from more of its points, on the same
//     tiers (a 256th and 8 x a 256th of the model). Y is up.
//   * EITHER SIDE: a face stays when a camera sees its FRONT or its BACK. What the Lab does after the fuse can turn a
//     face round - its inside-out fix, a Flip part, a TwoSided part, the recipe's double-sided option - and a face
//     whose back shows would then be a hole. Left out is only what no camera sees from either side: what lies INSIDE.
//   * A RAY STOPS AT A FRONT: a triangle hides what is behind it only where it shows its front to the camera, as under
//     back-face culling (TriBvh.Blocked). Glass (BLEND) and cut-outs (MASK) hide nothing and are never left out.
//   * WHAT HIDES, AND WHAT IS ASKED, IS ONLY WHAT STANDS STILL IN THE GAME (the caller's roleOf): a part the Lab
//     ignores is not drawn and hides nothing; a part that moves (spins, turns, folds, is placed, is skinned or morphs)
//     hides nothing and loses nothing, since what is behind it shows when it moves and its own insides may turn out.
//     A mesh two nodes share is asked of no one (leaving faces out of it would cut both) but still hides.
//   * IN THE POSE OF THE FILE, as the hidden twins: the water line is not modelled (more is hidden in the game than
//     this counts), and neither is anything the Lab moves that the caller did not name as moving.
//
// Each changed primitive keeps only the vertices its remaining faces use (every attribute copied byte for byte), so no
// orphaned vertices travel to the Lab, Blender or the Factory; the output is compacted on its way out as every Workshop
// output is (Write).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

public static partial class GlbDisconnectedParts
{
    /// <summary>How the unseen-faces pass treats a node with a mesh.</summary>
    public enum UnseenRole
    {
        Still,      // drawn, and never moves: hides what is behind it, and loses the faces no camera sees
        Moves,      // drawn, but moves or changes shape: hides nothing, loses nothing
        NotDrawn,   // the game does not draw it: hides nothing, loses nothing
    }

    public sealed class UnseenResult
    {
        public byte[] Bytes;
        public bool Changed;
        public int FacesAsked, FacesLeftOut, VerticesLeftOut;
        public readonly Dictionary<int, int> LeftOutByNode = new Dictionary<int, int>();   // node index -> faces left out
        public readonly List<string> Hiders = new List<string>();    // the names of the nodes whose faces stopped the rays (the Lab checks them)
        public readonly List<int> NodesEmptied = new List<int>();    // nodes that lost every face, and so their mesh
        public readonly List<string> Details = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public int RimKept;   // faces no sampled camera saw that stay because they touch a face that shows
        public long RayMs;
    }

    // THE RIM (drill 2026-11-27, Saleg's Revenge, C:/tmp/haf-build/unseendrill): rays from a few points along a few
    // directions miss what a camera sees through a slit or past a lip. Rendered before and after from 96 directions the
    // rule does not ask (640 x 640 each, back faces culled), the first cut changed 601 of 2.83 million ship pixels, 210 of
    // them showing sky - and a face seen past a lip touches the face it is seen beside. So each ring keeps the faces left
    // out that share a corner with a face that stays. Measured, all on that ship:
    //     rings 0: 76 % of the asked faces left out, 601 pixels changed, 210 sky        43 s
    //     rings 1: 59 %, 303 pixels, 23 sky                                              43 s
    //     rings 2: 51 %, 258 pixels, 7 sky (the default)                                 43 s
    //   asking every face as a MEDIUM one with one ring: 54 %, 256 pixels, 23 sky, 126 s; as a LARGE one: 42 %, 102
    //   pixels, 9 sky, 674 s. No sampled rule reaches zero; what remains shows a face about 2 % of the model's size
    //   behind the one left out, in 0.01 % of the pixels.
    public const int UnseenDefaultRimRings = 2;

    static (float, float, float) Corner(double[] c, int o) => ((float)c[o], (float)c[o + 1], (float)c[o + 2]);

    /// <summary>Leave out every face no camera above the horizon sees from either side (see the head of this file).
    /// <paramref name="roleOf"/> is asked once per node with a mesh (node index, node name).</summary>
    public static UnseenResult LeaveOutUnseenFaces(byte[] source, Func<int, string, UnseenRole> roleOf, int maxParallel = -1, int rimRings = UnseenDefaultRimRings)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (roleOf == null) throw new ArgumentNullException(nameof(roleOf));
        var result = new UnseenResult();
        Document document = Parse(source);
        JObject root = document.Root;
        JArray nodes = root["nodes"] as JArray ?? new JArray();
        JArray meshes = root["meshes"] as JArray;
        if (meshes == null || meshes.Count == 0) { result.Details.Add("unseen faces: the file has no meshes"); return result; }
        JArray materials = root["materials"] as JArray;
        Accessors reader = BinReader(document, root);

        var meshUsers = new Dictionary<int, int>();
        for (int i = 0; i < nodes.Count; i++)
            if ((nodes[i] as JObject)?["mesh"] is JToken m && m.Type == JTokenType.Integer) meshUsers[(int)m] = meshUsers.TryGetValue((int)m, out int c) ? c + 1 : 1;

        // every node that stands still and is drawn: its faces in their RENDERED winding (a mirrored node reverses it)
        var geos = new List<PartGeometry>(); var corners = new List<double[]>(); var stops = new List<bool[]>(); var asked = new List<bool>();
        int moving = 0, notDrawn = 0, shared = 0, unread = 0;
        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i] as JObject;
            if (node?["mesh"] == null || node["mesh"].Type != JTokenType.Integer) continue;
            int meshIndex = (int)node["mesh"];
            string name = (string)node["name"] ?? ("node " + i);
            UnseenRole role = roleOf(i, name);
            var meshPrims = (meshes.ElementAtOrDefault(meshIndex) as JObject)?["primitives"] as JArray;
            if (role == UnseenRole.Still && (node["skin"] != null || (meshPrims != null && meshPrims.OfType<JObject>().Any(p => p["targets"] != null)))) role = UnseenRole.Moves;   // a skinned or morphing mesh moves
            if (role == UnseenRole.NotDrawn) { notDrawn++; continue; }
            if (role == UnseenRole.Moves) { moving++; continue; }
            PartGeometry g;
            try { g = ExtractNode(root, nodes, meshes, reader, i); }
            catch (Exception ex) when (ex is InvalidDataException || ex is OverflowException)
            {
                unread++; result.Warnings.Add("unseen faces: '" + name + "' kept whole and hides nothing - " + ex.Message);
                continue;
            }
            bool mirrored = Det3(NodeWorldMatrix(nodes, i)) < 0;
            int faceCount = g.Triangles.Length / 3;
            var c9 = new double[9 * faceCount]; var stop = new bool[faceCount];
            for (int f = 0; f < faceCount; f++)
            {
                for (int k = 0; k < 3; k++)
                {
                    int corner = mirrored && k > 0 ? 3 - k : k;   // 0,2,1 for a mirrored node: the winding it renders with
                    int v = g.Triangles[3 * f + corner];
                    c9[9 * f + 3 * k] = g.Positions[3 * v]; c9[9 * f + 3 * k + 1] = g.Positions[3 * v + 1]; c9[9 * f + 3 * k + 2] = g.Positions[3 * v + 2];
                }
            }
            var prims = TrianglePrimitives(meshPrims).ToList();
            for (int pi = 0; pi < prims.Count; pi++)
            {
                bool opaque = !MaterialUsesAlpha(materials, prims[pi].Value<int?>("material") ?? -1);
                int end = pi + 1 < prims.Count ? g.PrimitiveStart[pi + 1] : g.Triangles.Length;
                for (int t = g.PrimitiveStart[pi] / 3; t < end / 3; t++) stop[t] = opaque;
            }
            bool isShared = meshUsers.TryGetValue(meshIndex, out int users) && users > 1;
            if (isShared) shared++;
            geos.Add(g); corners.Add(c9); stops.Add(stop); asked.Add(!isShared);
        }
        if (geos.Count == 0) { result.Details.Add("unseen faces: no part stands still and is drawn - nothing asked"); return result; }

        // the tree of every face that hides: opaque faces of what stands still (glass and cut-outs are seen through)
        var hideSegs = new double[geos.Count][];
        for (int s = 0; s < geos.Count; s++)
        {
            bool all = stops[s].All(b => b);
            if (all) { hideSegs[s] = corners[s]; continue; }
            var keep = new List<double>();
            for (int f = 0; f < stops[s].Length; f++) if (stops[s][f]) for (int k = 0; k < 9; k++) keep.Add(corners[s][9 * f + k]);
            hideSegs[s] = keep.ToArray();
        }
        var tree = new TriBvh(hideSegs, null, 0);
        if (tree.Empty) { result.Details.Add("unseen faces: nothing opaque stands still - nothing asked"); return result; }
        double lift = tree.Extent * 1e-6;   // off the face towards the camera, clear of the face itself
        double small = tree.Extent / 256.0, large = 8 * small;   // the hidden twins' tiers, of the model

        // the faces to ask
        var work = new List<(int s, int f)>();
        for (int s = 0; s < geos.Count; s++) if (asked[s]) for (int f = 0; f < stops[s].Length; f++) if (stops[s][f]) work.Add((s, f));
        result.FacesAsked = work.Count;
        var seen = new bool[work.Count];
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Parallel.For(0, work.Count, new ParallelOptions { MaxDegreeOfParallelism = maxParallel }, i =>
        {
            var (s, f) = work[i];
            seen[i] = UnseenFaceSeen(corners[s], 9 * f, tree, lift, small, large);
        });
        result.RayMs = clock.ElapsedMilliseconds;

        // THE RIM: a face touching a face that shows stays (see UnseenDefaultRimRings)
        for (int ring = 0; ring < rimRings; ring++)
        {
            var grow = new List<int>();
            int at = 0;
            for (int s = 0; s < geos.Count; s++)
            {
                int n = stops[s].Length, first = at;
                if (!asked[s]) continue;
                // this node's asked faces run work[first .. first + asked count): faces by corner position
                var shown = new HashSet<(float, float, float)>();
                int count = 0;
                for (int i = first; i < work.Count && work[i].s == s; i++, count++)
                    if (seen[i]) for (int k = 0; k < 3; k++) shown.Add(Corner(corners[s], 9 * work[i].f + 3 * k));
                for (int i = first; i < first + count; i++)
                    if (!seen[i]) for (int k = 0; k < 3; k++) if (shown.Contains(Corner(corners[s], 9 * work[i].f + 3 * k))) { grow.Add(i); break; }
                at = first + count;
            }
            if (grow.Count == 0) break;
            foreach (int i in grow) seen[i] = true;
            result.RimKept += grow.Count;
        }

        // which faces go, per node
        var dropOf = new Dictionary<int, bool[]>();   // geos index -> dropped faces
        for (int i = 0; i < work.Count; i++)
        {
            if (seen[i]) continue;
            var (s, f) = work[i];
            if (!dropOf.TryGetValue(s, out bool[] d)) dropOf[s] = d = new bool[stops[s].Length];
            d[f] = true;
        }
        for (int s = 0; s < geos.Count; s++) result.Hiders.Add(geos[s].NodeName);
        string kept = string.Format(CultureInfo.InvariantCulture, "{0} part(s) move, {1} are not drawn, {2} share a mesh, {3} could not be read: kept whole", moving, notDrawn, shared, unread);
        if (dropOf.Count == 0)
        {
            result.Details.Add(string.Format(CultureInfo.InvariantCulture, "unseen faces: none - every one of {0:N0} face(s) is seen by some camera from one side or the other; {1}; rays {2} ms", work.Count, kept, result.RayMs));
            return result;
        }

        // rewrite every changed primitive: its kept faces, and only the vertices they use
        byte[] sourceBin = document.Chunks[document.BinIndex].Data;
        var bin = new List<byte>(sourceBin);
        foreach (var kv in dropOf.OrderBy(k => geos[k.Key].NodeIndex))
        {
            PartGeometry g = geos[kv.Key]; bool[] drop = kv.Value;
            var node = (JObject)nodes[g.NodeIndex];
            int meshIndex = (int)node["mesh"];
            var mesh = (JObject)meshes[meshIndex];
            var primitives = (JArray)mesh["primitives"];
            var prims = TrianglePrimitives(primitives).ToList();
            var emptied = new List<JObject>();
            int leftOut = 0, vertsBefore = 0, vertsAfter = 0;
            for (int pi = 0; pi < prims.Count; pi++)
            {
                JObject primitive = prims[pi];
                int firstFace = g.PrimitiveStart[pi] / 3, endFace = (pi + 1 < prims.Count ? g.PrimitiveStart[pi + 1] : g.Triangles.Length) / 3;
                int dropped = 0; for (int f = firstFace; f < endFace; f++) if (drop[f]) dropped++;
                if (dropped == 0) continue;
                if (dropped == endFace - firstFace) { emptied.Add(primitive); leftOut += dropped; continue; }
                var attributes = (JObject)primitive["attributes"];
                int posAcc = attributes.Value<int>("POSITION");
                int vertCount = reader.Count(posAcc);
                int? indexAcc = primitive.Value<int?>("indices");
                var remap = new int[vertCount]; for (int v = 0; v < vertCount; v++) remap[v] = -1;
                var pick = new List<int>(); var indices = new List<uint>((endFace - firstFace - dropped) * 3);
                for (int f = firstFace; f < endFace; f++)
                {
                    if (drop[f]) continue;
                    uint local = (uint)(f - firstFace) * 3;
                    for (uint k = 0; k < 3; k++)
                    {
                        int v = (int)(indexAcc.HasValue ? reader.Index(indexAcc.Value, local + k) : local + k);
                        if (remap[v] < 0) { remap[v] = pick.Count; pick.Add(v); }
                        indices.Add((uint)remap[v]);
                    }
                }
                var newAttributes = new JObject();
                try
                {
                    foreach (var attr in attributes.Properties())
                        newAttributes[attr.Name] = CopyElements(root, sourceBin, bin, (int)attr.Value, pick, attr.Name == "POSITION");
                }
                catch (InvalidDataException ex)
                {
                    // what was appended is read by nothing and compacted away on Write; the primitive stays as it was
                    result.Warnings.Add("unseen faces: a primitive of '" + g.NodeName + "' kept whole - " + ex.Message);
                    continue;
                }
                vertsBefore += CountReferenced(reader, indexAcc, endFace - firstFace, vertCount); vertsAfter += pick.Count;
                primitive["attributes"] = newAttributes;
                primitive["indices"] = AppendIndices(root, bin, indices, pick.Count <= ushort.MaxValue ? 5123 : 5125);
                leftOut += dropped;
            }
            if (leftOut == 0) continue;
            foreach (JObject p in emptied) primitives.Remove(p);
            result.LeftOutByNode[g.NodeIndex] = leftOut; result.FacesLeftOut += leftOut;
            result.VerticesLeftOut += vertsBefore - vertsAfter;
            if (primitives.Count == 0) { node.Remove("mesh"); result.NodesEmptied.Add(g.NodeIndex); }
        }
        document.Chunks[document.BinIndex].Data = bin.ToArray();
        var writeResult = new Result();
        result.Bytes = Write(document, writeResult);
        result.Changed = true;
        string byPart = string.Join(", ", result.LeftOutByNode.OrderByDescending(k => k.Value).Take(6)
            .Select(k => ((string)nodes[k.Key]["name"] ?? ("node " + k.Key)) + " " + k.Value.ToString("N0", CultureInfo.InvariantCulture)));
        result.Details.Add(string.Format(CultureInfo.InvariantCulture,
            "unseen faces: {0:N0} of {1:N0} face(s) no camera above the horizon sees from either side - left out (by part: {2}){3}; {4:N0} vertices fewer; {5:N0} kept beside faces that show (the rim); {6} part(s) hid them; {7}; rays {8} ms",
            result.FacesLeftOut, work.Count, byPart, result.NodesEmptied.Count > 0 ? "; " + result.NodesEmptied.Count + " part(s) lost every face and their mesh" : "",
            result.VerticesLeftOut, result.RimKept, geos.Count, kept, result.RayMs));
        result.Details.AddRange(writeResult.Details);
        return result;
    }

    // THE VEHICLE LAB'S ROLES (VehicleLabWindow.Role, by number: the recipes and role lists store the number). What the
    // game draws standing still hides and is asked; Ignore is not drawn; every other role moves or is turned round by
    // the Lab (wheels, turrets, guns, rotors, trails, muzzles, cradles, oars, sails, flags, rudders, chains, caterpillars;
    // Flip and TwoSided change which side shows; Edgecase is not a still part by any rule this file knows).
    static readonly HashSet<int> LabStillRoles = new HashSet<int> { 0 /*Body*/, 4 /*Default*/, 15 /*Rigging*/, 16 /*Structure*/, 19 /*Preserve*/, 21 /*Detail*/, 22 /*Shroud*/, 23 /*Window*/, 24 /*Railing*/ };
    const int LabIgnoreRole = 3;

    /// <summary>How the pass treats a part the Lab knows under <paramref name="labRoles"/> (every role any of the Lab's
    /// records gives it - the recipe and the last Generate's lists can disagree): Still only when every one is a still,
    /// drawn role and the part is not placed (a Lab offset or scale moves it). Not drawn only when every one is Ignore.
    /// A part the Lab has never seen is Still: the Lab gives a new part the Default role.</summary>
    public static UnseenRole UnseenRoleOfLab(IEnumerable<int> labRoles, bool placed)
    {
        var roles = (labRoles ?? Enumerable.Empty<int>()).Distinct().ToList();
        if (roles.Count == 0) return placed ? UnseenRole.Moves : UnseenRole.Still;
        if (roles.All(r => r == LabIgnoreRole)) return UnseenRole.NotDrawn;
        if (!placed && roles.All(r => LabStillRoles.Contains(r))) return UnseenRole.Still;
        return UnseenRole.Moves;
    }

    /// <summary>The Lab names a fused shell "Fused_&lt;letter&gt;_&lt;group or first part&gt;", and a fuse after a re-split can name
    /// the same group's shell after another first part. So a Lab name matches a node by name, or - for a fused shell - by
    /// its group letter.</summary>
    public static bool LabNameMatches(string nodeName, string labName)
    {
        if (string.Equals(nodeName, labName, StringComparison.Ordinal)) return true;
        string a = FusedShellLetter(nodeName), b = FusedShellLetter(labName);
        return a != null && a == b;
    }

    // THE LIST BESIDE THE OUTPUT: the parts that hid faces when the pass ran, under a header holding the output's length
    // (a file written over without the pass, or by anything else, no longer matches and the list is not read). The Lab
    // reads it before it generates: a part that hid faces and is now ignored, moving or placed may leave a hole.
    public const string UnseenSidecarHeader = "#unseen-faces v1";
    public static string UnseenSidecarPath(string glb) => string.IsNullOrEmpty(glb) ? null : glb + ".unseen.txt";

    /// <summary>The hiders the list names, or null when the list does not describe a file of <paramref name="glbLength"/> bytes.</summary>
    public static List<string> ReadUnseenSidecar(IEnumerable<string> lines, long glbLength)
    {
        var all = (lines ?? Enumerable.Empty<string>()).ToList();
        if (all.Count == 0 || all[0].Trim() != UnseenSidecarHeader + "|" + glbLength.ToString(CultureInfo.InvariantCulture)) return null;
        return all.Skip(1).Where(l => l.StartsWith("hid|", StringComparison.Ordinal)).Select(l => l.Substring(4)).ToList();
    }

    /// <summary>The Lab's parts (name, role, placed) that hid faces at the fuse and are no longer still, drawn parts.</summary>
    public static List<string> UnseenConflicts(IEnumerable<(string name, int role, bool placed)> labParts, IList<string> hiders)
    {
        var found = new List<string>();
        if (hiders == null || hiders.Count == 0) return found;
        foreach (var p in labParts ?? Enumerable.Empty<(string, int, bool)>())
            if (UnseenRoleOfLab(new[] { p.role }, p.placed) != UnseenRole.Still && hiders.Any(h => LabNameMatches(p.name, h)))
                found.Add(p.name);
        return found;
    }

    static string FusedShellLetter(string name)
    {
        if (name == null || !name.StartsWith("Fused_", StringComparison.Ordinal)) return null;
        int end = name.IndexOf('_', 6);
        return end > 6 ? name.Substring(6, end - 6) : null;
    }

    // a face is seen when a camera sees its front, or its back, from one of its sample points (the hidden twins' tiers)
    static bool UnseenFaceSeen(double[] c, int o, TriBvh tree, double lift, double small, double large)
    {
        double ax = c[o + 3] - c[o], ay = c[o + 4] - c[o + 1], az = c[o + 5] - c[o + 2];
        double bx = c[o + 6] - c[o], by = c[o + 7] - c[o + 1], bz = c[o + 8] - c[o + 2];
        double nx = ay * bz - az * by, ny = az * bx - ax * bz, nz = ax * by - ay * bx;
        double l = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        if (!(l > 1e-18) || double.IsInfinity(l)) return true;   // a face with no area draws nothing: left as it is
        nx /= l; ny /= l; nz /= l;
        double longest2 = 0;
        for (int e = 0; e < 3; e++)
        {
            int a = o + 3 * e, b = o + 3 * ((e + 1) % 3);
            double ex = c[a] - c[b], ey = c[a + 1] - c[b + 1], ez = c[a + 2] - c[b + 2];
            longest2 = Math.Max(longest2, ex * ex + ey * ey + ez * ez);
        }
        int[] points = longest2 < small * small ? RayPointsSmall : longest2 < large * large ? RayPointsMedium : RayPointsLarge;
        double[] views = ReferenceEquals(points, RayPointsLarge) ? CoveredViewsLarge : ReferenceEquals(points, RayPointsMedium) ? CoveredViewsMedium : CoveredViews;
        double cx = (c[o] + c[o + 3] + c[o + 6]) / 3.0, cy = (c[o + 1] + c[o + 4] + c[o + 7]) / 3.0, cz = (c[o + 2] + c[o + 5] + c[o + 8]) / 3.0;
        for (int d = 0; d < views.Length; d += 3)
        {
            double dx = views[d], dy = views[d + 1], dz = views[d + 2];
            double facing = nx * dx + ny * dy + nz * dz;
            double side = facing >= 0 ? 1 : -1;   // the side of the face this camera looks at
            if (side * facing < CoveredFacing) continue;   // edge-on
            foreach (int k in points)
            {
                UnseenSample(c, o, cx, cy, cz, k, out double x, out double y, out double z);
                if (!tree.Blocked(x + side * nx * lift, y + side * ny * lift, z + side * nz * lift, dx, dy, dz)) return true;
            }
        }
        return false;
    }

    // the hidden twins' sample points: the centroid (0), 0.7 of the way to the corners (1-3) and to the edge midpoints
    // (4-6), and the same six at 0.95 (7-12)
    static void UnseenSample(double[] c, int o, double cx, double cy, double cz, int k, out double x, out double y, out double z)
    {
        if (k == 0) { x = cx; y = cy; z = cz; return; }
        double reach = k <= 6 ? 0.7 : 0.95; int j = k <= 6 ? k : k - 6;
        double tx, ty, tz;
        if (j <= 3) { int a = (j - 1) * 3; tx = c[o + a]; ty = c[o + a + 1]; tz = c[o + a + 2]; }
        else { int a = (j - 4) * 3, b = ((j - 4 + 1) % 3) * 3; tx = (c[o + a] + c[o + b]) * 0.5; ty = (c[o + a + 1] + c[o + b + 1]) * 0.5; tz = (c[o + a + 2] + c[o + b + 2]) * 0.5; }
        x = cx + reach * (tx - cx); y = cy + reach * (ty - cy); z = cz + reach * (tz - cz);
    }

    static int CountReferenced(Accessors reader, int? indexAcc, int faces, int vertCount)
    {
        if (!indexAcc.HasValue) return faces * 3;
        var used = new bool[vertCount]; int n = 0;
        for (uint i = 0; i < faces * 3; i++) { uint v = reader.Index(indexAcc.Value, i); if (!used[v]) { used[v] = true; n++; } }
        return n;
    }

    // the elements `pick` of an accessor, byte for byte, as a new tightly packed accessor at the end of the BIN
    static int CopyElements(JObject root, byte[] sourceBin, List<byte> bin, int accessorIndex, IList<int> pick, bool withMinMax)
    {
        var accessor = (JObject)root["accessors"][accessorIndex];
        if (accessor["sparse"] != null) throw new InvalidDataException("A sparse accessor cannot be compacted.");
        int componentType = accessor.Value<int>("componentType");
        string type = (string)accessor["type"];
        int components = type == "SCALAR" ? 1 : type == "VEC2" ? 2 : type == "VEC3" ? 3 : type == "VEC4" ? 4 : type == "MAT2" ? 4 : type == "MAT3" ? 9 : type == "MAT4" ? 16
            : throw new InvalidDataException("Unknown accessor type " + type + ".");
        int componentSize = componentType == 5120 || componentType == 5121 ? 1 : componentType == 5122 || componentType == 5123 ? 2 : componentType == 5125 || componentType == 5126 ? 4
            : throw new InvalidDataException("Unknown component type " + componentType + ".");
        int elementSize = components * componentSize;
        int start = 0, stride = elementSize;
        if (accessor["bufferView"] != null)
        {
            var view = (JObject)root["bufferViews"][accessor.Value<int>("bufferView")];
            if ((view.Value<int?>("buffer") ?? 0) != 0) throw new InvalidDataException("Only the GLB's own buffer can be read.");
            start = (view.Value<int?>("byteOffset") ?? 0) + (accessor.Value<int?>("byteOffset") ?? 0);
            stride = view.Value<int?>("byteStride") ?? elementSize;
        }
        while ((bin.Count & 3) != 0) bin.Add(0);
        int byteOffset = bin.Count;
        var min = new double[components]; var max = new double[components];
        for (int k = 0; k < components; k++) { min[k] = double.PositiveInfinity; max[k] = double.NegativeInfinity; }
        foreach (int v in pick)
        {
            int at = start + v * stride;
            if (accessor["bufferView"] == null) { for (int b = 0; b < elementSize; b++) bin.Add(0); continue; }   // an accessor with no view reads as zeros
            if (at < 0 || at + elementSize > sourceBin.Length) throw new InvalidDataException("Accessor element outside the BIN.");
            for (int b = 0; b < elementSize; b++) bin.Add(sourceBin[at + b]);
            if (withMinMax && componentType == 5126)
                for (int k = 0; k < components; k++) { double x = BitConverter.ToSingle(sourceBin, at + 4 * k); if (x < min[k]) min[k] = x; if (x > max[k]) max[k] = x; }
        }
        var views = (JArray)root["bufferViews"];
        int viewIndex = views.Count;
        views.Add(new JObject { ["buffer"] = 0, ["byteOffset"] = byteOffset, ["byteLength"] = bin.Count - byteOffset, ["target"] = 34962 });
        var accessors = (JArray)root["accessors"];
        var copy = new JObject { ["bufferView"] = viewIndex, ["componentType"] = componentType, ["count"] = pick.Count, ["type"] = type };
        if (accessor.Value<bool?>("normalized") == true) copy["normalized"] = true;
        if (withMinMax && componentType == 5126 && pick.Count > 0) { copy["min"] = new JArray(min.Select(d => (float)d)); copy["max"] = new JArray(max.Select(d => (float)d)); }
        accessors.Add(copy);
        return accessors.Count - 1;
    }
}
