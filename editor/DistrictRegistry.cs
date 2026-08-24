// DistrictRegistry.cs (ENC editor) — the District Factory's config store: haf_districts.json in the game's
// BepInEx/config, read by the plugin's district repoint (UniversalInjectPatch.EnsureDistrictConfig). Since 2026-08-20 a
// ONE-file registry like ModelRegistry (git-tracked source, deployed build artifact, pinpointed corruption, one-click
// recovery — engine: SingleSourceRegistry) but for DISTRICT
// models: each entry binds one district (ConstructibleDefinitionName) to one baked FxMesh GUID.
//
// The RUNTIME reads only { district, fxMeshGuid, isolate } (Newtonsoft JObject — extra fields ignored); everything
// else here is BAKE-TIME state so the window can reload + re-bake an entry with its knobs intact. Same JsonUtility
// caveat as ModelRegistry: the editor WRITES with JsonUtility, the plugin must keep parsing with Newtonsoft.

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

// One extra model composed onto the district tile at bake ("pizza topping"): baked with its own knobs, grounded to
// the BASE model's floor, placed by facing + posOffset, then merged into the entry's single mesh + super-atlas.
// Purely bake-time — the runtime still ships ONE FxMesh + ONE atlas per entry.
[Serializable]
public class DistrictPart
{
    public string modelFile = "";      // .glb/.gltf/.obj/.fbx/.blend for this part
    public float size = 2f;            // world length of the part's longest axis (its own Size knob)
    public Vector3 rotation;           // stand-it-up rotation offset, baked in the part's own import (same semantic as the entry's)
    public float facing = 0f;          // turn the part on the tile (deg about the vertical)
    public Vector3 posOffset;          // place it: X/Z slide across the tile, Y lifts off the base's floor
    public float alphaBoost = 1f;      // cutout-foliage fullness: multiplies the part's texture alpha at compose (1 = as authored). Sources authored for a LOW alpha cutoff (the beech: 0.227) erode to slivers against the game's fixed threshold — 2-3 restores full leaves.
    public float leafScale = 1f;       // GEOMETRY: scale every small disconnected triangle island (leaf cards) around its own centroid at compose. Texture tricks can't outgrow the card — this makes each leaf physically bigger. Trunk/big islands untouched (size-characteristic selection).
    public List<Vector3> copies = new List<Vector3>();   // EXTRA placements of the SAME part (a grove): one bake, one atlas slot, geometry appended per copy. Each copy auto-rotates by the golden angle (137.5° x n) so the grove doesn't look cloned. Triangles multiply per copy.
    public int targetTris = 6000;      // THIS part's decimation ceiling (separate from the entry's, which is the base's): parts render small on the tile, so a low budget keeps the composed mesh under the ~65,535 per-district-mesh vertex limit even with a grove of copies. 0 = use the entry's target.
}

// One district model. `district` is the key (one custom model per district).
[Serializable]
public class DistrictDef
{
    public string district = "";       // ConstructibleDefinitionName to match (e.g. Extension_Base_BreederReactor) — RUNTIME
    public string fxMeshGuid = "";     // baked FxMesh Amplitude GUID "a,b,c,d" — RUNTIME
    public string atlasGuid = "";      // baked albedo atlas Amplitude GUID "a,b,c,d" — RUNTIME (texture injection; empty = untextured legacy entry)
    public string normalAtlasGuid = ""; // baked normal atlas GUID — RUNTIME (bound on _NormalMap instead of the neutral flat; empty = neutral)
    public string roughAtlasGuid = "";  // baked roughness atlas GUID — RUNTIME (bound on _RoughnessMap; empty = neutral matte)
    public bool isolate = true;        // true = private per-instance leaf (this district's tiles only); false = global culture-wide swap — RUNTIME
    public string groundMaterial = ""; // RUNTIME: terrain paint under this district (GroundMaterialDefinition name, e.g. Prairie_Grassland) — "" = the game's default (usually bare for a wonder). The plugin forces it via ApplyGroundMaterialDefinition.
    public string hexSculpt = "";      // RUNTIME: hexagon sculpting (HexagonSculptingDefinition name) — the raised terrain platform + strategic-zoom footprint. "" = the game's default (flat for a custom wonder). The plugin forces it via ApplyHexagonSculptingDefinition.

