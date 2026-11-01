"""Static FBX scenes for tools/fbx_model_drill.sh: the shapes the real files lack, complete enough for Blender's importer
(GlobalSettings, Definitions, Objects, Connections). The oracle is Blender itself (fbx_to_glb.py)."""
from pathlib import Path
import math
import sys
from fbx_fixtures import Node, prop, write_fbx


def P(name, kind, sub, *values):
    def value(v):
        return prop('S', v) if isinstance(v, str) else prop('D', v) if isinstance(v, float) else prop('I', v)
    return Node('P', [prop('S', name), prop('S', kind), prop('S', sub), prop('S', '')] + [value(v) for v in values])


def vec(name, x, y, z, kind='Vector3D', sub='Vector'):
    return P(name, kind, sub, float(x), float(y), float(z))


def properties(*ps):
    return Node('Properties70', [], list(ps))


def obj(kind, ident, name, subtype='', children=()):
    return Node(kind, [prop('L', ident), prop('S', name + '\0\1' + kind), prop('S', subtype)], list(children))


def connect(src, dst):
    return Node('C', [prop('S', 'OO'), prop('L', src), prop('L', dst)])


def header(up=(1, 1), front=(2, 1), coord=(0, 1), unit=1.0, creator='fbx_model_fixtures'):
    """Y up, -Z forward (the FBX SDK's default frame), centimetres."""
    settings = properties(P('UpAxis', 'int', 'Integer', up[0]), P('UpAxisSign', 'int', 'Integer', up[1]),
                          P('FrontAxis', 'int', 'Integer', front[0]), P('FrontAxisSign', 'int', 'Integer', front[1]),
                          P('CoordAxis', 'int', 'Integer', coord[0]), P('CoordAxisSign', 'int', 'Integer', coord[1]),
                          P('UnitScaleFactor', 'double', 'Number', float(unit)), P('OriginalUnitScaleFactor', 'double', 'Number', float(unit)),
                          P('TimeMode', 'enum', '', 11), P('CustomFrameRate', 'double', 'Number', 24.0))
    model_template = Node('PropertyTemplate', [prop('S', 'FbxNode')], [properties(
        vec('Lcl Translation', 0, 0, 0, 'Lcl Translation', ''), vec('Lcl Rotation', 0, 0, 0, 'Lcl Rotation', ''), vec('Lcl Scaling', 1, 1, 1, 'Lcl Scaling', ''),
        P('RotationActive', 'bool', '', 0), P('RotationOrder', 'enum', '', 0), P('Visibility', 'Visibility', '', 1.0))])
    return [Node('FBXHeaderExtension', [], [Node('FBXHeaderVersion', [prop('I', 1003)]), Node('FBXVersion', [prop('I', 7400)]), Node('Creator', [prop('S', creator)])]),
            Node('GlobalSettings', [], [Node('Version', [prop('I', 1000)]), settings]),
            Node('Definitions', [], [Node('Version', [prop('I', 100)]), Node('ObjectType', [prop('S', 'Model')], [model_template]),
                                     Node('ObjectType', [prop('S', 'Geometry')], []), Node('ObjectType', [prop('S', 'Material')], [])])]


def scene(objects, connections, head=None):
    return (head or header()) + [Node('Objects', [], list(objects)), Node('Connections', [], list(connections))]


def model(ident, name, *props):
    return obj('Model', ident, name, 'Mesh', [properties(*props)] if props else [])


def material(ident, name):
    return obj('Material', ident, name, '', [Node('Version', [prop('I', 102)]), Node('ShadingModel', [prop('S', 'phong')]),
                                             properties(vec('DiffuseColor', 0.8, 0.8, 0.8, 'Color', ''))])


def layer(kind, data_name, mapping, reference, values, index=None, index_name=None, name='', typ='d'):
    children = [Node('Version', [prop('I', 101)]), Node('Name', [prop('S', name)]), Node('MappingInformationType', [prop('S', mapping)]),
                Node('ReferenceInformationType', [prop('S', reference)]), Node(data_name, [prop(typ, values)])]
    if index is not None:
        children.append(Node(index_name, [prop('i', index)]))
    return Node('LayerElement' + kind, [prop('I', 0)], children)


