using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using Xunit;

// HIDDEN TWINS (2026-11-24, the RSS Centurion's upper deck; editor/GlbDisconnectedParts.Covered.cs): where a face lies
// exactly on a face wound the other way and no camera above the horizon can see its front, the fuse leaves it out, so
// one surface remains. Y is up. Each fixture has one thing to prove; the kernel (FindCoveredFaces) is asked directly,
// and the last tests go through FuseNodes and read the written bytes.
[Collection("native kernels switch")]
public class GlbFuseHiddenTwinTests
{
    const double Tol = 1e-5;

    sealed class Soup
    {
        public readonly List<double> Corners = new List<double>(); public readonly List<string> Tags = new List<string>();
        public int Count => Corners.Count / 9;
        // a quad a-b-c-d, counter-clockwise seen from its front; `otherDiagonal` splits it b-d instead of a-c
        public Soup Quad(string tag, double[] a, double[] b, double[] c, double[] d, bool otherDiagonal = false)
        {
            if (otherDiagonal) { Tri(tag, a, b, d); Tri(tag, b, c, d); }
            else { Tri(tag, a, b, c); Tri(tag, a, c, d); }
            return this;
        }
        public Soup Tri(string tag, double[] a, double[] b, double[] c)
        {
            Corners.AddRange(a); Corners.AddRange(b); Corners.AddRange(c); Tags.Add(tag); return this;
        }
        // the six faces of the box lo..hi, wound outward (or inward); `skip` leaves faces out by name: top bottom north south east west
        public Soup Box(string tag, double[] lo, double[] hi, bool inward = false, params string[] skip)
        {
            double x0 = lo[0], y0 = lo[1], z0 = lo[2], x1 = hi[0], y1 = hi[1], z1 = hi[2];
            void Face(string name, double[] a, double[] b, double[] c, double[] d)
            {
                if (skip.Contains(name)) return;
                if (inward) Quad(tag + ":" + name, a, d, c, b); else Quad(tag + ":" + name, a, b, c, d);
            }
            Face("top", V(x0, y1, z1), V(x1, y1, z1), V(x1, y1, z0), V(x0, y1, z0));
            Face("bottom", V(x0, y0, z0), V(x1, y0, z0), V(x1, y0, z1), V(x0, y0, z1));
            Face("south", V(x0, y0, z1), V(x1, y0, z1), V(x1, y1, z1), V(x0, y1, z1));      // +Z
            Face("north", V(x1, y0, z0), V(x0, y0, z0), V(x0, y1, z0), V(x1, y1, z0));      // -Z
            Face("east", V(x1, y0, z1), V(x1, y0, z0), V(x1, y1, z0), V(x1, y1, z1));       // +X
            Face("west", V(x0, y0, z0), V(x0, y0, z1), V(x0, y1, z1), V(x0, y1, z0));       // -X
            return this;
        }
        public string[] Dropped(GlbDisconnectedParts.CoveredFaces found) => Enumerable.Range(0, Count).Where(f => found.Drop[f]).Select(f => Tags[f]).Distinct().OrderBy(t => t, StringComparer.Ordinal).ToArray();
    }

    static double[] V(double x, double y, double z) => new[] { x, y, z };

    static GlbDisconnectedParts.CoveredFaces Find(Soup soup, Soup others = null, Func<string, bool> glass = null) =>
        GlbDisconnectedParts.FindCoveredFaces(soup.Corners.ToArray(), Tol, glass == null ? null : soup.Tags.Select(glass).ToArray(),
            () => others == null ? null : new GlbDisconnectedParts.RayStop(new GlbDisconnectedParts.TriBvh(new[] { others.Corners.ToArray() }, null, 0).Blocked));

    // every face normal points away from the box's centre: the fixture is what its comments say
    [Fact]
    public void The_box_fixture_is_wound_outward()
    {
        var s = new Soup().Box("b", V(0, 0, 0), V(2, 2, 2));
        for (int f = 0; f < s.Count; f++)
        {
            double[] c = s.Corners.Skip(9 * f).Take(9).ToArray();
            double[] e1 = { c[3] - c[0], c[4] - c[1], c[5] - c[2] }, e2 = { c[6] - c[0], c[7] - c[1], c[8] - c[2] };
            double[] n = { e1[1] * e2[2] - e1[2] * e2[1], e1[2] * e2[0] - e1[0] * e2[2], e1[0] * e2[1] - e1[1] * e2[0] };
            double[] centre = { (c[0] + c[3] + c[6]) / 3 - 1, (c[1] + c[4] + c[7]) / 3 - 1, (c[2] + c[5] + c[8]) / 3 - 1 };
            Assert.True(n[0] * centre[0] + n[1] * centre[1] + n[2] * centre[2] > 0, s.Tags[f]);
        }
    }

    // THE SHIP'S CASE: a house whose own top face is wound down, and a deck laid on it, triangulated the other way
    static Soup HouseWithDeck(params string[] skipWalls)
    {
        var s = new Soup().Box("house", V(0, 0, 0), V(10, 10, 10), false, skipWalls.Concat(new[] { "top" }).ToArray());
        s.Quad("house:ceiling", V(0, 10, 0), V(10, 10, 0), V(10, 10, 10), V(0, 10, 10));                 // the top, wound DOWN
        s.Quad("deck", V(0, 10, 10), V(10, 10, 10), V(10, 10, 0), V(0, 10, 0), otherDiagonal: true);     // the deck, wound UP
        return s;
    }

    [Fact]
    public void A_ceiling_under_a_deck_is_left_out_and_the_deck_stays()
    {
        Soup s = HouseWithDeck();
        var found = Find(s);
        Assert.Equal(new[] { "house:ceiling" }, s.Dropped(found));
        Assert.Equal(2, found.Removed); Assert.Equal(4, found.Candidates);
    }

    // the Centurion's ceiling again: a wall missing lets a level ray out from under it, and no camera is down there
    [Fact]
    public void A_ceiling_is_left_out_even_when_a_wall_is_open()
    {
        Soup s = HouseWithDeck("east", "south");
        Assert.Equal(new[] { "house:ceiling" }, s.Dropped(Find(s)));
    }

    [Fact]
    public void A_sheet_doubled_in_the_open_keeps_both_sides()
    {
        var s = new Soup();
        s.Quad("flag:a", V(0, 0, 0), V(4, 0, 0), V(4, 3, 0), V(0, 3, 0));
        s.Quad("flag:b", V(0, 3, 0), V(4, 3, 0), V(4, 0, 0), V(0, 0, 0));
        var found = Find(s);
        Assert.Empty(s.Dropped(found));
        Assert.Equal(4, found.TwoSidedKept);
    }

    [Fact]
    public void A_pair_nothing_sees_is_kept()
    {
        var s = new Soup().Box("shell", V(-5, -5, -5), V(5, 5, 5));
        s.Quad("inner:a", V(-1, -1, 0), V(1, -1, 0), V(1, 1, 0), V(-1, 1, 0));
        s.Quad("inner:b", V(-1, 1, 0), V(1, 1, 0), V(1, -1, 0), V(-1, -1, 0));
        var found = Find(s);
        Assert.Empty(s.Dropped(found));
        Assert.Equal(4, found.BuriedKept);
    }

    // glass hides nothing: the same pair in a glass case is seen from both sides
    [Fact]
    public void Glass_stops_no_ray()
    {
        var s = new Soup().Box("case", V(-5, -5, -5), V(5, 5, 5));
        s.Quad("inner:a", V(-1, -1, 0), V(1, -1, 0), V(1, 1, 0), V(-1, 1, 0));
        s.Quad("inner:b", V(-1, 1, 0), V(1, 1, 0), V(1, -1, 0), V(-1, -1, 0));
        var found = Find(s, glass: tag => tag.StartsWith("case"));
        Assert.Empty(s.Dropped(found));
        Assert.Equal(4, found.TwoSidedKept);
    }

