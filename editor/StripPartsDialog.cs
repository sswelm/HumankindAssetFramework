// StripPartsDialog.cs — the "Also remove" part-picker (2026-08-01). Opened from the Animation Lab's deploy-conversion
// "Also remove (adds to defaults)" row. Builds the `deployStripExtra` CSV by TICKING parts of the source model instead
// of hand-typing substrings — the user's ask ("let the user create a list of parts").
//   - The list is the SOURCE model's node/part names (glb/gltf inspection of cur.modelFile — NOT the converted file,
//     whose already-stripped parts are gone). A search box filters long lists.
//   - The default crew/prop kill-list ALWAYS applies on top of this at bake time (deploy_convert), so it is NOT shown
//     here — you only pick the EXTRA parts (the M114 control hand-wheels), never re-typing the defaults.
//   - A free-text row keeps any manual substrings that don't match a listed part (forward/back compatible).
// Apply writes the assembled CSV back to the Lab field; Bake → rebuild removes them.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

public class StripPartsDialog : EditorWindow
{
    List<string> parts = new List<string>();       // the source model's part/node names
    HashSet<string> picked = new HashSet<string>();// parts (exact names) currently ticked
    string manual = "";                            // tokens that don't match any listed part (kept verbatim)
    string filter = "";
    Vector2 scroll;
    Action<string> onApply;

    public static void Open(string currentCsv, List<string> sourceParts, Action<string> onApply)
    {
        var w = GetWindow<StripPartsDialog>(true, "Also remove — pick parts", true);
        w.minSize = new Vector2(460, 420);
        w.onApply = onApply;
        w.parts = (sourceParts ?? new List<string>())
            .Where(p => !string.IsNullOrEmpty(p)).Distinct().OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        // Split the existing CSV into ticks (match a listed part) vs manual leftovers.
        var partSet = new HashSet<string>(w.parts, StringComparer.OrdinalIgnoreCase);
        var leftover = new List<string>();
        foreach (var tok in (currentCsv ?? "").Split(',').Select(t => t.Trim()).Where(t => t.Length > 0))
        {
            var hit = w.parts.FirstOrDefault(p => string.Equals(p, tok, StringComparison.OrdinalIgnoreCase));
            if (hit != null) w.picked.Add(hit);
            else leftover.Add(tok);
        }
        w.manual = string.Join(", ", leftover);
    }

    void OnGUI()
    {
        EditorGUILayout.HelpBox(
            "Tick the SOURCE parts to remove from this model. The default crew/prop kill-list is ALWAYS applied on top " +
            "automatically — you never re-type it here; only add the extras (e.g. a mis-animated control wheel). A ticked " +
            "name also removes any sub-part whose name contains it (so ticking the parent catches its '…_small_part' child).",
            MessageType.Info);

        if (parts.Count == 0)
            EditorGUILayout.HelpBox("No parts readable from the source model (glb/gltf inspection only). Use the manual field below, " +
                "or set the model file in the Model Factory.", MessageType.Warning);

        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Label("Filter", GUILayout.Width(40));
            filter = EditorGUILayout.TextField(filter);
            if (GUILayout.Button("✕", GUILayout.Width(24))) filter = "";
        }

        scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.ExpandHeight(true));
        foreach (var p in parts)
        {
            if (filter.Length > 0 && p.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            bool was = picked.Contains(p);
            bool now = EditorGUILayout.ToggleLeft(p, was);
            if (now && !was) picked.Add(p);
            else if (!now && was) picked.Remove(p);
        }
        EditorGUILayout.EndScrollView();

        EditorGUILayout.LabelField(new GUIContent("Manual substrings (comma-sep)",
            "Extra name substrings not in the list above — kept verbatim and appended. Rarely needed."));
        manual = EditorGUILayout.TextField(manual);

        GUILayout.Space(4);
        string csv = Build();
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Apply:   " + (csv.Length == 0 ? "(clear)" : csv), GUILayout.Height(28)))
            { onApply?.Invoke(csv); Close(); }
            if (GUILayout.Button("Cancel", GUILayout.Height(28), GUILayout.Width(90))) Close();
        }
    }

    string Build()
    {
        var toks = new List<string>(picked.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
        toks.AddRange((manual ?? "").Split(',').Select(t => t.Trim()).Where(t => t.Length > 0));
        return string.Join(",", toks.Distinct());
    }
}
