#!/usr/bin/env python
"""drill-merge2.py - the second-model merge, drilled through the REAL vehicle_rig.py in headless Blender.

Not in tools/check.sh (it launches Blender six times, ~1-2 min). Run by hand after touching the merge block:
    python tools/drill-merge2.py            (finds Blender under Program Files, or set HAF_BLENDER)

Every fixture is GENERATED here, so nothing binary is committed. Three placements, each a shipped-or-caught bug:
  1. SHAPE KEY   a glTF morph target must not snap the model back to its file position (PR #78 review, P2:
                 Mesh.transform() leaves shape keys alone unless told; a cube at x=27 landed at the origin).
  2. SHEAR       per-axis scale under a root rotated 30 deg inside the file (PR #78: a Blender object cannot hold
                 a shear, so the placement was only exact while the scale was uniform).
  3. MIRROR      a mirrored source root must come out with its faces pointing OUTWARD (signed volume > 0); the
                 flip_normals() line is what makes that true.
"""
import os, sys, glob, math, subprocess, tempfile, re

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RIG = os.path.join(ROOT, "editor", "Tools~", "vehicle_rig.py")

def blender():
    b = os.environ.get("HAF_BLENDER")
    if b and os.path.isfile(b): return b
    cands = sorted(glob.glob(r"C:\Program Files\Blender Foundation\Blender *\blender.exe"),
                   key=lambda p: [int(x) for x in re.findall(r"\d+", p.rsplit("Blender ", 1)[1].split("\\")[0])])
    if not cands: sys.exit("drill-merge2: no Blender found (set HAF_BLENDER)")
    return cands[-1]

B = blender(); TMP = tempfile.mkdtemp(prefix="haf_merge2_")
def run_py(code, *args):
    p = os.path.join(TMP, "s%d.py" % len(os.listdir(TMP))); open(p, "w", encoding="utf-8").write(code)
    r = subprocess.run([B, "-b", "--factory-startup", "--python", p, "--", *args], capture_output=True, text=True, encoding="utf-8", errors="replace")
    return r.stdout + r.stderr

FIRST = os.path.join(TMP, "first.glb")
run_py("import bpy,sys\nbpy.ops.wm.read_factory_settings(use_empty=True)\nbpy.ops.mesh.primitive_cube_add(size=2)\n"
       "bpy.ops.export_scene.gltf(filepath=sys.argv[-1], export_format='GLB')", FIRST)

def run_file(path, *args):
    r = subprocess.run([B, "-b", "--factory-startup", "--python", path, "--", *args], capture_output=True, text=True, encoding="utf-8", errors="replace")
    return r.stdout + r.stderr

def probe(second, m2):
    out = run_file(RIG, "probe", FIRST, os.path.join(TMP, "prev.fbx"), "merge2=%s|%s" % (second, m2))
    m = re.search(r"B bbox min \(([-\d., ]+)\) max \(([-\d., ]+)\)", out)
    if not m: sys.exit("drill-merge2: no B bbox line\n" + out[-2000:])
    lo = [float(x) for x in m.group(1).split(",")]; hi = [float(x) for x in m.group(2).split(",")]
    return lo, hi, out

fails = []
def check(name, cond, detail):
    print(("ok   " if cond else "FAIL ") + name + "  " + detail)
    if not cond: fails.append(name)

# 1) SHAPE KEY
SK = os.path.join(TMP, "shapekey.glb")
run_py("import bpy,sys\nbpy.ops.wm.read_factory_settings(use_empty=True)\n"
       "bpy.ops.mesh.primitive_cube_add(size=4, location=(27,0,0)); c=bpy.context.active_object\n"
       "c.shape_key_add(name='Basis'); k=c.shape_key_add(name='Bulge')\nfor v in k.data: v.co.x += 0.5\n"
       "bpy.ops.export_scene.gltf(filepath=sys.argv[-1], export_format='GLB', export_morph=True)", SK)
lo, hi, _ = probe(SK, "0,0,0|0,0,0|1")
check("shape-key model keeps its file position", abs(lo[0] - 25.5) < 0.01 and abs(hi[0] - 29.5) < 0.01,
      "x %.2f..%.2f (want 25.50..29.50)" % (lo[0], hi[0]))

# 2) SHEAR: 4 x 1 x 0.5 box under a root rotated 30 deg about Z, scale (2,1,1) -> 7.928 x 2.866 x 0.5
SH = os.path.join(TMP, "shear.glb")
run_py("import bpy,sys,math\nbpy.ops.wm.read_factory_settings(use_empty=True)\n"
       "r=bpy.data.objects.new('Root',None); bpy.context.scene.collection.objects.link(r); r.rotation_euler=(0,0,math.radians(30))\n"
       "bpy.ops.mesh.primitive_cube_add(size=1); c=bpy.context.active_object; c.scale=(4,1,0.5); c.location=(1,0,0); c.parent=r\n"
       "bpy.ops.export_scene.gltf(filepath=sys.argv[-1], export_format='GLB')", SH)
lo, hi, _ = probe(SH, "0,0,0|0,0,0|2,1,1")
size = [hi[i] - lo[i] for i in range(3)]
check("per-axis scale under a 30-deg root is exact (no shear lost)", abs(size[0] - 7.928) < 0.02 and abs(size[1] - 2.866) < 0.02,
      "size %.2f x %.2f x %.2f (want 7.93 x 2.87 x 0.50)" % tuple(size))

# 3) MIRROR: faces must point outward after the bake
MI = os.path.join(TMP, "mirror.glb")
run_py("import bpy,sys,math\nbpy.ops.wm.read_factory_settings(use_empty=True)\n"
       "r=bpy.data.objects.new('MirrorRoot',None); bpy.context.scene.collection.objects.link(r); r.scale=(-1,1,1); r.rotation_euler=(0,0,math.radians(90))\n"
       "bpy.ops.mesh.primitive_cube_add(size=2); c=bpy.context.active_object; c.location=(3,1,0.5); c.parent=r\n"
       "for p in c.data.polygons: p.use_smooth=True\n"
       "bpy.ops.export_scene.gltf(filepath=sys.argv[-1], export_format='GLB')", MI)
_, _, _ = probe(MI, "0,0,0|0,0,0|1")
vol = run_py("import bpy,sys\nbpy.ops.wm.read_factory_settings(use_empty=True)\nbpy.ops.import_scene.fbx(filepath=sys.argv[-1])\n"
             "bpy.context.view_layer.update()\n"
             "for o in bpy.data.objects:\n"
             "    if o.type!='MESH' or not o.name.startswith('B_'): continue\n"
             "    mw=o.matrix_world; v=0.0\n"
             "    for p in o.data.polygons:\n"
             "        vs=[mw @ o.data.vertices[i].co for i in p.vertices]\n"
             "        for k in range(1,len(vs)-1): v+=vs[0].dot(vs[k].cross(vs[k+1]))/6.0\n"
             "    print('VOLUME %.4f' % v)", os.path.join(TMP, "prev.fbx"))
m = re.search(r"VOLUME (-?[\d.]+)", vol); v = float(m.group(1)) if m else float("nan")
check("mirrored source comes out facing outward", v > 7.9, "signed volume %+.2f (want +8)" % v)

print("\ndrill-merge2: " + ("PASS" if not fails else "FAIL - " + ", ".join(fails)))
sys.exit(1 if fails else 0)
