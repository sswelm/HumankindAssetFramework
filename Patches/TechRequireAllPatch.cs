// TechRequireAllPatch.cs — technologies that need ALL their prerequisites (2026-11-20).
//
// The game unlocks a technology as soon as ONE of its listed prerequisites is completed, and its data has no field to
// say otherwise (TechnologyPrerequisite is a bare list of names). The editor's "Tech Requires All" window writes the
// names of the technologies that must wait for EVERY prerequisite to haf_tech_requires_all.json in BepInEx/config;
// this file makes the game obey that list. No baked asset: remove a name and the technology is vanilla on the next
// launch. Without the file the availability and queue hooks return at their first line.
//
// ONE thing does reach the save: the ordering constraints of a queue planned here (the game serializes
// TechnologyQueue.QueuedTechnologyConstraints). A technology can then be the child of TWO constraints, which the
// game's own queueing never produces, so hook 3 below is NOT gated on the list: a save made with a list must stay
// safe after the list is emptied or switched off. With the plugin uninstalled nothing guards such a save.
//
// The lock is only half of the game's rule (decompiled 2026-11-20, DepartmentOfScience):
//   1. UpdateTechnologiesState — refine the result of the game's prerequisite check using the technology definition
//      from that call site. A listed technology stays locked until all its prerequisites are completed.
//   2. AddLockedTechnologyToQueue — what a click (or the AI) on a LOCKED technology runs. Research is paid into the
//      queue in order with no look at a technology's state, so the queue's order is the real guard, and vanilla puts
//      ONE chain of parents in front (one parent per level). Prefix: when a listed technology is in that tree, queue
//      every missing parent in front instead, with the game's own queue methods and its own ordering constraints.
//   3. RemoveTechnologyFromQueueAt — the game's removal cascade (RemoveTechnologyAndChildrenFromQueueAt) puts a
//      technology on its work list once per constraint that names it as the child. With two such constraints the
//      second visit finds it gone and calls this with -1, which throws. Prefix: -1 is skipped, ALWAYS (see above);
//      unpatched that call can only throw, so skipping it changes nothing the game could have done.
//
// The hooks run on the SIMULATION thread. Everything they share sits behind one lock; the member lookups go through the
// plugin's concurrent reflection cache. Every game member is resolved once and a missing one THROWS by name, so a
// game update shows as one warning and vanilla behaviour, never as a silently wrong lock.
//
// Not covered: a technology already sitting in a saved queue behind only one parent keeps that place (the queue is
// only planned when something is added), anything that completes a technology outside the research queue, and a save
// with such a planned queue loaded WITHOUT the plugin (see hook 3).

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using HarmonyLib;

namespace HumankindAssetFramework
{
    internal static class TechRequireAll
    {
        static readonly object gate = new object();
        static volatile bool loaded;
        [ProcessLived("the list from haf_tech_requires_all.json: read once, replaced whole, never mutated")]
        static volatile HashSet<string> listed = new HashSet<string>(StringComparer.Ordinal);
        static Bind bind;   // resolved under `gate`, read-only afterwards

        internal static bool Active
        {
            get
            {
                if (!loaded) EnsureConfig();
                return listed.Count > 0;
            }
        }

        static void EnsureConfig()
        {
            lock (gate)
            {
                if (loaded) return;
                try
                {
                    if (Plugin.TechRequireAllOn != null && !Plugin.TechRequireAllOn.Value) return;
                    var path = Path.Combine(Paths.ConfigPath, TechRequireAllRules.FileName);
                    if (!File.Exists(path)) return;
                    var names = TechRequireAllRules.ParseList(File.ReadAllText(path), out string problem);
                    if (problem.Length > 0) { Plugin.Log.LogError($"[TechAll] {TechRequireAllRules.FileName} is not used: {problem}"); return; }
                    listed = names;
                    if (names.Count > 0)
                        Plugin.Log.LogInfo($"[TechAll] {names.Count} technolog{(names.Count == 1 ? "y needs" : "ies need")} ALL prerequisites ({TechRequireAllRules.FileName})");
                }
                catch (Exception ex) { Plugin.Log.LogError($"[TechAll] {TechRequireAllRules.FileName} could not be read: " + ex); }
                finally { loaded = true; }
            }
        }

        // ---- the game's members, resolved once; a missing one throws with its name ----
        sealed class Bind
        {
            public readonly FieldInfo Technologies, Data, Definition, State, InQueue, Prerequisite, Names, Queue, Constraints, Parent, Child;
            public readonly MemberInfo Name;
            public readonly MethodInfo GetIndex, FindInQueue, Add, Move;
            public readonly Type ConstraintType, EnqueueType;

