// PropBaker.cs (ENC editor) — EXPERIMENTAL pawn PROP/attachment axis (custom weapons & gear; the sling experiment).
// A pawn attachment slot references a PresentationPawnFragmentMesh (the EQ_* assets) = {ModelPrefab, ModelName,
// MaterialRef}: a RIGID mesh glued to the slot's bone. The mesh must live in a MeshCollection registered with the
// game's AnimationManager (the plugin's [Props] PropCollectionGuids does that at runtime). This window authors the
// whole chain from a model file:
//   static bake (UniversalBaker) -> bone-free FxMesh (DistrictBaker.BakeFxMesh) -> MeshCollection -> FragmentMesh.
// It also has a DUMP tool: paste a vanilla fragment's Amplitude GUID (from the SDK Asset Picker's info panel) to log
// its exact field values — the authoring template (esp. MaterialRef, which must match an existing output layer).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

// A saved prop RECIPE (the Prop Lab form for one prop). Editor-side bookkeeping only — the runtime never reads
// this file; it works from the baked assets + the GUIDs the model entries carry. Stored in
// Assets/Databases/haf_props.json so recipes survive editor restarts and are re-loadable per prop (the form used
// to be one shared EditorPrefs blob, which forced overwriting the previous prop's settings to start a new one).
[Serializable]
public class PropDef
{
    public string resourceName = "", modelFile = "", materialGuid = "";
    public float size = 0.6f;
    public Vector3 rotation, posOffset;   // importAngles removed 2026-07-19: baked FxMesh angles don't survive the mod bundle (orientation = Rotation offset)
    public int targetTris = 1500;
}

public static class PropRegistry
{
    [Serializable] class PropFile { public List<PropDef> props = new List<PropDef>(); }
    const string PathJson = "Assets/Databases/haf_props.json";

    // Why the file on disk can't be read right now ("" = it can). The window shows it, so an unreadable file doesn't
    // pass for "no recipes yet".
    public static string Unreadable { get; private set; } = "";

    // Why it can't be TOLD whether a missing file existed ("" = it can): git hasn't answered yet, or couldn't. The
    // window shows it, so a list that is empty for want of an answer doesn't pass for "no recipes yet" either (review
    // of PR #101, sixth round). Not an error: a bake asks before creating anything.
    public static string Unsure { get; private set; } = "";

    // null = the file EXISTS but can't be read — never the same as "no recipes yet". That confusion wiped every recipe:
    // Upsert added one prop to the empty list an unreadable file loaded as, and wrote it back (the same defect as the
    // model registry's, PR #100). An empty result must show the "props" array in the raw text: JsonUtility reads `{}`
    // as an empty list, and every file this class writes carries the key.
    static List<PropDef> Read()
    {
        // Every look decides BOTH of the window's warnings (review of PR #101, seventh round: a read caught by a lock
        // returned before Unsure was reset, so "the registry is missing" stayed up beside "it can't be read"). Unsure is
        // reset before anything can return; Unreadable is assigned on every exit instead (Fault, MissingText, the unknown
        // branch, Parse) - not reset here, because Fault compares with it to log each fault once, not per repaint.
        Unsure = "";
        string json;
        try { json = System.IO.File.Exists(PathJson) ? System.IO.File.ReadAllText(PathJson) : null; }
        catch (Exception e) { return Fault($"it can't be read right now ({e.Message}) — another program has it open"); }
        if (json != null) gitCheck.Forget();   // present: the next time it goes missing, git is asked afresh
        else
        {
            // The SAME evidence the write path uses (review of PR #101, fourth round: the window checked only the .meta,
            // so a tracked registry without one showed as "no recipes" while a save was rightly refused). The .meta is
            // looked at live; git in the background (below), because this runs on every repaint. Three outcomes, and
            // only git's "no" reads as "no registry yet" (sixth round).
            bool meta = System.IO.File.Exists(PathJson + ".meta");
            bool? tracked = meta ? (bool?)null : GitSaysCached();
            string ev = meta ? "Unity still has its .meta" : tracked == true ? "git tracks it" : null;
            if (ev != null)
            {
                Unreadable = MissingText(ev);   // shown, not logged: an editor's save-by-rename passes through this state on every save
                return null;
            }
            if (tracked == null)
            {
                Unreadable = "";   // nothing was read: an older fault no longer applies
                Unsure = "whether a registry existed can't be told: git " + (gitCheck.Pending ? "hasn't answered yet" : "didn't answer (not installed, or too slow)");
                return null;
            }
        }
        return Parse(json);
    }

    // Git for the WINDOW, off the editor thread (review of PR #101, fifth round: asked on repaint, git could block the
    // editor for up to its 5 s timeout, and again on the next repaint). The repaint takes the latest answer that has
    // arrived and never waits; the window repaints once when a new one lands. Until the first does, the window says it
    // doesn't know yet (Unsure) rather than showing no recipes (sixth round). The write path still asks git
    // itself, synchronously: a bake has to know before it decides, and it is an explicit action that takes seconds.
    static readonly string FullPathJson = System.IO.Path.GetFullPath(PathJson);   // resolved here, on the editor thread
    static readonly BackgroundCheck gitCheck = new BackgroundCheck(() => CheckedReplace.GitTracks(FullPathJson), 5);
    static bool? GitSaysCached()
    {
        bool? tracked = gitCheck.Latest(EditorApplication.timeSinceStartup, out bool started);
        if (started) EditorApplication.update += RepaintWhenGitAnswers;
        return tracked;
    }
    static void RepaintWhenGitAnswers()
    {
        if (gitCheck.Pending) return;
        EditorApplication.update -= RepaintWhenGitAnswers;
        foreach (var w in Resources.FindObjectsOfTypeAll<PropBakerWindow>()) w.Repaint();   // its next repaint takes the answer
    }

