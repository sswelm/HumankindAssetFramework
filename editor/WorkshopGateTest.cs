// WorkshopGateTest.cs — run via Tools ▸ HAF ▸ Bake Tests… (BakeTestRunnerWindow).
// THE MODEL WORKSHOP ON REAL FILES (2026-09-25, user: "could you add fuse and split ... test to the bake test"). The
// Workshop's split, tear and fuse are covered by the C# suite on synthetic fixtures (Tests/GlbFuseTests.cs and its
// neighbours) and by nothing on a real ship: the hole censuses and renders that settled PR #83 and PR #85 were run by
// hand. This row takes every Vehicle Lab recipe's SOURCE GLB — the files the Workshop produced or was fed, unskinned,
// on disk, already known to the pipeline — and works on each IN MEMORY (nothing on disk is touched):
//   SPLIT (no merge)   every node with more than one island: output triangles == source triangles, every new
//                      _Part_NNN child is exactly one island, every node name unique afterwards
//   SPLIT (1 % merge)  the Workshop's default dial — counts only
//   TEAR  (1 % merge)  the SMALLEST multi-island node (a tear labels every vertex of the node against every island's
//                      mirror: every node took 374 s on the 410k-triangle frigate, its largest node alone 215 s, its
//                      smallest 3 s): output triangles == source triangles
//   FUSE  (weld 0)     the largest nodes up to a 150,000-triangle budget, into ONE shell (a whole million-triangle
//                      liner would not finish inside a test row): the shell is in the output with the triangle count
//                      the fuse reported, and at least half the source triangles survived (a collapse beyond that is
//                      a broken weld, not a tidy-up)
// then diffs the counts — parts, islands, children, triangles, welded vertices, islands after, faces rewound — against a
// blessed golden, Tools/workshop_golden/<file>.txt (the deploy row's golden-master idea): the direction pass's verdicts
// are exactly what PR #83 changed, and a rewound-faces count that moves on a ship nobody touched IS the regression. A
// missing golden is captured from the run and reported as such (not a pass); delete it to re-bless an intended change;
// a mismatch leaves the run's lines in Logs/bake_tests/workshop/<file>.candidate.txt.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

public static class WorkshopGateTest
{
    const string RecipesDir = "Assets/FactorySource/VehicleLab/Recipes";
    const string GoldenDir = "Tools/workshop_golden";
    const string WorkDir = "Logs/bake_tests/workshop";

