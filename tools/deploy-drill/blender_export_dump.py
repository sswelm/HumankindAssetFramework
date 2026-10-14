"""The GLB deploy_convert.py exports, as Blender writes it (tools/deploy_drill.sh, part 8).

usage: blender --background --python blender_export_dump.py -- <deploy_convert.py> <jobs file> <work dir>

A jobs file line is  <key>|<input>|<argv[2]>|<argv[3]>|...  (the decisions dump's). Each job runs THE WHOLE SCRIPT with
its output set to <work dir>/<key>.glb, then the GLB is read back and written out row by row, every float as the hex of
its bits (the glTF JSON's doubles as the float32 the exporter stored - the writer emits float32 values):

    JOB     <key>
    LOG8    <a DEPLOY line of the export step: the trim, the purge, the sanitize, the write>
    DIES8   <exception>                      the script died in the export step
    GLB     <bytes> <json bytes> <bin bytes> <sha256 of the file>
    SCENE   <name> <root node indices>
    NODE    <index> <name> <children> <mesh or -> <skin or -> <translation 3 or -> <rotation 4 or -> <scale 3 or ->
    SKIN    <index> <name> <joints> <skeleton or ->
    IBM     <skin> <joint ordinal> <16, column-major as stored>
    MESH    <index> <name> <primitive count>
    PRIM    <mesh> <primitive> <material or -> <mode> <indices count> <sha256 of the indices> <attribute:accessor count:type:sha256 of its bytes ...>
    MAT     <index> <json of the material, keys sorted>
    IMG     <index> <name> <mimeType> <sha256 of the bytes>
    TEX     <index> <source> <sampler or ->
    ANIM    <index> <name> <channel count> <sampler count>
    CHAN    <anim> <channel> <target node> <path> <sampler> <interpolation> <input count> <input min> <input max> <sha256 of the input bytes> <sha256 of the output bytes>
    DONE    <key>
"""
import bpy, sys, io, os, json, struct, hashlib, contextlib, traceback

sys.stdout.reconfigure(encoding="utf-8")
args = sys.argv[sys.argv.index("--") + 1:]
script, jobs, work = args[0], args[1], args[2]
source = open(script, encoding="utf-8").read()
code = compile(source, script, "exec")
os.makedirs(work, exist_ok=True)


sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from glb_rows import rows   # noqa: E402  (the reader, shared with the rig dump)


fails = 0
for line in open(jobs, encoding="utf-8-sig").read().split("\n"):
    line = line.rstrip("\r")
    if not line.strip():
        continue
    t = line.split("|")
    key = t[0]
    print("JOB\t%s" % key, flush=True)
    saved = sys.argv
    try:
        outp = os.path.join(work, key.replace(":", "_") + ".glb")
        if os.path.exists(outp): os.remove(outp)
        sys.argv = ["blender", "--", t[1], outp] + t[2:]
        g = {"__name__": "__main__"}
        out = io.StringIO(); code_exit = None; died = None
        try:
            with contextlib.redirect_stdout(out):
                exec(code, g)
        except SystemExit as e:
            code_exit = e.code
        except Exception as e:
            died = "%s: %s" % (type(e).__name__, e)
        for l in out.getvalue().split("\n"):
            if l.startswith("DEPLOY trim") or l.startswith("DEPLOY purged") or l.startswith("DEPLOY sanitized") or l.startswith("DEPLOY wrote"):
                print("LOG8\t%s" % l)
        if code_exit is not None:
            print("EXIT8\t%s" % code_exit)
        elif died is not None:
            print("DIES8\t%s" % died)
        elif os.path.exists(outp):
            for r in rows(outp):
                print(r)
        else:
            print("DIES8\tno output written")
        print("DONE\t%s" % key, flush=True)
    except Exception as e:
        traceback.print_exc()
        print("FAIL\t%s\t%s: %s" % (key, type(e).__name__, e), flush=True)
        fails += 1
    finally:
        sys.argv = saved
sys.exit(1 if fails else 0)
