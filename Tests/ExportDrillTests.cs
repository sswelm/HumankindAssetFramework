using System;
using System.IO;
using Xunit;

public class ExportDrillTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "haf-export-evidence-" + Guid.NewGuid().ToString("N"));

    public ExportDrillTests() { Directory.CreateDirectory(dir); }
    public void Dispose() { Directory.Delete(dir, true); }

    int Run(string dump, bool animated = false, string key = "empty", string argv = "0|24")
    {
        var model = new HafModel();
        model.Scenes.Add(new HafScene()); model.Scene = 0;
        if (animated)
        {
            var mesh = new HafMesh { Name = "part" };
            mesh.Primitives.Add(new HafPrimitive { VertexCount = 3, Positions = new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f } });
            model.Meshes.Add(mesh); model.Nodes.Add(new HafNode { Name = "Part", Mesh = 0 }); model.Scenes[0].Nodes.Add(0);
            var action = new HafAnimation { Name = "Deploy" };
            action.Samplers.Add(new HafSampler { Times = new[] { 0f, 1f }, Values = new[] { 0f, 0f, 0f, 0f, 1f, 0f }, Components = 3, Interpolation = "LINEAR" });
            action.Channels.Add(new HafChannel { Node = 0, Path = "translation", Sampler = 0 }); model.Animations.Add(action);
        }
        var path = Path.Combine(dir, "empty.glb");
        GlbWriter.Write(model, path);
        Assert.Equal(!animated, BlenderDeploy.Decide(GlbReader.Read(path), argv.Split('|')).Exit);
        var jobs = Path.Combine(dir, "jobs.txt"); var evidence = Path.Combine(dir, "dump.txt");
        File.WriteAllText(jobs, key + "|" + path + "|" + argv + "\n");
        File.WriteAllText(evidence, dump);
        return ExportDrill.Run(new[] { jobs, evidence });
    }

    [Fact]
    public void A_single_matching_exit_and_end_record_is_valid()
    {
        Assert.Equal(0, Run("JOB\tempty\nEXIT8\t1\nDONE\tempty\n"));
    }

    [Theory]
    [InlineData("JOB\tempty\nEXIT8\t1\nDONE\tother\n")]
    [InlineData("JOB\tempty\nEXIT8\t1\nDONE\tempty\nDONE\tempty\n")]
    [InlineData("JOB\tempty\nEXIT8\t1\nDONE\n")]
    [InlineData("JOB\tempty\nEXIT8\t1\nDONE\tempty\textra\n")]
    [InlineData("JOB\tempty\nEXIT8\t1\nFAIL\tempty\tplanted failure\nDONE\tempty\n")]
    [InlineData("JOB\tempty\nEXIT8\t1\nDIES8\tRuntimeError: planted\nDONE\tempty\n")]
    [InlineData("JOB\tempty\nEXIT8\t1\nGLB\t28\t0\t0\nDONE\tempty\n")]
    [InlineData("JOB\tempty\nEXIT8\t1\nDONE\tempty\nJOB\tempty\nEXIT8\t1\nDONE\tempty\n")]
    [InlineData("JOB\tempty\nEXIT8\t1\nDONE\tempty\nUNKNOWN\tplanted\n")]
    public void Malformed_or_contradictory_evidence_cannot_pass(string dump)
    {
        Assert.Equal(1, Run(dump));
    }

    [Theory]
    [InlineData("JOB\tempty\nDIES8\tRuntimeError: planted\nDONE\tempty\n", "empty", 1)]
    [InlineData("JOB\tEXPORTLEFT:empty\nDIES8\tRuntimeError: planted\nDONE\tEXPORTLEFT:empty\n", "EXPORTLEFT:empty", 0)]
    public void An_export_fallback_with_a_script_death_requires_an_explicit_job_marker(string dump, string key, int expected)
    {
        // Python accepts this trim start; the C# port deliberately leaves underscore forms to Blender.
        Assert.Equal(expected, Run(dump, animated: true, key: key, argv: "0_0|24"));
    }
}
