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


def bad_fixtures():
    yield 'cycle_self', scene([obj('Model', 1, 'Self', 'Null')], [connect(1, 1)])
    yield 'cycle_pair', scene([obj('Model', 1, 'One', 'Null'), obj('Model', 2, 'Two', 'Null')], [connect(1, 2), connect(2, 1)])


if __name__ == '__main__':
    sys.stdout.reconfigure(encoding='utf-8')
    reject = sys.argv[1] == '--bad'
    out = Path(sys.argv[2] if reject else sys.argv[1]); out.mkdir(parents=True, exist_ok=True)
    def good():
        yield from fixtures()
        yield from triangulation_fixtures()
    for name, nodes in (bad_fixtures() if reject else good()):
        path = out / (name + '.fbx')
        write_fbx(path, 7400, nodes)
        print(path.as_posix())
