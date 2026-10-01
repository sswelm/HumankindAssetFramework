"""Fixtures for the rules Blender's importer names objects by, one file per rule, for tools/vehicle_probe_drill.sh:
the C# side (editor/BlenderNames.cs) must give every part the name Blender's own probe prints for it.

    python naming_fixtures.py <outDir>      -> writes them, prints one path per line
"""
import os, sys
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "glb-reader-drill"))
import fixtures as F

TRI = [(0, 0, 0), (1, 0, 0), (0, 1, 0)]


def tri(b, dx=0.0, size=1.0):
    return b.accessor([(dx, 0, 0), (dx + size, 0, 0), (dx, size, 0)], "f", "VEC3")


def skinned(b, joint=0):
    """A triangle fully weighted to one joint: (POSITION, JOINTS_0, WEIGHTS_0)."""
    return {"POSITION": tri(b), "JOINTS_0": b.accessor([(joint, 0, 0, 0)] * 3, "H", "VEC4", minmax=False), "WEIGHTS_0": b.accessor([(1, 0, 0, 0)] * 3, "f", "VEC4", minmax=False)}


def fx_naming(out):
    """Missing and duplicate names: a nameless node takes its mesh's name, else Mesh_<index>; a taken name counts up
    from its own numeric tail (Foo.001 authored, then Foo twice -> Foo, Foo.002); 70-character names are kept."""
    b = F.Buf(); pos = tri(b); long = "L" * 70
    root = F.base("naming",
                  meshes=[{"name": "hull", "primitives": [{"attributes": {"POSITION": pos}}]}, {"primitives": [{"attributes": {"POSITION": pos}}]}],
                  nodes=[{"mesh": 0}, {"mesh": 0}, {"mesh": 1}, {"name": "Foo.001", "mesh": 1}, {"name": "Foo", "mesh": 1}, {"name": "Foo", "mesh": 1},
                         {"name": long, "mesh": 1}, {"name": long + "X", "mesh": 1}, {"name": "hull", "mesh": 1}, {"name": "Mesh_1", "mesh": 1}],
                  scenes=[{"nodes": list(range(10))}], scene=0)
    F.write_glb(os.path.join(out, "naming.glb"), root, b)


def fx_order(out):
    """Creation order: every node named N, the scene listing an EMPTY first and the roots out of index order; a node
    in no scene. The importer walks the parentless nodes in INDEX order, depth-first, whatever the scene says."""
    b = F.Buf(); pos = tri(b)
    root = F.base("order", meshes=[{"name": "m", "primitives": [{"attributes": {"POSITION": pos}}]}],
                  nodes=[{"name": "N", "mesh": 0, "children": [1, 3]}, {"name": "N", "mesh": 0, "children": [2]}, {"name": "N", "mesh": 0}, {"name": "N", "mesh": 0},
                         {"name": "N", "mesh": 0}, {"name": "N"}, {"name": "N", "mesh": 0}],
                  scenes=[{"nodes": [5, 0, 4]}], scene=0)
    F.write_glb(os.path.join(out, "order.glb"), root, b)
    b = F.Buf(); pos = tri(b)   # node 0 is node 2's child: tree order, not index order, numbers it last
    root = F.base("order2", meshes=[{"name": "m", "primitives": [{"attributes": {"POSITION": pos}}]}],
                  nodes=[{"name": "N", "mesh": 0}, {"name": "N", "mesh": 0}, {"name": "N", "mesh": 0, "children": [0]}], scenes=[{"nodes": [1, 2]}], scene=0)
    F.write_glb(os.path.join(out, "order2.glb"), root, b)


