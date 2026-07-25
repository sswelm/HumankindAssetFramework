# deploy_convert.py — turn a model animated by RIGID MOVING PARTS (node transforms, no skinning) into a bone-per-part
# SKINNED armature that the Factory's animated bake (rig_anim.py) can consume. Many Maya/Sketchfab models animate parts
# by moving separate nodes (a howitzer's trail legs, a turret, landing gear, folding wings, a crane) rather than skinning
# — rig_anim.py needs an armature, so this bridges the gap: it builds one bone per animated part (hierarchy preserved),
# retargets each node's animation onto its bone (Copy Transforms + bake), and rigidly binds each mesh to its bone at 100%.
# Soft-skinned character rigs (crew) collapse the bake, so a strip-list removes them (and any loose props).
#
# Run headless:
#   blender -b -P deploy_convert.py -- <in.glb> <out.glb> [start end] [stripCsv] [readyFrame] [legScale] [barrelScale] [recoilSrcStart recoilSrcEnd] [step] [mag] [arcR] [returnSlow] [slamDeg] [slamSettle]
#     start end   : trim the clip to this sub-range (the deploy). Omit = full clip.
#     stripCsv    : comma-separated name substrings to delete (crew/props). Omit = the M114 defaults below.
#     readyFrame  : (5b) source frame of the fully-elevated barrel; retargets the barrel to rise there over the deploy's back half.
#     legScale    : (5c) scale the leg spread (1 = full, 0.5 = half as wide).
#     barrelScale : (5b) scale the barrel elevation (>1 exaggerates past the source's firing max).
#     recoilSrcStart recoilSrcEnd : (5d) the recoil sub-range IN THE SOURCE clip; its kickback TIMING is remapped onto a recoil
#                                   tail appended after 'end', played on-fire from the deployed hold.
#     step        : (5d) source-frame sampling step for the recoil (default 2).
#     mag         : (5d) slide-distance scale (default 1 = the source distance; 2 ~= half the tube).
#     arcR        : (5d) FK-arc pivot distance (default 400). Larger = straighter slide (less swing) but more jitter-prone.
#   NOTE (5d): the clip bake keeps per-bone ROTATION but DISCARDS per-bone translation, so a literal barrel slide bakes to nothing.
#   The recoil is faked via an FK-arc: a hidden far-pivot 'RecoilArm' bone the tube hangs off, rotated so the tube swings on a long
#   arc that reads as a near-straight backward slide (the arm's rotation bakes; runtime FK rebuilds it). It keeps a slight swing;
#   DON'T counter-rotate the tube to straighten it — that needs translation the bake drops, and the model explodes in-game.
# Checks BOTH the object name AND the mesh-data name (glTF import can name an object 'Object_NNN' while its mesh keeps
# the real name, so an object-name-only filter misses them). Node `matrix` transforms aren't handled (TRS only).
import bpy, sys
from mathutils import Vector, Quaternion

argv = sys.argv[sys.argv.index("--") + 1:]
inp, outp = argv[0], argv[1]

# FAMILY CONVENTION: OFF is the default; a feature exists only when its knob asks for it. Recoil step empty
# or 0 = the ENTIRE fire animation OFF — the recoil sections are skipped, and the 'recoil' role bakes as a
# held deployed stance so an Attack field still pointing at it plays a graceful no-op instead of failing the
# bake. Set step 1 (finest) or 2 to ENABLE the fire cycle.
_recoil_off = len(argv) <= 10 or argv[10].strip() in ("", "0")
_had_recoil_arg = len(argv) > 8 and argv[8].strip() != ""
if _recoil_off and _had_recoil_arg:
    argv[8] = ""
    print("DEPLOY recoil step empty/0 — fire cycle DISABLED ('recoil' role = held stance)")

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=inp)
scene = bpy.context.scene

# --- 1. strip crew + loose props (soft-skinned rigs = the bake-breakers; ammo/pole/string = loose firing props) ---
KILL = tuple(k.strip().lower() for k in argv[4].split(",")) if len(argv) > 4 and argv[4].strip() else \
    ("solder", "soldier", "pole", "string", "shell", "dynam", "ammun", "pcylinder1", "pcylinder3", "icosphere", "basicgal",
     "polysurface")   # polySurface1/5 = a loose prop floating ~20u above the gun (a stray shell), not part of the howitzer
def is_kill(o):
    names = [o.name.lower()]
    if getattr(o, "data", None) is not None and hasattr(o.data, "name"):
        names.append(o.data.name.lower())
    return any(k in n for n in names for k in KILL)
for obj in list(bpy.data.objects):
    if is_kill(obj):
        bpy.data.objects.remove(obj, do_unlink=True)
survivors = [o.name for o in bpy.data.objects if o.type == 'MESH']
print("DEPLOY after strip: %d objects, %d meshes: %s" % (len(bpy.data.objects), len(survivors), ", ".join(survivors)))

# --- 2. frame range from the surviving actions ---
fmin, fmax = 1e9, -1e9
for o in bpy.data.objects:
    if o.animation_data and o.animation_data.action:
        fr = o.animation_data.action.frame_range
        fmin = min(fmin, fr[0]); fmax = max(fmax, fr[1])
if fmin > fmax:
    fmin, fmax = 1.0, 1.0
fmin, fmax = int(fmin), int(fmax)
scene.frame_start, scene.frame_end = fmin, fmax
scene.frame_set(fmin)   # rest = deploy-start pose, so the bind is consistent
print("DEPLOY frame range: %d..%d" % (fmin, fmax))

