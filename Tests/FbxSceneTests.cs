using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

// The FBX object layer (editor/FbxScene.cs, step 2 of the native FBX reader) on documents built in memory: the
// Properties70 reading rules, the templates, the node tree and its ordering, the transform composition, the polygon
// and layer-element decoding, the material slots. The bit-level claim against ufbx is tools/fbx_drill.sh's.
public class FbxSceneTests
{
    // ---- builders -------------------------------------------------------------------------------------------------
    static FbxNode N(string name, params object[] props) { var n = new FbxNode { Name = name }; n.Properties.AddRange(props); return n; }
    static FbxNode With(FbxNode n, params FbxNode[] children) { n.Children.AddRange(children); return n; }
    static FbxNode P(string name, string type, params object[] values) { var p = N("P", name, type, "", ""); p.Properties.AddRange(values); return p; }
    static FbxNode Props70(params FbxNode[] ps) => With(N("Properties70"), ps);
    static FbxNode Object(string cls, long id, string name, string sub, params FbxNode[] children) => With(N(cls, id, name + "\0\u0001" + cls, sub), children);
    static FbxNode C(string kind, long src, long dst, string prop = null) => prop == null ? N("C", kind, src, dst) : N("C", kind, src, dst, prop);

    static FbxDocument Doc(params FbxNode[] top) { var d = new FbxDocument { Version = 7400 }; d.Nodes.AddRange(top); return d; }

    static FbxNode Cube(long id, string name, int[] polygonIndices, double[] vertices, params FbxNode[] layers) =>
        Object("Geometry", id, name, "Mesh", new[] { N("Vertices", vertices), N("PolygonVertexIndex", polygonIndices), N("GeometryVersion", 124) }.Concat(layers).ToArray());

    // ---- properties -----------------------------------------------------------------------------------------------
    [Fact]
    public void Properties70_read_as_ufbx_reads_them()
    {
        var owner = With(N("Model"), Props70(
            P("Lcl Translation", "Lcl Translation", 1.5, 2, 3L),        // reals from consecutive numbers of any type; the int from the first
            P("InheritType", "enum", 2),
            P("Name", "KString", "hello"),
            P("Mixed", "Enum", 7, "A~B"),                                  // a number then a string: the string is still found (one skip)
            P("Dup", "int", 1), P("Dup", "int", 2),                        // twice: the last wins
            P("Half", "double", 2.75)));
        var props = FbxProps.Read(owner);
        Assert.Equal(new[] { 1.5, 2.0, 3.0 }, props.Vec3("Lcl Translation", 0, 0, 0));
        Assert.Equal(1, props.Int("Lcl Translation", 0));                 // 1.5 truncated toward zero, ufbxi_f64_to_i64
        Assert.Equal(2, props.Int("InheritType", -1));
        Assert.Equal("hello", props.Str("Name", ""));
        Assert.Equal("A~B", props.Str("Mixed", "")); Assert.Equal(7, props.Int("Mixed", 0));
        Assert.Equal(2, props.Int("Dup", 0));
        Assert.Equal(2.75, props.Real("Half", 0));
        Assert.Null(props.Find("Absent")); Assert.Equal(9, props.Int("Absent", 9));
        var tmpl = FbxProps.Read(With(N("T"), Props70(P("Lcl Scaling", "Lcl Scaling", 2.0, 2.0, 2.0), P("Dup", "int", 5))));
        props.Defaults = tmpl;
        Assert.Equal(new[] { 2.0, 2.0, 2.0 }, props.Vec3("Lcl Scaling", 1, 1, 1));   // the template answers what the object lacks
        Assert.Equal(2, props.Int("Dup", 0));                                       // and never overrides what it has
    }