def fx_armature_names(out):
    """An armature at the dummy root takes the SKIN's name before any node gets one; its bone shape takes "Icosphere"
    before a real part of that name; a joint named like a mesh node is a bone, not an object."""
    b = F.Buf()
    root = F.base("armature_names",
                  meshes=[{"name": "m", "primitives": [{"attributes": skinned(b)}]}, {"name": "m2", "primitives": [{"attributes": {"POSITION": tri(b)}}]}],
                  nodes=[{"name": "Body", "mesh": 0, "skin": 0}, {"name": "Body"}, {"name": "Icosphere", "mesh": 1}, {"name": "Body", "mesh": 1}],
                  skins=[{"name": "Body", "joints": [1]}], scenes=[{"nodes": [0, 1, 2, 3]}], scene=0)
    F.write_glb(os.path.join(out, "armature_names.glb"), root, b)


def fx_skinned_not_moved(out):
    """A skinned mesh node the importer cannot move under its armature - it has a CHILD - stays an empty with its
    name; a new object, appended to the armature, carries the mesh and is named after the MESH. The armature here is
    a real node (the joint's parent), so it keeps the node's name, not the skin's."""
    b = F.Buf()
    root = F.base("skinned_not_moved",
                  meshes=[{"name": "hull_mesh", "primitives": [{"attributes": skinned(b)}]}, {"name": "lamp_mesh", "primitives": [{"attributes": {"POSITION": tri(b, 5)}}]}],
                  nodes=[{"name": "Tank", "mesh": 0, "skin": 0, "children": [1]}, {"name": "Lamp", "mesh": 1}, {"name": "Rig", "children": [3]}, {"name": "Bone"}],
                  skins=[{"name": "Skin", "joints": [3]}], scenes=[{"nodes": [0, 2]}], scene=0)
    F.write_glb(os.path.join(out, "skinned_not_moved.glb"), root, b)


def fx_skinned_animated(out):
    """A skinned mesh node that an animation channel targets is not moved either: the same split, by animation."""
    b = F.Buf()
    attrs = skinned(b)
    t = b.accessor([0.0, 1.0], "f", "SCALAR"); v = b.accessor([(0, 0, 0), (0, 0, 0)], "f", "VEC3", minmax=False)
    root = F.base("skinned_animated",
                  meshes=[{"name": "hull_mesh", "primitives": [{"attributes": attrs}]}, {"name": "other_mesh", "primitives": [{"attributes": {"POSITION": tri(b, 5)}}]}],
                  nodes=[{"name": "Tank", "mesh": 0, "skin": 0}, {"name": "Rig", "children": [2]}, {"name": "Bone"}, {"name": "Other", "mesh": 1}],
                  skins=[{"name": "Skin", "joints": [2]}], scenes=[{"nodes": [0, 1, 3]}], scene=0,
                  animations=[{"name": "idle", "samplers": [{"input": t, "output": v}], "channels": [{"sampler": 0, "target": {"node": 0, "path": "translation"}}]}])
    F.write_glb(os.path.join(out, "skinned_animated.glb"), root, b)


def fx_mesh_on_bone(out):
    """A mesh on a JOINT node and a mesh on the ARMATURE node: Blender cannot hold either on a bone or an armature
    object, so each moves to a child object named after its mesh."""
    b = F.Buf()
    root = F.base("mesh_on_bone",
                  meshes=[{"name": "turret_mesh", "primitives": [{"attributes": {"POSITION": tri(b, 3)}}]}, {"name": "base_mesh", "primitives": [{"attributes": {"POSITION": tri(b)}}]},
                          {"name": "skin_mesh", "primitives": [{"attributes": skinned(b)}]}],
                  nodes=[{"name": "Root", "mesh": 1, "children": [1]}, {"name": "Turret", "mesh": 0, "translation": [0, 2, 0]}, {"name": "Skinned", "mesh": 2, "skin": 0}],
                  skins=[{"name": "Rig", "joints": [1]}], scenes=[{"nodes": [0, 2]}], scene=0)
    F.write_glb(os.path.join(out, "mesh_on_bone.glb"), root, b)