            public Bind(Type dept)
            {
                Technologies = F(dept, "Technologies");
                Data = F(Technologies.FieldType, "Data");
                var tech = Data.FieldType.GetElementType() ?? throw new InvalidOperationException("Technologies.Data is not an array");
                Definition = F(tech, "TechnologyDefinition");
                State = F(tech, "TechnologyState");
                InQueue = F(tech, "IsInQueue");
                Prerequisite = F(Definition.FieldType, "TechnologyPrerequisite");
                Names = F(Prerequisite.FieldType, "TechnologyNames");
                Name = NameMember(Definition.FieldType) ?? throw new InvalidOperationException("TechnologyDefinition.Name not found");
                Queue = F(dept, "TechnologyQueue");
                Constraints = F(Queue.FieldType, "QueuedTechnologyConstraints");
                ConstraintType = Constraints.FieldType.IsGenericType ? Constraints.FieldType.GetGenericArguments()[0]
                               : throw new InvalidOperationException("QueuedTechnologyConstraints is not a generic list");
                Parent = F(ConstraintType, "ParentTechnologyIndex");
                Child = F(ConstraintType, "ChildTechnologyIndex");
                GetIndex = M(dept, "GetTechnologyIndex");
                FindInQueue = M(dept, "FindTechnologyIndexInQueue");
                Add = M(dept, "AddTechnologyToQueue");
                Move = M(dept, "MoveTechnologyInQueue");
                EnqueueType = Add.GetParameters().Length == 2 ? Add.GetParameters()[1].ParameterType
                            : throw new InvalidOperationException("AddTechnologyToQueue no longer takes (index, position)");
            }

            // Name lives on a base class of the definition (the datatable element); property or field, whichever the game has
            static MemberInfo NameMember(Type t)
            {
                const BindingFlags declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                for (var cur = t; cur != null; cur = cur.BaseType)
                {
                    var p = cur.GetProperty("Name", declared);
                    if (p != null && p.GetIndexParameters().Length == 0) return p;
                    var f = cur.GetField("Name", declared);
                    if (f != null) return f;
                }
                return null;
            }
            static FieldInfo F(Type t, string name) =>
                UniversalInject.CachedField(t, name) ?? throw new InvalidOperationException($"{t.Name}.{name} not found");
            static MethodInfo M(Type t, string name) =>
                AccessTools.Method(t, name) ?? throw new InvalidOperationException($"{t.Name}.{name}() not found");

            public Array NamesOf(object def) => def == null ? null : Names.GetValue(Prerequisite.GetValue(def)) as Array;
            public string NameOf(object def)
            {
                if (def == null) return "";
                var value = Name is PropertyInfo p ? p.GetValue(def, null) : ((FieldInfo)Name).GetValue(def);
                return value?.ToString() ?? "";
            }
        }

        static Bind BindFor(object dept)
        {
            var b = bind;
            if (b != null) return b;
            lock (gate) return bind ?? (bind = new Bind(dept.GetType()));
        }

        // One view of one department for one call: the technology array is read fresh on every access (Technology is
        // a struct, so GetValue copies the element as it is NOW - the game's queue methods change it between reads).
        sealed class View
        {
            readonly Bind b; readonly object dept; readonly Array techs;
            readonly Dictionary<int, int[]> prereqMemo = new Dictionary<int, int[]>();
            public View(Bind b, object dept)
            {
                this.b = b; this.dept = dept;
                techs = b.Data.GetValue(b.Technologies.GetValue(dept)) as Array ?? throw new InvalidOperationException("Technologies.Data is null");
            }
            public int Count => techs.Length;
            public object Def(int i) => b.Definition.GetValue(techs.GetValue(i));
            public TechRequireAllRules.State State(int i) => (TechRequireAllRules.State)Convert.ToInt32(b.State.GetValue(techs.GetValue(i)));
            public bool InQueue(int i) => (bool)b.InQueue.GetValue(techs.GetValue(i));
            public bool Listed(int i) => listed.Contains(b.NameOf(Def(i)));
            public int[] IndicesOf(Array names)
            {
                var idx = new int[names?.Length ?? 0];
                for (int k = 0; k < idx.Length; k++) idx[k] = (int)b.GetIndex.Invoke(dept, new[] { names.GetValue(k) });
                return idx;
            }
            public int[] Prerequisites(int i)
            {
                if (!prereqMemo.TryGetValue(i, out var idx)) prereqMemo[i] = idx = IndicesOf(b.NamesOf(Def(i)));
                return idx;
            }
        }

