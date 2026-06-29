using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace ENCAccessProof
{
    // POC of shakee's data-driven approach: a mod ships its own AnimationManagerContent asset (listing its custom
    // skeletons); this GENERIC hook merges it into the game's loaded content at AnimationResolveDependencies — BEFORE
    // AnimationLoad registers + Apply()s — so the engine registers the skeleton natively (no manual Apply/LoadIFN/
    // fragment surgery). Validation only here: it merges + proves the skeleton got a real SkeletonId. The visible
    // unit repoint (Description.Template -> SourcePrefab) is the next step.
    //
    // Generic: it loads our mod AnimationManagerContent by GUID and merges whatever skeletons it lists. (A fully
    // generic plugin would ENUMERATE all mod AnimationManagerContent assets; hardcoded GUID here for the POC.)
    internal static class ShakeeMerge
    {
        // ENC_ModAnimationContent.asset GUID (from the editor probe). Lists the zeppelin skeleton.
        const int AmcA = 809739139, AmcB = 1322108152, AmcC = 419288479, AmcD = -727521717;
        // zeppelin skeleton ASSET GUID — for the registration proof.
        const int ZepA = 781638270, ZepB = 1224895347, ZepC = -2137756227, ZepD = -1885149832;

        internal static void Merge(object animMgr)
        {
            try
            {
                var amcType = AccessTools.TypeByName("Amplitude.Mercury.Animation.AnimationManagerContent");
                var mcType = AccessTools.TypeByName("Amplitude.Mercury.Animation.MeshCollection");
                if (amcType == null || mcType == null) return;

                var modAmc = LoadAsset(amcType, MakeGuid(AmcA, AmcB, AmcC, AmcD));
                if (modAmc == null) { Plugin.Log.LogWarning("[ShakeeMerge] mod AnimationManagerContent not loaded (rebuild the mod so it ships?)"); return; }
                var meshGuids = AccessTools.Field(amcType, "MeshCollections")?.GetValue(modAmc) as Array;
                if (meshGuids == null || meshGuids.Length == 0) { Plugin.Log.LogWarning("[ShakeeMerge] mod content has no MeshCollections"); return; }

                var modSkels = new List<object>();
                foreach (var g in meshGuids) { var s = LoadAsset(mcType, g); if (s != null) modSkels.Add(s); }
                if (modSkels.Count == 0) { Plugin.Log.LogWarning("[ShakeeMerge] none of the mod skeletons loaded"); return; }

                var lmcField = AccessTools.Field(animMgr.GetType(), "loadedMeshCollections");
                var lmc = lmcField?.GetValue(animMgr) as Array;
                int oldLen = lmc?.Length ?? 0;
                if (lmc != null) foreach (var e in lmc) if (ReferenceEquals(e, modSkels[0])) return; // already merged this pass

                var merged = Array.CreateInstance(mcType, oldLen + modSkels.Count);
                if (lmc != null) Array.Copy(lmc, merged, oldLen);
                for (int i = 0; i < modSkels.Count; i++) merged.SetValue(modSkels[i], oldLen + i);
                lmcField.SetValue(animMgr, merged);
                Plugin.Log.LogInfo($"[ShakeeMerge] merged {modSkels.Count} mod skeleton(s) into loadedMeshCollections ({oldLen} -> {merged.Length}) — AnimationLoad will register them natively");
            }
            catch (Exception e) { Plugin.Log.LogError("[ShakeeMerge] merge error: " + e); }
        }

        internal static void ProveRegistered(object animMgr)
        {
            try
            {
                var mcType = AccessTools.TypeByName("Amplitude.Mercury.Animation.MeshCollection");
                var zep = LoadAsset(mcType, MakeGuid(ZepA, ZepB, ZepC, ZepD));
                if (zep == null) { Plugin.Log.LogInfo("[ShakeeMerge] proof: zeppelin skeleton not loadable"); return; }
                var sid = GetMember(zep, "SkeletonId");
                Plugin.Log.LogInfo($"[ShakeeMerge] PROOF: zeppelin skeleton SkeletonId={sid}  (>=0 = registered natively via the merge — no manual Apply/LoadIFN)");
            }
            catch (Exception e) { Plugin.Log.LogError("[ShakeeMerge] proof error: " + e); }
        }

        static object LoadAsset(Type T, object guid)
        {
            var adb = AccessTools.TypeByName("Amplitude.Framework.Asset.AssetDatabase");
            var m = adb.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(x => (x.Name == "LoadAsset" || x.Name == "TryLoadAsset") && x.IsGenericMethodDefinition && x.GetParameters().Length >= 1);
            var g = m.MakeGenericMethod(T);
            var args = g.GetParameters().Length == 1 ? new[] { guid } : new[] { guid, null };
            return g.Invoke(null, args);
        }
        static object GetMember(object o, string n) { var t = o.GetType(); var p = AccessTools.Property(t, n); if (p != null) try { return p.GetValue(o); } catch { } var f = AccessTools.Field(t, n); return f?.GetValue(o); }
        static object MakeGuid(int a, int b, int c, int d)
        {
            var gt = AccessTools.TypeByName("Amplitude.Framework.Guid"); var g = Activator.CreateInstance(gt);
            const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            gt.GetField("a", BF)?.SetValue(g, a); gt.GetField("b", BF)?.SetValue(g, b); gt.GetField("c", BF)?.SetValue(g, c); gt.GetField("d", BF)?.SetValue(g, d); return g;
        }
    }

    [HarmonyPatch]
    internal static class ShakeeMergeHook
    {
        static MethodBase TargetMethod() { var t = AccessTools.TypeByName("Amplitude.Mercury.Animation.AnimationManager"); return t != null ? AccessTools.Method(t, "AnimationResolveDependencies") : null; }
        static void Postfix(object __instance, bool __result) { if (Plugin.MergeModContent.Value && __result) ShakeeMerge.Merge(__instance); }
    }

    [HarmonyPatch]
    internal static class ShakeeProofHook
    {
        static MethodBase TargetMethod() { var t = AccessTools.TypeByName("Amplitude.Mercury.Animation.AnimationManager"); return t != null ? AccessTools.Method(t, "AnimationLoad") : null; }
        static void Postfix(object __instance) { if (Plugin.MergeModContent.Value) ShakeeMerge.ProveRegistered(__instance); }
    }
}
