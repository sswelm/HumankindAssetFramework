#!/usr/bin/env python
"""oracle.py - the recorded Blender oracle of the push gate's parity drills (2026-10-28).

The parity drills hold a C# port to what Blender does. Until this file every one of them ran Blender on every push;
now Blender runs only when the record is REFRESHED (tools/refresh_blender_oracle.sh, HAF_ORACLE=record) and the gate
REPLAYS the record (HAF_ORACLE=replay, the default). A record is one Blender invocation's raw output, its exit code and
the output folders it wrote, keyed by what the invocation depends on: the dump script, the Blender script under test,
the declared input files, nested job/shard lists, external glTF/GLB buffers and images, the actual Blender arguments,
and a conservative set of tracked Python support files in tools/ and editor/Tools~/. A replay whose key differs from the record's names the item that changed and
exits 99 - the drill then SKIPs, which the gate counts as a failure, so a changed input or script cannot pass on a stale
record. Paths under the drill's temp folder are stored as @WTMP@ and put back on replay, so a record made in one temp
folder replays in another. Format 3 normalises script dependency line endings and checks the record manifest and every payload file before restoring contained
outputs, including recorded absence. Literal marker text and non-UTF-8 stdout bytes round-trip without alteration.
Old records require a one-time refresh; failed writes or changing inputs do not replace the previous record.

    oracle.py store --store S --drill D --name N --wtmp W [--tmpd T] [--wroot R] --rc RC --raw FILE [--dirs rel...] [--blender EXE] [--arg VALUE]... [--expected-key KEY] -- item...
    oracle.py load  --store S --drill D --name N --wtmp W [--tmpd T] [--wroot R] --raw FILE [--dirs rel...] [--arg VALUE]... -- item...
    oracle.py key   --wtmp W [--tmpd T] [--wroot R] -- item...            (prints the key; for tests and curiosity)
    oracle.py deps  --root R -- item...   (the tracked Python files the .py items reach by import or by name, as JSON)

load prints "RC <n>" on success (exit 0); exit 99 when there is no usable record (the reason printed); exit 1 on an error.
An existing input file is content-keyed. A .txt/.json/.args input is a recursive list, including paths used as JSON keys.
Declared glTF resources are always byte-keyed regardless of extension. --arg values are typed literals, so created output
files are never mistaken for input dependencies. Network resource URIs cannot be fingerprinted and fail closed.
"""
import hashlib, json, ntpath, os, re, shutil, struct, sys, tempfile, time
from pathlib import Path
from urllib.parse import unquote, urlsplit

PLACEHOLDERS = ("@WTMP@", "@TMPD@", "@ROOT@")


def digest_file(path):
    h = hashlib.blake2b(digest_size=16)
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def digest_text(text):
    return hashlib.blake2b(text.encode("utf-8", "surrogateescape"), digest_size=16).hexdigest()


def digest_script(path):
    """A tracked script hashed with CRLF read as LF: the same commit checks out with either ending (core.autocrlf), and a
    record made in one checkout must answer in another (2026-10-29: four editor/Tools~ scripts were LF in the main checkout
    and CRLF in a worktree, and a push that changed none of them was STALE on all four)."""
    with open(path, "rb") as f: data = f.read()
    return hashlib.blake2b(data.replace(b"\r\n", b"\n"), digest_size=16).hexdigest()


