"""The recorded Blender oracle (tools/blender-oracle/oracle.py) and its shell wrapper (tools/blender_oracle.sh):
a record keyed by its inputs' bytes replays in another temp folder, a changed input or script names itself and refuses,
a record never made refuses, the raw output and the output folders come back with the run's own paths, and the wrapper
turns a refusal into the drill's SKIP (exit 3) while live mode runs the given executable untouched."""
import hashlib, json, os, pathlib, shutil, struct, subprocess, sys, tempfile, unittest
from unittest.mock import patch

ROOT = pathlib.Path(__file__).resolve().parents[1]
ORACLE = ROOT / "tools" / "blender-oracle" / "oracle.py"
BASH = shutil.which("bash") or r"C:\Program Files\Git\bin\bash.exe"


# Hooks export repository-local Git variables. Child test repositories must not inherit the caller's index or Git dir.
GIT_LOCAL_ENV = subprocess.check_output(["git", "rev-parse", "--local-env-vars"], text=True).splitlines()


def isolated_git_env(**updates):
    env = dict(os.environ)
    for name in GIT_LOCAL_ENV: env.pop(name, None)
    env.update(updates)
    return env


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
        self.model.parent.mkdir(parents=True, exist_ok=True)
        self.model.write_bytes(model_bytes)
        self.jobs = self.dir / "jobs.txt"
        write(self.jobs, "JOB\tkey\t%s\t%s\n" % (self.model.as_posix(), job_extra))
        self.raw = self.dir / "raw.txt"
        write(self.raw, "FILE\t%s/out/0/x.glb\nDONE\n" % self.wtmp)
        (self.dir / "out" / "0").mkdir(parents=True, exist_ok=True)
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

    def test_a_script_checked_out_with_crlf_replays_a_record_made_with_lf(self):
        # core.autocrlf gives the same commit LF files in one checkout and CRLF in another; the record must answer in both,
        # while a changed line still refuses (.py and .sh alike)
        a = Run(self.tmp, "runA")
        script = self.tmp / "dump.py"; write(script, "print(1)\nprint(2)\n")
        wrapper = self.tmp / "wrap.sh"; write(wrapper, "echo 1\necho 2\n")
        run("store", *self.store_args(a), "--rc", "0", "--", str(script), str(wrapper), *a.items())
        script.write_bytes(b"print(1)\r\nprint(2)\r\n"); wrapper.write_bytes(b"echo 1\r\necho 2\r\n")
        l = run("load", *self.store_args(a), "--", str(script), str(wrapper), *a.items())
        self.assertEqual(l.returncode, 0, l.stdout); self.assertIn("RC 0", l.stdout)
        script.write_bytes(b"print(1)\r\nprint(3)\r\n")
        l = run("load", *self.store_args(a), "--", str(script), str(wrapper), *a.items())
        self.assertEqual(l.returncode, 99); self.assertIn("dump.py changed", l.stdout)
        script.write_bytes(b"print(1)\r\nprint(2)\r\n"); wrapper.write_bytes(b"echo 1\r\necho 3\r\n")
        l = run("load", *self.store_args(a), "--", str(script), str(wrapper), *a.items())
        self.assertEqual(l.returncode, 99); self.assertIn("wrap.sh changed", l.stdout)

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


    def test_absent_recorded_folder_removes_leftovers(self):
        a = Run(self.tmp, "runA"); shutil.rmtree(a.dir / "out")
        self.assertEqual(run("store", *self.store_args(a), "--rc", "0", "--dirs", "out/0", "--", *a.items()).returncode, 0)
        b = Run(self.tmp, "runB")
        result = run("load", *self.store_args(b), "--dirs", "out/0", "--", *b.items())
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertFalse((b.dir / "out" / "0").exists())

    def test_damaged_record_refuses_before_touching_the_run(self):
        for damage in ("raw", "asset", "missing", "extra", "metadata"):
            with self.subTest(damage=damage):
                a = Run(self.tmp, "runA")
                run("store", *self.store_args(a), "--rc", "0", "--dirs", "out/0", "--", *a.items())
                entry = self.store / "t" / "posed_0"
                if damage == "raw": (entry / "raw.txt").write_bytes(b"changed")
                elif damage == "asset": (entry / "dirs/out/0/x.glb").write_bytes(b"changed")
                elif damage == "missing": (entry / "dirs/out/0/x.glb").unlink()
                elif damage == "extra": (entry / "dirs/out/0/extra.glb").write_bytes(b"extra")
                else:
                    meta = json.loads((entry / "meta.json").read_text()); meta["rc"] = 7
                    (entry / "meta.json").write_text(json.dumps(meta))
                b = Run(self.tmp, "runB"); original = b.raw.read_bytes()
                result = run("load", *self.store_args(b), "--dirs", "out/0", "--", *b.items())
                self.assertEqual(result.returncode, 99, result.stdout + result.stderr)
                self.assertIn("corrupt", result.stdout.lower())
                self.assertEqual(b.raw.read_bytes(), original)
                self.assertEqual((b.dir / "out/0/x.glb").read_bytes(), b"written by blender")

    def test_record_metadata_cannot_delete_a_folder_outside_the_run(self):
        a = Run(self.tmp, "runA")
        run("store", *self.store_args(a), "--rc", "0", "--dirs", "out/0", "--", *a.items())
        entry = self.store / "t/posed_0"
        meta = json.loads((entry / "meta.json").read_text())
        meta["dirs"] = ["../victim"]
        if "manifest" in meta:
            meta.pop("manifest")
            meta["manifest"] = hashlib.blake2b(json.dumps(meta, sort_keys=True).encode("utf-8"), digest_size=16).hexdigest()
        (entry / "meta.json").write_text(json.dumps(meta))
        write(entry / "victim" / "replacement.txt", "replacement")
        victim = self.tmp / "victim"; write(victim / "keep.txt", "keep")
        self.assertEqual(victim.resolve().parent, self.tmp.resolve())
        b = Run(self.tmp, "runB")
        result = run("load", *self.store_args(b), "--dirs", "out/0", "--", *b.items())
        self.assertEqual(result.returncode, 99, result.stdout + result.stderr)
        self.assertEqual((victim / "keep.txt").read_text(), "keep")

    def test_literal_markers_and_unicode_paths_roundtrip_without_changing_bytes(self):
        a = Run(self.tmp / "É", "runA")
        markers = b"\xffNAME\t@WTMP@ @wtmp@ @ROOT@ @TMPD@ @@\n"
        a.raw.write_bytes(markers + ("FILE\t" + a.wtmp + "/out/0/x.glb\n").encode("utf-8"))
        run("store", *self.store_args(a), "--rc", "0", "--", *a.items())
        b = Run(self.tmp / "É", "runB")
        result = run("load", *self.store_args(b), "--", *b.items())
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(b.raw.read_bytes(), markers + ("FILE\t" + b.wtmp + "/out/0/x.glb\n").encode("utf-8"))

    def test_input_keys_do_not_depend_on_temp_prefix_casing(self):
        upper = run("key", "--wtmp", "/TMP/UPPER", "--", "/TMP/UPPER/output")
        lower = run("key", "--wtmp", "/tmp/lower", "--", "/tmp/lower/output")
        self.assertEqual(upper.returncode, 0); self.assertEqual(lower.returncode, 0)
        self.assertEqual(upper.stdout, lower.stdout)

    def test_external_gltf_buffers_and_images_are_fingerprinted(self):
        for changed in ("geometry.bin", "texture.png"):
            with self.subTest(changed=changed):
                a = Run(self.tmp, "runA"); b = Run(self.tmp, "runB")
                for r in (a, b):
                    write(r.dir / "scene.gltf", json.dumps({"asset": {"version": "2.0"}, "buffers": [{"uri": "geometry.bin"}], "images": [{"uri": "texture.png"}]}))
                    (r.dir / "geometry.bin").write_bytes(b"geometry")
                    (r.dir / "texture.png").write_bytes(b"texture")
                run("store", *self.store_args(a), "--rc", "0", "--", *a.items(), a.dir / "scene.gltf")
                (b.dir / changed).write_bytes(b"changed")
                result = run("load", *self.store_args(b), "--", *b.items(), b.dir / "scene.gltf")
                self.assertEqual(result.returncode, 99, result.stdout + result.stderr)
                self.assertIn(changed, result.stdout)

    def test_nested_lists_and_json_path_keys_are_fingerprinted(self):
        for nested in (False, True):
            with self.subTest(nested=nested):
                a = Run(self.tmp, "runA"); b = Run(self.tmp, "runB", model_bytes=b"changed")
                for r in (a, b):
                    r.jobs = r.dir / "jobs.json"
                    if nested:
                        inner = r.dir / "options.json"; write(inner, json.dumps({"model": r.model.as_posix()}))
                        write(r.jobs, json.dumps({"options": inner.as_posix()}))
                    else: write(r.jobs, json.dumps({r.model.as_posix(): {"mode": "probe"}}))
                run("store", *self.store_args(a), "--rc", "0", "--", *a.items())
                result = run("load", *self.store_args(b), "--", *b.items())
                self.assertEqual(result.returncode, 99, result.stdout + result.stderr)
                self.assertIn("m.glb", result.stdout)

    def test_json_with_escaped_windows_paths_is_portable(self):
        a = Run(self.tmp, "runA"); b = Run(self.tmp, "runB")
        for r in (a, b):
            r.jobs = r.dir / "jobs.json"
            write(r.jobs, json.dumps({"file": str(r.model)}))
        run("store", *self.store_args(a), "--rc", "0", "--", *a.items())
        result = run("load", *self.store_args(b), "--", *b.items())
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_literal_backslashes_remain_significant(self):
        first = run("key", "--", "parameter\\n"); second = run("key", "--", "parameter/n")
        self.assertEqual(first.returncode, 0); self.assertEqual(second.returncode, 0)
        self.assertNotEqual(first.stdout, second.stdout)

    def test_binary_external_resource_with_a_list_extension_is_hashed_as_bytes(self):
        a = Run(self.tmp, "runA"); b = Run(self.tmp, "runB")
        for r, data in ((a, b"\xff"), (b, b"\xfe")):
            write(r.dir / "scene.gltf", json.dumps({"asset": {"version": "2.0"}, "buffers": [{"uri": "buffer.txt"}]}))
            (r.dir / "buffer.txt").write_bytes(data)
        run("store", *self.store_args(a), "--rc", "0", "--", *a.items(), a.dir / "scene.gltf")
        result = run("load", *self.store_args(b), "--", *b.items(), b.dir / "scene.gltf")
        self.assertEqual(result.returncode, 99, result.stdout + result.stderr)
        self.assertIn("buffer.txt", result.stdout)

    def test_external_assets_with_script_suffixes_keep_exact_byte_fingerprints(self):
        for container in ("gltf", "glb"):
            for section in ("buffers", "images"):
                for suffix in ("py", "sh"):
                    with self.subTest(container=container, section=section, suffix=suffix):
                        a = Run(self.tmp, "runA"); b = Run(self.tmp, "runB")
                        for r, payload in ((a, b"\r\nX"), (b, b"\nX")):
                            doc = {"asset": {"version": "2.0"}, section: [{"uri": "external." + suffix}]}
                            data = json.dumps(doc).encode("utf-8")
                            if container == "glb":
                                data += b" " * (-len(data) % 4)
                                data = struct.pack("<4sIIII", b"glTF", 2, 20 + len(data), len(data), 0x4e4f534a) + data
                            (r.dir / ("scene." + container)).write_bytes(data)
                            (r.dir / ("external." + suffix)).write_bytes(payload)
                        result = run("store", *self.store_args(a), "--rc", "0", "--", *a.items(), a.dir / ("scene." + container))
                        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                        result = run("load", *self.store_args(b), "--", *b.items(), b.dir / ("scene." + container))
                        self.assertEqual(result.returncode, 99, result.stdout + result.stderr)
                        self.assertIn("external." + suffix, result.stdout)

    def test_nested_script_dependencies_normalise_endings_but_reject_changed_lines(self):
        a = Run(self.tmp, "runA")
        scripts = [self.tmp / "helper.py", self.tmp / "helper.sh"]
        dependencies = self.tmp / "code.json"
        write(dependencies, json.dumps([p.as_posix() for p in scripts]))
        for p in scripts: p.write_bytes(b"first\nsecond\n")
        result = run("store", *self.store_args(a), "--rc", "0", "--", *a.items(), dependencies)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        for p in scripts: p.write_bytes(b"first\r\nsecond\r\n")
        result = run("load", *self.store_args(a), "--", *a.items(), dependencies)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        for p in scripts:
            p.write_bytes(b"first\r\nchanged\r\n")
            result = run("load", *self.store_args(a), "--", *a.items(), dependencies)
            self.assertEqual(result.returncode, 99, result.stdout + result.stderr)
            self.assertIn(p.name, result.stdout)
            p.write_bytes(b"first\r\nsecond\r\n")

    def test_invalid_utf8_in_a_list_does_not_collapse_distinct_input_bytes(self):
        args = self.tmp / "options.txt"
        args.write_bytes(b"flag\xff"); first = run("key", "--", args)
        args.write_bytes(b"flag\xfe"); second = run("key", "--", args)
        self.assertEqual(first.returncode, 0, first.stderr); self.assertEqual(second.returncode, 0, second.stderr)
        self.assertNotEqual(first.stdout, second.stdout)

    def test_external_resources_in_a_glb_json_chunk_are_fingerprinted(self):
        a = Run(self.tmp, "runA"); b = Run(self.tmp, "runB")
        for r, image in ((a, b"first"), (b, b"changed")):
            document = json.dumps({"asset": {"version": "2.0"}, "images": [{"uri": "external.png"}]}).encode()
            document += b" " * (-len(document) % 4)
            r.model.write_bytes(struct.pack("<IIIII", 0x46546c67, 2, 20 + len(document), len(document), 0x4e4f534a) + document)
            (r.model.parent / "external.png").write_bytes(image)
        run("store", *self.store_args(a), "--rc", "0", "--", *a.items())
        result = run("load", *self.store_args(b), "--", *b.items())
        self.assertEqual(result.returncode, 99, result.stdout + result.stderr)
        self.assertIn("external.png", result.stdout)

    def test_failed_refresh_preserves_the_previous_record(self):
        a = Run(self.tmp, "runA")
        run("store", *self.store_args(a), "--rc", "0", "--", *a.items())
        original = (self.store / "t/posed_0/raw.txt").read_bytes()
        a.raw.unlink()
        failed = run("store", *self.store_args(a), "--rc", "0", "--", *a.items())
        self.assertNotEqual(failed.returncode, 0)
        self.assertTrue((self.store / "t/posed_0/meta.json").exists())
        self.assertEqual((self.store / "t/posed_0/raw.txt").read_bytes(), original)