# --- 3. which objects are ANIMATED parts (get a bone) vs plain meshes (get bound to a bone) ---
parts = [o for o in bpy.data.objects if o.animation_data and o.animation_data.action]
meshes = [o for o in bpy.data.objects if o.type == 'MESH']
print("DEPLOY animated parts: %d, meshes: %d" % (len(parts), len(meshes)))
for p in parts:
    print("   part: %-40s parent=%s" % (p.name, p.parent.name if p.parent else None))

# --- CULL DEGENERATE PARTS (2026-07-25, the howitzer-kickback finding) ---
# Zero-scale ancestor chains give some source nodes GARBAGE world matrices (the m114's door/handle/Object_56/
# barrel1: heads ~1e10, NaN anim keys, 1e28 mesh bounds). Bones built on them were inert under rotation-only
# clips, but any POSITION curve makes Amplitude's quantizer do math with the garbage rests and abort the whole
# clip bake (BonesCount 0). These nodes could never render sanely — delete the whole subtree outright.
def _mat_bad(m):
    t = m.translation; s = m.to_scale()
    for v in (t.x, t.y, t.z):
        if v != v or abs(v) > 1e6: return True
    for v in (s.x, s.y, s.z):
        if v != v or abs(v) > 1e4 or abs(v) < 1e-6: return True
    return False
# THE GARBAGE IS LOCAL, NOT WORLD (the night's key finding): the m114's broken chain carries HUGE local
# translations (~1e11) compensated by a tiny ancestor scale (~1e-11) — matrix_world looks perfectly sane at
# every frame while matrix_basis/local is astronomical. Everything that works in LOCAL/JOINT space (glTF
# joint rests, pose curves, Amplitude's quantizer) inherits the madness. Cull by LOCAL values, full range.
_bad_names = set()
for _f in list(range(fmin, fmax + 1, 7)) + [fmax]:
    scene.frame_set(_f)
    for p in parts:
        if p.name in _bad_names:
            continue
        if _mat_bad(p.matrix_basis) or _mat_bad(p.matrix_local) or _mat_bad(p.matrix_world):
            _bad_names.add(p.name)
scene.frame_set(fmin)   # restore the bind-pose frame
if _bad_names:
    def _anc_chain(o):
        out = []
        q = o
        while q is not None:
            out.append(q.name); q = q.parent
        return out
    _victims = [o for o in bpy.data.objects if any(n in _bad_names for n in _anc_chain(o))]
    print("DEPLOY culled %d degenerate part(s) (garbage world matrix): %s  (+%d descendant object(s))"
          % (len(_bad_names), sorted(_bad_names), max(0, len(_victims) - len(_bad_names))))
    parts = [p for p in parts if not any(n in _bad_names for n in _anc_chain(p))]   # BEFORE removal — dead references
    for _o in _victims:
        bpy.data.objects.remove(_o, do_unlink=True)

# --- 4. armature: one bone per animated part at its current (fmin) world pos, hierarchy mirrored ---
arm_data = bpy.data.armatures.new("DeployArm")
arm = bpy.data.objects.new("DeployArm", arm_data)
scene.collection.objects.link(arm)
bpy.context.view_layer.objects.active = arm
bpy.ops.object.mode_set(mode='EDIT')
bone_of = {}
for p in parts:
    b = arm_data.edit_bones.new(p.name)
    head = p.matrix_world.translation.copy()
    b.head = head
    b.tail = head + Vector((0, 0, 0.1))
    bone_of[p.name] = b.name
for p in parts:   # mirror object parenting onto the bones
    if p.parent and p.parent.name in bone_of:
        arm_data.edit_bones[bone_of[p.name]].parent = arm_data.edit_bones[bone_of[p.parent.name]]
# STATIC ROOT (helicopter finding, 2026-07-19): a model whose BODY is unanimated (only rotors move) has meshes
# with NO animated ancestor — they'd bind to nothing and render garbage. Give them a root bone — and BAKE it
# like a real part (constrained to a static mesh's topmost ancestor node): a naked synthetic bone skipped the
# node-scale treatment the part bones get (glTF roots carry ~9-100x scales) and bound ~9x off — the AW101's
# fuselage vanished in-game while its rotors rendered.
_sr = arm_data.edit_bones.new("StaticRoot")
_sr.head = Vector((0.0, 0.0, 0.0)); _sr.tail = Vector((0.0, 0.0, 0.1))
static_root = _sr.name
bpy.ops.object.mode_set(mode='OBJECT')
def _ancestors(o):
    out = []
    p = o
    while p is not None:
        out.append(p); p = p.parent
    return out
_static_anchor = None
for m in [o for o in bpy.data.objects if o.type == 'MESH']:
    if not any(a.name in bone_of for a in _ancestors(m)):
        # the mesh's IMMEDIATE parent (else the mesh itself) — the same bone-to-node relationship the animated
        # parts have; the topmost root often sits at scale 1 while the accumulated node scale lives mid-chain
        _static_anchor = m.parent if m.parent is not None else m
        break

# --- 5. retarget: each bone copies its part's WORLD transform, then bake to keyframes ---
bpy.context.view_layer.objects.active = arm
bpy.ops.object.mode_set(mode='POSE')
for p in parts:
    c = arm.pose.bones[bone_of[p.name]].constraints.new('COPY_TRANSFORMS')
    c.target = p
if _static_anchor is not None:
    # bake StaticRoot against the static meshes' topmost node so it inherits the SAME scale semantics as the
    # part bones (the naked-bone version bound ~9x off and the static geometry vanished in-game)
    c = arm.pose.bones[static_root].constraints.new('COPY_TRANSFORMS')
    c.target = _static_anchor
    print("DEPLOY StaticRoot baked against '%s' (static geometry scale anchor)" % _static_anchor.name)
bpy.ops.nla.bake(frame_start=fmin, frame_end=fmax, only_selected=False,
                 visual_keying=True, clear_constraints=True, bake_types={'POSE'})
