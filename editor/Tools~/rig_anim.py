# rig_anim.py — prepare a RIGGED, ANIMATED model for the Universal Model Factory's animated path.
# Unlike mesh_reduce.py (which only decimates geometry), this KEEPS the model's armature + one animation clip so the
# Factory can bake an Amplitude Skeleton + ClipCollection from it. Steps:
#   1. import (glb/gltf/fbx/blend) and drop material-less junk meshes (stray icospheres etc.)
#   2. pick one action, assign it, delete the rest
#   3. OPTIONAL bone filter: keep animation only on bones whose name starts with one of the given prefixes
#      (e.g. "prop,rotor") — strips camera/body-bob curves that make a model wobble; empty = keep the whole clip
#   4. clamp the SCENE frame range to the action's real range — bake_anim otherwise pads frozen tail frames (default
#      1..250) that make the animation stall for ~1s each loop
#   5. export the first material's base-colour image as the albedo PNG (for the Factory's atlas)
#   6. join to 1 mesh + 1 material + quadric-decimate to ~target tris (KEEPING the armature + weights)
#   7. export a slim FBX with baked animation
# Usage: blender -b --python rig_anim.py -- <input> <output.fbx> <targetTris> [bonePrefixesCSV] [clipName] [albedoOut.png] [keepMats] [rotXdeg,rotYdeg,rotZdeg] [convertRig]
import bpy, bmesh, sys, os
from math import radians
from mathutils import Matrix, Vector

argv = sys.argv[sys.argv.index("--") + 1:]
inp, outp, target = argv[0], argv[1], int(argv[2])
prefixes = [p.strip() for p in (argv[3] if len(argv) > 3 else "").split(",") if p.strip()]
clip_name = (argv[4] if len(argv) > 4 else "").strip()
albedo_out = argv[5] if len(argv) > 5 else ""
keep_materials = len(argv) > 6 and argv[6].strip() == "1"   # multi-material bake: keep the slots (submeshes) instead of collapsing to 1
# Optional rig ROTATION (degrees, registry semantics: x = pitch/stand-up, y = heading, z = roll). Some rigs round-trip
# glTF->Blender->FBX lying down or facing sideways (the Combine soldier bakes on his back); the game orients animated
# units by the RIG, so the fix must be baked into the rig here — the Factory's Rotation field is meaningless at runtime.
rig_rot = [0.0, 0.0, 0.0]
if len(argv) > 7 and argv[7].strip():
    try:
        rig_rot = [float(v) for v in argv[7].split(",")][:3] + [0.0] * max(0, 3 - len(argv[7].split(",")))
    except Exception:
        print("RIGANIM WARN: bad rotation arg '%s' — ignoring" % argv[7])
# EXPLICIT conversion switch (argv[8], "1"/"0"). Selects the RAW-RIG CONVERSION path: no-op root collapse, topological
# bone rename, rotation/scale fold into the data, clean-unit export (global_scale 0.01). It used to be inferred from
# rotation != 0 (the soldier shipped with a 360,0,0 identity trick; a rotation edit on a legacy model silently
# rerouted its bake) — the flag makes rotation just a rotation again. Absent arg = the old inference, so old callers
# keep their exact behavior.
if len(argv) > 8 and argv[8].strip() in ("0", "1"):
    convert_rig = argv[8].strip() == "1"
else:
    convert_rig = any(abs(v) > 1e-4 for v in rig_rot)
print("RIGANIM conversion path: %s" % ("ON (raw-rig convert)" if convert_rig else "off (legacy byte-identical)"))
# STATE-DRIVEN roles (argv[9], optional; Phase 2 2026-07-19): "role=clipName;role=clipName" (e.g.
# "move=Skel|a_RunN;after=Skel|Settle"). Each role exports the SAME prepared rig with that role's clip to a sibling
# folder anim_<role>/ next to the primary output — one FBX per folder, so each ClipCollection scan sees exactly one
# clip. CRITICAL for the conversion path: every role's clip must be rebaked against ONE shared rest (the PRIMARY
# clip's frame-0 pose) — converting roles in separate Blender runs would derive a DIFFERENT rest per clip and the
# non-primary clips would play rigidly displaced on the primary-baked skeleton (the torn-head failure, reborn).
role_specs = []   # ordered [(role, clipName)]
# keepTranslations (argv[12], opt-in, 2026-07-25 — the caterpillar unlock): keep VARYING bone-location curves
# through the strip below. The engine plays RotationTranslation curves (vanilla tank shuttle bones); the strip
# exists for the native-scale trap + legacy hygiene, so translations stay opt-in per model.
keep_translations = len(argv) > 12 and argv[12].strip() == "1"
# staticParts (argv[13], canoe finding 2026-07-30): comma-separated MESH/MATERIAL-name substrings the bone-parent->skin
# conversion must SKIP — the parts stay weightless (root-bone, static at their authored position) instead of being
# bound to a governing bone. For rigid decor whose skeleton's ANIMATED pose frame disagrees with the static node
# layout: the canoe's sail slats bound to rib bones that sit 100+ units from rest at EVERY frame (they ride the
# animated joint chain), so the rest-normalize frame-0 fold dragged the sail off its (static, never-bound) mast.
static_parts = [p.strip().lower() for p in (argv[13] if len(argv) > 13 else "").split(",") if p.strip()]
# localNodeAnim (argv[14], canoe finding 2026-07-30): TRANSPLANT object-level node animation into LOCAL-DELTA bones
# instead of clearing it. For models whose motion lives on NODES (a canoe's hull rock / log bob / paddle strokes /
# sail sway), where Blender's re-composition of the full hierarchy SCATTERS the parts (the animated pose frame
# disagrees with the static layout — the same defect that displaced the sail; a world-space visual bake, i.e.
# deploy_convert, faithfully bakes the scattering). Local-delta sidesteps the composition entirely: one bone per
# animated node AT ITS STATIC PLACEMENT, keyed with only that node's OWN wiggle relative to its own static TRS —
# parts stay assembled by construction and move the way the author keyed them locally. Off = the plain clear
# (existing models bake byte-identically).
local_node_anim = len(argv) > 14 and argv[14].strip() == "1"
# CLEAN-UNIT sources export with global_scale=0.01 (net node scale 1) — deploy_convert output qualifies since
# the 2026-07-26 identity-node/meter-vert/delta-form rework. Decided EARLY: gates both the legacy x100
# translation amplify (whose compensation the clean export does NOT need — the exporter's global_scale never
# touches ANIMATION curves, so amplified link crawls rendered 100x away, ~300-unit clip bboxes) and the export.
clean_units_input = False   # decided AFTER import from the armature's contract marker (see below)
_KEEP_LOC_PATHS = set()   # filled by the conversion rebake with the paths of genuinely translation-animated bones

if len(argv) > 9 and argv[9].strip():
    for _pair in argv[9].split(";"):
        _pair = _pair.strip()
        if _pair and "=" in _pair:
            _r, _cn = _pair.split("=", 1)
            if _r.strip() and _cn.strip():
                role_specs.append((_r.strip(), _cn.strip()))
if role_specs:
    print("RIGANIM state roles: %s" % ", ".join("%s='%s'" % rc for rc in role_specs))

# DONOR SOCKETS (argv[11], 2026-07-24): "DonorName=ParentSubstr[@x,y,z];..." — create EXACT-NAMED zero-weight leaf
# bones on our rig so the DONOR's fire/VFX events resolve NATIVELY (GetBoneTRS('Canon_Up_left') just FINDS the bone):
# muzzle flash, launch smoke and projectile origin all anchor correct-by-construction, following the parent bone
# (e.g. a tracking turret). This obsoletes the runtime interception chain (muzzleBone redirect/offset compensation)
# for re-baked models. The offset "@x,y,z" is in armature/model space, added to the parent bone's head.
# WHY THE PREFIX CHANGES: Amplitude sorts bones alphabetically and requires parents-first; a donor name (capital
# 'C'anon...) would sort BEFORE a 'b###_' parent — so socketed models rename with 'A###_' instead ('A' < any donor
# initial), keeping alphabetical == topological. A socket that still sorts before its parent fails the bake loudly.
socket_specs = []   # ordered [(donorName, parentSubstr, (x,y,z))]
if len(argv) > 11 and argv[11].strip():
    for _pair in argv[11].split(";"):
        _pair = _pair.strip()
        if not _pair or "=" not in _pair:
            continue
        _dn, _rest = _pair.split("=", 1)
        _off = (0.0, 0.0, 0.0)
        if "@" in _rest:
            _ps, _os = _rest.split("@", 1)
            try:
                _off = tuple(([float(v) for v in _os.split(",")] + [0.0, 0.0, 0.0])[:3])
            except Exception:
                print("RIGANIM WARN: bad socket offset '%s' — using 0,0,0" % _os)
        else:
            _ps = _rest
        if _dn.strip() and _ps.strip():
            socket_specs.append((_dn.strip(), _ps.strip(), _off))
if socket_specs:
    print("RIGANIM donor sockets requested: %s" % ", ".join("%s->%s" % (d, p) for d, p, _ in socket_specs))

bpy.ops.wm.read_factory_settings(use_empty=True)
ext = os.path.splitext(inp)[1].lower()
if ext == ".fbx":
    bpy.ops.import_scene.fbx(filepath=inp)
elif ext == ".blend":
    bpy.ops.wm.open_mainfile(filepath=inp)
else:
    bpy.ops.import_scene.gltf(filepath=inp)   # .glb / .gltf

# drop material-less junk meshes (e.g. a gltf placeholder icosphere)
for o in [o for o in bpy.context.scene.objects if o.type == 'MESH' and len(o.data.materials) == 0]:
    bpy.data.objects.remove(o, do_unlink=True)

arm = next((o for o in bpy.context.scene.objects if o.type == 'ARMATURE'), None)
if arm is None:
    print("RIGANIM ERROR: no armature found — the model is not rigged, use the normal (static) bake instead")
    sys.exit(1)
# CONTRACT MARKER (2026-07-27): deploy_convert names NEW-contract armatures "DeployArmV2" (identity nodes,
# meter verts, delta-form clips) — those get the clean-unit export + no legacy amplify. A plain "DeployArm"
# is a PRE-rework conversion (the m114's cached one): it gets the EXACT legacy handling its shipped, in-game-
# verified bake was produced with. Keyed off the FILE CONTENT, not the filename, so cached old conversions
# stay safe no matter what path they live at.
clean_units_input = arm.name.startswith("DeployArmV2")
if clean_units_input:
    print("RIGANIM contract: V2 (clean-unit deploy conversion)")

