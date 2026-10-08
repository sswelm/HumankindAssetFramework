"""Remove a nonidentity inverse-bind accessor from a Blender export for the negative regression check."""
import json
import pathlib
import struct
import sys


def mutate(rows_path, output_path, output_rows):
    identity = tuple(1.0 if k % 5 == 0 else 0.0 for k in range(16))
    for line in pathlib.Path(rows_path).read_text(encoding="utf-8-sig").splitlines():
        fields = line.split("\t")
        if len(fields) < 6 or fields[0] != "PREP":
            continue
        data = pathlib.Path(fields[5]).read_bytes()
        length, kind = struct.unpack_from("<II", data, 12)
        assert kind == 0x4E4F534A
        root = json.loads(data[20:20 + length])
        for skin in root.get("skins", []):
            if "inverseBindMatrices" not in skin:
                continue
            accessor = root["accessors"][skin["inverseBindMatrices"]]
            assert accessor["type"] == "MAT4" and accessor["componentType"] == 5126
            view = root["bufferViews"][accessor["bufferView"]]
            offset = 20 + length + 8 + view.get("byteOffset", 0) + accessor.get("byteOffset", 0)
            stride = view.get("byteStride", 64)
            if all(struct.unpack_from("<16f", data, offset + i * stride) == identity for i in range(accessor["count"])):
                continue
            del skin["inverseBindMatrices"]
            encoded = json.dumps(root, separators=(",", ":")).encode("utf-8")
            encoded += b" " * (-len(encoded) % 4)
            body = struct.pack("<II", len(encoded), kind) + encoded + data[20 + length:]
            pathlib.Path(output_path).write_bytes(struct.pack("<III", 0x46546C67, 2, 12 + len(body)) + body)
            fields[5] = str(pathlib.Path(output_path).resolve()).replace("\\", "/")
            pathlib.Path(output_rows).write_text("\t".join(fields) + "\n", encoding="utf-8")
            return
    raise RuntimeError("no exported skin with nonidentity inverse bind matrices was available")


if __name__ == "__main__":
    mutate(*sys.argv[1:])
