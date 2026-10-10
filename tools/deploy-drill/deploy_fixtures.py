"""Fixtures for the decisions of deploy_convert.py (tools/deploy_drill.sh, part 2): each shape a branch of the script
takes that no registry source does. Writes the files and prints one JOB line per run:  <key>|<file>|<argv[2]>|...

usage: deploy_fixtures.py <output directory>
"""
import os, sys
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "glb-reader-drill"))
from fixtures import Buf, base, write_glb   # noqa: E402

DEFAULT = "0|24|||||||0|||4|0|1"
FLAT3_SEEDS = (6, 7, 14, 18, 20, 24)


class Scene:
    def __init__(self):
        self.b = Buf(); self.nodes = []; self.meshes = []; self.samplers = []; self.channels = []; self.roots = []; self.skins = []

    def mesh(self, name, size=1.0, at=(0.0, 0.0, 0.0), skinned=False):
        x, y, z = at
        pos = [(x, y, z), (x + size, y, z), (x, y + size * 0.5, z + size * 0.25)]
        attrs = {"POSITION": self.b.accessor(pos, "f", "VEC3")}
        if skinned:
            attrs["JOINTS_0"] = self.b.accessor([(0, 0, 0, 0)] * 3, "H", "VEC4", minmax=False)
            attrs["WEIGHTS_0"] = self.b.accessor([(1, 0, 0, 0)] * 3, "f", "VEC4", minmax=False)
        self.meshes.append({"name": name, "primitives": [{"attributes": attrs}]})
        return len(self.meshes) - 1

    def node(self, name, parent=None, **kw):
        n = {"name": name}; n.update(kw)
        self.nodes.append(n); i = len(self.nodes) - 1
        if parent is None:
            self.roots.append(i)
        else:
            self.nodes[parent].setdefault("children", []).append(i)
        return i

    def anim(self, node, path, times, values):
        kind = "VEC4" if path == "rotation" else "VEC3"
        self.samplers.append({"input": self.b.accessor(times, "f", "SCALAR"), "output": self.b.accessor(values, "f", kind, minmax=False)})
        self.channels.append({"sampler": len(self.samplers) - 1, "target": {"node": node, "path": path}})

    def write(self, out, name):
        extra = {"skins": self.skins} if self.skins else {}
        root = base(name, meshes=self.meshes, nodes=self.nodes, scenes=[{"nodes": self.roots}], scene=0,
                    animations=[{"name": "Deploy", "samplers": self.samplers, "channels": self.channels}], **extra)
        path = os.path.join(out, name + ".glb")
        write_glb(path, root, self.b)
        return path.replace("\\", "/")


TURN = [(0.0, 0.0, 0.0, 1.0), (0.0, 0.3826834, 0.0, 0.9238795), (0.0, 0.7071068, 0.0, 0.7071068)]


def small(out, name, unit=1.0, skin=False):
    """A hull under a turned and scaled root; a turret that turns with a gun that slides; a rotor at the scene's root
    that is itself animated; a crew mesh (killed by the default list) whose child must stay, as a root; a prop killed by
    its MESH's name alone; parked well off the origin. `unit` scales every length."""
    s = Scene(); u = unit
    root = s.node("Root", translation=[30.0 * u, 2.0 * u, -12.0 * u], rotation=[0.0, 0.2588190, 0.0, 0.9659258], scale=[1.25, 1.25, 1.25])
    s.node("Hull", root, mesh=s.mesh("hull", 8.0 * u))
    turret = s.node("Turret", root, mesh=s.mesh("turret", 3.0 * u), translation=[0.0, 2.0 * u, 0.0])
    s.anim(turret, "rotation", [0.0, 0.5, 1.0], TURN)
    gun = s.node("Gun", turret, mesh=s.mesh("gun", 2.0 * u), translation=[1.0 * u, 0.5 * u, 0.0])
    s.anim(gun, "translation", [0.0, 1.0], [(1.0 * u, 0.5 * u, 0.0), (2.5 * u, 0.5 * u, 0.0)])
    crew = s.node("Crew_Soldier", turret, mesh=s.mesh("crewmesh", 1.0 * u), translation=[0.0, 1.0 * u, 0.0], rotation=[0.3826834, 0.0, 0.0, 0.9238795])
    # the crew is stripped, and its curve is the longest: the action's range runs over it all the same
    s.anim(crew, "translation", [0.0, 1.5], [(0.0, 1.0 * u, 0.0), (0.0, 2.0 * u, 0.0)])
    hat = s.node("Hat", crew, mesh=s.mesh("hat", 0.5 * u), translation=[0.2 * u, 0.4 * u, 0.1 * u], scale=[1.0, -1.0, 1.0])
    s.anim(hat, "scale", [0.0, 1.0], [(1.0, -1.0, 1.0), (1.5, -1.5, 1.5)])
    s.node("Prop", root, mesh=s.mesh("ShellCase", 1.0 * u), translation=[4.0 * u, 0.0, 0.0])
    rotor = s.node("Rotor", None, mesh=s.mesh("rotor", 2.0 * u), translation=[28.0 * u, 6.0 * u, -11.0 * u])
    s.anim(rotor, "rotation", [0.0, 0.25, 0.5, 0.75, 1.0], [(0.0, 0.0, 0.0, 1.0), (0.0, 0.0, 0.7071068, 0.7071068), (0.0, 0.0, 1.0, 0.0), (0.0, 0.0, 0.7071068, -0.7071068), (0.0, 0.0, 0.0, -1.0)])
    # a mirrored root nothing animates: `o.matrix_world = keep` gives it three NEGATIVE sizes and another rotation
    s.node("Mirror", None, mesh=s.mesh("mirror", 1.0 * u), translation=[27.0 * u, 3.0 * u, -10.0 * u], scale=[1.0, -1.0, 1.0])
    # a root part turned half round about X, its rotation keyed and constant
    flip = s.node("Flip", None, mesh=s.mesh("flip", 1.0 * u), translation=[29.0 * u, 1.0 * u, -10.0 * u], rotation=[1.0, 0.0, 0.0, 0.0])
    s.anim(flip, "rotation", [0.0, 1.0], [(1.0, 0.0, 0.0, 0.0), (1.0, 0.0, 0.0, 0.0)])
    # a matrix node with unequal axes and a slight shear: `o.matrix_world = keep` cannot give it back as it was
    s.node("Sheared", None, mesh=s.mesh("sheared", 1.0 * u), matrix=[1.0, 0.1, 0.0, 0.0, 0.0, 2.0, 0.0, 0.0, 0.0, 0.0, 0.5, 0.0, 26.0 * u, 1.0 * u, -9.0 * u, 1.0])
    if skin:
        # a skinned crew mesh: stripped by name, its armature and the importer's bone shape stay
        j = s.node("CrewJoint", root, translation=[0.0, 0.5 * u, 0.0])
        s.node("SoldierBody", root, mesh=s.mesh("soldierbody", 1.0 * u, skinned=True), skin=0)
        s.skins.append({"joints": [j]})
    return s.write(out, name)


