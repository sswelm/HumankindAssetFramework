// PawnRigDumpWindow.cs — Tools ▸ HAF ▸ Diagnostics ▸ Pawn Rig Dump (2026-07-25): walk a vanilla pawn
// definition's ENTIRE serialized graph (skeleton, clip collections, referenced assets) into a text file.
// Born for the caterpillar investigation: vanilla tank treads visibly roll in-game, but the mechanism leaves
// no trace in managed code — the answer (track-link bones vs shader scroll) lives in the ASSETS. Bone-name
// arrays and clip channel data show up as plain serialized properties, so a generic dump settles it.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public class PawnRigDumpWindow : EditorWindow
{
    [MenuItem("Tools/HAF/Diagnostics/Pawn Rig Dump")]
    static void Open()
    {
        var w = GetWindow<PawnRigDumpWindow>("Pawn Rig Dump");
        w.minSize = new Vector2(420, 120);
    }

    [SerializeField] string filter = "Tank";
    string status = "";

    void OnGUI()
    {
        EditorGUILayout.HelpBox("Dumps every PresentationPawnDefinition whose name contains the filter — plus every " +
            "asset it references (skeleton, clip collections), recursively — to rig_dump.txt in the project root. " +
            "Large arrays are summarized (counts, first elements).", MessageType.Info);
        filter = EditorGUILayout.TextField("Pawn name contains", filter);
        if (GUILayout.Button("Dump", GUILayout.Height(26))) Dump();
        if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, MessageType.None);
    }

    void Dump()
    {
        var sb = new System.Text.StringBuilder();
        var seen = new HashSet<UnityEngine.Object>();
        int defs = 0;
        foreach (var guid in AssetDatabase.FindAssets("PresentationPawnDefinition"))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (o == null || o.GetType().Name != "PresentationPawnDefinition") continue;
                if (!string.IsNullOrEmpty(filter) && o.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                defs++;
                sb.AppendLine();
                sb.AppendLine($"================ PAWN DEF: {o.name}   ({path}) ================");
                DumpObj(o, sb, seen, 0);
            }
        }
        string projRoot = Directory.GetParent(Application.dataPath).FullName;
        string outPath = Path.Combine(projRoot, "rig_dump.txt");
        File.WriteAllText(outPath, sb.ToString());
        status = defs == 0
            ? $"No PresentationPawnDefinition matches '{filter}'."
            : $"Dumped {defs} pawn def(s) + referenced assets -> {outPath}  ({sb.Length / 1024} KB)";
        Debug.Log("[PawnRigDump] " + status);
    }

    // Generic serialized walk: primitives/strings printed, object references queued for their own dump, big
    // arrays summarized (first 8 elements) — bone-name string arrays survive intact, byte blobs don't flood.
    void DumpObj(UnityEngine.Object o, System.Text.StringBuilder sb, HashSet<UnityEngine.Object> seen, int depth)
    {
        if (o == null || !seen.Add(o) || depth > 3) return;
        sb.AppendLine($"--- [{new string('>', depth)}] {o.GetType().FullName}  '{o.name}' ---");
        SerializedObject so;
        try { so = new SerializedObject(o); } catch { sb.AppendLine("    (not serializable)"); return; }
        var it = so.GetIterator();
        var refs = new List<UnityEngine.Object>();
        bool enter = true;
        int lines = 0;
        while (it.Next(enter) && lines < 6000)
        {
            enter = true;
            // summarize big arrays: print the size once, skip elements past [8]
            int di = it.propertyPath.LastIndexOf(".data[", StringComparison.Ordinal);
            if (di >= 0)
            {
                int close = it.propertyPath.IndexOf(']', di);
                if (close > di && int.TryParse(it.propertyPath.Substring(di + 6, close - di - 6), out int idx) && idx > 8)
                { enter = false; continue; }
            }
            switch (it.propertyType)
            {
                case SerializedPropertyType.String: sb.AppendLine($"    {it.propertyPath} = \"{it.stringValue}\""); lines++; break;
                case SerializedPropertyType.Integer: sb.AppendLine($"    {it.propertyPath} = {it.longValue}"); lines++; break;
                case SerializedPropertyType.Float: sb.AppendLine($"    {it.propertyPath} = {it.doubleValue:0.####}"); lines++; break;
                case SerializedPropertyType.Boolean: sb.AppendLine($"    {it.propertyPath} = {it.boolValue}"); lines++; break;
                case SerializedPropertyType.Enum: sb.AppendLine($"    {it.propertyPath} = enum:{it.intValue}"); lines++; break;
                case SerializedPropertyType.ArraySize: if (it.intValue > 0) { sb.AppendLine($"    {it.propertyPath} = [{it.intValue}]"); lines++; } break;
                case SerializedPropertyType.ObjectReference:
                    var r = it.objectReferenceValue;
                    sb.AppendLine($"    {it.propertyPath} -> {(r != null ? r.GetType().Name + " '" + r.name + "'" : "null")}"); lines++;
                    if (r != null && !(r is GameObject) && !(r is Texture)) refs.Add(r);
                    break;
            }
        }
        if (lines >= 6000) sb.AppendLine("    ... (line cap reached for this object)");
        foreach (var r in refs.Distinct())
        {
            var ns = r.GetType().Namespace ?? "";
            if (ns.Contains("Amplitude") || r is ScriptableObject || r is Mesh || r is AnimationClip)
                DumpObj(r, sb, seen, depth + 1);
        }
    }
}
