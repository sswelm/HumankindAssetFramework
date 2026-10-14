"""A written GLB read back row by row (the deploy export dump's rows, tools/deploy_drill.sh part 8; the rig GLB oracle of
tools/riganim_drill.sh step (f) shares it): every float as the hex of its float32 bits.

    GLB     <bytes> <json bytes> <bin bytes> <sha256 of the file>
    SCENE   <name> <root node indices>
    NODE    <index> <name> <children> <mesh or -> <skin or -> <translation 3 or -> <rotation 4 or -> <scale 3 or ->
    SKIN    <index> <name> <joints> <skeleton or ->
    IBM     <skin> <joint ordinal> <16, column-major as stored>
    MESH    <index> <name> <primitive count>
    PRIM    <mesh> <primitive> <material or -> <mode> <indices count> <sha256 of the indices> <attribute:accessor count:type:sha256 of its bytes ...>
    MAT     <index> <json of the material, keys sorted>
    IMG     <index> <name> <mimeType> <sha256 of the bytes>
    TEX     <index> <source> <sampler or -> <json of the other fields>
    SAMP    <index> <json>
    ANIM    <index> <name> <channel count> <sampler count>
    CHAN    <anim> <channel> <target node> <path> <sampler> <interpolation> <input count> <input min> <input max> <sha256 of the input bytes> <sha256 of the output bytes>
"""
import hashlib, json, struct


def h32(v): return struct.pack(">f", v).hex()


def sha(b): return hashlib.sha256(b).hexdigest()


def rows(path):
    b = open(path, "rb").read()
    jl = struct.unpack_from("<I", b, 12)[0]; j = json.loads(b[20:20 + jl].decode("utf-8"))
    bin_ = b""; bl = 0
    if len(b) >= 28 + jl:   # a file without accessors has no BIN chunk
        bl = struct.unpack_from("<I", b, 20 + jl)[0]; bin_ = b[28 + jl:28 + jl + bl]
    out = ["GLB\t%d\t%d\t%d\t%s" % (len(b), jl, bl, sha(b))]   # ... and the file's SHA-256: the port's bytes against it (8c)
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
        out.append("TEX\t%d\t%s\t%s\t%s" % (ti, t.get("source", "-"), t.get("sampler", "-"), json.dumps({k: v for k, v in t.items() if k not in ("source", "sampler")}, sort_keys=True, separators=(",", ":"))))
    for si, s in enumerate(j.get("samplers", [])):
        out.append("SAMP\t%d\t%s" % (si, json.dumps(s, sort_keys=True, separators=(",", ":"))))
    for ai, a in enumerate(j.get("animations", [])):
        out.append("ANIM\t%d\t%s\t%d\t%d" % (ai, a.get("name", "-"), len(a["channels"]), len(a["samplers"])))
        for ci, c in enumerate(a["channels"]):
            s = a["samplers"][c["sampler"]]
            raw_i, acc_i, _ = acc_bytes(s["input"]); raw_o, acc_o, _ = acc_bytes(s["output"])
            out.append("CHAN\t%d\t%d\t%s\t%s\t%d\t%s\t%d\t%s\t%s\t%s\t%s" % (ai, ci, c["target"].get("node", "-"), c["target"]["path"], c["sampler"], s.get("interpolation", "LINEAR"),
                       acc_i["count"], h32(acc_i["min"][0]), h32(acc_i["max"][0]), sha(raw_i), sha(raw_o)))
    return out

