"""rig_anim.py's scene as a GLB - the oracle of the Factory's GLB rig route (step (f) and (g) of replacing Blender, 2026-10-14).

usage: blender --background --python rig_anim_glb.py -- <rig_anim.py> <in model> <out .glb> <argv[2]> ... <argv[14]>

Runs rig_anim.py's own source cut just before its FBX export (the four stages the C# port models: tools/riganim_drill.sh
holds them row for row), then exports the scene ONCE through Blender's glTF exporter: every action in the file, the
scene's frame range widened to their rounded ranges, the first frame evaluated (the script's own export pins the pose so
before each clip), `export_scene.gltf(GLB, animations, frame range, Y up)` as deploy_convert.py calls it. The C# port
(BlenderRigAnimInProcess -> BlenderRigExport + BlenderGlbWriter) writes the same file; the Bake Tests row holds the two
byte for byte in Unity, the drill outside it (tools/riganim-drill/blender_riganim_dump.py runs this export step through
`export_glb`, so there is one export and one oracle). The script's own exits (sys.exit(1) after a RIGANIM ERROR line)
are passed through: no GLB then.
"""
import bpy, sys, os, io, contextlib

# the cut: the script's own top-level line that starts its export (found exactly once at column 0, else a FAIL)
EXPORT_MARKER = "bpy.ops.object.select_all(action='SELECT')\n# EXPORT SCALE"

# The export step, run in the script's namespace after stage 4.
EXPORT_CODE = '''
_acts5 = list(bpy.data.actions)
_ranges5 = [[int(round(v)) for v in _a.frame_range] for _a in _acts5]
if _ranges5:
    bpy.context.scene.frame_start = min(r[0] for r in _ranges5)
    bpy.context.scene.frame_end = max(r[1] for r in _ranges5)
bpy.context.scene.frame_set(bpy.context.scene.frame_start)
bpy.context.view_layer.update()
print("RIGANIM glb: %d action(s), frames %d..%d" % (len(_acts5), bpy.context.scene.frame_start, bpy.context.scene.frame_end))
bpy.ops.export_scene.gltf(filepath=_GLB_OUT, export_format='GLB', export_animations=True, export_frame_range=True, export_yup=True)
print("RIGANIM glb wrote %s" % _GLB_OUT)
'''
EXPORT_CHUNK = compile(EXPORT_CODE, "<riganim export>", "exec")


def export_glb(g, path):
    """The export in the namespace `g` the script ran in; returns (the RIGANIM lines, the exception text or None)."""
    out = io.StringIO(); died = None
    g["_GLB_OUT"] = path
    try:
        with contextlib.redirect_stdout(out):
            exec(EXPORT_CHUNK, g)
    except Exception as e:
        died = "%s: %s" % (type(e).__name__, e)
    lines = [l for l in out.getvalue().split("\n") if l.startswith("RIGANIM")]
    if died is None and not os.path.exists(path):
        died = "no GLB written"
    return lines, died


def cut_before_export(source, script):
    """The script's source up to its export marker, compiled with the script's line numbers."""
    hits = [i for i in range(len(source)) if source.startswith("\n" + EXPORT_MARKER, i)]
    if len(hits) != 1:
        raise RuntimeError("the export marker is found %d times in %s" % (len(hits), script))
    return compile(source[:hits[0] + 1], script, "exec")


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    args = sys.argv[sys.argv.index("--") + 1:]
    script, src, out = args[0], args[1], args[2]
    source = open(script, encoding="utf-8").read()
    code = cut_before_export(source, script)
    # the script sees the argv it expects: [blender, --, in, out, argv[2]...]; its out is never written (the FBX export is cut)
    sys.argv = ["blender", "--", src, os.path.splitext(out)[0] + ".fbx"] + list(args[3:])
    g = {"__name__": "__main__"}
    exec(code, g)   # a sys.exit(1) of the script passes through: no GLB
    lines, died = export_glb(g, out)
    for l in lines:
        print(l)
    if died is not None:
        print("RIGANIM ERROR: the GLB export died: " + died)
        sys.exit(1)