    // What a missing file known to have existed means, and what to do about a deliberate deletion — per evidence.
    static string MissingText(string evidence) =>
        $"it is missing, but {evidence}, so it existed — another program may be saving it (it comes back by itself). If you deleted it on purpose, " +
        (evidence.StartsWith("git") ? "commit the deletion (git rm)" : "delete haf_props.json.meta too, or let Unity refresh");

    // The verdict on one text as read (null = no file: no recipes yet). A text that WAS read ends any doubt about
    // whether the file exists — also when the write path read it, not only the window's Read().
    static List<PropDef> Parse(string json)
    {
        if (json == null) { Unreadable = ""; return new List<PropDef>(); }
        Unsure = "";
        string why = null;
        List<PropDef> props = null;
        try
        {
            props = JsonUtility.FromJson<PropFile>(json)?.props;
            if ((props == null || props.Count == 0) && !(Newtonsoft.Json.Linq.JObject.Parse(json)["props"] is Newtonsoft.Json.Linq.JArray))
            { why = "it has no \"props\" array"; props = null; }
        }
        catch (Exception e) { why = Pinpoint(json) ?? e.Message; props = null; }
        if (props != null) { Unreadable = ""; return props; }
        return Fault(why);
    }

    static List<PropDef> Fault(string why)
    {
        if (Unreadable != why) Debug.LogError($"[Props] {PathJson} is unreadable — {why}. Recipes won't save until it is fixed (git has every committed version)."); // once per fault: the window calls Load() every repaint
        Unreadable = why;
        return null;
    }

    // JsonUtility's exceptions carry no location; Newtonsoft's reader names the line and column.
    static string Pinpoint(string json)
    {
        if (json == null) return null;
        try { Newtonsoft.Json.Linq.JObject.Parse(json); return null; }
        catch (Newtonsoft.Json.JsonReaderException jre) { return $"line {jre.LineNumber}, position {jre.LinePosition}: {jre.Message}"; }
        catch (Exception ex) { return ex.Message; }
    }

    public static List<PropDef> Load() => Read() ?? new List<PropDef>();

    // What a change did, as far as the file on disk is concerned (review of PR #101: a bool could not say "it may
    // have been written"). Saved = the file now holds the change (or already did). NotSaved = it doesn't: the file is
    // as it was, or holds another writer's version. Unknown = a failed rollback left it undecided (see the Console).
    public enum SaveResult { Saved, NotSaved, Unknown }

    public static SaveResult Upsert(PropDef d) => Change(d.resourceName, true, l =>
    {
        int i = l.FindIndex(x => x.resourceName == d.resourceName);
        if (i >= 0) l[i] = d; else l.Add(d);
        return true;
    });

    // The Prop Lab's one-shot migration: add the form's recipe only if the registry lacks it — decided on the file as
    // read at write time, not on the Load() that prompted it. It NEVER creates the file (review of PR #101, second
    // round): an automatic step can't tell a new registry from one another program has moved aside for a moment, and
    // guessing wrong leaves a one-recipe file in its place. Only a bake creates one, and only once the user says so.
    public static SaveResult AddIfMissing(PropDef d) => Change(d.resourceName, false, quietWithoutFile: true, apply: l =>
    {
        if (l.Any(x => x.resourceName == d.resourceName)) return false;
        l.Add(d);
        return true;
    });

    public static SaveResult Remove(string name) => Change(name, false, l => l.RemoveAll(x => x.resourceName == name) > 0);