# HOLD REST THROUGH THE CAPTURE STAGES (canoe finding 2026-07-30): every capture below (bone-parent re-home,
# wrapper flatten) must see the AUTHORED rest layout — but with the armature's action assigned, bone-parented
# wrappers evaluate at the CURRENT FRAME's pose and the captures pin parts at scrambled animated positions.
# This was masked by the multi-slot bug (assign_action bound a mesh's slot, so the rig played statues); both the
# slot fix and this hold are GATED to localNodeAnim — the legacy static path's captures depend on the frozen-rig
# import state (holding REST instead collapsed the canoe's weightless sail onto the rib bones' rest cluster).
if local_node_anim:
    arm.data.pose_position = 'REST'
    bpy.context.view_layer.update()

# OBJECT-ANIMATION CLEAR (canoe finding 2026-07-30): rigid-hung parts often dangle from wrapper nodes carrying
# OBJECT-level animation (the dug-out canoe's 19 sail panels flap via rotation curves on their wrapper empties).
# That animation can never survive this pipeline (the wrappers are flattened below and every mesh is joined), but
# while it stays ASSIGNED it poisons both matrix_world captures below: the parts get pinned at the CURRENT FRAME's
# animated pose (the canoe's sail draped over the hull) instead of the authored rest TRS that Unity's importer
# preview shows. Clear object-level animation on every non-armature object FIRST so the captures see the authored
# rest. Bone animation lives on the ARMATURE object and is untouched. Gated to convertRig (legacy path unchanged);
# a no-op for rigs that animate bones only (soldier / mech / Ehrhardt — those have no object-level curves).
if convert_rig:
    _objanim = [o for o in bpy.context.scene.objects if o.type != 'ARMATURE' and o.animation_data is not None]
    # localNodeAnim v2 (clip-picker finding 2026-07-30): SAMPLE the EVALUATED per-frame world matrix of every mesh
    # BEFORE clearing — Blender's own depsgraph composition plays the take correctly (the inspection FBX the clip
    # picker shows is exactly this evaluation, and it plays assembled); it's only surgical re-derivations (per-node
    # deltas, world-space constraint bakes) that scatter a pathological hierarchy. The sampled worlds are sane at
    # every frame, so flat bones keyed from them can't inherit any far-out wrapper pivot.
    _mesh_track = {}
    _fr0 = _fr1 = 0
    if local_node_anim and any(_o.animation_data.action is not None for _o in _objanim):
        _fr0, _fr1 = 1e9, -1e9
        _acts0 = [arm.animation_data.action] if (arm.animation_data and arm.animation_data.action) else                  [_o.animation_data.action for _o in _objanim if _o.animation_data.action]
        for _a0 in _acts0:
            _fr0 = min(_fr0, _a0.frame_range[0]); _fr1 = max(_fr1, _a0.frame_range[1])
        _fr0, _fr1 = int(_fr0), int(_fr1)
        # ROUND-TRIP SAMPLING (the clip-picker finding, final form): Blender's LIVE evaluation of a pathological
        # glTF hierarchy is itself scrambled (bone-parented far-out wrappers) — but the inspection-FBX round-trip
        # (bake per-object local curves -> clean transform hierarchy) re-composes the SAME take correctly; that is
        # the file the clip picker demonstrably plays right. So: export the scene exactly the way inspect_fbx.py
        # does, re-import, and sample the mesh worlds THERE — then restore the original scene and continue.
        _rt_fbx = outp + ".nodeanim_roundtrip.fbx"
        bpy.context.scene.frame_start, bpy.context.scene.frame_end = _fr0, _fr1
        bpy.ops.export_scene.fbx(filepath=_rt_fbx, use_selection=False, add_leaf_bones=False,
                                 bake_anim=True, bake_anim_use_all_actions=False,
                                 bake_anim_use_nla_strips=False, object_types={'EMPTY', 'ARMATURE', 'MESH'})
        bpy.ops.wm.read_factory_settings(use_empty=True)
        bpy.ops.import_scene.fbx(filepath=_rt_fbx)
        _smeshes = [o for o in bpy.context.scene.objects if o.type == 'MESH']
        for _f in range(_fr0, _fr1 + 1):
            bpy.context.scene.frame_set(_f)
            bpy.context.view_layer.update()
            for _m in _smeshes:
                _l1, _r1, _s1 = _m.matrix_world.decompose()
                _mesh_track.setdefault(_m.name, []).append(Matrix.Translation(_l1) @ _r1.to_matrix().to_4x4())
        print("RIGANIM localNodeAnim: sampled %d mesh world track(s) over frames %d..%d from the inspection ROUND-TRIP (the composition the clip picker plays)" % (len(_mesh_track), _fr0, _fr1))
        # restore the ORIGINAL scene for the rest of the pipeline
        bpy.ops.wm.read_factory_settings(use_empty=True)
        if ext == ".fbx":
            bpy.ops.import_scene.fbx(filepath=inp)
        elif ext == ".blend":
            bpy.ops.wm.open_mainfile(filepath=inp)
        else:
            bpy.ops.import_scene.gltf(filepath=inp)
        for o in [o for o in bpy.context.scene.objects if o.type == 'MESH' and len(o.data.materials) == 0]:
            bpy.data.objects.remove(o, do_unlink=True)
        arm = next((o for o in bpy.context.scene.objects if o.type == 'ARMATURE'), None)
        _objanim = [o for o in bpy.context.scene.objects if o.type != 'ARMATURE' and o.animation_data is not None]
        try: os.remove(_rt_fbx)
        except Exception: pass
    for _o in _objanim:
        _o.animation_data_clear()
    if _objanim:
        bpy.context.view_layer.update()
        print("RIGANIM cleared object-level animation on %d non-armature node(s) — rigid-part captures use the authored rest TRS" % len(_objanim))


# BONE-PARENT -> SKIN-WEIGHT CONVERSION (mech finding 2026-07-20): many downloaded mech/vehicle rigs never SKIN their
# meshes (vertex weights); they RIGIDLY HANG each part off a bone via Blender bone-parenting (parent_type='BONE'),
# often through intermediate empties or parent meshes (bone -> empty -> mesh, or bone -> mesh -> child mesh). Blender
# animates that fine, but our pipeline JOINS all meshes into ONE skinned mesh and rebinds via VERTEX WEIGHTS —
# bone-parenting carries NO weights, so the join drops every part's bone and all verts fall to bone #0 (Unity warns
# "N verts with no weight -> assigned to bone #0"); the whole model then collapses onto the root bone in-game — it
# lies flat and the arms fling up. Fix: BEFORE the join, convert each part's bone-parenting into a FULL-WEIGHT vertex
# group on its governing bone + an armature modifier, so the join preserves per-part bone binding. Walk the parent
# chain to find the governing bone (a child hung off a bone-parented mesh follows that same bone). BIND AT REST so the
# armature deform is identity there — otherwise the modifier double-applies the current pose on top of the parented
# position. Gated to convertRig (properly skinned rigs need none of this); a no-op when nothing is bone-parented.
if convert_rig:
    def _governing_bone(o):
        cur = o
        while cur is not None and cur.parent is not None:
            if cur.parent_type == 'BONE' and cur.parent.type == 'ARMATURE' and cur.parent_bone:
                return cur.parent_bone
            cur = cur.parent
        return None
    def _is_static_part(o):
        if not static_parts:
            return False
        _names = [o.name.lower()] + [m.name.lower() for m in o.data.materials if m]
        return any(sp in n for sp in static_parts for n in _names)
    # localNodeAnim supersedes this block entirely: every mesh gets its own evaluated-world bone downstream, and a
    # rib-bone bind left here would double-weight the part 50/50 onto the scrambled joint frame (the crossed-mast bake).
    _bp = [] if (local_node_anim and _mesh_track) else \
          [o for o in bpy.context.scene.objects if o.type == 'MESH' and _governing_bone(o) is not None and not _is_static_part(o)]
    _skipped = [o.name for o in bpy.context.scene.objects if o.type == 'MESH' and _governing_bone(o) is not None and _is_static_part(o)]
    if _skipped:
        print("RIGANIM staticParts: %d bone-parented mesh(es) kept WEIGHTLESS (authored position, no bind): %s" % (len(_skipped), ", ".join(sorted(_skipped)[:8]) + ("…" if len(_skipped) > 8 else "")))
    if _bp:
        _root_bone = next((b.name for b in arm.data.bones if b.parent is None), None)
        _prev_pp = arm.data.pose_position
        arm.data.pose_position = 'REST'                        # bind against rest: bone delta is identity there
        bpy.context.view_layer.update()
        _plan = [(o, _governing_bone(o), o.matrix_world.copy()) for o in _bp]   # capture bone + rest world BEFORE reparenting
        _bound = 0
        for _o, _bone, _mw in _plan:
            _tgt = _bone if arm.data.bones.get(_bone) else _root_bone
            if _tgt is None:
                continue
            _o.parent = arm                                   # re-home onto the armature OBJECT (not the bone)
            _o.parent_type = 'OBJECT'
            _o.parent_bone = ''
            _o.matrix_parent_inverse = Matrix()               # clear the old bone-parent inverse ...
            _o.matrix_world = _mw                             # ... then pin the rest-pose world position exactly
            _vg = _o.vertex_groups.get(_tgt) or _o.vertex_groups.new(name=_tgt)
            _vg.add(list(range(len(_o.data.vertices))), 1.0, 'REPLACE')
            if not any(md.type == 'ARMATURE' for md in _o.modifiers):
                _am = _o.modifiers.new("Armature", 'ARMATURE'); _am.object = arm
            _bound += 1
        arm.data.pose_position = _prev_pp
        bpy.context.view_layer.update()
        print("RIGANIM bone-parent->skin: bound %d rigidly-hung mesh(es) full-weight to their governing bones (was: no weights -> bone #0)" % _bound)

