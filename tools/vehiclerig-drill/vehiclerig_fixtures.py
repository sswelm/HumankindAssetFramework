"""Fixtures for the vehicle_rig drill (tools/vehiclerig_drill.sh): the branches of the script no saved recipe takes, each a
small glTF under <out>/models and a Lab recipe under <out>/recipes - the drill's jobs writer (VehicleRigJobs.cs) turns the
recipes into jobs exactly as it turns the saved ones, so a fixture runs the script on the Lab's own argument shape.

    python vehiclerig_fixtures.py <out dir>          -> writes the models and recipes; prints one line per recipe: FIXTURE <key>
    python vehiclerig_fixtures.py --exits <jobs>     -> the jobs of <out>/recipes_exit post-edited into what the Lab's builder never
                                                        writes (a malformed tagged token, a dial Python cannot parse), their keys
                                                        EXIT:1:<key> / DIES:1:<key> for stops, unchanged for valid argument variants

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
                     to 16 (the material border stays split), every box edge sharp (90 degrees), then the decimate at ratio 0.5 on
                     it; the 8-vertex box beside it sits at its target and keeps its datablock
  fx_c_soup_flat     a flat 6 x 6 grid of triangles in the RIGGING tier at 90: the limited dissolve joins every coplanar face into
                     one n-gon and collapses the straight-run vertices, the triangulate cuts the square, the decimate follows
  fx_c_soup_L        a flat L-shaped strip in the SHROUD tier at 50: the dissolve leaves a CONCAVE n-gon, so the ear clipping's
                     kd-tree of concave corners decides the ears; a lone flat quad beside it takes the quad rule
  fx_c_soup_attributes custom normals, vertex colours and two UV layers through the soup operators
  fx_d_chunk_splice  an earlier BSP cell deletes an edge queued for a later cell: the script dies in stage 2
  fx_c_soup_bumpy    a 60 x 40 bumpy grid (4,800 faces) in the RIGGING tier at 80: over the 4,000-face cap, the dissolve runs
                     per BSP cell on the cell's interior edges and vertices
  fx_c_scaled        the seamed grid on a node scaled (2, 1, 0.5) and turned: the weld distance from o.dimensions after
                     transform_apply (the extents of the baked mesh at an identity matrix), BODY at 60
  fx_c_grid          a bumpy 9 x 9 grid with its middle row of vertices doubled (one seam), BODY at 60: the weld, then the decimate
                     collapse to the dial, the new mesh datablock named after the old
  fx_c_tiers         four tiers in one scene: BODY at 50 and STRUCTURE at 30 on two grids, a STRUCTURE name the scene has not (the
                     WARN), a DETAIL dial of exactly 0.5 (the tier skipped), a RUDDER dial of 120 (clamped: "dial 95%")
  fx_r_turret_reduce the TURRET tier (2026-10-16): a seamed 11x11 grid marked Turret at 60 % beside a hull - weld, then collapse
  fx_r_turret_tiers  Wheel -> Turret -> Gun order, turret dial clamped at 95, material borders kept split during the weld
  fx_r_turret_threshold exactly 0.5 skips the turret tier entirely, including the weld
Stage 3 (step (d), part d1: the frame every rig shares):
  fx_r_wheels        two tires thin along Y, an off-centre bolt joining the first (the 0.75 rule), a cube wheel (tied extents: axle X),
                     the tire listed twice; the wave rock (RootHull); a rudder also listed as preserve, a missing name in each set (the
                     WARNs); a body part named Mesh_Wheel_00 (the wheel group renamed Mesh_Wheel_00.001); an animated part joined
  fx_r_axis          the axle forced to Z; the wave on with a one-frame cycle (no RootHull)
  fx_r_many          101 clusters (the last bone Wheel_100); a 0.1-wide wheel on the 0.05 length floor; a hundred tied sizes
  fx_r_missing_wheel a wheel name the scene has not: find()'s VEHICLE ERROR and exit in stage 3 (EXIT:3:)
Stage 3 (step (d), part d2: the sail, rigging on the sail, the flag):
  fx_r_sail          an upright canvas grid under the sail fold (all four bands; the quarter-height rise and drop), a sail part also a
                     wheel, a jib also a rudder, a burgee sail and flag, an animated sail part, rigging on the sail, a halyard rigging
                     and flag, missing sail and rigging names, a struck flag on the 0.3 floor, a wheel
  fx_r_flagfold      the flag hinged at its top (flagfold=-30|8), a missing flag name, rigging with no sail (stays on the body)
  fx_r_flat_sail     a zero-height sail taking the 1e-6 fold-height clamp, all rigging missing, flagfold=| taking its defaults
Argument variants (recipes_exit, post-edited by --exits): fx_r_numeric_pct (the decimal above 0.5 whose rounding decides whether
  to weld); fx_d_sailfold_overflow (1e309 becomes infinity before int() raises); fx_d_unicode_fold (a Kawi digit accepted before
  the next dial raises ValueError)
  fx_x_turret_quote / fx_x_turret_space hold Python's quote choice and non-ASCII whitespace escapes in malformed-tag errors.
Exits and deaths (recipes_exit, keys renamed by --exits): fx_x_allignored (every part Ignored: the _guard crash), fx_x_bright (bright=1.5|x),
  fx_x_flagfold (flagfold=a|b), fx_x_defaultreduce (no '|'), fx_x_shroudreduce (a percent float() refuses), fx_x_gunreduce (likewise), fx_x_turretreduce (an infinite percent),
  fx_x_windowreduce (no '|'), fx_x_merge_missing (a second model path that is not there); fx_d_frames (argv[7] "15.0": int() dies),
  fx_d_merge_offset (merge2 offset "a,0,0": float() dies), fx_d_names (a wheels file that is not there: namelist() dies)
"""
import json, math, os, sys
sys.stdout.reconfigure(encoding="utf-8")
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


