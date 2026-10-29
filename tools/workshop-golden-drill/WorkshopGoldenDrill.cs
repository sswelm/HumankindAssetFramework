// WorkshopGoldenDrill.cs — the Bake Tests' Workshop row, outside Unity (2026-10-12).
//
// The Workshop row (editor/WorkshopGateTest.cs) ran only inside the editor, by hand, from the Bake Tests window. Its
// last run (2026-10-10 18:38) was red on seven sources — six FUSE lines moved against their goldens and one split
// left children that are not one island — and the goldens were then rewritten by hand to the failing values, with
// no run after them. Nothing outside Unity could say which commit moved a count, or whether the hand edit matched
// what the code produces. This drill runs the SAME Exercise (the row's own method, compiled from editor/) on the
// recipe sources and judges each against the goldens, writing nothing into the project: a candidate goes to --out,
// a missing golden is reported as NOGOLDEN (the Unity row would capture it), and a child that is not one island is
// NAMED with its island count. Compiled from any checkout's editor/ (HAF_EDITOR_ROOT in the .sh), it attributes a
// moved count to the commits between two checkouts.
//
// Caveat, printed with every run: this is the 64-bit .NET Framework runtime, not Unity's 64-bit Mono. Mono evaluates
// an uncast float product in double; a count that differs between the two runtimes is a precision question, not a
// code change. A golden is BLESSED only by a run in Unity (the row captures it); this drill never writes one.
//
//   WorkshopGoldenDrill.exe [--goldens <dir>] [--out <dir>] [--progress <file>] [--only a,b] [--no-detail] <source.glb>...
//
// Exit 0 when every judged source passes, 1 on any FAIL, 3 when nothing could be judged (no source on disk).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

