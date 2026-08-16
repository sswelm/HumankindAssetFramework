// bindcheck — HEADLESS validation of GameBinding's reflection catalog against a Humankind build's assemblies,
// WITHOUT launching the game. Reads Patches/GameBinding.cs directly (so it's always in sync with the catalog — no
// manifest to go stale), and checks every catalogued type + member exists in the game's Managed DLLs via
// MetadataLoadContext (reflection-only load: it inspects metadata without executing, so Unity's native deps and
// static ctors are irrelevant). Mirrors GameBinding's own resolution: FQN with fallbacks, a simple-name scan for
// namespace-less entries, nested types via '+', and a base-chain member walk. Exit 0 = all resolved, 1 = drift, 2 = bad args.
//
//   bindcheck <GameBinding.cs> <Humankind .../Humankind_Data/Managed>
//
// This is the headless half of the reflection-drift net: run it on a NEW game build and it names exactly which
// bindings that build broke, before anyone launches the game. See docs/Framework-Review.md (A5) + Testing.md.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

static class Program
{
    static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: bindcheck <GameBinding.cs> <Managed dir>");
            return 2;
        }
        string gbPath = args[0], managed = args[1];
        if (!File.Exists(gbPath)) { Console.Error.WriteLine("not found: " + gbPath); return 2; }
        if (!Directory.Exists(managed)) { Console.Error.WriteLine("not a dir: " + managed); return 2; }

        // --- parse the catalog out of GameBinding.cs ---
        string src = File.ReadAllText(gbPath);
        string noComments = Regex.Replace(src, @"//[^\n]*", "");   // members/FQNs never contain '//', so this is safe

        // accessor -> [FQN fallbacks]   from   internal static Type X => Cached("A", "B", ...);
        var accessorFqns = new Dictionary<string, List<string>>();
        foreach (Match m in Regex.Matches(noComments, @"static\s+Type\s+(\w+)\s*=>\s*Cached\(([^)]*)\)"))
        {
            var names = Regex.Matches(m.Groups[2].Value, "\"([^\"]+)\"").Select(x => x.Groups[1].Value).ToList();
            if (names.Count > 0) accessorFqns[m.Groups[1].Value] = names;
        }

        // Deps: accessor -> [members]   from the Catalog block's   new Dep(Accessor, nameof(Accessor), "m1", "m2", ...)
        var deps = new List<(string accessor, List<string> members)>();
        foreach (var chunk in ExtractDeps(noComments))
        {
            var acc = Regex.Match(chunk, @"^\s*(\w+)");
            if (!acc.Success) continue;
            var afterNameof = Regex.Match(chunk, @"nameof\(\s*\w+\s*\)(.*)$", RegexOptions.Singleline);
            string tail = afterNameof.Success ? afterNameof.Groups[1].Value : "";
            var members = Regex.Matches(tail, "\"([^\"]+)\"").Select(x => x.Groups[1].Value).ToList();
            deps.Add((acc.Groups[1].Value, members));
        }
        if (deps.Count == 0) { Console.Error.WriteLine("parsed 0 Dep entries — GameBinding.cs shape changed?"); return 2; }

        // --- load the game's assemblies for reflection-only metadata inspection ---
        var dlls = Directory.GetFiles(managed, "*.dll");
        var resolver = new PathAssemblyResolver(dlls);
        using var mlc = new MetadataLoadContext(resolver);
        var asms = new List<Assembly>();
        foreach (var d in dlls) { try { asms.Add(mlc.LoadFromAssemblyPath(d)); } catch { /* unloadable dll — skip */ } }

        Type Resolve(IEnumerable<string> fqns)
        {
            foreach (var name in fqns)
            {
                foreach (var a in asms) { Type t = null; try { t = a.GetType(name, false); } catch { } if (t != null) return t; }
                if (!name.Contains('.') && !name.Contains('+'))   // simple-name scan (game types the runtime resolves by Type.Name)
                    foreach (var a in asms) { try { var t = a.GetTypes().FirstOrDefault(x => x.Name == name); if (t != null) return t; } catch { } }
            }
            return null;
        }
        bool MemberExists(Type t, string member)
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            for (var bt = t; bt != null; )
            {
                try { if (bt.GetMember(member, flags).Length > 0) return true; } catch { }
                Type next = null; try { next = bt.BaseType; } catch { }
                bt = next;
            }
            return false;
        }

        // --- validate ---
        int typesMissing = 0, membersMissing = 0;
        var lines = new List<string>();
        foreach (var (accessor, members) in deps)
        {
            var fqns = accessorFqns.TryGetValue(accessor, out var f) ? f : new List<string> { accessor };
            var type = Resolve(fqns);
            if (type == null) { typesMissing++; lines.Add($"[MISSING TYPE]    {accessor}  ({string.Join(" | ", fqns)})"); continue; }
            var miss = members.Where(mm => !MemberExists(type, mm)).ToList();
            if (miss.Count > 0) { membersMissing += miss.Count; lines.Add($"[MISSING MEMBER]  {accessor}: {string.Join(", ", miss)}"); }
        }

        Console.WriteLine($"bindcheck: {deps.Count - typesMissing}/{deps.Count} types | {membersMissing} member(s) missing | managed={managed}");
        foreach (var l in lines) Console.WriteLine("  " + l);
        if (typesMissing == 0 && membersMissing == 0) { Console.WriteLine("OK - every catalogued binding resolves against this build."); return 0; }
        Console.WriteLine("DRIFT - the game build is missing the binding(s) above (game update? re-verify the catalog).");
        return 1;
    }

    // Yield each `new Dep( ... )` argument string from the Catalog block, matching parens with balance so nested
    // nameof(...) / commas inside don't split an entry.
    static IEnumerable<string> ExtractDeps(string text)
    {
        int cat = text.IndexOf("Dep[] Catalog", StringComparison.Ordinal);
        if (cat < 0) yield break;
        string s = text.Substring(cat);
        int i = 0;
        while ((i = s.IndexOf("new Dep(", i, StringComparison.Ordinal)) >= 0)
        {
            int start = i + "new Dep(".Length, depth = 1, j = start;
            for (; j < s.Length && depth > 0; j++)
            {
                if (s[j] == '(') depth++;
                else if (s[j] == ')') depth--;
            }
            yield return s.Substring(start, Math.Max(0, j - start - 1));
            i = j;
        }
    }
}
