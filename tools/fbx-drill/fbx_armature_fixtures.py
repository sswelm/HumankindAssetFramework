"""Armature, bone, skin and colour-layer scenes for tools/fbx_model_drill.sh (sub-step 6c of the native FBX reader): the
shapes the real files lack - a Null turned armature and an inserted one, bind poses from a Pose, from a cluster's
TransformLink and from none, clusters with every weight shape (two bones, none, a dropped tiny weight, five bones on one
vertex), objects parented to bones, a mesh node with bone children (a fake bone), an armature no mesh uses, rotated and
scaled bones, twin bone names, colour layers under every mapping beside a material-less slot. The oracle is Blender itself
(fbx_to_glb.py). A fixture named *_fallback is one the reader must leave to Blender by name."""
import math
from fbx_fixtures import Node, prop
from fbx_model_fixtures import P, vec, properties, obj, connect, header, scene, model, geometry, layer, material


def limb(ident, name, *props, kind='LimbNode'):
    return obj('Model', ident, name, kind, [properties(*props)] if props else [])


def null(ident, name, *props):
    return obj('Model', ident, name, 'Null', [properties(*props)] if props else [])


def mat16(m):
    """A mathutils-style row-major 4x4 (list of rows) as the FBX's column-major 16 doubles."""
    return [float(m[r][c]) for c in range(4) for r in range(4)]


def translation(x, y, z):
    return [[1, 0, 0, x], [0, 1, 0, y], [0, 0, 1, z], [0, 0, 0, 1]]


def rot_z(deg, x=0.0, y=0.0, z=0.0, s=1.0):
    a = math.radians(deg); c, si = math.cos(a), math.sin(a)
    return [[c * s, -si * s, 0, x], [si * s, c * s, 0, y], [0, 0, s, z], [0, 0, 0, 1]]


def matmul(a, b):
    return [[sum(a[r][k] * b[k][c] for k in range(4)) for c in range(4)] for r in range(4)]


def skin(ident, name='Skin'):
    return obj('Deformer', ident, name, 'Skin', [Node('Version', [prop('I', 101)]), Node('Link_DeformAcuracy', [prop('D', 50.0)])])


def cluster(ident, name, indexes=None, weights=None, transform=None, transform_link=None, associate=None):
    children = [Node('Version', [prop('I', 100)]), Node('UserData', [prop('S', ''), prop('S', '')])]
    if indexes is not None:
        children.append(Node('Indexes', [prop('i', list(indexes))]))
        children.append(Node('Weights', [prop('d', [float(w) for w in weights])]))
    if transform is not None:
        children.append(Node('Transform', [prop('d', mat16(transform))]))
    if transform_link is not None:
        children.append(Node('TransformLink', [prop('d', mat16(transform_link))]))
    if associate is not None:
        children.append(Node('TransformAssociateModel', [prop('d', mat16(associate))]))
    return obj('Deformer', ident, name, 'Cluster', children)


def bind_pose(ident, nodes):
    """nodes: [(model id, matrix rows)] - the global bind matrix of each."""
    children = [Node('Type', [prop('S', 'BindPose')]), Node('Version', [prop('I', 100)]), Node('NbPoseNodes', [prop('I', len(nodes))])]
    for node_id, m in nodes:
        children.append(Node('PoseNode', [], [Node('Node', [prop('L', node_id)]), Node('Matrix', [prop('d', mat16(m))])]))
    return obj('Pose', ident, 'BindPose', 'BindPose', children)


# a 3x3 grid of quads standing in XY (12 vertices, 6 faces), Z 0: skinned along X
GRID_V = []
for j in range(3):
    for i in range(4):
        GRID_V += [float(i), float(j), 0.0]
GRID_F = [[j * 4 + i, j * 4 + i + 1, (j + 1) * 4 + i + 1, (j + 1) * 4 + i] for j in range(2) for i in range(3)]
GRID_UV = layer('UV', 'UV', 'ByVertice', 'Direct', [v / 3.0 for j in range(3) for i in range(4) for v in (i, j)], name='map1')


