using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

/// <summary>A mesh as Blender's glTF exporter lays it out (BlenderExport, VehicleProbe.BlenderCornerNormals): each rule on a
/// case where the rule alone decides the result. The rounding, the normalizing and the colour quantizing are held to values
/// numpy 2.3.4 (Blender 5.1.2's own) gave for the same inputs on 2026-10-05; tools/prep_drill.sh holds whole meshes to the
/// files Blender's prep_model.py wrote.</summary>
public class BlenderExportTests
{
    static float F(uint bits) => BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
    static uint U(float f) => BitConverter.ToUInt32(BitConverter.GetBytes(f), 0);

    /// <summary>A reduced mesh as BlenderReduce hands it over: Blender's frame, every face on slot 0 and flat unless told.</summary>
    static BlenderReduce.Result Mesh(float[] positions, int[] faces, int[] faceMaterial = null, bool sharp = true)
    {
        int nf = faces.Length / 3;
        var r = new BlenderReduce.Result { VertexCount = positions.Length / 3, FaceCount = nf, Positions = positions, Faces = faces, FaceMaterial = faceMaterial ?? new int[nf], FaceSharp = Enumerable.Repeat(sharp, nf).ToArray() };
        r.Slots.Add((0, false)); r.SlotAlpha.Add(("OPAQUE", 0.5f, 1.0));
        return r;
    }

    // a flat square in Blender's XY plane, two triangles around the diagonal 0-2
    static readonly float[] Square = { 0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0 };
    static readonly int[] SquareFaces = { 0, 1, 2, 0, 2, 3 };

    [Theory]
    [InlineData(0x3983126Fu, 0x3951B717u)]   // 0.00025 -> 2.5 -> 2 (ties to EVEN; away from zero would give 0.0003)
    [InlineData(0x39B78034u, 0x399D4952u)]   // 0.00035 (a little under in float32) -> 3
    [InlineData(0x3DFCD35Bu, 0x3DFCB924u)]   // 0.12345 -> 0.1234 (the product rounds to 1234.5 exactly, ties to even)
    [InlineData(0xBF7CD6A1u, 0xBF7CD35Bu)]   // -0.98765 -> -0.9876
    [InlineData(0x3F7FFCB9u, 0x3F800000u)]   // 0.99995 -> 1
    [InlineData(0x3827C5ACu, 0x00000000u)]   // 0.00004 -> 0
    [InlineData(0xB851B717u, 0x80000000u)]   // -0.00005 -> -0.0, the sign kept
    [InlineData(0x3EAAAAABu, 0x3EAAA64Cu)]   // a third -> 0.3333
    public void Round4_is_numpys_around_on_a_float32(uint input, uint expected)
    {
        Assert.Equal(expected, U(BlenderExport.Round4(F(input))));
    }

    [Theory]
    [InlineData(0x3F19999Au, 0x3EF5C28Fu, 0x3F23D70Au, 0x3F19999Au, 0x3EF5C28Fu, 0x3F23D70Au)]   // (0.6, 0.48, 0.64): already unit at 4 decimals
    [InlineData(0x3DFCD680u, 0xBF278195u, 0x3F3EF9DBu, 0x3DFCF18Fu, 0xBF2782D9u, 0x3F3EFCDEu)]   // rounded, then renormalized in float32
    [InlineData(0x3827C5ACu, 0xB7FBA882u, 0x37A7C5ACu, 0x00000000u, 0x80000000u, 0x3F800000u)]   // rounds to zero: made up (0, 0, 1), the -0.0 left
    [InlineData(0x3E99999Au, 0x3E99999Au, 0x3E99999Au, 0x3F13CD3Bu, 0x3F13CD3Bu, 0x3F13CD3Bu)]   // (0.3, 0.3, 0.3): not unit, normalized
    [InlineData(0xBF3504F3u, 0x00000000u, 0x3F3504F3u, 0xBF3504F4u, 0x00000000u, 0x3F3504F4u)]   // 0.7071 renormalized lands one ulp ABOVE the unrounded
    public void A_normal_is_rounded_to_four_decimals_renormalized_and_a_zero_made_up(uint x, uint y, uint z, uint ex, uint ey, uint ez)
    {
        float a = F(x), b = F(y), c = F(z);
        BlenderExport.ExportedNormal(ref a, ref b, ref c);
        Assert.Equal(new[] { ex, ey, ez }, new[] { U(a), U(b), U(c) });
    }

