"""Fixtures for rig_anim.py (tools/riganim_drill.sh): the branches of the script no registry recipe takes, each a small
rigged GLB with the arguments that reach the branch. Writes the files and prints JOB lines in riganim_jobs.py's form
(TAB-separated: JOB <key> <input> <argv[2]> ... <argv[14]>). A key that starts with "EXIT:<stage>:" names a job the
script is expected to STOP at that stage (its own sys.exit(1), e.g. no armature): the drill fails a different stop.
A key that starts with "LEFT:" names a job the C# leaves to Blender by name (localNodeAnim).

usage: riganim_fixtures.py <output directory>

Stage 1 (step (b)): the import, the material-less drop (a lone one, one with a textured child that becomes a root, one
coloured by COLOR_0 - Blender decides whether that counts as a material), the contract marker (an armature named
DeployArmV2), object-level animation on a mesh node (cleared on the conversion path, kept on the legacy one), several
animations (the first one's action is the active one; a longer second one), a bone-parented mesh, a scaled wrapper
empty, a bone in Euler mode, and the exit when there is no armature. For the stage 1 MODEL (BlenderRigAnim): the vertex
group rules (an unused joint, a repeated joint, zero weights), a mesh without UV, COLOR_0 beside a material, merged
keys, a refused second channel, the armature object's own curves (fx_groups); an animated skinned mesh node, a mesh
on the armature node, one mesh on two nodes (fx_skinned_anim).

Stage 2 (step (c), fx_s2_*): staticParts by name and by material (two skipped beside one bound; nine skipped, nothing
bound, the ellipsis), the governing bone found up a chain (bone -> mesh -> mesh -> mesh, bone -> empty -> mesh, bone ->
mesh -> empty -> mesh, with rotations and scales on the way), a second armature hung from the first one's bone (a bone
the script's armature has not: the root-bone fallback; a bone of the same name; its skinned mesh bound through it with
a vertex group of that name and an Armature modifier already there), an armature hung from another's bone, a scaled and
rotated wrapper with a hung mesh, a rigid mesh with a child, an empty holding a mesh and a lone empty (several empties
flattened), a dropped parent's child beside a bind (the update re-evaluates it), the armature itself a dropped parent's
child (re-rooted before the pin), object animation cleared beside a bind, Python Unicode lowercasing for staticParts,
supplementary-script casing (LEFT: by name), both blocks under the legacy path (nothing happens), and two hung objects
sharing one mesh (LEFT: by name).

Stage 3 (step (d), fx_s3_*): slices of the rotation-only rig (a plain range, reversed with a speed step, a single frame
with the held-stance nudge - these three take the rest fold on the conversion path and are deferred there), slices on
the legacy path over LINEAR and CONSTANT keys (a plain one, a reversed stepped one that lands on its end frame, a held
one), four roles sharing slices and the primary, the three exits (a clip not found, a slice source not found, no
animation), the bone filter keeping and matching nothing, the filter over the armature's own object curves, legacy
keepTranslations kept (with the x100 amplify), nothing kept, kept under the V2 marker (no amplify), the multi-slot trap
(a mesh node before the armature: the armature takes the mesh's slot), an animated mesh under a slice, and keys off the
frame grid (the scene range rounded).
"""
import math, os, sys
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "glb-reader-drill"))
from fixtures import Buf, base, write_glb   # noqa: E402

ARGS = "	".join(["0", "{prefixes}", "{clip}", "1", "1", "0,0,0", "{convert}", "{roles}", "0", "", "{keept}", "{static}", "{local}"])   # target, prefixes, clip, albedo, keepmats, rot, convertRig, roles, ground, sockets, keepT, static, localNode


