"""Fixtures for the vehicle_rig drill (tools/vehiclerig_drill.sh): the branches of the script no saved recipe takes, each a
small glTF under <out>/models and a Lab recipe under <out>/recipes - the drill's jobs writer (VehicleRigJobs.cs) turns the
recipes into jobs exactly as it turns the saved ones, so a fixture runs the script on the Lab's own argument shape.

    python vehiclerig_fixtures.py <out dir>          -> writes the models and recipes; prints one line per recipe: FIXTURE <key>
    python vehiclerig_fixtures.py --exits <jobs>     -> the jobs of <out>/recipes_exit post-edited into what the Lab's builder never
                                                        writes (a malformed tagged token, a dial Python cannot parse), their keys
                                                        EXIT:1:<key> / DIES:1:<key>; printed as JOB lines for the drill's jobs file

Stage 1 (step (b)):
  fx_b_ignore        two boxes under a turned root; one Ignored (deleted), one Body
  fx_b_placement     a part placed (offset and scale: the PLACED line) and a placement naming a part the file has not (the WARN)
  fx_b_orient        the model orientation (30, 10, -20): the straighten line before rigging
  fx_b_shared        two nodes instancing ONE mesh, one mirrored (scale -1): a shared datablock copied at apply, its faces flipped
  fx_b_merge         a second model (turned 30 degrees, scaled 2 x 1 x 1, an empty helper, an animated node and an icosphere
                     placeholder by signature) merged in: the MERGE lines, the B_ names, the purge, B's action in the scene
  fx_b_skinned       a skinned two-bone rig with a clip: the bone-shape purge at import, the vertex groups cleared and the
                     armature dropped at the flatten, the action kept
  fx_b_single        ONE mesh object of three loose boxes: the split into loose parts (the walk's vertex, edge and face order)
  fx_b_bright        a brightness factor on flat, directly textured and tinted materials (the line's counts)
  fx_b_merge2        static tracks (the STATIC line AFTER the merge lines) beside a second model of two nodes instancing ONE mesh,
                     one mirrored (the merge's own datablock copy and flip_normals), brightened on its own (the second-model line)
  fx_b_orient also names an Ignore part the file has not (0 deleted); fx_b_placement also places a part with a zero scale (the WARN, scale 1)
Stage 2 (step (c), the weld tiers):
  fx_c_weld          a box of 24 per-face vertices (a glTF's seams) over two materials, BODY at 50: the weld within each material
                     (the material border stays split), every box edge sharp (90 degrees), the target reached without a decimate
  fx_c_grid          a bumpy 9 x 9 grid with its middle row of vertices doubled (one seam), BODY at 60: the weld, then the decimate
                     collapse to the dial, the new mesh datablock named after the old
  fx_c_tiers         four tiers in one scene: BODY at 50 and STRUCTURE at 30 on two grids, a STRUCTURE name the scene has not (the
                     WARN), a DETAIL dial of exactly 0.5 (the tier skipped), a RUDDER dial of 120 (clamped: "dial 95%")
Exits and deaths (recipes_exit, keys renamed by --exits): fx_x_allignored (every part Ignored: the _guard crash), fx_x_bright (bright=1.5|x),
  fx_x_flagfold (flagfold=a|b), fx_x_defaultreduce (no '|'), fx_x_shroudreduce (a percent float() refuses), fx_x_gunreduce (likewise),
  fx_x_windowreduce (no '|'), fx_x_merge_missing (a second model path that is not there); fx_d_frames (argv[7] "15.0": int() dies),
  fx_d_merge_offset (merge2 offset "a,0,0": float() dies), fx_d_names (a wheels file that is not there: namelist() dies)
"""
import json, math, os, sys
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "riganim-drill"))
from riganim_fixtures import Rig, rigged   # noqa: E402