# FLATTEN WRAPPER EMPTIES (mech finding 2026-07-20): glTF/FBX sources often wrap the rig in a parent empty (a
# "group"/scene-root) carrying a NON-IDENTITY scale — the Light Assault Mech's was 0.010. convertRig's later
# transform_apply only bakes an object's OWN transform, never an inherited parent scale, so that wrapper survived
# to the export as a scaled root node. Unity folds it into the mesh, but Amplitude's skeleton import reads bind
# poses WITHOUT it → the skeleton sits ~100× off the mesh and every rigid single-bone vert flings into a "wing".
# Un-parent the rig from any EMPTY (KEEP_TRANSFORM bakes the empty's transform onto the object, where convertRig's
# transform_apply then folds it into the data) and delete the empties → identity export nodes. Gated to convertRig
# so the legacy byte-identical path is untouched; a no-op for rigs without wrapper empties (the soldier).
if convert_rig:
    _wrapped = [o for o in bpy.context.scene.objects if o.type in ('MESH', 'ARMATURE') and o.parent is not None and o.parent.type == 'EMPTY']
    for _o in _wrapped:
        _mw = _o.matrix_world.copy()
        _o.parent = None
        _o.matrix_world = _mw
    _empties = [o for o in list(bpy.data.objects) if o.type == 'EMPTY']
    for _e in _empties:
        bpy.data.objects.remove(_e, do_unlink=True)
    if _empties:
        print("RIGANIM flattened %d wrapper empt%s; rig reparented to root for identity export nodes" % (len(_empties), "y" if len(_empties) == 1 else "ies"))

# EVALUATED-WORLD TRANSPLANT (localNodeAnim v2, clip-picker finding 2026-07-30). One FLAT bone per mesh, rest =
# the mesh's frame-0 EVALUATED world (rot+trans), keys = rest^-1 @ sampledWorld(f) — straight from the depsgraph
# composition the clip picker demonstrably plays correctly. No node-chain math, no wrapper pivots, no hierarchy:
# each part carries its own fully-composed motion. staticParts still excludes by mesh/material name.
if convert_rig and local_node_anim and _mesh_track:
    def _is_static_part2(o):
        if not static_parts:
            return False
        _names = [o.name.lower()] + [m.name.lower() for m in o.data.materials if m]
        return any(sp in n for sp in static_parts for n in _names)
    if not arm.animation_data:
        arm.animation_data_create()
    if arm.animation_data.action is None:
        _na0 = bpy.data.actions.new("NodeAnim")
        arm.animation_data.action = _na0
        try: arm.animation_data.action_slot = _na0.slots.new(id_type='OBJECT', name=arm.name)
        except Exception: pass
    _tmeshes = [o for o in bpy.context.scene.objects if o.type == 'MESH' and o.name in _mesh_track and not _is_static_part2(o)]
    # edit_bone.matrix lives in ARMATURE-LOCAL space — map the sampled worlds through the armature's inverse.
    # This block runs AFTER the wrapper flatten on purpose: that's when the armature has absorbed its wrapper's
    # transform (the glTF's ~0.01 unit wrapper), so armature space here matches the original bones' units.
    _armInv = arm.matrix_world.inverted()
    def _ortho(_M):
        _l2, _r2, _s2 = _M.decompose()
        return Matrix.Translation(_l2) @ _r2.to_matrix().to_4x4()
    bpy.ops.object.select_all(action='DESELECT')
    arm.select_set(True); bpy.context.view_layer.objects.active = arm
    bpy.ops.object.mode_set(mode='EDIT')
    _blen = 0.05 * max((arm.dimensions.length, 1.0))
    _rest0 = {}
    for _m in _tmeshes:
        _w0 = _ortho(_armInv @ _mesh_track[_m.name][0])
        _rest0[_m.name] = _w0
        _eb = arm.data.edit_bones.new("nd_" + _m.name)
        _eb.matrix = _w0.copy()
        _eb.length = _blen
    bpy.ops.object.mode_set(mode='OBJECT')
    # key via keyframe_insert on the ACTIVE action (the RNA path the proven rebake loop uses — manually built
    # channelbag fcurves existed in the file but never evaluated in the depsgraph, so the snapshots saw statues)
    for _m in _tmeshes:
        _pb = arm.pose.bones["nd_" + _m.name]
        _pb.rotation_mode = 'QUATERNION'
    _w0i = {_m.name: _rest0[_m.name].inverted() for _m in _tmeshes}
    _qprev = {}
    for _fi in range(_fr1 - _fr0 + 1):
        _f = _fr0 + _fi
        for _m in _tmeshes:
            _pb = arm.pose.bones["nd_" + _m.name]
            _d = _w0i[_m.name] @ _ortho(_armInv @ _mesh_track[_m.name][_fi])
            _dq = _d.to_quaternion()
            _qp = _qprev.get(_m.name)
            if _qp is not None and _qp.dot(_dq) < 0.0:
                _dq.negate()
            _qprev[_m.name] = _dq.copy()
            _pb.rotation_quaternion = _dq
            _pb.keyframe_insert("rotation_quaternion", frame=_f)
            _pb.location = _d.to_translation()
            _pb.keyframe_insert("location", frame=_f)
    # bind each mesh AT its frame-0 world (the pose the bone rest represents; deform = identity there)
    for _m in _tmeshes:
        _m.parent = arm; _m.parent_type = 'OBJECT'; _m.parent_bone = ''
        _m.matrix_parent_inverse = Matrix()
        _m.matrix_world = _mesh_track[_m.name][0].copy()
        _m.vertex_groups.clear()   # imported skin weights would split influence 50/50 with the nd_ bone — full ownership
        _vg = _m.vertex_groups.get("nd_" + _m.name) or _m.vertex_groups.new(name="nd_" + _m.name)
        _vg.add(list(range(len(_m.data.vertices))), 1.0, 'REPLACE')
        if not any(md.type == 'ARMATURE' for md in _m.modifiers):
            _am = _m.modifiers.new("Armature", 'ARMATURE'); _am.object = arm
    bpy.context.view_layer.update()
    print("RIGANIM localNodeAnim v2: %d mesh bone(s) keyed from EVALUATED per-frame worlds (frames %d..%d)" % (len(_tmeshes), _fr0, _fr1))

if not arm.animation_data:
    arm.animation_data_create()
def assign_action(a):
    arm.animation_data.action = a
    try:
        _slots = list(getattr(a, "slots", []) or [])
        if _slots and local_node_anim:
            # MULTI-SLOT TRAP (canoe finding 2026-07-30): a glTF import can pack EVERY object's animation into ONE
            # action with one slot per object — slots[0] is then some mesh/empty's slot, and evaluating the armature
            # against it plays STATUES. localNodeAnim needs real evaluation, so pick the slot whose channelbag
            # carries pose.bones curves. GATED to that mode: the legacy static path's captures DEPEND on the frozen
            # rig (fixing the slot for everyone moved the canoe's weightless sail — the un-frozen ribs relocated the
            # flatten captures), and FBX imports are single-slot so nothing else ever hit this.
            _best = None
            for _s in _slots:
                for _ly in getattr(a, "layers", []):
                    for _st in _ly.strips:
                        _cb = _st.channelbag(_s) if hasattr(_st, "channelbag") else None
                        if _cb is not None and any(fc.data_path.startswith("pose.bones") for fc in _cb.fcurves):
                            _best = _s
                            break
                    if _best is not None: break
                if _best is not None: break
            arm.animation_data.action_slot = _best if _best is not None else _slots[0]
        elif _slots:
            arm.animation_data.action_slot = _slots[0]   # legacy: byte-faithful to every proven bake
    except Exception as e:
        print("RIGANIM slot warn:", e)

# BONE CAP (mech finding, 2026-07-20): Amplitude's GPU crowd-skinning caps at 256 bones; verts weighted to a bone
# index >255 get garbage transforms and stretch into huge "wing" spikes in-game (invisible in Blender, which has no
# limit). Detailed mech/robot rigs blow past it (the Light Assault Mech had 332 bones; 4084 verts / 5.6% flung).
# Fix: merge LEAF bones (no surviving children) into their nearest surviving ancestor — transferring their skin
# weights so the geometry follows the parent limb — prioritizing mechanical DETAIL (pistons/tubes/_end/targets),
# until under a safe budget. No-op for rigs already under it (soldier 62, howitzer ~27), so proven models are
# byte-identical. The important limb/gun bones are never leaves, so the animation is preserved.
_BONE_LIMIT = 240
if len(arm.data.bones) > _BONE_LIMIT:
    _skmesh = [m for m in bpy.data.objects if m.type == 'MESH' and m.find_armature() == arm]
    # which bones actually deform the mesh? Only those need to survive with a low index. The rest — IK targets,
    # _end markers, control bones — carry NO skin weight, so removing them NEVER moves a vertex (unlike transferring
    # weights, which corrupted the bind shape: a first attempt flung 350 verts). Remove zero-weight LEAF bones
    # iteratively (a leaf has no children, so deleting it can't break a weighted descendant's hierarchy or its
    # animation); each pass may expose new zero-weight leaves as their children go. Stops when under the cap or when
    # only weighted bones + their zero-weight ANCESTORS remain (those are load-bearing and kept).
    _weighted = set()
    for _m in _skmesh:
        _vgn = {vg.index: vg.name for vg in _m.vertex_groups}
        for _v in _m.data.vertices:
            for _g in _v.groups:
                if _g.weight > 0.001:
                    _weighted.add(_vgn.get(_g.group))
    _removed = 0
    bpy.context.view_layer.objects.active = arm
    while len(arm.data.bones) > _BONE_LIMIT:
        _leaves = [b.name for b in arm.data.bones if len(b.children) == 0 and b.name not in _weighted]
        if not _leaves:
            break                                     # only weighted bones + their load-bearing ancestors remain
        bpy.ops.object.mode_set(mode='EDIT')
        for _n in _leaves:                            # remove every zero-weight leaf this pass (all safe to delete)
            _eb = arm.data.edit_bones.get(_n)
            if _eb:
                arm.data.edit_bones.remove(_eb); _removed += 1
        bpy.ops.object.mode_set(mode='OBJECT')        # a parent may become a new zero-weight leaf next pass
    print("RIGANIM bone-cap: removed %d zero-weight leaf bones -> %d bones (Amplitude 256-bone GPU limit; weighted bones untouched)" % (_removed, len(arm.data.bones)))

