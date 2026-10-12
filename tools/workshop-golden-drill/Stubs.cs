// Stubs.cs — what editor/WorkshopGateTest.cs needs from Unity and the editor windows, outside Unity (2026-10-12).
// RunSection (the Bake Tests entry) is compiled but never called here; Exercise is what the drill runs. Every stub
// is the smallest thing that compiles: a log line to the console, a progress step that does nothing, a recipe
// reader that refuses (the .sh lists the sources itself, through tools/vehicle-probe-drill/recipe_check.py).
using System;
using System.Collections.Generic;

namespace UnityEngine
{
    public static class Debug
    {
        public static void Log(object m) { Console.WriteLine("[log] " + m); }
        public static void LogWarning(object m) { Console.WriteLine("[warn] " + m); }
        public static void LogError(object m) { Console.WriteLine("[error] " + m); }
    }
    public static class Application
    {
        public static string dataPath => Environment.GetEnvironmentVariable("HAF_UNITY_DATA") ?? Environment.CurrentDirectory;
    }
}

public class BakeTestSection { public string title; public int pass, fail, skip; public string body; }

public static class BakeTestRunnerWindow
{
    public static class Progress
    {
        public static void Step(string what, float fraction) { }
        public static void ThrowIfCancelled() { }
    }
}

public static class VehicleLabWindow
{
    internal static bool ReadRecipe(string recipePath, out string srcFile, out HashSet<string> roles)
    { srcFile = null; roles = new HashSet<string>(); return false; }
}
