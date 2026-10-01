"""Every `extensions` object and every `extras` value (any JSON type - the schema allows any) in a source file must sit
at a path the model CARRIES (review of PR #110):
the reader does not model what lives elsewhere, so the writer would drop it without a word, and the writer drill's
field compare - reader against reader - cannot see that. This reads the files' JSON directly and names the first
object at a path the model does not carry.

    python carried_paths.py <file.glb or .gltf>...      ->  CARRIED <n files> <k objects>   |   FAIL <file> <path>
"""
import json, struct, sys

# a path is carried when it is one of these, or lies inside one (a nested extension inside a material's payload,
# a value inside an extras object); array indices are written [] - keep in step with HafModel's verbatim fields
CARRIED = (
    "asset.extras",                         # HafModel.AssetExtrasJson
    "materials[].extensions",               # HafMaterial.ExtensionsJson
    "materials[].extras",                   # HafMaterial.ExtrasJson
    "nodes[].extras",                       # HafNode.ExtrasJson
    "meshes[].extras",                      # HafMesh.ExtrasJson
    "scenes[].extras",                      # HafScene.ExtrasJson
    "samplers[].extensions", "samplers[].extras",   # HafModel.Samplers (the whole object, verbatim)
)


def carried(path):
    # inside a carried value everything is carried, whether reached through a key (".") or an array ("[]")
    return any(path == c or path.startswith(c + ".") or path.startswith(c + "[") for c in CARRIED)


def read_json(path):
    with open(path, "rb") as fh:
        head = fh.read(12)
        if len(head) == 12 and struct.unpack("<I", head[:4])[0] == 0x46546C67:
            clen, _ = struct.unpack("<II", fh.read(8))
            return json.loads(fh.read(clen).decode("utf-8"))
        fh.seek(0)
        return json.loads(fh.read().decode("utf-8"))


def walk(node, path, found, bad):
    if isinstance(node, dict):
        for k, v in node.items():
            p = path + "." + k if path else k
            if k == "extras" or (k == "extensions" and isinstance(v, dict) and v):   # an empty extensions object carries nothing; an extras of any shape is data
                found.append(p)
                if carried(p):
                    continue          # verbatim from here down: an extras nested inside it travels with it
                bad.append(p)
            walk(v, p, found, bad)
    elif isinstance(node, list):
        for v in node:
            walk(v, path + "[]", found, bad)


def main(files):
    total = 0
    for f in files:
        found, bad = [], []
        walk(read_json(f), "", found, bad)
        total += len(found)
        if bad:
            print(f"FAIL\t{f}\t{bad[0]} (and {len(bad) - 1} more)" if len(bad) > 1 else f"FAIL\t{f}\t{bad[0]}")
            return 1
    print(f"CARRIED\t{len(files)} files\t{total} extension/extras objects, every one at a path the model carries")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
