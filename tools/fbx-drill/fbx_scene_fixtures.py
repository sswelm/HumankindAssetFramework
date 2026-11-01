"""Small object-layer scenes for branches absent from the real-file corpus; the oracle is vendored ufbx."""
from pathlib import Path
import sys
from fbx_fixtures import Node, prop, write_fbx


def p(name, *values):
    def value(v):
        return prop('S', v) if isinstance(v, str) else prop('D', v) if isinstance(v, float) else prop('I', v)
    return Node('P', [prop('S', name), prop('S', 'Vector3D'), prop('S', ''), prop('S', '')] + [value(v) for v in values])


def properties(*ps):
    return Node('Properties70', [], ps)


def obj(kind, ident, name, subtype='', children=(), id_type='L'):
    return Node(kind, [prop(id_type, ident), prop('S', name + '\0\1' + kind), prop('S', subtype)], children)


def connect(src, dst, id_type='L'):
    return Node('C', [prop('S', 'OO'), prop(id_type, src), prop(id_type, dst)])


def scene(objects, connections, extra=()):
    return list(extra) + [Node('Objects', [], objects), Node('Connections', [], connections)]


def layer(kind, index, values, components, name='', array_type='d'):
    field = {'UV': 'UV', 'Color': 'Colors', 'Tangent': 'Tangents', 'Binormal': 'Binormals'}[kind]
    return Node('LayerElement' + kind, [prop('I', index)], [
        Node('Name', [prop('S', name)]), Node('MappingInformationType', [prop('S', 'ByPolygonVertex')]),
        Node('ReferenceInformationType', [prop('S', 'Direct')]), Node(field, [prop(array_type, values * 3)])])


def triangle(layers=(), vertex_type='d'):
    return obj('Geometry', 30, 'Triangle', 'Mesh', [Node('Vertices', [prop(vertex_type, [0, 0, 0, 1, 0, 0, 0, 1, 0])]),
        Node('PolygonVertexIndex', [prop('i', [0, 1, -3])]), Node('GeometryVersion', [prop('I', 124)])] + list(layers))


def fixtures():
    # Outside the Windows ANSI code page: the oracle must receive Unicode command-line paths intact.
    yield 'control-\u00e9-\U0001f600', scene([obj('Model', 1, 'Node', 'Null')], [connect(1, 0)])
    yield 'integer_ids', scene([obj('Model', 1, 'IntegerId', 'Null', id_type='I')], [connect(1, 0, 'I')])
    template = Node('Definitions', [], [Node('ObjectType', [prop('S', 'Model')], [
        Node('PropertyTemplate', [prop('S', 'FbxNode')], [properties(p('Lcl Scaling', 3.0, 3.0, 3.0))])])])
    yield 'empty_property', scene([obj('Model', 1, 'EmptyScale', 'Null', [properties(p('Lcl Scaling'))])], [connect(1, 0)], [template])
    layers = [layer('UV', 2, [0.2, 0.8], 2, 'uv2'), layer('UV', 0, [0.7, 0.3], 2, 'uv0'),
              layer('Color', 2, [0.2, 0.3, 0.4, 1.0], 4, 'color2'), layer('Color', 0, [1.0, 0.0, 0.0, 1.0], 4, 'color0')]
    yield 'layer_order', scene([obj('Model', 1, 'Node', 'Mesh'), triangle(layers)], [connect(1, 0), connect(30, 1)])
    links = Node('Layer', [prop('I', 0)], [Node('LayerElement', [], [Node('Type', [prop('S', 'LayerElement' + kind)]),
        Node('TypedIndex', [prop('I', 0)])]) for kind in ('UV', 'Tangent', 'Binormal')])
    layers = [layer('UV', 0, [0.0, 1.0], 2, 'uv0'), layer('Tangent', 1, [0.0, 1.0, 0.0], 3),
              layer('Tangent', 0, [1.0, 0.0, 0.0], 3), layer('Binormal', 1, [1.0, 0.0, 0.0], 3),
              layer('Binormal', 0, [0.0, 1.0, 0.0], 3), links]
    yield 'primary_tangent', scene([obj('Model', 1, 'Node', 'Mesh'), triangle(layers)], [connect(1, 0), connect(30, 1)])
    yield 'byte_vertices', scene([obj('Model', 1, 'Node', 'Mesh'), triangle(vertex_type='c')], [connect(1, 0), connect(30, 1)])
    yield 'multiple_parents', scene([obj('Model', 1, 'Child', 'Null'), obj('Model', 2, 'ParentA', 'Null'), obj('Model', 3, 'ParentB', 'Null')],
        [connect(1, 2), connect(1, 3), connect(2, 0), connect(3, 0)])
    yield 'instance_materials', scene([obj('Model', 1, 'Child', 'Mesh'), obj('Model', 2, 'Parent', 'Mesh'), triangle(),
        obj('Material', 40, 'ChildMat', children=[Node('ShadingModel', [prop('S', 'phong')])]),
        obj('Material', 41, 'ParentMat', children=[Node('ShadingModel', [prop('S', 'phong')])])],
        [connect(1, 2), connect(2, 0), connect(30, 2), connect(30, 1), connect(40, 1), connect(41, 2)])
    yield 'texture_names', scene([obj('Material', 40, 'Material', children=[Node('ShadingModel', [prop('S', 'phong')])]),
        obj('Texture', 50, 'PUA', children=[Node('RelativeFileName', [prop('S', 'textures/pua.png')])]),
        obj('Texture', 51, 'Emoji', children=[Node('RelativeFilename', [prop('S', 'textures/emoji.png')])])],
        [Node('C', [prop('S', 'OP'), prop('L', 50), prop('L', 40), prop('S', '\ue000')]),
         Node('C', [prop('S', 'OP'), prop('L', 51), prop('L', 40), prop('S', '\U0001f600')])])
    yield 'layered_texture', scene([obj('Material', 40, 'Material', children=[Node('ShadingModel', [prop('S', 'phong')])]),
        obj('LayeredTexture', 50, 'Layers', children=[Node('BlendModes', [prop('i', [0])])]),
        obj('Texture', 51, 'Leaf', children=[Node('RelativeFilename', [prop('S', 'textures/leaf.png')])])],
        [connect(51, 50), Node('C', [prop('S', 'OP'), prop('L', 50), prop('L', 40), prop('S', 'DiffuseColor')])])


def polygon(ident, name, vertices, faces):
    """A Geometry of the given faces (lists of vertex indices) over flat xyz vertices."""
    idx = []
    for face in faces:
        idx += list(face[:-1]) + [~face[-1]]
    return obj('Geometry', ident, name, 'Mesh', [Node('Vertices', [prop('d', vertices)]),
        Node('PolygonVertexIndex', [prop('i', idx)]), Node('GeometryVersion', [prop('I', 124)])])


def star(points, outer, inner, z=0.0, phase=0.0):
    """A 2*points-gon alternating outer and inner radius: half its corners are reflex."""
    import math
    xyz = []
    for i in range(points * 2):
        r = outer if i % 2 == 0 else inner
        a = phase + math.pi * i / points
        xyz += [r * math.cos(a), r * math.sin(a), z]
    return xyz


