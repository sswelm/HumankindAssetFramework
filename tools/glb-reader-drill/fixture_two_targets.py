# A synthetic GLB the registry does not have (review of PR #109, round 5): one animation driving TWO nodes whose
# channels end at different times (1 s and 2 s) and start after zero (0.5 s on the second). Blender 5.1 imports that as
# ONE action spanning 0..2 s (slotted actions: one action per glTF animation, every target a slot); a per-node reading
# would say {0.5, 1}. Written beside the drill's temp files; both sides read it like any registry file.
import json, struct, sys

def accessor(buffers, views, accessors, data, fmt, comp_type, kind, comps):
    raw = b"".join(struct.pack("<" + fmt * comps, *v) if comps > 1 else struct.pack("<" + fmt, v) for v in data)
    while len(buffers) % 4: buffers.append(0)
    views.append({"buffer": 0, "byteOffset": len(buffers), "byteLength": len(raw)})
    buffers.extend(raw)
    accessors.append({"bufferView": len(views) - 1, "componentType": comp_type, "type": kind, "count": len(data)})
    if kind == "VEC3" and comp_type == 5126:
        accessors[-1]["min"] = [min(v[i] for v in data) for i in range(3)]
        accessors[-1]["max"] = [max(v[i] for v in data) for i in range(3)]
    return len(accessors) - 1

buffers = bytearray(); views = []; accessors = []
pos = accessor(buffers, views, accessors, [(0, 0, 0), (1, 0, 0), (0, 1, 0)], "f", 5126, "VEC3", 3)
idx = accessor(buffers, views, accessors, [0, 1, 2], "H", 5123, "SCALAR", 1)
t_a = accessor(buffers, views, accessors, [0.0, 1.0], "f", 5126, "SCALAR", 1)
v_a = accessor(buffers, views, accessors, [(0, 0, 0), (1, 0, 0)], "f", 5126, "VEC3", 3)
t_b = accessor(buffers, views, accessors, [0.5, 2.0], "f", 5126, "SCALAR", 1)
v_b = accessor(buffers, views, accessors, [(0, 0, 0), (0, 1, 0)], "f", 5126, "VEC3", 3)
root = {
    "asset": {"version": "2.0", "generator": "fixture_two_targets"},
    "buffers": [{"byteLength": len(buffers)}], "bufferViews": views, "accessors": accessors,
    "meshes": [{"name": "tri", "primitives": [{"attributes": {"POSITION": pos}, "indices": idx}]}],
    "nodes": [{"name": "A", "mesh": 0}, {"name": "B", "mesh": 0, "translation": [3, 0, 0]}],
    "scenes": [{"nodes": [0, 1]}], "scene": 0,
    "animations": [{"name": "both", "samplers": [{"input": t_a, "output": v_a}, {"input": t_b, "output": v_b}],
                    "channels": [{"sampler": 0, "target": {"node": 0, "path": "translation"}},
                                 {"sampler": 1, "target": {"node": 1, "path": "translation"}}]}],
}
js = json.dumps(root, separators=(",", ":")).encode("utf-8")
while len(js) % 4: js += b" "
while len(buffers) % 4: buffers.append(0)
glb = struct.pack("<III", 0x46546C67, 2, 12 + 8 + len(js) + 8 + len(buffers)) + struct.pack("<II", len(js), 0x4E4F534A) + js + struct.pack("<II", len(buffers), 0x004E4942) + bytes(buffers)
open(sys.argv[1], "wb").write(glb)
print(sys.argv[1])