def rig_scene(name, objects, connections, head=None):
    return name, scene(objects, connections, head)


def fixtures():
    # ---- a Null turned armature: Root -> Spine -> Head, Spine -> Arm; a skinned grid with every weight shape; a BindPose
    # for every bone; a second mesh unskinned under the Null; the mesh's material
    bones = [limb(10, 'Root', vec('Lcl Translation', 0, 0, 0)), limb(11, 'Spine', vec('Lcl Translation', 1, 0, 0), vec('Lcl Rotation', 0, 0, 20)),
             limb(12, 'Head', vec('Lcl Translation', 1, 0, 0)), limb(13, 'Arm', vec('Lcl Translation', 0.5, 0.5, 0), vec('Lcl Rotation', 0, 0, -45))]
    globals_ = {10: translation(0, 0, 0), 11: rot_z(20, 1, 0, 0), 12: matmul(rot_z(20, 1, 0, 0), translation(1, 0, 0)), 13: matmul(rot_z(20, 1, 0, 0), rot_z(-45, 0.5, 0.5, 0))}
    grid = geometry(30, 'Grid', GRID_V, GRID_F, [GRID_UV])
    # vertex 0: Root only; 1: Root + Spine; 2, 3: Spine + Head (weights not summing to 1); 4: a tiny Spine weight beside Root;
    # 5: five bones (Root, Spine, Head, Arm and Root again through a second cluster); 6: Arm; 7: Arm beside a Head weight of
    # 1.5 (clamped to 1 by vg.add); 8-11: none (the neutral bone); an index past the mesh and a negative one (ignored by vg.add)
    clusters = [cluster(40, 'RootCluster', [0, 1, 4, 5], [1.0, 0.5, 0.9, 0.2], transform=translation(0, 0, 0), transform_link=globals_[10]),
                cluster(41, 'SpineCluster', [1, 2, 3, 4, 5], [0.5, 0.7, 0.3, 0.00005, 0.3], transform=translation(0, 0, 0), transform_link=globals_[11]),
                cluster(42, 'HeadCluster', [2, 3, 5, 7, -1], [0.3, 0.3, 0.25, 1.5, 0.7], transform=translation(0, 0, 0), transform_link=globals_[12]),
                cluster(43, 'ArmCluster', [5, 6, 7, 12], [0.15, 1.0, 0.8, 1.0], transform=translation(0, 0, 0), transform_link=globals_[13]),
                cluster(44, 'RootCluster2', [5], [0.1], transform=translation(0, 0, 0), transform_link=globals_[10])]
    objects = [null(1, 'Rig', vec('Lcl Scaling', 2, 2, 2)), model(2, 'Skinned'), model(3, 'Loose', vec('Lcl Translation', 0, 5, 0)), grid,
               geometry(31, 'LooseGeo', [0, 0, 0, 1, 0, 0, 0, 1, 0], [[0, 1, 2]]), material(100, 'Mat'), skin(50)] + bones + clusters + [
               bind_pose(60, [(1, [[2, 0, 0, 0], [0, 2, 0, 0], [0, 0, 2, 0], [0, 0, 0, 1]])] + [(i, globals_[i]) for i in (10, 11, 12, 13)])]
    conns = [connect(1, 0), connect(2, 1), connect(3, 1), connect(30, 2), connect(31, 3), connect(100, 2), connect(100, 3),
             connect(10, 1), connect(11, 10), connect(12, 11), connect(13, 11),
             connect(50, 30)] + [connect(40 + i, 50) for i in range(5)] + [connect(10, 40), connect(11, 41), connect(12, 42), connect(13, 43), connect(10, 44)]
    yield rig_scene('armature_null', objects, conns)

    # ---- bones under the scene root: an "Armature" is inserted; bind poses from the Pose for one bone, from the cluster's
    # TransformLink for another, from none (the node's own transform) for a third; a Limb type; a bone with an empty name
    # and twin names
    bones = [limb(10, 'A', vec('Lcl Translation', 0, 0, 1)), limb(11, 'B', vec('Lcl Translation', 0, 2, 0), vec('Lcl Rotation', 30, 0, 0), kind='Limb'),
             limb(12, '', vec('Lcl Translation', 1, 0, 0)), limb(13, 'B', vec('Lcl Translation', 0, 0, 3)), limb(14, 'B', vec('Lcl Translation', 0, 0, 4))]
    grid = geometry(30, 'Grid', GRID_V, GRID_F, [GRID_UV])
    clusters = [cluster(40, 'ACluster', [0, 1, 2, 3], [1.0, 1.0, 0.5, 0.5]),
                cluster(41, 'BCluster', [2, 3, 4, 5], [0.5, 0.5, 1.0, 1.0], transform=translation(0, 0, 0), transform_link=translation(0, 2, 0.25)),
                cluster(42, 'CCluster', [6, 7, 8, 9, 10, 11], [1.0] * 6, transform=translation(0.1, 0, 0))]
    objects = [model(2, 'Skinned', vec('Lcl Translation', 0, 0, -1)), grid, material(100, 'Mat'), skin(50)] + bones + clusters + [bind_pose(60, [(10, translation(0, 0, 1.5))])]
    conns = [connect(2, 0), connect(30, 2), connect(100, 2), connect(10, 0), connect(11, 10), connect(12, 11), connect(13, 10), connect(14, 13),
             connect(50, 30), connect(40, 50), connect(41, 50), connect(42, 50), connect(10, 40), connect(11, 41), connect(12, 42)]
    yield rig_scene('armature_inserted', objects, conns)

    # ---- objects parented to bones (an empty, a mesh), a mesh node with a bone child (a fake bone), two armatures: the
    # second's bones drive nothing (an unused skin); a bone-parented mesh with its own children; the Leg 1/128 under the
    # Hip (bone matrices are in the armature's space, the file's units: the bone length floor of 0.01 applies to the Hip
    # with a Toe 1/128 below it: the Leg's size floors to 0.01 and the objects hang from its tail); a second root bone of the
    # first armature
    bones = [limb(10, 'Hip', vec('Lcl Translation', 0, 0, 1)), limb(11, 'Leg', vec('Lcl Translation', 0, 0, -0.0078125)),   # bind poses equal to the transforms: an object under a posed bone is a fallback
             limb(22, 'Aux', vec('Lcl Translation', 2, 0, 0)), limb(23, 'Toe', vec('Lcl Translation', 0, 0, -0.0078125))]
    objects = [null(1, 'RigA'), null(5, 'RigB'), model(2, 'Body'), geometry(30, 'BodyGeo', GRID_V, GRID_F, [GRID_UV]), material(100, 'Mat'),
               null(3, 'Marker', vec('Lcl Translation', 0, 0, 0.5)), model(4, 'Shoe', vec('Lcl Translation', 0.2, 0, 0)), geometry(31, 'ShoeGeo', [0, 0, 0, 1, 0, 0, 0, 1, 0], [[0, 1, 2]]),
               model(6, 'Lamp', vec('Lcl Translation', 0, 0, 2)), geometry(32, 'LampGeo', [0, 0, 0, 1, 0, 0, 0, 1, 0], [[0, 1, 2]]),
               model(7, 'Holder', vec('Lcl Translation', 0, 1, 0)), geometry(33, 'HolderGeo', [0, 0, 0, 1, 0, 0, 0, 1, 0], [[0, 1, 2]]),
               limb(12, 'Antenna', vec('Lcl Translation', 0, 0, 1)),
               limb(20, 'Other', vec('Lcl Translation', 3, 0, 0)), limb(21, 'OtherChild', vec('Lcl Translation', 0, 0, 1)),
               skin(50), cluster(40, 'HipCluster', list(range(12)), [1.0] * 12, transform=translation(0, 0, 0), transform_link=translation(0, 0, 1)),
               cluster(41, 'LegCluster', [0, 1, 2, 3], [0.5] * 4, transform=translation(0, 0, 0), transform_link=translation(0, 0, 0.9921875))] + bones
    conns = [connect(1, 0), connect(5, 0), connect(2, 1), connect(30, 2), connect(100, 2), connect(10, 1), connect(11, 10), connect(22, 1), connect(23, 11),
             connect(3, 11), connect(4, 11), connect(31, 4), connect(6, 4), connect(32, 6),        # Marker and Shoe under Leg; Lamp under Shoe
             connect(7, 10), connect(33, 7), connect(12, 7),                                        # Holder (a mesh) under Hip with the bone Antenna below: a fake bone
             connect(20, 5), connect(21, 20),
             connect(50, 30), connect(40, 50), connect(41, 50), connect(10, 40), connect(11, 41)]
    yield rig_scene('armature_bone_children', objects, conns)

    # ---- a bone whose bind pose differs from its transform, with an object parented to it: the pose evaluation (6d,
    # FbxActions.Evaluate) puts the object against the rest bone through the pose the exporter sees at frame 0
    bones = [limb(10, 'Pivot', vec('Lcl Translation', 0, 0, 1), vec('Lcl Rotation', 0, 0, 30))]
    objects = [null(1, 'Rig'), null(3, 'Marker', vec('Lcl Translation', 1, 0, 0)), model(2, 'Skinned'), geometry(30, 'Grid', GRID_V, GRID_F, [GRID_UV]),
               skin(50), cluster(40, 'PivotCluster', list(range(12)), [1.0] * 12, transform=translation(0, 0, 0), transform_link=translation(0, 0, 1))] + bones
    conns = [connect(1, 0), connect(2, 1), connect(30, 2), connect(10, 1), connect(3, 10), connect(50, 30), connect(40, 50), connect(10, 40)]
    yield rig_scene('armature_posed', objects, conns)

    # ---- rotated and scaled bones: a chain of three with rotations about every axis, a PreRotation under RotationActive,
    # a non-uniform scale on the armature Null, bind poses from a Pose that disagree slightly with the transforms (no
    # object under a bone, so the pose basis is not read); the cluster matrices carry the Null's scale
    bones = [limb(10, 'J1', vec('Lcl Translation', 0, 0, 0), vec('Lcl Rotation', 10, 20, 30)),
             limb(11, 'J2', vec('Lcl Translation', 0, 1.5, 0), vec('Lcl Rotation', 0, 0, 40), P('RotationActive', 'bool', '', 1), vec('PreRotation', 90, 0, 0)),
             limb(12, 'J3', vec('Lcl Translation', 0, 1, 0), vec('Lcl Rotation', -15, 25, 0), vec('Lcl Scaling', 1, 1, 1))]
    g = {10: translation(0, 0, 0), 11: rot_z(30, 0, 1.5, 0), 12: rot_z(70, 0, 2.5, 0)}
    objects = [null(1, 'Rig', vec('Lcl Scaling', 1, 2, 3), vec('Lcl Rotation', 0, 0, 15)), model(2, 'Skinned'), geometry(30, 'Grid', GRID_V, GRID_F, [GRID_UV]), material(100, 'Mat'), skin(50),
               cluster(40, 'C1', [0, 1, 2, 3], [1.0] * 4, transform=translation(0, 0, 0), transform_link=g[10]),
               cluster(41, 'C2', [4, 5, 6, 7], [1.0] * 4, transform=translation(0, 0, 0), transform_link=g[11]),
               cluster(42, 'C3', [8, 9, 10, 11], [1.0] * 4, transform=translation(0, 0, 0), transform_link=g[12]),
               bind_pose(60, [(10, g[10]), (11, g[11]), (12, g[12])])] + bones
    conns = [connect(1, 0), connect(2, 1), connect(30, 2), connect(100, 2), connect(10, 1), connect(11, 10), connect(12, 11),
             connect(50, 30), connect(40, 50), connect(41, 50), connect(42, 50), connect(10, 40), connect(11, 41), connect(12, 42)]
    yield rig_scene('armature_rotated', objects, conns)

    # ---- a connected chain: the Spine's head sits exactly on the Root's tail (the Root's one bone child along its Y at the
    # distance the tail takes): use_connect, the head snapped; the Head and the Arm under the Spine are not (two children: the
    # tail at their mean distance); an empty under the connected Spine; the Spine's bind pose a hair from its transform
    g = {10: translation(0, 0, 0), 11: translation(0, 1, 0), 12: translation(0, 2, 0), 13: translation(0.2, 2, 0)}
    bones = [limb(10, 'Root'), limb(11, 'Spine', vec('Lcl Translation', 0, 1, 0)), limb(12, 'Head', vec('Lcl Translation', 0, 1, 0)), limb(13, 'Arm', vec('Lcl Translation', 0.2, 1, 0))]
    objects = [null(1, 'Rig'), null(3, 'Marker', vec('Lcl Translation', 0.1, 0, 0)), model(2, 'Skinned'), geometry(30, 'Grid', GRID_V, GRID_F, [GRID_UV]), material(100, 'Mat'), skin(50),
               cluster(40, 'RootCluster', [0, 1, 2, 3], [1.0] * 4, transform=translation(0, 0, 0), transform_link=g[10]),
               cluster(41, 'SpineCluster', [4, 5, 6, 7], [1.0] * 4, transform=translation(0, 0, 0), transform_link=g[11]),
               cluster(42, 'HeadCluster', [8, 9, 10, 11], [1.0] * 4, transform=translation(0, 0, 0), transform_link=g[12])] + bones
    conns = [connect(1, 0), connect(2, 1), connect(30, 2), connect(100, 2), connect(10, 1), connect(11, 10), connect(12, 11), connect(13, 11), connect(3, 11),
             connect(50, 30), connect(40, 50), connect(41, 50), connect(42, 50), connect(10, 40), connect(11, 41), connect(12, 42)]
    yield rig_scene('armature_connected', objects, conns)

    # ---- colour layers: ByPolygonVertex direct, ByVertice through an index, AllSame; a slot without a material beside one
    # with (the active layer becomes COLOR_0 with alpha for the material-less slot's mesh; a forced COLOR_0 beside materials)
    V = [0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0, 2, 0, 0, 2, 1, 0]
    F = [[0, 1, 2, 3], [1, 4, 5, 2]]
    corner_colors = [c for k in range(8) for c in (k / 8.0, 1.0 - k / 8.0, (k % 3) / 2.0, 0.25 + k / 16.0)]
    layers_a = [layer('Color', 'Colors', 'ByPolygonVertex', 'Direct', corner_colors, name='corner'),
                layer('Color', 'Colors', 'ByVertice', 'IndexToDirect', [0.1, 0.2, 0.3, 1.0, 0.9, 0.8, 0.7, 0.5], [0, 1, 1, 0, 1, 0], 'ColorIndex', name='byvert'),
                layer('Color', 'Colors', 'AllSame', 'IndexToDirect', [0.2, 0.4, 0.6, 0.8], name='same'),
                layer('Material', 'Materials', 'ByPolygon', 'IndexToDirect', [0, 1], typ='i')]
    layers_b = [layer('Color', 'Colors', 'ByPolygonVertex', 'Direct', corner_colors, name='corner')]
    objects = [model(1, 'TwoSlots'), geometry(30, 'TwoSlotsGeo', V, F, layers_a), model(2, 'NoMaterial', vec('Lcl Translation', 0, 3, 0)), geometry(31, 'NoMaterialGeo', V, F, layers_b),
               model(3, 'WithMaterial', vec('Lcl Translation', 0, 6, 0)), geometry(32, 'WithMaterialGeo', V, F, layers_b), material(100, 'Mat')]
    conns = [connect(1, 0), connect(2, 0), connect(3, 0), connect(30, 1), connect(31, 2), connect(32, 3), connect(100, 1), connect(100, 3)]
    yield rig_scene('colour_layers', objects, conns)
    # an AllSame layer that carries an index array: Blender's importer asserts on it (the polyloop readers: UVs and colours alike)
    objects = [model(1, 'Bad'), geometry(30, 'BadGeo', V, F, [layer('Color', 'Colors', 'AllSame', 'IndexToDirect', [0.2, 0.4, 0.6, 0.8], [0], 'ColorIndex', name='same')])]
    yield rig_scene('colour_allsame_index_fallback', objects, [connect(1, 0), connect(30, 1)])

    # A polygon mapping is not supported by Blender's polyloop reader: the colour attributes stay white.
    v = [0, 0, 0, 1, 0, 0, 0, 1, 0]; faces = [[0, 1, 2]]
    layers = [layer('Color', 'Colors', 'ByPolygon', 'Direct', [1., 0., 0., 1.], name='direct'),
              layer('Color', 'Colors', 'ByPolygon', 'IndexToDirect', [0., 1., 0., 1.], [0], 'ColorIndex', name='indexed')]
    yield rig_scene('colour_polygon_ignored', [model(1, 'Mesh'), geometry(30, 'Geo', v, faces, layers)], [connect(1, 0), connect(30, 1)])
    # mesh.validate removes the leading face and its corners from every colour attribute, just as from UVs/normals.
    layers = [layer('Color', 'Colors', 'ByPolygonVertex', 'Direct', [1., 0., 0., 1.] * 3 + [0., 0., 1., 1.] * 3, name='blue'),
              layer('Color', 'Colors', 'ByPolygonVertex', 'Direct', [1., 1., 0., 1.] * 3 + [0., 1., 0., 1.] * 3, name='green')]
    yield rig_scene('colour_face_repair', [model(1, 'Mesh'), geometry(30, 'Geo', v, [[0, 0, 1], [0, 1, 2]], layers)], [connect(1, 0), connect(30, 1)])
    # These are declared reader limits, enforced before the importer builds a misleading or invalid skin.
    objects = [null(1, 'Rig'), limb(10, 'Bone'), model(2, 'A'), model(3, 'B'), geometry(30, 'Geo', v, faces), skin(50),
               cluster(40, 'ACluster', [0, 1, 2], [1., 1., 1.])]
    links = [connect(1, 0), connect(10, 1), connect(2, 0), connect(3, 0), connect(30, 2), connect(50, 30), connect(40, 50), connect(10, 40)]
    yield rig_scene('skin_shared_datablock_fallback', objects, links + [connect(30, 3)])
    yield rig_scene('skin_multiple_meshes_fallback', objects + [geometry(31, 'GeoB', v, faces), skin(51), cluster(41, 'BCluster', [0], [1.])],
                    links + [connect(31, 3), connect(51, 31), connect(41, 51), connect(10, 41)])
    for kind in ('I', 'D'):
        pose = obj('Pose', 60, 'BindPose', 'BindPose', [Node('Type', [prop('S', 'BindPose')]),
            Node('PoseNode', [], [Node('Node', [prop(kind, 10)]), Node('Matrix', [prop('d', mat16(translation(0, 0, 0)))])])])
        yield rig_scene('pose_node_' + kind + '_fallback', [null(1, 'Rig'), limb(10, 'Bone'), pose], [connect(1, 0), connect(10, 1)])
    for mapping, reference in [('ByVertice', 'Direct'), ('AllSame', 'IndexToDirect')]:
        normal = layer('Normal', 'Normals', mapping, reference, [0., 0., 1.] * 3, [0, 1, 2], 'NormalsIndex')
        yield rig_scene('normal_' + mapping + '_index_fallback', [model(1, 'Mesh'), geometry(30, 'Geo', v, faces, [normal])], [connect(1, 0), connect(30, 1)])