# CLIP SLICING (howitzer migration take 2, 2026-07-19): a clip name may carry a FRAME RANGE — "deploy[0..180]" —
# and optionally a SPEED STEP — "deploy[179..0/3]" = every 3rd source frame, so the slice plays 3× faster (BAKE-ONLY
# pacing: a 7.5 s authored fold outlasts a one-tile map move; at /3 it completes in 2.5 s — the runtime deliberately
# plays clips at face value, no speed knobs). The slice is synthesized as a NEW action by sampling the SOURCE
# action's evaluated pose basis per frame INSIDE THIS session (the same pipeline that provably bakes the source
# right) — the Blender import→export round-trip retrofit (add_role_clips.py) altered the clip-vs-rest relationship
# and baked flipped/inverse in-game; never round-trip a converted GLB. start>end = REVERSED (a fold from an
# unfold); single frame = padded to 2 identical frames (a held stance; 0-length clips can be dropped by importers).
_slice_re = __import__("re").compile(r"^(.*)\[(\d+)\.\.(\d+)(?:/(\d+))?\]$")
def resolve_clip(spec, tag):
    m = _slice_re.match(spec.strip())
    if not m:
        a = bpy.data.actions.get(spec)
        if a is None:
            print("RIGANIM ERROR: clip '%s' (%s) not found. Available: %s" % (spec, tag, [a.name for a in bpy.data.actions]))
            sys.exit(1)
        return a
    src_name, f0, f1 = m.group(1), int(m.group(2)), int(m.group(3))
    step = max(1, int(m.group(4))) if m.group(4) else 1
    src = bpy.data.actions.get(src_name)
    if src is None:
        print("RIGANIM ERROR: slice source '%s' (%s) not found. Available: %s" % (src_name, tag, [a.name for a in bpy.data.actions]))
        sys.exit(1)
    frames = list(range(f0, f1 + 1, step)) if f1 >= f0 else list(range(f0, f1 - 1, -step))
    if frames[-1] != f1:
        frames.append(f1)                                # always land exactly on the end frame (the held pose)
    if len(frames) == 1:
        frames = frames * 2                              # held stance: 2 identical frames
    new_name = "%s_%s_%d_%d_%d" % (src_name, tag, f0, f1, step)
    old = bpy.data.actions.get(new_name)
    if old is not None:
        return old                                       # same slice requested by two roles -> share it
    # SCENE-STATE HYGIENE (the byte-gate finding, 2026-07-19): slicing SETS pose values on every bone; any channel
    # the PRIMARY action doesn't key then evaluates to those leftovers in the primary's own export — the sandbox
    # primary baked byte-identical to the proven legacy clip for ~100 frames and then diverged. Save every bone's
    # pose (and rotation mode) and RESTORE it before returning, so role processing is invisible to every other export.
    saved = {pb.name: (pb.location.copy(), pb.rotation_quaternion.copy(), pb.scale.copy(), pb.rotation_mode) for pb in arm.pose.bones}
    assign_action(src)
    for pb in arm.pose.bones:
        pb.rotation_mode = 'QUATERNION'
    snap = {}
    lo, hi = min(frames), max(frames)
    for f in range(lo, hi + 1):                          # snapshot the evaluated basis over the span once
        bpy.context.scene.frame_set(f)
        bpy.context.view_layer.update()
        snap[f] = {pb.name: pb.matrix_basis.decompose() for pb in arm.pose.bones}
    a = bpy.data.actions.new(new_name)
    assign_action(a)
    try: arm.animation_data.action_slot = a.slots.new(id_type='OBJECT', name=arm.name)
    except Exception: pass
    for i, f in enumerate(frames):
        for pb in arm.pose.bones:
            loc, rot, _s = snap[f][pb.name]
            pb.location = loc
            pb.rotation_quaternion = rot
            pb.keyframe_insert('location', frame=i)
            pb.keyframe_insert('rotation_quaternion', frame=i)
    if len(frames) >= 2 and len(set(frames)) == 1:
        # HELD-STANCE EPSILON (2026-07-26, the T-62 idle finding): two IDENTICAL padded frames collapse back
        # to FrameCount 1 in Unity's constant-curve dedupe, and the engine sampler's Clamp(f, 0, FrameCount-2)
        # wraps to frame 4-billion on a 1-frame clip — a constant garbage pose (the scattered-at-idle tank).
        # A ~0.03deg nudge on the first bone at the pad frame keeps the second frame alive; invisible in-game.
        pb0 = arm.pose.bones[0]
        _l0, _r0, _s0 = snap[frames[0]][pb0.name]
        _r2 = _r0.copy(); _r2.w += 3e-4; _r2.normalize()
        pb0.rotation_quaternion = _r2
        pb0.keyframe_insert('rotation_quaternion', frame=len(frames) - 1)
        print("RIGANIM held-stance pad: epsilon nudge on '%s' frame %d (defeats Unity's constant-curve collapse)" % (pb0.name, len(frames) - 1))
    for pb in arm.pose.bones:                            # restore the found pose exactly (mode LAST-set wins, so set it first)
        if pb.name in saved:
            l, q, s, mode = saved[pb.name]
            pb.rotation_mode = mode
            pb.location = l; pb.rotation_quaternion = q; pb.scale = s
    print("RIGANIM sliced '%s' -> '%s' (%d frames%s)" % (spec, new_name, len(frames), ", reversed" if f1 < f0 else ""))
    return a

# release the REST hold from the capture stages — clip resolution/slicing/rest-normalize need real evaluation
if local_node_anim:
    arm.data.pose_position = 'POSE'
    bpy.context.view_layer.update()

# pick an action: the named clip (slice-aware) if given, else the one already on the armature, else the first
act = None
if clip_name:
    act = resolve_clip(clip_name, "primary")
if act is None and arm.animation_data and arm.animation_data.action:
    act = arm.animation_data.action
if act is None and len(bpy.data.actions):
    act = bpy.data.actions[0]
if act is None:
    print("RIGANIM ERROR: no animation action found in the model")
    sys.exit(1)
assign_action(act)
# resolve the state-role clips (slice-aware) BEFORE pruning actions (a role may share the primary clip)
role_acts = {}   # role -> action
for _r, _cn in role_specs:
    role_acts[_r] = resolve_clip(_cn, _r)
keep_names = set([act.name] + [a.name for a in role_acts.values()])
for a in list(bpy.data.actions):
    if a.name not in keep_names:
        try: bpy.data.actions.remove(a)
        except Exception: pass
all_acts = [act] + [a for a in dict.fromkeys(role_acts.values()) if a is not act]   # unique, primary first
print("RIGANIM action '%s'%s" % (act.name, (" + %d state clip(s)" % (len(all_acts) - 1)) if len(all_acts) > 1 else ""))

# all f-curves across the (Blender 5.x slotted) action, with their owning collection so we can remove them
def all_fcurve_owners(action):
    if getattr(action, "fcurves", None) is not None and len(action.fcurves):
        return [(action.fcurves, fc) for fc in list(action.fcurves)]
    out = []
    for layer in getattr(action, "layers", []):
        for strip in layer.strips:
            for cb in getattr(strip, "channelbags", []):
                for fc in list(cb.fcurves):
                    out.append((cb.fcurves, fc))
    return out

# OPTIONAL: keep animation only on bones matching a prefix (strip camera pans / root-bob that cause wobble).
# Applied to EVERY exported clip (primary + state roles) so no role can smuggle back a stripped bone.
if prefixes:
    def bone_of(dp):
        if 'pose.bones[' not in dp: return None
        return dp.split('["', 1)[1].split('"]', 1)[0]
    for _fa in all_acts:
        owners = all_fcurve_owners(_fa)   # snapshot: safe to remove from the owning collections while iterating this list
        avail = sorted({bone_of(fc.data_path) for _, fc in owners if bone_of(fc.data_path) is not None})
        kept = rem = 0
        for coll, fc in owners:
            b = bone_of(fc.data_path)
            if b is not None and any(b.startswith(p) for p in prefixes):
                kept += 1
            else:
                coll.remove(fc); rem += 1
        print("RIGANIM bone-filter %s on '%s': kept %d fcurves, removed %d" % (prefixes, _fa.name, kept, rem))
        # T6: if the prefix matched NOTHING, every fcurve was stripped -> a frozen 1-frame clip would bake and ship
        # with exit 0 (silent). Hard-fail instead, listing the animated bones so the prefix can be corrected.
        if kept == 0:
            print("RIGANIM ERROR: bone-filter %s matched no animated bone in '%s' — every fcurve was stripped, the clip would be frozen. Animated bones: %s" % (prefixes, _fa.name, avail))
            sys.exit(1)

