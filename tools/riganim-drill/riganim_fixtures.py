"""Fixtures for rig_anim.py (tools/riganim_drill.sh): the branches of the script no registry recipe takes, each a small
rigged GLB with the arguments that reach the branch. Writes the files and prints JOB lines in riganim_jobs.py's form
(TAB-separated: JOB <key> <input> <argv[2]> ... <argv[14]>). A key that starts with "EXIT:<stage>:" names a job the
script is expected to STOP at that stage (its own sys.exit(1), e.g. no armature): the drill fails a different stop.
A key that starts with "LEFT:" names a job the C# leaves to Blender by name (localNodeAnim, the weld, a socket sorting
before its parent): the drill accepts whatever Blender does with it (a run through, an exit, a death). "EXPORTLEFT:" names
a job whose four stages are compared and whose GLB export alone is left by name (a second armature's unused skin).

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

Step (d2), the rest fold and rebake and the bone cap (fx_s4_*): two skinned nodes sharing one mesh (copied before the
modifier is applied), a static part hung from a bone through the fold (its parent inverse rewritten, its evaluated copy
left unrefreshed), the nested armatures with a location key on the carrier (the inner mesh deformed by its own
armature, then re-bound), keepTranslations on the conversion path (the kept paths from the rebake, a single-frame role
clip padded), a rig a thousand times the size (the residual's digits), and 492 bones for the cap (two passes: the
leaves, then the unweighted middles; a weighted leaf stays).

Step (e), stage 4 (fx_s4_*, the rest): the albedo (a base-colour texture on the first mesh, on the second mesh only, an
emissive texture without a base colour - the tree's first image node, the importer's image names in the order the
materials are made, a KHR_materials_specular colour texture as the first image, a mesh made after the body but sorting
before it, the albedo switched off), keepmats 1 and 0 on two materials, the decimate (on 13 and 12 triangles, on the
legacy path, at 1/60 under the ratio's 0.02 floor), the root collapse (a pass-through root removed, kept when keyed, kept with two children, untouched on the
legacy path), the rebind after a join whose active object has no Armature modifier, every mesh culled (the stage 4
exit), a mesh already centred and grounded, the weld of a one-material, one-bone mesh (LEFT), three meshes with one,
two and no UV sets joined, a skinned body under a rigid hull mesh, two sockets with an offset and a bad offset, a
socket through a rotation fold, the two socket exits (no parent; sorting before its parent - LEFT, the script's
message is undefined), the rotation fold at an odd angle, at 180 degrees on the multi rig, and on the legacy path.

Step (f), the GLB export (fx_f_*): 300 weighted bones (the joints as unsigned shorts), an animation of one key (the
exporter's undo of frame_range's +1), sparse STEP keys (CONSTANT segments sampled between the keys). Every other job's
export is compared too; the three second-armature jobs are EXPORTLEFT: (their unused skin).
"""
import math, os, sys
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "glb-reader-drill"))
from fixtures import Buf, base, png, write_glb   # noqa: E402

ARGS = "	".join(["{target}", "{prefixes}", "{clip}", "{albedo}", "{keepmats}", "{rot}", "{convert}", "{roles}", "0", "{sockets}", "{keept}", "{static}", "{local}"])   # target, prefixes, clip, albedo, keepmats, rot, convertRig, roles, ground, sockets, keepT, static, localNode


class Rig:
    """A glTF scene: nodes (some of them joints), skinned and rigid meshes, materials, animations."""

    def __init__(self):
        self.b = Buf(); self.nodes = []; self.meshes = []; self.materials = []; self.skins = []; self.roots = []
        self.anims = []   # [{"name", "samplers", "channels"}]
        self.images = []; self.textures = []

    def material(self, name, rgb=(0.8, 0.8, 0.8), texture=None, emissive=None, extensions=None):
        m = {"name": name, "pbrMetallicRoughness": {"baseColorFactor": [rgb[0], rgb[1], rgb[2], 1.0], "metallicFactor": 0.0}}
        if extensions:
            m["extensions"] = extensions
        if texture is not None:
            m["pbrMetallicRoughness"]["baseColorTexture"] = {"index": texture}
        if emissive is not None:
            m["emissiveTexture"] = {"index": emissive}; m["emissiveFactor"] = [1.0, 1.0, 1.0]
        self.materials.append(m)
        return len(self.materials) - 1

    def image(self, name, w=4, h=4, rgb=(200, 40, 40)):
        """An embedded PNG of one colour (a buffer view); `name` None leaves the image unnamed (the importer names it Image_<index>)."""
        img = {"bufferView": self.b.blob(png(w, h, rgb)), "mimeType": "image/png"}
        if name is not None:
            img["name"] = name
        self.images.append(img)
        return len(self.images) - 1

    def texture(self, image):
        self.textures.append({"source": image})
        return len(self.textures) - 1

    def node(self, name, parent=None, **kw):
        n = {"name": name}; n.update(kw)
        self.nodes.append(n); i = len(self.nodes) - 1
        if parent is None:
            self.roots.append(i)
        else:
            self.nodes[parent].setdefault("children", []).append(i)
        return i

    def mesh(self, name, at=(0.0, 0.0, 0.0), size=1.0, material=None, joints=None, colors=False, uv=True, second=None, uv2=False, dupes=False):
        """A small box of 8 vertices and 12 triangles. `joints` = a list of (joint ordinal, weight) sets, one per vertex or a
        single one for all vertices; `second` a material for a second primitive (a triangle beside the box); `uv2` a second
        UV set (TEXCOORD_1); `dupes` repeats the 8 vertices (equal in every attribute) with two triangles on the copies and
        one collapsed triangle (two corners on one vertex) - what Unity's FBX import welds and drops."""
        x, y, z = at; s = size
        pos = [(x + dx * s, y + dy * s, z + dz * s) for dx in (0.0, 1.0) for dy in (0.0, 1.0) for dz in (0.0, 1.0)]
        idx = [0, 1, 3, 0, 3, 2, 4, 6, 7, 4, 7, 5, 0, 4, 5, 0, 5, 1, 2, 3, 7, 2, 7, 6, 0, 2, 6, 0, 6, 4, 1, 5, 7, 1, 7, 3]
        normals = [(0.0, 0.0, 1.0)] * 8
        uvs = [(dx, dy) for dx in (0.0, 1.0) for dy in (0.0, 1.0) for _ in (0, 1)]
        uvs2 = [(0.25 + 0.5 * dz, 0.75 - 0.5 * dx) for dx in (0.0, 1.0) for _ in (0, 1) for dz in (0.0, 1.0)]
        cols = [(1.0, 0.5, 0.25, 1.0)] * 8
        if dupes:
            pos = pos + pos; normals = normals + normals; uvs = uvs + uvs; uvs2 = uvs2 + uvs2; cols = cols + cols
            idx = idx + [8, 9, 11, 8, 11, 10, 0, 0, 1]
        attrs = {"POSITION": self.b.accessor(pos, "f", "VEC3"), "NORMAL": self.b.accessor(normals, "f", "VEC3", minmax=False)}
        if uv:
            attrs["TEXCOORD_0"] = self.b.accessor(uvs, "f", "VEC2", minmax=False)
        if uv2:
            attrs["TEXCOORD_1"] = self.b.accessor(uvs2, "f", "VEC2", minmax=False)
        if colors:
            attrs["COLOR_0"] = self.b.accessor(cols, "f", "VEC4", minmax=False)
        if joints is not None:
            per = joints * 8 if len(joints) == 1 else joints   # one set for every vertex, or one per vertex
            if dupes:
                per = list(per) + list(per)
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
        if self.images: extra["images"] = self.images
        if self.textures: extra["textures"] = self.textures
        used = sorted({k for m in self.materials for k in (m.get("extensions") or {})})
        if used: extra["extensionsUsed"] = used
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


