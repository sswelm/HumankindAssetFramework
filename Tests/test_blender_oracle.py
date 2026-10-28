"""The recorded Blender oracle (tools/blender-oracle/oracle.py) and its shell wrapper (tools/blender_oracle.sh):
a record keyed by its inputs' bytes replays in another temp folder, a changed input or script names itself and refuses,
a record never made refuses, the raw output and the output folders come back with the run's own paths, and the wrapper
turns a refusal into the drill's SKIP (exit 3) while live mode runs the given executable untouched."""
import json, os, pathlib, shutil, subprocess, sys, tempfile, unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]
ORACLE = ROOT / "tools" / "blender-oracle" / "oracle.py"
BASH = shutil.which("bash") or r"C:\Program Files\Git\bin\bash.exe"


def run(*args):
    return subprocess.run([sys.executable, str(ORACLE)] + [str(a) for a in args], capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=60)


def write(path, text):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8", newline="\n")


class Run:
    """A drill run's temp folder with a model, a jobs list naming it, and a raw Blender output mentioning the folder."""
    def __init__(self, base, name, model_bytes=b"GLB\x00model", job_extra=""):
        self.wtmp = (base / name).as_posix()
        self.dir = base / name
        self.model = self.dir / "fixtures" / "m.glb"
        self.model.parent.mkdir(parents=True)
        self.model.write_bytes(model_bytes)
        self.jobs = self.dir / "jobs.txt"
        write(self.jobs, "JOB\tkey\t%s\t%s\n" % (self.model.as_posix(), job_extra))
        self.raw = self.dir / "raw.txt"
        write(self.raw, "FILE\t%s/out/0/x.glb\nDONE\n" % self.wtmp)
        (self.dir / "out" / "0").mkdir(parents=True)
        (self.dir / "out" / "0" / "x.glb").write_bytes(b"written by blender")

    def items(self):
        return [str(ROOT / "tools" / "blender-oracle" / "oracle.py"), str(self.jobs), self.wtmp + "/out/0"]


