#!/usr/bin/env python
"""The Workshop advisor's corpus: one table of every part the user has judged, with the features the probe sees.

Phase 0 of docs/Workshop-Advisor-Plan.md. The user's decisions live in three places beside the models and recipes:

    <file>.glb.marks.txt   S = split, T = tear, X = delete           (the Cutter and the Fuser, per part at that file)
    <file>.glb.fuse.txt    <letter>|<node index>|<name>, #name|L|..  (the Fuser's groups, per part at that file)
    <out>.fuse-report.txt  which parts each letter fused into which 'Fused_L_<first>' part (what was actually fused)
    Recipes/*.json         per part: role, vis, flip, bone, placement; per model: the reduce dials  (the Vehicle Lab)

A model goes raw -> (cut) -> _split -> _split_fused -> recipe; a decision is made AT a stage file and the join is by
node index and unique name exactly as the Workshop resolves a sidecar (EditorRules.MigrateSidecarNames /
ResolveFuseSidecar): a line applies to the node at its index when that node still carries the name, else by name where
the name is unique, else it is refused and reported. Node names are made unique first, as the Cutter does
(GlbDisconnectedParts.UniqueNodeNames: the first of a name keeps it, the next become Name_2, Name_3 ... in node order,
never colliding with a name already in the file).

The features are computed here from the GLB with no dependency beyond the standard library: vertex and triangle
counts, islands (exact-position weld plus index connectivity; the probe welds at a few ulps, so a mesh with near-
coincident seams can count more islands here than in the Lab), the world-space bounds. `verts` counts the distinct
vertices a part's primitives reference (a blocked mesh, which has no triangles to reference them with, keeps its
POSITION accessor's count, as the Workshop's MeasureNode does), which is the Lab's probe count: the five recipes on _split files (Khelandion,
Bremen, Teutonic, Triconter, Sikorsky; 675 parts) agree with it to the vertex, while the Fuser's report sums whole
accessors and reads higher for split children. The escape-ray visibility and
the inside-out verdict are NOT recomputed; they are taken from the recipe where one covers the file.

    python tools/advisor/corpus.py [--models D:/3DModels] [--recipes <dir>] [--out tools/advisor/out] [--no-cache]

Writes parts.csv (a row per mesh node per decision-bearing file), models.csv (a row per model folder), join_report.txt
(every refused sidecar line, every recipe part no node matched, every disagreement between a sidecar and the fuse
report). Exit 0; 1 on a malformed input the script cannot step over. Per-file features are cached in the output
folder keyed by path, size and nanosecond mtime; --no-cache recomputes.
"""
import argparse
import csv
import glob
import hashlib
import io
import json
import math
import os
import re
import struct
import sys
import time
from array import array
from collections import Counter, defaultdict

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
import recipe_migrate  # noqa: E402  (ROLE_NAMES and the fuse-report parser are shared with the migration tool)

ROLE_NAMES = recipe_migrate.ROLE_NAMES
DEFAULT_MODELS = "D:/3DModels"
DEFAULT_RECIPES = "C:/Repo/ENCReload/Assets/FactorySource/VehicleLab/Recipes"

# EXPLICIT CATEGORIES (the corpus is two thirds ships; every metric is read per category, so the category is a column).
# Marked by hand, folder by folder, never guessed from a name: a folder not listed here is 'unknown' and named in the
# join report so it gets added, not silently binned.
CATEGORY = {
    "ship": ["CoastalMonitor", "Espana", "Khalandion", "Peddle Gunboat", "Peddle Steam Boat", "Protected Cruiser", "RMS Teutonic",
             "RobertsHeavyMonitor", "SS Romanic", "SiriusLightCruiser", "Steam Frigate", "Steam Ship Of The Line", "OceanLiner",
             "Triconter", "Dugout Canoe", "Great Eastern", "EarlySteamer", "SlavaMissileCruiser", "SubmarineDiver"],
    "armour": ["Abrams", "Bradley", "Heavy Tiger", "Jagdpanzer Tank Destroyer", "SU122", "T-62", "Tiger Tank", "Tiger",
               "Armored Car Ehnhardt", "Lancia IZ armored car", "Rolls-Royce armoured car", "HalftrackGun", "m901"],
    "aircraft": ["Bell H-13", "F-117 Nighthawk", "F-86F Saber", "F15 Eagle", "GlobalHawk", "Mig-15", "Rah66", "U2", "WW1Bomber", "Zeppelin"],
    "gun": ["Ballista", "Cannon Gun", "Catapult", "GattlingGun", "Ozhiga Organ Gun", "Ribauldequin", "TOW Launcher", "Towed Howitzer", "NuclearMissile"],
    "mech": ["FreeBattleMech", "Light Assault Mech", "Mecha", "Abomination"],
    "creature": ["Bear", "GrayWolf", "Lion", "SabertoothTiger"],
    "building": ["Beech Tree", "Greek Temple", "Nuclear Power Plant", "Space Nuclear Power"],
    "other": ["Nieuwe map", "_TranslationTest"],
}
FOLDER_CATEGORY = {f: c for c, fs in CATEGORY.items() for f in fs}

PART_COLUMNS = ["model", "category", "file", "stage", "node_index", "name", "file_name", "mesh_index", "mesh_shared", "parent_index",
                "depth", "verts", "tris", "islands", "islands_merged", "island_top_pct", "island_second_pct", "islands_big",
                "cx", "cy", "cz", "sx", "sy", "sz", "blocked", "triangle_free",
                "mark", "auto_mark", "fuse_letter", "fuse_group_name", "fused_into", "fused_letter",
                "role", "role_id", "vis", "flip", "bone", "placed", "recipe", "recipe_verts", "recipe_name", "name_join", "role_via_fuse", "recipe_via_fuse"]
MODEL_COLUMNS = ["model", "category", "files", "recipe", "recipe_src", "recipe_src_stage", "recipe_parts", "recipe_parts_matched", "dials"]


# ---------------------------------------------------------------- GLB reading (standard library only)

COMPONENT = {5120: ("b", 1), 5121: ("B", 1), 5122: ("h", 2), 5123: ("H", 2), 5125: ("I", 4), 5126: ("f", 4)}
TYPE_COUNT = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT2": 4, "MAT3": 9, "MAT4": 16}


