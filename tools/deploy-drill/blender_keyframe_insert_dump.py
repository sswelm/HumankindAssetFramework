"""The real thing, for the deploy drill: pose_bone.keyframe_insert on an armature, the way deploy_convert.py's step 5d
keys its RecoilArm - identity holds at 0 and at the deploy's end, a turn frame after frame whose first key lands ON
the hold (the key is moved, not replaced), the same turns backwards and slower, an identity key to settle, and now
and then a key again on a frame that has one.

usage: blender --background --python blender_keyframe_insert_dump.py -- <seed> <trials> [every|final]

After EVERY keyframe_insert (every, the default) or only at the end (final) every curve of the bone is written as a
row of blender_handles_dump.py:  H <ordinal> <extrapolation> <smoothing> <frame:value:lx:ly:rx:ry> ...  and
END <curves> <keys>. So every state the handles pass through is held against ONE calculation from the keys alone:
nothing of an earlier insert survives in them. A replaced key's value is checked here against old + (new - old) in
float32, and every key's type against BEZIER / AUTO_CLAMPED: the end row is not written when either fails.
"""
import math
import random
import struct
import sys

import bpy
from mathutils import Quaternion, Vector

a = sys.argv[sys.argv.index("--") + 1:]
seed = int(a[0]); trials = int(a[1])
every = len(a) < 3 or a[2] == "every"
bpy.ops.wm.read_factory_settings(use_empty=True)   # as the script does: a new curve's smoothing is a user preference


def h32(v):
    return "%08x" % struct.unpack("<I", struct.pack("<f", v))[0]


def f32(v):
    return struct.unpack("<f", struct.pack("<f", v))[0]


r = random.Random(seed)
scene = bpy.context.scene
arm_data = bpy.data.armatures.new("A")
arm = bpy.data.objects.new("Arm", arm_data)
scene.collection.objects.link(arm)
bpy.context.view_layer.objects.active = arm
arm.select_set(True)
made = 0; nkeys = 0; bad_types = 0; vbad = 0; vrows = 0


def curves(bone):
    ad = arm.animation_data
    if not ad or not ad.action: return []
    act = ad.action
    out = []
    for layer in act.layers:
        for strip in layer.strips:
            bag = strip.channelbag(ad.action_slot)
            if not bag: continue
            for fc in bag.fcurves:
                if ('pose.bones["%s"]' % bone) in fc.data_path: out.append(fc)
    return out


def dump(bone):
    global made, nkeys, bad_types
    for fc in curves(bone):
        for kp in fc.keyframe_points:
            if kp.handle_left_type != 'AUTO_CLAMPED' or kp.handle_right_type != 'AUTO_CLAMPED' or kp.interpolation != 'BEZIER':
                bad_types += 1
        print("H\t%d\t%s\t%s\t%s" % (made, fc.extrapolation, fc.auto_smoothing, "\t".join(
            ":".join(h32(v) for v in (*kp.co, *kp.handle_left, *kp.handle_right)) for kp in fc.keyframe_points)))
        nkeys += len(fc.keyframe_points); made += 1


