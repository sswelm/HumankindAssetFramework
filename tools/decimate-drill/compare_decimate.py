"""The C# Decimate collapse beside Blender's, per file, object and ratio (tools/decimate-drill): PASS/FAIL per file, exit 1
on any FAIL.

    python compare_decimate.py <csharp.txt> <blender.txt>

Both hold DEC rows (key, object, ratio tag, ratio bits, then either SKIP + why, or verts, faces, sha1 positions, sha1 faces,
sha1 UVs, sha1 custom normals, sha1 face material+sharp, sha1 vertex groups, colour layers, first vertices). Per object and
ratio: every field must be equal; a mismatch names the first field that differs.
"""
import sys
sys.stdout.reconfigure(encoding="utf-8")

FIELDS = ["ratio bits", "verts", "faces", "positions", "face list", "UVs", "custom normals", "face material/sharp", "vertex groups", "colour layers", "first vertices"]


def load(path):
    files = {}
    for line in open(path, encoding="utf-8", errors="replace"):
        t = line.rstrip("\r\n").split("\t")
        if len(t) >= 7 and t[0] == "DEC":
            files.setdefault(t[1], {})[(t[2], t[3])] = t[4:]
        elif len(t) >= 3 and t[0] == "FILE":
            files.setdefault(t[1], {})
    return files


def main():
    cs, bl = load(sys.argv[1]), load(sys.argv[2])
    fails = 0; compared = 0; rows = 0; skipped = 0; collapsed_faces = 0; notes = []
    for key in sorted(bl):
        short = "/".join(key.split("/")[-2:])
        if key not in cs:
            print(f"FAIL {short}: Blender read it, the C# side has no rows"); fails += 1; continue
        compared += 1
        b, c = bl[key], cs[key]
        problems = []
        only_b = [k for k in b if k not in c]; only_c = [k for k in c if k not in b]
        if only_b or only_c:
            problems.append(f"rows differ: only Blender {only_b[:3]}, only C# {only_c[:3]}")
        for k in b:
            if k not in c:
                continue
            rows += 1
            bb, cc = b[k], c[k]
            if bb[1] == "SKIP" or cc[1] == "SKIP":
                if bb[1] == "SKIP" and cc[1] == "SKIP" and bb[2] == cc[2]:
                    skipped += 1; continue
                if cc[1] == "SKIP" and bb[1] != "SKIP":
                    # the port declined this mesh by design (BlenderReduce.FallbackReason): the reduce keeps Blender for it
                    skipped += 1; notes.append(f"{short} {k[0]} {k[1]}: kept for Blender ({cc[2]})"); continue
                problems.append(f"{k[0]} {k[1]}: skipped on one side only (Blender {bb[1:3]}, C# {cc[1:3]})"); continue
            collapsed_faces += int(bb[2])
            if bb != cc:
                for i, name in enumerate(FIELDS):
                    if i < len(bb) and i < len(cc) and bb[i] != cc[i]:
                        problems.append(f"{k[0]} {k[1]}: {name} differ (C# {cc[i][:24]} vs Blender {bb[i][:24]}; verts/faces C# {cc[1]}/{cc[2]} vs {bb[1]}/{bb[2]})"); break
                else:
                    problems.append(f"{k[0]} {k[1]}: rows differ in length")
        if problems:
            fails += 1; print(f"FAIL {short}: " + "; ".join(problems[:4]))
        else:
            print(f"PASS {short}: {len(b)} collapse(s) equal")
    for n in notes[:20]:
        print("NOTE " + n)
    if len(notes) > 20:
        print("NOTE ... and %d more kept for Blender" % (len(notes) - 20))
    print(f"COMPARED {compared} FAILED {fails} ROWS {rows} SKIPPED {skipped} FACES {collapsed_faces}")
    return 1 if fails else 0


if __name__ == "__main__":
    sys.exit(main())
