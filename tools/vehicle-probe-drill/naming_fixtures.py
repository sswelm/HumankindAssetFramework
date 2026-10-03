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
    """Missing and duplicate names: a nameless node takes its mesh's name, else Mesh_<index>; a taken name gets the
    smallest number its base has free (Foo.001 authored, then Foo twice -> Foo, Foo.002; the name_tails fixture holds
    the cases that tell this from counting up); 70-character names are kept."""
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


def named_objects(out, file, names):
    """One mesh node per name, all roots, each with its own mesh at x = 3 * index (so a row's box says which node it is)."""
    b = F.Buf()
    root = F.base(file, meshes=[{"name": "m%d" % i, "primitives": [{"attributes": {"POSITION": tri(b, 3.0 * i)}}]} for i in range(len(names))],
                  nodes=[{"name": n, "mesh": i} for i, n in enumerate(names)], scenes=[{"nodes": list(range(len(names)))}], scene=0)
    F.write_glb(os.path.join(out, file + ".glb"), root, b)


def fx_name_tails(out):
    """What a duplicate OBJECT name becomes - Blender's Main name map, one case per branch of it (external review of
    PR #112: "Hull.\u0661", an Arabic-Indic digit, threw in the first port; the measurement behind the fix found that port
    wrong for every duplicate with a numeric tail of its own). The second of each pair, as Blender names it:
    a tail that is no ASCII number, or does not fit an int, is part of the name (Hull.\u0661.001, Big.99999999999.001,
    Q.-1.001, C..001); a numeric tail is split off and the base's SMALLEST free number taken, never tail + 1
    (B.7 -> B.001, Y.002 beside Y.009 -> Y.001, V.005 three times -> V.001, V.002, Max.999999 -> Max.001,
    Int.2147483647 -> Int.001 but Int.2147483648 -> Int.2147483648.001, W.1023 -> W.001, T.1.2 -> T.1.001, Zero.000 -> Zero.001, .5 -> .001); the number
    counts, not its spelling (G.01 uses 1: the next is G.002)."""
    named_objects(out, "name_tails", [
        "Hull.\u0661", "Hull.\u0661", "Deck.\uff11", "Deck.\uff11", "Big.99999999999", "Big.99999999999", "Max.999999", "Max.999999",
        "Over.1000000", "Over.1000000", ".5", ".5", "Zero.000", "Zero.000", "Int.2147483647", "Int.2147483647", "Int.2147483648", "Int.2147483648", "B.7", "B.7",
        "Y.009", "Y.002", "Y.002", "N.999", "N.999", "W.1023", "W.1023", "W.1024", "W.1024", "T.1.2", "T.1.2", "Q.-1", "Q.-1",
        "123", "123", "P.0", "P.0", "K", "K.001", "K.001", "G.01", "G.01", "V.005", "V.005", "V.005", "C.", "C."])


def fx_long_names(out):
    """A datablock name holds 255 BYTES of UTF-8, cut at a whole character (130 e-acutes are 260 bytes: 127 stay). A
    duplicate that has no room for ".001" is cut by one character and tried again AS A NAME - so the second of two
    255-byte names is the 254-byte one, with no number at all; a 252-byte base with ".05" ends as the 251-byte base. A
    character outside the BMP counts its 4 bytes."""
    named_objects(out, "long_names", ["L" * 255, "L" * 255, "M" * 300, "M" * 300, "\u00e9" * 130, "\u00e9" * 130, "S" * 255, "S" * 254 + "T", "R" * 252 + ".05", "R" * 252 + ".05",
                                     "\U0001F600" + "a" * 300, "\U0001F600" + "a" * 300])


def fx_many_names(out):
    """Past the 1,023 numbers Blender tracks exactly: 1,025 objects named E beside an E.5000 are E, E.001 ... E.1023 and
    then E.5001 (one above the highest seen); beside an F.999999999 - the last number there is - the 1,025th F has none
    left and is F_001."""
    b = F.Buf(); pos = tri(b)
    many = ["E.5000"] + ["E"] * 1025 + ["F.999999999"] + ["F"] * 1025
    root = F.base("many_names", meshes=[{"name": "m", "primitives": [{"attributes": {"POSITION": pos}}]}],
                  nodes=[{"name": n, "mesh": 0, "translation": [3.0 * i, 0, 0]} for i, n in enumerate(many)], scenes=[{"nodes": list(range(len(many)))}], scene=0)
    F.write_glb(os.path.join(out, "many_names.glb"), root, b)


