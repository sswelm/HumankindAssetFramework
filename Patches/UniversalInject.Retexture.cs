using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json.Linq;             // provided by the game (mod.io); robust registry parse where JsonUtility no-ops in the game runtime

namespace HumankindAssetFramework
{
    internal static partial class UniversalInject
    {
        static void ApplyTexture(ModelEntry e, object mgr)
        {
            try
            {
                if (e.tex == null)
                {
                    // A custom model's skin is its baked atlas — UNLESS a textureFile recolour is set on the model entry,
                    // then hot-load THAT PNG instead (recolour a custom model without a re-bake; same enc_skins PNG the
                    // Unit Retexture window writes). Desaturate/RGB then adjust the loaded PNG. Falls back to the baked
                    // atlas if the PNG is missing. (Adjust-only with no PNG stays on the baked atlas — that path isn't
                    // CPU-readable for AdjustSkin, so a model recolour goes through a PNG.)
                    if (!string.IsNullOrEmpty(e.textureFile))
                    {
                        e.tex = LoadSkinPng(e.textureFile, e.resourceName, e.assetDir);
                        if (e.tex != null && NeedsAdjust(e))
                        {
                            AdjustSkin(e.tex, e.brightness, e.desaturate, e.tintR, e.tintG, e.tintB);
                            Plugin.Log.LogInfo($"[Skin] {e.resourceName}: adjustments applied to retexture (gamma {e.brightness:0.00}, desat {UnityEngine.Mathf.Clamp01(e.desaturate):0.00}, rgb {e.tintR:+0;-0;0}/{e.tintG:+0;-0;0}/{e.tintB:+0;-0;0})");
                        }
                    }
                    if (e.tex == null) e.tex = LoadAtlas(e.ta, e.tb, e.tc, e.td, e.resourceName);
                }
                // isolated path: paint ONLY our private clone (the host layer + real units keep their own skin)
                if (e.isolatedLayer != null) { e.hostOutputLayer = e.isolatedLayer; TickOne(e); return; }
                // TEXTURE-ONLY SAFETY: a skinless entry (all-zero skeleton guid) that never got its isolated
                // clone must NOT fall through to the shared host layer. That layer is shared with the emblematic
                // ORIGINAL unit, so painting it would bleed this reskin/desaturate onto the vanilla unit. This is
                // reachable when GreyIsolate couldn't clone at inject — the fragment's output layer wasn't ready
                // yet (host==null), or its meshName didn't EXACTLY equal layerHint (GreyIsolate is exact-match,
                // this fallback is substring). Degrade to vanilla instead of corrupting the original. A custom
                // model (non-zero skeleton guid) legitimately owns its own layer and keeps the fallback below.
                if (e.sa == 0 && e.sb == 0 && e.sc == 0 && e.sd == 0)
                {
                    lock (_ambigLogged) if (_ambigLogged.Add("isoFail:" + e.resourceName))
                        Plugin.Log.LogWarning($"[Uni] '{e.resourceName}' retexture has no isolated layer clone (layer not ready or meshName!=layerHint '{e.layerHint}' at inject) — skipping the shared-layer repaint so the reskin can't bleed onto the emblematic original; this unit shows its vanilla skin.");
                    return;
                }
                // fallback (no clone): the shared host layer — reached only by a custom model that owns its layer
                var content = GetMember(mgr, "Content");
                var list = content != null ? GetMember(content, "OutputLayerEntries") as Array : null;
                if (list == null || string.IsNullOrEmpty(e.layerHint)) return;
                foreach (var entry in list)
                {
                    var ol = GetMember(entry, "OutputLayerInstance");
                    var oln = (ol as UnityEngine.Object)?.name ?? "";
                    if (ol == null || oln.IndexOf(e.layerHint, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    e.hostOutputLayer = ol; TickOne(e);
                }
            }
            catch (Exception ex) { Plugin.Log.LogError("[Uni] texture: " + ex); }
        }

        // ---- TEXTURE-ONLY override: keep the vanilla mesh, repaint the isolated skin (custom PNG or desaturate) ----

        static void ApplyTextureOnly(object addon, object animMgr, ModelEntry e, string name)
        {
            try
            {
                var bodyName = DiscoverBodyMeshName(addon);
                if (!string.IsNullOrEmpty(bodyName)) e.layerHint = bodyName;
                // Custom skin PNG takes precedence; otherwise the desaturated original is built in GreyIsolate/TickOne.
                if (!string.IsNullOrEmpty(e.textureFile) && e.tex == null)
                {
                    e.tex = LoadSkinPng(e.textureFile, e.resourceName, e.assetDir);
                    if (e.tex != null && NeedsAdjust(e)) AdjustSkin(e.tex, e.brightness, e.desaturate, e.tintR, e.tintG, e.tintB);   // brighten/desaturate/tint the loaded PNG too
                }
                GreyIsolate(addon, animMgr, e);    // clone the body fragment's output layer (+ build the desaturated atlas if desaturate>0)
                ApplyTexture(e, animMgr);          // paint e.tex on the isolated clone + neutralise the civ-colour/overlay maps (TickOne)
                if (!e.repointed) { e.repointed = true; Plugin.Log.LogInfo($"[Skin] '{name}' -> {e.resourceName}: {(string.IsNullOrEmpty(e.textureFile) ? $"greyed (desaturate {e.desaturate:0.00})" : $"retextured ('{e.textureFile}')")}, layer '{e.layerHint}', atlas={(e.tex != null ? e.tex.width + "x" + e.tex.height : "NONE — will retry")}"); }
            }
            catch (Exception ex) { Plugin.Log.LogError("[Skin] " + ex); }
        }

        // Load a retexture PNG: the owning pack's <assetDir>/skins/<file> first (per-pack assets, 2026-07-19), then the
        // legacy shared BepInEx/config/enc_skins/<file>. (Needs ImageConversionModule.LoadImage.)
        static UnityEngine.Texture2D LoadSkinPng(string file, string tag, string assetDir = "")
        {
            try
            {
                var path = Path.Combine(Paths.ConfigPath, "enc_skins", file);
                if (!string.IsNullOrEmpty(assetDir))
                {
                    var pp = Path.Combine(assetDir, "skins", file);
                    if (File.Exists(pp)) path = pp;
                }
                if (!File.Exists(path)) { Plugin.Log.LogWarning($"[Skin] {tag}: retexture file not found: {path}" + (string.IsNullOrEmpty(assetDir) ? "" : $" (also tried {Path.Combine(assetDir, "skins")})")); return null; }
                // mipChain TRUE: the baked atlas ships with mips, and a hot-loaded PNG without them undersamples at map
                // zoom (each screen pixel picks 1 raw texel instead of an average) — a dark high-contrast skin renders
                // as noisy near-black. LoadImage fills the chain; AdjustSkin's Apply() rebuilds it after the pixel pass.
                var t = new UnityEngine.Texture2D(2, 2, UnityEngine.TextureFormat.RGBA32, true) { name = tag + "_Skin" };
                if (!UnityEngine.ImageConversion.LoadImage(t, File.ReadAllBytes(path))) { Plugin.Log.LogWarning($"[Skin] {tag}: LoadImage failed for {file} (not a PNG/JPG?)"); UnityEngine.Object.DestroyImmediate(t); return null; }
                Plugin.Log.LogInfo($"[Skin] {tag}: loaded retexture '{file}' ({t.width}x{t.height})");
                return t;
            }
            catch (Exception e) { Plugin.Log.LogError($"[Skin] {tag}: retexture load failed: " + e); return null; }
        }

        // Isolate the copy's body-fragment output layer (a private clone, so the shared emblematic original is untouched)
        // and build a desaturated atlas from that layer's CURRENT skin into e.tex. Keeps the vanilla meshCollection
        // (texture-only). Mirrors ReloadFragments' isolation, minus the mesh repoint.
        static void GreyIsolate(object addon, object animMgr, ModelEntry e)
        {
            try
            {
                var frags = GetMember(addon, "FragmentEntries") as Array;
                if (frags == null) return;
                var renderer = GetMember(animMgr, "FxComponentRenderer");
                var mcm = GetMember(animMgr, "FxComponentMeshContentManager");
                var layerObj = GetMember(animMgr, "FXMeshLayerIndex");
                int layer = layerObj is int li ? li : Convert.ToInt32(layerObj ?? 0);
                var fragType = frags.GetType().GetElementType();
                var mcField = AccessTools.Field(fragType, "meshCollection");
                var mnField = AccessTools.Field(fragType, "meshName");
                var folField = AccessTools.Field(fragType, "fxOutputLayer");
                var load = AccessTools.Method(fragType, "Load");
                if (folField == null) return;
                for (int i = 0; i < frags.Length; i++)
                {
                    var item = frags.GetValue(i);
                    if (item == null) continue;
                    var mn = mnField?.GetValue(item) as string;
                    if (string.IsNullOrEmpty(e.layerHint) || mn != e.layerHint) continue;   // only the body layer
                    var host = folField.GetValue(item);
                    if (e.tex == null && host != null && NeedsAdjust(e)) e.tex = BuildAdjustedAtlas(host, e.brightness, e.desaturate, e.tintR, e.tintG, e.tintB, e.resourceName);   // adjust the ORIGINAL skin, once (skipped when a custom PNG already set e.tex)
                    if (e.isolatedLayer == null && host is UnityEngine.Object ho && ho != null)
                    {
                        var clone = UnityEngine.Object.Instantiate(ho); clone.name = e.resourceName + "_GreyLayer"; e.isolatedLayer = clone;
                        Plugin.Log.LogInfo($"[Grey] cloned output layer for {e.resourceName} -> '{clone.name}'");
                    }
                    if (e.isolatedLayer != null) folField.SetValue(item, e.isolatedLayer);
                    var mc = mcField?.GetValue(item);   // KEEP the vanilla meshCollection; re-Load so the clone gets its own GPU slot
                    try { load?.Invoke(item, new object[] { mc, renderer, mcm, layer }); }
                    catch (Exception ex) { Plugin.Log.LogWarning("[Grey] frag reload: " + (ex.InnerException ?? ex).Message); }
                    frags.SetValue(item, i);
                    break;
                }
            }
            catch (Exception ex) { Plugin.Log.LogError("[Grey] isolate: " + ex); }
        }

        // Read the output layer's current _MainTex and return an ADJUSTED copy (AdjustSkin: desaturate toward luminance by
        // `desat`, then add the R/G/B colour offset -255..+255). Blits through a RenderTexture first (the host atlas isn't
        // CPU-readable). The civ-colour tint is killed separately by TickOne (_ColorMask -> black).
        static UnityEngine.Texture2D BuildAdjustedAtlas(object hostLayer, float brightness, float desat, float tR, float tG, float tB, string tag)
        {
            try
            {
                UnityEngine.Texture src = null;
                if (GetMember(hostLayer, "RenderOutputs") is Array ros)
                    foreach (var ro in ros)
                    {
                        foreach (var fld in RenderMatFields)
                            if (GetMember(ro, fld) is UnityEngine.Material mat && mat.GetTexture("_MainTex") is UnityEngine.Texture mt) { src = mt; break; }
                        if (src != null) break;
                    }
                // log the wait ONCE — TickOne retries this every frame until the layer's atlas loads, and a slow load
                // used to flood the BepInEx log with one warning per frame (perf pass 2026-07-19)
                if (src == null) { if (!greyWaitLogged) { greyWaitLogged = true; Plugin.Log.LogWarning($"[Grey] {tag}: no _MainTex on the output layer yet (will keep retrying silently)"); } return null; }
                int w = src.width, h = src.height;
                var rt = UnityEngine.RenderTexture.GetTemporary(w, h, 0, UnityEngine.RenderTextureFormat.ARGB32, UnityEngine.RenderTextureReadWrite.sRGB);
                var prev = UnityEngine.RenderTexture.active;
                UnityEngine.Graphics.Blit(src, rt);
                UnityEngine.RenderTexture.active = rt;
                var t = new UnityEngine.Texture2D(w, h, UnityEngine.TextureFormat.RGBA32, false) { name = tag + "_Grey" };
                t.ReadPixels(new UnityEngine.Rect(0, 0, w, h), 0, 0); t.Apply();
                UnityEngine.RenderTexture.active = prev; UnityEngine.RenderTexture.ReleaseTemporary(rt);
                AdjustSkin(t, brightness, desat, tR, tG, tB);
                Plugin.Log.LogInfo($"[Grey] {tag}: adjusted atlas {w}x{h} (gamma {brightness:0.00}, desat {UnityEngine.Mathf.Clamp01(desat):0.00}, rgb {tR:+0;-0;0}/{tG:+0;-0;0}/{tB:+0;-0;0})");
                return t;
            }
            catch (Exception e) { Plugin.Log.LogError("[Grey] build atlas: " + e); return null; }
        }

        // Apply the universal skin adjustments in place, in this order: (1) BRIGHTNESS — a gamma lift (1 = unchanged;
        // LUT, endpoint-preserving, multiplicative in the dark range — the only adjust that meaningfully lightens a
        // near-black atlas), (2) pull each pixel toward its luminance by `desat` (1 = full grey), (3) add the
        // per-channel colour offset tR/tG/tB (-255..+255). Shared by the own-atlas path and the PNG path. The editor's
        // Retexture-window preview mirrors this math exactly — keep them in lockstep.
        static void AdjustSkin(UnityEngine.Texture2D t, float brightness, float desat, float tR, float tG, float tB)
        {
            var px = t.GetPixels32();
            float s = UnityEngine.Mathf.Clamp01(desat);
            byte[] lut = null;
            if (UnityEngine.Mathf.Abs(brightness - 1f) > 0.001f)
            {
                float inv = 1f / UnityEngine.Mathf.Clamp(brightness, 0.2f, 4f);
                lut = new byte[256];
                for (int v = 0; v < 256; v++) lut[v] = (byte)UnityEngine.Mathf.Clamp(UnityEngine.Mathf.RoundToInt(255f * UnityEngine.Mathf.Pow(v / 255f, inv)), 0, 255);
            }
            for (int i = 0; i < px.Length; i++)
            {
                if (lut != null) { px[i].r = lut[px[i].r]; px[i].g = lut[px[i].g]; px[i].b = lut[px[i].b]; }
                float lum = px[i].r * 0.299f + px[i].g * 0.587f + px[i].b * 0.114f;
                px[i].r = (byte)UnityEngine.Mathf.Clamp((px[i].r + (lum - px[i].r) * s) + tR, 0, 255);
                px[i].g = (byte)UnityEngine.Mathf.Clamp((px[i].g + (lum - px[i].g) * s) + tG, 0, 255);
                px[i].b = (byte)UnityEngine.Mathf.Clamp((px[i].b + (lum - px[i].b) * s) + tB, 0, 255);
            }
            t.SetPixels32(px); t.Apply();
        }

        // True if this entry carries any texture adjustment (brightness gamma, desaturate or a non-zero colour offset).
        static bool NeedsAdjust(ModelEntry e) => e.desaturate > 0f || e.tintR != 0f || e.tintG != 0f || e.tintB != 0f || UnityEngine.Mathf.Abs(e.brightness - 1f) > 0.001f;

    }
}