class Paths:
    """Portable path references in raw output; literal marker text and arbitrary stdout bytes round-trip exactly."""
    def __init__(self, wtmp, tmpd, wroot):
        pairs = {}
        for value, tag in ((wtmp, "@WTMP@"), (tmpd, "@TMPD@"), (wroot, "@ROOT@")):
            if value:
                value = value.rstrip("/\\")
                if not value or (len(value) == 2 and value[1] == ":"): continue
                for form in (value.replace("\\", "/"), value.replace("/", "\\")):
                    pairs.setdefault(form.lower(), tag)
        forms = sorted(pairs, key=len, reverse=True)
        self.groups = {"p%d" % i: pairs[form] for i, form in enumerate(forms)}
        self.pattern = re.compile("|".join("(?P<p%d>%s)" % (i, re.escape(form)) for i, form in enumerate(forms)), re.IGNORECASE) if forms else None
        self.wtmp = os.path.normpath(wtmp).replace("\\", "/") if wtmp else ""
        self.tmpd = os.path.normpath(tmpd).replace("\\", "/") if tmpd else ""
        self.wroot = os.path.normpath(wroot).replace("\\", "/") if wroot else ""
        self.values = {"@WTMP@": self.wtmp, "@TMPD@": self.tmpd or self.wtmp, "@ROOT@": self.wroot}

    def normalise(self, text, preserve_case=True):
        # Escape literals outside matched prefixes, so a model named @ROOT@ cannot turn into a filesystem path.
        result = []; start = 0
        for match in self.pattern.finditer(text) if self.pattern else ():
            result.append(text[start:match.start()].replace("@", "@@"))
            tag = self.groups[match.lastgroup]; spelling = match.group(0)
            if preserve_case and spelling == spelling.lower() and spelling != spelling.upper(): tag = tag.lower()
            result.append(tag); start = match.end()
        result.append(text[start:].replace("@", "@@"))
        return "".join(result)

    def restore(self, text):
        def replace(match):
            tag = match.group(0)
            if tag == "@@": return "@"
            value = self.values[tag.upper()]
            return value.lower() if tag == tag.lower() else value
        return re.sub(r"@@|@(?:WTMP|TMPD|ROOT)@", replace, text, flags=re.IGNORECASE)

    def normalise_bytes(self, data):
        return self.normalise(data.decode("utf-8", "surrogateescape")).encode("utf-8", "surrogateescape")

    def restore_bytes(self, data):
        return self.restore(data.decode("utf-8", "surrogateescape")).encode("utf-8", "surrogateescape")

    def key_text(self, text):
        return self.normalise(text, preserve_case=False)

    def argument(self, text):
        # Arguments are literals, including output paths which may exist only AFTER recording. Canonicalise path
        # separators only for path-shaped arguments; backslashes inside e.g. --python-expr remain significant.
        candidate = text[1:] if text.startswith("@") else text
        if ntpath.splitdrive(candidate)[0] or candidate.startswith(("/", "\\", "./", ".\\", "../", "..\\")):
            text = text.replace("\\", "/")
        return self.key_text(text)


LIST_EXT = (".txt", ".json", ".args")
SCRIPT_EXT = (".py", ".sh")   # hashed with CRLF read as LF (digest_script)


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


def text_tokens(text, is_json):
    if is_json:
        def walk(value):
            if isinstance(value, str): yield value
            elif isinstance(value, dict):
                for key, item in value.items():
                    yield key
                    yield from walk(item)
            elif isinstance(value, list):
                for item in value: yield from walk(item)
        try: return list(walk(json.loads(text)))
        except ValueError: pass
    return text.splitlines()


def gltf_resources(path):
    """Declared external buffers/images are inputs too, including those referenced by a GLB JSON chunk."""
    suffix = Path(path).suffix.lower()
    if suffix not in (".gltf", ".glb"): return []
    try:
        with open(path, "rb") as source:
            if suffix == ".gltf": data = json.load(source)
            else:
                header = source.read(20)
                if len(header) != 20 or header[:4] != b"glTF": return []
                length, kind = struct.unpack_from("<II", header, 12)
                if kind != 0x4e4f534a: return []
                data = json.loads(source.read(length).rstrip(b"\x00 ").decode("utf-8"))
    except (ValueError, UnicodeError, struct.error): return []  # malformed inputs must still record Blender's failure
    if not isinstance(data, dict): return []
    refs = []
    for section in ("buffers", "images"):
        for item in data.get(section, []) if isinstance(data.get(section, []), list) else []:
            if not isinstance(item, dict) or not isinstance(item.get("uri"), str): continue
            uri = item["uri"]
            if uri.lower().startswith("data:"): continue
            parsed = urlsplit(uri)
            if parsed.scheme and parsed.scheme != "file" and not ntpath.splitdrive(uri)[0]:
                raise ValueError("cannot fingerprint external resource URI " + uri)
            decoded = unquote(uri if ntpath.splitdrive(uri)[0] else parsed.path)
            if parsed.scheme == "file" and re.match(r"^/[A-Za-z]:/", decoded): decoded = decoded[1:]
            if parsed.netloc and parsed.scheme == "file": decoded = "//" + parsed.netloc + decoded
            refs.append(os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(path)), decoded)))
    return refs


def normalised_list(text, is_json, paths):
    if is_json:
        try:
            json.loads(text)
            return re.sub(r'"(?:\\.|[^"\\])*"', lambda m: json.dumps(paths.key_text(json.loads(m.group(0)))), text)
        except ValueError: pass
    return paths.key_text(text)