bpy.ops.object.mode_set(mode='OBJECT')
print("DEPLOY baked %d bones" % len(bone_of))

# --- 5a. PRISTINE FIRE-WINDOW SNAPSHOT (2026-07-19): capture the recoil range NOW, BEFORE 5b/5c clear and re-key
#         the barrel/leg channels — the source's own fire choreography (the barrel LOWERING to reload and any other
#         rotational content) lives in these frames and the retarget would erase it. The 'recoil' role clip (7c) is
#         built from THIS snapshot, with the Slam arc layered on the (later-born) RecoilArm on top. ---
# MULTI-SEGMENT recoil (2026-07-26, user-designed): argv[8]/argv[9] are CSV lists of starts/ends. Segment 1
# is THE fire window (slide profile, slam arc, kept translations all derive from it); further segments are
# EPILOGUE choreography appended pristine to the recoil role clip (the M114: "443..530,330..440" = fire+kick+
# reload, then the source's own aiming RAISE re-used to bring the barrel back up — the raise doesn't exist
# after the load in the source, but it exists before the fire).
_seg_starts = [int(s) for s in argv[8].split(",") if s.strip()] if len(argv) > 8 and argv[8].strip() else []
_seg_ends = []
_seg_steps = []   # per-segment /N speed step (every Nth frame = N x faster); applied to EPILOGUE segments
for _tok in (argv[9].split(",") if len(argv) > 9 and argv[9].strip() else []):
    _tok = _tok.strip()
    if not _tok:
        continue
    if "/" in _tok:
        _e, _st = _tok.split("/", 1)
        _seg_ends.append(int(_e)); _seg_steps.append(max(1, int(_st)))
    else:
        _seg_ends.append(int(_tok)); _seg_steps.append(1)
_segments = list(zip(_seg_starts, _seg_ends, _seg_steps))
_fire_snap = {}
if _segments:
    for pb in arm.pose.bones:
        pb.rotation_mode = 'QUATERNION'
    for _ss, _se, _st in _segments:
        for f in range(_ss, _se + 1):
            scene.frame_set(f)
            bpy.context.view_layer.update()
            _fire_snap[f] = {pb.name: (pb.location.copy(), pb.rotation_quaternion.copy()) for pb in arm.pose.bones}
    print("DEPLOY fire-window snapshot: %d frames (%s) captured PRISTINE (pre-retarget)"
          % (len(_fire_snap), ", ".join("%d..%d/%d" % s for s in _segments)))

# --- 5b. optional: RETARGET the barrel to its fully-elevated 'ready' pose by the deploy's end (argv[5] = readyFrame) ---
# The rest/deployed pose should be combat-ready (barrel up). In the source the barrel pauses at the aiming angle for a
# long crew-loading hold before rising to the firing elevation, so a plain trim would deploy then sit then finish. Instead
# capture the barrel's local pose at the firing frame and re-key JUST the barrel bones to rise there over the deploy's
# back half — legs spread, then barrel elevates fully, no dead pause. Only the barrel/cannon bones are touched.
# Blender 4.4+/5.x: fcurves live in slotted channelbags (action.fcurves removed). Clear the given bones' channels so
# their existing keys don't fight a retarget. Works on both legacy and slotted actions.
def clear_bone_channels(act, bone_names):
    bags = ([act] if hasattr(act, "fcurves") else []) + \
           [cb for layer in getattr(act, "layers", []) for strip in layer.strips for cb in getattr(strip, "channelbags", [])]
    for cb in bags:
        for fc in list(cb.fcurves):
            if any(('pose.bones["%s"]' % bn) in fc.data_path for bn in bone_names):
                cb.fcurves.remove(fc)

if len(argv) > 5 and argv[5].strip():
    from mathutils import Quaternion
    ready_frame = int(argv[5])
    barrel_scale = float(argv[7]) if len(argv) > 7 and argv[7].strip() else 1.0   # >1 exaggerates the elevation (extrapolate)
    end_frame = int(argv[3]) if len(argv) > 3 else fmax
    mid = max(int(end_frame * 0.5), 1)
    barrel_bones = [bn for bn in bone_of.values() if any(k in bn.lower() for k in ("barrel", "cannon"))]
    scene.frame_set(ready_frame)
    ready = {bn: (arm.pose.bones[bn].rotation_quaternion.copy(), arm.pose.bones[bn].location.copy()) for bn in barrel_bones}
    clear_bone_channels(arm.animation_data.action, barrel_bones)
    bpy.context.view_layer.objects.active = arm
    bpy.ops.object.mode_set(mode='POSE')
    for bn in barrel_bones:      # rest (level) at mid-deploy, fully-elevated 'ready' (x barrel_scale) at the end
        pb = arm.pose.bones[bn]
        pb.rotation_quaternion = (1, 0, 0, 0); pb.location = (0, 0, 0)
        pb.keyframe_insert('rotation_quaternion', frame=mid); pb.keyframe_insert('location', frame=mid)
        rq, lc = ready[bn]
        ax, ang = rq.to_axis_angle(); rq = Quaternion(ax, ang * barrel_scale); lc = lc * barrel_scale   # amplify elevation (scale the angle) past the source's max
        pb.rotation_quaternion = rq; pb.location = lc
        pb.keyframe_insert('rotation_quaternion', frame=end_frame); pb.keyframe_insert('location', frame=end_frame)
    bpy.ops.object.mode_set(mode='OBJECT')
    print("DEPLOY barrel retargeted to ready-frame %d over %d..%d (%d bones)" % (ready_frame, mid, end_frame, len(barrel_bones)))

