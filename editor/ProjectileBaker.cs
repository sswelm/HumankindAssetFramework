// ProjectileBaker.cs (ENC editor) — EXPERIMENTAL 4th injection axis: PROJECTILES (the kamikaze-drone-as-munition).
//
// A ProjectileAsset (Amplitude.Mercury.Data.World.ProjectileAsset) has NO mesh field. Its visuals are three HgFx
// particle effects — muzzle (launch flash), trail (what you see flying), impact — each an FxEvolverMaterial GUID, plus
// speed/slowingFactor/missProbability/missSpread. HgFx is a GPU compute-particle system, BUT one of its render modes is
// MESH particles, backed by the SAME FxMesh + content-layer machinery our pawn/prop/district bakes already feed
// (FxComponentMeshContentManager.ContentLayer; FxVisualParticleAdder.fxMesh is a FakeAssetReference(FxMesh)). So a
// projectile that renders a physical drone is the same asset type we already produce — wired into the FX graph.
//
// This window starts where every axis started: a DUMP. Paste a vanilla projectile's Amplitude GUID (from the SDK Asset
// Picker's info panel — select e.g. Projectile_ThrownSpear_01 and copy the Guid line) to log its FX GUIDs + speed, then
// FOLLOW each FX one hop into its FxEvolverMaterial and surface every GUID / mesh-typed field — so we learn empirically
// where a mesh-particle trail keeps its mesh (the field we'll later swap to our baked drone). Mirrors PropBaker's Dump.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

public class ProjectileBakerWindow : EditorWindow
{
    const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    [MenuItem("Tools/HAF/Projectile Lab (munitions)")]
    static void Open() => GetWindow<ProjectileBakerWindow>("Projectile Lab");

    // [SerializeField] so Unity preserves the window + its form across a DOMAIN RELOAD (any script recompile / bake /
    // Play toggle) instead of discarding it — matches the Model Factory (which survives reloads for this reason).
    [SerializeField] string dumpGuid = "";
    [SerializeField] string status = "";
    [SerializeField] Vector2 scroll;
    // bake
    [SerializeField] string donorProjGuid = "", impactDonorGuid = "", modelFile = "", resourceName = "KamikazeDrone";
    [SerializeField] float size = 0.9f, importSize = 100f, speed = 0f;
    [SerializeField] Vector3 rotation, importAngles, posOffset;
    [SerializeField] int targetTris = 3000;
    [SerializeField] Color tintColor = Color.black;

    const string P = "ENC.ProjLab.";
    void OnEnable()
    {
        dumpGuid = EditorPrefs.GetString(P + "dumpGuid", dumpGuid);
        donorProjGuid = EditorPrefs.GetString(P + "donorProjGuid", donorProjGuid);
        impactDonorGuid = EditorPrefs.GetString(P + "impactDonorGuid", impactDonorGuid);
        modelFile = EditorPrefs.GetString(P + "modelFile", modelFile);
        resourceName = EditorPrefs.GetString(P + "resourceName", resourceName);
        size = EditorPrefs.GetFloat(P + "size", size);
        importSize = EditorPrefs.GetFloat(P + "importSize", importSize);
        speed = EditorPrefs.GetFloat(P + "speed", speed);
        targetTris = EditorPrefs.GetInt(P + "targetTris", targetTris);
        importAngles = new Vector3(EditorPrefs.GetFloat(P + "impX", 0), EditorPrefs.GetFloat(P + "impY", 0), EditorPrefs.GetFloat(P + "impZ", 0));
        rotation = new Vector3(EditorPrefs.GetFloat(P + "rotX", 0), EditorPrefs.GetFloat(P + "rotY", 0), EditorPrefs.GetFloat(P + "rotZ", 0));
        posOffset = new Vector3(EditorPrefs.GetFloat(P + "posX", 0), EditorPrefs.GetFloat(P + "posY", 0), EditorPrefs.GetFloat(P + "posZ", 0));
        tintColor = new Color(EditorPrefs.GetFloat(P + "tintR", 0), EditorPrefs.GetFloat(P + "tintG", 0), EditorPrefs.GetFloat(P + "tintB", 0), EditorPrefs.GetFloat(P + "tintA", 1));
    }
    void SavePrefs()
    {
        EditorPrefs.SetString(P + "dumpGuid", dumpGuid);
        EditorPrefs.SetString(P + "donorProjGuid", donorProjGuid);
        EditorPrefs.SetString(P + "impactDonorGuid", impactDonorGuid);
        EditorPrefs.SetString(P + "modelFile", modelFile);
        EditorPrefs.SetString(P + "resourceName", resourceName);
        EditorPrefs.SetFloat(P + "size", size); EditorPrefs.SetFloat(P + "importSize", importSize);
        EditorPrefs.SetFloat(P + "speed", speed); EditorPrefs.SetInt(P + "targetTris", targetTris);
        EditorPrefs.SetFloat(P + "impX", importAngles.x); EditorPrefs.SetFloat(P + "impY", importAngles.y); EditorPrefs.SetFloat(P + "impZ", importAngles.z);
        EditorPrefs.SetFloat(P + "rotX", rotation.x); EditorPrefs.SetFloat(P + "rotY", rotation.y); EditorPrefs.SetFloat(P + "rotZ", rotation.z);
        EditorPrefs.SetFloat(P + "posX", posOffset.x); EditorPrefs.SetFloat(P + "posY", posOffset.y); EditorPrefs.SetFloat(P + "posZ", posOffset.z);
        EditorPrefs.SetFloat(P + "tintR", tintColor.r); EditorPrefs.SetFloat(P + "tintG", tintColor.g); EditorPrefs.SetFloat(P + "tintB", tintColor.b); EditorPrefs.SetFloat(P + "tintA", tintColor.a);
    }
    void OnDisable() { SavePrefs(); }