    // EVERY change is an operation on the file as it is AT WRITE TIME (review of PR #101): read, apply, and a checked
    // write that re-applies on a conflict (CheckedReplace.Apply). A recipe another writer added between the read and
    // the write is kept; a missing file is looked at twice before it counts as absent; and the write's result is the
    // file's, whatever the asset import does afterwards.
    // mayCreate = this change may create a missing file (an explicit bake) — never one known to have existed, and only
    // once the user says so (below).
    // quietWithoutFile = a missing file is no news to this caller (the migration runs on every window open).
    static SaveResult Change(string name, bool mayCreate, Func<List<PropDef>, bool> apply, bool quietWithoutFile = false)
    {
        bool refused = false, noFile = false;
        string existed = null;   // why a missing file is known to have existed (CheckedReplace.ExistedBefore)
        bool? create = null;     // the user's answer to "create a new registry?" — asked once per change
        string declined = null;  // why a new registry was not created
        CheckedReplace.Outcome outcome;
        string note;
        try
        {
            System.IO.Directory.CreateDirectory("Assets/Databases");
            outcome = CheckedReplace.Apply(PathJson, text =>
            {
                refused = noFile = false; existed = declined = null;   // each attempt decides afresh
                if (text == null && !mayCreate) { noFile = true; return null; }
                if (text == null && (existed = CheckedReplace.ExistedBefore(PathJson)) != null) return null;
                if (text == null)
                {
                    // NOTHING ON DISK SAYS WHETHER A REGISTRY EXISTED (review of PR #101, fourth round: one with no .meta
                    // and not in git, moved aside by another program's save, was still created over). Absence can't be
                    // proven, so the person decides - and the seconds a dialog takes are also time for a save-by-rename
                    // to finish: a file that is back by then is applied to, never overwritten (File.Move refuses it).
                    if (create == null)
                        create = Application.isBatchMode ? false : EditorUtility.DisplayDialog("Create haf_props.json?",
                            $"The prop recipe registry ({PathJson}) doesn't exist.\n\n" +
                            $"If this is a new project, create it now, holding only '{name}'.\n\n" +
                            "If you do have a recipe registry and another program (a text editor, git, a sync tool) is saving it right now, " +
                            "cancel and bake again in a moment: creating it now would put a one-recipe file in its place.",
                            "Create", "Cancel");
                    if (create == false) { declined = Application.isBatchMode ? "batch mode can't ask whether to create it" : "you chose not to create it"; return null; }
                }
                var l = Parse(text);
                if (l == null) { refused = true; return null; }   // unreadable: said by Parse, and never written over
                if (!apply(l)) return null;                        // nothing to change
                return JsonUtility.ToJson(new PropFile { props = l }, true);
            }, 3, out note);
        }
        catch (Exception e)
        {
            // CheckedReplace throws only when nothing of this change was written (after a conflict the file holds the
            // other writer's version, so not "as it was")
            Debug.LogError($"[Props] not saving '{name}': {PathJson} could not be read or written ({e.Message}). Nothing of this save was written.");
            return SaveResult.NotSaved;
        }
        if (note != null) Debug.LogWarning($"[Props] {PathJson}: {note}.");
        if (noFile)
        {
            // nothing to change in a file that isn't there, and only a bake creates one
            if (!quietWithoutFile)
            {
                string ev = CheckedReplace.ExistedBefore(PathJson);
                Debug.LogWarning($"[Props] not saving '{name}': {PathJson} " + (ev != null ? MissingText(ev) + "." : "is missing."));
            }
            return SaveResult.NotSaved;
        }
        if (existed != null)
        {
            Debug.LogWarning($"[Props] not saving '{name}': {PathJson} {MissingText(existed)}, then save again. " +
                             "Creating it now could leave a one-recipe registry where the real one belongs.");
            return SaveResult.NotSaved;
        }
        if (declined != null)
        {
            Debug.LogWarning($"[Props] not saving '{name}': {PathJson} doesn't exist, and a new one was not created ({declined})." +
                             (Application.isBatchMode ? "" : " Bake again to be asked again."));
            return SaveResult.NotSaved;
        }
        if (refused)
        {
            Debug.LogError($"[Props] not saving '{name}': {PathJson} is unreadable ({Unreadable}). Refusing to overwrite it and lose the other recipes.");
            return SaveResult.NotSaved;
        }
        switch (outcome)
        {
            case CheckedReplace.Outcome.Unchanged:
                return SaveResult.Saved;   // the file already says what this change would have
            case CheckedReplace.Outcome.Conflict:
                Debug.LogWarning($"[Props] not saving '{name}': {PathJson} kept changing while this save tried to apply it (another editor, git, a sync tool). " +
                                 "That version is in place and nothing of this save was written. Try again.");
                return SaveResult.NotSaved;
            case CheckedReplace.Outcome.Unresolved:
                Debug.LogError($"[Props] saving '{name}' could not finish restoring another writer's version of {PathJson}. It may now hold this save or that version; " +
                               "inspect it and the copies named above before saving again.");
                return SaveResult.Unknown;
        }
        // WRITTEN. What Unity makes of the new file afterwards is not the save's result (review of PR #101: an import
        // error used to turn a committed Remove into "still saved").
        try { AssetDatabase.ImportAsset(PathJson); }
        catch (Exception e) { Debug.LogWarning($"[Props] {PathJson} was saved, but Unity's import of it failed ({e.Message}); it re-imports on the next refresh."); }
        return SaveResult.Saved;
    }
}

public class PropBakerWindow : EditorWindow
{
    const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    [MenuItem("Tools/HAF/Prop Lab (attachments)")]
    static void Open() => GetWindow<PropBakerWindow>("Prop Lab");

    // [SerializeField] so Unity keeps the window + its form across a DOMAIN RELOAD (recompile / bake / Play toggle)
    // instead of discarding it — matches the Model Factory / Unit Retexture windows, which survive for this reason.
    // dump
    [SerializeField] string dumpGuid = "";
    // bake
    [SerializeField] string modelFile = "", resourceName = "Sling", materialGuid = "";
    [SerializeField] string lastDumpedMaterial = "";   // MaterialRef harvested by the last Dump — the 'From dump' picker
    [SerializeField] float size = 0.6f;
    [SerializeField] Vector3 rotation, posOffset;
    [SerializeField] int targetTris = 1500;
    [SerializeField] string status = "";
    [SerializeField] Vector2 scroll;
    UnityEditor.Editor previewEditor; string previewFor = "";   // previewEditor is non-serializable (rebuilt in OnEnable)