class OracleStoreTests(unittest.TestCase):
    def setUp(self):
        self.tmp = pathlib.Path(tempfile.mkdtemp(prefix="haf-oracle-"))
        self.store = self.tmp / "store"

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def store_args(self, r, name="posed_0"):
        return ["--store", self.store, "--drill", "t", "--name", name, "--wtmp", r.wtmp, "--raw", r.raw]

    def test_a_record_replays_in_another_temp_folder_with_its_paths_and_folders_put_back(self):
        a = Run(self.tmp, "runA")
        s = run("store", *self.store_args(a), "--rc", "0", "--dirs", "out/0", "--", *a.items())
        self.assertEqual(s.returncode, 0, s.stdout + s.stderr)
        self.assertTrue((self.store / ".gitignore").read_text().strip().endswith("*"))
        b = Run(self.tmp, "runB")
        shutil.rmtree(b.dir / "out"); b.raw.unlink()
        l = run("load", *self.store_args(b), "--dirs", "out/0", "--", *b.items())
        self.assertEqual(l.returncode, 0, l.stdout + l.stderr)
        self.assertIn("RC 0", l.stdout)
        self.assertEqual(b.raw.read_text(), "FILE\t%s/out/0/x.glb\nDONE\n" % b.wtmp)   # runB's folder, not runA's
        self.assertEqual((b.dir / "out" / "0" / "x.glb").read_bytes(), b"written by blender")

    def test_paths_blender_spelled_in_lower_case_or_with_backslashes_are_put_back_too(self):
        # the dumps print what Blender gives them: `c:/users/sebast~1/...`; the gate's C# rows carry the drill's own spelling
        a = Run(self.tmp, "runA")
        root = ROOT.as_posix()
        write(a.raw, "FILE\t%s/fixtures/m.glb\tok\nFILE\t%s\\fixtures\\m.glb\tok\nROOT\t%s/tools/x.py\nPOSIX\t%s/x\n" % (a.wtmp.lower(), a.wtmp, root.lower(), a.wtmp))
        s = run("store", *self.store_args(a), "--wroot", root, "--tmpd", a.wtmp, "--rc", "0", "--", *a.items())
        self.assertEqual(s.returncode, 0, s.stdout + s.stderr)
        stored = (self.store / "t" / "posed_0" / "raw.txt").read_text()
        # a lower-case spelling keeps a lower-case tag; the drill's own spelling the plain tag; TMPD equal to WTMP is WTMP
        self.assertEqual(stored, "FILE\t@wtmp@/fixtures/m.glb\tok\nFILE\t@WTMP@\\fixtures\\m.glb\tok\nROOT\t@root@/tools/x.py\nPOSIX\t@WTMP@/x\n")
        b = Run(self.tmp, "runB"); b.raw.unlink()
        l = run("load", *self.store_args(b), "--wroot", root, "--tmpd", b.wtmp, "--", *b.items())
        self.assertEqual(l.returncode, 0, l.stdout + l.stderr)
        self.assertEqual(b.raw.read_text(), "FILE\t%s/fixtures/m.glb\tok\nFILE\t%s\\fixtures\\m.glb\tok\nROOT\t%s/tools/x.py\nPOSIX\t%s/x\n" % (b.wtmp.lower(), b.wtmp, root.lower(), b.wtmp))

    def test_the_recorded_exit_code_comes_back(self):
        a = Run(self.tmp, "runA")
        run("store", *self.store_args(a), "--rc", "7", "--", *a.items())
        l = run("load", *self.store_args(a), "--", *a.items())
        self.assertEqual(l.returncode, 0); self.assertIn("RC 7", l.stdout)

    def test_a_changed_model_named_in_the_jobs_list_refuses_and_names_the_file(self):
        a = Run(self.tmp, "runA")
        run("store", *self.store_args(a), "--rc", "0", "--", *a.items())
        b = Run(self.tmp, "runB", model_bytes=b"GLB\x00model v2")
        l = run("load", *self.store_args(b), "--", *b.items())
        self.assertEqual(l.returncode, 99, l.stdout)
        self.assertIn("STALE", l.stdout); self.assertIn("m.glb", l.stdout)

    def test_a_changed_job_argument_refuses(self):
        a = Run(self.tmp, "runA", job_extra="ratio=0.5")
        run("store", *self.store_args(a), "--rc", "0", "--", *a.items())
        b = Run(self.tmp, "runB", job_extra="ratio=0.25")
        l = run("load", *self.store_args(b), "--", *b.items())
        self.assertEqual(l.returncode, 99, l.stdout); self.assertIn("jobs.txt changed", l.stdout)

    def test_a_changed_script_refuses_and_a_record_never_made_refuses(self):
        a = Run(self.tmp, "runA")
        script = self.tmp / "dump.py"; write(script, "print(1)\n")
        run("store", *self.store_args(a), "--rc", "0", "--", str(script), *a.items())
        write(script, "print(2)\n")
        l = run("load", *self.store_args(a), "--", str(script), *a.items())
        self.assertEqual(l.returncode, 99); self.assertIn("dump.py changed", l.stdout)
        m = run("load", *self.store_args(a, name="never"), "--", *a.items())
        self.assertEqual(m.returncode, 99); self.assertIn("never recorded", m.stdout)

    def test_the_key_ignores_the_temp_folder_and_path_slashes(self):
        a = Run(self.tmp, "runA"); b = Run(self.tmp, "runB")
        ka = run("key", "--wtmp", a.wtmp, "--", *a.items()).stdout.strip()
        kb = run("key", "--wtmp", b.wtmp, "--", *b.items()).stdout.strip()
        kc = run("key", "--wtmp", a.wtmp.replace("/", "\\"), "--", *[i.replace("/", "\\") for i in a.items()]).stdout.strip()
        self.assertEqual(ka, kb); self.assertEqual(ka, kc); self.assertEqual(len(ka), 32)

    def test_a_record_is_replaced_whole_on_a_new_store(self):
        a = Run(self.tmp, "runA")
        run("store", *self.store_args(a), "--rc", "0", "--dirs", "out/0", "--", *a.items())
        (a.dir / "out" / "0" / "x.glb").write_bytes(b"second")
        write(a.raw, "DONE second\n")
        run("store", *self.store_args(a), "--rc", "0", "--dirs", "out/0", "--", *a.items())
        meta = json.loads((self.store / "t" / "posed_0" / "meta.json").read_text())
        self.assertEqual(meta["dirs"], ["out/0"])
        self.assertEqual((self.store / "t" / "posed_0" / "dirs" / "out" / "0" / "x.glb").read_bytes(), b"second")
        self.assertEqual((self.store / "t" / "posed_0" / "raw.txt").read_text(), "DONE second\n")


