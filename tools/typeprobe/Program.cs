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
        // --find <substring>: every type, method, field, property or event whose name contains it (case-insensitive),
        // with the declaring type and the method signature — for locating a SEAM (e.g. "LoadingCompleted") before a hook
        // is written against it (2026-08-21, the end-of-loading smoke tier).
        if (args[1] == "--find" && args.Length >= 3)
        {
            var needle = args[2];
            int hits = 0;
            foreach (var a in asms)
            {
                Type[] types; try { types = a.GetTypes(); } catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(x => x != null).ToArray(); } catch { continue; }
                foreach (var t in types)
                {
                    try
                    {
                        if (t.FullName != null && t.FullName.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) { Console.WriteLine($"type    {t.FullName}  [{a.GetName().Name}]"); hits++; }
                        foreach (var m in t.GetMethods(F))
                            if (m.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                            { Console.WriteLine($"method  {t.FullName}::{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))}){(m.IsStatic ? " [static]" : "")}{(m.IsPublic ? "" : " [non-public]")}"); hits++; }
                        foreach (var f in t.GetFields(F)) if (f.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) { Console.WriteLine($"field   {t.FullName}::{f.Name} : {f.FieldType.Name}"); hits++; }
                        foreach (var p in t.GetProperties(F)) if (p.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) { Console.WriteLine($"prop    {t.FullName}::{p.Name} : {p.PropertyType.Name}"); hits++; }
                        foreach (var e in t.GetEvents(F)) if (e.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) { Console.WriteLine($"event   {t.FullName}::{e.Name} : {e.EventHandlerType?.Name}"); hits++; }
                    }
                    catch { }
                }
            }
            Console.WriteLine($"-- {hits} hit(s) for '{needle}'");
            return 0;
        }
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
