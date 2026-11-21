// TechRequireAllRules.cs — the PURE rules of "this technology needs ALL its prerequisites" (2026-11-20).
//
// No game type, no Unity type, no reflection: the tests drive every rule here directly, and the patch
// (TechRequireAllPatch.cs) is only the glue that reads the game's state into these calls and carries the answer out.
//
// The game's own rule (DepartmentOfScience.CheckTechnologyPrerequisite, decompiled 2026-11-20) is ANY ONE: a
// technology is available as soon as one listed prerequisite is completed. A technology named in
// haf_tech_requires_all.json is available only when EVERY listed prerequisite is completed.
//
// The lock alone does not stop research: InvestResearch pays into whatever sits in the queue without looking at the
// technology's state, so the queue's ORDER is the real guard. Vanilla queues ONE chain of parents in front of a locked
// technology (one parent per level). PlanQueue is the replacement for the trees that hold a listed technology: every
// missing parent of a listed technology goes in front of it.

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace HumankindAssetFramework
{
    internal static class TechRequireAllRules
    {
        // The game's TechnologyStates, same order and values (the patch converts by integer).
        internal enum State { LockedByEra = 0, LockedByTechnology = 1, Available = 2, Completed = 3 }

        internal const string FileName = "haf_tech_requires_all.json";
        internal const string ArrayKey = "technologies";

        /// <summary>
        /// The names in the file. A missing or malformed list is NO names plus a <paramref name="problem"/> that says
        /// why - never an exception, and never "the file is fine and empty".
        /// </summary>
        internal static HashSet<string> ParseList(string json, out string problem)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            problem = "";
            JObject root;
            try { root = JObject.Parse(json ?? ""); }
            catch (Exception ex) { problem = "not valid JSON: " + ex.Message; return names; }
            if (!(root[ArrayKey] is JArray arr)) { problem = $"no \"{ArrayKey}\" list in the file"; return names; }
            foreach (var t in arr)
            {
                if (t == null || t.Type != JTokenType.String) continue;
                var s = ((string)t ?? "").Trim();
                if (s.Length > 0) names.Add(s);
            }
            return names;
        }

        /// <summary>
        /// ALL rule: every KNOWN prerequisite is completed. An index below 0 is a name the game does not hold (it logs
        /// that itself); it is left out, so a typo in the data can never lock a technology for good.
        /// </summary>
        internal static bool AllCompleted(IList<int> prerequisites, Func<int, State> state)
        {
            if (prerequisites == null) return true;
            for (int i = 0; i < prerequisites.Count; i++)
            {
                int p = prerequisites[i];
                if (p < 0) continue;
                if (state(p) != State.Completed) return false;
            }
            return true;
        }

        // ---- the tech screen's hint (2026-11-21): a mark in front of a listed technology's title, a line in its tooltip ----

        // [SharedProject] is the game's own branching symbol (one point joined to two). Chosen in the game on 2026-11-21 from
        // eleven candidates shown in a tooltip; of the plain characters tried, the game's font has & and » but not ⋀ or ⇉.
        /// <summary>Default mark. Plain text or the game's own text markup: an inline symbol "[Name]", a colour "&lt;c=RRGGBB&gt;..&lt;/c&gt;".</summary>
        internal const string DefaultMark = "[SharedProject]";
        /// <summary>
        /// Default tooltip lines, for exactly two prerequisites and for three or more ("both" is wrong for three).
        /// {0} = every prerequisite title but the last, comma-separated; {1} = the last. The wording and the italics
        /// are the user's, from the first look in the game (2026-11-21); the game's own texts use &lt;i&gt; too.
        /// </summary>
        internal const string DefaultTooltip = "<i>Requires both {0} and {1}</i>";
        internal const string DefaultTooltipMany = "<i>Requires all of {0} and {1}</i>";

        /// <summary>The title the tech screen shows: the mark, a space, the game's title. No mark = the game's title.</summary>
        internal static string MarkedTitle(string mark, string title)
        {
            mark = (mark ?? "").Trim();
            title = title ?? "";
            return mark.Length == 0 ? title : mark + " " + title;
        }

        /// <summary>
        /// The tooltip's line, or "" for none (no format, or fewer than two prerequisites: the rule changes nothing
        /// then). <paramref name="formatTwo"/> is used for exactly two prerequisites, <paramref name="formatMany"/>
        /// for more. {0} = every title but the last, comma-separated; {1} = the last. A format WITHOUT {1} gets the
        /// whole list in {0}, so no prerequisite is ever left out of the sentence. One pass by hand, not
        /// string.Format: the format is a person's config text, a stray brace in it must not throw on the UI thread,
        /// and a title that itself holds "{1}" must not be filled in a second time.
        /// </summary>
        internal static string TooltipLine(string formatTwo, string formatMany, IList<string> prerequisiteTitles)
        {
            if (prerequisiteTitles == null || prerequisiteTitles.Count < 2) return "";
            int n = prerequisiteTitles.Count;
            string format = ((n == 2 ? formatTwo : formatMany) ?? "").Trim();
            if (format.Length == 0) return "";
            bool namesLast = format.IndexOf("{1}", StringComparison.Ordinal) >= 0;
            var head = new List<string>(n);
            for (int i = 0; i < (namesLast ? n - 1 : n); i++) head.Add(prerequisiteTitles[i] ?? "");
            string first = string.Join(", ", head.ToArray()), last = prerequisiteTitles[n - 1] ?? "";
            var sb = new System.Text.StringBuilder(format.Length + first.Length + last.Length);
            for (int i = 0; i < format.Length; i++)
            {
                bool slot = format[i] == '{' && i + 2 < format.Length && format[i + 2] == '}' && (format[i + 1] == '0' || format[i + 1] == '1');
                if (!slot) { sb.Append(format[i]); continue; }
                sb.Append(format[i + 1] == '0' ? first : last);
                i += 2;
            }
            return sb.ToString();
        }

        /// <summary>
        /// The description with the line as its last line, ONCE: the game rebuilds the description on every refresh,
        /// and a second pass over one it did not rebuild must not add the line again.
        /// </summary>
        internal static string WithTooltipLine(string description, string line)
        {
            description = description ?? "";
            if (string.IsNullOrEmpty(line)) return description;
            if (description == line || description.EndsWith("\n" + line, StringComparison.Ordinal)) return description;
            return description.Length == 0 ? line : description + "\n" + line;
        }

        internal sealed class Plan
        {
            public bool Ok = true;             // false: nothing may be queued (Why says what stopped it)
            public bool UsesRequireAll;        // false: no listed technology in the tree - the game's own code runs
            public string Why = "";
            public readonly List<int> Order = new List<int>();   // what must sit in front of the target, parents first
            public readonly List<KeyValuePair<int, int>> Edges = new List<KeyValuePair<int, int>>();   // (parent, child) queue constraints
        }

        internal const int MaxDepth = 32;   // a cycle in modded data must end the walk, not hang the simulation thread

        /// <summary>
        /// What has to be queued in front of the locked technology <paramref name="target"/>.
        /// A listed technology asks for every prerequisite that is not completed; any other technology asks for one,
        /// chosen as the game chooses it (the last prerequisite already in the queue, else the first one listed).
        /// The walk stops at a technology that is available or already queued, exactly as the game's does.
        /// </summary>
        internal static Plan PlanQueue(int target, Func<int, IList<int>> prerequisites, Func<int, State> state,
                                       Func<int, bool> inQueue, Func<int, bool> requireAll)
        {
            var plan = new Plan();
            var visited = new HashSet<int> { target };
            var open = new HashSet<int>();   // the technologies whose parents are being walked right now: meeting one again is a cycle
            Expand(plan, target, 0, prerequisites, state, inQueue, requireAll, visited, open);
            if (!plan.Ok) { plan.Order.Clear(); plan.Edges.Clear(); }
            return plan;
        }

        static void Expand(Plan plan, int tech, int depth, Func<int, IList<int>> prerequisites, Func<int, State> state,
                           Func<int, bool> inQueue, Func<int, bool> requireAll, HashSet<int> visited, HashSet<int> open)
        {
            if (!plan.Ok) return;
            if (depth > MaxDepth) { plan.Ok = false; plan.Why = $"the prerequisites go deeper than {MaxDepth} levels"; return; }
            var parents = prerequisites(tech) ?? new int[0];
            open.Add(tech);

            if (parents.Count >= 2 && requireAll(tech))
            {
                plan.UsesRequireAll = true;
                bool foundParent = false;
                for (int i = 0; i < parents.Count && plan.Ok; i++)
                {
                    int p = parents[i];
                    if (p < 0) continue;
                    foundParent = true;
                    Require(plan, p, tech, depth, prerequisites, state, inQueue, requireAll, visited, open);
                }
                if (!foundParent)
                {
                    plan.Ok = false;
                    plan.Why = $"technology #{tech} is locked and names no prerequisite the game holds";
                }
            }
            else
            {
                // the game's choice (TryFillParentTechnologiesToCompleteBefore): walk from the last name to the first
                // and stop at one that is in the queue; with none in the queue the walk ends on the FIRST name
                int chosen = -1;
                for (int i = parents.Count - 1; i >= 0; i--)
                {
                    chosen = parents[i];
                    if (chosen >= 0 && inQueue(chosen)) break;
                }
                if (chosen < 0) { plan.Ok = false; plan.Why = $"technology #{tech} is locked and names no prerequisite the game holds"; }
                else Require(plan, chosen, tech, depth, prerequisites, state, inQueue, requireAll, visited, open);
            }
            open.Remove(tech);
        }

        static void Require(Plan plan, int parent, int child, int depth, Func<int, IList<int>> prerequisites,
                            Func<int, State> state, Func<int, bool> inQueue, Func<int, bool> requireAll,
                            HashSet<int> visited, HashSet<int> open)
        {
            if (open.Contains(parent)) { plan.Ok = false; plan.Why = $"technology #{parent} is its own prerequisite (through #{child})"; return; }
            var s = state(parent);
            if (s == State.Completed) return;                    // nothing to research, nothing to wait for
            plan.Edges.Add(new KeyValuePair<int, int>(parent, child));
            if (!visited.Add(parent)) return;                    // reached twice (a diamond): one place in the queue, both constraints
            if (s == State.Available || inQueue(parent)) { plan.Order.Add(parent); return; }
            if (s == State.LockedByEra) { plan.Ok = false; plan.Why = $"prerequisite #{parent} is locked by its era"; return; }
            Expand(plan, parent, depth + 1, prerequisites, state, inQueue, requireAll, visited, open);
            if (plan.Ok) plan.Order.Add(parent);                 // after its own parents
        }
    }
}