# FOLD FRAME-0 BONE LOCATIONS INTO THE REST POSE — auto-rigged models (the Combine soldier) often park a bone's REST
# somewhere else and hold it in place with a constant corrective location key in every clip. Amplitude can't play
# location keys (see the strip below), so without this fold the correction is lost and the part sits rigidly displaced
# (the soldier's head). Fix: evaluate each location-keyed bone at the clip's first frame, convert that offset to
# armature space via the bone's rest orientation, and move the bone's REST (head+tail, whole subtree — pose location
# shifts descendants too, and offsets from nested keyed bones compose additively since they're pure translations).
# After the fold, frame-0 pose == rest, and the rotation-only clip plays around the corrected pivots.
_rest_applied = False   # set once armature_apply rewrites the rest — after that, a failure must abort the bake
try:
    _loc0 = {}
    for _coll, _fc in all_fcurve_owners(act):
        _dp = _fc.data_path
        if _dp.startswith("pose.bones") and _dp.endswith(".location"):
            _b = _dp.split('["', 1)[1].split('"]', 1)[0]
            _loc0.setdefault(_b, [0.0, 0.0, 0.0])[_fc.array_index] = _fc.evaluate(act.frame_range[0])
    if _loc0 and convert_rig:
        # REST NORMALIZATION + VISUAL REBAKE — CONVERSION PATH ONLY (gating decision 2026-07-19). This block rewrites
        # the rest pose and re-derives the whole clip: exactly the manipulation the legacy contract promises NOT to
        # do, and it used to run on location-key PRESENCE alone — so a legacy re-bake of any deploy_convert output
        # (nla.bake writes location keys on every bone) silently routed through it, and after the hard-fail hardening
        # a legacy model with location keys + shape keys ABORTED instead of baking. Legacy rigs have a sane rest by
        # definition (that's what makes them legacy); the fold at a sane rest is a near-no-op they don't need. The
        # location-STRIP below stays on BOTH paths deliberately: every verified legacy bake (drone, howitzer) went
        # through it, and un-stripping could re-introduce the drone's unscaled-translation wobble.
        # Auto-rigs can ship a SCRAMBLED rest pose that the clip's location keys
        # ASSEMBLE into the actual body every frame (the Combine soldier: frame-0 posed positions sit up to 91 units
        # from their rests on a 73-unit rig — the 129 location curves are structural, not decorative). Amplitude
        # can't play location keys, so the fix is to make the clip's FIRST VISUAL POSE the new rest and re-derive the
        # animation against it:
        #   1. at frame 0: apply the armature modifier on the meshes (bakes the assembled body as the new bind shape)
        #   2. Apply Pose As Rest on the armature (the assembled pose becomes the rest pose)
        #   3. re-add the armature modifiers (re-bind the assembled mesh to the assembled rest)
        #   4. re-bake the action with VISUAL KEYING (curves re-derived relative to the new rest — translations
        #      collapse to ~0, rotations stay true), then the location-strip below removes the residue.
        _fs0 = int(act.frame_range[0]); _fe0 = int(act.frame_range[1])
        # SNAPSHOT the true visual pose of every bone on every frame of EVERY exported clip FIRST (original rig +
        # original actions) — everything after this destroys the old reference frame. All clips share ONE canonical
        # rest (the PRIMARY clip's frame 0): a per-clip rest would displace every non-primary clip on the shared
        # skeleton (Phase 2, 2026-07-19).
        _snaps = {}   # action -> {frame -> {bone -> world matrix}}
        for _sa in all_acts:
            assign_action(_sa)
            _sfs = int(_sa.frame_range[0]); _sfe = int(_sa.frame_range[1])
            _snaps[_sa] = {}
            for _f in range(_sfs, _sfe + 1):
                bpy.context.scene.frame_set(_f)
                bpy.context.view_layer.update()
                _snaps[_sa][_f] = {pb.name: pb.matrix.copy() for pb in arm.pose.bones}
        _snap = _snaps[act]   # the primary clip's snapshots (rest source + residual check)
        assign_action(act)
        bpy.context.scene.frame_set(_fs0)
        bpy.context.view_layer.update()
        _rebind = []
        for _mo in [o for o in bpy.context.scene.objects if o.type == 'MESH']:
            for _md in [m for m in _mo.modifiers if m.type == 'ARMATURE']:
                bpy.ops.object.select_all(action='DESELECT')
                _mo.select_set(True); bpy.context.view_layer.objects.active = _mo
                # modifier_apply refuses multi-user mesh data ("Modifiers cannot be applied to multi-user data") —
                # instanced duplicates (wheels, blades) hit this. Single-user-ize first, same as prep_model.py does.
                if _mo.data.users > 1:
                    _mo.data = _mo.data.copy()
                try:
                    bpy.ops.object.modifier_apply(modifier=_md.name)
                    _rebind.append(_mo.name)
                except Exception as _e2:
                    # HARD-FAIL: continuing would let armature_apply below rewrite the rest pose underneath this
                    # mesh's still-old bind — a permanently deformed export shipped with exit 0 (review 2026-07-19).
                    print("RIGANIM ERROR: could not apply armature modifier on %s (%s) — aborting: a partial rest-fold would silently mis-bake the model" % (_mo.name, _e2))
                    sys.exit(1)
        bpy.ops.object.select_all(action='DESELECT')
        arm.select_set(True); bpy.context.view_layer.objects.active = arm
        bpy.ops.object.mode_set(mode='POSE')
        bpy.ops.pose.select_all(action='SELECT')
        bpy.ops.pose.armature_apply(selected=False)          # frame-0 visual pose -> new rest
        _rest_applied = True                                 # PAST THE POINT OF NO RETURN: the rest is rewritten
        bpy.ops.object.mode_set(mode='OBJECT')
        for _mn in _rebind:
            _mo = bpy.context.scene.objects.get(_mn)
            if _mo is not None:
                _nm = _mo.modifiers.new("Armature", 'ARMATURE'); _nm.object = arm
        # MANUAL visual rebake from the pre-apply snapshots (an nla.bake at this point would sample the old keys
        # double-applied on the new rest). New local basis per frame, pure matrix math:
        #   local_f(bone)  = parentWorld_f^-1 @ world_f          (armature-space snapshots)
        #   basis_f(bone)  = newRestLocal^-1 @ local_f           (Blender: poseLocal = restLocal @ basis)
        # ROTATION of basis_f is always written. TRANSLATION (2026-07-25, the caterpillar unlock): the engine
        # PLAYS RotationTranslation curves (the vanilla tank's tread shuttle bones prove it; the old "rotation-
        # only" law described THIS loop's keying, not an engine wall) — so `location` is ALSO keyed, but ONLY for
        # bones whose basis translation VARIES within the clip (> 1e-4). Constant offsets stay dropped exactly as
        # before, so every existing rotation-only model rebakes identically. The primary's frame-0 basis ==
        # identity by construction, so the rest IS the primary's first frame. EVERY clip (primary + state roles)
        # is rebaked against the SAME rest here — that shared reference is what lets all role ClipCollections play
        # on one baked skeleton.
        _parent_of = {b.name: (b.parent.name if b.parent else None) for b in arm.data.bones}
        _rest_local = {}
        for _b3 in arm.data.bones:
            if _b3.parent: _rest_local[_b3.name] = _b3.parent.matrix_local.inverted() @ _b3.matrix_local
            else: _rest_local[_b3.name] = _b3.matrix_local.copy()
        for pb in arm.pose.bones:
            pb.rotation_mode = 'QUATERNION'
        _rebaked = {}   # old action -> rebaked action
        for _oa in all_acts:
            _na = bpy.data.actions.new(_oa.name + "_rebaked")
            arm.animation_data.action = _na
            try: arm.animation_data.action_slot = _na.slots.new(id_type='OBJECT', name=arm.name)
            except Exception: pass
            _frames = sorted(_snaps[_oa].keys())
            # SINGLE-FRAME clip (a held stance like CombatIdle1, range 0..0): also key the same pose at frame+1 —
            # a zero-length animation can be dropped whole by Unity's FBX importer, which would fail the role's
            # ClipCollection bake downstream. Two identical frames = a valid (visually static) clip everywhere.
            if len(_frames) == 1:
                _frames = [_frames[0], _frames[0] + 1]
                _snaps[_oa][_frames[1]] = _snaps[_oa][_frames[0]]
                print("RIGANIM single-frame clip '%s' padded to 2 identical frames (stance)" % _oa.name)
            # pre-pass: which bones genuinely translate within THIS clip? (varying basis translation, not a
            # constant offset — the gate that keeps all legacy models byte-identical)
            _trans_bones = set()
            for pb in arm.pose.bones:
                _pn = _parent_of[pb.name]
                _lo = None; _hi = None
                for _f in _frames:
                    _world = _snaps[_oa][_f]
                    _localf = (_world[_pn].inverted() @ _world[pb.name]) if _pn else _world[pb.name]
                    _t = (_rest_local[pb.name].inverted() @ _localf).to_translation()
                    if _lo is None: _lo = _t.copy(); _hi = _t.copy()
                    else:
                        _lo = Vector((min(_lo.x, _t.x), min(_lo.y, _t.y), min(_lo.z, _t.z)))
                        _hi = Vector((max(_hi.x, _t.x), max(_hi.y, _t.y), max(_hi.z, _t.z)))
                if _lo is not None and (_hi - _lo).length > 1e-4:
                    _trans_bones.add(pb.name)
            if _trans_bones:
                print("RIGANIM TRANSLATION-animated bone(s) in '%s': %s%s" % (_oa.name, sorted(_trans_bones),
                      " (location keys KEPT — keepTranslations)" if keep_translations else " (location keys stripped below — set keepTranslations to keep)"))
                for _tb in _trans_bones:
                    _KEEP_LOC_PATHS.add('pose.bones["%s"].location' % _tb)
            for _f in _frames:
                _world = _snaps[_oa][_f]
                for pb in arm.pose.bones:
                    _pn = _parent_of[pb.name]
                    _localf = (_world[_pn].inverted() @ _world[pb.name]) if _pn else _world[pb.name]
                    _basis = _rest_local[pb.name].inverted() @ _localf
                    pb.rotation_quaternion = _basis.to_quaternion()
                    pb.keyframe_insert("rotation_quaternion", frame=_f)
                    if pb.name in _trans_bones:
                        pb.location = _basis.to_translation()
                        pb.keyframe_insert("location", frame=_f)
            _rebaked[_oa] = _na
        act = _rebaked[all_acts[0]]
        role_acts = {r: _rebaked[a] for r, a in role_acts.items()}
        all_acts = [act] + [a for a in dict.fromkeys(role_acts.values()) if a is not act]
        assign_action(act)
        # VERIFY: at frame 0 the evaluated PRIMARY pose must coincide with the rest
        bpy.context.scene.frame_set(_fs0)
        bpy.context.view_layer.update()
        _worst = 0.0; _scaleref = 0.0
        for pb in arm.pose.bones:
            _d = (pb.matrix.translation - arm.data.bones[pb.name].matrix_local.translation).length
            if _d > _worst: _worst = _d
            _rl = arm.data.bones[pb.name].matrix_local.translation.length
            if _rl > _scaleref: _scaleref = _rl
        print("RIGANIM rest-normalized + rebaked %d clip(s) x %d bones (%d meshes re-bound); primary frame-0 residual = %.6f (should be ~0)" % (len(all_acts), len(arm.pose.bones), len(_rebind), _worst))
        # ASSERT convergence (review 2026-08-16): the residual was computed + printed but never checked. A fold that
        # completes yet leaves frame-0 several units off rest = a rigidly displaced bone that ships with exit 0 (the
        # "head off shoulders" class). Fail loudly on NaN or a residual > 25% of the rig's own bone scale (generous —
        # a converged fold is ~1e-4; a real failure is model-scale, so this never trips a valid bake).
        _thr = max(0.02, 0.25 * _scaleref)
        if _worst != _worst or _worst > _thr:
            print("RIGANIM ERROR: rest-fold did NOT converge — frame-0 residual %.6f exceeds %.6f (rig bone-scale %.4f); a rigidly displaced bone would ship (the 'head off shoulders' bug). Aborting." % (_worst, _thr, _scaleref))
            sys.exit(1)
