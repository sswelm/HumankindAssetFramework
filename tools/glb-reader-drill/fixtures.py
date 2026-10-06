"""Synthetic glTF files for the shapes the registry does NOT have (review of PR #109 round 5 made the first one; the
round-trip diversity PR made a library of them). Every registry file is a Blender or Sketchfab export: .glb only,
float attributes, one tight buffer view per attribute, TRIANGLES only, one scene, LINEAR/STEP keys, 4 influences.
A reader that only ever saw those has not been tested on the rest of the specification. Each fixture here is small,
deterministic (no timestamps, no randomness), named for the one shape it exercises, and goes through BOTH drills:
the reader against Blender (values), the writer round trip (field by field) and back through Blender.

    python fixtures.py <outDir>        -> writes every fixture, prints one path per line

Blender notes, measured 2026-10-02 (Blender 5.1): it imports EVERY node, the other scenes' and the orphans' too (the
scenes fixture: 4 triangles drawn on both sides); POINTS become loose vertices and LINES loose edges (no faces, no
area); strips and fans arrive triangulated; normalized integer attributes arrive dequantized; a material is created
only when a primitive uses it, and one is INVENTED for a COLOR_0 primitive that has none.
"""
import base64, json, math, os, struct, sys, zlib

# ---------------------------------------------------------------------------------------------- a buffer builder
CT = {"b": 5120, "B": 5121, "h": 5122, "H": 5123, "I": 5125, "f": 5126}
NCOMP = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT4": 16}


class Buf:
    """One glTF buffer with its views and accessors; every accessor tight unless `view` names a shared one."""

    def __init__(self):
        self.bytes = bytearray(); self.views = []; self.accessors = []

    def pad(self, n=4):
        while len(self.bytes) % n:
            self.bytes.append(0)

    def view(self, raw, stride=None, target=None):
        self.pad()
        v = {"buffer": 0, "byteOffset": len(self.bytes), "byteLength": len(raw)}
        if stride:
            v["byteStride"] = stride
        if target:
            v["target"] = target
        self.views.append(v); self.bytes.extend(raw)
        return len(self.views) - 1

    def accessor(self, data, fmt, kind, normalized=False, minmax=None, view=None, offset=0, count=None):
        n = NCOMP[kind]
        if view is None:
            raw = b"".join(struct.pack("<" + fmt * n, *(v if n > 1 else (v,))) for v in data)
            view = self.view(raw)
        a = {"bufferView": view, "byteOffset": offset, "componentType": CT[fmt], "type": kind, "count": count if count is not None else len(data)}
        if offset == 0:
            del a["byteOffset"]
        if normalized:
            a["normalized"] = True
        if minmax or (kind == "VEC3" and fmt == "f" and minmax is None and data):
            a["min"] = [min(v[i] for v in data) for i in range(n)]
            a["max"] = [max(v[i] for v in data) for i in range(n)]
        if kind == "SCALAR" and fmt == "f" and data and minmax is None:
            a["min"] = [min(data)]; a["max"] = [max(data)]
        self.accessors.append(a)
        return len(self.accessors) - 1

    def blob(self, raw):
        """An opaque view (an image), its index."""
        return self.view(raw)


def png(w, h, rgb):
    """A valid PNG of one colour, written by hand (no library): what an image loader and Blender both accept."""
    raw = b"".join(b"\x00" + bytes(rgb) * w for _ in range(h))

    def chunk(tag, body):
        return struct.pack(">I", len(body)) + tag + body + struct.pack(">I", zlib.crc32(tag + body) & 0xFFFFFFFF)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")


