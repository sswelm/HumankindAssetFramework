using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json.Linq;             // provided by the game (mod.io); robust registry parse where JsonUtility no-ops in the game runtime
using static HumankindAssetFramework.DistrictInject;   // GF / ParseGuidCsv (shared probing helpers that stayed with districts)

namespace HumankindAssetFramework
{
    internal static partial class UniversalInject
    {
        // ---- EXPERIMENTAL pawn PROP/attachment axis (custom weapons & gear on attachment slots; the sling experiment) ----
        // A pawn's Attachements[] slot references a PresentationPawnFragmentMesh (the EQ_* assets) = {ModelPrefab, ModelName,
        // MaterialRef}: a RIGID mesh glued to the slot's bone, GPU-encoded at spawn. The loader hard-gates on
        // AnimationManager.GetMeshCollection(ModelPrefab.Guid) finding a REGISTERED collection ("was not registered ...
        // please add it to AnimationManagerContent" -> draws nothing). AnimationManager.RegisterMeshCollection is PUBLIC and
        // also uploads the collection's meshes to the GPU content manager (LoadIFN) — so one call crosses the gate. Skeleton
        // DERIVES from MeshCollection, so our baked Skeleton assets qualify directly. Retry per-frame: the manager instance
        // and its internal list only exist once the presentation loads, and our bundle assets load async.
        // TIMING is the crux: pawn definitions resolve their fragments INSIDE the game's loading chunk, so an Update-tick
        // registration loses the race by construction — the pawn definition then fails its Load, never registers a pawn
        // id, and its units draw as pawn definition 0 (the MAMMOTH — observed). The real seam is Hk_PropRegister below:
        // a postfix on AnimationManager.AnimationLoad, which rebuilds the manager's collection list and registers the
        // game's own collections; we append ours right there, before any pawn resolves. The Update tick stays as a
        // late-repair safety net only.
        // ---- EXPERIMENTAL projectile axis (docs/Projectiles.md) ----
        // A unit's PresentationPawnDefinition.Projectile (a ProjectileAssetReference) is read at attack time to spawn the
        // flying FX. We load the pawn def AND our baked ProjectileAsset by GUID and re-point the reference's inner guid —
        // the same AssetReference-guid swap the prop axis uses for a fragment's ModelPrefab. Applied at AnimationLoad (data
        // is up, before combat); idempotent, so re-running each session just re-asserts it.
        static bool projParsed;
        [SessionScoped(Manual = "RearmProjectileOverrides (registry-derived, re-parsed per session)")] static readonly List<(object pawnGuid, object projGuid, string raw)> projOverrides = new List<(object, object, string)>();
        internal static void RearmProjectileOverrides() { projParsed = false; projOverrides.Clear(); }

        // Comma-ONLY 4-int parser. (ParseGuidCsv splits on '-' too, which would corrupt the negative ints a projectile
        // GUID routinely has, e.g. -839228096.)
        internal static object ParseGuid4(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var p = s.Split(',');
            if (p.Length == 4 && int.TryParse(p[0].Trim(), out var a) && int.TryParse(p[1].Trim(), out var b)
                && int.TryParse(p[2].Trim(), out var c) && int.TryParse(p[3].Trim(), out var d))
                return MakeGuid(a, b, c, d);
            return null;
        }

        static MethodInfo adbLoadAsset;
        internal static object LoadAmpliAsset(Type assetType, object guid)
        {
            if (adbLoadAsset == null)
            {
                var adb = GameBinding.AssetDatabase;
                adbLoadAsset = adb?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => (m.Name == "TryLoadAsset" || m.Name == "LoadAsset") && m.IsGenericMethodDefinition && m.GetParameters().Length == 1);
            }
            try { return adbLoadAsset?.MakeGenericMethod(assetType).Invoke(null, new[] { guid }); } catch { return null; }
        }

        // AssetReference<T> hides its guid on a private base — walk the chain (mirrors PropBaker.FindFieldDeep).
        static FieldInfo FindGuidField(Type t)
        {
            for (; t != null; t = t.BaseType)
                foreach (var n in new[] { "guid", "Guid" })
                {
                    var f = t.GetField(n, BF | BindingFlags.DeclaredOnly);
                    if (f != null && f.FieldType.Name == "Guid") return f;
                }
            return null;
        }

