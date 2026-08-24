// SocketBonesDialog.cs — the DONOR SOCKETS mapping editor (2026-07-25). Opened from the Animation Lab's
// "Donor sockets (bake)" row: builds the `socketBones` spec ("DonorName=OurBoneSubstr[@x,y,z];...") by LINKING the
// donor's hardpoint names to bones of OUR model, instead of hand-writing the string.
//   - LEFT (donor hardpoint): the names the donor's fire events actually ask for, harvested from the BepInEx log's
//     [Muzzle] GetBoneTRS diagnostic lines for THIS unit (fire the unit once in-game to populate them) — plus a
//     free-text field for names known some other way.
//   - RIGHT (our bone): the model's full bone list (glb/gltf inspection, per-bone precision — a parent must name ONE
//     bone: "MW_T", not the "MW" prefix group).
//   - Optional model-space offset per socket (the "@x,y,z" suffix) to nudge the socket off its parent's head.
// Apply writes the assembled spec back to the Lab field; the bake does the rest (Factory-Manual §17).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

public class SocketBonesDialog : EditorWindow
{
    class Row { public string donor = "", parent = ""; public Vector3 off; }

    List<Row> rows = new List<Row>();
    List<string> boneNames = new List<string>();
    List<string> donorCandidates = new List<string>();
    Action<string> onApply;
    string pawnDesc = "";

    public static void Open(string currentSpec, string pawnDescription, List<string> modelBones, Action<string> onApply)
    {
        var w = GetWindow<SocketBonesDialog>(true, "Donor sockets", true);
        w.minSize = new Vector2(560, 300);
        w.onApply = onApply;
        w.pawnDesc = pawnDescription ?? "";
        w.boneNames = modelBones ?? new List<string>();
        w.rows = ParseSpec(currentSpec);
        w.donorCandidates = HarvestHardpoints(w.pawnDesc);
        foreach (var r in w.rows)   // names already mapped count as candidates too (re-editing an old spec)
            if (!string.IsNullOrEmpty(r.donor) && !w.donorCandidates.Contains(r.donor)) w.donorCandidates.Add(r.donor);
    }

    // "DonorName=Parent@x,y,z;..." -> rows (forgiving: skips malformed pairs).
    static List<Row> ParseSpec(string spec)
    {
        var res = new List<Row>();
        foreach (var pair in (spec ?? "").Split(';'))
        {
            var p = pair.Trim();
            if (p.Length == 0 || !p.Contains("=")) continue;
            var dn = p.Split('=')[0].Trim();
            var rest = p.Substring(p.IndexOf('=') + 1).Trim();
            var row = new Row { donor = dn };
            if (rest.Contains("@"))
            {
                row.parent = rest.Split('@')[0].Trim();
                var o = rest.Split('@')[1].Split(',');
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                if (o.Length == 3
                    && float.TryParse(o[0], System.Globalization.NumberStyles.Float, inv, out var x)
                    && float.TryParse(o[1], System.Globalization.NumberStyles.Float, inv, out var y)
                    && float.TryParse(o[2], System.Globalization.NumberStyles.Float, inv, out var z))
                    row.off = new Vector3(x, y, z);
            }
            else row.parent = rest;
            res.Add(row);
        }
        return res;
    }

    // The donor hardpoint names the game actually asked for on THIS unit — from the plugin's [Muzzle] GetBoneTRS
    // diagnostic lines in the BepInEx log ("GetBoneTRS('Canon_Up_left') subPawn='...<pawnDesc>'"). Distinct, ordered.
    static List<string> HarvestHardpoints(string pawnDesc)
    {
        var res = new List<string>();
        try
        {
            var logPath = Path.Combine(Directory.GetParent(ModelRegistry.ConfigDir).FullName, "LogOutput.log");
            if (!File.Exists(logPath) || string.IsNullOrEmpty(pawnDesc)) return res;
            var rx = new System.Text.RegularExpressions.Regex(@"GetBoneTRS\('([^']+)'\) subPawn='[^']*" + System.Text.RegularExpressions.Regex.Escape(pawnDesc));
            foreach (var line in File.ReadLines(logPath))
            {
                var m = rx.Match(line);
                if (m.Success && !res.Contains(m.Groups[1].Value)) res.Add(m.Groups[1].Value);
            }
        }
        catch { }
        return res;
    }

