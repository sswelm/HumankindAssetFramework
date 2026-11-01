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


def bad_fixtures():
    yield 'cycle_self', scene([obj('Model', 1, 'Self', 'Null')], [connect(1, 1)])
    yield 'cycle_pair', scene([obj('Model', 1, 'One', 'Null'), obj('Model', 2, 'Two', 'Null')], [connect(1, 2), connect(2, 1)])


if __name__ == '__main__':
    sys.stdout.reconfigure(encoding='utf-8')
    reject = sys.argv[1] == '--bad'
    out = Path(sys.argv[2] if reject else sys.argv[1]); out.mkdir(parents=True, exist_ok=True)
    for name, nodes in (bad_fixtures() if reject else fixtures()):
        path = out / (name + '.fbx')
        write_fbx(path, 7400, nodes)
        print(path.as_posix())