def fx_bone_tails(out):
    """What a duplicate BONE name becomes - the OLDER rule (BLI_uniquename_cb), unlike a datablock's: the numeric tail is
    split off and tail + 1, tail + 2 ... tried (J.7 -> J.008, C.002 beside C.009 -> C.003, .5 -> .006), a tail that is
    no ASCII number or does not fit an int is part of the name; 63 BYTES of UTF-8, the base cut so the number fits (70 X
    -> 63 X, then 59 X + .001). And the int wraps: the bone after Bn.2147483647 is Bn.-2147483648."""
    bone_names = ["J.7", "J.7", "Bone.\u0661", "Bone.\u0661", "C.009", "C.002", "C.002", "X" * 70, "X" * 70, "\u00e9" * 40, "\u00e9" * 40, "D", "D",
                  "Huge.99999999999", "Huge.99999999999", ".5", ".5", "Bn.2147483647", "Bn.2147483647"]
    b = F.Buf(); n = len(bone_names)
    prims = [{"attributes": {"POSITION": tri(b, 3.0 * j), "JOINTS_0": b.accessor([(j, 0, 0, 0)] * 3, "H", "VEC4", minmax=False), "WEIGHTS_0": b.accessor([(1, 0, 0, 0)] * 3, "f", "VEC4", minmax=False)}} for j in range(n)]
    nodes = [{"name": "Rig", "children": list(range(1, n + 1))}] + [{"name": bn, "translation": [3.0 * j, 0, 0]} for j, bn in enumerate(bone_names)] + [{"name": "Body", "mesh": 0, "skin": 0}]
    root = F.base("bone_tails", meshes=[{"name": "body", "primitives": prims}], nodes=nodes, skins=[{"name": "Skin", "joints": list(range(1, n + 1))}], scenes=[{"nodes": [0, n + 1]}], scene=0)
    F.write_glb(os.path.join(out, "bone_tails.glb"), root, b)


def fx_split_tails(out):
    """The loose split of a single mesh whose name HAS a numeric tail: each new object is a copy asking for the first
    one's name, so it gets the base's smallest free number - Hull.005 beside empties Hull.002 and Hull.006 splits into
    Hull.005, Hull.001, Hull.003, Hull.004 (the first port counted up: Hull.007, .008, .009).
    And a PURGED object frees its number: two armatures make the bone shapes Icosphere and Icosphere.001, the script
    purges both, and the skinned mesh Icosphere.005 splits into Icosphere.005, Icosphere.001, Icosphere.002."""
    b = F.Buf()
    pos = b.accessor([(0, 0, 0), (1, 0, 0), (0, 1, 0), (10, 0, 0), (13, 0, 0), (10, 3, 0), (20, 0, 0), (22, 0, 0), (20, 2, 0), (30, 0, 0), (34, 0, 0), (30, 4, 0)], "f", "VEC3")
    idx = b.accessor(list(range(12)), "H", "SCALAR")
    root = F.base("split_tails", meshes=[{"name": "boat", "primitives": [{"attributes": {"POSITION": pos}, "indices": idx}]}],
                  nodes=[{"name": "Hull.005", "mesh": 0}, {"name": "Hull.002"}, {"name": "Hull.006"}], scenes=[{"nodes": [0, 1, 2]}], scene=0)
    F.write_glb(os.path.join(out, "split_tails.glb"), root, b)
    b = F.Buf()
    pos = b.accessor([(0, 0, 0), (1, 0, 0), (0, 1, 0), (10, 0, 0), (13, 0, 0), (10, 3, 0), (20, 0, 0), (22, 0, 0), (20, 2, 0)], "f", "VEC3")
    attrs = {"POSITION": pos, "JOINTS_0": b.accessor([(0, 0, 0, 0)] * 9, "H", "VEC4", minmax=False), "WEIGHTS_0": b.accessor([(1, 0, 0, 0)] * 9, "f", "VEC4", minmax=False)}
    idx = b.accessor(list(range(9)), "H", "SCALAR")
    root = F.base("split_purged", meshes=[{"name": "body", "primitives": [{"attributes": attrs, "indices": idx}]}],
                  nodes=[{"name": "Rig1", "children": [1]}, {"name": "BoneA"}, {"name": "Rig2", "children": [3]}, {"name": "BoneB"}, {"name": "Icosphere.005", "mesh": 0, "skin": 0}],
                  skins=[{"name": "SkinA", "joints": [1]}, {"name": "SkinB", "joints": [3]}], scenes=[{"nodes": [0, 2, 4]}], scene=0)
    F.write_glb(os.path.join(out, "split_purged.glb"), root, b)


