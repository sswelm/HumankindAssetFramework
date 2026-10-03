"""The C# mesh layout beside Blender's, per file and object (tools/decimate-drill): PASS/FAIL per file, exit 1 on any FAIL.

    python compare_edges.py <csharp.txt> <blender.txt>

Both hold MESH rows (key, object, verts, edges, faces, sha1 of the edge pairs, first edges). Per object by NAME: the vertex,
edge and face counts must be equal and the edge-pair hash must be equal - the whole edge list, in order, each edge as
Blender stores it. A mismatch prints the first eight edges of both sides.
"""
import sys
sys.stdout.reconfigure(encoding="utf-8")


def load(path):
    files = {}
    for line in open(path, encoding="utf-8", errors="replace"):
        t = line.rstrip("\r\n").split("\t")
        if len(t) >= 8 and t[0] == "MESH":
            files.setdefault(t[1], {})[t[2]] = t[3:8]
        elif len(t) >= 3 and t[0] == "FILE":
            files.setdefault(t[1], {})
    return files


def main():
    cs, bl = load(sys.argv[1]), load(sys.argv[2])
    fails = 0; compared = 0; objects = 0; edges = 0
    for key in sorted(bl):
        short = "/".join(key.split("/")[-2:])
        if key not in cs:
            print(f"FAIL {short}: Blender read it, the C# side has no rows"); fails += 1; continue
        compared += 1
        b, c = bl[key], cs[key]
        problems = []
        only_b = [n for n in b if n not in c]; only_c = [n for n in c if n not in b]
        if only_b or only_c:
            problems.append(f"objects differ: only Blender {only_b[:4]}, only C# {only_c[:4]}")
        for name in b:
            if name not in c:
                continue
            objects += 1; edges += int(b[name][1])
            bv, be, bf, bh, bfirst = b[name]; cv, ce, cf, ch, cfirst = c[name]
            if (bv, be, bf) != (cv, ce, cf):
                problems.append(f"{name}: verts/edges/faces C# {cv}/{ce}/{cf} vs Blender {bv}/{be}/{bf}")
            elif bh != ch:
                problems.append(f"{name}: the edge list differs (same counts); first edges C# [{cfirst}] vs Blender [{bfirst}]")
        if problems:
            fails += 1; print(f"FAIL {short}: " + "; ".join(problems[:4]))
        else:
            print(f"PASS {short}: {len(b)} object(s), every edge list equal")
    print(f"COMPARED {compared} FAILED {fails} OBJECTS {objects} EDGES {edges}")
    return 1 if fails else 0


if __name__ == "__main__":
    sys.exit(main())