    // ...and the rest of the file stops rays as the group's own faces do
    [Fact]
    public void Another_part_of_the_file_hides_a_pair()
    {
        var s = new Soup();
        s.Quad("inner:a", V(-1, -1, 0), V(1, -1, 0), V(1, 1, 0), V(-1, 1, 0));
        s.Quad("inner:b", V(-1, 1, 0), V(1, 1, 0), V(1, -1, 0), V(-1, -1, 0));
        Assert.Equal(4, Find(s).TwoSidedKept);
        Assert.Equal(4, Find(s, others: new Soup().Box("shell", V(-5, -5, -5), V(5, 5, 5))).BuriedKept);
    }

    // a level sheet doubled in the open: its underside faces no camera, its top faces them all
    [Fact]
    public void The_underside_of_a_level_sheet_is_left_out()
    {
        var s = new Soup();
        s.Quad("awning:top", V(0, 5, 4), V(4, 5, 4), V(4, 5, 0), V(0, 5, 0));
        s.Quad("awning:under", V(0, 5, 0), V(4, 5, 0), V(4, 5, 4), V(0, 5, 4));
        Assert.Equal(new[] { "awning:under" }, s.Dropped(Find(s)));
    }

    // a tall open funnel with a doubled wall: the inside is hidden low down and seen at the rim
    [Fact]
    public void The_inside_of_a_funnel_goes_low_down_and_stays_at_the_rim()
    {
        var s = new Soup(); const int Rings = 20;
        for (int r = 0; r < Rings; r++)
        {
            double y0 = r * 2.0, y1 = y0 + 2.0;
            s.Box("out" + r, V(0, y0, 0), V(2, y1, 2), false, "top", "bottom");
            s.Box("in" + r, V(0, y0, 0), V(2, y1, 2), true, "top", "bottom");
        }
        s.Box("base", V(-3, -1, -3), V(5, 0, 5));
        var found = Find(s);
        string[] dropped = s.Dropped(found);
        Assert.All(dropped, tag => Assert.StartsWith("in", tag));                          // never the outside
        foreach (string wall in new[] { "north", "south", "east", "west" })
        {
            Assert.Contains("in0:" + wall, dropped);                                       // the bottom ring: nothing looks down that far
            Assert.DoesNotContain("in" + (Rings - 1) + ":" + wall, dropped);               // the rim: seen from above
        }
    }

    [Fact]
    public void Faces_apart_by_more_than_the_rounding_are_not_twins()
    {
        Soup s = HouseWithDeck();
        for (int i = 0; i < s.Count; i++) if (s.Tags[i] == "deck") for (int c = 0; c < 3; c++) s.Corners[9 * i + 3 * c + 1] += 10 * Tol;
        var found = Find(s);
        Assert.Equal(0, found.Candidates); Assert.Empty(s.Dropped(found));
    }

    // in the same plane but BESIDE it: a ceiling next to a deck that does not lie on it has no twin
    [Fact]
    public void A_face_beside_another_in_its_plane_is_not_covered()
    {
        var s = new Soup().Box("house", V(0, 0, 0), V(10, 10, 10), false, "top");
        s.Quad("house:ceiling", V(0, 10, 0), V(10, 10, 0), V(10, 10, 10), V(0, 10, 10));                 // wound DOWN
        s.Quad("deck", V(12, 10, 10), V(22, 10, 10), V(22, 10, 0), V(12, 10, 0), otherDiagonal: true);   // wound UP, beside the house
        var found = Find(s);
        Assert.Equal(0, found.Candidates); Assert.Empty(s.Dropped(found));
    }

    // ...nor does one that lies inside the other's bounding box but outside its outline: the two halves of a square
    [Fact]
    public void A_face_in_the_box_of_another_but_outside_its_outline_is_not_covered()
    {
        var s = new Soup().Box("house", V(0, 0, 0), V(10, 10, 10), false, "top");
        s.Tri("house:ceiling", V(0, 10, 0), V(10, 10, 0), V(0, 10, 10));      // wound DOWN, the half at the origin
        s.Tri("deck", V(10, 10, 10), V(10, 10, 0), V(0, 10, 10));             // wound UP, the other half
        var found = Find(s);
        Assert.Equal(0, found.Candidates); Assert.Empty(s.Dropped(found));
    }

    // "the same plane" is within the rounding, not to the bit: half the rounding apart is still a twin
    [Fact]
    public void Faces_apart_by_less_than_the_rounding_are_twins()
    {
        Soup s = HouseWithDeck();
        for (int i = 0; i < s.Count; i++) if (s.Tags[i] == "deck") for (int c = 0; c < 3; c++) s.Corners[9 * i + 3 * c + 1] += 0.5 * Tol;
        Assert.Equal(new[] { "house:ceiling" }, s.Dropped(Find(s)));
    }

    // A FACE IS SEEN AS SOON AS ITS RIM IS (review of the first cut): a doubled panel with a plate three quarters its
    // size standing just in front of it. Every point in the panel's middle is behind the plate; its rim is in plain view.
    [Fact]
    public void A_face_whose_rim_shows_round_a_plate_in_front_of_it_stays()
    {
        var s = new Soup(); double[] a = V(0, 0, 0), b = V(0, 10, 5), c = V(0, 0, 10), g = V(0, 10.0 / 3, 5);
        s.Tri("panel:east", a, b, c); s.Tri("panel:west", c, b, a);
        double[] In(double[] p, double x) => V(x, g[1] + 0.75 * (p[1] - g[1]), g[2] + 0.75 * (p[2] - g[2]));
        foreach (double x in new[] { 0.002, 0.1 }) s.Tri("plate", In(a, x), In(b, x), In(c, x));   // 0.002 off the panel: no camera looks along a gap that thin
        s.Quad("plate", In(a, .002), In(b, .002), In(b, .1), In(a, .1)); s.Quad("plate", In(b, .002), In(c, .002), In(c, .1), In(b, .1)); s.Quad("plate", In(c, .002), In(a, .002), In(a, .1), In(c, .1));
        var found = Find(s);
        Assert.Empty(s.Dropped(found));
        Assert.Equal(2, found.Candidates); Assert.Equal(2, found.TwoSidedKept);
    }

    // "wound the other way" is all but exactly opposed: a speck smaller than the rounding lies "in the plane" of whatever
    // it touches at any angle, and one tilted 30 degrees off the deck's opposite is not its twin
    [Fact]
    public void A_speck_at_an_angle_is_not_a_twin()
    {
        var s = new Soup().Box("house", V(0, 0, 0), V(10, 10, 10), false, "top");
        s.Quad("deck", V(0, 10, 10), V(10, 10, 10), V(10, 10, 0), V(0, 10, 0));
        double e = 0.5 * Tol;   // its normal is (0.5, -0.866, 0): 30 degrees off straight down
        s.Tri("speck", V(5, 10, 5), V(5, 10, 5 + e), V(5 - 0.866 * e, 10 - 0.5 * e, 5));
        var found = Find(s);
        Assert.Empty(s.Dropped(found));
        Assert.Equal(0, found.Candidates);
    }

    // the CENTROID must lie on the other face: a ceiling under a deck with a hole over its middle has six of its seven
    // points covered and is still not that deck's twin
    [Fact]
    public void A_face_whose_centroid_is_uncovered_is_not_a_twin()
    {
        var s = new Soup().Box("house", V(0, 0, 0), V(10, 10, 10), false, "top");
        s.Tri("ceiling", V(0, 10, 0), V(10, 10, 0), V(0, 10, 10));                              // wound DOWN, centroid (3.33, 3.33)
        void Up(double x0, double x1, double z0, double z1) => s.Quad("deck", V(x0, 10, z1), V(x1, 10, z1), V(x1, 10, z0), V(x0, 10, z0));
        Up(-1, 3, -1, 11); Up(3.7, 11, -1, 11); Up(3, 3.7, -1, 3); Up(3, 3.7, 3.7, 11);       // a deck with a hole 3..3.7 square
        Assert.Empty(s.Dropped(Find(s)));
    }

