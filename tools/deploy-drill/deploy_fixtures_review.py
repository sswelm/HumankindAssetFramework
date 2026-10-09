"""The shapes an independent review of the decisions built (2026-10-10), each run against Blender: eight of them were
wrong or silently wrong in the first port. deploy_fixtures.py calls main(); a key that starts LEFT: must fall back.
Not here: a name with a TAB (the dump is tab-separated; BlenderDeployTests holds Python's repr of it)."""
import os, sys
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE); sys.path.insert(0, os.path.join(HERE, "..", "glb-reader-drill"))
from deploy_fixtures import Scene, DEFAULT, TURN   # noqa
from fixtures import base, write_glb   # noqa


class S(Scene):
    def __init__(self):
        super().__init__(); self.extra = {}; self.anims = None; self.scenes = None; self.scene = 0

    def write(self, out, name):
        extra = dict(self.extra)
        if self.skins: extra["skins"] = self.skins
        anims = self.anims if self.anims is not None else [{"name": "Deploy", "samplers": self.samplers, "channels": self.channels}]
        kw = dict(meshes=self.meshes, nodes=self.nodes, scenes=self.scenes or [{"nodes": self.roots}], animations=anims, **extra)
        if self.scene is not None: kw["scene"] = self.scene
        root = base(name, **kw)
        path = os.path.join(out, name + ".glb")
        write_glb(path, root, self.b)
        return path.replace("\\", "/")

    def part(self, name="Part", parent=None, at=(0.0, 0.0, 0.0), size=1.0, **kw):
        n = self.node(name, parent, mesh=self.mesh(name.lower() + "_m", size), translation=list(at), **kw)
        self.anim(n, "translation", [0.0, 1.0], [tuple(at), (at[0], at[1] + 1.0, at[2])])
        return n


def J(key, f, args=DEFAULT):
    print("%s|%s|%s" % (key, f, args))