# --- 5c. optional: SCALE the leg spread (argv[6] = factor; 0.5 = half as wide). The legs fold->spread in the source; we
#         scale the spread rotation via slerp(identity, full, factor) so the deployed stance is narrower, no re-authoring. ---
if len(argv) > 6 and argv[6].strip():
    leg_scale = float(argv[6])
    end_frame = int(argv[3]) if len(argv) > 3 else fmax
    spread_frame = max(int(end_frame * 0.5), 1)   # legs are fully spread by mid-deploy
    leg_bones = [bn for bn in bone_of.values() if "leg" in bn.lower()]
    scene.frame_set(fmin)                                                                          # true INITIAL (travel) pose
    folded = {bn: arm.pose.bones[bn].rotation_quaternion.copy() for bn in leg_bones}
    scene.frame_set(spread_frame)                                                                  # fully-spread pose
    full = {bn: arm.pose.bones[bn].rotation_quaternion.copy() for bn in leg_bones}
    scaled = {bn: folded[bn].slerp(full[bn], leg_scale) for bn in leg_bones}                       # 0 = initial, 1 = full spread
    clear_bone_channels(arm.animation_data.action, leg_bones)
    bpy.context.view_layer.objects.active = arm
    bpy.ops.object.mode_set(mode='POSE')
    for bn in leg_bones:      # INITIAL at start, SCALED spread by mid, held to the end (scale 0 = stay at initial width)
        pb = arm.pose.bones[bn]
        pb.rotation_quaternion = folded[bn]
        pb.keyframe_insert('rotation_quaternion', frame=fmin)
        pb.rotation_quaternion = scaled[bn]
        pb.keyframe_insert('rotation_quaternion', frame=spread_frame)
        pb.keyframe_insert('rotation_quaternion', frame=end_frame)
    bpy.ops.object.mode_set(mode='OBJECT')
    print("DEPLOY legs scaled x%.2f from initial (%d bones), spread by %d held to %d" % (leg_scale, len(leg_bones), spread_frame, end_frame))

