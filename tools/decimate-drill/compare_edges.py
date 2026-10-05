"""The C# mesh layout and BMesh links beside Blender's, per file and object (tools/decimate-drill): PASS/FAIL per file,
exit 1 on any FAIL.

    python compare_edges.py <csharp.txt> <blender.txt>

Both hold MESH rows (key, object, verts, edges, faces, sha1 of the edge pairs, first edges), BM rows (key, object, verts,
edges, loops, faces, sha1 of the link lists) and OP rows (key, object, step, what, verts, edges, faces, sha1). Per object by
NAME: a MESH row's counts and edge-pair hash must be equal (the whole edge list, in order, each edge as Blender stores
it; a mismatch prints the first eight edges of both sides); a BM row must be equal whole (every link list, in order); every
OP step must exist on both sides and be equal whole (the same operation, the same counts, the same lists after it).
"""
import sys
sys.stdout.reconfigure(encoding="utf-8")


def load(path):
    files = {}
    for line in open(path, encoding="utf-8", errors="replace"):
        t = line.rstrip("\r\n").split("\t")
        if len(t) >= 8 and t[0] == "MESH":
            files.setdefault(t[1], {}).setdefault(t[2], {})["MESH"] = t[3:8]
        elif len(t) >= 8 and t[0] == "BM":
            files.setdefault(t[1], {}).setdefault(t[2], {})["BM"] = t[3:8]
        elif len(t) >= 9 and t[0] == "OP":
            files.setdefault(t[1], {}).setdefault(t[2], {}).setdefault("OP", {})[t[3]] = t[4:9]
        elif len(t) >= 3 and t[0] == "FILE":
            files.setdefault(t[1], {})
    return files


def main():
    cs, bl = load(sys.argv[1]), load(sys.argv[2])
    fails = 0; compared = 0; objects = 0; edges = 0; links = 0; ops = 0
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
            objects += 1
            bo, co = b[name], c[name]
            if "MESH" in bo and "MESH" in co:
                edges += int(bo["MESH"][1])
                bv, be, bf, bh, bfirst = bo["MESH"]; cv, ce, cf, ch, cfirst = co["MESH"]
                if (bv, be, bf) != (cv, ce, cf):
                    problems.append(f"{name}: verts/edges/faces C# {cv}/{ce}/{cf} vs Blender {bv}/{be}/{bf}")
                elif bh != ch:
                    problems.append(f"{name}: the edge list differs (same counts); first edges C# [{cfirst}] vs Blender [{bfirst}]")
            elif ("MESH" in bo) != ("MESH" in co):
                problems.append(f"{name}: a MESH row on one side only")
            if "BM" in bo and "BM" in co:
                links += 1
                if bo["BM"] != co["BM"]:
                    problems.append(f"{name}: the BMesh links differ: C# verts/edges/loops/faces {'/'.join(co['BM'][:4])} hash {co['BM'][4][:12]} vs Blender {'/'.join(bo['BM'][:4])} hash {bo['BM'][4][:12]}")
            elif ("BM" in bo) != ("BM" in co):
                problems.append(f"{name}: a BM row on one side only")
            bops, cops = bo.get("OP", {}), co.get("OP", {})
            if set(bops) != set(cops):
                problems.append(f"{name}: OP steps differ: Blender {len(bops)}, C# {len(cops)}")
            for step in sorted(bops, key=int):
                if step not in cops:
                    continue
                ops += 1
                if bops[step] != cops[step]:
                    problems.append(f"{name}: OP step {step} differs: C# [{' '.join(cops[step][:4])} {cops[step][4][:12]}] vs Blender [{' '.join(bops[step][:4])} {bops[step][4][:12]}]")
                    break
        if problems:
            fails += 1; print(f"FAIL {short}: " + "; ".join(problems[:4]))
        else:
            print(f"PASS {short}: {len(b)} object(s), every edge list and link list equal")
    print(f"COMPARED {compared} FAILED {fails} OBJECTS {objects} EDGES {edges} LINKS {links} OPS {ops}")
    return 1 if fails else 0


if __name__ == "__main__":
    sys.exit(main())
