using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using HarmonyLib;
using HumankindAssetFramework;

// Drive the TechRequireAll hooks on the installed game assemblies without launching the game.
// The drill runs the game's state update loop, locked enqueue and cascading removal with the live simulation
// reads and leaf queue mutations stubbed.
static class TechRequireAllGameDrill
{
    const BindingFlags ALL = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static string Managed;   // the game's Humankind_Data/Managed folder: argument 1
    static int fails;
    static void Check(string what, bool ok, string detail = "") { Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what + (detail != "" ? "   [" + detail + "]" : "")); if (!ok) fails++; }

    static Type tDept, tTech, tDef, tQueue, tSS, tEnq;
    static FieldInfo fTechs, fData, fQueue;
    static object dept; static Array data;

    static bool AddStub(object __instance, int technologyIndex, object[] __args)
    {
        var t = data.GetValue(technologyIndex); tTech.GetField("IsInQueue").SetValue(t, true); data.SetValue(t, technologyIndex);
        var list = (List<int>)tQueue.GetField("TechnologyIndices").GetValue(fQueue.GetValue(__instance));
        if (Convert.ToInt32(__args[1]) == 1) list.Add(technologyIndex); else list.Insert(0, technologyIndex);
        return false;
    }
    static bool MoveStub(object __instance, int fromIndex, int toIndex)
    {
        var list = (List<int>)tQueue.GetField("TechnologyIndices").GetValue(fQueue.GetValue(__instance));
        int v = list[fromIndex]; list.RemoveAt(fromIndex); if (toIndex >= list.Count) list.Add(v); else list.Insert(toIndex, v);
        return false;
    }

    static void RemoveStub(object department, int indexInQueue)
    {
        var list = (List<int>)tQueue.GetField("TechnologyIndices").GetValue(fQueue.GetValue(department));
        int technologyIndex = list[indexInQueue];   // like the game's leaf method, this requires a valid queue index
        list.RemoveAt(indexInQueue);
        var t = data.GetValue(technologyIndex);
        tTech.GetField("IsInQueue").SetValue(t, false);
        data.SetValue(t, technologyIndex);
    }

    // Substitute the leaf body so the production removal prefix still runs before it.
    static IEnumerable<CodeInstruction> StubRemoveBody(IEnumerable<CodeInstruction> instructions)
    {
        yield return new CodeInstruction(OpCodes.Ldarg_0);
        yield return new CodeInstruction(OpCodes.Ldarg_1);
        yield return new CodeInstruction(OpCodes.Call, typeof(TechRequireAllGameDrill).GetMethod(nameof(RemoveStub), ALL));
        yield return new CodeInstruction(OpCodes.Ret);
    }

    static bool StubEraCheck(ref bool __result)
    {
        __result = true;
        return false;
    }

    // Keep the game's state loop, including its prerequisite call and state write. Stub the frame read
    // and stop at the empire counter updates, which need the live simulation.
    static IEnumerable<CodeInstruction> StubStateUpdateEnvironment(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        int empire = code.FindIndex(i => i.opcode == OpCodes.Ldfld && i.operand is FieldInfo f && f.Name == "majorEmpire");
        if (empire < 2 || code[empire - 1].opcode != OpCodes.Ldarg_0 || code[empire - 2].opcode != OpCodes.Ldloc_0)
            throw new InvalidOperationException("State update's empire counter block was not found");
        for (int i = 0; i < empire - 2; i++)
        {
            var instruction = code[i];
            if (instruction.opcode == OpCodes.Ldsfld && instruction.operand is FieldInfo frame && frame.Name == "Frame")
            {
                instruction.opcode = OpCodes.Ldc_I4_0;
                instruction.operand = null;
            }
            yield return instruction;
        }
        yield return new CodeInstruction(OpCodes.Ret);
    }

    static object SS(string s) => Activator.CreateInstance(tSS, new object[] { s });
    static string[] names;
    static void Build(params (string name, string[] pre, int state)[] techs)
    {
        names = techs.Select(t => t.name).ToArray();
        data = Array.CreateInstance(tTech, techs.Length);
        for (int i = 0; i < techs.Length; i++)
        {
            var def = FormatterServices.GetUninitializedObject(tDef);
            SetName(def, techs[i].name);
            var fPre = tDef.GetField("TechnologyPrerequisite", ALL); var pre = fPre.GetValue(def);
            var arr = Array.CreateInstance(tSS, techs[i].pre.Length);
            for (int k = 0; k < arr.Length; k++) arr.SetValue(SS(techs[i].pre[k]), k);
            pre.GetType().GetField("TechnologyNames", ALL).SetValue(pre, arr); fPre.SetValue(def, pre);
            var t = Activator.CreateInstance(tTech);
            tTech.GetField("TechnologyDefinition").SetValue(t, def);
            tTech.GetField("TechnologyState").SetValue(t, Enum.ToObject(tTech.GetField("TechnologyState").FieldType, techs[i].state));
            data.SetValue(t, i);
        }
        dept = FormatterServices.GetUninitializedObject(tDept);
        var awf = Activator.CreateInstance(fTechs.FieldType); fData.SetValue(awf, data); fTechs.SetValue(dept, awf);
        var q = FormatterServices.GetUninitializedObject(tQueue);
        tQueue.GetField("TechnologyIndices").SetValue(q, new List<int>());
        var fc = tQueue.GetField("QueuedTechnologyConstraints"); fc.SetValue(q, Activator.CreateInstance(fc.FieldType));
        fQueue.SetValue(dept, q);
    }
    static void SetName(object def, string name)
    {
        for (var c = def.GetType(); c != null; c = c.BaseType)
        {
            var p = c.GetProperty("Name", ALL | BindingFlags.DeclaredOnly);
            if (p != null && p.PropertyType == tSS)
            {
                var set = p.GetSetMethod(true);
                if (set != null) { set.Invoke(def, new[] { SS(name) }); return; }
                var bf = c.GetField("<Name>k__BackingField", ALL);
                if (bf != null) { bf.SetValue(def, SS(name)); return; }
            }
            var f = c.GetField("Name", ALL | BindingFlags.DeclaredOnly);
            if (f != null && f.FieldType == tSS) { f.SetValue(def, SS(name)); return; }
        }
        throw new Exception("cannot set Name");
    }
    static int Idx(string n) => Array.IndexOf(names, n);
    static void SetState(string n, int s) { var t = data.GetValue(Idx(n)); tTech.GetField("TechnologyState").SetValue(t, Enum.ToObject(tTech.GetField("TechnologyState").FieldType, s)); data.SetValue(t, Idx(n)); }
    static bool GameCheck(string n)
    {
        AccessTools.Method(tDept, "UpdateTechnologiesState").Invoke(dept, null);
        return Convert.ToInt32(tTech.GetField("TechnologyState").GetValue(data.GetValue(Idx(n)))) == 2;
    }

    static void SharePrerequisites(string from, string to)
    {
        var definition = tTech.GetField("TechnologyDefinition");
        var prerequisite = tDef.GetField("TechnologyPrerequisite", ALL);
        var source = definition.GetValue(data.GetValue(Idx(from)));
        var destination = definition.GetValue(data.GetValue(Idx(to)));
        prerequisite.SetValue(destination, prerequisite.GetValue(source));
    }
    static void GameQueue(string n, int pos) => AccessTools.Method(tDept, "AddLockedTechnologyToQueue").Invoke(dept, new object[] { Idx(n), Enum.ToObject(tEnq, pos) });
    static void GameAdd(string n) => AccessTools.Method(tDept, "AddTechnologyToQueue").Invoke(dept, new object[] { Idx(n), Enum.ToObject(tEnq, 1) });
    static string Queue() => string.Join(",", ((List<int>)tQueue.GetField("TechnologyIndices").GetValue(fQueue.GetValue(dept))).Select(i => names[i]));
    static string Constraints()
    {
        var l = (IList)tQueue.GetField("QueuedTechnologyConstraints").GetValue(fQueue.GetValue(dept));
        var s = new List<string>();
        foreach (var c in l) s.Add(names[(int)c.GetType().GetField("ParentTechnologyIndex").GetValue(c)] + ">" + names[(int)c.GetType().GetField("ChildTechnologyIndex").GetValue(c)]);
        s.Sort(StringComparer.Ordinal); return string.Join(" ", s);
    }
    // A queue as a SAVE made with a list carries it: A,B,T,X queued, T behind two constraints (A>T, B>T) and B behind A.
    // Built by hand, no hook involved: the game's own queueing never makes a technology the child of two constraints.
    static void SavedOverlappingQueue()
    {
        Build(("A", new string[0], 2), ("B", new[] { "A" }, 1), ("T", new[] { "B", "A" }, 1), ("X", new string[0], 2));
        foreach (var n in new[] { "A", "B", "T", "X" }) GameAdd(n);
        var fc = tQueue.GetField("QueuedTechnologyConstraints");
        var l = (IList)fc.GetValue(fQueue.GetValue(dept));
        var tc = fc.FieldType.GetGenericArguments()[0];
        // In the ORDER the plan writes them (measured: queueing T below leaves exactly this list). The order decides:
        // the cascade scans from the end, so A puts T then B on its work list, B is taken first and puts T on again.
        // With A>T first in the list T is taken before B and nothing is visited twice.
        foreach (var (p, c) in new[] { ("B", "T"), ("A", "B"), ("A", "T") })
        {
            object k = Activator.CreateInstance(tc);
            tc.GetField("ParentTechnologyIndex").SetValue(k, Idx(p));
            tc.GetField("ChildTechnologyIndex").SetValue(k, Idx(c));
            l.Add(k);
        }
    }
    // null = the cascade ran to its end; otherwise the exception the game's code threw
    static Exception RemoveCascadeAt(int indexInQueue)
    {
        try { AccessTools.Method(tDept, "RemoveTechnologyAndChildrenFromQueueAt").Invoke(dept, new object[] { indexInQueue }); return null; }
        catch (TargetInvocationException ex) { return ex.InnerException ?? ex; }
    }

    // A,B roots; T=A+B (LISTED); U=A+B (not listed); C=T; X root. States: 1 locked by technology, 2 available, 3 completed.
    static void Tree(int a, int b, int t) => Build(("A", new string[0], a), ("B", new string[0], b), ("T", new[] { "A", "B" }, t), ("U", new[] { "A", "B" }, 1), ("C", new[] { "T" }, 1), ("X", new string[0], 2));

    static int Main(string[] args)
    {
        if (args.Length < 1 || !Directory.Exists(args[0])) { Console.WriteLine("usage: <Humankind_Data/Managed>"); return 2; }
        Managed = args[0];
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            var f = Path.Combine(Managed, new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(f) ? Assembly.LoadFrom(f) : null;
        };
        return Run();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static int Run()
    {
        Console.WriteLine("runtime: " + (Type.GetType("Mono.Runtime") != null ? "Mono" : ".NET Framework") + (IntPtr.Size == 8 ? " 64-bit" : " 32-bit"));
        var fp = Assembly.LoadFrom(Path.Combine(Managed, "Amplitude.Mercury.Firstpass.dll"));
        Assembly.LoadFrom(Path.Combine(Managed, "Amplitude.Mercury.Data.dll"));
        tDept = fp.GetType("Amplitude.Mercury.Simulation.DepartmentOfScience", true);
        tQueue = fp.GetType("Amplitude.Mercury.Simulation.TechnologyQueue", true);
        fTechs = tDept.GetField("Technologies", ALL); fData = fTechs.FieldType.GetField("Data"); fQueue = tDept.GetField("TechnologyQueue", ALL);
        tTech = fData.FieldType.GetElementType(); tDef = tTech.GetField("TechnologyDefinition").FieldType;
        tSS = tDef.GetField("TechnologyPrerequisite", ALL).FieldType.GetField("TechnologyNames", ALL).FieldType.GetElementType();
        tEnq = AccessTools.Method(tDept, "AddTechnologyToQueue").GetParameters()[1].ParameterType;

        // The script's control for a runtime that crashes while EXITING with the game's assemblies loaded (the Mono
        // bundled with the Unity editor does, before any hook or any HAF code has run): stop here, having only loaded them.
        if (Environment.GetEnvironmentVariable("HAF_DRILL_STAGE") == "load") { Console.WriteLine("LOADED"); return 0; }
        var log = new BepInEx.Logging.ManualLogSource("drill");
        log.LogEvent += (s, e) => Console.WriteLine("      log " + e.Level + ": " + e.Data);
        Plugin.Log = log;
        // HAF_DRILL_STAGE=nolist: the same process WITHOUT a list file, for the one hook that must work without one.
        // A stage of its own because the plugin reads the list once per process.
        bool noList = Environment.GetEnvironmentVariable("HAF_DRILL_STAGE") == "nolist";
        var cfg = Path.Combine(Path.GetTempPath(), "haf-techall-drill-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(cfg);
        if (!noList) File.WriteAllText(Path.Combine(cfg, "haf_tech_requires_all.json"), "{\"technologies\":[\"T\",\"NotATech\"]}");
        typeof(BepInEx.Paths).GetProperty("ConfigPath", ALL).SetValue(null, cfg, null);

        var h = new Harmony("drill");
        h.Patch(AccessTools.Method(tDept, "AddTechnologyToQueue"), prefix: new HarmonyMethod(typeof(TechRequireAllGameDrill).GetMethod("AddStub", ALL)));
        h.Patch(AccessTools.Method(tDept, "MoveTechnologyInQueue"), prefix: new HarmonyMethod(typeof(TechRequireAllGameDrill).GetMethod("MoveStub", ALL)));
        h.Patch(AccessTools.Method(tDept, "RemoveTechnologyFromQueueAt"),
            transpiler: new HarmonyMethod(typeof(TechRequireAllGameDrill).GetMethod(nameof(StubRemoveBody), ALL)));
        h.Patch(AccessTools.Method(tDept, "IsEraValidFor"),
            prefix: new HarmonyMethod(typeof(TechRequireAllGameDrill).GetMethod(nameof(StubEraCheck), ALL)));
        h.Patch(AccessTools.Method(tDept, "UpdateTechnologiesState"),
            transpiler: new HarmonyMethod(typeof(TechRequireAllGameDrill).GetMethod(nameof(StubStateUpdateEnvironment), ALL)));

        Console.WriteLine("--- CONTROL: the game's own code, HAF hooks not applied ---");
        Tree(3, 2, 1);
        Check("vanilla: T (A done, B not) is AVAILABLE on one prerequisite", GameCheck("T"));
        Tree(2, 2, 1);
        GameQueue("C", 1);
        Check("vanilla: queueing C puts ONE parent of T in front", Queue() == "A,T,C", Queue() + " | " + Constraints());
        SavedOverlappingQueue();
        var thrown = RemoveCascadeAt(0);
        Check("vanilla: a saved queue with T behind two constraints THROWS when A is removed", thrown is ArgumentOutOfRangeException, thrown == null ? "no exception | " + Queue() : thrown.GetType().Name);

        Console.WriteLine("--- the HAF hooks, applied as the plugin applies them ---");
        foreach (var t in new[] { typeof(Hk_TechRequireAllCheck), typeof(Hk_TechRequireAllQueue), typeof(Hk_TechRequireAllRemove) })
        { var applied = h.CreateClassProcessor(t).Patch(); Check(t.Name + " patched a method", applied != null && applied.Count > 0); }

        if (noList)
        {
            Console.WriteLine("--- NO list file: the save's constraints are still there ---");
            Check("no list file: the feature is inactive", !TechRequireAll.Active);
            Tree(3, 2, 1);
            Check("no list file: T (A done, B not) is available, the game's rule", GameCheck("T"));
            SavedOverlappingQueue();
            thrown = RemoveCascadeAt(0);
            Check("no list file: removing A from the saved queue removes B and T once and keeps X",
                  thrown == null && Queue() == "X" && Constraints() == "", thrown != null ? thrown.ToString() : Queue() + " | " + Constraints());
            Console.WriteLine(fails == 0 ? "DRILL PASS" : "DRILL FAIL (" + fails + ")");
            try { Directory.Delete(cfg, true); } catch { }
            return fails == 0 ? 0 : 1;
        }

        Tree(3, 2, 1);
        Check("listed T: A done, B not -> LOCKED", !GameCheck("T"));
        Check("unlisted U: A done, B not -> available (vanilla rule kept)", GameCheck("U"));
        Check("C (one prerequisite, T not done) -> locked", !GameCheck("C"));
        SetState("B", 3);
        Check("listed T: A and B done -> available", GameCheck("T"));
        Tree(2, 2, 1);
        Check("listed T: nothing done -> locked", !GameCheck("T"));
        Tree(2, 3, 1);
        Check("listed T: only B done -> LOCKED", !GameCheck("T"));

        Tree(3, 2, 1);
        SharePrerequisites("T", "U");
        Check("shared array: listed T stays locked when the unlisted definition comes last", !GameCheck("T"));
        Check("shared array: unlisted U still unlocks on A", GameCheck("U"));

        Build(("A", new string[0], 3), ("B", new string[0], 2),
              ("U", new[] { "A", "B" }, 1), ("T", new[] { "A", "B" }, 1));
        SharePrerequisites("U", "T");
        Check("shared array: unlisted U unlocks when the listed definition comes last", GameCheck("U"));
        Check("shared array: listed T still waits for B", !GameCheck("T"));

        Tree(2, 2, 1);
        GameQueue("C", 1);
        Check("queue C at end: BOTH parents of T, then T, then C", Queue() == "A,B,T,C", Queue());
        Check("  constraints A>T B>T T>C", Constraints() == "A>T B>T T>C", Constraints());

        Tree(3, 2, 1);
        GameQueue("T", 1);
        Check("queue T (A done): only B in front", Queue() == "B,T" && Constraints() == "B>T", Queue() + " | " + Constraints());

        Tree(2, 2, 1);
        GameAdd("X");
        GameQueue("T", 0);
        Check("queue T at the BEGINNING of a queue holding X", Queue() == "A,B,T,X", Queue());
        Check("  constraints A>T B>T", Constraints() == "A>T B>T", Constraints());

        Tree(2, 2, 1);
        GameAdd("B");
        GameQueue("T", 1);
        Check("queue T with B already queued: A added, B not doubled", Queue() == "B,A,T", Queue());

        Tree(2, 2, 1);
        GameQueue("U", 1);
        Check("unlisted U: the game's own queueing runs (one parent)", Queue() == "A,U" && Constraints() == "A>U", Queue() + " | " + Constraints());

        Build(("A", new string[0], 2), ("B", new[] { "A" }, 1),
              ("T", new[] { "B", "A" }, 1), ("X", new string[0], 2));
        GameQueue("T", 1);
        GameAdd("X");
        Check("overlapping branches queue both required parents", Queue() == "A,B,T,X" && Constraints() == "A>B A>T B>T", Queue() + " | " + Constraints());
        thrown = RemoveCascadeAt(0);
        Check("removing A removes B and T once and keeps unrelated X", thrown == null && Queue() == "X" && Constraints() == "",
              thrown != null ? thrown.ToString() : Queue() + " | " + Constraints());

        Console.WriteLine(fails == 0 ? "DRILL PASS" : "DRILL FAIL (" + fails + ")");
        try { Directory.Delete(cfg, true); } catch { }
        return fails == 0 ? 0 : 1;
    }
}
