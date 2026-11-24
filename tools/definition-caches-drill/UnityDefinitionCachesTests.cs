using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

// Stand-ins for the game's definition types. The windows find a definition by its type NAME (they hold no reference
// to the game's assemblies), so a type of the same name in this drill's assembly is read exactly as the game's is.
namespace DefinitionCachesDrill
{
    public class PresentationPawnDefinition : ScriptableObject { public int AnimationCapabilityProfile; }
    public class ExtensionDistrictDefinition : ScriptableObject { }
    public class DrillCollection : ScriptableObject { }
}

// Run in the temporary project made by definition_caches_unity_drill.sh: a definition added to a database asset
// during the session must reach every kept list without a restart (the report of 2026-11-24: a pawn made in the
// editor was in the Model Factory's Pick list only after one).
public static class UnityDefinitionCachesTests
{
    const string Dir = "Assets/Databases";
    const string PawnAsset = Dir + "/PresentationPawnDefinition_Era5_Drill.asset";
    const string DistrictAsset = Dir + "/ConstructibleCommonExtensionDefinition_Drill.asset";
    const string ExtraAsset = "Assets/Other/UnitPresentation_Extra.asset";

    static string[] Private(Type owner, string method) =>
        (string[])owner.GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
    static string[] FactoryNames() => ModelFactoryWindow.GatherPawnNames();
    static string[] ResizeNames() => Private(typeof(ResizeLabWindow), "GatherScalablePawnNames");
    static string[] SoundNames() => Private(typeof(SoundWindow), "LoadPawnCatalog");
    static string[] DistrictNames() => Private(typeof(DistrictFactoryWindow), "GatherDistrictNames");
    // What each window's Pick button reads (one call per button).
    static string[] ResizeClick() => Private(typeof(ResizeLabWindow), "FreshScalablePawnNames");
    static string[] SoundClick() => Private(typeof(SoundWindow), "FreshPawnCatalog");
    static string[] DistrictClick() => Private(typeof(DistrictFactoryWindow), "FreshDistrictNames");

    static DefinitionCachesDrill.PresentationPawnDefinition AddPawn(string name, int profile = 0, string asset = PawnAsset)
    {
        var pawn = ScriptableObject.CreateInstance<DefinitionCachesDrill.PresentationPawnDefinition>();
        pawn.name = name; pawn.AnimationCapabilityProfile = profile;
        AssetDatabase.AddObjectToAsset(pawn, asset);
        return pawn;
    }

    // What the editor does when the user saves an edited database: write it, then import what changed.
    static void Save() { AssetDatabase.SaveAssets(); AssetDatabase.Refresh(); }

    public static void Run()
    {
        var log = new System.Text.StringBuilder(); int failed = 0;
        void Check(string name, bool ok) { log.AppendLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failed++; }
        // Read every list under the CURRENT generation, so each is a kept one before the next change. Without it a
        // list last read a generation ago would scan again by itself and its row would pass whatever the hook does
        // (found by planting: with the asset hook off, the Resize Lab's save row still passed).
        void Keep() { FactoryNames(); ResizeNames(); SoundNames(); DistrictNames(); }
        try
        {
            Directory.CreateDirectory(Dir); Directory.CreateDirectory("Assets/Resources");
            AssetDatabase.Refresh();
            AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<DefinitionCachesDrill.DrillCollection>(), PawnAsset);
            AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<DefinitionCachesDrill.DrillCollection>(), DistrictAsset);
            AddPawn("Era5_Common_Riflemen_01");
            var district = ScriptableObject.CreateInstance<DefinitionCachesDrill.ExtensionDistrictDefinition>();
            district.name = "Extension_Base_Harbour"; AssetDatabase.AddObjectToAsset(district, DistrictAsset);
            Save();