def fx_islands(out):
    """ONE mesh of three islands, split into loose parts: the island with the lowest VERTEX index keeps the name, the
    rest count up in that order - whatever order the faces are listed in (islands2 lists them backwards)."""
    b = F.Buf()
    pos = b.accessor([(0, 0, 0), (1, 0, 0), (0, 1, 0), (10, 0, 0), (13, 0, 0), (10, 3, 0), (20, 0, 0), (22, 0, 0), (20, 2, 0)], "f", "VEC3")
    idx = b.accessor([0, 1, 2, 3, 4, 5, 6, 7, 8], "H", "SCALAR")
    root = F.base("islands", meshes=[{"name": "boat", "primitives": [{"attributes": {"POSITION": pos}, "indices": idx}]}], nodes=[{"name": "Hull", "mesh": 0}], scenes=[{"nodes": [0]}], scene=0)
    F.write_glb(os.path.join(out, "islands.glb"), root, b)
    b = F.Buf()
    pos = b.accessor([(20, 0, 0), (22, 0, 0), (20, 2, 0), (10, 0, 0), (13, 0, 0), (10, 3, 0), (0, 0, 0), (1, 0, 0), (0, 1, 0)], "f", "VEC3")
    idx = b.accessor([6, 7, 8, 3, 4, 5, 0, 1, 2], "H", "SCALAR")
    root = F.base("islands2", meshes=[{"name": "boat", "primitives": [{"attributes": {"POSITION": pos}, "indices": idx}]}], nodes=[{"name": "Hull", "mesh": 0}], scenes=[{"nodes": [0]}], scene=0)
    F.write_glb(os.path.join(out, "islands2.glb"), root, b)


def fx_rotated_armature(out):
    """A rig whose armature node carries a quarter turn the inverse bind matrices do not account for (the Sketchfab
    shape: combine_soldier), and a second whose armature carries a 0.01 scale they DO account for (the FBX shape:
    drone_clean). The RIGBONE rows read the vertices skinned into the importer's bind pose through the armature."""
    b = F.Buf()
    s = 0.7071068
    root = F.base("rotated_armature",
                  meshes=[{"name": "m", "primitives": [{"attributes": skinned(b)}]}],
                  nodes=[{"name": "Mesh", "mesh": 0, "skin": 0}, {"name": "Rig", "rotation": [-s, 0, 0, s], "children": [2]}, {"name": "Bone"}],
                  skins=[{"name": "Skin", "joints": [2]}], scenes=[{"nodes": [0, 1]}], scene=0)
    F.write_glb(os.path.join(out, "rotated_armature.glb"), root, b)
    b = F.Buf()
    attrs = skinned(b)
    ibm = b.accessor([(100, 0, 0, 0, 0, 100, 0, 0, 0, 0, 100, 0, 0, 0, 0, 1)], "f", "MAT4", minmax=False)
    root = F.base("scaled_armature",
                  meshes=[{"name": "m", "primitives": [{"attributes": attrs}]}],
                  nodes=[{"name": "Mesh", "mesh": 0, "skin": 0}, {"name": "Rig", "scale": [0.01, 0.01, 0.01], "children": [2]}, {"name": "Bone"}],
                  skins=[{"name": "Skin", "joints": [2], "inverseBindMatrices": ibm}], scenes=[{"nodes": [0, 1]}], scene=0)
    F.write_glb(os.path.join(out, "scaled_armature.glb"), root, b)


def fx_two_armatures(out):
    """TWO armatures, and the one created first is NOT skin 0's: Rig2 sits at a lower node index than Rig1. The script's
    rig_report reads `arms[0]` - the first armature in creation order - so the RIGBONE rows are BoneB's (review of
    PR #112: no file of the 105 had more than one skin, and the code took skin 0)."""
    b = F.Buf()
    a0 = skinned(b); a1 = {"POSITION": tri(b, 5), "JOINTS_0": b.accessor([(0, 0, 0, 0)] * 3, "H", "VEC4", minmax=False), "WEIGHTS_0": b.accessor([(1, 0, 0, 0)] * 3, "f", "VEC4", minmax=False)}
    root = F.base("two_armatures",
                  meshes=[{"name": "ma", "primitives": [{"attributes": a0}]}, {"name": "mb", "primitives": [{"attributes": a1}]}],
                  nodes=[{"name": "Rig2", "children": [1]}, {"name": "BoneB"}, {"name": "Rig1", "children": [3]}, {"name": "BoneA"},
                         {"name": "MeshA", "mesh": 0, "skin": 0}, {"name": "MeshB", "mesh": 1, "skin": 1}],
                  skins=[{"name": "SkinA", "joints": [3]}, {"name": "SkinB", "joints": [1]}], scenes=[{"nodes": [0, 2, 4, 5]}], scene=0)
    F.write_glb(os.path.join(out, "two_armatures.glb"), root, b)