def cull(out):
    """Parts the degenerate cull takes, each for one of its reasons, and what hangs below one."""
    s = Scene()
    s.node("Hull", None, mesh=s.mesh("hull", 6.0))
    ok = s.node("Good", None, mesh=s.mesh("good", 2.0), translation=[1.0, 0.0, 0.0])
    s.anim(ok, "translation", [0.0, 1.0], [(1.0, 0.0, 0.0), (2.0, 0.0, 0.0)])
    # a scale that passes through zero on a frame the cull samples (frame 14 of 0..24: every seventh)
    flat = s.node("Flat", None, mesh=s.mesh("flat", 1.0))
    s.anim(flat, "scale", [0.0, 14.0 / 24.0, 1.0], [(1.0, 1.0, 1.0), (1.0, 0.0, 1.0), (1.0, 1.0, 1.0)])
    s.node("FlatChild", flat, mesh=s.mesh("flatchild", 1.0))
    kid = s.node("FlatPart", flat, mesh=s.mesh("flatpart", 1.0))
    s.anim(kid, "translation", [0.0, 1.0], [(0.0, 0.0, 0.0), (0.0, 1.0, 0.0)])
    # far out on the LAST frame only (the sample `+ [fmax]`): 24 is not a multiple of seven
    far = s.node("Far", None, mesh=s.mesh("far", 1.0))
    s.anim(far, "translation", [0.0, 23.0 / 24.0, 1.0], [(0.0, 0.0, 0.0), (0.0, 0.0, 0.0), (2.0e6, 0.0, 0.0)])
    # the garbage is LOCAL: a huge local translation under a tiny parent - the world matrix is sane throughout
    tiny = s.node("Tiny", None, scale=[1.0e-9, 1.0e-9, 1.0e-9])
    local = s.node("LocalBad", tiny, mesh=s.mesh("localbad", 1.0), translation=[2.0e9, 0.0, 0.0], scale=[1.0e9, 1.0e9, 1.0e9])
    s.anim(local, "rotation", [0.0, 1.0], [(0.0, 0.0, 0.0, 1.0), (0.0, 0.3826834, 0.0, 0.9238795)])
    # a scale just inside the limit stays: 2e-6 is not below 1e-6
    small_ = s.node("SmallScale", None, mesh=s.mesh("smallscale", 1.0), scale=[2.0e-6, 2.0e-6, 2.0e-6])
    s.anim(small_, "translation", [0.0, 1.0], [(0.0, 0.0, 0.0), (0.0, 0.0, 1.0)])
    return s.write(out, "deploy_cull")


def frame0(out, name, times, values):
    s = Scene(); s.node("Hull", None, mesh=s.mesh("hull", 4.0, at=(-2.0, 0.0, -0.5)))
    p = s.node("Gun", None, mesh=s.mesh("gun", 1.0, at=(-0.5, 0.0, -0.125)), translation=[0.0, 1.0, 0.0], rotation=[0.0, 0.2588190, 0.0, 0.9659258])
    s.anim(p, "translation", times, values)
    return s.write(out, name)


def flat3(out, seed):
    """The review's (part 4): a part FLATTENED on one axis on frame 3 - a frame the cull does not sample - with a turned
    child part and a grandchild. The parent's pose has no inverse there: Eigen's float32 determinant is exactly 0 and
    Blender bakes zeros; a determinant in double did not cancel and gave NaN. Whether the float32 one cancels hangs on
    the rotations: the seeds in main() are those of forty on which a double determinant bakes other keys."""
    import random, math
    r = random.Random(seed)
    def rq():
        q = [r.gauss(0, 1) for _ in range(4)]; n = math.sqrt(sum(c * c for c in q)); return [round(c / n, 4) for c in q]
    s = Scene(); s.node("Hull", None, mesh=s.mesh("hull", 6.0))
    p = s.node("Flat", None, mesh=s.mesh("flat", 1.0), translation=[1.0, 2.0, 0.5], rotation=rq())
    s.anim(p, "scale", [0.0, 2 / 24.0, 3 / 24.0, 4 / 24.0, 1.0], [(1, 1, 1), (1, 1, 1), (1, 0, 1), (1, 1, 1), (1, 1, 1)])
    c = s.node("Kid", p, mesh=s.mesh("kid", 0.5), translation=[0.5, 0.25, 0.125], rotation=rq())
    s.anim(c, "translation", [0.0, 1.0], [(0.5, 0.25, 0.125), (0.5, 0.5, 0.125)])
    g = s.node("GrandKid", c, mesh=s.mesh("gk", 0.5), translation=[0.125, 0.25, 0.375])
    s.anim(g, "translation", [0.0, 1.0], [(0.125, 0.25, 0.375), (0.5, 0.25, 0.375)])
    return s.write(out, "deploy_flat3_%d" % seed)


def wall(out, links, pads, wrappers, name, small_classes=0, turning=False, crew=None, span=1.0, link="Link"):
    """Over the bone wall: chains of instanced parts ("Link.007": the script groups by the name before the first dot),
    a few of a class too small to merge, and animated wrappers no mesh hangs from."""
    s = Scene()
    s.node("Hull", None, mesh=s.mesh("hull", 10.0))
    m = s.mesh("link", 0.2)
    def chain(base_name, count, y):
        for i in range(count):
            n = s.node(base_name if i == 0 else "%s.%03d" % (base_name, i), None, mesh=m, translation=[0.3 * i, y, 0.0])
            s.anim(n, "translation", [0.0, span], [(0.3 * i, y, 0.0), (0.3 * i + 1.0, y, 0.0)])
    chain(link, links, 0.0); chain("Pad", pads, 1.0); chain("Few", 6, 2.0)
    for c in range(small_classes):
        chain("Class%d" % c, 7, 4.0 + c)
    if crew is not None:
        # an imported armature beside the parts, nothing hanging from its bones (crew: is a bone's SCALE animated?). The
        # bake bakes it too - it is selected - and the scale-free step counts its curves and the importer's
        rig = s.node("Rig", None, translation=[1.0, 6.0, -0.5], scale=[1.5, 1.5, 1.5])
        j0 = s.node("J0", rig, translation=[0.0, 1.0, 0.0]); j1 = s.node("J1", j0, translation=[0.3, 1.2, 0.1], scale=[1.2, 0.8, 1.5])
        _crew(s, rig, [j0, j1], "SoldierBody")
        s.anim(j0, "rotation", [0.0, 1.0], [(0.0, 0.0, 0.0, 1.0), (0.0, 0.3826834, 0.0, 0.9238795)])
        if crew:
            s.anim(j1, "scale", [0.0, 1.0], [(1.2, 0.8, 1.5), (0.9, 1.4, 1.1)])
    if turning:
        # the bake's rotations on the contract path: a turret that turns right round (its quaternion changes sign on
        # the way: make_compatible in the bake, the hemisphere rule in the delta-form rebase), a part turned at the
        # bind frame, and a LEG - the rebase leaves every bone with "leg" in its name alone
        t = s.node("Turret", None, mesh=m, translation=[2.0, 5.0, 0.0])
        s.anim(t, "rotation", [0.0, 0.25, 0.5, 0.75, 1.0], [(0.0, 0.0, 0.0, 1.0), (0.0, 0.7071068, 0.0, 0.7071068), (0.0, 1.0, 0.0, 0.0), (0.0, 0.7071068, 0.0, -0.7071068), (0.0, 0.0, 0.0, -1.0)])
        b = s.node("Barrel", t, mesh=m, translation=[0.5, 0.2, 0.0], rotation=[0.2588190, 0.0, 0.0, 0.9659258], scale=[1.0, 2.0, 0.5])
        s.anim(b, "rotation", [0.0, 1.0], [(0.2588190, 0.0, 0.0, 0.9659258), (0.0, 0.0, 0.3826834, 0.9238795)])
        leg = s.node("LandingLeg", None, mesh=m, translation=[-2.0, 5.0, 0.0], rotation=[0.0, 0.0, 0.3826834, 0.9238795])
        s.anim(leg, "rotation", [0.0, 1.0], [(0.0, 0.0, 0.3826834, 0.9238795), (0.3826834, 0.0, 0.0, 0.9238795)])
    for i in range(wrappers):
        w = s.node("Wrap%d" % i, None, translation=[0.0, 3.0, 0.1 * i])
        s.anim(w, "translation", [0.0, 1.0], [(0.0, 3.0, 0.1 * i), (1.0, 3.0, 0.1 * i)])
        if i == 0:   # a wrapper a mesh does hang from, through an unanimated empty: it is a binding target and stays
            s.node("Hung", s.node("Between", w), mesh=m)
    return s.write(out, name)


