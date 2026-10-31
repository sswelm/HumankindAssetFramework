"""check-worker-thread.py - no main-thread Unity API inside a worker body, directly or one call away (2026-10-31).

WHY THIS EXISTS. The daily backup's delete-guard prune never ran once (720 guards, 321 past the 14-day retention,
not one "pruned" line in any Editor.log): its worker read `KeepGuardDays` - a property wrapping EditorPrefs.GetInt -
from inside Task.Run. Unity's EditorPrefs throws off the main thread, a faulted Task says nothing, and the log showed
the snapshot and the rotation and then silence, exactly where the pref was read. A grep for EditorPrefs inside the
lambda would have found nothing: the read was one hop away, behind a property on the same class.

THE SHAPES CAUGHT, in editor/*.cs: inside the body of a lambda handed to Task.Run / Task.Factory.StartNew /
ThreadPool.QueueUserWorkItem / new Thread - a block, or an expression up to the next top-level comma: a cancellation
token or scheduling option after the lambda is the CALLER's - (1) a main-thread Unity API used directly (EditorPrefs.,
AssetDatabase., EditorUtility., EditorApplication., Selection., Application.dataPath, ...), and (2) the name of a
member declared in the same file whose own body uses one directly (one hop: `KeepGuardDays => EditorPrefs.GetInt(...)`,
a property or a method, expression- or block-bodied). Comments and string TEXT are blanked first, so a log line that
mentions EditorPrefs is not a use; the {holes} of an interpolated string are code and stay (the review of PR #230).

WHAT IT CANNOT SEE, said: a delegate passed by name (`Task.Run(check)`) - those are listed, not judged; two hops;
a Unity API reached through another class's member (the hop is per file). Debug.Log is thread-safe and not listed.

    python tools/check-worker-thread.py            the gate's step: 0 clean, 1 a use found, 2 the self-test failed
"""
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

MAIN_THREAD = ["EditorPrefs", "PlayerPrefs", "AssetDatabase", "EditorUtility", "EditorApplication", "EditorGUIUtility",
               "EditorGUI", "EditorGUILayout", "GUI", "GUILayout", "Selection", "Undo", "PrefabUtility",
               "EditorSceneManager", "AssetImporter", "EditorWindow", "SceneView", "Handles", "Gizmos"]
MT_RE = re.compile(r"\b(" + "|".join(MAIN_THREAD) + r")\s*\.|\bApplication\s*\.\s*(dataPath|persistentDataPath|streamingAssetsPath|temporaryCachePath)\b")
STARTERS = re.compile(r"\b(Task\s*\.\s*Run|Task\s*\.\s*Factory\s*\.\s*StartNew|ThreadPool\s*\.\s*QueueUserWorkItem|new\s+Thread)\s*\(")
# a declaration wherever it starts (after a line start, a brace or a semicolon: `{ static int Days() => ...; }` on one line too)
DECL = re.compile(r"(?<![\w.])(?:(?:public|internal|private|protected|static|readonly|override|virtual|abstract|async|unsafe|new)\s+)+"
                  r"[\w<>\[\],.?]+\s+(\w+)\s*(=>|\(|\{)")


def blank(text):
    """Comments and string contents replaced by spaces (newlines kept): structure stays, words inside them vanish."""
    out = []
    i, n = 0, len(text)
    while i < n:
        c = text[i]
        two = text[i:i + 2]
        if two == "//":
            j = text.find("\n", i)
            j = n if j < 0 else j
            out.append(" " * (j - i)); i = j
        elif two == "/*":
            j = text.find("*/", i + 2)
            j = n if j < 0 else j + 2
            out.append(re.sub(r"[^\n]", " ", text[i:j])); i = j
        elif c == '"' or (c in "@$" and text[i + 1:i + 2] == '"') or (text[i:i + 2] in ("@$", "$@") and text[i + 2:i + 3] == '"'):
            k = i
            verbatim = interpolated = False
            while text[k] in "@$":
                verbatim = verbatim or text[k] == "@"; interpolated = interpolated or text[k] == "$"; k += 1
            if interpolated:   # the holes are CODE (the review of PR #230: `$"days: {EditorPrefs.GetInt(...)}"` had vanished with the text)
                j, content = scan_interpolated(text, k, verbatim)
                out.append(text[i:k] + '"' + content + '"'); i = j
                continue
            j = k + 1
            while j < n:
                if verbatim and text[j] == '"' and text[j + 1:j + 2] == '"':
                    j += 2; continue
                if text[j] == '"':
                    break
                if not verbatim and text[j] == "\\":
                    j += 2; continue
                if not verbatim and text[j] == "\n":
                    break
                j += 1
            j = min(n, j + 1)
            out.append(text[i:k] + '"' + re.sub(r"[^\n]", " ", text[k + 1:j - 1]) + '"'); i = j
        elif c == "'":
            j = i + 1
            while j < n and text[j] != "'" and text[j] != "\n":
                j += 2 if text[j] == "\\" else 1
            j = min(n, j + 1)
            out.append("'" + " " * (j - i - 2) + "'" if j - i >= 2 else text[i:j]); i = j
        else:
            out.append(c); i += 1
    return "".join(out)


