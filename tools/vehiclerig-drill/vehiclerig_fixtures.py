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
Stage 3 (step (d), part d3: the turret, the gun assembly, the trails):
  fx_r_turret        two turret parts, a gun on the turret with a muzzle brake and recoil (RecoilArm + Barrel), a trail arm, a trail of
                     three vertices and one of four coincident vertices (no bone for either)
  fx_r_cradle_only   a cradle and no tube (the span from the cradle; recoil on, no split), the gun on the body
  fx_r_vertical      an upright tube under recoil (the arc's axis from the fallback cross)
  fx_r_missing_turret / fx_r_missing_trail   find()'s exit in stage 3; fx_d_trail_frames (int(float("nan"))), fx_d_gun_pivot (float("x"))
  fx_r_turret also: the rock's RootHull under the turret, gun and trail; a turret part also a wheel; the brake also a gun, a mantlet gun and
                     cradle (the de-duplication); the three-vertex trail first (the arm is Trail_01)
  fx_r_spike         a three-vertex muzzle brake and no gun under recoil (fewer than 4 tube points: the defaults)
  fx_r_gunpoint      a tube of four coincident vertices under recoil (no GUNSPAN line, a zero bore; stage 4 then dies dividing by it: DIES:4:)
  fx_r_pivot_end     the pivot at the muzzle under recoil (the script's bore from gc ~ muzzle - an open script defect, reproduced)
  fx_x_default_names_dir / fx_x_shroud_names_dir / fx_x_window_names_dir: directory names in reduce tags print their caught error
  fx_d_dial_before_tag_names / fx_x_tag_before_bad_names: an earlier error wins over a later non-UTF-8 tagged names file
  fx_d_pos_names_utf8 / fx_x_tag_names_utf8: native decode death/exit; the C# model explicitly leaves decoding to Blender
  fx_r_names_ws / fx_r_names_bom   a names file line wrapped in \\x1c..\\x1f (stripped) / starting with a BOM (kept: the exit)
  fx_d_names_empty / fx_d_names_dir / fx_d_dial_before_names   open("") / open(a directory) / a dial dying before a later names file
  fx_r_flat_sail     a zero-height sail taking the 1e-6 fold-height clamp, all rigging missing, flagfold=| taking its defaults
Stage 3 (step (d), part d4: the rotors):
  fx_r_rotor         a main rotor of a tilted, animated 16-sided mast hub (numpy's cov and eigh on a real cloud) and two blades, the hub also a
                     wheel and a blade also a turret part (the later bones win); a three-part tail fan trimmed yaw 12.5, pitch -7.25;
                     a skid wheel; the wave rock on (RootHull carries every cluster)
  fx_r_rotor_forced  the main axle forced to X (the wheels' override, a wheel beside it), the tail forced to Z: the tail bone's in-disc
                     direction falls back to +Y, the yaw trim leaves Z as it is and the pitch has no axis ((0, 0, 1) x Z is zero)
  fx_r_rotor_centred two unit-cube blades tied on their distance to the mean (the first is the hub; three equal eigenvalues), the tail
                     fan right above the model's centre (no boom direction: the axle (0, 1, 0))
  fx_r_missing_rotor a tail rotor name the scene has not: find()'s exit in stage 3
  fx_r_rotor_point   a main rotor whose hub (listed second, strictly nearest the mean) is ONE vertex: np.cov gives NaN, eigh raises,
                     and the script's except takes the axis of least spread of the parts' centres; no tail rotor
  fx_r_tail_only     a tail rotor and no main rotor
Stage 3 (step (d), part d5: the oars):
  fx_r_oars          two oar parts, three raked oars per bank (a pole, a blade and an oarlock ring below the min span, three islands
                     clustered into one oar) and one raked the other way (its PC1 opposite: the sign alignment), no roll, the pivot
                     at 30 %; a missing oar name (the WARN); one part also a wheel
  fx_r_oars_roll     ONE part with oars on both banks, listed twice (its islands twice: the roll rotates them twice), roll 25,
                     pivot 60, length 150, its node 1e-7 off the identity (stage 1 still leaves an identity world: measured)
  fx_r_oars_odd      exactly vertical oars on one bank (the cross axis (1, 0, 0), a zero rake, the pivot snapped to a vertex) and only
                     ring-sized islands on the other (no pole direction: the bank skipped, its vertices in no group); pivot 120 and
                     length 10 clamp to 95 and 25
  fx_r_oars_ghost    an oar list naming only a part the scene has not: no recovery line, 0 oars from 0 parts
  fx_r_oars_args     argument variant: the roll "nan" (no roll), the pivot "nan" (max(0, nan) = 0) and the length "inf" (300)
  fx_r_oars_split    the shafts (poles + rings) and the blades in two parts: every cluster spans both (each part's own inverse
                     and skin under the roll and the length); a one-vertex island far out (no scatter: PC1 stops at (1, 0.3, 0.1));
                     two exactly vertical ring stubs placed in eps units beside a vertical pole - one in the neighbouring grid cell
                     but farther than eps (rejected), one merged with it across the next cell boundary
  fx_r_oars_inf      the roll "inf": angle_wrap_rad gives NaN, every rewritten coordinate NaN, and stage 4 dies inverting a NaN bone
                     matrix (DIES:4:); the C# leaves stage 3 to Blender by name - a NaN's sign bit follows each compiler's operand order
  fx_r_tail_args     argument variant: the tail axis "x" (upper(): forced X), the trim yaw "-inf" and pitch "nan" (a NaN axle the
                     normalize zeroes; align_roll on a zero vector)
Stage 4 (step (e), part e1: the Spin action, the wheels, the rock):
  fx_e_rock          the rock forced about X, turned by a 30-degree heading, rolling twice and pitching three times at a 45-degree
                     phase over 24 frames while the Spin's 15 repeat twice (the shared clip); four wheels
  fx_e_speeds        argument variant: static tracks (the rearmost wheel per side the idler), road wheels between, the road speed
                     1.5 and the idler speed 0.5 as the Lab never writes them (no symmetry snap off 1); an animated part and no
                     rock (only the last frame_set's evaluation moves it)
  fx_e_rockdefault   argument variant: the rock on with its frames blank (rock_frames = the Spin's frames), forced about Y
  fx_e_ground        a long flat hull pitched by the rock and a small wheel just above the undeformed ground band: after the rock's
                     frame_set the bounds are the DEFORMED meshes', the band drops below every wheel and all three keep the dial
  fx_e_rock2         heading 0, four pitch cycles at phase 0.01: four samples share the clip's last frame, each replacing its key
                     (value + float(delta)), and the wheels' frame_set leaves the hull at that key; the hull longer along Y (the
                     auto roll axis Y); the rock repeating 3 times. The replaced key equals the last sample here and on every
                     fixture: the angles pass angle_wrap_rad's float32 (multiples of pi's ulp), so the delta is exact
  fx_e_rock3         the rock with no wheels (no frame_set after it), the auto axis turned by a 15-degree heading
  (no fixture for the rock past 1,000,000 samples or 100,000 frames: Blender samples every one of them - a 200,000-frame rock took
  40 minutes in the dump)
  fx_e_long          argument variant: a Spin of 200,000 frames - left to Blender by name
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


def raw_mesh(r, name, material, pos, idx):
    """A mesh of the given glTF positions and triangle indices (one primitive)."""
    prim = {"attributes": {"POSITION": r.b.accessor(pos, "f", "VEC3")}, "indices": r.b.accessor(idx, "H", "SCALAR", minmax=False), "material": material}
    r.meshes.append({"name": name, "primitives": [prim]})
    return len(r.meshes) - 1


def ring_hub(r, name, material, radius, height, n=16):
    """A hub: two rings of n vertices (glTF y up) joined by a band of quads, open at both ends."""
    pos = [(radius * math.cos(2 * math.pi * i / n), y, radius * math.sin(2 * math.pi * i / n)) for y in (0.0, height) for i in range(n)]
    idx = []
    for i in range(n):
        j = (i + 1) % n
        idx += [i, j, n + j, i, n + j, n + i]
    return raw_mesh(r, name, material, pos, idx)


def _slab(pos, idx, c, ax, ay, az):
    base = len(pos)
    pos += [tuple(c[i] + sx * ax[i] + sy * ay[i] + sz * az[i] for i in range(3)) for sx in (-1, 1) for sy in (-1, 1) for sz in (-1, 1)]
    idx += [base + i for i in [0, 1, 3, 0, 3, 2, 4, 6, 7, 4, 7, 5, 0, 4, 5, 0, 5, 1, 2, 3, 7, 2, 7, 6, 0, 2, 6, 0, 6, 4, 1, 5, 7, 1, 7, 3]]


def _norm(v):
    l = math.sqrt(sum(x * x for x in v)); return tuple(x / l for x in v)


def _cross(a, b):
    return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])


def oar_bank(r, name, material, oars, parts=("pole", "blade", "ring")):
    """One mesh of oars, each three islands: a pole, a blade at its outboard end and a small ring at the oarlock (glTF y up; a
    bank's side is -z for Blender +y). oars = [(x, side, rake, lift, vertical)]."""
    pos = []; idx = []
    for x, side, rake, lift, vertical in oars:
        lock = (x, 1.25, -side * 0.875)   # dyadic sizes below: an axis-aligned pole's float32 mean is exact, its PC1 exactly the axis
        d = (0.0, 1.0, 0.0) if vertical else _norm((rake, -lift, -side * 1.0))
        a = _norm(_cross(d, (0.0, 0.0, 1.0) if vertical else (0.0, 1.0, 0.0))); b = _cross(a, d)
        mul = lambda v, s: tuple(t * s for t in v)
        if "pole" in parts: _slab(pos, idx, tuple(lock[i] + d[i] * 0.75 for i in range(3)), mul(d, 1.5), mul(a, 0.03125), mul(b, 0.03125))
        if "blade" in parts: _slab(pos, idx, tuple(lock[i] + d[i] * 2.25 for i in range(3)), mul(d, 0.25), mul(a, 0.125), mul(b, 0.0078125))
        if "ring" in parts: _slab(pos, idx, lock, (0.015625, 0.0, 0.0), (0.0, 0.015625, 0.0), (0.0, 0.0, 0.015625))
    return raw_mesh(r, name, material, pos, idx)


def oar_rings(r, name, material, rings):
    """Only ring-sized islands (below the oar min span): [(x, side)]."""
    pos = []; idx = []
    for x, side in rings:
        _slab(pos, idx, (x, 1.25, -side * 0.875), (0.015625, 0.0, 0.0), (0.0, 0.015625, 0.0), (0.0, 0.0, 0.015625))
    return raw_mesh(r, name, material, pos, idx)


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
    # ---- stage 3, part d3 (the turret, the gun assembly, the trails); glTF is Y-up: Blender's z is the glTF y, its y the glTF -z
    # fx_r_turret: two turret parts (one Turret bone at their combined box), a tube along x on the turret with a wide muzzle brake at its
    # far end (the brake pins the tip) and recoil at 0.2 (RecoilArm + Barrel; the brake rides the Barrel), the pivot at 0.3; a trail arm
    # (a bone hinged at its end nearer the body), a trail of three vertices and one whose four vertices coincide (no bone for either)
    r = Rig(); ma = r.material("Steel", (0.4, 0.4, 0.45))
    r.node("Hull", mesh=r.mesh("Hull", at=(-2.0, -1.0, -1.0), size=2.0, material=ma))
    r.node("Dome", mesh=r.mesh("Dome", at=(-0.5, 1.0, -0.5), size=1.0, material=ma)); r.node("Ring", mesh=r.mesh("Ring", at=(-0.1, 1.0, -0.1), size=0.2, material=ma), scale=[7.0, 1.0, 7.0])   # x, z -0.7 .. 0.7 round the dome
    r.node("Tube", mesh=r.mesh("Tube", at=(0.5 / 15.0, 1.4, -0.1), size=0.2, material=ma), scale=[15.0, 1.0, 1.0])   # x 0.5 .. 3.5 (the node scale scales the offset too)
    r.node("Brake", mesh=r.mesh("Brake", at=(3.5, 1.3, -0.2), size=0.4, material=ma))
    r.node("Arm", mesh=r.mesh("Arm", at=(-1.0, -0.8, 0.2), size=0.2, material=ma), scale=[1.0, 1.0, 20.0])
    r.node("Stub", mesh=raw_mesh(r, "Stub", ma, [(-1.5, -0.8, 0.5), (-1.4, -0.8, 0.5), (-1.5, -0.7, 0.5)], [0, 1, 2]))
    r.node("Pin", mesh=raw_mesh(r, "Pin", ma, [(-1.8, -0.5, 0.5)] * 4, [0, 1, 2, 0, 2, 3]))
    r.node("Mantlet", mesh=r.mesh("Mantlet", at=(0.3, 1.3, -0.3), size=0.6, material=ma))
    recipe(out, "fx_r_turret", r.write(models, "fx_r_turret"),
           [("Hull", 0, {}), ("Dome", 2, {}), ("Ring", 2, {}), ("Ring", 1, {}), ("Tube", 7, {}), ("Brake", 11, {}), ("Brake", 7, {}), ("Mantlet", 7, {}),
            ("Mantlet", 12, {}), ("Stub", 10, {}), ("Arm", 10, {}), ("Pin", 10, {})],
           gunPivot=0.3, recoilDist=0.2, waveEnabled=True, rockDegrees=4.0, rockFrames=15)
    # fx_r_cradle_only: only a cradle marked (the span from the cradle: no tube), recoil on (no tube: no split); a gun with no turret
    r = Rig(); ma = r.material("Steel", (0.4, 0.4, 0.45))
    r.node("Hull", mesh=r.mesh("Hull", at=(-2.0, -1.0, -1.0), size=2.0, material=ma))
    r.node("Cradle", mesh=r.mesh("Cradle", at=(0.5, 1.0, -0.2), size=0.4, material=ma), scale=[5.0, 1.0, 1.0])
    recipe(out, "fx_r_cradle_only", r.write(models, "fx_r_cradle_only"), [("Hull", 0, {}), ("Cradle", 12, {})], recoilDist=0.5)
    # fx_r_vertical: a tube standing upright (its bore along z: the recoil arc's axis takes the fallback cross with y)
    r = Rig(); ma = r.material("Steel", (0.4, 0.4, 0.45))
    r.node("Hull", mesh=r.mesh("Hull", at=(-2.0, -1.0, -1.0), size=2.0, material=ma))
    r.node("Mortar", mesh=r.mesh("Mortar", at=(-0.1, 1.0, -0.1), size=0.2, material=ma), scale=[1.0, 15.0, 1.0])
    recipe(out, "fx_r_vertical", r.write(models, "fx_r_vertical"), [("Hull", 0, {}), ("Mortar", 7, {})], recoilDist=0.3, gunPivot=0.0)
    # fx_r_spike: a muzzle brake and no gun (the tube is the brake), a three-vertex one - fewer than 4 tube points: the head stays at the
    # box centre, nothing printed, recoil on its defaults ((0, -1, 0), a slide of 1)
    r = Rig(); ma = r.material("Steel", (0.4, 0.4, 0.45))
    r.node("Hull", mesh=r.mesh("Hull", at=(-2.0, -1.0, -1.0), size=2.0, material=ma))
    r.node("Spike", mesh=raw_mesh(r, "Spike", ma, [(1.0, 1.2, 0.0), (2.0, 1.2, 0.0), (1.0, 1.4, 0.0)], [0, 1, 2]))
    recipe(out, "fx_r_spike", r.write(models, "fx_r_spike"), [("Hull", 0, {}), ("Spike", 11, {})], recoilDist=0.4)
    # fx_r_gunpoint: a tube of four coincident vertices (a zero span: no GUNSPAN line; a zero bore under recoil)
    r = Rig(); ma = r.material("Steel", (0.4, 0.4, 0.45))
    r.node("Hull", mesh=r.mesh("Hull", at=(-2.0, -1.0, -1.0), size=2.0, material=ma))
    r.node("Dot", mesh=raw_mesh(r, "Dot", ma, [(1.5, 1.2, 0.0)] * 4, [0, 1, 2, 0, 2, 3]))
    recipe(out, "fx_r_gunpoint", r.write(models, "fx_r_gunpoint"), [("Hull", 0, {}), ("Dot", 7, {})], recoilDist=0.4, sub="recipes_exit")   # stage 4 divides by the zero bore: the script dies there
    # fx_r_pivot_end: the pivot at the muzzle (1.0) under recoil - the script's bore comes from gc ~ muzzle (an open script defect,
    # recorded in the backlog); the port reproduces it bit for bit
    r = Rig(); ma = r.material("Steel", (0.4, 0.4, 0.45))
    r.node("Hull", mesh=r.mesh("Hull", at=(-2.0, -1.0, -1.0), size=2.0, material=ma))
    r.node("Tube", mesh=r.mesh("Tube", at=(0.5 / 15.0, 1.4, -0.1), size=0.2, material=ma), scale=[15.0, 1.0, 1.0])
    recipe(out, "fx_r_pivot_end", r.write(models, "fx_r_pivot_end"), [("Hull", 0, {}), ("Tube", 7, {})], recoilDist=0.3, gunPivot=1.0)
    # ---- stage 3 (step (d), part d4: the rotors)
    # fx_r_rotor: a tilted mast hub (8 degrees about a leaning axis) between two blades; a three-part tail fan on the boom, trimmed
    r = Rig(); ma = r.material("Paint", (0.4, 0.45, 0.3))
    tilt_axis = (1.0, 0.0, 0.25); tl = math.sqrt(sum(x * x for x in tilt_axis)); half = math.radians(8.0) / 2.0
    tilt = [tilt_axis[0] / tl * math.sin(half), tilt_axis[1] / tl * math.sin(half), tilt_axis[2] / tl * math.sin(half), math.cos(half)]
    r.node("Hull", mesh=r.mesh("Hull", at=(-1.0, 0.0, -1.0), size=2.0, material=ma), scale=[1.5, 1.0, 1.0])
    r.node("Boom", mesh=r.mesh("Boom", material=ma), translation=[1.5, 1.2, -0.15], scale=[6.0, 0.3, 0.3])
    mast = r.node("Mast", mesh=ring_hub(r, "Mast", ma, 0.3, 0.15), translation=[0.1, 2.4, -0.05], rotation=tilt)
    # the mast animated (a turn about a second leaning axis): transform_apply bakes the action's first key, not the node's own
    # rotation, into the hub, and leaves its world at the identity (Blender's dump: the hub's matrix_world after stage 1)
    turn_axis = (0.3, 0.2, 1.0); ql = math.sqrt(sum(x * x for x in turn_axis)); h2 = math.radians(20.0) / 2.0
    turned = [turn_axis[0] / ql * math.sin(h2), turn_axis[1] / ql * math.sin(h2), turn_axis[2] / ql * math.sin(h2), math.cos(h2)]
    a_mast = r.anim("MastTurn"); r.key(a_mast, mast, "rotation", [0.0, 1.0], [tuple(turned), tuple(tilt)])
    r.node("BladeA", mesh=r.mesh("BladeA", material=ma), translation=[0.4, 2.55, -0.2], scale=[4.0, 0.04, 0.4])
    r.node("BladeB", mesh=r.mesh("BladeB", material=ma), translation=[-4.2, 2.55, -0.2], scale=[4.0, 0.04, 0.4])
    r.node("FanHub", mesh=r.mesh("FanHub", at=(-0.075, -0.075, -0.075), size=0.15, material=ma), translation=[7.4, 1.35, 0.3])
    r.node("FanA", mesh=r.mesh("FanA", at=(-0.5, -0.5, -0.5), material=ma), translation=[7.4, 1.35, 0.3], scale=[0.9, 0.08, 0.03])
    r.node("FanB", mesh=r.mesh("FanB", at=(-0.5, -0.5, -0.5), material=ma), translation=[7.4, 1.35, 0.3], scale=[0.08, 0.9, 0.03])
    r.node("Skid", mesh=r.mesh("Skid", at=(-0.5, -0.5, -0.5), material=ma), translation=[0.0, -0.2, 0.9], scale=[2.0, 0.1, 0.1])
    recipe(out, "fx_r_rotor", r.write(models, "fx_r_rotor"),
           [("Hull", 0, {}), ("Boom", 0, {}), ("BladeA", 8, {}), ("Mast", 8, {}), ("BladeB", 8, {}), ("Mast", 1, {}), ("BladeB", 2, {}),
            ("FanHub", 9, {}), ("FanA", 9, {}), ("FanB", 9, {}), ("Skid", 1, {})],
           tailYawAdj=12.5, tailPitchAdj=-7.25, waveEnabled=True, rockDegrees=5.0, rockFrames=15)
    # fx_r_rotor_forced: the main axle forced to X (axisChoice: the wheels' override too), the tail forced to Z and trimmed
    r = Rig(); ma = r.material("Paint", (0.4, 0.45, 0.3))
    r.node("Hull", mesh=r.mesh("Hull", at=(-1.0, 0.0, -1.0), size=2.0, material=ma))
    r.node("Disc", mesh=r.mesh("Disc", at=(-0.5, -0.5, -0.5), material=ma), translation=[0.0, 2.5, 0.0], scale=[3.0, 0.05, 3.0])
    r.node("Tire", mesh=r.mesh("Tire", at=(-0.5, -0.5, -0.5), material=ma), translation=[0.0, 0.0, 1.2], scale=[0.6, 0.6, 0.2])
    r.node("Fan", mesh=r.mesh("Fan", at=(-0.5, -0.5, -0.5), material=ma), translation=[6.0, 1.5, 0.3], scale=[0.8, 0.8, 0.04])
    recipe(out, "fx_r_rotor_forced", r.write(models, "fx_r_rotor_forced"), [("Hull", 0, {}), ("Disc", 8, {}), ("Tire", 1, {}), ("Fan", 9, {})],
           axisChoice=1, tailAxisChoice=3, tailYawAdj=30.0, tailPitchAdj=10.0)
    # fx_r_rotor_centred: two unit-cube blades the same distance from their mean (min keeps the first: a cube hub, cov = c * I), the
    # tail fan straight above the model's centre (the model centre minus the fan's has no horizontal part)
    r = Rig(); ma = r.material("Paint", (0.4, 0.45, 0.3))
    r.node("Hull", mesh=r.mesh("Hull", at=(-1.0, -1.0, -1.0), size=2.0, material=ma))
    r.node("BladeL", mesh=r.mesh("BladeL", at=(-0.5, -0.5, -0.5), material=ma), translation=[-3.0, 2.5, 0.0])
    r.node("BladeR", mesh=r.mesh("BladeR", at=(-0.5, -0.5, -0.5), material=ma), translation=[3.0, 2.5, 0.0])
    r.node("Fan", mesh=r.mesh("Fan", at=(-0.5, -0.5, -0.5), material=ma), translation=[0.0, 4.0, 0.0], scale=[0.6, 0.6, 0.05])
    recipe(out, "fx_r_rotor_centred", r.write(models, "fx_r_rotor_centred"), [("Hull", 0, {}), ("BladeL", 8, {}), ("BladeR", 8, {}), ("Fan", 9, {})])
    # fx_r_rotor_point: the hub a single vertex (a POINTS primitive), listed second and strictly nearest the blades' mean
    r = Rig(); ma = r.material("Paint", (0.4, 0.45, 0.3))
    r.node("Hull", mesh=r.mesh("Hull", at=(-1.0, 0.0, -1.0), size=2.0, material=ma))
    r.node("BladeL", mesh=r.mesh("BladeL", at=(-0.5, -0.5, -0.5), material=ma), translation=[-3.0, 2.5, 0.0], scale=[2.0, 0.05, 0.4])
    r.node("BladeR", mesh=r.mesh("BladeR", at=(-0.5, -0.5, -0.5), material=ma), translation=[3.0, 2.7, 0.4], scale=[2.0, 0.05, 0.4])
    r.meshes.append({"name": "Pin", "primitives": [{"attributes": {"POSITION": r.b.accessor([(0.0, 2.6, 0.1)], "f", "VEC3")}, "mode": 0, "material": ma}]})
    r.node("Pin", mesh=len(r.meshes) - 1)
    recipe(out, "fx_r_rotor_point", r.write(models, "fx_r_rotor_point"), [("Hull", 0, {}), ("BladeL", 8, {}), ("Pin", 8, {}), ("BladeR", 8, {})])
    # fx_r_tail_only: a tail fan and no main rotor
    r = Rig(); ma = r.material("Paint", (0.4, 0.45, 0.3))
    r.node("Hull", mesh=r.mesh("Hull", at=(-1.0, 0.0, -1.0), size=2.0, material=ma))
    r.node("Fan", mesh=r.mesh("Fan", at=(-0.5, -0.5, -0.5), material=ma), translation=[5.0, 1.2, -0.4], scale=[0.7, 0.7, 0.04])
    recipe(out, "fx_r_tail_only", r.write(models, "fx_r_tail_only"), [("Hull", 0, {}), ("Fan", 9, {})])
    # ---- stage 3 (step (d), part d5: the oars)
    # fx_r_oars: two parts, three raked oars per bank, no roll; a missing name; the starboard part also a wheel
    def oars_model(key):
        r = Rig(); ma = r.material("Wood", (0.5, 0.35, 0.2))
        r.node("Hull", mesh=r.mesh("Hull", at=(-0.5, 0.0, -0.8), size=1.6, material=ma), scale=[2.5, 1.0, 1.0])
        r.node("OarsA", mesh=oar_bank(r, "OarsA", ma, [(k * 1.0, 1, 0.3, 0.25, False) for k in range(3)] + [(5.0, 1, -0.9, 0.25, False)]))
        r.node("OarsB", mesh=oar_bank(r, "OarsB", ma, [(k * 1.0 + 0.1, -1, 0.3, 0.25, False) for k in range(3)]))
        return r.write(models, key)
    recipe(out, "fx_r_oars", oars_model("fx_r_oars"), [("Hull", 0, {}), ("OarsA", 13, {}), ("OarsB", 13, {}), ("GhostOar", 13, {}), ("OarsB", 1, {})],
           oarBladeRollDeg=0.0, oarPivotPct=30.0, oarLengthPct=100.0)
    # fx_r_oars_roll: one part, both banks, listed twice; roll 25, pivot 60, length 150; the node 1e-7 off the identity (its world
    # after stage 1 is still the identity - Blender's dump -, so the rewrite's inverse is the identity's)
    r = Rig(); ma = r.material("Wood", (0.5, 0.35, 0.2))
    r.node("Hull", mesh=r.mesh("Hull", at=(-0.5, 0.0, -0.8), size=1.6, material=ma), scale=[2.5, 1.0, 1.0])
    r.node("Bank", mesh=oar_bank(r, "Bank", ma, [(0.0, 1, 0.2, 0.3, False), (1.1, 1, 0.25, 0.3, False), (0.05, -1, 0.2, 0.3, False), (1.15, -1, 0.25, 0.3, False)]),
           translation=[1e-7, 0.0, 0.0])
    recipe(out, "fx_r_oars_roll", r.write(models, "fx_r_oars_roll"), [("Hull", 0, {}), ("Bank", 13, {}), ("Bank", 13, {})],
           oarBladeRollDeg=25.0, oarPivotPct=60.0, oarLengthPct=150.0)
    # fx_r_oars_odd: vertical oars on one bank, only rings on the other; the pivot and length dials clamped
    r = Rig(); ma = r.material("Wood", (0.5, 0.35, 0.2))
    r.node("Hull", mesh=r.mesh("Hull", at=(-0.5, 0.0, -0.8), size=1.6, material=ma), scale=[2.5, 1.0, 1.0])
    r.node("Poles", mesh=oar_bank(r, "Poles", ma, [(0.0, 1, 0.0, 0.0, True), (1.2, 1, 0.0, 0.0, True)]))
    r.node("Rings", mesh=oar_rings(r, "Rings", ma, [(0.0, -1), (1.2, -1)]))
    recipe(out, "fx_r_oars_odd", r.write(models, "fx_r_oars_odd"), [("Hull", 0, {}), ("Poles", 13, {}), ("Rings", 13, {})],
           oarBladeRollDeg=10.0, oarPivotPct=120.0, oarLengthPct=10.0)
    # fx_r_oars_ghost: only a missing oar name
    r = Rig(); ma = r.material("Wood", (0.5, 0.35, 0.2))
    r.node("Hull", mesh=r.mesh("Hull", at=(-0.5, 0.0, -0.8), size=1.6, material=ma))
    recipe(out, "fx_r_oars_ghost", r.write(models, "fx_r_oars_ghost"), [("Hull", 0, {}), ("GhostOar", 13, {})])
    # fx_r_oars_args: the oar dials as the Lab never writes them (post-edited by --exits)
    recipe(out, "fx_r_oars_args", oars_model("fx_r_oars_args"), [("Hull", 0, {}), ("OarsA", 13, {}), ("OarsB", 13, {})], sub="recipes_exit")
    # fx_r_oars_split: shafts and blades in two parts (clusters across parts), roll 15, length 130; a one-vertex island; ring stubs
    # beside a vertical pole on the other bank, placed against the merge eps's grid (eps computed here as the script will: 0.00786 x
    # the oar points' diagonal - the pole and the stubs stay inside the other points' x range, so their x does not move it)
    raked = [(0.0, 1, 0.3, 0.25, False), (1.0, 1, 0.3, 0.25, False), (2.0, 1, 0.3, 0.25, False)]
    pts = []
    for oars_, parts_ in ((raked + [(0.5, -1, 0.0, 0.0, True)], ("pole", "ring")), (raked, ("blade",))):
        pp = []; ii = []
        for x, side, rake, lift, vertical in oars_:
            lock = (x, 1.25, -side * 0.875)
            dd = (0.0, 1.0, 0.0) if vertical else _norm((rake, -lift, -side * 1.0))
            aa = _norm(_cross(dd, (0.0, 0.0, 1.0) if vertical else (0.0, 1.0, 0.0))); bb = _cross(aa, dd)
            mul = lambda v, s: tuple(t * s for t in v)
            if "pole" in parts_: _slab(pp, ii, tuple(lock[i] + dd[i] * 0.75 for i in range(3)), mul(dd, 1.5), mul(aa, 0.03125), mul(bb, 0.03125))
            if "blade" in parts_: _slab(pp, ii, tuple(lock[i] + dd[i] * 2.25 for i in range(3)), mul(dd, 0.25), mul(aa, 0.125), mul(bb, 0.0078125))
            if "ring" in parts_: _slab(pp, ii, lock, (0.015625, 0.0, 0.0), (0.0, 0.015625, 0.0), (0.0, 0.0, 0.015625))
        pts += pp
    lo = [min(p[k] for p in pts) for k in range(3)]; hi = [max(p[k] for p in pts) for k in range(3)]
    eps = 0.00786 * math.sqrt(sum((hi[k] - lo[k]) ** 2 for k in range(3)))
    # the vertical bank: its pole direction exactly (0, 0, 1) in Blender, e1 = (0, 1, 0), e2 = (-1, 0, 0), so an island's v = -x and its
    # cell int(-x / eps) (toward zero: cell c holds v in ((c - 1) eps, c eps] for c <= 0). The pole at v = (c - 0.1) eps; stub A at
    # (c - 1.25) eps - the next cell, 1.15 eps away: rejected; stub B at (c - 2.15) eps - the cell after, 0.9 eps from A: merged
    c = -round(0.5 / eps)
    vert = [((0.1 - c) * eps, -1, 0.0, 0.0, True)]
    r = Rig(); ma = r.material("Wood", (0.5, 0.35, 0.2))
    r.node("Hull", mesh=r.mesh("Hull", at=(-0.5, 0.0, -0.8), size=1.6, material=ma), scale=[2.5, 1.0, 1.0])
    r.node("Shafts", mesh=oar_bank(r, "Shafts", ma, raked + vert, parts=("pole", "ring")))
    r.node("Blades", mesh=oar_bank(r, "Blades", ma, raked, parts=("blade",)))
    r.node("Stubs", mesh=oar_rings(r, "Stubs", ma, [((1.25 - c) * eps, -1), ((2.15 - c) * eps, -1)]))
    r.meshes.append({"name": "Dot", "primitives": [{"attributes": {"POSITION": r.b.accessor([(2.5, 1.25, -2.0)], "f", "VEC3")}, "mode": 0, "material": ma}]})
    r.node("Dot", mesh=len(r.meshes) - 1)
    recipe(out, "fx_r_oars_split", r.write(models, "fx_r_oars_split"), [("Hull", 0, {}), ("Shafts", 13, {}), ("Blades", 13, {}), ("Stubs", 13, {}), ("Dot", 13, {})],
           oarBladeRollDeg=15.0, oarPivotPct=40.0, oarLengthPct=130.0)
    # fx_r_oars_inf: the roll as the Lab never writes it (post-edited by --exits)
    recipe(out, "fx_r_oars_inf", oars_model("fx_r_oars_inf"), [("Hull", 0, {}), ("OarsA", 13, {}), ("OarsB", 13, {})], sub="recipes_exit")
    # ---- stage 4 (step (e), part e1)
    def wheeled(key, sub="recipes", mid=False, anim=False, **fields):
        r = Rig(); ma = r.material("Paint", (0.5, 0.5, 0.5))
        r.node("Hull", mesh=r.mesh("Hull", at=(-1.5, 0.0, -1.0), size=2.0, material=ma), scale=[1.5, 1.0, 1.0])
        for i, (x, z) in enumerate(((1.6, 1.2), (-1.6, 1.2), (1.6, -1.2), (-1.6, -1.2))):
            r.node("W%d" % i, mesh=r.mesh("W%d" % i, at=(-0.5, -0.5, -0.5), material=ma), translation=[x, 0.0, z], scale=[0.8 if i < 2 else 0.6, 0.8 if i < 2 else 0.6, 0.25])
        r.node("Belt", mesh=r.mesh("Belt", at=(-0.5, -0.5, -0.5), material=ma), translation=[0.0, -0.3, 1.25], scale=[4.0, 0.1, 0.2])
        parts = [("Hull", 0, {})] + [("W%d" % i, 1, {}) for i in range(4)] + [("Belt", 6, {})]
        if mid:   # road wheels between the carriers on each side
            for i, z in ((4, 1.2), (5, -1.2)):
                r.node("W%d" % i, mesh=r.mesh("W%d" % i, at=(-0.5, -0.5, -0.5), material=ma), translation=[0.0, -0.1, z], scale=[0.5, 0.5, 0.25])
                parts.append(("W%d" % i, 1, {}))
        if anim:  # an animated body part: frame_set moves it, and nothing else in the job evaluates it
            an = r.node("Mast", mesh=r.mesh("Mast", at=(-0.2, 1.0, -0.2), size=0.4, material=ma))
            ak = r.anim("Sway"); r.key(ak, an, "translation", [0.0, 2.0], [(0.0, 0.0, 0.0), (0.3, 0.0, 0.0)])
            parts.append(("Mast", 19, {}))   # PRESERVE: its own mesh through the join, so it keeps its action (a body part joins into the hull and loses it)
        recipe(out, key, r.write(models, key), parts, sub=sub, **fields)
    wheeled("fx_e_rock", waveEnabled=True, rockDegrees=4.0, rockFrames=24, rockAxisChoice=1, rockHeading=30.0, rockPitchDeg=3.0,
            rockPitchCycles=3, rockRollCycles=2, rockPitchPhase=45.0, tracksStatic=True)
    wheeled("fx_e_speeds", sub="recipes_exit", mid=True, anim=True, tracksStatic=True)
    wheeled("fx_e_rockdefault", sub="recipes_exit", waveEnabled=True, rockDegrees=3.0, rockFrames=24, rockAxisChoice=2, tracksStatic=True)
    # fx_e_ground: a 10-long, 1-high hull, two big wheels on the ground, a small one at 0.07 above it (under the undeformed band 0.15);
    # the rock pitches 3 degrees (full at the clip's end, phase 90): the deformed bounds drop the band below the small wheel
    r = Rig(); ma = r.material("Paint", (0.5, 0.5, 0.5))
    r.node("Hull", mesh=r.mesh("Hull", at=(-0.5, 0.0, -0.5), material=ma), scale=[10.0, 1.0, 2.0])
    r.node("Big0", mesh=r.mesh("Big0", at=(-0.5, 0.0, -0.5), material=ma), translation=[3.0, 0.0, 1.2], scale=[0.8, 0.8, 0.2])
    r.node("Big1", mesh=r.mesh("Big1", at=(-0.5, 0.0, -0.5), material=ma), translation=[-3.0, 0.0, 1.2], scale=[0.8, 0.8, 0.2])
    r.node("Small", mesh=r.mesh("Small", at=(-0.5, 0.0, -0.5), material=ma), translation=[0.0, 0.07, 1.2], scale=[0.3, 0.3, 0.2])
    recipe(out, "fx_e_ground", r.write(models, "fx_e_ground"), [("Hull", 0, {}), ("Big0", 1, {}), ("Big1", 1, {}), ("Small", 1, {})],
           waveEnabled=True, rockDegrees=0.5, rockPitchDeg=3.0, rockFrames=15, rockPitchPhase=90.0)
    # fx_e_rock2: the hull longer along Blender Y (glTF z), heading 0, roll 2 / pitch 4 cycles over rock frames 10 under a 30-frame Spin
    r = Rig(); ma = r.material("Paint", (0.5, 0.5, 0.5))
    r.node("Hull", mesh=r.mesh("Hull", at=(-0.5, 0.0, -0.5), material=ma), scale=[2.0, 1.0, 6.0])
    for i, z in enumerate((2.0, -2.0)):
        r.node("W%d" % i, mesh=r.mesh("W%d" % i, at=(-0.5, -0.5, -0.5), material=ma), translation=[1.3, 0.3, z], scale=[0.2, 0.6, 0.6])
    recipe(out, "fx_e_rock2", r.write(models, "fx_e_rock2"), [("Hull", 0, {}), ("W0", 1, {}), ("W1", 1, {})],
           frames=30, waveEnabled=True, rockDegrees=4.0, rockPitchDeg=2.0, rockFrames=10, rockRollCycles=2, rockPitchCycles=4, rockPitchPhase=0.01)
    # fx_e_rock3: a hull and nothing else - the rock, no wheels; the auto axis turned by 15 degrees
    r = Rig(); ma = r.material("Paint", (0.5, 0.5, 0.5))
    r.node("Hull", mesh=r.mesh("Hull", at=(-0.5, 0.0, -0.5), material=ma), scale=[5.0, 1.0, 2.0])
    recipe(out, "fx_e_rock3", r.write(models, "fx_e_rock3"), [("Hull", 0, {})], waveEnabled=True, rockDegrees=2.0, rockFrames=20, rockHeading=15.0)
    wheeled("fx_e_long", sub="recipes_exit", tracksStatic=True)
    # fx_r_missing_rotor: a tail rotor name the scene has not - find()'s exit in stage 3, after the main rotor measured
    r = Rig(); ma = r.material("Paint", (0.4, 0.45, 0.3))
    r.node("Hull", mesh=r.mesh("Hull", at=(-1.0, 0.0, -1.0), size=2.0, material=ma))
    r.node("Disc", mesh=r.mesh("Disc", at=(-0.5, -0.5, -0.5), material=ma), translation=[0.0, 2.5, 0.0], scale=[3.0, 0.05, 3.0])
    recipe(out, "fx_r_missing_rotor", r.write(models, "fx_r_missing_rotor"), [("Hull", 0, {}), ("Disc", 8, {}), ("GhostFan", 9, {})], sub="recipes_exit")
    # fx_r_tail_args: the tail's own axis and trim dials as the Lab never writes them (post-edited by --exits)
    r = Rig(); ma = r.material("Paint", (0.4, 0.45, 0.3))
    r.node("Hull", mesh=r.mesh("Hull", at=(-1.0, 0.0, -1.0), size=2.0, material=ma))
    r.node("Disc", mesh=r.mesh("Disc", at=(-0.5, -0.5, -0.5), material=ma), translation=[0.0, 2.5, 0.0], scale=[3.0, 0.05, 3.0])
    r.node("Fan", mesh=r.mesh("Fan", at=(-0.5, -0.5, -0.5), material=ma), translation=[6.0, 1.5, 0.3], scale=[0.8, 0.8, 0.04])
    recipe(out, "fx_r_tail_args", r.write(models, "fx_r_tail_args"), [("Hull", 0, {}), ("Disc", 8, {}), ("Fan", 9, {})], sub="recipes_exit")
    # names files as Python reads them: a line wrapped in \x1c..\x1f (str.strip() takes them), and one starting with a BOM (kept: the
    # wheel is not found - the exit)
    io_w = os.path.join(models, "names_ws.txt"); open(io_w, "w", encoding="utf-8", newline="\n").write("\x1cTire\x1f\n")
    io_b = os.path.join(models, "names_bom.txt"); open(io_b, "w", encoding="utf-8", newline="\n").write("\ufeffTire\n")
    for key in ("fx_r_names_ws", "fx_r_names_bom", "fx_d_names_empty", "fx_d_names_dir", "fx_d_dial_before_names"):
        r = Rig(); ma = r.material("Steel", (0.4, 0.4, 0.45))
        r.node("Hull", mesh=r.mesh("Hull", material=ma)); r.node("Tire", mesh=r.mesh("Tire", at=(3.0, 0.0, 0.0), material=ma))
        recipe(out, key, r.write(models, key), [("Hull", 0, {}), ("Tire", 1, {})], sub="recipes_exit")
    # the missing-part exits in stage 3 (find): a turret name, a trail name the scene has not
    for key, role in (("fx_r_missing_turret", 2), ("fx_r_missing_trail", 10)):
        r = Rig(); ma = r.material("Steel", (0.4, 0.4, 0.45))
        r.node("Hull", mesh=r.mesh("Hull", material=ma)); r.node("Part", mesh=r.mesh("Part", at=(3.0, 0.0, 0.0), material=ma))
        recipe(out, key, r.write(models, key), [("Hull", 0, {}), ("Part", role, {}), ("Ghost", role, {})], sub="recipes_exit")
    # The dials read among the names, plus tagged-file failures. Invalid UTF-8 escapes from positional namelist(),
    # but the reduce tags catch it and print VEHICLE ERROR. Earlier positional/tag errors must win over later files.
    for key in ("fx_d_trail_frames", "fx_d_gun_pivot", "fx_x_default_names_dir", "fx_x_shroud_names_dir", "fx_x_window_names_dir",
                "fx_d_dial_before_tag_names", "fx_x_tag_before_bad_names", "fx_d_pos_names_utf8", "fx_x_tag_names_utf8"):
        r = Rig(); ma = r.material("Steel", (0.4, 0.4, 0.45))
        r.node("Hull", mesh=r.mesh("Hull", material=ma)); r.node("Tire", mesh=r.mesh("Tire", at=(3.0, 0.0, 0.0), material=ma))
        recipe(out, key, r.write(models, key), [("Hull", 0, {}), ("Tire", 1, {})], sub="recipes_exit")
    with open(os.path.join(models, "names_bad_utf8.txt"), "wb") as f:
        f.write(b"\xff")
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
    "fx_x_default_names_dir": ("EXIT", lambda a: set_tag(a, "defaultreduce", "@" + os.path.dirname(a[1]).replace("\\", "/") + "|20")),
    "fx_x_shroud_names_dir": ("EXIT", lambda a: set_tag(a, "shroudreduce", "@" + os.path.dirname(a[1]).replace("\\", "/") + "|20")),
    "fx_x_window_names_dir": ("EXIT", lambda a: set_tag(a, "windowreduce", "@" + os.path.dirname(a[1]).replace("\\", "/") + "|20")),
    "fx_d_dial_before_tag_names": ("DIES", lambda a: (a.__setitem__(34, "bad pivot"), set_tag(a, "shroudreduce", "@" + os.path.join(os.path.dirname(a[1]), "names_bad_utf8.txt").replace("\\", "/") + "|20"))),
    "fx_x_tag_before_bad_names": ("EXIT", lambda a: (set_tag(a, "defaultreduce", "nopipe"), set_tag(a, "shroudreduce", "@" + os.path.join(os.path.dirname(a[1]), "names_bad_utf8.txt").replace("\\", "/") + "|20"))),
    "fx_d_pos_names_utf8": ("DIES", lambda a: a.__setitem__(4, "@" + os.path.join(os.path.dirname(a[1]), "names_bad_utf8.txt").replace("\\", "/"))),
    "fx_x_tag_names_utf8": ("EXIT", lambda a: set_tag(a, "shroudreduce", "@" + os.path.join(os.path.dirname(a[1]), "names_bad_utf8.txt").replace("\\", "/") + "|20")),
    # None leaves the job positive: only its argument text is changed after the Lab's builder runs.
    "fx_r_numeric_pct": (None, lambda a: a.__setitem__(54, "0.500000000000000055511151231257827021181583404541015625000000000000000000001")),
    "fx_r_flat_sail": (None, lambda a: set_tag(a, "flagfold", "|")),
    "fx_d_sailfold_overflow": ("DIES", lambda a: a.__setitem__(72, "1e309")),
    "fx_d_unicode_fold": ("DIES", lambda a: (a.__setitem__(72, "\U00011f51"), a.__setitem__(73, "later"))),
    "fx_d_chunk_splice": ("DIES", lambda a: None),
    "fx_r_names_ws": (None, lambda a: a.__setitem__(4, "@" + os.path.join(os.path.dirname(a[1]), "names_ws.txt").replace("\\", "/"))),
    "fx_r_names_bom": ("EXIT", lambda a: a.__setitem__(4, "@" + os.path.join(os.path.dirname(a[1]), "names_bom.txt").replace("\\", "/"))),
    "fx_d_names_empty": ("DIES", lambda a: a.__setitem__(4, "@")),
    "fx_d_names_dir": ("DIES", lambda a: a.__setitem__(4, "@" + os.path.dirname(a[1]).replace("\\", "/"))),
    "fx_d_dial_before_names": ("DIES", lambda a: (a.__setitem__(45, "x"), a.__setitem__(63, "@" + os.path.join(os.path.dirname(a[1]), "absent.txt").replace("\\", "/")))),
    "fx_r_gunpoint": ("DIES", lambda a: None),
    "fx_r_missing_turret": ("EXIT", lambda a: None),
    "fx_r_missing_trail": ("EXIT", lambda a: None),
    "fx_d_trail_frames": ("DIES", lambda a: a.__setitem__(33, "nan")),
    "fx_d_gun_pivot": ("DIES", lambda a: a.__setitem__(34, "x")),
    "fx_r_missing_wheel": ("EXIT", lambda a: None),
    "fx_r_missing_rotor": ("EXIT", lambda a: None),
    "fx_e_speeds": (None, lambda a: (a.__setitem__(13, "1.5"), a.__setitem__(14, "0.5"))),
    "fx_e_rockdefault": (None, lambda a: a.__setitem__(18, "")),
    "fx_e_long": (None, lambda a: a.__setitem__(7, "200000")),
    "fx_r_oars_inf": ("DIES", lambda a: a.__setitem__(47, "inf")),
    "fx_r_oars_args": (None, lambda a: (a.__setitem__(47, "nan"), a.__setitem__(58, "nan"), a.__setitem__(59, "inf"))),
    "fx_r_tail_args": (None, lambda a: (a.__setitem__(28, "x"), a.__setitem__(29, "-inf"), a.__setitem__(30, "nan"))),
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
STAGE_OF = {"fx_r_oars_inf": 4, "fx_d_chunk_splice": 2, "fx_r_missing_wheel": 3, "fx_r_missing_rotor": 3, "fx_r_missing_turret": 3, "fx_r_missing_trail": 3, "fx_r_names_bom": 3, "fx_r_gunpoint": 4}


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