    void OnGUI()
    {
        EditorGUILayout.HelpBox(
            "Link the DONOR's hardpoint names (left) to bones of YOUR model (right). Hardpoint candidates come from " +
            "the plugin's [Muzzle] log for this unit — fire the unit once in-game to populate them, or type a name. " +
            "The optional offset (model space) nudges the socket off its parent bone's head. Apply writes the spec; " +
            "then Bake → rebuild → the donor's flash/smoke/projectile origin resolve natively on your bones.", MessageType.Info);
        if (donorCandidates.Count == 0)
            EditorGUILayout.HelpBox("No hardpoints harvested from the log for '" + pawnDesc + "' — launch the game, make the unit fire once, and reopen (or add names manually).", MessageType.Warning);

        int remove = -1;
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            using (new EditorGUILayout.HorizontalScope())
            {
                r.donor = EditorGUILayout.TextField(r.donor, GUILayout.MinWidth(120));
                PickInto(donorCandidates, "Donor hardpoint", s => { r.donor = s; Repaint(); });
                GUILayout.Label("=", GUILayout.Width(14));
                r.parent = EditorGUILayout.TextField(r.parent, GUILayout.MinWidth(100));
                PickInto(boneNames, "Your bone", s => { r.parent = s; Repaint(); });
                GUILayout.Label("@", GUILayout.Width(16));
                r.off.x = EditorGUILayout.FloatField(r.off.x, GUILayout.Width(44));
                r.off.y = EditorGUILayout.FloatField(r.off.y, GUILayout.Width(44));
                r.off.z = EditorGUILayout.FloatField(r.off.z, GUILayout.Width(44));
                if (GUILayout.Button("✕", GUILayout.Width(24))) remove = i;
            }
        }
        if (remove >= 0) rows.RemoveAt(remove);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("+ Add mapping", GUILayout.Width(110))) rows.Add(new Row());
            // one-click sensible default: every harvested hardpoint mapped to the same picked bone
            using (new EditorGUI.DisabledScope(donorCandidates.Count == 0 || boneNames.Count == 0))
                if (GUILayout.Button(new GUIContent("Map ALL hardpoints to one bone…",
                        "Adds a row for every harvested hardpoint, all parented to the bone you pick (the ArmouredCar recipe: everything on the gun)."), GUILayout.Width(220)))
                {
                    var rect = GUILayoutUtility.GetLastRect();
                    new StringDropdown(new AdvancedDropdownState(), boneNames.ToArray(), boneNames.ToArray(), "Your bone", s =>
                    {
                        foreach (var dn in donorCandidates)
                            if (!rows.Any(x => x.donor == dn)) rows.Add(new Row { donor = dn, parent = s });
                        Repaint();
                    }).Show(rect);
                }
        }

        GUILayout.FlexibleSpace();
        using (new EditorGUILayout.HorizontalScope())
        {
            string spec = BuildSpec();
            if (GUILayout.Button("Apply:   " + (spec.Length == 0 ? "(clear)" : spec), GUILayout.Height(28)))
            { onApply?.Invoke(spec); Close(); }
            if (GUILayout.Button("Cancel", GUILayout.Height(28), GUILayout.Width(90))) Close();
        }
    }

    void PickInto(List<string> options, string title, Action<string> set)
    {
        using (new EditorGUI.DisabledScope(options == null || options.Count == 0))
            if (GUILayout.Button("▾", GUILayout.Width(24)))
            {
                var rect = GUILayoutUtility.GetLastRect();
                new StringDropdown(new AdvancedDropdownState(), options.ToArray(), options.ToArray(), title, set).Show(rect);
            }
    }

    string BuildSpec()
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return string.Join(";", rows
            .Where(r => !string.IsNullOrEmpty(r.donor) && !string.IsNullOrEmpty(r.parent))
            .Select(r => r.donor.Trim() + "=" + r.parent.Trim()
                + (r.off == Vector3.zero ? "" : string.Format(inv, "@{0:0.###},{1:0.###},{2:0.###}", r.off.x, r.off.y, r.off.z))));
    }
}
