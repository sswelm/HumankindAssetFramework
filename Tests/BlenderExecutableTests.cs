using System;
using System.IO;
using Xunit;

public class BlenderExecutableTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "blender-path-" + Guid.NewGuid().ToString("N"));
    string Name => Path.DirectorySeparatorChar == '\\' ? "blender.exe" : "blender";
    public BlenderExecutableTests() { Directory.CreateDirectory(root); }
    public void Dispose() { Directory.Delete(root, true); }
    [Fact]
    public void Missing_PATH_and_directories_without_the_executable_are_unavailable()
    {
        Assert.Null(BlenderExecutable.FindOnPath(null));
        Assert.Null(BlenderExecutable.FindOnPath(""));
        File.WriteAllText(Path.Combine(root,"other.exe"),"");
        Directory.CreateDirectory(Path.Combine(root,Name));
        Assert.Null(BlenderExecutable.FindOnPath(root));
    }
    [Fact]
    public void Finds_a_quoted_directory_with_spaces_and_returns_the_absolute_file()
    {
        string directory=Path.Combine(root,"Blender tools");Directory.CreateDirectory(directory);
        string exe=Path.Combine(directory,Name);File.WriteAllText(exe,"");
        string path=Path.Combine(root,"missing")+Path.PathSeparator+"\""+directory+"\"";
        Assert.Equal(exe,BlenderExecutable.FindOnPath(path));
        File.Delete(exe);Assert.Null(BlenderExecutable.FindOnPath(path));
    }
    [Fact]
    public void Preserves_PATH_order_and_ignores_empty_or_invalid_entries()
    {
        string a=Path.Combine(root,"first"),b=Path.Combine(root,"second");Directory.CreateDirectory(a);Directory.CreateDirectory(b);
        File.WriteAllText(Path.Combine(a,Name),"");File.WriteAllText(Path.Combine(b,Name),"");
        string path=string.Join(Path.PathSeparator.ToString(),new[]{"", "\0", "\"\"", a, b});
        Assert.Equal(Path.Combine(a,Name),BlenderExecutable.FindOnPath(path));
        File.Delete(Path.Combine(a,Name));Assert.Equal(Path.Combine(b,Name),BlenderExecutable.FindOnPath(path));
    }
}