def fx_icosphere_mesh(out):
    """The bone shape is an object AND a mesh datablock, both named Icosphere. Two glTF meshes named Icosphere in a
    rigged file are therefore the datablocks Icosphere.001 and Icosphere.002 - and a NAMELESS node takes its datablock's
    name: the second one's object is Icosphere.002 (the port did not reserve the datablock and said Icosphere.001; it
    was the one neighbour of the camera fix named as unmeasured in PR #112)."""
    b = F.Buf()
    root = F.base("icosphere_mesh",
                  meshes=[{"name": "body", "primitives": [{"attributes": skinned(b)}]}, {"name": "Icosphere", "primitives": [{"attributes": {"POSITION": tri(b, 5)}}]},
                          {"name": "Icosphere", "primitives": [{"attributes": {"POSITION": tri(b, 9)}}]}],
                  nodes=[{"name": "Rig", "children": [1]}, {"name": "Bone"}, {"name": "Body", "mesh": 0, "skin": 0}, {"name": "Buoy", "mesh": 1}, {"mesh": 2}],
                  skins=[{"name": "Skin", "joints": [1]}], scenes=[{"nodes": [0, 2, 3, 4]}], scene=0)
    F.write_glb(os.path.join(out, "icosphere_mesh.glb"), root, b)


def box(cx, cy, cz, h):
    """A closed cube of 12 triangles around (cx, cy, cz), half-size h: positions and indices."""
    pos = [(cx + sx * h, cy + sy * h, cz + sz * h) for sz in (-1, 1) for sy in (-1, 1) for sx in (-1, 1)]
    idx = [0, 2, 1, 1, 2, 3,  4, 5, 6, 5, 7, 6,  0, 1, 5, 0, 5, 4,  2, 6, 7, 2, 7, 3,  0, 4, 6, 0, 6, 2,  1, 3, 7, 1, 7, 5]
    return pos, idx