public static class WorkshopGoldenDrill
{
    public static int Main(string[] args)
    {
        string goldens = null, outDir = null, progress = null; bool detail = true;
        var sources = new List<string>(); var only = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var verify = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--verify-goldens": verify.Add(args[++i]); break;
                case "--goldens": goldens = args[++i]; break;
                case "--out": outDir = args[++i]; break;
                case "--progress": progress = args[++i]; break;
                case "--only": foreach (var n in args[++i].Split(',')) if (n.Trim().Length > 0) only.Add(n.Trim()); break;
                case "--no-detail": detail = false; break;
                default: sources.Add(args[i]); break;
            }
        }
        if (verify.Count > 0) return VerifyGoldens(verify);
        if (sources.Count == 0) { Console.Error.WriteLine("usage: WorkshopGoldenDrill [--goldens <dir>] [--out <dir>] [--progress <file>] [--only a,b] <source.glb>...  |  --verify-goldens <dir>..."); return 2; }
        Console.WriteLine($"runtime: {(Environment.Is64BitProcess ? "64" : "32")}-bit {(Type.GetType("Mono.Runtime") != null ? "Mono" : ".NET Framework")} {Environment.Version} — not Unity's Mono; a golden is blessed only by the Unity row");
        Console.WriteLine("kernels: " + HafNativeKernels.Status);   // the fuse's column and twin census run natively when this says so
        Console.WriteLine($"goldens: {(goldens != null && Directory.Exists(goldens) ? goldens : "(none — every source reads NOGOLDEN)")}");
        int pass = 0, fail = 0, nogolden = 0, skip = 0;
        foreach (string src0 in sources)
        {
            string src = src0.Replace('\\', '/'); string name = Path.GetFileNameWithoutExtension(src);
            if (only.Count > 0 && !only.Contains(name)) continue;
            if (!File.Exists(src)) { Console.WriteLine($"SKIP {name} — source missing ({src})"); skip++; continue; }
            var w = System.Diagnostics.Stopwatch.StartNew();
            var problems = new List<string>(); var snap = new List<string>();
            byte[] bytes = File.ReadAllBytes(src);
            try { WorkshopGateTest.Exercise(bytes, name, snap, problems); }
            catch (Exception ex) { problems.Add("exception " + ex.GetType().Name + ": " + ex.Message); }
            w.Stop();
            string took = FormattableString.Invariant($"{w.Elapsed.TotalSeconds:0}s");
            string verdict;
            if (problems.Count > 0)
            {
                verdict = $"FAIL {name} ({took}) — " + string.Join("; ", problems);
                if (detail && problems.Any(p => p.Contains("not one island")))
                    foreach (string line in IslandDetail(bytes)) verdict += "\n    " + line;
                fail++;
            }
            else
            {
                string goldFile = goldens == null ? null : Path.Combine(goldens, name + ".txt");
                if (goldFile == null || !File.Exists(goldFile))
                {
                    verdict = $"NOGOLDEN {name} ({took}) — no golden to judge against; the Unity row would capture this run ({snap.Last()})";
                    nogolden++;
                }
                else
                {
                    // the row's own judge (BakeGoldenRules.Judge): an edited golden and a source that moved are named as such
                    var inputs = new Dictionary<string, string> { ["source"] = BakeGoldenRules.InputHash(bytes) };
                    var v = BakeGoldenRules.Judge(File.ReadAllLines(goldFile), snap, inputs, out string reason, out bool legacy);
                    if (v == BakeGoldenRules.GoldenVerdict.Match) { verdict = $"PASS {name} ({took}; {snap.Last()}){(legacy ? " [legacy golden: no provenance header yet]" : "")}"; pass++; }
                    else if (v == BakeGoldenRules.GoldenVerdict.Edited) { verdict = $"FAIL {name} ({took}) — {reason}"; fail++; }
                    else { verdict = $"FAIL {name} ({took}) — the Workshop's result CHANGED vs golden: {reason}"; fail++; }
                }
                if (outDir != null)
                {
                    Directory.CreateDirectory(outDir);
                    File.WriteAllLines(Path.Combine(outDir, name + ".candidate.txt"), snap);
                }
            }
            Console.WriteLine(verdict);
            Note(progress, verdict.Split('\n')[0]);
        }
        Console.WriteLine($"DONE pass={pass} fail={fail} nogolden={nogolden} skip={skip} of {pass + fail + nogolden + skip}");
        if (fail > 0) return 1;
        if (pass + nogolden == 0) return 3;
        return 0;
    }

    // Every golden under the given directories, read for its provenance alone (no source needed, seconds): a header
    // whose checksum does not match its lines is an edit outside a run - FAIL, named; a golden without a header is
    // legacy (counted, allowed until a Unity run verifies it and adds the header). The gate's fast step.
    static int VerifyGoldens(List<string> dirs)
    {
        int ok = 0, legacy = 0, edited = 0, missing = 0;
        foreach (string dir in dirs)
        {
            if (!Directory.Exists(dir)) { Console.WriteLine($"SKIP {dir} — no such directory"); missing++; continue; }
            foreach (string f in Directory.GetFiles(dir, "*.txt").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var file = File.ReadAllLines(f);
                var header = BakeGoldenRules.ParseHeader(file);
                int markers = file.Count(l => l != null && l.StartsWith("# golden", StringComparison.Ordinal));
                if (markers == 0) { legacy++; continue; }
                string sum = BakeGoldenRules.Checksum(file);
                if (markers == 1 && header != null && header.Sum == sum) { ok++; continue; }
                edited++;
                Console.WriteLine($"EDITED {Path.GetFileName(f)} — malformed header or changed lines (checksum {sum} on disk, {header?.Sum ?? "missing"} when captured {header?.Captured ?? "unknown"} at editor {header?.Editor ?? "unknown"})");
            }
        }
        Console.WriteLine($"goldens: {ok} verified by header, {legacy} legacy (no provenance header yet), {edited} EDITED outside a run, {missing} director{(missing == 1 ? "y" : "ies")} missing");
        if (edited > 0) return 1;
        if (ok + legacy == 0) return 3;
        return 0;
    }

    // The split at merge 0 once more, and every new child whose island count is not one, by name. The row's own
    // message counts them; a count cannot be investigated, a name can.
    static IEnumerable<string> IslandDetail(byte[] bytes)
    {
        var lines = new List<string>();
        try
        {
            var unique = GlbDisconnectedParts.UniqueNodeNames(bytes);
            if (unique.Bytes != null) bytes = unique.Bytes;
            var parts = GlbDisconnectedParts.Analyze(bytes, 0);
            var multi = new HashSet<int>(parts.Where(p => p.Blocked == null && p.Islands > 1).Select(p => p.NodeIndex));
            if (multi.Count == 0) { lines.Add("(no multi-island node to split)"); return lines; }
            var split = GlbDisconnectedParts.Split(bytes, multi, 0);
            var after = GlbDisconnectedParts.Analyze(split.Bytes, 0);
            var before = new HashSet<string>(parts.Select(p => p.NodeName ?? ""), StringComparer.Ordinal);
            var bad = after.Where(p => !before.Contains(p.NodeName ?? "") && p.Blocked == null && p.Islands != 1)
                           .OrderByDescending(p => p.Islands).ThenBy(p => p.NodeName, StringComparer.Ordinal).ToList();
            lines.Add($"children not one island: {bad.Count} of {after.Count(p => !before.Contains(p.NodeName ?? ""))} new nodes");
            foreach (var p in bad.Take(40))
                lines.Add($"  {p.NodeName}: islands={p.Islands} tris={p.Triangles}");
            if (bad.Count > 40) lines.Add($"  … and {bad.Count - 40} more");
            // the measure behind the count (GlbDisconnectedParts.ExplainIslands, 2026-10-12; by reflection so a checkout
            // from before it still compiles): the first three children, and the node each came from, once
            var explain = typeof(GlbDisconnectedParts).GetMethod("ExplainIslands", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            if (explain != null)
            {
                var parentsDone = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in bad.Take(3))
                {
                    lines.Add("  " + (string)explain.Invoke(null, new object[] { split.Bytes, p.NodeName, 12 }));
                    string parent = System.Text.RegularExpressions.Regex.Replace(p.NodeName ?? "", @"_Part_\d{3}$", "");
                    if (parent.Length > 0 && parentsDone.Add(parent))
                        lines.Add("  from " + (string)explain.Invoke(null, new object[] { bytes, parent, 6 }));
                }
            }
        }
        catch (Exception ex) { lines.Add("(detail failed: " + ex.GetType().Name + ": " + ex.Message + ")"); }
        return lines;
    }

    static void Note(string progress, string line)
    {
        if (string.IsNullOrEmpty(progress)) return;
        try { File.AppendAllText(progress, $"{DateTime.Now:HH:mm}  drill  workshop-golden {line}\n"); } catch { }
    }
}
