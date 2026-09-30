// RegistryBanner.cs — the ONE status banner of a SingleSourceRegistry window (District, Formation, Game Sound Lab).
//
// Review of PR #103: each window had its own copy of the lock/corrupt banner, and they drifted - the Sound Lab had none
// at all while the engine's messages pointed at "the window's recovery", and an empty source beside a full deploy was
// refused with advice to use buttons no window showed. One drawer, fed the window's OWN frozen load verdict
// (RegistryLoadVerdict - the engine's live flags change whenever any caller loads), offering for each state exactly
// the actions that state allows:
//   locked            -> a warning, no actions (nothing can be recovered from a file that can't be seen)
//   corrupt           -> Restore last deploy / Restore last commit / Open broken file
//   missing, known    -> Restore last commit (its .meta or git says it exists)
//   missing, other    -> the reason (unreadable deploy, copies of an unsettled save to compare)
//   empty but deployed-> Restore last deploy / Keep it empty (asks first)
//   stale deploy      -> Deploy the source (the game still reads an older copy)
// Returns the status line of an action it ran (the window reloads and shows it), else null.
using System;
using UnityEditor;
using UnityEngine;

public static class RegistryBanner
{
    public sealed class Actions
    {
        public Func<string> RestoreDeploy, RestoreCommit;
        public Func<string, string> AcceptSource;   // takes the version the window loaded: deploys only what the person saw
        public string SourcePath = "";
    }

    public static string Draw(RegistryLoadVerdict v, string label, Actions a)
    {
        if (v == null) return null;
        string title = label.Substring(0, 1).ToUpperInvariant() + label.Substring(1);
        if (v.Locked)
        {
            EditorGUILayout.HelpBox($"The {label} registry source can't be read right now — {v.LockDetail}\n{v.LockAdvice} " +
                                    "Changes are refused until it can be read; nothing can be recovered from a file that can't be seen. Refresh once it's free.", MessageType.Warning);
            return null;
        }
        if (v.Corrupt)
        {
            EditorGUILayout.HelpBox($"{title.ToUpperInvariant()} REGISTRY SOURCE IS CORRUPT — {v.CorruptDetail}\n" +
                                    "The broken file is preserved beside the source; changes are locked so nothing can be wiped. Recover:", MessageType.Error);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Restore last deploy", "Copy the deployed file (what the game reads) back over the source. Validated before writing; refused if the source is readable again with entries (fixed by hand since this banner appeared)."), GUILayout.Width(140)))
                    return a.RestoreDeploy();
                if (GUILayout.Button(new GUIContent("Restore last commit", "The last committed version, read with `git show` and validated BEFORE it is written — the file on disk is never checked out over. Refused if the source is readable again with entries."), GUILayout.Width(140)))
                    return a.RestoreCommit();
                if (GUILayout.Button(new GUIContent("Open broken file", "Reveal the source in Explorer to fix the reported line by hand — then Refresh."), GUILayout.Width(120)))
                    EditorUtility.RevealInFinder(a.SourcePath);
            }
            return null;
        }
        if (v.NoCopy)
        {
            EditorGUILayout.HelpBox($"The {label} registry source is missing — {v.MissingDetail}\nThe list is empty only because of that; changes are refused until it is resolved (then Refresh).", MessageType.Warning);
            if (v.MissingKnown && GUILayout.Button(new GUIContent("Restore last commit", "Bring the source back from git (read with `git show`, validated before it is written)."), GUILayout.Width(140)))
                return a.RestoreCommit();
            return null;
        }
        if (v.EmptyButDeployed)
        {
            EditorGUILayout.HelpBox($"The {label} registry source {v.MissingDetail}. This editor has no record of emptying it (a pull, a teammate's commit, a hand edit, or an editor update), " +
                                    "so nothing is changed until you decide:", MessageType.Warning);
            using (new EditorGUILayout.HorizontalScope())
            {
                // "Restore last deploy" only when the deploy can be READ (a count of -1 means it can't: the restore would refuse)
                if (v.DeployedCount > 0 && GUILayout.Button(new GUIContent("Restore last deploy", "Bring the entries back: copy the deployed file over the empty source (validated first)."), GUILayout.Width(140)))
                    return a.RestoreDeploy();
                if (GUILayout.Button(new GUIContent("Keep it empty", "The registry really is empty now: empty the game's deployed copy too. The replaced deployed copy is kept beside it."), GUILayout.Width(120))
                    && EditorUtility.DisplayDialog($"Keep the {label} registry empty?",
                        v.DeployedCount > 0 ? $"The game's copy still holds {v.DeployedCount} entr{(v.DeployedCount == 1 ? "y" : "ies")}. They will be removed from what the game reads (the replaced copy is kept beside it)."
                                            : "The game's copy can't be checked. It will be replaced by the empty registry (the replaced copy is kept beside it).",
                        "Keep it empty", "Cancel"))
                    return a.AcceptSource(v.LoadedVersion);
            }
            return null;
        }
        if (v.Stale)
        {
            EditorGUILayout.HelpBox($"The {label} registry source changed outside the editor (git, a hand edit): the GAME still reads the older deployed copy until a save changes something.", MessageType.Info);
            if (GUILayout.Button(new GUIContent("Deploy the source", "Write the project source to the game now. The replaced deployed copy is kept beside it."), GUILayout.Width(140)))
                return a.AcceptSource(v.LoadedVersion);
            return null;
        }
        if (v.DeployHandEdited)
            EditorGUILayout.HelpBox($"The game's deployed copy of the {label} registry differs from what this editor last wrote (a hand edit there, or another project's save). The editor ignores it; the next save overwrites it from the source.", MessageType.Info);
        return null;
    }
}
