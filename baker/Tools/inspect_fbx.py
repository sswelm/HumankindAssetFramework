# inspect_fbx.py — convert a model into inspection FBXs for the editor's clip player dialog (preview / play /
# scrub / frame-step, pick a start..end slice). Two source kinds, detected automatically:
#
# - ARMATURE-animated (skinned rigs, deploy-converted files): one FBX PER ACTION, meshes JOINED into one object
#   (HARD-LEARNED: Unity's FBX import of a skinned rig as MULTIPLE mesh objects mirrors the animation against the
#   meshes — the M114 previewed with crossed legs while the same FBX sampled correctly in Blender).
# - NODE-animated (raw Sketchfab vehicles: separate part objects moved by their own transforms — the kind the
#   deploy conversion exists FOR): one FBX of the WHOLE SCENE, all simultaneous per-object actions baked, meshes
#   NOT joined and animated EMPTY parents included (joining freezes node motion dead — the "nothing moves" bug;
#   the parts' motion lives on their parent empties, so 'EMPTY' must be in the export object_types).
#
# Fidelity rules learned the hard way:
# - the animation is exported COMPLETE — rotations AND locations. An earlier location-strip (meant to mimic the
#   shipping pipeline) CAUSED the M114's crossed-legs preview: the trail legs spread by rotation+translation, and
#   rotation-only swings them about the wrong pivot, sweeping them inward. The picker is a RAW animation player of
#   the source; the shipping pipeline's rotation-only conversion is made safe by its own rest-fold step, not here.
# - clip names are recorded in a manifest.txt (filename<TAB>exact action name): FBX take names are unreliable
#   (slot-assignment quirks yielded a take named "Scene"), and the safe filename can't round-trip characters like
#   the '|' in Sketchfab action names.
#
#   blender -b -P inspect_fbx.py -- <in.glb|gltf|fbx|blend> <outDir>
import bpy, sys, os, re

argv = sys.argv[sys.argv.index("--") + 1:]
inp, outdir = argv[0], argv[1]

bpy.ops.wm.read_factory_settings(use_empty=True)
ext = os.path.splitext(inp)[1].lower()
if ext == ".fbx":
    bpy.ops.import_scene.fbx(filepath=inp)
elif ext == ".blend":
    bpy.ops.wm.open_mainfile(filepath=inp)
else:
    bpy.ops.import_scene.gltf(filepath=inp)

os.makedirs(outdir, exist_ok=True)
manifest = []

arm = next((o for o in bpy.data.objects if o.type == 'ARMATURE'), None)
node_anim = [o for o in bpy.data.objects if o.type != 'ARMATURE' and o.animation_data and o.animation_data.action]

if node_anim:
    # --- NODE-ANIMATED source: many per-object actions are simultaneous tracks of ONE take. Export the whole
    #     scene once over the union frame range; bake_anim_use_all_actions=False bakes each object's own active
    #     action — exactly the take as authored. No join, no per-action split. ---
    fmin, fmax = 1e9, -1e9
    for o in bpy.data.objects:
        if o.animation_data and o.animation_data.action:
            fr = o.animation_data.action.frame_range
            fmin = min(fmin, fr[0]); fmax = max(fmax, fr[1])
    fs, fe = int(round(fmin)), int(round(fmax))
    bpy.context.scene.frame_start, bpy.context.scene.frame_end = fs, fe
    take = next((a.name for a in bpy.data.actions if a.name.lower().startswith("take")), bpy.data.actions[0].name)
    out = os.path.join(outdir, "scene_take.fbx")
    bpy.ops.export_scene.fbx(filepath=out, use_selection=False, add_leaf_bones=False,
                             bake_anim=True, bake_anim_use_all_actions=False,
                             bake_anim_use_nla_strips=False, object_types={'EMPTY', 'ARMATURE', 'MESH'})
    manifest.append("scene_take.fbx\t%s" % take)
    print("INSPECT node-animated source: %d animated objects, whole scene as ONE take ('%s' frames %d..%d)"
          % (len(node_anim), take, fs, fe))
else:
    # --- ARMATURE-animated source: one FBX per action, meshes joined (see header). ---
    if arm is None:
        print("INSPECT ERROR: no animation found (no armature, no animated nodes)"); sys.exit(1)
    if arm.animation_data is None:
        arm.animation_data_create()
    meshes = [o for o in bpy.context.scene.objects if o.type == 'MESH']
    for m in meshes:
        if m.data.users > 1:
            m.data = m.data.copy()          # single-user-ize (join refuses multi-user data)
    if len(meshes) > 1:
        bpy.ops.object.select_all(action='DESELECT')
        for m in meshes:
            m.select_set(True)
        bpy.context.view_layer.objects.active = meshes[0]
        bpy.ops.object.join()
        print("INSPECT joined %d meshes" % len(meshes))

    def assign(a):
        arm.animation_data.action = a
        try: arm.animation_data.action_slot = a.slots[0]   # Blender 4.4+/5 slotted actions
        except Exception: pass

    for a in list(bpy.data.actions):
        assign(a)
        fs, fe = [int(round(v)) for v in a.frame_range]
        bpy.context.scene.frame_start, bpy.context.scene.frame_end = fs, fe
        safe = re.sub(r'[^A-Za-z0-9_.-]+', '_', a.name)
        out = os.path.join(outdir, safe + ".fbx")
        bpy.ops.export_scene.fbx(filepath=out, use_selection=False, add_leaf_bones=False,
                                 bake_anim=True, bake_anim_use_all_actions=False,
                                 bake_anim_use_nla_strips=False, object_types={'ARMATURE', 'MESH'})
        manifest.append("%s\t%s" % (safe + ".fbx", a.name))
        print("INSPECT wrote %s ('%s' frames %d..%d)" % (out, a.name, fs, fe))

with open(os.path.join(outdir, "manifest.txt"), "w", encoding="utf-8") as f:
    f.write("\n".join(manifest))
print("INSPECT done: %d clip fbx(s) + manifest" % len(manifest))