        internal static void ApplyProjectileOverrides(string cfg)
        {
            if (!projParsed)
            {
                projParsed = true;
                foreach (var entry in (cfg ?? "").Split(';'))
                {
                    var e = entry.Trim(); if (e.Length == 0) continue;
                    int eq = e.IndexOf('=');
                    if (eq < 0) { Plugin.Log.LogError($"[Projectile] override needs '<pawnDefGuid>=<projectileGuid>' (got '{e}')"); continue; }
                    var pawnGuid = ParseGuid4(e.Substring(0, eq));
                    var projGuid = ParseGuid4(e.Substring(eq + 1));
                    if (pawnGuid == null || projGuid == null) { Plugin.Log.LogError($"[Projectile] both sides must be four ints \"a,b,c,d\" (got '{e}')"); continue; }
                    projOverrides.Add((pawnGuid, projGuid, e));
                }
                if (projOverrides.Count > 0) Plugin.Diag($"[Projectile] {projOverrides.Count} override(s) to apply");
            }
            if (projOverrides.Count == 0) return;

            var pawnType = GameBinding.PresentationPawnDefinition;
            var projType = GameBinding.ProjectileAsset;
            if (pawnType == null || projType == null) { Plugin.Log.LogError("[Projectile] ProjectileAsset/PresentationPawnDefinition type not found (game update?)"); return; }
            var projField = AccessTools.Field(pawnType, "Projectile");   // ProjectileAssetReference (declared on the base; AccessTools walks it)
            if (projField == null) { Plugin.Log.LogError("[Projectile] pawn def has no 'Projectile' field (game update?)"); return; }

            foreach (var (pawnGuid, projGuid, raw) in projOverrides)
            {
                var pawnDef = LoadAmpliAsset(pawnType, pawnGuid) as UnityEngine.Object;
                if (pawnDef == null) { Plugin.Log.LogWarning($"[Projectile] pawn def GUID didn't resolve ('{raw}') — check the GUID / that its bundle is loaded."); continue; }
                var proj = LoadAmpliAsset(projType, projGuid) as UnityEngine.Object;
                if (proj == null) { Plugin.Log.LogWarning($"[Projectile] projectile GUID didn't resolve for '{pawnDef.name}' — is Projectile_KamikazeDrone in a BUILT, loaded bundle?"); continue; }
                var pref = projField.GetValue(pawnDef);
                if (pref == null) { pref = Activator.CreateInstance(projField.FieldType); projField.SetValue(pawnDef, pref); }
                var gf = FindGuidField(pref.GetType());
                if (gf == null) { Plugin.Log.LogError("[Projectile] ProjectileAssetReference has no guid field (layout changed?)"); continue; }
                gf.SetValue(pref, projGuid);
                Plugin.Diag($"[Projectile] '{pawnDef.name}'.Projectile -> '{proj.name}'  ({raw})");
            }
        }

        [SessionScoped(Manual = "UniversalInject.PropsBudget load path")] static readonly List<object> propPending = new List<object>();   // parsed GUIDs not yet registered (per-session)
        static bool propParsed; static int propWait; static bool propTickArmed; static int propTick;
        internal static void RearmPropRegistration() { propParsed = false; propPending.Clear(); propTickArmed = true; }   // AnimationLoad cleared the manager's list — register ours again
        static void ParsePropGuidsIFN()
        {
            if (propParsed) return;
            propParsed = true;
            foreach (var part in (Plugin.PropCollectionGuids?.Value ?? "").Split(';'))
            {
                var g = ParseGuidCsv(part.Trim());
                if (g != null) propPending.Add(g);
            }
            if (propPending.Count > 0) Plugin.Diag($"[Props] {propPending.Count} mesh collection(s) to register");
        }

