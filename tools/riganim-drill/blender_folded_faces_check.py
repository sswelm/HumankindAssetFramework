"""Exercise rig_anim.py's actual Reduce block, including Blender's small-mesh modifier no-op.

Run with Blender --background --python-exit-code 1 --python <this file> -- <rig_anim.py>.
The FBX route can carry quads: equal vertex sets do not imply equal polygon boundaries.
"""
import contextlib
import io
import itertools
import sys

import bmesh
import bpy


script = sys.argv[sys.argv.index("--") + 1]
source = open(script, encoding="utf-8").read()
start = "total = sum(len(p.vertices) - 2 for p in me.polygons)"
end = "\n# GUARANTEE the skin binding:"
assert source.count(start) == 1 and source.count(end) == 1, "the Reduce block's boundaries changed"
reduce_code = compile(source[source.index(start):source.index(end)], script, "exec")


def face_rows(me):
    return [
        (tuple(p.vertices), p.material_index, p.use_smooth,
         tuple(tuple(me.uv_layers[0].data[i].uv) for i in p.loop_indices),
         tuple(tuple(me.color_attributes["Col"].data[i].color) for i in p.loop_indices))
        for p in me.polygons
    ]


def check(name, faces, target, keep):
    me = bpy.data.meshes.new(name)
    me.from_pydata([(0, 0, 0), (1, 0, 0), (0, 1, 0), (0, 0, 1)], [], faces)
    joined = bpy.data.objects.new(name, me)
    bpy.context.collection.objects.link(joined)
    bpy.ops.object.select_all(action="DESELECT")
    joined.select_set(True)
    bpy.context.view_layer.objects.active = joined
    uv = me.uv_layers.new()
    color = me.color_attributes.new(name="Col", type="BYTE_COLOR", domain="CORNER")
    for f, p in enumerate(me.polygons):
        me.materials.append(bpy.data.materials.new(name + str(f)))
        p.material_index = f
        p.use_smooth = f % 2 == 0
        for i in p.loop_indices:
            uv.data[i].uv = (i / 16, (i + 1) / 16)
            color.data[i].color = (f / 4, i / 16, 0.5, 1)
    group = joined.vertex_groups.new(name="Bone")
    group.add(list(range(len(me.vertices))), 0.75, "REPLACE")
    before = face_rows(me)
    vertices = [tuple(v.co) for v in me.vertices]
    edges = [tuple(e.vertices) for e in me.edges]
    weights = [[(g.group, g.weight) for g in v.groups] for v in me.vertices]
    log = io.StringIO()
    with contextlib.redirect_stdout(log):
        exec(reduce_code, dict(me=me, joined=joined, target=target, bpy=bpy, bmesh=bmesh))
    assert face_rows(me) == [before[i] for i in keep], name + ": changed the wrong faces or their corner data"
    assert [tuple(v.co) for v in me.vertices] == vertices, name + ": changed vertices"
    assert [tuple(e.vertices) for e in me.edges] == edges, name + ": changed edges"
    assert [[(g.group, g.weight) for g in v.groups] for v in me.vertices] == weights, name + ": changed weights"
    if target > 0 and sum(len(f) - 2 for f in faces) > target:
        assert "RIGANIM decimate " in log.getvalue(), name + ": did not enter Reduce"
    bpy.data.objects.remove(joined, do_unlink=True)
    bpy.data.meshes.remove(me)


# Different edge loops on four noncoplanar vertices describe different surfaces. With just two
# polygons Blender leaves the mesh unreduced, even with use_collapse_triangulate=True.
check("different quad boundaries", [(0, 1, 2, 3), (0, 2, 1, 3)], 3, [0, 1])
for winding in itertools.permutations((0, 1, 2)):
    check("duplicate triangle " + str(winding), [(0, 1, 2), winding], 1, [0])
check("corner data after a gap", [(0, 1, 2), (2, 1, 0), (0, 2, 3)], 2, [0, 2])
check("no duplicate", [(0, 1, 2), (2, 1, 3), (0, 3, 1)], 2, [0, 1, 2])
for target in (0, 4):
    check("Reduce skipped " + str(target), [(0, 1, 2), (2, 1, 0)], target, [0, 1])
print("FOLDED PASS: 11 cases; distinct quads, all triangle windings, corner data, weights and skipped Reduce")