# --- 5d. optional: RECOIL-ON-FIRE tail — EXTRACT the source's own kickback (argv[8]=recoilSrcStart, argv[9]=recoilSrcEnd,
#         argv[10]=step default 2) and remap it onto the deployed pose as a tail after the deploy. The source clip already
#         animates a real firing recoil (the tube slams back + down then slowly runs out); we transfer that rigid motion,
#         expressed relative to the source's aim pose, onto OUR deployed hold — faithful to the original, not synthesized.
#         The runtime plays this tail once on ArtilleryStrikeStarted from the deployed hold (deployPoseTime = deployEnd/outEnd).
#         Same clip, no extra slot. Carriage/legs stay planted (only the barrel/cannon bones get keys). ---
recoil_out_end = None
if len(argv) > 8 and argv[8].strip():
    from mathutils import Matrix, Quaternion
    deploy_end = int(argv[3])
    rs, re = _segments[0][0], _segments[0][1]                  # SEGMENT 1 only — the actual fire window drives slide/arc
    step = int(argv[10]) if len(argv) > 10 and argv[10].strip() else 2
    recoil_bones = [bn for bn in bone_of.values() if any(k in bn.lower() for k in ("barrel", "cannon"))]
    bone_to_src = {bone_of[p.name]: p for p in parts if p.name in bone_of}   # bone -> its source node (still animated 0..fmax)
    # parents before children so a child's local back-solves against the parent's ALREADY-posed recoil
    def bone_depth(bn):
        d = 0; b = arm.data.bones[bn].parent
        while b: d += 1; b = b.parent
        return d
    ordered = sorted([bn for bn in recoil_bones if bn in bone_to_src], key=bone_depth)
    if not ordered:
        # A silent empty set used to crash the max() below with a bare ValueError traceback (exit 0, "produced no
        # GLB" downstream) — fail loudly with the fix instead: the tube match is a NAME substring.
        print("DEPLOY ERROR: recoil requested but no animated part name contains 'barrel'/'cannon' — cannot pick the tube. Animated parts: %s" % ", ".join(sorted(bone_of.values())))
        sys.exit(1)

    # Phase A — read the source: capture each tube node's world matrix at the aim frame + across the recoil.
    frames = list(range(rs, re + 1, step))
    if frames[-1] != re: frames.append(re)
    scene.frame_set(rs)
    m_aim = {bn: bone_to_src[bn].matrix_world.copy() for bn in ordered}
    src_w = {bn: {} for bn in ordered}
    for t in frames:
        scene.frame_set(t)
        for bn in ordered:
            src_w[bn][t] = bone_to_src[bn].matrix_world.copy()

    # Phase B — write onto the bones: hold the scene at deploy_end (carriage/legs deployed), pose the recoil bones for each
    # mapped frame f = deploy_end + (t - rs), and key them. target = home @ (aim^-1 @ src_t)  = the source's relative motion
    # (in the aim's own frame) applied to our deployed pose. Parents first + a depsgraph update so children back-solve right.
    bpy.context.view_layer.objects.active = arm
    bpy.ops.object.mode_set(mode='POSE')
    scene.frame_set(deploy_end)
    m_home = {bn: arm.pose.bones[bn].matrix.copy() for bn in ordered}
    prev_q = {}
    # A bone's OWN local translation is DROPPED by the bake (verified: sliding cannon2's local left its baked bbox unchanged) —
    # only ROTATION survives. But a bone's position derived from an ANCESTOR's rotation DOES bake (forward kinematics). So add a
    # hidden RECOIL-ARM bone with its pivot placed FAR from the tube, reparent the tube under it, and rotate the ARM: the tube
    # swings through a long arc that, over the recoil distance, reads as a near-straight backward SLIDE. The arm's ROTATION bakes;
    # the tube's arc is rebuilt by FK at runtime. Only the tube hangs off the arm (wheels/legs are untouched). argv[11] = slide
    # magnitude scale (default 1 = the source distance). Driven by the source's clean barrel-relative-to-cradle slide profile.
    driver = max(ordered, key=lambda bn: max((src_w[bn][t].translation - m_aim[bn].translation).length for t in frames))
    parent_bone = arm.data.bones[driver].parent
    cradle = parent_bone.name if parent_bone and parent_bone.name in bone_to_src else driver
    tube_root = cradle if cradle in m_home else driver
    mag = float(argv[11]) if len(argv) > 11 and argv[11].strip() else 1.0
    if mag == 0.0:
        # a zero slide scale annihilates the slide profile — and with it the SLAM layer's amplitude curve,
        # regardless of the requested degrees (a hidden legacy field silently killing a visible knob). 0 is
        # meaningless here; treat as 1.
        mag = 1.0
        print("DEPLOY slide scale 0 treated as 1 (zero would silently kill the Slam)")
    if cradle not in src_w:
        # The tube's parent can be ANY animated part ('cradle', 'mount' — only the M114's barrel-named parent masked
        # this): Phase A sampled just the barrel/cannon bones, so dereferencing the parent below was a guaranteed
        # KeyError on other naming. Sample the parent's world matrices over the same frames.
        src_w[cradle] = {}
        for t in frames:
            scene.frame_set(t)
            src_w[cradle][t] = bone_to_src[cradle].matrix_world.copy()
        scene.frame_set(deploy_end)   # restore the deployed hold the back-solve below relies on
    Sc_aim = src_w[cradle][rs]; Sb_aim = src_w[driver][rs]
    Cbar3 = (m_home[driver] @ Sb_aim.inverted()).to_3x3()
    slide = {}
    for t in frames:
        bt = Sc_aim @ (src_w[cradle][t].inverted() @ src_w[driver][t])   # barrel world, cradle FROZEN at aim = clean slide (no re-aim)
        slide[t] = (Cbar3 @ (bt.translation - Sb_aim.translation)) * mag  # slide vector, output space
    peak = max(slide.values(), key=lambda v: v.length)
    dist = peak.length or 1.0
    d = peak.normalized()                                      # slide direction (output)
    A = d.cross(Vector((0, 0, 1)))
    if A.length < 1e-4: A = d.cross(Vector((0, 1, 0)))
    A = A.normalized()                                         # arc rotation axis (perp to slide, horizontal-ish)
    # SLAM IN DEGREES (user-designed, 2026-07-19): the recipe states the desired kick PITCH directly (argv[14]);
    # the arc radius is derived so the rendered in-game peak equals it exactly (Law 5: the arc renders as a tube
    # pitch of peak_dist/R radians). DEFAULT 0 = NO kick pitch — the slam exists only when a modder asks for
    # degrees. Legacy Arc R (argv[12]) is honored only for old recipes that set it explicitly.
    _slam_deg = float(argv[14]) if len(argv) > 14 and argv[14].strip() else 0.0
    if abs(_slam_deg) > 0.0:
        # SIGNED slam (user request): positive = muzzle-DOWN dip (the legacy look), NEGATIVE = muzzle-UP jump
        # (the same arc mirrored — a negative radius flips theta's sign through the identical math).
        R = dist * 57.2958 / _slam_deg
        print("DEPLOY slam %.1f deg -> derived Arc R %.1f (peak slide %.1f)%s" % (_slam_deg, R, dist, " [REVERSED: muzzle-up]" if _slam_deg < 0 else ""))
    elif len(argv) > 12 and argv[12].strip():
        R = float(argv[12])   # legacy explicit Arc R
    else:
        R = 1.0e9             # slam 0/off: near-zero theta — the arm holds identity, no kick pitch
        print("DEPLOY slam 0 — no kick pitch (arm stays identity)")
    radius = A.cross(d).normalized()
    tube_head = m_home[tube_root].translation.copy()
    # PIVOT DISTANCE CLAMP (2026-07-25, the collapsed-chain bug): slam 0 uses the R=1e9 sentinel — placing the
    # arm's HEAD a billion units out. A bone at 1e9 destroys its whole subtree via float32 catastrophic
    # cancellation (the barrel chain's ~5-unit offsets vanish against 1e9; all rests collapse onto one point,
    # and the glTF export manufactures degenerate joints from the coincident chain). The pivot only needs to be
    # far ENOUGH: theta uses the true R, so a 1000-unit cap renders identically (theta~1e-8 -> ~1e-5 displacement)
    # while every bone stays in clean float range. Slam>0 recipes (R ~ tens-hundreds) are untouched by the cap.
    pivot = tube_head - radius * min(R, 1000.0)                # place the pivot R away (capped), perpendicular to the slide

    # insert a RecoilArm bone (head=pivot) between the tube and its parent
    bpy.ops.object.mode_set(mode='EDIT')
    ra = arm_data.edit_bones.new("RecoilArm"); ra_name = ra.name
    ra.head = pivot; ra.tail = pivot + A * 10.0
    teb = arm_data.edit_bones[tube_root]
    ra.parent = teb.parent
    teb.parent = ra
    bpy.ops.object.mode_set(mode='POSE')
    scene.frame_set(deploy_end)                               # parents held at their deployed pose
    # STRIP-SAFE ARC (2026-07-19, byte-gate finding): the shipping pipeline DROPS per-bone translation, so any
    # location component in the arm's keys silently dies and the tube drifts (the old matrix-assignment targets
    # decomposed into rotation+location whenever the parent chain had moved off rest — even the long-proven
    # legacy file carries a latent mid-deploy contamination from this, invisible at map zoom). The arm's HEAD
    # sits exactly at the arc pivot, so the arc is expressible as PURE LOCAL ROTATION about an axis converted
    # into the arm's own frame — zero location BY CONSTRUCTION; the strip has nothing left to break. The pivot
    # also now follows the deployed carriage (parents moved the head with them), which is physically right.
    prev_q.clear()
    _pbra = arm.pose.bones[ra_name]
    def key_arm_identity(f):                                  # the true no-op at any parent pose — and strip-safe
        _pbra.rotation_quaternion = (1.0, 0.0, 0.0, 0.0); _pbra.location = (0.0, 0.0, 0.0)
        _pbra.keyframe_insert('location', frame=f); _pbra.keyframe_insert('rotation_quaternion', frame=f)
    for hold in (0, deploy_end):                              # identity basis through the whole deploy so it can't disturb it
        key_arm_identity(hold)
    bpy.context.view_layer.update()
    A_local = (_pbra.matrix.to_3x3().inverted() @ A).normalized()   # world arc axis -> the arm's local frame (identity basis at the hold)
    def key_theta(f, theta):
        q = Quaternion(A_local, theta)
        if 'ra' in prev_q and q.dot(prev_q['ra']) < 0.0: q.negate()
        _pbra.rotation_quaternion = q; _pbra.location = (0.0, 0.0, 0.0); prev_q['ra'] = q
        _pbra.keyframe_insert('location', frame=f); _pbra.keyframe_insert('rotation_quaternion', frame=f)
    thetas = []
    _arc_by_src = {}                                          # source frame -> theta, reused by 7c's recoil role (Slam layer)
    for t in frames:
        theta = -(slide[t].length) / R * (1 if slide[t].dot(d) >= 0 else -1)   # arc length R*theta along the slide dir
        key_theta(deploy_end + (t - rs), theta)
        thetas.append(theta)
        _arc_by_src[t] = theta
    _arc_axis = A_local
    kick_end = deploy_end + (frames[-1] - rs)
    # PALINDROME RETURN (user-designed, 2026-07-19): the source's post-slam frames are usually RELOAD choreography
    # (the crew lowers the barrel), not a run-out — so give deployRecoil the SLAM ONLY and the return is synthesized
    # here: the same kick played BACKWARD, slowed by argv[13] (default 4 = a quarter-speed glide back into battery;
    # 0 = no return, the kick holds and the idle hold snaps the tube forward). The whole cycle lives in the
    # generated 'recoil' role clip — set the Attack clip to plain `recoil`, no frame math.
    ret_slow = int(argv[13]) if len(argv) > 13 and argv[13].strip() else 4
    f = kick_end
    if ret_slow > 0:
        for i in range(len(thetas) - 2, -1, -1):          # reversed, skipping the peak frame we're already on
            f += step * ret_slow
            key_theta(f, thetas[i])
    recoil_out_end = f
    key_arm_identity(recoil_out_end + 1)                  # settle exactly into the pass-through hold
    recoil_out_end += 1
    print("DEPLOY recoil return: %s" % ("x%d slow-back glide" % ret_slow if ret_slow > 0 else "none (hold + snap)"))
    bpy.ops.object.mode_set(mode='OBJECT')
    print("DEPLOY recoil (ARC slide x%g, R=%g, peak=%.1f) tail %d..%d via RecoilArm; tube '%s'" %
          (mag, R, dist, deploy_end, recoil_out_end, tube_root))