class Rig:
    """A glTF scene: nodes (some of them joints), skinned and rigid meshes, materials, animations."""

    def __init__(self):
        self.b = Buf(); self.nodes = []; self.meshes = []; self.materials = []; self.skins = []; self.roots = []
        self.anims = []   # [{"name", "samplers", "channels"}]

    def material(self, name, rgb=(0.8, 0.8, 0.8)):
        self.materials.append({"name": name, "pbrMetallicRoughness": {"baseColorFactor": [rgb[0], rgb[1], rgb[2], 1.0], "metallicFactor": 0.0}})
        return len(self.materials) - 1

    def node(self, name, parent=None, **kw):
        n = {"name": name}; n.update(kw)
        self.nodes.append(n); i = len(self.nodes) - 1
        if parent is None:
            self.roots.append(i)
        else:
            self.nodes[parent].setdefault("children", []).append(i)
        return i

    def mesh(self, name, at=(0.0, 0.0, 0.0), size=1.0, material=None, joints=None, colors=False, uv=True, second=None):
        """A small box of 8 vertices and 12 triangles. `joints` = a list of (joint ordinal, weight) sets, one per vertex or a
        single one for all vertices; `second` a material for a second primitive (a triangle beside the box)."""
        x, y, z = at; s = size
        pos = [(x + dx * s, y + dy * s, z + dz * s) for dx in (0.0, 1.0) for dy in (0.0, 1.0) for dz in (0.0, 1.0)]
        idx = [0, 1, 3, 0, 3, 2, 4, 6, 7, 4, 7, 5, 0, 4, 5, 0, 5, 1, 2, 3, 7, 2, 7, 6, 0, 2, 6, 0, 6, 4, 1, 5, 7, 1, 7, 3]
        attrs = {"POSITION": self.b.accessor(pos, "f", "VEC3"), "NORMAL": self.b.accessor([(0.0, 0.0, 1.0)] * 8, "f", "VEC3", minmax=False)}
        if uv:
            attrs["TEXCOORD_0"] = self.b.accessor([(dx, dy) for dx in (0.0, 1.0) for dy in (0.0, 1.0) for _ in (0, 1)], "f", "VEC2", minmax=False)
        if colors:
            attrs["COLOR_0"] = self.b.accessor([(1.0, 0.5, 0.25, 1.0)] * 8, "f", "VEC4", minmax=False)
        if joints is not None:
            per = joints * 8 if len(joints) == 1 else joints   # one set for every vertex, or one per vertex
            j4 = []; w4 = []
            for jw in per:
                jw = (list(jw) + [(0, 0.0)] * 4)[:4]
                j4.append(tuple(j for j, _ in jw)); w4.append(tuple(w for _, w in jw))
            attrs["JOINTS_0"] = self.b.accessor(j4, "H", "VEC4", minmax=False)
            attrs["WEIGHTS_0"] = self.b.accessor(w4, "f", "VEC4", minmax=False)
        prim = {"attributes": attrs, "indices": self.b.accessor(idx, "H", "SCALAR", minmax=False)}
        if material is not None:
            prim["material"] = material
        prims = [prim]
        if second is not None:
            tri = [(x + 2.0 * s, y, z), (x + 3.0 * s, y, z), (x + 2.0 * s, y + s, z)]
            prims.append({"attributes": {"POSITION": self.b.accessor(tri, "f", "VEC3")}, "material": second})
        self.meshes.append({"name": name, "primitives": prims})
        return len(self.meshes) - 1

    def skin(self, name, joints, ibms):
        self.skins.append({"name": name, "joints": joints, "inverseBindMatrices": self.b.accessor(ibms, "f", "MAT4", minmax=False)})
        return len(self.skins) - 1

    def anim(self, name):
        self.anims.append({"name": name, "samplers": [], "channels": []})
        return len(self.anims) - 1

    def key(self, anim, node, path, times, values, interpolation=None):
        a = self.anims[anim]
        kind = "VEC4" if path == "rotation" else ("SCALAR" if path == "weights" else "VEC3")
        s = {"input": self.b.accessor(times, "f", "SCALAR"), "output": self.b.accessor(values, "f", kind, minmax=False)}
        if interpolation:
            s["interpolation"] = interpolation
        a["samplers"].append(s)
        a["channels"].append({"sampler": len(a["samplers"]) - 1, "target": {"node": node, "path": path}})

    def write(self, out, name):
        extra = {}
        if self.materials: extra["materials"] = self.materials
        if self.skins: extra["skins"] = self.skins
        if self.anims: extra["animations"] = self.anims
        root = base(name, meshes=self.meshes, nodes=self.nodes, scenes=[{"nodes": self.roots}], scene=0, **extra)
        path = os.path.join(out, name + ".glb")
        write_glb(path, root, self.b)
        return path.replace("\\", "/")


def ibm(tx, ty, tz):
    """The inverse bind matrix of a joint at (tx, ty, tz) with no rotation, column-major."""
    return (1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, -tx, -ty, -tz, 1.0)


def spin(n, axis="y", turns=1.0):
    """n quaternion keys turning about an axis."""
    out = []
    for i in range(n):
        a = 2.0 * math.pi * turns * i / max(1, n - 1)
        s, c = math.sin(a / 2), math.cos(a / 2)
        out.append((s, 0.0, 0.0, c) if axis == "x" else ((0.0, s, 0.0, c) if axis == "y" else (0.0, 0.0, s, c)))
    return out


def rigged(r, arm_name="Armature", euler_bone=False):
    """A two-bone rig under `arm_name` with a skinned box: returns (armature node, root joint, child joint, skin)."""
    arm = r.node(arm_name)
    j0 = r.node("Root", arm)
    j1 = r.node("Arm", j0, translation=[0.0, 1.0, 0.0])
    sk = r.skin("Skin", [j0, j1], [ibm(0.0, 0.0, 0.0), ibm(0.0, 1.0, 0.0)])
    m = r.material("Paint", (0.2, 0.4, 0.9))
    body = r.node("Body", arm, mesh=r.mesh("body", material=m, joints=[[(0, 1.0)]] * 4 + [[(0, 0.5), (1, 0.5)]] * 4), skin=sk)
    a = r.anim("Spin")
    r.key(a, j1, "rotation", [0.0, 0.5, 1.0], spin(3, "x", 0.5))
    if euler_bone:
        # the importer keeps the bone in quaternion mode; an Euler mode is a Blender-side state no glTF can ask for
        pass
    return arm, j0, j1, sk, m