def flat_grid(r, name, material, nx, ny, bump=0.0, mask=None):
    """An (nx x ny)-vertex grid of two triangles per cell, z = bump * sin/cos (flat at 0); `mask(i, j)` keeps a cell or not."""
    import math as _m
    pos = []; uv = []; rows = []
    for j in range(ny):
        row = []
        for i in range(nx):
            x, y = i / (nx - 1.0), j / (ny - 1.0)
            row.append(len(pos)); pos.append((x, y, bump * _m.sin(5.0 * x) * _m.cos(4.0 * y))); uv.append((x, y))
        rows.append(row)
    idx = []
    for j in range(ny - 1):
        for i in range(nx - 1):
            if mask is not None and not mask(i, j):
                continue
            a = rows[j][i]; b = rows[j][i + 1]; c = rows[j + 1][i + 1]; d = rows[j + 1][i]
            idx += [a, b, c, a, c, d]
    prim = {"attributes": {"POSITION": r.b.accessor(pos, "f", "VEC3"), "TEXCOORD_0": r.b.accessor(uv, "f", "VEC2", minmax=False)},
            "indices": r.b.accessor(idx, "H" if len(pos) < 65536 else "I", "SCALAR", minmax=False), "material": material}
    r.meshes.append({"name": name, "primitives": [prim]})
    return len(r.meshes) - 1


def chunk_splice(r, material):
    """4,002 triangles: a right cell's collapse removes an edge queued for the left cell (PR #177 regression)."""
    pos = [(0, 0, 0), (1.5, 0, 0), (3, 0, 0), (1.5, 2, 0), (-10, 1, 0), (-10, -1, 0)]
    idx = [0, 1, 3, 1, 2, 3, 0, 2, 4, 0, 5, 2]
    for side in (-1, 1):
        for i in range(1999):
            x = side * (10 + i * .002); base = len(pos)
            pos += [(x, 10, 0), (x + .001, 10, 0), (x, 10.001, 0)]
            idx += [base, base + 1, base + 2]
    r.meshes.append({"name": "Shroud", "primitives": [{"attributes": {"POSITION": r.b.accessor(pos, "f", "VEC3")},
        "indices": r.b.accessor(idx, "H", "SCALAR", minmax=False), "material": material}]})
    return len(r.meshes) - 1