    [Fact]
    public void The_vertices_are_the_unique_corners_sorted_as_raw_words_so_a_negative_normal_follows_a_positive_one()
    {
        // vertex 0 is in two flat faces: B (listed FIRST) with the normal -X, A with +Z. Its two dots differ in the normal's
        // first word: A's 0.0 (0x00000000) sorts before B's -1.0 (0xBF800000) - by value, and by face order, B would lead
        var r = Mesh(new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 0 }, new[] { 0, 3, 4, 0, 1, 2 });
        var p = Assert.Single(BlenderExport.MeshPrimitives(r));
        Assert.Equal(new[] { 0, 0, 1, 2, 3, 4 }, p.SourceVertex);
        Assert.Equal(new[] { 1, 4, 5, 0, 2, 3 }, p.Indices);
        // Y up: +Z becomes (0, 1, 0) and -X stays (-1, 0, 0); every -0.0 the turn made is 0.0
        Assert.Equal(new[] { U(0f), U(1f), U(0f), U(-1f), U(0f), U(0f) }, p.Normals.Take(6).Select(U).ToArray());
        Assert.All(p.Normals, n => Assert.NotEqual(0x80000000u, U(n)));
        // the position is the vertex's, (x, z, -y): vertex 3 at Blender's (0, 0, 1) is glTF's (0, 1, 0)
        Assert.True(p.Positions[12] == 0f && p.Positions[13] == 1f && p.Positions[14] == 0f);
    }

    [Fact]
    public void Corners_that_agree_share_a_vertex_and_a_negative_zero_is_a_zero()
    {
        // a smooth square (vertex normals: all +Z). Vertex 0's two corners carry u = -0.0 and u = 0.0: one vertex.
        var r = Mesh(Square, SquareFaces, sharp: false);
        r.Uv.Add(new float[] { -0f, 1f, 1f, 1f, 1f, 0f, /* face 2 */ 0f, 1f, 1f, 0f, 0f, 0f });
        var p = Assert.Single(BlenderExport.MeshPrimitives(r));
        Assert.Equal(4, p.VertexCount);
        Assert.Equal(new[] { 0, 1, 2, 0, 2, 3 }, p.Indices);
        // glTF's v is 1 - v: (-0, 1) -> (0, 0), (1, 1) -> (1, 0), (1, 0) -> (1, 1), (0, 0) -> (0, 1)
        Assert.Equal(new[] { U(0f), U(0f), U(1f), U(0f), U(1f), U(1f), U(0f), U(1f) }, p.Uv[0].Select(U).ToArray());
    }

    [Fact]
    public void A_vertex_with_two_uvs_is_two_vertices_ordered_by_the_uvs_bits()
    {
        // vertex 2: v = 0 in the first face (exported 1.0, 0x3F800000), v = 0.5 in the second (exported 0.5, 0x3F000000)
        var r = Mesh(Square, SquareFaces, sharp: false);
        r.Uv.Add(new float[] { 0f, 1f, 1f, 1f, 1f, 0f, /* face 2 */ 0f, 1f, 1f, 0.5f, 0f, 0f });
        var p = Assert.Single(BlenderExport.MeshPrimitives(r));
        Assert.Equal(new[] { 0, 1, 2, 2, 3 }, p.SourceVertex);
        Assert.Equal(new[] { 0, 1, 3, 0, 2, 4 }, p.Indices);
        Assert.Equal(0.5f, p.Uv[0][2 * 2 + 1]); Assert.Equal(1f, p.Uv[0][2 * 3 + 1]);
    }

    [Fact]
    public void One_primitive_per_material_slot_in_use_ascending_each_with_its_own_vertices()
    {
        // the first face is on slot 2, the second on slot 0; slot 1 has no face
        var r = Mesh(Square, SquareFaces, new[] { 2, 0 }, sharp: false);
        r.Slots.Add((1, false)); r.Slots.Add((2, false)); r.SlotAlpha.Add(("OPAQUE", 0.5f, 1.0)); r.SlotAlpha.Add(("OPAQUE", 0.5f, 1.0));
        var prims = BlenderExport.MeshPrimitives(r);
        Assert.Equal(new[] { 0, 2 }, prims.Select(p => p.MaterialSlot).ToArray());
        Assert.Equal(new[] { 0, 2, 3 }, prims[0].SourceVertex); Assert.Equal(new[] { 0, 1, 2 }, prims[0].Indices);
        Assert.Equal(new[] { 0, 1, 2 }, prims[1].SourceVertex); Assert.Equal(new[] { 0, 1, 2 }, prims[1].Indices);
    }

    [Fact]
    public void Validate_removes_the_later_face_on_the_same_three_vertices_and_a_face_that_repeats_a_vertex_with_their_corners()
    {
        // face 0 a triangle; face 1 repeats a vertex; face 2 is face 0 wound the other way (a twin); face 3 stays
        var r = Mesh(new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 0 }, new[] { 0, 1, 2, 1, 1, 3, 2, 1, 0, 1, 3, 2 }, new[] { 0, 1, 2, 1 }, sharp: false);
        r.Slots.Add((1, false)); r.Slots.Add((2, false)); r.SlotAlpha.Add(("OPAQUE", 0.5f, 1.0)); r.SlotAlpha.Add(("OPAQUE", 0.5f, 1.0));
        r.FaceSharp = new[] { false, true, true, true };
        r.Uv.Add(Enumerable.Range(0, 24).Select(i => (float)i).ToArray());
        r.CustomNormal = Enumerable.Range(0, 24).Select(i => (short)(100 + i)).ToArray();
        r.Colors.Add(("Corner", false, Enumerable.Range(0, 48).Select(i => (byte)i).ToArray()));
        r.Colors.Add(("Point", true, Enumerable.Range(0, 16).Select(i => (byte)(200 + i)).ToArray()));
        var v = BlenderExport.Validated(r);
        Assert.Equal(2, v.FaceCount);
        Assert.Equal(new[] { 0, 1, 2, 1, 3, 2 }, v.Faces);                        // the EARLIER twin stays, in its own winding
        Assert.Equal(new[] { 0, 1 }, v.FaceMaterial); Assert.Equal(new[] { false, true }, v.FaceSharp);
        Assert.Equal(new float[] { 0, 1, 2, 3, 4, 5, 18, 19, 20, 21, 22, 23 }, v.Uv[0]);                    // 2 per corner: faces 0 and 3
        Assert.Equal(new short[] { 100, 101, 102, 103, 104, 105, 118, 119, 120, 121, 122, 123 }, v.CustomNormal);
        Assert.Equal(Enumerable.Range(0, 12).Concat(Enumerable.Range(36, 12)).Select(i => (byte)i).ToArray(), v.Colors[0].bytes);   // 4 per corner
        Assert.Same(r.Colors[1].bytes, v.Colors[1].bytes);                         // a layer on the vertices: the vertices stay
        Assert.Same(r.Positions, v.Positions); Assert.Equal(4, v.VertexCount);
        // nothing to remove: the same object
        var clean = Mesh(Square, SquareFaces);
        Assert.Same(clean, BlenderExport.Validated(clean));
    }

    [Fact]
    public void The_layout_is_of_the_validated_mesh_so_a_twin_neither_adds_a_triangle_nor_bends_the_normal_of_the_face_that_stays()
    {
        // a smooth square whose first face has a twin wound the other way: vertex normals would cancel around it
        var r = Mesh(Square, new[] { 0, 1, 2, 0, 2, 3, 2, 1, 0 }, sharp: false);
        var p = Assert.Single(BlenderExport.MeshPrimitives(r));
        Assert.Equal(new[] { 0, 1, 2, 0, 2, 3 }, p.Indices);
        for (int i = 0; i < 4; i++) Assert.Equal(new[] { U(0f), U(1f), U(0f) }, new[] { U(p.Normals[3 * i]), U(p.Normals[3 * i + 1]), U(p.Normals[3 * i + 2]) });
        // a slot whose only face was the twin has no primitive, and no say in the colour sets: the empty slot 0 would have
        // decided "with alpha" ahead of the coloured slot 1
        var c = Mesh(Square, new[] { 0, 1, 2, 0, 2, 3, 2, 1, 0 }, new[] { 1, 1, 0 }, sharp: false);
        c.Slots[0] = (-1, false); c.Slots.Add((0, true)); c.SlotAlpha.Add(("OPAQUE", 0.5f, 1.0));
        c.Colors.Add(("Color", false, new byte[36]));
        var pc = Assert.Single(BlenderExport.MeshPrimitives(c));
        Assert.Equal(1, pc.MaterialSlot); Assert.False(Assert.Single(pc.Colors).Alpha);
        // nor is it the deciding material: a coloured BLEND material whose only face is the twin would have given alpha
        c.Slots[0] = (1, true); c.SlotAlpha[0] = ("BLEND", 0.5f, 1.0);
        Assert.False(Assert.Single(Assert.Single(BlenderExport.MeshPrimitives(c)).Colors).Alpha);
    }

    [Theory]
    [InlineData("BLEND", 0.5, true)]
    [InlineData("MASK", 0.5, true)]
    [InlineData("MASK", 0.0, false)]
    public void Removing_a_twin_preserves_the_retained_materials_alpha(string mode, double cutoff, bool alpha)
    {
        var r = Mesh(Square, new[] { 0, 1, 2, 0, 2, 3, 2, 1, 0 });
        r.Slots[0] = (0, true); r.SlotAlpha[0] = (mode, cutoff, 1.0);
        r.Colors.Add(("Color", false, Enumerable.Repeat((byte)100, 36).ToArray()));
        Assert.Same(r.SlotAlpha, BlenderExport.Validated(r).SlotAlpha);   // review of PR #128: Validated() rebuilt the Result without SlotAlpha
        var p = Assert.Single(BlenderExport.MeshPrimitives(r));
        Assert.Equal(6, p.Indices.Length);
        Assert.Equal(alpha, Assert.Single(p.Colors).Alpha);
    }

    // one colour per corner of the square: the corners of vertex 0 differ in red (10 and 200), the rest agree per vertex
    static byte[] CornerColors() => new byte[]
    {
        200, 20, 30, 255,   0, 128, 255, 38,   64, 64, 64, 128,
        10, 20, 30, 255,    64, 64, 64, 128,   255, 0, 0, 0,
    };

    [Fact]
    public void A_material_built_with_the_vertex_colour_writes_the_first_layer_as_linear_rgb_floats()
    {
        var r = Mesh(Square, SquareFaces, sharp: false);
        r.Slots[0] = (0, true);
        r.Colors.Add(("Color", false, CornerColors()));
        var p = Assert.Single(BlenderExport.MeshPrimitives(r));
        var set = Assert.Single(p.Colors);
        Assert.False(set.Alpha); Assert.False(set.Forced); Assert.Null(set.Shorts);
        // vertex 0 splits by its colour, the red of 10 before the red of 200 (the linear floats order as the bytes do)
        Assert.Equal(new[] { 0, 0, 1, 2, 3 }, p.SourceVertex);
        Assert.Equal(new[] { 1, 2, 3, 0, 3, 4 }, p.Indices);
        Assert.Equal(new[] { 10, 20, 30, 200, 20, 30, 0, 128, 255 }.Select(b => U(BlenderColor.SrgbByteToLinear((byte)b))).ToArray(), set.Data.Take(9).Select(U).ToArray());
    }

    [Fact]
    public void A_face_without_material_ahead_writes_the_layer_with_alpha_as_normalized_shorts()
    {
        var r = Mesh(Square, SquareFaces, sharp: false);
        r.Slots[0] = (-1, false);
        r.Colors.Add(("Color", false, CornerColors()));
        var p = Assert.Single(BlenderExport.MeshPrimitives(r));
        var set = Assert.Single(p.Colors);
        Assert.True(set.Alpha); Assert.False(set.Forced);
        // the alpha is byte * (1 / 255) in float32; the short is clip, * 65535, + 0.5, truncated (numpy: 38 -> 9766,
        // 128 -> 32896, 255 -> 65535, 0 -> 0). Vertex order: 0 (red 10), 0 (red 200), 1, 2, 3
        Assert.Equal(new uint[] { 0x3F800000, 0x3F800000, 0x3E189899, 0x3F008081, 0x00000000 }, Enumerable.Range(0, 5).Select(i => U(set.Data[4 * i + 3])).ToArray());
        Assert.Equal(new ushort[] { 65535, 65535, 9766, 32896, 0 }, Enumerable.Range(0, 5).Select(i => set.Shorts[4 * i + 3]).ToArray());
        // the colours' shorts: sRGB 255 is 1.0 -> 65535, 0 -> 0, and a middle value by the same rule
        Assert.Equal((ushort)65535, set.Shorts[4 * 4]); Assert.Equal((ushort)0, set.Shorts[4 * 4 + 1]);
        Assert.Equal((ushort)(float)((float)(BlenderColor.SrgbByteToLinear(64) * 65535f) + 0.5f), set.Shorts[4 * 3]);
    }

    [Fact]
    public void Materials_without_the_vertex_colour_force_a_color_0_of_255s_and_the_layer_follows_with_alpha()
    {
        var r = Mesh(Square, SquareFaces, sharp: false);   // slot 0: a material built WITHOUT the vertex colour
        r.Colors.Add(("Color", false, CornerColors()));
        var p = Assert.Single(BlenderExport.MeshPrimitives(r));
        Assert.Equal(2, p.Colors.Count);
        Assert.True(p.Colors[0].Forced); Assert.Null(p.Colors[0].Data);
        Assert.True(p.Colors[1].Alpha); Assert.False(p.Colors[1].Forced);
        Assert.Equal(5, p.VertexCount);   // the layer still splits vertex 0
        Assert.Equal(U(BlenderColor.SrgbByteToLinear(10)), U(p.Colors[1].Data[0]));
    }

    [Fact]
    public void The_first_slot_in_use_that_decides_wins_and_every_other_layer_follows_with_alpha()
    {
        byte[] second = Enumerable.Repeat((byte)77, 24).ToArray();
        // (a) slot 0 plain material, slot 1 empty, slot 2 coloured - faces on 0 and 1: the EMPTY slot decides (alpha)
        var a = Mesh(Square, SquareFaces, new[] { 0, 1 }, sharp: false);
        a.Slots.Add((-1, false)); a.Slots.Add((1, true)); a.SlotAlpha.Add(("OPAQUE", 0.5f, 1.0)); a.SlotAlpha.Add(("OPAQUE", 0.5f, 1.0));
        a.Colors.Add(("Color", false, CornerColors())); a.Colors.Add(("Color.001", false, second));
        var pa = BlenderExport.MeshPrimitives(a);
        Assert.Equal(2, pa.Count);
        Assert.All(pa, p => { Assert.Equal(2, p.Colors.Count); Assert.True(p.Colors[0].Alpha && !p.Colors[0].Forced); Assert.True(p.Colors[1].Alpha); });
        // (b) the coloured slot is met first: RGB on EVERY primitive, the empty slot's too; the second layer with alpha
        var b = Mesh(Square, SquareFaces, new[] { 0, 1 }, sharp: false);
        b.Slots[0] = (1, true); b.Slots.Add((-1, false)); b.SlotAlpha.Add(("OPAQUE", 0.5f, 1.0));
        b.Colors.Add(("Color", false, CornerColors())); b.Colors.Add(("Color.001", false, second));
        var pb = BlenderExport.MeshPrimitives(b);
        Assert.All(pb, p => { Assert.Equal(2, p.Colors.Count); Assert.False(p.Colors[0].Alpha); Assert.True(p.Colors[1].Alpha); });
        Assert.Equal(U(BlenderColor.SrgbByteToLinear(77)), U(pb[1].Colors[1].Data[0]));
        // (c) the coloured slot exists but no face is on it: it does not decide - forced, then both layers
        var c = Mesh(Square, SquareFaces, sharp: false);
        c.Slots.Add((1, true)); c.SlotAlpha.Add(("OPAQUE", 0.5f, 1.0));
        c.Colors.Add(("Color", false, CornerColors())); c.Colors.Add(("Color.001", false, second));
        var pc = Assert.Single(BlenderExport.MeshPrimitives(c));
        Assert.Equal(new[] { true, false, false }, pc.Colors.Select(s => s.Forced).ToArray());
        // (d) no colour layer: no colour set, whatever the slots say
        var d = Mesh(Square, SquareFaces, sharp: false); d.Slots[0] = (0, true);
        Assert.Empty(Assert.Single(BlenderExport.MeshPrimitives(d)).Colors);
    }

    [Fact]
    public void A_layer_on_the_point_domain_is_read_by_vertex()
    {
        var r = Mesh(Square, SquareFaces, sharp: false);
        r.Slots[0] = (0, true);
        r.Colors.Add(("Color", true, new byte[] { 10, 0, 0, 255, 20, 0, 0, 255, 30, 0, 0, 255, 40, 0, 0, 255 }));
        var p = Assert.Single(BlenderExport.MeshPrimitives(r));
        Assert.Equal(4, p.VertexCount);
        Assert.Equal(new[] { 10, 20, 30, 40 }.Select(b => U(BlenderColor.SrgbByteToLinear((byte)b))).ToArray(), Enumerable.Range(0, 4).Select(i => U(p.Colors[0].Data[3 * i])).ToArray());
    }

    [Fact]
    public void A_coloured_material_writes_alpha_only_when_the_importer_wired_the_vertex_alpha_into_it()
    {
        // base_color (the importer) DROPS the alpha socket for OPAQUE (or no mode) and for MASK at a cutoff of 0 or over 1;
        // everything else keeps it - a mode the spec does not know and a negative cutoff included (review of PR #128,
        // both measured in Blender: RGBA shorts)
        Assert.False(BlenderExport.VertexAlphaWired("OPAQUE", 0.5f)); Assert.False(BlenderExport.VertexAlphaWired("", 0.5f)); Assert.False(BlenderExport.VertexAlphaWired(null, 0.5f));
        Assert.True(BlenderExport.VertexAlphaWired("BLEND", 0.5f)); Assert.True(BlenderExport.VertexAlphaWired("Blend", 0.5f));
        Assert.True(BlenderExport.VertexAlphaWired("MASK", 0.5f)); Assert.True(BlenderExport.VertexAlphaWired("MASK", 1f)); Assert.True(BlenderExport.VertexAlphaWired("MASK", 1e-6f)); Assert.True(BlenderExport.VertexAlphaWired("MASK", -0.5f));
        Assert.False(BlenderExport.VertexAlphaWired("MASK", 0f)); Assert.False(BlenderExport.VertexAlphaWired("MASK", 1.5f));
        Assert.True(BlenderExport.VertexAlphaWired("MASK", -1e-50)); Assert.False(BlenderExport.VertexAlphaWired("MASK", 1.00000001)); Assert.True(BlenderExport.VertexAlphaWired("MASK", 1e-50));   // the double decides, not its float32
        // a reduced mesh must carry one alpha mode per slot
        var short_ = Mesh(Square, SquareFaces, sharp: false); short_.Slots[0] = (0, true); short_.SlotAlpha.Clear(); short_.Colors.Add(("Color", false, CornerColors()));
        Assert.Throws<InvalidOperationException>(() => BlenderExport.MeshPrimitives(short_));
        // the plan: a BLEND material on the deciding slot gives COLOR_0 with alpha, as normalized shorts, every layer still following
        var r = Mesh(Square, SquareFaces, sharp: false);
        r.Slots[0] = (1, true); r.SlotAlpha[0] = ("BLEND", 0.5f, 1.0);
        r.Colors.Add(("Color", false, CornerColors()));
        var set = Assert.Single(Assert.Single(BlenderExport.MeshPrimitives(r)).Colors);
        Assert.True(set.Alpha); Assert.False(set.Forced); Assert.NotNull(set.Shorts);
        Assert.Equal((ushort)9766, set.Shorts[4 * 2 + 3]);   // vertex 1's alpha byte 38 (order: 0 (red 10), 0 (red 200), 1, 2, 3)
        // the invented material of a coloured primitive without one is OPAQUE: RGB
        r.Slots[0] = (-1, true); r.SlotAlpha[0] = ("BLEND", 0.5f, 1.0);
        Assert.False(Assert.Single(Assert.Single(BlenderExport.MeshPrimitives(r)).Colors).Alpha);
        // a plain material first does not decide; the MASK one behind it does, by its cutoff
        var e = Mesh(Square, SquareFaces, new[] { 0, 1 }, sharp: false);
        e.Slots.Add((1, true)); e.SlotAlpha.Add(("MASK", 0f, 1.0)); e.Colors.Add(("Color", false, CornerColors()));
        Assert.All(BlenderExport.MeshPrimitives(e), p => Assert.False(p.Colors[0].Alpha));
        e.SlotAlpha[1] = ("MASK", 0.5f, 1.0);
        Assert.All(BlenderExport.MeshPrimitives(e), p => Assert.True(p.Colors[0].Alpha));
    }

    [Theory]
    [InlineData("BLEND", 0.99999999, false)]
    [InlineData("BLEND", 1.0, true)]
    [InlineData("BLEND", 0.9999999, true)]
    [InlineData("MASK", 0.99999999, true)]
    public void Only_a_created_factor_node_rounding_to_one_changes_the_colour_format(string mode, double factor, bool alpha)
    {
        var r = Mesh(Square, SquareFaces);
        r.Slots[0] = (0, true); r.SlotAlpha[0] = (mode, 0.5, factor);
        r.Colors.Add(("Color", false, CornerColors()));
        Assert.Equal(alpha, Assert.Single(Assert.Single(BlenderExport.MeshPrimitives(r)).Colors).Alpha);
    }

    // a ridge: two faces over the edge 0-1, one leaning each way, and a third face apart from them
    static readonly float[] Ridge = { 0, 0, 1, 0, 1, 1, 1, 0, 0, -1, 0, 0, 5, 0, 0, 6, 0, 0, 5, 1, 0 };
    static readonly int[] RidgeFaces = { 0, 1, 2, 1, 0, 3, 4, 5, 6 };

    [Fact]
    public void Corner_normals_without_custom_normals_follow_the_meshs_domain()
    {
        var face = VehicleProbe.FaceNormals(Ridge, RidgeFaces);
        var vertex = VehicleProbe.BlenderVertexNormals(Ridge, RidgeFaces, null);
        // every face sharp: the face's normal on its three corners
        var flat = VehicleProbe.BlenderCornerNormals(Ridge, RidgeFaces, new[] { true, true, true }, null, null);
        for (int c = 0; c < 9; c++) for (int k = 0; k < 3; k++) Assert.Equal(U(face[(c / 3) * 3 + k]), U(flat[c * 3 + k]));
        // none sharp: the vertex's normal on each of its corners
        var smooth = VehicleProbe.BlenderCornerNormals(Ridge, RidgeFaces, new[] { false, false, false }, null, null);
        for (int c = 0; c < 9; c++) for (int k = 0; k < 3; k++) Assert.Equal(U(vertex[RidgeFaces[c] * 3 + k]), U(smooth[c * 3 + k]));
        // a mix (only the face apart is sharp): fans. The ridge's two smooth faces share ONE normal at vertex 0 and at
        // vertex 1 - neither face's own - while the sharp face keeps its own on every corner
        var mixed = VehicleProbe.BlenderCornerNormals(Ridge, RidgeFaces, new[] { false, false, true }, null, null);
        float[] At(int c) => new[] { mixed[c * 3], mixed[c * 3 + 1], mixed[c * 3 + 2] };
        Assert.Equal(At(0).Select(U), At(4).Select(U));   // vertex 0: corner 0 of face 0, corner 1 of face 1
        Assert.Equal(At(1).Select(U), At(3).Select(U));   // vertex 1
        Assert.True(Math.Abs(At(0)[0]) < 1e-6f && At(0)[2] < -0.999f, "the ridge's shared normal points along -Z, between the two faces");
        Assert.True(face[0] < -0.5f && face[3] > 0.5f, "the ridge's faces lean apart in X");
        for (int c = 6; c < 9; c++) for (int k = 0; k < 3; k++) Assert.True(Math.Abs(face[6 + k] - mixed[c * 3 + k]) < 1e-6f);
        // the lone corners of the ridge (vertices 2 and 3) are fans of one face: that face's normal
        for (int k = 0; k < 3; k++) { Assert.True(Math.Abs(face[k] - mixed[2 * 3 + k]) < 1e-6f); Assert.True(Math.Abs(face[3 + k] - mixed[5 * 3 + k]) < 1e-6f); }
    }

    [Fact]
    public void A_fan_whose_normal_runs_along_its_own_edge_has_no_custom_normal_space_and_its_corners_are_exported_as_up()
    {
        // Blender's frame. Around vertex 0: a sliver along +X and a quarter in the XZ plane (both with the normal -Y), then
        // 120 degrees in a plane whose normal is (0.661, 0.75, 0). Weighted by angle, -Y cancels and +X is left - along the
        // sliver's outer edge, so corner_fan_space_define gives up and the decode returns the space's zeroed normal.
        // Blender 5.1.2's corner_normals at vertex 0 of this mesh: (0, 0, 0), read 2026-10-05.
        var positions = new float[] { 0, 0, 0, 1, 0, -0.004f, 1, 0, 0, 0, 0, 1, 0.6495f, -0.5727f, -0.5f };
        var faces = new[] { 0, 1, 2, 0, 2, 3, 0, 3, 4 };
        var smooth = new[] { false, false, false };
        var fan = VehicleProbe.BlenderFanNormals(positions, faces, smooth);
        foreach (int c in new[] { 0, 3, 6 }) Assert.True(fan[3 * c] > 0.9999f && Math.Abs(fan[3 * c + 1]) < 0.01f && Math.Abs(fan[3 * c + 2]) < 0.01f, "the fan's own normal is +X");
        var corner = VehicleProbe.BlenderCornerNormals(positions, faces, smooth, new short[9], new short[9]);
        foreach (int c in new[] { 0, 3, 6 }) Assert.Equal(new[] { 0f, 0f, 0f }, new[] { corner[3 * c], corner[3 * c + 1], corner[3 * c + 2] });
        Assert.True(Math.Abs(corner[3 * 1 + 1]) > 0.9f, "a corner of a fan with a space keeps its normal (-Y here)");
        // the exporter makes the zero (0, 0, 1), which is glTF's (0, 1, 0) - not the fan's +X
        var r = Mesh(positions, faces, sharp: false);
        r.CustomNormal = new short[18];
        var p = Assert.Single(BlenderExport.MeshPrimitives(r));
        Assert.Equal(0, p.SourceVertex[0]);
        Assert.Equal(new[] { U(0f), U(1f), U(0f) }, new[] { U(p.Normals[0]), U(p.Normals[1]), U(p.Normals[2]) });
    }

    // An armature's matrix_world in Blender 5.1.2 (rows of the upper 3x4, read 2026-10-06 off a rig like the export_skin
    // fixture's, before its rotation was made a unit quaternion - a matrix and what Blender made of it either way): a node
    // turned by a quaternion that is not unit, scaled (1.5, 0.75, 1.25), moved to (2, 0.5, -1) in glTF's frame
    static readonly uint[] ArmatureRows =
    {
        0x3F25438A, 0xBF8F29CC, 0xBDB6CD26, 0x40000000,
        0x3F91F3FE, 0x3F09B849, 0xBEBAE878, 0x3F800000,
        0x3F3AE878, 0x3E1855A0, 0x3F26283E, 0x3F000000,
    };

    static float[] ArmatureWorld()   // column-major, as VehicleProbe holds Blender's matrices
    {
        var m = new float[16]; m[15] = 1f;
        for (int row = 0; row < 3; row++) for (int col = 0; col < 4; col++) m[col * 4 + row] = F(ArmatureRows[row * 4 + col]);
        return m;
    }

    [Fact]
    public void A_skinned_meshs_positions_go_through_the_objects_matrix_in_float32()
    {
        // the fixture's first four rest positions (v.co) and what the exporter's np.matmul made of them, read off Blender:
        // numpy sees the mathutils matrix as float32, so every product and sum rounds to float32
        var co = new[] { 0x00000000u, 0x00000000u, 0x3DCCCCCDu, 0x3F400000u, 0x00000000u, 0x3EB00A2Eu, 0x3FBFFCB9u, 0x00000000u, 0x3F0D6B7Cu, 0x3FDFF8CCu, 0x00000000u, 0x3F196578u };
        var expected = new[] { 0x3FFEDB85u, 0x3F76A794u, 0x3F109DA0u, 0x401D05D9u, 0x3FDD6616u, 0x3FA2A7BEu, 0x403AD067u, 0x40208D10u, 0x3FFA10ECu, 0x4044DEF0u, 0x4031B182u, 0x400AA70Eu };
        Assert.Equal(expected, BlenderExport.SkinnedPositions(co.Select(F).ToArray(), ArmatureWorld()).Select(U).ToArray());
    }

    [Fact]
    public void A_skinned_meshs_normals_go_through_the_armatures_3x3_times_the_inverse_transpose_of_what_is_almost_identity()
    {
        // a mesh that hangs from its armature has the armature's matrix_world; armature^-1 @ object is the identity plus
        // float noise, and its inverse transpose leaves the armature's 3x3 an ulp or two off in three entries (Blender's
        // own normal_transform for the fixture)
        var nt = VehicleProbe.ExporterNormalTransform(ArmatureWorld(), ArmatureWorld());
        Assert.Equal(new[] { 0x3F25438Au, 0xBF8F29CDu, 0xBDB6CD28u, 0x3F91F3FEu, 0x3F09B849u, 0xBEBAE878u, 0x3F3AE878u, 0x3E1855A0u, 0x3F26283Du }, nt.Select(U).ToArray());
        // four normals through round, normalize, the transform, normalize - numpy's results in Blender for the same inputs
        var input = new[] { 0.6f, 0.48f, 0.64f, 0f, 0f, 1f, -0.7071f, 0.7071f, 0f, 0.1234f, -0.6543f, 0.7461f };
        var expected = new[] { 0xBE32D159u, 0x3F19525Fu, 0x3F48132Bu, 0xBDF3BC37u, 0xBEF935F7u, 0x3F5D8AFDu, 0xBF6747E8u, 0xBE9DEEAAu, 0xBE9870AFu, 0x3F3D2712u, 0xBEF5A2A7u, 0x3EF243EAu };
        for (int i = 0; i < 4; i++)
        {
            float x = input[3 * i], y = input[3 * i + 1], z = input[3 * i + 2];
            BlenderExport.ExportedNormal(ref x, ref y, ref z, nt);
            Assert.Equal(new[] { expected[3 * i], expected[3 * i + 1], expected[3 * i + 2] }, new[] { U(x), U(y), U(z) });
        }
    }

    static BlenderReduce.Result Weighted(params (int group, float weight)[][] perVertex)
    {
        var r = Mesh(new float[3 * perVertex.Length], new int[0]);
        r.DefNr = perVertex.Select(v => v.Select(g => g.group).ToList()).ToArray();
        r.DefWeight = perVertex.Select(v => v.Select(g => g.weight).ToList()).ToArray();
        return r;
    }

    [Fact]
    public void A_vertexs_joints_are_its_groups_over_the_threshold_by_weight_the_first_four_kept_and_divided_by_their_sum()
    {
        // groups 0..5 are joints 10..15, group 6 names no joint
        var skin = new BlenderExport.Skin { GroupJoint = new[] { 10, 11, 12, 13, 14, 15, -1 }, JointCount = 20 };
        var r = Weighted(
            new[] { (0, 0.25f), (1, 0.5f), (2, 0.125f) },                                        // 0: by weight, descending
            new[] { (3, 0.5f), (1, 0.5f), (0, 0.5f) },                                           // 1: equal weights keep the vertex's own order (a stable sort)
            new[] { (0, 0.0001f), (1, 0.00011f), (2, 0.5f) },                                    // 2: float32 0.0001 is not over 0.0001, 0.00011 is
            new[] { (0, 0.1f), (1, 0.2f), (2, 0.3f), (3, 0.4f), (4, 0.5f), (5, 0.6f) },          // 3: six groups, the four heaviest
            new[] { (6, 1f), (0, 0.00005f) },                                                    // 4: a group without a joint, a weight under the threshold: no bone
            new (int, float)[0],                                                                 // 5: no group at all
            new[] { (0, 1.5f), (1, -0.5f), (2, float.NaN) });                                    // 6: validate clamps to 0..1 and zeroes what is not finite
        BlenderExport.VertexBones(r, skin, out var joints, out var weights, out bool neutral);
        ushort[] J(int v) => joints.Skip(4 * v).Take(4).ToArray(); float[] Wt(int v) => weights.Skip(4 * v).Take(4).ToArray();
        Assert.Equal(new ushort[] { 11, 10, 12, 0 }, J(0));
        float s0 = (float)((float)((float)(0.5f + 0.25f) + 0.125f) + 0f);
        Assert.Equal(new[] { (float)(0.5f / s0), (float)(0.25f / s0), (float)(0.125f / s0), 0f }, Wt(0));
        Assert.Equal(new ushort[] { 13, 11, 10, 0 }, J(1));
        Assert.Equal(new ushort[] { 12, 11, 0, 0 }, J(2));
        float s2 = (float)(0.5f + 0.00011f);
        Assert.Equal(new[] { U((float)(0.5f / s2)), U((float)(0.00011f / s2)), U(0f), U(0f) }, Wt(2).Select(U).ToArray());
        Assert.Equal(new ushort[] { 15, 14, 13, 12 }, J(3));
        float s3 = (float)((float)((float)(0.6f + 0.5f) + 0.4f) + 0.3f);
        Assert.Equal(new[] { U((float)(0.6f / s3)), U((float)(0.5f / s3)), U((float)(0.4f / s3)), U((float)(0.3f / s3)) }, Wt(3).Select(U).ToArray());
        // no bone: the joint the exporter adds after the armature's own, at full weight
        Assert.Equal(new ushort[] { 20, 0, 0, 0 }, J(4)); Assert.Equal(new[] { 1f, 0f, 0f, 0f }, Wt(4));
        Assert.Equal(new ushort[] { 20, 0, 0, 0 }, J(5));
        Assert.True(neutral);
        Assert.Equal(new ushort[] { 10, 0, 0, 0 }, J(6)); Assert.Equal(new[] { 1f, 0f, 0f, 0f }, Wt(6));
        // every vertex with a bone: no neutral bone
        BlenderExport.VertexBones(Weighted(new[] { (0, 1f) }), skin, out _, out _, out neutral);
        Assert.False(neutral);
    }

    [Fact]
    public void A_skinned_primitive_carries_each_exported_vertexs_joints_and_weights()
    {
        var r = Mesh(Square, SquareFaces, sharp: false);
        r.DefNr = new[] { new List<int> { 0 }, new List<int> { 1 }, new List<int> { 0, 1 }, new List<int>() };
        r.DefWeight = new[] { new List<float> { 1f }, new List<float> { 1f }, new List<float> { 0.25f, 0.75f }, new List<float>() };
        r.Uv.Add(new float[] { 0f, 1f, 1f, 1f, 1f, 0f, /* face 2 */ 0f, 1f, 1f, 0.5f, 0f, 0f });   // vertex 2 splits in two
        var identity = new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
        var skin = new BlenderExport.Skin { ObjectWorld = identity, ArmatureWorld = identity, GroupJoint = new[] { 3, 5 }, JointCount = 7 };
        var p = Assert.Single(BlenderExport.MeshPrimitives(r, skin));
        Assert.Equal(new[] { 0, 1, 2, 2, 3 }, p.SourceVertex);
        Assert.Equal(new ushort[] { 3, 0, 0, 0, 5, 0, 0, 0, 5, 3, 0, 0, 5, 3, 0, 0, 7, 0, 0, 0 }, p.Joints);
        Assert.Equal(new[] { 1f, 0, 0, 0, 1f, 0, 0, 0, 0.75f, 0.25f, 0, 0, 0.75f, 0.25f, 0, 0, 1f, 0, 0, 0 }, p.Weights);
        Assert.True(p.NeutralBone);
        Assert.Null(Assert.Single(BlenderExport.MeshPrimitives(r)).Joints);   // unskinned: none
    }

    [Fact]
    public void The_srgb_byte_table_is_exact_at_the_ends_rises_strictly_and_stays_on_the_srgb_curve()
    {
        Assert.Equal(U(0f), U(BlenderColor.SrgbByteToLinear(0)));
        Assert.Equal(U(1f), U(BlenderColor.SrgbByteToLinear(255)));
        for (int b = 1; b < 256; b++)
        {
            float lin = BlenderColor.SrgbByteToLinear((byte)b);
            Assert.True(lin > BlenderColor.SrgbByteToLinear((byte)(b - 1)), "byte " + b);
            // Blender fills the table in float32 (powf): a few ulps off the double curve - 4.4e-7 relative at byte 195, measured -
            // which is a thousand times less than the step to a neighbouring entry: a damaged entry cannot hide in it
            double c = b / 255.0, curve = c < 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            Assert.True(Math.Abs(lin - curve) <= 1e-6 * Math.Max(curve, 1e-3), "byte " + b + ": " + lin.ToString("R") + " vs the curve's " + curve.ToString("R"));
        }
    }
}