def junk(out):
    r = Rig(); arm, j0, j1, sk, m = rigged(r)
    r.node("Lone", None, mesh=r.mesh("lone", at=(3.0, 0.0, 0.0)), translation=[1.0, 0.0, 0.0])                      # dropped: no material
    wrap = r.node("Wrap", None, mesh=r.mesh("wrap", at=(5.0, 0.0, 0.0)), translation=[0.0, 2.0, 0.0], scale=[2.0, 2.0, 2.0])   # dropped: its child must stay, a root with the old local transform
    r.node("Kept", wrap, mesh=r.mesh("kept", at=(5.0, 2.0, 0.0), material=m), translation=[0.5, 0.0, 0.0])
    r.node("Coloured", None, mesh=r.mesh("coloured", at=(7.0, 0.0, 0.0), colors=True), translation=[0.0, 0.0, 1.0])   # COLOR_0 and no material: Blender decides
    r.node("Mixed", None, mesh=r.mesh("mixed", at=(9.0, 0.0, 0.0), second=m))                                           # a primitive without and one with a material
    return r.write(out, "riganim_junk")


def objanim(out, name):
    r = Rig(); arm, j0, j1, sk, m = rigged(r)
    prop = r.node("Prop", arm, mesh=r.mesh("prop", at=(2.0, 0.0, 0.0), material=m), translation=[2.0, 0.0, 0.0])
    r.key(0, prop, "rotation", [0.0, 1.0], spin(2, "z", 0.25))          # object-level animation beside the bone's
    r.key(0, prop, "translation", [0.0, 1.0], [(2.0, 0.0, 0.0), (2.0, 1.0, 0.0)])
    return r.write(out, name)


def multi(out):
    r = Rig(); arm, j0, j1, sk, m = rigged(r)
    a2 = r.anim("Longer")
    r.key(a2, j1, "rotation", [0.0, 2.0, 4.0], spin(3, "y", 1.0))
    r.key(a2, j0, "translation", [0.0, 4.0], [(0.0, 0.0, 0.0), (0.0, 0.0, 2.0)])
    r.key(a2, j0, "scale", [0.0, 1.0, 4.0], [(1.0, 1.0, 1.0), (1.5, 1.5, 1.5), (1.0, 1.0, 1.0)], "STEP")
    a3 = r.anim("Held")
    r.key(a3, j1, "rotation", [0.0], [(0.0, 0.0, 0.0, 1.0)])
    # a mesh hung off a bone (a bone-parented object: stage 2's conversion, stage 1's OBJ row)
    r.node("Hung", j1, mesh=r.mesh("hung", at=(0.0, 1.5, 0.0), material=m), translation=[0.0, 0.5, 0.0])
    return r.write(out, "riganim_multi")


def wrapped(out):
    r = Rig()
    wrap = r.node("Wrapper", None, scale=[0.01, 0.01, 0.01], rotation=[0.7071068, 0.0, 0.0, 0.7071068])
    arm = r.node("Armature", wrap)
    j0 = r.node("Root", arm); j1 = r.node("Tip", j0, translation=[0.0, 100.0, 0.0])
    sk = r.skin("Skin", [j0, j1], [ibm(0.0, 0.0, 0.0), ibm(0.0, 100.0, 0.0)])
    m = r.material("Steel")
    r.node("Body", arm, mesh=r.mesh("body", size=100.0, material=m, joints=[[(0, 1.0)]]), skin=sk)
    r.node("Rigid", wrap, mesh=r.mesh("rigid", at=(150.0, 0.0, 0.0), size=50.0, material=m))
    a = r.anim("Spin"); r.key(a, j1, "rotation", [0.0, 1.0], spin(2, "z", 0.5))
    return r.write(out, "riganim_wrapped")


def noarm(out):
    r = Rig(); m = r.material("Paint")
    n = r.node("Static", None, mesh=r.mesh("static", material=m))
    a = r.anim("Spin"); r.key(a, n, "rotation", [0.0, 1.0], spin(2, "y", 0.5))
    return r.write(out, "riganim_noarm")


def v2(out):
    r = Rig(); rigged(r, arm_name="DeployArmV2")
    return r.write(out, "riganim_v2")


def groups(out):
    """Stage 1's vertex groups and curves (the stage 1 model's branches no recipe takes): a joint no vertex uses, a vertex
    weighted twice to one joint (REPLACE: the later weight), a vertex with zero weights (the importer's 1.0 on its first
    joint), a mesh without UV and with COLOR_0 beside a material, two keys closer than 0.01 frame (merged, the last
    wins), a second channel on one node and path (refused), the armature object's own curves."""
    r = Rig()
    arm = r.node("Armature")
    j0 = r.node("Root", arm)
    j1 = r.node("Arm", j0, translation=[0.0, 1.0, 0.0])
    j2 = r.node("Spare", j0, translation=[1.0, 0.0, 0.0])
    sk = r.skin("Skin", [j0, j1, j2], [ibm(0.0, 0.0, 0.0), ibm(0.0, 1.0, 0.0), ibm(1.0, 0.0, 0.0)])
    m = r.material("Paint", (0.2, 0.4, 0.9))
    per = [[(0, 1.0)]] * 2 + [[(1, 0.3), (1, 0.6)]] * 2 + [[(0, 0.0)]] * 2 + [[(0, 0.5), (1, 0.5)]] * 2
    r.node("Body", arm, mesh=r.mesh("body", material=m, joints=per), skin=sk)
    r.node("Tinted", arm, mesh=r.mesh("tinted", at=(3.0, 0.0, 0.0), material=m, colors=True, uv=False), translation=[0.0, 0.0, 3.0])
    a = r.anim("Spin")
    r.key(a, j1, "rotation", [0.0, 0.0001, 0.5, 1.0], spin(4, "x", 0.5))
    r.key(a, j1, "rotation", [0.0, 1.0], spin(2, "y", 0.25))
    r.key(a, arm, "translation", [0.0, 1.0], [(0.0, 0.0, 0.0), (0.0, 0.5, 0.0)])
    return r.write(out, "riganim_groups")