def geometry(ident, name, vertices, faces, layers=(), edges=None):
    """Vertices flat xyz; faces as lists of vertex indices (the last negated-complemented)."""
    idx = []
    for face in faces:
        idx += list(face[:-1]) + [~face[-1]]
    children = [Node('Vertices', [prop('d', [float(v) for v in vertices])]), Node('PolygonVertexIndex', [prop('i', idx)])]
    if edges is not None:
        children.append(Node('Edges', [prop('i', edges)]))
    children.append(Node('GeometryVersion', [prop('I', 124)]))
    children += list(layers)
    children.append(Node('Layer', [prop('I', 0)], [Node('Version', [prop('I', 100)])] + [
        Node('LayerElement', [], [Node('Type', [prop('S', l.name)]), Node('TypedIndex', [prop('I', 0)])]) for l in layers]))
    return obj('Geometry', ident, name, 'Mesh', children)


def face_normals(vertices, faces, hard_pairs=()):
    """Per-corner normals: each corner takes its face's Newell normal (a hard edge everywhere)."""
    out = []
    for face in faces:
        nx = ny = nz = 0.0
        prev = face[-1]
        for v in face:
            px, py, pz = vertices[3 * prev:3 * prev + 3]
            cx, cy, cz = vertices[3 * v:3 * v + 3]
            nx += (py - cy) * (pz + cz); ny += (pz - cz) * (px + cx); nz += (px - cx) * (py + cy)
            prev = v
        n = math.sqrt(nx * nx + ny * ny + nz * nz) or 1.0
        for v in face:
            out += [nx / n, ny / n, nz / n]
    return out


CUBE_V = [-1, -1, -1, 1, -1, -1, 1, 1, -1, -1, 1, -1, -1, -1, 1, 1, -1, 1, 1, 1, 1, -1, 1, 1]
CUBE_F = [[0, 1, 2, 3], [4, 7, 6, 5], [0, 4, 5, 1], [1, 5, 6, 2], [2, 6, 7, 3], [3, 7, 4, 0]]