# --- 6. bind each mesh 100% to the bone of its nearest animated ancestor (rigid) ---
def anim_ancestor(o):
    while o:
        try:
            nm = o.name
        except ReferenceError:
            return None   # chain runs into a CULLED (deleted) object — treat as no animated ancestor
        if nm in bone_of:
            return nm
        try:
            o = o.parent
        except ReferenceError:
            return None
    return None

scene.frame_set(fmin)   # CRITICAL: bind at the rest frame (matches the armature rest), NOT wherever a retarget left the
                        # scene — else the mesh is baked in a posed (spread) position and the animation deforms it AGAIN.
_live_meshes = []
for m in meshes:
    try:
        m.name; _live_meshes.append(m)
    except ReferenceError:
        pass   # deleted by the degenerate-part cull
meshes = _live_meshes
bound = 0
for m in meshes:
    bname = anim_ancestor(m)
    if not bname:
        bname = static_root
        print("DEPLOY static mesh '%s' -> StaticRoot (no animated ancestor)" % m.name)
    # detach from the old animated parent, keeping world transform at fmin
    mw = m.matrix_world.copy()
    m.parent = None
    m.matrix_world = mw
    # one vertex group = the bone, all verts at weight 1 (rigid)
    for vg in list(m.vertex_groups):
        m.vertex_groups.remove(vg)
    vg = m.vertex_groups.new(name=bname)
    vg.add(range(len(m.data.vertices)), 1.0, 'REPLACE')
    for mod in [md for md in m.modifiers if md.type == 'ARMATURE']:
        m.modifiers.remove(mod)
    am = m.modifiers.new("arm", 'ARMATURE')
    am.object = arm
    m.parent = arm   # object-parent to the armature (standard skinned setup)
    m.matrix_world = mw
    bound += 1
print("DEPLOY bound %d meshes" % bound)

# --- 7. delete the now-redundant animated empties (their motion lives on the bones) ---
for p in list(parts):
    if p.type != 'MESH':
        bpy.data.objects.remove(p, do_unlink=True)

# --- 7b. keep ONLY the armature's baked action; strip every other object's animation + purge stray actions,
#         so the export produces ONE clean deploy clip (not 17 leftover per-part animations) ---
arm_action = arm.animation_data.action if arm.animation_data else None
for o in bpy.data.objects:
    if o is not arm and o.animation_data:
        o.animation_data_clear()
for a in list(bpy.data.actions):
    if a is not arm_action:
        bpy.data.actions.remove(a)
if arm_action:
    arm_action.name = "deploy"   # clean clip name for the Factory picker (was an auto 'Action.NNN')