def skinned_anim(out):
    """An animated skinned mesh node (the importer leaves the node as an empty with the object animation and puts the mesh on
    a '.skinned' child of the armature), a mesh on the armature node itself (a '.mesh' child), one mesh on two nodes."""
    r = Rig(); arm, j0, j1, sk, m = rigged(r)
    rider = r.node("Rider", None, mesh=r.mesh("rider", at=(2.0, 0.0, 0.0), material=m, joints=[[(1, 1.0)]]), skin=sk, translation=[0.0, 0.0, 2.0])
    r.key(0, rider, "translation", [0.0, 1.0], [(0.0, 0.0, 2.0), (0.0, 0.0, 3.0)])
    r.nodes[arm]["mesh"] = r.mesh("crest", at=(0.0, 2.0, 0.0), material=m)
    shared = r.mesh("stud", at=(4.0, 0.0, 0.0), size=0.5, material=m)
    r.node("StudA", arm, mesh=shared, translation=[1.0, 0.0, 0.0])
    r.node("StudB", arm, mesh=shared, translation=[-1.0, 0.0, 0.0])
    return r.write(out, "riganim_skinned_anim")


# ---- stage 2 (step (c)): the bone-parent -> skin conversion and the wrapper flatten. No registry recipe takes either.

def s2_static(out):
    """Bone-hung meshes kept weightless by staticParts: one matched by its NAME, one by its MATERIAL's name (both lowered,
    substring), one that binds (rotated and scaled: its recomposition carries noise) with a static child mesh that stays
    under it and is re-evaluated below its new matrix; a non-hung mesh whose name matches is no part of the count."""
    r = Rig(); arm, j0, j1, sk, m = rigged(r)
    glass = r.material("Glass_Pane", (0.2, 0.9, 0.9))
    r.node("TurretTop", j1, mesh=r.mesh("turret", at=(0.0, 1.5, 0.0), material=m), translation=[0.0, 0.5, 0.0])
    r.node("Canopy", j0, mesh=r.mesh("canopy", at=(1.0, 0.0, 0.0), material=glass), translation=[1.0, 0.0, 0.0])
    gun = r.node("Gun", j1, mesh=r.mesh("gun", at=(0.0, 2.0, 0.5), material=m), translation=[0.0, 1.0, 0.5], rotation=[0.1830127, 0.6830127, 0.1830127, 0.6830127], scale=[1.1, 0.9, 1.3])
    r.node("GunSight", gun, mesh=r.mesh("sight", at=(0.0, 2.5, 0.5), size=0.3, material=glass), translation=[0.0, 0.5, 0.0])
    r.node("TurretRing", arm, mesh=r.mesh("ring", at=(3.0, 0.0, 0.0), material=m), translation=[3.0, 0.0, 0.0])
    return r.write(out, "riganim_s2_static")


def s2_static_many(out):
    """Nine bone-hung meshes all static (the log's sorted first eight and the ellipsis), nothing bound: no bind, no update."""
    r = Rig(); arm, j0, j1, sk, m = rigged(r)
    for i in range(9):
        r.node("plate_%d" % (9 - i), j1 if i % 2 else j0, mesh=r.mesh("plate%d" % i, at=(float(i), 0.0, 0.0), size=0.5, material=m), translation=[float(i), 0.5, 0.0])
    return r.write(out, "riganim_s2_static_many")


def s2_unicode_static(out):
    """Python lower expands U+0130 and uses final sigma; .NET invariant lower does neither."""
    r = Rig(); arm, j0, j1, sk, m = rigged(r)
    r.node("İTurret", j1, mesh=r.mesh("turret", at=(0.0, 1.5, 0.0), material=m), translation=[0.0, 0.5, 0.0])
    r.node("AΣ", j0, mesh=r.mesh("plate", at=(1.0, 0.0, 0.0), material=m), translation=[1.0, 0.0, 0.0])
    r.node("Gun", j1, mesh=r.mesh("gun", at=(0.0, 2.0, 0.0), material=m), translation=[0.0, 1.0, 0.0])
    return r.write(out, "riganim_s2_unicode_static")


def s2_supplementary_static(out):
    """Deseret capital letters lowercase in Python but need a named fallback in the C# stage 2 model."""
    r = Rig(); arm, j0, j1, sk, m = rigged(r)
    r.node("\U00010400Panel", j1, mesh=r.mesh("panel", at=(0.0, 1.5, 0.0), material=m), translation=[0.0, 0.5, 0.0])
    return r.write(out, "riganim_s2_supplementary_static")