    public static BakeTestSection RunSection()
    {
        const string title = "Model Workshop — split, tear and fuse every recipe's source";
        BakeTestSection Skip(string why) { Debug.LogWarning("[WorkshopGate] " + why); return new BakeTestSection { title = title, skip = 1, body = "SKIP — " + why }; }
        string projRoot = Directory.GetParent(Application.dataPath).FullName;
        string recipesDir = Path.Combine(projRoot, RecipesDir);
        if (!Directory.Exists(recipesDir)) return Skip("no Vehicle Lab recipes (" + RecipesDir + ") — no source models for the Workshop to work on.");
        var sources = Directory.GetFiles(recipesDir, "*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Select(p => VehicleLabWindow.ReadRecipe(p, out string src, out _) ? src : null)
            .Where(s => !string.IsNullOrWhiteSpace(s) && s.EndsWith(".glb", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Replace('\\', '/')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (sources.Count == 0) return Skip("no recipe names a .glb source — nothing for the Workshop to work on.");

        string goldDir = Path.Combine(projRoot, GoldenDir);
        string workDir = Path.Combine(projRoot, WorkDir);
        int pass = 0, fail = 0, skip = 0;
        var lines = new List<string>();
        string current = null;
        try
        {
            for (int i = 0; i < sources.Count; i++)
            {
                string src = sources[i]; string name = Path.GetFileNameWithoutExtension(src); current = name;
                if (!File.Exists(src)) { lines.Add($"SKIP {name} — source missing ({src})"); skip++; continue; }
                BakeTestRunnerWindow.Progress.Step($"{name} ({i + 1}/{sources.Count}) — split, tear, fuse…", (float)i / sources.Count);
                var w = System.Diagnostics.Stopwatch.StartNew();
                var problems = new List<string>(); var snap = new List<string>();
                byte[] bytes = File.ReadAllBytes(src);
                try { Exercise(bytes, name, snap, problems); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { problems.Add("exception " + ex.GetType().Name + ": " + ex.Message); }
                w.Stop();
                string took = FormattableString.Invariant($"{w.Elapsed.TotalSeconds:0}s");
                if (problems.Count > 0)
                {
                    lines.Add($"FAIL {name} ({took}) — " + string.Join("; ", problems));
                    Debug.LogError($"[WorkshopGate] {name}:\n" + string.Join("\n", problems) + "\n" + string.Join("\n", snap));
                    fail++;
                }
                else
                {
                    // the golden's provenance (BakeGoldenFiles, 2026-10-12): the source bytes are the row's one input
                    var inputs = new Dictionary<string, string> { ["source"] = BakeGoldenRules.InputHash(bytes) };
                    string goldFile = Path.Combine(goldDir, name + ".txt");
                    if (!File.Exists(goldFile))
                    {
                        BakeGoldenFiles.Capture(goldFile, snap, inputs);
                        lines.Add($"CAPTURED {name} ({took}) — no golden yet; this run's counts are now {GoldenDir}/{name}.txt (not a pass — run again to verify)");
                        skip++;
                    }
                    else
                    {
                        var verdict = BakeGoldenFiles.Judge(goldFile, snap, inputs, out string reason, out string note);
                        if (verdict == BakeGoldenRules.GoldenVerdict.Match) { lines.Add($"PASS {name} ({took}; {snap.Last()}){note}"); pass++; }
                        else
                        {
                            Directory.CreateDirectory(workDir);
                            File.WriteAllLines(Path.Combine(workDir, name + ".candidate.txt"), snap);
                            string what = verdict == BakeGoldenRules.GoldenVerdict.Edited ? "" : "the Workshop's result CHANGED vs golden: ";
                            lines.Add($"FAIL {name} ({took}) — {what}{reason}. Candidate in {WorkDir}/{name}.candidate.txt; delete {GoldenDir}/{name}.txt and run again to re-bless an intended change.");
                            fail++;
                        }
                    }
                }
                BakeTestRunnerWindow.Progress.ThrowIfCancelled();
            }
        }
        catch (OperationCanceledException)
        { lines.Add($"CANCELLED by the user at {current ?? "start"} — the files above are complete, the rest did not run"); }
        Debug.Log($"[WorkshopGate] {pass} pass, {fail} fail, {skip} skipped/captured (of {sources.Count} source files).");
        return new BakeTestSection { title = title, pass = pass, fail = fail, skip = skip, body = string.Join("\n", lines) };
    }

    // The four operations on one file, in memory. Every hard invariant goes to `problems`; the counts go to `snap`
    // (the golden). The file first gets unique node names, as the Workshop does at probe (UniqueBytes), so a source
    // that already repeats a name does not read as a split that broke uniqueness.
    internal static void Exercise(byte[] bytes, string name, List<string> snap, List<string> problems)
    {
        var unique = GlbDisconnectedParts.UniqueNodeNames(bytes);
        if (unique.Bytes != null) bytes = unique.Bytes;
        var parts = GlbDisconnectedParts.Analyze(bytes, 0);
        var usable = parts.Where(p => p.Blocked == null).ToList();
        snap.Add($"SOURCE parts={parts.Count} tris={parts.Sum(p => p.Triangles)} blocked={parts.Count - usable.Count} islands={usable.Sum(p => p.Islands)}");
        if (usable.Count == 0) { problems.Add("no part the Workshop can work on (every node is blocked)"); return; }

        // ---- split: the nodes with more than one island, at no merge (the invariants) and at the 1 % default (the counts)
        var multi = new HashSet<int>(usable.Where(p => p.Islands > 1).Select(p => p.NodeIndex));
        if (multi.Count > 0)
        {
            var split = GlbDisconnectedParts.Split(bytes, multi, 0);
            if (split.OutputTriangles != split.SourceTriangles) problems.Add($"the split lost triangles ({split.SourceTriangles} -> {split.OutputTriangles})");
            var after = GlbDisconnectedParts.Analyze(split.Bytes, 0);
            // the children are the NEW nodes (a source that is itself a Workshop output already carries _Part_NNN names)
            var before = new HashSet<string>(parts.Select(p => p.NodeName ?? ""), StringComparer.Ordinal);
            var children = after.Where(p => !before.Contains(p.NodeName ?? "")).ToList();
            if (children.Count != split.ChildPartsCreated) problems.Add($"the split reports {split.ChildPartsCreated} children but the output has {children.Count} new nodes");
            if (children.Any(c => !IsCutChild(c.NodeName))) problems.Add("a new node is not named _Part_NNN");
            // named, not counted (2026-10-12: "23 split child(ren) are not one island" on the salegs source could not be
            // investigated from the count; GlbDisconnectedParts.ExplainIslands measures the first one)
            var notOneList = children.Where(c => c.Blocked == null && c.Islands != 1).OrderByDescending(c => c.Islands).ThenBy(c => c.NodeName, StringComparer.Ordinal).ToList();
            if (notOneList.Count > 0)
                problems.Add($"{notOneList.Count} split child(ren) are not one island: " +
                             string.Join(", ", notOneList.Take(5).Select(c => $"{c.NodeName} ({c.Islands})")) + (notOneList.Count > 5 ? ", …" : "") +
                             "; " + GlbDisconnectedParts.ExplainIslands(split.Bytes, notOneList[0].NodeName, 3));
            var names = after.Select(p => p.NodeName ?? "").Where(n => n.Length > 0).ToList();
            if (names.Distinct(StringComparer.Ordinal).Count() != names.Count) problems.Add("node names are not unique after the split");
            snap.Add($"SPLIT@0 nodes={split.NodesSplit} children={split.ChildPartsCreated} tris={split.OutputTriangles}");
            BakeTestRunnerWindow.Progress.ThrowIfCancelled();
            var split1 = GlbDisconnectedParts.Split(bytes, multi, 0.01);
            if (split1.OutputTriangles != split1.SourceTriangles) problems.Add($"the split at 1 % lost triangles ({split1.SourceTriangles} -> {split1.OutputTriangles})");
            snap.Add($"SPLIT@1% nodes={split1.NodesSplit} children={split1.ChildPartsCreated} tris={split1.OutputTriangles}");
        }
        else snap.Add("SPLIT none (every part is one island)");
        BakeTestRunnerWindow.Progress.ThrowIfCancelled();

        // ---- tear: the smallest multi-island node (else the smallest node), at the 1 % default
        var torn = usable.OrderByDescending(p => p.Islands > 1 ? 1 : 0).ThenBy(p => p.Triangles).ThenBy(p => p.NodeIndex).First();
        var tear = GlbDisconnectedParts.Tear(bytes, new HashSet<int> { torn.NodeIndex }, 0.01);
        if (tear.OutputTriangles != tear.SourceTriangles) problems.Add($"the tear lost triangles ({tear.SourceTriangles} -> {tear.OutputTriangles})");
        snap.Add($"TEAR@1% node={torn.NodeName} children={tear.ChildPartsCreated} tris={tear.OutputTriangles}");
        BakeTestRunnerWindow.Progress.ThrowIfCancelled();

        // ---- fuse: the largest nodes up to the triangle budget, into one shell, at weld 0 (the Workshop's default)
        var picked = BakeGoldenRules.FuseSelection(usable.Select(p => new KeyValuePair<int, int>(p.NodeIndex, p.Triangles)), FuseTriangleBudget);
        var group = usable.Where(p => picked.Contains(p.NodeIndex)).ToList();
        var job = new GlbDisconnectedParts.FuseJob { NodeIndices = picked, Name = "Fused_T_" + name, CheckMirrored = false };
        byte[] fused; List<GlbDisconnectedParts.Result> results;
        try { fused = GlbDisconnectedParts.FuseGroups(bytes, new[] { job }, 0, out results); }
        catch (InvalidDataException ex) when (ex.Message.IndexOf("skinned", StringComparison.OrdinalIgnoreCase) >= 0)
        { snap.Add("FUSE skipped (a skinned source — the Workshop fuses static parts only)"); return; }
        var r = results[0];
        if (r.OutputTriangles > r.SourceTriangles) problems.Add($"the fuse GAINED triangles ({r.SourceTriangles} -> {r.OutputTriangles})");
        if (r.OutputTriangles * 2 < r.SourceTriangles) problems.Add($"the fuse kept only {r.OutputTriangles} of {r.SourceTriangles} triangles");
        var fusedParts = GlbDisconnectedParts.Analyze(fused, 0);
        var shell = fusedParts.FirstOrDefault(p => p.NodeName == job.Name);
        if (shell == null) problems.Add("the fused shell is missing from the output");
        else if (shell.Triangles != r.OutputTriangles) problems.Add($"the shell has {shell.Triangles} triangles, the fuse reported {r.OutputTriangles}");
        // a one-part group is reported as such: the multi-part join is what a fuse is for, and this file could not offer it inside the budget
        snap.Add($"FUSE parts={group.Count}{(group.Count == 1 && usable.Count > 1 ? " (single: no two parts fit the budget)" : "")} tris={r.SourceTriangles}->{r.OutputTriangles} verts={r.VerticesBefore}->{r.VerticesAfter} islands={r.IslandsBefore}->{r.IslandsAfter} rewound={r.FacesRewound}");
    }

    internal const int FuseTriangleBudget = 150000;

    static bool IsCutChild(string n) => n != null && System.Text.RegularExpressions.Regex.IsMatch(n, @"_Part_\d{3}$");
}