def box_with_double(r, name, material):
    """A unit box about the origin whose +x,-y,-z corner sits 5e-6 out along x, its twin at the true corner on an extra triangle: the WHEEL tier's
    weld (1e-5 of the part's size) merges the twin back, so the mesh's max x drops while its last evaluated bounds keep it."""
    pos = [(-0.5 + dx, -0.5 + dy, -0.5 + dz) for dx in (0.0, 1.0) for dy in (0.0, 1.0) for dz in (0.0, 1.0)]
    pos[4] = (0.500005, -0.5, -0.5)   # the corner 5e-6 out; the weld keeps the twin's position (index 8, measured) and the extreme goes
    pos.append((0.5, -0.5, -0.5))
    idx = [0, 1, 3, 0, 3, 2, 4, 6, 7, 4, 7, 5, 0, 4, 5, 0, 5, 1, 2, 3, 7, 2, 7, 6, 0, 2, 6, 0, 6, 4, 1, 5, 7, 1, 7, 3, 8, 6, 5]
    prim = {"attributes": {"POSITION": r.b.accessor(pos, "f", "VEC3")}, "indices": r.b.accessor(idx, "H", "SCALAR", minmax=False), "material": material}
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
    for k in ("fx_x_flagfold", "fx_x_defaultreduce", "fx_x_shroudreduce", "fx_x_gunreduce", "fx_x_turretreduce", "fx_x_turret_quote", "fx_x_turret_space", "fx_x_windowreduce", "fx_d_frames", "fx_d_names", "fx_d_infinite_count", "fx_d_nan_count", "fx_d_flagfold_infinite", "fx_d_reduce_before_names"):
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
    # A loose split with imported custom normals: the copied parts decode then re-encode through BMesh's spaces.
    r = Rig(); ma = r.material("Paint")
    pos = []; normals = []; idx = []
    for k in range(4):
        start = len(pos)
        pos += [(3.0 * k + 0.123, 0.019, 0.0), (3.0 * k + 1.124, 0.021, 0.007),
                (3.0 * k + 1.122, 1.017, 0.014), (3.0 * k + 0.127, 1.016, 0.011)]
        normals += [(0.2, -0.4, 0.8944272)] * 4
        idx += [start, start + 1, start + 2, start, start + 2, start + 3]
    r.meshes.append({"name": "Panels", "primitives": [{"attributes": {
        "POSITION": r.b.accessor(pos, "f", "VEC3"), "NORMAL": r.b.accessor(normals, "f", "VEC3", minmax=False)},
        "indices": r.b.accessor(idx, "H", "SCALAR", minmax=False), "material": ma}]})
    r.node("Panels", mesh=0)
    recipe(out, "fx_b_split_normals", r.write(models, "fx_b_split_normals"), [("Panels", 0, {})])
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
    r = Rig(); ma = r.material("Paint", (0.3, 0.5, 0.7))
    r.node("Deck", mesh=grid_seam(r, "Deck", ma), scale=[2.0, 1.0, 0.5], rotation=[0.0, 0.258819, 0.0, 0.9659258])
    recipe(out, "fx_c_scaled", r.write(models, "fx_c_scaled"), [("Deck", 0, {})], bodyReducePct=60.0)
    # the soup tiers: the limited dissolve, the n-gon triangulation, the chunked path
    r = Rig(); ma = r.material("Rope", (0.4, 0.3, 0.2))
    r.node("Net", mesh=flat_grid(r, "Net", ma, 6, 6)); r.node("Other", mesh=r.mesh("Other", at=(3.0, 0.0, 0.0), material=ma))
    recipe(out, "fx_c_soup_flat", r.write(models, "fx_c_soup_flat"), [("Net", 15, {}), ("Other", 0, {})], riggingReducePct=90.0)
    r = Rig(); ma = r.material("Rope", (0.4, 0.3, 0.2))
    r.node("Stay", mesh=flat_grid(r, "Stay", ma, 7, 7, mask=lambda i, j: i < 2 or j < 2)); r.node("Pane", mesh=flat_grid(r, "Pane", ma, 2, 2), translation=[2.0, 0.0, 0.0])
    recipe(out, "fx_c_soup_L", r.write(models, "fx_c_soup_L"), [("Stay", 22, {}), ("Pane", 22, {})], shroudReducePct=50.0)
    r = Rig(); ma = r.material("Rope", (0.4, 0.3, 0.2))
    r.node("Shroud", mesh=flat_grid(r, "Shroud", ma, 61, 41, bump=0.02))
    recipe(out, "fx_c_soup_bumpy", r.write(models, "fx_c_soup_bumpy"), [("Shroud", 15, {})], riggingReducePct=80.0)
    # A collapse in the first BSP cell splices away an edge queued for a later one. The script dies in stage 2.
    r = Rig(); ma = r.material("Rope")
    r.node("Shroud", mesh=chunk_splice(r, ma)); r.node("Other", mesh=r.mesh("Other", at=(30, 0, 0), material=ma))
    recipe(out, "fx_d_chunk_splice", r.write(models, "fx_d_chunk_splice"), [("Shroud", 15, {}), ("Other", 0, {})], sub="recipes_exit", riggingReducePct=80.0)
    # Corner data through the soup's joins, vertex collapse and triangulation: custom normals, colours and two UV layers.
    r = Rig(); ma = r.material("Rope")
    mesh = flat_grid(r, "ColouredNet", ma, 6, 6)
    attrs = r.meshes[mesh]["primitives"][0]["attributes"]
    attrs["NORMAL"] = r.b.accessor([(0.2, -0.4, 0.8944272)] * 36, "f", "VEC3", minmax=False)
    attrs["COLOR_0"] = r.b.accessor([(i / 35.0, .5, .25, 1.0) for i in range(36)], "f", "VEC4", minmax=False)
    attrs["TEXCOORD_1"] = r.b.accessor([(i / 7.0, 1.0 - i / 35.0) for i in range(36)], "f", "VEC2", minmax=False)
    r.node("ColouredNet", mesh=mesh); r.node("Other", mesh=r.mesh("Other", at=(3, 0, 0), material=ma))
    recipe(out, "fx_c_soup_attributes", r.write(models, "fx_c_soup_attributes"), [("ColouredNet", 15, {}), ("Other", 0, {})], riggingReducePct=90.0)
    # ---- stage 3 (step (d), part d1: the frame every rig shares)
    # fx_r_wheels: two tires thin along Y with an off-centre bolt joining the first (the 0.75 rule), a cube wheel (its extents tie:
    # the axle is the first, X), the tire listed twice; the wave rock on (RootHull carries the body); a rudder that is also listed as
    # preserve (the rudder key wins), a preserve and a flip part, a missing name in each of the three lists (the WARNs); a body part
    # named Mesh_Wheel_00 still in the scene when the first wheel group is renamed (Mesh_Wheel_00.001), joined away later; an
    # animated body part joined into the hull (its world at the current frame)
    r = Rig(); ma = r.material("Paint", (0.5, 0.5, 0.5))
    r.node("TireL", mesh=r.mesh("TireL", at=(-0.5, -0.5, -0.5), material=ma), translation=[0.0, 2.0, 0.0], scale=[1.0, 0.3, 1.0])
    r.node("BoltL", mesh=r.mesh("BoltL", at=(0.2, -0.05, 0.2), size=0.1, material=ma), translation=[0.0, 2.0, 0.0])
    r.node("TireR", mesh=r.mesh("TireR", at=(-0.5, -0.5, -0.5), material=ma), translation=[0.0, -2.0, 0.0], scale=[1.0, 0.3, 1.0])
    r.node("BoltR", mesh=r.mesh("BoltR", at=(-0.3, -0.05, -0.3), size=0.1, material=ma), translation=[0.0, -2.0, 0.0])
    r.node("Disc", mesh=r.mesh("Disc", at=(-0.5, -0.5, -0.5), material=ma), translation=[8.0, 0.0, 0.0], scale=[0.8, 0.8, 0.1])
    r.node("RudWheel", mesh=r.mesh("RudWheel", at=(-0.2, -0.2, -0.2), size=0.4, material=ma), translation=[11.0, 0.0, 0.0])
    r.node("Cube", mesh=r.mesh("Cube", at=(-0.25, -0.25, -0.25), size=0.5, material=ma), translation=[5.0, 0.0, 0.0])
    r.node("Hull", mesh=r.mesh("Hull", at=(-1.0, -1.0, 0.0), size=2.0, material=ma))
    r.node("Mesh_Wheel_00", mesh=r.mesh("Taken", at=(1.5, 0.0, 0.0), size=0.3, material=ma))
    anim_node = r.node("Turn", mesh=r.mesh("Turn", at=(-1.5, 0.0, 0.0), size=0.3, material=ma, colors=True, uv2=True))
    a0 = r.anim("Bob"); r.key(a0, anim_node, "translation", [0.0, 1.0], [(0.0, 0.0, 0.5), (0.0, 0.0, 1.5)])
    r.node("Rud", mesh=r.mesh("Rud", at=(-3.0, 0.0, 0.0), size=0.4, material=ma))
    r.node("Keep", mesh=r.mesh("Keep", at=(-3.0, 1.0, 0.0), size=0.4, material=ma))
    r.node("Flp", mesh=r.mesh("Flp", at=(-3.0, -1.0, 0.0), size=0.4, material=ma))
    recipe(out, "fx_r_wheels", r.write(models, "fx_r_wheels"),
           [("TireL", 1, {}), ("BoltL", 1, {}), ("TireR", 1, {}), ("BoltR", 1, {}), ("TireL", 1, {}), ("Cube", 1, {}), ("Disc", 1, {}), ("RudWheel", 1, {}),
            ("RudWheel", 18, {}), ("Hull", 0, {}), ("Mesh_Wheel_00", 0, {}),
            ("Turn", 0, {}), ("Rud", 18, {}), ("Rud", 19, {}), ("Keep", 19, {}), ("Flp", 20, {}), ("GhostRudder", 18, {}), ("GhostKeep", 19, {}),
            ("GhostFlip", 20, {})],
           waveEnabled=True, rockDegrees=5.0, rockFrames=15, rockPitchDeg=2.4, preserveReducePct=30.0)
    # fx_r_axis: the axle forced to Z on a tire thin along Y; the wave on with a one-frame cycle (rock_frames 1 < 2: no RootHull)
    r = Rig(); ma = r.material("Paint", (0.5, 0.5, 0.5))
    r.node("Tire", mesh=r.mesh("Tire", at=(-0.5, -0.5, -0.5), material=ma), translation=[0.0, 2.0, 0.0], scale=[1.0, 0.3, 1.0])
    r.node("Hull", mesh=r.mesh("Hull", at=(-1.0, -1.0, 0.0), size=2.0, material=ma))
    r.node("Keep", mesh=r.mesh("Keep", at=(4.0, 0.0, 0.0), material=ma))
    recipe(out, "fx_r_axis", r.write(models, "fx_r_axis"), [("Tire", 1, {}), ("Hull", 0, {}), ("Keep", 19, {})], axisChoice=3, waveEnabled=True, rockDegrees=5.0, rockFrames=1)
    # fx_r_many: 101 wheels far apart - 101 clusters, the last bone Wheel_100 (the %02d field widens); the first only 0.1 wide (its bone
    # takes the 0.05 floor; sorted last, it is Wheel_100); the other 100 tie on size (the stable sort keeps their list order)
    r = Rig(); ma = r.material("Paint", (0.5, 0.5, 0.5))
    for i in range(101):
        r.node("W%03d" % i, mesh=r.mesh("W%03d" % i, at=(3.0 * i, 0.0, 0.0), size=0.1 if i == 0 else 0.5, material=ma))
    r.node("Hull", mesh=r.mesh("Hull", at=(0.0, 3.0, 0.0), size=2.0, material=ma))
    recipe(out, "fx_r_many", r.write(models, "fx_r_many"), [("W%03d" % i, 1, {}) for i in range(101)] + [("Hull", 0, {})])
    # fx_r_missing_wheel: a wheel name the scene has not - find()'s VEHICLE ERROR and exit in stage 3
    r = Rig(); ma = r.material("Paint", (0.5, 0.5, 0.5))
    r.node("Hull\u00a0Gap\u200bX", mesh=r.mesh("Hull\u00a0Gap\u200bX", material=ma)); r.node("Tire", mesh=r.mesh("Tire", at=(3.0, 0.0, 0.0), material=ma))
    r.node("Bob's", mesh=r.mesh("Bobs", at=(6.0, 0.0, 0.0), material=ma))
    recipe(out, "fx_r_missing_wheel", r.write(models, "fx_r_missing_wheel"), [("Hull\u00a0Gap\u200bX", 0, {}), ("Tire", 1, {}), ("Bob's", 0, {}), ("Ghost's wheel", 1, {})], sub="recipes_exit")
    # fx_r_slotless: meshes without material slots joined - a wheel cluster whose active has none (the empty slot first), a hull
    # whose second member has none (the empty slot after Paint), a second cluster of two slot-less parts (the set dropped again)
    r = Rig(); ma = r.material("Paint", (0.5, 0.5, 0.5))
    r.node("WheelBare", mesh=r.mesh("WheelBare", at=(-0.5, -0.5, -0.5)), translation=[0.0, 2.0, 0.0], scale=[1.0, 0.3, 1.0])
    r.node("WheelHub", mesh=r.mesh("WheelHub", at=(-0.2, -0.1, -0.2), size=0.4, material=ma), translation=[0.0, 2.0, 0.0])
    r.node("BareA", mesh=r.mesh("BareA", at=(-0.5, -0.5, -0.5)), translation=[0.0, -2.0, 0.0], scale=[1.0, 0.3, 1.0])
    r.node("BareB", mesh=r.mesh("BareB", at=(-0.2, -0.1, -0.2), size=0.4), translation=[0.0, -2.0, 0.0])
    r.node("Hull", mesh=r.mesh("Hull", at=(-1.0, -1.0, 0.0), size=2.0, material=ma)); r.node("Bare", mesh=r.mesh("Bare", at=(3.0, 0.0, 0.0)))
    recipe(out, "fx_r_slotless", r.write(models, "fx_r_slotless"), [("WheelBare", 1, {}), ("WheelHub", 1, {}), ("BareA", 1, {}), ("BareB", 1, {}), ("Hull", 0, {}), ("Bare", 0, {})])
    # fx_r_stale: the WHEEL tier is the last active one and decimates nothing (8 vertices after the weld), so no evaluation follows
    # its to_mesh: the wheel's bound_box still holds the merged twin, and its bone sits at the stale centre
    r = Rig(); ma = r.material("Paint", (0.5, 0.5, 0.5))
    r.node("Tire", mesh=box_with_double(r, "Tire", ma), translation=[0.0, 2.0, 0.0]); r.node("Hull", mesh=r.mesh("Hull", at=(-1.0, -1.0, 0.0), size=2.0, material=ma))
    recipe(out, "fx_r_stale", r.write(models, "fx_r_stale"), [("Tire", 1, {}), ("Hull", 0, {})], wheelReducePct=50.0)
    # fx_r_turret_reduce (2026-10-16): the TURRET tier - a seamed 11x11 heightfield marked Turret at 60 %: the weld closes the seam,
    # the collapse runs, and the Turret bone is measured from the reduced mesh
    r = Rig(); ma = r.material("Paint", (0.5, 0.5, 0.5))
    r.node("Hull", mesh=r.mesh("Hull", at=(-1.0, -1.0, 0.0), size=2.0, material=ma)); r.node("Cupola", mesh=grid_seam(r, "Cupola", ma, 11), translation=[0.0, 0.0, 2.0])
    recipe(out, "fx_r_turret_reduce", r.write(models, "fx_r_turret_reduce"), [("Hull", 0, {}), ("Cupola", 2, {})], turretReducePct=60.0)
    # The turret tier between Wheel and Gun, clamped at 95; its two materials keep their seam split during the weld.
    r = Rig(); ma = r.material("Paint", (0.3, 0.5, 0.7)); mb = r.material("Trim", (0.8, 0.6, 0.2))
    r.node("Hull", mesh=r.mesh("Hull", at=(0, -3, 0), material=ma))
    r.node("Cupola", mesh=box_split(r, "Cupola", (ma, mb)))
    r.node("Wheel", mesh=box_split(r, "Wheel", (ma, ma)), translation=[3, 0, 0])
    r.node("Barrel", mesh=grid_seam(r, "Barrel", ma, 5), translation=[0, 3, 0])
    recipe(out, "fx_r_turret_tiers", r.write(models, "fx_r_turret_tiers"), [("Hull", 0, {}), ("Cupola", 2, {}), ("Wheel", 1, {}), ("Barrel", 7, {})],
           wheelReducePct=40.0, turretReducePct=120.0, gunReducePct=30.0)
    # Exactly 0.5 skips the whole tier, including the weld: the seamed turret must stay at 24 vertices.
    r = Rig(); ma = r.material("Paint", (0.3, 0.5, 0.7))
    r.node("Hull", mesh=r.mesh("Hull", at=(0, -3, 0), material=ma)); r.node("Cupola", mesh=box_split(r, "Cupola", (ma, ma)))
    recipe(out, "fx_r_turret_threshold", r.write(models, "fx_r_turret_threshold"), [("Hull", 0, {}), ("Cupola", 2, {})], turretReducePct=0.5)
    # fx_r_orphan: a node in no scene, created FIRST (the Orphan Nodes collection: listed after the scene's own objects), kept as a
    # rudder so it survives the join: VehicleRig links between the two
    r = Rig(); ma = r.material("Paint", (0.5, 0.5, 0.5))
    stray = r.node("Stray", mesh=r.mesh("Stray", at=(4.0, 0.0, 0.0), material=ma)); r.roots.remove(stray)
    r.node("Hull", mesh=r.mesh("Hull", at=(-1.0, -1.0, 0.0), size=2.0, material=ma)); r.node("Tire", mesh=r.mesh("Tire", at=(-0.5, 2.0, -0.5), material=ma))
    recipe(out, "fx_r_orphan", r.write(models, "fx_r_orphan"), [("Hull", 0, {}), ("Tire", 1, {}), ("Stray", 18, {})], sub="recipes_exit")
    # fx_r_noscene: the one scene lists no node - every object in Orphan Nodes, VehicleRig first in scene.objects
    r = Rig(); ma = r.material("Paint", (0.5, 0.5, 0.5))
    r.node("Hull", mesh=r.mesh("Hull", at=(-1.0, -1.0, 0.0), size=2.0, material=ma)); r.node("Tire", mesh=r.mesh("Tire", at=(-0.5, 2.0, -0.5), material=ma)); r.roots.clear()
    recipe(out, "fx_r_noscene", r.write(models, "fx_r_noscene"), [("Hull", 0, {}), ("Tire", 1, {})], sub="recipes_exit")
    # the sail-fold and oar dials Python reads unconditionally (argv 72, 73, 75, 56-59): a bad one kills the script before the dials
    for key in ("fx_d_sailfold_frames", "fx_d_sailfold_inf", "fx_d_oar_lift", "fx_d_sailfold_overflow", "fx_d_unicode_fold"):
        r = Rig(); ma = r.material("Paint", (0.5, 0.5, 0.5))
        r.node("Hull", mesh=r.mesh("Hull", material=ma)); r.node("Tire", mesh=r.mesh("Tire", at=(3.0, 0.0, 0.0), material=ma))
        recipe(out, key, r.write(models, key), [("Hull", 0, {}), ("Tire", 1, {})], sub="recipes_exit")
    # fx_r_noscene1: one mesh node in no scene - the source's single object, so the loose split (unguarded) selects it first
    r = Rig(); ma = r.material("Paint", (0.5, 0.5, 0.5))
    r.node("Hull", mesh=r.mesh("Hull", material=ma)); r.roots.clear()
    recipe(out, "fx_r_noscene1", r.write(models, "fx_r_noscene1"), [("Hull", 0, {})], sub="recipes_exit")
    # ---- stage 3, part d2 (the sail, rigging on the sail, the flag)
    # fx_r_sail (glTF is Y-up: Blender's z is the glTF y): a standing canvas (a 5 x 9 grid in the glTF XY plane, 4 tall) under the sail
    # fold - its rows fall in all four height bands, the sail's rise and the fold drop take their quarter-height branches; a topsail
    # above it also marked as a wheel (the Sail bone wins); a jib that is also a rudder (banded, keyed apart); a burgee that is sail and
    # flag (the Flag bone, banded inside Mesh_Flag); rigging riding the sail (its own mesh), a halyard that is rigging and flag, and a
    # rigging name the scene has not (skipped silently); a missing sail name (the WARN); an animated sail part (its world off the
    # identity in the bands and the box); a struck flag (the 0.3 rise floor); a wheel
    r = Rig(); ma = r.material("Canvas", (0.9, 0.9, 0.8))
    r.node("Hull", mesh=r.mesh("Hull", at=(-2.0, -1.0, 0.0), size=2.0, material=ma))
    r.node("Canvas", mesh=flat_grid(r, "Canvas", ma, 5, 9), translation=[-1.0, 0.0, 2.0], scale=[2.0, 4.0, 1.0])
    r.node("Topsail", mesh=r.mesh("Topsail", at=(-0.2, 4.2, 1.8), size=0.4, material=ma))
    r.node("Jib", mesh=r.mesh("Jib", at=(1.2, 1.0, 1.8), size=0.6, material=ma))
    r.node("Burgee", mesh=r.mesh("Burgee", at=(-1.6, 2.5, 1.8), size=0.3, material=ma))
    streamer = r.node("Streamer", mesh=r.mesh("Streamer", at=(0.8, 3.0, 1.8), size=0.3, material=ma))
    a1 = r.anim("Flutter"); r.key(a1, streamer, "translation", [0.0, 1.0], [(0.0, 0.5, 0.0), (0.0, 0.9, 0.0)])
    r.node("Rope", mesh=r.mesh("Rope", at=(1.5, 0.0, 1.0), size=0.2, material=ma))
    r.node("Halyard", mesh=r.mesh("Halyard", at=(1.9, 0.5, 1.0), size=0.2, material=ma))
    r.node("Pennant", mesh=r.mesh("Pennant", at=(-3.0, 0.0, 3.0), size=0.5, material=ma))
    r.node("Wheel", mesh=r.mesh("Wheel", at=(-0.5, -1.5, -0.5), material=ma), scale=[1.0, 0.3, 1.0])
    recipe(out, "fx_r_sail", r.write(models, "fx_r_sail"),
           [("Hull", 0, {}), ("Canvas", 14, {}), ("Topsail", 14, {}), ("Topsail", 1, {}), ("Jib", 14, {}), ("Jib", 18, {}), ("Burgee", 14, {}),
            ("Burgee", 17, {}), ("Streamer", 14, {}), ("GhostSail", 14, {}), ("Rope", 15, {}), ("Halyard", 15, {}), ("Halyard", 17, {}),
            ("GhostRope", 15, {}), ("Pennant", 17, {}), ("Wheel", 1, {})], sailFoldIdle=True)
    # fx_r_flagfold: the flag hinged at its top under flagfold= (-30 degrees over 8 frames); a missing flag name (the WARN); rigging with
    # no sail stays on the body
    r = Rig(); ma = r.material("Cloth", (0.8, 0.2, 0.2))
    r.node("Hull", mesh=r.mesh("Hull", at=(-2.0, -1.0, 0.0), size=2.0, material=ma))
    r.node("Tripod", mesh=r.mesh("Tripod", at=(0.5, -0.25, 2.0), size=0.5, material=ma), scale=[1.0, 6.0, 1.0])   # 3 tall: the quarter-height rise
    r.node("Rope", mesh=r.mesh("Rope", at=(1.5, 0.0, 1.0), size=0.2, material=ma))
    recipe(out, "fx_r_flagfold", r.write(models, "fx_r_flagfold"), [("Hull", 0, {}), ("Tripod", 17, {}), ("GhostFlag", 17, {}), ("Rope", 15, {})],
           flagFoldOn=True, flagFoldDeg=-30.0, flagFoldFrames=8)
    # A decimal just above the halfway rounding boundary above 0.5: Python keeps it > 0.5, so the weld runs (24 -> 8
    # vertices), while Framework's double.Parse rounded it to 0.5 and skipped the entire tier.
    r = Rig(); ma = r.material("Paint", (0.5, 0.5, 0.5))
    r.node("Hull", mesh=box_split(r, "Hull", (ma, ma))); r.node("Other", mesh=r.mesh("Other", at=(3.0, 0.0, 0.0), material=ma))
    recipe(out, "fx_r_numeric_pct", r.write(models, "fx_r_numeric_pct"), [("Hull", 0, {}), ("Other", 16, {})], sub="recipes_exit")
    # A sail with exactly zero world height (glTF XZ -> Blender XY): the 1e-6 fold-height clamp; every rigging name
    # missing; an enabled flagfold whose blank fields take the script's defaults. These are valid argument variants.
    r = Rig(); ma = r.material("Cloth", (0.8, 0.2, 0.2))
    r.node("Hull", mesh=r.mesh("Hull", material=ma))
    p = r.b.accessor([(-1, 0, 0), (1, 0, 0), (1, 0, 1), (-1, 0, 1)], "f", "VEC3")
    idx = r.b.accessor([0, 1, 2, 0, 2, 3], "H", "SCALAR", minmax=False)
    mesh = len(r.meshes); r.meshes.append({"name": "Sheet", "primitives": [{"attributes": {"POSITION": p}, "indices": idx, "material": ma}]})
    r.node("Sheet", mesh=mesh); r.node("Flag", mesh=r.mesh("Flag", at=(3.0, 0.0, 0.0), material=ma))
    recipe(out, "fx_r_flat_sail", r.write(models, "fx_r_flat_sail"), [("Hull", 0, {}), ("Sheet", 14, {}), ("GhostRope", 15, {}), ("Flag", 17, {})],
           sailFoldIdle=True, flagFoldOn=True, sub="recipes_exit")
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
    # None leaves the job positive: only its argument text is changed after the Lab's builder runs.
    "fx_r_numeric_pct": (None, lambda a: a.__setitem__(54, "0.500000000000000055511151231257827021181583404541015625000000000000000000001")),
    "fx_r_flat_sail": (None, lambda a: set_tag(a, "flagfold", "|")),
    "fx_d_sailfold_overflow": ("DIES", lambda a: a.__setitem__(72, "1e309")),
    "fx_d_unicode_fold": ("DIES", lambda a: (a.__setitem__(72, "\U00011f51"), a.__setitem__(73, "later"))),
    "fx_d_chunk_splice": ("DIES", lambda a: None),
    "fx_r_missing_wheel": ("EXIT", lambda a: None),
    "fx_r_orphan": ("EXIT", lambda a: None),
    "fx_r_noscene": ("EXIT", lambda a: None),
    "fx_r_noscene1": ("DIES", lambda a: None),
    "fx_d_sailfold_frames": ("DIES", lambda a: a.__setitem__(72, "x")),
    "fx_d_sailfold_inf": ("DIES", lambda a: a.__setitem__(72, "inf")),
    "fx_d_oar_lift": ("DIES", lambda a: a.__setitem__(56, "1__0")),
    "fx_x_allignored": ("EXIT", lambda a: None),
    "fx_x_bright": ("EXIT", lambda a: set_tag(a, "bright", "1.5|x")),
    "fx_x_flagfold": ("EXIT", lambda a: set_tag(a, "flagfold", "a|b")),
    "fx_x_defaultreduce": ("EXIT", lambda a: set_tag(a, "defaultreduce", "nopipe")),
    "fx_x_shroudreduce": ("EXIT", lambda a: set_tag(a, "shroudreduce", "|abc")),
    "fx_x_gunreduce": ("EXIT", lambda a: set_tag(a, "gunreduce", "abc")),
    "fx_x_turretreduce": ("EXIT", lambda a: set_tag(a, "turretreduce", "inf")),
    "fx_x_turret_quote": ("EXIT", lambda a: set_tag(a, "turretreduce", "can't")),
    "fx_x_turret_space": ("EXIT", lambda a: set_tag(a, "turretreduce", "\u00a0inf")),
    "fx_x_windowreduce": ("EXIT", lambda a: set_tag(a, "windowreduce", "nopipe")),
    "fx_x_merge_missing": ("EXIT", lambda a: set_tag(a, "merge2", "|".join([a[[t.startswith("merge2=") for t in a].index(True)][7:].split("|")[0] + ".missing.glb"] + a[[t.startswith("merge2=") for t in a].index(True)][7:].split("|")[1:]))),
    "fx_d_frames": ("DIES", lambda a: a.__setitem__(7, "15.0")),
    "fx_d_reduce_before_names": ("DIES", lambda a: (a.__setitem__(52, "half"), a.__setitem__(69, a[69] + ".missing.txt"))),
    "fx_d_infinite_count": ("DIES", lambda a: a.__setitem__(12, "inf")),
    "fx_d_nan_count": ("DIES", lambda a: a.__setitem__(22, "nan")),
    "fx_d_flagfold_infinite": ("DIES", lambda a: set_tag(a, "flagfold", "10|inf")),
    "fx_d_merge_offset": ("DIES", lambda a: set_tag(a, "merge2", "|".join([a[[t.startswith("merge2=") for t in a].index(True)][7:].split("|")[0], "a,0,0"] + a[[t.startswith("merge2=") for t in a].index(True)][7:].split("|")[2:]))),
    "fx_d_names": ("DIES", lambda a: a.__setitem__(4, a[4] + ".missing.txt")),
}


# the stage an exit or death fixture stops in, when not stage 1
STAGE_OF = {"fx_d_chunk_splice": 2, "fx_r_missing_wheel": 3}


def exits(jobs_path):
    n = 0
    for line in open(jobs_path, encoding="utf-8").read().split("\n"):
        if not line.startswith("JOB\t"):
            continue
        f = line.rstrip("\r").split("\t"); key = f[1]; argv = f[2:]
        if key not in EXITS:
            raise SystemExit("an exit recipe %s has no edit in EXITS" % key)
        kind, edit = EXITS[key]; edit(argv); n += 1
        stage = STAGE_OF.get(key, 1)
        print("\t".join(["JOB", key if kind is None else "%s:%d:%s" % (kind, stage, key)] + argv))
    if n != len(EXITS):
        raise SystemExit("expected %d exit jobs, got %d" % (len(EXITS), n))


if __name__ == "__main__":
    if sys.argv[1] == "--exits":
        exits(sys.argv[2])
    else:
        main(sys.argv[1])
