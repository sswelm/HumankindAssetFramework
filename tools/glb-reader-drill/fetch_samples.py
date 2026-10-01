"""The Khronos glTF-Sample-Assets named in samples.txt, fetched once into a cache the drills read from.

    python fetch_samples.py <cacheDir>            fetch what is missing (network), then list
    python fetch_samples.py <cacheDir> --list     list what the cache holds, no network: one line per sample,
                                                  <path> TAB <stage> TAB <text>  (stage "ok" = round-tripped;
                                                  "reader"/"guard"/"writer" = that stage must refuse it and say <text>)

A .gltf sample's sidecars (.bin, images) are found from its JSON and fetched beside it. A sample missing from the
cache is reported on stderr and left out of the listing, so a drill runs with what is there and says how many.
"""
import json, os, sys, urllib.parse, urllib.request

RAW = "https://raw.githubusercontent.com/KhronosGroup/glTF-Sample-Assets/main/Models/"
HERE = os.path.dirname(os.path.abspath(__file__))


def entries():
    for line in open(os.path.join(HERE, "samples.txt"), encoding="utf-8"):
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        path, _, expect = line.partition("|")
        path = path.strip(); stage, text = "ok", ""
        if expect.strip():
            words = expect.strip().split(None, 2)   # "expect", stage, text
            stage, text = words[1], words[2] if len(words) > 2 else ""
        yield path, stage, text


def fetch(rel, dest):
    url = RAW + "/".join(urllib.parse.quote(p) for p in rel.split("/"))
    os.makedirs(os.path.dirname(dest), exist_ok=True)
    with urllib.request.urlopen(url, timeout=60) as r, open(dest, "wb") as f:
        f.write(r.read())


def sidecars(gltf_path):
    root = json.load(open(gltf_path, encoding="utf-8"))
    for b in root.get("buffers", []) + root.get("images", []):
        uri = b.get("uri", "")
        if uri and not uri.startswith("data:"):
            yield urllib.parse.unquote(uri)


def main(argv):
    sys.stdout.reconfigure(encoding="utf-8")   # a sample is named with emoji; the console's code page is not the limit
    cache = argv[1]; list_only = "--list" in argv
    for rel, stage, text in entries():
        local = os.path.join(cache, rel.replace("/", os.sep))
        if not os.path.isfile(local):
            if list_only:
                print(f"missing: {rel}", file=sys.stderr); continue
            try:
                fetch(rel, local)
                if local.endswith(".gltf"):
                    for side in sidecars(local):
                        fetch(rel.rsplit("/", 1)[0] + "/" + side, os.path.join(os.path.dirname(local), side.replace("/", os.sep)))
            except Exception as e:
                print(f"missing: {rel} ({e})", file=sys.stderr)
                if os.path.exists(local):
                    os.remove(local)
                continue
        print(f"{local.replace(os.sep, '/')}\t{stage}\t{text}")


if __name__ == "__main__":
    main(sys.argv)