def write_glb(path, root, buf):
    buf.pad()
    root["buffers"] = [{"byteLength": len(buf.bytes)}]
    root["bufferViews"] = buf.views; root["accessors"] = buf.accessors
    js = json.dumps(root, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
    while len(js) % 4:
        js += b" "
    with open(path, "wb") as f:
        f.write(struct.pack("<III", 0x46546C67, 2, 12 + 8 + len(js) + 8 + len(buf.bytes)) + struct.pack("<II", len(js), 0x4E4F534A) + js + struct.pack("<II", len(buf.bytes), 0x004E4942) + bytes(buf.bytes))


def write_gltf(path, root, buf, bin_name):
    buf.pad()
    root["buffers"] = [{"byteLength": len(buf.bytes), "uri": bin_name}]
    root["bufferViews"] = buf.views; root["accessors"] = buf.accessors
    with open(os.path.join(os.path.dirname(path), bin_name), "wb") as f:
        f.write(bytes(buf.bytes))
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(root, f, indent=1, ensure_ascii=False)


def base(generator, **extra):
    r = {"asset": {"version": "2.0", "generator": "fixtures.py " + generator}}
    r.update(extra)
    return r


def grid(nx, ny, size=1.0):
    """A flat grid in XY: positions, normals, uvs, indices."""
    pos, nrm, uv = [], [], []
    for j in range(ny + 1):
        for i in range(nx + 1):
            pos.append((i * size / nx, j * size / ny, 0.0)); nrm.append((0.0, 0.0, 1.0)); uv.append((i / nx, 1 - j / ny))
    idx = []
    for j in range(ny):
        for i in range(nx):
            a = j * (nx + 1) + i; b = a + 1; c = a + nx + 1; d = c + 1
            idx += [a, b, d, a, d, c]
    return pos, nrm, uv, idx


# ---------------------------------------------------------------------------------------------- the fixtures

def fx_two_targets(out):
    """One animation driving TWO nodes whose channels end at different times (1 s and 2 s), one starting at 0.5 s:
    Blender 5.1 makes ONE slotted action spanning 0..2 s (review of PR #109, round 5)."""
    b = Buf()
    pos = b.accessor([(0, 0, 0), (1, 0, 0), (0, 1, 0)], "f", "VEC3")
    idx = b.accessor([0, 1, 2], "H", "SCALAR")
    t_a = b.accessor([0.0, 1.0], "f", "SCALAR"); v_a = b.accessor([(0, 0, 0), (1, 0, 0)], "f", "VEC3")
    t_b = b.accessor([0.5, 2.0], "f", "SCALAR"); v_b = b.accessor([(0, 0, 0), (0, 1, 0)], "f", "VEC3")
    root = base("two_targets",
                meshes=[{"name": "tri", "primitives": [{"attributes": {"POSITION": pos}, "indices": idx}]}],
                nodes=[{"name": "A", "mesh": 0}, {"name": "B", "mesh": 0, "translation": [3, 0, 0]}],
                scenes=[{"nodes": [0, 1]}], scene=0,
                animations=[{"name": "both", "samplers": [{"input": t_a, "output": v_a}, {"input": t_b, "output": v_b}],
                             "channels": [{"sampler": 0, "target": {"node": 0, "path": "translation"}},
                                          {"sampler": 1, "target": {"node": 1, "path": "translation"}}]}])
    write_glb(os.path.join(out, "two_targets.glb"), root, b)


def fx_normalized(out):
    """Normalized integer attributes, as quantizing exporters and many game rips write them: UV as unsigned short,
    colour as unsigned byte VEC3, joints as unsigned byte, weights as unsigned short; one skin of two joints with
    inverse bind matrices. The reader must dequantize every one; Blender does. No material anywhere: Blender invents
    one per mesh that has a COLOR_0 primitive without one (two meshes here -> two; the drill states that count)."""
    b = Buf()
    pos, nrm, uv, idx = grid(2, 2, 2.0)
    P = b.accessor(pos, "f", "VEC3"); N = b.accessor(nrm, "f", "VEC3")
    UV = b.accessor([(int(u * 65535), int(v * 65535)) for u, v in uv], "H", "VEC2", normalized=True, minmax=False)
    COL = b.accessor([(255, 128, 0)] * len(pos), "B", "VEC3", normalized=True, minmax=False)
    J = b.accessor([(0, 1, 0, 0)] * len(pos), "B", "VEC4", minmax=False)
    W = b.accessor([(int(0.75 * 65535), 65535 - int(0.75 * 65535), 0, 0)] * len(pos), "H", "VEC4", normalized=True, minmax=False)
    I = b.accessor(idx, "H", "SCALAR")
    ibm = b.accessor([(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1), (1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, -1, 0, 1)], "f", "MAT4", minmax=False)
    P2 = b.accessor([(4, 0, 0), (5, 0, 0), (4, 1, 0)], "f", "VEC3"); COL2 = b.accessor([(0, 0, 255, 255)] * 3, "B", "VEC4", normalized=True, minmax=False)
    root = base("normalized",
                meshes=[{"name": "quad", "primitives": [{"attributes": {"POSITION": P, "NORMAL": N, "TEXCOORD_0": UV, "COLOR_0": COL, "JOINTS_0": J, "WEIGHTS_0": W}, "indices": I}]},
                        {"name": "tri", "primitives": [{"attributes": {"POSITION": P2, "COLOR_0": COL2}}]}],
                nodes=[{"name": "Mesh", "mesh": 0, "skin": 0}, {"name": "Root", "children": [2]}, {"name": "Tip", "translation": [0, 1, 0]}, {"name": "Tri", "mesh": 1}],
                skins=[{"name": "Skin", "joints": [1, 2], "inverseBindMatrices": ibm, "skeleton": 1}],
                scenes=[{"nodes": [0, 1, 3]}], scene=0)
    write_glb(os.path.join(out, "normalized.glb"), root, b)


def fx_interleaved(out):
    """One buffer view holding POSITION, NORMAL and TEXCOORD_0 interleaved (byteStride 32), accessors at byte offsets
    into it; UNSIGNED_INT indices for a small mesh; two primitives whose index accessors share one view at offsets."""
    b = Buf()
    pos, nrm, uv, idx = grid(3, 1)
    raw = bytearray()
    for p, n, t in zip(pos, nrm, uv):
        raw += struct.pack("<3f3f2f", *p, *n, *t)
    v = b.view(bytes(raw), stride=32, target=34962)
    P = b.accessor(pos, "f", "VEC3", view=v, offset=0, count=len(pos))
    N = b.accessor(nrm, "f", "VEC3", view=v, offset=12, count=len(pos), minmax=False)
    UV = b.accessor(uv, "f", "VEC2", view=v, offset=24, count=len(pos), minmax=False)
    half = len(idx) // 2
    iv = b.view(struct.pack("<%dI" % len(idx), *idx), target=34963)
    I1 = b.accessor(idx[:half], "I", "SCALAR", view=iv, offset=0, count=half, minmax=False)
    I2 = b.accessor(idx[half:], "I", "SCALAR", view=iv, offset=half * 4, count=len(idx) - half, minmax=False)
    root = base("interleaved",
                materials=[{"name": "left", "pbrMetallicRoughness": {"baseColorFactor": [1, 0, 0, 1]}}, {"name": "right", "pbrMetallicRoughness": {"baseColorFactor": [0, 0, 1, 1]}}],
                meshes=[{"name": "strip", "primitives": [{"attributes": {"POSITION": P, "NORMAL": N, "TEXCOORD_0": UV}, "indices": I1, "material": 0},
                                                         {"attributes": {"POSITION": P, "NORMAL": N, "TEXCOORD_0": UV}, "indices": I2, "material": 1}]}],
                nodes=[{"name": "Strip", "mesh": 0}], scenes=[{"nodes": [0]}], scene=0)
    write_glb(os.path.join(out, "interleaved.glb"), root, b)


def fx_modes(out):
    """Every primitive mode in one mesh: non-indexed TRIANGLES, TRIANGLE_STRIP, TRIANGLE_FAN, LINES, POINTS; and a
    second mesh of LINES only on its own node (a node that draws nothing: the preview must allocate nothing for it).
    Triangles as drawn: 2 + 2 + 3 = 7; the lines and points draw none (Blender: loose edges and vertices)."""
    b = Buf()
    tri = b.accessor([(0, 0, 0), (1, 0, 0), (0, 1, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0)], "f", "VEC3")
    strip = b.accessor([(2, 0, 0), (2, 1, 0), (3, 0, 0), (3, 1, 0)], "f", "VEC3")
    fan = b.accessor([(5, 0.5, 0), (4, 0, 0), (6, 0, 0), (6, 1, 0), (4, 1, 0), (4, 0, 0)], "f", "VEC3")
    fan_idx = b.accessor([0, 1, 2, 3, 4], "H", "SCALAR")
    lines = b.accessor([(7, 0, 0), (8, 0, 0), (7, 1, 0), (8, 1, 0)], "f", "VEC3")
    points = b.accessor([(9, 0, 0), (9, 1, 0), (9, 2, 0)], "f", "VEC3")
    wire = b.accessor([(10, 0, 0), (11, 0, 0), (11, 1, 0), (10, 1, 0)], "f", "VEC3")
    root = base("modes",
                meshes=[{"name": "modes", "primitives": [{"attributes": {"POSITION": tri}},
                                                         {"attributes": {"POSITION": strip}, "mode": 5},
                                                         {"attributes": {"POSITION": fan}, "indices": fan_idx, "mode": 6},
                                                         {"attributes": {"POSITION": lines}, "mode": 1},
                                                         {"attributes": {"POSITION": points}, "mode": 0}]},
                        {"name": "wire", "primitives": [{"attributes": {"POSITION": wire}, "mode": 2}]}],   # LINE_LOOP
                nodes=[{"name": "Modes", "mesh": 0}, {"name": "Wire", "mesh": 1}], scenes=[{"nodes": [0, 1]}], scene=0)
    write_glb(os.path.join(out, "modes.glb"), root, b)


def fx_external(out):
    """A .gltf with its buffer in a .bin beside it, one image in a .png file beside it and one as a data URI; the
    textured quad references both through two textures of one material (base colour, emissive)."""
    b = Buf()
    pos, nrm, uv, idx = grid(1, 1)
    P = b.accessor(pos, "f", "VEC3"); N = b.accessor(nrm, "f", "VEC3"); UV = b.accessor(uv, "f", "VEC2", minmax=False); I = b.accessor(idx, "H", "SCALAR")
    with open(os.path.join(out, "external_base.png"), "wb") as f:
        f.write(png(4, 4, (200, 40, 40)))
    data_uri = "data:image/png;base64," + base64.b64encode(png(2, 2, (0, 255, 0))).decode("ascii")
    root = base("external",
                images=[{"name": "base", "uri": "external_base.png"}, {"name": "glow", "uri": data_uri}],
                samplers=[{"magFilter": 9728, "minFilter": 9728, "wrapS": 33071, "wrapT": 33648}],
                textures=[{"source": 0, "sampler": 0}, {"source": 1}],
                materials=[{"name": "painted", "pbrMetallicRoughness": {"baseColorTexture": {"index": 0}, "metallicFactor": 0.0}, "emissiveTexture": {"index": 1}, "emissiveFactor": [1, 1, 1]}],
                meshes=[{"name": "quad", "primitives": [{"attributes": {"POSITION": P, "NORMAL": N, "TEXCOORD_0": UV}, "indices": I, "material": 0}]}],
                nodes=[{"name": "Quad", "mesh": 0}], scenes=[{"name": "Scene", "nodes": [0]}], scene=0)
    write_gltf(os.path.join(out, "external.gltf"), root, b, "external.bin")


def fx_cubic(out):
    """CUBICSPLINE translation and rotation (in-tangent, value, out-tangent per key), STEP scale, LINEAR rotation on
    the same node in a second animation, a third animation with no name. The pose both sides state is animation 0 at
    t = 0: translation (0, 2, 0) from the first key's VALUE, not its tangent."""
    b = Buf()
    pos = b.accessor([(0, 0, 0), (1, 0, 0), (0, 1, 0)], "f", "VEC3"); idx = b.accessor([0, 1, 2], "H", "SCALAR")
    t3 = b.accessor([0.0, 0.5, 1.0], "f", "SCALAR")
    tr = b.accessor([(0, 0, 0), (0, 2, 0), (1, 0, 0), (0, 0, 0), (1, 2, 0), (1, 0, 0), (0, 0, 0), (2, 2, 0), (0, 0, 0)], "f", "VEC3", minmax=False)
    s2 = math.sqrt(0.5)
    rot = b.accessor([(0, 0, 0, 0), (0, 0, 0, 1), (0, 0, 0, 0), (0, 0, 0, 0), (0, 0, s2, s2), (0, 0, 0, 0), (0, 0, 0, 0), (0, 0, 1, 0), (0, 0, 0, 0)], "f", "VEC4", minmax=False)
    t2 = b.accessor([0.0, 1.5], "f", "SCALAR")
    sc = b.accessor([(1, 1, 1), (2, 2, 2)], "f", "VEC3", minmax=False)
    rl = b.accessor([(0, 0, 0, 1), (0, s2, 0, s2)], "f", "VEC4", minmax=False)
    root = base("cubic",
                meshes=[{"name": "tri", "primitives": [{"attributes": {"POSITION": pos}, "indices": idx}]}],
                nodes=[{"name": "Mover", "mesh": 0}], scenes=[{"nodes": [0]}], scene=0,
                animations=[{"name": "cubic", "samplers": [{"input": t3, "output": tr, "interpolation": "CUBICSPLINE"}, {"input": t3, "output": rot, "interpolation": "CUBICSPLINE"}],
                             "channels": [{"sampler": 0, "target": {"node": 0, "path": "translation"}}, {"sampler": 1, "target": {"node": 0, "path": "rotation"}}]},
                            {"name": "stepped", "samplers": [{"input": t2, "output": sc, "interpolation": "STEP"}, {"input": t2, "output": rl}],
                             "channels": [{"sampler": 0, "target": {"node": 0, "path": "scale"}}, {"sampler": 1, "target": {"node": 0, "path": "rotation"}}]},
                            {"samplers": [{"input": t2, "output": sc}], "channels": [{"sampler": 0, "target": {"node": 0, "path": "scale"}}]}])
    write_glb(os.path.join(out, "cubic.glb"), root, b)


def fx_scenes(out):
    """Two scenes, the default the SECOND one (both named, the first with extras); a node in no scene at all (orphan,
    with a mesh); a child placed by a matrix with a negative scale (mirrored winding). Both scenes and the orphan
    survive the round trip (review of PR #111: the non-default scene used to be dropped); Blender imports every node
    (the drill counts 4 triangles), a viewer - the Model Reader's preview - draws the default scene's B and D only."""
    b = Buf()
    pos = b.accessor([(0, 0, 0), (1, 0, 0), (0, 1, 0)], "f", "VEC3"); idx = b.accessor([0, 1, 2], "H", "SCALAR")
    root = base("scenes",
                meshes=[{"name": "tri", "primitives": [{"attributes": {"POSITION": pos}, "indices": idx}]}],
                nodes=[{"name": "A", "mesh": 0}, {"name": "B", "mesh": 0, "children": [3]}, {"name": "C", "mesh": 0, "translation": [10, 0, 0]},
                       {"name": "D", "mesh": 0, "matrix": [-1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 2, 0, 1]}],
                scenes=[{"name": "First", "nodes": [0], "extras": {"camera": "front"}}, {"name": "Second", "nodes": [1]}], scene=1)
    write_glb(os.path.join(out, "scenes.glb"), root, b)


def fx_materials(out):
    """Material variety on four primitives of one mesh: MASK with cutoff and double-sided, a base-colour texture on
    TEXCOORD_1 and an occlusion of strength 0.5, a normal map of scale 2; BLEND with emissive texture, factor and
    KHR_materials_emissive_strength and KHR_materials_specular with a texture; KHR_materials_unlit; a material with
    no pbr block and a unicode name. Two textures share one image; one texture has no sampler; the sampler sets all
    four fields. Extras of every JSON type on asset, node, mesh and material. Tangents with both handednesses."""
    b = Buf()
    pos, nrm, uv, idx = grid(2, 1)
    P = b.accessor(pos, "f", "VEC3"); N = b.accessor(nrm, "f", "VEC3")
    UV0 = b.accessor(uv, "f", "VEC2", minmax=False); UV1 = b.accessor([(v, u) for u, v in uv], "f", "VEC2", minmax=False)
    T = b.accessor([(1, 0, 0, 1 if i % 2 == 0 else -1) for i in range(len(pos))], "f", "VEC4", minmax=False)
    C = b.accessor([(0.2, 0.4, 0.6, 1.0)] * len(pos), "f", "VEC4", minmax=False)
    I = b.accessor(idx, "H", "SCALAR")
    img = b.blob(png(4, 4, (90, 90, 200))); img2 = b.blob(png(2, 2, (255, 255, 255)))
    attrs = {"POSITION": P, "NORMAL": N, "TANGENT": T, "TEXCOORD_0": UV0, "TEXCOORD_1": UV1, "COLOR_0": C}
    root = base("materials",
                images=[{"name": "blue", "mimeType": "image/png", "bufferView": img}, {"name": "white", "mimeType": "image/png", "bufferView": img2}],
                samplers=[{"magFilter": 9729, "minFilter": 9987, "wrapS": 10497, "wrapT": 33071, "name": "mip"}],
                textures=[{"name": "blueA", "source": 0, "sampler": 0}, {"name": "blueB", "source": 0}, {"name": "white", "source": 1, "sampler": 0}],
                materials=[
                    {"name": "masked", "alphaMode": "MASK", "alphaCutoff": 0.3, "doubleSided": True,
                     "pbrMetallicRoughness": {"baseColorTexture": {"index": 0, "texCoord": 1}, "baseColorFactor": [0.5, 0.5, 0.5, 1]},
                     "occlusionTexture": {"index": 2, "texCoord": 1, "strength": 0.5}, "normalTexture": {"index": 1, "scale": 2.0}, "extras": {}},
                    {"name": "glowing", "alphaMode": "BLEND", "emissiveTexture": {"index": 2}, "emissiveFactor": [1, 0.5, 0],
                     "pbrMetallicRoughness": {"metallicFactor": 0.0, "roughnessFactor": 0.3},
                     "extensions": {"KHR_materials_emissive_strength": {"emissiveStrength": 4.0}, "KHR_materials_specular": {"specularFactor": 0.5, "specularTexture": {"index": 1}}}},
                    {"name": "flat", "extensions": {"KHR_materials_unlit": {}}, "pbrMetallicRoughness": {"baseColorFactor": [0, 1, 0, 1]}},
                    {"name": "Stahl – ø \"quoted\" \\ back", "extras": [1, "two", None]}],
                extensionsUsed=["KHR_materials_emissive_strength", "KHR_materials_specular", "KHR_materials_unlit"],
                meshes=[{"name": "four", "extras": 7, "primitives": [{"attributes": attrs, "indices": I, "material": i} for i in range(4)]}],
                nodes=[{"name": "Four", "mesh": 0, "extras": "a note"}], scenes=[{"nodes": [0]}], scene=0)
    root["asset"]["extras"] = [{"author": "fixtures.py"}, 2, "three"]
    root["asset"]["copyright"] = "CC0 – nobody"
    write_glb(os.path.join(out, "materials.glb"), root, b)


def fx_skin8(out):
    """Eight influences per vertex (JOINTS_1 / WEIGHTS_1) over eight joints in a chain; the skin names a skeleton and
    carries NO inverse bind matrices (identity); the mesh node sits under a translated parent, which the spec says a
    skinned mesh ignores."""
    b = Buf()
    pos = b.accessor([(0, 0, 0), (1, 0, 0), (0, 1, 0), (1, 1, 0)], "f", "VEC3")
    idx = b.accessor([0, 1, 3, 0, 3, 2], "H", "SCALAR")
    J0 = b.accessor([(0, 1, 2, 3)] * 4, "H", "VEC4", minmax=False); J1 = b.accessor([(4, 5, 6, 7)] * 4, "H", "VEC4", minmax=False)
    W0 = b.accessor([(0.125, 0.125, 0.125, 0.125)] * 4, "f", "VEC4", minmax=False); W1 = b.accessor([(0.125, 0.125, 0.125, 0.125)] * 4, "f", "VEC4", minmax=False)
    joints = [{"name": "j%d" % i, "translation": [0, 0.25, 0], "children": [3 + i]} for i in range(7)] + [{"name": "j7", "translation": [0, 0.25, 0]}]
    joints[0]["translation"] = [0, 0, 0]
    root = base("skin8",
                meshes=[{"name": "quad", "primitives": [{"attributes": {"POSITION": pos, "JOINTS_0": J0, "WEIGHTS_0": W0, "JOINTS_1": J1, "WEIGHTS_1": W1}, "indices": idx}]}],
                nodes=[{"name": "Holder", "translation": [100, 0, 0], "children": [1]}, {"name": "Skinned", "mesh": 0, "skin": 0}] + joints,
                skins=[{"name": "chain", "joints": list(range(2, 10)), "skeleton": 2}],
                scenes=[{"nodes": [0, 2]}], scene=0)
    write_glb(os.path.join(out, "skin8.glb"), root, b)


def fx_big(out):
    """A grid of 70,000+ vertices: UNSIGNED_INT indices in the source, and the writer must keep them wide (65,535 is
    the restart value, unusable as an index)."""
    b = Buf()
    pos, nrm, uv, idx = grid(300, 240, 10.0)
    P = b.accessor(pos, "f", "VEC3"); I = b.accessor(idx, "I", "SCALAR", minmax=False)
    root = base("big", meshes=[{"name": "grid", "primitives": [{"attributes": {"POSITION": P}, "indices": I}]}],
                nodes=[{"name": "Grid", "mesh": 0}], scenes=[{"nodes": [0]}], scene=0)
    write_glb(os.path.join(out, "big.glb"), root, b)


def fx_names(out):
    """Names: empty everywhere a name may be, duplicates, unicode, JSON escapes; a mesh instanced by three nodes in a
    chain with TRS at every level; a node with a mesh and children; a material with an empty name; a material no
    primitive uses."""
    b = Buf()
    pos = b.accessor([(0, 0, 0), (1, 0, 0), (0, 1, 0)], "f", "VEC3")
    root = base("names",
                materials=[{"name": ""}, {"name": "same"}, {"name": "same"}],   # material 1 is used by nothing: Blender creates a material only when a primitive uses it (the drill states that count)
                meshes=[{"primitives": [{"attributes": {"POSITION": pos}, "material": 2}]}, {"name": "", "primitives": [{"attributes": {"POSITION": pos}, "material": 0}]}],
                nodes=[{"name": "Ärmel \"x\" \\ 日本", "mesh": 0, "children": [1], "translation": [1, 0, 0], "rotation": [0, 0.7071068, 0, 0.7071068]},
                       {"name": "", "mesh": 0, "children": [2], "scale": [2, 2, 2]},
                       {"name": "Ärmel \"x\" \\ 日本", "mesh": 1, "translation": [0, 0, 1]}],
                scenes=[{"nodes": [0]}], scene=0)
    write_glb(os.path.join(out, "names.glb"), root, b)


def fx_no_default_scene(out):
    """Two scenes and NO `scene` property: the specification gives that a meaning of its own (a viewer shows nothing at
    load), so the round trip must keep it absent rather than pick scene 0 (review of PR #111, round 4)."""
    b = Buf()
    pos = b.accessor([(0, 0, 0), (1, 0, 0), (0, 1, 0)], "f", "VEC3"); idx = b.accessor([0, 1, 2], "H", "SCALAR")
    root = base("no_default_scene",
                meshes=[{"name": "tri", "primitives": [{"attributes": {"POSITION": pos}, "indices": idx}]}],
                nodes=[{"name": "A", "mesh": 0}, {"name": "B", "mesh": 0, "translation": [2, 0, 0]}],
                scenes=[{"name": "One", "nodes": [0]}, {"name": "Two", "nodes": [1]}])
    write_glb(os.path.join(out, "no_default_scene.glb"), root, b)


def fx_dropped(out):
    """What Blender's import DROPS of a file, all valid glTF and all kept by the reader and the writer: a mesh no node
    uses (with a material and an image only it names), a triangle that repeats a vertex, and a second triangle over
    the same three vertices (a face made double-sided by duplication - the Workshop writes such). A Workshop-fused Lab
    source carries all three at scale (2,084 unused meshes of 2,107; 6,003 dropped triangles); the drill states
    Blender's view beside the file's."""
    b = Buf()
    pos, nrm, uv, idx = grid(2, 1)
    P = b.accessor(pos, "f", "VEC3"); UV = b.accessor(uv, "f", "VEC2", minmax=False)
    # 4 real triangles, then one degenerate (0, 0, 1) and the first triangle again with its winding reversed
    I = b.accessor(idx + [0, 0, 1] + [idx[0], idx[2], idx[1]], "H", "SCALAR")
    P2 = b.accessor([(9, 0, 0), (10, 0, 0), (9, 1, 0)], "f", "VEC3")
    used_img = b.blob(png(2, 2, (10, 200, 10))); spare_img = b.blob(png(2, 2, (200, 10, 10)))
    root = base("dropped",
                images=[{"name": "used", "mimeType": "image/png", "bufferView": used_img}, {"name": "spare", "mimeType": "image/png", "bufferView": spare_img}],
                textures=[{"source": 0}, {"source": 1}],
                materials=[{"name": "kept", "pbrMetallicRoughness": {"baseColorTexture": {"index": 0}}}, {"name": "orphaned", "pbrMetallicRoughness": {"baseColorTexture": {"index": 1}}}],
                meshes=[{"name": "strip", "primitives": [{"attributes": {"POSITION": P, "TEXCOORD_0": UV}, "indices": I, "material": 0}]},
                        {"name": "left_behind", "primitives": [{"attributes": {"POSITION": P2}, "material": 1}]}],
                nodes=[{"name": "Strip", "mesh": 0}], scenes=[{"nodes": [0]}], scene=0)
    write_glb(os.path.join(out, "dropped.glb"), root, b)


def fx_mixed_skin(out):
    """A skinned node whose mesh MIXES a skinned primitive and an unskinned one (no file of the registry, the recipes or the
    Khronos samples has this shape - 0 of 275 skinned nodes, counted 2026-10-03): Blender assigns the unskinned triangle
    all-zero joint data, so both primitives ride joint 0. The reader, probe and live rig share this measured rule."""
    b = Buf()
    tri = b.accessor([(0, 0, 0), (1, 0, 0), (0, 1, 0)], "f", "VEC3")
    tri2 = b.accessor([(0, 0, 2), (1, 0, 2), (0, 1, 2)], "f", "VEC3")
    J0 = b.accessor([(0, 0, 0, 0)] * 3, "H", "VEC4", minmax=False); W0 = b.accessor([(1, 0, 0, 0)] * 3, "f", "VEC4", minmax=False)
    root = base("mixed_skin",
                meshes=[{"name": "mixed", "primitives": [{"attributes": {"POSITION": tri, "JOINTS_0": J0, "WEIGHTS_0": W0}}, {"attributes": {"POSITION": tri2}}]}],
                nodes=[{"name": "Holder", "translation": [5, 0, 0], "children": [1, 2]}, {"name": "Mixed", "mesh": 0, "skin": 0, "rotation": [0, 0.3826834, 0, 0.9238795]},
                       {"name": "Joint", "translation": [0, 3, 0]}],
                skins=[{"joints": [2], "skeleton": 2}], scenes=[{"nodes": [0]}], scene=0)
    write_glb(os.path.join(out, "mixed_skin.glb"), root, b)


def fx_clip_switch(out):
    """Consecutive clips animate different nodes and different properties of the same node. Sampling a clip must reset
    properties that only another clip animates, including translation and scale left at the previous clip's middle frame."""
    b = Buf()
    pos = b.accessor([(0, 0, 0), (1, 0, 0), (0, 1, 0)], "f", "VEC3")
    times = b.accessor([0, 1], "f", "SCALAR")
    move = b.accessor([(0, 0, 0), (4, 0, 0)], "f", "VEC3")
    scale = b.accessor([(1, 1, 1), (2, 2, 2)], "f", "VEC3")
    turn = b.accessor([(0, 0, 0, 1), (0, 0, 0.70710677, 0.70710677)], "f", "VEC4", minmax=False)
    other = b.accessor([(0, 0, 0), (0, 3, 0)], "f", "VEC3")
    root = base("clip_switch", meshes=[{"primitives": [{"attributes": {"POSITION": pos}}]}],
                nodes=[{"name": "Mover", "mesh": 0}, {"name": "Other", "mesh": 0}], scenes=[{"nodes": [0, 1]}], scene=0,
                animations=[{"name": "Move", "samplers": [{"input": times, "output": move}, {"input": times, "output": scale}],
                             "channels": [{"sampler": 0, "target": {"node": 0, "path": "translation"}}, {"sampler": 1, "target": {"node": 1, "path": "scale"}}]},
                            {"name": "Turn", "samplers": [{"input": times, "output": turn}], "channels": [{"sampler": 0, "target": {"node": 0, "path": "rotation"}}]},
                            {"name": "Other", "samplers": [{"input": times, "output": other}], "channels": [{"sampler": 0, "target": {"node": 1, "path": "translation"}}]}])
    write_glb(os.path.join(out, "clip_switch.glb"), root, b)


def fx_path_clip(out):
    """A legal glTF node name containing Unity's animation path separator: '/' is a name character, not a parent."""
    b = Buf()
    pos = b.accessor([(0, 0, 0), (1, 0, 0), (0, 1, 0)], "f", "VEC3")
    times = b.accessor([0, 1], "f", "SCALAR")
    values = b.accessor([(0, 0, 0), (4, 0, 0)], "f", "VEC3")
    root = base("path_clip", meshes=[{"primitives": [{"attributes": {"POSITION": pos}}]}],
                nodes=[{"name": "Part/Port", "mesh": 0}], scenes=[{"nodes": [0]}], scene=0,
                animations=[{"name": "Move", "samplers": [{"input": times, "output": values}], "channels": [{"sampler": 0, "target": {"node": 0, "path": "translation"}}]}])
    write_glb(os.path.join(out, "path_clip.glb"), root, b)


def fx_far_clip(out):
    """A large translation at frame zero has acceptable float32 rounding; the middle frame at the origin is exact.
    The rig gate must judge each pose with its own deviation, rather than reuse the first pose's worst error."""
    b = Buf()
    # Keep the triangle in the YZ plane: the rounding along X is a uniform offset, preserving its area in Blender.
    pos = b.accessor([(0.012345, 0, 0), (0.012345, 1, 0), (0.012345, 0, 1)], "f", "VEC3")
    times = b.accessor([0, 0.5, 1], "f", "SCALAR")
    values = b.accessor([(100000, 0, 0), (0, 0, 0), (0, 0, 0)], "f", "VEC3")
    root = base("far_clip", meshes=[{"primitives": [{"attributes": {"POSITION": pos}}]}],
                nodes=[{"name": "Mover", "mesh": 0}], scenes=[{"nodes": [0]}], scene=0,
                animations=[{"name": "Return", "samplers": [{"input": times, "output": values}], "channels": [{"sampler": 0, "target": {"node": 0, "path": "translation"}}]}])
    write_glb(os.path.join(out, "far_clip.glb"), root, b)


def fx_unicode_clip(out):
    """A clip name truncated to Blender's 63-byte limit must preserve whole Unicode code points, even before a suffix."""
    b = Buf()
    pos = b.accessor([(0, 0, 0), (1, 0, 0), (0, 1, 0)], "f", "VEC3")
    times = b.accessor([0, 1], "f", "SCALAR")
    values = b.accessor([(0, 0, 0), (1, 0, 0)], "f", "VEC3")
    anim = {"name": "\U0001F680" * 40, "samplers": [{"input": times, "output": values}], "channels": [{"sampler": 0, "target": {"node": 0, "path": "translation"}}]}
    root = base("unicode_clip", meshes=[{"primitives": [{"attributes": {"POSITION": pos}}]}],
                nodes=[{"name": "Mover", "mesh": 0}], scenes=[{"nodes": [0]}], scene=0, animations=[anim, anim])
    write_glb(os.path.join(out, "unicode_clip.glb"), root, b)


def bent_grid(nx, ny, ox, oy):
    """A curved grid (so the Decimate quadrics see real costs), its outward normals, three UV sets, indices."""
    pos, nrm, uv0, uv1, uv2 = [], [], [], [], []
    for j in range(ny + 1):
        for i in range(nx + 1):
            x, y = ox + i / nx, oy + j / ny
            z = 0.3 * math.sin(2.0 * x) * math.cos(1.5 * y)
            dzx, dzy = 0.6 * math.cos(2.0 * x) * math.cos(1.5 * y), -0.45 * math.sin(2.0 * x) * math.sin(1.5 * y)
            ln = math.sqrt(dzx * dzx + dzy * dzy + 1.0)
            pos.append((x, z, -y)); nrm.append((-dzx / ln, 1.0 / ln, dzy / ln))   # glTF Y up: the grid lies in XZ
            uv0.append((i / nx, j / ny)); uv1.append((0.5 * i / nx, 0.25 + 0.5 * j / ny)); uv2.append((1.0 - j / ny, i / nx))
    idx = []
    for j in range(ny):
        for i in range(nx):
            a = j * (nx + 1) + i; b = a + 1; c = a + nx + 1; d = c + 1
            idx += [a, d, b, a, c, d]
    return pos, nrm, uv0, uv1, uv2, idx


def fx_decimate_attrs(out):
    """The attribute combinations the Decimate port (step 5 c) must lay out as Blender's importer does, which no registry,
    recipe or Khronos file has (counted 2026-10-03): in ONE mesh, a coloured and a plain primitive on the SAME material (two
    Blender materials, blender_material[vertex_color]), a coloured primitive with no material (the invented "default +
    vertex colour" slot) and a plain one with none (the empty slot); a primitive without NORMAL beside ones with (the
    importer's zeros, replaced by the vertex normal before encoding); TEXCOORD_2 on one primitive only (zeros elsewhere);
    COLOR_1 as unsigned short on one primitive (white elsewhere); colours as float VEC4, unsigned byte VEC3 and unsigned
    short. A second mesh carries COLOR_0 on a LINES primitive too, which puts its colour layer on the POINT domain; a
    third has no normals at all (flat faces, no custom normals), which the drill's Mono pass can collapse."""
    b = Buf()
    prims = []
    specs = [  # (offset, material, colour, normals, texcoords)
        (0.0, 0, "float4", True, 3),
        (1.2, 0, None, True, 1),
        (2.4, None, "ubyte3", False, 2),
        (3.6, None, None, True, 1),
    ]
    for k, (ox, mat, col, normals, nuv) in enumerate(specs):
        pos, nrm, uv0, uv1, uv2, idx = bent_grid(5, 5, ox, 0.0)
        attrs = {"POSITION": b.accessor(pos, "f", "VEC3")}
        if normals:
            attrs["NORMAL"] = b.accessor(nrm, "f", "VEC3", minmax=False)
        for t, uv in enumerate([uv0, uv1, uv2][:nuv]):
            attrs["TEXCOORD_%d" % t] = b.accessor(uv, "f", "VEC2", minmax=False)
        n = len(pos)
        if col == "float4":
            attrs["COLOR_0"] = b.accessor([(0.1 + 0.8 * (i % 6) / 5.0, 0.5, 0.9 - 0.8 * (i // 6) / 5.0, 1.0) for i in range(n)], "f", "VEC4", minmax=False)
        elif col == "ubyte3":
            attrs["COLOR_0"] = b.accessor([(20 + 7 * (i % 6), 200, 3 * (i // 6)) for i in range(n)], "B", "VEC3", normalized=True, minmax=False)
            attrs["COLOR_1"] = b.accessor([(65535, 1000 * (i % 6), 30000, 65535) for i in range(n)], "H", "VEC4", normalized=True, minmax=False)
        p = {"attributes": attrs, "indices": b.accessor(idx, "H", "SCALAR")}
        if mat is not None:
            p["material"] = mat
        prims.append(p)
    # the second mesh: a coloured surface and a coloured line strip -> COLOR_0 on the POINT domain
    pos, nrm, uv0, uv1, uv2, idx = bent_grid(4, 4, 0.0, 2.0)
    c0 = b.accessor([(0.25 * (i % 5), 0.75, 0.25 * (i // 5), 1.0) for i in range(len(pos))], "f", "VEC4", minmax=False)
    line = b.accessor([(0, 0, -3.5), (1, 0.2, -3.5), (2, 0, -3.5)], "f", "VEC3")
    lcol = b.accessor([(1.0, 0.0, 0.0, 1.0), (0.0, 1.0, 0.0, 1.0), (0.0, 0.0, 1.0, 1.0)], "f", "VEC4", minmax=False)
    point_mesh = {"name": "point_colours", "primitives": [
        {"attributes": {"POSITION": b.accessor(pos, "f", "VEC3"), "NORMAL": b.accessor(nrm, "f", "VEC3", minmax=False), "TEXCOORD_0": b.accessor(uv0, "f", "VEC2", minmax=False), "COLOR_0": c0},
         "indices": b.accessor(idx, "H", "SCALAR"), "material": 1},
        {"attributes": {"POSITION": line, "COLOR_0": lcol}, "mode": 3, "material": 1}]}
    # the third mesh: no normals at all, UVs only - every face flat-shaded, no custom normals, so the collapse needs no
    # cosine and the drill's 32-bit Mono pass can run it (the float32 discipline of the quadrics, the costs and the UV blend)
    pos, nrm, uv0, uv1, uv2, idx = bent_grid(7, 7, 0.0, 5.0)
    bare_mesh = {"name": "bare", "primitives": [{"attributes": {"POSITION": b.accessor(pos, "f", "VEC3"), "TEXCOORD_0": b.accessor(uv0, "f", "VEC2", minmax=False)},
                                                 "indices": b.accessor(idx, "H", "SCALAR"), "material": 0}]}
    root = base("decimate_attrs",
                materials=[{"name": "painted", "pbrMetallicRoughness": {"baseColorFactor": [0.8, 0.8, 0.8, 1]}}, {"name": "lined", "pbrMetallicRoughness": {"baseColorFactor": [0.3, 0.6, 0.9, 1]}}],
                meshes=[{"name": "attrs", "primitives": prims}, point_mesh, bare_mesh],
                nodes=[{"name": "Attrs", "mesh": 0}, {"name": "PointColours", "mesh": 1}, {"name": "Bare", "mesh": 2}], scenes=[{"nodes": [0, 1, 2]}], scene=0)
    write_glb(os.path.join(out, "decimate_attrs.glb"), root, b)


def fx_decimate_limits(out):
    """Review of PR #120: Blender imports only eight UV/colour layers, and a large flat grid exposes
    the thread-dependent edge order that the reducer's runtime default must reproduce."""
    b = Buf()
    meshes = []
    for kind in ("uv9", "color9", "thread_grid"):
        n = 25 if kind == "thread_grid" else 5
        pos, _, uv0, _, _, idx = bent_grid(n, n, 0.0, 0.0)
        if kind == "thread_grid":
            pos = [(x, 0.0, z) for x, _, z in pos]
        attrs = {"POSITION": b.accessor(pos, "f", "VEC3")}
        if kind == "uv9":
            for i in range(9):
                attrs["TEXCOORD_%d" % i] = b.accessor([(u + i / 10.0, v) for u, v in uv0], "f", "VEC2", minmax=False)
        if kind == "color9":
            for i in range(9):
                attrs["COLOR_%d" % i] = b.accessor([(0.2, 0.4, i / 10.0, 1.0)] * len(pos), "f", "VEC4", minmax=False)
        meshes.append({"name": kind, "primitives": [{"attributes": attrs, "indices": b.accessor(idx, "H", "SCALAR")}]})
    root = base("decimate_limits", meshes=meshes,
                nodes=[{"name": mesh["name"], "mesh": i} for i, mesh in enumerate(meshes)],
                scenes=[{"nodes": [0, 1, 2]}], scene=0)
    write_glb(os.path.join(out, "decimate_limits.glb"), root, b)


def fx_export_layout(out):
    """The rules of Blender's glTF EXPORTER (step 5 d, tools/prep_drill.sh) that no other fixture and no registry file
    exercised (counted 2026-10-05 by the drill's COVER rows): a mesh whose FIRST material slot in use is the empty one while
    another primitive is coloured (COLOR_0 then carries alpha - and these alphas vary); a mesh whose faces are all on a plain
    material while a LINES primitive brings a colour layer (the exporter forces a COLOR_0 of 255s ahead of it); a triangle of
    no area on a mesh without normals (Blender gives such a face the normal +Z) and one on a mesh with them, standing along
    Blender's Z: at its middle vertex the fan's normal (+Z again) runs along the fan's own edges, the custom-normal space is
    invalid, the corner normal decodes to ZERO and the exporter makes it "up" - which that triangle cannot tell from "the
    fan's normal is kept", +Z as well; so a fan of three faces follows whose angle-weighted normal runs along its own first
    edge, +X: Blender's `corner_normals` there is (0, 0, 0) (read 2026-10-05), exported as up. And small closed solids - a tetrahedron, a
    bipyramid, an octahedron, a box, each with normals and without: at a third of the faces the collapse closes them onto
    themselves, leaving two faces on the same three vertices, and the exporter's `mesh.validate()` removes the later one
    (found on five fused ships by the full run, 2026-10-05: their bolts and rivets are such solids)."""
    b = Buf()

    def surface(ox, oy, normals=True, colour=None, material=None, extra=None):
        pos, nrm, uv0, _, _, idx = bent_grid(4, 4, ox, oy)
        if extra:   # three more vertices on one line: a triangle of no area
            n0 = len(pos); pos += extra; nrm += [(0.0, 1.0, 0.0)] * 3; uv0 += [(0.0, 0.0), (0.5, 0.0), (1.0, 0.0)]; idx += [n0, n0 + 1, n0 + 2]
        attrs = {"POSITION": b.accessor(pos, "f", "VEC3"), "TEXCOORD_0": b.accessor(uv0, "f", "VEC2", minmax=False)}
        if normals:
            attrs["NORMAL"] = b.accessor(nrm, "f", "VEC3", minmax=False)
        if colour:
            attrs["COLOR_0"] = b.accessor([colour(i) for i in range(len(pos))], "f", "VEC4", minmax=False)
        p = {"attributes": attrs, "indices": b.accessor(idx, "H", "SCALAR")}
        if material is not None:
            p["material"] = material
        return p

    alpha_first = {"name": "alpha_first", "primitives": [
        surface(0.0, 0.0),
        surface(1.2, 0.0, colour=lambda i: (0.1 + 0.2 * (i % 5), 0.6, 0.9 - 0.2 * (i // 5), 0.15 + 0.2 * (i % 5)), material=0)]}
    line = b.accessor([(0, 0, -2.5), (1, 0.2, -2.5), (2, 0, -2.5)], "f", "VEC3")
    lcol = b.accessor([(1.0, 0.0, 0.0, 0.25), (0.0, 1.0, 0.0, 0.5), (0.0, 0.0, 1.0, 1.0)], "f", "VEC4", minmax=False)
    forced = {"name": "forced", "primitives": [surface(0.0, 2.0, material=0), {"attributes": {"POSITION": line, "COLOR_0": lcol}, "mode": 3, "material": 1}]}
    flat = {"name": "flat_degenerate", "primitives": [surface(0.0, 4.0, normals=False, material=0, extra=[(3.0, 0.0, -4.0), (4.0, 0.0, -4.0), (5.0, 0.0, -4.0)])]}
    smooth = {"name": "smooth_degenerate", "primitives": [surface(0.0, 6.0, material=0, extra=[(3.0, 0.0, -6.0), (3.0, 1.0, -6.0), (3.0, 2.0, -6.0)])]}
    # the invalid fan, in Blender's frame (written (x, z, -y)): a sliver along +X, a quarter in the XZ plane (both with
    # the normal -Y), then 120 degrees in a plane whose normal is (0.661, 0.75, 0): 90 x -Y + 120 x 0.75 Y leaves +X
    fan_bl = [(0.0, 0.0, 0.0), (1.0, 0.0, -0.004), (1.0, 0.0, 0.0), (0.0, 0.0, 1.0), (0.6495, -0.5727, -0.5)]
    fan_gl = [(x + 8.0, z, -y) for x, y, z in fan_bl]
    invalid_fan = {"name": "invalid_fan", "primitives": [{
        "attributes": {"POSITION": b.accessor(fan_gl, "f", "VEC3"), "NORMAL": b.accessor([(0.0, 1.0, 0.0)] * 5, "f", "VEC3", minmax=False)},
        "indices": b.accessor([0, 1, 2, 0, 2, 3, 0, 3, 4], "H", "SCALAR"), "material": 0}]}
    meshes = [alpha_first, forced, flat, smooth, invalid_fan]
    names = ["AlphaFirst", "Forced", "FlatDegenerate", "SmoothDegenerate", "InvalidFan"]
    # a coloured primitive on a material with alpha (materials 2..5): the importer wires the vertex colour's alpha into a
    # BLEND material and into a MASK material whose cutoff is in (0, 1], not into MASK at a cutoff of 0 or over 1 - and
    # only a wired alpha makes the exporter write COLOR_0 with alpha (2026-10-06, the 96 such object runs of the population)
    colour = lambda i: (0.1 + 0.2 * (i % 5), 0.6, 0.9 - 0.2 * (i // 5), 0.15 + 0.2 * (i % 5))
    for k, label in enumerate(["Blend", "MaskHalf", "MaskZero", "MaskOver", "MaskOne", "MaskJustOverOne", "MaskTiny"]):
        meshes.append({"name": "coloured_" + label.lower(), "primitives": [surface(8.0 + 1.2 * k, 2.0, colour=colour, material=2 + k)]})
        names.append("Coloured" + label)
    solids = [
        ("Tetra", [(0, 0, 0), (1, 0, 0), (0.4, 0.9, 0), (0.45, 0.3, 0.8)], [(0, 2, 1), (0, 1, 3), (1, 2, 3), (2, 0, 3)]),
        ("Bipyramid", [(0, 0, 0), (1, 0, 0), (0.4, 0.9, 0), (0.45, 0.3, 0.8), (0.5, 0.35, -0.7)], [(0, 1, 3), (1, 2, 3), (2, 0, 3), (1, 0, 4), (2, 1, 4), (0, 2, 4)]),
        ("Octa", [(1, 0, 0), (-1, 0, 0.1), (0, 1.1, 0), (0, -0.9, 0), (0.1, 0, 1.2), (0, 0.1, -1)], [(0, 2, 4), (2, 1, 4), (1, 3, 4), (3, 0, 4), (2, 0, 5), (1, 2, 5), (3, 1, 5), (0, 3, 5)]),
        ("Box", [(0, 0, 0), (1, 0, 0), (1, 0.6, 0), (0, 0.6, 0), (0, 0, 0.3), (1, 0, 0.3), (1, 0.6, 0.3), (0, 0.6, 0.3)],
         [(0, 2, 1), (0, 3, 2), (4, 5, 6), (4, 6, 7), (0, 1, 5), (0, 5, 4), (1, 2, 6), (1, 6, 5), (2, 3, 7), (2, 7, 6), (3, 0, 4), (3, 4, 7)]),
    ]
    for with_normals in (True, False):
        for i, (name, verts, faces) in enumerate(solids):
            verts = [(x + 2.0 * i, y + 3.0, z - (9.0 if with_normals else 12.0)) for x, y, z in verts]
            centre = [sum(v[k] for v in verts) / len(verts) for k in range(3)]
            attrs = {"POSITION": b.accessor(verts, "f", "VEC3")}
            if with_normals:   # outward from the centre
                nrm = []
                for v in verts:
                    d = [v[k] - centre[k] for k in range(3)]; ln = math.sqrt(sum(x * x for x in d))
                    nrm.append(tuple(x / ln for x in d))
                attrs["NORMAL"] = b.accessor(nrm, "f", "VEC3", minmax=False)
            meshes.append({"name": name.lower() + ("" if with_normals else "_flat"), "primitives": [{"attributes": attrs, "indices": b.accessor([k for f in faces for k in f], "H", "SCALAR"), "material": 0}]})
            names.append(name + ("" if with_normals else "Flat"))
            if not with_normals and name in ("Tetra", "Octa"):
                # Reduction leaves twins; export validation must retain this BLEND slot's alpha metadata.
                coloured = dict(attrs)
                coloured["COLOR_0"] = b.accessor([colour(i) for i in range(len(verts))], "f", "VEC4", minmax=False)
                meshes.append({"name": name.lower() + "_alpha", "primitives": [{"attributes": coloured, "indices": b.accessor([k for f in faces for k in f], "H", "SCALAR"), "material": 2}]})
                names.append(name + "Alpha")
    root = base("export_layout",
                materials=[{"name": "plain", "pbrMetallicRoughness": {"baseColorFactor": [0.8, 0.8, 0.8, 1]}}, {"name": "lined", "pbrMetallicRoughness": {"baseColorFactor": [0.3, 0.6, 0.9, 1]}},
                           {"name": "glass", "alphaMode": "BLEND", "pbrMetallicRoughness": {"baseColorFactor": [0.8, 0.9, 1, 0.5]}},
                           {"name": "leaves", "alphaMode": "MASK", "alphaCutoff": 0.5, "pbrMetallicRoughness": {"baseColorFactor": [0.2, 0.7, 0.2, 1]}},
                           {"name": "mask_zero", "alphaMode": "MASK", "alphaCutoff": 0.0, "pbrMetallicRoughness": {"baseColorFactor": [0.7, 0.7, 0.2, 1]}},
                           {"name": "mask_over", "alphaMode": "MASK", "alphaCutoff": 1.5, "pbrMetallicRoughness": {"baseColorFactor": [0.7, 0.2, 0.7, 1]}},
                           {"name": "mask_one", "alphaMode": "MASK", "alphaCutoff": 1.0, "pbrMetallicRoughness": {"baseColorFactor": [0.2, 0.7, 0.7, 1]}},
                           {"name": "mask_just_over_one", "alphaMode": "MASK", "alphaCutoff": 1.00000001},
                           {"name": "mask_tiny", "alphaMode": "MASK", "alphaCutoff": 1e-50}],
                meshes=meshes,
                nodes=[{"name": name, "mesh": i} for i, name in enumerate(names)],
                scenes=[{"nodes": list(range(len(names)))}], scene=0)
    write_glb(os.path.join(out, "export_layout.glb"), root, b)


def export_skin(out, name, bad_weights):
    """The rules of the glTF exporter's SKINNED layout (step 5 d) no other fixture reaches (the drill's COVER rows,
    2026-10-06): the armature is the turned, unevenly scaled node above the joints, so positions and normals really go
    through its matrix; vertex 1 has two EQUAL weights listed joint 1 first (a stable sort keeps that order); vertex 2 a
    weight of float32 0.0001 (not over the exporter's 0.0001: dropped) beside a full one; vertex 3 one of 0.00011 (kept).
    With bad_weights, vertex 0 carries one weight of 0.00005 and nothing else: under the threshold, so it is left with no
    bone and the exporter adds its "neutral bone" to the skin shared with a fully weighted second mesh.
    That breaks the contract every other file keeps (a skinned vertex's weights sum to about 1), which the reader drill checks - hence a file of its own, marked in its NAME."""
    b = Buf()
    pos, nrm, uv0, _, _, idx = bent_grid(4, 4, 0.0, 0.0)
    J, Wt = [], []
    for i in range(len(pos)):
        t = (i % 5) / 4.0
        J.append((0, 1, 0, 0)); Wt.append((1.0 - t, t, 0.0, 0.0))
    if bad_weights:
        J[0], Wt[0] = (0, 0, 0, 0), (0.00005, 0.0, 0.0, 0.0)
    J[1], Wt[1] = (1, 0, 0, 0), (0.5, 0.5, 0.0, 0.0)
    J[2], Wt[2] = (0, 1, 0, 0), (0.0001, 1.0, 0.0, 0.0)
    J[3], Wt[3] = (0, 1, 0, 0), (0.00011, 0.99989, 0.0, 0.0)
    attrs = {"POSITION": b.accessor(pos, "f", "VEC3"), "NORMAL": b.accessor(nrm, "f", "VEC3", minmax=False), "TEXCOORD_0": b.accessor(uv0, "f", "VEC2", minmax=False),
             "JOINTS_0": b.accessor(J, "H", "VEC4", minmax=False), "WEIGHTS_0": b.accessor(Wt, "f", "VEC4", minmax=False)}
    root = base(name,
                meshes=[{"name": "body", "primitives": [{"attributes": attrs, "indices": b.accessor(idx, "H", "SCALAR")}]}],
                nodes=[{"name": "Rig", "translation": [2.0, 0.5, -1.0], "rotation": [0.2886751, 0.2886751, 0.2886751, 0.8660254], "scale": [1.5, 0.75, 1.25], "children": [1, 2]},
                       {"name": "Body", "mesh": 0, "skin": 0},
                       {"name": "Root", "translation": [0.0, 0.1, 0.0], "children": [3]},
                       {"name": "Tip", "translation": [1.0, 0.2, 0.0]}],
                skins=[{"name": "rig", "joints": [2, 3]}],
                scenes=[{"nodes": [0]}], scene=0)
    if bad_weights:
        # GoodBody itself needs no neutral joint, but Blender appends one to their shared armature/skin.
        # Put it first in object order to also catch a checker that only accumulates requirements as it compares.
        good_attrs = dict(attrs)
        good_attrs["WEIGHTS_0"] = b.accessor([(1.0, 0.0, 0.0, 0.0)] + Wt[1:], "f", "VEC4", minmax=False)
        root["meshes"].append({"name": "good_body", "primitives": [{"attributes": good_attrs, "indices": root["meshes"][0]["primitives"][0]["indices"]}]})
        root["nodes"].append({"name": "GoodBody", "mesh": 1, "skin": 0})
        root["nodes"][0]["children"].insert(0, 4)
    write_glb(os.path.join(out, name + ".glb"), root, b)


def fx_export_skin(out):
    export_skin(out, "export_skin", False)


def fx_export_skin_badweights(out):
    export_skin(out, "export_skin_badweights", True)


FIXTURES = [fx_two_targets, fx_normalized, fx_interleaved, fx_modes, fx_external, fx_cubic, fx_scenes, fx_materials, fx_skin8, fx_big, fx_names, fx_no_default_scene, fx_dropped, fx_mixed_skin, fx_clip_switch, fx_path_clip, fx_far_clip, fx_unicode_clip, fx_decimate_attrs, fx_decimate_limits, fx_export_layout, fx_export_skin, fx_export_skin_badweights]


def main(out):
    os.makedirs(out, exist_ok=True)
    for fx in FIXTURES:
        fx(out)
    for name in sorted(os.listdir(out)):
        if name.endswith((".glb", ".gltf")):
            print(os.path.join(out, name).replace("\\", "/"))


if __name__ == "__main__":
    main(sys.argv[1])
