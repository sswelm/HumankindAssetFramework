using System.Collections.Generic;
using Xunit;

/// <summary>rig_anim's folded-face removal after the decimate (2026-11-16, the Zeppelin): the collapse squashed a small
/// tetrahedron flat and left two triangles on the same three vertices, back to back; Blender's FBX export kept both, its
/// glTF export one, and the rig-route row failed on the two-triangle difference. BlenderRigAnim.RemoveFoldedFaces removes
/// the LATER face of such a pair and closes every per-face and per-corner array up in order. The live drill holds the
/// whole stage against Blender on the Zeppelin's own job; this holds the rule in the unit suite.</summary>
public class RigAnimFoldedFacesTests
{
    static BlenderRigAnim.MeshData Mesh(params int[] faces)
    {
        int nf = faces.Length / 3;
        var d = new BlenderRigAnim.MeshData { Vertices = 5, Faces = faces, FaceMaterial = new int[nf], FaceSharp = new bool[nf] };
        var uv = new float[6 * nf]; var cn = new short[6 * nf]; var col = new byte[12 * nf]; var point = new byte[4 * 5];
        for (int f = 0; f < nf; f++)
        {
            d.FaceMaterial[f] = f; d.FaceSharp[f] = f % 2 == 1;
            for (int k = 0; k < 6; k++) { uv[6 * f + k] = 10 * f + k; cn[6 * f + k] = (short)(100 * f + k); }
            for (int k = 0; k < 12; k++) col[12 * f + k] = (byte)(20 * f + k);
        }
        d.Uv = uv; d.UvAll.Add(uv); d.UvAll.Add((float[])uv.Clone()); d.CustomNormal = cn;
        d.Colors.Add(("Col", false, col)); d.Colors.Add(("Pt", true, point));
        d.Edges = new[] { 0, 1, 1, 2, 2, 0 };
        return d;
    }

    [Fact]
    public void The_later_face_of_a_back_to_back_pair_is_removed_and_the_arrays_close_up()
    {
        // face 1 is face 0 reversed (the fold), face 3 is face 0 rotated (a plain duplicate): both go, 0 and 2 stay
        var d = Mesh(0, 1, 2, 2, 1, 0, 1, 2, 3, 1, 2, 0);
        var edges = d.Edges; var point = d.Colors[1].bytes;
        Assert.Equal(2, BlenderRigAnim.RemoveFoldedFaces(d));
        Assert.Equal(new[] { 0, 1, 2, 1, 2, 3 }, d.Faces);
        Assert.Equal(new[] { 0, 2 }, d.FaceMaterial);
        Assert.Equal(new[] { false, false }, d.FaceSharp);
        Assert.Equal(new float[] { 0, 1, 2, 3, 4, 5, 20, 21, 22, 23, 24, 25 }, d.Uv);
        Assert.Same(d.UvAll[0], d.Uv);                       // the active layer stays the first layer's own array
        Assert.Equal(d.Uv, d.UvAll[1]);
        Assert.Equal(new short[] { 0, 1, 2, 3, 4, 5, 200, 201, 202, 203, 204, 205 }, d.CustomNormal);
        var want = new List<byte>(); for (int k = 0; k < 12; k++) want.Add((byte)k); for (int k = 0; k < 12; k++) want.Add((byte)(40 + k));
        Assert.Equal(want.ToArray(), d.Colors[0].bytes);
        Assert.Same(point, d.Colors[1].bytes);               // per vertex: FACES_ONLY removes no vertex
        Assert.Same(edges, d.Edges);                         // nor an edge: they are the earlier face's too
        Assert.Equal(5, d.Vertices);
    }

    [Fact]
    public void A_mesh_without_a_pair_is_not_touched()
    {
        // faces sharing an edge, and two faces sharing two vertices, are no fold
        var d = Mesh(0, 1, 2, 2, 1, 3, 0, 1, 4);
        var faces = d.Faces; var uv = d.Uv; var cn = d.CustomNormal;
        Assert.Equal(0, BlenderRigAnim.RemoveFoldedFaces(d));
        Assert.Same(faces, d.Faces); Assert.Same(uv, d.Uv); Assert.Same(cn, d.CustomNormal);
    }

    [Fact]
    public void A_mesh_without_optional_layers_closes_up_too()
    {
        var d = new BlenderRigAnim.MeshData { Vertices = 3, Faces = new[] { 0, 1, 2, 0, 2, 1 }, FaceMaterial = new[] { 0, 1 } };
        Assert.Equal(1, BlenderRigAnim.RemoveFoldedFaces(d));
        Assert.Equal(new[] { 0, 1, 2 }, d.Faces);
        Assert.Equal(new[] { 0 }, d.FaceMaterial);
        Assert.Null(d.FaceSharp); Assert.Null(d.Uv); Assert.Null(d.CustomNormal);
    }
}