    // CACHED (parity with PropBaker.FindType): MakeAmpliGuid runs on every repaint via canBake, and the uncached scan
    // enumerated every loaded assembly each time — a per-mouse-move CPU/GC hit. Loaded types don't change outside a
    // domain reload, which resets this static cache anyway (negative results cached too, same reload-scoped validity).
    static readonly Dictionary<string, Type> typeCache = new Dictionary<string, Type>();
    static Type FindType(string full)
    {
        if (typeCache.TryGetValue(full, out var cached)) return cached;
        var t = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(full)).FirstOrDefault(x => x != null);
        typeCache[full] = t;
        return t;
    }

    // GetField can't see PRIVATE fields of BASE classes (AssetReference<T> hides its guid there) — walk the chain.
    static FieldInfo FindFieldDeep(Type t, params string[] names)
    {
        for (; t != null; t = t.BaseType)
            foreach (var n in names)
            {
                var f = t.GetField(n, BF | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
        return null;
    }

    // Accepts BOTH the Unity 32-hex Guid (Asset Picker info panel) AND Amplitude's "a,b,c,d". (See PropBaker for the
    // nibble-swap encoding, auto-calibrated during the portrait work.)
    static object MakeAmpliGuid(string text)
    {
        var gt = FindType("Amplitude.Framework.Guid");
        if (gt == null) return null;
        text = (text ?? "").Trim();
        int[] ints;
        if (text.Length == 32 && text.All(Uri.IsHexDigit))
        {
            var bytes = new byte[16];
            for (int i = 0; i < 16; i++)
            {
                byte b = Convert.ToByte(text.Substring(i * 2, 2), 16);
                bytes[i] = (byte)(((b << 4) | (b >> 4)) & 0xFF);   // nibble-swap
            }
            ints = new int[4];
            for (int i = 0; i < 4; i++) ints[i] = BitConverter.ToInt32(bytes, i * 4);   // little-endian
        }
        else
        {
            var p = text.Split(',');
            if (p.Length != 4) return null;
            ints = new int[4];
            for (int i = 0; i < 4; i++) if (!int.TryParse(p[i].Trim(), out ints[i])) return null;
        }
        object g = Activator.CreateInstance(gt);
        for (int i = 0; i < 4; i++) gt.GetField(new[] { "a", "b", "c", "d" }[i], BF)?.SetValue(g, ints[i]);
        return g;
    }

    static bool IsAmpliGuid(object v) => v != null && v.GetType().FullName == "Amplitude.Framework.Guid";
    static bool IsNullGuid(object g)
    {
        if (g == null) return true;
        var t = g.GetType();
        return new[] { "a", "b", "c", "d" }.All(n => Convert.ToInt32(t.GetField(n, BF)?.GetValue(g) ?? 0) == 0);
    }
    static string GuidCsv(object g)
    {
        if (g == null) return "<null>";
        var t = g.GetType();
        return $"{t.GetField("a", BF)?.GetValue(g)},{t.GetField("b", BF)?.GetValue(g)},{t.GetField("c", BF)?.GetValue(g)},{t.GetField("d", BF)?.GetValue(g)}";
    }

    // "a,b,c,d" Amplitude GUID of an authored asset (mirrors PropBaker/DistrictBaker.AmplitudeGuid).
    static string AmplitudeGuid(UnityEngine.Object asset)
    {
        var adb = FindType("Amplitude.Framework.Asset.AssetDatabase");
        var g = adb?.GetMethod("GetAssetGUID", new[] { typeof(UnityEngine.Object) })?.Invoke(null, new object[] { asset });
        return g == null ? "" : GuidCsv(g);
    }

    // Generic Amplitude ADB load: TryLoadAsset<T>(guid) / LoadAsset<T>(guid). Returns null if the type doesn't match.
    static object LoadAsset(Type assetType, object guid)
    {
        var adb = FindType("Amplitude.Framework.Asset.AssetDatabase");
        var load = adb?.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => (m.Name == "TryLoadAsset" || m.Name == "LoadAsset") && m.IsGenericMethodDefinition && m.GetParameters().Length == 1)?
            .MakeGenericMethod(assetType);
        try { return load?.Invoke(null, new[] { guid }); } catch { return null; }
    }

    void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);
        EditorGUILayout.LabelField("Dump a vanilla projectile (the donor template)", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "A ProjectileAsset's visuals are FX (muzzle / trail / impact), not a mesh field. This dumps those FX GUIDs " +
            "+ speed, then follows each FX one hop into its FxEvolverMaterial and surfaces every GUID / mesh-typed field " +
            "— so we find where a mesh-particle trail (e.g. ThrownSpear's) keeps the mesh we'll swap for the drone.",
            MessageType.Info);
        using (new EditorGUILayout.HorizontalScope())
        {
            dumpGuid = EditorGUILayout.TextField(new GUIContent("Projectile GUID",
                "Select Projectile_ThrownSpear_01 in the SDK Asset Picker and copy the Guid line (32-hex), or paste " +
                "Amplitude's \"a,b,c,d\" ints."), dumpGuid);
            if (GUILayout.Button("Dump", GUILayout.Width(70))) DumpProjectile(dumpGuid.Trim());
        }
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Bake a munition (model → FxMesh → clone the donor's trail drawer + ProjectileAsset)", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Bakes the model to a rigid FxMesh, then CLONES the donor projectile: its trail FxEvolverMaterialDrawer with " +
            "our FxMesh swapped in (the drawer auto-loads it by GUID — no runtime registration), and a ProjectileAsset " +
            "whose trail points at the clone (muzzle + impacts kept from the donor). Then set the unit's Projectile field " +
            "to the new asset.", MessageType.None);
        using (new EditorGUILayout.HorizontalScope())
        {
            donorProjGuid = EditorGUILayout.TextField(new GUIContent("Donor projectile GUID",
                "The projectile to clone — e.g. Projectile_ThrownSpear_01 (a mesh-drawer trail). Same GUID you dumped."), donorProjGuid);
            if (GUILayout.Button("← dumped", GUILayout.Width(70))) donorProjGuid = dumpGuid;
        }
        using (new EditorGUILayout.HorizontalScope())
        {
            impactDonorGuid = EditorGUILayout.TextField(new GUIContent("Impact donor GUID (opt.)",
                "OPTIONAL: pull the impact FX (Default Impact + the per-surface Material-To-Impact rows) from a DIFFERENT " +
                "projectile than the trail. Blank = keep the trail donor's impacts. Use this to give a MESH donor (e.g. " +
                "Torpedo — visible + dark) the explosions of a SPRITE donor (e.g. CanonObusier — boom). Impacts are just FX, " +
                "so the donor being a sprite doesn't matter here."), impactDonorGuid);
            if (GUILayout.Button("← dumped", GUILayout.Width(70))) impactDonorGuid = dumpGuid;
        }
        resourceName = EditorGUILayout.TextField("Resource name", resourceName);
        using (new EditorGUILayout.HorizontalScope())
        {
            modelFile = EditorGUILayout.TextField("Model file", modelFile);
            if (GUILayout.Button("Browse", GUILayout.Width(70)))
            {
                var p = EditorUtility.OpenFilePanel("Select munition model", "", "glb,gltf,obj,fbx,blend");
                if (!string.IsNullOrEmpty(p)) modelFile = p;
            }
        }
        size = EditorGUILayout.FloatField(new GUIContent("Size (units ≈ meters)",
            "World length of the longest axis at BAKE time. A drone munition is small — ~0.8-1.2."), size);
        rotation = EditorGUILayout.Vector3Field(new GUIContent("Rotation offset (deg)",
            "Bake-time rotation on top of the auto longest-axis align. The drawer aligns the mesh to the flight direction, " +
            "so this dials nose-forward — expect to iterate."), rotation);
        importAngles = EditorGUILayout.Vector3Field(new GUIContent("FxMesh import angles",
            "Draw-time rotation on the FxMesh — tweak on the asset without re-baking."), importAngles);
        posOffset = EditorGUILayout.Vector3Field("Position offset", posOffset);
        targetTris = EditorGUILayout.IntField(new GUIContent("Target triangles",
            "Decimation ceiling. A projectile is small on screen and shares the FX layer's mesh budget — keep it lean (2-4k)."), targetTris);
        importSize = EditorGUILayout.FloatField(new GUIContent("Drawer importSize (%)",
            "The cloned drawer's in-flight scale (donor default 100). Raise to make the drone read bigger mid-air without re-baking."), importSize);
        speed = EditorGUILayout.FloatField(new GUIContent("Speed override (0 = keep donor)",
            "Projectile flight speed. 0 keeps the donor's (ThrownSpear = 35). A drone loiters slower — try 12-20."), speed);

        bool canBake = !string.IsNullOrWhiteSpace(resourceName) && !string.IsNullOrWhiteSpace(modelFile) && MakeAmpliGuid((donorProjGuid ?? "").Trim()) != null;
        using (new EditorGUI.DisabledScope(!canBake))
            if (GUILayout.Button("Bake munition", GUILayout.Height(30))) BakeMunition();
        if (!canBake) EditorGUILayout.HelpBox("Need a resource name, a model file, and a valid donor projectile GUID.", MessageType.Warning);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Tint the trail drawer (no re-bake)", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "The drawer draws the mesh through the DONOR output-layer atlas (borrowed skin). It can't show the model's own " +
            "texture without building a custom FX atlas, but it applies a uniform COLOR tint over that atlas (color × texture). " +
            "Black → a black silhouette; olive → a green cast. Sets the existing <name>_TrailDrawer's color (no re-bake); rebuild the bundle after.",
            MessageType.None);
        using (new EditorGUILayout.HorizontalScope())
        {
            tintColor = EditorGUILayout.ColorField(new GUIContent("Tint color",
                "Multiplies the borrowed atlas. Black = black drone. White = untouched (the raw borrowed skin)."), tintColor);
            if (GUILayout.Button("Apply tint", GUILayout.Width(90))) ApplyTint();
        }

        if (!string.IsNullOrEmpty(status))
        {
            EditorGUILayout.Space();
            EditorGUILayout.SelectableLabel(status, EditorStyles.textArea, GUILayout.ExpandHeight(true));
        }
        EditorGUILayout.EndScrollView();
    }

    void BakeMunition()
    {
        resourceName = resourceName.Trim(); modelFile = modelFile.Trim();
        var donorGuid = MakeAmpliGuid(donorProjGuid.Trim());
        var projType = FindType("Amplitude.Mercury.Data.World.ProjectileAsset");
        var evolverType = FindType("Amplitude.Graphics.Fx.FxEvolverMaterial");
        if (projType == null || evolverType == null) { status = "Amplitude FX types not loaded (SDK?)."; return; }
        var donorProj = LoadAsset(projType, donorGuid) as UnityEngine.Object;
        if (donorProj == null) { status = "Donor ProjectileAsset not found by that GUID."; return; }

        // Donor's trail = the mesh-drawer we clone. (private serialized guid on ProjectileAsset)
        var trailField = FindFieldDeep(projType, "trail");
        object trailGuid = trailField?.GetValue(donorProj);
        if (!IsAmpliGuid(trailGuid) || IsNullGuid(trailGuid)) { status = "Donor has no 'trail' FX — pick a mesh-drawer projectile (ThrownSpear)."; return; }
        var donorDrawer = LoadAsset(evolverType, trailGuid) as UnityEngine.Object;
        if (donorDrawer == null) { status = "Donor trail FxEvolverMaterial couldn't be loaded."; return; }
        var meshField = FindFieldDeep(donorDrawer.GetType(), "mesh");
        if (meshField == null || meshField.FieldType.FullName != "Amplitude.Framework.Guid")
        { status = $"Donor trail '{donorDrawer.GetType().Name}' has no swappable FxMesh 'mesh' field — it's not a mesh drawer. Try Torpedo/ThrownAxe."; return; }
        // SPRITE-DONOR BLOCK (review 2026-07-19): the field existing isn't enough — a sprite drawer is the SAME type
        // with a NULL mesh value, and Dump's own verdict logic knows it renders our munition INVISIBLE in flight.
        // Baking used to proceed anyway with a full success message; refuse with the same guidance as the verdict.
        var donorMeshVal = meshField.GetValue(donorDrawer);
        if (!IsAmpliGuid(donorMeshVal) || IsNullGuid(donorMeshVal))
        { status = $"Donor trail '{(donorDrawer as UnityEngine.Object)?.name}' is a SPRITE drawer (NULL mesh) — the baked munition would be INVISIBLE in flight. Pick a MESH projectile (ThrownSpear / ThrownAxe / Torpedo / Boomerang); run Dump for the verdict."; return; }

        SavePrefs();

        // 1) static bake → mesh, then bone-free rigid FxMesh (same as districts; the drawer draws a rigid mesh particle)
        var cfg = new BakeConfig
        {
            resourceName = resourceName, modelFile = modelFile, pawnDescription = "",
            rotationEuler = rotation, positionOffset = posOffset, size = size,
            normals = NormalsMode.Recalculate, smoothingAngle = 30f, convertGrid = 0,
            targetTris = targetTris, materialMode = MaterialMode.Auto, atlasMaxDim = 256,
            albedoBrightness = 1f, albedoSaturation = 1f,
        };
        var r = UniversalBaker.Build(cfg);
        if (!r.ok) { status = "Bake FAILED: " + r.error; return; }
        var mesh = AssetDatabase.LoadAssetAtPath<Mesh>("Assets/Resources/" + resourceName + "_ModelMesh.asset");
        if (mesh == null) { status = "Baked, but _ModelMesh not found."; return; }
        string fxGuidCsv = DistrictBaker.BakeFxMesh(mesh, resourceName, importAngles, out _, mergeSubMeshes: true);
        if (string.IsNullOrEmpty(fxGuidCsv)) { status = "FxMesh bake FAILED (see Console)."; return; }
        object fxGuid = MakeAmpliGuid(fxGuidCsv);
        // GUARD (parity with PropBaker, review 2026-08-01): FieldInfo.SetValue(struct, null) silently writes
        // default(Guid), so a null fxGuid here baked a ZERO-GUID trail drawer that reports success but shows an
        // INVISIBLE munition (the FxMesh isn't in Amplitude's database until a Build runs). Fail loudly instead.
        if (fxGuid == null)
        { status = "Amplitude GUID missing for the baked FxMesh — the asset isn't in Amplitude's database yet. Run the mod Build once, then re-bake."; Debug.LogError("[Projectile] " + status); return; }

        // 2) clone the trail drawer, swap its mesh to our FxMesh (CopySerialized carries the HgFx property tables +
        //    output layer verbatim; we only re-point the leaf and clear the cached content so it re-resolves our mesh).
        var drawer = ScriptableObject.CreateInstance(donorDrawer.GetType());
        EditorUtility.CopySerialized(donorDrawer, drawer);
        meshField.SetValue(drawer, fxGuid);
        var fmcField = FindFieldDeep(drawer.GetType(), "fxMeshContent");
        if (fmcField != null) fmcField.SetValue(drawer, Activator.CreateInstance(fmcField.FieldType));   // default(FxMeshContent) → re-resolve
        FindFieldDeep(drawer.GetType(), "meshIndex")?.SetValue(drawer, (uint)0);
        var impSizeField = FindFieldDeep(drawer.GetType(), "importSize");
        if (impSizeField != null && impSizeField.FieldType == typeof(float)) impSizeField.SetValue(drawer, importSize);
        string drawerPath = "Assets/Resources/" + resourceName + "_TrailDrawer.asset";
        drawer = WriteAssetKeepingGuid(drawer, drawerPath);
        AssetDatabase.SaveAssets(); AssetDatabase.Refresh();
        object drawerGuid = MakeAmpliGuid(AmplitudeGuid(drawer));
        if (drawerGuid == null) { status = "Cloned drawer created but has no Amplitude GUID yet — rebuild the bundle, then re-bake."; return; }

        // 3) clone the ProjectileAsset, point its trail at our drawer (muzzle + impacts kept), optional speed override.
        var proj = ScriptableObject.CreateInstance(projType);
        EditorUtility.CopySerialized(donorProj, proj);
        trailField.SetValue(proj, drawerGuid);
        if (speed > 0f) FindFieldDeep(projType, "speed")?.SetValue(proj, speed);

        // 3b) OPTIONAL impact donor: replace the impact FX (defaultImpact + materialToImpact) with those of another
        //     projectile — so a MESH donor (visible drone) gets a SPRITE donor's explosions. Impacts are plain FX refs.
        string impactNote = "";
        var impGuid = MakeAmpliGuid((impactDonorGuid ?? "").Trim());
        if (impGuid != null)
        {
            var impDonor = LoadAsset(projType, impGuid);
            if (impDonor == null) impactNote = "\n(impact donor GUID didn't resolve — kept the trail donor's impacts)";
            else
            {
                foreach (var fn in new[] { "defaultImpact", "materialToImpact", "muzzle" })
                {
                    var f = FindFieldDeep(projType, fn);
                    if (f != null) f.SetValue(proj, f.GetValue(impDonor));
                }
                impactNote = $"\nimpacts (+muzzle) FROM '{(impDonor as UnityEngine.Object)?.name}'";
            }
        }

        string projPath = "Assets/Resources/Projectile_" + resourceName + ".asset";
        proj = WriteAssetKeepingGuid(proj, projPath);
        EditorUtility.SetDirty(drawer); EditorUtility.SetDirty(proj);
        AssetDatabase.SaveAssets(); AssetDatabase.Refresh();

        string projGuid = AmplitudeGuid(proj);
        status = $"Munition baked for '{resourceName}':\n" +
                 $"FxMesh {fxGuidCsv}  (verts={mesh.vertexCount})\n" +
                 $"TRAIL DRAWER {drawerPath}\n  GUID = {GuidCsv(drawerGuid)}\n" +
                 $"PROJECTILE {projPath}\n  GUID = {projGuid}{impactNote}\n" +
                 "→ Build the bundle, then set the unit's Projectile field to this asset.\n" +
                 "(projectile GUID copied to clipboard)";
        EditorGUIUtility.systemCopyBuffer = projGuid;
        Debug.Log("[Projectile] " + status);
        Selection.activeObject = proj;
    }

    void DumpProjectile(string csv)
    {
        var guid = MakeAmpliGuid(csv);
        if (guid == null) { status = "Bad GUID — expected a 32-hex Guid or four ints \"a,b,c,d\"."; return; }
        var projType = FindType("Amplitude.Mercury.Data.World.ProjectileAsset");
        if (projType == null) { status = "ProjectileAsset type not loaded (SDK not present?)."; return; }
        var proj = LoadAsset(projType, guid);
        if (proj == null) { status = "ProjectileAsset not found by that GUID (is it a projectile?)."; return; }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[Projectile] '{(proj as UnityEngine.Object)?.name}'  type={proj.GetType().Name}");

        // The FX GUIDs to follow (muzzle/trail/defaultImpact are private serialized guids on ProjectileAsset).
        var fxGuids = new List<(string label, object g)>();
        for (var t = proj.GetType(); t != null && t != typeof(ScriptableObject); t = t.BaseType)
            foreach (var f in t.GetFields(BF | BindingFlags.DeclaredOnly))
            {
                object v; try { v = f.GetValue(proj); } catch { continue; }
                sb.AppendLine($"  {t.Name}.{f.Name} = {Describe(v)}");
                if (IsAmpliGuid(v) && !IsNullGuid(v)) fxGuids.Add((f.Name, v));
                // materialToImpact[] carries per-material Fx guids
                if (v is System.Collections.IEnumerable en && !(v is string))
                    foreach (var it in en)
                    {
                        var gf = it != null ? FindFieldDeep(it.GetType(), "Fx") : null;
                        var g2 = gf?.GetValue(it);
                        if (IsAmpliGuid(g2) && !IsNullGuid(g2)) fxGuids.Add((f.Name + ".Fx", g2));
                    }
            }

        // Follow each FX one hop: load as FxEvolverMaterial and surface its GUID / mesh-typed fields.
        var evolverType = FindType("Amplitude.Graphics.Fx.FxEvolverMaterial");
        foreach (var (label, g) in fxGuids)
        {
            sb.AppendLine();
            sb.AppendLine($"  --> FX '{label}' = {GuidCsv(g)}");
            var fx = evolverType != null ? LoadAsset(evolverType, g) : null;
            if (fx == null) { sb.AppendLine("      (could not resolve as FxEvolverMaterial — may be a different Fx type)"); continue; }
            DumpFxGraph(fx, sb, "      ", 0, new HashSet<object>());
        }

        // VERDICT (at the top): is this a USABLE mesh donor? The trail must be a mesh-drawer whose 'mesh' is NON-NULL. A
        // sprite drawer (mesh = null, renders a textured sprite via texture0) has NO mesh layer, so our injected drone mesh
        // renders NOTHING in flight. This is the difference between ThrownSpear (works) and CanonObusier (invisible).
        var trailGuid2 = FindFieldDeep(projType, "trail")?.GetValue(proj);
        string verdict;
        if (IsAmpliGuid(trailGuid2) && !IsNullGuid(trailGuid2))
        {
            var trailFx = evolverType != null ? LoadAsset(evolverType, trailGuid2) : null;
            var mg = trailFx != null ? FindFieldDeep(trailFx.GetType(), "mesh")?.GetValue(trailFx) : null;
            bool isMesh = IsAmpliGuid(mg) && !IsNullGuid(mg);
            verdict = isMesh
                ? $"VERDICT: ✓ USABLE MESH DONOR — trail '{(trailFx as UnityEngine.Object)?.name}' draws a real mesh (mesh={GuidCsv(mg)}). Safe to bake."
                : $"VERDICT: ✗ SPRITE DONOR — trail '{(trailFx as UnityEngine.Object)?.name}' has a NULL mesh (textured sprite, not a mesh). " +
                  "Our injected drone would be INVISIBLE. Pick a MESH projectile (ThrownSpear / ThrownAxe / Torpedo / Boomerang).";
        }
        else verdict = "VERDICT: this projectile has no 'trail' FX — not a mesh donor.";
        sb.Insert(0, verdict + "\n\n");

        Debug.Log(sb.ToString());
        status = sb.ToString();
        EditorPrefs.SetString(P + "dumpGuid", dumpGuid);
        // Auto-fill the donor field ONLY on a usable verdict — dumping CanonObusier to INSPECT it used to silently
        // repoint the donor at a sprite drawer, and the next Bake shipped an invisible munition (review 2026-07-19).
        if (verdict.StartsWith("VERDICT: ✓"))
        {
            donorProjGuid = dumpGuid;
            EditorPrefs.SetString(P + "donorProjGuid", donorProjGuid);
        }
    }

    // Write `built` to `path` while PRESERVING the existing asset's GUID if one is already there — so the unit's Projectile
    // reference (and the drawer↔projectile links) survive a re-bake. DeleteAsset+CreateAsset would mint a NEW GUID every
    // bake and silently break the FPV's Projectile field (→ nothing fires). Returns the live asset object to keep using.
    static ScriptableObject WriteAssetKeepingGuid(ScriptableObject built, string path)
    {
        var existing = AssetDatabase.LoadMainAssetAtPath(path) as ScriptableObject;
        if (existing != null && existing.GetType() == built.GetType())
        {
            EditorUtility.CopySerialized(built, existing);   // overwrite contents, keep the .meta GUID
            UnityEngine.Object.DestroyImmediate(built);      // discard the temp instance
            EditorUtility.SetDirty(existing);
            return existing;
        }
        AssetDatabase.DeleteAsset(path);
        AssetDatabase.CreateAsset(built, path);
        return built;
    }

    // Read a drawer PropertyInfo static field's Index (e.g. ColorProperty -> 35), falling back if the layout changed.
    static int PropIndex(Type drawerType, string staticFieldName, int fallback)
    {
        var f = drawerType.GetField(staticFieldName, BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
        var pi = f?.GetValue(null);
        var idx = pi?.GetType().GetField("Index", BF)?.GetValue(pi);
        return idx is int i ? i : fallback;
    }

    // Set the (already-baked) trail drawer's uniform color tint — color × the borrowed atlas. No re-bake; rebuild the
    // bundle after. The drawer gates custom values behind UseCustomValue(propertyIndex), so we flip that on for 'color'.
    void ApplyTint()
    {
        string path = "Assets/Resources/" + resourceName.Trim() + "_TrailDrawer.asset";
        var drawer = AssetDatabase.LoadMainAssetAtPath(path);
        if (drawer == null) { status = "No trail drawer at " + path + " — bake the munition first."; return; }
        var colorField = FindFieldDeep(drawer.GetType(), "color");
        if (colorField == null || colorField.FieldType.Name != "HgFxColorTableProperty")
        { status = "Drawer has no HgFxColorTableProperty 'color' field (type changed?)."; return; }

        // color = HgFxColorTableProperty.DefaultValue(tintColor)  (Value0=Value1=tint, constant over lifetime)
        var ctpType = colorField.FieldType;
        var def = ctpType.GetMethod("DefaultValue", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Color) }, null);
        if (def == null) { status = "HgFxColorTableProperty.DefaultValue(Color) not found."; return; }
        colorField.SetValue(drawer, def.Invoke(null, new object[] { tintColor }));

        // The color TABLE only reaches the render if exportColor is ON (else the mesh shows the raw atlas). Force it true.
        var expField = FindFieldDeep(drawer.GetType(), "exportColor");
        if (expField != null && expField.FieldType == typeof(bool)) expField.SetValue(drawer, true);

        // Each of these only applies if its per-property UseCustomValue flag is set (unless the material has no prototype,
        // in which case all are custom). Flip them on explicitly so the tint survives prototype inheritance.
        var setUse = drawer.GetType().GetMethod("SetUseCustomValue", new[] { typeof(int), typeof(bool) });
        int colorIndex = PropIndex(drawer.GetType(), "ColorProperty", 35);
        int exportIndex = PropIndex(drawer.GetType(), "ExportColorProperty", 18);
        setUse?.Invoke(drawer, new object[] { colorIndex, true });
        setUse?.Invoke(drawer, new object[] { exportIndex, true });

        EditorUtility.SetDirty(drawer);
        AssetDatabase.SaveAssets(); AssetDatabase.Refresh();
        status = $"Tinted '{resourceName}' drawer to RGBA({tintColor.r:0.##},{tintColor.g:0.##},{tintColor.b:0.##},{tintColor.a:0.##}) " +
                 $"(color prop {colorIndex}, exportColor on). Rebuild the bundle to see it.";
        Debug.Log("[Projectile] " + status);
        // (deliberately does NOT touch the clipboard — it may hold the projectile GUID from Bake, which the user is
        //  about to paste into the unit's Projectile field; wiping it here killed that workflow. Review 2026-07-19.)
    }

    // Recursively surface any field that is a GUID or whose type name mentions Mesh — depth-limited, cycle-guarded.
    void DumpFxGraph(object obj, System.Text.StringBuilder sb, string indent, int depth, HashSet<object> seen)
    {
        if (obj == null || depth > 3 || !seen.Add(obj)) return;
        sb.AppendLine($"{indent}{(obj as UnityEngine.Object)?.name ?? obj.GetType().Name}  ({obj.GetType().Name})");
        for (var t = obj.GetType(); t != null && t != typeof(ScriptableObject) && t != typeof(object); t = t.BaseType)
            foreach (var f in t.GetFields(BF | BindingFlags.DeclaredOnly))
            {
                object v; try { v = f.GetValue(obj); } catch { continue; }
                if (v == null) continue;
                var tn = f.FieldType.Name;
                bool meshy = tn.IndexOf("Mesh", StringComparison.OrdinalIgnoreCase) >= 0
                          || f.Name.IndexOf("mesh", StringComparison.OrdinalIgnoreCase) >= 0;
                if (IsAmpliGuid(v))
                {
                    if (!IsNullGuid(v)) sb.AppendLine($"{indent}  {t.Name}.{f.Name} = GUID {GuidCsv(v)}{(meshy ? "   <== MESH?" : "")}");
                    continue;
                }
                if (meshy) sb.AppendLine($"{indent}  {t.Name}.{f.Name} : {tn} = {Describe(v)}   <== MESH-TYPED");
                // recurse into nested Amplitude/Unity objects (not primitives/strings/collections of primitives)
                if (v is UnityEngine.Object || (v.GetType().Namespace ?? "").StartsWith("Amplitude"))
                    DumpFxGraph(v, sb, indent + "    ", depth + 1, seen);
            }
    }

    static string Describe(object v)
    {
        if (v == null) return "<null>";
        if (IsAmpliGuid(v)) return GuidCsv(v);
        var gf = FindFieldDeep(v.GetType(), "guid", "Guid");
        if (gf != null && IsAmpliGuid(gf.GetValue(v))) return $"{v.GetType().Name}({GuidCsv(gf.GetValue(v))})";
        return v.ToString();
    }
}
