using System;
using System.IO;

// Resolve PATH to an actual file so availability checks and process launch agree.
public static class BlenderExecutable
{
    public static string FindOnPath(string searchPath)
    {
        string name = Path.DirectorySeparatorChar == '\\' ? "blender.exe" : "blender";
        foreach (string entry in (searchPath ?? "").Split(Path.PathSeparator))
        {
            string directory = entry.Trim().Trim('"');
            if (directory.Length == 0) continue;
            try
            {
                string candidate = Path.GetFullPath(Path.Combine(directory, name));
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
            catch (PathTooLongException) { }
        }
        return null;
    }
}
