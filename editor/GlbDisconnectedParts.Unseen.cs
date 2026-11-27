// GlbDisconnectedParts.Unseen.cs - UNSEEN FACES (2026-11-27, Saleg's Revenge: "most of the guns bodies are still inside
// that are not visible").
//
// A ship modelled for close-ups carries its insides: gun bodies under the deck, the inner skin of the hull, frames and
// decks no one looks into. Measured on the Saleg's Revenge after a careful cut (C:/tmp/haf-build/hidden): of the faces
// the game would draw, 73 % showed their front to no camera above the horizon (53 % neither side), and most of them
// were joined to faces that do show. The Factory bakes the unit to a fixed triangle budget, so every one of those faces
// took detail from a face that shows. A split cannot take them (they are joined to what shows), and the Vehicle Lab's
// visibility probe judges a whole part. With the rule below and its rim, the pass leaves out 57 % of that ship's still
// faces, in 100 s on the editor's Mono (43 s on .NET Framework) - the drill under UnseenDefaultRimRings.
//
// So the fuse, when asked, leaves out every face that no camera of the game can see from EITHER side:
//
//   * THE CAMERAS ARE THE GAME'S (UnseenLowestCamera): straight above, and rings at 65, 40 and 28 degrees above the
//     horizon (a large face also 52.5); a larger face is asked from more directions and from more of its points, on the
//     hidden twins' tiers (GlbDisconnectedParts.Covered.cs: a 256th and 8 x a 256th of the model). Y is up.
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
        public bool Cancelled;   // the caller's progress callback said stop: nothing was left out
        public int FacesAsked, FacesLeftOut, VerticesLeftOut;
        public readonly Dictionary<int, int> LeftOutByNode = new Dictionary<int, int>();   // node index -> faces left out
        public readonly List<string> Hiders = new List<string>();    // the names of the nodes whose opaque faces made up the ray tree: every part that COULD hide a face, not only those that did (the Lab checks them)
        public readonly List<int> NodesEmptied = new List<int>();    // nodes that lost every face, and so their mesh
        public readonly List<string> Details = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public int RimKept;   // faces no sampled camera saw that stay because they touch a face that shows
        public long RayMs;
    }

    // THE RIM (drill 2026-11-27, Saleg's Revenge, C:/tmp/haf-build/unseenfinal): rays from a few points along a few
    // directions miss what a camera sees through a slit or past a lip, and a face seen past a lip touches the face it is
    // seen beside. So each ring keeps the faces left out that share a corner position with a face of the same part that
    // stays. Rendered before and after from 144 directions the rule does not ask (16 round at 10, 20, 28, 33, 40, 47,
    // 58, 72 and 85 degrees; 640 x 640 each, back faces culled, 4.25 million ship pixels), measured on that ship:
    //     rings 0: 80 % of the asked faces left out; pixels changed 16,608, 14,195 of them sky; 28 degrees and up 842
    //     rings 1: 65 %; 4,425, 1,474 sky; 28 degrees and up 377
    //     rings 2: 57 %; 1,499, 106 sky; 28 degrees and up 343, the rest at 10 (993) and 20 (163) - the top edge of a
    //              fully zoomed-in screen, below the game's lowest tilt (the default)
    //   No sampled rule reaches zero; a changed pixel mostly shows a face a little behind the one left out (median 4.5 %
    //   of the model's radius, half its bounding diagonal).
    public const int UnseenDefaultRimRings = 2;

    static (float, float, float) Corner(double[] c, int o) => ((float)c[o], (float)c[o + 1], (float)c[o + 2]);

    /// <summary>Leave out every face no camera of the game sees from either side (see the head of this file).
    /// <paramref name="roleOf"/> is asked once per node with a mesh (node index, node name). <paramref name="progress"/>,
    /// when given, is called on the CALLING thread about four times a second while the rays run, with the share of faces
    /// asked; it returns false to cancel, and the pass then leaves every face in (Cancelled). The editor's Mono takes
    /// about 100 s on a large ship (review round 3: the bar sat at 90 % with no way out).</summary>
    public static UnseenResult LeaveOutUnseenFaces(byte[] source, Func<int, string, UnseenRole> roleOf, int maxParallel = -1, int rimRings = UnseenDefaultRimRings, Func<double, bool> progress = null, double lowestCamera = UnseenLowestCamera)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (roleOf == null) throw new ArgumentNullException(nameof(roleOf));
        if (!(lowestCamera >= UnseenLowestCameraMin && lowestCamera <= UnseenLowestCameraMax))
            throw new ArgumentOutOfRangeException(nameof(lowestCamera), lowestCamera, $"the lowest camera is {UnseenLowestCameraMin}..{UnseenLowestCameraMax} degrees above the horizon");
        double[][] viewSets = UnseenViewSets(lowestCamera);
        var result = new UnseenResult();
        Document document = Parse(source);
        JObject root = document.Root;
        JArray nodes = root["nodes"] as JArray ?? new JArray();
        JArray meshes = root["meshes"] as JArray;
        if (meshes == null || meshes.Count == 0) { result.Details.Add("unseen faces: the file has no meshes"); return result; }
        JArray materials = root["materials"] as JArray;
        Accessors reader = BinReader(document, root);

        // a node no scene reaches is not drawn (review of the first cut): it hides nothing and is not asked
        var inScene = new HashSet<int>();
        if (root["scenes"] is JArray scenes && scenes.Count > 0)
        {
            var todo = new Stack<int>();
            foreach (JObject scene in scenes.OfType<JObject>()) foreach (JToken r in (scene["nodes"] as JArray ?? new JArray())) if (r.Type == JTokenType.Integer) todo.Push((int)r);
            while (todo.Count > 0)
            {
                int n = todo.Pop();
                if (n < 0 || n >= nodes.Count || !inScene.Add(n)) continue;
                foreach (JToken c in ((nodes[n] as JObject)?["children"] as JArray ?? new JArray())) if (c.Type == JTokenType.Integer) todo.Push((int)c);
            }
        }
        else for (int i = 0; i < nodes.Count; i++) inScene.Add(i);   // no scenes: every node counts as drawn
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
            UnseenRole role = inScene.Contains(i) ? roleOf(i, name) : UnseenRole.NotDrawn;
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
        int facesDone = 0;
        using (var cancelRays = new CancellationTokenSource())
        {
            var options = new ParallelOptions { MaxDegreeOfParallelism = maxParallel, CancellationToken = cancelRays.Token };
            void Rays() => Parallel.For(0, work.Count, options, i =>
            {
                var (s, f) = work[i];
                seen[i] = UnseenFaceSeen(corners[s], 9 * f, tree, lift, small, large, viewSets);
                if (progress != null) Interlocked.Increment(ref facesDone);
            });
            if (progress == null) Rays();
            else
            {
                // the rays on the pool, the caller's thread free to draw the bar (Unity's UI calls belong to it)
                var running = Task.Run(Rays);
                try
                {
                    do { if (!cancelRays.IsCancellationRequested && !progress(Volatile.Read(ref facesDone) / (double)Math.Max(1, work.Count))) cancelRays.Cancel(); }
                    while (!running.Wait(250));
                }
                catch (AggregateException e) when (cancelRays.IsCancellationRequested && e.Flatten().InnerExceptions.All(x => x is OperationCanceledException))
                {
                    result.Cancelled = true;
                    result.Details.Add(string.Format(CultureInfo.InvariantCulture, "unseen faces: cancelled after {0:N0} of {1:N0} face(s) - nothing was left out", Volatile.Read(ref facesDone), work.Count));
                    return result;
                }
                catch { cancelRays.Cancel(); throw; }   // the callback itself threw: the rays stop rather than run on unseen
            }
        }
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
                // this node's asked faces run work[first .. first + asked count): the corner positions of the ones seen so far
            // (by a camera, or kept by an earlier ring); a face of the SAME part touching one of them stays
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
        for (int s = 0; s < geos.Count; s++) if (hideSegs[s].Length > 0) result.Hiders.Add(geos[s].NodeName);   // what could stop a ray: an opaque face of a still part
        string kept = string.Format(CultureInfo.InvariantCulture, "{0} part(s) move, {1} are not drawn, {2} share a mesh, {3} could not be read: kept whole", moving, notDrawn, shared, unread);
        if (dropOf.Count == 0)
        {
            result.Details.Add(string.Format(CultureInfo.InvariantCulture, "unseen faces: none - every one of {0:N0} face(s) is seen by some camera ({3:0.#} degrees and up) from one side or the other; {1}; rays {2} ms", work.Count, kept, result.RayMs, lowestCamera));
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
                var attributes = (JObject)primitive["attributes"];
                int posAcc = attributes.Value<int>("POSITION");
                int vertCount = reader.Count(posAcc);
                int? indexAcc = primitive.Value<int?>("indices");
                if (dropped == endFace - firstFace)
                {
                    emptied.Add(primitive); leftOut += dropped;
                    vertsBefore += CountReferenced(reader, indexAcc, endFace - firstFace, vertCount);
                    continue;
                }
                // source vertex -> kept vertex. A split fragment indexes a few vertices of its parent's whole buffer, so a
                // buffer far larger than the faces asks for a map, not an array the buffer's size (review of the first cut)
                bool sparse = vertCount > 12 * (endFace - firstFace);
                int[] remapArray = sparse ? null : new int[vertCount]; var remapMap = sparse ? new Dictionary<int, int>() : null;
                if (!sparse) for (int v = 0; v < vertCount; v++) remapArray[v] = -1;
                var pick = new List<int>(); var indices = new List<uint>((endFace - firstFace - dropped) * 3);
                for (int f = firstFace; f < endFace; f++)
                {
                    if (drop[f]) continue;
                    uint local = (uint)(f - firstFace) * 3;
                    for (uint k = 0; k < 3; k++)
                    {
                        int v = (int)(indexAcc.HasValue ? reader.Index(indexAcc.Value, local + k) : local + k);
                        int to;
                        if (sparse) { if (!remapMap.TryGetValue(v, out to)) { to = pick.Count; remapMap[v] = to; pick.Add(v); } }
                        else { to = remapArray[v]; if (to < 0) { to = remapArray[v] = pick.Count; pick.Add(v); } }
                        indices.Add((uint)to);
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
            // a node that loses every face loses its mesh, and the mesh stays as it was: glTF forbids a mesh with no
            // primitives, and Write's compaction, which drops a mesh no node uses, may be refused (review of the first cut)
            if (emptied.Count == primitives.Count) { node.Remove("mesh"); result.NodesEmptied.Add(g.NodeIndex); }
            else foreach (JObject p in emptied) primitives.Remove(p);
            result.LeftOutByNode[g.NodeIndex] = leftOut; result.FacesLeftOut += leftOut;
            result.VerticesLeftOut += vertsBefore - vertsAfter;
        }
        document.Chunks[document.BinIndex].Data = bin.ToArray();
        var writeResult = new Result();
        result.Bytes = Write(document, writeResult);
        result.Changed = true;
        string byPart = string.Join(", ", result.LeftOutByNode.OrderByDescending(k => k.Value).Take(6)
            .Select(k => ((string)nodes[k.Key]["name"] ?? ("node " + k.Key)) + " " + k.Value.ToString("N0", CultureInfo.InvariantCulture)));
        result.Details.Add(string.Format(CultureInfo.InvariantCulture,
            "unseen faces: {0:N0} of {1:N0} face(s) no camera of the game ({9:0.#} degrees and up) sees from either side - left out (by part: {2}){3}; {4:N0} vertices fewer; {5:N0} kept beside faces that show (the rim); {6} part(s) could hide them; {7}; rays {8} ms",
            result.FacesLeftOut, work.Count, byPart, result.NodesEmptied.Count > 0 ? "; " + result.NodesEmptied.Count + " part(s) lost every face and their mesh" : "",
            result.VerticesLeftOut, result.RimKept, result.Hiders.Count, kept, result.RayMs, lowestCamera));
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

    /// <summary>How the pass treats the node <paramref name="nodeName"/> from the Lab's records of the file (name, role,
    /// placed): by its own records (<see cref="LabNameMatches"/>); with none, as the Lab's Probe does
    /// (VehicleLabRules.ProbeSessionRole), by a MARKED role kept under a parent name - a split child Gun_Part_003 takes
    /// Gun's (review of the first cut: it took the name guess, Default, and stood still); with none of those either, by
    /// the role the Lab would guess from its name (<paramref name="guessRole"/>).</summary>
    public static UnseenRole UnseenRoleFromLabRecords(string nodeName, IList<(string name, int role, bool placed)> records, Func<string, int> guessRole)
    {
        var mine = (records ?? new List<(string, int, bool)>()).Where(x => LabNameMatches(nodeName, x.name)).ToList();
        if (mine.Count == 0 && records != null)
            foreach (string parent in VehicleLabRules.ParentNames(nodeName))
            {
                mine = records.Where(x => string.Equals(x.name, parent, StringComparison.Ordinal) && x.role != LabDefaultRole).ToList();
                if (mine.Count > 0) break;
            }
        if (mine.Count == 0) return UnseenRoleOfLab(new[] { guessRole != null ? guessRole(nodeName) : LabDefaultRole }, false);
        return UnseenRoleOfLab(mine.Select(x => x.role), mine.Any(x => x.placed));
    }
    const int LabDefaultRole = 4;

    // A GUN NOTHING MOVES STANDS STILL (2026-11-27, the Saleg's Revenge: every cannon of the ship is one part marked Gun
    // - for the Gun reduce dial - and the pass kept all 1.6 million of its faces; counted still, it leaves out 1,391,036).
    // A Gun part (and its Muzzle and Cradle) moves when something moves it: a Turret part it rides, the recipe's deploy
    // raise or recoil, or, in the game, the pack's gun elevation or a turret bone. With none of those it stands still.
    // The pack is the only place the game's settings live, so with no pack entry for the Lab's output the gun is taken
    // to move.
    static readonly HashSet<int> LabGunRoles = new HashSet<int> { 7 /*Gun*/, 11 /*Muzzle*/, 12 /*Cradle*/ };
    const int LabTurretRole = 2;

    /// <summary>True when a recipe's guns cannot move: no Turret part among <paramref name="recipeRoles"/>, no deploy
    /// raise, no recoil, and at least one pack model built from the Lab's output with no gun elevation and no turret bone.</summary>
    public static bool LabGunStands(IEnumerable<int> recipeRoles, float deployRaiseDeg, float recoilDist, IList<(float gunElevMax, string turretBone)> packModels)
    {
        if ((recipeRoles ?? Enumerable.Empty<int>()).Contains(LabTurretRole)) return false;
        if (Math.Abs(deployRaiseDeg) > 1e-4f || recoilDist > 1e-6f) return false;
        if (packModels == null || packModels.Count == 0) return false;
        return packModels.All(m => m.gunElevMax == 0f && string.IsNullOrEmpty(m.turretBone));
    }

    /// <summary>The role the pass reads for a Lab role: a Gun, Muzzle or Cradle counts as Default when the gun stands.</summary>
    public static int UnseenLabRole(int labRole, bool gunStands) => gunStands && LabGunRoles.Contains(labRole) ? LabDefaultRole : labRole;

    /// <summary>The pack's models built from <paramref name="labOutput"/> (pack.json text), with their gun settings;
    /// empty when the text cannot be read. <paramref name="samePath"/> compares two paths.</summary>
    public static List<(float gunElevMax, string turretBone)> PackGunSettings(string packJson, string labOutput, Func<string, string, bool> samePath)
    {
        var found = new List<(float, string)>();
        if (string.IsNullOrEmpty(packJson) || string.IsNullOrEmpty(labOutput)) return found;
        try
        {
            foreach (JObject m in (JObject.Parse(packJson)["models"] as JArray ?? new JArray()).OfType<JObject>())
                if (samePath((string)m["modelFile"] ?? "", labOutput))
                    found.Add(((float?)m["gunElevMax"] ?? 0f, (string)m["turretBone"] ?? ""));
        }
        catch (Exception e) when (e is Newtonsoft.Json.JsonException || e is FormatException || e is InvalidCastException || e is ArgumentException) { found.Clear(); }
        return found;
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

    // THE LIST BESIDE THE OUTPUT: every part that could hide a face when the pass ran (a still, drawn part with an opaque
    // face - which of them stopped which ray is not kept, so the list errs towards asking), under a header holding the
    // output's length (a file written over without the pass, or by anything else, no longer matches and the list is not
    // read). The Lab reads it before it generates: such a part that is now ignored, moving or placed may leave a hole.
    public const string UnseenSidecarHeader = "#unseen-faces v1";
    public static string UnseenSidecarPath(string glb) => string.IsNullOrEmpty(glb) ? null : glb + ".unseen.txt";

    /// <summary>The hiders the list names, or null when the list does not describe a file of <paramref name="glbLength"/> bytes.</summary>
    public static List<string> ReadUnseenSidecar(IEnumerable<string> lines, long glbLength)
    {
        var all = (lines ?? Enumerable.Empty<string>()).ToList();
        if (all.Count == 0 || all[0].Trim() != UnseenSidecarHeader + "|" + glbLength.ToString(CultureInfo.InvariantCulture)) return null;
        return all.Skip(1).Where(l => l.StartsWith("hid|", StringComparison.Ordinal)).Select(l => l.Substring(4)).ToList();
    }

    /// <summary>The Lab's parts (name, role, placed) that could hide faces at the fuse and are no longer still, drawn parts.
    /// <paramref name="hiders"/> is the list beside the Lab's model, <paramref name="hidersOfSecond"/> the one beside its
    /// second model, whose parts the Lab names with <see cref="LabSecondModelPrefix"/> (review of the first cut: they never
    /// matched). A "B_" name is checked against both lists - a first model may name a part so - which can only ask more.</summary>
    public static List<string> UnseenConflicts(IEnumerable<(string name, int role, bool placed)> labParts, IList<string> hiders, IList<string> hidersOfSecond = null)
    {
        var found = new List<string>();
        if ((hiders == null || hiders.Count == 0) && (hidersOfSecond == null || hidersOfSecond.Count == 0)) return found;
        foreach (var p in labParts ?? Enumerable.Empty<(string, int, bool)>())
        {
            if (UnseenRoleOfLab(new[] { p.role }, p.placed) == UnseenRole.Still) continue;
            string second = LabSecondModelName(p.name);
            if ((hiders != null && hiders.Any(h => LabNameMatches(p.name, h))) || (second != null && hidersOfSecond != null && hidersOfSecond.Any(h => LabNameMatches(second, h))))
                found.Add(p.name);
        }
        return found;
    }

    /// <summary>The prefix the Vehicle Lab gives the parts of its second model (vehicle_rig.py merges it into the scene so).</summary>
    public const string LabSecondModelPrefix = "B_";

    /// <summary>The second model's own name for a Lab part, or null when the Lab name is not a second-model one.</summary>
    public static string LabSecondModelName(string labName)
        => labName != null && labName.Length > LabSecondModelPrefix.Length && labName.StartsWith(LabSecondModelPrefix, StringComparison.Ordinal) ? labName.Substring(LabSecondModelPrefix.Length) : null;

    static string FusedShellLetter(string name)
    {
        if (name == null || !name.StartsWith("Fused_", StringComparison.Ordinal)) return null;
        int end = name.IndexOf('_', 6);
        return end > 6 ? name.Substring(6, end - 6) : null;
    }

    // WHERE THE GAME'S CAMERA LOOKS FROM (read from the game's data, 2026-11-27: Mercury.Data.Shared's DefaultCameraProfile,
    // MercuryCameraMoverProfile.CameraAngle, is 28 degrees below the horizon fully zoomed in and 80 zoomed out; the close-up
    // profiles start at 28 too). So the cameras are the hidden twins' with their lowest ring at 28 degrees instead of 15:
    // straight above, then 65, 40 and 28 (a large face also 52.5), as many ways round as there. The camera is a
    // perspective one (MainCamera, 35 degrees high), so the top edge of a fully zoomed-in screen looks along about 10
    // degrees: there, and only there, a face this leaves out can show (the drill under UnseenDefaultRimRings).
    // THE LOWEST CAMERA IS THE USER'S TO RAISE (2026-11-27, user: "perhaps that 45 degree option is more interesting,
    // could you make it configurable?"): the rings above it stay, the lowest becomes the one asked for. Measured on the
    // Saleg's Revenge (144 render directions the pass does not ask): 28 leaves out 57 %, 45 leaves out 63 % and shows
    // small see-through gaps - many of them sky - when the camera is zoomed in (5,505 changed pixels at 10 and 20
    // degrees, 257 at 28), and from 47 degrees up looks the same as 28.
    public const double UnseenLowestCamera = 28.0, UnseenLowestCameraMin = 15.0, UnseenLowestCameraMax = 85.0;
    static readonly System.Collections.Concurrent.ConcurrentDictionary<double, double[][]> unseenViewSets = new System.Collections.Concurrent.ConcurrentDictionary<double, double[][]>();
    // small, medium and large faces' directions for a lowest ring of `lowest` degrees
    static double[][] UnseenViewSets(double lowest) => unseenViewSets.GetOrAdd(lowest, f =>
    {
        double[] Rings(params double[] above) => above.Where(r => r > f + 1e-9).Concat(new[] { f }).ToArray();
        return new[] { BuildCoveredViews(Rings(65.0, 40.0), 8, true), BuildCoveredViews(Rings(65.0, 40.0), 16, false), BuildCoveredViews(Rings(65.0, 52.5, 40.0), 32, false) };
    });

    // a face is seen when a camera sees its front, or its back, from one of its sample points (the hidden twins' tiers)
    static bool UnseenFaceSeen(double[] c, int o, TriBvh tree, double lift, double small, double large, double[][] viewSets)
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
        double[] views = ReferenceEquals(points, RayPointsLarge) ? viewSets[2] : ReferenceEquals(points, RayPointsMedium) ? viewSets[1] : viewSets[0];
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
        var used = new HashSet<uint>();
        for (uint i = 0; i < faces * 3; i++) used.Add(reader.Index(indexAcc.Value, i));
        return used.Count;
    }

    // the elements `pick` of an accessor, byte for byte, as a new accessor at the end of the BIN. Each element starts on a
    // 4-byte boundary, as glTF asks of a vertex attribute (a ubyte VEC3 colour is padded to 4 and the view states its
    // stride); POSITION carries its min and max whatever its component type (KHR_mesh_quantization) - review of the first cut
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
        int elementStride = (elementSize + 3) & ~3;
        while ((bin.Count & 3) != 0) bin.Add(0);
        int byteOffset = bin.Count;
        var min = new double[components]; var max = new double[components];
        for (int k = 0; k < components; k++) { min[k] = double.PositiveInfinity; max[k] = double.NegativeInfinity; }
        foreach (int v in pick)
        {
            int at = start + v * stride;
            if (accessor["bufferView"] == null)   // an accessor with no view reads as zeros
            {
                for (int b = 0; b < elementStride; b++) bin.Add(0);
                if (withMinMax) for (int k = 0; k < components; k++) { min[k] = Math.Min(min[k], 0); max[k] = Math.Max(max[k], 0); }
                continue;
            }
            if (at < 0 || at + elementSize > sourceBin.Length) throw new InvalidDataException("Accessor element outside the BIN.");
            for (int b = 0; b < elementSize; b++) bin.Add(sourceBin[at + b]);
            for (int b = elementSize; b < elementStride; b++) bin.Add(0);
            if (withMinMax)
                for (int k = 0; k < components; k++)
                {
                    int o = at + k * componentSize;
                    double x = componentType == 5126 ? BitConverter.ToSingle(sourceBin, o) : componentType == 5125 ? BitConverter.ToUInt32(sourceBin, o)
                             : componentType == 5123 ? BitConverter.ToUInt16(sourceBin, o) : componentType == 5122 ? BitConverter.ToInt16(sourceBin, o)
                             : componentType == 5121 ? sourceBin[o] : (sbyte)sourceBin[o];
                    if (x < min[k]) min[k] = x; if (x > max[k]) max[k] = x;
                }
        }
        var views = (JArray)root["bufferViews"];
        int viewIndex = views.Count;
        var newView = new JObject { ["buffer"] = 0, ["byteOffset"] = byteOffset, ["byteLength"] = bin.Count - byteOffset, ["target"] = 34962 };
        if (elementStride != elementSize) newView["byteStride"] = elementStride;
        views.Add(newView);
        var accessors = (JArray)root["accessors"];
        var copy = new JObject { ["bufferView"] = viewIndex, ["componentType"] = componentType, ["count"] = pick.Count, ["type"] = type };
        if (accessor.Value<bool?>("normalized") == true) copy["normalized"] = true;
        if (withMinMax && pick.Count > 0)
        {
            if (componentType == 5126) { copy["min"] = new JArray(min.Select(d => (float)d)); copy["max"] = new JArray(max.Select(d => (float)d)); }
            else { copy["min"] = new JArray(min.Select(d => (long)d)); copy["max"] = new JArray(max.Select(d => (long)d)); }
        }
        accessors.Add(copy);
        return accessors.Count - 1;
    }
}