def fx_nested_skins(out):
    """Two skins in ONE armature: the second skin's joints are a sub-chain of the first's, so its would-be armature is
    already a bone and no second armature is made. Both meshes hang under the one armature; rig_report lists the bones
    both carry."""
    b = F.Buf()
    a0 = {"POSITION": tri(b), "JOINTS_0": b.accessor([(0, 0, 0, 0), (1, 0, 0, 0), (2, 0, 0, 0)], "H", "VEC4", minmax=False), "WEIGHTS_0": b.accessor([(1, 0, 0, 0)] * 3, "f", "VEC4", minmax=False)}
    a1 = {"POSITION": tri(b, 5), "JOINTS_0": b.accessor([(0, 0, 0, 0), (1, 0, 0, 0), (1, 0, 0, 0)], "H", "VEC4", minmax=False), "WEIGHTS_0": b.accessor([(1, 0, 0, 0)] * 3, "f", "VEC4", minmax=False)}
    root = F.base("nested_skins",
                  meshes=[{"name": "ma", "primitives": [{"attributes": a0}]}, {"name": "mb", "primitives": [{"attributes": a1}]}],
                  nodes=[{"name": "Rig", "children": [1]}, {"name": "J0", "children": [2]}, {"name": "J1", "translation": [0, 1, 0], "children": [3]}, {"name": "J2", "translation": [0, 1, 0]},
                         {"name": "MeshA", "mesh": 0, "skin": 0}, {"name": "MeshB", "mesh": 1, "skin": 1}],
                  skins=[{"name": "Whole", "joints": [1, 2, 3]}, {"name": "Upper", "joints": [2, 3]}], scenes=[{"nodes": [0, 4, 5]}], scene=0)
    F.write_glb(os.path.join(out, "nested_skins.glb"), root, b)


def fx_split_collision(out):
    """A single mesh of three islands whose split names are partly TAKEN: empties named Hull.001 and Hull.003 exist, so
    Blender's loose parts are Hull, Hull.002, Hull.004 (review of PR #112: the split counted up in a pool of its own)."""
    b = F.Buf()
    pos = b.accessor([(0, 0, 0), (1, 0, 0), (0, 1, 0), (10, 0, 0), (13, 0, 0), (10, 3, 0), (20, 0, 0), (22, 0, 0), (20, 2, 0)], "f", "VEC3")
    idx = b.accessor([0, 1, 2, 3, 4, 5, 6, 7, 8], "H", "SCALAR")
    root = F.base("split_collision", meshes=[{"name": "boat", "primitives": [{"attributes": {"POSITION": pos}, "indices": idx}]}],
                  nodes=[{"name": "Hull", "mesh": 0}, {"name": "Hull.001"}, {"name": "Hull.003"}], scenes=[{"nodes": [0, 1, 2]}], scene=0)
    F.write_glb(os.path.join(out, "split_collision.glb"), root, b)


