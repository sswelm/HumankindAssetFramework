using Xunit;

public class RigInProcessResultCheckTests
{
    static string RequiredLeave()
    {
        var model = new HafModel(); model.Skins.Add(new HafSkin());
        return HafModelLoader.RigImportFallback("rig.fbx", model);
    }

    [Fact]
    public void Required_direct_FBX_fallback_accepts_a_clean_decline()
    {
        string reason = RequiredLeave();
        Assert.NotNull(reason);
        Assert.Null(RigInProcessResultCheck.Problem(reason,
            new BlenderRigAnimInProcess.Outcome { UseBlender = reason }, false));
    }

    [Theory]
    [InlineData("written")]
    [InlineData("partial_file")]
    [InlineData("script_exit")]
    [InlineData("missing_reason")]
    [InlineData("wrong_reason")]
    [InlineData("stage_log")]
    public void Required_direct_FBX_fallback_rejects_unsafe_or_contradictory_results(string defect)
    {
        string reason = RequiredLeave();
        var result = new BlenderRigAnimInProcess.Outcome { UseBlender = reason };
        bool fileExists = false;
        switch (defect)
        {
            case "written": result.Written = true; result.UseBlender = null; fileExists = true; break;
            case "partial_file": fileExists = true; break;
            case "script_exit": result.ScriptExit = true; break;
            case "missing_reason": result.UseBlender = null; break;
            case "wrong_reason": result.UseBlender = HafModelLoader.FbxRigLeavePrefix + "another reason"; break;
            case "stage_log": result.Log.Add("RIGANIM stage ran"); break;
        }
        Assert.NotNull(RigInProcessResultCheck.Problem(reason, result, fileExists));
    }

    [Fact]
    public void Allowed_source_can_write_but_cannot_claim_the_restricted_FBX_fallback()
    {
        Assert.Null(RigInProcessResultCheck.Problem(null,
            new BlenderRigAnimInProcess.Outcome { Written = true }, true));
        Assert.NotNull(RigInProcessResultCheck.Problem(null,
            new BlenderRigAnimInProcess.Outcome { UseBlender = RequiredLeave() }, false));
    }
}