def s2_chain(out):
    """The governing bone found up the parent chain: bone -> mesh -> mesh -> mesh, bone -> empty -> mesh, bone -> mesh ->
    empty -> mesh; the hung meshes rotated and scaled so the rest-world pin and its decomposition carry real values."""
    r = Rig(); arm, j0, j1, sk, m = rigged(r)
    top = r.node("Hull", j1, mesh=r.mesh("hull", at=(0.0, 1.5, 0.0), material=m), translation=[0.1, 0.5, 0.2], rotation=[0.1830127, 0.6830127, 0.1830127, 0.6830127], scale=[1.1, 0.9, 1.3])
    mid = r.node("Hatch", top, mesh=r.mesh("hatch", at=(0.5, 2.0, 0.0), size=0.5, material=m), translation=[0.5, 0.5, 0.0], rotation=[0.0, 0.3826834, 0.0, 0.9238795])
    r.node("Handle", mid, mesh=r.mesh("handle", at=(0.5, 2.5, 0.0), size=0.2, material=m), translation=[0.0, 0.5, 0.0], scale=[2.0, 2.0, 2.0])
    grp = r.node("WheelGroup", j0, translation=[1.0, 0.0, 0.0], rotation=[0.0, 0.0, 0.2588190, 0.9659258], scale=[0.5, 0.5, 0.5])
    r.node("Wheel", grp, mesh=r.mesh("wheel", at=(1.0, 0.0, 0.0), material=m), translation=[0.0, 0.0, 1.0])
    sub = r.node("LampGroup", top, translation=[0.0, 1.0, 0.0])
    r.node("Lamp", sub, mesh=r.mesh("lamp", at=(0.0, 3.0, 0.0), size=0.3, material=m), translation=[0.3, 0.0, 0.0])
    return r.write(out, "riganim_s2_chain")


def s2_two_arms(out):
    """A second armature hung from the first one's 'Arm' bone: a mesh hung from a bone of its that the script's armature
    has no bone of (the root-bone fallback), one from a bone that shares the name 'Arm' (bound to the FIRST armature's),
    and its own skinned mesh, whose governing bone is found through the armature and whose vertex groups already hold
    an 'Arm' (the existing-group branch) beside an Armature modifier of its own - all three re-homed onto the first
    armature, whose pose is the one held at rest while the second stays posed (its root bone animated: the dump tells one
    action's fcurves apart by path alone, so the shared-name bone is not)."""
    r = Rig(); arm, j0, j1, sk, m = rigged(r)
    arm2 = r.node("Armature2", j1, translation=[4.0, 0.0, 0.0])
    k0 = r.node("Other", arm2)
    k1 = r.node("Arm", k0, translation=[0.0, 1.0, 0.0], rotation=[0.0, 0.0, 0.3826834, 0.9238795])
    sk2 = r.skin("Skin2", [k0, k1], [ibm(4.0, 0.0, 0.0), ibm(4.0, 1.0, 0.0)])
    r.node("Body2", arm2, mesh=r.mesh("body2", at=(4.0, 0.0, 0.0), material=m, joints=[[(0, 0.7), (1, 0.3)]]), skin=sk2)   # 'Arm' at 0.3: the bind REPLACES it with 1.0
    r.key(0, k0, "rotation", [0.0, 1.0], spin(2, "y", 0.25))
    r.node("HungOther", k0, mesh=r.mesh("hungother", at=(4.0, 0.5, 0.0), size=0.5, material=m), translation=[0.0, 0.5, 0.0])
    r.node("HungArm2", k1, mesh=r.mesh("hungarm2", at=(4.0, 1.5, 0.0), size=0.5, material=m), translation=[0.0, 0.5, 0.0])
    return r.write(out, "riganim_s2_two_arms")


def s2_nested_arms(out):
    """An armature hung from another armature's bone: its skinned mesh's governing bone is found through it, the mesh
    already has vertex groups (the skin's) and an Armature modifier - the bind's "existing" branches."""
    r = Rig()
    outer = r.node("Carrier")
    c0 = r.node("Deck", outer)
    skc = r.skin("SkinC", [c0], [ibm(0.0, 0.0, 0.0)])
    m = r.material("Paint", (0.2, 0.4, 0.9))
    r.node("Hull", outer, mesh=r.mesh("hull", size=3.0, material=m, joints=[[(0, 1.0)]]), skin=skc)
    inner = r.node("Armature", c0, translation=[0.0, 3.0, 0.0], rotation=[0.0, 0.0, 0.3826834, 0.9238795])
    j0 = r.node("Root", inner)
    j1 = r.node("Arm", j0, translation=[0.0, 1.0, 0.0])
    sk = r.skin("Skin", [j0, j1], [ibm(0.0, 3.0, 0.0), ibm(0.0, 4.0, 0.0)])
    r.node("Body", inner, mesh=r.mesh("body", at=(0.0, 3.0, 0.0), material=m, joints=[[(0, 1.0)]] * 4 + [[(0, 0.5), (1, 0.5)]] * 4), skin=sk)
    r.node("Hung", j1, mesh=r.mesh("hung", at=(0.0, 4.5, 0.0), size=0.5, material=m), translation=[0.0, 0.5, 0.0])
    a = r.anim("Spin")
    r.key(a, j1, "rotation", [0.0, 0.5, 1.0], spin(3, "x", 0.5))
    r.key(a, c0, "rotation", [0.0, 1.0], spin(2, "y", 0.25))
    return r.write(out, "riganim_s2_nested_arms")