def fx_nonunit_rotation(out):
    """Rotations that are not unit quaternions (invalid by the letter, common in the wild): (0, 0, 0, 2) is no rotation
    twice as long; (0, 1, 0, 1) is a quarter turn about Y at length 1.41. Blender normalizes; so must the probe, or the
    part comes out scaled by the quaternion's squared length."""
    b = F.Buf()
    root = F.base("nonunit_rotation", meshes=[{"name": "m", "primitives": [{"attributes": {"POSITION": tri(b)}}]}, {"name": "m2", "primitives": [{"attributes": {"POSITION": tri(b)}}]}],
                  nodes=[{"name": "Long", "mesh": 0, "rotation": [0, 0, 0, 2]}, {"name": "Turned", "mesh": 1, "rotation": [0, 1, 0, 1], "translation": [5, 0, 0]}],
                  scenes=[{"nodes": [0, 1]}], scene=0)
    F.write_glb(os.path.join(out, "nonunit_rotation.glb"), root, b)


def fx_cameras(out):
    """Cameras take names in the object pool: a NAMELESS camera node becomes an object named after its camera ("Camera"
    when that has no name either), so the mesh node named Camera is Camera.001; a node with a mesh AND a camera keeps
    the mesh and gets a child object for the camera."""
    b = F.Buf()
    cam = {"type": "perspective", "perspective": {"yfov": 0.8, "znear": 0.1}}
    root = F.base("cameras", cameras=[dict(cam), dict(cam, name="Lens")],
                  meshes=[{"name": "m", "primitives": [{"attributes": {"POSITION": tri(b)}}]}, {"name": "m2", "primitives": [{"attributes": {"POSITION": tri(b, 5)}}]}, {"name": "m3", "primitives": [{"attributes": {"POSITION": tri(b, 9)}}]}],
                  nodes=[{"camera": 0}, {"name": "Camera", "mesh": 0}, {"name": "Both", "mesh": 1, "camera": 1}, {"name": "Lens", "mesh": 2}],
                  scenes=[{"nodes": [0, 1, 2, 3]}], scene=0)
    F.write_glb(os.path.join(out, "cameras.glb"), root, b)


def fx_camera_data_names(out):
    """A camera OBJECT always makes a camera datablock, and that takes its name among the cameras whether or not the node
    has a name of its own (external review of PR #112: a named camera node skipped the reservation). Two cameras named
    Lens: the first on the node Eye, the second on a nameless node - whose object is therefore Lens.001, not Lens - so
    the mesh node named Lens keeps its name. And ONE glTF camera on two nodes makes two datablocks: the nameless
    second node is Wide.001."""
    b = F.Buf()
    cam = {"type": "perspective", "perspective": {"yfov": 0.8, "znear": 0.1}}
    root = F.base("camera_data_names", cameras=[dict(cam, name="Lens"), dict(cam, name="Lens"), dict(cam, name="Wide")],
                  meshes=[{"name": "m", "primitives": [{"attributes": {"POSITION": tri(b)}}]}, {"name": "m2", "primitives": [{"attributes": {"POSITION": tri(b, 5)}}]}, {"name": "m3", "primitives": [{"attributes": {"POSITION": tri(b, 9)}}]}],
                  nodes=[{"name": "Eye", "camera": 0}, {"camera": 1}, {"name": "Lens", "mesh": 0},
                         {"name": "First", "camera": 2}, {"camera": 2}, {"name": "Wide", "mesh": 1}, {"name": "Wide.001", "mesh": 2}],
                  scenes=[{"nodes": [0, 1, 2, 3, 4, 5, 6]}], scene=0)
    F.write_glb(os.path.join(out, "camera_data_names.glb"), root, b)


FIXTURES = [fx_naming, fx_order, fx_armature_names, fx_skinned_not_moved, fx_skinned_animated, fx_mesh_on_bone, fx_islands, fx_rotated_armature,
            fx_two_armatures, fx_nested_skins, fx_split_collision, fx_nonunit_rotation, fx_cameras, fx_camera_data_names]


def main(out):
    os.makedirs(out, exist_ok=True)
    for fx in FIXTURES:
        fx(out)
    for name in sorted(os.listdir(out)):
        if name.endswith((".glb", ".gltf")):
            print(os.path.join(out, name).replace("\\", "/"))


if __name__ == "__main__":
    main(sys.argv[1])