    // ---- the math, against values ufbx is known to produce --------------------------------------------------------
    [Fact]
    public void Transform_math_composes_as_ufbx()
    {
        var id = FbxMath.ToMatrix(FbxTransform.Identity);
        Assert.Equal(FbxMath.IdentityMatrix, id);
        var q = FbxMath.EulerToQuat(new[] { 0.0, 0.0, 0.0 }, 0);
        Assert.Equal(new[] { 0.0, 0.0, 0.0, 1.0 }, q);
        // a 90 degree turn about Z: x maps to y
        var t = FbxTransform.Identity; t.MulRotate(new[] { 0.0, 0.0, 90.0 }, 0);
        var m = FbxMath.ToMatrix(t);
        Assert.InRange(m[0], -1e-15, 1e-15); Assert.InRange(m[1], 1 - 1e-15, 1 + 1e-15);   // column 0 = (0, 1, 0)
        // translation then scale then rotation: the pivot formula's order (scale pivot inside, rotation outside)
        var t2 = FbxTransform.Identity; t2.MulScale(new[] { 2.0, 2.0, 2.0 }); t2.AddTranslate(new[] { 1.0, 0.0, 0.0 }); t2.MulRotate(new[] { 0.0, 0.0, 90.0 }, 0);
        Assert.InRange(t2.ty, 1 - 1e-15, 1 + 1e-15); Assert.InRange(t2.tx, -1e-15, 1e-15); Assert.Equal(2.0, t2.sx);
        var a = new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 5, 6, 7 }; var b = new double[] { 2, 0, 0, 0, 2, 0, 0, 0, 2, 1, 1, 1 };
        var ab = FbxMath.Mul(a, b);
        Assert.Equal(new double[] { 2, 0, 0, 0, 2, 0, 0, 0, 2, 6, 7, 8 }, ab);
    }

    // ---- a scene ----------------------------------------------------------------------------------------------------
    static FbxDocument SceneDoc() => Doc(
        With(N("GlobalSettings"), Props70(P("UpAxis", "int", 1), P("UpAxisSign", "int", 1), P("FrontAxis", "int", 1), P("FrontAxisSign", "int", -1), P("CoordAxis", "int", 0), P("UnitScaleFactor", "double", 100.0), P("TimeMode", "enum", 6))),
        With(N("Definitions"), With(N("ObjectType", "Model"), With(N("PropertyTemplate", "FbxNode"), Props70(P("Lcl Scaling", "Lcl Scaling", 3.0, 3.0, 3.0), P("RotationActive", "bool", 1))))),
        With(N("Objects"),
            Object("Model", 10, "Root", "Null", Props70(P("Lcl Translation", "Lcl Translation", 1.0, 2.0, 3.0), P("InheritType", "enum", 2))),
            Object("Model", 11, "Child", "Mesh", Props70(P("Lcl Translation", "Lcl Translation", 0.0, 1.0, 0.0), P("Lcl Rotation", "Lcl Rotation", 0.0, 0.0, 90.0), P("PreRotation", "Vector3D", 90.0, 0.0, 0.0), P("InheritType", "enum", 0))),
            Object("Model", 12, "Sibling", "Null", Props70(P("InheritType", "enum", 1))),
            Object("NodeAttribute", 20, "Root", "Null"),
            Cube(30, "Quad", new[] { 0, 1, 2, -4, 2, 1, ~3, 0, 1, -3 },   // a quad, a triangle ending in ~3 (an un-negated last index is tolerated elsewhere), a last triangle
                 new double[] { 0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0 },
                 With(N("LayerElementNormal", 0), N("MappingInformationType", "ByPolygonVertex"), N("ReferenceInformationType", "Direct"), N("Normals", Enumerable.Repeat(new[] { 0.0, 0.0, 1.0 }, 10).SelectMany(x => x).ToArray())),
                 With(N("LayerElementUV", 0), N("Name", "map1"), N("MappingInformationType", "ByPolygonVertex"), N("ReferenceInformationType", "IndexToDirect"), N("UV", new double[] { 0, 0, 1, 0, 1, 1, 0, 1 }), N("UVIndex", new[] { 0, 1, 2, 3, 2, 1, 3, 0, 1, 9 })),   // 9 is past the 4 values: clamped to 3
                 With(N("LayerElementColor", 0), N("MappingInformationType", "ByVertice"), N("ReferenceInformationType", "Direct"), N("Colors", new double[] { 1, 0, 0, 1, 0, 1, 0, 1, 0, 0, 1, 1, 1, 1, 1, 1 })),
                 With(N("LayerElementMaterial", 0), N("MappingInformationType", "ByPolygon"), N("ReferenceInformationType", "IndexToDirect"), N("Materials", new[] { 1, 0, 5 }))),   // 5 is past the two slots: 0
            Object("Material", 40, "Steel", "", N("ShadingModel", "Phong"), Props70(P("DiffuseColor", "Color", 0.5, 0.25, 0.125))),
            Object("Material", 41, "Rust", "", N("ShadingModel", "lambert")),
            Object("Texture", 50, "Rust_D", "", N("RelativeFilename", "tex/rust.png")),
            Object("Texture", 51, "Rust_N", "", N("RelativeFilename", "tex/rust_n.png"))),
        With(N("Connections"),
            C("OO", 10, 0), C("OO", 11, 10), C("OO", 12, 10), C("OO", 20, 10), C("OO", 30, 11),
            C("OO", 41, 11), C("OO", 40, 11),            // the Model's materials in FILE order: Rust is slot 0, Steel slot 1
            C("OP", 50, 41, "DiffuseColor"), C("OO", 51, 41)));

    [Fact]
    public void Settings_nodes_order_and_inherit_modes_read_as_ufbx()
    {
        var s = FbxScene.Read(SceneDoc());
        Assert.Equal(2, s.UpAxis); Assert.Equal(3, s.FrontAxis); Assert.Equal(0, s.RightAxis);   // +Y, -Y, +X
        Assert.Equal(1.0, s.UnitMeters); Assert.Equal(30.0, s.FramesPerSecond);
        Assert.Equal(new[] { "", "Root", "Child", "Sibling" }, s.Nodes.Select(n => n.Name));   // by depth, then parent, then file order
        var root = s.Nodes[1]; var child = s.Nodes[2]; var sib = s.Nodes[3];
        Assert.True(s.Nodes[0].IsRoot); Assert.Same(s.Nodes[0], root.Parent); Assert.Same(root, child.Parent);
        Assert.Equal("empty", root.AttribType); Assert.Equal("mesh", child.AttribType); Assert.Equal("none", sib.AttribType);
        Assert.Equal(0, root.InheritMode);     // InheritType 2 would be "ignore parent scale", but a top-level node is forced normal
        Assert.Equal(2, child.InheritMode);    // 0 (RrSs) -> componentwise
        Assert.Equal(0, sib.InheritMode);      // 1 -> normal
        Assert.Same(root, child.InheritScaleNode);
        // the template's scale 3 applies to every Model; the root's translation is its own
        Assert.Equal(new[] { 3.0, 3.0, 3.0 }, new[] { root.LocalTransform.sx, root.LocalTransform.sy, root.LocalTransform.sz });
        Assert.Equal(new[] { 1.0, 2.0, 3.0 }, new[] { root.LocalTransform.tx, root.LocalTransform.ty, root.LocalTransform.tz });
        Assert.True(child.UseRotationSpace);   // RotationActive from the template: the pre-rotation applies
        // the child: componentwise scale under a scaled parent, translation (0,1,0) scaled by the parent's 3 then rotated by the parent (none)
        Assert.Equal(root.NodeToWorld.Skip(9).ToArray(), new[] { 1.0, 2.0, 3.0 });
        Assert.InRange(child.NodeToWorld[10], 2 + 3 - 1e-12, 2 + 3 + 1e-12);   // parent y 2 + 1 x 3
        Assert.Equal(9.0, child.InheritScale[0], 12);                            // 3 (own, template) x 3 (parent)
    }

    [Fact]
    public void Polygons_layer_elements_and_material_slots_decode_as_ufbx()
    {
        var s = FbxScene.Read(SceneDoc());
        var m = Assert.Single(s.Meshes);
        Assert.Equal(4, m.NumVertices); Assert.Equal(10, m.NumIndices); Assert.Equal(3, m.NumFaces);
        Assert.Equal(new[] { 4, 3, 3 }, m.FaceSize); Assert.Equal(new[] { 0, 4, 7 }, m.FaceBegin);
        Assert.Equal(new[] { 0, 1, 2, 3, 2, 1, 3, 0, 1, 2 }, m.VertexIndices);
        Assert.Equal(4, m.NumTriangles); Assert.Equal(2, m.MaxFaceTriangles);
        Assert.True(m.Normal.Exists); Assert.Equal(Enumerable.Range(0, 10).ToArray(), m.Normal.Indices); Assert.False(m.Normal.UniquePerVertex);
        var uv = Assert.Single(m.UvSets);
        Assert.Equal("map1", uv.Name);
        Assert.Equal(new[] { 0, 1, 2, 3, 2, 1, 3, 0, 1, 3 }, uv.Indices);   // the 9 clamped to the last value
        Assert.Equal(1.0, uv.Get(2, 0)); Assert.Equal(1.0, uv.Get(2, 1));
        var col = Assert.Single(m.ColorSets);
        Assert.True(col.UniquePerVertex); Assert.Equal(m.VertexIndices, col.Indices);   // ByVertice direct: the position indices
        Assert.Equal(0.0, col.Get(1, 0)); Assert.Equal(1.0, col.Get(1, 1));
        Assert.Equal(new[] { "Rust", "Steel" }, m.Materials.Select(x => x.Name));
        Assert.Equal(new[] { 1, 0, 0 }, m.FaceMaterial);                          // 5 was past the slots
        Assert.Same(s.Nodes[2], Assert.Single(m.Instances));
        Assert.Equal(2, s.Materials.Count);
        Assert.Equal(2, s.Materials[0].ShaderType); Assert.Equal(1, s.Materials[1].ShaderType);
        Assert.Equal(new[] { 0.5, 0.25, 0.125 }, s.Materials[0].Diffuse);
        Assert.Equal(new[] { ("", "tex/rust_n.png"), ("DiffuseColor", "tex/rust.png") }, s.Materials[1].Textures.Select(t => (t.prop, t.relativeFilename)));   // ufbx: by property name, the unnamed first
    }

    [Fact]
    public void A_mesh_without_a_model_and_a_model_without_a_parent_connection_are_kept()
    {
        var s = FbxScene.Read(Doc(With(N("Objects"), Object("Model", 1, "Loose", "Null"), Cube(2, "Orphan", new[] { 0, 1, ~2 }, new double[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 })), N("Connections")));
        Assert.Equal(2, s.Nodes.Count); Assert.Same(s.Nodes[0], s.Nodes[1].Parent);   // an orphan Model hangs from the root, as in ufbx
        var m = Assert.Single(s.Meshes); Assert.Empty(m.Instances); Assert.Equal(1, m.NumFaces);
        Assert.Empty(m.FaceMaterial);   // no material slot at all: no face material list (ufbx); one slot would give every face 0
    }

    [Fact]
    public void Numeric_identifiers_are_not_limited_to_int64_properties()
    {
        var model = N("Model", 1, "IntegerId\0\u0001Model", "Null");
        var s = FbxScene.Read(Doc(With(N("Objects"), model), With(N("Connections"), N("C", "OO", 1, 0))));
        Assert.Equal("IntegerId", Assert.Single(s.Nodes.Where(n => !n.IsRoot)).Name);
        Assert.Same(s.Nodes[0], s.Nodes[1].Parent);
        Assert.Same(s.Objects[0], Assert.Single(s.Connections).Src);
    }

    [Fact]
    public void An_empty_declared_property_overrides_a_template_with_zero_values()
    {
        var props = FbxProps.Read(With(N("Model"), Props70(P("Lcl Scaling", "Lcl Scaling"), P("Dup", "double", 2.0), P("Dup", "double"))));
        props.Defaults = FbxProps.Read(With(N("Template"), Props70(P("Lcl Scaling", "Lcl Scaling", 3.0, 3.0, 3.0))));
        Assert.NotNull(props.Find("Lcl Scaling"));
        Assert.Equal(new[] { 0.0, 0.0, 0.0 }, props.Vec3("Lcl Scaling", 1, 1, 1));
        Assert.Equal(0.0, props.Real("Dup", 99));
    }

    static FbxNode Layer(string kind, int index, string field, double[] values, string name = "") =>
        With(N("LayerElement" + kind, index), N("Name", name), N("MappingInformationType", "ByPolygonVertex"),
            N("ReferenceInformationType", "Direct"), N(field, values));
    static FbxScene MeshScene(params FbxNode[] layers) => FbxScene.Read(Doc(With(N("Objects"), Cube(1, "Triangle", new[] { 0, 1, -3 },
        new double[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, layers))));

    [Fact]
    public void Uv_and_color_sets_sort_by_typed_index_stably()
    {
        var m = Assert.Single(MeshScene(Layer("UV", 2, "UV", new double[6], "uv2"), Layer("UV", 0, "UV", new double[6], "uv0"),
            Layer("Color", 2, "Colors", new double[12], "color2"), Layer("Color", 0, "Colors", new double[12], "color0")).Meshes);
        Assert.Equal(new[] { "uv0", "uv2" }, m.UvSets.Select(s => s.Name));
        Assert.Equal(new[] { "color0", "color2" }, m.ColorSets.Select(s => s.Name));
    }

    [Fact]
    public void Primary_tangents_follow_the_uv_layer_references()
    {
        var link = With(N("Layer", 0), With(N("LayerElement"), N("Type", "LayerElementUV"), N("TypedIndex", 0)),
            With(N("LayerElement"), N("Type", "LayerElementTangent"), N("TypedIndex", 0)));
        var layers = new[] { Layer("UV", 0, "UV", new double[6]),
            Layer("Tangent", 1, "Tangents", new double[] { 0, 1, 0, 0, 1, 0, 0, 1, 0 }),
            Layer("Tangent", 0, "Tangents", new double[] { 1, 0, 0, 1, 0, 0, 1, 0, 0 }) };
        var m = Assert.Single(MeshScene(layers.Concat(new[] { link }).ToArray()).Meshes);
        Assert.Equal(1.0, m.Tangent.Get(0, 0));
        Assert.Equal(0.0, m.Tangent.Get(0, 1));
        Assert.False(Assert.Single(MeshScene(layers).Meshes).Tangent.Exists); // without a Layer association it is not the primary UV's tangent
    }

    [Fact]
    public void Numeric_mesh_arrays_are_converted_like_ufbx()
    {
        var mesh = Object("Geometry", 1, "Bytes", "Mesh", N("Vertices", new byte[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }),
            N("PolygonVertexIndex", new long[] { 0, 1, -3 }),
            With(N("LayerElementColor", 0), N("MappingInformationType", "ByPolygonVertex"),
                N("Colors", new[] { true, false, false, true, true, false, false, true, true, false, false, true })));
        var m = Assert.Single(FbxScene.Read(Doc(With(N("Objects"), mesh))).Meshes);
        Assert.Equal(new double[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, m.Vertices);
        Assert.Equal(new[] { 0, 1, 2 }, m.VertexIndices);
        Assert.Equal(1.0, Assert.Single(m.ColorSets).Get(0, 0));
    }

    [Fact]
    public void Multiple_parents_use_ufbx_element_order_instead_of_the_first_connection()
    {
        var objects = With(N("Objects"), Object("Model", 1, "Child", "Null"), Object("Model", 2, "ParentA", "Null"), Object("Model", 3, "ParentB", "Null"));
        foreach (bool reverse in new[] { false, true })
        {
            var edges = reverse ? new[] { C("OO", 1, 3), C("OO", 1, 2) } : new[] { C("OO", 1, 2), C("OO", 1, 3) };
            var s = FbxScene.Read(Doc(objects, With(N("Connections"), edges)));
            Assert.Equal("ParentB", s.Nodes.Single(n => n.Name == "Child").Parent.Name);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cyclic_hierarchies_are_refused_by_name(bool self)
    {
        var objects = With(N("Objects"), Object("Model", 1, "One", "Null"), Object("Model", 2, "Two", "Null"));
        var connections = self ? With(N("Connections"), C("OO", 1, 1)) : With(N("Connections"), C("OO", 1, 2), C("OO", 2, 1));
        Assert.Contains("Cyclic", Assert.Throws<InvalidDataException>(() => FbxScene.Read(Doc(objects, connections))).Message);
    }

    [Fact]
    public void Texture_aliases_and_utf8_property_order_are_preserved()
    {
        string pua = "\ue000", emoji = "\ud83d\ude00";
        var s = FbxScene.Read(Doc(With(N("Objects"), Object("Material", 1, "Material", ""),
            Object("Texture", 2, "PUA", "", N("RelativeFileName", "textures/pua.png")),
            Object("Texture", 3, "Emoji", "", N("RelativeFilename", "textures/emoji.png"))),
            With(N("Connections"), C("OP", 2, 1, pua), C("OP", 3, 1, emoji))));
        var textures = Assert.Single(s.Materials).Textures;
        Assert.Equal(new[] { pua, emoji }, textures.Select(t => t.prop));
        Assert.Equal(new[] { "textures/pua.png", "textures/emoji.png" }, textures.Select(t => t.relativeFilename));
    }

    [Fact]
    public void Layered_texture_references_keep_their_children()
    {
        var s = FbxScene.Read(Doc(With(N("Objects"), Object("Material", 1, "Material", ""),
            Object("LayeredTexture", 2, "Layers", ""), Object("Texture", 3, "Leaf", "", N("RelativeFilename", "leaf.png"))),
            With(N("Connections"), C("OP", 2, 1, "DiffuseColor"), C("OO", 3, 2))));
        var texture = Assert.Single(Assert.Single(s.Materials).Textures).texture;
        Assert.Equal("LayeredTexture", texture.Class);
        Assert.True(texture.IsTexture);
        Assert.Equal("Leaf", Assert.Single(texture.Sources).Src.Name);
    }
}
