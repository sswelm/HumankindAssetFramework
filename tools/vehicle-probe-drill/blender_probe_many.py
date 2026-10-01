"""Blender's own probe (editor/Tools~/vehicle_rig.py, mode `probe`, posestart=1) on MANY files in ONE Blender process:
the script is a program that reads sys.argv and exits, so each file runs it afresh in its own namespace with argv
set for it and the SystemExit caught. The 1.6 s boot is paid once. Every line the probe prints is echoed behind the
file's key, as the C# drill prints its rows:

    ROW <TAB> <file key> <TAB> PART|...       (and RIGBONE|..., VEHICLE ...)

usage: blender --background --python blender_probe_many.py -- <vehicle_rig.py> <file>...
"""
import builtins, sys, time

args = sys.argv[sys.argv.index("--") + 1:]
script, files = args[0], args[1:]
source = compile(open(script, encoding="utf-8").read(), script, "exec")
sys.stdout.reconfigure(encoding="utf-8")
real_print = builtins.print
failures = 0
for path in files:
    key = path.replace("\\", "/").lower()

    def keyed_print(*a, **k):
        text = " ".join(str(x) for x in a)
        for line in text.split("\n"):
            if line.startswith(("PART|", "RIGBONE|", "VEHICLE ")):
                real_print("ROW\t%s\t%s" % (key, line), flush=True)

    t0 = time.time()
    sys.argv = ["blender", "--", "probe", path, "", "posestart=1"]
    builtins.print = keyed_print
    code = 0
    try:
        exec(source, {"__name__": "__main__", "__file__": script})
    except SystemExit as e:
        code = e.code or 0
    except Exception as e:   # a crash in the probe is a FAIL of that file, not of the batch
        builtins.print = real_print
        real_print("ROW\t%s\tVEHICLE ERROR: %s: %s" % (key, type(e).__name__, e), flush=True)
        code = 1
    builtins.print = real_print
    if code != 0:
        failures += 1
    real_print("FILE\t%s\texit=%s\tseconds=%.2f" % (key, code, time.time() - t0), flush=True)
sys.exit(1 if failures else 0)