class Glb:
    """A parsed GLB: the JSON root and the first binary chunk."""

    def __init__(self, data):
        if len(data) < 12 or data[:4] != b"glTF":
            raise ValueError("not a GLB (no glTF magic)")
        version, length = struct.unpack_from("<II", data, 4)
        if version != 2:
            raise ValueError("GLB version %d, only 2 is read" % version)
        off = 12
        self.root = None
        self.bin = b""
        while off + 8 <= min(length, len(data)):
            clen, ctype = struct.unpack_from("<II", data, off)
            chunk = data[off + 8: off + 8 + clen]
            if ctype == 0x4E4F534A and self.root is None:
                self.root = json.loads(chunk.decode("utf-8"))
            elif ctype == 0x004E4942 and not self.bin:
                self.bin = chunk
            off += 8 + clen
        if self.root is None:
            raise ValueError("GLB without a JSON chunk")
        self.nodes = self.root.get("nodes") or []
        self.meshes = self.root.get("meshes") or []
        self.accessors = self.root.get("accessors") or []
        self.views = self.root.get("bufferViews") or []

    def accessor_array(self, index):
        """The accessor's values as an array (flat, componentCount per element) and the component count.

        Returns (None, why) for a layout this reader does not handle (sparse, out-of-range, a missing bufferView)."""
        if index < 0 or index >= len(self.accessors):
            return None, "accessor %d out of range" % index
        acc = self.accessors[index]
        if "sparse" in acc:
            return None, "sparse accessor"
        if "bufferView" not in acc:
            return None, "accessor without a bufferView"
        if acc["componentType"] not in COMPONENT or acc.get("type") not in TYPE_COUNT:
            return None, "component type %s / %s" % (acc.get("componentType"), acc.get("type"))
        code, size = COMPONENT[acc["componentType"]]
        ncomp = TYPE_COUNT[acc["type"]]
        count = int(acc.get("count", 0))
        view = self.views[acc["bufferView"]]
        if view.get("buffer", 0) != 0:
            return None, "a second buffer"
        start = int(view.get("byteOffset", 0)) + int(acc.get("byteOffset", 0))
        stride = int(view.get("byteStride", 0)) or size * ncomp
        elem = size * ncomp
        end = start + (count - 1) * stride + elem if count else start
        if end > len(self.bin):
            return None, "accessor past the end of the binary chunk"
        out = array(code)
        if stride == elem:
            out.frombytes(self.bin[start:end])
        else:
            for i in range(count):
                out.frombytes(self.bin[start + i * stride: start + i * stride + elem])
        if sys.byteorder != "little":
            out.byteswap()
        return out, ncomp


def unique_node_names(nodes):
    """The Cutter's renaming (GlbDisconnectedParts.UniqueNodeNames): {node index: unique name} for every mesh node.

    The first of a name keeps it; the next become Name_2, Name_3 ... in node order, never a name already in the file."""
    taken = set(n.get("name") for n in nodes if isinstance(n, dict) and n.get("name"))
    seen = set()
    unique = {}
    for i, n in enumerate(nodes):
        if not isinstance(n, dict) or "mesh" not in n:
            continue
        name = n.get("name") or ("node %d" % i)
        if not n.get("name"):
            unique[i] = name
            continue
        if name not in seen:
            seen.add(name)
            unique[i] = name
            continue
        suffix = 2
        while (name + "_%d" % suffix) in taken:
            suffix += 1
        fresh = name + "_%d" % suffix
        taken.add(fresh)
        seen.add(fresh)
        unique[i] = fresh
    return unique


def node_world_matrices(nodes):
    """Column-major 4x4 world matrices per node (glTF: a node's matrix or its TRS, parents first)."""
    parent = {}
    for i, n in enumerate(nodes):
        for c in (n.get("children") or []) if isinstance(n, dict) else []:
            parent.setdefault(c, i)

    def local(n):
        if "matrix" in n:
            return list(n["matrix"])
        t = n.get("translation", [0.0, 0.0, 0.0])
        q = n.get("rotation", [0.0, 0.0, 0.0, 1.0])
        s = n.get("scale", [1.0, 1.0, 1.0])
        x, y, z, w = q
        r = [1 - 2 * (y * y + z * z), 2 * (x * y + z * w), 2 * (x * z - y * w),
             2 * (x * y - z * w), 1 - 2 * (x * x + z * z), 2 * (y * z + x * w),
             2 * (x * z + y * w), 2 * (y * z - x * w), 1 - 2 * (x * x + y * y)]   # column-major 3x3
        m = [r[0] * s[0], r[1] * s[0], r[2] * s[0], 0.0,
             r[3] * s[1], r[4] * s[1], r[5] * s[1], 0.0,
             r[6] * s[2], r[7] * s[2], r[8] * s[2], 0.0,
             t[0], t[1], t[2], 1.0]
        return m

    def mul(a, b):   # a * b, both column-major
        return [sum(a[r + 4 * k] * b[4 * c + k] for k in range(4)) for c in range(4) for r in range(4)]

    world = {}
    depth = {}
    for i in range(len(nodes)):
        # the chain to the root, iteratively: a self-child or a two-node cycle is a malformed file to report, not a
        # RecursionError that kills the whole export (review of this file)
        chain = []
        j = i
        seen = set()
        while j not in world:
            if j in seen:
                raise ValueError("node cycle through node %d" % j)
            seen.add(j)
            chain.append(j)
            if j not in parent:
                break
            j = parent[j]
        for k in reversed(chain):
            n = nodes[k] if isinstance(nodes[k], dict) else {}
            m = local(n)
            if k in parent:
                m = mul(world[parent[k]], m)
                depth[k] = depth[parent[k]] + 1
            else:
                depth[k] = 0
            world[k] = m
    return world, parent, depth


IDENTITY = [1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0]


class Dsu:
    __slots__ = ("p",)

    def __init__(self, n):
        self.p = list(range(n))

    def find(self, a):
        p = self.p
        while p[a] != a:
            p[a] = p[p[a]]
            a = p[a]
        return a

    def union(self, a, b):
        ra, rb = self.find(a), self.find(b)
        if ra != rb:
            self.p[rb] = ra