    // a copy wound the SAME way is a different defect (it fights under any renderer) and is not this rule's
    [Fact]
    public void A_copy_wound_the_same_way_is_left_alone()
    {
        var s = new Soup().Box("house", V(0, 0, 0), V(10, 10, 10));
        s.Quad("deck", V(0, 10, 10), V(10, 10, 10), V(10, 10, 0), V(0, 10, 0), otherDiagonal: true);
        var found = Find(s);
        Assert.Equal(0, found.Candidates); Assert.Empty(s.Dropped(found));
    }

    // a small plate lying on a big one is found although the big one is not covered by it
    [Fact]
    public void A_small_hidden_face_on_a_large_one_is_left_out()
    {
        var s = new Soup().Box("house", V(0, 0, 0), V(10, 10, 10), false, "top");
        s.Quad("deck", V(0, 10, 10), V(10, 10, 10), V(10, 10, 0), V(0, 10, 0));
        s.Quad("patch", V(4, 10, 4), V(6, 10, 4), V(6, 10, 6), V(4, 10, 6));                // wound DOWN, inside the house
        var found = Find(s);
        Assert.Equal(new[] { "patch" }, s.Dropped(found));
    }

    [Fact]
    public void Degenerate_and_empty_input_is_no_one_s_twin()
    {
        Assert.Equal(0, GlbDisconnectedParts.FindCoveredFaces(new double[0], Tol, null, null).Candidates);
        var s = new Soup().Tri("needle", V(0, 0, 0), V(1, 0, 0), V(2, 0, 0)).Tri("needle", V(2, 0, 0), V(1, 0, 0), V(0, 0, 0))
            .Tri("nan", V(double.NaN, 0, 0), V(1, 0, 0), V(0, 1, 0));
        Assert.Equal(0, Find(s).Candidates);
    }

    // the ray tree: a segment left out, a triangle of the first segment left out, and the rest still in the way
    [Fact]
    public void The_ray_tree_leaves_out_what_it_is_told_to()
    {
        double[] wallA = new Soup().Quad("a", V(-1, -1, 2), V(1, -1, 2), V(1, 1, 2), V(-1, 1, 2)).Corners.ToArray();
        double[] wallB = new Soup().Quad("b", V(-1, -1, 4), V(1, -1, 4), V(1, 1, 4), V(-1, 1, 4)).Corners.ToArray();
        var tree = new GlbDisconnectedParts.TriBvh(new[] { wallA, wallB }, null, 0, 1);   // a leaf per triangle: the ray walks the tree
        bool Ray(bool[] skipSeg, bool[] skipTri) => tree.Blocked(0.25, 0.1, 0, 0, 0, 1, skipSeg, skipTri);
        Assert.True(Ray(null, null));
        Assert.True(Ray(new[] { true, false }, null));                    // the first wall gone, the second stops it
        Assert.True(Ray(new[] { false, true }, null));
        Assert.False(Ray(new[] { true, true }, null));
        Assert.True(Ray(new[] { false, true }, new[] { false, true }));   // the ray meets the first wall's FIRST triangle
        Assert.False(Ray(new[] { false, true }, new[] { true, false }));
        Assert.False(tree.Blocked(0.25, 0.1, 0, 0, 0, -1));               // nothing behind
        Assert.False(tree.Blocked(5, 5, 0, 0, 0, 1));                     // nothing beside
        Assert.True(tree.Blocked(0.25, 0.1, 0, 0.0001, 0, 1));            // ...and nearly along an axis is not along it
    }

    // every box of the tree is its triangles' box widened by the pad: a point within the rounding of a flat face's plane
    // is outside that face's own box, and the coincidence test asks the boxes first
    [Fact]
    public void The_tree_s_boxes_are_widened_by_the_pad()
    {
        var soup = new Soup();
        for (int i = 0; i < 6; i++) soup.Tri("t", V(3 * i, 1, 0), V(3 * i + 1, 1, 0), V(3 * i, 1, 2));   // level triangles in a row, y = 1
        var tree = new GlbDisconnectedParts.TriBvh(new[] { soup.Corners.ToArray() }, null, 0.25, 1);
        Assert.True(tree.Count.Length > 6);   // it split
        for (int node = 0; node < tree.Count.Length; node++)
        {
            Assert.Equal(0.75, tree.Box[6 * node + 1], 12); Assert.Equal(1.25, tree.Box[6 * node + 4], 12);   // y: 1 either way by the pad
            Assert.Equal(-0.25, tree.Box[6 * node + 2], 12); Assert.Equal(2.25, tree.Box[6 * node + 5], 12);  // z: 0..2 likewise
        }
        Assert.Equal(-0.25, tree.Box[0], 12); Assert.Equal(16.25, tree.Box[3], 12);                           // x of the whole: 0..16
    }

    // a ray stops on a triangle's FRONT only: from behind, a wall is not in the way (a culling camera does not draw it)
    [Fact]
    public void A_triangle_seen_from_behind_stops_no_ray()
    {
        double[] wall = new Soup().Quad("w", V(-1, -1, 2), V(1, -1, 2), V(1, 1, 2), V(-1, 1, 2)).Corners.ToArray();   // its normal is +Z
        var tree = new GlbDisconnectedParts.TriBvh(new[] { wall }, null, 0);
        Assert.True(tree.Blocked(0.25, 0.1, 0, 0, 0, 1));     // going +Z, towards a camera the wall faces
        Assert.False(tree.Blocked(0.25, 0.1, 4, 0, 0, -1));   // going -Z through its back
    }

    // every triangle of a large, uneven set is found by a ray aimed at it and by none aimed beside it: the tree against
    // the plain loop over all triangles, 400 rays, with a big sheet behind dense small ones
    [Fact]
    public void The_ray_tree_answers_as_the_plain_loop_does()
    {
        var soup = new Soup().Quad("big", V(-500, -500, 9), V(500, -500, 9), V(500, 500, 9), V(-500, 500, 9));
        var rnd = new Random(7);
        for (int i = 0; i < 600; i++)
        {
            double x = rnd.NextDouble() * 40 - 20, y = rnd.NextDouble() * 40 - 20, z = rnd.NextDouble() * 6, w = 0.05 + rnd.NextDouble() * (i % 50 == 0 ? 6 : 0.4);
            soup.Tri("small", V(x, y, z), V(x + w, y, z + rnd.NextDouble() * 0.3), V(x, y + w, z));
        }
        double[] c = soup.Corners.ToArray();
        var tree = new GlbDisconnectedParts.TriBvh(new[] { c }, null, 0, 2);
        var skip = new bool[soup.Count]; skip[0] = skip[1] = true;   // the big sheet left out
        bool Plain(double ox, double oy, double oz, double dx, double dy, double dz, bool withoutBig)
        {
            for (int t = withoutBig ? 2 : 0; t < soup.Count; t++)
            {
                double[] a = { c[9 * t], c[9 * t + 1], c[9 * t + 2] }, e1 = { c[9 * t + 3] - a[0], c[9 * t + 4] - a[1], c[9 * t + 5] - a[2] }, e2 = { c[9 * t + 6] - a[0], c[9 * t + 7] - a[1], c[9 * t + 8] - a[2] };
                double px = dy * e2[2] - dz * e2[1], py = dz * e2[0] - dx * e2[2], pz = dx * e2[1] - dy * e2[0], det = e1[0] * px + e1[1] * py + e1[2] * pz;
                if (det > -1e-18) continue;   // only a triangle showing its front to where the ray goes stops it
                double tx = ox - a[0], ty = oy - a[1], tz = oz - a[2], u = (tx * px + ty * py + tz * pz) / det;
                if (u < -1e-9 || u > 1 + 1e-9) continue;
                double qx = ty * e1[2] - tz * e1[1], qy = tz * e1[0] - tx * e1[2], qz = tx * e1[1] - ty * e1[0], v = (dx * qx + dy * qy + dz * qz) / det;
                if (v < -1e-9 || u + v > 1 + 1e-9) continue;
                if ((e2[0] * qx + e2[1] * qy + e2[2] * qz) / det > 0) return true;
            }
            return false;
        }
        int stopped = 0, free = 0;
        for (int i = 0; i < 400; i++)
        {
            double ox = rnd.NextDouble() * 60 - 30, oy = rnd.NextDouble() * 60 - 30, oz = rnd.NextDouble() * 12 - 2;
            double dx = rnd.NextDouble() - 0.5, dy = rnd.NextDouble() - 0.5, dz = rnd.NextDouble() - 0.5, l = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (i % 2 == 1)
            {   // every other ray is aimed at a small triangle: a ray thrown at random meets one twice in 400
                int t = 2 + rnd.Next(soup.Count - 2);
                dx = (c[9 * t] + c[9 * t + 3] + c[9 * t + 6]) / 3 - ox; dy = (c[9 * t + 1] + c[9 * t + 4] + c[9 * t + 7]) / 3 - oy; dz = (c[9 * t + 2] + c[9 * t + 5] + c[9 * t + 8]) / 3 - oz;
                l = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }
            if (i % 10 == 0) { dx = 0; dy = 0; dz = i % 20 == 0 ? 1 : -1; l = 1; }   // along an axis: the flat branches of the box test
            dx /= l; dy /= l; dz /= l;
            bool all = Plain(ox, oy, oz, dx, dy, dz, false), small = Plain(ox, oy, oz, dx, dy, dz, true);
            Assert.Equal(all, tree.Blocked(ox, oy, oz, dx, dy, dz));
            Assert.Equal(small, tree.Blocked(ox, oy, oz, dx, dy, dz, null, skip));
            Assert.Equal(small, tree.Blocked(ox, oy, oz, dx, dy, dz, new[] { false }, skip));
            Assert.False(tree.Blocked(ox, oy, oz, dx, dy, dz, new[] { true }, null));
            if (small) stopped++; else free++;
        }
        Assert.True(stopped >= 20 && free >= 20, stopped + " stopped, " + free + " free");   // the comparison saw both answers
    }

