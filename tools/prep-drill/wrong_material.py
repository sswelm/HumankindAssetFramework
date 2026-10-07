"""Swap the materials of two triangle primitives in one of Blender's exports: the material list keeps its names and
order and every primitive its vertex data, so only a comparison of each primitive's material INDEX can tell."""
import json
import pathlib
import struct
import sys


def mutate(rows_path, output_path, output_rows):
    for line in pathlib.Path(rows_path).read_text(encoding="utf-8-sig").splitlines():
        fields = line.split("\t")
        if len(fields) < 6 or fields[0] != "PREP" or fields[2] != "full":
            continue
        data = pathlib.Path(fields[5]).read_bytes()
        length, kind = struct.unpack_from("<II", data, 12)
        assert kind == 0x4E4F534A
        root = json.loads(data[20:20 + length])
        prims = [p for mesh in root.get("meshes", []) for p in mesh["primitives"] if p.get("mode", 4) == 4]
        pair = next(((a, b) for i, a in enumerate(prims) for b in prims[i + 1:] if a.get("material") != b.get("material")), None)
        if pair is None:
            continue
        a, b = pair
        ma, mb = a.get("material"), b.get("material")
        for prim, material in ((a, mb), (b, ma)):
            if material is None:
                prim.pop("material", None)
            else:
                prim["material"] = material
        encoded = json.dumps(root, separators=(",", ":")).encode("utf-8")
        encoded += b" " * (-len(encoded) % 4)
        body = struct.pack("<II", len(encoded), kind) + encoded + data[20 + length:]
        pathlib.Path(output_path).write_bytes(struct.pack("<III", 0x46546C67, 2, 12 + len(body)) + body)
        fields[5] = str(pathlib.Path(output_path).resolve()).replace("\\", "/")
        pathlib.Path(output_rows).write_text("\t".join(fields) + "\n", encoding="utf-8")
        return
    raise RuntimeError("no export with two primitives of different materials was available")


if __name__ == "__main__":
    mutate(*sys.argv[1:])