def describe_items(items, paths, arguments=()):
    """Hash declared inputs and their nested job/asset dependencies. Output arguments remain typed literals."""
    out = []
    for item in items:
        path = as_path(item); shown = paths.key_text(item.replace("\\", "/")) if path else paths.argument(item)
        if path is None:
            out.append({"item": shown, "kind": "text", "digest": digest_text(shown)}); continue
        refs = {}; seen = set()
        def visit(current, top=False, asset=False):
            identity = (os.path.normcase(os.path.realpath(current)), asset)
            if identity in seen: return None
            seen.add(identity)
            is_list = not asset and current.lower().endswith(LIST_EXT)
            if is_list:
                with open(current, "r", encoding="utf-8", errors="surrogateescape") as source: text = source.read().replace("\r\n", "\n")
                digest = digest_text(normalised_list(text, current.lower().endswith(".json"), paths))
            else: digest = digest_script(current) if not asset and current.lower().endswith(SCRIPT_EXT) else digest_file(current)
            if not top: refs[paths.key_text(os.path.abspath(current).replace("\\", "/")) + (" (external asset)" if asset else "")] = digest
            if is_list:
                for line in text_tokens(text, current.lower().endswith(".json")):
                    for token in line.replace("\t", "\n").replace("|", "\n").splitlines():
                        token = token.strip()
                        if not token: continue
                        candidates = [token]
                        if not os.path.isabs(token) and not ntpath.splitdrive(token)[0]: candidates.append(os.path.join(os.path.dirname(current), token))
                        for candidate in candidates:
                            found = as_path(candidate)
                            if found: visit(found)
            elif not asset:
                for resource in gltf_resources(current):
                    if os.path.isfile(resource): visit(resource, asset=True)
                    else: refs[paths.key_text(resource.replace("\\", "/")) + " (external asset)"] = None
            return digest
        digest = visit(path, top=True)
        description = {"item": shown, "kind": "list" if path.lower().endswith(LIST_EXT) else "file", "digest": digest}
        if refs or description["kind"] == "list": description["refs"] = refs
        out.append(description)
    if arguments:
        values = [paths.argument(arg) for arg in arguments]
        out.append({"item": "<Blender arguments>", "kind": "arguments", "digest": digest_text(json.dumps(values)), "values": values})
    return out


def dependencies(root, items):
    """The tracked Python files under the checkout that the .py items reach: what each imports (`import x`, `from x import`)
    and what each names as a `.py` file in a whole string literal (a script read and exec'd, a chunk cut from a sibling),
    followed through the files found. Resolved among the files git tracks, in the naming file's own folder first, then by basename anywhere under the
    checkout. NOT every tracked Python file (the first record format 2 keyed on all 63 of them, 2026-10-29, so an edit to
    any drill's fixture generator staled every drill's record and cost a 20-minute refresh): the closure is what Blender
    can execute for this invocation, and nothing else can change its output."""
    import subprocess
    # not the hook's repository: git exports GIT_DIR (and sometimes GIT_WORK_TREE, GIT_INDEX_FILE) to a pre-push hook,
    # and `git -C <root>` would then list THAT repository's files for any root (the oracle tests' temp repositories
    # listed nothing under the hook, 2026-10-29); the root names the repository
    env = {k: v for k, v in os.environ.items() if k not in ("GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE")}
    listing = subprocess.run(["git", "-c", "safe.directory=" + root, "-C", root, "ls-files", "-z", "--", "*.py"],
                             capture_output=True, check=True, env=env).stdout
    tracked = {}
    for rel in listing.decode("utf-8", "surrogateescape").split("\0"):
        if rel: tracked[os.path.normcase(os.path.abspath(os.path.join(root, rel)))] = os.path.join(root, rel).replace("\\", "/")
    by_base = {}
    for absolute in tracked.values(): by_base.setdefault(os.path.basename(absolute).lower(), []).append(absolute)
    queue = [p for p in (as_path(i) for i in items) if p and p.lower().endswith(".py")]
    seen = set(); out = []
    while queue:
        current = queue.pop()
        identity = os.path.normcase(os.path.abspath(current))
        if identity in seen: continue
        seen.add(identity)
        with open(current, "r", encoding="utf-8", errors="surrogateescape") as source: text = source.read()
        names = set()
        for match in re.finditer(r"^\s*(?:import|from)\s+([\w.]+(?:\s*,\s*[\w.]+)*)", text, re.M):
            for name in match.group(1).split(","): names.add(name.strip().split(".")[0] + ".py")
        # a .py NAME counts only as a whole string literal (`"vehicle_rig.py"`, `'../shared/rows.py'`): prose in comments
        # and docstrings names scripts constantly ("rig_anim.py's own helper") and would pull half the tree in
        for match in re.finditer(r"""(["'])([\w./\\~-]+\.py)\1""", text): names.add(match.group(2).replace("\\", "/"))
        for name in names:
            candidates = [os.path.join(os.path.dirname(current), name)] + by_base.get(os.path.basename(name).lower(), [])
            for candidate in candidates:
                key = os.path.normcase(os.path.abspath(candidate))
                if key in tracked and key not in seen:
                    queue.append(tracked[key]); out.append(tracked[key]); break
    return sorted(set(out))


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
    if not argv or argv[0] not in ("store", "load", "key", "deps"):
        print(__doc__); sys.exit(2)
    op = argv[0]; opts = {"dirs": [], "args": []}; i = 1
    while i < len(argv):
        a = argv[i]
        if a == "--":
            opts["items"] = argv[i + 1:]; break
        if a == "--arg" and i + 1 < len(argv):
            opts["args"].append(argv[i + 1]); i += 2; continue
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