    // the cameras, written out: straight above, then eight ways round at 65, 40 and 15 degrees above the horizon, the
    // middle ring turned half a step. (The first version of this test took its bound from the code's own constant and
    // could not fail on it - review.)
    [Fact]
    public void The_cameras_are_the_twenty_five_directions_the_rule_names()
    {
        var field = typeof(GlbDisconnectedParts).GetField("CoveredViews", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var views = (double[])field.GetValue(null);
        var expected = new List<double> { 0, 1, 0 };
        foreach (var ring in new[] { (up: 65.0, turn: 0.0), (up: 40.0, turn: 22.5), (up: 15.0, turn: 0.0) })
            for (int k = 0; k < 8; k++)
            {
                double e = ring.up * Math.PI / 180.0, a = (k * 45.0 + ring.turn) * Math.PI / 180.0;
                expected.Add(Math.Cos(e) * Math.Cos(a)); expected.Add(Math.Sin(e)); expected.Add(Math.Cos(e) * Math.Sin(a));
            }
        Assert.Equal(expected.Count, views.Length);
        for (int i = 0; i < views.Length; i++) Assert.Equal(expected[i], views[i], 12);
        Assert.Equal(15.0, GlbDisconnectedParts.CoveredLowestCamera);
    }

    // the denser sets a larger face is asked from: 16 ways round on the same three rings, 32 ways round on five; every
    // one a unit vector, none below 15 degrees, and each ring evenly spaced
    [Theory]
    [InlineData("CoveredViewsMedium", 1 + 3 * 16, 16)]
    [InlineData("CoveredViewsLarge", 1 + 5 * 32, 32)]
    public void The_denser_camera_sets_stay_above_fifteen_degrees(string name, int count, int round)
    {
        var views = (double[])typeof(GlbDisconnectedParts).GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).GetValue(null);
        Assert.Equal(count * 3, views.Length);
        Assert.Equal(new[] { 0.0, 1.0, 0.0 }, views.Take(3).ToArray());
        double lowest = Math.Sin(15.0 * Math.PI / 180.0);
        for (int i = 0; i < views.Length; i += 3)
        {
            Assert.Equal(1.0, Math.Sqrt(views[i] * views[i] + views[i + 1] * views[i + 1] + views[i + 2] * views[i + 2]), 12);
            Assert.True(views[i + 1] >= lowest - 1e-12);
        }
        for (int ring = 0; ring < (count - 1) / round; ring++)
            for (int k = 0; k < round; k++)
            {
                int at = 3 * (1 + ring * round + k);
                Assert.Equal(views[3 * (1 + ring * round) + 1], views[at + 1], 12);                                          // one height a ring
                Assert.Equal(k * 360.0 / round, (Math.Atan2(views[at + 2], views[at]) * 180.0 / Math.PI + 360.0) % 360.0, 9);   // evenly round, from +X
            }
        Assert.Equal(Math.Sin(15.0 * Math.PI / 180.0), views[views.Length - 2], 12);                                          // the last ring is the lowest: 15 degrees
    }

    // a long thin face behind a wall with one narrow slit, seen through it only from a direction BETWEEN the eight a small
    // face is asked from (an azimuth of 11.25 degrees): a face this size is asked from thirty-two, and is seen
    [Fact]
    public void A_large_face_seen_through_a_slit_between_the_eight_directions_is_seen()
    {
        var s = new Soup();
        s.Quad("strip:slit", V(0, 0, 40), V(0, 0, 0), V(0, 1, 0), V(0, 1, 40));        // faces +X, 40 long and 1 high
        s.Quad("strip:open", V(0, 0, 0), V(0, 0, 40), V(0, 1, 40), V(0, 1, 0));        // faces -X, in the open
        // a wall at x = 5, far taller than any camera looks over, with a slit 1.2 wide in it at z = 21: from the strip's
        // middle (z = 20) the slit lies 11.3 degrees round from +X
        s.Box("wall", V(5, -1, -400), V(5.2, 400, 20.4)); s.Box("wall", V(5, -1, 21.6), V(5.2, 400, 400));
        var found = Find(s);
        Assert.Empty(s.Dropped(found));
        Assert.Equal(4, found.TwoSidedKept);
        // ...and with the slit closed it is hidden: the fixture can tell
        var shut = new Soup();
        shut.Quad("strip:slit", V(0, 0, 40), V(0, 0, 0), V(0, 1, 0), V(0, 1, 40));
        shut.Quad("strip:open", V(0, 0, 0), V(0, 0, 40), V(0, 1, 40), V(0, 1, 0));
        shut.Box("wall", V(5, -1, -400), V(5.2, 400, 400));
        Assert.Equal(new[] { "strip:slit" }, shut.Dropped(Find(shut)));
    }

    // a doubled panel, standing, one side open and the other in a slot: a wall one unit in front of it, 2.45 units high.
    // Only the steep cameras (65 degrees) look over the wall at it, and they see it at 25 degrees off edge-on or less:
    // every camera more than 30 degrees off edge-on is stopped by the wall. It is seen.
    [Fact]
    public void A_face_seen_only_steeply_and_nearly_edge_on_is_seen()
    {
        var s = new Soup();
        s.Quad("panel:slot", V(0, 0, 4), V(0, 0, 0), V(0, 0.5, 0), V(0, 0.5, 4));      // faces +X, into the slot
        s.Quad("panel:open", V(0, 0, 0), V(0, 0, 4), V(0, 0.5, 4), V(0, 0.5, 0));      // faces -X
        s.Box("wall", V(1, -1, -50), V(1.2, 2.45, 54));
        var found = Find(s);
        Assert.Empty(s.Dropped(found));
        Assert.Equal(4, found.TwoSidedKept);
        // ...and with the wall forty units high no camera looks over it, not even along the slot: the slot side goes
        var deep = new Soup();
        deep.Quad("panel:slot", V(0, 0, 4), V(0, 0, 0), V(0, 0.5, 0), V(0, 0.5, 4));
        deep.Quad("panel:open", V(0, 0, 0), V(0, 0, 4), V(0, 0.5, 4), V(0, 0.5, 0));
        deep.Box("wall", V(1, -1, -50), V(1.2, 40, 54));
        Assert.Equal(new[] { "panel:slot" }, deep.Dropped(Find(deep)));
    }

