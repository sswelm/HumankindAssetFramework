using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

public class GlbDisconnectedPartsTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(6, false)]
    public void Point_and_line_modes_are_triangle_free_but_triangle_strips_and_fans_are_not(int mode, bool expected)
    {
        var source = BuildGlb(new float[] { 0,0,0, 1,0,0, 0,1,0 }, edit: root => root["meshes"][0]["primitives"][0]["mode"] = mode);
        Assert.Equal(expected, Assert.Single(GlbDisconnectedParts.Analyze(source)).TriangleFree);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_mixed_line_and_triangle_mesh_is_not_triangle_free(bool lineFirst)
    {
        var source = BuildGlb(new float[] { 0,0,0, 1,0,0, 0,1,0 }, edit: root =>
        {
            var primitives = (JArray)root["meshes"][0]["primitives"];
            primitives.Add(primitives[0].DeepClone());
            primitives[lineFirst ? 0 : 1]["mode"] = 1;
        });
        var info = Assert.Single(GlbDisconnectedParts.Analyze(source));
        Assert.NotNull(info.Blocked);
        Assert.False(info.TriangleFree);
    }

    [Fact]
    public void Splits_disconnected_triangles_into_child_nodes_and_preserves_materials()
    {
        byte[] source = BuildGlb(new[] {
            0f, 0f, 0f,  1f, 0f, 0f,  0f, 1f, 0f,
            10f, 0f, 0f, 11f, 0f, 0f, 10f, 1f, 0f
        });

        GlbDisconnectedParts.Result result = GlbDisconnectedParts.Split(source);

        Assert.True(result.Changed);
        Assert.Equal(1, result.MeshesSplit);
        Assert.Equal(1, result.NodesSplit);
        Assert.Equal(2, result.ChildPartsCreated);
        Assert.Equal(2, result.SourceTriangles);
        Assert.Equal(2, result.OutputTriangles);

        JObject root = ReadJson(result.Bytes);
        var nodes = (JArray)root["nodes"];
        var parent = (JObject)nodes[0];
        Assert.Null(parent["mesh"]);
        Assert.Equal(new[] { 1, 2, 3 }, ((JArray)parent["children"]).Values<int>().ToArray());
        Assert.Equal("Hull_Part_001", (string)nodes[2]["name"]);
        Assert.Equal("Hull_Part_002", (string)nodes[3]["name"]);

        var meshes = (JArray)root["meshes"];
        var accessors = (JArray)root["accessors"];
        int splitTriangleCount = 0;
        foreach (JObject child in nodes.Skip(2).OfType<JObject>())
        {
            JObject mesh = (JObject)meshes[child.Value<int>("mesh")];
            JObject primitive = (JObject)((JArray)mesh["primitives"])[0];
            Assert.Equal(7, primitive.Value<int>("material"));
            splitTriangleCount += accessors[primitive.Value<int>("indices")].Value<int>("count") / 3;
        }
        Assert.Equal(2, splitTriangleCount);
    }

    [Fact]
    public void Keeps_an_attached_surface_whose_seam_vertices_are_duplicated()
    {
        // Two triangles form one square, but the shared edge uses duplicate indices as happens at UV/normal seams.
        byte[] source = BuildGlb(new[] {
            0f, 0f, 0f,  1f, 0f, 0f,  0f, 1f, 0f,
            1f, 0f, 0f,  1f, 1f, 0f,  0f, 1f, 0f
        });

        GlbDisconnectedParts.Result result = GlbDisconnectedParts.Split(source);

        Assert.False(result.Changed);
        Assert.Equal(0, result.NodesSplit);
        Assert.Equal(0, result.ChildPartsCreated);
    }

    [Fact]
    public void Retargets_morph_weight_animation_to_every_split_child()
    {
        byte[] source = BuildGlb(new[] {
            0f, 0f, 0f,  1f, 0f, 0f,  0f, 1f, 0f,
            10f, 0f, 0f, 11f, 0f, 0f, 10f, 1f, 0f
        }, true);

        JObject root = ReadJson(GlbDisconnectedParts.Split(source).Bytes);
        var channels = (JArray)root["animations"][0]["channels"];

        Assert.Equal(new[] { 2, 3 }, channels.Select(c => c["target"].Value<int>("node")).ToArray());
        Assert.All(channels, c => Assert.Equal("weights", (string)c["target"]["path"]));
        Assert.All(channels, c => Assert.Equal(0, c.Value<int>("sampler")));
    }

    [Fact]
    public void Selective_split_only_touches_named_nodes_and_analyze_reports_islands()
    {
        byte[] source = BuildGlb(new[] {
            0f, 0f, 0f,  1f, 0f, 0f,  0f, 1f, 0f,
            10f, 0f, 0f, 11f, 0f, 0f, 10f, 1f, 0f
        });

        // Analyze: read-only picker data — one row for the Hull node, two islands, two triangles.
        var infos = GlbDisconnectedParts.Analyze(source);
        var hull = Assert.Single(infos, i => i.NodeName == "Hull");
        Assert.Equal(2, hull.Islands);
        Assert.Equal(2, hull.Triangles);
        Assert.Null(hull.Blocked);

        // A filter that names no present node splits nothing — the unchecked part stays whole.
        var none = GlbDisconnectedParts.Split(source, new HashSet<string> { "SomethingElse" });
        Assert.False(none.Changed);
        Assert.Equal(0, none.ChildPartsCreated);

        // Naming the node reproduces the full split exactly.
        var chosen = GlbDisconnectedParts.Split(source, new HashSet<string> { "Hull" });
        Assert.True(chosen.Changed);
        Assert.Equal(2, chosen.ChildPartsCreated);
        Assert.Equal(2, chosen.SourceTriangles);
        Assert.Equal(2, chosen.OutputTriangles);
    }

    [Fact]
    public void Distance_merge_fuses_near_islands_and_keeps_far_junk_separate()
    {
        // Three disconnected triangles: two 0.5 apart (a segmented rope), one 50 away (floating junk).
        byte[] source = BuildGlb(new[] {
            0f, 0f, 0f,    1f, 0f, 0f,    0f, 1f, 0f,
            1.5f, 0f, 0f,  2.5f, 0f, 0f,  1.5f, 1f, 0f,
            50f, 0f, 0f,   51f, 0f, 0f,   50f, 1f, 0f
        });

        // Pure topology: three islands.
        Assert.Equal(3, GlbDisconnectedParts.Analyze(source).Single(i => i.NodeName == "Hull").Islands);

        // 5% of the ~51-unit diagonal ≈ 2.5 merge reach: the rope halves fuse, the junk stays its own part.
        Assert.Equal(2, GlbDisconnectedParts.Analyze(source, 0.05).Single(i => i.NodeName == "Hull").Islands);
        var result = GlbDisconnectedParts.Split(source, new HashSet<string> { "Hull" }, 0.05);
        Assert.Equal(2, result.ChildPartsCreated);
        Assert.Equal(3, result.SourceTriangles);
        Assert.Equal(3, result.OutputTriangles);
    }

    [Fact]
    public void Direction_gate_keeps_parallel_dashed_lines_as_separate_parts()
    {
        // Two parallel dashed lines 0.3 apart: thin slivers along X, two dashes per line (in-line gap 0.5).
        // Distance alone would chain all four into one blob (0.3 < the 5% reach); the direction gate lets a dash
        // reach only ALONG its own axis, so each line chains to itself and the result is exactly two parts.
        byte[] source = BuildGlb(new[] {
            0f, 0f, 0f,     1f, 0f, 0f,     0f, 0.02f, 0f,
            1.5f, 0f, 0f,   2.5f, 0f, 0f,   1.5f, 0.02f, 0f,
            0f, 0.3f, 0f,   1f, 0.3f, 0f,   0f, 0.32f, 0f,
            1.5f, 0.3f, 0f, 2.5f, 0.3f, 0f, 1.5f, 0.32f, 0f
        });

        Assert.Equal(4, GlbDisconnectedParts.Analyze(source).Single(i => i.NodeName == "Hull").Islands);
        Assert.Equal(2, GlbDisconnectedParts.Analyze(source, 0.25).Single(i => i.NodeName == "Hull").Islands);
        var result = GlbDisconnectedParts.Split(source, new HashSet<string> { "Hull" }, 0.25);
        Assert.Equal(2, result.ChildPartsCreated);
        Assert.Equal(4, result.SourceTriangles);
        Assert.Equal(4, result.OutputTriangles);
    }

    // the rope (two triangles 0.5 apart) and the far junk of the distance-merge test: at 5 % two pieces, at 0 % three
    static readonly float[] RopeAndJunk = {
        0f, 0f, 0f,    1f, 0f, 0f,    0f, 1f, 0f,
        1.5f, 0f, 0f,  2.5f, 0f, 0f,  1.5f, 1f, 0f,
        50f, 0f, 0f,   51f, 0f, 0f,   50f, 1f, 0f
    };
    const double RopeX = 2, HalfX = 3.5, JunkX = 52;   // where each piece starts along X in WORLD space: the fixture node sits at (2, 3, 4)
    // (node index, name, where it starts along X) of every mesh-carrying node
    static List<(int node, string name, double minX)> Pieces(byte[] glb) =>
        GlbDisconnectedParts.ExtractAll(glb).Select(g => (g.NodeIndex, g.NodeName, g.Min[0])).ToList();

    [Fact]
    public void Carry_by_geometry_follows_the_triangles_when_a_lower_merge_renumbers_the_pieces()
    {
        // user 2026-11-26: split at 1 %, grouped the pieces in the Fuser, split again lower - and the kept groups
        // landed on "parts that just had the same name"
        byte[] source = BuildGlb(RopeAndJunk);
        byte[] coarse = GlbDisconnectedParts.Split(source, new HashSet<string> { "Hull" }, 0.05).Bytes;
        byte[] fine = GlbDisconnectedParts.Split(source, new HashSet<string> { "Hull" }, 0).Bytes;
        var old = Pieces(coarse); var now = Pieces(fine);
        Assert.Equal(2, old.Count); Assert.Equal(3, now.Count);
        var oldRope = old.Single(p => p.minX == RopeX); var oldJunk = old.Single(p => p.minX == JunkX);
        var values = new Dictionary<int, string> { [oldRope.node] = "A", [oldJunk.node] = "B" };

        // THE PREMISE: the junk's old name now belongs to half of the rope, so a carry by name puts B on the rope
        var sameName = now.Single(p => p.name == oldJunk.name);
        Assert.Equal(HalfX, sameName.minX);

        var carry = GlbDisconnectedParts.Carry(GlbDisconnectedParts.TraceOrigins(coarse, fine), values);
        Assert.Empty(carry.Conflicts);
        Assert.Equal(0, carry.Orphans);
        Assert.False(carry.FoundNothing);
        Assert.Equal(3, carry.Values.Count);
        Assert.Equal("A", carry.Values[now.Single(p => p.minX == RopeX).node]);     // both halves of the rope stay in the rope's group
        Assert.Equal("A", carry.Values[sameName.node]);
        Assert.Equal("B", carry.Values[now.Single(p => p.minX == JunkX).node]);    // and the junk's letter follows the junk
    }

    [Fact]
    public void Carry_by_geometry_leaves_a_piece_unmarked_when_it_joins_pieces_marked_differently()
    {
        byte[] source = BuildGlb(RopeAndJunk);
        byte[] fine = GlbDisconnectedParts.Split(source, new HashSet<string> { "Hull" }, 0).Bytes;
        byte[] coarse = GlbDisconnectedParts.Split(source, new HashSet<string> { "Hull" }, 0.05).Bytes;
        var old = Pieces(fine); var now = Pieces(coarse);
        int half1 = old.Single(p => p.minX == RopeX).node, half2 = old.Single(p => p.minX == HalfX).node, junk = old.Single(p => p.minX == JunkX).node;
        var rope = now.Single(p => p.minX == RopeX); int newJunk = now.Single(p => p.minX == JunkX).node;
        var trace = GlbDisconnectedParts.TraceOrigins(fine, coarse);

        // one half grouped, the other not: the joined rope is nobody's choice
        var carry = GlbDisconnectedParts.Carry(trace, new Dictionary<int, string> { [half1] = "A", [junk] = "B" });
        Assert.False(carry.Values.ContainsKey(rope.node));
        Assert.Equal("B", carry.Values[newJunk]);
        Assert.Single(carry.Conflicts);
        Assert.Equal(new[] { rope.node }, carry.ConflictNodes.ToArray());
        Assert.Contains("'" + rope.name + "' matches geometry from pieces that were unmarked and 'A'", carry.Conflicts[0]);
        Assert.Contains("not carried over", carry.Conflicts[0]);   // never "left unmarked": the source's hand-down may still mark it

        // two different groups: the same
        carry = GlbDisconnectedParts.Carry(trace, new Dictionary<int, string> { [half1] = "A", [half2] = "C" });
        Assert.Empty(carry.Values);
        Assert.Single(carry.Conflicts);
        Assert.False(carry.FoundNothing);   // a conflict is an answer, not "nothing found"

        // both halves in one group: the joined rope keeps it, and nothing is reported
        carry = GlbDisconnectedParts.Carry(trace, new Dictionary<int, string> { [half1] = "A", [half2] = "A" });
        Assert.Equal("A", carry.Values[rope.node]);
        Assert.False(carry.Values.ContainsKey(newJunk));
        Assert.Empty(carry.Conflicts);
    }

    [Fact]
    public void Carry_by_geometry_gives_nothing_to_geometry_the_old_output_did_not_have()
    {
        byte[] other = BuildGlb(new[] { 7f, 0f, 0f, 8f, 0f, 0f, 7f, 1f, 0f });
        byte[] now = BuildGlb(RopeAndJunk);
        int oldNode = Pieces(other).Single().node;
        var carry = GlbDisconnectedParts.Carry(GlbDisconnectedParts.TraceOrigins(other, now), new Dictionary<int, string> { [oldNode] = "A" });
        Assert.Empty(carry.Values);
        Assert.Empty(carry.Conflicts);
        Assert.Equal(1, carry.Orphans);
        Assert.True(carry.FoundNothing);   // the window then asks by name instead of dropping the saved work

        // the same file against itself: every part keeps exactly its own value, reversed winding is another triangle
        byte[] flipped = BuildGlb(new[] { 7f, 0f, 0f, 7f, 1f, 0f, 8f, 0f, 0f });
        Assert.Equal("A", GlbDisconnectedParts.Carry(GlbDisconnectedParts.TraceOrigins(other, other), new Dictionary<int, string> { [oldNode] = "A" }).Values[oldNode]);
        Assert.Empty(GlbDisconnectedParts.Carry(GlbDisconnectedParts.TraceOrigins(other, flipped), new Dictionary<int, string> { [oldNode] = "A" }).Values);
    }

    [Fact]
    public void Carry_by_geometry_moved_model_finds_nothing_and_says_so()
    {
        // review of PR #324, R2: the same model re-exported 1 mm off matches no triangle to the bit. That must read as
        // FoundNothing (the window falls back to the by-name question), never as "carried 0, nothing to say"
        byte[] a = BuildGlb(RopeAndJunk);
        byte[] b = BuildGlb(RopeAndJunk, edit: root => root["nodes"][0]["translation"] = new JArray(2.001, 3, 4));
        var oldAll = Pieces(a).ToDictionary(p => p.node, p => "A");
        var carry = GlbDisconnectedParts.Carry(GlbDisconnectedParts.TraceOrigins(a, b), oldAll);
        Assert.Empty(carry.Values);
        Assert.True(carry.FoundNothing);
        Assert.Equal(oldAll.Count, carry.Orphans);
    }

    [Fact]
    public void Carry_requires_agreement_from_every_old_piece_that_holds_a_shared_triangle()
    {
        float[] two = {
            0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f,
            1f, 0f, 0f, 1f, 1f, 0f, 0f, 1f, 0f
        };
        byte[] old = BuildGlb(two, edit: root =>
        {
            // The child shares the Hull's second triangle and inherits its transform.
            var accessors = (JArray)root["accessors"];
            var sharedIndices = (JObject)accessors[1].DeepClone();
            sharedIndices["byteOffset"] = 6;
            sharedIndices["count"] = 3;
            accessors.Add(sharedIndices);
            var meshes = (JArray)root["meshes"];
            var sharedMesh = meshes[0].DeepClone();
            sharedMesh["primitives"][0]["indices"] = 2;
            meshes.Add(sharedMesh);
            root["nodes"][1]["mesh"] = 1;
        });
        byte[] regenerated = BuildGlb(two, nodeName: "Regenerated");
        var trace = GlbDisconnectedParts.TraceOrigins(old, regenerated);
        var marks = new Dictionary<int, string> { [0] = "X" };

        var carry = GlbDisconnectedParts.Carry(trace, marks);
        Assert.Empty(carry.Values);
        Assert.Contains(0, carry.ConflictNodes);
        Assert.Contains("unmarked", Assert.Single(carry.Conflicts));
        Assert.False(carry.FoundNothing);

        marks[1] = "X";
        carry = GlbDisconnectedParts.Carry(trace, marks);
        Assert.Equal("X", carry.Values[0]);
        Assert.Empty(carry.Conflicts);
        Assert.Equal(0, carry.Orphans);

        // Byte-identical outputs establish identity directly, including for overlapping pieces.
        marks.Remove(1);
        carry = GlbDisconnectedParts.Carry(GlbDisconnectedParts.TraceOrigins(old, old.ToArray()), marks);
        Assert.Equal("X", carry.Values[0]);
        Assert.False(carry.Values.ContainsKey(1));
        Assert.Empty(carry.Conflicts);
    }

    [Fact]
    public void Carry_hands_on_by_index_and_name_what_the_trace_cannot_read()
    {
        // review of PR #324, R1: a node holding lines (rigging) is left out of ExtractAll, so it was in neither map and
        // lost its Delete mark and its letter in silence. Node 1 here is a lines-only "Rigging" beside the Hull.
        byte[] source = BuildGlb(RopeAndJunk, edit: root =>
        {
            var nodes = (JArray)root["nodes"]; var meshes = (JArray)root["meshes"];
            meshes.Add(new JObject { ["name"] = "RigMesh", ["primitives"] = new JArray(new JObject { ["attributes"] = new JObject { ["POSITION"] = 0 }, ["indices"] = 1, ["mode"] = 1 }) });
            nodes[1]["mesh"] = 1; nodes[1]["name"] = "Rigging";
        });
        Assert.DoesNotContain(GlbDisconnectedParts.ExtractAll(source), g => g.NodeName == "Rigging");   // the premise: the trace cannot read it
        byte[] fine = GlbDisconnectedParts.Split(source, new HashSet<string> { "Hull" }, 0).Bytes;
        byte[] coarse = GlbDisconnectedParts.Split(source, new HashSet<string> { "Hull" }, 0.05).Bytes;
        var oldNames = GlbDisconnectedParts.NodeNames(fine).ToDictionary(kv => kv.Key, kv => kv.Value);
        var newNames = GlbDisconnectedParts.NodeNames(coarse).ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.Equal("Rigging", oldNames[1]); Assert.Equal("Rigging", newNames[1]);
        var trace = GlbDisconnectedParts.TraceOrigins(fine, coarse);

        var carry = GlbDisconnectedParts.Carry(trace, new Dictionary<int, string> { [1] = "X" }, oldNames, newNames);
        Assert.Equal("X", carry.Values[1]);
        Assert.Equal(1, carry.ByName);
        Assert.Equal(0, carry.Orphans);

        // a different name at that index is not the same node: counted, not guessed
        newNames[1] = "SomethingElse";
        carry = GlbDisconnectedParts.Carry(trace, new Dictionary<int, string> { [1] = "X" }, oldNames, newNames);
        Assert.Empty(carry.Values);
        Assert.Equal(1, carry.Orphans);
    }

    [Fact]
    public void Carry_reports_new_geometry_beside_a_marked_piece_as_such()
    {
        // review of PR #324, R3: a piece that only GAINED triangles joined nothing, and must not be described as joining
        byte[] a = BuildGlb(new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f });
        byte[] b = BuildGlb(new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f,   1f, 0f, 0f, 1f, 1f, 0f, 0f, 1f, 0f });
        int n = Pieces(a).Single().node;
        var carry = GlbDisconnectedParts.Carry(GlbDisconnectedParts.TraceOrigins(a, b), new Dictionary<int, string> { [n] = "A" });
        Assert.Empty(carry.Values);
        Assert.Single(carry.Conflicts);
        Assert.Contains("was 'A' in the previous output and now also holds geometry that output did not have", carry.Conflicts[0]);
        Assert.DoesNotContain("matches geometry from pieces", carry.Conflicts[0]);
    }

    [Fact]
    public void Only_delete_marks_travel_by_geometry_so_an_unchecked_split_stays_unchecked()
    {
        // review of PR #324, R4: Hull split in the OLD run (its pieces carry the source's S), Deck only in the new one.
        // Carried by geometry, the S came back on the whole Hull and the next Probe re-checked it for Split.
        float[] two = { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f, 20f, 0f, 0f, 21f, 0f, 0f, 20f, 1f, 0f };
        byte[] source = BuildGlb(two, edit: root =>
        {
            var nodes = (JArray)root["nodes"]; var meshes = (JArray)root["meshes"];
            meshes.Add(new JObject { ["name"] = "DeckMesh", ["primitives"] = new JArray(new JObject { ["attributes"] = new JObject { ["POSITION"] = 0 }, ["indices"] = 1 }) });
            nodes[1]["mesh"] = 1; nodes[1]["name"] = "Deck"; nodes[1]["translation"] = new JArray(0, 10, 0);   // a second two-island part, elsewhere
        });
        byte[] oldOut = GlbDisconnectedParts.Split(source, new HashSet<int> { 0 }, 0).Bytes;
        byte[] newOut = GlbDisconnectedParts.Split(source, new HashSet<int> { 1 }, 0).Bytes;
        var oldPieces = GlbDisconnectedParts.Analyze(oldOut).Where(p => p.NodeIndex >= 2).Select(p => p.NodeIndex).ToList();
        Assert.Equal(2, oldPieces.Count);
        var marks = oldPieces.ToDictionary(i => i, i => "S");
        marks[1] = "X";   // and a Delete mark on Deck
        var trace = GlbDisconnectedParts.TraceOrigins(oldOut, newOut);

        Assert.Equal("S", GlbDisconnectedParts.Carry(trace, marks).Values[0]);   // THE DEFECT, unfiltered: Hull marked Split again
        var carried = GlbDisconnectedParts.Carry(trace, WorkshopRules.OnlyDeleteMarks(marks)).Values;
        Assert.False(carried.ContainsKey(0));
        Assert.All(carried.Values, v => Assert.Equal("X", v));
        Assert.NotEmpty(carried);   // Deck's Delete mark reached Deck's new pieces
    }

    [Fact]
    public void Direction_gate_keeps_parallel_DIAGONAL_dashed_lines_as_separate_parts()
    {
        // Review finding 7 regression: the fixture above rotated 45° about Z. Rotation preserves every
        // distance, so the correct result is unchanged (4 raw islands, 2 lines after the merge) — but the old
        // axis-aligned bbox aspect saw each diagonal dash with two EQUAL extents (0.72 x 0.72), classified it
        // blobby, skipped the direction gate, and fused all four dashes into ONE part. Elongation judged in
        // the island's own frame (PCA aspect) must keep the two diagonal lines apart.
        byte[] source = BuildGlb(new[] {
            0f, 0f, 0f,             0.70711f, 0.70711f, 0f,   -0.01414f, 0.01414f, 0f,
            1.06066f, 1.06066f, 0f, 1.76777f, 1.76777f, 0f,    1.04652f, 1.07480f, 0f,
            -0.21213f, 0.21213f, 0f, 0.49497f, 0.91924f, 0f,  -0.22627f, 0.22627f, 0f,
            0.84853f, 1.27279f, 0f,  1.55563f, 1.97990f, 0f,   0.83439f, 1.28693f, 0f
        });

        Assert.Equal(4, GlbDisconnectedParts.Analyze(source).Single(i => i.NodeName == "Hull").Islands);
        Assert.Equal(2, GlbDisconnectedParts.Analyze(source, 0.25).Single(i => i.NodeName == "Hull").Islands);
    }

    [Fact]
    public void Merge_uses_surface_distance_not_bounding_boxes()
    {
        // A long diagonal sliver whose bounding box covers the whole area, plus a small triangle floating
        // INSIDE that box but ~8 units from any of the sliver's geometry. The bbox-gap version read the gap
        // as ZERO and merged at every non-zero setting — hiding exactly the floating junk the Workshop exists
        // to expose. True surface distance keeps them apart.
        byte[] source = BuildGlb(new[] {
            0f, 0f, 0f,   10f, 10f, 0f,  0f, 0.05f, 0f,
            8f, 1f, 0f,   8.6f, 1f, 0f,  8f, 1.6f, 0f
        });

        Assert.Equal(2, GlbDisconnectedParts.Analyze(source, 0.05).Single(i => i.NodeName == "Hull").Islands);
    }

    [Fact]
    public void Merge_compares_every_vertex_not_just_a_cell_representative()
    {
        // Review round 3 regression: two islands whose CLOSEST vertices (0.58 and 0.61 — 0.03 apart, well
        // under the ~0.594 threshold) are neither island's first vertex in its grid cell. The per-cell
        // representative shortcut compared only first-comers (0,0,0) vs (0.61,0,0) = 0.61 -> "separate",
        // a false negative. Exact comparison must merge them into one island.
        byte[] source = BuildGlb(new[] {
            0f, 0f, 0f,     0.02f, 0.3f, 0f,  0.58f, 0f, 0f,
            0.61f, 0f, 0f,  1.1f, 0.3f, 0f,   1.15f, 0f, 0f
        });

        Assert.Equal(2, GlbDisconnectedParts.Analyze(source).Single(i => i.NodeName == "Hull").Islands);
        Assert.Equal(1, GlbDisconnectedParts.Analyze(source, 0.5).Single(i => i.NodeName == "Hull").Islands);
    }

    [Fact]
    public void Unnamed_nodes_split_by_index()
    {
        // A node with no name: Analyze must still list it (placeholder name, real index), and splitting by
        // NodeIndex must work — name-based selection can never match null (the review-caught dead end).
        byte[] source = BuildGlb(new[] {
            0f, 0f, 0f,  1f, 0f, 0f,  0f, 1f, 0f,
            10f, 0f, 0f, 11f, 0f, 0f, 10f, 1f, 0f
        }, nodeName: null);

        var info = GlbDisconnectedParts.Analyze(source).Single(i => i.Islands > 1);
        Assert.Equal(0, info.NodeIndex);
        Assert.False(string.IsNullOrEmpty(info.NodeName));   // display placeholder, never null

        var result = GlbDisconnectedParts.Split(source, new HashSet<int> { info.NodeIndex }, 0);
        Assert.True(result.Changed);
        Assert.Equal(2, result.ChildPartsCreated);
        Assert.Equal(2, result.OutputTriangles);
    }

    static byte[] BuildGlb(float[] positions, bool withWeightAnimation = false, string nodeName = "Hull", Action<JObject> edit = null)
    {
        byte[] positionBytes = new byte[positions.Length * 4];
        for (int i = 0; i < positions.Length; i++)
            Buffer.BlockCopy(BitConverter.GetBytes(positions[i]), 0, positionBytes, i * 4, 4);
        ushort[] sourceIndices = Enumerable.Range(0, positions.Length / 3).Select(i => (ushort)i).ToArray();
        byte[] indexBytes = new byte[sourceIndices.Length * 2];
        for (int i = 0; i < sourceIndices.Length; i++)
            Buffer.BlockCopy(BitConverter.GetBytes(sourceIndices[i]), 0, indexBytes, i * 2, 2);
        int indexOffset = (positionBytes.Length + 3) & ~3;
        byte[] bin = new byte[indexOffset + indexBytes.Length];
        Buffer.BlockCopy(positionBytes, 0, bin, 0, positionBytes.Length);
        Buffer.BlockCopy(indexBytes, 0, bin, indexOffset, indexBytes.Length);

        var root = new JObject {
            ["asset"] = new JObject { ["version"] = "2.0" },
            ["scene"] = 0,
            ["scenes"] = new JArray(new JObject { ["nodes"] = new JArray(0) }),
            ["nodes"] = new JArray(
                nodeName == null
                    ? new JObject { ["mesh"] = 0, ["children"] = new JArray(1), ["translation"] = new JArray(2, 3, 4) }
                    : new JObject { ["name"] = nodeName, ["mesh"] = 0, ["children"] = new JArray(1), ["translation"] = new JArray(2, 3, 4) },
                new JObject { ["name"] = "ExistingChild" }),
            ["meshes"] = new JArray(new JObject {
                ["name"] = "HullMesh",
                ["primitives"] = new JArray(new JObject {
                    ["attributes"] = new JObject { ["POSITION"] = 0 }, ["indices"] = 1, ["material"] = 7
                })
            }),
            ["buffers"] = new JArray(new JObject { ["byteLength"] = bin.Length }),
            ["bufferViews"] = new JArray(
                new JObject { ["buffer"] = 0, ["byteOffset"] = 0, ["byteLength"] = positionBytes.Length, ["target"] = 34962 },
                new JObject { ["buffer"] = 0, ["byteOffset"] = indexOffset, ["byteLength"] = indexBytes.Length, ["target"] = 34963 }),
            ["accessors"] = new JArray(
                new JObject { ["bufferView"] = 0, ["componentType"] = 5126, ["count"] = positions.Length / 3, ["type"] = "VEC3" },
                new JObject { ["bufferView"] = 1, ["componentType"] = 5123, ["count"] = sourceIndices.Length, ["type"] = "SCALAR" })
        };
        if (withWeightAnimation)
        {
            root["animations"] = new JArray(new JObject {
                ["samplers"] = new JArray(new JObject { ["input"] = 0, ["output"] = 0 }),
                ["channels"] = new JArray(new JObject {
                    ["sampler"] = 0,
                    ["target"] = new JObject { ["node"] = 0, ["path"] = "weights" }
                })
            });
        }
        edit?.Invoke(root);
        return WriteGlb(root, bin);
    }

    // ---- THE WELD TOLERANCE (2026-10-12): never finer than four float32 ulps at the mesh's coordinate magnitude, and a
    // distance test across neighbouring cells, not a same-cell test. The salegs_revenge source: a child the split had
    // just cut as one island read as two to seven on its own, every "gap" 0.1-1.1 ulp (GlbDisconnectedParts.Explain.cs).
    static float NextFloat(float f) => BitConverter.ToSingle(BitConverter.GetBytes(BitConverter.ToInt32(BitConverter.GetBytes(f), 0) + 1), 0);

    // two triangles that share an edge; the second triangle's copy of the shared vertices sits `dx` further along x
    static float[] TwoTrianglesSharingAnEdge(float at, float dx) => new[] {
        at, 0f, 0f,   at + 1f, 0f, 0f,   at, 1f, 0f,
        at + 1f + dx, 0f, 0f,   at + 1f + dx, 1f, 0f,   at + dx, 1f, 0f };

    [Fact]
    public void A_shared_edge_one_ulp_apart_far_from_the_origin_is_one_island()
    {
        // at x = 600 a float32 ulp is 6.1e-5; the mesh's diagonal is 1.7, whose ten-millionth (1.7e-7) is 360x finer than
        // what the file can hold - before the floor, the two triangles read as two islands
        float ulp = NextFloat(601f) - 601f;
        Assert.InRange(ulp, 6e-5f, 6.2e-5f);
        var parts = GlbDisconnectedParts.Analyze(TwoTriangles(TwoTrianglesSharingAnEdge(600f, ulp)));
        Assert.Equal(1, parts[0].Islands);
        Assert.Contains("islands=1", GlbDisconnectedParts.ExplainIslands(TwoTriangles(TwoTrianglesSharingAnEdge(600f, ulp)), "Hull"));
    }

    [Fact]
    public void A_shared_edge_within_the_tolerance_is_one_island_wherever_the_cell_boundary_falls()
    {
        // at x ~ 1 the tolerance is four ulps = 4.8e-7; offsets of half of it and of two ulps, over a sweep of base
        // positions, must all weld - the old same-cell test split a pair whenever a cell boundary fell between them
        float ulp = NextFloat(1f) - 1f;
        for (int k = 0; k < 40; k++)
        {
            float at = 1f + k * 0.37f;
            foreach (float dx in new[] { ulp, 2 * ulp })
                Assert.Equal(1, GlbDisconnectedParts.Analyze(TwoTriangles(TwoTrianglesSharingAnEdge(at, dx)))[0].Islands);
        }
    }

    [Fact]
    public void A_real_gap_is_still_two_islands_and_is_explained_as_one()
    {
        var bytes = TwoTriangles(TwoTrianglesSharingAnEdge(600f, 0.05f));   // 0.05 at x = 600: ~820 ulps, ~200 cells
        Assert.Equal(2, GlbDisconnectedParts.Analyze(bytes)[0].Islands);
        string why = GlbDisconnectedParts.ExplainIslands(bytes, "Hull");
        Assert.Contains("islands=2", why);
        Assert.Contains("above sampled weld tolerance", why);
        Assert.Contains("float32-ulp=", why);
        Assert.Equal("Nope: no mesh node of that name", GlbDisconnectedParts.ExplainIslands(bytes, "Nope"));
    }

    [Theory]
    [InlineData(0.02f)]
    [InlineData(16f)]
    public void A_real_gap_on_a_precise_axis_survives_a_large_other_coordinate(float gap)
    {
        var bytes = TwoTriangles(new[] {
            100000000f, 0f, 0f, 100000512f, 0f, 0f, 100000000f, 512f, 0f,
            100000000f, 512f + gap, 0f, 100000512f, 512f + gap, 0f, 100000000f, 1024f + gap, 0f
        });
        Assert.Equal(2, GlbDisconnectedParts.Analyze(bytes)[0].Islands);
    }

    static byte[] TwoTriangles(float[] positions) => BuildGlb(positions);

    static byte[] WriteGlb(JObject root, byte[] bin)
    {
        byte[] json = Encoding.UTF8.GetBytes(root.ToString(Formatting.None));
        int jsonLength = (json.Length + 3) & ~3;
        int binLength = (bin.Length + 3) & ~3;
        byte[] glb = new byte[12 + 8 + jsonLength + 8 + binLength];
        WriteUInt(glb, 0, 0x46546C67); WriteUInt(glb, 4, 2); WriteUInt(glb, 8, (uint)glb.Length);
        WriteUInt(glb, 12, (uint)jsonLength); WriteUInt(glb, 16, 0x4E4F534A);
        Buffer.BlockCopy(json, 0, glb, 20, json.Length);
        for (int i = json.Length; i < jsonLength; i++) glb[20 + i] = 0x20;
        int binHeader = 20 + jsonLength;
        WriteUInt(glb, binHeader, (uint)binLength); WriteUInt(glb, binHeader + 4, 0x004E4942);
        Buffer.BlockCopy(bin, 0, glb, binHeader + 8, bin.Length);
        return glb;
    }

    static JObject ReadJson(byte[] glb)
    {
        int length = (int)ReadUInt(glb, 12);
        return JObject.Parse(Encoding.UTF8.GetString(glb, 20, length).TrimEnd(' ', '\0'));
    }

    static uint ReadUInt(byte[] bytes, int offset) =>
        (uint)(bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16 | bytes[offset + 3] << 24);

    static void WriteUInt(byte[] bytes, int offset, uint value)
    {
        bytes[offset] = (byte)value; bytes[offset + 1] = (byte)(value >> 8);
        bytes[offset + 2] = (byte)(value >> 16); bytes[offset + 3] = (byte)(value >> 24);
    }
}