def s2_wrapped_hung(out):
    """A wrapper empty with an odd scale and rotation over the rig, a bone-hung mesh under it (its rest world carries the
    wrapper), a rigid mesh under the wrapper with a mesh of its own below (re-evaluated under its recomposed parent), an
    empty under the wrapper holding a mesh, a lone empty elsewhere: several empties flattened."""
    r = Rig()
    wrap = r.node("Wrapper", None, scale=[0.0123, 0.0123, 0.0123], rotation=[0.1830127, 0.6830127, 0.1830127, 0.6830127], translation=[0.7, 0.2, -0.4])
    arm = r.node("Armature", wrap, translation=[10.0, 0.0, 0.0])
    j0 = r.node("Root", arm); j1 = r.node("Tip", j0, translation=[0.0, 100.0, 0.0], rotation=[0.0, 0.0, 0.2588190, 0.9659258])
    sk = r.skin("Skin", [j0, j1], [ibm(10.0, 0.0, 0.0), ibm(10.0, 100.0, 0.0)])
    m = r.material("Steel")
    r.node("Body", arm, mesh=r.mesh("body", at=(10.0, 0.0, 0.0), size=100.0, material=m, joints=[[(0, 1.0)]] * 4 + [[(1, 1.0)]] * 4), skin=sk)
    r.node("Hung", j1, mesh=r.mesh("hung", at=(10.0, 150.0, 0.0), size=30.0, material=m), translation=[5.0, 50.0, 0.0], rotation=[0.0, 0.3826834, 0.0, 0.9238795])
    rigid = r.node("Rigid", wrap, mesh=r.mesh("rigid", at=(150.0, 0.0, 0.0), size=50.0, material=m), translation=[150.0, 0.0, 0.0], scale=[1.5, 1.5, 1.5])
    r.node("Bolt", rigid, mesh=r.mesh("bolt", at=(150.0, 60.0, 0.0), size=10.0, material=m), translation=[0.0, 60.0, 0.0])
    grp = r.node("Crates", wrap, translation=[0.0, 0.0, 200.0], rotation=[0.0, 0.0, 0.3826834, 0.9238795])
    r.node("Crate", grp, mesh=r.mesh("crate", at=(0.0, 0.0, 200.0), size=20.0, material=m), translation=[20.0, 0.0, 0.0])
    r.node("Marker", None, translation=[1.0, 2.0, 3.0])
    a = r.anim("Spin"); r.key(a, j1, "rotation", [0.0, 1.0], spin(2, "z", 0.5))
    return r.write(out, "riganim_s2_wrapped_hung")


def s2_junk_hung(out):
    """A dropped (material-less) parent's kept child beside a bone-hung mesh: the bind's scene update decides whether the
    kept child's matrix_world survives it."""
    r = Rig(); arm, j0, j1, sk, m = rigged(r)
    wrap = r.node("Wrap", None, mesh=r.mesh("wrap", at=(5.0, 0.0, 0.0)), translation=[0.0, 2.0, 0.0], scale=[2.0, 2.0, 2.0])
    r.node("Kept", wrap, mesh=r.mesh("kept", at=(5.0, 2.0, 0.0), material=m), translation=[0.5, 0.0, 0.0])
    r.node("Hung", j1, mesh=r.mesh("hung", at=(0.0, 1.5, 0.0), material=m), translation=[0.0, 0.5, 0.0])
    return r.write(out, "riganim_s2_junk_hung")


def s2_junk_arm(out):
    """The ARMATURE itself a dropped (material-less, scaled) mesh's child, with a bone-hung mesh: the bind's update re-roots
    the armature at its own transform first, and the pin's parent matrix is that one."""
    r = Rig(); arm, j0, j1, sk, m = rigged(r)
    crate = r.node("Crate", None, mesh=r.mesh("crate", at=(5.0, 0.0, 0.0)), translation=[0.0, 2.0, 0.0], scale=[2.0, 2.0, 2.0], rotation=[0.0, 0.3826834, 0.0, 0.9238795])
    r.roots.remove(arm); r.nodes[crate].setdefault("children", []).append(arm)
    r.nodes[arm]["translation"] = [0.5, 0.0, 0.0]
    r.node("Hung", j1, mesh=r.mesh("hung", at=(0.0, 1.5, 0.0), material=m), translation=[0.0, 0.5, 0.0])
    return r.write(out, "riganim_s2_junk_arm")


def s2_objanim_hung(out):
    """Object-level animation cleared in stage 1 beside a bone-hung mesh: the bind's scene update decides what world the
    cleared object shows (its frame-1 values, or its authored ones)."""
    r = Rig(); arm, j0, j1, sk, m = rigged(r)
    prop = r.node("Prop", arm, mesh=r.mesh("prop", at=(2.0, 0.0, 0.0), material=m), translation=[2.0, 0.0, 0.0])
    r.key(0, prop, "rotation", [0.0, 1.0], spin(2, "z", 0.25))
    r.key(0, prop, "translation", [0.0, 1.0], [(2.0, 0.0, 0.0), (2.0, 1.0, 0.0)])
    r.node("Hung", j1, mesh=r.mesh("hung", at=(0.0, 1.5, 0.0), material=m), translation=[0.0, 0.5, 0.0])
    return r.write(out, "riganim_s2_objanim_hung")