    // Persist the dialog's settings across domain reloads / editor restarts (plain EditorWindow fields don't survive).
    const string P = "ENC.PropLab.";
    void LoadPrefs()
    {
        dumpGuid = EditorPrefs.GetString(P + "dumpGuid", dumpGuid);
        resourceName = EditorPrefs.GetString(P + "resourceName", resourceName);
        modelFile = EditorPrefs.GetString(P + "modelFile", modelFile);
        materialGuid = EditorPrefs.GetString(P + "materialGuid", materialGuid);
        size = EditorPrefs.GetFloat(P + "size", size);
        targetTris = EditorPrefs.GetInt(P + "targetTris", targetTris);
        rotation = new Vector3(EditorPrefs.GetFloat(P + "rotX", 0), EditorPrefs.GetFloat(P + "rotY", 0), EditorPrefs.GetFloat(P + "rotZ", 0));
        posOffset = new Vector3(EditorPrefs.GetFloat(P + "posX", 0), EditorPrefs.GetFloat(P + "posY", 0), EditorPrefs.GetFloat(P + "posZ", 0));
        lastDumpedMaterial = EditorPrefs.GetString(P + "lastDumpedMaterial", lastDumpedMaterial);
    }
    void SavePrefs()
    {
        EditorPrefs.SetString(P + "dumpGuid", dumpGuid); EditorPrefs.SetString(P + "resourceName", resourceName);
        EditorPrefs.SetString(P + "modelFile", modelFile); EditorPrefs.SetString(P + "materialGuid", materialGuid);
        EditorPrefs.SetFloat(P + "size", size); EditorPrefs.SetInt(P + "targetTris", targetTris);
        EditorPrefs.SetFloat(P + "rotX", rotation.x); EditorPrefs.SetFloat(P + "rotY", rotation.y); EditorPrefs.SetFloat(P + "rotZ", rotation.z);
        EditorPrefs.SetFloat(P + "posX", posOffset.x); EditorPrefs.SetFloat(P + "posY", posOffset.y); EditorPrefs.SetFloat(P + "posZ", posOffset.z);
    }

    void OnEnable()
    {
        LoadPrefs();
        if (!string.IsNullOrEmpty(resourceName)) LoadPreview(resourceName);
        // MIGRATION (one-shot): the form predates the recipe registry — seed it with the current (last-baked)
        // settings so 'Edit existing' starts populated (the Sling) instead of empty.
        if (!string.IsNullOrEmpty(resourceName) && !string.IsNullOrEmpty(modelFile)
            && !PropRegistry.Load().Any(d => d.resourceName == resourceName))   // cheap filter; AddIfMissing decides on the file itself
            PropRegistry.AddIfMissing(new PropDef { resourceName = resourceName, modelFile = modelFile, materialGuid = materialGuid,
                                              size = size, rotation = rotation,
                                              posOffset = posOffset, targetTris = targetTris });
    }
    void OnDisable() { SavePrefs(); DestroyPreview(); }

    // Destroy the preview editor safely — Unity's own GameObjectInspector.OnDisable can throw
    // "SerializedObject ... has been Disposed" on a domain reload / window close; swallow it (we're destroying it anyway).
    void DestroyPreview()
    {
        if (previewEditor == null) return;
        try { DestroyImmediate(previewEditor); } catch { }
        previewEditor = null;
    }

    // Interactive 3D preview of the baked _Model.prefab, embedded like the unit Factory's — shows decimation damage
    // (a mangled pouch) right in the dialog instead of after a relaunch.
    void LoadPreview(string name, bool forceReimport = false)
    {
        DestroyPreview();
        previewFor = name ?? "";
        if (string.IsNullOrEmpty(name)) return;
        string path = "Assets/Resources/" + name + "_Model.prefab";
        if (AssetDatabase.LoadMainAssetAtPath(path) == null) return;
        if (forceReimport)
            foreach (var dep in new[] { "Assets/Resources/" + name + "_ModelMesh.asset", path })
                if (AssetDatabase.LoadMainAssetAtPath(dep) != null)
                    AssetDatabase.ImportAsset(dep, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
        var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (go != null) previewEditor = UnityEditor.Editor.CreateEditor(go);
    }

    void DrawPreview()
    {
        if (previewEditor == null || !previewEditor.HasPreviewGUI()) return;
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Preview — " + previewFor + "   (drag to orbit, scroll to zoom)", EditorStyles.miniBoldLabel);
        var r = GUILayoutUtility.GetRect(200, 260, GUILayout.ExpandWidth(true));
        previewEditor.OnInteractivePreviewGUI(r, EditorStyles.helpBox);
    }

    // CACHED: OnGUI calls this (via MakeAmpliGuid) on every repaint, and the uncached version enumerated every type
    // of every loaded assembly each time — a per-mouse-move CPU/GC hit that made the whole editor sluggish while
    // this window was open. Loaded-assembly types don't change outside a domain reload, which resets the cache anyway.
    static readonly Dictionary<string, Type> typeCache = new Dictionary<string, Type>();
    static Type FindType(string fullName)
    {
        if (typeCache.TryGetValue(fullName, out var cached)) return cached;
        var t = AppDomain.CurrentDomain.GetAssemblies().SelectMany(SafeTypes).FirstOrDefault(x => x.FullName == fullName);
        typeCache[fullName] = t;   // negative results cached too (same reload-scoped validity)
        return t;
    }
    static Type[] SafeTypes(Assembly a) { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } }

    // GetField can't see PRIVATE fields of BASE classes (AssetReference<T> hides its guid exactly there) — walk the chain.
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

    // Accepts BOTH forms: Amplitude "a,b,c,d" (four ints) AND the Unity 32-hex GUID the SDK Asset Picker's info
    // panel shows. Hex -> Amplitude: nibble-swap each of the 16 bytes, then read four little-endian int32
    // (the encoding auto-calibrated against 537/587 real references during the portrait work).
    static object MakeAmpliGuid(string text)
    {
        var gt = FindType("Amplitude.Framework.Guid");
        if (gt == null) return null;
        text = (text ?? "").Trim();
        int[] ints = null;
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

    static string GuidCsv(object g)
    {
        if (g == null) return "<null>";
        var t = g.GetType();
        return $"{t.GetField("a", BF)?.GetValue(g)},{t.GetField("b", BF)?.GetValue(g)},{t.GetField("c", BF)?.GetValue(g)},{t.GetField("d", BF)?.GetValue(g)}";
    }

    // "a,b,c,d" of an authored asset (mirrors DistrictBaker.AmplitudeGuid). Internal: the Animation Lab's
    // Hand-prop picker resolves a picked <name>_Collection's GUID through this exact helper.
    internal static string AmplitudeGuid(UnityEngine.Object asset)
    {
        var adb = FindType("Amplitude.Framework.Asset.AssetDatabase");
        var g = adb?.GetMethod("GetAssetGUID", new[] { typeof(UnityEngine.Object) })?.Invoke(null, new object[] { asset });
        return g == null ? "" : GuidCsv(g);
    }

    void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);