    // ---- MESH strategic footprint (RUNTIME) — the district's own 3D building becomes its strategic-map footprint. See
    // docs/District-Dedicated-Visual.md "MESH footprint". footprintMesh=false leaves the plugin's global config in charge. ----
    public bool footprintMesh = false;          // keep the 3D building mesh visible at strategic zoom (it IS the footprint), instead of a flat decal
    public bool footprintMeshBW = false;        // render the mesh footprint BLACK-AND-WHITE while zoomed out; full colour up close
    public bool footprintMeshFlat = false;      // squash the mesh flat on the strategic map so it reads as a sheet, not a 3D model
    public float footprintMeshFlatHeight = 0.17f; // flatten HEIGHT (size.y multiplier) when flat: ~0.02 = paper-flat but edges drown in rising terrain; ~0.17 reads flat yet clears the ground; 1 = full 3D
    public bool footprintMeshHideDecal = true;  // drop the template's inherited footprint DECAL (e.g. the MissileSilo outline) that would otherwise show beneath the mesh

    // ---- bake-time knobs (runtime ignores; kept so re-bakes reload the same settings) ----
    public string resourceName = "";   // names the baked assets (<name>_ModelMesh / _DistrictMesh / _FxMesh)
    public string modelFile = "";      // .glb/.obj/.fbx/.blend; empty = re-bake the existing resource with new settings
    public Vector3 rotation;           // bake rotation offset (deg) on top of the auto longest-axis align — near-cubic models often need Y/Z (the reactor: Y=180, Z=90)
    public float size = 5f;            // world length of the model's longest axis (a district tile is ~10; ~5 imposing, ~2.5 tile-furniture)
    public int normalsMode = 1;        // 0 KeepModel, 1 Recalculate, 2 Faceted
    public float smoothingAngle = 20f;
    public int convertGrid = 0;        // GLB->OBJ: 0 = faithful (preserve UV seams), >0 = decimate
    public int targetTris = 24000;     // quadric-decimate ceiling; districts share the 'Visual' GPU buffer (see DistrictBufferHeadroom)
    public string stripParts = "";     // Blender: comma-separated object-name substrings to DELETE before baking
    public bool reuseExtracted = false; // reuse the extracted OBJ/albedo on re-bake (keeps hand-edited textures)
    public Vector3 importAngles = Vector3.zero;   // FxMesh draw-time rotation — LEGACY (pre-Facing entries keep theirs; composed at bake). No longer a UI control: new entries stand up via `rotation`, turn via `facing`. Vanilla's own district FxMeshes use (-90,0,0) (Z-up authoring), ours bake upright.
    public float facing = 0f;          // rotation ON the tile (deg, about the drawn-space vertical) — composed on top of importAngles at bake; the safe "turn the building" knob
    public Vector3 posOffset = Vector3.zero;      // position on the tile, drawn-space world units (X/Z across the tile — a tile is ~10 — Y lifts off the ground); applied AFTER auto-level at bake
    public Vector3 posOffsetBaked = Vector3.zero; // BAKE-STATE: the posOffset the current FxMesh carries — the preview shows posOffset edits live as a delta against this
    public float clipHexPct = 0f;                 // >0 = CLIP the mesh to the tile hex at bake (100 = the exact in-game cell, 6.93 across flats), so the model tiles like a vanilla district; 0 = off
    public float foundationDepth = 0f;            // >0 = extrude the building's footprint straight DOWN into the earth by this many world units at bake (concrete plinth), so it plants on cliff/uneven tiles instead of overhanging; 0 = off
    public string footprintDonor = "";   // RUNTIME strategic-footprint donor selector GUID "a,b,c,d" (grafted by the plugin; building stays ours). Empty = keep the footprint baked into the selector.
    public string selectorGuid = "";     // RUNTIME: this district's baked SCOPED CityMapSelector GUID "a,b,c,d" (data-authored path — the reactor's route). Non-empty => the plugin renders it via the scoped path (mesh footprint etc.) instead of the legacy isolate/repoint path. Produced by "Bake strategic selector" in the window.
    public int atlasMaxDim = 1024;                // packed-atlas resolution (was hardcoded 512 — ten 1024² source sheets crushed to ~160² each on the temple); districts render close-up, 1024-2048 is right for multi-material models
    public int sourceTris = -1;                   // BAKE-STATE: the SOURCE model's triangle count before decimation (parsed from the Blender prep; -1 = unknown / no reduce ran)
    public List<DistrictPart> parts = new List<DistrictPart>();   // extra models composed onto the tile at bake (see DistrictPart) — runtime ignores (it ships as one merged mesh)
}

