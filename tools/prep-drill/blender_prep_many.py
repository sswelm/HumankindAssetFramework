"""Blender's own model prep for MANY files in ONE process (tools/prep-drill, step 5 milestone d): for each file the REAL
editor/Tools~/prep_model.py is run - by runpy, with its command line set, exactly as the Factory runs it - twice: with a
target of a third of the file's triangles (the reduce collapses) and with a target of all of them (the ratio clamps to 1:
nothing collapses, the file still goes through the importer, the modifier's apply and the exporter). Each run writes a
GLB; one row names it for PrepDrill.cs:

    PREP <TAB> <file key> <TAB> <tag: third | full> <TAB> <total triangles> <TAB> <target> <TAB> <the GLB it wrote>
    PREPFAIL <TAB> <file key> <TAB> <tag> <TAB> <total> <TAB> <target> <TAB> <why prep_model.py itself failed>

The total is counted as prep_model counts it (triangles over the scene's mesh objects, the importer's bone-shape
Icosphere purged), on an import of this script's own before the run.

usage: blender --background --python blender_prep_many.py -- <prep_model.py> <out dir> <file>...
"""
import bpy, os, runpy, sys

sys.stdout.reconfigure(encoding="utf-8")
args = sys.argv[sys.argv.index("--") + 1:]
prep_script, out_dir, files = args[0], args[1], args[2:]
os.makedirs(out_dir, exist_ok=True)
fails = 0
for index, path in enumerate(files):
    key = path.replace("\\", "/").lower()
    try:
        bpy.ops.wm.read_factory_settings(use_empty=True)
        bpy.ops.import_scene.gltf(filepath=path)
        for ico in [o for o in bpy.data.objects if o.type == 'MESH' and o.name.startswith('Icosphere') and not o.vertex_groups]:
            bpy.data.objects.remove(ico, do_unlink=True)
        total = sum(sum(len(p.vertices) - 2 for p in o.data.polygons) for o in bpy.context.scene.objects if o.type == 'MESH')
        for tag, target in (("third", max(1, total // 3)), ("full", max(1, total))):
            out = os.path.join(out_dir, "%04d_%s.glb" % (index, tag)).replace("\\", "/")
            argv = sys.argv
            sys.argv = ["blender", "--", path, out, "", str(target)]
            failed = None
            try:
                runpy.run_path(prep_script, run_name="__main__")
            except SystemExit as e:
                if e.code not in (0, None):
                    failed = "prep_model.py exited %s" % e.code
            except Exception as e:
                failed = "%s: %s" % (type(e).__name__, " ".join(str(e).split()))
            finally:
                sys.argv = argv
            if failed is None and not os.path.isfile(out):
                failed = "prep_model.py wrote no file"
            if failed is not None:
                # prep_model itself fails on this file (the Factory's bake would): a row of its own, judged by the C# side
                print("PREPFAIL\t%s\t%s\t%d\t%d\t%s" % (key, tag, total, target, failed), flush=True)
                continue
            print("PREP\t%s\t%s\t%d\t%d\t%s" % (key, tag, total, target, out), flush=True)
        print("FILE\t%s\tok" % key, flush=True)
    except Exception as e:
        print("FAIL\t%s\t%s: %s" % (key, type(e).__name__, e), flush=True)
        fails += 1
sys.exit(1 if fails else 0)