def recipe(out, key, src, parts, sub="recipes", **fields):
    r = {"srcFile": src, "outGlb": os.path.join(out, "models", key + "_Spin.glb").replace("\\", "/"), "frames": 15, "axisChoice": 0, "minVerts": 1, "degrees": -360.0,
         "parts": [dict({"name": n, "verts": 8, "role": role}, **extra) for n, role, extra in parts]}
    r.update(fields)
    os.makedirs(os.path.join(out, sub), exist_ok=True)
    with open(os.path.join(out, sub, key + ".json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(r, f, indent=1)
    print("FIXTURE\t%s" % key)


def icosahedron(r, name):
    """A 12-vertex icosahedron: the importer's bone-shape signature (12/42/162/642 vertices, near-cubic extents)."""
    t = (1.0 + math.sqrt(5.0)) / 2.0
    raw = [(-1, t, 0), (1, t, 0), (-1, -t, 0), (1, -t, 0), (0, -1, t), (0, 1, t), (0, -1, -t), (0, 1, -t), (t, 0, -1), (t, 0, 1), (-t, 0, -1), (-t, 0, 1)]
    pos = [(x / 2.0, y / 2.0, z / 2.0) for x, y, z in raw]
    idx = [0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11, 1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8, 3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9, 4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1]
    prim = {"attributes": {"POSITION": r.b.accessor(pos, "f", "VEC3")}, "indices": r.b.accessor(idx, "H", "SCALAR", minmax=False)}
    r.meshes.append({"name": name, "primitives": [prim]})
    return len(r.meshes) - 1


def box_split(r, name, materials):
    """A unit box of 24 vertices - four per face, as a glTF with per-face UVs carries it - with the first three faces on
    materials[0] and the last three on materials[1] (two primitives)."""
    faces = [((0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0)), ((0, 0, 1), (0, 1, 1), (1, 1, 1), (1, 0, 1)), ((0, 0, 0), (0, 0, 1), (1, 0, 1), (1, 0, 0)),
             ((0, 1, 0), (1, 1, 0), (1, 1, 1), (0, 1, 1)), ((0, 0, 0), (0, 1, 0), (0, 1, 1), (0, 0, 1)), ((1, 0, 0), (1, 0, 1), (1, 1, 1), (1, 1, 0))]
    prims = []
    for half in (0, 1):
        pos = []; uv = []; idx = []
        for fi in range(3 * half, 3 * half + 3):
            base = len(pos)
            pos += [tuple(float(c) for c in v) for v in faces[fi]]
            uv += [(0.0, 0.0), (1.0, 0.0), (1.0, 1.0), (0.0, 1.0)]
            idx += [base, base + 1, base + 2, base, base + 2, base + 3]
        prims.append({"attributes": {"POSITION": r.b.accessor(pos, "f", "VEC3"), "TEXCOORD_0": r.b.accessor(uv, "f", "VEC2", minmax=False)},
                      "indices": r.b.accessor(idx, "H", "SCALAR", minmax=False), "material": materials[half]})
    r.meshes.append({"name": name, "primitives": prims})
    return len(r.meshes) - 1


def grid_seam(r, name, material, n=9):
    """An n x n heightfield (z = a bump) whose middle row of vertices is carried twice - the faces above the row use one
    copy, the faces below the other - a seam the weld closes; one material, per-vertex UVs."""
    import math as _m
    pos = []; uv = []
    def vert(i, j):
        x, y = i / (n - 1.0), j / (n - 1.0)
        return (x, y, 0.25 * _m.sin(3.0 * x) * _m.cos(2.0 * y))
    rows = []
    mid = n // 2
    for j in range(n):
        row = []
        for i in range(n):
            row.append(len(pos)); pos.append(vert(i, j)); uv.append((i / (n - 1.0), j / (n - 1.0)))
        rows.append(row)
    dup = []
    for i in range(n):
        dup.append(len(pos)); pos.append(vert(i, mid)); uv.append((i / (n - 1.0), mid / (n - 1.0)))
    idx = []
    for j in range(n - 1):
        for i in range(n - 1):
            a = rows[j][i]; b = rows[j][i + 1]; c = rows[j + 1][i + 1]; d = rows[j + 1][i]
            if j + 1 == mid: c = dup[i + 1]; d = dup[i]   # the faces below the seam row use the copies
            idx += [a, b, c, a, c, d]
    prim = {"attributes": {"POSITION": r.b.accessor(pos, "f", "VEC3"), "TEXCOORD_0": r.b.accessor(uv, "f", "VEC2", minmax=False)},
            "indices": r.b.accessor(idx, "H", "SCALAR", minmax=False), "material": material}
    r.meshes.append({"name": name, "primitives": [prim]})
    return len(r.meshes) - 1


def boxes_in_one(r, name, material, count):
    """One primitive of `count` separate boxes (loose parts of one mesh)."""
    pos = []; idx = []
    box = [0, 1, 3, 0, 3, 2, 4, 6, 7, 4, 7, 5, 0, 4, 5, 0, 5, 1, 2, 3, 7, 2, 7, 6, 0, 2, 6, 0, 6, 4, 1, 5, 7, 1, 7, 3]
    for k in range(count):
        base = len(pos)
        pos += [(3.0 * k + dx, dy, dz) for dx in (0.0, 1.0) for dy in (0.0, 1.0) for dz in (0.0, 1.0)]
        idx += [base + i for i in box]
    prim = {"attributes": {"POSITION": r.b.accessor(pos, "f", "VEC3"), "TEXCOORD_0": r.b.accessor([(0.25, 0.5)] * len(pos), "f", "VEC2", minmax=False)},
            "indices": r.b.accessor(idx, "H", "SCALAR", minmax=False), "material": material}
    r.meshes.append({"name": name, "primitives": [prim]})
    return len(r.meshes) - 1


def main(out):
    models = os.path.join(out, "models"); os.makedirs(models, exist_ok=True)
    # two boxes under a turned root
    r = Rig(); m = r.material("Paint", (0.3, 0.5, 0.7)); root = r.node("Root", rotation=[0.0, 0.7071068, 0.0, 0.7071068])
    r.node("A", root, mesh=r.mesh("A", material=m)); r.node("B", root, mesh=r.mesh("B", at=(3.0, 0.0, 0.0), material=m), translation=[0.0, 2.0, 0.0])
    src = r.write(models, "fx_b_two_boxes")
    recipe(out, "fx_b_ignore", src, [("A", 0, {}), ("B", 3, {})])
    recipe(out, "fx_b_placement", src, [("A", 0, {"offset": {"x": 0.5, "y": -1.0, "z": 0.25}, "scale": {"x": 2.0, "y": 1.0, "z": 0.5}}), ("B", 0, {"scale": {"x": 0.0, "y": 1.0, "z": 1.0}}), ("Ghost", 0, {"offset": {"x": 1.0, "y": 0.0, "z": 0.0}})])
    recipe(out, "fx_b_orient", src, [("A", 0, {}), ("B", 0, {}), ("Ghost", 3, {})], modelRot={"x": 30.0, "y": 10.0, "z": -20.0})
    # two nodes instancing one mesh, one mirrored
    r = Rig(); m = r.material("Paint", (0.3, 0.5, 0.7)); shared = r.mesh("Shared", material=m)
    r.node("A", mesh=shared); r.node("B", mesh=shared, translation=[4.0, 0.0, 0.0], scale=[-1.0, 1.0, 1.0])
    shared_src = r.write(models, "fx_b_shared")
    recipe(out, "fx_b_shared", shared_src, [("A", 0, {}), ("B", 0, {})])
    # a second model
    r2 = Rig(); m2 = r2.material("Steel", (0.5, 0.5, 0.55)); root2 = r2.node("Root2", rotation=[0.0, 0.258819, 0.0, 0.9659258])
    r2.node("Gun", root2, mesh=r2.mesh("Gun", material=m2)); r2.node("Mount", root2, mesh=r2.mesh("Mount", at=(0.0, 2.0, 0.0), material=m2), translation=[1.0, 0.0, 0.0])
    r2.node("Helper", root2); r2.node("Icosphere", root2, mesh=icosahedron(r2, "Icosphere"))
    a2 = r2.anim("Swing"); r2.key(a2, 1, "rotation", [0.0, 0.5, 1.0], [(0.0, 0.0, 0.0, 1.0), (0.0, 0.258819, 0.0, 0.9659258), (0.0, 0.0, 0.0, 1.0)])
    second = r2.write(models, "fx_b_second")
    recipe(out, "fx_b_merge", src, [("A", 0, {}), ("B", 0, {})], srcFile2=second, model2Off={"x": 0.5, "y": 0.0, "z": 1.0}, model2Rot={"x": 0.0, "y": 0.0, "z": 30.0}, model2ScaleXYZ={"x": 2.0, "y": 1.0, "z": 1.0})
    # static tracks beside a second model that is mirrored, instanced and brightened on its own
    recipe(out, "fx_b_merge2", src, [("A", 6, {}), ("B", 0, {})], tracksStatic=True, srcFile2=shared_src, model2Off={"x": 0.0, "y": 3.0, "z": 0.0}, model2Bright=1.3)
    # the exits and deaths: plain recipes the --exits mode edits into the malformed shapes
    recipe(out, "fx_x_allignored", src, [("A", 3, {}), ("B", 3, {})], sub="recipes_exit")
    recipe(out, "fx_x_bright", src, [("A", 0, {}), ("B", 0, {})], sub="recipes_exit", model1Bright=1.5)
    for k in ("fx_x_flagfold", "fx_x_defaultreduce", "fx_x_shroudreduce", "fx_x_gunreduce", "fx_x_windowreduce", "fx_d_frames", "fx_d_names", "fx_d_infinite_count", "fx_d_nan_count", "fx_d_flagfold_infinite"):
        recipe(out, k, src, [("A", 0, {}), ("B", 0, {})], sub="recipes_exit")
    for k in ("fx_x_merge_missing", "fx_d_merge_offset"):
        recipe(out, k, src, [("A", 0, {}), ("B", 0, {})], sub="recipes_exit", srcFile2=second)
    # a skinned rig with a clip
    r = Rig(); rigged(r)
    recipe(out, "fx_b_skinned", r.write(models, "fx_b_skinned"), [("Body", 0, {})])
    # one mesh object of three loose boxes
    r = Rig(); m = r.material("Paint", (0.3, 0.5, 0.7)); r.node("Hull", mesh=boxes_in_one(r, "Hull", m, 3))
    recipe(out, "fx_b_single", r.write(models, "fx_b_single"), [("Hull", 0, {}), ("Hull.001", 1, {}), ("Hull.002", 0, {})])
    # Review counterexamples: names released by a helper purge must be available to the loose split.
    r = Rig(); m = r.material("Paint"); r.node("Hull", mesh=boxes_in_one(r, "Hull", m, 3)); r.node("Hull.001")
    recipe(out, "fx_b_reused_helper", r.write(models, "fx_b_reused_helper"), [("Hull", 0, {}), ("Hull.001", 0, {}), ("Hull.002", 0, {})])
    # The V2 marker is a rig_anim rule, absent from vehicle_rig's import.
    r = Rig(); rigged(r, arm_name="DeployArmV2")
    recipe(out, "fx_b_contract", r.write(models, "fx_b_contract"), [("Body", 0, {})])
    # Two files sharing action names still create distinct Blender action datablocks.
    r = Rig(); m = r.material("Paint"); n = r.node("First", mesh=r.mesh("First", material=m)); r.node("Other", mesh=r.mesh("Other", at=(3, 0, 0), material=m))
    anim = r.anim("Swing"); r.key(anim, n, "translation", [0, 1], [(0, 0, 0), (1, 0, 0)])
    recipe(out, "fx_b_merge_actions", r.write(models, "fx_b_merge_actions"), [("First", 0, {}), ("Other", 0, {})], srcFile2=second)
    # A deleted importer bone shape releases its object name before the second import.
    r = Rig(); rigged(r); primary_skin = r.write(models, "fx_b_first_skin")
    r = Rig(); rigged(r); second_skin = r.write(models, "fx_b_second_skin")
    recipe(out, "fx_b_merge_skinned", primary_skin, [("Body", 0, {})], srcFile2=second_skin)
    # stage 2: the weld tiers
    r = Rig(); ma = r.material("Paint", (0.3, 0.5, 0.7)); mb = r.material("Steel", (0.5, 0.5, 0.55))
    r.node("Hull", mesh=box_split(r, "Hull", (ma, mb))); r.node("Other", mesh=r.mesh("Other", at=(3.0, 0.0, 0.0), material=ma))
    recipe(out, "fx_c_weld", r.write(models, "fx_c_weld"), [("Hull", 0, {}), ("Other", 0, {})], bodyReducePct=50.0)
    r = Rig(); ma = r.material("Paint", (0.3, 0.5, 0.7))
    r.node("Deck", mesh=grid_seam(r, "Deck", ma)); r.node("Other", mesh=r.mesh("Other", at=(3.0, 0.0, 0.0), material=ma))
    recipe(out, "fx_c_grid", r.write(models, "fx_c_grid"), [("Deck", 0, {}), ("Other", 16, {})], bodyReducePct=60.0, structureReducePct=0.0)
    r = Rig(); ma = r.material("Paint", (0.3, 0.5, 0.7))
    r.node("Deck", mesh=grid_seam(r, "Deck", ma, 11)); r.node("Fin", mesh=grid_seam(r, "Fin", ma, 7), translation=[0.0, 0.0, 2.0])
    r.node("Keel", mesh=grid_seam(r, "Keel", ma, 5), translation=[0.0, 0.0, -2.0]); r.node("Helm", mesh=grid_seam(r, "Helm", ma, 6), translation=[2.0, 0.0, 0.0])
    # a part holds ONE role, so a name reaches one tier; four tiers share the scene instead
    recipe(out, "fx_c_tiers", r.write(models, "fx_c_tiers"), [("Deck", 0, {}), ("Fin", 16, {}), ("Ghost", 16, {}), ("Keel", 21, {}), ("Helm", 18, {})],
           bodyReducePct=50.0, structureReducePct=30.0, detailReducePct=0.5, rudderReducePct=120.0)
    # brightness on three material chains
    r = Rig(); img = r.image("Albedo", rgb=(120, 80, 40)); tex = r.texture(img)
    flat = r.material("Flat", (0.6, 0.4, 0.2)); direct = r.material("Direct", (1.0, 1.0, 1.0), texture=tex); tint = r.material("Tint", (0.5, 0.25, 0.9), texture=tex)
    r.node("A", mesh=r.mesh("A", material=flat)); r.node("B", mesh=r.mesh("B", at=(3.0, 0.0, 0.0), material=direct)); r.node("C", mesh=r.mesh("C", at=(6.0, 0.0, 0.0), material=tint))
    recipe(out, "fx_b_bright", r.write(models, "fx_b_bright"), [("A", 0, {}), ("B", 0, {}), ("C", 0, {})], model1Bright=1.5)


def set_tag(argv, tag, value):
    """The one <tag>=... token (the script takes the FIRST), replaced or appended."""
    for i, t in enumerate(argv):
        if t.startswith(tag + "="):
            argv[i] = tag + "=" + value; return
    argv.append(tag + "=" + value)


EXITS = {
    "fx_x_allignored": ("EXIT", lambda a: None),
    "fx_x_bright": ("EXIT", lambda a: set_tag(a, "bright", "1.5|x")),
    "fx_x_flagfold": ("EXIT", lambda a: set_tag(a, "flagfold", "a|b")),
    "fx_x_defaultreduce": ("EXIT", lambda a: set_tag(a, "defaultreduce", "nopipe")),
    "fx_x_shroudreduce": ("EXIT", lambda a: set_tag(a, "shroudreduce", "|abc")),
    "fx_x_gunreduce": ("EXIT", lambda a: set_tag(a, "gunreduce", "abc")),
    "fx_x_windowreduce": ("EXIT", lambda a: set_tag(a, "windowreduce", "nopipe")),
    "fx_x_merge_missing": ("EXIT", lambda a: set_tag(a, "merge2", "|".join([a[[t.startswith("merge2=") for t in a].index(True)][7:].split("|")[0] + ".missing.glb"] + a[[t.startswith("merge2=") for t in a].index(True)][7:].split("|")[1:]))),
    "fx_d_frames": ("DIES", lambda a: a.__setitem__(7, "15.0")),
    "fx_d_infinite_count": ("DIES", lambda a: a.__setitem__(12, "inf")),
    "fx_d_nan_count": ("DIES", lambda a: a.__setitem__(22, "nan")),
    "fx_d_flagfold_infinite": ("DIES", lambda a: set_tag(a, "flagfold", "10|inf")),
    "fx_d_merge_offset": ("DIES", lambda a: set_tag(a, "merge2", "|".join([a[[t.startswith("merge2=") for t in a].index(True)][7:].split("|")[0], "a,0,0"] + a[[t.startswith("merge2=") for t in a].index(True)][7:].split("|")[2:]))),
    "fx_d_names": ("DIES", lambda a: a.__setitem__(4, a[4] + ".missing.txt")),
}


def exits(jobs_path):
    n = 0
    for line in open(jobs_path, encoding="utf-8").read().split("\n"):
        if not line.startswith("JOB\t"):
            continue
        f = line.rstrip("\r").split("\t"); key = f[1]; argv = f[2:]
        if key not in EXITS:
            raise SystemExit("an exit recipe %s has no edit in EXITS" % key)
        kind, edit = EXITS[key]; edit(argv); n += 1
        print("\t".join(["JOB", "%s:1:%s" % (kind, key)] + argv))
    if n != len(EXITS):
        raise SystemExit("expected %d exit jobs, got %d" % (len(EXITS), n))


if __name__ == "__main__":
    if sys.argv[1] == "--exits":
        exits(sys.argv[2])
    else:
        main(sys.argv[1])