def analyse_mesh(glb, mesh_index):
    """verts, tris, islands, local bounds (min, max), and the unique positions - or a 'blocked' reason."""
    mesh = glb.meshes[mesh_index] if 0 <= mesh_index < len(glb.meshes) else None
    if not isinstance(mesh, dict):
        return {"blocked": "mesh %d out of range" % mesh_index}
    prims = mesh.get("primitives") or []
    ids = {}
    unique = []
    dsu_parent = []
    tri_ids = []
    verts = tris = 0
    lo = [math.inf] * 3
    hi = [-math.inf] * 3
    blocked = None
    # THE PROBE'S PRIMITIVE RULE (GlbDisconnectedParts.AnalyzeMesh): a primitive that is not TRIANGLES (mode 4) throws
    # "Only TRIANGLES primitives can be split safely." and the whole mesh is Blocked - no triangles, no islands. A
    # line or point mesh counted here instead made every vertex its own island (review of this file: 1,246 real rows,
    # the Espana's 787 rigging lines among them, every one a false split candidate).
    modes = [p.get("mode", 4) for p in prims if isinstance(p, dict)]
    triangle_free = bool(modes) and all(isinstance(m, int) and 0 <= m <= 3 for m in modes)
    if any(m != 4 for m in modes):
        pos_count = 0
        for p in prims:
            if isinstance(p, dict) and isinstance(p.get("attributes"), dict) and "POSITION" in p["attributes"]:
                arr, nc = glb.accessor_array(p["attributes"]["POSITION"])
                pos_count += len(arr) // 3 if arr is not None and nc == 3 else 0
        return {"verts": pos_count, "tris": 0, "islands": 0, "lo": [0.0] * 3, "hi": [0.0] * 3, "unique": [],
                "blocked": "Only TRIANGLES primitives can be split safely.", "triangle_free": triangle_free}
    for prim in prims:
        if not isinstance(prim, dict):
            continue
        attrs = prim.get("attributes") or {}
        if not isinstance(attrs, dict) or "POSITION" not in attrs:
            continue
        pos, ncomp = glb.accessor_array(attrs["POSITION"])
        if pos is None:
            blocked = ncomp
            continue
        if ncomp != 3:
            blocked = "POSITION is not VEC3"
            continue
        n = len(pos) // 3
        mode = prim.get("mode", 4)
        if "indices" in prim:
            idx, _ = glb.accessor_array(prim["indices"])
            if idx is None:
                blocked = _
                continue
            idx = list(idx)
        else:
            idx = list(range(n))
        # ONLY THE VERTICES THE PRIMITIVE REFERENCES: a Workshop Split writes every child of a part against the parent's
        # whole POSITION accessor and subsets the indices (lossless), so a child's accessor holds hundreds of vertices
        # it never draws - counted, they are singleton islands and the part's bounds are the parent's.
        referenced = sorted(set(i for i in idx if 0 <= i < n))
        if any(i >= n or i < 0 for i in idx):
            blocked = "index past the vertex count"
        verts += len(referenced)
        raw = pos.tobytes()
        local_ids = {}
        for i in referenced:
            key = raw[i * 12: i * 12 + 12]
            vid = ids.get(key)
            if vid is None:
                vid = len(unique)
                ids[key] = vid
                unique.append((pos[3 * i], pos[3 * i + 1], pos[3 * i + 2]))
                dsu_parent.append(vid)
            local_ids[i] = vid
            x, y, z = pos[3 * i], pos[3 * i + 1], pos[3 * i + 2]
            if x < lo[0]: lo[0] = x
            if x > hi[0]: hi[0] = x
            if y < lo[1]: lo[1] = y
            if y > hi[1]: hi[1] = y
            if z < lo[2]: lo[2] = z
            if z > hi[2]: hi[2] = z
        if mode == 4:
            tri_list = [(idx[k], idx[k + 1], idx[k + 2]) for k in range(0, len(idx) - 2, 3)]
        elif mode == 5:
            tri_list = [(idx[k], idx[k + 1], idx[k + 2]) for k in range(len(idx) - 2)]
        elif mode == 6:
            tri_list = [(idx[0], idx[k], idx[k + 1]) for k in range(1, len(idx) - 1)]
        else:
            tri_list = []   # points and lines: no triangles, no connectivity
        tris += len(tri_list)
        dsu = Dsu(0)
        dsu.p = dsu_parent
        find, union = dsu.find, dsu.union
        for a, b, c in tri_list:
            if a >= n or b >= n or c >= n:
                blocked = "index past the vertex count"
                break
            va, vb, vc = local_ids[a], local_ids[b], local_ids[c]
            union(va, vb)
            union(vb, vc)
            tri_ids.append((va, vb, vc))
        dsu_parent = dsu.p
    if verts == 0:
        return {"verts": 0, "tris": 0, "islands": 1 if blocked is None else 0, "lo": [0.0] * 3, "hi": [0.0] * 3, "unique": [], "blocked": blocked,
                "triangle_free": triangle_free, "islands_merged": 1 if blocked is None else 0, "island_top_pct": 100.0, "island_second_pct": 0.0, "islands_big": 1 if blocked is None else 0}
    dsu = Dsu(0)
    dsu.p = dsu_parent
    roots = set(dsu.find(i) for i in range(len(unique)))
    merged = merged_island_tris(unique, tri_ids, dsu, lo, hi, MERGE_FRACTION) if len(roots) > 1 and tris > 0 else [tris]
    merged.sort(reverse=True)
    total = float(sum(merged)) or 1.0
    return {"verts": verts, "tris": tris, "islands": len(roots), "lo": lo, "hi": hi, "unique": unique, "blocked": blocked, "triangle_free": triangle_free,
            "islands_merged": len(merged), "island_top_pct": 100.0 * merged[0] / total if merged else 0.0,
            "island_second_pct": 100.0 * merged[1] / total if len(merged) > 1 else 0.0,
            "islands_big": sum(1 for t in merged if t >= 0.01 * total)}


# THE WORKSHOP'S DISTANCE MERGE (GlbDisconnectedParts.AnalyzeMesh, "DISTANCE MERGE (2026-09-06, the 602-island rope)"):
# the row the user reads says how many islands a part has AFTER islands within 1 % of the mesh's own diagonal (the merge
# slider's default) have fused back together, so a segmented rope is one part and only genuinely distant junk
# separates. Ported here so a proposer sees the number the user saw: components in the probe's order, a vertex-distance
# grid of cells eps wide, two elongated islands (aspect over 3 by the principal axes) chaining only when colinear, a
# pair budget of 4,096 failed comparisons after which a pair stays separate. The base islands are this file's
# exact-position weld where the probe welds at a few ulps; no oracle for the merged count exists outside the editor.
MERGE_FRACTION = 0.01
PAIR_BUDGET = 4096