    // ...and one seen only by the LOWEST cameras: a doubled panel under a roof that reaches three units out. From 40
    // degrees up the roof is in the way; from 15 it is not.
    [Fact]
    public void A_face_seen_only_from_the_lowest_cameras_is_seen()
    {
        var s = new Soup();
        s.Quad("panel:in", V(0, 0, 4), V(0, 0, 0), V(0, 0.5, 0), V(0, 0.5, 4));        // faces +X, under the roof
        s.Quad("panel:out", V(0, 0, 0), V(0, 0, 4), V(0, 0.5, 4), V(0, 0.5, 0));       // faces -X, the roof ends a unit beyond it
        s.Box("roof", V(-1, 1.5, -50), V(3, 1.7, 54));
        var found = Find(s);
        Assert.Empty(s.Dropped(found));
        Assert.Equal(4, found.TwoSidedKept);
        // ...the same roof reaching eight units out hides it from 15 degrees as well
        var far = new Soup();
        far.Quad("panel:in", V(0, 0, 4), V(0, 0, 0), V(0, 0.5, 0), V(0, 0.5, 4));
        far.Quad("panel:out", V(0, 0, 0), V(0, 0, 4), V(0, 0.5, 4), V(0, 0.5, 0));
        far.Box("roof", V(-1, 1.5, -50), V(8, 1.7, 54));
        Assert.Equal(new[] { "panel:in" }, far.Dropped(Find(far)));
    }

    // the rays leave from just in FRONT of the face, clear of what lies within the rounding of its plane: a sheet with a
    // copy wound the SAME way half a rounding above it (the kind of duplicate this rule leaves alone) and a twin under
    // it. The copy is not in the sheet's way - both are seen, and only the twin goes.
    [Fact]
    public void What_lies_within_the_rounding_of_a_face_is_not_in_its_way()
    {
        var s = new Soup();
        s.Quad("sheet:top", V(0, 5, 4), V(4, 5, 4), V(4, 5, 0), V(0, 5, 0));                                           // wound UP
        s.Quad("sheet:copy", V(0, 5 + 0.5 * Tol, 4), V(4, 5 + 0.5 * Tol, 4), V(4, 5 + 0.5 * Tol, 0), V(0, 5 + 0.5 * Tol, 0));   // wound UP, half a rounding above
        s.Quad("sheet:under", V(0, 5, 0), V(4, 5, 0), V(4, 5, 4), V(0, 5, 4));                                         // wound DOWN
        var found = Find(s);
        Assert.Equal(new[] { "sheet:under" }, s.Dropped(found));
        Assert.Equal(0, found.BuriedKept);   // with the rays starting IN the plane the copy stops them, and the top reads as buried
    }

    // HOW MUCH of a face must lie on another: its centroid and four more of the seven points. A ceiling six of whose
    // points are under the deck is a twin; one with four is not.
    [Theory]
    [InlineData(6.0, true)]     // the deck reaches x = 6: every point but the one towards the far corner
    [InlineData(4.2, false)]    // x = 4.2: the centroid and three more
    public void A_face_is_a_twin_when_most_of_it_lies_on_another(double deckReach, bool twin)
    {
        var s = new Soup().Box("house", V(0, 0, 0), V(10, 10, 10), false, "top");
        s.Tri("ceiling", V(0, 10, 0), V(10, 10, 0), V(0, 10, 10));                                             // wound DOWN
        s.Quad("deck", V(-1, 10, 11), V(deckReach, 10, 11), V(deckReach, 10, -1), V(-1, 10, -1));             // wound UP
        Assert.Equal(twin ? new[] { "ceiling" } : new string[0], s.Dropped(Find(s)));
    }

    // ---- through the fuse, read back from the written bytes ----

    sealed class Part { public string Name; public float[] Positions; public int[] Indices; public int Material; public Part Second; public double[] Scale; }   // Second: another primitive of the same mesh; Scale: the node's

    static Part PartOf(string name, int material, Soup soup, Func<string, bool> pick)
    {
        var pos = new List<float>(); var idx = new List<int>();
        for (int f = 0; f < soup.Count; f++)
        {
            if (!pick(soup.Tags[f])) continue;
            for (int c = 0; c < 9; c++) pos.Add((float)soup.Corners[9 * f + c]);
            idx.Add(pos.Count / 3 - 3); idx.Add(pos.Count / 3 - 2); idx.Add(pos.Count / 3 - 1);
        }
        return new Part { Name = name, Positions = pos.ToArray(), Indices = idx.ToArray(), Material = material };
    }

    static byte[] BuildGlb(JArray materials, params Part[] parts)
    {
        var bin = new List<byte>();
        var accessors = new JArray(); var views = new JArray(); var meshes = new JArray(); var nodes = new JArray(); var sceneNodes = new JArray();
        int View(int offset, int length, int target) { views.Add(new JObject { ["buffer"] = 0, ["byteOffset"] = offset, ["byteLength"] = length, ["target"] = target }); return views.Count - 1; }
        foreach (Part part in parts)
        {
            var primitives = new JArray();
            for (Part prim = part; prim != null; prim = prim.Second)
            {
                int vcount = prim.Positions.Length / 3;
                int off = bin.Count; foreach (float f in prim.Positions) bin.AddRange(BitConverter.GetBytes(f));
                double[] mins = { double.MaxValue, double.MaxValue, double.MaxValue }, maxs = { double.MinValue, double.MinValue, double.MinValue };
                for (int i = 0; i < vcount; i++) for (int a = 0; a < 3; a++) { mins[a] = Math.Min(mins[a], prim.Positions[i * 3 + a]); maxs[a] = Math.Max(maxs[a], prim.Positions[i * 3 + a]); }
                accessors.Add(new JObject { ["bufferView"] = View(off, vcount * 12, 34962), ["componentType"] = 5126, ["count"] = vcount, ["type"] = "VEC3", ["min"] = new JArray(mins), ["max"] = new JArray(maxs) });
                int posAcc = accessors.Count - 1;
                off = bin.Count; foreach (int i in prim.Indices) bin.AddRange(BitConverter.GetBytes((ushort)i));
                while ((bin.Count & 3) != 0) bin.Add(0);
                accessors.Add(new JObject { ["bufferView"] = View(off, prim.Indices.Length * 2, 34963), ["componentType"] = 5123, ["count"] = prim.Indices.Length, ["type"] = "SCALAR" });
                primitives.Add(new JObject { ["attributes"] = new JObject { ["POSITION"] = posAcc }, ["indices"] = accessors.Count - 1, ["material"] = prim.Material });
            }
            meshes.Add(new JObject { ["name"] = part.Name + "Mesh", ["primitives"] = primitives });
            var nodeJson = new JObject { ["name"] = part.Name, ["mesh"] = meshes.Count - 1 };
            if (part.Scale != null) nodeJson["scale"] = new JArray(part.Scale);
            nodes.Add(nodeJson); sceneNodes.Add(nodes.Count - 1);
        }
        var root = new JObject
        {
            ["asset"] = new JObject { ["version"] = "2.0" }, ["scene"] = 0,
            ["scenes"] = new JArray { new JObject { ["nodes"] = sceneNodes } },
            ["nodes"] = nodes, ["meshes"] = meshes, ["accessors"] = accessors, ["bufferViews"] = views,
            ["buffers"] = new JArray { new JObject { ["byteLength"] = bin.Count } }, ["materials"] = materials,
        };
        byte[] json = Encoding.UTF8.GetBytes(root.ToString(Newtonsoft.Json.Formatting.None));
        int jsonPad = (4 - json.Length % 4) % 4, binPad = (4 - bin.Count % 4) % 4;
        var bytes = new List<byte>();
        void U32(uint v) { bytes.AddRange(BitConverter.GetBytes(v)); }
        U32(0x46546C67); U32(2); U32((uint)(12 + 8 + json.Length + jsonPad + 8 + bin.Count + binPad));
        U32((uint)(json.Length + jsonPad)); U32(0x4E4F534A); bytes.AddRange(json); for (int i = 0; i < jsonPad; i++) bytes.Add(0x20);
        U32((uint)(bin.Count + binPad)); U32(0x004E4942); bytes.AddRange(bin); for (int i = 0; i < binPad; i++) bytes.Add(0);
        return bytes.ToArray();
    }

