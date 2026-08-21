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
        // accessor -> derivation expression   from   internal static Type X => CachedDerived("X", () => <expr>);
        // (the A5/A6 DERIVED types — evaluated below along the same ElementType / FieldOrPropType / MethodParamType chain
        // the runtime walks; until 2026-08-21 these fell through to a bare-name lookup and 7 of 12 false-positived.)
        var accessorDerived = new Dictionary<string, string>();
        foreach (Match m in Regex.Matches(noComments, @"static\s+Type\s+(\w+)\s*=>\s*CachedDerived\(\s*""\w+""\s*,\s*\(\)\s*=>\s*(.+?)\);"))
            accessorDerived[m.Groups[1].Value] = m.Groups[2].Value.Trim();

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

        // --- derived-type evaluator: mirrors GameBinding.FieldOrPropType / ElementType / MethodParamType over MLC types ---
        var memberFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        Type FieldOrPropType(Type t, string member)
        {
            for (var cur = t; cur != null; )
            {
                try { var f = cur.GetField(member, memberFlags); if (f != null) return f.FieldType; } catch { }
                try { var p = cur.GetProperty(member, memberFlags); if (p != null) return p.PropertyType; } catch { }
                Type next = null; try { next = cur.BaseType; } catch { }
                cur = next;
            }
            return null;
        }
        Type ElementType(Type t)
        {
            if (t == null) return null;
            try { if (t.IsArray) return t.GetElementType(); if (t.IsGenericType) return t.GetGenericArguments().FirstOrDefault(); } catch { }
            return null;
        }
        Type MethodParamType(Type t, string method, int idx)
        {
            for (var cur = t; cur != null; )
            {
                try { foreach (var m in cur.GetMethods(memberFlags)) if (m.Name == method && m.GetParameters().Length > idx) return m.GetParameters()[idx].ParameterType; } catch { }
                Type next = null; try { next = cur.BaseType; } catch { }
                cur = next;
            }
            return null;
        }
        List<string> SplitArgs(string s)   // top-level comma split (nested calls keep their commas)
        {
            var parts = new List<string>(); int depth = 0, start = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '(') depth++; else if (s[i] == ')') depth--;
                else if (s[i] == ',' && depth == 0) { parts.Add(s.Substring(start, i - start).Trim()); start = i + 1; }
            }
            parts.Add(s.Substring(start).Trim());
            return parts;
        }
        string Unquote(string s) => s.Trim().Trim('"');
        var memo = new Dictionary<string, Type>();
        Type Eval(string expr)
        {
            expr = expr.Trim();
            string Inner(string call) => expr.Substring(call.Length, expr.Length - call.Length - 1);   // strip "Name(" ... ")"
            if (expr.StartsWith("ElementType(")) return ElementType(Eval(Inner("ElementType(")));
            if (expr.StartsWith("FieldOrPropType(")) { var a = SplitArgs(Inner("FieldOrPropType(")); return a.Count == 2 ? FieldOrPropType(Eval(a[0]), Unquote(a[1])) : null; }
            if (expr.StartsWith("MethodParamType(")) { var a = SplitArgs(Inner("MethodParamType(")); return a.Count == 3 && int.TryParse(a[2], out var ix) ? MethodParamType(Eval(a[0]), Unquote(a[1]), ix) : null; }
            return ResolveAccessor(expr);   // a bare accessor name
        }
        Type ResolveAccessor(string acc)
        {
            if (memo.TryGetValue(acc, out var hit)) return hit;
            memo[acc] = null;   // cycle guard
            Type t = accessorDerived.TryGetValue(acc, out var dx) ? Eval(dx)
                   : Resolve(accessorFqns.TryGetValue(acc, out var f) ? f : new List<string> { acc });
            memo[acc] = t;
            return t;
        }

        // --- validate ---
        int typesMissing = 0, membersMissing = 0;
        var lines = new List<string>();
        foreach (var (accessor, members) in deps)
        {
            var type = ResolveAccessor(accessor);
            string how = accessorDerived.TryGetValue(accessor, out var dx) ? "derived: " + dx
                       : accessorFqns.TryGetValue(accessor, out var f) ? string.Join(" | ", f) : accessor;
            if (type == null) { typesMissing++; lines.Add($"[MISSING TYPE]    {accessor}  ({how})"); continue; }
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