def principal_axis(points, lo, hi):
    """(unit direction of the first principal component, aspect = sqrt(var1 / var2)) as Component.PrincipalAxes."""
    n = max(1, len(points))
    mx = sum(p[0] for p in points) / n
    my = sum(p[1] for p in points) / n
    mz = sum(p[2] for p in points) / n
    c = [[0.0] * 3 for _ in range(3)]
    for x, y, z in points:
        d = (x - mx, y - my, z - mz)
        for a in range(3):
            for b in range(3):
                c[a][b] += d[a] * d[b]

    def power(sx, sy, sz, ox, oy, oz):
        ortho = ox != 0 or oy != 0 or oz != 0
        vx, vy, vz = sx, sy, sz
        if ortho:
            sd = vx * ox + vy * oy + vz * oz
            vx -= sd * ox; vy -= sd * oy; vz -= sd * oz
            if vx * vx + vy * vy + vz * vz < 1e-18:
                if abs(ox) <= abs(oy) and abs(ox) <= abs(oz):
                    vx, vy, vz = 0.0, -oz, oy
                elif abs(oy) <= abs(oz):
                    vx, vy, vz = oz, 0.0, -ox
                else:
                    vx, vy, vz = -oy, ox, 0.0
        ln = math.sqrt(vx * vx + vy * vy + vz * vz)
        if ln < 1e-12:
            vx, vy, vz, ln = 1.0, 0.0, 0.0, 1.0
        vx, vy, vz = vx / ln, vy / ln, vz / ln
        for _ in range(24):
            nx = c[0][0] * vx + c[0][1] * vy + c[0][2] * vz
            ny = c[1][0] * vx + c[1][1] * vy + c[1][2] * vz
            nz = c[2][0] * vx + c[2][1] * vy + c[2][2] * vz
            if ortho:
                d = nx * ox + ny * oy + nz * oz
                nx -= d * ox; ny -= d * oy; nz -= d * oz
            l2 = math.sqrt(nx * nx + ny * ny + nz * nz)
            if l2 < 1e-12:
                break
            vx, vy, vz = nx / l2, ny / l2, nz / l2
        rx = c[0][0] * vx + c[0][1] * vy + c[0][2] * vz
        ry = c[1][0] * vx + c[1][1] * vy + c[1][2] * vz
        rz = c[2][0] * vx + c[2][1] * vy + c[2][2] * vz
        return max(0.0, vx * rx + vy * ry + vz * rz), (vx, vy, vz)

    var1, v1 = power(hi[0] - lo[0] + 0.017, hi[1] - lo[1] + 0.013, hi[2] - lo[2] + 0.011, 0.0, 0.0, 0.0)
    var2, _ = power(0.31, 0.68, 0.55, v1[0], v1[1], v1[2])
    return v1, math.sqrt((var1 + 1e-24) / max(var2, 1e-24)), (mx, my, mz)


def merged_island_tris(unique, tri_ids, dsu, lo, hi, fraction):
    """Triangle counts of the islands after the Workshop's distance merge at `fraction` of the mesh diagonal."""
    comps = {}
    for order, (a, b, c) in enumerate(tri_ids):
        root = dsu.find(a)
        comp = comps.get(root)
        if comp is None:
            comp = comps[root] = {"tris": 0, "first": order, "lo": [math.inf] * 3, "hi": [-math.inf] * 3, "points": []}
        comp["tris"] += 1
        for v in (a, b, c):
            p = unique[v]
            comp["points"].append(p)
            for k in range(3):
                if p[k] < comp["lo"][k]: comp["lo"][k] = p[k]
                if p[k] > comp["hi"][k]: comp["hi"][k] = p[k]
    components = sorted(comps.values(), key=lambda cm: (cm["lo"][0], cm["lo"][1], cm["lo"][2], cm["first"]))
    if fraction <= 0 or len(components) <= 1:
        return [cm["tris"] for cm in components]
    diagonal = math.sqrt(sum((hi[k] - lo[k]) ** 2 for k in range(3)))
    eps = diagonal * fraction
    if eps <= 0:
        return [cm["tris"] for cm in components]
    n = len(components)
    cd = Dsu(n)
    elong = [False] * n
    direction = [None] * n
    centre = [None] * n
    for i, cm in enumerate(components):
        pc1, aspect, ctr = principal_axis(cm["points"], cm["lo"], cm["hi"])
        elong[i] = aspect > 3
        if elong[i]:
            direction[i], centre[i] = pc1, ctr

    def direction_ok(i, j):
        if not elong[i] or not elong[j]:
            return True
        di, dj = direction[i], direction[j]
        if abs(di[0] * dj[0] + di[1] * dj[1] + di[2] * dj[2]) < 0.85:
            return False
        sx, sy, sz = centre[j][0] - centre[i][0], centre[j][1] - centre[i][1], centre[j][2] - centre[i][2]
        s2 = sx * sx + sy * sy + sz * sz
        if s2 <= 1e-18:
            return True
        along_i = sx * di[0] + sy * di[1] + sz * di[2]
        along_j = sx * dj[0] + sy * dj[1] + sz * dj[2]
        perp2 = min(s2 - along_i * along_i, s2 - along_j * along_j)
        lateral = 0.35 * eps
        return perp2 <= lateral * lateral

    cells = {}
    spent = {}
    eps2 = eps * eps
    for i, cm in enumerate(components):
        seen = set()
        for p in cm["points"]:
            if p in seen:
                continue
            seen.add(p)
            cx, cy, cz = math.floor(p[0] / eps), math.floor(p[1] / eps), math.floor(p[2] / eps)
            for dx in (-1, 0, 1):
                for dy in (-1, 0, 1):
                    for dz in (-1, 0, 1):
                        by_comp = cells.get((cx + dx, cy + dy, cz + dz))
                        if not by_comp:
                            continue
                        for j, pts in by_comp.items():
                            if cd.find(j) == cd.find(i) or not direction_ok(i, j):
                                continue
                            pk = (i, j) if i < j else (j, i)
                            used_before = spent.get(pk, 0)
                            if used_before > PAIR_BUDGET:
                                continue
                            used = 0
                            fused = False
                            for q in pts:
                                used += 1
                                ddx, ddy, ddz = p[0] - q[0], p[1] - q[1], p[2] - q[2]
                                if ddx * ddx + ddy * ddy + ddz * ddz <= eps2:
                                    cd.union(i, j)
                                    fused = True
                                    break
                            if not fused:
                                spent[pk] = used_before + used
            home = cells.setdefault((cx, cy, cz), {})
            home.setdefault(i, []).append(p)
    merged = {}
    for i, cm in enumerate(components):
        root = cd.find(i)
        merged[root] = merged.get(root, 0) + cm["tris"]
    return list(merged.values())


