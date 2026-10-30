// The Bake Tests row "Do the C# ports still match Blender?" run from a real Unity editor process (batch mode), the way
// the window runs it: BlenderParityGateTest.RunSection, its section written out for tools/parity_row_unity_drill.sh.
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static class UnityParityRowDrill
{
    public static void Run()
    {
        string output = null;
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "-hafParityOutput") output = args[i + 1];
        int code = 1;
        try
        {
            var s = BlenderParityGateTest.RunSection();
            string text = "TITLE " + s.title + "\nPASS " + s.pass + "\nFAIL " + s.fail + "\nSKIP " + s.skip + "\n--- body ---\n" + s.body;
            if (output != null) File.WriteAllText(output, text);
            Debug.Log(text);
            code = s.fail == 0 && s.skip == 0 && s.pass > 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            if (output != null) File.WriteAllText(output, "CRASH " + ex);
            Debug.LogException(ex);
        }
        EditorApplication.Exit(code);
    }
}
