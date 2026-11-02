// HafModelLoader.cs - the one loader of a model source for every in-process port (step 6e of the native FBX reader,
// 2026-11-02): a .glb/.gltf through GlbReader, an .fbx through FbxImport + FbxModel - the HafModel Blender 5.1's FBX import
// followed by its glTF export would write for that file (held to Blender by tools/fbx_model_drill.sh), so the ports treat an
// .fbx like a .glb, except for the Vehicle Lab context limit below. A file the FBX reader leaves to Blender by name (a camera or light, shape keys, a shared skinned
// mesh datablock, a stack without its class, ...) comes back as null with the reason, and the caller takes its Blender route
// as it does for any other named limit; a file either reader refuses throws as GlbReader does (InvalidDataException,
// NotSupportedException). .obj and .blend stay Blender's (Unity's importer for a static .obj/.fbx the reader leaves).
using System;
using System.IO;

public static class HafModelLoader
{
    public const string CacheRevision = "2026-11-02.fbx-loader.1";
    public const string FbxVehicleLeavePrefix = "the FBX vehicle import needs Blender: ";
    public const string FbxLeavePrefix = "the FBX reader leaves this file to Blender by name: ";

    public static bool IsGltf(string path) => path != null && (path.Trim().EndsWith(".glb", StringComparison.OrdinalIgnoreCase) || path.Trim().EndsWith(".gltf", StringComparison.OrdinalIgnoreCase));
    public static bool IsFbx(string path) => path != null && path.Trim().EndsWith(".fbx", StringComparison.OrdinalIgnoreCase);
    /// <summary>A source the in-process ports read: .glb, .gltf or .fbx.</summary>
    public static bool CanRead(string path) => IsGltf(path) || IsFbx(path);
    public const string NotReadable = "the source is not a .glb/.gltf/.fbx file";

    /// <summary>The model, or null with `leave` - the FBX reader's named limit (FbxLeavePrefix + the reason) - for an .fbx the port
    /// leaves to Blender. A .glb/.gltf never leaves: it is read or it throws. Another extension throws NotSupportedException.</summary>
    public static HafModel Read(string path, out string leave)
    {
        leave = null;
        if (IsGltf(path)) return GlbReader.Read(path);
        if (!IsFbx(path)) throw new NotSupportedException(NotReadable);
        var m = FbxModel.Build(FbxImport.Read(path), out string why);
        if (m == null) { leave = FbxLeavePrefix + why; return null; }
        return m;
    }

    // VehicleProbe and vehicle_rig consume a direct FBX import on their Blender route. Exporting that
    // import to glTF changes skin weights, bone conventions and action ordering. Until that route is held
    // to the same convention, animated/skinned FBX must not silently produce different probe bounds or rigs.
    internal static string VehicleImportFallback(string path, HafModel model)
        => IsFbx(path) && (model.Skins.Count > 0 || model.Animations.Count > 0)
            ? FbxVehicleLeavePrefix + "a skinned or animated FBX (the direct FBX and glTF round-trip vehicle poses are not equivalent)" : null;

    public static HafModel ReadForVehicle(string path, out string leave)
    {
        var model = Read(path, out leave);
        if (model != null) leave = VehicleImportFallback(path, model);
        return leave == null ? model : null;
    }

    public static HafModel ReadForVehicleOrThrow(string path)
    {
        var model = ReadForVehicle(path, out string leave);
        if (model == null) throw new NotSupportedException(leave);
        return model;
    }

    /// <summary>Read, with a named FBX limit thrown as NotSupportedException (for the callers that take a reader's refusal as
    /// "Blender then" already).</summary>
    public static HafModel ReadOrThrow(string path)
    {
        var m = Read(path, out string leave);
        if (m == null) throw new NotSupportedException(leave);
        return m;
    }

    /// <summary>True for a reason this loader made for an .fbx the FBX reader leaves by name - an EXPECTED Blender fallback, not
    /// C# coverage lost (BlenderVehicleRigInProcess.ExpectedFallback). This also includes the named direct-FBX vehicle convention limit.</summary>
    public static bool IsNamedFbxLeave(string reason) => reason != null && (reason.StartsWith(FbxLeavePrefix, StringComparison.Ordinal) || reason.StartsWith(FbxVehicleLeavePrefix, StringComparison.Ordinal));
}
