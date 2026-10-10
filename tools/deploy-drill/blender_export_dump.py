"""The GLB deploy_convert.py exports, as Blender writes it (tools/deploy_drill.sh, part 8).

usage: blender --background --python blender_export_dump.py -- <deploy_convert.py> <jobs file> <work dir>

A jobs file line is  <key>|<input>|<argv[2]>|<argv[3]>|...  (the decisions dump's). Each job runs THE WHOLE SCRIPT with
its output set to <work dir>/<key>.glb, then the GLB is read back and written out row by row, every float as the hex of
its bits (the glTF JSON's doubles as the float32 the exporter stored - the writer emits float32 values):

    JOB     <key>
    LOG8    <a DEPLOY line of the export step: the trim, the purge, the sanitize, the write>
    DIES8   <exception>                      the script died in the export step
    GLB     <bytes> <json bytes> <bin bytes>
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


def h32(v): return struct.pack(">f", v).hex()


def sha(b): return hashlib.sha256(b).hexdigest()


def rows(path):
    b = open(path, "rb").read()
    jl = struct.unpack_from("<I", b, 12)[0]; j = json.loads(b[20:20 + jl].decode("utf-8"))
    bl = struct.unpack_from("<I", b, 20 + jl)[0]; bin_ = b[28 + jl:28 + jl + bl]
    out = ["GLB\t%d\t%d\t%d" % (len(b), jl, bl)]
    for s in j.get("scenes", []):
        out.append("SCENE\t%s\t%s" % (s.get("name", "-"), ",".join(str(n) for n in s.get("nodes", []))))
    def v(xs): return "\t".join(h32(x) for x in xs) if xs else "-"
    for i, n in enumerate(j.get("nodes", [])):
        out.append("NODE\t%d\t%s\t%s\t%s\t%s\t%s\t%s\t%s" % (i, n.get("name", "-"), ",".join(str(c) for c in n.get("children", [])) or "-",
                   n.get("mesh", "-"), n.get("skin", "-"), v(n.get("translation")), v(n.get("rotation")), v(n.get("scale"))))
    comp = {5120: ("b", 1), 5121: ("B", 1), 5122: ("h", 2), 5123: ("H", 2), 5125: ("I", 4), 5126: ("f", 4)}
    dims = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT4": 16}
    def acc_bytes(ai):
        a = j["accessors"][ai]; bv = j["bufferViews"][a["bufferView"]]
        fmt, size = comp[a["componentType"]]; n = dims[a["type"]] * a["count"]
        off = bv.get("byteOffset", 0) + a.get("byteOffset", 0)
        stride = bv.get("byteStride")
        if stride and stride != size * dims[a["type"]]:
            raise ValueError("a strided accessor is not read")
        return bin_[off:off + n * size], a, fmt
    for si, s in enumerate(j.get("skins", [])):
        out.append("SKIN\t%d\t%s\t%s\t%s" % (si, s.get("name", "-"), ",".join(str(x) for x in s["joints"]), s.get("skeleton", "-")))
        if "inverseBindMatrices" in s:
            raw, a, fmt = acc_bytes(s["inverseBindMatrices"])
            for k in range(a["count"]):
                m = struct.unpack_from("<16f", raw, 64 * k)
                out.append("IBM\t%d\t%d\t%s" % (si, k, "\t".join(h32(x) for x in m)))
    for mi, m in enumerate(j.get("meshes", [])):
        out.append("MESH\t%d\t%s\t%d" % (mi, m.get("name", "-"), len(m["primitives"])))
        for pi, p in enumerate(m["primitives"]):
            attrs = []
            for k in sorted(p["attributes"]):
                raw, a, fmt = acc_bytes(p["attributes"][k])
                attrs.append("%s:%d:%s:%s" % (k, a["count"], a["type"] + "/" + str(a["componentType"]) + ("n" if a.get("normalized") else ""), sha(raw)))
            if "indices" in p:
                raw, a, fmt = acc_bytes(p["indices"]); ic = a["count"]; ih = sha(raw)
            else:
                ic = 0; ih = "-"
            out.append("PRIM\t%d\t%d\t%s\t%d\t%d\t%s\t%s" % (mi, pi, p.get("material", "-"), p.get("mode", 4), ic, ih, "\t".join(attrs)))
    for mi, m in enumerate(j.get("materials", [])):
        out.append("MAT\t%d\t%s" % (mi, json.dumps(m, sort_keys=True, separators=(",", ":"))))
    for ii, im in enumerate(j.get("images", [])):
        bv = j["bufferViews"][im["bufferView"]]
        out.append("IMG\t%d\t%s\t%s\t%s" % (ii, im.get("name", "-"), im.get("mimeType", "-"), sha(bin_[bv.get("byteOffset", 0):bv.get("byteOffset", 0) + bv["byteLength"]])))
    for ti, t in enumerate(j.get("textures", [])):
        out.append("TEX\t%d\t%s\t%s" % (ti, t.get("source", "-"), t.get("sampler", "-")))
    for ai, a in enumerate(j.get("animations", [])):
        out.append("ANIM\t%d\t%s\t%d\t%d" % (ai, a.get("name", "-"), len(a["channels"]), len(a["samplers"])))
        for ci, c in enumerate(a["channels"]):
            s = a["samplers"][c["sampler"]]
            raw_i, acc_i, _ = acc_bytes(s["input"]); raw_o, acc_o, _ = acc_bytes(s["output"])
            out.append("CHAN\t%d\t%d\t%s\t%s\t%d\t%s\t%d\t%s\t%s\t%s\t%s" % (ai, ci, c["target"].get("node", "-"), c["target"]["path"], c["sampler"], s.get("interpolation", "LINEAR"),
                       acc_i["count"], h32(acc_i["min"][0]), h32(acc_i["max"][0]), sha(raw_i), sha(raw_o)))
    return out


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