FORMAT = 3   # 3 (2026-10-29): scripts keyed with CRLF read as LF


def linked(path):
    return path.is_symlink() or (hasattr(path, "is_junction") and path.is_junction())


def relative_folder(value):
    if not isinstance(value, str) or not value or ntpath.splitdrive(value)[0] or value.startswith(("/", "\\")):
        raise ValueError("output folder must be relative: " + repr(value))
    value = value.replace("\\", "/").rstrip("/")
    if any(part in ("", ".", "..") or part.endswith((".", " ")) or ":" in part for part in value.split("/")):
        raise ValueError("unsafe output folder: " + repr(value))
    return value


def contained(root, relative):
    root = Path(root).resolve(); target = root.joinpath(*relative.split("/"))
    resolved = target.resolve()
    if resolved == root or root not in resolved.parents: raise ValueError("path escapes its root: " + relative)
    check = root
    for part in relative.split("/"):
        check = check / part
        if linked(check): raise ValueError("linked path is not an oracle artifact: " + str(check))
    return target


def entry_dir(opts):
    root = Path(opts["store"]).resolve()
    for field in ("drill", "name"):
        value = opts[field]
        if value in ("", ".", "..") or value.endswith((".", " ")) or any(char in value for char in ("/", "\\", ":")):
            raise ValueError("invalid --" + field)
    return contained(root, opts["drill"] + "/" + opts["name"])


def inventory(folder):
    """Relative names, directory presence, and every file's digest; refuse symlinks/junctions before reading."""
    result = {}
    def scan(path, prefix):
        if linked(path): raise ValueError("linked oracle artifact: " + str(path))
        if path.is_dir():
            result[prefix] = None
            for child in sorted(path.iterdir()): scan(child, prefix + "/" + child.name)
        elif path.is_file(): result[prefix] = digest_file(path)
        else: raise ValueError("missing oracle artifact: " + str(path))
    scan(folder / "raw.txt", "raw.txt"); scan(folder / "dirs", "dirs")
    return result


def remove_output(path):
    if path.is_dir(): shutil.rmtree(path)
    elif path.exists(): path.unlink()


def store(o, paths, described, key, target):
    rc = int(o.get("rc", "0"))
    if not 0 <= rc <= 255: raise ValueError("exit code must be between 0 and 255")
    if target.exists() and not target.is_dir(): raise ValueError("record entry is not a directory")
    raw = Path(o["raw"]).read_bytes()  # a failed refresh must leave the previous record intact
    present = {}
    for rel in o["dirs"]:
        src = contained(paths.wtmp, rel)
        present[rel] = src.is_dir()
        if src.is_dir():
            # Check descendants before copytree can follow a link outside the run.
            def check_tree(path):
                if linked(path): raise ValueError("linked output artifact: " + str(path))
                if path.is_dir():
                    for child in path.iterdir(): check_tree(child)
            check_tree(src)
    target.parent.mkdir(parents=True, exist_ok=True)
    staging = Path(tempfile.mkdtemp(prefix=target.name + ".pending-", dir=target.parent)); backup = None
    try:
        (staging / "dirs").mkdir()
        (staging / "raw.txt").write_bytes(paths.normalise_bytes(raw))
        for rel in o["dirs"]:
            if present[rel]: shutil.copytree(contained(paths.wtmp, rel), contained(staging / "dirs", rel))
        meta = {"format": FORMAT, "key": key, "rc": rc, "recorded": time.strftime("%Y-%m-%d %H:%M:%S"),
                "blender": o.get("blender", ""), "dirs": o["dirs"], "present": present, "items": described, "payload": inventory(staging)}
        meta["manifest"] = digest_text(json.dumps(meta, sort_keys=True))
        (staging / "meta.json").write_text(json.dumps(meta, indent=1, sort_keys=True), encoding="utf-8")
        if target.exists():
            backup = Path(tempfile.mkdtemp(prefix=target.name + ".previous-", dir=target.parent)); backup.rmdir()
            os.replace(target, backup)
        try: os.replace(staging, target)
        except BaseException:
            if backup is not None: os.replace(backup, target); backup = None
            raise
    finally:
        if backup is not None and backup.exists():
            if not target.exists(): os.replace(backup, target)
            elif not staging.exists(): shutil.rmtree(backup)  # the fully written generation was published
        if staging.exists(): shutil.rmtree(staging)
    try:
        with (Path(o["store"]) / ".gitignore").open("x", encoding="utf-8", newline="\n") as ignore:
            ignore.write("# machine-local Blender oracle, refreshed by tools/refresh_blender_oracle.sh\n*\n")
    except FileExistsError: pass
    print("RECORDED %s/%s key %s rc %d" % (o["drill"], o["name"], key, rc)); return 0