@unittest.skipUnless(os.path.exists(BASH), "bash is needed for the wrapper tests")
class OracleShellTests(unittest.TestCase):
    def setUp(self):
        self.tmp = pathlib.Path(tempfile.mkdtemp(prefix="haf-oracle-sh-"))
        self.store = self.tmp / "store"
        self.fake = self.tmp / "blender.sh"
        write(self.fake, '#!/usr/bin/env bash\necho "BLENDER $*"\necho "OUT $PWD"\nexit ${FAKE_RC:-0}\n')
        self.fake.chmod(0o755)

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def shell(self, mode, script, rc_env="0", root=ROOT):
        env = isolated_git_env(HAF_ORACLE=mode, HAF_BLENDER_ORACLE=self.store.as_posix(), BLENDER=self.fake.as_posix(), FAKE_RC=rc_env)
        body = ('set -u\nROOT="%s"\nTMPD="%s"\nWTMP="$TMPD"\n. "$ROOT/tools/blender_oracle.sh"\n' % (root.as_posix(), self.tmp.as_posix())) + script
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
                             env=isolated_git_env(HAF_ORACLE="replay", HAF_BLENDER_ORACLE=self.store.as_posix(), BLENDER="/nonexistent/blender"),
                             capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=120)
        self.assertEqual(rep.returncode, 0, rep.stdout + rep.stderr)
        self.assertIn("rc 4", rep.stdout); self.assertIn("BLENDER --background one", rep.stdout)
        model.write_bytes(b"v2")
        stale = self.shell("replay", 'oracle_init t "the t drill"\noracle_blender a "$TMPD/raw3.txt" --key "%s" -- --background one; echo "rc $?"\noracle_check; echo not-reached\n' % model.as_posix())
        self.assertEqual(stale.returncode, 3, stale.stdout + stale.stderr)
        self.assertIn("rc 99", stale.stdout); self.assertIn("SKIP — the t drill", stale.stdout); self.assertIn("STALE", stale.stdout)
        self.assertNotIn("not-reached", stale.stdout)


    def test_init_before_temp_allocation_supports_the_drills_call_order(self):
        for mode in ("record", "replay"):
            result = self.shell(mode, 'unset TMPD WTMP\noracle_init t\nTMPD="%s"; WTMP="$TMPD"\noracle_blender a "$TMPD/raw.txt" --key stable -- --background one\noracle_check\n' % self.tmp.as_posix())
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_parallel_invocations_have_private_dependency_lists(self):
        for mode in ("record", "replay"):
            result = self.shell(mode, 'oracle_init t\nfor n in 0 1 2 3; do oracle_blender "part_$n" "$TMPD/raw_$n.txt" --key stable -- --background "$n" & done\nwait\noracle_check\n')
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            for n in range(4):
                self.assertTrue((self.tmp / ("oracle_code_part_%d.json" % n)).exists())
                self.assertIn("BLENDER --background %d" % n, (self.tmp / ("raw_%d.txt" % n)).read_text())

    def test_stderr_diagnostics_cannot_split_stdout_rows(self):
        write(self.fake, '#!/usr/bin/env bash\nprintf "JOB\\talpha"\nprintf "warning: depsgraph\\n" >&2\nprintf "\\tvalue\\nDONE\\talpha\\n"\n')
        expected = "JOB\talpha\tvalue\nDONE\talpha\nwarning: depsgraph\n"
        for mode in ("live", "record", "replay"):
            result = self.shell(mode, 'oracle_init t\noracle_blender a "$TMPD/raw.txt" --key stable -- --background one\noracle_check\n')
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertEqual((self.tmp / "raw.txt").read_text(), expected)

    def test_changed_actual_blender_arguments_refuse_without_changed_explicit_keys(self):
        recorded = self.shell("record", 'oracle_init t\noracle_blender a "$TMPD/raw.txt" --key stable -- --background one\noracle_check\n')
        self.assertEqual(recorded.returncode, 0, recorded.stdout + recorded.stderr)
        replay = self.shell("replay", 'oracle_init t\noracle_blender a "$TMPD/raw2.txt" --key stable -- --background two\noracle_check\n')
        self.assertEqual(replay.returncode, 3, replay.stdout + replay.stderr)
        self.assertIn("STALE", replay.stdout)

    def test_created_output_argument_is_not_hashed_as_an_input(self):
        write(self.fake, '#!/usr/bin/env bash\nprintf output > "$1"\necho DONE\n')
        recorded = self.shell("record", 'oracle_init t\noracle_blender a "$TMPD/raw.txt" --key stable -- "$TMPD/generated.glb"\noracle_check\n')
        self.assertEqual(recorded.returncode, 0, recorded.stdout + recorded.stderr)
        (self.tmp / "generated.glb").unlink()
        replay = self.shell("replay", 'oracle_init t\noracle_blender a "$TMPD/raw2.txt" --key stable -- "$TMPD/generated.glb"\noracle_check\n')
        self.assertEqual(replay.returncode, 0, replay.stdout + replay.stderr)

    def test_shared_tracked_python_helper_changes_invalidate_the_record(self):
        repo = self.tmp / "repo"; (repo / "tools/blender-oracle").mkdir(parents=True)
        shutil.copy2(ROOT / "tools/blender_oracle.sh", repo / "tools/blender_oracle.sh")
        shutil.copy2(ORACLE, repo / "tools/blender-oracle/oracle.py")
        write(repo / "tools/example/driver.py", "import helper\n")
        helper = repo / "tools/example/helper.py"; write(helper, "VALUE = 1\n")
        for args in (("init", "-q"), ("add", ".")):
            result = subprocess.run(["git", "-c", "safe.directory=" + repo.as_posix(), "-C", str(repo), *args], capture_output=True, text=True, env=isolated_git_env())
            self.assertEqual(result.returncode, 0, result.stderr)
        recorded = self.shell("record", 'oracle_init t\noracle_blender a "$TMPD/raw.txt" --key "$ROOT/tools/example/driver.py" -- --background one\noracle_check\n', root=repo)
        self.assertEqual(recorded.returncode, 0, recorded.stdout + recorded.stderr)
        write(helper, "VALUE = 2\n")
        replay = self.shell("replay", 'oracle_init t\noracle_blender a "$TMPD/raw2.txt" --key "$ROOT/tools/example/driver.py" -- --background one\noracle_check\n', root=repo)
        self.assertEqual(replay.returncode, 3, replay.stdout + replay.stderr)
        self.assertIn("helper.py", replay.stdout)

    def test_foreign_hook_git_context_does_not_change_its_index(self):
        parent = self.tmp / "parent"; parent.mkdir()
        write(parent / "sentinel.txt", "keep parent index unchanged")
        for args in (("init", "-q"), ("add", ".")):
            result = subprocess.run(["git", "-c", "safe.directory=" + parent.as_posix(), "-C", str(parent), *args],
                                    capture_output=True, text=True, env=isolated_git_env())
            self.assertEqual(result.returncode, 0, result.stderr)
        parent_index = parent / ".git/index"; before = parent_index.read_bytes()
        with patch.dict(os.environ, {"GIT_DIR": str(parent / ".git"), "GIT_WORK_TREE": str(parent), "GIT_INDEX_FILE": str(parent_index)}):
            self.test_shared_tracked_python_helper_changes_invalidate_the_record()
        self.assertEqual(parent_index.read_bytes(), before)

    def test_record_refuses_inputs_changed_during_blender(self):
        model = self.tmp / "changing.glb"; model.write_bytes(b"before")
        write(self.fake, '#!/usr/bin/env bash\ncat "$1"\nprintf after > "$1"\n')
        result = self.shell("record", 'oracle_init t\noracle_blender a "$TMPD/raw.txt" --key "%s" -- "%s"\noracle_check\n' % (model.as_posix(), model.as_posix()))
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("inputs changed", result.stdout)
        self.assertFalse((self.store / "t/a/meta.json").exists())

    def test_record_and_replay_preserve_the_callers_source_variable(self):
        for mode in ("record", "replay"):
            result = self.shell(mode, 'oracle_init t\nsrc=caller-source\noracle_blender a "$TMPD/raw.txt" --key stable -- --background one\noracle_check\necho "source=$src"\n')
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertIn("source=caller-source", result.stdout)

    def test_record_and_live_fail_loud_without_blender(self):
        for mode in ("record", "live"):
            env = isolated_git_env(HAF_ORACLE=mode, HAF_BLENDER_ORACLE=self.store.as_posix(), BLENDER="/nonexistent/blender.exe")
            r = subprocess.run([BASH, "-c", 'ROOT="%s"; TMPD="%s"; WTMP="$TMPD"; . "$ROOT/tools/blender_oracle.sh"; oracle_init t "the t drill"; echo not-reached' % (ROOT.as_posix(), self.tmp.as_posix())],
                               env=env, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=60)
            self.assertEqual(r.returncode, 2, mode); self.assertIn("FAIL — the t drill", r.stdout); self.assertNotIn("not-reached", r.stdout)

    def test_a_bad_mode_is_refused(self):
        r = self.shell("sometimes", 'echo not-reached\n')
        self.assertEqual(r.returncode, 2); self.assertIn("HAF_ORACLE must be", r.stdout)


if __name__ == "__main__":
    unittest.main()
