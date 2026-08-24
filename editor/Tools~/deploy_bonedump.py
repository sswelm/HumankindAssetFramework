# deploy_bonedump.py — a DETERMINISTIC snapshot of a deploy_convert output GLB's armature, for the golden-master
# regression test (Tools/deploy_regression.sh). Prints the armature NAME (= the legacy/contract path selector rig_anim
# keys off), the bone COUNT, and every bone's rotation + location at frame start / mid / end, rounded to 3 decimals.
# This is exactly the data that caught the m114 regressions (bone count, path, leg rotation). Run headless:
#   blender --background --python deploy_bonedump.py -- <in.glb>
import bpy, sys
glb = sys.argv[sys.argv.index("--") + 1:][0]
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=glb)
arms = [o for o in bpy.data.objects if o.type == 'ARMATURE']
if not arms:
    print("ARMATURE <none>"); print("BONES 0"); sys.exit(0)
arm = arms[0]
fs, fe = int(bpy.context.scene.frame_start), int(bpy.context.scene.frame_end)
frames = sorted(set([fs, (fs + fe) // 2, fe]))
print("ARMATURE %s" % arm.name)               # DeployArm = legacy path, DeployArmV2 = contract path
print("BONES %d" % len(arm.pose.bones))
print("FRAMES %s" % ",".join(map(str, frames)))
for f in frames:
    bpy.context.scene.frame_set(f)
    for b in sorted(arm.pose.bones, key=lambda x: x.name):
        q = b.rotation_quaternion; l = b.location
        print("f%04d %-34s r=%.3f,%.3f,%.3f,%.3f l=%.3f,%.3f,%.3f"
              % (f, b.name, q.w, q.x, q.y, q.z, l.x, l.y, l.z))