[Serializable]
class DistrictRegistryFile
{
    public List<DistrictDef> districts = new List<DistrictDef>();
}

public static class DistrictRegistry
{
    // THE COLLAPSE, inherited (2026-08-20): ONE file. The git-tracked project file is THE registry; the deployed
    // haf_districts.json under BepInEx/config is a BUILD ARTIFACT regenerated on every Save. Engine: SingleSourceRegistry
    // (migration, artifact sync, pinpointed corruption, one-click recovery). The source keeps its historical filename
    // (".backup.json") to spare git a rename — the name is now a misnomer; SourcePath is the honest accessor.
    static readonly SingleSourceRegistry<DistrictRegistryFile> Store = new SingleSourceRegistry<DistrictRegistryFile>(
        "[District]",
        () => Path.Combine(Application.dataPath, "Databases", "haf_districts.backup.json"),
        () => Path.Combine(ModelRegistry.ConfigDir, "haf_districts.json"),
        f => f?.districts?.Count ?? 0,
        "HAF.Districts.SingleSource", "Assets/Databases/haf_districts.backup.json", "district entries");

    public static string RegistryPath => Store.ArtifactPath;        // what the running game reads (derived)
    public static string SourcePath => Store.SourcePath;            // what the editor reads and writes (git-tracked)
    public static string ProjectBackupPath => Store.SourcePath;     // historical name, kept for callers
    public static bool LastLoadCorrupt => Store.LastLoadCorrupt;
    public static string LastCorruptDetail => Store.LastCorruptDetail;
    public static string RecoverFromArtifact() => Store.RecoverFromArtifact();
    public static string RecoverFromGit() => Store.RecoverFromGit();
    public static string TakeNotice() => Store.TakeNotice();   // self-healing event for the window status line

    static List<DistrictDef> Sort(List<DistrictDef> list)
    {
        list?.Sort((a, b) => string.Compare(a?.district, b?.district, StringComparison.OrdinalIgnoreCase));
        return list ?? new List<DistrictDef>();
    }

    public static List<DistrictDef> Load() => Sort(Store.Load()?.districts ?? new List<DistrictDef>());

    // True = written. False = nothing saved (corrupt-guard tripped, or the atomic write hit a lock) — surface it.
    public static bool Save(List<DistrictDef> districts)
    {
        Sort(districts);
        return Store.Save(new DistrictRegistryFile { districts = districts }, "the model baked but its entry");
    }

    public static bool Upsert(DistrictDef def)
    {
        var list = Load();
        list.RemoveAll(d => d.district == def.district);
        list.Add(def);
        return Save(list);
    }

    public static bool Remove(string district)
    {
        var list = Load();
        int before = list.Count;
        list.RemoveAll(d => d.district == district);
        if (list.Count == before) return false;
        return Save(list);
    }
}