        // Called from Hk_PropRegister's postfix (loud: this is THE moment it must work) and from the Update tick (quiet).
        internal static void RegisterPropCollections(object animationManager, bool loud)
        {
            ParsePropGuidsIFN();
            if (propPending.Count == 0 || animationManager == null) return;
            var amType = animationManager.GetType();
            var mcType = GameBinding.MeshCollection;
            var adb = GameBinding.AssetDatabase;
            var load = adb?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => (m.Name == "TryLoadAsset" || m.Name == "LoadAsset") && m.IsGenericMethodDefinition && m.GetParameters().Length == 1)?.MakeGenericMethod(mcType);
            var reg = amType.GetMethod("RegisterMeshCollection", BindingFlags.Public | BindingFlags.Instance);
            if (load == null || reg == null) { Plugin.Log.LogError("[Props] reflection targets missing (LoadAsset / RegisterMeshCollection) — axis disabled this session."); propPending.Clear(); return; }
            for (int i = propPending.Count - 1; i >= 0; i--)
            {
                var mc = load.Invoke(null, new[] { propPending[i] });
                if (mc == null || (mc is UnityEngine.Object uo && !uo))
                    mc = LoadCollectionFromLoadedBundles(mcType, i);   // Amplitude's catalog misses our MeshCollection (type-specific) — pull it from the mounted Unity bundle by name instead
                if (mc == null || (mc is UnityEngine.Object uo2 && !uo2))
                {
                    if (loud) Plugin.Log.LogError("[Props] mesh collection NOT loadable at AnimationLoad time (GUID catalog miss AND no loaded bundle carries it by name).");
                    else if (++propWait % 600 == 0) Plugin.Log.LogWarning("[Props] a mesh collection isn't loadable yet — retrying.");
                    continue;
                }
                reg.Invoke(animationManager, new[] { mc });   // dedupes internally; also LoadIFNs the meshes into the GPU content manager
                Plugin.Diag($"[Props] registered mesh collection '{(mc as UnityEngine.Object)?.name}'" + (loud ? " (at AnimationLoad — before pawn resolution)" : " (late tick)"));
                propPending.RemoveAt(i);
            }
        }

        // FALLBACK loader: Amplitude's AssetDatabase resolves our FxMesh/Skeleton GUIDs from the community bundle fine,
        // but NOT a MeshCollection (type-specific catalog gap). The bundle itself is a plain Unity AssetBundle the game
        // has already mounted, so load the asset object BY NAME from the loaded bundles — RegisterMeshCollection takes
        // the object, and the collection's internal FxMesh GUID still resolves through the game's own (working) path.
        static object LoadCollectionFromLoadedBundles(Type mcType, int pendingIndex)
        {
            try
            {
                var names = (Plugin.PropCollectionNames?.Value ?? "").Split(';');
                string name = pendingIndex < names.Length ? names[pendingIndex].Trim() : "";
                if (name.Length == 0) return null;
                foreach (var b in UnityEngine.AssetBundle.GetAllLoadedAssetBundles())
                {
                    var a = b.LoadAsset(name);                 // short-name lookup; unique within our bundle
                    if (a != null && mcType.IsInstanceOfType(a)) return a;
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[Props] bundle-name fallback: " + ex.Message); }
            return null;
        }

        internal static void TickPropRegister()   // safety net: late registration/repair only
        {
            try
            {
                // Until the first AnimationLoad the mod bundle isn't mounted, so every catalog request is a
                // guaranteed miss that LogErrors into the Amplitude diagnostics (64+ red lines per boot). The
                // loud AnimationLoad postfix is the registration moment that works; this tick only repairs
                // late failures after it — armed there, and paced to ~1 attempt/second.
                if (!propTickArmed || (++propTick % 60) != 0) return;
                ParsePropGuidsIFN();
                if (propPending.Count == 0) return;
                var amType = GameBinding.AnimationManager;
                var inst = amType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                           ?? GF(amType, "Instance")?.GetValue(null);
                if (inst != null) RegisterPropCollections(inst, loud: false);
            }
            // The manager exists but its internals aren't built yet (Register throws before its Load) — retry next frame.
            catch (Exception ex) { if (++propWait % 600 == 0) Plugin.Log.LogWarning("[Props] tick (retrying): " + ex.Message); }
        }

        // Diagnostic: dump the LIVE GPU mesh-content buffer usage per content layer. Answers the real scaling question
        // ("how many more models fit"): the Amplitude manager packs every registered skeleton/mesh-collection into a
        // fixed buffer sized 100k verts / 250k indices / 256 meshes PER ContentLayer, tracked by running cursors. Reading
        // those cursors tells us exactly how full each layer is and whether the mod's models are all resident at once or
        // only the active unit types. Bound to a hotkey — press in-game with custom units on the map.
        // Structured live budget read — ONE source shared by the F8 window, the Shift+F8 log dump, and the smoke
        // test's near-the-wall alarm (2026-08-17). Name == null marks a null layer slot.
        internal struct LayerBudget { public string Name; public int Verts, VertsMax, Idx, IdxMax, Meshes, MeshesMax, MaxTris; }
        internal static System.Collections.Generic.List<LayerBudget> ReadMeshBudget(out string error, out int pawnLayer)
        {
            var list = new System.Collections.Generic.List<LayerBudget>(); error = null; pawnLayer = -1;
            try
            {
                var amType = GameBinding.AnimationManager;
                var inst = amType != null ? AccessTools.Property(amType, "Instance")?.GetValue(null) : null;
                if (inst == null) { error = "AnimationManager.Instance is null — load a game first."; return list; }
                var fxMgr = GetMember(inst, "FxComponentMeshContentManager");
                if (fxMgr == null) { error = "FxComponentMeshContentManager is null."; return list; }
                pawnLayer = GetMember(inst, "FXMeshLayerIndex") is int pl ? pl : -1;
                if (!(GetMember(fxMgr, "Layers") is Array layers)) { error = "Layers array not found."; return list; }
                for (int i = 0; i < layers.Length; i++)
                {
                    var L = layers.GetValue(i);
                    if (L == null) { list.Add(new LayerBudget()); continue; }   // Name stays null = null slot
                    list.Add(new LayerBudget
                    {
                        Name = GetMember(L, "name") as string ?? "?",
                        Verts = ToInt(GetMember(L, "currentVertexIndex")),    VertsMax = ToInt(GetMember(L, "baseVertexBufferSize")),
                        Idx = ToInt(GetMember(L, "currentIndexIndex")),       IdxMax = ToInt(GetMember(L, "baseIndexBufferSize")),
                        Meshes = ToInt(GetMember(L, "currentMeshAddedCount")), MeshesMax = ToInt(GetMember(L, "maxMeshCount")),
                        // the PER-MESH ceiling: quads beyond this are SILENTLY dropped at encode (holes in the model). 0 = unlimited.
                        MaxTris = ToInt(GetMember(L, "maxMeshTriangleCount"))
                    });
                }
            }
            catch (Exception ex) { error = "budget read failed: " + ex.Message; }
            return list;
        }

        // Build the live budget readout as lines (shared by the F8 window and the Shift+F8 log dump).
        internal static System.Collections.Generic.List<string> MeshBudgetLines()
        {
            var lines = new System.Collections.Generic.List<string>();
            var layers = ReadMeshBudget(out string err, out int pawnLayer);
            if (layers.Count == 0) { lines.Add(err ?? "budget read returned nothing"); return lines; }
            lines.Add($"GPU mesh buffer — {layers.Count} layer(s), pawn layer = {pawnLayer}:");
            for (int i = 0; i < layers.Count; i++)
            {
                var b = layers[i];
                if (b.Name == null) { lines.Add($"  layer {i}: <null>"); continue; }
                string tag = i == pawnLayer ? "  <-- your models" : "";
                lines.Add($"  L{i} '{b.Name}': verts {b.Verts:n0}/{b.VertsMax:n0} ({Pct(b.Verts, b.VertsMax)}%) | idx {Pct(b.Idx, b.IdxMax)}% | meshes {b.Meshes}/{b.MeshesMax} | maxTris/mesh {(b.MaxTris == 0 ? "unlimited" : b.MaxTris.ToString("n0"))}{tag}");
            }
            if (err != null) lines.Add(err);
            return lines;
        }

        internal static void DumpMeshBudget()   // Shift+F8: same readout, to the log
        {
            foreach (var l in MeshBudgetLines()) Plugin.Log.LogInfo("[Budget] " + l);
        }
        static int ToInt(object o) { try { return o == null ? -1 : Convert.ToInt32(o); } catch { return -1; } }
        static int Pct(int a, int b) { return b > 0 ? (int)(100.0 * a / b) : 0; }

        // ---- ATLAS DUMP (retexture aid) ------------------------------------------------------------------------------
        // Dump every currently-loaded unit output-layer atlas (its material's _MainTex) to
        // BepInEx/config/haf_atlas_dump/<layer>.png, so a unit's skin can be found by its layer name and used as a
        // paint canvas (e.g. to make a desaturated "grey" variant of a Common copy). Reuses ApplyTexture's Content walk
        // (Content -> OutputLayerEntries -> OutputLayerInstance) and TickOne's material fields; the host atlas isn't
        // CPU-readable, so each is blitted through a RenderTexture first. One PNG per layer. Bound to the F8 window's
        // "Dump Atlases" button — load a game with the target units visible, then click.
        internal static void DumpOutputLayerAtlases(string filter = null)
        {
            try
            {
                var amType = GameBinding.AnimationManager;
                var mgr = amType != null ? AccessTools.Property(amType, "Instance")?.GetValue(null) : null;
                if (mgr == null) { Plugin.Log.LogWarning("[AtlasDump] AnimationManager.Instance is null — load a game first."); return; }
                var content = GetMember(mgr, "Content");
                var list = content != null ? GetMember(content, "OutputLayerEntries") as Array : null;
                if (list == null) { Plugin.Log.LogWarning("[AtlasDump] no OutputLayerEntries found."); return; }
                string dir = Path.Combine(Paths.ConfigPath, "haf_atlas_dump");
                Directory.CreateDirectory(dir);
                var seen = new HashSet<string>();
                int n = 0;
                foreach (var entry in list)
                {
                    var ol = GetMember(entry, "OutputLayerInstance");
                    if (ol == null) continue;
                    string layer = (ol as UnityEngine.Object)?.name ?? "layer";
                    if (!string.IsNullOrEmpty(filter) && layer.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;   // only this unit (e.g. "Corvette")
                    if (!seen.Add(layer)) continue;   // one dump per layer
                    UnityEngine.Texture tex = null;
                    if (GetMember(ol, "RenderOutputs") is Array ros)
                        foreach (var ro in ros)
                        {
                            foreach (var fld in new[] { "currentRenderMaterial", "runTimeRenderMaterial" })
                                if (GetMember(ro, fld) is UnityEngine.Material mat && mat.GetTexture("_MainTex") is UnityEngine.Texture mt) { tex = mt; break; }
                            if (tex != null) break;
                        }
                    if (tex == null) continue;
                    var png = ToReadablePng(tex);
                    if (png == null) continue;
                    File.WriteAllBytes(Path.Combine(dir, SanitizeFile(layer) + ".png"), png);
                    n++;
                    Plugin.Log.LogInfo($"[AtlasDump] {layer} -> {SanitizeFile(layer)}.png ({tex.width}x{tex.height}, {tex.name})");
                }
                Plugin.Log.LogInfo($"[AtlasDump] wrote {n} atlas PNG(s){(string.IsNullOrEmpty(filter) ? "" : $" matching '{filter}'")} to {dir}");
            }
            catch (Exception e) { Plugin.Log.LogError("[AtlasDump] " + e); }
        }

        // Blit any (possibly non-readable / compressed) texture through a RenderTexture into a readable Texture2D and
        // PNG-encode it. PNG (vs TGA) round-trips cleanly with LoadImage — paint on the dumped canvas and the retexture
        // maps back exactly. Uses UnityEngine.ImageConversionModule (also referenced for the retexture skin-load).
        // try/finally, matching BuildAdjustedAtlas and MakeGrayCopy — the third readback site, missed when the other
        // two were hardened (2026-08-21). It restored RenderTexture.active on the SUCCESS path only, so a throw in
        // ReadPixels / Apply / EncodeToPNG left the active target pointing at our temporary RT and never released it.
        // A dangling active target "corrupts the next draw" (BuildAdjustedAtlas's own words) — a whole-screen artifact
        // from a diagnostic dump. Reachable only via the operator-triggered atlas dump, so it has never fired in
        // practice, but it is the same defect class its two siblings were already fixed for.
        // Not unit-testable (RenderTexture/Blit need a live Unity render loop); the guard is that all three readback
        // sites now share one shape — if you add a fourth, copy this one.
        static byte[] ToReadablePng(UnityEngine.Texture src)
        {
            var prev = UnityEngine.RenderTexture.active;
            UnityEngine.RenderTexture rt = null;
            UnityEngine.Texture2D t = null;
            try
            {
                int w = src.width, h = src.height;
                rt = UnityEngine.RenderTexture.GetTemporary(w, h, 0, UnityEngine.RenderTextureFormat.ARGB32, UnityEngine.RenderTextureReadWrite.sRGB);
                UnityEngine.Graphics.Blit(src, rt);
                UnityEngine.RenderTexture.active = rt;
                t = new UnityEngine.Texture2D(w, h, UnityEngine.TextureFormat.RGBA32, false);
                t.ReadPixels(new UnityEngine.Rect(0, 0, w, h), 0, 0); t.Apply();
                return UnityEngine.ImageConversion.EncodeToPNG(t);   // static form: no `using UnityEngine;` in this file
            }
            catch (Exception e) { Plugin.Log.LogWarning("[AtlasDump] readable copy failed for '" + (src != null ? src.name : "?") + "': " + e.Message); return null; }
            finally
            {
                UnityEngine.RenderTexture.active = prev;   // FIRST: a dangling active target corrupts the next draw
                if (rt != null) UnityEngine.RenderTexture.ReleaseTemporary(rt);
                if (t != null) UnityEngine.Object.DestroyImmediate(t);   // the readback copy is always temporary here
            }
        }

        static string SanitizeFile(string s)
        {
            if (string.IsNullOrEmpty(s)) return "layer";
            var sb = new System.Text.StringBuilder();
            foreach (var ch in s) sb.Append(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_');
            return sb.ToString();
        }
    }
}