def rigged(r, arm_name="Armature", euler_bone=False, material=None, second=None, at=(0.0, 0.0, 0.0)):
    """A two-bone rig under `arm_name` with a skinned box: returns (armature node, root joint, child joint, skin, material)."""
    arm = r.node(arm_name)
    j0 = r.node("Root", arm)
    j1 = r.node("Arm", j0, translation=[0.0, 1.0, 0.0])
    sk = r.skin("Skin", [j0, j1], [ibm(0.0, 0.0, 0.0), ibm(0.0, 1.0, 0.0)])
    m = r.material("Paint", (0.2, 0.4, 0.9)) if material is None else material
    body = r.node("Body", arm, mesh=r.mesh("body", at=at, material=m, second=second, joints=[[(0, 1.0)]] * 4 + [[(0, 0.5), (1, 0.5)]] * 4), skin=sk)
    a = r.anim("Spin")
    r.key(a, j1, "rotation", [0.0, 0.5, 1.0], spin(3, "x", 0.5))
    if euler_bone:
        # the importer keeps the bone in quaternion mode; an Euler mode is a Blender-side state no glTF can ask for
        pass
    return arm, j0, j1, sk, m


def scaled_wrapper(r, factor=0.01):
    """rigged()'s rig under a 'RootNode' wrapper scaled by `factor` (an FBX-born glTF keeps its centimetre root at 0.01): the
    joints' and the clip's translations x 1/factor, the inverse bind matrices of the scaled joints, the skinned box's
    vertices in world units as before - the same model as "plain", the Factory folding the wrapper. Returns (wrapper, root
    joint, child joint, skin, material)."""
    w = r.node("RootNode", scale=[factor, factor, factor])
    arm = r.node("Armature", w)
    j0 = r.node("Root", arm)
    inv = 1.0 / factor
    j1 = r.node("Arm", j0, translation=[0.0, inv, 0.0])
    def sibm(ty):
        return (inv, 0.0, 0.0, 0.0, 0.0, inv, 0.0, 0.0, 0.0, 0.0, inv, 0.0, 0.0, -ty * inv, 0.0, 1.0)
    sk = r.skin("Skin", [j0, j1], [sibm(0.0), sibm(1.0)])
    m = r.material("Paint", (0.2, 0.4, 0.9))
    r.node("Body", arm, mesh=r.mesh("body", material=m, joints=[[(0, 1.0)]] * 4 + [[(0, 0.5), (1, 0.5)]] * 4), skin=sk)
    a = r.anim("Spin")
    r.key(a, j1, "rotation", [0.0, 0.5, 1.0], spin(3, "x", 0.5))
    r.key(a, j1, "translation", [0.0, 1.0], [(0.0, inv, 0.0), (2.0 * inv, inv, 0.0)])
    return w, j0, j1, sk, m


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


# ---- stage 3, step (d2): the rest fold and rebake, the bone cap

def s4_fold_shared(out):
    """Two skinned nodes sharing one mesh and skin, with a location key on the primary: the fold copies the multi-user
    mesh before applying the first modifier (the copy takes the next free name)."""
    r = Rig(); arm, j0, j1, sk, m = rigged(r)
    shared = r.mesh("pad", at=(1.0, 0.0, 0.0), size=0.5, material=m, joints=[[(1, 1.0)]])
    r.node("PadA", arm, mesh=shared, skin=sk, translation=[0.0, 0.0, 1.0])
    r.node("PadB", arm, mesh=shared, skin=sk, translation=[0.0, 0.0, -1.0])
    r.key(0, j0, "translation", [0.0, 1.0], [(0.0, 0.0, 0.0), (0.0, 0.3, 0.0)])
    return r.write(out, "riganim_s4_fold_shared")