def bones(out, name="deploy_bones", extras=False):
    """Objects that hang from BONES the animation moves (the dugout canoe's shape, and more): a bone that turns, one
    that slides and is scaled unevenly; under them an object that is itself animated (a part), one with a child, one
    that only rides. The skinned crew that makes the armature is stripped by the default list."""
    s = Scene()
    s.node("Hull", None, mesh=s.mesh("hull", 6.0))
    rig = s.node("Rig", None, translation=[1.0, 0.5, -0.5], rotation=[0.0, 0.2588190, 0.0, 0.9659258], scale=[1.5, 1.5, 1.5])
    j0 = s.node("J0", rig, translation=[0.0, 1.0, 0.0], rotation=[0.1305262, 0.0, 0.0, 0.9914449])
    j1 = s.node("J1", j0, translation=[0.3, 1.2, 0.1], scale=[1.2, 0.8, 1.5])
    j2 = s.node("J2", j1, translation=[0.0, 0.7, 0.0])
    s.node("SoldierBody", rig, mesh=s.mesh("soldierbody", 1.0, skinned=True), skin=0)
    s.skins.append({"joints": [j0, j1, j2]})
    s.anim(j0, "rotation", [0.0, 0.5, 1.0], [(0.1305262, 0.0, 0.0, 0.9914449), (0.0, 0.3826834, 0.0, 0.9238795), (0.0, -0.7071068, 0.0, -0.7071068)])
    s.anim(j1, "translation", [0.0, 1.0], [(0.3, 1.2, 0.1), (0.6, 1.5, -0.2)])
    s.anim(j1, "scale", [0.0, 1.0], [(1.2, 0.8, 1.5), (0.9, 1.4, 1.1)])
    s.anim(j2, "rotation", [0.0, 1.0], [(0.0, 0.0, 0.0, 1.0), (0.0, 0.0, 0.3826834, 0.9238795)])
    hang = s.node("Hang", j1, mesh=s.mesh("hang", 1.0), translation=[0.2, 0.5, -0.3], rotation=[0.0, 0.0, 0.2588190, 0.9659258], scale=[1.0, 2.0, 0.5])
    s.anim(hang, "translation", [0.0, 1.0], [(0.2, 0.5, -0.3), (0.2, 1.0, -0.3)])
    s.anim(hang, "rotation", [0.0, 1.0], [(0.0, 0.0, 0.2588190, 0.9659258), (0.2588190, 0.0, 0.0, 0.9659258)])
    s.node("Below", hang, mesh=s.mesh("below", 0.5), translation=[0.0, 0.3, 0.0])
    s.node("Rider", j2, mesh=s.mesh("rider", 0.5), translation=[0.1, 0.0, 0.2], rotation=[0.3826834, 0.0, 0.0, 0.9238795])
    s.node("RootRider", j0, mesh=s.mesh("rootrider", 0.5))
    p = s.node("Good", None, mesh=s.mesh("good", 1.0), translation=[2.0, 0.0, 0.0])
    s.anim(p, "translation", [0.0, 1.0], [(2.0, 0.0, 0.0), (2.0, 1.0, 0.0)])
    if extras:
        # `extras` on a joint and on the armature's node become custom properties, and the bake keys those too
        s.nodes[j1]["extras"] = {"power": 2.5, "tag": "x", "n": 3}
        s.nodes[rig]["extras"] = {"power": 2.5}
    return s.write(out, name)


def _crew(s, parent, joints, name):
    s.node(name, parent, mesh=s.mesh(name.lower(), 1.0, skinned=True), skin=len(s.skins))
    s.skins.append({"joints": joints})


def frame1(out, name="deploy_frame1"):
    """The review's: no negative zero anywhere in the file, and still a sign to get wrong. A bone whose rest pose
    location is (-0, 0, 0) in its own frame; its first key moves it along two axes only. When the import returns the
    property already holds the action at frame 1 - not zero on X - so the +0 the first frame set brings IS written."""
    h = 0.5 ** 0.5
    s = Scene(); s.node("Hull", None, mesh=s.mesh("hull", 6.0))
    p = s.node("Good", None, mesh=s.mesh("good", 1.0), translation=[2.0, 0.0, 0.0])
    s.anim(p, "translation", [0.0, 1.0], [(2.0, 0.0, 0.0), (2.0, 1.0, 0.0)])
    rig = s.node("Rig", None, translation=[0.25, 0.25, 0.0])
    j0 = s.node("J0", rig, translation=[0.25, 0.0, 0.5])
    j1 = s.node("J1", j0, translation=[0.0, 0.25, 0.5], rotation=[-h, 0.0, 0.0, -h])
    s.node("Rider", j1, mesh=s.mesh("rider", 0.5), translation=[0.1, 0.2, 0.3])
    _crew(s, rig, [j0, j1], "SoldierBody")
    s.anim(j1, "translation", [0.0, 0.5], [(0.0, 1.0, -0.5), (-1.0, 2.0, 0.25)])
    return s.write(out, name)


