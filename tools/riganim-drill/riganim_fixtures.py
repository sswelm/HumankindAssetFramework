"""Fixtures for rig_anim.py (tools/riganim_drill.sh): the branches of the script no registry recipe takes, each a small
rigged GLB with the arguments that reach the branch. Writes the files and prints JOB lines in riganim_jobs.py's form
(TAB-separated: JOB <key> <input> <argv[2]> ... <argv[14]>). A key that starts with "EXIT:" names a job the script is
expected to STOP on (its own sys.exit(1), e.g. no armature): the drill holds that it stops, and fails a full run of it.
A key that starts with "LEFT:" names a job the C# leaves to Blender by name (localNodeAnim).

usage: riganim_fixtures.py <output directory>

Stage 1 (step (b)): the import, the material-less drop (a lone one, one with a textured child that becomes a root, one
coloured by COLOR_0 - Blender decides whether that counts as a material), the contract marker (an armature named
DeployArmV2), object-level animation on a mesh node (cleared on the conversion path, kept on the legacy one), several
animations (the first one's action is the active one; a longer second one), a bone-parented mesh, a scaled wrapper
empty, a bone in Euler mode, and the exit when there is no armature.
"""
import math, os, sys
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "glb-reader-drill"))
from fixtures import Buf, base, write_glb   # noqa: E402

ARGS = "	".join(["0", "", "{clip}", "1", "1", "0,0,0", "{convert}", "", "0", "", "0", "", "{local}"])   # target, prefixes, clip, albedo, keepmats, rot, convertRig, roles, ground, sockets, keepT, static, localNode


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


def main(out):
    os.makedirs(out, exist_ok=True)
    def job(key, path, clip="Spin", convert="1", local="0"):
        print("JOB\t%s\t%s\t%s" % (key, path, ARGS.format(clip=clip, convert=convert, local=local)))
    job("fx_junk", junk(out))
    p = objanim(out, "riganim_objanim")
    job("fx_objanim_convert", p)
    job("fx_objanim_legacy", p, convert="0")
    job("fx_multi", multi(out), clip="Longer")
    job("fx_multi_first", multi(out), clip="")
    job("fx_wrapped", wrapped(out))
    job("fx_v2", v2(out), convert="0")
    job("EXIT:fx_noarm", noarm(out))
    job("LEFT:fx_localnode", p, local="1")


if __name__ == "__main__":
    main(sys.argv[1])