def transform_bounds(m, mesh_info):
    """World-space centre and size of a mesh under a node matrix: exact over every unique vertex when the matrix is
    not the identity, the local box otherwise."""
    if mesh_info["verts"] == 0:
        return (0.0, 0.0, 0.0), (0.0, 0.0, 0.0)
    if all(abs(m[i] - IDENTITY[i]) < 1e-9 for i in range(16)):
        lo, hi = mesh_info["lo"], mesh_info["hi"]
    else:
        lo = [math.inf] * 3
        hi = [-math.inf] * 3
        for x, y, z in mesh_info["unique"]:
            wx = m[0] * x + m[4] * y + m[8] * z + m[12]
            wy = m[1] * x + m[5] * y + m[9] * z + m[13]
            wz = m[2] * x + m[6] * y + m[10] * z + m[14]
            if wx < lo[0]: lo[0] = wx
            if wx > hi[0]: hi[0] = wx
            if wy < lo[1]: lo[1] = wy
            if wy > hi[1]: hi[1] = wy
            if wz < lo[2]: lo[2] = wz
            if wz > hi[2]: hi[2] = wz
    centre = tuple((lo[i] + hi[i]) / 2 for i in range(3))
    size = tuple(hi[i] - lo[i] for i in range(3))
    return centre, size


def glb_parts(data):
    """Every mesh node of a GLB as a feature dict, in node order; the probe's row set (GlbDisconnectedParts.Analyze)."""
    glb = Glb(data)
    unique = unique_node_names(glb.nodes)
    world, parent, depth = node_world_matrices(glb.nodes)
    mesh_cache = {}
    mesh_users = Counter(n.get("mesh") for n in glb.nodes if isinstance(n, dict) and "mesh" in n)
    rows = []
    for i, n in enumerate(glb.nodes):
        if not isinstance(n, dict) or "mesh" not in n:
            continue
        mi = n["mesh"]
        if mi not in mesh_cache:
            try:
                mesh_cache[mi] = analyse_mesh(glb, mi)
            except (ValueError, struct.error, OverflowError, IndexError) as e:
                mesh_cache[mi] = {"verts": 0, "tris": 0, "islands": 0, "lo": [0.0] * 3, "hi": [0.0] * 3, "unique": [], "blocked": str(e)}
        info = mesh_cache[mi]
        blocked = info.get("blocked")
        if n.get("extensions", {}).get("EXT_mesh_gpu_instancing") is not None:
            blocked = "GPU-instanced node"
        centre, size = transform_bounds(world[i], info) if "unique" in info else ((0.0,) * 3, (0.0,) * 3)
        rows.append({
            "node_index": i, "name": unique[i], "file_name": n.get("name") or ("node %d" % i), "mesh_index": mi,
            "mesh_shared": mesh_users[mi], "parent_index": parent.get(i, -1), "depth": depth[i],
            "verts": info.get("verts", 0), "tris": info.get("tris", 0), "islands": info.get("islands", 0),
            "cx": centre[0], "cy": centre[1], "cz": centre[2], "sx": size[0], "sy": size[1], "sz": size[2],
            "blocked": blocked or "", "triangle_free": int(bool(info.get("triangle_free"))),
            "islands_merged": info.get("islands_merged", 0), "island_top_pct": info.get("island_top_pct", 0.0),
            "island_second_pct": info.get("island_second_pct", 0.0), "islands_big": info.get("islands_big", 0),
        })
    return rows


def auto_marked(row, mark, letter):
    """The Workshop's own first-load deletion (WorkshopRules.ShouldMarkTinyPartForDeletion): a part of two triangles
    or fewer, not blocked unless triangle-free, with no Split, Tear or group of its own. An X on such a part is the
    tool's, not the user's judgement; the table says which."""
    return int(0 <= row["tris"] <= 2 and (not row["blocked"] or row["triangle_free"]) and mark not in ("S", "T") and not letter)


# ---------------------------------------------------------------- sidecars

def read_sidecar(path, skipped=None):
    """(lines [(letter, index or None, name)], group names {letter: name}) of a marks or fuse sidecar (v2, and the
    two legacy layouts the Workshop still reads), as EditorRules.ResolveFuseSidecar reads it: a BOM is dropped
    (File.ReadAllLines), every field is trimmed, a letter must be one of A-Z, and a v2 line without a node index is
    skipped, not applied. Legacy lines keep the whole remainder with index None, so resolution can check both the
    literal name and name|index readings. Lines the Workshop skips are appended to `skipped` when a list is given."""
    lines = []
    names = {}
    v2 = False
    with io.open(path, encoding="utf-8-sig", errors="replace") as f:
        for raw in f:
            line = raw.strip()
            if not line:
                continue
            if line.startswith("#"):
                if line == "#fuse-groups v2":
                    v2 = True
                m = re.match(r"^#name\|([^|]+)\|(.*)$", line)
                if m:
                    names[m.group(1).strip()] = m.group(2).strip()
                continue
            first = line.find("|")
            if first <= 0:
                continue
            letter, rest = line[:first].strip(), line[first + 1:]
            if len(letter) != 1 or not ("A" <= letter <= "Z"):
                if skipped is not None:
                    skipped.append("'%s' skipped: the letter is not one of A-Z" % line)
                continue
            if not rest.strip():
                if skipped is not None:
                    skipped.append("'%s' skipped: nothing after the letter" % line)
                continue
            if v2:
                second = rest.find("|")
                index = int_token(rest[:second]) if second > 0 else None
                if index is not None and rest[second + 1:].strip():
                    lines.append((letter, index, rest[second + 1:].strip()))
                elif skipped is not None:
                    skipped.append("'%s' skipped: a v2 line needs <letter>|<node index>|<name>" % line)
            else:
                # A legacy remainder may be either name|index or one literal name containing a numeric suffix.
                # Keep it whole until the available rows can settle that ambiguity, as ResolveFuseSidecar does.
                lines.append((letter, None, rest.strip()))
    return lines, names


INT_TOKEN = re.compile(r"[+-]?[0-9]+")


def int_token(text):
    """The node index of a sidecar field as int.TryParse(NumberStyles.Integer) reads it: an optional sign and ASCII
    digits, else None. (str.isdigit accepted '--3' and superscript digits and int() then threw - a dead run.)"""
    text = text.strip()
    if not INT_TOKEN.fullmatch(text):
        return None
    # int.TryParse is Int32, while Python accepts arbitrary precision and Unicode digits. Strip leading zeros before
    # converting so even a very long valid zero-padded index avoids Python's integer-string digit limit.
    digits = text.lstrip("+-").lstrip("0") or "0"
    if len(digits) > 10:
        return None
    value = int(digits) * (-1 if text.startswith("-") else 1)
    return value if -2147483648 <= value <= 2147483647 else None


