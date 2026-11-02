"""Animation scenes for tools/fbx_model_drill.sh (sub-step 6d of the native FBX reader): stacks, layers, curve nodes and
curves on objects and bones in the shapes the real files lack - a channel keyed on its own, curves of different key times
combined, a rotation past 180 degrees, a constant channel, a single key, an unsorted curve, two stacks on one armature,
two layers in a stack, the armature object itself animated, an object hanging from an animated bone, animated meshes under
an armature (which Blender skips), 30 fps and 29.97 fps files, a 7700 file with the old ktime by its TCDefinition. The
oracle is Blender itself (fbx_to_glb.py). A fixture named *_fallback is one the reader must leave to Blender by name."""
from fbx_fixtures import Node, prop
from fbx_model_fixtures import P, vec, properties, obj, connect, header, scene, model, geometry, material
from fbx_armature_fixtures import limb, null, skin, cluster, bind_pose, translation, rot_z, matmul, GRID_V, GRID_F, GRID_UV

KTIME_V7 = 46186158000
KTIME_V8 = 141120000


def frames(fps, *fr, ktime=KTIME_V7):
    """The ktimes of the frames (0-based: frame 0 is the start) at a frame rate."""
    return [int(round(f * ktime / fps)) for f in fr]


def anim_obj(kind, ident, name, cls, children=()):
    return Node(kind, [prop('L', ident), prop('S', name + '\0\1' + cls), prop('S', '')], list(children))


def stack(ident, name):
    return anim_obj('AnimationStack', ident, name, 'AnimStack', [properties(P('LocalStop', 'KTime', 'Time', 0), P('ReferenceStop', 'KTime', 'Time', 0))])


def layer(ident, name):
    return anim_obj('AnimationLayer', ident, name, 'AnimLayer')


def curve_node(ident, name, defaults=(0.0, 0.0, 0.0)):
    return anim_obj('AnimationCurveNode', ident, name, 'AnimCurveNode', [properties(P('d|X', 'Number', '', float(defaults[0])), P('d|Y', 'Number', '', float(defaults[1])), P('d|Z', 'Number', '', float(defaults[2])))])


def curve(ident, times, values):
    n = len(times)
    return anim_obj('AnimationCurve', ident, '', 'AnimCurve', [
        Node('Default', [prop('D', 0.0)]), Node('KeyVer', [prop('I', 4008)]),
        Node('KeyTime', [prop('l', list(times))]), Node('KeyValueFloat', [prop('f', [float(v) for v in values])]),
        Node('KeyAttrFlags', [prop('i', [24840])]), Node('KeyAttrDataFloat', [prop('f', [0.0, 0.0, 0.0, 0.0])]), Node('KeyAttrRefCount', [prop('i', [n])])])


def op(src, dst, prop_name):
    return Node('C', [prop('S', 'OP'), prop('L', src), prop('L', dst), prop('S', prop_name)])


class Rig:
    """Ids and connections for one stack with one layer; curves and curve nodes are numbered from `base`."""

    def __init__(self, stack_id, layer_id, base, stack_name='Take 001', layer_name=None):
        self.objects = [stack(stack_id, stack_name), layer(layer_id, layer_name or stack_name)]
        self.conns = [connect(layer_id, stack_id)]
        self.layer_id = layer_id; self.next = base

    def animate(self, model_id, prop_name, channels, times, values_per_channel, defaults=(0.0, 0.0, 0.0)):
        """A curve node on a Model's property with a curve per channel ('X', 'Y', 'Z'); `times` per channel or shared."""
        node_id = self.next; self.next += 1
        self.objects.append(curve_node(node_id, prop_name.replace('Lcl ', ''), defaults))
        self.conns += [op(node_id, model_id, prop_name), connect(node_id, self.layer_id)]
        for i, ch in enumerate(channels):
            cid = self.next; self.next += 1
            t = times[i] if isinstance(times[0], (list, tuple)) else times
            self.objects.append(curve(cid, t, values_per_channel[i]))
            self.conns.append(op(cid, node_id, 'd|' + ch))
        return node_id


def anim_scene(name, objects, connections, head=None, version=7400):
    return name, scene(objects, connections, head), version