def s4_fold_static(out):
    """A static part hung from a bone (kept weightless by staticParts) through the fold: applyarmature_fix_boneparents
    rewrites its properties and parent inverse so it keeps its place; a location key on the primary brings the fold."""
    r = Rig(); arm, j0, j1, sk, m = rigged(r)
    r.node("Canopy", j1, mesh=r.mesh("canopy", at=(0.0, 1.5, 0.0), material=m), translation=[0.2, 0.5, 0.1], rotation=[0.0, 0.3826834, 0.0, 0.9238795])
    r.node("Gun", j1, mesh=r.mesh("gun", at=(0.0, 2.0, 0.5), material=m), translation=[0.0, 1.0, 0.5])
    r.key(0, j1, "translation", [0.0, 1.0], [(0.0, 1.0, 0.0), (0.0, 1.2, 0.0)])
    return r.write(out, "riganim_s4_fold_static")


def s4_fold_nested(out):
    """The nested armatures with a location key on the carrier's bone: the inner armature's mesh is deformed by ITS
    armature's modifier, then re-bound to the script's."""
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
    a = r.anim("Spin")
    r.key(a, j1, "rotation", [0.0, 0.5, 1.0], spin(3, "x", 0.5))
    r.key(a, c0, "rotation", [0.0, 1.0], spin(2, "y", 0.25))
    r.key(a, c0, "translation", [0.0, 1.0], [(0.0, 0.0, 0.0), (0.5, 0.0, 0.0)])
    return r.write(out, "riganim_s4_fold_nested")


def s4_fold_big(out):
    """The multi file at a thousand times the size: the fold's frame-0 residual carries digits in its %.6f."""
    r = Rig()
    arm = r.node("Armature")
    j0 = r.node("Root", arm)
    j1 = r.node("Arm", j0, translation=[0.0, 1000.0, 0.0], rotation=[0.0, 0.0, 0.3826834, 0.9238795])
    sk = r.skin("Skin", [j0, j1], [ibm(0.0, 0.0, 0.0), ibm(0.0, 1000.0, 0.0)])
    m = r.material("Paint", (0.2, 0.4, 0.9))
    r.node("Body", arm, mesh=r.mesh("body", size=1000.0, material=m, joints=[[(0, 1.0)]] * 4 + [[(0, 0.5), (1, 0.5)]] * 4), skin=sk)
    a = r.anim("Longer")
    r.key(a, j1, "rotation", [0.0, 2.0, 4.0], spin(3, "y", 1.0))
    r.key(a, j0, "translation", [0.0, 4.0], [(0.0, 0.0, 0.0), (0.0, 0.0, 2000.0)])
    r.key(a, j1, "translation", [0.0, 4.0], [(0.3, 1000.0, 0.0), (0.3, 1000.0, 0.0)])
    return r.write(out, "riganim_s4_fold_big")


def s4_bonecap(out, weight=1.0, suffix=""):
    """492 bones: a root, 245 chains of a middle bone and a leaf, and a weighted leaf. Pass one removes the 245 leaves
    (the weighted one stays), the count is still over the cap, pass two removes the 238 unweighted middles."""
    r = Rig()
    arm = r.node("Armature")
    root = r.node("Root", arm)
    joints = [root]; ibms = [ibm(0.0, 0.0, 0.0)]
    mids = []
    for i in range(245):
        mid = r.node("Mid_%03d" % i, root, translation=[float(i), 1.0, 0.0]); joints.append(mid); ibms.append(ibm(float(i), 1.0, 0.0)); mids.append(len(joints) - 1)
        leaf = r.node("Leaf_%03d" % i, mid, translation=[0.0, 1.0, 0.0]); joints.append(leaf); ibms.append(ibm(float(i), 2.0, 0.0))
    keep = r.node("KeptLeaf", root, translation=[-1.0, 1.0, 0.0]); joints.append(keep); ibms.append(ibm(-1.0, 1.0, 0.0))
    sk = r.skin("Skin", joints, ibms)
    m = r.material("Paint", (0.2, 0.4, 0.9))
    per = [[(len(joints) - 1, 1.0)]] + [[(mids[v - 1], 1.0)] for v in range(1, 8)]
    if weight != 1.0:
        per[0] = [(len(joints) - 1, weight), (0, 1.0 - weight)]   # sum to one so the importer preserves the threshold weight
    r.node("Body", arm, mesh=r.mesh("body", size=3.0, material=m, joints=per), skin=sk)
    a = r.anim("Spin"); r.key(a, joints[1], "rotation", [0.0, 1.0], spin(2, "x", 0.25))
    return r.write(out, "riganim_s4_bonecap" + suffix)


def s4_fold_keep_name(out, name, suffix):
    """A genuinely translating bone after a conversion fold. keepTranslations keeps a plain name's three location
    curves, but rig_anim.py's literal keep-path set does not match quoted/backslashed names' escaped RNA paths."""
    r = Rig(); arm, root, child, skin, mat = rigged(r)
    r.nodes[child]["name"] = name
    r.key(0, child, "translation", [0.0, 1.0], [(0.0, 1.0, 0.0), (0.0, 1.2, 0.0)])
    return r.write(out, "riganim_s4_fold_keep_" + suffix)