    static JArray Materials(params string[] alphaModes) =>
        new JArray(alphaModes.Select((m, i) => { var j = new JObject { ["name"] = "m" + i }; if (m != null) j["alphaMode"] = m; return j; }));

    // per material of the fused node: the triangles as written, each with its normal's Y and its centroid's Y
    static Dictionary<int, List<(double normalY, double height)>> FusedTriangles(byte[] glb, string nodeName)
    {
        uint jsonLen = BitConverter.ToUInt32(glb, 12);
        JObject root = JObject.Parse(Encoding.UTF8.GetString(glb, 20, (int)jsonLen));
        int binAt = 20 + (int)jsonLen + 8;
        JObject node = root["nodes"].OfType<JObject>().First(n => (string)n["name"] == nodeName);
        var result = new Dictionary<int, List<(double, double)>>();
        foreach (JObject prim in ((JArray)root["meshes"][node.Value<int>("mesh")]["primitives"]).OfType<JObject>())
        {
            int Offset(int accessor) { var a = (JObject)root["accessors"][accessor]; var v = (JObject)root["bufferViews"][a.Value<int>("bufferView")]; return binAt + (v.Value<int?>("byteOffset") ?? 0) + (a.Value<int?>("byteOffset") ?? 0); }
            int posAt = Offset(prim["attributes"].Value<int>("POSITION")), idxAt = Offset(prim.Value<int>("indices"));
            int count = root["accessors"][prim.Value<int>("indices")].Value<int>("count");
            var list = new List<(double, double)>();
            for (int t = 0; t < count; t += 3)
            {
                var p = new double[9];
                for (int c = 0; c < 3; c++) { int i = (int)BitConverter.ToUInt32(glb, idxAt + 4 * (t + c)); for (int a = 0; a < 3; a++) p[3 * c + a] = BitConverter.ToSingle(glb, posAt + 12 * i + 4 * a); }
                double nx = (p[4] - p[1]) * (p[8] - p[2]) - (p[5] - p[2]) * (p[7] - p[1]), ny = (p[5] - p[2]) * (p[6] - p[0]) - (p[3] - p[0]) * (p[8] - p[2]), nz = (p[3] - p[0]) * (p[7] - p[1]) - (p[4] - p[1]) * (p[6] - p[0]);
                double l = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                list.Add((ny / l, (p[1] + p[4] + p[7]) / 3));
            }
            result[prim.Value<int?>("material") ?? -1] = list;
        }
        return result;
    }

    // of one material of the fused node: every written vertex's height and the Y of its normal
    static List<(double y, double ny)> FusedNormals(byte[] glb, string nodeName, int material)
    {
        uint jsonLen = BitConverter.ToUInt32(glb, 12);
        JObject root = JObject.Parse(Encoding.UTF8.GetString(glb, 20, (int)jsonLen));
        int binAt = 20 + (int)jsonLen + 8;
        JObject node = root["nodes"].OfType<JObject>().First(n => (string)n["name"] == nodeName);
        JObject prim = ((JArray)root["meshes"][node.Value<int>("mesh")]["primitives"]).OfType<JObject>().First(p => (p.Value<int?>("material") ?? -1) == material);
        int Offset(int accessor) { var a = (JObject)root["accessors"][accessor]; var v = (JObject)root["bufferViews"][a.Value<int>("bufferView")]; return binAt + (v.Value<int?>("byteOffset") ?? 0) + (a.Value<int?>("byteOffset") ?? 0); }
        int posAcc = prim["attributes"].Value<int>("POSITION"), nrmAcc = prim["attributes"].Value<int>("NORMAL");
        int count = root["accessors"][posAcc].Value<int>("count");
        var list = new List<(double, double)>();
        for (int i = 0; i < count; i++) list.Add((BitConverter.ToSingle(glb, Offset(posAcc) + 12 * i + 4), BitConverter.ToSingle(glb, Offset(nrmAcc) + 12 * i + 4)));
        return list;
    }

    [Fact]
    public void The_fuse_leaves_the_ceiling_out_and_says_so()
    {
        Soup s = HouseWithDeck();
        byte[] glb = BuildGlb(Materials(null, null), PartOf("House", 0, s, t => t.StartsWith("house")), PartOf("Deck", 1, s, t => t == "deck"));
        var r = GlbDisconnectedParts.FuseNodes(glb, new[] { 0, 1 }, 0, "Fused");
        Assert.True(r.Changed);
        Assert.Equal(14, r.SourceTriangles); Assert.Equal(2, r.HiddenFacesRemoved); Assert.Equal(12, r.OutputTriangles);
        Assert.EndsWith("; 2 hidden twin face(s) left out", r.Details[0]);   // the Workshop's status line says it
        string line = Assert.Single(r.Details, d => d.StartsWith("hidden twins:"));
        Assert.StartsWith("hidden twins: 2 face(s) lay exactly on a face wound the other way and no camera above the horizon can see them - left out, so one surface remains (by part: House 2)", line);
        var tris = FusedTriangles(r.Bytes, r.FusedNodeName);
        Assert.Equal(10, tris[0].Count);
        Assert.DoesNotContain(tris[0], t => Math.Abs(t.height - 10) < 1e-6);               // nothing of the house is left in the deck's plane
        Assert.Equal(2, tris[1].Count);
        Assert.All(tris[1], t => { Assert.Equal(1.0, t.normalY, 6); Assert.Equal(10.0, t.height, 6); });   // the deck, facing up
        Assert.Equal(12, tris.Values.Sum(l => l.Count));
    }

    // a fuse with nothing to leave out says nothing about it and preserves every triangle, as before
    [Fact]
    public void A_fuse_without_twins_is_as_it_was()
    {
        var s = new Soup().Box("house", V(0, 0, 0), V(10, 10, 10));
        byte[] glb = BuildGlb(Materials(null, null), PartOf("House", 0, s, t => true));
        var r = GlbDisconnectedParts.FuseNodes(glb, new[] { 0 }, 0, "Fused");
        Assert.Equal(12, r.SourceTriangles); Assert.Equal(12, r.OutputTriangles); Assert.Equal(0, r.HiddenFacesRemoved);
        Assert.DoesNotContain(r.Details, d => d.StartsWith("hidden twins:"));
        Assert.EndsWith("face(s) smaller than the weld kept collapsed", r.Details[0]);
    }