except Exception as _e:
    try: bpy.ops.object.mode_set(mode='OBJECT')
    except Exception: pass
    # Failed BEFORE the rest rewrite: the rig is untouched, continuing with a plain location-strip is safe.
    # Failed AFTER (rest already rewritten, clip rebake incomplete): the export would pair the NEW rest with the OLD
    # (or a partial) clip — a silent mis-bake with exit 0. Fail the bake loudly instead (review 2026-07-19).
    if _rest_applied:
        print("RIGANIM ERROR: rest-fold failed AFTER the rest pose was rewritten (%s) — aborting: exporting now would ship a half-normalized rig" % _e)
        sys.exit(1)
    print("RIGANIM WARN: rest-fold failed (%s) — location keys will just be stripped" % _e)

# STRIP BONE-TRANSLATION CURVES — Amplitude clips are effectively ROTATION-ONLY: its clip bake reads translation keys
# at the FBX's NATIVE scale, bypassing the importer's unit conversion, so on a Fix-100x rig a 2 cm neck bob becomes
# ~2 world units and the head rips off in-game ("the movement gets exaggerated") while Unity's own preview plays the
# same FBX fine. Rotations are scale-free; bone REST offsets come from the (properly scaled) skeleton, so a
# rotation-driven rig looks identical without these curves. (This generalizes the drone's old wobble fix, which
# stripped to rotation-only 'prop' curves by hand.) DELIBERATELY UNGATED — runs on BOTH paths: every verified legacy
# bake went through it, and Amplitude can't play the keys anyway (the 2026-07-19 gating decision moved only the
# destructive rest-fold above behind the convert flag).
# LEGACY-PATH keepTranslations (the howitzer's real kickback): the conversion rebake fills _KEEP_LOC_PATHS
# itself; on the legacy path (deploy_convert output etc.) scan the final clips directly — any pose-bone
# location channel that VARIES (>1e-4) is a genuine authored slide (deploy_convert's Phase B keys the tube's
# true recoil translation; only this strip ever removed it).
if keep_translations and not convert_rig:
    # SCOPE: translations are kept in the ATTACK-role clip (the recoil) and — since 2026-07-26, the T-62
    # finding — the MOVE-role clip (a driving vehicle's track links crawl by TRANSLATION; deploy_convert now
    # bakes them hull-relative/in-place so they are safe to keep). Deploy/stance clips stay rotation-only:
    # keeping their translations displaced the assembly (the hovering-gun incident) — deployed-pose basis
    # offsets that rotations already cover got double-rendered.
    # per-role RANGE FLOORS (2026-07-26, the T-62 wheel-wiggle finding): the MOVE role keeps only LARGE slides
    # (track links crawl ~6 units around the loop) and drops small ones (suspension bob 0.02-0.04 units — the
    # source tank rides bumpy terrain; replayed on flat game ground the wheels wiggle in the air). The ATTACK
    # role keeps its historical 1e-4 floor: the m114's recoil slide is only ~0.1 raw units.
    _role_floor = {}
    for _r, _cn in role_specs:
        if _r in ('attack', 'move') and _cn.strip():
            _bn = _cn.split('[')[0].strip()
            _fl = 1e-4 if _r == 'attack' else 0.5
            _role_floor[_bn] = min(_role_floor.get(_bn, 1e9), _fl)
    _keep_acts = [a for a in all_acts if a.name.split('_rebaked')[0] in _role_floor or a.name in _role_floor]
    if not _keep_acts:
        print("RIGANIM legacy keepTranslations: no attack/move-role clip found — nothing kept (translations live in the fire cycle / drive cycle)")
    _poison = set()   # any channel with NaN/absurd values poisons the whole bone path — decomposition garbage
    for _sa in _keep_acts:
        for coll, fc in all_fcurve_owners(_sa):
            _dp = fc.data_path
            if not (_dp.startswith("pose.bones") and _dp.endswith(".location")) or len(fc.keyframe_points) == 0:
                continue
            _vals = [kp.co[1] for kp in fc.keyframe_points]
            # SANITY GATE: matrix-decomposition against near-zero-scale parents leaves NaN / astronomically
            # large location keys on some bones (the documented "latent mid-deploy contamination") — keeping
            # those explodes mesh bounds (1e28) and NaNs the import. Only finite, model-scale slides pass.
            if any(v != v or abs(v) > 1e5 for v in _vals):
                _poison.add(_dp); continue
            _floor = _role_floor.get(_sa.name.split('_rebaked')[0], _role_floor.get(_sa.name, 1e-4))
            if _floor < (max(_vals) - min(_vals)) < 1e4:
                _KEEP_LOC_PATHS.add(_dp)
    _KEEP_LOC_PATHS -= _poison
    if _poison:
        print("RIGANIM legacy keepTranslations: %d path(s) REJECTED as decomposition garbage (NaN/huge): %s"
              % (len(_poison), sorted({p.split('"')[1] for p in _poison if '"' in p})))
    if _KEEP_LOC_PATHS:
        print("RIGANIM legacy keepTranslations: %d varying location path(s) kept: %s"
              % (len(_KEEP_LOC_PATHS), sorted({p.split('"')[1] for p in _KEEP_LOC_PATHS if '"' in p})))

_locs = 0; _kept = 0
for _sa in all_acts:
    for coll, fc in all_fcurve_owners(_sa):
        if fc.data_path.startswith("pose.bones") and fc.data_path.endswith(".location"):
            if keep_translations and fc.data_path in _KEEP_LOC_PATHS:
                _kept += 1; continue   # opt-in: genuinely translation-animated bone (caterpillar shuttle etc.)
            coll.remove(fc); _locs += 1
if _locs or _kept:
    print("RIGANIM stripped %d bone-LOCATION fcurves across %d clip(s)%s" % (_locs, len(all_acts),
          (", KEPT %d translation curve(s) (keepTranslations)" % _kept) if _kept else " (rotation-only; translations bake unscaled)"))

# LEGACY SANDWICH COMPENSATION (2026-07-25, the invisible-kickback finding): the legacy FBX ships the m->cm
# x100 root sandwich; Unity folds 0.01 into every bindpose, and Amplitude's clip import carries that 0.01 into
# TRANSLATION curves (rotations are scale-free — that's why only slides vanish). A kept slide therefore renders
# at 1/100 amplitude (the howitzer's ~10-unit slam baked to a 0.0115 bbox). Pre-amplify the kept location keys
# x100 so the baked curve lands at render scale. Conversion-path exports (global_scale 0.01, net scale 1) need
# no compensation — the translation-test cube rendered at correct amplitude there.
if keep_translations and not convert_rig and not clean_units_input and _KEEP_LOC_PATHS:
    # DELTA-REBASE + AMPLIFY: each kept curve is rebased to ZERO at its first frame before the x100 — the clip
    # carries pure MOTION (the slam's full travel), never constant pose offsets. Tiny basis residues (a 3.5 cm
    # rest mismatch) otherwise amplify into multi-unit displacements (the reared-up-gun incident): pose HOLDING
    # stays rotation-only exactly like the proven baseline, translation adds only the kick delta on top.
    _scaled = 0
    for _sa in _keep_acts:
        for coll, fc in all_fcurve_owners(_sa):
            if fc.data_path in _KEEP_LOC_PATHS and fc.data_path.endswith(".location"):
                _kps = sorted(fc.keyframe_points, key=lambda k: k.co[0])
                if not _kps:
                    continue
                _v0 = _kps[0].co[1]
                for kp in fc.keyframe_points:
                    kp.co[1] = (kp.co[1] - _v0) * 100.0
                    kp.handle_left[1] = (kp.handle_left[1] - _v0) * 100.0
                    kp.handle_right[1] = (kp.handle_right[1] - _v0) * 100.0
                _scaled += 1
    print("RIGANIM legacy keepTranslations: %d kept curve(s) delta-rebased + amplified x100 (attack clip only)" % _scaled)

# clamp scene frame range to the action's real range (else bake_anim pads a frozen tail -> ~1s stall per loop)
fs, fe = [int(round(v)) for v in act.frame_range]
bpy.context.scene.frame_start = fs
bpy.context.scene.frame_end = fe
print("RIGANIM frame range %d..%d" % (fs, fe))

# export the base-colour image as the albedo PNG (for the Factory atlas). Trace the Principled BSDF's Base Color input
# back to the actual base-colour texture — NOT just the first TEX_IMAGE node. Node order is creation order, so in a PBR
# material the first image node can be a normal / roughness / metallic map, which would hand the atlas a purple normal
# map as "albedo" (garbled skin, no error). Fall back to any image node only when there's no Principled / it's unlinked.
def base_color_image(mat):
    if not (mat and mat.node_tree):   # node_tree is non-None exactly when the material has nodes (use_nodes removed in Blender 6.0)
        return None
    nt = mat.node_tree
    for n in nt.nodes:
        if n.type == 'BSDF_PRINCIPLED':
            inp = n.inputs.get('Base Color')
            if inp and inp.is_linked:
                seen, stack = set(), [inp.links[0].from_node]   # walk upstream to the nearest image (through a mix/gamma node etc.)
                while stack:
                    node = stack.pop()
                    if node is None or node in seen: continue
                    seen.add(node)
                    if node.type == 'TEX_IMAGE' and node.image:
                        return node.image
                    for s in node.inputs:
                        if s.is_linked: stack.append(s.links[0].from_node)
            break
    for n in nt.nodes:                # fallback: no Principled, or Base Color unlinked -> any image (better than nothing)
        if n.type == 'TEX_IMAGE' and n.image:
            return n.image
    return None

if albedo_out:
    try:
        img = None
        for o in bpy.context.scene.objects:
            if o.type != 'MESH' or not o.data.materials: continue
            img = base_color_image(o.data.materials[0])
            if img: break
        if img:
            img.filepath_raw = albedo_out
            img.file_format = 'PNG'
            img.save()
            print("RIGANIM albedo ->", albedo_out, "(%s)" % img.name)
        else:
            print("RIGANIM no albedo image found (model may be untextured)")
    except Exception as e:
        print("RIGANIM albedo export warn:", e)

# join to 1 mesh + 1 material + decimate (KEEP the armature + skin weights)
# (material-less reference junk — e.g. a stray Icosphere — was already culled up top, right after import)
meshes = [o for o in bpy.context.scene.objects if o.type == 'MESH']
if not meshes:
    print("RIGANIM ERROR: no mesh to export"); sys.exit(1)