def fx_visibility(out):
    """The visibility verdict (PART field 6, step 3b): a part every ray from which meets other geometry is INTERIOR (0).
    Box: a closed cube - its own vertices escape outward (1). Core: a grid inside the box (0). Outside: a triangle far
    away (1). Normal: a triangle inside a box of 15 shields - one across each fixed ray direction and one across the
    direction opposite to the gap - with the gap in the direction Blender (1, 2, 0): only the vertex NORMAL ray, which
    the file gives as that direction, escapes (1); its twin Sideways, whose file normals point +X into a shield, is
    interior (0): the normal ray is the file's normal, not a computed one. Computed: the same spot WITHOUT a NORMAL
    attribute, a triangle wound so that the normal Blender computes from its face points into the gap (1);
    ComputedFlipped: wound the other way, into the opposite shield (0). ComputedTwin: the same triangle twice, the second
    wound against the first - Blender's validate drops the second face, so the normal is the first's (1); ComputedTwinFlipped:
    the flipped one first (0) (external review of PR #115: both faces summed to no normal, and the part read interior).
    Every node its own mesh: no split."""
    b = F.Buf()
    bpos, bidx = box(0, 0, 0, 2)
    gpos, _, _, gidx = F.grid(8, 8)
    gpos = [(x - 0.5, y - 0.5, 0.0) for x, y, _ in gpos]
    meshes = [{"name": "box", "primitives": [{"attributes": {"POSITION": b.accessor(bpos, "f", "VEC3")}, "indices": b.accessor(bidx, "H", "SCALAR")}]},
              {"name": "core", "primitives": [{"attributes": {"POSITION": b.accessor(gpos, "f", "VEC3")}, "indices": b.accessor(gidx, "H", "SCALAR")}]},
              {"name": "outside", "primitives": [{"attributes": {"POSITION": tri(b, 10)}}]}]
    nodes = [{"name": "Box", "mesh": 0}, {"name": "Core", "mesh": 1}, {"name": "Outside", "mesh": 2}]
    # the shields: 14 quads of size 3 x 3, each 5 units out along one fixed direction (Blender's frame; z here = -y of glTF)
    dirs = [(1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1), (1, 1, 1), (1, 1, -1), (1, -1, 1), (1, -1, -1), (-1, 1, 1), (-1, 1, -1), (-1, -1, 1), (-1, -1, -1),
            (-1, -2, 0)]   # the 15th: opposite to the gap, so a computed normal pointing the wrong way is blocked too
    for k, (ox, oy, oz) in enumerate(dirs):
        # Blender frame (x, y, z) = glTF (x, -z, y): the fixed direction in glTF terms
        gx, gy, gz = ox, oz, -oy
        l = (gx * gx + gy * gy + gz * gz) ** 0.5; gx, gy, gz = gx / l, gy / l, gz / l
        cx, cy, cz = 30 + gx * 5, gy * 5, gz * 5
        # two unit vectors across the direction
        ax, ay, az = (0, 1, 0) if abs(gy) < 0.9 else (1, 0, 0)
        ux, uy, uz = gy * az - gz * ay, gz * ax - gx * az, gx * ay - gy * ax
        l = (ux * ux + uy * uy + uz * uz) ** 0.5; ux, uy, uz = ux / l, uy / l, uz / l
        vx, vy, vz = gy * uz - gz * uy, gz * ux - gx * uz, gx * uy - gy * ux
        q = [(cx + s1 * 1.5 * ux + s2 * 1.5 * vx, cy + s1 * 1.5 * uy + s2 * 1.5 * vy, cz + s1 * 1.5 * uz + s2 * 1.5 * vz) for s1, s2 in ((-1, -1), (1, -1), (1, 1), (-1, 1))]
        meshes.append({"name": "shield%d" % k, "primitives": [{"attributes": {"POSITION": b.accessor(q, "f", "VEC3")}, "indices": b.accessor([0, 1, 2, 0, 2, 3], "H", "SCALAR")}]})
        nodes.append({"name": "Shield%02d" % k, "mesh": len(meshes) - 1})
    # the two triangles at (30, 0, 0): "Normal" with file normals along Blender (1, 2, 0) = glTF (1, 0, -2) (not a fixed direction), "Sideways" with +X
    tpos = [(30, 0, 0), (30.2, 0, 0), (30, 0.2, 0)]
    nn = (1 / 5 ** 0.5, 0.0, -2 / 5 ** 0.5)
    meshes.append({"name": "normal", "primitives": [{"attributes": {"POSITION": b.accessor(tpos, "f", "VEC3"), "NORMAL": b.accessor([nn] * 3, "f", "VEC3", minmax=False)}}]})
    nodes.append({"name": "Normal", "mesh": len(meshes) - 1})
    meshes.append({"name": "sideways", "primitives": [{"attributes": {"POSITION": b.accessor(tpos, "f", "VEC3"), "NORMAL": b.accessor([(1.0, 0.0, 0.0)] * 3, "f", "VEC3", minmax=False)}}]})
    nodes.append({"name": "Sideways", "mesh": len(meshes) - 1})
    # no NORMAL: the face normal Blender computes is (p1 - p0) x (p2 - p0); u = (0, 1, 0) and v = n x u = (2, 0, 1) / sqrt 5 span the plane across n
    s5 = 5 ** 0.5
    cpos = [(30.0, 0.0, 0.0), (30.0, 0.2, 0.0), (30.0 + 0.4 / s5, 0.0, 0.2 / s5)]
    meshes.append({"name": "computed", "primitives": [{"attributes": {"POSITION": b.accessor(cpos, "f", "VEC3")}, "indices": b.accessor([0, 1, 2], "H", "SCALAR")}]})
    nodes.append({"name": "Computed", "mesh": len(meshes) - 1})
    meshes.append({"name": "computedflipped", "primitives": [{"attributes": {"POSITION": b.accessor(cpos, "f", "VEC3")}, "indices": b.accessor([0, 2, 1], "H", "SCALAR")}]})
    nodes.append({"name": "ComputedFlipped", "mesh": len(meshes) - 1})
    meshes.append({"name": "computedtwin", "primitives": [{"attributes": {"POSITION": b.accessor(cpos, "f", "VEC3")}, "indices": b.accessor([0, 1, 2, 0, 2, 1], "H", "SCALAR")}]})
    nodes.append({"name": "ComputedTwin", "mesh": len(meshes) - 1})
    meshes.append({"name": "computedtwinflipped", "primitives": [{"attributes": {"POSITION": b.accessor(cpos, "f", "VEC3")}, "indices": b.accessor([0, 2, 1, 0, 1, 2], "H", "SCALAR")}]})
    nodes.append({"name": "ComputedTwinFlipped", "mesh": len(meshes) - 1})
    root = F.base("visibility", meshes=meshes, nodes=nodes, scenes=[{"nodes": list(range(len(nodes)))}], scene=0)
    F.write_glb(os.path.join(out, "visibility.glb"), root, b)