def main(out):
    os.makedirs(out, exist_ok=True)

    # R1 morph target, default weight 1: the evaluated mesh (bound_box) is the morphed one
    s = S(); s.node("Hull", None, mesh=s.mesh("hull", 4.0)); s.part()
    pos = [(0.0, 0.0, 0.0), (1.0, 0.0, 0.0), (0.0, 1.0, 0.0)]
    tgt = [(0.0, 0.0, 0.0), (30.0, 0.0, 0.0), (0.0, 0.0, 0.0)]
    s.meshes.append({"name": "morphy", "weights": [1.0], "primitives": [{"attributes": {"POSITION": s.b.accessor(pos, "f", "VEC3")}, "targets": [{"POSITION": s.b.accessor(tgt, "f", "VEC3")}]}]})
    s.node("Morphy", None, mesh=len(s.meshes) - 1)
    J("LEFT:r1_morph", s.write(out, "r1_morph"))

    # R2 two scenes, the default one named: the other scene's collection is excluded from the view layer
    s = S(); a = s.node("Hull", None, mesh=s.mesh("hull", 4.0)); p = s.part()
    o = s.node("Other", None, mesh=s.mesh("other", 2.0), translation=[50.0, 0.0, 0.0])
    op = s.part("OtherPart", at=(60.0, 0.0, 0.0))
    s.scenes = [{"nodes": [a, p]}, {"nodes": [o, op]}]
    J("LEFT:r2_scenes", s.write(out, "r2_scenes"))
    s.scene = None
    J("r2_scenes_nodefault", s.write(out, "r2_scenes_nodefault"))

    # R3 two animations: the first is the active one; B is animated by the second only, and it is the longer
    s = S(); s.node("Hull", None, mesh=s.mesh("hull", 4.0)); pa = s.part("A"); pb = s.node("B", None, mesh=s.mesh("b", 1.0))
    first = {"name": "First", "samplers": s.samplers, "channels": s.channels}
    s.samplers, s.channels = [], []
    s.anim(pb, "translation", [0.0, 3.0], [(0.0, 0.0, 0.0), (0.0, 0.0, 5.0)])
    s.anim(pa, "rotation", [0.0, 2.0], [TURN[0], TURN[2]])
    s.anims = [first, {"name": "Second", "samplers": s.samplers, "channels": s.channels}]
    J("r3_two_anims", s.write(out, "r3_two_anims"))

    # R4 one key, off a whole frame and not at zero
    s = S(); s.node("Hull", None, mesh=s.mesh("hull", 4.0))
    n = s.node("Part", None, mesh=s.mesh("part", 1.0))
    s.anim(n, "translation", [0.3], [(0.0, 2.0, 0.0)])
    J("r4_one_key", s.write(out, "r4_one_key"))
    s = S(); s.node("Hull", None, mesh=s.mesh("hull", 4.0))
    n = s.node("Part", None, mesh=s.mesh("part", 1.0))
    s.anim(n, "translation", [0.3, 1.31], [(0.0, 2.0, 0.0), (0.0, 3.0, 0.0)])
    J("r4_late_start", s.write(out, "r4_late_start"))

    # R5 an object named UnitNormalize: kept (the new root is .001), or stripped (its name is free again)
    for tag, strip in (("keep", ""), ("strip", "unitnorm")):
        s = S(); s.node("Hull", None, mesh=s.mesh("hull", 4.0, at=(40.0, 0.0, 0.0)))
        u = s.node("UnitNormalize", None, scale=[2.0, 2.0, 2.0])
        s.part("Part", u, at=(40.0, 0.0, 0.0))
        s.part("Zed", None, at=(41.0, 0.0, 0.0))
        J("r5_unitnorm_" + tag, s.write(out, "r5_unitnorm_" + tag), "0|24|%s||||||0|||4|0|1" % strip)

    # R6 a part under a turned parent with one axis at zero scale: the part turned so its world axes are not zero
    s = S(); s.node("Hull", None, mesh=s.mesh("hull", 4.0))
    z = s.node("ZeroAxis", None, rotation=[0.1830127, 0.5, 0.1830127, 0.8365163], scale=[0.0, 1.0, 1.0], translation=[1.0, 2.0, 3.0])
    n = s.node("Under", z, mesh=s.mesh("under", 1.0), rotation=[0.3535534, 0.3535534, 0.1464466, 0.8535534], translation=[0.5, 0.25, 0.125])
    s.anim(n, "translation", [0.0, 1.0], [(0.5, 0.25, 0.125), (0.5, 1.25, 0.125)])
    s.part("Good", None, at=(1.0, 0.0, 0.0))
    J("r6_zero_parent", s.write(out, "r6_zero_parent"))

    # R7 a mesh on the armature's own node (".mesh" child), the armature's node animated, bones animated
    s = S(); s.node("Hull", None, mesh=s.mesh("hull", 4.0))
    arm = s.node("Rig", None, mesh=s.mesh("rigmesh", 2.0), translation=[3.0, 1.0, 0.5], rotation=[0.0, 0.3826834, 0.0, 0.9238795], scale=[1.5, 1.5, 1.5])
    j0 = s.node("J0", arm, translation=[0.0, 1.0, 0.0]); j1 = s.node("J1", j0, translation=[0.0, 1.0, 0.0])
    s.node("Soldier", arm, mesh=s.mesh("soldiermesh", 1.0, skinned=True), skin=0)
    s.skins.append({"joints": [j0, j1]})
    s.anim(j1, "rotation", [0.0, 1.0], [TURN[0], TURN[1]])
    s.part("Good", None, at=(1.0, 0.0, 0.0))
    f = s.write(out, "r7_arm_mesh"); J("r7_arm_mesh", f)
    s.anim(arm, "translation", [0.0, 2.0], [(3.0, 1.0, 0.5), (4.0, 1.0, 0.5)])
    J("r7_arm_animated", s.write(out, "r7_arm_animated"))

    # R8 names: case-only differences, non-ASCII, long, and culled names with quotes
    s = S(); s.node("Hull", None, mesh=s.mesh("hull", 4.0))
    for i, nm in enumerate(["alpha", "Alpha", "ALPHA", "\u00c9mile", "zeta", "_under", "Zeta", "a" * 45, "caf\u00e9 part", "[bracket", "~tilde", "alpha.001", "Alpha.001"]):
        s.part(nm, None, at=(0.5 * i, 0.0, 0.0))
    for nm in ["It's", "Say\"hi'", "back\\slash", "\uff21wide"]:
        n = s.node(nm, None, mesh=s.mesh("m" + str(len(s.meshes)), 1.0))
        s.anim(n, "scale", [0.0, 1.0], [(1.0, 1.0, 1.0), (0.0, 1.0, 1.0)])
    J("r8_names", s.write(out, "r8_names"))
    s = S(); s.node("Hull", None, mesh=s.mesh("hull", 4.0))
    for nm in ["\U0001F600part", "\uff21wide", "plain"]:
        n = s.node(nm, None, mesh=s.mesh("m" + str(len(s.meshes)), 1.0))
        s.anim(n, "scale", [0.0, 1.0], [(1.0, 1.0, 1.0), (0.0, 1.0, 1.0)])
    s.part("\U0001F600ok", None, at=(1.0, 0.0, 0.0))
    J("r8_astral", s.write(out, "r8_astral"))

    # R9 a mirrored root whose ROTATION is animated, parked far off: the script's assignment decomposes, frame_set rewrites the rotation only
    s = S(); s.node("Hull", None, mesh=s.mesh("hull", 4.0, at=(40.0, 0.0, 0.0)))
    n = s.node("MirrorTurn", None, mesh=s.mesh("mt", 1.0), translation=[41.0, 1.0, 2.0], rotation=list(TURN[1]), scale=[1.0, -2.0, 1.0])
    s.anim(n, "rotation", [0.0, 1.0], [TURN[1], TURN[2]])
    n = s.node("ZeroRoot", None, mesh=s.mesh("zr", 1.0), translation=[41.0, 1.0, 2.0], rotation=list(TURN[1]), scale=[1.0, 0.0, 3.0])
    n = s.node("ZeroRootAnim", None, mesh=s.mesh("zra", 1.0), translation=[42.0, 1.0, 2.0], rotation=list(TURN[1]), scale=[0.0, 0.0, 3.0])
    s.anim(n, "translation", [0.0, 1.0], [(42.0, 1.0, 2.0), (43.0, 1.0, 2.0)])
    n = s.node("Unnormal", None, mesh=s.mesh("un", 1.0), translation=[39.0, 1.0, 2.0], rotation=[0.0, 0.6, 0.0, 1.9])
    s.anim(n, "scale", [0.0, 1.0], [(1.0, 1.0, 1.0), (2.0, 2.0, 2.0)])
    n = s.node("ShearAnim", None, mesh=s.mesh("sa", 1.0), matrix=[1.0, 0.3, 0.0, 0.0, 0.2, 2.0, 0.1, 0.0, 0.0, 0.4, 0.5, 0.0, 38.0, 1.0, -2.0, 1.0])
    s.anim(n, "translation", [0.0, 1.0], [(38.0, 1.0, -2.0), (38.0, 2.0, -2.0)])
    J("r9_roots", s.write(out, "r9_roots"))

    # R10 bound boxes: unused vertices, points, lines, several primitives, one mesh on two nodes
    s = S()
    pos = [(0.0, 0.0, 0.0), (1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (90.0, 90.0, 90.0), (0.0, 0.0, 7.0)]
    pa = s.b.accessor(pos, "f", "VEC3")
    tri = s.b.accessor([0, 1, 2], "H", "SCALAR", minmax=False)
    pts = s.b.accessor([4], "H", "SCALAR", minmax=False)
    lpos = s.b.accessor([(0.0, -6.0, 0.0), (0.0, 0.0, 0.0), (5.0, 0.0, 0.0)], "f", "VEC3")
    s.meshes.append({"name": "mixed", "primitives": [{"attributes": {"POSITION": pa}, "indices": tri}, {"attributes": {"POSITION": pa}, "indices": pts, "mode": 0}, {"attributes": {"POSITION": lpos}, "indices": s.b.accessor([0, 1], "H", "SCALAR", minmax=False), "mode": 1}]})
    mi = len(s.meshes) - 1
    s.node("MixedA", None, mesh=mi, translation=[3.0, 0.0, 0.0]); s.node("MixedB", None, mesh=mi, scale=[2.0, 2.0, 2.0])
    deg = s.b.accessor([(0.0, 0.0, 0.0), (1.0, 0.0, 0.0), (1.0, 0.0, 0.0), (0.0, 0.0, -11.0)], "f", "VEC3")
    s.meshes.append({"name": "degen", "primitives": [{"attributes": {"POSITION": deg}, "indices": s.b.accessor([0, 1, 2, 1, 1, 3], "H", "SCALAR", minmax=False)}]})
    s.node("Degen", None, mesh=len(s.meshes) - 1)
    s.part()
    J("r10_boxes", s.write(out, "r10_boxes"))

    # R11 an orphan node (in no scene), one scene; and a file with no scenes at all
    s = S(); a = s.node("Hull", None, mesh=s.mesh("hull", 4.0)); p = s.part()
    s.node("Orphan", None, mesh=s.mesh("orphan", 2.0), translation=[50.0, 0.0, 0.0]); s.part("OrphanPart", at=(60.0, 0.0, 0.0))
    s.scenes = [{"nodes": [a, p]}]
    J("LEFT:r11_orphan", s.write(out, "r11_orphan"))

    # R13 a skinned mesh node that is animated (it stays an empty, with a .skinned child), stripped by the default list
    s = S(); s.node("Hull", None, mesh=s.mesh("hull", 4.0))
    root = s.node("Root", None)
    j = s.node("CrewJoint", root, translation=[0.0, 0.5, 0.0])
    sb = s.node("SoldierBody", root, mesh=s.mesh("soldierbody", 1.0, skinned=True), skin=0)
    s.skins.append({"joints": [j]})
    s.anim(sb, "translation", [0.0, 4.0], [(0.0, 0.0, 0.0), (0.0, 1.0, 0.0)])
    s.part("Good", None, at=(1.0, 0.0, 0.0))
    J("r13_skinned_anim", s.write(out, "r13_skinned_anim"))

    # R18 the strip list's edges
    s = S(); s.node("Hull", None, mesh=s.mesh("hull", 4.0)); s.part("Gun"); s.part("gUN.001", at=(1.0, 0, 0)); s.part("Barrel", at=(2.0, 0, 0))
    f = s.write(out, "r18_strip")
    J("r18_strip_blank", f, "0|24|  ||||||0|||4|0|1")
    J("r18_strip_case", f, "0|24| GuN ,zzz||||||0|||4|0|1")
    J("r18_extra_blank", f, "0|24|||||||0|||4|0|1|  ")
    J("r18_extra_only", f, "0|24|||||||0|||4|0|1|barrel")
    J("r18_short", f, "0|24")
    J("r18_shortest", f, "")

    # R14 the state before frame_set(fmin) is not the import's: a key ON frame 1 with -0 (a flipped quaternion), +0 at frame 0
    s = S(); s.node("Hull", None, mesh=s.mesh("hull", 4.0))
    n = s.node("Part", None, mesh=s.mesh("part", 1.0), rotation=[0.3826834, 0.0, 0.0, 0.9238795])
    s.anim(n, "rotation", [0.0, 1.0 / 24.0, 1.0], [(0.0, 0.0, 0.0, 1.0), (0.0, 0.0, 0.0, -1.0), (0.0, 0.3826834, 0.0, 0.9238795)])
    J("r14_frame1_zero", s.write(out, "r14_frame1_zero"))

    # R1b morph target, default weight 0, the weight animated (1 at the start); R1c weight 0 and static
    for tag, w, anim in (("b", 0.0, True), ("c", 0.0, False)):
        s = S(); s.node("Hull", None, mesh=s.mesh("hull", 4.0)); s.part()
        pos = [(0.0, 0.0, 0.0), (1.0, 0.0, 0.0), (0.0, 1.0, 0.0)]
        tgt = [(0.0, 0.0, 0.0), (30.0, 0.0, 0.0), (0.0, 0.0, 0.0)]
        s.meshes.append({"name": "morphy", "weights": [w], "primitives": [{"attributes": {"POSITION": s.b.accessor(pos, "f", "VEC3")}, "targets": [{"POSITION": s.b.accessor(tgt, "f", "VEC3")}]}]})
        mn = s.node("Morphy", None, mesh=len(s.meshes) - 1)
        if anim:
            s.samplers.append({"input": s.b.accessor([0.0, 2.0], "f", "SCALAR"), "output": s.b.accessor([1.0, 0.0], "f", "SCALAR", minmax=False)})
            s.channels.append({"sampler": len(s.samplers) - 1, "target": {"node": mn, "path": "weights"}})
        J("LEFT:r1%s_morph" % tag, s.write(out, "r1%s_morph" % tag))

    # R8b Latin-1 names only (the stock run can print them)
    s = S(); s.node("Hull", None, mesh=s.mesh("hull", 4.0))
    for i, nm in enumerate(["zeta", "Émile", "~tilde", "café", "cafz"]):
        s.part(nm, None, at=(0.5 * i, 0.0, 0.0))
    J("r8b_latin", s.write(out, "r8b_latin"))

    # R20 %-40s pads by code point - no normalization, so the order question stays out
    s = S(); n = s.node("😀p", None, mesh=s.mesh("m0", 1.0, at=(-0.5, 0.0, 0.0)))
    s.anim(n, "translation", [0.0, 1.0], [(0.0, 0.0, 0.0), (0.0, 0.1, 0.0)])
    J("r20_pad", s.write(out, "r20_pad"))

    # R21 EXT_mesh_gpu_instancing, used and not required
    s = S(); s.node("Hull", None, mesh=s.mesh("hull", 4.0)); s.part()
    m = s.mesh("inst", 1.0)
    s.node("Inst", None, mesh=m, extensions={"EXT_mesh_gpu_instancing": {"attributes": {"TRANSLATION": s.b.accessor([(10.0, 0.0, 0.0), (20.0, 0.0, 0.0)], "f", "VEC3", minmax=False)}}})
    s.extra["extensionsUsed"] = ["EXT_mesh_gpu_instancing"]
    J("LEFT:r21_instancing", s.write(out, "r21_instancing"))


if __name__ == "__main__":
    main(sys.argv[1])