def resolve_sidecar(lines, rows, refused):
    """{node index: letter} as the Workshop resolves a sidecar against the probed rows: by index when the node at the
    index still carries the name (unique or as the file names it), else by unique name where that name is unique,
    else refused with a reason. An index of None denotes a legacy remainder: two different fits are refused."""
    by_index = {r["node_index"]: r for r in rows}
    count = Counter(r["name"] for r in rows)
    by_name = {r["name"]: r for r in rows if count[r["name"]] == 1}
    file_count = Counter(r["file_name"] for r in rows)

    def row_at(index, name):
        at = by_index.get(index) if index is not None else None
        return at if at is not None and name in (at["name"], at["file_name"]) else None

    def unique(name):
        return by_name.get(name) if file_count.get(name, 0) <= 1 else None

    out = {}
    for letter, index, name in lines:
        target = None
        if index is not None:   # v2: the index first, then an unambiguous name
            target = row_at(index, name)
            if target is None:
                target = unique(name)
        else:   # legacy: try both readings; an index settles it only when the whole remainder names no other part
            last = name.rfind("|")
            legacy_index = int_token(name[last + 1:]) if last > 0 else None
            mid = name[:last].strip() if legacy_index is not None else None
            at, whole = row_at(legacy_index, mid), unique(name)
            if at is not None and whole is None:
                target = at
            else:
                fits = {r["node_index"]: r for r in (at, unique(mid), whole) if r is not None}
                if len(fits) > 1:
                    refused.append("%s|%s fits %d different parts (ambiguous legacy name/index)" % (letter, name, len(fits)))
                    continue
                if fits:
                    target = next(iter(fits.values()))
        if target is None:
            refused.append("%s|%s|%s fits no single part" % (letter, "" if index is None else index, name))
            continue
        out[target["node_index"]] = letter
    return out


def stage_of(filename):
    stem = os.path.splitext(os.path.basename(filename))[0].lower()
    if "_fused" in stem:
        return "fused"
    if "split" in stem:
        return "split"
    if "_cut" in stem:
        return "cut"
    return "raw"


def norm_path(p):
    return os.path.normcase(os.path.normpath(p or "")).replace("\\", "/")


# ---------------------------------------------------------------- the export

def load_recipes(recipes_dir, notes=None):
    """[(path, recipe)] for every readable recipe with a parts list; what was skipped, and why, goes to `notes`."""
    out = []
    for f in sorted(glob.glob(os.path.join(recipes_dir, "*.json"))):
        try:
            with io.open(f, encoding="utf-8-sig") as fh:
                j = json.load(fh)
        except (OSError, ValueError) as e:
            if notes is not None:
                notes.append("RECIPE %s: unreadable (%s)" % (os.path.basename(f), e))
            continue
        if not isinstance(j, dict) or not isinstance(j.get("parts"), list):
            if notes is not None:
                notes.append("RECIPE %s: no parts list, skipped (its dials are not in the table)" % os.path.basename(f))
            continue
        out.append((f, j))
    return out


def recipe_dials(j):
    return {k: j[k] for k in sorted(j) if k.endswith("ReducePct")}


# Bump when a feature's definition changes: the cache below is keyed on it, so an old export never serves stale
# features for a file that did not change (the referenced-vertices fix would have been masked by the cache otherwise).
FEATURES_VERSION = 4   # 3: non-TRIANGLES primitives block the mesh; triangle_free carried. 4: the islands after the Workshop's 1 % distance merge


def file_features(path, cache, use_cache, log):
    st = os.stat(path)
    key = hashlib.sha1(("%s|%d|mtime_ns:%d|v%d" % (norm_path(path), st.st_size, st.st_mtime_ns, FEATURES_VERSION)).encode("utf-8")).hexdigest()
    if use_cache and key in cache:
        return cache[key], True
    t0 = time.time()
    with open(path, "rb") as f:
        rows = glb_parts(f.read())
    log("  read %s: %d mesh nodes, %.1f s" % (os.path.basename(path), len(rows), time.time() - t0))
    cache[key] = rows
    return rows, False


