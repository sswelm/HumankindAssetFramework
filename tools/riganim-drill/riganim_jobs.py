"""The rig_anim.py jobs of the registry's animated entries (tools/riganim_drill.sh), each the argument line the Factory
hands Blender for that entry (UniversalBaker: the slim step, RigAnimViaBlender), so the drill runs the script on exactly
what a bake runs it on.

usage: riganim_jobs.py <pack.json> <project root> [--max-mb N] [--all]

One TAB-separated line per animated entry (a clip name may hold "|": the soldier's "Soldier_reference_skeleton|Idle1"):

    JOB <resourceName> <input> <argv[2]> <argv[3]> ... <argv[14]>     the script's arguments after the input and output
    LEFT <resourceName> <reason>                                      an entry the drill does not run: its source is an
                                                                      .fbx (Blender's, until the native FBX reader), its
                                                                      source or deploy conversion is missing, or it is
                                                                      over the size limit (--all takes every size)

The argument fields, as the Factory formats them: argv[2] the reduce target (0 = off), [3] the bone prefixes, [4] the
primary clip, [5] the albedo ("1": the dump writes it to <work>/<key>_albedo.png; the Factory's 'Keep texture' blanks it,
the drill never does), [6] keep the material slots (materialMode other than Single), [7] the rotation as "x,y,z" with
"0.###" formatting, [8] convertRig, [9] the state roles "move=..;after=..;attack=..;combat=..;premove=..;idle=..;
idlealt=..;idlealt2=.." (only when state-driven; the move clip is required), [10] auto-ground, [11] the socket bones (as
typed, untrimmed), [12] keepTranslations, [13] the static parts, [14] localNodeAnim.
A deploy-converted entry runs on its deploy_converted.glb under Assets/FactorySource/<name>/ (the Factory's input).
"""
import json, os, sys
from decimal import Decimal, ROUND_HALF_UP


def fmt3(v):
    """.NET's ToString("0.###", InvariantCulture): up to three decimals, no trailing zeros, half away from zero."""
    d = Decimal(repr(float(v))).quantize(Decimal("0.001"), rounding=ROUND_HALF_UP)
    s = format(d, "f")
    if "." in s:
        s = s.rstrip("0").rstrip(".")
    if s in ("-0", ""):
        s = "0"
    return s


ROLES = (("move", "animClipMove"), ("after", "animClipAfter"), ("attack", "animClipAttack"), ("combat", "animClipCombat"),
         ("premove", "animClipPreMove"), ("idle", "animClipIdle"), ("idlealt", "animClipIdleAlt"), ("idlealt2", "animClipIdleAlt2"))


def jobs(pack, project, max_mb=40.0, everything=False):
    d = json.load(open(pack, encoding="utf-8-sig"))
    out = []
    for m in d.get("models", []):
        if not m.get("animated"):
            continue
        key = m.get("resourceName", "")
        if not key:
            continue
        src = (m.get("modelFile") or "").replace("\\", "/")
        if m.get("deployConvert"):
            src = os.path.join(project, "Assets", "FactorySource", key, "deploy_converted.glb").replace("\\", "/")
            if not os.path.isfile(src):
                out.append("LEFT	%s	the deploy conversion it bakes from is not on disk (%s)" % (key, src)); continue
        if not src:
            out.append("LEFT	%s	no model file" % key); continue
        if not os.path.isfile(src):
            out.append("LEFT	%s	the source is not on disk (%s)" % (key, src)); continue
        ext = os.path.splitext(src)[1].lower()
        if ext not in (".glb", ".gltf"):
            out.append("LEFT	%s	the source is %s - Blender's by name until the native FBX reader (%s)" % (key, ext, src)); continue
        if not everything and os.path.getsize(src) > max_mb * 1e6:
            out.append("LEFT	%s	over %g MB (%.1f MB; FULL=1 takes it)" % (key, max_mb, os.path.getsize(src) / 1e6)); continue
        state = bool(m.get("animStateDriven"))
        roles = ""
        if state:
            move = (m.get("animClipMove") or "").strip()
            if not move:
                out.append("LEFT	%s	state-driven without a movement clip: the Factory fails before Blender" % key); continue
            parts = []
            for role, field in ROLES:
                v = (m.get(field) or "").strip()
                if role == "move" or v:
                    parts.append("%s=%s" % (role, v))
            roles = ";".join(parts)
        rot = m.get("rotation") or {}
        rot_s = "%s,%s,%s" % (fmt3(rot.get("x", 0.0)), fmt3(rot.get("y", 0.0)), fmt3(rot.get("z", 0.0)))
        target = m.get("targetTris") or 0
        fields = [key, src, str(target if target > 0 else 0), m.get("animateBones") or "", m.get("animClip") or "", "1",
                  "1" if m.get("materialMode", 0) != 1 else "0", rot_s, "1" if m.get("convertRig") else "0", roles,
                  "1" if m.get("autoGroundWheels") else "0", m.get("socketBones") or "", "1" if m.get("keepTranslations") else "0",
                  m.get("staticParts") or "", "1" if m.get("localNodeAnim") else "0"]
        for f in fields:
            if "\t" in f or "\n" in f:
                out.append("LEFT\t%s\tan argument holds a tab or a line break" % key); break
        else:
            out.append("JOB\t" + "\t".join(fields))
    return out


if __name__ == "__main__":
    args = sys.argv[1:]
    everything = "--all" in args
    max_mb = 40.0
    if "--max-mb" in args:
        max_mb = float(args[args.index("--max-mb") + 1])
    pos = [a for i, a in enumerate(args) if not a.startswith("--") and (i == 0 or args[i - 1] != "--max-mb")]
    for line in jobs(pos[0], pos[1], max_mb, everything):
        print(line)