    // SEVERAL GROUPS share one ray tree of the file (OccluderCache.RayTree): another GROUP's part hides a pair as another
    // node does, a group's own nodes are not in its own way, and the answer is the one group's own
    [Theory]
    [InlineData("BLEND", "kept: 4 whose pair shows on both sides")]
    [InlineData(null, "4 buried on both sides")]
    public void Groups_fused_together_see_each_other(string caseAlpha, string expected)
    {
        var s = HouseWithDeck();
        s.Box("case", V(20, 0, 0), V(30, 10, 10));
        s.Quad("inner:a", V(24, 4, 5), V(26, 4, 5), V(26, 6, 5), V(24, 6, 5));
        s.Quad("inner:b", V(24, 6, 5), V(26, 6, 5), V(26, 4, 5), V(24, 4, 5));
        byte[] glb = BuildGlb(Materials(null, caseAlpha, null),
            PartOf("House", 0, s, t => t.StartsWith("house")), PartOf("Deck", 2, s, t => t == "deck"),
            PartOf("Inner", 0, s, t => t.StartsWith("inner")), PartOf("Case", 1, s, t => t.StartsWith("case")));
        var jobs = new List<GlbDisconnectedParts.FuseJob>
        {
            new GlbDisconnectedParts.FuseJob { NodeIndices = new List<int> { 0, 1 }, Name = "Fused_A" },
            new GlbDisconnectedParts.FuseJob { NodeIndices = new List<int> { 2 }, Name = "Fused_B" },
            new GlbDisconnectedParts.FuseJob { NodeIndices = new List<int> { 3 }, Name = "Fused_C" },
        };
        byte[] fused = GlbDisconnectedParts.FuseGroups(glb, jobs, 0, out List<GlbDisconnectedParts.Result> results);
        Assert.Equal(2, results[0].HiddenFacesRemoved); Assert.Equal(12, results[0].OutputTriangles);      // the house, as on its own
        Assert.Equal(0, results[1].HiddenFacesRemoved);
        Assert.Contains(expected, Assert.Single(results[1].Details, d => d.StartsWith("hidden twins:")));
        Assert.Equal(0, results[2].HiddenFacesRemoved); Assert.Equal(12, results[2].OutputTriangles);
        Assert.Equal(12, FusedTriangles(fused, results[0].FusedNodeName).Values.Sum(l => l.Count));
    }

    // a group is not in its own way in the shared tree: a part that is a glass case AND the pair inside it (one node, two
    // primitives) is read from its own welded faces, where glass stops nothing - not from its copy in the file's tree, where
    // the node counts as solid because not all of it is glass
    [Fact]
    public void A_group_is_not_in_its_own_way()
    {
        var s = new Soup().Box("case", V(20, 0, 0), V(30, 10, 10));
        s.Quad("inner:a", V(24, 4, 5), V(26, 4, 5), V(26, 6, 5), V(24, 6, 5));
        s.Quad("inner:b", V(24, 6, 5), V(26, 6, 5), V(26, 4, 5), V(24, 4, 5));
        Part mixed = PartOf("Showcase", 0, s, t => t.StartsWith("inner"));
        mixed.Second = PartOf("Showcase", 1, s, t => t.StartsWith("case"));
        var other = new Soup().Box("crate", V(-20, 0, 0), V(-10, 10, 10));
        byte[] glb = BuildGlb(Materials(null, "BLEND"), mixed, PartOf("Crate", 0, other, t => true));
        var jobs = new List<GlbDisconnectedParts.FuseJob>
        {
            new GlbDisconnectedParts.FuseJob { NodeIndices = new List<int> { 0 }, Name = "Fused_A" },
            new GlbDisconnectedParts.FuseJob { NodeIndices = new List<int> { 1 }, Name = "Fused_B" },   // a second group: the cache is the shared one
        };
        GlbDisconnectedParts.FuseGroups(glb, jobs, 0, out List<GlbDisconnectedParts.Result> results);
        Assert.Equal(0, results[0].HiddenFacesRemoved); Assert.Equal(16, results[0].OutputTriangles);
        Assert.Contains("kept: 4 whose pair shows on both sides", Assert.Single(results[0].Details, d => d.StartsWith("hidden twins:")));
    }

    // GLASS IS GLASS IN ANY NODE (review of the first cut): a canopy that is the BLEND primitive of a node which also has an
    // opaque frame, 50 units away. Only nodes made wholly of glass were left out of the file's tree then, so the canopy
    // stopped rays and the panel seen through it lost a side.
    [Fact]
    public void Glass_that_shares_a_node_with_opaque_parts_still_stops_nothing()
    {
        var s = new Soup();
        s.Quad("panel:a", V(0, 0, 4), V(0, 0, 0), V(0, 4, 0), V(0, 4, 4)); s.Quad("panel:b", V(0, 0, 0), V(0, 0, 4), V(0, 4, 4), V(0, 4, 0));
        s.Box("canopy", V(0.01, -1, -1), V(6, 6, 5)); s.Box("frame", V(50, 0, 0), V(51, 1, 1));
        Part cabin = PartOf("Cabin", 1, s, t => t.StartsWith("canopy")); cabin.Second = PartOf("Cabin", 0, s, t => t.StartsWith("frame"));
        byte[] glb = BuildGlb(Materials(null, "BLEND"), PartOf("Panel", 0, s, t => t.StartsWith("panel")), cabin);
        var r = GlbDisconnectedParts.FuseNodes(glb, new[] { 0 }, 0, "Fused");
        Assert.Equal(0, r.HiddenFacesRemoved); Assert.Equal(4, r.OutputTriangles);
        Assert.Contains("kept: 4 whose pair shows on both sides", Assert.Single(r.Details, d => d.StartsWith("hidden twins:")));
        // ...and the same canopy of an opaque material does hide the side behind it: the fixture can tell the difference
        byte[] solid = BuildGlb(Materials(null, null), PartOf("Panel", 0, s, t => t.StartsWith("panel")), cabin);
        Assert.Equal(2, GlbDisconnectedParts.FuseNodes(solid, new[] { 0 }, 0, "Fused").HiddenFacesRemoved);
    }

    // a face left out shapes no normal: the house with SHARED corners (one vertex for its walls and its ceiling). With the
    // ceiling gone the top corners' normals are the walls' alone, level; with it they would point down.
    [Fact]
    public void A_face_left_out_shapes_no_normal()
    {
        float[] corners = { 0, 0, 0, 10, 0, 0, 10, 0, 10, 0, 0, 10, 0, 10, 0, 10, 10, 0, 10, 10, 10, 0, 10, 10 };
        int[] quads = { 0, 1, 2, 3,   0, 4, 5, 1,   1, 5, 6, 2,   2, 6, 7, 3,   3, 7, 4, 0,   4, 5, 6, 7 };   // bottom, four walls (outward), the top wound DOWN
        var idx = new List<int>();
        for (int q = 0; q < quads.Length; q += 4) idx.AddRange(new[] { quads[q], quads[q + 1], quads[q + 2], quads[q], quads[q + 2], quads[q + 3] });
        var house = new Part { Name = "House", Positions = corners, Indices = idx.ToArray(), Material = 0 };
        var deck = new Soup().Quad("deck", V(0, 10, 10), V(10, 10, 10), V(10, 10, 0), V(0, 10, 0), otherDiagonal: true);
        byte[] glb = BuildGlb(Materials(null, null), house, PartOf("Deck", 1, deck, t => true));
        var r = GlbDisconnectedParts.FuseNodes(glb, new[] { 0, 1 }, 0, "Fused");
        Assert.Equal(2, r.HiddenFacesRemoved); Assert.Equal(12, r.OutputTriangles);
        var normals = FusedNormals(r.Bytes, r.FusedNodeName, 0);
        var top = normals.Where(v => Math.Abs(v.y - 10) < 1e-6).ToList();
        Assert.NotEmpty(top);
        Assert.All(top, v => Assert.Equal(0.0, v.ny, 6));
    }

    // JUDGED ON THE FINAL WINDING: a house stored inside out with a deck on it. The fuse turns the house, top included, so
    // the top and the deck end up wound the SAME way - a different defect, left alone. Judged as stored, the top would
    // have been the deck's twin and gone.
    [Fact]
    public void Twins_are_judged_after_the_winding_passes()
    {
        var s = new Soup().Box("house", V(0, 0, 0), V(10, 10, 10), inward: true);
        s.Quad("deck", V(0, 10, 10), V(10, 10, 10), V(10, 10, 0), V(0, 10, 0), otherDiagonal: true);
        byte[] glb = BuildGlb(Materials(null, null), PartOf("House", 0, s, t => t.StartsWith("house")), PartOf("Deck", 1, s, t => t == "deck"));
        var r = GlbDisconnectedParts.FuseNodes(glb, new[] { 0, 1 }, 0, "Fused");
        Assert.Equal(12, r.FacesRewound); Assert.Equal(0, r.HiddenFacesRemoved); Assert.Equal(14, r.OutputTriangles);
        var tris = FusedTriangles(r.Bytes, r.FusedNodeName);
        Assert.Equal(2, tris[0].Count(t => Math.Abs(t.height - 10) < 1e-6 && t.normalY > 0.99));   // the house's top, now up
    }