        static void ReportList(Bind b, object dept)
        {
            if (!Plugin.Once("techall:census")) return;
            var v = new View(b, dept);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var single = new List<string>();
            for (int i = 0; i < v.Count; i++)
            {
                var def = v.Def(i);
                string name = b.NameOf(def);
                if (!listed.Contains(name)) continue;
                seen.Add(name);
                if ((b.NamesOf(def)?.Length ?? 0) < 2) single.Add(name);
            }
            var missing = listed.Where(n => !seen.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            if (missing.Length > 0) Plugin.Log.LogWarning($"[TechAll] {missing.Length} listed name(s) are not technologies in this game (no effect): {string.Join(", ", missing)}");
            if (single.Count > 0) Plugin.Log.LogWarning($"[TechAll] {single.Count} listed technolog{(single.Count == 1 ? "y has" : "ies have")} fewer than two prerequisites (no effect): {string.Join(", ", single)}");
        }

        /// <summary>The availability hook: the game says "available"; true = the ALL rule says it is still locked.</summary>
        internal static bool StillLocked(object dept, object definition)
        {
            var b = BindFor(dept);
            ReportList(b, dept);
            if (!listed.Contains(b.NameOf(definition))) return false;
            var names = b.NamesOf(definition);
            if (names == null || names.Length < 2) return false;
            var v = new View(b, dept);
            return !TechRequireAllRules.AllCompleted(v.IndicesOf(names), v.State);
        }

        /// <summary>
        /// The queue hook. True = handled here (the game's own code must not run); false = no listed technology in
        /// this tree, the game's code runs untouched.
        /// </summary>
        internal static bool TryQueue(object dept, int target, bool atEnd)
        {
            var b = BindFor(dept);
            var v = new View(b, dept);
            if (target < 0 || target >= v.Count || v.State(target) != TechRequireAllRules.State.LockedByTechnology) return false;
            var plan = TechRequireAllRules.PlanQueue(target, v.Prerequisites, v.State, v.InQueue, v.Listed);
            if (!plan.UsesRequireAll) return false;
            string targetName = b.NameOf(v.Def(target));
            if (!plan.Ok)
            {
                Plugin.LogOnceWarning("techall:plan:" + targetName, $"[TechAll] '{targetName}' was not queued: {plan.Why}.");
                return true;
            }
            var constraints = b.Constraints.GetValue(b.Queue.GetValue(dept)) as IList ?? throw new InvalidOperationException("QueuedTechnologyConstraints is null");
            object begin = Enum.ToObject(b.EnqueueType, 0), end = Enum.ToObject(b.EnqueueType, 1);
            // From here the queue changes: a failure must NOT fall back to the game's code (it would queue the target twice).
            try
            {
                if (atEnd)
                {
                    foreach (int p in plan.Order) if (!v.InQueue(p)) b.Add.Invoke(dept, new[] { (object)p, end });
                    b.Add.Invoke(dept, new[] { (object)target, end });
                }
                else
                {
                    b.Add.Invoke(dept, new[] { (object)target, begin });
                    for (int k = plan.Order.Count - 1; k >= 0; k--)   // nearest parent first: each lands in front of the last
                    {
                        int p = plan.Order[k];
                        if (v.InQueue(p)) b.Move.Invoke(dept, new[] { b.FindInQueue.Invoke(dept, new[] { (object)p }), (object)0 });
                        else b.Add.Invoke(dept, new[] { (object)p, begin });
                    }
                }
                foreach (var e in plan.Edges)
                {
                    object c = Activator.CreateInstance(b.ConstraintType);
                    b.Parent.SetValue(c, e.Key);
                    b.Child.SetValue(c, e.Value);
                    constraints.Add(c);
                }
                Plugin.DiagOnce("techall:queued:" + targetName, $"[TechAll] '{targetName}' queued behind {plan.Order.Count} prerequisite(s), {plan.Edges.Count} ordering constraint(s).");
            }
            catch (Exception ex)
            {
                Plugin.LogOnceWarning("techall:queue:" + ex.GetType().Name, $"[TechAll] queueing '{targetName}' failed part-way; the queue is left as it is (first occurrence): " + ex);
            }
            return true;
        }
    }