def s4_textured(out, variant):
    """The albedo: a base-colour texture on the first mesh's first material ("albedo", an 8x6 image), on the SECOND mesh
    only ("second": base_color_image returns None for the first, the next object is tried), an emissive texture without a
    base colour ("emissive": the script takes the tree's first image node), the importer's image names ("names": an
    unnamed image is Image_<index>, two images called "tex" are tex and tex.001 in the order the materials are made, the
    body's first material uses the one made first), a KHR_materials_specular colour texture as the first image made and
    the first mesh's only texture ("ext": the albedo), and a mesh made after the body but sorting before it ("aft")."""
    r = Rig()
    if variant == "albedo":
        m = r.material("Paint", texture=r.texture(r.image("Paint_tex", 8, 6)))
        rigged(r, material=m)
    elif variant == "second":
        arm, j0, j1, sk, m = rigged(r)
        tm = r.material("Decal", texture=r.texture(r.image("decal", 4, 2, (10, 200, 30))))
        r.node("Pad", arm, mesh=r.mesh("pad", at=(1.0, 0.0, 0.0), size=0.5, material=tm, joints=[[(1, 1.0)]]), skin=sk)
    elif variant == "emissive":
        m = r.material("Glow", emissive=r.texture(r.image("glow", 2, 2, (0, 255, 0))))
        rigged(r, material=m)
    elif variant == "names":
        # every image used, in an order unlike the index order: the body's first material takes image 2 (made first: "tex"),
        # its second image 1 ("tex.001"), the pad's material image 0 (unnamed: Image_0)
        other = r.image(None, 2, 2); first = r.image("tex", 2, 2); used = r.image("tex", 6, 6, (0, 0, 250))
        m = r.material("Paint", texture=r.texture(used))
        arm, j0, j1, sk, m = rigged(r, material=m, second=r.material("Trim", texture=r.texture(first)))
        r.node("Pad", arm, mesh=r.mesh("pad", at=(1.0, 0.0, 0.0), size=0.5, material=r.material("Other", texture=r.texture(other)), joints=[[(1, 1.0)]]), skin=sk)
    elif variant == "ext":
        # the first mesh made (scene.objects keeps the creation order) carries a material with a KHR_materials_specular colour
        # texture and no base colour texture: its image, made first, is "tex" and - the tree's first image node - the albedo;
        # the body's base colour image, made second, is "tex.001"
        spec_img = r.image("tex", 2, 2); body_img = r.image("tex", 6, 4, (0, 0, 250))
        arm = r.node("Armature")
        j0 = r.node("Root", arm)
        j1 = r.node("Arm", j0, translation=[0.0, 1.0, 0.0])
        sk = r.skin("Skin", [j0, j1], [ibm(0.0, 0.0, 0.0), ibm(0.0, 1.0, 0.0)])
        spec = r.material("Spec", extensions={"KHR_materials_specular": {"specularColorTexture": {"index": r.texture(spec_img)}}})
        r.node("Zpad", arm, mesh=r.mesh("zpad", at=(1.0, 0.0, 0.0), size=0.5, material=spec, joints=[[(1, 1.0)]]), skin=sk)
        m = r.material("Paint", texture=r.texture(body_img))
        r.node("Body", arm, mesh=r.mesh("body", material=m, joints=[[(0, 1.0)]] * 4 + [[(0, 0.5), (1, 0.5)]] * 4), skin=sk)
        a = r.anim("Spin")
        r.key(a, j1, "rotation", [0.0, 0.5, 1.0], spin(3, "x", 0.5))
    elif variant == "aft":
        # a mesh made AFTER the body but sorting before it: its image is made second ("tex.001") and, the first object with
        # materials in the scene's order, it is the albedo
        body_img = r.image("tex", 2, 2); aft_img = r.image("tex", 6, 4, (0, 0, 250))
        arm, j0, j1, sk, m = rigged(r, material=r.material("Paint", texture=r.texture(body_img)))
        r.node("Aft", arm, mesh=r.mesh("aft", at=(-1.0, 0.0, 0.0), size=0.5, material=r.material("Stern", texture=r.texture(aft_img)), joints=[[(0, 1.0)]]), skin=sk)
    return r.write(out, "riganim_s4_tex_" + variant)


def s4_mats(out):
    """Two materials on the skinned box (a triangle beside it in the second): keepmats 1 keeps both slots, keepmats 0
    collapses them to the first; 13 triangles for the decimate."""
    r = Rig()
    second = r.material("Trim", (0.9, 0.2, 0.1))
    rigged(r, second=second)
    return r.write(out, "riganim_s4_mats")


def s4_root(out, variant):
    """A pass-through root: Base (no weights, no keys, one child) over Root over Arm. "collapse" removes Base; "animated"
    keys Base (kept); "two" gives Base a second child (kept)."""
    r = Rig()
    arm = r.node("Armature")
    base_j = r.node("Base", arm)
    j0 = r.node("Root", base_j, translation=[0.0, 0.5, 0.0])
    j1 = r.node("Arm", j0, translation=[0.0, 1.0, 0.0])
    joints = [base_j, j0, j1]; ibms = [ibm(0.0, 0.0, 0.0), ibm(0.0, 0.5, 0.0), ibm(0.0, 1.5, 0.0)]
    if variant == "two":
        extra = r.node("Side", base_j, translation=[1.0, 0.0, 0.0]); joints.append(extra); ibms.append(ibm(1.0, 0.0, 0.0))
    sk = r.skin("Skin", joints, ibms)
    m = r.material("Paint", (0.2, 0.4, 0.9))
    r.node("Body", arm, mesh=r.mesh("body", at=(0.0, 0.5, 0.0), material=m, joints=[[(1, 1.0)]] * 4 + [[(1, 0.5), (2, 0.5)]] * 4), skin=sk)
    a = r.anim("Spin")
    r.key(a, j1, "rotation", [0.0, 0.5, 1.0], spin(3, "x", 0.5))
    if variant == "animated":
        r.key(a, base_j, "rotation", [0.0, 1.0], spin(2, "y", 0.25))
    return r.write(out, "riganim_s4_root_" + variant)


