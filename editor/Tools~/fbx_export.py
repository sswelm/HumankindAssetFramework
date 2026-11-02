# Headless Blender FBX -> GLB for the Model Factory's Bake Tests (step 6e of the native FBX reader, 2026-11-02): Blender's own
# FBX import (every option at its default) followed by its own glTF export (every option at its default) - the file the native
# reader's HafModel is held to (tools/fbx_model_drill.sh). The rig-slim row runs Blender's rig_anim_glb.py on THIS file when a
# registry entry's source is an .fbx, so both routes start from the same scene convention (the glTF importer's), and the
# comparison judges the port, not Blender's two importers.
# Usage: blender --background --python fbx_export.py -- <in.fbx> <out.glb>
import bpy, sys

argv = sys.argv[sys.argv.index('--') + 1:]
inp, out_glb = argv[0], argv[1]
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=inp)
bpy.ops.export_scene.gltf(filepath=out_glb, export_format='GLB', use_selection=False)
print("[fbx_export] wrote " + out_glb)