def nested(out, name="deploy_nested"):
    """The review's: an armature that hangs (through an object) from ANOTHER armature's bone, and an object under the
    inner armature's bone whose node comes BEFORE both in the file - the inner armature's matrix must be the posed one."""
    import math
    def q(axis, deg):
        a = math.radians(deg) / 2.0; v = [0.0, 0.0, 0.0, math.cos(a)]; v[axis] = math.sin(a); return v
    s = Scene(); s.node("Hull", None, mesh=s.mesh("hull", 6.0))
    p = s.node("Good", None, mesh=s.mesh("good", 1.0), translation=[2.0, 0.0, 0.0])
    s.anim(p, "translation", [0.0, 1.0], [(2.0, 0.0, 0.0), (2.0, 1.0, 0.0)])
    s.nodes.append({"name": "Inner", "mesh": s.mesh("inner", 0.5), "translation": [0.1, 0.2, 0.3], "rotation": q(1, 30)}); inner = len(s.nodes) - 1
    s.anim(inner, "translation", [0.0, 1.0], [(0.1, 0.2, 0.3), (0.4, 0.2, 0.3)])
    rig = s.node("RigA", None, translation=[1.0, 0.5, -0.5], rotation=q(1, 30))
    a0 = s.node("A0", rig, translation=[0.0, 1.0, 0.0], rotation=q(0, 15))
    a1 = s.node("A1", a0, translation=[0.3, 1.2, 0.1])
    x = s.node("X", a1, translation=[0.2, 0.3, 0.4], rotation=q(2, 40), scale=[1.5, 1.5, 1.5])
    b0 = s.node("B0", x, translation=[0.0, 0.5, 0.0], rotation=q(0, -25))
    b1 = s.node("B1", b0, translation=[0.1, 0.6, 0.0])
    s.nodes[b1].setdefault("children", []).append(inner)
    s.node("Late", b1, mesh=s.mesh("late", 0.5), translation=[0.3, 0.0, 0.0])
    _crew(s, rig, [a0, a1], "SoldierBodyA"); _crew(s, rig, [b0, b1], "SoldierBodyB")
    s.anim(a1, "rotation", [0.0, 1.0], [(0.0, 0.0, 0.0, 1.0), tuple(q(2, 50))])
    s.anim(b0, "rotation", [0.0, 1.0], [tuple(q(0, -25)), tuple(q(1, 60))])
    return s.write(out, name)


def near(out, name, ride=True, names=False):
    """A model that stands at the origin (no normalization: its static mesh has NO parent, so StaticRoot is anchored to
    the mesh itself). `ride=False`: the only part is an empty no mesh hangs from - no mesh rides a bone, no travel is
    measured. `names`: bone names that collide - a part called StaticRoot, two parts whose names agree in their first
    63 bytes, an object called DeployArm (the armature's own name is taken)."""
    s = Scene()
    s.node("Hull", None, mesh=s.mesh("hull", 4.0, at=(-2.0, 0.0, -0.5)))
    if ride:
        p = s.node("Gun", None, mesh=s.mesh("gun", 1.0, at=(-0.5, 0.0, -0.125)), translation=[0.0, 1.0, 0.0])
        s.anim(p, "rotation", [0.0, 1.0], [(0.0, 0.0, 0.0, 1.0), (0.0, 0.3826834, 0.0, 0.9238795)])
        c = s.node("Sight", p, mesh=s.mesh("sight", 0.25, at=(-0.125, 0.0, 0.0)), translation=[0.0, 0.5, 0.0])
        s.anim(c, "translation", [0.0, 1.0], [(0.0, 0.5, 0.0), (0.0, 0.75, 0.0)])
    else:
        e = s.node("Spinner", None, translation=[0.0, 1.0, 0.0])
        s.anim(e, "rotation", [0.0, 1.0], [(0.0, 0.0, 0.0, 1.0), (0.0, 0.3826834, 0.0, 0.9238795)])
    if names:
        long_ = "L" * 63
        for i, nm in enumerate(["StaticRoot", long_ + "_first", long_ + "_second", "DeployArm"]):
            n = s.node(nm, None, mesh=s.mesh("m%d" % i, 0.5, at=(-0.25, 0.0, 0.0)), translation=[0.0, 0.25 * i, 0.0])
            if nm != "DeployArm":
                s.anim(n, "translation", [0.0, 1.0], [(0.0, 0.25 * i, 0.0), (0.0, 0.25 * i + 0.05, 0.0)])
    return s.write(out, name)


def chain(out):
    """The review's (part 3): 140 instanced links, each the CHILD of the one before. The bone budget merges some away;
    a link whose direct parent was merged gets a ROOT bone - the merged part's entry joins bone_of only after the
    parents are mirrored (a port that added it first passed every other job)."""
    s = Scene(); s.node("Hull", None, mesh=s.mesh("hull", 10.0, at=(-5.0, 0.0, -1.0)))
    m = s.mesh("link", 0.2); prev = None
    for i in range(140):
        prev = s.node("Link" if i == 0 else "Link.%03d" % i, prev, mesh=m, translation=[0.03, 0.01, 0.0], rotation=[0.0, 0.0499792, 0.0, 0.9987503])
        s.anim(prev, "translation", [0.0, 1.0], [(0.03, 0.01, 0.0), (0.03, 0.02, 0.0)])
    return s.write(out, "deploy_chain")


def ties(out, seed):
    """The review's (part 3): many parts carrying ONE mesh, turned and scaled so that their volumes are equal or an
    ulp apart - which of them is the biggest (the root-motion anchor) hangs on the float32 order of `dimensions`."""
    import random, math
    r = random.Random(seed); s = Scene()
    def rq():
        while True:
            q = [r.gauss(0, 1) for _ in range(4)]; n = math.sqrt(sum(c * c for c in q))
            if n > 1e-3:
                return [c / n for c in q]
    s.node("Hull", None, mesh=s.mesh("hull", 4.0, at=(-2.0, 0.0, -0.5)))
    m = s.mesh("same", 2.0, at=(-1.0, 0.0, -0.25)); mode = ["rot", "rotscale", "matrix", "nest"][seed % 4]; prev = None
    for i in range(40):
        kw = dict(translation=[r.uniform(-1, 1), r.uniform(0, 1), r.uniform(-1, 1)], rotation=rq())
        if mode == "rotscale":
            v = r.uniform(0.5, 2.0); w = r.uniform(0.5, 2.0); kw["scale"] = [v, w, 1.0 / (v * w)]
        if mode == "matrix":
            kw = dict(matrix=[1.0, r.uniform(-.3, .3), 0.0, 0.0, r.uniform(-.3, .3), 1.0, r.uniform(-.3, .3), 0.0, 0.0, 0.0, 1.0, 0.0, r.uniform(-1, 1), r.uniform(0, 1), 0.0, 1.0])
        a = s.node("P%03d" % i, prev if (mode == "nest" and r.random() < 0.7) else None, mesh=m, **kw)
        t0 = tuple(kw.get("translation", (0.0, 0.0, 0.0)))
        if mode == "matrix":
            s.anim(a, "rotation", [0.0, 1.0], [tuple(rq()), tuple(rq())])
        else:
            s.anim(a, "translation", [0.0, 1.0], [t0, (t0[0], t0[1] + r.choice([0.01, 1.0, 3.0]), t0[2])])
        prev = a
    return s.write(out, "deploy_ties_%d" % seed)


def flatx(out, name, travel):
    """The review's (part 3): every mesh flat in X. The normalization's size is 0 by its guard (`mx.x > mn.x`); the
    travel gate's model size has NO such guard and is the Y/Z extent - a small move must not anchor the armature."""
    s = Scene()
    def flat(mesh_name, size):
        s.meshes.append({"name": mesh_name, "primitives": [{"attributes": {"POSITION": s.b.accessor([(0.0, 0.0, 0.0), (0.0, size, 0.0), (0.0, 0.0, size)], "f", "VEC3")}}]})
        return len(s.meshes) - 1
    s.node("Wall", None, mesh=flat("wall", 4.0))
    p = s.node("Door", None, mesh=flat("door", 1.0))
    s.anim(p, "translation", [0.0, 1.0], [(0.0, 0.0, 0.0), (0.0, travel, 0.0)])
    return s.write(out, name)