def s4_rebind(out):
    """The skinned mesh has no material (culled as reference junk after the import); a rigid two-material box is a plain
    child of the armature object, so the join's active object carries no Armature modifier and the script re-binds one
    (two materials: the weld is skipped, which the model needs)."""
    r = Rig()
    arm = r.node("Armature")
    j0 = r.node("Root", arm)
    j1 = r.node("Arm", j0, translation=[0.0, 1.0, 0.0])
    sk = r.skin("Skin", [j0, j1], [ibm(0.0, 0.0, 0.0), ibm(0.0, 1.0, 0.0)])
    r.node("Body", arm, mesh=r.mesh("body", joints=[[(0, 1.0)]]), skin=sk)
    m = r.material("Paint", (0.2, 0.4, 0.9)); trim = r.material("Trim", (0.9, 0.2, 0.1))
    r.node("Crate", arm, mesh=r.mesh("crate", at=(1.0, 0.0, 0.0), size=0.5, material=m, second=trim))
    a = r.anim("Spin"); r.key(a, j1, "rotation", [0.0, 0.5, 1.0], spin(3, "x", 0.5))
    return r.write(out, "riganim_s4_rebind")


def s4_nomesh(out):
    """Every mesh culled (no material): the script exits in stage 4 with no mesh to export."""
    r = Rig()
    arm = r.node("Armature")
    j0 = r.node("Root", arm)
    j1 = r.node("Arm", j0, translation=[0.0, 1.0, 0.0])
    sk = r.skin("Skin", [j0, j1], [ibm(0.0, 0.0, 0.0), ibm(0.0, 1.0, 0.0)])
    r.node("Body", arm, mesh=r.mesh("body", joints=[[(0, 1.0)]]), skin=sk)
    a = r.anim("Spin"); r.key(a, j1, "rotation", [0.0, 0.5, 1.0], spin(3, "x", 0.5))
    return r.write(out, "riganim_s4_nomesh")


def s4_centred(out):
    """A box already centred over the origin with its lowest point on the ground (glTF (-0.5, 0, -0.5), size 1): the
    placement finds nothing to move."""
    r = Rig(); rigged(r, at=(-0.5, 0.0, -0.5))
    return r.write(out, "riganim_s4_centred")


def s4_onebone(out):
    """One material, one bone: the weld (bmesh remove_doubles) runs - left by name."""
    r = Rig()
    arm = r.node("Armature")
    j0 = r.node("Root", arm)
    sk = r.skin("Skin", [j0], [ibm(0.0, 0.0, 0.0)])
    m = r.material("Paint", (0.2, 0.4, 0.9))
    r.node("Body", arm, mesh=r.mesh("body", material=m, joints=[[(0, 1.0)]]), skin=sk)
    a = r.anim("Spin"); r.key(a, j0, "rotation", [0.0, 0.5, 1.0], spin(3, "x", 0.5))
    return r.write(out, "riganim_s4_onebone")


def s4_multi_uv(out):
    """Three skinned meshes joined: the body with one UV set, a pad with two (TEXCOORD_1 -> a second layer), a plate with
    none (zeros in every layer); the layers unite by name, the active's active layer stays."""
    r = Rig(); arm, j0, j1, sk, m = rigged(r)
    r.node("Pad", arm, mesh=r.mesh("pad", at=(1.0, 0.0, 0.0), size=0.5, material=m, joints=[[(1, 1.0)]], uv2=True), skin=sk)
    r.node("Plate", arm, mesh=r.mesh("plate", at=(-1.0, 0.0, 0.0), size=0.5, material=m, joints=[[(0, 1.0)]], uv=False), skin=sk)
    return r.write(out, "riganim_s4_multi_uv")


def s4_many(out):
    """The body and four pads, 60 triangles: a target of 1 asks for a ratio of 1/60, under the script's 0.02 floor."""
    r = Rig(); arm, j0, j1, sk, m = rigged(r)
    for i in range(4):
        r.node("Pad%d" % i, arm, mesh=r.mesh("pad%d" % i, at=(1.5 + i, 0.0, 0.0), size=0.5, material=m, joints=[[(i % 2, 1.0)]]), skin=sk)
    return r.write(out, "riganim_s4_many")