        EditorGUILayout.LabelField("Dump a vanilla fragment (the authoring template)", EditorStyles.boldLabel);
        using (new EditorGUILayout.HorizontalScope())
        {
            dumpGuid = EditorGUILayout.TextField(new GUIContent("Fragment GUID",
                "GUID of an EQ_* fragment — either the 32-hex Guid the Asset Picker's info panel shows (select the asset, " +
                "e.g. EQ_DLC_04_Weapon_Boomerang_01, and copy the Guid line at the bottom) or Amplitude's \"a,b,c,d\" ints."), dumpGuid);
            if (GUILayout.Button("Dump", GUILayout.Width(70))) DumpFragment(dumpGuid.Trim());
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Bake a prop (model → FxMesh → MeshCollection → FragmentMesh)", EditorStyles.boldLabel);
        // Edit existing / New / Remove — the same recipe header as the other Labs. Recipes live in haf_props.json
        // (saved on every successful bake); switching recipes loads that prop's form, New clears it for a fresh prop.
        using (new EditorGUILayout.HorizontalScope())
        {
            var defs = PropRegistry.Load();
            var names = defs.Select(d => d.resourceName).ToArray();
            int curI = Array.IndexOf(names, resourceName);
            int sel = EditorGUILayout.Popup(new GUIContent("Edit existing",
                "Saved prop recipes (one per baked prop). Picking one loads its settings into the form below."), curI, names);
            if (sel != curI && sel >= 0)
            {
                var d = defs[sel];
                resourceName = d.resourceName; modelFile = d.modelFile; materialGuid = d.materialGuid;
                size = d.size; rotation = d.rotation; posOffset = d.posOffset;
                targetTris = d.targetTris; status = "";
                LoadPreview(resourceName);
                GUI.FocusControl(null);
            }
            if (GUILayout.Button(new GUIContent("New", "Start a fresh prop: clears the form (saved recipes and baked assets are untouched)."), GUILayout.Width(50)))
            {
                resourceName = ""; modelFile = ""; size = 0.6f;
                rotation = posOffset = Vector3.zero; targetTris = 1500;
                materialGuid = "1356489961,1316891353,-864888678,1241300466";   // the shared EQ_DLC04_Weapons default
                status = ""; DestroyPreview();
                GUI.FocusControl(null);
            }
            using (new EditorGUI.DisabledScope(curI < 0))
                if (GUILayout.Button(new GUIContent("Remove", "Forget this prop's saved recipe. Baked assets are NOT deleted."), GUILayout.Width(60))
                    && EditorUtility.DisplayDialog("Remove prop recipe", $"Forget the saved settings for '{resourceName}'?\nBaked assets stay in Assets/Resources.", "Remove", "Cancel"))
                {
                    var removed = PropRegistry.Remove(resourceName);
                    if (removed == PropRegistry.SaveResult.Saved)
                    {
                        resourceName = ""; modelFile = ""; status = ""; DestroyPreview();
                    }
                    else status = removed == PropRegistry.SaveResult.Unknown
                        ? $"Remove of '{resourceName}' could not be confirmed — haf_props.json may or may not still hold it (see the Console)."
                        : $"Remove FAILED — nothing was written for '{resourceName}' (see the Console).";
                    GUI.FocusControl(null);
                }
        }
        if (PropRegistry.Unreadable != "")
            EditorGUILayout.HelpBox("haf_props.json is unreadable — " + PropRegistry.Unreadable + "\nThe recipe list is empty only because of that; " +
                                    "nothing will be saved over it until it is fixed (git has every committed version).", MessageType.Error);
        if (PropRegistry.Unsure != "")
            EditorGUILayout.HelpBox("haf_props.json is missing, and " + PropRegistry.Unsure + ".\nThe recipe list is empty because the file is missing, " +
                                    "not because there are no recipes. A bake asks before creating a new registry.", MessageType.Warning);
        resourceName = EditorGUILayout.TextField("Resource name", resourceName);
        using (new EditorGUILayout.HorizontalScope())
        {
            modelFile = EditorGUILayout.TextField("Model file", modelFile);
            if (GUILayout.Button("Browse", GUILayout.Width(70)))
            {
                var p = EditorUtility.OpenFilePanel("Select prop model", "", "glb,gltf,obj,fbx,blend");
                if (!string.IsNullOrEmpty(p)) modelFile = p;
            }
        }
        size = EditorGUILayout.FloatField(new GUIContent("Size (units ≈ meters)",
            "World length of the longest axis. A pawn is ~1.87 units tall, so ~0.5-0.8 for a hand weapon."), size);
        rotation = EditorGUILayout.Vector3Field(new GUIContent("Rotation offset (deg)",
            "Bake-time rotation on top of the auto longest-axis align. The prop glues RIGIDLY to the slot's hand bone, so " +
            "orientation is relative to the hand — expect to iterate."), rotation);
        // FxMesh import angles UI REMOVED (2026-07-19): the baked angle field does NOT survive the mod bundle for
        // props (in-game the encoder saw defaults while the project asset carried the value — field-proven with the
        // M60). Orientation is authored with Rotation offset above (baked into the vertices, preview-visible);
        // the runtime `handPropAngles` registry override remains the relaunch-only escape hatch.
        posOffset = EditorGUILayout.Vector3Field(new GUIContent("Position offset",
            "BAKE-TIME shift of the mesh relative to the bone it glues to (in world units, ~meters) — moves the prop in the " +
            "hand. Baked into the vertices, so changing it needs a re-bake (orientation alone doesn't: use the FxMesh " +
            "import angles in the Inspector instead)."), posOffset);
        targetTris = EditorGUILayout.IntField(new GUIContent("Target triangles",
            "Decimation ceiling. Props are tiny on screen — 1000-2000 is plenty."), targetTris);
        using (new EditorGUILayout.HorizontalScope())
        {
            materialGuid = EditorGUILayout.TextField(new GUIContent("Material GUID (borrowed)",
                "MaterialRef for the fragment — MUST be a material with an existing output layer, so borrow a vanilla weapon's. " +
                "Default = the shared EQ_DLC04_Weapons material (works for any weapon prop). Or Dump a vanilla fragment above " +
                "and click 'From dump'. Hex or \"a,b,c,d\" accepted."), materialGuid);
            // GUI.FocusControl(null): a focused TextField shows its own edit buffer, so a button-set value wouldn't
            // display until the user clicked elsewhere — drop focus so the new text shows immediately.
            if (GUILayout.Button(new GUIContent("Default", "EQ_DLC04_Weapons — the shared DLC-weapon material (verified: the sling renders with it)"), GUILayout.Width(60)))
            { materialGuid = "1356489961,1316891353,-864888678,1241300466"; GUI.FocusControl(null); }
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(lastDumpedMaterial)))
                if (GUILayout.Button(new GUIContent("From dump", string.IsNullOrEmpty(lastDumpedMaterial) ? "Dump a vanilla fragment first" : "Use the MaterialRef of the last dumped fragment (" + lastDumpedMaterial + ")"), GUILayout.Width(80)))
                { materialGuid = lastDumpedMaterial; GUI.FocusControl(null); }
        }

        bool canBake = !string.IsNullOrWhiteSpace(resourceName) && !string.IsNullOrWhiteSpace(modelFile) && MakeAmpliGuid(materialGuid.Trim()) != null;
        using (new EditorGUI.DisabledScope(!canBake))
            if (GUILayout.Button("Bake prop chain", GUILayout.Height(30))) DoBake();
        if (!canBake)
            EditorGUILayout.HelpBox("Set Resource name, Model file, and a valid Material GUID (a,b,c,d — Dump a vanilla weapon fragment to get one).", MessageType.Warning);
        if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, MessageType.Info);
        DrawPreview();
        EditorGUILayout.HelpBox(
            "After baking:\n" +
            "1. Put the COLLECTION GUID into the plugin cfg: [Props] PropRegister = true, PropCollectionGuids = <guid>.\n" +
            "2. Point the pawn definition's attachment slot (e.g. Weapon_RightHand_0) at the FRAGMENT asset.\n" +
            "3. Rebuild the mod + relaunch. If the hand stays empty, check the BepInEx log for [Props] lines and " +
            "'was not registered' errors.\n" +
            "ITERATION: orientation tweaks do NOT need a re-bake — edit the <name>_FxMesh Import Angles in the Inspector " +
            "and just rebuild the mod. A RE-BAKE can regenerate asset GUIDs: re-pick the fragment on the pawn slot and " +
            "re-copy the collection GUID into the cfg afterwards (the name fallback keeps working either way).", MessageType.None);
        EditorGUILayout.EndScrollView();
    }

    // Log every serialized field of a fragment asset loaded by Amplitude GUID — Guid-ish fields as "a,b,c,d".
    void DumpFragment(string csv)
    {
        var guid = MakeAmpliGuid(csv);
        if (guid == null) { status = "Bad GUID — expected four ints \"a,b,c,d\"."; return; }
        var fragType = FindType("Amplitude.Mercury.Data.World.PresentationPawnFragment");
        var adb = FindType("Amplitude.Framework.Asset.AssetDatabase");
        if (fragType == null || adb == null) { status = "Amplitude types not loaded (SDK?)."; return; }
        var load = adb.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => (m.Name == "TryLoadAsset" || m.Name == "LoadAsset") && m.IsGenericMethodDefinition && m.GetParameters().Length == 1)?
            .MakeGenericMethod(fragType);
        var frag = load?.Invoke(null, new[] { guid });
        if (frag == null) { status = "Fragment not found by that GUID."; return; }
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[Props] fragment '{(frag as UnityEngine.Object)?.name}'  type={frag.GetType().Name}");
        for (var t = frag.GetType(); t != null && t != typeof(ScriptableObject); t = t.BaseType)
            foreach (var f in t.GetFields(BF | BindingFlags.DeclaredOnly))
            {
                object v = f.GetValue(frag);
                string val;
                if (v == null) val = "<null>";
                else if (v.GetType().FullName == "Amplitude.Framework.Guid") val = GuidCsv(v);
                else
                {
                    // reference objects (GameObjectReference etc.) carry a nested guid on their AssetReference<T> BASE — surface it
                    var gf = FindFieldDeep(v.GetType(), "guid", "Guid");
                    val = gf != null && gf.FieldType.FullName == "Amplitude.Framework.Guid"
                        ? $"{v.GetType().Name}({GuidCsv(gf.GetValue(v))})" : v.ToString();
                }
                sb.AppendLine($"  {t.Name}.{f.Name} = {val}");
            }
        Debug.Log(sb.ToString());
        status = "Dumped to Console:\n" + sb;
        // harvest the MaterialRef for the 'From dump' picker
        var mrField = FindFieldDeep(frag.GetType(), "MaterialRef");
        if (mrField != null && mrField.FieldType.FullName == "Amplitude.Framework.Guid")
        {
            lastDumpedMaterial = GuidCsv(mrField.GetValue(frag));
            EditorPrefs.SetString(P + "lastDumpedMaterial", lastDumpedMaterial);
        }
    }

    void DoBake()
    {
        // Tear down every live preview BEFORE the delete-first bake — a preview inspector watching the deleted
        // prefab throws InstantiateForAnimatorPreview(null) from Unity internals (same fix as the Animation Lab).
        DestroyPreview();
        ModelFactoryWindow.ReleasePreviews();
        AnimationLabWindow.InvalidateFitPreviews();   // a prop re-bake recreates <prop>_Mat/_ModelMesh — a stale fit preview would render magenta
        resourceName = resourceName.Trim(); modelFile = modelFile.Trim();
        var matGuid = MakeAmpliGuid(materialGuid.Trim());

        // 1) static bake via the shared core (pawnDescription is registry-only; unused by Build)
        var cfg = new BakeConfig
        {
            resourceName = resourceName, modelFile = modelFile, pawnDescription = "",
            rotationEuler = rotation, positionOffset = posOffset, size = size,
            normals = NormalsMode.Recalculate, smoothingAngle = 30f, convertGrid = 0,
            targetTris = targetTris, materialMode = MaterialMode.Auto, atlasMaxDim = 256,
            albedoBrightness = 1f, albedoSaturation = 1f,
        };
        SavePrefs();   // settings survive even if the bake (or Unity) dies mid-way
        var r = UniversalBaker.Build(cfg);
        if (!r.ok) { status = "Bake FAILED: " + r.error; return; }

        // 2) bone-free FxMesh (same requirement as districts: rigid GPU paths reject skinned vertex formats)
        var mesh = AssetDatabase.LoadAssetAtPath<Mesh>("Assets/Resources/" + resourceName + "_ModelMesh.asset");
        if (mesh == null) { status = "Baked, but _ModelMesh not found."; return; }
        string fxGuidCsv = DistrictBaker.BakeFxMesh(mesh, resourceName, Vector3.zero, out _, mergeSubMeshes: true);   // pawn-fragment encoder draws only submesh 0 — flatten the multi-material split
        if (string.IsNullOrEmpty(fxGuidCsv)) { status = "FxMesh bake FAILED (see Console)."; return; }

        // 3) MeshCollection asset: prefab = our _Model.prefab's Amplitude GUID (the fragment's ModelPrefab must MATCH it —
        //    that's how AnimationManager.GetMeshCollection finds the collection), skeleton = null (rigid prop),
        //    skinnedMeshInfos = [{ MeshName, FxMeshContent{ Guid = our FxMesh } }] (encoding fields fill at GetMeshIndex).
        var mcType = FindType("Amplitude.Mercury.Animation.MeshCollection");
        var siType = mcType?.GetNestedType("SkinnedMeshInfo", BindingFlags.Public | BindingFlags.NonPublic);
        var fmcType = FindType("Amplitude.Graphics.Fx.FxMeshContent");
        if (mcType == null || siType == null || fmcType == null) { status = "MeshCollection/FxMeshContent types not loaded (SDK?)."; return; }

        var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Resources/" + resourceName + "_Model.prefab");
        if (prefabAsset == null) { status = "Baked, but _Model.prefab not found (needed as the collection's SourcePrefab key)."; return; }
        object prefabGuid = MakeAmpliGuid(AmplitudeGuid(prefabAsset));
        object fxGuid = MakeAmpliGuid(fxGuidCsv);
        // GUARD (review 2026-07-19): FieldInfo.SetValue(struct, null) silently writes default(Guid) — a null here
        // used to bake a ZERO-GUID collection/fragment that reports success but can never match at runtime (the
        // "mammoth fallback" with nothing pointing at the cause). Same recovery as ProjectileBaker: rebuild + re-bake.
        if (prefabGuid == null || fxGuid == null)
        { status = "Amplitude GUID missing for the " + (prefabGuid == null ? "_Model.prefab" : "FxMesh") + " — the asset isn't in Amplitude's database yet. Run the mod Build once, then re-bake."; Debug.LogError("[Props] " + status); return; }
        string meshName = resourceName + "_DistrictMesh";   // the bone-free mesh BakeFxMesh wrapped (its name inside the FxMesh)

        var mc = ScriptableObject.CreateInstance(mcType);
        mcType.GetField("prefab", BF)?.SetValue(mc, prefabGuid);
        object si = Activator.CreateInstance(siType);
        siType.GetField("MeshName", BF)?.SetValue(si, meshName);
        object fmc = Activator.CreateInstance(fmcType);
        fmcType.GetField("Guid", BF)?.SetValue(fmc, fxGuid);
        fmcType.GetField("ImportAngles", BF)?.SetValue(fmc, Vector3.zero);
        siType.GetField("FxMeshContent", BF)?.SetValue(si, fmc);
        var arr = Array.CreateInstance(siType, 1); arr.SetValue(si, 0);
        mcType.GetField("skinnedMeshInfos", BF)?.SetValue(mc, arr);
        string mcPath = "Assets/Resources/" + resourceName + "_Collection.asset";
        AssetDatabase.DeleteAsset(mcPath); AssetDatabase.CreateAsset(mc, mcPath);

        // 4) PresentationPawnFragmentMesh: what the pawn's attachment slot references
        var fragType = FindType("Amplitude.Mercury.Data.World.PresentationPawnFragmentMesh");
        if (fragType == null) { status = "PresentationPawnFragmentMesh type not loaded (SDK?)."; return; }
        var frag = ScriptableObject.CreateInstance(fragType);
        var mpField = fragType.GetField("ModelPrefab", BF);
        if (mpField != null)
        {
            object mp = Activator.CreateInstance(mpField.FieldType);
            var gf = FindFieldDeep(mpField.FieldType, "guid", "Guid");   // private on the AssetReference<T> base
            if (gf == null) { status = "ModelPrefab's guid field not found (AssetReference layout changed?)."; return; }
            gf.SetValue(mp, prefabGuid);
            mpField.SetValue(frag, mp);
        }
        fragType.GetField("ModelName", BF)?.SetValue(frag, meshName);
        fragType.GetField("MaterialRef", BF)?.SetValue(frag, matGuid);
        string fragPath = "Assets/Resources/EQ_" + resourceName + "_Fragment.asset";
        AssetDatabase.DeleteAsset(fragPath); AssetDatabase.CreateAsset(frag, fragPath);
        EditorUtility.SetDirty(mc); EditorUtility.SetDirty(frag);
        AssetDatabase.SaveAssets(); AssetDatabase.Refresh();

        string mcGuid = AmplitudeGuid(mc);
        string fragGuid = AmplitudeGuid(frag);
        status = $"Prop chain baked for '{resourceName}':\n" +
                 $"FxMesh {fxGuidCsv}  (verts={mesh.vertexCount})\n" +
                 $"COLLECTION {mcPath}\n  GUID = {mcGuid}   → [Props] PropCollectionGuids\n" +
                 $"FRAGMENT {fragPath}\n  GUID = {fragGuid}   → the pawn's attachment slot\n" +
                 "(collection GUID copied to clipboard)";
        EditorGUIUtility.systemCopyBuffer = mcGuid;
        // Persist this prop's recipe so 'Edit existing' can bring it back (and the Animation Lab picker lists it).
        var saved = PropRegistry.Upsert(new PropDef { resourceName = resourceName, modelFile = modelFile, materialGuid = materialGuid,
                                                      size = size, rotation = rotation,
                                                      posOffset = posOffset, targetTris = targetTris });
        if (saved == PropRegistry.SaveResult.NotSaved) status += "\n⚠ the prop baked, but its recipe was NOT saved (see the Console).";
        else if (saved == PropRegistry.SaveResult.Unknown) status += "\n⚠ the prop baked, but saving its recipe could not be confirmed (see the Console).";
        Debug.Log("[Props] " + status);
        LoadPreview(resourceName, forceReimport: true);   // show the just-baked prop in the dialog
        ModelFactoryWindow.ReloadPreviews();              // give the Factory tab its preview back
        AnimationLabWindow.RebuildFitPreviews();          // rebuild the Lab's (fit) preview from the fresh assets
        Selection.activeObject = frag;
    }
}
