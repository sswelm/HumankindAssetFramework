// MaxImport.cs - the door a 3ds Max file comes through (2026-11-21). The Model Cutter, the Model Fuser, the Vehicle Lab
// and the Model Factory take a .max in their file pickers; picking one CONVERTS it here - MaxModel reads it, GlbWriter
// writes `name_max.glb` beside it - and the tool goes on with that .glb. A .max has no second reader anywhere (Blender
// and Unity cannot open one), so the conversion happens once, at the door, and everything after it - probes, bakes,
// recipes, and every Blender fallback of the in-process ports - sees an ordinary GLB.
//
// The report (parts, triangles, textures found or missing, and every thing the reader left by name) is shown once and
// written to the Console. A `name_max.glb` this reader wrote before is replaced without asking; any other file of that
// name asks first.
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class MaxImport
{
    static bool dialogShown;   // a conversion ran (its progress bar and dialogs) during the current GUI pass

    /// <summary>What a file picker hands its tool: the picked path as it is, or - for a .max - the .glb converted from it.
    /// Null when the conversion failed or was declined (the user has been told); the tool keeps its old source then.
    /// A caller inside OnGUI calls <see cref="ExitGuiAfterDialogs"/> once it has stored the result.</summary>
    public static string Picked(string picked)
    {
        if (string.IsNullOrEmpty(picked) || !MaxModel.IsMax(picked)) return picked;
        dialogShown = true;
        return Convert(picked);
    }

    /// <summary>Ends the current GUI pass when a conversion showed its dialogs in it: a modal dialog in the middle of an
    /// IMGUI pass leaves Unity's layout groups unbalanced ("EndLayoutGroup: BeginLayoutGroup must be called first"), and
    /// leaving the pass is Unity's own remedy. The fields the caller set stay set; the window repaints.</summary>
    public static void ExitGuiAfterDialogs()
    {
        if (!dialogShown) return;
        dialogShown = false;
        GUIUtility.ExitGUI();
    }

    /// <summary>For a text field that holds a typed .max path: a line with a Convert button under it. The conversion runs
    /// after the GUI pass; `onConverted` then receives the .glb's path (set the field there, reset what depends on it).</summary>
    public static void TypedPathGui(string path, Action<string> onConverted)
    {
        if (!MaxModel.IsMax(path) || !File.Exists(path.Trim())) return;
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.HelpBox("A 3ds Max file is converted to a .glb beside it first; the tool then works on that copy.", MessageType.Info);
            if (GUILayout.Button("Convert .max", GUILayout.Width(100), GUILayout.Height(38)))
            {
                string max = path.Trim();
                EditorApplication.delayCall += () =>
                {
                    string glb = Convert(max);
                    dialogShown = false;
                    if (glb != null) { GUI.FocusControl(null); onConverted(glb); }
                };
            }
        }
    }

    /// <summary>Converts a .max to `name_max.glb` beside it and returns that path; null on failure or when the user keeps an existing file.</summary>
    public static string Convert(string maxPath)
    {
        string target;
        try { target = MaxModel.DefaultGlbPath(maxPath); }
        catch (Exception e) { EditorUtility.DisplayDialog("3ds Max file", "The path cannot be used:\n" + maxPath + "\n\n" + e.Message, "OK"); return null; }
        if (File.Exists(target) && !MaxModel.IsOurs(target)
            && !EditorUtility.DisplayDialog("Overwrite existing file?",
                "The converted copy would be written to\n" + target + "\n\nA file of that name exists and was NOT written by the .max reader. Overwrite it?", "Overwrite", "Cancel"))
            return null;
        MaxReport report;
        try
        {
            EditorUtility.DisplayProgressBar("3ds Max file", "Reading " + Path.GetFileName(maxPath) + " ...", 0.3f);
            report = MaxModel.ConvertToGlb(maxPath, target, Decode);
        }
        catch (Exception e) when (e is InvalidDataException || e is NotSupportedException || e is IOException || e is UnauthorizedAccessException)
        {
            EditorUtility.ClearProgressBar();
            Debug.LogError("[HAF] the .max file could not be converted: " + maxPath + "\n" + e);
            EditorUtility.DisplayDialog("3ds Max file not read", Path.GetFileName(maxPath) + " could not be converted:\n\n" + e.Message, "OK");
            return null;
        }
        finally { EditorUtility.ClearProgressBar(); }
        string summary = report.Summary();
        Debug.Log("[HAF] 3ds Max file converted: " + maxPath + " -> " + target + "\n" + summary);
        EditorUtility.DisplayDialog("3ds Max file converted",
            "Written as\n" + target + "\nThe tool works on this copy from here on.\n\n" + (summary.Length > 1800 ? summary.Substring(0, 1800) + "\n... (the whole report is in the Console)" : summary), "OK");
        return target;
    }

    /// <summary>JPEG (and anything else Unity's loader takes) to pixels, rows top-down. Main thread only: the pickers are.</summary>
    static bool Decode(byte[] file, string name, out int width, out int height, out byte[] rgba)
    {
        width = height = 0; rgba = null;
        var t = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
        try
        {
            if (!ImageConversion.LoadImage(t, file, false)) return false;
            width = t.width; height = t.height;
            var px = t.GetPixels32();                 // rows bottom-up
            if (px == null || px.Length != width * height) return false;
            rgba = new byte[px.Length * 4];
            for (int y = 0; y < height; y++)
            {
                int from = (height - 1 - y) * width, to = y * width * 4;
                for (int x = 0; x < width; x++)
                {
                    var c = px[from + x];
                    rgba[to + x * 4] = c.r; rgba[to + x * 4 + 1] = c.g; rgba[to + x * 4 + 2] = c.b; rgba[to + x * 4 + 3] = c.a;
                }
            }
            return true;
        }
        catch (Exception) { rgba = null; return false; }
        finally { UnityEngine.Object.DestroyImmediate(t); }
    }
}