def fx_visibility_skinned(out):
    """A SKINNED triangle inside the 15 shields, its file normals +X and its one joint turned 63.43 degrees about glTF Y
    so that the bind pose takes that normal to the gap direction (1, 0, -2) / sqrt 5: Blender skins the normals into the
    bind pose as it skins the positions (skin_into_bind_pose), so the normal ray escapes (1). With the file's normal as
    given it would meet the +X shield (external review of PR #115). Its twin Still, whose joint is not turned, is 0."""
    b = F.Buf()
    import math
    th = math.atan2(2.0, 1.0)
    meshes = []; nodes = []
    dirs = [(1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1), (1, 1, 1), (1, 1, -1), (1, -1, 1), (1, -1, -1), (-1, 1, 1), (-1, 1, -1), (-1, -1, 1), (-1, -1, -1), (-1, -2, 0)]
    for k, (ox, oy, oz) in enumerate(dirs):
        gx, gy, gz = ox, oz, -oy
        l = (gx * gx + gy * gy + gz * gz) ** 0.5; gx, gy, gz = gx / l, gy / l, gz / l
        cx, cy, cz = gx * 5, gy * 5, gz * 5
        ax, ay, az = (0, 1, 0) if abs(gy) < 0.9 else (1, 0, 0)
        ux, uy, uz = gy * az - gz * ay, gz * ax - gx * az, gx * ay - gy * ax
        l = (ux * ux + uy * uy + uz * uz) ** 0.5; ux, uy, uz = ux / l, uy / l, uz / l
        vx, vy, vz = gy * uz - gz * uy, gz * ux - gx * uz, gx * uy - gy * ux
        q = [(cx + s1 * 1.5 * ux + s2 * 1.5 * vx, cy + s1 * 1.5 * uy + s2 * 1.5 * vy, cz + s1 * 1.5 * uz + s2 * 1.5 * vz) for s1, s2 in ((-1, -1), (1, -1), (1, 1), (-1, 1))]
        meshes.append({"name": "shield%d" % k, "primitives": [{"attributes": {"POSITION": b.accessor(q, "f", "VEC3")}, "indices": b.accessor([0, 1, 2, 0, 2, 3], "H", "SCALAR")}]})
        nodes.append({"name": "Shield%02d" % k, "mesh": len(meshes) - 1})
    tpos = [(0, 0, 0), (0.2, 0, 0), (0, 0.2, 0)]
    def skinned_tri(joint):
        return {"POSITION": b.accessor(tpos, "f", "VEC3"), "NORMAL": b.accessor([(1.0, 0.0, 0.0)] * 3, "f", "VEC3", minmax=False),
                "JOINTS_0": b.accessor([(joint, 0, 0, 0)] * 3, "H", "VEC4", minmax=False), "WEIGHTS_0": b.accessor([(1, 0, 0, 0)] * 3, "f", "VEC4", minmax=False)}
    meshes.append({"name": "turned", "primitives": [{"attributes": skinned_tri(0)}]})
    meshes.append({"name": "still", "primitives": [{"attributes": skinned_tri(0)}]})
    n0 = len(nodes)
    nodes += [{"name": "Rig", "children": [n0 + 1]}, {"name": "Turned", "rotation": [0, math.sin(th / 2), 0, math.cos(th / 2)]},
              {"name": "Rig2", "children": [n0 + 3]}, {"name": "StillJoint"},
              {"name": "TurnedMesh", "mesh": len(meshes) - 2, "skin": 0}, {"name": "StillMesh", "mesh": len(meshes) - 1, "skin": 1}]
    skins = [{"name": "Skin", "joints": [n0 + 1]}, {"name": "Skin2", "joints": [n0 + 3]}]
    root = F.base("visibility_skinned", meshes=meshes, nodes=nodes, skins=skins, scenes=[{"nodes": list(range(n0)) + [n0, n0 + 2, n0 + 4, n0 + 5]}], scene=0)
    F.write_glb(os.path.join(out, "visibility_skinned.glb"), root, b)