for trial in range(trials):
    bpy.ops.object.mode_set(mode='EDIT')
    eb = arm_data.edit_bones.new("RecoilArm")
    eb.head = (r.uniform(-5, 5), r.uniform(-5, 5), r.uniform(-5, 5)); eb.tail = Vector(eb.head) + Vector((0, 10, 0))
    name = eb.name
    bpy.ops.object.mode_set(mode='POSE')
    pb = arm.pose.bones[name]
    deploy_end = r.choice((1, 10, 24, 30, 48, 73, 120, 400))
    step = r.choice((1, 2, 2, 2, 3, 5))
    ret_slow = r.choice((0, 1, 2, 4, 4, 4, 7))
    nfr = r.choice((1, 2, 3, 5, 9, 14, 20, 33))
    A_local = Vector((r.uniform(-1, 1), r.uniform(-1, 1), r.uniform(-1, 1))).normalized()
    R = r.choice((1.0e9, 1000.0, 57.2958 * 3.0 / r.uniform(-25, 25), 10.0, 150.0))
    peak = r.randrange(nfr); dist = r.uniform(0.1, 8.0)
    shape = r.randrange(5)
    prev_q = {}

    def after():
        if every: dump(name)

    def key_arm_identity(f):
        pb.rotation_quaternion = (1.0, 0.0, 0.0, 0.0); pb.location = (0.0, 0.0, 0.0)
        pb.keyframe_insert('location', frame=f); after(); pb.keyframe_insert('rotation_quaternion', frame=f); after()

    def key_theta(f, theta):
        q = Quaternion(A_local, theta)
        if 'ra' in prev_q and q.dot(prev_q['ra']) < 0.0: q.negate()
        pb.rotation_quaternion = q; pb.location = (0.0, 0.0, 0.0); prev_q['ra'] = q
        pb.keyframe_insert('location', frame=f); after(); pb.keyframe_insert('rotation_quaternion', frame=f); after()

    for hold in (0, deploy_end):
        key_arm_identity(hold)
    thetas = []
    for i in range(nfr):
        if shape == 0: s = dist * math.sqrt(i / max(1, nfr - 1))                        # one rise
        elif shape == 1: s = dist * (i / max(1, peak) if i <= peak else max(0.0, 1 - (i - peak) / max(1, nfr - 1 - peak)))   # kick and back
        elif shape == 2: s = dist * r.random()                                         # noise
        elif shape == 3: s = dist * (1.0 if i >= nfr // 2 else 0.001 * i)              # a step
        else: s = dist * math.sin(i * 0.9) * (1 if r.random() < 0.9 else -1)           # sign changes
        theta = -abs(s) / R * (1 if s >= 0 else -1)
        key_theta(deploy_end + i * step, theta); thetas.append(theta)
    f = deploy_end + (nfr - 1) * step
    if ret_slow > 0:
        for i in range(len(thetas) - 2, -1, -1):
            f += step * ret_slow
            key_theta(f, thetas[i])
    key_arm_identity(f + 1)
    # a key again on a frame that has one (another value): the key is moved, not replaced
    for _ in range(r.randint(0, 3)):
        fcs = curves(name)
        frames = [kp.co[0] for kp in fcs[0].keyframe_points]
        g = int(r.choice(frames))
        old = {(fc.data_path, fc.array_index): [kp.co[1] for kp in fc.keyframe_points if kp.co[0] == g][0] for fc in fcs}
        key_theta(g, r.uniform(-3.5, 3.5) * r.choice((1.0, 1e-3, 1e-7)))
        for fc in curves(name):
            if 'rotation_quaternion' not in fc.data_path: continue
            new = [kp.co[1] for kp in fc.keyframe_points if kp.co[0] == g][0]
            want = f32(pb.rotation_quaternion[fc.array_index]); o = old[(fc.data_path, fc.array_index)]
            model = f32(o + f32(want - o))
            vrows += 1
            if h32(new) != h32(model): vbad += 1
            if h32(new) != h32(want): print("V\tmoved-not-set\t%s\t%s\t%s" % (h32(o), h32(want), h32(new)))
            if h32(new) != h32(model): print("V\tMODEL-WRONG\t%s\t%s\t%s\t%s" % (h32(o), h32(want), h32(new), h32(model)))
    if not every: dump(name)
    if trial == 0:
        for fc in curves(name): print("INFO\tcurve\t%s[%d]\t%s\t%s\tflags=%s\tmods=%d" % (fc.data_path, fc.array_index, fc.extrapolation, fc.auto_smoothing, "", len(fc.modifiers)))
    # clear this bone's curves for the next trial
    ad = arm.animation_data
    for layer in ad.action.layers:
        for strip in layer.strips:
            bag = strip.channelbag(ad.action_slot)
            for fc in list(curves(name)): bag.fcurves.remove(fc)
    bpy.ops.object.mode_set(mode='EDIT')
    arm_data.edit_bones.remove(arm_data.edit_bones[name])
    bpy.ops.object.mode_set(mode='POSE')

if bad_types or vbad:
    raise RuntimeError("%d keys that are not BEZIER / AUTO_CLAMPED, %d of %d replaced values that are not old + (new - old)" % (bad_types, vbad, vrows))
print("END\t%d\t%d" % (made, nkeys), flush=True)
