# The .glb/.gltf model files the registry names, one per line, as the editor resolves them (absolute paths; a
# recipe whose file is missing is skipped - the drill reads files, the Factory reports missing ones).
import json, os, sys
pack = sys.argv[1]
d = json.load(open(pack, encoding="utf-8"))
for m in d.get("models", []):
    f = (m.get("modelFile") or "").replace("\\", "/")
    if f.lower().endswith((".glb", ".gltf")) and os.path.exists(f):
        print(f)