bpy.ops.object.select_all(action='DESELECT')
for o in meshes: o.select_set(True)
# join() keeps ONLY the active object's modifiers, so make the active one a mesh that HAS an armature modifier —
# scene-order meshes[0] can be a bone-parented prop with no armature modifier, and joining onto it would drop the skin
# binding and export the whole model rigid/frozen. Prefer a skinned mesh here; we also re-guarantee the modifier below.
active = next((o for o in meshes if any(md.type == 'ARMATURE' for md in o.modifiers)), meshes[0])
bpy.context.view_layer.objects.active = active
if len(meshes) > 1:
    bpy.ops.object.join()
joined = bpy.context.view_layer.objects.active
me = joined.data
# WELD FIRST. Many exports (this da-Vinci ribauldequin included) ship massively duplicated vertices —
# coincident but unmerged — so the mesh is disconnected "face soup": 76k verts that weld down to 19k
# (75% were dupes). Two problems fall out of that: (1) the vertex count looks huge and triggers brutal
# decimation it never actually needed, and (2) quadric COLLAPSE needs connected edge loops, so on soup
# it caves smooth cylinders (a cannon's barrels) into slivers while spoked parts survive. Merging by
# distance reconnects the surface: decimation (if still needed) is clean, and the honest vert count is a
# fraction of the raw one. Blender stores UVs per face-corner, so welding VERTICES preserves the UV seams.
# WELD only a SINGLE-material AND SINGLE-BONE mesh. Welding merges coincident verts, which corrupts anything that
# relies on them staying split: (a) MULTI-MATERIAL seams (the howitzer: 6 mats) and (b) MULTI-BONE skinning seams
# (the ReconDrone: a spinning 'prop' bone + body) — a merged vertex straddling two bones gives Amplitude's skeleton
# importer (MeshCollection.ImportMeshes) a bad index -> IndexOutOfRangeException at bake. len(vertex_groups) is the
# skinned-bone count. Only a truly simple 1-material / 1-bone mesh (a fragmented static-style rig) is safe to weld.
_nvg = len(joined.vertex_groups)
if len(me.materials) <= 1 and _nvg <= 1:
    _wb = bmesh.new(); _wb.from_mesh(me); _n0 = len(_wb.verts)
    bmesh.ops.remove_doubles(_wb, verts=_wb.verts, dist=1e-4)
    _n1 = len(_wb.verts); _wb.to_mesh(me); _wb.free()
    print("RIGANIM weld: %d -> %d verts (%.0f%% duplicates removed)" % (_n0, _n1, 100.0 * (1 - _n1 / max(_n0, 1))))
else:
    print("RIGANIM weld: SKIPPED (%d materials, %d bones -> preserve seams)" % (len(me.materials), _nvg))
if not keep_materials:                       # SINGLE-material path: collapse to one slot (the old default)
    while len(me.materials) > 1:
        me.materials.pop(index=len(me.materials) - 1)
    for p in me.polygons:
        p.material_index = 0
else:
    print("RIGANIM keeping %d material slots (multi-material)" % len(me.materials))
total = sum(len(p.vertices) - 2 for p in me.polygons)
# target <= 0 = decimation OFF (the Factory's "0 = off" promise — decimation shreds link-cell tread meshes by
# collapsing verts ACROSS cell boundaries, blending weights between distant link bones = spike ribbons in-game)
if target <= 0 or total <= target:
    print("RIGANIM decimate SKIPPED (%d tris, target %s)" % (total, "OFF" if target <= 0 else target))
else:
    ratio = min(1.0, max(0.02, target / max(1, total)))
    mdec = joined.modifiers.new("dec", 'DECIMATE')
    mdec.decimate_type = 'COLLAPSE'; mdec.ratio = ratio; mdec.use_collapse_triangulate = True
    bpy.ops.object.select_all(action='DESELECT'); joined.select_set(True); bpy.context.view_layer.objects.active = joined
    bpy.ops.object.modifier_apply(modifier=mdec.name)
    print("RIGANIM decimate %d -> %d tris (ratio %.3f)" % (total, sum(len(p.vertices) - 2 for p in me.polygons), ratio))

# GUARANTEE the skin binding: whichever object won the join, the joined mesh keeps every source mesh's vertex groups
# (weights) regardless — so if the join dropped the armature modifier, re-adding one bound to `arm` fully restores
# skinning. Without this a model whose first mesh was a bone-parented prop exports rigid/frozen (T4).
if not any(md.type == 'ARMATURE' for md in joined.modifiers):
    _am = joined.modifiers.new("Armature", 'ARMATURE'); _am.object = arm
    print("RIGANIM re-bound armature modifier (join had dropped it)")

# BAKE-TIME RIG ROTATION: rotate every parent-less object (the armature root; children follow) in WORLD space, then
# APPLY the transform INTO THE DATA (vertices + bone rest matrices). Object-level rotation alone is NOT enough: a
# skinned mesh keeps its vertices in mesh/bind space, and both the Factory preview (raw mesh) and Amplitude's
# skeleton bake (SetPrefab/ImportMeshes) read the DATA, discarding object transforms — an object-only rotation
# looked applied in the log yet changed nothing in-game. Registry mapping: x -> Blender world X (pitch — stands a
# lying-on-its-back rig upright), y -> Blender world Z (heading), z -> Blender world Y (roll). Applying the SAME
# world rotation to armature rest bones AND mesh vertices keeps the skin binding aligned; bone-local animation
# curves are relative to the (now-rotated) rest pose, so the clip plays identically.
# COLLAPSE NO-OP ROOT BONES (depth reduction) — the runtime composes bone chains with a bounded depth (CPU cap 15,
# the GPU pass suspected lower): a raw rig's pass-through roots (the soldier's `_rootJoint`: no animation channels,
# no vertex weights, single child) burn depth for nothing and push the head/hand chains over the working range.
# Deleting them re-roots their child; Amplitude adds the armature object as one more level on top regardless.
if convert_rig:
    _animated_bones = set()
    for _c2, _fc2 in all_fcurve_owners(act):
        if _fc2.data_path.startswith('pose.bones['):
            _animated_bones.add(_fc2.data_path.split('["', 1)[1].split('"]', 1)[0])
    _weighted = set()   # only groups with REAL weights protect a bone (glTF exports empty groups for every joint)
    for _o2 in bpy.context.scene.objects:
        if _o2.type == 'MESH':
            _gnames = [g.name for g in _o2.vertex_groups]
            for _v in _o2.data.vertices:
                for _ge in _v.groups:
                    if _ge.weight > 1e-6:
                        _weighted.add(_gnames[_ge.group])
    bpy.context.view_layer.objects.active = arm
    bpy.ops.object.mode_set(mode='EDIT')
    _removed = 0
    while True:
        _victim = None
        for _b2 in [b for b in arm.data.edit_bones if b.parent is None]:
            if len(_b2.children) == 1 and _b2.name not in _animated_bones and _b2.name not in _weighted:
                _victim = _b2; break
        if _victim is None:
            break
        arm.data.edit_bones.remove(_victim); _removed += 1
    bpy.ops.object.mode_set(mode='OBJECT')
    if _removed:
        print("RIGANIM collapsed %d no-op root bone(s) — every chain is now %d level(s) shallower" % (_removed, _removed))

# TOPOLOGICAL BONE RENAME — Amplitude's Skeleton bake SORTS BONES ALPHABETICALLY (decompiled BuildBoneEntry.Compare:
# roots first, then string.Compare on the name) and the runtime composes them in array order, ASSUMING PARENTS COME
# BEFORE CHILDREN. Amplitude's own rigs satisfy that by naming convention; a raw rig like the ValveBiped does NOT
# ('..._014' sorts before '..._02'), so the head/neck and forearm chains read their parents' garbage transforms and
# hang displaced in-game. Prefixing every bone with its breadth-first index makes alphabetical == topological.
# Blender auto-syncs vertex groups + THE ASSIGNED action's fcurve paths on rename; the bone-filter above already ran
# on the ORIGINAL names. Gated to the CONVERSION path: legacy stays the byte-identical pipeline.
if convert_rig:
    _order = []
    def _walkb(b):
        _order.append(b.name)
        for c in sorted(b.children, key=lambda x: x.name):
            _walkb(c)
    for _root in [b for b in arm.data.bones if b.parent is None]:
        _walkb(_root)
    _bprefix = "A" if socket_specs else "b"   # sockets keep DONOR names ('C'anon...) — 'A###_' sorts every real bone first
    for _i, _bname in enumerate(_order):
        arm.data.bones[_bname].name = "%s%03d_%s" % (_bprefix, _i, _bname)
    print("RIGANIM bones renamed with topological prefixes (%d bones, prefix %s###_) — parents now sort before children" % (len(_order), _bprefix))
    # THE FROZEN-RUNNER FIX (2026-07-19): Blender's rename syncs fcurve data_paths ONLY for the action ASSIGNED at
    # rename time — every DORMANT state-role action kept its OLD bone names, its curves then targeted nonexistent
    # bones, evaluated to NOTHING at export, and the role FBX baked an 18-frame CONSTANT clip (in-game: the soldier
    # frozen mid-stride while moving; pose-data byte analysis showed all 63 curves constant). Patch every kept
    # action's paths explicitly — idempotent: only OLD names are in the map, already-synced paths pass through.
    _newname = {n: "%s%03d_%s" % (_bprefix, i, n) for i, n in enumerate(_order)}
    _patched = 0
    for _pa in all_acts:
        for _coll2, _fc2 in all_fcurve_owners(_pa):
            _dp2 = _fc2.data_path
            if 'pose.bones["' in _dp2:
                _bn2 = _dp2.split('["', 1)[1].split('"]', 1)[0]
                if _bn2 in _newname:
                    _fc2.data_path = _dp2.replace('pose.bones["%s"]' % _bn2, 'pose.bones["%s"]' % _newname[_bn2])
                    _patched += 1
    if _patched:
        print("RIGANIM patched %d dormant fcurve path(s) onto the renamed bones (state-role clips)" % _patched)

