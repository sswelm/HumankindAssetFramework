#!/usr/bin/env python
"""oracle.py - the recorded Blender oracle of the push gate's parity drills (2026-10-28).

The parity drills hold a C# port to what Blender does. Until this file every one of them ran Blender on every push;
now Blender runs only when the record is REFRESHED (tools/refresh_blender_oracle.sh, HAF_ORACLE=record) and the gate
REPLAYS the record (HAF_ORACLE=replay, the default). A record is one Blender invocation's raw output, its exit code and
the output folders it wrote, keyed by what the invocation depends on: the dump script, the Blender script under test,
the input files (their bytes, not their paths), the job or shard lists (their text, with every file they name hashed by
content too) and the literal arguments. A replay whose key differs from the record's names the item that changed and
exits 99 - the drill then SKIPs, which the gate counts as a failure, so a changed input or script cannot pass on a stale
record. Paths under the drill's temp folder are stored as @WTMP@ and put back on replay, so a record made in one temp
folder replays in another.

    oracle.py store --store S --drill D --name N --wtmp W [--tmpd T] [--wroot R] --rc RC --raw FILE [--dirs rel...] [--blender EXE] -- item...
    oracle.py load  --store S --drill D --name N --wtmp W [--tmpd T] [--wroot R] --raw FILE [--dirs rel...] -- item...
    oracle.py key   --wtmp W [--tmpd T] [--wroot R] -- item...            (prints the key; for tests and curiosity)

load prints "RC <n>" on success (exit 0); exit 99 when there is no usable record (the reason printed); exit 1 on an error.
An item that is an existing file is hashed by content; a .txt/.json/.args item is a LIST: its normalised text is hashed
and every token in it that names an existing file is hashed by content as well; any other item is a literal string.
"""
import hashlib, json, os, shutil, sys, time

PLACEHOLDERS = ("@WTMP@", "@TMPD@", "@ROOT@")


def digest_file(path):
    h = hashlib.blake2b(digest_size=16)
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def digest_text(text):
    return hashlib.blake2b(text.encode("utf-8"), digest_size=16).hexdigest()


class Paths:
    """The run-specific prefixes, longest first, each in forward and backslash form."""
    def __init__(self, wtmp, tmpd, wroot):
        pairs = []
        for value, tag in ((wtmp, "@WTMP@"), (tmpd, "@TMPD@"), (wroot, "@ROOT@")):
            if not value:
                continue
            value = value.rstrip("/\\")
            for form in (value.replace("\\", "/"), value.replace("/", "\\")):
                if (form, tag) not in pairs:
                    pairs.append((form, tag))
        pairs.sort(key=lambda p: -len(p[0]))
        self.pairs = pairs
        self.wtmp = (wtmp or "").rstrip("/\\").replace("\\", "/")
        self.tmpd = (tmpd or "").rstrip("/\\").replace("\\", "/")
        self.wroot = (wroot or "").rstrip("/\\").replace("\\", "/")

    def normalise(self, text):
        for form, tag in self.pairs:
            text = text.replace(form, tag)
        return text

    def restore(self, text):
        return text.replace("@WTMP@", self.wtmp).replace("@TMPD@", self.tmpd or self.wtmp).replace("@ROOT@", self.wroot)

    def normalise_bytes(self, data):
        for form, tag in self.pairs:
            data = data.replace(form.encode("utf-8"), tag.encode("utf-8"))
        return data

    def restore_bytes(self, data):
        return (data.replace(b"@WTMP@", self.wtmp.encode("utf-8"))
                    .replace(b"@TMPD@", (self.tmpd or self.wtmp).encode("utf-8"))
                    .replace(b"@ROOT@", self.wroot.encode("utf-8")))


LIST_EXT = (".txt", ".json", ".args")


def as_path(item):
    """The item as a local path if it names an existing file (the drills pass cygpath -m forms; a posix /c/... form too)."""
    for cand in (item, item[1:] if item.startswith("@") else None):
        if cand and os.path.isfile(cand):
            return cand
    if item.startswith("/") and len(item) > 3 and item[2] == "/":   # /c/Repo/... -> C:/Repo/...
        cand = item[1].upper() + ":" + item[2:]
        if os.path.isfile(cand):
            return cand
    return None


def describe_items(items, paths):
    """One entry per item: a stable description with digests, independent of the temp folder of the run."""
    out = []
    for item in items:
        path = as_path(item)
        shown = paths.normalise(item.replace("\\", "/"))
        if path is None:
            out.append({"item": shown, "kind": "text", "digest": digest_text(shown)})
            continue
        if path.lower().endswith(LIST_EXT):
            try:
                text = open(path, "r", encoding="utf-8", errors="replace").read().replace("\r\n", "\n")
            except OSError as e:
                print("FAIL - cannot read %s: %s" % (item, e)); sys.exit(1)
            refs = {}
            tokens = []
            if path.lower().endswith(".json"):   # a jobs file: every string in it, wherever it sits
                def walk(v):
                    if isinstance(v, str): tokens.append(v)
                    elif isinstance(v, dict): [walk(x) for x in v.values()]
                    elif isinstance(v, list): [walk(x) for x in v]
                try:
                    walk(json.loads(text))
                except ValueError:
                    tokens = text.split("\n")
            else:
                tokens = text.split("\n")
            for line in tokens:
                for sep in ("\t", "|"):
                    line = line.replace(sep, "\n")
                for token in line.split("\n"):
                    token = token.strip()
                    if not token or token in refs:
                        continue
                    tpath = as_path(token)
                    if tpath is not None and not tpath.lower().endswith(LIST_EXT):
                        refs[paths.normalise(token.replace("\\", "/"))] = digest_file(tpath)
            out.append({"item": shown, "kind": "list", "digest": digest_text(paths.normalise(text)), "refs": refs})
        else:
            out.append({"item": shown, "kind": "file", "digest": digest_file(path)})
    return out