def s2_shared_hung(out):
    """Two bone-hung objects sharing one mesh datablock (one vertex group list, one weight layer, two binds): left to
    Blender by name."""
    r = Rig(); arm, j0, j1, sk, m = rigged(r)
    shared = r.mesh("pad", at=(0.0, 0.0, 0.0), size=0.5, material=m)
    r.node("PadA", j0, mesh=shared, translation=[1.0, 0.0, 0.0])
    r.node("PadB", j1, mesh=shared, translation=[-1.0, 0.0, 0.0])
    return r.write(out, "riganim_s2_shared_hung")


# ---- stage 3 (step (d)): the clip path

def s3_plain(out):
    """The two-bone rig with its rotation-only Spin (24 frames): the slices' source."""
    r = Rig(); rigged(r)
    return r.write(out, "riganim_s3_plain")


def s3_noanim(out):
    """A rig without an animation: the script's 'no animation action' exit."""
    r = Rig(); arm, j0, j1, sk, m = rigged(r)
    r.anims.clear()
    return r.write(out, "riganim_s3_noanim")


def s3_statues(out):
    """The multi-slot trap: a mesh node BEFORE the armature carries object-level keys in the same animation, so the action's
    first slot is the mesh's and assign_action hands the armature that slot - the bones play statues and the armature
    object itself takes the mesh's location keys."""
    r = Rig()
    m = r.material("Paint", (0.2, 0.4, 0.9))
    prop = r.node("Prop", None, mesh=r.mesh("prop", at=(2.0, 0.0, 0.0), material=m), translation=[2.0, 0.0, 0.0])
    arm, j0, j1, sk, m2 = rigged(r)
    r.key(0, prop, "translation", [0.0, 1.0], [(2.0, 0.0, 0.0), (2.0, 1.0, 0.0)])
    return r.write(out, "riganim_s3_statues")


def s3_frac(out):
    """Keys off the frame grid (0.7 s = frame 16.8): the scene range is Python's round of the action's range."""
    r = Rig(); arm, j0, j1, sk, m = rigged(r)
    r.anims.clear(); a = r.anim("Spin")
    r.key(a, j1, "rotation", [0.0, 0.7], spin(2, "x", 0.5))
    return r.write(out, "riganim_s3_frac")


def s3_keept(out):
    """Whole-action role clips for legacy keepTranslations: 'Slide' moves Root from an offset (the rebase bites), 'Creep'
    moves Arm by 0.2 (under the move role's 0.5 floor, over the attack role's 1e-4). A SLICED role clip is named
    '<source>_<role>_...' and never matches the floor table keyed by the source name - the script keeps nothing for it
    (fx_s3_keept_kept records that)."""
    r = Rig(); arm, j0, j1, sk, m = rigged(r)
    a2 = r.anim("Slide"); r.key(a2, j0, "translation", [0.0, 1.0], [(0.5, 0.0, 0.0), (1.5, 0.0, 0.0)])
    a3 = r.anim("Creep"); r.key(a3, j1, "translation", [0.0, 1.0], [(0.0, 1.0, 0.0), (0.0, 1.2, 0.0)])
    return r.write(out, "riganim_s3_keept")


def s3_v2_multi(out):
    """The multi file under a DeployArmV2 armature: legacy keepTranslations keeps a path but the amplify is skipped."""
    r = Rig(); arm, j0, j1, sk, m = rigged(r, arm_name="DeployArmV2")
    a2 = r.anim("Longer")
    r.key(a2, j1, "rotation", [0.0, 2.0, 4.0], spin(3, "y", 1.0))
    r.key(a2, j0, "translation", [0.0, 4.0], [(0.0, 0.0, 0.0), (0.0, 0.0, 2.0)])
    return r.write(out, "riganim_s3_v2_multi")


def s3_escaped_bone(out, name, suffix):
    """The bone filter reads an ESCAPED RNA path, not the original bone name."""
    r = Rig(); arm, j0, j1, sk, m = rigged(r)
    r.nodes[j1]["name"] = name
    return r.write(out, "riganim_s3_filter_" + suffix)