# DONOR SOCKET CREATION (after the rename so parent substrings match final names; before the fold so sockets share
# the same rotation/scale treatment as their parents). Zero-weight leaves: no vertex moves, no animation, pure
# anchors for the donor's GetBoneTRS lookups. Orientation inherits the parent bone (tail direction + roll) — the
# donor's Forward/Up launch vectors are expressed in this frame; if fire DIRECTION comes out wrong, orientation
# control is the next knob to add here.
if socket_specs and arm is not None:
    bpy.context.view_layer.objects.active = arm
    bpy.ops.object.mode_set(mode='EDIT')
    from mathutils import Vector as _SockV
    _made = 0
    for _dn, _ps, _off in socket_specs:
        _parent = None
        for _eb in arm.data.edit_bones:
            if _ps.lower() in _eb.name.lower():
                _parent = _eb; break
        if _parent is None:
            _names = [b.name for b in arm.data.edit_bones]
            bpy.ops.object.mode_set(mode='OBJECT')
            print("RIGANIM ERROR: socket '%s': parent substring '%s' matches no bone. Bones: %s" % (_dn, _ps, _names[:40]))
            sys.exit(1)
        if not (_dn > _parent.name):
            bpy.ops.object.mode_set(mode='OBJECT')
            print("RIGANIM ERROR: socket '%s' would sort BEFORE its parent '%s' — Amplitude needs parents-first alphabetically; this donor-name/parent pair cannot satisfy it" % (_dn, _parent.name))
            sys.exit(1)
        _sb = arm.data.edit_bones.get(_dn) or arm.data.edit_bones.new(_dn)
        _dirv = _parent.tail - _parent.head
        _dirv = _dirv.normalized() * max(0.02, _dirv.length * 0.25) if _dirv.length > 1e-6 else _SockV((0.0, 0.05, 0.0))
        _sb.head = _parent.head + _SockV(_off)
        _sb.tail = _sb.head + _dirv
        _sb.roll = _parent.roll
        _sb.parent = _parent
        _sb.use_deform = False
        _made += 1
        print("RIGANIM socket '%s' -> parent '%s' head=(%.3f, %.3f, %.3f)" % (_dn, _parent.name, _sb.head.x, _sb.head.y, _sb.head.z))
    bpy.ops.object.mode_set(mode='OBJECT')
    print("RIGANIM donor sockets: %d created" % _made)

# RIG ROTATION + TRANSFORM FOLD — CONVERSION path only (legacy = the EXACT old pipeline, byte-for-byte: no object
# fiddling, no fold — models that were correct before stay correct; the fold is world-preserving for Unity's mesh
# import but NOT for Amplitude's skeleton bake, so folding unconditionally flipped the previously-good howitzer
# upside-down in-game). On the conversion path: rotate the parent-less objects in world space (identity when rotation
# is 0,0,0 — the fold/strip still run, they are what makes the clean-unit export sound), strip OBJECT-level animation
# fcurves (a glTF often keys the armature NODE itself — they block transform_apply and re-assert the old orientation
# each frame), then transform_apply INTO THE DATA (vertices + bone rests, identity nodes) — object-level rotation
# alone is dropped downstream (proven in-game on the Combine soldier).
# ALSO on deploy-converted sources (2026-07-26): they are identity-node/clean-unit by construction — exactly
# the precondition the fold wants — and the Rotation knob was otherwise silently ignored on the deploy path
# (bone basis keys are rest-relative, so rotating rests + verts together keeps the delta-form clips coherent).
if convert_rig or clean_units_input:
    rot = (Matrix.Rotation(radians(rig_rot[1]), 4, 'Z') @
           Matrix.Rotation(radians(rig_rot[0]), 4, 'X') @
           Matrix.Rotation(radians(rig_rot[2]), 4, 'Y'))
    for o in bpy.context.scene.objects:
        if o.parent is None:
            o.matrix_world = rot @ o.matrix_world
    print("RIGANIM rig rotation: x=%s y=%s z=%s (deg, registry semantics)" % tuple(rig_rot))
    if arm.animation_data and arm.animation_data.action:
        _rm = 0
        for coll, fc in all_fcurve_owners(arm.animation_data.action):
            if not fc.data_path.startswith("pose.bones"):
                coll.remove(fc); _rm += 1
        if _rm: print("RIGANIM stripped %d OBJECT-level fcurves (non-bone) so the rig orientation can bake" % _rm)
    bpy.ops.object.select_all(action='SELECT')
    bpy.context.view_layer.objects.active = arm
    try:
        # ROTATION **AND SCALE** into the data. Decompiled Amplitude bake (ClipEntry.Reimport / Skeleton.Reimport):
        # the clip is sampled from SCENE NODE transforms but the skeleton's rest comes from MESH BINDPOSES, and the
        # pose TRS holds a single UNIFORM scale — FBX unit-scale compensation living on nodes desyncs the two sources
        # (constant rest deltas -> a rigidly displaced bone, the soldier's head). Folding scale leaves nothing on the
        # nodes to lose: the export is in true units, identity transforms everywhere.
        bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)
        print("RIGANIM rig rotation+scale APPLIED TO DATA (identity nodes)")
    except Exception as e:
        # This IS the sole scale-fold on the conversion/clean path (gated above); without it the skeleton ships
        # ~100x off the mesh (the soldier's-head desync). A swallowed failure = a broken rig with exit 0. Fail loudly
        # (review 2026-08-16). (The legacy path never reaches this block — it deliberately keeps object scale.)
        print("RIGANIM ERROR: transform_apply(rotation+scale) failed (%s) — the skeleton would ship un-normalized (~100x off the mesh). Aborting instead of exporting a broken rig." % e)
        sys.exit(1)

# AUTO-GROUND (argv[10] == "1"): sit a rigged VEHICLE on the terrain with NO manual Position-offset dial — the
# animated path has no keel->z=0 like the static path. Robust measure: drop the model's LOWEST point (the tyre
# contact) to the skeleton origin — shift the whole rig up by -minZ. SELF-CORRECTING: a raw file lifts by its full
# sink, an already-grounded file lifts by ~0, so it can NEVER double-apply (the earlier "wheels-on minus wheels-off"
# protrusion measure did — a fixed lift that floated a pre-grounded file). Verts + bone rests move together (skin +
# rotation-only clips untouched). Runs after the convert fold, so Z is world-vertical. OPT-IN: only sensible for a
# vehicle whose lowest point IS its ground contact (a flyer/hover model would get pinned to the terrain).
if len(argv) > 10 and argv[10].strip() == "1" and arm is not None and me.vertices:
    _all_min = min(v.co.z for v in me.vertices)
    _off = -_all_min
    if abs(_off) > 1e-3:
        for v in me.vertices: v.co.z += _off
        me.update()
        bpy.context.view_layer.objects.active = arm
        bpy.ops.object.mode_set(mode='EDIT')
        for _eb in arm.data.edit_bones: _eb.head.z += _off; _eb.tail.z += _off
        bpy.ops.object.mode_set(mode='OBJECT')
        print("RIGANIM auto-ground: lowest point %.4f -> origin (lifted %.4f)" % (_all_min, _off))
    else:
        print("RIGANIM auto-ground: already grounded (minZ %.4f)" % _all_min)

# (2026-08-07: a POSITION-OFFSET block briefly lived here — argv[15]/[16], removed same-day: the runtime plugin
# has always applied the registry position to the pawn each frame, so baking it too DOUBLE-applied it. The
# runtime mechanism is the keeper; see UniversalBaker.RigAnimViaBlender's note.)

bpy.ops.object.select_all(action='SELECT')
# EXPORT SCALE (raw-FBX-parse evidence, fbx_lclscale/fbx_binddump): Blender's exporter writes meters->cm by scaling
# the ROOT OBJECTS x100 (`Lcl Scaling [100,100,100]`). Unity compensates with 0.01 in every skinned-mesh bindpose +
# a x100 root — a sandwich Amplitude's uniform-scale TRS composition mangles on deep bone chains (the Combine
# soldier's head rode off his shoulders). The proven ReconDrone file has NO node scaling only by luck: its glTF's
# tiny-authored 0.01 object scale exactly cancels the exporter's x100.
# - CONVERSION path: transform_apply normalized objects to scale 1, so pre-divide with
#   global_scale=0.01 -> net node scale 1, UnitScaleFactor 1, bind clusters 1 — the clean drone profile, by design.
# - DEPLOY-CONVERTED sources (2026-07-26, the T-62 finding): deploy_convert now guarantees identity nodes +
#   meter verts + a scale-free rig, so they get the SAME clean-unit export. The legacy sandwich (0.01 bindpose
#   + x100 root) tolerated rotation-only clips (the m114) but mangles the TRANSLATION curves a driving vehicle
#   needs — the T-62 rendered ~x100 giant off the sandwiched skeleton root.
# - LEGACY path (everything else): keep the exporter untouched, byte-identical output (the working models' contract).
clean_units = convert_rig or clean_units_input
gscale = 0.01 if clean_units else 1.0
if gscale != 1.0:
    print("RIGANIM export global_scale=0.01 (cancels the exporter's m->cm x100 root scaling)")
# EXPORT — the primary clip to outp, then each STATE ROLE'S clip to a sibling anim_<role>/ folder (same prepared
# rig/mesh, only the assigned action + frame range differ; bake_anim_use_all_actions=False bakes the ACTIVE action
# only, so each FBX carries exactly one take and each ClipCollection folder scan sees exactly one clip).
def _export_one(_a, _o):
    assign_action(_a)
    _fs, _fe = [int(round(v)) for v in _a.frame_range]
    bpy.context.scene.frame_start = _fs
    bpy.context.scene.frame_end = _fe
    # PIN THE EXPORT-TIME POSE (2026-07-19): the FBX's node defaults — which downstream become the imported
    # prefab's default pose and thereby the REFERENCE the engine encodes clips against — used to be whatever
    # frame the last processing step happened to leave the scene on (arbitrary). Evaluate the clip's own first
    # frame before every export so the reference is deterministic: the primary's frame 0 = the travel/rest pose.
    bpy.context.scene.frame_set(_fs)
    bpy.context.view_layer.update()
    _d = os.path.dirname(_o)
    if _d and not os.path.isdir(_d):
        os.makedirs(_d)
    bpy.ops.export_scene.fbx(filepath=_o, use_selection=False, add_leaf_bones=False, global_scale=gscale,
                             bake_anim=True, bake_anim_use_all_actions=False,
                             bake_anim_use_nla_strips=False, object_types={'ARMATURE', 'MESH'})
    print("RIGANIM wrote %s ('%s', frames %d..%d)" % (_o, _a.name, _fs, _fe))
_export_one(act, outp)
for _r, _cn in role_specs:
    _ro = os.path.join(os.path.dirname(os.path.dirname(outp)), "anim_" + _r, os.path.basename(outp))
    _export_one(role_acts[_r], _ro)