def key_of(described):
    return hashlib.blake2b(json.dumps(described, sort_keys=True).encode("utf-8"), digest_size=16).hexdigest()


def differences(recorded, current):
    """The items whose description changed, in plain words."""
    rec = {d["item"]: d for d in recorded}
    cur = {d["item"]: d for d in current}
    out = []
    for name, d in cur.items():
        r = rec.get(name)
        if r is None:
            out.append("new item %s" % name); continue
        if r.get("digest") != d.get("digest"):
            out.append("%s changed" % name)
        for ref, dg in d.get("refs", {}).items():
            if r.get("refs", {}).get(ref) != dg:
                out.append("%s (named in %s) changed" % (ref, name))
        for ref in r.get("refs", {}):
            if ref not in d.get("refs", {}):
                out.append("%s (named in %s) is gone" % (ref, name))
    for name in rec:
        if name not in cur:
            out.append("recorded item %s is gone" % name)
    return out


def parse(argv):
    if not argv or argv[0] not in ("store", "load", "key"):
        print(__doc__); sys.exit(2)
    op = argv[0]; opts = {"dirs": []}; i = 1
    while i < len(argv):
        a = argv[i]
        if a == "--":
            opts["items"] = argv[i + 1:]; break
        if a == "--dirs":
            i += 1
            while i < len(argv) and not argv[i].startswith("--"):
                opts["dirs"].append(argv[i]); i += 1
            continue
        if a.startswith("--") and i + 1 < len(argv):
            opts[a[2:]] = argv[i + 1]; i += 2; continue
        print("FAIL - bad argument %s" % a); sys.exit(2)
    opts.setdefault("items", [])
    return op, opts


def entry_dir(opts):
    return os.path.join(opts["store"], opts["drill"], opts["name"])


def main(argv):
    op, o = parse(argv)
    paths = Paths(o.get("wtmp"), o.get("tmpd"), o.get("wroot"))
    described = describe_items(o["items"], paths)
    key = key_of(described)
    if op == "key":
        print(key); return 0
    for need in ("store", "drill", "name", "raw"):
        if not o.get(need):
            print("FAIL - --%s is required" % need); return 2
    d = entry_dir(o)
    if op == "store":
        rc = int(o.get("rc", "0"))
        if os.path.isdir(d):
            shutil.rmtree(d)
        os.makedirs(os.path.join(d, "dirs"), exist_ok=True)
        root = o["store"]
        ignore = os.path.join(root, ".gitignore")
        if not os.path.exists(ignore):
            with open(ignore, "w", encoding="utf-8", newline="\n") as f:
                f.write("# the recorded Blender oracle of the push gate: machine-local, refreshed by tools/refresh_blender_oracle.sh\n*\n")
        with open(o["raw"], "rb") as f:
            raw = f.read()
        with open(os.path.join(d, "raw.txt"), "wb") as f:
            f.write(paths.normalise_bytes(raw))
        dirs = []
        for rel in o["dirs"]:
            src = os.path.join(paths.wtmp, rel)
            if os.path.isdir(src):
                shutil.copytree(src, os.path.join(d, "dirs", rel))
                dirs.append(rel)
            else:
                dirs.append(rel)   # an output folder Blender never made: restored as absent, the drill judges that
        meta = {"key": key, "rc": rc, "recorded": time.strftime("%Y-%m-%d %H:%M:%S"), "blender": o.get("blender", ""),
                "dirs": dirs, "items": described}
        with open(os.path.join(d, "meta.json"), "w", encoding="utf-8", newline="\n") as f:
            json.dump(meta, f, indent=1, sort_keys=True)
        print("RECORDED %s/%s key %s rc %d" % (o["drill"], o["name"], key, rc)); return 0
    # load
    meta_path = os.path.join(d, "meta.json")
    if not os.path.isfile(meta_path):
        print("no record for %s/%s (never recorded under %s)" % (o["drill"], o["name"], o["store"])); return 99
    try:
        meta = json.load(open(meta_path, encoding="utf-8"))
    except (OSError, ValueError) as e:
        print("the record for %s/%s is unreadable: %s" % (o["drill"], o["name"], e)); return 99
    if meta.get("key") != key:
        diffs = differences(meta.get("items", []), described)
        print("the record for %s/%s (made %s) is STALE: %s" % (o["drill"], o["name"], meta.get("recorded", "?"),
              "; ".join(diffs[:6]) + ("; ..." if len(diffs) > 6 else "") if diffs else "a key mismatch")); return 99
    with open(os.path.join(d, "raw.txt"), "rb") as f:
        raw = f.read()
    with open(o["raw"], "wb") as f:
        f.write(paths.restore_bytes(raw))
    for rel in meta.get("dirs", []):
        src = os.path.join(d, "dirs", rel); dst = os.path.join(paths.wtmp, rel)
        if os.path.isdir(src):
            if os.path.isdir(dst):
                shutil.rmtree(dst)
            shutil.copytree(src, dst)
    print("RC %d" % int(meta.get("rc", 0))); return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