    [HarmonyPatch] internal static class Hk_TechRequireAllCheck
    {
        static MethodBase TargetMethod()
        {
            var t = GameBinding.DepartmentOfScience;
            var m = t != null ? AccessTools.Method(t, "UpdateTechnologiesState") : null;
            if (m == null) Plugin.Log.LogWarning("[TechAll] NOT found: DepartmentOfScience.UpdateTechnologiesState — listed technologies unlock on ANY prerequisite (vanilla)");
            return m;
        }

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var check = AccessTools.Method(GameBinding.DepartmentOfScience, "CheckTechnologyPrerequisite");
            var field = AccessTools.Field(GameBinding.TechnologyDefinition, "TechnologyPrerequisite");
            var code = instructions.ToList();
            var calls = code.Select((instruction, index) => (instruction, index))
                            .Where(x => check != null && x.instruction.Calls(check)).Select(x => x.index).ToArray();
            if (field == null || calls.Length != 1 || calls[0] == 0)
                throw new InvalidOperationException("Expected one prerequisite check in DepartmentOfScience.UpdateTechnologiesState");
            int call = calls[0];
            var load = code[call - 1];
            if (load.opcode != OpCodes.Ldfld || !Equals(load.operand, field) || code[call].labels.Count != 0 ||
                load.blocks.Count != 0 || code[call].blocks.Count != 0 || field.DeclaringType.IsValueType)
                throw new InvalidOperationException("Expected a technology definition field load before the prerequisite check");

            // Keep the exact definition on a local before the game's call consumes its prerequisite struct.
            // Another definition can share that same struct's array but have a different configured rule.
            var definition = generator.DeclareLocal(field.DeclaringType);
            var applyRequirement = ((Func<bool, object, object, bool>)ApplyRequirement).Method;
            var copy = new CodeInstruction(OpCodes.Dup);
            copy.labels.AddRange(load.labels);
            load.labels.Clear();
            code.InsertRange(call + 1, new[] {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldloc, definition),
                new CodeInstruction(OpCodes.Call, applyRequirement)
            });
            code.InsertRange(call - 1, new[] { copy, new CodeInstruction(OpCodes.Stloc, definition) });
            Plugin.Log.LogInfo("[TechAll] hooked DepartmentOfScience.UpdateTechnologiesState (listed technologies need ALL prerequisites)");
            return code;
        }

        static bool ApplyRequirement(bool available, object department, object definition)
        {
            if (!available || !TechRequireAll.Active) return available;
            try { return !TechRequireAll.StillLocked(department, definition); }
            catch (Exception ex)
            {
                Plugin.LogOnceWarning("techall:check:" + ex.GetType().Name, "[TechAll] availability check failed — the game's own answer stands (first occurrence): " + ex);
                return available;
            }
        }
    }

    [HarmonyPatch] internal static class Hk_TechRequireAllQueue
    {
        static MethodBase TargetMethod()
        {
            var t = GameBinding.DepartmentOfScience;
            var m = t != null ? AccessTools.Method(t, "AddLockedTechnologyToQueue") : null;
            if (m != null) Plugin.Log.LogInfo("[TechAll] hooked DepartmentOfScience.AddLockedTechnologyToQueue (queue every missing prerequisite of a listed technology)");
            else Plugin.Log.LogWarning("[TechAll] NOT found: DepartmentOfScience.AddLockedTechnologyToQueue — a queued listed technology gets only ONE chain of prerequisites in front (vanilla)");
            return m;
        }
        // false = skip the game's code (the plan was carried out here)
        static bool Prefix(object __instance, object[] __args)
        {
            if (!TechRequireAll.Active) return true;
            try { return !TechRequireAll.TryQueue(__instance, (int)__args[0], Convert.ToInt32(__args[1]) == 1); }
            catch (Exception ex)
            {
                // thrown BEFORE the queue was touched (binding, planning): the game's own code is safe to run
                Plugin.LogOnceWarning("techall:prefix:" + ex.GetType().Name, "[TechAll] queue planning failed — the game's own queueing runs (first occurrence): " + ex);
                return true;
            }
        }
    }

    [HarmonyPatch] internal static class Hk_TechRequireAllRemove
    {
        static MethodBase TargetMethod()
        {
            var t = GameBinding.DepartmentOfScience;
            var m = t != null ? AccessTools.Method(t, "RemoveTechnologyFromQueueAt") : null;
            if (m != null) Plugin.Log.LogInfo("[TechAll] hooked DepartmentOfScience.RemoveTechnologyFromQueueAt (a technology behind two queued prerequisites is removed once)");
            else Plugin.Log.LogWarning("[TechAll] NOT found: DepartmentOfScience.RemoveTechnologyFromQueueAt — removing a prerequisite shared by two queued branches can throw in the game's own removal");
            return m;
        }

        // The game's cascade can put the same technology on its work list through two branches.
        // Once one branch removes it, FindTechnologyIndexInQueue returns -1 for the second.
        // Let the cascade finish draining its work list and process valid removals normally.
        // NOT gated on the list: the constraints that cause the second visit are in the SAVE, and a save made with a
        // list is loaded just the same after the list is emptied or [Technologies] TechRequireAll is set to false.
        static bool Prefix(int __0) => __0 != -1;
    }
}