def fire_limits(out, name, skin=False, contract=False):
    """A clip starting at frame 24, sampled at the Int32 limits: scene.frame_set clamps, snapshot keys do not.
    The imported rig checks its rebaked evaluator; the large rig checks the contract path too."""
    s = Scene(); s.node("Hull", mesh=s.mesh("hull", 6.0))
    p = s.node("Gun", mesh=s.mesh("gun", 1.0))
    s.anim(p, "translation", [1.0, 2.0], [(0, 0, 0), (0.04, 0, 0)])
    if skin:
        rig = s.node("Rig", translation=[0.2, 0.0, 0.0])
        j0 = s.node("J0", rig, translation=[0, 1, 0]); j1 = s.node("J1", j0, translation=[0, 0.5, 0])
        _crew(s, rig, [j0, j1], "SoldierBody")
        s.anim(j1, "translation", [1.0, 2.0], [(0, 0.5, 0), (0.1, 0.5, 0)])
        s.node("Rider", j1, mesh=s.mesh("rider", 0.2))
    if contract:
        for i in range(125):
            p = s.node("Part%03d" % i, mesh=s.mesh("part%d" % i, 0.1), translation=[i * 0.02, 0, 0])
            s.anim(p, "translation", [1.0, 2.0], [(i * 0.02, 0, 0), (i * 0.02, 0.03, 0)])
    return s.write(out, name)


def huge(out):
    """The review's (5a): a part thrown past float32's range in world space on frame 3 - a frame the cull does not
    sample. It is the root-motion anchor, so the armature's matrix overflows there and its determinant is NaN: Eigen
    gives the ZERO matrix for it (it asks abs(det) > 0), Blender bakes zeros, a port that asked det == 0 baked NaN."""
    s = Scene(); s.node("Hull", None, mesh=s.mesh("hull", 4.0, at=(-2.0, 0.0, -0.5)))
    root = s.node("Root", None, scale=[2.0, 2.0, 2.0])
    p = s.node("Gun", root, mesh=s.mesh("gun", 1.0), translation=[0.0, 1.0, 0.0])
    s.anim(p, "translation", [0.0, 2 / 24.0, 3 / 24.0, 4 / 24.0, 1.0], [(0, 1, 0), (0, 1, 0), (3.0e38, 1, 0), (0, 1, 0), (0, 2, 0)])
    return s.write(out, "deploy_huge")


def gunnery(out, name="deploy_gunnery", barrel=True):
    """A gun for the script's steps 5b and 5c: a barrel that elevates and a cannon tube under it that slides (their
    names decide: "barrel", "cannon"), two legs that spread ("leg"), a carriage that rolls. The clip runs to frame 36."""
    import math
    def q(axis, deg):
        a = math.radians(deg) / 2.0; v = [0.0, 0.0, 0.0, math.cos(a)]; v[axis] = math.sin(a); return v
    s = Scene(); s.node("Hull", None, mesh=s.mesh("hull", 4.0, at=(-2.0, 0.0, -0.5)))
    car = s.node("Carriage", None, mesh=s.mesh("carriage", 2.0, at=(-1.0, 0.0, -0.25)), translation=[0.0, 0.5, 0.0])
    s.anim(car, "translation", [0.0, 1.5], [(0.0, 0.5, 0.0), (0.0, 0.5, 0.4)])
    if barrel:
        b = s.node("Main_Barrel", car, mesh=s.mesh("barrelmesh", 1.5, at=(0.0, 0.0, -0.1)), translation=[0.2, 0.6, 0.0], rotation=q(2, 5))
        s.anim(b, "rotation", [0.0, 0.5, 1.0, 1.5], [tuple(q(2, 5)), tuple(q(2, 20)), tuple(q(2, 48)), tuple(q(2, 63))])
        c = s.node("CANNON tube", b, mesh=s.mesh("tube", 1.0, at=(0.0, 0.0, -0.05)), translation=[0.5, 0.0, 0.0])
        s.anim(c, "translation", [0.0, 1.0, 1.5], [(0.5, 0.0, 0.0), (0.9, 0.0, 0.0), (0.7, 0.1, 0.0)])
    for leg, sign in (("L_Leg", 1.0), ("r_LEG", -1.0)):
        l = s.node(leg, car, mesh=s.mesh(leg.lower(), 1.0, at=(-1.0, 0.0, -0.05)), translation=[-0.5, 0.0, 0.3 * sign], rotation=q(1, 4 * sign))
        s.anim(l, "rotation", [0.0, 0.5, 1.5], [tuple(q(1, 4 * sign)), tuple(q(1, 38 * sign)), tuple(q(1, 55 * sign))])
        s.anim(l, "translation", [0.0, 1.5], [(-0.5, 0.0, 0.3 * sign), (-0.6, 0.05, 0.3 * sign)])
    return s.write(out, name)


def gunnery_general(out, name, seed):
    """Barrels and legs about general axes: names with a dot and with brackets, a bone that is a barrel AND a leg
    (its location is cleared too), a leg that turns the long way round (250 degrees) and one that stands still."""
    import math, random
    def qa(ax, deg):
        n = math.sqrt(sum(c * c for c in ax)); a = math.radians(deg) / 2.0
        return [ax[0] / n * math.sin(a), ax[1] / n * math.sin(a), ax[2] / n * math.sin(a), math.cos(a)]
    r = random.Random(seed); s = Scene()
    s.node("Hull", None, mesh=s.mesh("hull", 4.0, at=(-2.0, 0.0, -0.5)))
    car = s.node("Carriage", None, mesh=s.mesh("carriage", 2.0, at=(-1.0, 0.0, -0.25)), translation=[0.0, 0.5, 0.0], rotation=qa((1, 2, 3), 20))
    s.anim(car, "translation", [0.0, 1.5], [(0.0, 0.5, 0.0), (0.3, 0.5, 0.4)])
    s.anim(car, "rotation", [0.0, 1.5], [tuple(qa((1, 2, 3), 20)), tuple(qa((1, 2.5, 3), 31))])
    def rnd(): return (r.uniform(-1, 1), r.uniform(-1, 1), r.uniform(-1, 1))
    for n in ("Barrel", "Barrel.001", "my cannon[2]", "BARREL_Leg", "Leg.L", "leg", "LEGO far", "Leg_near"):
        a0, a1, a2 = rnd(), rnd(), rnd(); r0 = qa(a0, r.uniform(0, 40)); t0 = [r.uniform(-1, 1) for _ in range(3)]
        nd = s.node(n, car, mesh=s.mesh("m" + n, 1.0, at=(0.0, 0.0, -0.05)), translation=t0, rotation=r0)
        if n == "LEGO far":
            s.anim(nd, "rotation", [0.0, 0.25, 0.5, 1.5], [tuple(qa(a0, d)) for d in (0, 125, 250, 300)])
        elif n == "Leg_near":
            s.anim(nd, "rotation", [0.0, 0.5, 1.5], [tuple(r0), tuple(r0), tuple(r0)])
            s.anim(nd, "translation", [0.0, 1.5], [tuple(t0), (t0[0] + 0.1, t0[1], t0[2])])
        else:
            s.anim(nd, "rotation", [0.0, 0.5, 1.0, 1.5], [tuple(r0), tuple(qa(a1, r.uniform(10, 170))), tuple(qa(a2, r.uniform(10, 170))), tuple(qa(a1, r.uniform(10, 170)))])
            s.anim(nd, "translation", [0.0, 1.0, 1.5], [tuple(t0), tuple(rnd()), tuple(rnd())])
    return s.write(out, name)


