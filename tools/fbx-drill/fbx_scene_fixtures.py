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


def bad_fixtures():
    yield 'cycle_self', scene([obj('Model', 1, 'Self', 'Null')], [connect(1, 1)])
    yield 'cycle_pair', scene([obj('Model', 1, 'One', 'Null'), obj('Model', 2, 'Two', 'Null')], [connect(1, 2), connect(2, 1)])
    # step 4: a cluster whose index and weight counts differ, a pose matrix and a cluster transform under 16 values
    yield 'cluster_mismatch', scene([model(1, 'Mesh', 'Mesh'), quad(), skin(40, 'Skin'), cluster(41, 'C', [0, 1, 2], [1.0, 1.0])],
        [connect(1, 0), connect(30, 1), connect(40, 30), connect(41, 40)])
    yield 'pose_short_matrix', scene([model(10, 'Root', 'LimbNode'), pose(50, 'BindPose', [(10, [1.0] * 12)])], [connect(10, 0)])
    yield 'cluster_short_transform', scene([model(1, 'Mesh', 'Mesh'), quad(), skin(40, 'Skin'), cluster(41, 'C', [0], [1.0], [1.0] * 9, translation(0, 0, 0))],
        [connect(1, 0), connect(30, 1), connect(40, 30), connect(41, 40)])


if __name__ == '__main__':
    sys.stdout.reconfigure(encoding='utf-8')
    reject = sys.argv[1] == '--bad'
    out = Path(sys.argv[2] if reject else sys.argv[1]); out.mkdir(parents=True, exist_ok=True)
    def good():
        yield from fixtures()
        yield from triangulation_fixtures()
        yield from skin_fixtures()
    for name, nodes in (bad_fixtures() if reject else good()):
        path = out / (name + '.fbx')
        write_fbx(path, 7400, nodes)
        print(path.as_posix())
