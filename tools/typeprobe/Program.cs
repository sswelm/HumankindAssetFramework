// typeprobe — HEADLESS dump of a game type's fields/properties (name, type, kind) from the Managed DLLs via
// MetadataLoadContext, so a member's real shape is known before a launch. Sibling of bindcheck.
//   typeprobe <Managed dir> <TypeName|simple name> [more types...]
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

static class Program
{
    static int Main(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: typeprobe <Managed dir> <type> [type...]"); return 2; }
        var dlls = Directory.GetFiles(args[0], "*.dll");
        using var mlc = new MetadataLoadContext(new PathAssemblyResolver(dlls));
        var asms = new List<Assembly>();
        foreach (var d in dlls) { try { asms.Add(mlc.LoadFromAssemblyPath(d)); } catch { } }
        const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var name in args.Skip(1))
        {
            Type t = null;
            foreach (var a in asms) { try { t = a.GetType(name, false); } catch { } if (t != null) break; }
            if (t == null && !name.Contains('.'))
                foreach (var a in asms) { try { t = a.GetTypes().FirstOrDefault(x => x.Name == name); } catch { } if (t != null) break; }
            if (t == null) { Console.WriteLine($"== {name}: NOT FOUND"); continue; }
            Console.WriteLine($"== {t.FullName}  ({(t.IsValueType ? "struct" : "class")}, base {t.BaseType?.Name})");
            for (var cur = t; cur != null && cur.FullName != "System.Object" && cur.FullName != "System.ValueType"; cur = cur.BaseType)
            {
                foreach (var f in cur.GetFields(F)) Console.WriteLine($"   field {f.FieldType.Name,-28} {f.Name}{(f.IsStatic ? "  [static]" : "")}");
                foreach (var p in cur.GetProperties(F)) Console.WriteLine($"   prop  {p.PropertyType.Name,-28} {p.Name}");
            }
        }
        return 0;
    }
}