def fx_visibility_split(out):
    """The same box, core and outside triangle as ONE mesh of three islands, split into loose parts - the core a 40 x 40
    grid (1,681 vertices, so every 56th is sampled, and 3,200 faces, so Blender's edge array is built in 8 hash buckets):
    the sampled vertices are the ones Blender's separated island holds at those indices (VehicleProbe.Islands.cs)."""
    b = F.Buf()
    bpos, bidx = box(0, 0, 0, 2)
    gpos, _, _, gidx = F.grid(40, 40)
    gpos = [(x - 0.5, y - 0.5, 0.0) for x, y, _ in gpos]
    opos = [(10, 0, 0), (11, 0, 0), (10, 1, 0)]
    pos = bpos + gpos + opos
    idx = bidx + [i + len(bpos) for i in gidx] + [i + len(bpos) + len(gpos) for i in (0, 1, 2)]
    root = F.base("visibility_split", meshes=[{"name": "all", "primitives": [{"attributes": {"POSITION": b.accessor(pos, "f", "VEC3")}, "indices": b.accessor(idx, "H", "SCALAR")}]}],
                  nodes=[{"name": "Hull", "mesh": 0}], scenes=[{"nodes": [0]}], scene=0)
    F.write_glb(os.path.join(out, "visibility_split.glb"), root, b)


def fx_insideout(out):
    """The inside-out verdict (PART field 8, step 3c): the number of face islands whose faces on average point INTO the
    hull - towards the hull's length axis (Blender's X axis through the mid-Y and lower-quartile Z of every part's
    vertices). Keel: an 81-vertex grid 5 below the plates that pins the axis there (0). PlateDown: a strip of quads 5
    above the axis wound so its face normals point down, at the axis (1); PlateUp: the same strip wound the other way (0).
    MirroredDown / MirroredUp: the same two strips under a node of scale (-1, 1, 1) - bmesh's face normal is computed from
    the LOCAL corners and taken through matrix_world's 3x3, so the mirror does not turn it: still 1 and 0, where the cross
    product of the world corners would say the opposite (negativescaletest's Shiny1). TwoIslands: one mesh holding a down
    strip and an up strip that share no vertex - two islands, one reversed (1). SkinnedDown: the down strip skinned to a
    joint at rest (1). Collinear: the down strip beside a triangle whose three corners lie on one line - its normal is
    exactly zero, so that island casts no vote and does not count (1). VertexTouch: two inward faces sharing only a
    vertex are two face islands (2). NonManifold: two inward and one outward face share an edge, forming one island
    with an inward average (1). TurnedDown / TurnedUp: the two strips under a node turned 90 degrees about glTF X and
    moved along X - the strips stand upright beside the axis and the local normal goes through a real rotation, not only
    a mirror: 1 and 0 (a transposed 3x3 would swap them). Every node its own mesh: no split."""
    b = F.Buf()
    gpos, _, _, gidx = F.grid(8, 8)
    kpos = [(x - 0.5, -5.0, y - 0.5) for x, y, _ in gpos]   # Blender z = glTF y: 5 below the plates

    def strip(x0, down):
        # 4 quads from x0 to x0 + 4 at glTF y = 5 (Blender z = 5), across glTF z in [-1, 1] (Blender y); corners
        # p0 (x, z0) p1 (x + 1, z0) p2 (x + 1, z1) p3 (x, z1): (p1 - p0) x (p2 - p0) = (0, -dx dz, 0), glTF -y = Blender -z = down
        pos = [(x0 + q, 5.0, -1.0) for q in range(5)] + [(x0 + q, 5.0, 1.0) for q in range(5)]   # shared corners: one island
        idx = []
        for q in range(4):
            p0, p1, p2, p3 = q, q + 1, 5 + q + 1, 5 + q
            idx += [p0, p1, p2, p0, p2, p3] if down else [p0, p2, p1, p0, p3, p2]
        return pos, idx

    def mesh(name, pos, idx, extra=None):
        attrs = {"POSITION": b.accessor(pos, "f", "VEC3")}
        if extra:
            attrs.update(extra)
        return {"name": name, "primitives": [{"attributes": attrs, "indices": b.accessor(idx, "H", "SCALAR")}]}

    dpos, didx = strip(0.0, True); upos, uidx = strip(10.0, False)
    tpos, tidx = strip(20.0, True); t2pos, t2idx = strip(30.0, False)
    meshes = [mesh("keel", kpos, gidx), mesh("platedown", dpos, didx), mesh("plateup", upos, uidx),
              mesh("mirroreddown", dpos, didx), mesh("mirroredup", upos, uidx),
              mesh("twoislands", tpos + t2pos, tidx + [i + len(tpos) for i in t2idx]),
              mesh("collinear", dpos + [(40.0, 5.0, 0.0), (41.0, 5.0, 0.0), (42.0, 5.0, 0.0)], didx + [len(dpos), len(dpos) + 1, len(dpos) + 2]),
              mesh("skinneddown", dpos, didx, {"JOINTS_0": b.accessor([(0, 0, 0, 0)] * len(dpos), "H", "VEC4", minmax=False),
                                               "WEIGHTS_0": b.accessor([(1, 0, 0, 0)] * len(dpos), "f", "VEC4", minmax=False)})]
    nodes = [{"name": "Keel", "mesh": 0}, {"name": "PlateDown", "mesh": 1}, {"name": "PlateUp", "mesh": 2},
             {"name": "MirroredDown", "mesh": 3, "scale": [-1, 1, 1]}, {"name": "MirroredUp", "mesh": 4, "scale": [-1, 1, 1]},
             {"name": "TwoIslands", "mesh": 5}, {"name": "Collinear", "mesh": 6},
             {"name": "Rig", "children": [8]}, {"name": "Joint"}, {"name": "SkinnedDown", "mesh": 7, "skin": 0}]
    skins = [{"name": "Skin", "joints": [8]}]
    meshes += [mesh("vertextouch", [(0, 5, 0), (1, 5, 0), (0, 5, 1), (-1, 5, 0), (0, 5, -1)], [0, 1, 2, 0, 3, 4]),
               mesh("nonmanifold", [(0, 5, 0), (1, 5, 0), (0, 5, 1), (0, 5, 2), (0, 5, -1)], [0, 1, 2, 0, 1, 3, 0, 1, 4])]
    nodes += [{"name": "VertexTouch", "mesh": 8}, {"name": "NonManifold", "mesh": 9}]
    meshes += [mesh("turneddown", dpos, didx), mesh("turnedup", upos, uidx)]
    nodes += [{"name": "TurnedDown", "mesh": 10, "rotation": [0.7071067811865476, 0, 0, 0.7071067811865476], "translation": [50, 0, 0]},
              {"name": "TurnedUp", "mesh": 11, "rotation": [0.7071067811865476, 0, 0, 0.7071067811865476], "translation": [60, 0, 0]}]
    root = F.base("insideout", meshes=meshes, nodes=nodes, skins=skins, scenes=[{"nodes": [0, 1, 2, 3, 4, 5, 6, 7, 9, 10, 11, 12, 13]}], scene=0)
    F.write_glb(os.path.join(out, "insideout.glb"), root, b)