TRI = [0, 0, 0, 1, 0, 0, 0, 1, 0]
TRI_F = [[0, 1, 2]]


def bone_rig(prefix_id=0):
    """The 6c null rig: Root -> Spine -> Head and Spine -> Arm, a skinned grid, the Arm's bind pose rotated 5 degrees past its
    transform (a posed bone the import's set_pose leaves in the pose bone)."""
    g = {10: translation(0, 0, 0), 11: rot_z(20, 1, 0, 0), 12: matmul(rot_z(20, 1, 0, 0), translation(1, 0, 0)), 13: matmul(rot_z(20, 1, 0, 0), rot_z(-40, 0.5, 0.5, 0))}
    bones = [limb(10, 'Root'), limb(11, 'Spine', vec('Lcl Translation', 1, 0, 0), vec('Lcl Rotation', 0, 0, 20)),
             limb(12, 'Head', vec('Lcl Translation', 1, 0, 0)), limb(13, 'Arm', vec('Lcl Translation', 0.5, 0.5, 0), vec('Lcl Rotation', 0, 0, -45))]
    clusters = [cluster(40, 'RootCluster', [0, 1, 4, 5], [1.0, 0.5, 0.9, 0.2], transform=translation(0, 0, 0), transform_link=g[10]),
                cluster(41, 'SpineCluster', [1, 2, 3, 4, 5], [0.5, 0.7, 0.3, 0.3, 0.3], transform=translation(0, 0, 0), transform_link=g[11]),
                cluster(42, 'HeadCluster', [2, 3, 5, 8, 9, 10, 11], [0.3, 0.3, 0.25, 1, 1, 1, 1], transform=translation(0, 0, 0), transform_link=g[12]),
                cluster(43, 'ArmCluster', [5, 6, 7], [0.15, 1.0, 0.8], transform=translation(0, 0, 0), transform_link=g[13])]
    objects = [null(1, 'Rig', vec('Lcl Translation', 0, 0, 1)), model(2, 'Skinned'), geometry(30, 'Grid', GRID_V, GRID_F, [GRID_UV]), material(100, 'Mat'), skin(50)] + bones + clusters + [
        bind_pose(60, [(i, g[i]) for i in (10, 11, 12, 13)])]
    conns = [connect(1, 0), connect(2, 1), connect(30, 2), connect(100, 2), connect(10, 1), connect(11, 10), connect(12, 11), connect(13, 11),
             connect(50, 30)] + [connect(40 + i, 50) for i in range(4)] + [connect(10, 40), connect(11, 41), connect(12, 42), connect(13, 43)]
    return objects, conns