def scan_interpolated(text, k, verbatim):
    """From the opening quote at k of a $-string: (index past the closing quote, the content with its text blanked and
    its {holes} kept). A plain string nested inside a hole is blanked whole; `{{` and `}}` are text."""
    n = len(text)
    j, depth, out = k + 1, 0, []
    while j < n:
        c = text[j]
        if depth == 0:
            if c in "{}" and text[j + 1:j + 2] == c:
                out.append("  "); j += 2; continue
            if c == "{":
                depth = 1; out.append("{"); j += 1; continue
            if verbatim and c == '"' and text[j + 1:j + 2] == '"':
                out.append("  "); j += 2; continue
            if c == '"':
                return j + 1, "".join(out)
            if not verbatim and c == "\\":
                out.append("  "); j += 2; continue
            out.append("\n" if c == "\n" else " "); j += 1
        else:   # inside a hole: code, whose own comments and strings are blanked like anywhere else (recursively)
            if text.startswith("//", j):
                e = text.find("\n", j); e = n if e < 0 else e
                out.append(" " * (e - j)); j = e; continue
            if text.startswith("/*", j):
                e = text.find("*/", j + 2); e = n if e < 0 else e + 2
                out.append(re.sub(r"[^\n]", " ", text[j:e])); j = e; continue
            if c == '"' or (c in "@$" and text[j + 1:j + 2] == '"') or (text[j:j + 2] in ("@$", "$@") and text[j + 2:j + 3] == '"'):
                kk = j; vb = ip = False
                while text[kk] in "@$":
                    vb = vb or text[kk] == "@"; ip = ip or text[kk] == "$"; kk += 1
                if ip:
                    e, content = scan_interpolated(text, kk, vb)
                    out.append(text[j:kk] + '"' + content + '"'); j = e; continue
                e = kk + 1
                while e < n:
                    if vb and text[e] == '"' and text[e + 1:e + 2] == '"':
                        e += 2; continue
                    if text[e] == '"' or (not vb and text[e] == "\n"):
                        break
                    e += 2 if (not vb and text[e] == "\\") else 1
                out.append(text[j:kk] + '"' + " " * max(0, e - kk - 1) + '"'); j = e + 1; continue
            if c == "{":
                depth += 1
            elif c == "}":
                depth -= 1
            out.append(c); j += 1
    return n, "".join(out)


def matching(text, start, open_ch, close_ch):
    """Index just past the bracket that closes the one at `start`, or len(text)."""
    depth = 0
    for j in range(start, len(text)):
        if text[j] == open_ch:
            depth += 1
        elif text[j] == close_ch:
            depth -= 1
            if depth == 0:
                return j + 1
    return len(text)


def member_bodies(text):
    """(name, body) of every declared member with an expression or block body; the first-hop table is built from them."""
    for m in DECL.finditer(text):
        name, tail = m.group(1), m.group(2)
        at = m.end()
        if tail == "(":   # a method: past its parameter list, an arrow or a block (the review of PR #230: `Days() => ...` was skipped)
            at = matching(text, m.end() - 1, "(", ")")
            rest = text[at:at + 40].lstrip()
            tail = "=>" if rest.startswith("=>") else "{" if rest.startswith("{") else ""
            if tail == "=>":
                at = text.find("=>", at) + 2
        if tail == "=>":
            end = text.find(";", at)
            yield name, text[at:end if end >= 0 else len(text)]
        elif tail == "{":
            brace = text.find("{", at - 1)
            if brace < 0:
                continue
            yield name, text[brace:matching(text, brace, "{", "}")]


def worker_bodies(text):
    """(start offset, body text or None for a delegate by name, that name) for every worker starter."""
    for m in STARTERS.finditer(text):
        arg_start = m.end()
        arg_end = matching(text, m.end() - 1, "(", ")") - 1
        arg = text[arg_start:arg_end]
        lam = re.match(r"\s*(?:async\s+)?(?:\(\s*[\w\s,]*\)|\w+)\s*=>\s*", arg)
        if not lam:
            yield m.start(), None, arg.strip()
            continue
        body = arg[lam.end():]
        if body.lstrip().startswith("{"):
            b = body.find("{")
            body = body[b:matching(body, b, "{", "}")]
        else:   # an expression body ends at the first top-level comma (a cancellation token, scheduling options: the caller's thread)
            # `<` right after a name opens generic arguments (`F<int, int>(...)`): its comma is not the lambda's end. A
            # comparison spelt `a<b` opens one that never closes, which only makes the body longer: the safe direction.
            depth, cut = 0, len(body)
            for idx, ch in enumerate(body):
                if ch in "([{" or (ch == "<" and idx > 0 and (body[idx - 1].isalnum() or body[idx - 1] == "_")):
                    depth += 1
                elif ch in ")]}" or (ch == ">" and depth > 0 and idx > 0 and body[idx - 1] not in "=-"):
                    depth -= 1
                elif ch == "," and depth == 0:
                    cut = idx; break
            body = body[:cut]
        yield m.start(), body, None