def main(out):
    sys.stdout.reconfigure(encoding="utf-8")  # JOB records may contain non-ASCII staticParts on Windows
    os.makedirs(out, exist_ok=True)
    def job(key, path, clip="Spin", convert="1", local="0", static="", prefixes="", roles="", keept="0"):
        print("JOB\t%s\t%s\t%s" % (key, path, ARGS.format(clip=clip, convert=convert, local=local, static=static, prefixes=prefixes, roles=roles, keept=keept)))
    job("fx_junk", junk(out))
    p = objanim(out, "riganim_objanim")
    job("fx_objanim_convert", p)
    job("fx_objanim_legacy", p, convert="0")
    job("fx_multi", multi(out), clip="Longer")
    job("fx_multi_first", multi(out), clip="")
    job("fx_wrapped", wrapped(out))
    job("fx_v2", v2(out), convert="0")
    job("EXIT:1:fx_noarm", noarm(out))
    job("LEFT:fx_localnode", p, local="1")
    job("fx_groups", groups(out))
    job("fx_skinned_anim", skinned_anim(out))
    # stage 2
    job("fx_s2_static", s2_static(out), static="turret,glass")
    job("fx_s2_static_many", s2_static_many(out), static="plate")
    job("fx_s2_unicode_static", s2_unicode_static(out), static="i\u0307turret,aς")
    job("LEFT:fx_s2_supplementary_static", s2_supplementary_static(out), static="\U00010428panel")
    job("fx_s2_chain", s2_chain(out))
    job("fx_s2_two_arms", s2_two_arms(out))
    job("fx_s2_nested_arms", s2_nested_arms(out))
    job("fx_s2_wrapped_hung", s2_wrapped_hung(out))
    job("fx_s2_junk_hung", s2_junk_hung(out))
    job("fx_s2_junk_arm", s2_junk_arm(out))
    job("fx_s2_objanim_hung", s2_objanim_hung(out))
    job("fx_s2_multi_legacy", multi(out), clip="Longer", convert="0")
    job("fx_s2_wrapped_legacy", wrapped(out), convert="0")
    job("LEFT:fx_s2_shared_hung", s2_shared_hung(out))
    # stage 3 (step (d), the clip path; the rest fold is deferred): slices on the rotation-only rig, the exits, the filter,
    # legacy keepTranslations, the multi-slot trap
    plain = s3_plain(out)
    job("fx_s3_slice", plain, clip="Spin[2..10]")
    job("fx_s3_slice_rev_step", plain, clip="Spin[24..0/5]")
    job("fx_s3_slice_held", plain, clip="Spin[5..5]")
    job("fx_s3_roles", plain, clip="Spin", roles="attack=Spin[0..6];move=Spin[0..6];idle=Spin;deploy=Spin[6..0]")
    job("EXIT:3:fx_s3_noclip", plain, clip="Nope")
    job("EXIT:3:fx_s3_noslicesrc", plain, clip="Nope[0..5]")
    job("EXIT:3:fx_s3_noanim", s3_noanim(out), clip="")
    job("fx_s3_filter", plain, clip="", prefixes="Ar,Zz")
    job("EXIT:3:fx_s3_filter_none", plain, clip="", prefixes="Ro")
    job("fx_s3_slice_legacy", multi(out), clip="Longer[1..7/2]", convert="0")
    job("fx_s3_slice_objanim", p, clip="Spin[0..8]", convert="0")
    job("fx_s3_statues", s3_statues(out), clip="Spin[0..5]", convert="0")
    job("fx_s3_keept_kept", multi(out), clip="Spin", convert="0", keept="1", roles="attack=Longer[10..40];move=Longer[0..10]")   # the attack slide starts off zero (the rebase bites); the move slide stays under its 0.5 floor
    job("fx_s3_keept_none", multi(out), clip="Longer", convert="0", keept="1", roles="idle=Spin")
    job("fx_s3_keept_whole", s3_keept(out), clip="Spin", convert="0", keept="1", roles="attack=Slide;move=Creep")
    job("fx_s3_keept_v2", s3_v2_multi(out), clip="Spin", convert="0", keept="1", roles="attack=Longer")
    job("fx_s3_slice_legacy_rev", multi(out), clip="Longer[9..0/4]", convert="0")
    job("fx_s3_slice_legacy_held", multi(out), clip="Longer[5..5]", convert="0")
    job("fx_s3_filter_objcurves", groups(out), clip="", prefixes="Arm")
    job("fx_s3_frac", s3_frac(out), clip="")
    # Review regressions: dictionary overwrite/order, escaped-path filtering, named fallback from either resolver,
    # and bpy.data.actions.new's UTF-8 truncation and collision suffixes.
    mp = multi(out)
    job("fx_s3_roles_overwritten", mp, convert="0", roles="attack=Longer;attack=Held")
    job("fx_s3_roles_overwrite_order", mp, convert="0", prefixes="Arm", roles="move=Held;attack=Spin[0..2];move=Longer")
    for name, suffix in [('Ar"m', "quote"), (r"Ar\m", "slash")]:
        ep = s3_escaped_bone(out, name, suffix)
        job("EXIT:3:fx_s3_filter_" + suffix, ep, prefixes=name)
        escaped = name.replace("\\", "\\\\").replace('"', '\\"')
        job("fx_s3_filter_" + suffix + "_escaped", ep, prefixes=escaped)
    job("LEFT:fx_s3_primary_frame_limit", plain, clip="Spin[1048575..1048575]", convert="0")
    job("LEFT:fx_s3_role_frame_limit", plain, convert="0", roles="attack=Spin[1048575..1048575]")
    long_role = "a" * 250
    job("fx_s3_action_name_limit", plain, convert="0", roles=long_role + "=Spin[0..2];" + long_role + "b=Spin[1..3]")


if __name__ == "__main__":
    main(sys.argv[1])