def build(models_root, recipes_dir, out_dir, use_cache=True, log=print, folders=None):
    os.makedirs(out_dir, exist_ok=True)
    cache_path = os.path.join(out_dir, "features-cache.json")
    cache = {}
    if use_cache and os.path.exists(cache_path):
        try:
            with io.open(cache_path, encoding="utf-8") as f:
                cache = json.load(f)
        except (OSError, ValueError):
            cache = {}
    report = []
    recipes = load_recipes(recipes_dir, report)
    recipe_by_src = defaultdict(list)
    for f, j in recipes:
        recipe_by_src[norm_path(j.get("srcFile"))].append((f, j))

    part_rows = []
    model_rows = []
    group_diffs = Counter()   # (file, sidecar letter, report letter) -> parts: the sidecar was re-lettered after the fuse
    folders_seen = sorted(d for d in os.listdir(models_root) if os.path.isdir(os.path.join(models_root, d)))
    if folders:
        folders_seen = [d for d in folders_seen if d in folders]
    for folder in folders_seen:
        fdir = os.path.join(models_root, folder)
        category = FOLDER_CATEGORY.get(folder, "unknown")
        if category == "unknown":
            report.append("CATEGORY unknown: folder '%s' is not in corpus.CATEGORY" % folder)
        glbs = sorted(f for f in os.listdir(fdir) if f.lower().endswith(".glb") and not f.lower().endswith("_spin.glb"))
        reports = {}
        for rp in sorted(f for f in os.listdir(fdir) if f.endswith(".fuse-report.txt")):
            try:
                with io.open(os.path.join(fdir, rp), encoding="utf-8", errors="replace") as fh:
                    text = fh.read()
                output, groups = recipe_migrate.parse_report(text)
                src = re.search(r"^source: (.*)$", text, re.M)
                if output and groups and src:
                    reports[norm_path(src.group(1).strip())] = (norm_path(output), groups, rp)
            except OSError as e:
                report.append("REPORT unreadable: %s: %s" % (rp, e))
        # a decision-bearing file: a sidecar beside it, a fuse report naming it as source or output, or a recipe on it
        chosen = []
        for g in glbs:
            path = os.path.join(fdir, g)
            np_ = norm_path(path)
            has = os.path.exists(path + ".marks.txt") or os.path.exists(path + ".fuse.txt") or np_ in reports \
                or np_ in recipe_by_src or any(np_ == o for o, _, _ in reports.values())
            if has:
                chosen.append(path)
        # a recipe belongs to the folder its source is under, at any depth (the Bradley's FBX sits in Bradley/FBX/)
        recipes_here = [(f, j) for f, j in recipes if norm_path(j.get("srcFile", "")).startswith(norm_path(fdir) + "/")]
        if not chosen and not recipes_here:
            model_rows.append({"model": folder, "category": category, "files": "", "recipe": "", "recipe_src": "", "recipe_src_stage": "",
                               "recipe_parts": 0, "recipe_parts_matched": 0, "dials": ""})
            continue
        log("%s (%s): %d file(s), %d recipe(s)" % (folder, category, len(chosen), len(recipes_here)))
        rows_by_file = {}
        for path in chosen:
            try:
                rows, _ = file_features(path, cache, use_cache, log)
            except Exception as e:   # noqa: BLE001 - one malformed file is a report line, never a dead run (review: a 3-value matrix, "mesh": "0", extensions: [] were all fatal)
                report.append("GLB unreadable: %s: %s: %s" % (path, type(e).__name__, e))
                continue
            rows_by_file[norm_path(path)] = rows
            rel = os.path.relpath(path, models_root).replace("\\", "/")
            stage = stage_of(path)
            refused = []
            marks = {}
            if os.path.exists(path + ".marks.txt"):
                lines, _ = read_sidecar(path + ".marks.txt", refused)
                marks = resolve_sidecar(lines, rows, refused)
            letters, group_names = {}, {}
            if os.path.exists(path + ".fuse.txt"):
                lines, group_names = read_sidecar(path + ".fuse.txt", refused)
                letters = resolve_sidecar(lines, rows, refused)
            for why in refused:
                report.append("SIDECAR refused at %s: %s" % (rel, why))
            fused_into, fused_letter = {}, {}
            if norm_path(path) in reports:
                _, groups, rp = reports[norm_path(path)]
                names = {r["name"] for r in rows}
                for letter, fused, members in groups:
                    for m in members:
                        if m not in names:
                            report.append("REPORT %s names '%s', not a part of %s" % (rp, m, rel))
                        if m in fused_into and fused_into[m] != fused:
                            report.append("REPORT %s names '%s' in two groups (%s and %s); the later one is kept" % (rp, m, fused_into[m], fused))
                        fused_into[m] = fused
                        fused_letter[m] = letter
            for r in rows:
                row = dict(r)
                row.update({"model": folder, "category": category, "file": rel, "stage": stage,
                            "mark": marks.get(r["node_index"], ""), "fuse_letter": letters.get(r["node_index"], ""),
                            "auto_mark": auto_marked(r, marks.get(r["node_index"], ""), letters.get(r["node_index"], "")),
                            "fuse_group_name": group_names.get(letters.get(r["node_index"], ""), ""),
                            "fused_into": fused_into.get(r["name"], ""), "fused_letter": fused_letter.get(r["name"], ""),
                            "role": "", "role_id": "", "vis": "", "flip": "", "bone": "", "placed": "", "recipe": "", "recipe_verts": "",
                            "role_via_fuse": "", "recipe_via_fuse": ""})
                if row["fuse_letter"] and row["fused_letter"] and row["fuse_letter"] != row["fused_letter"]:
                    group_diffs[(rel, row["fuse_letter"], row["fused_letter"])] += 1
                part_rows.append(row)
        # recipes: join each to its source file's rows by unique name
        for rf, j in recipes_here:
            src = norm_path(j.get("srcFile"))
            rname = os.path.basename(rf)
            dials = recipe_dials(j)
            matched = 0
            rows = rows_by_file.get(src)
            parts = j.get("parts") or []
            # A TWO-MODEL RECIPE (srcFile2) prefixes the second model's parts with "B_": they are nodes of the other file,
            # carried here with the recipe's own probe fields and named in the report.
            src2 = j.get("srcFile2") or ""
            second = [p for p in parts if src2 and str(p.get("name", "")).startswith("B_")]
            second_ids = set(id(p) for p in second)
            main = [p for p in parts if id(p) not in second_ids]
            if second:
                rel2 = os.path.relpath(src2, models_root).replace("\\", "/") if norm_path(src2).startswith(norm_path(models_root)) else src2
                for p in second:
                    part_rows.append(recipe_only_row(folder, category, rel2, p, rname))
                report.append("RECIPE %s: %d part(s) of the second model (%s) carried with the recipe's probe fields" % (rname, len(second), os.path.basename(src2)))
                matched += len(second)
            if rows is None:
                # an FBX source, or a GLB this run could not read: the recipe's own probe fields stand in for the features
                rel = os.path.relpath(j.get("srcFile", ""), models_root).replace("\\", "/") if j.get("srcFile") else ""
                for p in main:
                    part_rows.append(recipe_only_row(folder, category, rel, p, rname))
                    matched += 1
                if not os.path.exists(j.get("srcFile", "")):
                    report.append("RECIPE %s: source missing: %s" % (rname, j.get("srcFile")))
                elif not j.get("srcFile", "").lower().endswith((".glb", ".gltf")):
                    report.append("RECIPE %s: source is not a GLB (%s), features from the recipe's probe fields only" % (rname, os.path.basename(j.get("srcFile", ""))))
                else:
                    report.append("RECIPE %s: source read failed, features from the recipe's probe fields only" % rname)
            else:
                file_rows = [r for r in part_rows
                             if r["model"] == folder and r["stage"] != "recipe-only" and norm_path(os.path.join(models_root, r["file"])) == src]
                by_name = {r["name"]: r for r in file_rows}

                def take(r, p, how):
                    r.update({"role": ROLE_NAMES.get(p.get("role"), str(p.get("role"))), "role_id": p.get("role", ""), "vis": p.get("vis", ""),
                              "flip": p.get("flip", ""), "bone": p.get("bone", ""), "placed": int(is_placed(p)), "recipe": rname,
                              "recipe_verts": p.get("verts", ""), "recipe_name": p.get("name", ""), "name_join": how})
                    if p.get("verts") not in (None, r["verts"]):
                        report.append("RECIPE %s: '%s' has %s verts in the recipe, %s in the file" % (rname, p.get("name"), p.get("verts"), r["verts"]))

                unmatched = []
                for p in main:
                    r = by_name.get(p.get("name"))
                    if r is None or r.get("recipe"):
                        unmatched.append(p)
                        continue
                    matched += 1
                    take(r, p, "exact")
                if unmatched and len(main) > 2 * max(1, len(file_rows)):
                    # the Lab's probe can split a mesh into its loose parts (minVerts): such a recipe has many parts per node
                    report.append("RECIPE %s: %d parts against %d mesh node(s) in %s - the recipe's parts are not the file's nodes, %d unmatched"
                                  % (rname, len(main), len(file_rows), os.path.basename(j["srcFile"]), len(unmatched)))
                elif unmatched:
                    matched += join_blender_names(unmatched, file_rows, take, rname, os.path.basename(j["srcFile"]), report)
                # the recipe sits on a fused output: carry each fused part's role back to its members at the fuse source
                for src_np, (out_np, groups, rp) in reports.items():
                    if out_np != src:
                        continue
                    roles = {p.get("name"): p for p in parts}
                    for letter, fused, members in groups:
                        p = roles.get(fused)
                        if p is None:
                            report.append("RECIPE %s: fused part '%s' (report %s) has no recipe entry" % (rname, fused, rp))
                            continue
                        for r in part_rows:
                            if r["model"] == folder and norm_path(os.path.join(models_root, r["file"])) == src_np and r["name"] in members:
                                r["role_via_fuse"] = ROLE_NAMES.get(p.get("role"), str(p.get("role")))
                                r["recipe_via_fuse"] = rname
            model_rows.append({"model": folder, "category": category, "files": ";".join(os.path.basename(c) for c in chosen), "recipe": rname,
                               "recipe_src": os.path.basename(j.get("srcFile", "")), "recipe_src_stage": stage_of(j.get("srcFile", "")) if rows is not None else "recipe-only",
                               "recipe_parts": len(parts), "recipe_parts_matched": matched, "dials": json.dumps(dials, sort_keys=True)})
        if not recipes_here:
            model_rows.append({"model": folder, "category": category, "files": ";".join(os.path.basename(c) for c in chosen), "recipe": "",
                               "recipe_src": "", "recipe_src_stage": "", "recipe_parts": 0, "recipe_parts_matched": 0, "dials": ""})

    for (rel, side, rep), n in sorted(group_diffs.items()):
        report.append("GROUP differs at %s: %d part(s) lettered %s in the sidecar fused as %s in the report (the sidecar was re-lettered after the fuse)" % (rel, n, side, rep))
    with io.open(cache_path, "w", encoding="utf-8") as f:
        json.dump(cache, f)
    write_csv(os.path.join(out_dir, "parts.csv"), PART_COLUMNS, part_rows)
    write_csv(os.path.join(out_dir, "models.csv"), MODEL_COLUMNS, model_rows)
    with io.open(os.path.join(out_dir, "join_report.txt"), "w", encoding="utf-8") as f:
        f.write("advisor corpus join report (%s)\n" % time.strftime("%Y-%m-%d %H:%M"))
        f.write("parts: %d rows; models: %d folders, %d with decisions\n" % (len(part_rows), len(model_rows), sum(1 for m in model_rows if m["files"] or m["recipe"])))
        for line in report:
            f.write(line + "\n")
    return part_rows, model_rows, report