def fx_custom_normals(out):
    """Blender's vertex normals for a mesh WITH file normals (the visibility verdict's normal ray): the importer sets them as
    custom normals, which Blender stores as two shorts against each corner's smooth-fan space and mixes back per vertex.
    Leaning: a quad whose file normals lean 10 degrees about X, +Z side and -Z side - the decoded vertex normals carry the
    quantization (read off Blender: v0 (-7.6e-9, -0.17361137, 0.98481423)). Flat: a quad whose file normals are +Y while its
    winding faces -Y - the fan normal is the face normal, the custom normal its opposite, and the decode lands 4.8e-5 off
    axis (v0 (-2.1e-12, -4.777114e-05, 1.0)), the Dragon's decal case. Every node its own mesh: no split."""
    import math
    b = F.Buf()
    th = math.radians(10.0)
    pos = [(0, 0, 0), (1, 0, 0), (1, 0, 1), (0, 0, 1)]
    idx = [0, 1, 2, 0, 2, 3]
    lean = [(0.0, math.cos(th), math.sin(th))] * 2 + [(0.0, math.cos(th), -math.sin(th))] * 2
    flat = [(0.0, 1.0, 0.0)] * 4
    meshes = [{"name": "leaning", "primitives": [{"attributes": {"POSITION": b.accessor(pos, "f", "VEC3"), "NORMAL": b.accessor(lean, "f", "VEC3", minmax=False)}, "indices": b.accessor(idx, "H", "SCALAR")}]},
              {"name": "flat", "primitives": [{"attributes": {"POSITION": b.accessor([(x + 5, y, z) for x, y, z in pos], "f", "VEC3"), "NORMAL": b.accessor(flat, "f", "VEC3", minmax=False)}, "indices": b.accessor(idx, "H", "SCALAR")}]}]
    nodes = [{"name": "Leaning", "mesh": 0}, {"name": "Flat", "mesh": 1}]
    root = F.base("custom_normals", meshes=meshes, nodes=nodes, scenes=[{"nodes": [0, 1]}], scene=0)
    F.write_glb(os.path.join(out, "custom_normals.glb"), root, b)


