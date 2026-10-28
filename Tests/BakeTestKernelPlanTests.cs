using System.Linq;
using Xunit;

// The Bake Tests window's "Kernels" dropdown, the pure half (editor/BakeTestKernelPlan.cs): which passes a row runs under
// each mode, what its title says, and the loader row's verdict from HafNativeKernels.Status. The window itself needs
// Unity; this table is what it executes, so a changed row here is a changed run there.
public class BakeTestKernelPlanTests
{
    [Theory]
    [InlineData(BakeTestKernelMode.Native, true)]
    [InlineData(BakeTestKernelMode.Managed, true)]
    [InlineData(BakeTestKernelMode.Both, true)]
    [InlineData(BakeTestKernelMode.Native, false)]
    [InlineData(BakeTestKernelMode.Both, false)]
    public void A_row_that_cannot_reach_a_kernel_runs_once_untouched(BakeTestKernelMode mode, bool loaded)
    {
        var p = BakeTestKernelPlan.Passes(mode, kernelRow: false, loaded: loaded);
        Assert.Single(p);
        Assert.Equal(loaded, p[0].native);   // the switch is left as the loader set it
        Assert.Equal("", p[0].suffix);
    }

    [Fact]
    public void Native_mode_with_the_dll_runs_a_kernel_row_once_with_the_kernels_and_a_plain_title()
    {
        var p = BakeTestKernelPlan.Passes(BakeTestKernelMode.Native, kernelRow: true, loaded: true);
        Assert.Single(p);
        Assert.True(p[0].native);
        Assert.Equal("", p[0].suffix);
    }

    [Fact]
    public void Managed_mode_runs_a_kernel_row_once_with_the_switch_off_and_says_so()
    {
        var p = BakeTestKernelPlan.Passes(BakeTestKernelMode.Managed, kernelRow: true, loaded: true);
        Assert.Single(p);
        Assert.False(p[0].native);
        Assert.Contains("C# only", p[0].suffix);
    }

    [Fact]
    public void Both_runs_a_kernel_row_twice_kernels_first_then_managed_with_distinct_titles()
    {
        var p = BakeTestKernelPlan.Passes(BakeTestKernelMode.Both, kernelRow: true, loaded: true);
        Assert.Equal(2, p.Length);
        Assert.True(p[0].native); Assert.False(p[1].native);
        Assert.Contains("C++", p[0].suffix); Assert.Contains("C# only", p[1].suffix);
        Assert.NotEqual(p[0].suffix, p[1].suffix);
    }

    [Theory]
    [InlineData(BakeTestKernelMode.Native)]
    [InlineData(BakeTestKernelMode.Both)]
    public void Without_the_dll_a_kernel_row_runs_managed_once_and_its_title_blames_the_load(BakeTestKernelMode mode)
    {
        var p = BakeTestKernelPlan.Passes(mode, kernelRow: true, loaded: false);
        Assert.Single(p);
        Assert.False(p[0].native);
        Assert.Contains("did not load", p[0].suffix);
    }

    [Fact]
    public void Without_the_dll_managed_mode_does_not_blame_a_load_nobody_asked_for()
    {
        var p = BakeTestKernelPlan.Passes(BakeTestKernelMode.Managed, kernelRow: true, loaded: false);
        Assert.Single(p);
        Assert.DoesNotContain("did not load", p[0].suffix);
        Assert.Contains("C# only", p[0].suffix);
    }

    [Fact]
    public void No_pass_ever_asks_for_the_kernels_when_the_dll_did_not_load()
    {
        foreach (BakeTestKernelMode mode in System.Enum.GetValues(typeof(BakeTestKernelMode)))
            foreach (bool kernelRow in new[] { true, false })
                Assert.All(BakeTestKernelPlan.Passes(mode, kernelRow, loaded: false), x => Assert.False(x.native));
    }

    [Fact]
    public void The_loader_row_passes_on_a_native_status_and_names_it()
    {
        BakeTestKernelPlan.Loader("native (hafkernels.dll version 7, C:/x/hafkernels.dll)", BakeTestKernelMode.Native, out int pass, out int fail, out int skip, out string body);
        Assert.Equal((1, 0, 0), (pass, fail, skip));
        Assert.Contains("version 7", body);
    }

    [Fact]
    public void The_loader_row_fails_on_a_managed_status_with_the_reason_and_a_remedy()
    {
        BakeTestKernelPlan.Loader("managed (hafkernels.dll answered version 6, expected 7)", BakeTestKernelMode.Native, out int pass, out int fail, out int skip, out string body);
        Assert.Equal((0, 1, 0), (pass, fail, skip));
        Assert.Contains("answered version 6", body);
        Assert.Contains("restart Unity", body);
    }

    [Fact]
    public void The_loader_row_fails_in_managed_mode_too_the_question_is_whether_the_dll_loads()
    {
        BakeTestKernelPlan.Loader("managed (DllNotFoundException: hafkernels)", BakeTestKernelMode.Managed, out int pass, out int fail, out int skip, out _);
        Assert.Equal((0, 1, 0), (pass, fail, skip));
    }

    [Fact]
    public void The_loader_row_skips_only_the_deliberate_environment_switch()
    {
        BakeTestKernelPlan.Loader("managed (HAF_NATIVE_KERNELS=0)", BakeTestKernelMode.Native, out int pass, out int fail, out int skip, out string body);
        Assert.Equal((0, 0, 1), (pass, fail, skip));
        Assert.Contains("on purpose", body);
    }

    [Fact]
    public void The_loader_row_treats_a_missing_status_as_a_failure_not_a_pass()
    {
        BakeTestKernelPlan.Loader(null, BakeTestKernelMode.Native, out int pass, out int fail, out int skip, out _);
        Assert.Equal((0, 1, 0), (pass, fail, skip));
    }

    [Fact]
    public void Stored_preference_values_outside_the_labels_fall_back_to_native()
    {
        Assert.Equal(BakeTestKernelMode.Both, BakeTestKernelPlan.ParseMode(2));
        Assert.Equal(BakeTestKernelMode.Native, BakeTestKernelPlan.ParseMode(-1));
        Assert.Equal(BakeTestKernelMode.Native, BakeTestKernelPlan.ParseMode(99));
        Assert.Equal(BakeTestKernelPlan.Labels.Length, System.Enum.GetValues(typeof(BakeTestKernelMode)).Length);
    }

    [Fact]
    public void The_report_header_names_the_mode_and_the_loaded_status()
    {
        string h = BakeTestKernelPlan.HeaderLine(BakeTestKernelMode.Both, "native (hafkernels.dll version 7)");
        Assert.StartsWith("Kernels: ", h);
        Assert.Contains(BakeTestKernelPlan.Labels[2], h);
        Assert.Contains("version 7", h);
    }
}