def recipe_only_row(folder, category, rel, p, rname):
    """A part row built from the recipe's own probe fields, for a source this run has no node rows of."""
    c, s = p.get("center") or {}, p.get("size") or {}
    return {"model": folder, "category": category, "file": rel, "stage": "recipe-only", "node_index": "", "name": p.get("name", ""),
            "file_name": "", "mesh_index": "", "mesh_shared": "", "parent_index": "", "depth": "",
            "verts": p.get("verts", ""), "tris": "", "islands": "", "cx": c.get("x", ""), "cy": c.get("y", ""), "cz": c.get("z", ""),
            "sx": s.get("x", ""), "sy": s.get("y", ""), "sz": s.get("z", ""), "blocked": "",
            "mark": "", "fuse_letter": "", "fuse_group_name": "", "fused_into": "", "fused_letter": "",
            "role": ROLE_NAMES.get(p.get("role"), str(p.get("role"))), "role_id": p.get("role", ""), "vis": p.get("vis", ""),
            "flip": p.get("flip", ""), "bone": p.get("bone", ""), "placed": int(is_placed(p)), "recipe": rname,
            "recipe_verts": p.get("verts", ""), "recipe_name": p.get("name", ""), "name_join": "recipe-only", "role_via_fuse": "", "recipe_via_fuse": ""}


BLENDER_SUFFIX = re.compile(r"^(.*)\.(\d{3})$")


def join_blender_names(unmatched, file_rows, take, rname, src_name, report):
    """Recipes the Lab probed through Blender name a duplicate node Name.001, Name.002 ... where the Cutter says Name_2,
    Name_3: the same parts in the same node order under another suffix rule. The recipe's parts of one base name, in
    suffix order, are matched to the file's nodes of that name, in node order, when the counts agree; anything else is
    reported as unmatched. Returns the number joined."""
    groups = defaultdict(list)
    for p in unmatched:
        m = BLENDER_SUFFIX.match(str(p.get("name", "")))
        base, order = (m.group(1), int(m.group(2))) if m else (str(p.get("name", "")), -1)
        groups[base].append((order, p))
    joined = 0
    for base, items in groups.items():
        items.sort(key=lambda t: t[0])
        candidates = [r for r in file_rows if (r["file_name"] == base or r["name"] == base) and not r.get("recipe")]
        if items and len(candidates) == len(items):
            for (_, p), r in zip(items, candidates):
                take(r, p, "order")
                joined += 1
        else:
            for _, p in items:
                report.append("RECIPE %s: part '%s' is not a node of %s (stale recipe?)" % (rname, p.get("name"), src_name))
    return joined


def is_placed(p):
    o = p.get("offset") or {}
    s = p.get("scale") or {}
    return any(abs(float(o.get(k, 0.0))) > 1e-5 for k in "xyz") or any(abs(float(s.get(k, 1.0)) - 1.0) > 1e-5 for k in "xyz")


def write_csv(path, columns, rows):
    with io.open(path, "w", encoding="utf-8", newline="") as f:
        w = csv.DictWriter(f, fieldnames=columns, extrasaction="ignore")
        w.writeheader()
        for r in rows:
            w.writerow({k: fmt(r.get(k, "")) for k in columns})


def fmt(v):
    if isinstance(v, float):
        return "%.9g" % v   # float32 positions survive; "%.6g" lost the centimetres of a 1,400-unit centre
    return v


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--models", default=DEFAULT_MODELS)
    ap.add_argument("--recipes", default=DEFAULT_RECIPES)
    ap.add_argument("--out", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "out"))
    ap.add_argument("--no-cache", action="store_true")
    ap.add_argument("--folder", action="append", help="only this model folder (repeatable)")
    a = ap.parse_args(argv)
    if not os.path.isdir(a.models):
        print("models root not found: %s" % a.models)
        return 1
    if not os.path.isdir(a.recipes):
        print("recipes folder not found: %s" % a.recipes)
        return 1
    t0 = time.time()
    parts, models, report = build(a.models, a.recipes, a.out, use_cache=not a.no_cache, folders=set(a.folder) if a.folder else None)
    print("parts.csv: %d rows; models.csv: %d; join_report.txt: %d line(s); %.0f s -> %s" % (len(parts), len(models), len(report), time.time() - t0, a.out))
    return 0


if __name__ == "__main__":
    sys.exit(main())