@unittest.skipUnless(os.path.exists(BASH), "bash is needed for the wrapper tests")
class OracleShellTests(unittest.TestCase):
    def setUp(self):
        self.tmp = pathlib.Path(tempfile.mkdtemp(prefix="haf-oracle-sh-"))
        self.store = self.tmp / "store"
        self.fake = self.tmp / "blender.sh"
        write(self.fake, '#!/usr/bin/env bash\necho "BLENDER $*"\necho "OUT $PWD"\nexit ${FAKE_RC:-0}\n')

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def shell(self, mode, script, rc_env="0"):
        env = dict(os.environ, HAF_ORACLE=mode, HAF_BLENDER_ORACLE=self.store.as_posix(), BLENDER=self.fake.as_posix(), FAKE_RC=rc_env)
        body = ('set -u\nROOT="%s"\nTMPD="%s"\nWTMP="$TMPD"\n. "$ROOT/tools/blender_oracle.sh"\n' % (ROOT.as_posix(), self.tmp.as_posix())) + script
        return subprocess.run([BASH, "-c", body], env=env, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=120)

    def test_live_runs_the_executable_and_returns_its_code(self):
        r = self.shell("live", 'oracle_init t "the t drill"\noracle_blender a "$TMPD/raw.txt" --key x -- --background one; echo "rc $?"\ncat "$TMPD/raw.txt"\n', rc_env="5")
        self.assertIn("rc 5", r.stdout); self.assertIn("BLENDER --background one", r.stdout)
        self.assertFalse(self.store.exists())

    def test_record_then_replay_without_blender_and_a_stale_replay_skips(self):
        model = self.tmp / "m.glb"; model.write_bytes(b"v1")
        rec = self.shell("record", 'oracle_init t\noracle_blender a "$TMPD/raw.txt" --key "%s" -- --background one; echo "rc $?"\noracle_check; echo checked\n' % model.as_posix(), rc_env="4")
        self.assertIn("rc 4", rec.stdout); self.assertIn("checked", rec.stdout)
        self.assertTrue((self.store / "t" / "a" / "meta.json").exists())
        rep = subprocess.run([BASH, "-c", 'set -u\nROOT="%s"\nTMPD="%s"\nWTMP="$TMPD"\n. "$ROOT/tools/blender_oracle.sh"\noracle_init t\n'
                              'oracle_blender a "$TMPD/raw2.txt" --key "%s" -- --background one; echo "rc $?"\noracle_check; cat "$TMPD/raw2.txt"\n'
                              % (ROOT.as_posix(), self.tmp.as_posix(), model.as_posix())],
                             env=dict(os.environ, HAF_ORACLE="replay", HAF_BLENDER_ORACLE=self.store.as_posix(), BLENDER="/nonexistent/blender"),
                             capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=120)
        self.assertEqual(rep.returncode, 0, rep.stdout + rep.stderr)
        self.assertIn("rc 4", rep.stdout); self.assertIn("BLENDER --background one", rep.stdout)
        model.write_bytes(b"v2")
        stale = self.shell("replay", 'oracle_init t "the t drill"\noracle_blender a "$TMPD/raw3.txt" --key "%s" -- --background one; echo "rc $?"\noracle_check; echo not-reached\n' % model.as_posix())
        self.assertEqual(stale.returncode, 3, stale.stdout + stale.stderr)
        self.assertIn("rc 99", stale.stdout); self.assertIn("SKIP — the t drill", stale.stdout); self.assertIn("STALE", stale.stdout)
        self.assertNotIn("not-reached", stale.stdout)

    def test_record_and_live_fail_loud_without_blender(self):
        for mode in ("record", "live"):
            env = dict(os.environ, HAF_ORACLE=mode, HAF_BLENDER_ORACLE=self.store.as_posix(), BLENDER="/nonexistent/blender.exe")
            r = subprocess.run([BASH, "-c", 'ROOT="%s"; TMPD="%s"; WTMP="$TMPD"; . "$ROOT/tools/blender_oracle.sh"; oracle_init t "the t drill"; echo not-reached' % (ROOT.as_posix(), self.tmp.as_posix())],
                               env=env, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=60)
            self.assertEqual(r.returncode, 2, mode); self.assertIn("FAIL — the t drill", r.stdout); self.assertNotIn("not-reached", r.stdout)

    def test_a_bad_mode_is_refused(self):
        r = self.shell("sometimes", 'echo not-reached\n')
        self.assertEqual(r.returncode, 2); self.assertIn("HAF_ORACLE must be", r.stdout)


if __name__ == "__main__":
    unittest.main()
