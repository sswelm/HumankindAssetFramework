# Headless Blender exporter for the Universal Model Factory.
# Opens the .blend passed on the command line and writes a GLB the baker can import.
# Usage: blender <file.blend> --background --python blend_export.py -- <out.glb>
import bpy, sys, os

argv = sys.argv[sys.argv.index('--') + 1:]
out_glb = argv[0]

blend_dir = os.path.dirname(bpy.data.filepath)
# Where textures might live if the blend stored dead/absolute paths (common for Sketchfab: source/*.blend + textures/*).
search_dirs = [
    blend_dir,
    os.path.join(blend_dir, "textures"),
    os.path.join(os.path.dirname(blend_dir), "textures"),
    os.path.join(os.path.dirname(blend_dir), "texture"),
]

for img in bpy.data.images:
    if img.source == 'GENERATED' or img.name in ('Render Result', 'Viewer Node'):
        continue
    cur = bpy.path.abspath(img.filepath) if img.filepath else ""
    if cur and os.path.exists(cur):
        img.reload()
        continue
    base = os.path.basename(img.filepath) if img.filepath else (img.name if "." in img.name else img.name + ".png")
    for d in search_dirs:
        cand = os.path.join(d, base)
        if os.path.exists(cand):
            img.filepath = cand
            img.reload()
            print(f"[img] recovered {img.name} -> {cand}")
            break
    else:
        print(f"[img] MISSING texture for '{img.name}' (was {img.filepath}) - model may export untextured")

try:
    bpy.ops.export_scene.gltf(
        filepath=out_glb,
        export_format='GLB',
        use_selection=False,
        export_apply=True,   # apply modifiers
        export_yup=True,     # glTF convention (matches our GLB path)
    )
    print(f"[export] wrote {out_glb}")
except Exception as e:
    print(f"[export] FAILED: {e}")
    sys.exit(1)
