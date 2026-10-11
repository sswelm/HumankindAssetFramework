"""The materials, textures, samplers and images as Blender's glTF exporter writes them after an import - the round
trip every glTF source takes through deploy_convert.py and prep_model.py (part 8a-ii).

usage: blender --background --python blender_material_dump.py -- <file.glb|.gltf> ...

Per file: a factory-fresh scene, the import, an export with deploy_convert.py's settings (GLB, animations, the frame
range, Y up), and the written file read back:

    FILE    <path>
    DIES    <exception>                       the import or the export failed
    MAT     <index> <json of the material, keys sorted>
    IMG     <index> <name> <mimeType> <bytes> <sha256 of the bytes>
    TEX     <index> <source> <sampler or -> <json of the other fields>
    SAMP    <index> <json of the sampler>
    MATS    <names of the Blender materials, tab-separated, in bpy.data.materials order>
    DONE    <path>
"""
import bpy, sys, os, json, struct, hashlib, tempfile, traceback

sys.stdout.reconfigure(encoding="utf-8")
files = sys.argv[sys.argv.index("--") + 1:]
work = tempfile.mkdtemp(prefix="hafmat")


def rows(path):
    b = open(path, "rb").read()
    jl = struct.unpack_from("<I", b, 12)[0]; j = json.loads(b[20:20 + jl].decode("utf-8"))
    bin_ = b""
    if len(b) >= 28 + jl:   # a file without accessors has no BIN chunk
        bl = struct.unpack_from("<I", b, 20 + jl)[0]; bin_ = b[28 + jl:28 + jl + bl]
    out = []
    for mi, m in enumerate(j.get("materials", [])):
        out.append("MAT\t%d\t%s" % (mi, json.dumps(m, sort_keys=True, separators=(",", ":"))))
    for ii, im in enumerate(j.get("images", [])):
        bv = j["bufferViews"][im["bufferView"]]
        d = bin_[bv.get("byteOffset", 0):bv.get("byteOffset", 0) + bv["byteLength"]]
        out.append("IMG\t%d\t%s\t%s\t%d\t%s" % (ii, im.get("name", "-"), im.get("mimeType", "-"), len(d), hashlib.sha256(d).hexdigest()))
    for ti, t in enumerate(j.get("textures", [])):
        out.append("TEX\t%d\t%s\t%s\t%s" % (ti, t.get("source", "-"), t.get("sampler", "-"), json.dumps({k: v for k, v in t.items() if k not in ("source", "sampler")}, sort_keys=True, separators=(",", ":"))))
    for si, s in enumerate(j.get("samplers", [])):
        out.append("SAMP\t%d\t%s" % (si, json.dumps(s, sort_keys=True, separators=(",", ":"))))
    return out


fails = 0
for i, f in enumerate(files):
    print("FILE\t%s" % f, flush=True)
    try:
        bpy.ops.wm.read_factory_settings(use_empty=True)
        outp = os.path.join(work, "m%d.glb" % i)
        try:
            bpy.ops.import_scene.gltf(filepath=f)
            bpy.ops.export_scene.gltf(filepath=outp, export_format='GLB', export_animations=True, export_frame_range=True, export_yup=True)
        except Exception as e:
            print("DIES\t%s: %s" % (type(e).__name__, e)); print("DONE\t%s" % f, flush=True); continue
        for r in rows(outp):
            print(r)
        print("MATS\t" + "\t".join(m.name for m in bpy.data.materials))
        os.remove(outp)
        print("DONE\t%s" % f, flush=True)
    except Exception as e:
        traceback.print_exc()
        print("FAIL\t%s\t%s: %s" % (f, type(e).__name__, e), flush=True)
        fails += 1
sys.exit(1 if fails else 0)
