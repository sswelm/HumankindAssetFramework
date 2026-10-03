// HeadlessBakeTests.cs — batch-mode entry for the in-editor INTEGRATION suites, so the baker's feature
// invariants can run without the GUI (tools/editor_tests.ps1 drives it):
//
//   Unity.exe -batchmode -nographics -projectPath <modding project> -executeMethod HeadlessBakeTests.Run
//
// Runs BakeFeatureTest Tier 1 (self-contained synthetic cubes, non-destructive "__feat_*" names, cleaned up
// by the section itself) and the Model Reader section (every registry model and the GLB fixtures through the
// reader, the preview builder and the writer, in Unity's runtime) and exits 0 on all-pass, 1 on any failure — deliberately binary (exit codes wrap at
// 255, so a count would lie for large suites); the log carries the per-check detail and the fail count.
// Deliberately NOT in the per-push gate: a Unity boot costs ~a minute, and hosted CI runners have no
// licensed Unity — this is the opt-in lane for baker changes (Factory-Manual §11's "run the bake tests before
// committing baker changes", now automatable). The registry-dependent smoke suites stay in the GUI runner:
// they bake real registered models with machine-local source files, which a headless verdict can't normalize.
using UnityEditor;
using UnityEngine;

public static class HeadlessBakeTests
{
    public static void Run()
    {
        int fail = 0;
        try
        {
            var s = BakeFeatureTest.RunTier1Section();
            Debug.Log($"[HeadlessBakeTests] {s.title}: PASS {s.pass} / FAIL {s.fail} / SKIP {s.skip}\n{s.body}");
            fail += s.fail;
            // the GLB reader and writer in Unity's own runtime (its Newtonsoft, its Mono, the SYSTEM locale a batch run gets):
            // every registry model and the fixtures read, built as meshes, written, read back equal field by field
            s = ModelReaderHeadlessTest.RunSection();
            Debug.Log($"[HeadlessBakeTests] {s.title}: PASS {s.pass} / FAIL {s.fail} / SKIP {s.skip}\n{s.body}");
            fail += s.fail;
            // the Clip Range picker's in-process rig: Unity's own skinning of every registry model against the reader's pose
            s = HafModelRigHeadlessTest.RunSection();
            Debug.Log($"[HeadlessBakeTests] {s.title}: PASS {s.pass} / FAIL {s.fail} / SKIP {s.skip}\n{s.body}");
            fail += s.fail;
        }
        catch (System.Exception e)
        {
            Debug.LogError("[HeadlessBakeTests] runner threw: " + e);
            fail++;
        }
        Debug.Log(fail == 0 ? "[HeadlessBakeTests] RESULT: PASS" : $"[HeadlessBakeTests] RESULT: FAIL ({fail})");
        EditorApplication.Exit(fail == 0 ? 0 : 1);
    }
}