def load(o, paths, described, key, target):
    meta_path = target / "meta.json"
    if not meta_path.is_file():
        print("no record for %s/%s (never recorded under %s)" % (o["drill"], o["name"], o["store"])); return 99
    if linked(meta_path): raise ValueError("linked metadata")
    meta = json.loads(meta_path.read_text(encoding="utf-8"))
    if not isinstance(meta, dict): raise ValueError("invalid record manifest")
    if meta.get("format") != FORMAT:
        print("the record uses an older format; refresh the Blender oracle"); return 99
    manifest = {name: value for name, value in meta.items() if name != "manifest"}
    if meta.get("manifest") != digest_text(json.dumps(manifest, sort_keys=True)): raise ValueError("record manifest checksum differs")
    recorded_dirs = [relative_folder(rel) for rel in meta["dirs"]]
    if recorded_dirs != o["dirs"]:
        print("the record is STALE: requested output folders changed"); return 99
    # Validate every destination, including absent outputs, before writing anything.
    destinations = {rel: contained(paths.wtmp, rel) for rel in recorded_dirs}
    if meta.get("key") != key:
        diffs = differences(meta.get("items", []), described)
        print("the record for %s/%s (made %s) is STALE: %s" % (o["drill"], o["name"], meta.get("recorded", "?"), "; ".join(diffs[:6]) if diffs else "a key mismatch")); return 99
    if key_of(meta["items"]) != key: raise ValueError("input manifest checksum differs")
    rc = meta["rc"]
    if type(rc) is not int or not 0 <= rc <= 255: raise ValueError("invalid recorded exit code")
    if inventory(target) != meta["payload"]: raise ValueError("raw output or output-folder checksum differs")
    if set(meta["present"]) != set(recorded_dirs): raise ValueError("output presence manifest differs")
    for rel in recorded_dirs:
        present = meta["present"][rel]
        if type(present) is not bool: raise ValueError("invalid output presence")
        src = contained(target / "dirs", rel)
        if src.is_dir() != present: raise ValueError("missing or unexpected output folder: " + rel)
    raw = paths.restore_bytes((target / "raw.txt").read_bytes())
    for rel, dst in destinations.items():
        remove_output(dst)
        if meta["present"][rel]: shutil.copytree(contained(target / "dirs", rel), dst)
    Path(o["raw"]).write_bytes(raw)
    print("RC %d" % rc); return 0


def main(argv):
    op, o = parse(argv)
    try:
        if op == "deps":
            if not o.get("root"): print("FAIL - --root is required"); return 2
            print(json.dumps(dependencies(o["root"].replace("\\", "/"), o["items"]))); return 0
        paths = Paths(o.get("wtmp"), o.get("tmpd"), o.get("wroot"))
        described = describe_items(o["items"], paths, o["args"]); key = key_of(described)
        if op == "key": print(key); return 0
        for need in ("store", "drill", "name", "raw", "wtmp"):
            if not o.get(need): print("FAIL - --%s is required" % need); return 2
        o["dirs"] = [relative_folder(rel) for rel in o["dirs"]]
        for i, rel in enumerate(o["dirs"]):
            if any(rel == other or rel.startswith(other + "/") or other.startswith(rel + "/") for other in o["dirs"][:i]):
                raise ValueError("overlapping output folders")
        if op == "store" and o.get("expected-key") and o["expected-key"] != key:
            raise ValueError("inputs changed while Blender was running; no record published")
        target = entry_dir(o)
        return store(o, paths, described, key, target) if op == "store" else load(o, paths, described, key, target)
    except (OSError, ValueError, TypeError, KeyError) as error:
        print(("the record is CORRUPT or unusable: " if op == "load" else "FAIL - cannot record: ") + str(error))
        return 99 if op == "load" else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