def gunnery_guard(out):
    """Barrels whose ready pose is a hair off the rest (0.001 to 0.06 degrees, about Z and about a general axis): the
    half angle's sine is under 0.0005 and Quaternion.to_axis_angle divides by 1 instead - Blender keys -0.0 there."""
    import math
    def qa(ax, deg):
        n = math.sqrt(sum(c * c for c in ax)); a = math.radians(deg) / 2.0
        return [ax[0] / n * math.sin(a), ax[1] / n * math.sin(a), ax[2] / n * math.sin(a), math.cos(a)]
    s = Scene(); s.node("Hull", None, mesh=s.mesh("hull", 4.0, at=(-2.0, 0.0, -0.5)))
    car = s.node("Carriage", None, mesh=s.mesh("carriage", 2.0, at=(-1.0, 0.0, -0.25)), translation=[0.0, 0.5, 0.0])
    s.anim(car, "translation", [0.0, 1.5], [(0.0, 0.5, 0.0), (0.0, 0.5, 0.4)])
    for i, d in enumerate((-0.01, -0.02, -0.03, -0.04, -0.045, -0.05, -0.055, 0.02, 0.05, -0.06, -0.001)):
        ax = (1, -2, 3) if i % 2 else (0, 0, 1)
        b = s.node("Barrel_%d" % i, car, mesh=s.mesh("b%d" % i, 1.0, at=(0.0, 0.0, -0.1)), translation=[0.2 * i, 0.6, 0.0], rotation=qa(ax, 5))
        s.anim(b, "rotation", [0.0, 0.5, 1.5], [tuple(qa(ax, 5)), tuple(qa(ax, 25)), tuple(qa(ax, d))])
        s.anim(b, "translation", [0.0, 1.5], [(0.2 * i, 0.6, 0.0), (0.2 * i, 0.6 - 0.001 * i, 0.0)])
    return s.write(out, "deploy_gunnery_guard")


def gunnery_signs(out):
    """A held zero's sign: parts that are no barrel and no leg (never re-keyed) turning about ONE axis of their own past
    a quarter turn at frame 12 - the bake keys -0 in the two idle components there (frames 9 to 12), +0 before - about a
    general axis at the last frame, at rest on the first. A frame set that lands on such a frame from a non-zero one
    writes the -0; one that comes from a +0 does not (an equal value is not written)."""
    import math
    def q(axis, deg):
        a = math.radians(deg) / 2.0; v = [0.0, 0.0, 0.0, math.cos(a)]; v[axis] = math.sin(a); return v
    def qa(ax, deg):
        n = math.sqrt(sum(c * c for c in ax)); a = math.radians(deg) / 2.0
        return [ax[0] / n * math.sin(a), ax[1] / n * math.sin(a), ax[2] / n * math.sin(a), math.cos(a)]
    s = Scene(); s.node("Hull", None, mesh=s.mesh("hull", 4.0, at=(-2.0, 0.0, -0.5)))
    car = s.node("Carriage", None, mesh=s.mesh("carriage", 2.0, at=(-1.0, 0.0, -0.25)), translation=[0.0, 0.5, 0.0])
    s.anim(car, "translation", [0.0, 1.5], [(0.0, 0.5, 0.0), (0.0, 0.5, 0.4)])
    b = s.node("Main_Barrel", car, mesh=s.mesh("barrelmesh", 1.5, at=(0.0, 0.0, -0.1)), translation=[0.2, 0.6, 0.0], rotation=q(2, 5))
    s.anim(b, "rotation", [0.0, 0.5, 1.0, 1.5], [tuple(q(2, 5)), tuple(q(2, 20)), tuple(q(2, 48)), tuple(q(2, 63))])
    l = s.node("L_Leg", car, mesh=s.mesh("lleg", 1.0, at=(-1.0, 0.0, -0.05)), translation=[-0.5, 0.0, 0.3], rotation=q(1, 4))
    s.anim(l, "rotation", [0.0, 0.5, 1.5], [tuple(q(1, 4)), tuple(q(1, 38)), tuple(q(1, 55))])
    i = 0
    for parent in (None, car):
        for ax in (0, 1, 2):
            for deg in (-135, 135, -100, 170, -179):
                n = "Arm%d" % i; i += 1
                nd = s.node(n, parent, mesh=s.mesh("m" + n, 1.0, at=(0.0, 0.0, -0.05)), translation=[0.25 * i, 0.5, 0.0])
                s.anim(nd, "rotation", [0.0, 0.25, 0.5, 1.0, 1.5], [(0.0, 0.0, 0.0, 1.0), tuple(q(ax, deg / 2)), tuple(q(ax, deg)), tuple(qa((1, 2, 3), 50)), tuple(qa((3, -2, 1), 70))])
                s.anim(nd, "translation", [0.0, 0.5, 1.5], [(0.25 * i, 0.5, 0.0), (0.25 * i, 0.5, 0.0), (0.25 * i + 0.1, 0.4, 0.2)])
    return s.write(out, "deploy_gunnery_signs")


def half(out):
    """The lowest point exactly 0.125 below zero: Python prints the vertical offset as 0.12 (the exact half goes to the
    even digit), .NET's own formatting as 0.13."""
    s = Scene()
    s.node("Hull", None, mesh=s.mesh("hull", 4.0, at=(40.0, -0.125, 0.0)))
    p = s.node("Part", None, mesh=s.mesh("part", 1.0, at=(40.0, 0.0, 0.0)))
    s.anim(p, "translation", [0.0, 1.0], [(0.0, 0.0, 0.0), (0.0, 1.0, 0.0)])
    return s.write(out, "deploy_half")