    // THE ROUNDING IS THE GROUP'S OWN: a pair ten roundings apart (0.00002 on a group two units long) is not a pair of
    // twins, although one side of it is against a wall and the other in the open
    [Fact]
    public void Faces_ten_roundings_apart_are_not_twins_in_a_fuse()
    {
        var s = new Soup();
        s.Quad("pair:a", V(24, 4, 5), V(26, 4, 5), V(26, 6, 5), V(24, 6, 5));                                   // faces +Z, at the wall
        s.Quad("pair:b", V(24, 6, 5 - 2e-5), V(26, 6, 5 - 2e-5), V(26, 4, 5 - 2e-5), V(24, 4, 5 - 2e-5));       // faces -Z, open
        s.Box("wall", V(-50, -50, 5.5), V(100, 200, 8));   // far wider and taller than any camera looks round
        byte[] glb = BuildGlb(Materials(null, null), PartOf("Pair", 0, s, t => t.StartsWith("pair")), PartOf("Wall", 1, s, t => t.StartsWith("wall")));
        var r = GlbDisconnectedParts.FuseNodes(glb, new[] { 0 }, 0, "Fused");
        Assert.Equal(0, r.HiddenFacesRemoved); Assert.Equal(4, r.OutputTriangles);
        Assert.DoesNotContain(r.Details, d => d.StartsWith("hidden twins:"));
        // ...and coincident, the side at the wall goes: the fixture can tell
        var t2 = new Soup();
        t2.Quad("pair:a", V(24, 4, 5), V(26, 4, 5), V(26, 6, 5), V(24, 6, 5));
        t2.Quad("pair:b", V(24, 6, 5), V(26, 6, 5), V(26, 4, 5), V(24, 4, 5));
        byte[] glb2 = BuildGlb(Materials(null, null), PartOf("Pair", 0, t2, t => true), PartOf("Wall", 1, s, t => t.StartsWith("wall")));
        Assert.Equal(2, GlbDisconnectedParts.FuseNodes(glb2, new[] { 0 }, 0, "Fused").HiddenFacesRemoved);
    }

    // AT WELD 0 ONLY: the same house and deck fused with a weld of one percent. The pass is not run, and the report says so.
    [Fact]
    public void A_non_zero_weld_leaves_hidden_twins_alone_and_says_so()
    {
        Soup s = HouseWithDeck();
        byte[] glb = BuildGlb(Materials(null, null), PartOf("House", 0, s, t => t.StartsWith("house")), PartOf("Deck", 1, s, t => t == "deck"));
        var r = GlbDisconnectedParts.FuseNodes(glb, new[] { 0, 1 }, 0.01, "Fused");
        Assert.Equal(14, r.SourceTriangles); Assert.Equal(14, r.OutputTriangles); Assert.Equal(0, r.HiddenFacesRemoved);
        Assert.StartsWith("hidden twins: not looked for - the weld is not 0", Assert.Single(r.Details, d => d.StartsWith("hidden twins:")));
        Assert.EndsWith("face(s) smaller than the weld kept collapsed", r.Details[0]);
    }

    // the report counts what is WRITTEN: with two faces of fourteen left out, "of 12" and "House 0/10"
    [Fact]
    public void The_report_counts_the_faces_written()
    {
        Soup s = HouseWithDeck();
        byte[] glb = BuildGlb(Materials(null, null), PartOf("House", 0, s, t => t.StartsWith("house")), PartOf("Deck", 1, s, t => t == "deck"));
        var r = GlbDisconnectedParts.FuseNodes(glb, new[] { 0, 1 }, 0, "Fused");
        Assert.Contains("0 of 12 face(s) rewound", r.Details[0]);
        Assert.Contains("rewound by part: House 0/10; Deck 0/2", Assert.Single(r.Details, d => d.StartsWith("rewound by part:")));
        Assert.Contains("this pass runs last", Assert.Single(r.Details, d => d.StartsWith("hidden twins:")));
    }

    // A MIRRORED NODE IS READ AS IT RENDERS: the case around the pair is stored at x = -30..-20 under a node scaled -1 in
    // X, so it stands at x = 20..30 with its triangles wound the other way in world space, and glTF draws them reversed -
    // outward again. It hides the pair. (Read as stored it would be a box seen from behind on every side: in no one's way.)
    [Fact]
    public void A_mirrored_node_stops_rays_as_it_renders()
    {
        var s = new Soup();
        s.Quad("inner:a", V(24, 4, 5), V(26, 4, 5), V(26, 6, 5), V(24, 6, 5));
        s.Quad("inner:b", V(24, 6, 5), V(26, 6, 5), V(26, 4, 5), V(24, 4, 5));
        var stored = new Soup().Box("case", V(-30, 0, 0), V(-20, 10, 10));
        Part mirrored = PartOf("Case", 1, stored, t => true); mirrored.Scale = new[] { -1.0, 1.0, 1.0 };
        byte[] glb = BuildGlb(Materials(null, null), PartOf("Inner", 0, s, t => true), mirrored);
        var r = GlbDisconnectedParts.FuseNodes(glb, new[] { 0 }, 0, "Fused");
        Assert.Equal(0, r.HiddenFacesRemoved);
        Assert.Contains("4 buried on both sides", Assert.Single(r.Details, d => d.StartsWith("hidden twins:")));
        // ...and the same box stored inside out under the same node renders inside out: seen from behind, it hides nothing
        var inside = new Soup().Box("case", V(-30, 0, 0), V(-20, 10, 10), inward: true);
        Part wrong = PartOf("Case", 1, inside, t => true); wrong.Scale = new[] { -1.0, 1.0, 1.0 };
        byte[] glb2 = BuildGlb(Materials(null, null), PartOf("Inner", 0, s, t => true), wrong);
        Assert.Contains("kept: 4 whose pair shows on both sides", Assert.Single(GlbDisconnectedParts.FuseNodes(glb2, new[] { 0 }, 0, "Fused").Details, d => d.StartsWith("hidden twins:")));
    }

    // the fuse reads BLEND from the file: a pair in a glass case is seen, a pair in a steel one is not - neither is touched,
    // and the house's ceiling beside them goes either way
    [Theory]
    [InlineData("BLEND", "show on both sides")]
    [InlineData(null, "buried on both sides")]
    public void The_fuse_reads_glass_from_the_materials(string caseAlpha, string expected)
    {
        var s = new Soup().Box("case", V(20, 0, 0), V(30, 10, 10));
        s.Quad("inner:a", V(24, 4, 5), V(26, 4, 5), V(26, 6, 5), V(24, 6, 5));
        s.Quad("inner:b", V(24, 6, 5), V(26, 6, 5), V(26, 4, 5), V(24, 4, 5));
        byte[] glb = BuildGlb(Materials(null, caseAlpha), PartOf("Inner", 0, s, t => t.StartsWith("inner")), PartOf("Case", 1, s, t => t.StartsWith("case")));
        var r = GlbDisconnectedParts.FuseNodes(glb, new[] { 0 }, 0, "Fused");   // the case is ANOTHER node of the file
        Assert.Equal(0, r.HiddenFacesRemoved); Assert.Equal(4, r.OutputTriangles);
        string line = Assert.Single(r.Details, d => d.StartsWith("hidden twins:"));
        Assert.Contains(expected == "show on both sides" ? "kept: 4 whose pair shows on both sides" : "4 buried on both sides", line);
    }
}