print("DEPLOY kept 1 action:", arm_action.name if arm_action else None)

# --- 7c. ROLE CLIPS for the STATE-DRIVEN machine (howitzer migration prep, 2026-07-19): sample the baked deploy
#         action into separate actions so the Animation Lab can assign one per state:
#           deployed = idle stance (2 identical frames at the deploy end)   folded = move stance (travel pose)
#           unfold   = after-movement one-shot (the deploy segment)         fold   = PRE-movement one-shot (reversed)
#           recoil   = attack one-shot (the recoil tail, when present)
#         The legacy single 'deploy' action is kept unchanged (and stays the active one), so existing legacy-path
#         bakes are byte-identical; the new actions just ride along in the GLB for the Lab's Pick dropdowns. ---
if arm_action:
    deploy_end = int(argv[3]) if len(argv) > 3 else fmax
    tail_end = recoil_out_end if recoil_out_end is not None else None
    for pb in arm.pose.bones:
        pb.rotation_mode = 'QUATERNION'
    _last = tail_end if (tail_end is not None and tail_end > deploy_end) else deploy_end
    _snap = {}
    for f in range(fmin, _last + 1):    # snapshot the evaluated pose basis per frame (what the bake keyed)
        scene.frame_set(f)
        bpy.context.view_layer.update()
        _snap[f] = {pb.name: (pb.location.copy(), pb.rotation_quaternion.copy()) for pb in arm.pose.bones}
    def make_role(name, frames, snaps=None, arm_override=None):
        src = snaps if snaps is not None else _snap
        a = bpy.data.actions.new(name)
        arm.animation_data.action = a
        try: arm.animation_data.action_slot = a.slots.new(id_type='OBJECT', name=arm.name)   # Blender 4.4+/5 slotted actions
        except Exception: pass
        for i, f in enumerate(frames):
            for pb in arm.pose.bones:
                if arm_override is not None and pb.name == arm_override[0]:
                    pb.location = (0.0, 0.0, 0.0)
                    pb.rotation_quaternion = arm_override[1][i]      # the Slam layer: per-output-frame arm quaternion
                elif pb.name in src[f]:
                    loc, quat = src[f][pb.name]
                    pb.location = loc
                    pb.rotation_quaternion = quat
                else:
                    # bone born after this snapshot (the RecoilArm vs the pristine 5a fire window) — identity no-op
                    pb.location = (0.0, 0.0, 0.0)
                    pb.rotation_quaternion = (1.0, 0.0, 0.0, 0.0)
                # keyed from fmin so every role stays inside the export frame range (rig_anim re-clamps per role later)
                pb.keyframe_insert('location', frame=fmin + i)
                pb.keyframe_insert('rotation_quaternion', frame=fmin + i)
        return a
    _dep = list(range(fmin, deploy_end + 1))
    make_role("unfold", _dep)
    make_role("fold", list(reversed(_dep)))
    make_role("folded", [fmin, fmin])            # 2 identical frames: a valid HELD pose (0-length clips can be dropped by importers)
    make_role("deployed", [deploy_end, deploy_end])
    has_recoil = len(_fire_snap) > 0
    if _recoil_off and _had_recoil_arg:
        make_role("recoil", [deploy_end, deploy_end])   # graceful no-op: the attack holds the deployed stance
        has_recoil = True
    elif has_recoil:
        # THE FULL FIRE CYCLE (user-designed): the 'recoil' role plays the PRISTINE source fire window — the real
        # barrel lowering-to-reload and every other rotational content 5b would have erased — with the SLAM layered
        # on the RecoilArm (theta from 5d's slam-derived arc, interpolated per source frame; identity outside the
        # kick). The palindrome return (argv[13], default 4) plays the whole thing backward slowed, raising the
        # barrel back to battery and gliding the kick home; 0 = no return (ends as the source ends).
        rs2, re2 = _segments[0][0], _segments[0][1]
        ret2 = int(argv[13]) if len(argv) > 13 and argv[13].strip() else 4
        fwd = list(range(rs2, re2 + 1))
        frames2 = list(fwd) + (list(reversed(fwd[:-1])) if ret2 > 0 else [])
        # EPILOGUE SEGMENTS (multi-range recoil): appended pristine after the fire cycle (+return), arm identity —
        # e.g. the M114's aiming-raise window replayed to bring the barrel back up after the reload. The /N step
        # plays a segment every Nth frame = N x faster (always landing on the end frame).
        _epilogue = []
        for _ss, _se, _st in _segments[1:]:
            _fr = list(range(_ss, _se + 1, _st))
            if _fr and _fr[-1] != _se:
                _fr.append(_se)
            _epilogue += _fr
        frames2 += _epilogue
        # per-entry arm quaternion: linear-interpolate theta between 5d's sampled arc keys (dict on source frames)
        def theta_at(t):
            if not _arc_by_src: return 0.0
            ks = sorted(_arc_by_src)
            if t <= ks[0]: return _arc_by_src[ks[0]]
            if t >= ks[-1]: return _arc_by_src[ks[-1]]
            lo = max(k for k in ks if k <= t); hi = min(k for k in ks if k >= t)
            if lo == hi: return _arc_by_src[lo]
            w = (t - lo) / float(hi - lo)
            return _arc_by_src[lo] * (1 - w) + _arc_by_src[hi] * w
        # THE SLAM IS A SPIKE — forward pass only, and SHORT by construction. Two field findings shaped this:
        # (1) mirroring the arm into the palindrome return re-enacted the kick in reverse at the cycle's end
        # ("recoiled twice"); (2) the raw slide profile keeps the tube displaced through the whole reload, so the
        # pitch lingered for seconds ("active too long"). The arm now follows the profile only UP TO the slam's
        # peak, then plays the same rise MIRRORED back to zero — a symmetric snap of ~2x the natural attack time
        # (~half a second), independent of how long the recoil window is. Identity everywhere else.
        _t_peak = max(_arc_by_src, key=lambda k: abs(_arc_by_src[k])) if _arc_by_src else 0
        _settle = float(argv[15]) if len(argv) > 15 and argv[15].strip() else 1.0   # Slam settle: recovery takes N x the rise (1 = symmetric snap)
        if _settle <= 0.0: _settle = 1.0
        def slam_theta(t):
            if not _arc_by_src: return 0.0
            if t <= _t_peak: return theta_at(t)                     # the rise, exactly as the source slams
            m = _t_peak - (t - _t_peak) / _settle                   # mirrored decay, stretched by the settle factor
            return theta_at(m) if m >= rs2 else 0.0
        arm_quats = [Quaternion(_arc_axis, slam_theta(t)) if (_arc_by_src and i < len(fwd)) else Quaternion((1.0, 0.0, 0.0, 0.0))
                     for i, t in enumerate(frames2)]
        make_role("recoil", frames2, snaps=_fire_snap, arm_override=(ra_name, arm_quats))
        print("DEPLOY recoil role: PRISTINE fire cycle %d..%d (barrel choreography intact) + Slam layer%s%s" %
              (rs2, re2, (" + palindrome return x%d" % ret2) if ret2 > 0 else " (no return)",
               (" + epilogue %s (%d frames)" % (", ".join("%d..%d" % s for s in _segments[1:]), len(_epilogue))) if _epilogue else ""))
    arm.animation_data.action = arm_action       # the legacy action stays active (legacy bakes untouched)
    print("DEPLOY role clips: unfold/fold/folded/deployed%s (+ legacy 'deploy')" % ("/recoil" if has_recoil else ""))