            // ---- the session's first read fills every list
            const string made = "Era5_Common_SemiSubmersibleTorpedoBoats_01";   // the reported pawn
            Check("first read: the Factory lists the existing pawn", FactoryNames().SequenceEqual(new[] { "Era5_Common_Riflemen_01" }));
            Check("first read: the Resize Lab lists it", ResizeNames().SequenceEqual(new[] { "Era5_Common_Riflemen_01" }));
            Check("first read: the Sound window lists it", SoundNames().SequenceEqual(new[] { "Era5_Common_Riflemen_01" }));
            Check("first read: the District Factory lists the existing district", DistrictNames().SequenceEqual(new[] { "Extension_Base_Harbour" }));
            Check("first read: a pawn that does not exist yet is no boat", !ModelFactoryWindow.IsBoatPawn(made));

            // ---- a pawn is added and NOT saved. Nothing has told the lists: they are still the kept ones
            // (this is the control - without it a drill that never kept anything would pass every row below).
            var pawn = AddPawn(made);
            int g0 = DefinitionCaches.Generation;
            Check("control: an unsaved addition alone leaves the kept list (the lists ARE kept)", !FactoryNames().Contains(made));

            // ---- the Pick click: Invalidate, then read (what every Pick button does; the Factory's is FreshPawnNames,
            // which the pre-bake check and Validate pack call as well)
            Check("Pick click / pre-bake check / Validate pack: the Factory's fresh names hold the unsaved pawn", ModelFactoryWindow.FreshPawnNames().Contains(made));
            Check("Pick click: the Factory's kept list is that fresh one", FactoryNames().Contains(made));
            // Each other window gets its OWN unsaved pawn, added after every list was read under the current
            // generation. With one shared pawn the Factory's click had already moved the generation, and the other
            // two lists scanned again by themselves: their Pick functions passed without their re-read (two plants
            // of 2026-11-24 went uncaught).
            Keep();
            AddPawn("Era5_Common_ResizeClick_01");
            Check("control: the Resize Lab's kept list lacks its unsaved pawn", !ResizeNames().Contains("Era5_Common_ResizeClick_01"));
            Check("Pick click: the Resize Lab lists it", ResizeClick().Contains("Era5_Common_ResizeClick_01"));
            Keep();
            AddPawn("Era5_Common_SoundClick_01");
            Check("control: the Sound window's kept list lacks its unsaved pawn", !SoundNames().Contains("Era5_Common_SoundClick_01"));
            Check("Pick click: the Sound window lists it", SoundClick().Contains("Era5_Common_SoundClick_01"));
            Check("Pick click: the list is kept again afterwards (same array, no second scan)", ReferenceEquals(FactoryNames(), FactoryNames()));

            // ---- saving the database: no click, the asset hook alone
            const string second = "Era5_Common_Monitors_01";
            Keep();
            AddPawn(second);
            Check("control: the unsaved second pawn is in none of the kept lists", !FactoryNames().Contains(second) && !ResizeNames().Contains(second) && !SoundNames().Contains(second));
            int g1 = DefinitionCaches.Generation;
            Save();
            Check("save: importing the saved pawn database moves the generation", DefinitionCaches.Generation != g1);
            Check("save: the Factory lists the new pawn with no click", FactoryNames().Contains(second) && FactoryNames().Contains(made));
            Check("save: the Resize Lab lists it", ResizeNames().Contains(second));
            Check("save: the Sound window lists it", SoundNames().Contains(second));

            // ---- the boat answer follows the definition (a new pawn starts on the default profile)
            Check("boat: the new pawn on the default profile is no boat", !ModelFactoryWindow.IsBoatPawn(made));
            var so = new SerializedObject(pawn);
            so.FindProperty("AnimationCapabilityProfile").intValue = 7; so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(pawn);
            Check("control: the boat answer is kept until the database is saved", !ModelFactoryWindow.IsBoatPawn(made));
            Save();
            Check("boat: after the save the pawn set to profile 7 is a boat", ModelFactoryWindow.IsBoatPawn(made));
            Check("boat: the Resize Lab still offers it (a boat is scalable)", ResizeNames().Contains(made));
            Keep();