def s4_manybones(out):
    """300 weighted bones (the cap keeps a weighted bone): the joints are written as unsigned shorts, every box's vertex on
    its own bone."""
    r = Rig()
    arm = r.node("Armature")
    root = r.node("Root", arm)
    joints = [root]; ibms = [ibm(0.0, 0.0, 0.0)]
    for i in range(300):
        joints.append(r.node("J%03d" % i, root, translation=[float(i % 20), 1.0 + i // 20, 0.0])); ibms.append(ibm(float(i % 20), 1.0 + i // 20, 0.0))
    sk = r.skin("Skin", joints, ibms)
    m = r.material("Paint", (0.2, 0.4, 0.9))
    for box in range(38):
        per = [[(1 + (8 * box + v) % 300, 1.0)] for v in range(8)]
        r.node("Box%02d" % box, arm, mesh=r.mesh("box%02d" % box, at=(float(box % 8), 0.0, float(box // 8)), size=0.5, material=m, joints=per), skin=sk)
    a = r.anim("Spin"); r.key(a, joints[1], "rotation", [0.0, 1.0], spin(2, "x", 0.25))
    return r.write(out, "riganim_s4_manybones")


def f_onekey(out):
    """An animation of one key: Blender's frame_range is (0, 1) for it, the exporter samples frame 0 alone."""
    r = Rig(); arm, j0, j1, sk, m = rigged(r)
    r.anims = []
    a = r.anim("Spin"); r.key(a, j1, "rotation", [0.0], [spin(2, "x", 0.25)[1]])
    return r.write(out, "riganim_f_onekey")


def f_step(out):
    """Sparse STEP keys (the importer's CONSTANT segments): the sampler holds the value between frames 0 and 24."""
    r = Rig(); arm, j0, j1, sk, m = rigged(r)
    r.anims = []
    a = r.anim("Spin"); r.key(a, j1, "rotation", [0.0, 1.0], spin(2, "x", 0.25), interpolation="STEP")
    return r.write(out, "riganim_f_step")


def s4_join_parent(out):
    """The skinned body is a child of a rigid hull mesh (a mesh parent, not a wrapper empty): the hull is joined into the
    body, the body's parent becomes the hull's."""
    r = Rig()
    arm = r.node("Armature")
    j0 = r.node("Root", arm)
    j1 = r.node("Arm", j0, translation=[0.0, 1.0, 0.0])
    sk = r.skin("Skin", [j0, j1], [ibm(0.0, 0.0, 0.0), ibm(0.0, 1.0, 0.0)])
    m = r.material("Paint", (0.2, 0.4, 0.9))
    hull = r.node("Hull", arm, mesh=r.mesh("hull", at=(0.0, -1.0, 0.0), size=2.0, material=m))
    r.node("Body", hull, mesh=r.mesh("body", material=m, joints=[[(0, 1.0)]] * 4 + [[(0, 0.5), (1, 0.5)]] * 4), skin=sk)
    a = r.anim("Spin"); r.key(a, j1, "rotation", [0.0, 0.5, 1.0], spin(3, "x", 0.5))
    return r.write(out, "riganim_s4_join_parent")


def s4_rename_name(out, name, suffix):
    """Topological prefixes exercise bone-name truncation and sequential name collisions."""
    r = Rig(); arm, root, child, skin, mat = rigged(r)
    r.nodes[child]["name"] = name
    return r.write(out, "riganim_s4_rename_" + suffix)


def s4_unicode_siblings(out, translate=False):
    """Python orders U+E000 before U+10000; UTF-16 ordinal comparison reverses those siblings."""
    r = Rig(); arm = r.node("Armature"); root = r.node("Root", arm)
    sup = r.node("\U00010000", root, translation=[0.0, 1.0, 0.0])
    bmp = r.node("\ue000", root, translation=[1.0, 0.0, 0.0])
    skin = r.skin("Skin", [root, sup, bmp], [ibm(0, 0, 0), ibm(0, 1, 0), ibm(1, 0, 0)])
    mat = r.material("Paint")
    r.node("Body", arm, mesh=r.mesh("body", material=mat, joints=[[(0, 1.0)]]), skin=skin)
    a = r.anim("Spin")
    if translate:
        r.key(a, sup, "translation", [0.0, 1.0], [(0.0, 1.0, 0.0), (0.0, 1.2, 0.0)])
        r.key(a, bmp, "translation", [0.0, 1.0], [(1.0, 0.0, 0.0), (1.1, 0.0, 0.0)])
    else:
        r.key(a, sup, "rotation", [0.0, 1.0], spin(2, "x", 0.25))
    return r.write(out, "riganim_s4_unicode_siblings" + ("_translate" if translate else ""))


def s4_image_paths(out, variant):
    """The first mesh's second material loads an image; the second mesh supplies albedo. URI aliases share one image,
    distinct files with the same image name must get two names. This checks the importer's check_existing behavior."""
    r = Rig(); arm = r.node("Armature"); root = r.node("Root", arm)
    child = r.node("Arm", root, translation=[0.0, 1.0, 0.0])
    skin = r.skin("Skin", [root, child], [ibm(0, 0, 0), ibm(0, 1, 0)])
    first = "image_" + variant + ".png"
    with open(os.path.join(out, first), "wb") as f: f.write(png(4, 4, (200, 40, 40)))
    if variant == "distinct":
        second = "image_distinct_other.png"
        with open(os.path.join(out, second), "wb") as f: f.write(png(4, 4, (40, 200, 40)))
    elif variant == "dotdot":
        os.makedirs(os.path.join(out, "nested"), exist_ok=True)
        second = "nested/../" + first
    else:
        second = "./" + first
    r.images = [{"name": "tex", "uri": first}, {"name": "tex", "uri": second}]
    blank = r.material("Blank"); trim = r.material("Trim", texture=r.texture(0)); paint = r.material("Paint", texture=r.texture(1))
    r.node("Zpad", arm, mesh=r.mesh("zpad", material=blank, joints=[[(0, 1.0)]], second=trim), skin=skin)
    r.node("Body", arm, mesh=r.mesh("body", at=(2.0, 0.0, 0.0), material=paint, joints=[[(0, 0.5), (1, 0.5)]]), skin=skin)
    a = r.anim("Spin"); r.key(a, child, "rotation", [0.0, 1.0], spin(2, "x", 0.25))
    return r.write(out, "riganim_s4_image_paths_" + variant)


def f_review_actions(out, names, suffix):
    """Distinct clips expose ID ordering, Python lowercase, and Unicode code-point export ordering."""
    r = Rig(); arm, j0, j1, sk, m = rigged(r)
    for i, name in enumerate(names):
        a = r.anim(name); r.key(a, j1, "rotation", [0.0, 1.0], spin(2, "y", 0.125 * (i + 1)))
    return r.write(out, "riganim_f_review_" + suffix)


def f_review_join_uv(out, variant):
    """A texture naming UVMap.001 sees the joined mesh's layers, including layers from other objects."""
    r = Rig(); image = r.image("texture"); mat = r.material("Tex", texture=r.texture(image))
    r.materials[mat]["pbrMetallicRoughness"]["baseColorTexture"]["texCoord"] = 1
    arm, j0, j1, sk, m = rigged(r, material=mat if variant == "shared" else None)
    if variant != "missing":
        r.meshes[0]["primitives"][0]["attributes"]["TEXCOORD_1"] = r.b.accessor([(0.2, 0.8)] * 8, "f", "VEC2", minmax=False)
    r.node("Zbody", arm, mesh=r.mesh("zbody", material=mat, joints=[[(0, 0.5), (1, 0.5)]], uv2=variant == "present"), skin=sk)
    return r.write(out, "riganim_f_review_uv_" + variant)


def f_review_jpeg(out, declared="image/jpeg", suffix=""):
    """An albedo encoded as PNG by Blender still needs a GLB evidence row even though its bytes cannot be compared."""
    import base64
    r = Rig()
    jpeg = base64.b64decode("/9j/4AAQSkZJRgABAQEASABIAAD/2wBDAAMCAgICAgMCAgIDAwMDBAYEBAQEBAgGBgUGCQgKCgkICQkKDA8MCgsOCwkJDRENDg8QEBEQCgwSExIQEw8QEBD/yQALCAABAAEBAREA/8wABgAQEAX/2gAIAQEAAD8A0s8g/9k=")
    r.images.append({"bufferView": r.b.blob(jpeg), "mimeType": declared, "name": "tiny"})
    mat = r.material("Paint", texture=r.texture(0)); rigged(r, material=mat)
    return r.write(out, "riganim_f_review_jpeg" + suffix)


def unity_import_fixtures(out):
    """Small raw GLBs for UnityRigImportTests: names, atlas slot order, translation scaling, and clip rebasing."""
    os.makedirs(out, exist_ok=True)
    for case in ("plain", "long", "late", "underscore", "twist", "offset"):
        r = Rig(); arm, j0, j1, sk, m = rigged(r)
        if case == "long": r.anims[0]["name"] = "Spin_" + "a" * 120
        if case == "underscore": r.anims[0]["name"] = "_Spin"
        times = [0.5, 1.0] if case == "late" else [0.0, 1.0]
        if case == "late":
            r.anims = []; a = r.anim("Spin"); r.key(a, j1, "rotation", times, spin(2, "x", 0.25))
        # "twist": the same rest and length as "plain", another motion - the acceptance row's per-frame comparison must catch it
        # "offset": the clip's first frame holds the Arm a unit off its rest - the Factory's prefab must sit at that first frame
        first = (1.0, 1.0, 0.0) if case == "offset" else (0.0, 1.0, 0.0)
        r.key(0, j1, "translation", times, [first, (0.0, 1.0, 2.0) if case == "twist" else (2.0, 1.0, 0.0)])
        r.write(out, case)
    r = Rig(); scaled_wrapper(r); r.write(out, "wrapped")   # "plain" under a 0.01 wrapper: the Factory folds it, the two imports are equal
    r = Rig(); arm, j0, j1, sk, m = rigged(r)   # "dupes": the rig's box replaced by one carrying every vertex twice and a collapsed triangle
    r.nodes[[i for i, n in enumerate(r.nodes) if n["name"] == "Body"][0]]["mesh"] = r.mesh("body_dupes", material=m, joints=[[(0, 1.0)]] * 4 + [[(0, 0.5), (1, 0.5)]] * 4, dupes=True)
    r.write(out, "dupes")
    r = Rig(); red = r.material("Red", (1.0, 0.0, 0.0)); blue = r.material("Blue", (0.0, 0.0, 1.0))
    rigged(r, material=blue, second=red)  # submesh order opposite the extraction's material order
    r.write(out, "materials")


def main(out):
    sys.stdout.reconfigure(encoding="utf-8")  # JOB records may contain non-ASCII staticParts on Windows
    os.makedirs(out, exist_ok=True)
    def job(key, path, clip="Spin", convert="1", local="0", static="", prefixes="", roles="", keept="0", albedo="1", keepmats="1", target="0", rot="0,0,0", sockets=""):
        print("JOB\t%s\t%s\t%s" % (key, path, ARGS.format(clip=clip, convert=convert, local=local, static=static, prefixes=prefixes, roles=roles, keept=keept, albedo=albedo, keepmats=keepmats, target=target, rot=rot, sockets=sockets)))
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
    job("EXPORTLEFT:fx_s2_two_arms", s2_two_arms(out))
    job("EXPORTLEFT:fx_s2_nested_arms", s2_nested_arms(out))
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
    # step (d2): the rest fold and rebake, the bone cap
    job("fx_s4_fold_shared", s4_fold_shared(out))
    job("fx_s4_fold_static", s4_fold_static(out), static="canopy")
    job("EXPORTLEFT:fx_s4_fold_nested", s4_fold_nested(out))
    job("fx_s4_multi_keept", multi(out), clip="Longer", keept="1", roles="attack=Held;move=Spin[0..6]")
    job("fx_s4_fold_big", s4_fold_big(out), clip="Longer")
    job("fx_s4_bonecap", s4_bonecap(out))
    # PR #167 review: Python's double threshold protects nominal 0.001f; the lower neighbor is removed, the upper kept.
    for weight, suffix in ((0.000999, "below"), (0.001, "exact"), (0.001001, "above")):
        job("fx_s4_bonecap_" + suffix, s4_bonecap(out, weight, "_" + suffix))
    # The fold's keep set uses literal bone names while fcurves escape them; include the ordinary-name control.
    for name, suffix in (("Arm", "plain"), ('Ar"m', "quote"), (r"Ar\m", "slash")):
        job("fx_s4_fold_keep_" + suffix, s4_fold_keep_name(out, name, suffix), keept="1")
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
    # step (e), stage 4: the albedo, the join, the slots, the decimate, the rebind, the root collapse, the sockets, the
    # fold with a rotation, the placement
    job("fx_s4_albedo", s4_textured(out, "albedo"))
    job("fx_s4_albedo_second", s4_textured(out, "second"))
    job("fx_s4_albedo_emissive", s4_textured(out, "emissive"))
    job("fx_s4_albedo_names", s4_textured(out, "names"))
    job("fx_s4_albedo_aft", s4_textured(out, "aft"))
    job("fx_s4_albedo_ext", s4_textured(out, "ext"))
    job("fx_s4_albedo_off", s4_textured(out, "albedo"), albedo="0")
    mats = s4_mats(out)
    job("fx_s4_keepmats", mats)
    job("fx_s4_keepmats0", mats, keepmats="0")
    job("fx_s4_decimate", mats, target="8")
    job("fx_s4_decimate_one", plain, target="6")
    job("fx_s4_decimate_legacy", mats, target="1", convert="0")
    job("fx_s4_decimate_floor", s4_many(out), target="1")   # 1/60 -> the ratio's 0.02 floor
    job("fx_s4_root_collapse", s4_root(out, "collapse"))
    job("fx_s4_root_animated", s4_root(out, "animated"))
    job("fx_s4_root_two", s4_root(out, "two"))
    job("fx_s4_root_legacy", s4_root(out, "collapse"), convert="0")   # no collapse, no rename on the legacy path
    job("fx_s4_rebind", s4_rebind(out))
    job("EXIT:4:fx_s4_nomesh", s4_nomesh(out))
    job("fx_s4_centred", s4_centred(out))
    job("LEFT:fx_s4_weld", s4_onebone(out))
    job("fx_s4_multi_uv", s4_multi_uv(out))
    job("fx_s4_join_parent", s4_join_parent(out))
    job("fx_s4_sockets", plain, sockets="Canon=Arm@0,0.1,0;Flag=Root;Mast=Arm@x,y")
    job("fx_s4_sockets_rot", plain, sockets="Canon=Arm@0.2,0,0.1", rot="0,90,0")
    job("EXIT:4:fx_s4_socket_noparent", plain, sockets="Canon=Nope")
    job("LEFT:fx_s4_socket_sort", plain, sockets="A=Root")   # the script exits, with a message read from a freed edit bone (undefined)
    job("fx_s4_rot", plain, rot="10,90,-30")
    job("fx_s4_rot_multi", multi(out), clip="Longer", rot="0,0,180")
    job("fx_s4_rot_legacy", plain, rot="90,0,0", convert="0")
    # step (f), the GLB export: 300 weighted bones (joints as shorts), the lone branch no other fixture reaches
    job("fx_f_manybones", s4_manybones(out), albedo="0")
    job("fx_f_onekey", f_onekey(out), albedo="0", convert="0")   # the legacy path keeps the single key as it is (no fold, no pad)
    job("fx_f_step", f_step(out), albedo="0", convert="0")
    # PR #168 review: bone RNA setters cap names at 63 UTF-8 bytes and allocate collisions against live names.
    for name, suffix in (("Arm", "plain"), ("Ar" + "x" * 56, "exact_limit"), ("Arm" + "x" * 60, "long_ascii"),
                         ("é" * 31 + "X", "long_unicode"), ("b000_Root", "collision")):
        job("fx_s4_rename_" + suffix, s4_rename_name(out, name, suffix), albedo="0")
    job("fx_s4_rename_dormant", s4_rename_name(out, "Arm" + "x" * 60, "dormant"), roles="attack=Spin[0..2]", albedo="0")
    job("fx_s4_unicode_siblings", s4_unicode_siblings(out), albedo="0")
    utf = s4_unicode_siblings(out, translate=True)
    job("fx_s4_unicode_fold", utf, keept="1", albedo="0")
    job("EXIT:3:fx_s4_unicode_filter", utf, prefixes="NoBone", albedo="0")
    job("fx_s4_unicode_legacy_keep", utf, convert="0", keept="1", roles="attack=Spin", albedo="0")
    long_socket = "Canon" + "x" * 70
    job("fx_s4_socket_long", plain, sockets=long_socket + "=Arm", albedo="0")
    job("fx_s4_socket_long_twice", plain, sockets=long_socket + "=Arm;" + long_socket + "=Arm@0.1,0,0", albedo="0")
    job("fx_s4_socket_long_unicode", plain, sockets="Canon" + "é" * 35 + "=Arm", albedo="0")
    up = s4_rename_name(out, "\U00010000", "socket_unicode")
    job("LEFT:fx_s4_socket_unicode_sort", up, sockets="\ue000=\U00010000", convert="0", albedo="0")
    for variant in ("dot", "dotdot", "distinct"):
        job("fx_s4_image_paths_" + variant, s4_image_paths(out, variant))

    # PR #169 review: clips and texture coordinates whose export differs despite otherwise equal stages.
    for suffix, names in (("unicode_order", ["\ue000", "\U00010000"]), ("unicode_lower", ["\u0130A", "iz"]),
                          ("unicode_deseret", ["\U00010400z", "\U00010428a"]), ("unicode_sigma", ["αΣ", "ασ"])):
        job("fx_f_review_" + suffix, f_review_actions(out, names, suffix), convert="0", roles="attack=" + names[0] + ";move=" + names[1])
    for variant in ("added", "present", "missing", "shared"):
        job("fx_f_review_uv_" + variant, f_review_join_uv(out, variant))
    job("fx_f_review_jpeg", f_review_jpeg(out))
    job("fx_g_jpeg_declared_png", f_review_jpeg(out, "image/png", "_declared_png"))


if __name__ == "__main__":
    main(sys.argv[1])