def line_of(text, off):
    return text.count("\n", 0, off) + 1


def check_text(text, path="<text>"):
    """The findings for one file's text: (line, message) rows, plus the delegates passed by name."""
    t = blank(text)
    wrappers = {}
    for name, body in member_bodies(t):
        hit = MT_RE.search(body)
        if hit and name not in wrappers:
            wrappers[name] = (hit.group(0).strip(), line_of(t, t.find(body)))
    findings, unseen = [], []
    for off, body, by_name in worker_bodies(t):
        line = line_of(t, off)
        if body is None:
            unseen.append((line, by_name)); continue
        for hit in MT_RE.finditer(body):
            findings.append((line_of(t, off + len(t[off:].split(body, 1)[0]) + hit.start()) if body in t[off:] else line,
                             f"{path}:{line}: '{hit.group(0).strip()}' is a main-thread Unity API, used inside a worker body"))
        for name, (api, decl_line) in wrappers.items():
            for use in re.finditer(r"\b" + re.escape(name) + r"\b", body):
                findings.append((line, f"{path}:{line}: '{name}' uses {api} ({path}:{decl_line}), and is read inside a worker body"))
                break
    return findings, unseen


PLANT = '''
static class Planted
{
    static int Days => EditorPrefs.GetInt("HAF.Days", 14);   // the wrapper that hid the real one
    static int Days2() => EditorPrefs.GetInt("HAF.Days2", 14);   // an expression-bodied METHOD wrapper
    static string Note() { return "EditorPrefs. in a string is not a use"; }
    static System.Threading.CancellationToken Token() { EditorPrefs.GetInt("t", 0); return default; }
    static void Run()
    {
        System.Threading.Tasks.Task.Run(() =>
        {
            int keep = Days;                 // one hop: must be found
            AssetDatabase.Refresh();         // direct: must be found
            Debug.Log("EditorPrefs.");       // a string: must not
        });
        System.Threading.Tasks.Task.Run(() => Note());   // a hop to a string-only member: must not
        System.Threading.Tasks.Task.Run(() => Debug.Log($"days: {EditorPrefs.GetInt("x", 14)} {{not a hole}}"));   // a hole: must be found
        System.Threading.Tasks.Task.Run(() => { int d2 = Days2(); }, Token());   // the method hop: found; Token() runs on the caller: not
        System.Threading.Tasks.Task.Run(() => F<int, int>(EditorPrefs.GetInt("g", 1)));   // the generic comma is not the end: found
        System.Threading.Tasks.Task.Run(() => Debug.Log($"outer {$"inner {EditorPrefs.GetInt("n", 1)}"} end"));   // a nested $-string's hole: found
        System.Threading.Tasks.Task.Run(() => Debug.Log($"c {/* EditorPrefs.GetInt */ 1} {"EditorPrefs."}"));   // a comment and a string in holes: not
        System.Threading.Tasks.Task.Run(check);           // by name: listed, not judged
    }
}
'''


def self_test():
    findings, unseen = check_text(PLANT, "plant.cs")
    msgs = "\n".join(m for _, m in findings)
    by_name_line = next(i + 1 for i, l in enumerate(PLANT.split("\n")) if "Task.Run(check)" in l)
    ok = ("'Days' uses EditorPrefs." in msgs and "'Days2' uses EditorPrefs." in msgs
          and "'AssetDatabase.' is a main-thread" in msgs and "'EditorPrefs.' is a main-thread" in msgs
          and "Note" not in msgs and "Token" not in msgs and msgs.count("EditorPrefs") == 5
          and unseen == [(by_name_line, "check")])
    if not ok:
        print("FAIL - worker-thread self-test: the planted shapes were not reported as expected:\n" + msgs + f"\n  unseen: {unseen}")
        return False
    return True


def main():
    if not self_test():
        return 2
    files = subprocess.run(["git", "ls-files", "--", "editor/*.cs"], cwd=ROOT, capture_output=True, text=True, check=True).stdout.split()
    all_findings, all_unseen, bodies = [], [], 0
    for rel in files:
        text = (ROOT / rel).read_text(encoding="utf-8", errors="replace")
        if not STARTERS.search(text):
            continue
        f, u = check_text(text, rel)
        bodies += sum(1 for _, b, _ in worker_bodies(blank(text)) if b is not None)
        all_findings += f
        all_unseen += [(rel, l, n) for l, n in u]
    for _, m in sorted(set(all_findings)):
        print("FAIL - " + m)
    for rel, l, n in all_unseen:
        print(f"  cannot see: {rel}:{l} hands the worker a delegate by name ('{n}')")
    if all_findings:
        print(f"worker thread: FAIL - {len(set(all_findings))} main-thread use(s) inside worker bodies ({bodies} bodies in {len(files)} files)")
        return 1
    print(f"worker thread: OK - no main-thread Unity API inside {bodies} worker bodies, directly or one hop away ({len(files)} editor files; {len(all_unseen)} delegate(s) by name listed above, not seen)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