# --- 8. export GLB, trimmed to the DEPLOY (+recoil tail) sub-range if given (argv: in out [start] [end] ...) ---
if len(argv) >= 4:
    trim_end = recoil_out_end if recoil_out_end is not None else int(argv[3])   # extend past the deploy to include the recoil tail
    scene.frame_start, scene.frame_end = int(argv[2]), trim_end
    print("DEPLOY trim to frames %d..%d" % (scene.frame_start, scene.frame_end))
bpy.ops.object.select_all(action='SELECT')
# PURGE SOURCE SCAFFOLDING (2026-07-25, the export-garbage riddle SOLVED): after binding, the scene still
# carried ~39 leftover source objects (the .fbx root empty, lights, cameras, locators, group empties — the
# COMPENSATED-SCALE chain among them). The glTF export wrote them as nodes; re-import folded same-named nodes
# into the bone hierarchy as garbage joints/curves (rest heads ~1e10, curves ~1e11) while the actual BONES were
# clean all along (probe-verified: barrel1's real recoil slide lives as sane location keys post-bake). The
# output needs exactly the armature + its bound meshes (all re-parented to the armature at bind); delete
# everything else.
_keepset = set(o for o in bpy.data.objects if o.type == 'MESH')
_keepset.add(arm)
_purged = 0
for _o in list(bpy.data.objects):
    if _o not in _keepset:
        bpy.data.objects.remove(_o, do_unlink=True); _purged += 1
if _purged:
    print("DEPLOY purged %d leftover source object(s) (empties/lights/cameras/locators/groups)" % _purged)

# SANITIZE BEFORE EXPORT (2026-07-25, found via the howitzer kickback work): degenerate SOURCE nodes (zero-scale
# ancestors — the m114's door/handle/barrel1 chain) decompose into garbage keys (locations ~1e11, scales 0..5e14,
# NaN) that ride into the GLB, NaN the Unity import and explode skinned bounds. Any bone with a non-finite or
# absurd key loses ALL its curves — it holds rest pose and rides its parent, which is visually what those broken
# micro-parts did anyway (the old location-strip masked half of this for every previous bake).
import math as _math
_garbage_bones = set()
for _act in bpy.data.actions:
    try: _fcs = list(_act.fcurves)
    except AttributeError:
        _fcs = [fc for layer in _act.layers for strip in layer.strips for cb in strip.channelbags for fc in cb.fcurves]
    for _fc in _fcs:
        if not _fc.data_path.startswith('pose.bones["'):
            continue
        for _kp in _fc.keyframe_points:
            _v = _kp.co[1]
            if _v != _v or _math.isinf(_v) or abs(_v) > 1e6:
                _garbage_bones.add(_fc.data_path.split('"')[1]); break
if _garbage_bones:
    _removed_fc = 0
    for _act in bpy.data.actions:
        try: _fcs = list(_act.fcurves); _coll = _act.fcurves
        except AttributeError:
            _fcs = [(cb, fc) for layer in _act.layers for strip in layer.strips for cb in strip.channelbags for fc in cb.fcurves]
        if _fcs and isinstance(_fcs[0], tuple):
            for _cb, _fc in list(_fcs):
                if _fc.data_path.startswith('pose.bones["') and _fc.data_path.split('"')[1] in _garbage_bones:
                    _cb.fcurves.remove(_fc); _removed_fc += 1
        else:
            for _fc in list(_fcs):
                if _fc.data_path.startswith('pose.bones["') and _fc.data_path.split('"')[1] in _garbage_bones:
                    _coll.remove(_fc); _removed_fc += 1
print("DEPLOY sanitized: %d garbage bone(s) de-animated (%d curves) — rest-pose ride: %s"
      % (len(_garbage_bones), _removed_fc if _garbage_bones else 0, sorted(_garbage_bones) if _garbage_bones else "none"))

bpy.ops.export_scene.gltf(filepath=outp, export_format='GLB', export_animations=True,
                          export_frame_range=True, export_yup=True)
print("DEPLOY wrote:", outp)