            // ---- a human profile set later leaves the Resize Lab's list (its list reads the profile too)
            so = new SerializedObject(pawn);
            so.FindProperty("AnimationCapabilityProfile").intValue = 1; so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(pawn); Save();
            Check("profile: a pawn set to a human profile leaves the Resize Lab's list", !ResizeNames().Contains(made));
            Check("profile: and is no boat any more", !ModelFactoryWindow.IsBoatPawn(made));

            // ---- districts
            Keep();
            Check("districts: the kept list", DistrictNames().SequenceEqual(new[] { "Extension_Base_Harbour" }));
            var district2 = ScriptableObject.CreateInstance<DefinitionCachesDrill.ExtensionDistrictDefinition>();
            district2.name = "Extension_Base_TorpedoYard"; AssetDatabase.AddObjectToAsset(district2, DistrictAsset);
            Check("control: an unsaved district alone leaves the kept list", !DistrictNames().Contains("Extension_Base_TorpedoYard"));
            Check("Pick click: the District Factory lists the unsaved district", DistrictClick().Contains("Extension_Base_TorpedoYard"));
            var district3 = ScriptableObject.CreateInstance<DefinitionCachesDrill.ExtensionDistrictDefinition>();
            district3.name = "Extension_Base_Slipway"; AssetDatabase.AddObjectToAsset(district3, DistrictAsset);
            Check("control: a second unsaved district is not in the kept list", !DistrictNames().Contains("Extension_Base_Slipway"));
            Save();
            Check("save: the District Factory lists the new district with no click", DistrictNames().Contains("Extension_Base_Slipway"));

            // ---- a file that is no .asset moves nothing (the control for the hook: it does not fire on everything)
            Keep();
            int g2 = DefinitionCaches.Generation;
            var kept = FactoryNames();
            File.WriteAllText("Assets/Resources/notes.txt", "not a database"); AssetDatabase.Refresh();
            Check("other file: an imported .txt leaves the generation", DefinitionCaches.Generation == g2);
            Check("other file: and the kept list (same array)", ReferenceEquals(kept, FactoryNames()));

            // ---- a renamed pawn, and a deleted database
            Keep();
            pawn.name = "Era5_Common_Renamed_01"; EditorUtility.SetDirty(pawn); Save();
            Check("rename: the Factory lists the new name and drops the old", FactoryNames().Contains("Era5_Common_Renamed_01") && !FactoryNames().Contains(made));
            Check("rename: the Sound window too", SoundNames().Contains("Era5_Common_Renamed_01") && !SoundNames().Contains(made));
            Keep();
            int g3 = DefinitionCaches.Generation;
            AssetDatabase.DeleteAsset(PawnAsset); AssetDatabase.Refresh();
            Check("delete: removing the database moves the generation", DefinitionCaches.Generation != g3);
            Check("delete: the Factory's, the Resize Lab's and the Sound window's lists are empty", FactoryNames().Length == 0 && ResizeNames().Length == 0 && SoundNames().Length == 0);

