using System;
using System.IO;
using Xunit;

public class MaterialDrillTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "haf-material-evidence-" + Guid.NewGuid().ToString("N"));
    public MaterialDrillTests() { Directory.CreateDirectory(dir); }
    public void Dispose() { Directory.Delete(dir, true); }

    int Run(string evidence, string expectedSources = null)
    {
        var model = new HafModel(); model.Scene = 0; model.Scenes.Add(new HafScene());
        string source = Path.Combine(dir, "empty.glb"); GlbWriter.Write(model, source);
        string dump = Path.Combine(dir, "dump.txt");
        File.WriteAllText(dump, evidence.Replace("SOURCE", source));
        if (expectedSources == null) return MaterialDrill.Run(new[] { dump });
        string expected = Path.Combine(dir, "sources.txt");
        File.WriteAllText(expected, expectedSources.Replace("SOURCE", source));
        return MaterialDrill.Run(new[] { "--expected", expected, dump });
    }

    [Fact]
    public void A_complete_empty_material_export_is_valid()
    {
        Assert.Equal(0, Run("FILE\tSOURCE\nMATS\t\nDONE\tSOURCE\n"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("DONE\tSOURCE\n")]
    [InlineData("FILE\tSOURCE\nMATS\t\nDONE\tother\n")]
    [InlineData("FILE\tSOURCE\nMATS\t\nDONE\n")]
    [InlineData("FILE\tSOURCE\nMATS\t\nDONE\tSOURCE\textra\n")]
    [InlineData("FILE\tSOURCE\nMATS\t\nDONE\tSOURCE\nDONE\tSOURCE\n")]
    [InlineData("FILE\tSOURCE\nMATS\t\nDONE\tSOURCE\nMATS\t\n")]
    [InlineData("FILE\tSOURCE\nMATS\t\nFAIL\tSOURCE\tplanted failure\nDONE\tSOURCE\n")]
    [InlineData("FILE\tSOURCE\nMATS\t\nUNKNOWN\tplanted\nDONE\tSOURCE\n")]
    [InlineData("FILE\tSOURCE\nMATS\t\nDONE\tSOURCE\nFILE\tSOURCE\nMATS\t\nDONE\tSOURCE\n")]
    [InlineData("FILE\tSOURCE\textra\nMATS\t\nDONE\tSOURCE\n")]
    [InlineData("FILE\tSOURCE\nDIES\tRuntimeError: planted\nMATS\t\nDONE\tSOURCE\n")]
    [InlineData("FILE\tSOURCE\nDIES\tRuntimeError: planted\nDIES\tRuntimeError: twice\nDONE\tSOURCE\n")]
    [InlineData("FILE\tSOURCE\nDIES\nDONE\tSOURCE\n")]
    [InlineData("FILE\tSOURCE\nDONE\tSOURCE\n")]
    public void Malformed_or_contradictory_evidence_cannot_pass(string evidence)
    {
        Assert.Equal(1, Run(evidence));
    }

    [Theory]
    [InlineData("SOURCE\n", 0)]
    [InlineData("SOURCE\nother.glb\n", 1)]
    [InlineData("other.glb\n", 1)]
    [InlineData("", 1)]
    public void The_dump_must_cover_exactly_the_expected_sources(string sources, int expected)
    {
        Assert.Equal(expected, Run("FILE\tSOURCE\nMATS\t\nDONE\tSOURCE\n", sources));
    }

    [Fact]
    public void An_import_failure_cannot_replace_a_material_comparison()
    {
        Assert.Equal(1, Run("FILE\tSOURCE\nDIES\tRuntimeError: planted\nDONE\tSOURCE\n"));
    }
}