def fixtures():
    f24 = lambda *fr: frames(24, *fr)
    # ---- objects: a translation on every axis at frames 0, 24, 48; a rotation about Z alone at 0, 12, 48 (its keys combined
    # with the translation's, each curve interpolated at the other's times); a scale on X alone from frame 24 (the static
    # value before its first key); a second object driven by the same curve node (two actions, one node); a child that
    # follows (no action of its own); a rotation past 180 degrees (the compatible Euler); a constant channel (two samples,
    # STEP); a single key (a one-frame action stretched by one and found again)
    rig = Rig(200, 201, 300)
    rig.animate(1, 'Lcl Translation', 'XYZ', f24(0, 24, 48), [[0, 1, 2.5], [0, 0.5, -1], [0, 2, 0.25]])
    rig.animate(1, 'Lcl Rotation', 'Z', f24(0, 12, 48), [[0, 30, 75]], defaults=(10, 20, 30))
    rig.animate(1, 'Lcl Scaling', 'X', f24(24, 48), [[2, 3]], defaults=(1, 1, 1))   # the static 1 before the first key, not the key's 2
    rig.animate(3, 'Lcl Rotation', 'Y', f24(0, 10, 20, 30), [[0, 170, 340, 350]])
    rig.animate(4, 'Lcl Translation', 'Z', f24(0, 20), [[3, 3]])
    rig.animate(5, 'Lcl Rotation', 'X', f24(7), [[45]])
    node = rig.animate(6, 'Lcl Translation', 'Y', f24(0, 12), [[0, 2]])
    rig.conns.append(op(node, 7, 'Lcl Translation'))   # the same curve node drives a second model
    objects = [model(1, 'Mover', vec('Lcl Rotation', 10, 20, 30)), geometry(30, 'MoverGeo', TRI, TRI_F), model(2, 'Rider', vec('Lcl Translation', 0.5, 0, 0)), geometry(31, 'RiderGeo', TRI, TRI_F),
               null(3, 'Spinner'), null(4, 'Still'), null(5, 'OneKey'), null(6, 'Twin'), null(7, 'Twin2'), material(100, 'Mat')] + rig.objects
    conns = [connect(1, 0), connect(2, 1), connect(30, 1), connect(31, 2), connect(100, 1), connect(100, 2), connect(3, 0), connect(4, 0), connect(5, 0), connect(6, 0), connect(7, 0)] + rig.conns
    yield anim_scene('anim_objects', objects, conns)

    # ---- bones: the Spine's rotation and the Head's translation keyed; the Arm posed and unkeyed (the import's pose stays);
    # an empty hanging from the Head (sampled against the bone's pose each frame) and keyed itself; the armature object
    # animated too (its own channels after the bones'); a mesh under the armature and the skinned mesh keyed (Blender
    # skips both)
    objects, conns = bone_rig()
    objects += [null(3, 'Marker', vec('Lcl Translation', 0, 0.5, 0)), model(4, 'Prop', vec('Lcl Translation', 2, 0, 0)), geometry(31, 'PropGeo', TRI, TRI_F)]
    conns += [connect(3, 12), connect(4, 1), connect(31, 4), connect(100, 4)]
    rig = Rig(200, 201, 300)
    rig.animate(11, 'Lcl Rotation', 'Z', f24(0, 12, 36), [[20, 50, -10]], defaults=(0, 0, 20))
    rig.animate(12, 'Lcl Translation', 'XY', f24(0, 36), [[1, 1.5], [0, 0.25]], defaults=(1, 0, 0))
    rig.animate(12, 'Lcl Rotation', 'Y', f24(0, 18, 36), [[0, 200, 400]])   # past 180 degrees between keys: the quaternion's sign flips against the frame before
    rig.animate(3, 'Lcl Translation', 'X', f24(0, 18, 36), [[0, 0.3, 0]])
    rig.animate(1, 'Lcl Translation', 'Z', f24(0, 36), [[1, 2]], defaults=(0, 0, 1))
    rig.animate(4, 'Lcl Translation', 'Y', f24(0, 36), [[0, 5]])
    rig.animate(2, 'Lcl Rotation', 'Z', f24(0, 36), [[0, 90]])
    yield anim_scene('anim_bones', objects + rig.objects, conns + rig.conns)

    # ---- a connected bone keyed in translation (the pose ignores it: its channel stays at the rest) and in rotation, with an
    # empty under it keyed too
    g = {10: translation(0, 0, 0), 11: translation(0, 1, 0), 12: translation(0, 2, 0)}
    bones = [limb(10, 'Root'), limb(11, 'Spine', vec('Lcl Translation', 0, 1, 0)), limb(12, 'Head', vec('Lcl Translation', 0, 1, 0))]
    objects = [null(1, 'Rig'), null(3, 'Marker', vec('Lcl Translation', 0.1, 0, 0)), model(2, 'Skinned'), geometry(30, 'Grid', GRID_V, GRID_F, [GRID_UV]), material(100, 'Mat'), skin(50),
               cluster(40, 'RootCluster', [0, 1, 2, 3], [1.0] * 4, transform=translation(0, 0, 0), transform_link=g[10]),
               cluster(41, 'SpineCluster', [4, 5, 6, 7], [1.0] * 4, transform=translation(0, 0, 0), transform_link=g[11]),
               cluster(42, 'HeadCluster', [8, 9, 10, 11], [1.0] * 4, transform=translation(0, 0, 0), transform_link=g[12])] + bones
    conns = [connect(1, 0), connect(2, 1), connect(30, 2), connect(100, 2), connect(10, 1), connect(11, 10), connect(12, 11), connect(3, 11),
             connect(50, 30), connect(40, 50), connect(41, 50), connect(42, 50), connect(10, 40), connect(11, 41), connect(12, 42)]
    rig = Rig(200, 201, 300)
    rig.animate(11, 'Lcl Translation', 'XZ', f24(0, 24), [[0, 0.5], [0, 0.25]], defaults=(0, 1, 0))
    rig.animate(11, 'Lcl Rotation', 'X', f24(0, 24), [[0, 40]])
    rig.animate(12, 'Lcl Translation', 'Y', f24(0, 24), [[1, 1.5]], defaults=(0, 1, 0))
    rig.animate(3, 'Lcl Translation', 'Z', f24(0, 24), [[0, 0.3]])
    yield anim_scene('anim_connected', objects + rig.objects, conns + rig.conns)

    # ---- two stacks on one armature: the lone armature takes both actions (reset_bone_matrix between them: the posed Arm
    # at the identity in the second), an object takes its first only; the second stack's layer named differently
    objects, conns = bone_rig()
    objects += [null(3, 'Marker', vec('Lcl Translation', 0, 0.5, 0))]
    conns += [connect(3, 12)]
    walk = Rig(200, 201, 300, 'Walk')
    walk.animate(11, 'Lcl Rotation', 'Z', f24(0, 24), [[20, 40]], defaults=(0, 0, 20))
    walk.animate(3, 'Lcl Translation', 'Y', f24(0, 24), [[0.5, 1]], defaults=(0, 0.5, 0))
    run = Rig(210, 211, 400, 'Run', 'Base')
    run.animate(11, 'Lcl Rotation', 'Z', f24(0, 12), [[20, -30]], defaults=(0, 0, 20))
    run.animate(12, 'Lcl Scaling', 'XYZ', f24(0, 12), [[1, 1.5], [1, 1.5], [1, 1.5]], defaults=(1, 1, 1))
    run.animate(3, 'Lcl Translation', 'Y', f24(0, 12), [[0.5, 2]], defaults=(0, 0.5, 0))
    yield anim_scene('anim_two_stacks', objects + walk.objects + run.objects, conns + walk.conns + run.conns)

    # ---- two layers in one stack, named apart from it: two actions per object, the first assigned
    rig = Rig(200, 201, 300, 'Take 001', 'BaseLayer')
    rig.animate(1, 'Lcl Translation', 'X', f24(0, 24), [[0, 1]])
    extra = Rig(200, 202, 400, 'Take 001', 'ExtraLayer')
    extra.objects = extra.objects[1:]   # the stack exists already
    extra.animate(1, 'Lcl Translation', 'X', f24(0, 24), [[0, -1]])
    objects = [null(1, 'Layered')] + rig.objects + extra.objects
    yield anim_scene('anim_two_layers', objects, [connect(1, 0)] + rig.conns + extra.conns)

    # ---- 30 fps (TimeMode 6) and 29.97 fps (TimeMode 9: fps 30 with a base of 1.001); a custom mode with CustomFrameRate 12
    for name, mode, fps, custom in (('anim_fps30', 6, 30, 24.0), ('anim_fps2997', 9, 30, 24.0), ('anim_fps_custom', 14, 12, 12.0)):
        rig = Rig(200, 201, 300)
        rig.animate(1, 'Lcl Translation', 'XZ', frames(fps, 0, 15, 30), [[0, 1, 0], [0, 0, 2]])
        yield anim_scene(name, [null(1, 'Timed')] + rig.objects, [connect(1, 0)] + rig.conns, head=header(time_mode=mode, custom_fps=custom))

    # ---- a 7700 file: the old ktime by default (the new one is opt-in before 8000), the new one through a 1004 header's
    # TCDefinition 0, the old one again through 127
    for name, tc, ktime in (('anim_v7700_default_ktime', None, KTIME_V7), ('anim_v7700_old_ktime', 127, KTIME_V7), ('anim_v7700_tc0', 0, KTIME_V8)):
        rig = Rig(200, 201, 300)
        rig.animate(1, 'Lcl Translation', 'X', frames(24, 0, 24, ktime=ktime), [[0, 3]])
        yield anim_scene(name, [null(1, 'Timed')] + rig.objects, [connect(1, 0)] + rig.conns, head=header(header_version=1004 if tc is not None else 1003, tc_definition=tc, version=7700), version=7700)

    # ---- an unsorted curve (blen_read_invalid_animation_curve), a curve with a repeated time, keys on fractional frames
    rig = Rig(200, 201, 300)
    rig.animate(1, 'Lcl Translation', 'X', [f24(0, 48, 24, 24)], [[0, 4, 2, 9]])
    rig.animate(1, 'Lcl Translation', 'Y', [[0, 1300000000, 2600000000]], [[0, 1, 0]])
    yield anim_scene('anim_odd_keys', [null(1, 'Odd')] + rig.objects, [connect(1, 0)] + rig.conns)

    # ---- the armature object keyed in the stack that sorts last: its properties linger into the sampling of the empty under
    # the Head and the Null beside it (their channels against the armature's stale world); the posed Arm reset by the switch
    objects, conns = bone_rig()
    objects += [null(3, 'Marker', vec('Lcl Translation', 0, 0.5, 0)), null(4, 'Beside', vec('Lcl Translation', 0, 0, 2))]
    conns += [connect(3, 12), connect(4, 1)]
    walk = Rig(200, 201, 300, 'Walk')
    walk.animate(11, 'Lcl Rotation', 'Z', f24(0, 24), [[20, 40]], defaults=(0, 0, 20))
    walk.animate(3, 'Lcl Translation', 'X', f24(0, 24), [[0, 1]])
    walk.animate(4, 'Lcl Translation', 'X', f24(0, 24), [[0, -1]])
    zoom = Rig(210, 211, 400, 'Zoom')
    zoom.animate(1, 'Lcl Translation', 'Z', f24(0, 12), [[1, 3]], defaults=(0, 0, 1))
    zoom.animate(1, 'Lcl Rotation', 'Y', f24(0, 12), [[0, 25]])
    zoom.animate(11, 'Lcl Rotation', 'Z', f24(0, 12), [[20, 0]], defaults=(0, 0, 20))
    yield anim_scene('anim_armature_later', objects + walk.objects + zoom.objects, conns + walk.conns + zoom.conns)

    # ---- an armature no mesh uses, under a keyed empty, keyed itself in the stack that sorts last: its skin is gathered after
    # the animations, from the scene the sampler left (the last frame, the armature on its last action, the empty on its own)
    bones = [limb(10, 'Root'), limb(11, 'Spine', vec('Lcl Translation', 1, 0, 0), vec('Lcl Rotation', 0, 0, 20))]
    objects = [null(5, 'Carrier'), null(1, 'Rig', vec('Lcl Translation', 0, 0, 1))] + bones + [bind_pose(60, [(10, translation(0, 0, 0)), (11, rot_z(20, 1, 0, 0))])]
    conns = [connect(5, 0), connect(1, 5), connect(10, 1), connect(11, 10)]
    walk = Rig(200, 201, 300, 'Walk')
    walk.animate(5, 'Lcl Translation', 'X', f24(0, 24), [[0, 2]])
    walk.animate(11, 'Lcl Rotation', 'Z', f24(0, 24), [[20, 60]], defaults=(0, 0, 20))
    zoom = Rig(210, 211, 400, 'Zoom')
    zoom.animate(1, 'Lcl Rotation', 'Y', f24(0, 12), [[0, 30]])
    zoom.animate(11, 'Lcl Rotation', 'Z', f24(0, 12), [[20, -20]], defaults=(0, 0, 20))
    yield anim_scene('anim_unused_skin', objects + walk.objects + zoom.objects, conns + walk.conns + zoom.conns)

    # ---- fallbacks: a stack without its class, a curve without keys
    rig = Rig(200, 201, 300)
    rig.objects[0] = obj('AnimationStack', 200, 'Take 001')
    rig.animate(1, 'Lcl Translation', 'X', f24(0, 24), [[0, 1]])
    yield anim_scene('anim_class_fallback', [null(1, 'A')] + rig.objects, [connect(1, 0)] + rig.conns)
    rig = Rig(200, 201, 300)
    rig.animate(1, 'Lcl Translation', 'X', [[]], [[]])
    yield anim_scene('anim_nokeys_fallback', [null(1, 'A')] + rig.objects, [connect(1, 0)] + rig.conns)