            // ---- a database whose FILE name lacks the word (review of 2026-11-24): FindAssets also matches the names
            // of sub-assets, so the scan reads this file; a hook that judged by file name left its lists stale.
            Directory.CreateDirectory("Assets/Other"); AssetDatabase.Refresh();
            AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<DefinitionCachesDrill.DrillCollection>(), ExtraAsset);
            var inner = ScriptableObject.CreateInstance<DefinitionCachesDrill.DrillCollection>();
            inner.name = "PresentationPawnDefinitionCollection"; AssetDatabase.AddObjectToAsset(inner, ExtraAsset);
            AddPawn("Extra_First_01", 0, ExtraAsset);
            AddPawn("Era5_Boat_Case", 7, ExtraAsset);
            Save(); Keep();
            Check("unnamed database: the scan reads a database found by a sub-asset's name", FactoryNames().Contains("Extra_First_01"));
            AddPawn("Extra_Second_01", 0, ExtraAsset);
            Check("control: its unsaved pawn is not in the kept list", !FactoryNames().Contains("Extra_Second_01"));
            Save();
            Check("unnamed database: its saved pawn is listed with no click", FactoryNames().Contains("Extra_Second_01") && ResizeNames().Contains("Extra_Second_01") && SoundNames().Contains("Extra_Second_01"));

            // ---- the boat answer belongs to the exact name asked (the keys were case-insensitive, the scan is not)
            Check("boat case: a name in the wrong case is no boat", !ModelFactoryWindow.IsBoatPawn("era5_boat_case"));
            Check("boat case: the right spelling asked afterwards IS a boat", ModelFactoryWindow.IsBoatPawn("Era5_Boat_Case"));
            AssetDatabase.DeleteAsset(ExtraAsset); AssetDatabase.Refresh();
            Check("unnamed database: deleted, the lists are empty again", FactoryNames().Length == 0 && SoundNames().Length == 0);

            // ---- the Model Factory's new entry starts on Keep Model (2026-11-24); an entry stored without the field
            // still reads as Recalculate, which is what it was baked with.
            var fresh = ModelFactoryWindow.FreshEntry();
            Check("new entry: the Factory's new entry (and its Reset) starts on Keep Model", fresh.normalsMode == (int)NormalsMode.KeepModel && !fresh.animated);
            var freshAnimated = ModelFactoryWindow.FreshEntry(animated: true);
            Check("new entry: the Animation Lab's new entry is animated and on Keep Model", freshAnimated.normalsMode == (int)NormalsMode.KeepModel && freshAnimated.animated);
            var parseArgs = new object[] { "{\"models\":[{\"resourceName\":\"Old\",\"pawnDescription\":\"Era5_Common_Riflemen_01\"},{\"resourceName\":\"Flat\",\"normalsMode\":2}]}", "drill.json", null };
            object file = typeof(ModelRegistry).GetMethod("ParseRegistry", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, parseArgs);
            var read = (System.Collections.Generic.List<ModelDef>)file.GetType().GetField("models").GetValue(file);
            Check("new entry: the registry reader returns both stored entries", read != null && read.Count == 2);
            Check("new entry: a stored entry WITHOUT normalsMode still reads Recalculate", read[0].normalsMode == (int)NormalsMode.Recalculate);
            Check("new entry: a stored value is kept", read[1].normalsMode == (int)NormalsMode.Faceted);

            // ---- what one Pick click costs: a re-read of databases that are already loaded. A measurement for the
            // report, not a check. The real project holds 1,361 pawn definitions in 26 databases (2026-11-24).
            for (int c = 0; c < 26; c++)
            {
                string path = Dir + "/PresentationPawnDefinition_Timing" + c + ".asset";
                AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<DefinitionCachesDrill.DrillCollection>(), path);
                for (int i = 0; i < 54; i++)
                {
                    var p = ScriptableObject.CreateInstance<DefinitionCachesDrill.PresentationPawnDefinition>();
                    p.name = "Era" + (c % 7) + "_Timing_" + c + "_" + i; AssetDatabase.AddObjectToAsset(p, path);
                }
            }
            Save();
            var clock = new Stopwatch(); double worst = 0; int count = 0;
            for (int run = 0; run < 5; run++)
            {
                DefinitionCaches.Invalidate(); clock.Restart();
                count = FactoryNames().Length;
                clock.Stop(); worst = Math.Max(worst, clock.Elapsed.TotalMilliseconds);
            }
            Check("timing fixture: 26 databases, 1404 pawns read", count == 26 * 54);
            log.AppendLine("INFO one Pick click's re-read of " + count + " loaded pawn definitions in 26 databases: worst of 5 = " +
                worst.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " ms (stand-in definitions with one field)");
            log.AppendLine("INFO generation at the end " + DefinitionCaches.Generation + " (started " + g0 + ")");
        }
        catch (Exception ex) { failed++; log.AppendLine("FAIL " + ex); }
        log.AppendLine("TOTAL failed " + failed);
        var args = Environment.GetCommandLineArgs();
        int at = Array.IndexOf(args, "-hafDefinitionCachesOutput");
        if (at >= 0 && at + 1 < args.Length) File.WriteAllText(args[at + 1], log.ToString());
        UnityEngine.Debug.Log(log.ToString()); EditorApplication.Exit(failed == 0 ? 0 : 1);
    }
}
