"""Miswire one of the twin armatures while preserving joint names and inverse bind matrices."""
import json
import pathlib
import struct
import sys


def mutate(rows_path, output_path, output_rows, mode):
    for line in pathlib.Path(rows_path).read_text(encoding="utf-8-sig").splitlines():
        fields = line.split("\t")
        if len(fields) < 6 or fields[0] != "PREP" or fields[2] != "full" or pathlib.Path(fields[1]).name != "export_skin_twins.glb":
            continue
        data = pathlib.Path(fields[5]).read_bytes()
        length, kind = struct.unpack_from("<II", data, 12)
        assert kind == 0x4E4F534A
        root = json.loads(data[20:20 + length])
        a = next(n for n in root["nodes"] if n.get("name") == "BodyA")
        b = next(n for n in root["nodes"] if n.get("name") == "BodyB")
        assert a["skin"] != b["skin"]
        if mode == "skin":
            a["skin"] = b["skin"]
        elif mode == "joints":
            root["skins"][a["skin"]]["joints"] = root["skins"][b["skin"]]["joints"]
        else:
            raise ValueError(mode)
        encoded = json.dumps(root, separators=(",", ":")).encode("utf-8")
        encoded += b" " * (-len(encoded) % 4)
        body = struct.pack("<II", len(encoded), kind) + encoded + data[20 + length:]
        pathlib.Path(output_path).write_bytes(struct.pack("<III", 0x46546C67, 2, 12 + len(body)) + body)
        fields[5] = str(pathlib.Path(output_path).resolve()).replace("\\", "/")
        pathlib.Path(output_rows).write_text("\t".join(fields) + "\n", encoding="utf-8")
        return
    raise RuntimeError("the twin-armature full export was not available")


if __name__ == "__main__":
    mutate(*sys.argv[1:])