def fx_shared_skin_bind_pose(out):
    """Two skins share a joint: Blender guesses one bind pose from the LAST skin's IBM (+10), then retargets each
    mesh with its OWN IBM. Skin|A and NoIBM are outside Box; SkinB is inside. Pipe names exercise diagnostic parsing."""
    b = F.Buf()
    pos, idx = box(0, 0, 0, 2)
    identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]
    shifted = identity.copy(); shifted[12] = -10
    meshes = [{"primitives": [{"attributes": {"POSITION": b.accessor(pos, "f", "VEC3")}, "indices": b.accessor(idx, "H", "SCALAR")}]},
              {"primitives": [{"attributes": skinned(b)}]}]
    nodes = [{"name": "Rig", "children": [1]}, {"name": "Joint"}, {"name": "Box", "mesh": 0},
             {"name": "Skin|A", "mesh": 1, "skin": 0}, {"name": "SkinB", "mesh": 1, "skin": 1}, {"name": "NoIBM", "mesh": 1, "skin": 2}]
    skins = [{"skeleton": 0, "joints": [1], "inverseBindMatrices": b.accessor([matrix], "f", "MAT4")} for matrix in (identity, shifted)]
    skins.append({"skeleton": 0, "joints": [1]})
    root = F.base("shared_skin_bind_pose", meshes=meshes, nodes=nodes, skins=skins, scenes=[{"nodes": [0, 2, 3, 4, 5]}], scene=0)
    F.write_glb(os.path.join(out, "shared_skin_bind_pose.glb"), root, b)


def fx_zero_weight_joint(out):
    """Blender assigns a zero-weight vertex to its first JOINTS_0 influence (joint 1, translated +10), not joint 0."""
    b = F.Buf()
    attrs = skinned(b, joint=1)
    attrs["WEIGHTS_0"] = b.accessor([(0, 0, 0, 0)] * 3, "f", "VEC4", minmax=False)
    root = F.base("zero_weight_joint", meshes=[{"primitives": [{"attributes": attrs}]}],
                  nodes=[{"name": "Rig", "children": [1, 2]}, {"name": "Unused"}, {"name": "FirstInfluence", "translation": [10, 0, 0]},
                         {"name": "ZeroWeight", "mesh": 0, "skin": 0}], skins=[{"joints": [1, 2]}], scenes=[{"nodes": [0, 3]}], scene=0)
    F.write_glb(os.path.join(out, "zero_weight_joint.glb"), root, b)


def fx_bind_inverse_underflow(out):
    """An invertible IBM with det=1e-48 underflows in float32. Blender's safe inverse keeps Mesh finite inside Box."""
    b = F.Buf()
    attrs = skinned(b)
    attrs["NORMAL"] = b.accessor([(0, 0, 1)] * 3, "f", "VEC3", minmax=False)
    pos, idx = box(0, 0, 0, 2)
    ibm = [1e-16,0,0,0, 0,1e-16,0,0, 0,0,1e-16,0, 0,0,0,1]
    meshes = [{"primitives": [{"attributes": attrs}]},
              {"primitives": [{"attributes": {"POSITION": b.accessor(pos, "f", "VEC3")}, "indices": b.accessor(idx, "H", "SCALAR")}]}]
    nodes = [{"name": "Rig", "children": [1]}, {"name": "Joint", "scale": [1e16, 1e16, 1e16]},
             {"name": "Mesh", "mesh": 0, "skin": 0}, {"name": "Box", "mesh": 1}]
    skins = [{"skeleton": 0, "joints": [1], "inverseBindMatrices": b.accessor([ibm], "f", "MAT4")}]
    root = F.base("bind_inverse_underflow", meshes=meshes, nodes=nodes, skins=skins, scenes=[{"nodes": [0, 2, 3]}], scene=0)
    F.write_glb(os.path.join(out, "bind_inverse_underflow.glb"), root, b)


FIXTURES = [fx_naming, fx_order, fx_armature_names, fx_skinned_not_moved, fx_skinned_animated, fx_mesh_on_bone, fx_islands, fx_rotated_armature,
            fx_two_armatures, fx_nested_skins, fx_split_collision, fx_nonunit_rotation, fx_cameras, fx_camera_data_names,
            fx_name_tails, fx_long_names, fx_many_names, fx_bone_tails, fx_split_tails, fx_icosphere_mesh, fx_visibility, fx_visibility_skinned, fx_visibility_split, fx_insideout, fx_custom_normals,
            fx_shared_skin_bind_pose, fx_zero_weight_joint, fx_bind_inverse_underflow]


def main(out):
    os.makedirs(out, exist_ok=True)
    for fx in FIXTURES:
        fx(out)
    for name in sorted(os.listdir(out)):
        if name.endswith((".glb", ".gltf")):
            print(os.path.join(out, name).replace("\\", "/"))


if __name__ == "__main__":
    main(sys.argv[1])