def fixtures():
    # polygons of every kind in one mesh: a triangle, a convex quad, an arrowhead quad (the 0-2 split flips), a pentagon, an
    # L-shaped hexagon; two material slots by polygon; UVs by corner through an index; normals per corner from the faces
    # (every edge hard); smoothing by polygon with one smooth face
    V = [0, 0, 0, 2, 0, 0, 1, 2, 0,                     # triangle
         3, 0, 0, 5, 0, 0, 5, 2, 0, 3, 2, 0,            # quad
         6, 0, 0, 8, 1, 0, 10, 0, 0, 8, 3, 0,           # an arrowhead quad: corner 1 inside, so the 0-2 split flips to 1-3
         9, 0, 0, 11, 0, 0, 12, 1.5, 0, 10, 3, 0, 8.5, 1.5, 0,   # pentagon
         13, 0, 0, 16, 0, 0, 16, 1, 0, 14, 1, 0, 14, 3, 0, 13, 3, 0]  # L
    F = [[0, 1, 2], [3, 4, 5, 6], [7, 8, 9, 10], [11, 12, 13, 14, 15], [16, 17, 18, 19, 20, 21]]
    uv = [0.0, 0.0, 1.0, 0.0, 1.0, 1.0, 0.0, 1.0, 0.5, 0.5]
    uv_index = [0, 1, 2] + [0, 1, 2, 3] + [0, 1, 2, 3] + [0, 1, 2, 3, 4] + [0, 1, 2, 3, 4, 1]
    layers = [layer('Material', 'Materials', 'ByPolygon', 'IndexToDirect', [0, 1, 0, 1, 0], typ='i'),
              layer('UV', 'UV', 'ByPolygonVertex', 'IndexToDirect', uv, uv_index, 'UVIndex', 'map1'),
              layer('Normal', 'Normals', 'ByPolygonVertex', 'Direct', face_normals(V, F)),
              layer('Smoothing', 'Smoothing', 'ByPolygon', 'Direct', [0, 1, 0, 0, 0], typ='i')]
    yield 'polygons', scene([model(1, 'Polygons'), geometry(30, 'PolygonsGeo', V, F, layers), material(40, 'MatA'), material(41, 'MatB')],
                            [connect(1, 0), connect(30, 1), connect(40, 1), connect(41, 1)])
    # a cube with an Edges array and ByEdge smoothing (two sharp edges), normals ByVertice (shared across faces), UVs ByVertice
    edges = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11]   # one entry per corner of the first three faces: those faces' edges
    vnorm = []
    for i in range(8):
        x, y, z = CUBE_V[3 * i:3 * i + 3]
        n = math.sqrt(3.0)
        vnorm += [x / n, y / n, z / n]
    layers = [layer('Smoothing', 'Smoothing', 'ByEdge', 'Direct', [1, 0, 1, 1, 1, 1, 0, 1, 1, 1, 1, 1], typ='i'),
              layer('Normal', 'Normals', 'ByVertice', 'Direct', vnorm),
              layer('UV', 'UV', 'ByVertice', 'Direct', [0.1 * i for i in range(16)], name='byvert')]
    yield 'cube_edges', scene([model(1, 'Cube'), geometry(30, 'CubeGeo', CUBE_V, CUBE_F, layers, edges), material(40, 'Mat')],
                              [connect(1, 0), connect(30, 1), connect(40, 1)])
    # transforms: pivots, offsets, pre/post rotation under RotationActive, every rotation order, geometric transforms, a
    # non-uniform scale, a three-deep chain, siblings whose names sort by BLI_strcasecmp (case folded, byte order)
    cube = geometry(30, 'CubeGeo', CUBE_V, CUBE_F, [layer('Normal', 'Normals', 'ByPolygonVertex', 'Direct', face_normals(CUBE_V, CUBE_F))])
    objects = [cube, material(40, 'Mat'),
               model(1, 'Root', vec('Lcl Translation', 10, 20, 30), vec('Lcl Rotation', 10, 20, 30), vec('Lcl Scaling', 1, 2, 3)),
               model(2, 'b_child', vec('Lcl Translation', 1, 0, 0), vec('Lcl Rotation', 0, 45, 0), vec('RotationPivot', 1, 1, 1), vec('ScalingPivot', 2, 0, 0), vec('RotationOffset', 0, 1, 0), vec('ScalingOffset', 0, 0, 1), vec('Lcl Scaling', 2, 2, 2)),
               model(3, 'A_child', vec('Lcl Rotation', 30, 40, 50), P('RotationActive', 'bool', '', 1), vec('PreRotation', 90, 0, 0), vec('PostRotation', 0, 0, 15), P('RotationOrder', 'enum', '', 4)),
               model(4, 'a_child', vec('Lcl Rotation', 30, 40, 50), P('RotationActive', 'bool', '', 1), P('RotationOrder', 'enum', '', 5), vec('GeometricTranslation', 0, 5, 0), vec('GeometricRotation', 0, 0, 90), vec('GeometricScaling', 1, 1, 0.5)),
               model(5, 'grandchild', vec('Lcl Translation', 0, 0, 7), vec('Lcl Rotation', 30, 40, 50), P('RotationActive', 'bool', '', 1), P('RotationOrder', 'enum', '', 1)),
               model(6, 'order2', vec('Lcl Rotation', 30, 40, 50), P('RotationActive', 'bool', '', 1), P('RotationOrder', 'enum', '', 2)),
               model(7, 'order3', vec('Lcl Rotation', 30, 40, 50), P('RotationActive', 'bool', '', 1), P('RotationOrder', 'enum', '', 3)),
               model(8, 'inactive_pre', vec('Lcl Rotation', 30, 40, 50), vec('PreRotation', 90, 0, 0), P('RotationOrder', 'enum', '', 5))]
    conns = [connect(1, 0), connect(2, 1), connect(3, 1), connect(4, 1), connect(5, 2), connect(6, 0), connect(7, 0), connect(8, 0)]
    conns += [connect(30, n) for n in range(1, 9)] + [connect(40, n) for n in range(1, 9)]
    yield 'transforms', scene(objects, conns)
    # a Z-up file in metres (unit 100) and a Y-up one in inches (2.54)
    tri = geometry(30, 'Tri', [0, 0, 0, 1, 0, 0, 0, 1, 0], [[0, 1, 2]], [layer('Normal', 'Normals', 'ByPolygonVertex', 'Direct', [0, 0, 1] * 3)])
    yield 'zup_metres', scene([model(1, 'Tri', vec('Lcl Translation', 1, 2, 3), vec('Lcl Rotation', 0, 0, 30)), tri], [connect(1, 0), connect(30, 1)],
                              header(up=(2, 1), front=(1, -1), coord=(0, 1), unit=100.0))
    yield 'yup_inches', scene([model(1, 'Tri', vec('Lcl Translation', 1, 2, 3)), tri], [connect(1, 0), connect(30, 1)], header(unit=2.54))
    # names: an empty geometry name, a name over 63 bytes, two Models named Cube (Cube, Cube.001), "Cube.001" before "Cube",
    # one geometry under two Models with different materials (object slots), a Model connected to two parents (the last wins)
    long_name = 'L' * 70
    objects = [geometry(30, '', CUBE_V, CUBE_F), geometry(31, long_name, [0, 0, 0, 1, 0, 0, 0, 1, 0], [[0, 1, 2]]),
               model(1, 'Cube.001'), model(2, 'Cube'), model(3, 'Cube'), model(4, long_name), model(5, 'Shared'), model(6, 'Twice'), model(7, 'ParentA'), model(8, 'ParentB'),
               # siblings the exporter orders by BLI_strcasecmp: a_part before B_part (case folded), é_part last (unsigned UTF-8 bytes)
               model(9, 'B_part'), model(10, 'a_part'), model(11, 'é_part'),
               # digits: natural order would put part9 before part10 and Part2 between; plain byte order does not
               model(12, 'part10'), model(13, 'part9'), model(14, 'Part2'), model(15, 'part'),
               material(40, 'MatA'), material(41, 'MatB')]
    conns = [connect(1, 0), connect(2, 0), connect(3, 0), connect(4, 0), connect(5, 0), connect(7, 0), connect(8, 0), connect(6, 7), connect(6, 8), connect(9, 7), connect(10, 7), connect(11, 7), connect(12, 8), connect(13, 8), connect(14, 8), connect(15, 8),
             connect(30, 1), connect(30, 2), connect(30, 3), connect(31, 4), connect(30, 5), connect(31, 6), connect(40, 2), connect(41, 5), connect(40, 5)]
    yield 'names', scene(objects, conns)
    # degenerate polygons: a repeated vertex, a reversed duplicate, two corners; a mesh without faces (a node without a mesh);
    # normals and materials AllSame; a UV index out of range (that corner keeps the default)
    V = [0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0, 2, 0, 0]
    F = [[0, 1, 2, 3], [0, 1, 1, 2], [3, 2, 1, 0], [1, 4], [1, 4, 2]]
    layers = [layer('Normal', 'Normals', 'AllSame', 'IndexToDirect', [0, 0, 1]), layer('Material', 'Materials', 'AllSame', 'IndexToDirect', [0], typ='i'),
              layer('UV', 'UV', 'ByPolygonVertex', 'IndexToDirect', [0.25, 0.75, 1.0, 0.0], [0, 1, 0, 1, 0, 1, 1, 0, 0, 1, 0, 1, 0, 1, 9, 0, 1], 'UVIndex')]
    objects = [model(1, 'Degenerate'), geometry(30, 'Deg', V, F, layers), model(2, 'NoFaces'), geometry(31, 'Points', [0, 0, 0, 1, 1, 1], []), material(40, 'Mat')]
    yield 'degenerate', scene(objects, [connect(1, 0), connect(2, 0), connect(30, 1), connect(31, 2), connect(40, 1)])
    # normals by polygon (flat), by vertex through an index, and per corner with SMOOTH shading (the custom normals split the
    # fans only where they differ: two faces of the cube share their corner normals, the rest are hard)
    flat = geometry(30, 'Flat', CUBE_V, CUBE_F, [layer('Normal', 'Normals', 'ByPolygon', 'Direct', face_normals(CUBE_V, [[f[0], f[1], f[2]] for f in CUBE_F])[0::3][:0] + [n for f in CUBE_F for n in face_normals(CUBE_V, [f])[:3]])])
    vidx = geometry(31, 'VertIndexed', CUBE_V, CUBE_F, [layer('Normal', 'Normals', 'ByVertice', 'IndexToDirect', [0, 0, 1, 0, 0, -1], [0, 1, 0, 1, 0, 1, 0, 1], 'NormalsIndex')])
    mixed = []
    for fi, f in enumerate(CUBE_F):
        for v in f:
            if fi < 2:
                x, y, z = CUBE_V[3 * v:3 * v + 3]; n = math.sqrt(3.0); mixed += [x / n, y / n, z / n]
            else:
                mixed += face_normals(CUBE_V, [f])[:3]
    smooth = geometry(32, 'Mixed', CUBE_V, CUBE_F, [layer('Normal', 'Normals', 'ByPolygonVertex', 'Direct', mixed), layer('Smoothing', 'Smoothing', 'ByPolygon', 'Direct', [1] * 6, typ='i')])
    objects = [model(1, 'Flat'), flat, model(2, 'VertIndexed', vec('Lcl Translation', 3, 0, 0)), vidx, model(3, 'Mixed', vec('Lcl Translation', 6, 0, 0)), smooth, material(40, 'Mat')]
    yield 'normals', scene(objects, [connect(1, 0), connect(2, 0), connect(3, 0), connect(30, 1), connect(31, 2), connect(32, 3), connect(40, 1), connect(40, 2), connect(40, 3)])

    # Blender chooses the last parent (including root) before evaluating: the final root link breaks an earlier cycle.
    yield 'late_root', scene([model(1, 'A'), model(2, 'B')], [connect(1, 2), connect(2, 1), connect(1, 0)])
    # mesh.validate removes the bad face, preserving the valid triangle. ufbx's pre-validation would reject the file.
    vertices = [0, 0, 0, 1, 0, 0, 0, 1, 0]
    yield 'invalid_face', scene([model(1, 'Node'), geometry(30, 'Geo', vertices, [[0, 1, 2], [0, 1, 99]])], [connect(1, 0), connect(30, 1)])
    yield 'negative_material', scene([model(1, 'Node'), geometry(30, 'Geo', vertices, [[0, 1, 2]],
        [layer('Material', 'Materials', 'AllSame', 'IndexToDirect', [-1], typ='i')]), material(40, 'Mat')], [connect(1, 0), connect(30, 1), connect(40, 1)])
    # Blender only maps links with two long identifiers, not int32 or double identifiers with the same numeric values.
    yield 'connection_types', scene([model(1, 'Node'), geometry(30, 'Geo', vertices, [[0, 1, 2]])],
        [connect(1, 0)] + [Node('C', [prop('S', 'OO'), prop(t, 30), prop(t, 1)]) for t in ('I', 'D')])
    # Appending a mesh slot also extends every object's earlier overrides of that mesh's slots.
    objects = [model(i, name) for i, name in ((1, 'A'), (2, 'B'), (3, 'C'))]
    objects += [geometry(30, 'Geo', vertices, [[0, 1, 2]], [layer('Material', 'Materials', 'AllSame', 'IndexToDirect', [2], typ='i')])]
    objects += [material(i, 'Mat' + str(i)) for i in range(40, 44)]
    yield 'late_material_slot', scene(objects, [connect(i, 0) for i in (1, 2, 3)] + [connect(30, i) for i in (1, 2, 3)] +
        [connect(40, 1), connect(41, 1), connect(42, 2), connect(41, 2), connect(40, 3), connect(41, 3), connect(43, 3)])
    yield 'duplicate_material_id', scene([model(1, 'Node'), geometry(30, 'Geo', vertices, [[0, 1, 2]]), material(40, 'Old'), material(40, 'Last')],
        [connect(1, 0), connect(30, 1), connect(40, 1)])


if __name__ == '__main__':
    sys.stdout.reconfigure(encoding='utf-8')
    out = Path(sys.argv[1]); out.mkdir(parents=True, exist_ok=True)
    import fbx_material_fixtures   # sub-step 6b: materials, textures, images (and the image files beside the scenes)
    fbx_material_fixtures.write_assets(out)
    import itertools
    for name, nodes in itertools.chain(fixtures(), fbx_material_fixtures.fixtures()):
        path = out / (name + '.fbx')
        write_fbx(path, 7400, nodes)
        print(path.as_posix())
