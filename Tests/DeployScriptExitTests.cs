using System;
using Xunit;

public class DeployScriptExitTests
{
    const string Expected = "DEPLOY ERROR: no animated parts to convert (0 after filtering)";
    static BakerRules.DeployBlenderRun Exit(string stdout = Expected) => new BakerRules.DeployBlenderRun
    { Completed = true, ExitCode = 1, Stdout = stdout };

    [Fact]
    public void Completed_SystemExit_with_matching_diagnostic_passes_with_Blender_chatter_and_crlf()
    {
        var run = Exit("Blender 5.1\r\nDEPLOY frame range: 0..24\r\n" + Expected + "\r\nBlender quit\r\n");
        run.Stderr = "a harmless importer warning";
        Assert.True(BakerRules.DeployScriptExitMatches(new[] { "DEPLOY frame range: 0..24", Expected }, run, false, out string error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("DEPLOY frame range: 0..24")]
    [InlineData("DEPLOY ERROR: recoil requested but no barrel/cannon")]
    [InlineData(Expected + "\nDEPLOY wrote: partial.glb")]
    [InlineData("DEPLOY ERROR: another failure\n" + Expected)]
    public void Missing_incomplete_or_different_script_diagnostic_fails(string stdout)
    {
        Assert.False(BakerRules.DeployScriptExitMatches(new[] { Expected }, Exit(stdout), false, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(-1073741819)]
    public void Successful_or_crashed_process_exit_is_not_SystemExit(int code)
    {
        var run = Exit(); run.ExitCode = code;
        Assert.False(BakerRules.DeployScriptExitMatches(new[] { Expected }, run, false, out _));
    }

    [Theory]
    [InlineData("timed out")]
    [InlineData("stderr did not finish draining")]
    public void Incomplete_run_fails_even_if_it_printed_the_expected_error(string failure)
    {
        var run = Exit(); run.Completed = false; run.Error = failure;
        Assert.False(BakerRules.DeployScriptExitMatches(new[] { Expected }, run, false, out string error));
        Assert.Contains(failure, error);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Python_exception_fails_even_with_the_expected_DEPLOY_line(bool onStderr)
    {
        var run = Exit(); const string traceback = "Traceback (most recent call last):\nRuntimeError: unexpected failure";
        if (onStderr) run.Stderr = traceback; else run.Stdout += "\n" + traceback;
        Assert.False(BakerRules.DeployScriptExitMatches(new[] { Expected }, run, false, out string error));
        Assert.Contains("Python exception", error);
    }

    [Fact]
    public void A_written_file_or_successful_write_verdict_cannot_agree_with_script_exit()
    {
        var run = Exit();
        Assert.False(BakerRules.DeployScriptExitMatches(new[] { Expected }, run, true, out _));
        run.Written = true;
        Assert.False(BakerRules.DeployScriptExitMatches(new[] { Expected }, run, false, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("DEPLOY frame range: 0..24")]
    public void Port_exit_without_its_own_error_evidence_fails(string portLine)
    {
        Assert.False(BakerRules.DeployScriptExitMatches(new[] { portLine }, Exit(), false, out _));
    }
}
