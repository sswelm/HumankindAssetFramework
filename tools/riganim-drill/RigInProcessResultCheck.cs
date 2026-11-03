using System;

// The reader/export oracle uses Blender's glTF round trip. It cannot establish that a direct-FBX
// production job is safe: when the import policy requires Blender, the wrapper MUST decline it.
internal static class RigInProcessResultCheck
{
    public static string Problem(string expectedLeave, BlenderRigAnimInProcess.Outcome result, bool fileExists)
    {
        if (expectedLeave == null)
            return result.UseBlender != null && result.UseBlender.StartsWith(HafModelLoader.FbxRigLeavePrefix, StringComparison.Ordinal)
                ? "the in-process wrapper applied the FBX rig guard to a source the policy allows" : null;
        if (result.Written || fileExists)
            return "the in-process wrapper wrote a file although the FBX rig policy requires Blender";
        if (result.ScriptExit)
            return "the in-process wrapper reported a script exit instead of the required FBX rig fallback";
        if (!string.Equals(result.UseBlender, expectedLeave, StringComparison.Ordinal))
            return "the in-process wrapper did not report the required FBX rig fallback: " + expectedLeave;
        if (result.Log.Count != 0)
            return "the in-process wrapper ran rig stages before the required FBX rig fallback";
        return null;
    }
}