def main(out):
    os.makedirs(out, exist_ok=True)
    f = small(out, "deploy_small", 1.0, skin=True)
    print("small_default|%s|%s" % (f, DEFAULT))
    # a strip list GIVEN replaces the default one: the bone shape is not in it and stays; "shell" hits a mesh's name
    print("small_strip|%s|0|24|soldier, SHELL ||||||0|||4|0|1" % f)
    # stripExtra on top of the default list; the recoil step off while a recoil range is given
    print("small_extra|%s|0|24||||| 5,6 ||0||||0|1|gun, HAT" % f)
    # everything animated stripped: no action left, the range falls to 1..1 and the script stops
    print("small_none|%s|0|24|turret,gun,hat,rotor,soldier,flip||||||0|||4|0|1" % f)
    # an EMPTY name in the list is in every name: nothing is left at all
    print("small_empty|%s|0|24|turret,,gun||||||0|||4|0|1" % f)
    f = small(out, "deploy_tiny", 0.001)
    print("tiny|%s|%s" % (f, DEFAULT))
    # 0.4 across: still under the half unit the x100 gate asks for
    print("tiny_edge|%s|%s" % (small(out, "deploy_tiny_edge", 0.01), DEFAULT))
    print("half|%s|%s" % (half(out), DEFAULT))
    # 5b and 5c: argv[5] the ready frame, argv[6] the leg scale, argv[7] the barrel scale, argv[3] the deploy's end.
    # The ready frame ON the new curve's last key, past it, and before its first; the scales 1.5 / 0.5, 0 / 0, 1 / 1;
    # a barrel alone, legs alone, a gun without a barrel; an end of 2, of 1 (both keys on one frame) and of 0 (the
    # "end" key before the "mid" one); a ready frame INSIDE the new Bezier segment (nothing evaluates there: the legs'
    # frame_sets land on keys)
    fg = gunnery(out)
    print("gun_on_key|%s|0|24||24|0.5|1.5|||0|||4|0|1" % fg)
    print("gun_past|%s|0|24||30|0|0|||0|||4|0|1" % fg)
    print("gun_before|%s|0|24||3|1|1|||0|||4|0|1" % fg)
    print("gun_barrel_only|%s|0|24||36||2|||0|||4|0|1" % fg)
    print("gun_legs_only|%s|0|24|||0.25||||0|||4|0|1" % fg)
    print("gun_no_scale|%s|0|24||36|||||0|||4|0|1" % fg)
    print("gun_end2|%s|0|2||36|0.5|1.5|||0|||4|0|1" % fg)
    print("gun_end1|%s|0|1||36|0.5|1.5|||0|||4|0|1" % fg)
    print("gun_end0|%s|0|0||36|0.5|1.5|||0|||4|0|1" % fg)
    print("gun_no_barrel|%s|0|24||24|0.5|1.5|||0|||4|0|1" % gunnery(out, "deploy_gunnery_legs", barrel=False))
    print("gun_fire|%s|0|24||30|0.5|1.5|26,3|34,9/2|1|||0|0|1" % fg)
    print("gun_odd|%s|0|25||36|0.5|1.5|||0|||4|0|1" % fg)       # an odd end: the mid frame is int(12.5) = 12
    # the contract path with a chain of "Cannon" links: the merged ones ride a neighbour's bone, so bone_of.values()
    # names that bone twice - it is re-keyed twice and counted twice in the log
    print("wall_cannon|%s|0|24||24||1.5|||0|||4|0|1" % wall(out, 110, 30, 0, "deploy_wall_cannon", link="Cannon"))
    print("gun_inside|%s|0|24||18|0.5|1.5|||0|||4|0|1" % fg)
    # what the bones HOLD after the retarget is the end pose assigned last, not the curve at the ready frame (mode_set
    # evaluates nothing): a barrel alone, the ready frame before the end - before the first key, on it, inside the
    # segment, with a fire window, with the end at 0, on the contract path
    print("gun_held_before|%s|0|24||3||1.5|||0|||4|0|1" % fg)
    print("gun_held_first|%s|0|24||0||1.5|||0|||4|0|1" % fg)
    print("gun_held_mid|%s|0|24||12||1.5|||0|||4|0|1" % fg)
    print("gun_held_inside|%s|0|24||18||1.5|||0|||4|0|1" % fg)
    print("gun_held_fire|%s|0|24||3||1.5|26,3|34,9/2|1|||0|0|1" % fg)
    print("gun_held_end0|%s|0|0||36||1.5|||0|||4|0|1" % fg)
    fw = wall(out, 110, 30, 0, "deploy_wall_gun", turning=True)
    print("wall_held|%s|0|24||3||1.5|||0|||4|0|1" % fw)
    print("wall_gun|%s|0|24||24|0.5|1.5|||0|||4|0|1" % fw)
    print("wall_gun_end1|%s|0|1||24|0.5|2.4|||0|||4|0|1" % fw)
    # a key that is already there is MOVED by the difference (old + (new - old) in float32): both barrel keys on frame
    # 1, the difference in a higher binade than the value (scales past a 120 degree turn; 3 gives +0 where the value
    # is -0), and the legs' first and last key on frame 0
    for bs in ("2", "2.9", "3", "-2.3"):
        print("gun_replace_%s|%s|0|1||36||%s|||0|||4|0|1" % (bs, fg, bs))
    print("gun_replace_legs|%s|0|0|||0.9||||0|||4|0|1" % fg)
    # scales as Python's float() reads them: a negative zero stays negative (the log prints x-0.00); the turn wrapped
    # past pi; forms with a sign, a bare point, an exponent, blanks
    print("gun_bs_negzero|%s|0|24||36||-0|||0|||4|0|1" % fg)
    print("gun_ls_negzero|%s|0|24|||-0.0||||0|||4|0|1" % fg)
    print("gun_bs_wrap|%s|0|24||36||5.9|||0|||4|0|1" % fg)
    print("gun_bs_huge|%s|0|24||36||1e6|||0|||4|0|1" % fg)
    print("gun_bs_forms|%s|0|24||36|.5|+.5e1|||0|||4|0|1" % fg)
    print("gun_ls_forms|%s|0|24|| 24| 1E0 |1.|||0|||4|0|1" % fg)
    # .NET Framework used to read this decimal one ulp above Python, printing leg scale x0.40 instead of x0.39.
    print("gun_ls_decimal|%s|0|24|||0.39499999999999999||||0|||4|0|1" % fg)
    print("gun_bs_decimal|%s|0|24||36||0.39499999999999999|||0|||4|0|1" % fg)
    for seed in (1, 2, 3):
        f = gunnery_general(out, "deploy_gunnery_general%d" % seed, seed)
        print("gun_gen%d_a|%s|0|24||36|0.3|1.7|||0|||4|0|1" % (seed, f))
        print("gun_gen%d_b|%s|0|24||24|0.85|0.4|||0|||4|0|1" % (seed, f))
        print("gun_gen%d_c|%s|0|24||30||3.3|||0|||4|0|1" % (seed, f))
        print("gun_gen%d_d|%s|0|24|||0.05||||0|||4|0|1" % (seed, f))
        print("gun_gen%d_e|%s|0|1||36|0.6|2.6|||0|||4|0|1" % (seed, f))
        print("gun_gen%d_f|%s|0|0||36|0.6|2.6|||0|||4|0|1" % (seed, f))
    f = gunnery_guard(out)
    print("gun_guard_1|%s|0|24||36||1|||0|||4|0|1" % f)
    print("gun_guard_x|%s|0|24||36||1.5|||0|||4|0|1" % f)
    print("gun_guard_big|%s|0|24||36||9000|||0|||4|0|1" % f)
    print("gun_guard_first|%s|0|24||0||2|||0|||4|0|1" % f)
    # a negative end with a leg scale: the legs' frame_set lands INSIDE the barrel's new Bezier segment (-5..1), and
    # what the legs are slerped from is the curve's value there
    print("gun_end_negative|%s|0|-5||36|0.5|1.5|||0|||4|0|1" % fg)
    print("gun_end_negative1|%s|0|-1||0|0.5|1.5|||0|||4|0|1" % fg)
    for seed in (1, 2, 3):
        print("gun_gen%d_negative|%s|0|-40||30|0.7|2.2|||0|||4|0|1" % (seed, gunnery_general(out, "deploy_gunnery_general%d" % seed, seed)))
    # ends far outside the clip: the barrel's segment lies where only the sweep about its keys looks
    print("gun_end_5000|%s|0|5000||36|0.5|1.5|||0|||4|0|1" % fg)
    print("gun_end_65537|%s|0|65537||36||2.2|||0|||4|0|1" % fg)
    print("gun_end_minus5000|%s|0|-5000||12|0.25|0.7|||0|||4|0|1" % fg)
    # a held zero's sign: the fire window's last frame is a -0 frame reached from a non-zero one, and the frame set
    # after it (the last-frame probe, then the sweep) must start from what THAT left
    fs = gunnery_signs(out)
    print("gun_signs_12|%s|0|24||12||1.5|30,10|36,12|1|||0|0|1" % fs)
    print("gun_signs_11|%s|0|24||11||1.5|30,9|36,11|1|||0|0|1" % fs)
    # left to Blender from the bake on: a barrel scale past a float
    print("BAKELEFT:gun_bs_overflow|%s|0|24||36||1e39|||0|||4|0|1" % fg)
    # (a leg scale outside 0..1 is not here: Blender itself fails on it - BlenderDeployTests hold the fallback)
    # 5a, the fire-window snapshot: the recoil range is a list of starts (argv[8]) and of ends with a speed step
    # (argv[9]); the step (argv[10]) switches it on. Two segments, the second past the clip's last frame (the baked
    # curves are held there); two segments that overlap; a range with the step off; more starts than ends (the lists are zipped); a segment that runs backwards (no frame,
    # still in the log) with a step of 0 (read as 1); and the same on the contract path, where the keys are rebased
    fs = small(out, "deploy_fire", 1.0)
    print("fire_two|%s|0|24||||| 3,10 |8, 45/2|1|||0|0|1" % fs)   # the clip ends at 36: 37..45 hold its last keys
    print("fire_overlap|%s|0|24|||||3,5|8,10|1|||0|0|1" % fs)          # 3..8 and 5..10 share four frames: 8 are captured, not 12
    print("fire_off|%s|0|24|||||3|8|0|||0|0|1" % fs)
    print("fire_huge|%s|0|24|||||1|6|1|||0|0|1" % huge(out))                   # a range, and the step 0: the fire cycle is off, no snapshot
    print("fire_uneven|%s|0|24|||||3,10,15|8|1|||0|0|1" % fs)
    print("fire_backwards|%s|0|24|||||10|5/0|1|||0|0|1" % fs)
    print("fire_wall|%s|0|24|||||0,5|3,9|1|||0|0|1" % wall(out, 110, 30, 0, "deploy_wall_fire", turning=True))
    for kind, skin, contract in (("plain", False, False), ("rig", True, False), ("contract", True, True)):
        limit_file = fire_limits(out, "deploy_fire_limits_" + kind, skin, contract)
        for label, frame in (("min", -2147483648), ("max", 2147483647)):
            print("fire_%s_%s|%s|0|48|||||%d|%d|1|||0|0|1" % (kind, label, limit_file, frame, frame))
    # the armature (part 3): a static mesh without a parent; no mesh on any bone; bone and armature names already taken
    print("near|%s|%s" % (near(out, "deploy_near"), DEFAULT))
    print("near_noride|%s|%s" % (near(out, "deploy_near_noride", ride=False), DEFAULT))
    print("near_names|%s|%s" % (near(out, "deploy_near_names", names=True), DEFAULT))
    print("chain|%s|%s" % (chain(out), DEFAULT))
    for seed in (3, 11, 47, 52, 117, 119):   # 3, 11, 47, 52: the four of eighty seeds on which a size summed in another float order picks another anchor
        print("ties_%d|%s|%s" % (seed, ties(out, seed), DEFAULT))
    print("flatx_small|%s|%s" % (flatx(out, "deploy_flatx_small", 0.05), DEFAULT))
    print("flatx_big|%s|%s" % (flatx(out, "deploy_flatx_big", 3.0), DEFAULT))
    fb = bones(out)
    print("bones|%s|%s" % (fb, DEFAULT))
    print("bones_extras|%s|%s" % (bones(out, "deploy_bones_extras", extras=True), DEFAULT))
    # the armature itself stripped: what hung from its bones is left as roots, where its own transform puts it
    print("bones_norig|%s|0|24|rig,soldier||||||0|||4|0|1" % fb)
    print("frame1|%s|%s" % (frame1(out), DEFAULT))
    print("nested|%s|%s" % (nested(out), DEFAULT))
    # the bake operator's frame_end is at least 1: a clip whose keys all sit within frame 0 (the range 0..0) is baked
    # on frames 0 and 1 - one key at time 0; two keys 0.03 s apart; and the same on the contract path, where the
    # rebase runs over 0..0 alone and leaves the extra key as baked (the review of PR #137)
    print("frame0_one|%s|%s" % (frame0(out, "deploy_frame0_one", [0.0], [(0.0, 1.0, 0.0)]), DEFAULT))
    print("frame0_two|%s|%s" % (frame0(out, "deploy_frame0_two", [0.0, 0.03], [(0.0, 1.0, 0.0), (0.0, 1.5, 0.0)]), DEFAULT))
    print("wall_frame0|%s|%s" % (wall(out, 110, 30, 0, "deploy_wall_frame0", turning=False, span=0.03), DEFAULT))
    for seed in FLAT3_SEEDS:
        print("flat3_%d|%s|%s" % (seed, flat3(out, seed), DEFAULT))
    # the contract path beside an imported armature: with a bone's scale animated, without, and with the armature stripped
    fr = wall(out, 110, 30, 0, "deploy_wall_rig", crew=True)
    print("wall_rig|%s|%s" % (fr, DEFAULT))
    print("wall_rig_plain|%s|%s" % (wall(out, 110, 30, 0, "deploy_wall_rig_plain", crew=False), DEFAULT))
    print("wall_rig_stripped|%s|0|24|rig,soldier,icosphere||||||0|||4|0|1" % fr)
    # a skinned mesh the strip leaves in: its bound_box is the deformed mesh's - left to Blender, by name
    print("LEFT:skinned|%s|0|24|zzz||||||0|||4|0|1" % small(out, "deploy_skinned", 1.0, skin=True))
    print("cull|%s|%s" % (cull(out), DEFAULT))
    print("wall_budget|%s|%s" % (wall(out, 110, 30, 10, "deploy_wall", turning=True), DEFAULT))
    # slimmed back under the wall: the legacy path after all, no merge
    print("wall_slim|%s|%s" % (wall(out, 90, 26, 10, "deploy_wall_slim"), DEFAULT))
    # exactly AT the wall, wrappers included: no slimming, the legacy path
    print("wall_at|%s|%s" % (wall(out, 100, 14, 4, "deploy_wall_at"), DEFAULT))
    # over the wall with no class of eight to merge: nothing is merged, the warning
    print("wall_warn|%s|%s" % (wall(out, 7, 7, 0, "deploy_wall_few", small_classes=17), DEFAULT))


def review(out):
    import deploy_fixtures_review   # it takes Scene from this module: imported late
    deploy_fixtures_review.main(out)


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    main(sys.argv[1])
    sys.modules["deploy_fixtures"] = sys.modules["__main__"]
    review(sys.argv[1])