def triangulation_fixtures():
    # step 3 (triangulation): the quad rule and the n-gon ear clipping on shapes the real files do not reach
    quads = [0, 0, 0, 2, 0, 0, 2, 1, 0, 0, 1, 0,             # a flat trapezoid: the shorter diagonal splits it
             0, 0, 1, 1, 0, 1, 1, 1, 1, 0, 1, 1,             # a square: equal diagonals, the first wins (<=)
             0, 0, 2, 1, 0, 2, 1, 1, 2, 0, 1, 4,             # a square with one corner lifted: a vertex crosses the shorter diagonal's plane
             0, 0, 3, 3, 0, 3, 3, 3, 3, 0, 0.1, 3,           # a near-fold the other way
             0, 0, 4, 1, 1, 4, 2, 2, 4, 3, 3, 4,             # four collinear points: zero normals, the dot tie
             0, 0, 5, 1, 3, 5, 0, 2.5, 5, -1, 3, 5,          # a dart: the SHORTER diagonal (v1-v3) is crossed by the reflex corner, the longer one splits
             0, 0, 6, 1, 0, 7, 1, 1, 6, 0, 1, 7,             # a twisted quad: both diagonals crossed equally, the first wins (>=)
             0, 0, 8, 1, 0, 9, 1, 1, 8, 0, 1, 9.5]           # twisted unevenly: the less crossed diagonal (the larger dot) wins
    yield 'quads', scene([obj('Model', 1, 'Node', 'Mesh'),
        polygon(30, 'Quads', quads, [[4 * k + j for j in range(4)] for k in range(8)])], [connect(1, 0), connect(30, 1)])
    # an L-shape (one reflex corner), a U-shape (two), a star of 8 (eight reflex corners), a convex octagon
    L = [0, 0, 0, 3, 0, 0, 3, 1, 0, 1, 1, 0, 1, 3, 0, 0, 3, 0]
    U = [0, 0, 0, 3, 0, 0, 3, 3, 0, 2, 3, 0, 2, 1, 0, 1, 1, 0, 1, 3, 0, 0, 3, 0]
    S = star(8, 2.0, 0.8)
    O = star(4, 1.0, 1.0)
    verts = L + U + S + O
    nL, nU, nS = len(L) // 3, len(U) // 3, len(S) // 3
    faces = [list(range(nL)), list(range(nL, nL + nU)), list(range(nL + nU, nL + nU + nS)), list(range(nL + nU + nS, len(verts) // 3))]
    yield 'ngons', scene([obj('Model', 1, 'Node', 'Mesh'), polygon(30, 'Ngons', verts, faces)], [connect(1, 0), connect(30, 1)])
    # the same shapes reversed (clockwise): the reflex set flips, and the ear weights turn negative
    rev = [list(reversed(f)) for f in faces]
    yield 'ngons_reversed', scene([obj('Model', 1, 'Node', 'Mesh'), polygon(30, 'Reversed', verts, rev)], [connect(1, 0), connect(30, 1)])
    # a tilted star in 3D (the Newell normal picks the Y axis helper when x*x >= 0.5) and a near-degenerate strip
    import math
    tilted = []
    for i in range(0, len(S), 3):
        x, y = S[i], S[i + 1]
        tilted += [y, x * 0.6, x * 0.8]     # rotate so the normal is mostly along x
    strip = [0, 0, 0, 1, 0, 0, 2, 0, 0, 3, 0, 0, 4, 0, 0, 4, 1e-9, 0, 2, 1e-9, 0, 0, 1e-9, 0]
    verts = tilted + strip
    nT = len(tilted) // 3
    yield 'ngons_3d', scene([obj('Model', 1, 'Node', 'Mesh'), polygon(30, 'Ngons3D', verts, [list(range(nT)), list(range(nT, len(verts) // 3))])], [connect(1, 0), connect(30, 1)])
    # a star of 160 (160 reflex corners: past the 127 the KD tree's cached levels hold, into its slow ranges)
    big = star(160, 10.0, 4.0, phase=0.3)
    yield 'ngon_big_star', scene([obj('Model', 1, 'Node', 'Mesh'), polygon(30, 'BigStar', big, [list(range(320))])], [connect(1, 0), connect(30, 1)])
    # repeated vertices (zero-length edges: NaN ear weights) and a self-touching polygon
    dup = [0, 0, 0, 2, 0, 0, 2, 0, 0, 2, 2, 0, 0, 2, 0, 0, 2, 0, 0, 1, 0]
    touch = [0, 0, 0, 2, 0, 0, 1, 1, 0, 2, 2, 0, 0, 2, 0, 1, 1, 0]
    verts = dup + touch
    nD = len(dup) // 3
    yield 'ngons_degenerate', scene([obj('Model', 1, 'Node', 'Mesh'), polygon(30, 'Degenerate', verts, [list(range(nD)), list(range(nD, len(verts) // 3))])], [connect(1, 0), connect(30, 1)])
    # Finite coordinates, but ear-weight products underflow: some cosines are 0/0. The C99 fmax must
    # discard a NaN on either side. A comparison-only oracle wrapper incorrectly falls back to a fan.
    s = 5e-151
    tiny = [0, 0, 0, 0, s, 0, 0, 0, s, 0, 0, -s, 0, 0, 0, 0, -s, 0]
    yield 'ngon_underflow', scene([obj('Model', 1, 'Node', 'Mesh'),
        polygon(30, 'Underflow', tiny, [list(range(6))])], [connect(1, 0), connect(30, 1)])


def translation(x, y, z):
    """A stored 4x4 (row-major, translation in the last row: ufbxi_read_transform_matrix takes data[12..14])."""
    return [1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, float(x), float(y), float(z), 1.0]


def diagonal(x, y, z, t=(0.0, 0.0, 0.0)):
    return [float(x), 0.0, 0.0, 0.0, 0.0, float(y), 0.0, 0.0, 0.0, 0.0, float(z), 0.0, float(t[0]), float(t[1]), float(t[2]), 1.0]


def model(ident, name, subtype, *props):
    return obj('Model', ident, name, subtype, [properties(*props)] if props else [])


def limb(ident, name, subtype='LimbNode', *props):
    return obj('NodeAttribute', ident, name, subtype, [Node('TypeFlags', [prop('S', 'Skeleton')])] + ([properties(*props)] if props else []))


def cluster(ident, name, indices=None, weights=None, transform=None, link=None):
    children = []
    if indices is not None:
        children += [Node('Indexes', [prop('i', indices)]), Node('Weights', [prop('d', weights)])]
    if transform is not None:
        children.append(Node('Transform', [prop('d', transform)]))
    if link is not None:
        children.append(Node('TransformLink', [prop('d', link)]))
    return obj('Deformer', ident, name, 'Cluster', children)


def skin(ident, name, method=None, dq=None):
    children = [Node('Version', [prop('I', 101)])]
    if method is not None:
        children.append(Node('SkinningType', [prop('S', method)]))
    if dq is not None:
        children += [Node('Indexes', [prop('i', dq[0])]), Node('BlendWeights', [prop('d', dq[1])])]
    return obj('Deformer', ident, name, 'Skin', children)


def pose(ident, name, entries, subtype='BindPose'):
    nodes = []
    for entry in entries:
        children = []
        if entry[0] is not None:
            children.append(Node('Node', [prop('L', entry[0])]))
        if entry[1] is not None:
            children.append(Node('Matrix', [prop('d', entry[1])]))
        nodes.append(Node('PoseNode', [], children))
    return obj('Pose', ident, name, subtype, [Node('Type', [prop('S', subtype)]), Node('NbPoseNodes', [prop('I', len(entries))])] + nodes)


def header(creator):
    return [Node('FBXHeaderExtension', [], [Node('FBXHeaderVersion', [prop('I', 1003)]), Node('FBXVersion', [prop('I', 7400)]),
                                           Node('Creator', [prop('S', creator)])])]


def quad():
    return polygon(30, 'Quad', [0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0], [[0, 1, 2], [0, 2, 3]])


def skin_fixtures():
    # step 4 (skins): the Blender shape - a skinned mesh node, two bones, two clusters with Transform and
    # TransformLink, a bind pose that also names the root (id 0, as the FBX SDK writes it), an unknown id and a
    # Geometry id in the pose (both skipped); vertex 0 has equal weights from both clusters (the stable sort keeps the
    # cluster order), vertex 1 a larger one from the second (sorted first), cluster 2 names a vertex past the mesh
    import math
    nan = math.nan
    rig = [model(1, 'Mesh', 'Mesh', p('Lcl Translation', 1.0, 2.0, 3.0)), quad(),
           model(10, 'Root', 'LimbNode', p('Lcl Translation', 0.0, 1.0, 0.0)), model(11, 'Child', 'LimbNode', p('Lcl Translation', 0.0, 1.0, 0.0), p('Lcl Rotation', 0.0, 0.0, 90.0)),
           limb(20, 'RootAttr', 'LimbNode', p('Size', 1.0)), limb(21, 'ChildAttr', 'LimbNode', p('Size', 2.0))]
    links = [connect(1, 0), connect(10, 0), connect(11, 10), connect(30, 1), connect(20, 10), connect(21, 11)]
    yield 'skin_basic', scene(rig + [skin(40, 'Skin'),
        cluster(41, 'RootCluster', [0, 1, 2, 3], [0.5, 0.25, 1.0, 0.0], translation(-1, -2, -3), translation(0, 1, 0)),
        cluster(42, 'ChildCluster', [1, 0, 3, 7], [0.5, 0.5, 0.25, 0.9], translation(-1, -3, -3), diagonal(0, -1, 0, (0, 2, 0))),
        pose(50, 'BindPose', [(10, translation(0, 1, 0)), (11, diagonal(0, -1, 0, (0, 2, 0))), (1, translation(1, 2, 3)), (0, translation(0, 0, 0)),
                              (999, translation(9, 9, 9)), (30, translation(3, 3, 3))])],
        links + [connect(41, 40), connect(42, 40), connect(40, 30), connect(10, 41), connect(11, 42)])
    # clusters without matrices: bind_to_world comes from the bind pose and mesh_node_to_bone is computed from its
    # inverse and the mesh node's world transform; the skin is connected to the MODEL, not the Geometry; a bone the
    # pose does not name keeps a zero bind matrix, whose inverse is zero
    yield 'skin_from_pose', scene(rig + [skin(40, 'Skin'),
        cluster(41, 'RootCluster', [0, 1, 2, 3], [0.5, 0.25, 1.0, 0.0]),
        cluster(42, 'ChildCluster', [1, 0, 3], [0.5, 0.5, 0.25]),
        pose(50, 'BindPose', [(10, diagonal(2, 2, 2, (0, 1, 0))), (1, translation(1, 2, 3))])],
        links + [connect(41, 40), connect(42, 40), connect(40, 1), connect(10, 41), connect(11, 42)])
    # the broken shapes: a cluster connected twice (ignored once), one without a bone (dropped from the skin, still a
    # cluster), one connected to no skin (no patch), a skin deforming nothing, NaN weights (the stable sort's own
    # order), a mesh node with a geometric translation (geometry_to_bone = mesh_node_to_bone * geometry_to_node)
    broken = [model(1, 'Mesh', 'Mesh', p('Lcl Translation', 1.0, 2.0, 3.0), p('GeometricTranslation', 0.5, 0.0, 0.0)), quad(),
              model(10, 'Root', 'LimbNode', p('Lcl Translation', 0.0, 1.0, 0.0)), model(11, 'Child', 'LimbNode', p('Lcl Translation', 0.0, 1.0, 0.0)),
              limb(20, 'RootAttr'), limb(21, 'ChildAttr'),
              skin(40, 'Skin'), skin(45, 'EmptySkin'),
              cluster(41, 'RootCluster', [0, 1, 2, 3], [nan, 0.25, nan, 0.0], translation(-1, -2, -3), translation(0, 1, 0)),
              cluster(42, 'ChildCluster', [2, 1, 3], [0.5, 0.5, 0.25], translation(-1, -3, -3), translation(0, 2, 0)),
              cluster(43, 'NoBone', [2, 0], [0.75, 0.5], translation(0, 0, 0), translation(0, 0, 0)),
              cluster(44, 'SecondRoot', [2, 1], [nan, 1.0], translation(-1, -2, -3), translation(0, 1, 0)),
              cluster(46, 'Loose', [0], [1.0], translation(4, 4, 4), translation(0, 0, 1)),
              pose(50, 'BindPose', [(10, translation(0, 1, 0)), (11, translation(0, 2, 0))])]
    yield 'skin_broken', scene(broken, [connect(1, 0), connect(10, 0), connect(11, 10), connect(30, 1), connect(20, 10), connect(21, 11),
        connect(41, 40), connect(42, 40), connect(41, 40), connect(43, 40), connect(44, 40), connect(40, 30), connect(10, 41), connect(11, 42), connect(10, 44), connect(10, 46)])
    # the skinning methods and the dual quaternion blend weights (a vertex past the mesh is skipped; DualQuaternion
    # defaults every vertex to 1.0, Blend to 0.0 then the stored ones)
    methods = [model(1, 'Mesh', 'Mesh'), quad(), model(10, 'Root', 'LimbNode'), limb(20, 'RootAttr'),
               skin(40, 'Blend', 'Blend', ([1, 9, 3], [0.3, 0.4, 0.5, 0.6])), skin(45, 'DQ', 'DualQuaternion'), skin(46, 'Rigid', 'Rigid'), skin(47, 'Linear', 'Linear'), skin(48, 'Odd', 'Spline'),
               cluster(41, 'C', [0, 1, 2, 3], [1.0, 1.0, 1.0, 1.0], translation(0, 0, 0), translation(0, 0, 0))]
    yield 'skin_methods', scene(methods, [connect(1, 0), connect(10, 0), connect(30, 1), connect(20, 10), connect(41, 40), connect(41, 45), connect(41, 46), connect(41, 47), connect(41, 48),
        connect(40, 30), connect(45, 30), connect(46, 30), connect(47, 30), connect(48, 30), connect(10, 41), connect(40, 30)])
    # the cluster matrices through ufbx_matrix_to_transform's branches (trace, x, y, z), a negative determinant, a zero
    # column, and a singular bind matrix (its inverse is zero); the bone sits at (0,1,0) so geometry_to_world is a product
    xforms = [model(1, 'Mesh', 'Mesh'), quad(), model(10, 'Root', 'LimbNode', p('Lcl Translation', 0.0, 1.0, 0.0), p('Lcl Rotation', 30.0, 0.0, 0.0)), limb(20, 'RootAttr'), skin(40, 'Skin'),
              cluster(41, 'Trace', [0], [1.0], translation(1, 0, 0), translation(0, 1, 0)),
              cluster(42, 'X', [0], [1.0], diagonal(1, -1, -1, (1, 2, 3)), translation(0, 1, 0)),
              cluster(43, 'Y', [0], [1.0], diagonal(-1, 1, -1), translation(0, 1, 0)),
              cluster(44, 'Z', [0], [1.0], diagonal(-1, -1, 1), translation(0, 1, 0)),
              cluster(45, 'Mirror', [0], [1.0], diagonal(-2, 3, 4, (1, 1, 1)), translation(0, 1, 0)),
              cluster(46, 'ZeroColumn', [0], [1.0], diagonal(0, 2, 2), translation(0, 1, 0)),
              cluster(47, 'Singular', [0], [1.0], None, None),
              cluster(48, 'Scaled', [0], [1.0], diagonal(0.5, 0.25, 2, (1, 2, 3)), diagonal(2, 2, 2, (0, 1, 0))),
              pose(50, 'BindPose', [(10, diagonal(1, 1, 0, (0, 1, 0)))])]
    ids = [41, 42, 43, 44, 45, 46, 47, 48]
    yield 'cluster_transforms', scene(xforms, [connect(1, 0), connect(10, 0), connect(30, 1), connect(20, 10), connect(40, 30)] + [connect(c, 40) for c in ids] + [connect(10, c) for c in ids])
    # bones: the exporter decides the Size unit (Blender binary 33, Blender ASCII 1 with no LimbLength, else 100/3);
    # an attribute shared by two nodes, a node with two bone attributes (the last is its bone), a node with a bone
    # and a mesh (the first attribute names its type)
    bones = [model(10, 'A', 'LimbNode'), model(11, 'B', 'LimbNode'), model(12, 'Two', 'LimbNode'), model(13, 'BoneAndMesh', 'LimbNode'), quad(),
             limb(20, 'Shared', 'LimbNode', p('Size', 2.0), p('LimbLength', 0.5)), limb(21, 'First', 'Limb', p('Size', 3.0)), limb(22, 'Second', 'Root'), limb(23, 'Plain', 'LimbNode')]
    bone_links = [connect(10, 0), connect(11, 0), connect(12, 0), connect(13, 0), connect(20, 10), connect(20, 11), connect(20, 11), connect(21, 12), connect(22, 12), connect(23, 13), connect(30, 13)]
    yield 'bones_blender', scene(bones, bone_links, header('Blender (stable FBX IO) - 4.2.3 - 5.12.4'))
    yield 'bones_blender_short', scene(bones, bone_links, header('Blender - 2.79'))
    yield 'bones_ascii_creator', scene(bones, bone_links, header('Blender version 2.79 (sub 0)'))
    yield 'bones_sdk', scene(bones, bone_links, header('FBX SDK/FBX Plugins version 2020.3.4'))
    yield 'bones_sdk_build', scene(bones, bone_links, header('FBX SDK/FBX Plugins build 20200312'))
    yield 'bones_motionbuilder', scene(bones, bone_links, header('MotionBuilder/Mocap/Online version 7.0'))
    yield 'bones_unknown', scene(bones, bone_links, header('Some Exporter 1.0'))
    yield 'bones_no_header', scene(bones, bone_links)
    # poses: a non-bind pose (no node gets it, no cluster is filled), two bind poses naming one node (the first is the
    # node's, the first fills the cluster), a node named twice in one pose, entries without Node or Matrix (skipped),
    # a 20-value matrix (accepted), and a parent the pose does not name (bone_to_parent from its world matrix)
    poses = [model(10, 'Root', 'LimbNode', p('Lcl Translation', 0.0, 1.0, 0.0)), model(11, 'Child', 'LimbNode', p('Lcl Translation', 0.0, 1.0, 0.0)),
             model(12, 'Grandchild', 'LimbNode', p('Lcl Translation', 0.0, 1.0, 0.0)), limb(20, 'Attr'),
             model(1, 'Mesh', 'Mesh'), quad(), skin(40, 'Skin'), cluster(41, 'C', [0], [1.0]),
             pose(50, 'Rest', [(10, translation(5, 5, 5)), (12, translation(6, 6, 6))], 'Rest'),
             pose(51, 'First', [(12, translation(0, 3, 0)), (10, translation(0, 1, 0)), (12, translation(0, 4, 0)), (None, translation(7, 7, 7)), (11, None), (11, translation(0, 2, 0) + [9.0, 9.0, 9.0, 9.0])]),
             pose(52, 'Second', [(10, translation(1, 1, 1)), (11, translation(2, 2, 2))])]
    yield 'pose_variants', scene(poses, [connect(10, 0), connect(11, 10), connect(12, 11), connect(20, 10), connect(20, 11), connect(20, 12),
        connect(1, 0), connect(30, 1), connect(40, 30), connect(41, 40), connect(12, 41)])
    # instances: a Geometry connected to two nodes in reverse node order (its instances follow the file), a node
    # connected to two Geometries (the last is its mesh, both list it)
    yield 'instances_order', scene([model(1, 'A', 'Mesh'), model(2, 'B', 'Mesh'), quad(), polygon(31, 'Tri', [0, 0, 0, 1, 0, 0, 0, 1, 0], [[0, 1, 2]])],
        [connect(1, 0), connect(2, 0), connect(30, 2), connect(30, 1), connect(31, 1)])
    # Property-free OP/PO/PP connections are object links, too. PO/PP store the source property BEFORE the
    # destination ID. Exercise the entire graph and each encoding's texture property routing.
    for kind in ('OP', 'PO', 'PP'):
        def edge(src, dst, dst_prop=''):
            fields = [prop('S', kind), prop('L', src)]
            if kind in ('PO', 'PP'):
                fields.append(prop('S', ''))
            fields.append(prop('L', dst))
            if kind in ('OP', 'PP'):
                fields.append(prop('S', dst_prop))
            return Node('C', fields)
        objects = rig + [skin(40, 'Skin'), cluster(41, 'C', [0], [1.0]), pose(50, 'Pose', [(10, translation(0, 1, 0))]),
            obj('Material', 60, 'Mat', children=[Node('ShadingModel', [prop('S', 'phong')])]),
            obj('Texture', 70, 'Tex', children=[Node('RelativeFilename', [prop('S', 'tex.png')])])]
        yield 'skin_connections_' + kind.lower(), scene(objects, [edge(a, b) for a, b in
            [(1, 0), (10, 0), (11, 10), (30, 1), (20, 10), (21, 11), (41, 40), (40, 30), (10, 41), (60, 30)]] + [edge(70, 60, 'DiffuseColor')])
    # The synthetic root is a valid cluster bone, including bind-pose filling through its outgoing links.
    yield 'skin_root_bone', scene([model(1, 'Mesh', 'Mesh', p('Lcl Translation', 1.0, 2.0, 3.0)), quad(),
        skin(40, 'Skin'), cluster(41, 'C', [0], [1.0]), pose(50, 'Pose', [(0, translation(0, 2, 0))])],
        [connect(1, 0), connect(30, 1), connect(41, 40), connect(40, 30), connect(0, 41)])
    # Named property links are not deformers attached to a mesh.
    yield 'skin_named_property', scene(rig + [skin(40, 'Skin'), cluster(41, 'C', [0], [1.0])],
        links + [connect(41, 40), Node('C', [prop('S', 'OP'), prop('L', 40), prop('L', 30), prop('S', 'OtherProperty')]), connect(10, 41)])
    # More than one insertion-sort block, NaNs and equal weights: pin the reference merge order after
    # removing per-vertex scratch allocations from the common short-list path.
    many = [cluster(100 + i, 'C' + str(i), [0], [nan if i % 5 == 1 else (i * 7 % 11) / 10.0]) for i in range(45)]
    yield 'skin_many_weights', scene(rig + [skin(40, 'Skin')] + many,
        links + [connect(40, 30)] + [c for i in range(45) for c in (connect(100 + i, 40), connect(10, 100 + i))])
    # Invalid numeric property names are skipped, not converted to empty strings and then treated as bindings.
    malformed = [Node('C', [prop('S', 'OP'), prop('L', 10), prop('L', 100), prop('I', 17)]),
        Node('C', [prop('S', 'PO'), prop('L', 10), prop('I', 17), prop('L', 101)]),
        Node('C', [prop('S', 'PP'), prop('L', 10), prop('I', 17), prop('L', 102), prop('I', 17)])]
    yield 'skin_bad_property_fields', scene(rig + [skin(40, 'Skin')] + [cluster(i, 'C' + str(i), [0], [1.0]) for i in (100, 101, 102)],
        links + [connect(40, 30)] + [connect(i, 40) for i in (100, 101, 102)] + malformed)


KTIME = 46186158000


def ticks(seconds, ktime=KTIME):
    return int(round(seconds * ktime))


def pl(name, value, kind='KTime', sub='Time'):
    """A P with a 64-bit value (the stack's LocalStart/LocalStop are KTime ticks past 2^31)."""
    return Node('P', [prop('S', name), prop('S', kind), prop('S', sub), prop('S', ''), prop('L', value)])


def op(src, dst, name):
    return Node('C', [prop('S', 'OP'), prop('L', src), prop('L', dst), prop('S', name)])


def pp(src, src_prop, dst, dst_prop):
    return Node('C', [prop('S', 'PP'), prop('L', src), prop('S', src_prop), prop('L', dst), prop('S', dst_prop)])


def curve(ident, name, times, values, flags, attrs, refs, pre=None, post=None, time_type='l', value_type='d', attr_type='f', ktime=KTIME):
    """An AnimationCurve: KeyTime in seconds (written as ticks), KeyValueFloat, the run-length attribute arrays (one
    flag and four attribute floats per KeyAttrRefCount entry); pre/post = (extrapolation type letter[, repetition])."""
    children = []
    for tag, ext in (('Pre-Extrapolation', pre), ('Post-Extrapolation', post)):
        if ext is not None:
            kids = [Node('Type', [prop('I', ord(ext[0]))])]
            if len(ext) > 1:
                kids.append(Node('Repetition', [prop('I', ext[1])]))
            children.append(Node(tag, [], kids))
    children += [Node('Default', [prop('D', 0.0)]), Node('KeyVer', [prop('I', 4009)]),
                 Node('KeyTime', [prop(time_type, [ticks(t, ktime) for t in times])]), Node('KeyValueFloat', [prop(value_type, values)]),
                 Node('KeyAttrFlags', [prop('i', flags)]), Node('KeyAttrDataFloat', [prop(attr_type, attrs)]), Node('KeyAttrRefCount', [prop('i', refs)])]
    return obj('AnimationCurve', ident, name, '', children)


def packed_weights(right, next_left):
    """Two 0.4 decimal fixed-point weights in the bits of one float (KeyAttrDataFloat[2])."""
    import struct
    return struct.unpack('<f', struct.pack('<I', (int(round(right * 10000)) & 0xffff) | (int(round(next_left * 10000)) << 16)))[0]


def curve_node(ident, name, *defaults):
    ps = [p('d|' + axis, float(v)) for axis, v in zip('XYZ', defaults)]
    return obj('AnimationCurveNode', ident, name, '', [properties(*ps)] if ps else [])


def anim_layer(ident, name, *props):
    return obj('AnimationLayer', ident, name, '', [properties(*props)] if props else [])


def anim_stack(ident, name, start=None, stop=None, reference=False, *props):
    ps = list(props)
    if start is not None:
        ps += [pl('ReferenceStart' if reference else 'LocalStart', ticks(start)), pl('ReferenceStop' if reference else 'LocalStop', ticks(stop))]
    return obj('AnimationStack', ident, name, '', [properties(*ps)] if ps else [])


AUTO, LINEAR, CONST, CONST_NEXT, USER, TCB = 0x108, 0x4, 0x2, 0x102, 0x408, 0x208
CLAMP, TIME_INDEPENDENT, CLAMP_PROGRESSIVE, BROKEN, WEIGHTED_RIGHT, WEIGHTED_NEXT_LEFT = 0x1000, 0x2000, 0x4000, 0x800, 0x1000000, 0x2000000


def anim_fixtures():
    # step 5 (animation): one stack, one layer, a node whose translation, rotation and scale curves cover the three
    # interpolations (constant previous and next, linear, cubic with auto and user tangents), a one-key curve, a value
    # with no curves, a node without animation
    node = model(1, 'Anim', 'Null', p('Lcl Translation', 1.0, 2.0, 3.0), p('Lcl Rotation', 10.0, 20.0, 30.0), p('Lcl Scaling', 1.0, 1.0, 1.0))
    still = model(2, 'Still', 'Null', p('Lcl Translation', 4.0, 5.0, 6.0))
    base = [node, still, anim_stack(60, 'Take', 0.0, 2.0), anim_layer(61, 'Base')]
    links = [connect(1, 0), connect(2, 0), connect(61, 60)]
    zeros = [0.0, 0.0, 0.0, 0.0]
    curves = [curve_node(70, 'T', 1.0, 2.0, 3.0), curve_node(71, 'R', 0.0, 0.0, 0.0), curve_node(72, 'S', 1.0, 1.0, 1.0), curve_node(73, 'Empty', 7.0, 8.0, 9.0),
              curve(80, 'TX', [0.0, 1.0, 2.0], [0.0, 1.0, 3.0], [LINEAR], zeros, [3]),
              curve(81, 'TY', [0.0, 0.5, 1.5, 2.0], [0.0, 1.0, -1.0, 0.5], [AUTO], zeros, [4]),
              curve(82, 'TZ', [0.0, 0.4, 0.8, 1.2, 1.6, 2.0], [0.0, 1.0, 0.5, 2.0, -1.0, 0.0], [CONST, CONST_NEXT, LINEAR, AUTO | CLAMP, USER, AUTO],
                    zeros + zeros + zeros + zeros + [2.0, -1.0, 0.0, 0.0] + zeros, [1, 1, 1, 1, 1, 1]),
              curve(83, 'RZ', [0.0, 1.0, 2.0, 3.0], [0.0, 90.0, 180.0, 45.0], [AUTO], zeros, [4]),
              curve(84, 'SX', [1.0], [2.0], [AUTO], zeros, [1])]
    anim_links = [connect(70, 61), connect(71, 61), connect(72, 61), connect(73, 61), op(70, 1, 'Lcl Translation'), op(71, 1, 'Lcl Rotation'), op(72, 1, 'Lcl Scaling'), op(73, 1, 'RotationPivot'),
                  op(80, 70, 'd|X'), op(81, 70, 'd|Y'), op(82, 70, 'd|Z'), op(83, 71, 'd|Z'), op(84, 72, 'd|X')]
    yield 'anim_basic', scene(base + curves, links + anim_links)
    # tangent solvers the Blender files never write: TCB, auto with time-independent / clamp-progressive / a bias past
    # 500, user tangents weighted on either side and broken, equal consecutive key times, keys out of order
    tcb = [0.0, 0.0, 0.0, 0.0, 0.5, 0.0, 0.0, 0.0, 0.0, 0.5, -0.5, 0.0, 0.0, -0.3, 0.8, 0.0, 0.0, 0.0, 0.0, 0.0]
    curves = [curve_node(70, 'T', 0.0, 0.0, 0.0), curve_node(71, 'R', 0.0, 0.0, 0.0),
              curve(80, 'TX', [0.0, 0.5, 1.0, 1.5, 2.0], [0.0, 2.0, 1.0, 3.0, 0.0], [TCB] * 5, tcb, [1] * 5),
              curve(81, 'TY', [0.0, 0.5, 1.0, 1.5, 2.0], [0.0, 2.0, 1.0, 3.0, 0.0], [AUTO | TIME_INDEPENDENT, AUTO | CLAMP_PROGRESSIVE, AUTO, AUTO, AUTO | CLAMP],
                    zeros + zeros + [650.0, 0.0, 0.0, 0.0] + [-200.0, 0.0, 0.0, 0.0] + zeros, [1] * 5),
              curve(82, 'TZ', [0.0, 0.5, 1.0, 1.5, 2.0], [0.0, 2.0, 1.0, 3.0, 0.0], [USER | WEIGHTED_RIGHT, USER | WEIGHTED_NEXT_LEFT, USER | BROKEN, USER | WEIGHTED_RIGHT | WEIGHTED_NEXT_LEFT, USER],
                    [1.0, 0.5, packed_weights(0.25, 0.75), 0.0] + [-1.0, 2.0, packed_weights(0.1, 0.6), 0.0] + [3.0, -3.0, 0.0, 0.0] + [0.5, 0.5, packed_weights(0.9, 0.05), 0.0] + [1.0, 1.0, 0.0, 0.0], [1] * 5),
              curve(83, 'RX', [0.0, 1.0, 1.0, 2.0], [0.0, 10.0, 20.0, 30.0], [AUTO], zeros, [4]),
              curve(84, 'RY', [0.0, 2.0, 1.0, 3.0], [0.0, 10.0, 20.0, 30.0], [LINEAR], zeros, [4]),
              curve(85, 'RZ', [0.0, 0.5, 1.0, 1.5, 2.0], [0.0, 2.0, 1.0, 3.0, 0.0], [AUTO | CLAMP], zeros, [5])]
    anim_links = [connect(70, 61), connect(71, 61), op(70, 1, 'Lcl Translation'), op(71, 1, 'Lcl Rotation'),
                  op(80, 70, 'd|X'), op(81, 70, 'd|Y'), op(82, 70, 'd|Z'), op(83, 71, 'd|X'), op(84, 71, 'd|Y'), op(85, 71, 'd|Z')]
    yield 'anim_tangents', scene(base + curves, links + anim_links)
    # every extrapolation mode on both sides, sampled 0.5 and 2.3 spans beyond the keys: repeat, mirror (twice), slope,
    # relative repeat, constant, a repeat count of zero, a zero-length curve
    keys = ([0.0, 0.5, 1.0, 2.0], [0.0, 1.0, 3.0, 2.0], [AUTO], zeros, [4])
    curves = [curve_node(70, 'T', 0.0, 0.0, 0.0), curve_node(71, 'R', 0.0, 0.0, 0.0), curve_node(72, 'S', 1.0, 1.0, 1.0),
              curve(80, 'Repeat', *keys, pre=('R', -1), post=('R', 1)), curve(81, 'Mirror', *keys, pre=('M', 2), post=('M', -1)),
              curve(82, 'Slope', *keys, pre=('K',), post=('K', 3)), curve(83, 'Relative', *keys, pre=('A', -1), post=('A', 2)),
              curve(84, 'Constant', *keys, pre=('C', 5), post=('R', 0)), curve(85, 'Unknown', *keys, pre=('Z', 1), post=('M', 0)),
              curve(86, 'Point', [1.0, 1.0], [2.0, 3.0], [LINEAR], zeros, [2], pre=('R', -1), post=('M', -1)),
              curve(87, 'None', [], [], [], [], [], pre=('R', -1), post=('R', -1))]
    anim_links = [connect(70, 61), connect(71, 61), connect(72, 61), op(70, 1, 'Lcl Translation'), op(71, 1, 'Lcl Rotation'), op(72, 1, 'Lcl Scaling'),
                  op(80, 70, 'd|X'), op(81, 70, 'd|Y'), op(82, 70, 'd|Z'), op(83, 71, 'd|X'), op(84, 71, 'd|Y'), op(85, 71, 'd|Z'), op(86, 72, 'd|X'), op(87, 72, 'd|Y')]
    yield 'anim_extrapolation', scene(base + curves, links + anim_links)
    # layers: an override base, an additive layer at half weight composing rotations and scales, an override-passthrough
    # layer at 30% without composition, a layer whose weight is itself animated, an unknown blend mode; a second stack
    # with the same layers in another order (the first layer assigns, the others blend); rotation order YZX
    rot = model(1, 'Anim', 'Null', p('Lcl Translation', 1.0, 2.0, 3.0), p('Lcl Rotation', 10.0, 20.0, 30.0), p('Lcl Scaling', 2.0, 2.0, 2.0), p('RotationOrder', 2))
    layers = [anim_layer(61, 'Base', p('BlendMode', 1)), anim_layer(62, 'Add', p('BlendMode', 0), p('Weight', 50.0), p('RotationAccumulationMode', 0), p('ScaleAccumulationMode', 0)),
              anim_layer(63, 'Pass', p('BlendMode', 2), p('Weight', 30.0), p('RotationAccumulationMode', 1), p('ScaleAccumulationMode', 1)),
              anim_layer(64, 'Weighted', p('BlendMode', 2), p('Weight', 100.0)), anim_layer(65, 'Odd', p('BlendMode', 7), p('Weight', 120.0))]
    objects = [rot, still, anim_stack(60, 'Take', 0.0, 2.0), anim_stack(66, 'Reversed', 0.0, 2.0)] + layers
    conns = [connect(1, 0), connect(2, 0), connect(61, 60), connect(62, 60), connect(63, 60), connect(64, 60), connect(65, 60),
             connect(63, 66), connect(62, 66), connect(61, 66)]
    curves = []
    ident = 70
    for layer, scale in ((61, 1.0), (62, 2.0), (63, 3.0), (64, 4.0), (65, 5.0)):
        t, r, s = ident, ident + 1, ident + 2
        curves += [curve_node(t, 'T', 0.0, 0.0, 0.0), curve_node(r, 'R', 0.0, 0.0, 0.0), curve_node(s, 'S', 1.0, 1.0, 1.0),
                   curve(t + 10, 'TX', [0.0, 2.0], [0.0, scale], [LINEAR], zeros, [2]), curve(r + 10, 'RY', [0.0, 2.0], [0.0, 45.0 * scale], [LINEAR], zeros, [2]),
                   curve(s + 10, 'SZ', [0.0, 2.0], [1.0, scale], [LINEAR], zeros, [2])]
        conns += [connect(t, layer), connect(r, layer), connect(s, layer), op(t, 1, 'Lcl Translation'), op(r, 1, 'Lcl Rotation'), op(s, 1, 'Lcl Scaling'),
                  op(t + 10, t, 'd|X'), op(r + 10, r, 'd|Y'), op(s + 10, s, 'd|Z')]
        ident += 20
    curves += [curve_node(99, 'W', 100.0), curve(98, 'Weight', [0.0, 2.0], [0.0, 100.0], [LINEAR], zeros, [2])]
    conns += [connect(99, 64), op(99, 64, 'Weight'), op(98, 99, 'd|X')]
    yield 'anim_layers', scene(objects + curves, conns)
    # connected properties: a node driven by another's animated rotation (PP), a two-hop chain, a cycle that leaves the
    # node to its own animation, an animated property the node does not store (synthesised: scale ones, visibility
    # zero), an animated rotation order read through the evaluation, a value connected to a material property
    nodes = [model(1, 'Driven', 'Null', p('Lcl Rotation', 1.0, 1.0, 1.0)), model(2, 'Driver', 'Null', p('Lcl Rotation', 5.0, 6.0, 7.0)),
             model(3, 'Cycle', 'Null', p('Lcl Translation', 1.0, 1.0, 1.0)), model(4, 'Loop', 'Null', p('Lcl Translation', 2.0, 2.0, 2.0)),
             model(5, 'Chain', 'Null', p('Lcl Scaling', 1.0, 1.0, 1.0)), model(6, 'Link', 'Null', p('Lcl Scaling', 2.0, 2.0, 2.0)), model(7, 'Source', 'Null', p('Lcl Scaling', 3.0, 3.0, 3.0)),
             model(8, 'Missing', 'Null'), model(9, 'Ordered', 'Null', p('Lcl Rotation', 10.0, 20.0, 30.0)),
             obj('Material', 40, 'Mat', children=[Node('ShadingModel', [prop('S', 'phong')])])]
    objects = nodes + [anim_stack(60, 'Take', 0.0, 2.0), anim_layer(61, 'Base', p('BlendMode', 1)), anim_layer(62, 'Add', p('BlendMode', 0), p('Weight', 50.0))]
    conns = [connect(n, 0) for n in range(1, 10)] + [connect(61, 60), connect(62, 60),
             pp(2, 'Lcl Rotation', 1, 'Lcl Rotation'), pp(4, 'Lcl Translation', 3, 'Lcl Translation'), pp(3, 'Lcl Translation', 4, 'Lcl Translation'),
             pp(6, 'Lcl Scaling', 5, 'Lcl Scaling'), pp(7, 'Lcl Scaling', 6, 'Lcl Scaling')]
    curves = [curve_node(70, 'R', 0.0, 0.0, 0.0), curve(80, 'RX', [0.0, 2.0], [0.0, 90.0], [LINEAR], zeros, [2]),
              curve_node(71, 'T', 0.0, 0.0, 0.0), curve(81, 'TX', [0.0, 2.0], [0.0, 4.0], [LINEAR], zeros, [2]),
              curve_node(72, 'S', 1.0, 1.0, 1.0), curve(82, 'SX', [0.0, 2.0], [1.0, 5.0], [LINEAR], zeros, [2]),
              curve_node(73, 'S', 1.0, 1.0, 1.0), curve(83, 'SY', [0.0, 2.0], [1.0, 6.0], [LINEAR], zeros, [2]),
              curve_node(74, 'V', 1.0), curve(84, 'VX', [0.0, 2.0], [1.0, 0.0], [LINEAR], zeros, [2]),
              curve_node(75, 'O', 4.0), curve(85, 'OX', [0.0, 2.0], [4.0, 1.0], [CONST], zeros, [2]),
              curve_node(76, 'R', 0.0, 0.0, 0.0), curve(86, 'RZ', [0.0, 2.0], [0.0, 60.0], [LINEAR], zeros, [2]),
              curve_node(77, 'R', 0.0, 0.0, 0.0), curve(87, 'RX', [0.0, 2.0], [0.0, 30.0], [LINEAR], zeros, [2]),
              curve_node(78, 'D', 0.5, 0.5, 0.5), curve(88, 'DX', [0.0, 2.0], [0.0, 1.0], [LINEAR], zeros, [2]),
              curve_node(79, 'R', 0.0, 0.0, 0.0), curve(89, 'RY', [0.0, 2.0], [0.0, 90.0], [LINEAR], zeros, [2])]
    conns += [connect(70, 61), op(70, 2, 'Lcl Rotation'), op(80, 70, 'd|X'), connect(71, 61), op(71, 3, 'Lcl Translation'), op(81, 71, 'd|X'),
              connect(72, 61), op(72, 7, 'Lcl Scaling'), op(82, 72, 'd|X'), connect(73, 61), op(73, 8, 'Lcl Scaling'), op(83, 73, 'd|Y'),
              connect(74, 61), op(74, 8, 'Visibility'), op(84, 74, 'd|X'), connect(75, 61), op(75, 9, 'RotationOrder'), op(85, 75, 'd|X'),
              connect(76, 61), op(76, 9, 'Lcl Rotation'), op(86, 76, 'd|Z'), connect(77, 62), op(77, 9, 'Lcl Rotation'), op(87, 77, 'd|X'),
              connect(78, 61), op(78, 40, 'DiffuseColor'), op(88, 78, 'd|X'), connect(79, 61), op(79, 1, 'Lcl Rotation'), op(89, 79, 'd|Y')]
    yield 'anim_connected', scene(objects + curves, conns)
    # the same with a Model template: the synthesised properties take the template's values
    template = Node('Definitions', [], [Node('ObjectType', [prop('S', 'Model')], [
        Node('PropertyTemplate', [prop('S', 'FbxNode')], [properties(p('Lcl Scaling', 3.0, 3.0, 3.0), p('Visibility', 1.0), p('Lcl Translation', 0.0, 0.0, 0.0))])])])
    yield 'anim_connected_template', scene(objects + curves, conns, [template])
    # stacks and values in odd shapes: a reference range, no range (zero span, the samples a second apart), no layers,
    # a layer connected twice, a value connected twice, two values on one property (the first connected wins), a curve
    # on "Y" and another on "d|Y" (the later-sorted wins), a
    # curve on an unknown axis name (index 0), a value without default properties, 32-bit key times (under 46 ms), float values,
    # double attributes (narrowed to float), a layer with no values, a curve of signed zeros
    nodes = [model(1, 'Anim', 'Null', p('Lcl Translation', 1.0, 2.0, 3.0))]
    objects = nodes + [anim_stack(60, 'Reference', 0.5, 1.5, True), anim_stack(66, 'Empty'), anim_stack(67, 'NoLayers', 0.0, 1.0),
                       anim_layer(61, 'Twice'), anim_layer(62, 'Bare'),
                       curve_node(70, 'T', 1.0, 2.0, 3.0), curve_node(71, 'R'), curve_node(72, 'T2', 9.0, 9.0, 9.0),
                       curve(80, 'Y', [0.0, 1.0], [0.0, 1.0], [LINEAR], zeros, [2]), curve(81, 'dY', [0.0, 1.0], [0.0, 2.0], [LINEAR], zeros, [2]),
                       curve(82, 'W', [0.0, 0.04], [0.0, 3.0], [LINEAR], zeros, [2], time_type='i', value_type='f', attr_type='d'),
                       curve(83, 'RX', [0.0, 0.04], [0.0, 4.0], [AUTO, AUTO], [0.25, 0.5, 0.75, 1.0] * 2, [1, 1], time_type='i', value_type='f', attr_type='d'),
                       # signed zeros compare equal: ufbx's max keeps the FIRST of a tie (+0), its min the LAST (-0); a linear
                       # tangent dy of -0 stays -0
                       curve(84, 'Zeros', [0.0, 1.0], [0.0, -0.0], [LINEAR], zeros, [2])]
    conns = [connect(1, 0), connect(61, 60), connect(61, 60), connect(62, 66), connect(61, 66), connect(70, 61), connect(70, 61), connect(71, 61), connect(72, 61),
             op(70, 1, 'Lcl Translation'), op(71, 1, 'Lcl Rotation'), op(72, 1, 'Lcl Translation'), op(80, 70, 'Y'), op(81, 70, 'd|Y'), op(82, 70, 'd|W'), op(83, 71, 'd|X'), op(84, 71, 'd|Z')]
    yield 'anim_shapes', scene(objects, conns)
    # the FBX 8000 time code: FBXHeaderVersion 1004 with TCDefinition 128 switches the KTime base, 127 keeps it
    for name, tc in (('anim_ktime_new', 128), ('anim_ktime_old', 127)):
        head = [Node('FBXHeaderExtension', [], [Node('FBXHeaderVersion', [prop('I', 1004)]), Node('FBXVersion', [prop('I', 7400)]),
                                               Node('OtherFlags', [], [Node('TCDefinition', [prop('I', tc)])])])]
        objects = nodes + [anim_stack(60, 'Take', 0.0, 2.0), anim_layer(61, 'Base'), curve_node(70, 'T', 1.0, 2.0, 3.0),
                           curve(80, 'TX', [0.0, 1.0, 2.0], [0.0, 1.0, 3.0], [AUTO], zeros, [3], pre=('R', -1), post=('R', -1))]
        yield name, scene(objects, [connect(1, 0), connect(61, 60), connect(70, 61), op(70, 1, 'Lcl Translation'), op(80, 70, 'd|X')], head)


def anim_review_fixtures():
    # A template exists, but does not declare Scaling. ufbx keeps the synthetic zeros without NO_VALUE,
    # for both animated and connected properties (built-in ones are used only without a template).
    template = Node('Definitions', [], [Node('ObjectType', [prop('S', 'Model')], [
        Node('PropertyTemplate', [prop('S', 'FbxNode')], [properties(p('Other', 9.0))])])])
    for connected in (False, True):
        objects = [model(1, 'Node', 'Null'), model(2, 'Driver', 'Null', p('Lcl Scaling', 2.0, 3.0, 4.0)),
            anim_stack(60, 'Take', 0.0, 1.0), anim_layer(61, 'Base'), curve_node(70, 'S', 2.0, 3.0, 4.0)]
        binding = pp(2, 'Lcl Scaling', 1, 'Lcl Scaling') if connected else op(70, 1, 'Lcl Scaling')
        yield 'anim_template_missing_' + ('connected' if connected else 'animated'), scene(objects,
            [connect(1, 0), connect(2, 0), connect(61, 60), connect(70, 61), binding], [template])
    # ID zero is a valid property source. A missing root property evaluates to zero, overriding the stored value.
    yield 'anim_root_driver', scene([model(1, 'Node', 'Null', p('Lcl Translation', 1.0, 2.0, 3.0)),
        anim_stack(60, 'Take', 0.0, 1.0), anim_layer(61, 'Base')],
        [connect(1, 0), connect(61, 60), pp(0, 'Nothing', 1, 'Lcl Translation')])
    # The same four-byte name key, then BMP private-use and a supplementary character: UTF-8 and UTF-16
    # ordinal ordering differ. The reader and both dumps must use UTF-8 order.
    yield 'anim_unicode_properties', scene([model(1, 'Node', 'Null'), anim_stack(60, 'Take', 0.0, 1.0),
        anim_layer(61, 'Base'), curve_node(70, 'V', 1.0), curve_node(71, 'W', 2.0)],
        [connect(1, 0), connect(61, 60), connect(70, 61), connect(71, 61), op(70, 1, 'aaaa\ue000'), op(71, 1, 'aaaa\U0001f600')])


def bad_fixtures():
    yield 'cycle_self', scene([obj('Model', 1, 'Self', 'Null')], [connect(1, 1)])
    yield 'cycle_pair', scene([obj('Model', 1, 'One', 'Null'), obj('Model', 2, 'Two', 'Null')], [connect(1, 2), connect(2, 1)])
    # step 4: a cluster whose index and weight counts differ, a pose matrix and a cluster transform under 16 values
    yield 'cluster_mismatch', scene([model(1, 'Mesh', 'Mesh'), quad(), skin(40, 'Skin'), cluster(41, 'C', [0, 1, 2], [1.0, 1.0])],
        [connect(1, 0), connect(30, 1), connect(40, 30), connect(41, 40)])
    yield 'pose_short_matrix', scene([model(10, 'Root', 'LimbNode'), pose(50, 'BindPose', [(10, [1.0] * 12)])], [connect(10, 0)])
    yield 'cluster_short_transform', scene([model(1, 'Mesh', 'Mesh'), quad(), skin(40, 'Skin'), cluster(41, 'C', [0], [1.0], [1.0] * 9, translation(0, 0, 0))],
        [connect(1, 0), connect(30, 1), connect(40, 30), connect(41, 40)])
    # step 5: a curve whose times and values differ in length, attributes short of four per entry, reference counts
    # that run out before the keys, a zero reference count, a curve without KeyAttrRefCount
    zeros = [0.0, 0.0, 0.0, 0.0]
    yield 'anim_curve_mismatch', scene([curve(80, 'C', [0.0, 1.0, 2.0], [0.0, 1.0], [LINEAR], zeros, [3])], [])
    yield 'anim_curve_short_attrs', scene([curve(80, 'C', [0.0, 1.0], [0.0, 1.0], [LINEAR], [0.0, 0.0, 0.0], [2])], [])
    yield 'anim_curve_refs_exhausted', scene([curve(80, 'C', [0.0, 1.0, 2.0], [0.0, 1.0, 2.0], [LINEAR], zeros, [2])], [])
    yield 'anim_curve_refs_zero', scene([curve(80, 'C', [0.0, 1.0], [0.0, 1.0], [LINEAR, LINEAR], zeros + zeros, [0, 2])], [])
    yield 'anim_curve_no_refs', scene([obj('AnimationCurve', 80, 'C', '', [Node('KeyTime', [prop('l', [0])]), Node('KeyValueFloat', [prop('d', [1.0])]),
        Node('KeyAttrFlags', [prop('i', [LINEAR])]), Node('KeyAttrDataFloat', [prop('f', zeros)])])], [])


if __name__ == '__main__':
    sys.stdout.reconfigure(encoding='utf-8')
    reject = sys.argv[1] == '--bad'
    out = Path(sys.argv[2] if reject else sys.argv[1]); out.mkdir(parents=True, exist_ok=True)
    def good():
        yield from fixtures()
        yield from triangulation_fixtures()
        yield from skin_fixtures()
        yield from anim_fixtures()
        yield from anim_review_fixtures()
    for name, nodes in (bad_fixtures() if reject else good()):
        path = out / (name + '.fbx')
        write_fbx(path, 7400, nodes)
        print(path.as_posix())
