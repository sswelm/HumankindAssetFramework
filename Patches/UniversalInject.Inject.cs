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
        // register every skeleton before Apply() builds GPU buffers (AnimationLoad postfix)
        internal static void EnsureRegistered(object animMgr)
        {
            if (registered) return;
            if (animMgr == null) { Plugin.Log.LogWarning("[Uni] EnsureRegistered: animMgr null"); return; }
            Plugin.Diag("[Uni] EnsureRegistered fired");
            LoadRegistry();
            // Latch on empty ONLY if the load actually succeeded (`loaded` — a genuinely empty/absent registry).
            // While a transient load failure is still retrying (`loaded` false), leave `registered` unlatched too, or
            // the retry would succeed later but registration would never run again — injection dead for the session.
            // CAPTURE THE HANDLE BEFORE ANY EARLY RETURN (audit finding 2, 2026-08-01). animMgrRef used to be set
            // further down, inside the block a zero-model pack never reaches — and it is assigned nowhere else. A
            // RULES-ONLY pack (the R.E.D. Patch shape: unitScales / eraGrid / formationThresholds parse independently
            // of the `models` array, so this is a supported configuration) therefore latched registered = loaded here,
            // never ran again, and left animMgrRef null forever. ScaleDescriptorMeshes then bailed on `am == null`
            // whose comment promises "the per-frame path retries" — a retry that could never succeed. The symptom was
            // scale rules half-working: the per-pawn ObjectSpace.Scale placement still ran, so a multi-part unit's
            // parts spread apart while nothing actually resized, with nothing in the log naming the cause.
            animMgrRef = animMgr;
            if (entries.Count == 0) { registered = loaded; return; }
            try
            {
                var reg = AccessTools.Method(animMgr.GetType(), "RegisterMeshCollection");
                if (reg == null) { Plugin.Log.LogError("[Uni] RegisterMeshCollection not found"); return; }
                int n = 0;
                foreach (var e in entries)
                {
                    // isolate each entry: a single bad model (missing asset, reflection miss) must not abort the whole
                    // loop -- that would skip Apply and take down EVERY custom model, not just the broken one.
                    try
                    {
                        if (e.skeleton == null) e.skeleton = LoadSkeleton(e.sa, e.sb, e.sc, e.sd, e.resourceName);
                        if (e.skeleton == null) continue;
                        var sf = AccessTools.Field(e.skeleton.GetType(), "loadingStatus");
                        if (sf != null) sf.SetValue(e.skeleton, Enum.ToObject(sf.FieldType, 0)); // NotLoaded
                        SetMember(e.skeleton, "SkeletonId", -1);
                        reg.Invoke(animMgr, new[] { e.skeleton });
                        n++;
                    }
                    catch (Exception ex) { Plugin.Log.LogError($"[Uni] register '{e.resourceName}' failed (skipped, others continue): " + ex); }
                }
                // inject our ClipCollections (animated models) into loadedAnimationClipCollections BEFORE Apply, so
                // Apply's builder bakes their pose data + assigns each clip an animation id.
                InjectClipCollections(animMgr);
                // (animMgrRef is captured above, before the zero-model early return — see audit finding 2)
                if (n > 0 || entries.Any(x => x.clipColl != null))
                {
                    var apply = AccessTools.Method(animMgr.GetType(), "Apply", Type.EmptyTypes)
                        ?? animMgr.GetType().GetMethods(BF).FirstOrDefault(m => m.Name == "Apply" && m.GetParameters().Length == 0);
                    apply?.Invoke(animMgr, null);
                }
                // capture each skeleton's runtime SkeletonId (assigned during Apply's GPU build) so the pawn-pose hook
                // can match PawnManager.PawnEntry.SkeletonId; and resolve our clip's animation id for the pose override.
                foreach (var e in entries)
                {
                    if (e.skeleton != null) { try { e.skeletonId = Convert.ToInt32(GetMember(e.skeleton, "SkeletonId")); } catch { } }
                    if (e.clipColl != null) e.animId = ResolveAnimId(animMgr, e);
                    if (e.moveClipColl != null) e.moveAnimId = ResolveCollAnimId(animMgr, e.moveClipColl, e.resourceName + ":move", out e.moveDur);
                    if (e.afterClipColl != null) e.afterAnimId = ResolveCollAnimId(animMgr, e.afterClipColl, e.resourceName + ":after", out e.afterDur);
                    if (e.attackClipColl != null) e.attackAnimId = ResolveCollAnimId(animMgr, e.attackClipColl, e.resourceName + ":attack", out e.attackDur);
                    if (e.combatClipColl != null) e.combatAnimId = ResolveCollAnimId(animMgr, e.combatClipColl, e.resourceName + ":combat", out e.combatDur);
                    if (e.preMoveClipColl != null) e.preMoveAnimId = ResolveCollAnimId(animMgr, e.preMoveClipColl, e.resourceName + ":premove", out e.preMoveDur);
                    if (e.idleClipColl != null) e.idleAnimId = ResolveCollAnimId(animMgr, e.idleClipColl, e.resourceName + ":idle", out e.idleDur);
                    if (e.idleAltClipColl != null) e.idleAltAnimId = ResolveCollAnimId(animMgr, e.idleAltClipColl, e.resourceName + ":idlealt", out e.idleAltDur);
                    if (e.idleAlt2ClipColl != null) e.idleAlt2AnimId = ResolveCollAnimId(animMgr, e.idleAlt2ClipColl, e.resourceName + ":idlealt2", out e.idleAlt2Dur);
                }
                registered = true;
                Plugin.Diag($"[Uni] registered {n} skeleton(s) + re-Apply'd; " + string.Join(", ", entries.Select(x => $"{x.resourceName}(skel {x.skeletonId}, anim {x.animId})")));
            }
            catch (Exception e) { InjectionErrors++; Plugin.Log.LogError("[Uni] register: " + e); }
        }

        // repoint a matching unit (AddOn.Load postfix)
        internal static void RepointMatch(object addon, object animMgr)
        {
            if (addon == null || animMgr == null) return;
            if (!repointActiveLogged) { repointActiveLogged = true; Plugin.Diag($"[Uni] repoint-hook ACTIVE (UniversalInject={Plugin.UniversalInjectOn.Value}, entries={(entries == null ? -1 : entries.Count)})"); }
            if (!Plugin.UniversalInjectOn.Value) return;
            LoadRegistry();
            try
            {
                var def = GetMember(addon, "Definition");
                var name = (def as UnityEngine.Object)?.name ?? "";
                if (name.Length == 0) return;
                MaybeDumpPawnRig(addon, name);   // caterpillar investigation: vanilla rig dump, BEFORE any registry matching
                // RESIZE LAB: resolve this definition's scale (product of every matching rule) to its descriptor
                // id — BEFORE the model-entry gate, because rules target vanilla units with no entry at all.
                if (unitScaleRules.Count > 0)
                {
                    float prod = 1f;
                    int ruleEraOverride = 0;
                    for (int ri = 0; ri < unitScaleRules.Count; ri++)
                        if (name.IndexOf(unitScaleRules[ri].match, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            prod *= unitScaleRules[ri].scale;
                            if (unitScaleRules[ri].era > 0) ruleEraOverride = unitScaleRules[ri].era;   // last explicit wins
                        }
                    if (Math.Abs(prod - 1f) > 1e-4f)
                    {
                        // HUMAN-PRESENTATION EXCLUSION (user rule: "prevents disappointments"): scaled humans read
                        // as absurd instantly, and scaling a mount/chariot under an unscaled rider is just as bad —
                        // so the whole human-carrying family is skipped: Human(1), HumanMountedFighter(2),
                        // ChariotHumanFighter(4), Mount(6), HumanMountedDriver(9), Chariot(10), ChariotMount(12),
                        // ChariotHumanDriver(13), HumanServant(16). ANIMALS stay scalable (AnimalFighter=3 — cave
                        // bears!), as do boats, planes, vehicles, missiles and custom rigs.
                        int prof = -1;
                        try { prof = Convert.ToInt32(GetMember(def, "AnimationCapabilityProfile")); } catch { }
                        bool humanClass = prof == 1 || prof == 2 || prof == 4 || prof == 6 || prof == 9 || prof == 10 || prof == 12 || prof == 13 || prof == 16;
                        if (unitScaleLogged == null) unitScaleLogged = new HashSet<string>();
                        if (humanClass)
                        {
                            if (unitScaleLogged.Add(name)) Plugin.Diag($"[Resize] '{name}' SKIPPED (human-presentation profile {prof}) — humans/mounts/chariots are excluded from scaling");
                        }
                        else
                        {
                            int sdefId = -1;
                            try { sdefId = Convert.ToInt32(GetMember(addon, "PawnDefinitionId")); } catch { }
                            if (sdefId >= 0)
                            {
                                // THE UNIT'S OWN ERA (user: "the unit needs to register its era as well"): era
                                // the grid ages a unit FROM ITS OWN ERA, so we need that era. Amplitude names its
                                // definitions "Era4_Common_ManOWar_01", so it reads straight off the name; a rule
                                // may override it for definitions that carry no era token (custom/modded units).
                                int homeEra = 1;
                                var em = Regex.Match(name, "Era(\\d+)", RegexOptions.IgnoreCase);
                                if (em.Success && int.TryParse(em.Groups[1].Value, out int he) && he >= 0) homeEra = he;
                                if (ruleEraOverride > 0) homeEra = ruleEraOverride;
                                unitScaleByDesc[sdefId] = new UnitScaleInfo { scale = prod, homeEra = homeEra, domain = DomainFromProfile(prof) };
                                unitScaleNameByDesc[sdefId] = name;
                                if (unitScaleLogged.Add(name)) Plugin.Diag($"[Resize] '{name}' -> desc {sdefId} scale x{prod:0.###} (profile {prof}, own era {homeEra}{(ruleEraOverride > 0 ? " from rule" : " from name")})");
                            }
                        }
                    }
                }
                if (entries.Count == 0) return;
                var e = LongestMatch(entries, name, x => x.pawnDescription);   // most-specific (longest) match, not first-in-order — a variant entry wins over the base it extends
                if (e == null) return;
                Plugin.Diag($"[Uni] MATCH addon='{name}' -> {e.resourceName} (skel {e.sa},{e.sb},{e.sc},{e.sd})");
                // SEED THE DESCRIPTOR HERE, not from the first correctly-skinned pawn (2026-07-31). OnPawnAdded's
                // safety net — "this pawn is on the DONOR skeleton, force ours" — matches by descId, but descId was
                // only ever LEARNED from a pawn that had already arrived on our skeleton. One-directional: if the
                // first pawns of a model appear before injection matched anything (load restore, or an LOD rebuild
                // on zoom), nothing is learned, the net stays disarmed for the whole session, and those pawns keep
                // the donor rig — weights addressing the wrong bones, geometry flung into spikes. The addon knows
                // its PawnDefinitionId before any pawn exists, and it is the SAME id space OnPawnAdded reads as
                // ctx.descId (the Resize path above keys unitScaleByDesc with it), so seed it and the net is armed
                // from the first frame regardless of who wins the race.
                try
                {
                    int seedDesc = Convert.ToInt32(GetMember(addon, "PawnDefinitionId"));
                    if (seedDesc >= 0 && e.descId != seedDesc)
                    {
                        e.descId = seedDesc;
                        Plugin.Diag($"[Uni] '{e.resourceName}' descriptor seeded at injection: desc={seedDesc} (wrong-skeleton net armed before any pawn spawns)");
                    }
                }
                catch (Exception exSeed) { Plugin.Log.LogWarning($"[Uni] '{e.resourceName}' could not seed descriptor from the addon ({exSeed.Message}) — falling back to learning it from the first correct pawn"); }

                // TEXTURE-ONLY override: keep the vanilla mesh, just isolate this unit's output layer and repaint its skin
                // — either a hot-loaded PNG (textureFile) or a desaturated copy of its own atlas (desaturate). Returns
                // before any skeleton repoint. The isolation leaves the emblematic original untouched (shared layer).
                // A CUSTOM MODEL entry (non-zero skeleton guid) must NOT take this path: it carries its OWN baked atlas,
                // and a textureFile/adjust on it is a recolour of THAT atlas (applied in ApplyTexture), not a donor-layer
                // repaint — diverting here would paint the vanilla donor layer and return before the mesh is even repointed
                // (symptom: the custom mech stayed its baked colour). Only true texture-only entries (no skeleton) divert.
                bool isModelEntry = !(e.sa == 0 && e.sb == 0 && e.sc == 0 && e.sd == 0);
                if (!isModelEntry && (NeedsAdjust(e) || !string.IsNullOrEmpty(e.textureFile))) { ApplyTextureOnly(addon, animMgr, e, name); return; }

                // One-shot diagnostic (BEFORE we swap): dump the DONOR skeleton / mesh-collection sub-meshes, so we can
                // find parts that aren't separate fragments (e.g. a helicopter rotor baked as its own skinned sub-mesh).
                if (!e.fragsLogged)
                {
                    var sk0 = GetMember(addon, "Skeleton"); var mc0 = GetMember(addon, "MeshCollection");
                    Plugin.Diag($"[Uni] {e.resourceName} donor Skeleton='{(sk0 as UnityEngine.Object)?.name}' MeshCollection='{(mc0 as UnityEngine.Object)?.name}'");
                    DumpSkinned(sk0, e.resourceName + " donor.Skeleton");
                    if (mc0 != null && !ReferenceEquals(mc0, sk0)) DumpSkinned(mc0, e.resourceName + " donor.MeshCollection");
                    // WIDE NET: the rotor is neither a sub-mesh nor a fragment, so dump every field/array on the addon
                    // and the skeleton to find where that mesh is referenced (or confirm it's engine-procedural).
                    DumpFields(addon, e.resourceName + " addon");
                    DumpFields(sk0, e.resourceName + " skeleton");
                }
                // SUB-PAWNS — the "GPU rotor" (2026-08-03): a PresentationPawnDefinition can carry SubPawnDefinitions —
                // SECONDARY pawns spawned alongside the main pawn (the cavalry-mount mechanism; a helicopter gunship's
                // rotor rides here). They render independently of the main pawn's mesh/skeleton, which is why no mesh
                // swap, fragment hide, or pawn-array rescue ever touched the ghost rotor. Dump the list once (ground
                // truth), and when the entry opts in (hideSubPawns) clear the array so future spawns — including the
                // respawnAfterLoad respawn — attach nothing.
                DumpAndMaybeClearSubPawns(addon, e);

                EnsureRegistered(animMgr);
                if (e.skeleton == null) return;

                var bodyName = DiscoverBodyMeshName(addon);
                if (!string.IsNullOrEmpty(bodyName)) { RenameBodyMesh(e.skeleton, bodyName); e.layerHint = bodyName; }
                EnsureUploaded(e, animMgr);
                SetMember(addon, "Skeleton", e.skeleton);
                SetMember(addon, "MeshCollection", e.skeleton);
                ReloadFragments(addon, animMgr, e.skeleton, e);
                InjectHandProp(addon, animMgr, e.skeleton, e);
                ApplyTexture(e, animMgr);
                if (!e.repointed) { e.repointed = true; anyRescuable = null; Plugin.Diag($"[Uni] repointed '{name}' -> {e.resourceName} (mesh '{bodyName}', layer '{e.layerHint}')"); }
            }
            catch (Exception ex) { InjectionErrors++; Plugin.Log.LogError("[Uni] repoint: " + ex); }
        }

        internal static void TickTexture()
        {
            if (entries == null) return;
            foreach (var e in entries)
            {
                TickOne(e);
                // prop-skin recovery: same reason as TickOne's re-set path — the game can recreate/reset the weapon
                // material, reverting _MainTex to the EQ proxy. Steady state is a few ReferenceEquals checks.
                if (e.handPropLayer != null && e.propAtlasTex != null)
                    PaintLayer(e.handPropLayer, e.propAtlasTex, e.resourceName);
            }
        }

        // ---- helpers (per-entry generalizations of StealthCruiserInject) ----

        static object LoadSkeleton(int a, int b, int c, int d, string tag)
        {
            var guid = MakeGuid(a, b, c, d);
            var mcType = GameBinding.MeshCollection;
            var adb = GameBinding.AssetDatabase;
            if (guid == null || mcType == null || adb == null) return null;
            var load = adb.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => (m.Name == "LoadAsset" || m.Name == "TryLoadAsset") && m.IsGenericMethodDefinition && m.GetParameters().Length >= 1);
            var g = load?.MakeGenericMethod(mcType);
            if (g == null) { Plugin.Log.LogError($"[Uni] LoadSkeleton '{tag}': Amplitude LoadAsset/TryLoadAsset not resolved (game update?) — skipping this model"); return null; }
            var args = g.GetParameters().Length == 1 ? new[] { guid } : new[] { guid, null };
            var skel = g.Invoke(null, args);
            Plugin.Diag($"[Uni] loaded skeleton '{tag}': " + ((skel as UnityEngine.Object)?.name ?? "NULL (rebuild mod?)"));
            return skel;
        }

        static void EnsureUploaded(ModelEntry e, object animMgr)
        {
            try
            {
                var smi = AccessTools.Field(e.skeleton.GetType(), "skinnedMeshInfos")?.GetValue(e.skeleton) as Array;
                if (smi != null && smi.Length > 0 && Convert.ToInt32(GetMember(smi.GetValue(0), "MeshIndex")) != 0) return;
                var fxMgr = GetMember(animMgr, "FxComponentMeshContentManager");
                if (fxMgr == null) return;
                var sf = AccessTools.Field(e.skeleton.GetType(), "loadingStatus");
                if (sf != null) sf.SetValue(e.skeleton, Enum.ToObject(sf.FieldType, 0));
                var layerIdx = GetMember(animMgr, "FXMeshLayerIndex");
                int slot = GetMember(e.skeleton, "SkeletonId") is int s ? s : 0;
                AccessTools.Method(e.skeleton.GetType(), "LoadIFN")?.Invoke(e.skeleton, new object[] { fxMgr, layerIdx, slot });
            }
            catch (Exception ex) { Plugin.Log.LogError("[Uni] upload: " + ex); }
        }

        // ---- caterpillar investigation (2026-07-25): one-shot dump of a VANILLA pawn's rig ----
        // Runtime is the ONLY place the vanilla rig exists (bundle-side assets; nothing loadable in the SDK
        // project). Config DumpPawnRig = pawn-name substring; when that addon loads we log the skeleton's name
        // tables (bone lists), skinned meshes and every clip-flavoured field — the data that decides how vanilla
        // tank treads roll (many Track/Link bones = clip mechanism; none = shader scroll).
        static readonly HashSet<string> rigDumped = new HashSet<string>();   // once per pawn NAME (a broad filter can match several)
        internal static void MaybeDumpPawnRig(object addon, string name)
        {
            string want;
            try { want = Plugin.DumpPawnRig?.Value ?? ""; } catch { return; }
            if (want.Length == 0 || name.IndexOf(want, StringComparison.OrdinalIgnoreCase) < 0 || !rigDumped.Add(name)) return;
            try
            {
                Plugin.Diag($"[RigDump] ================ VANILLA PAWN RIG: '{name}' ================");
                var sk = GetMember(addon, "Skeleton"); var mc = GetMember(addon, "MeshCollection");
                DumpSkinned(sk, name + ".Skeleton");
                if (mc != null && !ReferenceEquals(mc, sk)) DumpSkinned(mc, name + ".MeshCollection");
                DumpFields(addon, name + ".addon");
                DumpFields(sk, name + ".skeleton");
                DumpNameTables(sk, name + ".skeleton");
                // chase clip-flavoured objects one hop from the addon and the skeleton
                foreach (var host in new[] { addon, sk })
                {
                    if (host == null) continue;
                    for (var bt = host.GetType(); bt != null && bt != typeof(object); bt = bt.BaseType)
                        foreach (var f in bt.GetFields(BF | BindingFlags.DeclaredOnly))
                        {
                            object v = null; try { v = f.GetValue(host); } catch { }
                            if (v == null) continue;
                            string tn = v.GetType().Name;
                            if (tn.IndexOf("Clip", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                tn.IndexOf("Anim", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                DumpFields(v, name + "." + f.Name);
                                DumpNameTables(v, name + "." + f.Name);
                            }
                        }
                }
                DumpClipCollections(sk, name);
                Plugin.Diag($"[RigDump] ================ END '{name}' ================");
            }
            catch (Exception ex) { Plugin.Log.LogError("[RigDump] " + ex); }
        }

        // The GPU clip data: every loaded ClipCollection belonging to this pawn's family — per-clip entries and
        // per-curve encoding. THE caterpillar question in one table: do vanilla clips carry TRANSLATION curves
        // (EncodingFormat RotationTranslation/RotationTranslationScale), and on which bones?
        static void DumpClipCollections(object sk, string name)
        {
            try
            {
                var ccType = GameBinding.ClipCollection;
                if (ccType == null) { Plugin.Diag("[RigDump] ClipCollection type not found"); return; }
                // bone index -> name from the skeleton's BoneInfos
                var boneNames = new List<string>();
                var bi = sk == null ? null : AccessTools.Field(sk.GetType(), "BoneInfos")?.GetValue(sk) as Array;
                if (bi != null) for (int i = 0; i < bi.Length; i++) boneNames.Add(GetMember(bi.GetValue(i), "Name") as string ?? ("#" + i));
                string family = ((sk as UnityEngine.Object)?.name ?? name).Replace("_Skeleton", "");
                foreach (var cc in UnityEngine.Resources.FindObjectsOfTypeAll(ccType))
                {
                    string ccName = (cc as UnityEngine.Object)?.name ?? "";
                    if (ccName.IndexOf(family, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var entries = AccessTools.Field(ccType, "animationClipEntries")?.GetValue(cc) as Array;
                    var curves = AccessTools.Field(ccType, "animationClipCurveEntries")?.GetValue(cc) as Array;
                    Plugin.Diag($"[RigDump] ClipCollection '{ccName}': {entries?.Length ?? 0} clip(s), {curves?.Length ?? 0} curve(s)");
                    if (entries == null || curves == null) continue;
                    for (int e = 0; e < entries.Length; e++)
                    {
                        var en = entries.GetValue(e);
                        string cn = GetMember(en, "Name") as string ?? "?";
                        int frames = GetMember(en, "FrameCount") is int fc ? fc : -1;
                        int bones = GetMember(en, "BonesCount") is int bc ? bc : -1;
                        uint ci = GetMember(en, "CurveIndex") is uint cu ? cu : 0;
                        bool loop = GetMember(en, "Looping") is bool lo && lo;
                        var perBone = new List<string>();
                        var formats = new Dictionary<string, int>();
                        for (int k = 0; k < bones && ci + k < curves.Length; k++)
                        {
                            var cv = curves.GetValue((int)ci + k);
                            int bIdx = GetMember(cv, "BoneIndex") is int b ? b : -1;
                            string fmt = GetMember(cv, "EncodingFormat")?.ToString() ?? "?";
                            formats[fmt] = formats.TryGetValue(fmt, out var n0) ? n0 + 1 : 1;
                            if (fmt != "Fixe")   // the interesting curves: anything that MOVES
                                perBone.Add($"{(bIdx >= 0 && bIdx < boneNames.Count ? boneNames[bIdx] : "#" + bIdx)}={fmt}");
                        }
                        Plugin.Diag($"[RigDump]   clip '{cn}' frames={frames} bones={bones} loop={loop} formats={{{string.Join(", ", formats.Select(kv => kv.Key + ":" + kv.Value))}}}");
                        if (perBone.Count > 0)
                            Plugin.Diag($"[RigDump]     moving: {string.Join(", ", perBone)}");
                    }
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[RigDump] clip collections: " + ex.Message); }
        }

        // Every array field whose elements carry a name-ish member, printed as a full name list (bone tables,
        // clip lists, mesh tables) — capped at 400 entries per table.
        static void DumpNameTables(object o, string label)
        {
            if (o == null) return;
            try
            {
                for (var bt = o.GetType(); bt != null && bt != typeof(object); bt = bt.BaseType)
                    foreach (var f in bt.GetFields(BF | BindingFlags.DeclaredOnly))
                    {
                        object v = null; try { v = f.GetValue(o); } catch { }
                        if (!(v is Array a) || a.Length == 0) continue;
                        var first = a.GetValue(0);
                        if (first == null) continue;
                        string Probe(object it) =>
                            it as string
                            ?? (it is UnityEngine.Object uo ? uo.name : null)
                            ?? GetMember(it, "Name") as string
                            ?? GetMember(it, "BoneName") as string
                            ?? GetMember(it, "MeshName") as string;
                        if (first is string || first is UnityEngine.Object || Probe(first) != null)
                        {
                            var names = new List<string>();
                            for (int i = 0; i < a.Length && i < 400; i++) names.Add(Probe(a.GetValue(i)) ?? "?");
                            Plugin.Diag($"[RigDump] {label}.{f.Name}[{a.Length}]: {string.Join(", ", names)}");
                        }
                    }
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"[RigDump] name tables {label}: " + ex.Message); }
        }

        // GHOST-ROTOR HIERARCHY DUMP (2026-08-03): the pawn's visual root is a Unity PREFAB (description.Template) —
        // child renderers there (the gunship's rotor blur disc) render through ORDINARY Unity rendering, outside the
        // GPU-instanced pipeline every previous lever targeted. One-shot per hideSubPawns entry: walk a matched live
        // PresentationSubPawn's transform tree and log every child with its renderer/mesh/material names, so the next
        // step can disable the rotor children BY NAME instead of guessing. Poll-driven (~3s) from Plugin.Update.
        static readonly HashSet<string> hierDumped = new HashSet<string>();
        static float hierNextAt;
        internal static void ProcessSubPawnVisuals()
        {
            var list = entries;
            if (list == null || !Plugin.UniversalInjectOn.Value) return;
            float now = UnityEngine.Time.time;
            if (now < hierNextAt) return;
            hierNextAt = now + 3f;
            bool anyWanted = false;
            for (int i = 0; i < list.Count; i++) if (list[i].hideSubPawns && !hierDumped.Contains(list[i].resourceName)) { anyWanted = true; break; }
            if (!anyWanted) return;
            try
            {
                var spType = GameBinding.PresentationSubPawn;
                if (spType == null) return;
                foreach (var o in UnityEngine.Object.FindObjectsOfType(spType))
                {
                    if (!(o is UnityEngine.Component c) || c == null) continue;
                    var e = LongestMatch(list, c.gameObject.name, x => x.pawnDescription);
                    if (e == null || !e.hideSubPawns || !hierDumped.Add(e.resourceName)) continue;
                    Plugin.Log.LogInfo($"[Uni][HIER] '{e.resourceName}' pawn GO '{c.gameObject.name}' hierarchy:");
                    DumpTransformTree(c.transform, 0);
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[Uni] ProcessSubPawnVisuals: " + ex.Message); }
        }
        static void DumpTransformTree(UnityEngine.Transform t, int depth)
        {
            if (depth > 8) return;
            var rend = t.GetComponent<UnityEngine.Renderer>();
            string rinfo = "";
            if (rend != null)
            {
                string mesh = rend is UnityEngine.SkinnedMeshRenderer smr && smr.sharedMesh != null ? smr.sharedMesh.name
                            : t.GetComponent<UnityEngine.MeshFilter>()?.sharedMesh?.name ?? "?";
                rinfo = $"  <{rend.GetType().Name} enabled={rend.enabled} mesh='{mesh}' mat='{rend.sharedMaterial?.name}'>";
            }
            Plugin.Log.LogInfo($"[Uni][HIER] {new string(' ', depth * 2)}{t.name} (active={t.gameObject.activeSelf}){rinfo}");
            for (int i = 0; i < t.childCount; i++) DumpTransformTree(t.GetChild(i), depth + 1);
        }

        // The donor definition's SubPawnDefinitions: log what's attached (once per entry), and clear the array when the
        // entry sets hideSubPawns. The definition asset is per-unit-type, so clearing only affects OUR overridden unit;
        // clearing is idempotent (an emptied array stays empty across re-Loads).
        static readonly HashSet<string> subPawnsLogged = new HashSet<string>();
        static void DumpAndMaybeClearSubPawns(object addon, ModelEntry e)
        {
            try
            {
                var def = GetMember(addon, "definition");
                if (def == null) { if (subPawnsLogged.Add(e.resourceName)) Plugin.Diag($"[Uni] {e.resourceName} sub-pawns: no definition on addon"); return; }
                var arr = GetMember(def, "SubPawnDefinitions") as Array;
                if (arr == null) { if (subPawnsLogged.Add(e.resourceName)) Plugin.Diag($"[Uni] {e.resourceName} sub-pawns: definition has no SubPawnDefinitions field ({def.GetType().Name})"); return; }
                if (subPawnsLogged.Add(e.resourceName))
                {
                    Plugin.Diag($"[Uni] {e.resourceName} sub-pawns: {arr.Length} SubPawnDefinition(s) on '{(def as UnityEngine.Object)?.name}'");
                    for (int i = 0; i < arr.Length; i++)
                    {
                        var sub = GetMember(arr.GetValue(i), "Definition");
                        Plugin.Diag($"[Uni]    subPawn[{i}] = '{(sub as UnityEngine.Object)?.name ?? sub?.GetType().Name ?? "null"}'");
                    }
                }
                if (e.hideSubPawns && arr.Length > 0)
                {
                    SetMember(def, "SubPawnDefinitions", Array.CreateInstance(arr.GetType().GetElementType(), 0));
                    Plugin.Log.LogInfo($"[Uni] {e.resourceName}: CLEARED {arr.Length} donor sub-pawn(s) (hideSubPawns — the independent rotor/attachment pawns)");
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"[Uni] {e.resourceName} sub-pawn dump/clear: " + ex.Message); }
        }

        // Diagnostic: list a MeshCollection/Skeleton's skinned sub-meshes (names + fx mesh index), to spot baked-in
        // parts like a rotor that aren't separate fragments.
        static void DumpSkinned(object mc, string label)
        {
            try
            {
                if (mc == null) { Plugin.Diag($"[Uni] {label}: <null>"); return; }
                var arr = AccessTools.Field(mc.GetType(), "skinnedMeshInfos")?.GetValue(mc) as Array;
                if (arr == null) { Plugin.Diag($"[Uni] {label}: no skinnedMeshInfos field"); return; }
                Plugin.Diag($"[Uni] {label}: {arr.Length} skinned sub-mesh(es)");
                for (int i = 0; i < arr.Length; i++)
                {
                    var it = arr.GetValue(i);
                    var nm = GetMember(it, "MeshName");
                    var mi = GetMember(it, "MeshIndex");
                    Plugin.Diag($"[Uni]    {label}[{i}] mesh='{nm}' meshIndex={mi}");
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"[Uni] DumpSkinned {label}: " + ex.Message); }
        }

        // Wide diagnostic: shallow-dump every field of an object — arrays (length + any MeshName/name elements),
        // Unity object refs (name), strings, primitives — to find a mesh/rotor reference hiding outside the usual slots.
        static void DumpFields(object o, string label)
        {
            try
            {
                if (o == null) { Plugin.Diag($"[Uni] {label}: <null>"); return; }
                var t = o.GetType();
                Plugin.Diag($"[Uni] === {label} ({t.Name}) fields ===");
                for (var bt = t; bt != null && bt != typeof(object); bt = bt.BaseType)
                    foreach (var f in bt.GetFields(BF | BindingFlags.DeclaredOnly))
                    {
                        object v = null; try { v = f.GetValue(o); } catch { }
                        string disp;
                        if (v == null) disp = "null";
                        else if (v is Array a)
                        {
                            var parts = new List<string>();
                            for (int i = 0; i < a.Length && i < 8; i++)
                            {
                                var el = a.GetValue(i);
                                var nm = GetMember(el, "MeshName") ?? GetMember(el, "meshName") ?? GetMember(el, "Name") ?? (el as UnityEngine.Object)?.name;
                                parts.Add(nm?.ToString() ?? el?.GetType().Name ?? "null");
                            }
                            disp = $"[{a.Length}] {{{string.Join(", ", parts)}}}";
                        }
                        else if (v is UnityEngine.Object uo) disp = "obj:" + uo.name;
                        else if (v is string s) disp = "\"" + s + "\"";
                        else if (f.FieldType.IsPrimitive || f.FieldType.IsEnum) disp = v.ToString();
                        else disp = v.GetType().Name;
                        // only log the interesting ones to keep the log readable
                        string ln = f.Name.ToLowerInvariant();
                        if (v is Array || v is UnityEngine.Object || v is string || ln.Contains("mesh") || ln.Contains("rotor") || ln.Contains("fx") || ln.Contains("bone") || ln.Contains("attach") || ln.Contains("sub") || ln.Contains("frag"))
                            Plugin.Diag($"[Uni]    {label}.{f.Name} ({f.FieldType.Name}) = {disp}");
                    }
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"[Uni] DumpFields {label}: " + ex.Message); }
        }

        static string DiscoverBodyMeshName(object addon)
        {
            try
            {
                var frags = GetMember(addon, "FragmentEntries") as Array;
                if (frags == null) return null;
                var mnField = AccessTools.Field(frags.GetType().GetElementType(), "meshName");
                if (mnField == null) return null;
                string hull = null, any = null;
                foreach (var f in frags)
                {
                    if (f == null) continue;
                    var mn = mnField.GetValue(f) as string;
                    if (string.IsNullOrEmpty(mn)) continue;
                    if (any == null) any = mn;
                    if (hull == null && mn.IndexOf("Unit_", StringComparison.OrdinalIgnoreCase) >= 0
                        && mn.IndexOf("Water", StringComparison.OrdinalIgnoreCase) < 0
                        && mn.IndexOf("Wake", StringComparison.OrdinalIgnoreCase) < 0
                        && mn.IndexOf("Foam", StringComparison.OrdinalIgnoreCase) < 0
                        && mn.IndexOf("Proof", StringComparison.OrdinalIgnoreCase) < 0)
                        hull = mn;
                }
                return hull ?? any;
            }
            catch { return null; }
        }

        static void RenameBodyMesh(object skel, string newName)
        {
            try
            {
                var arr = AccessTools.Field(skel.GetType(), "skinnedMeshInfos")?.GetValue(skel) as Array;
                if (arr != null && arr.Length > 0)
                {
                    var item = arr.GetValue(0);
                    AccessTools.Field(item.GetType(), "MeshName")?.SetValue(item, newName);
                    arr.SetValue(item, 0);
                }
                var amnField = AccessTools.Field(skel.GetType(), "allMeshNames");
                var amn = amnField?.GetValue(skel) as string[];
                if (amn != null && amn.Length > 0) amn[0] = newName;
                else if (arr != null && arr.Length > 0)
                {
                    var names = new string[arr.Length];
                    for (int i = 0; i < arr.Length; i++) names[i] = GetMember(arr.GetValue(i), "MeshName") as string;
                    amnField?.SetValue(skel, names);
                }
            }
            catch (Exception e) { Plugin.Log.LogError("[Uni] rename: " + e); }
        }

        static void ReloadFragments(object addon, object animMgr, object skel, ModelEntry e)
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
                var encField = AccessTools.Field(fragType, "EncodedMeshAndVisualParticleCount"); // 0 => fragment renders nothing
                var load = AccessTools.Method(fragType, "Load");
                var hides = (e?.hideMeshes ?? "").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
                var hiddenIdx = new System.Collections.Generic.List<int>();
                for (int i = 0; i < frags.Length; i++)
                {
                    var item = frags.GetValue(i);
                    if (item == null) continue;

                    // Dump the donor's fragment mesh names once, so the modder can see what to hide (e.g. a rotor).
                    var fragMesh = mnField?.GetValue(item) as string;
                    if (e != null && !e.fragsLogged) Plugin.Diag($"[Uni] {e.resourceName} donor fragment[{i}] mesh='{fragMesh}'");

                    // HIDE donor fragments whose mesh name matches hideMeshes (kept separate per model: a drone hides the
                    // helicopter rotor; a custom helicopter leaves hideMeshes empty and borrows that same spinning rotor).
                    if (!string.IsNullOrEmpty(fragMesh) && hides.Any(h => fragMesh.IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        encField?.SetValue(item, (uint)0);   // force EncodedMeshAndVisualParticleCount = 0 => invisible
                        frags.SetValue(item, i);
                        hiddenIdx.Add(i);
                        if (e != null && !e.fragsLogged) Plugin.Diag($"[Uni] {e.resourceName} HID donor fragment[{i}] mesh='{fragMesh}'");
                        continue;
                    }

                    mcField?.SetValue(item, skel);
                    // TEXTURE ISOLATION: give OUR body fragment a private CLONE of the output layer. Load() then calls
                    // GetLayerIndexAddItIFN(clone), allocating it a fresh GPU slot -> our skin (painted on the clone)
                    // no longer bleeds onto other units that share the host layer (e.g. the real Visby Corvette).
                    var mn = mnField?.GetValue(item) as string;
                    if (e != null && folField != null && !string.IsNullOrEmpty(e.layerHint) && mn == e.layerHint)
                    {
                        if (e.isolatedLayer == null && folField.GetValue(item) is UnityEngine.Object host && host != null)
                        {
                            var clone = UnityEngine.Object.Instantiate(host);
                            clone.name = e.resourceName + "_OutputLayer";
                            e.isolatedLayer = clone;
                            Plugin.Diag($"[Uni] cloned output layer for {e.resourceName} -> '{clone.name}'");
                        }
                        if (e.isolatedLayer != null) folField.SetValue(item, e.isolatedLayer);
                    }
                    try { load?.Invoke(item, new object[] { skel, renderer, mcm, layer }); }
                    catch (Exception ex) { Plugin.Log.LogWarning("[Uni] frag reload: " + (ex.InnerException ?? ex).Message); }
                    frags.SetValue(item, i);
                }
                // DESCRIPTOR-LEVEL HIDE (the tread spike plague, part 2 — 2026-07-26): the GPU pawn descriptor
                // SNAPSHOTS FragmentEntries at RegisterPawnDefinition, which for persistent definitions happens
                // BEFORE this hook — zeroing the addon array alone leaves the donor fragment drawing (the old
                // "a rotor can't be hidden this late" wall). Patch the snapshot's fragment entries in place,
                // exactly like the hand-prop append does; a zero packed mesh-count renders nothing. This is what
                // finally hides a two-fragment donor's separate skinned tread submesh (MediumTanks_02_tracks),
                // which otherwise skins by donor bone indices against OUR skeleton = giant spikes.
                try
                {
                    var pmType = GameBinding.PawnManager;
                    var pm = pmType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                             ?? AccessTools.Field(pmType, "Instance")?.GetValue(null);
                    int defId = -1;
                    try { defId = Convert.ToInt32(GetMember(addon, "PawnDefinitionId")); } catch { }
                    if (pm != null && defId >= 0)
                    {
                        var descs = AccessTools.Field(pmType, "gpuPawnDescriptorEntries")?.GetValue(pm) as Array;
                        var gfrags = AccessTools.Field(pmType, "gpuPawnDescriptorFragmentEntries")?.GetValue(pm) as Array;
                        var dirtyF = AccessTools.Field(pmType, "descriptorBufferDirty");
                        if (descs != null && gfrags != null && defId < descs.Length)
                        {
                            var dEntry = descs.GetValue(defId);
                            var dT = dEntry.GetType();
                            bool changed = false;
                            uint start = (uint)dT.GetField("StartFragment").GetValue(dEntry);
                            uint count = (uint)dT.GetField("FragmentCount").GetValue(dEntry);
                            var feType = gfrags.GetType().GetElementType();
                            var encGpuF = feType.GetField("EncodedMeshAndVisualParticleCountFxMeshIndex");
                            int patched = 0;
                            foreach (int hi in hiddenIdx)
                                if (hi < count && start + hi < gfrags.Length)
                                {
                                    var ge = gfrags.GetValue((int)(start + hi));
                                    encGpuF.SetValue(ge, 0u);
                                    gfrags.SetValue(ge, (int)(start + hi));
                                    patched++;
                                }
                            if (patched > 0)
                            {
                                changed = true;
                                Plugin.Diag($"[Uni] {e?.resourceName}: descriptor[{defId}] hide patched IN PLACE ({patched} donor fragment(s) zeroed on the GPU snapshot)");
                            }
                            // BONES-COUNT SYNC (the tread spike plague, part 3): RegisterPawnDefinition snapshots
                            // BonesCount from the addon's Skeleton BEFORE our skeleton swap — the descriptor still
                            // says the DONOR's count (MediumTanks: 34). Every vert weighted to a bone past that
                            // reads OUTSIDE the pawn's per-frame slot in the shared pool: hull/wheels/gun (low
                            // indices) rendered fine while all 216 tread-link bones skinned garbage = the spike
                            // sheet. Sync count + bbox from OUR skeleton.
                            try
                            {
                                uint ourBones = 0;
                                try { ourBones = Convert.ToUInt32(GetMember(skel, "BonesCount")); } catch { }
                                var bcF = dT.GetField("BonesCount");
                                if (ourBones > 0 && bcF != null)
                                {
                                    uint dBones = (uint)bcF.GetValue(dEntry);
                                    if (dBones != ourBones)
                                    {
                                        bcF.SetValue(dEntry, ourBones);
                                        var bmin = GetMember(skel, "BBoxMin"); var bmax = GetMember(skel, "BBoxMax");
                                        if (bmin != null) dT.GetField("BBoxMin")?.SetValue(dEntry, bmin);
                                        if (bmax != null) dT.GetField("BBoxMax")?.SetValue(dEntry, bmax);
                                        changed = true;
                                        Plugin.Diag($"[Uni] {e?.resourceName}: descriptor[{defId}] BonesCount {dBones} -> {ourBones} (donor snapshot starved the skeleton; bones past #{dBones - 1} skinned garbage)");
                                    }
                                }
                            }
                            catch (Exception bex) { Plugin.Log.LogWarning("[Uni] descriptor bones sync: " + bex.Message); }
                            if (changed)
                            {
                                descs.SetValue(dEntry, defId);
                                dirtyF?.SetValue(pm, true);
                            }
                        }
                    }
                }
                catch (Exception ex) { Plugin.Log.LogWarning("[Uni] descriptor sync: " + ex.Message); }
                if (e != null) e.fragsLogged = true;   // dumped the donor fragment names once; don't spam on every load
            }
            catch (Exception ex) { InjectionErrors++; Plugin.Log.LogError("[Uni] ReloadFragments: " + ex); }
        }

        // HAND PROP (weapon axis, 2026-07-19): append ONE rigid fragment — a Prop-Lab weapon glued to a bone of OUR
        // skeleton — to the addon's FragmentEntries. The soldier's donor (an APC) has no weapon slots, and the vanilla
        // path needs a matching slot in the pawn DESCRIPTION (GetSlotIndex -1 silently drops the attachment), so the
        // plugin constructs the FragmentEntry directly: our MeshCollection + mesh, a borrowed weapon output layer, and
        // boneName resolved by SUBSTRING against our renamed (b###_<orig>) bones. FragmentEntry.Load() then computes
        // BoneIndex = skeleton.GetBoneIndex(boneName) against OUR skeleton and GPU-encodes the mesh — the same call
        // the vanilla loader makes. Runs right after ReloadFragments in the AddOn.Load window; re-entrant (skips if
        // our mesh name is already in the array). Parse failures / missing assets log a warning and change nothing.
        static void InjectHandProp(object addon, object animMgr, object skel, ModelEntry e)
        {
            if (e == null || string.IsNullOrEmpty(e.handPropGuid)) return;
            try
            {
                int[] Csv(string s)
                {
                    var parts = (s ?? "").Split(','); if (parts.Length != 4) return null;
                    var r = new int[4];
                    for (int i = 0; i < 4; i++) if (!int.TryParse(parts[i].Trim(), out r[i])) return null;
                    return r;
                }
                var cg = Csv(e.handPropGuid);
                if (cg == null) { Plugin.Log.LogWarning($"[Props] '{e.resourceName}' hand prop: bad collection guid '{e.handPropGuid}' (want \"a,b,c,d\")"); return; }
                string propName = string.IsNullOrEmpty(e.handPropName) ? e.resourceName + "Prop" : e.handPropName;
                string meshName = propName + "_DistrictMesh";   // the Prop Lab's fixed mesh name inside the collection
                var frags = GetMember(addon, "FragmentEntries") as Array;
                if (frags == null) return;
                var fragType = frags.GetType().GetElementType();
                var mnField = AccessTools.Field(fragType, "meshName");
                for (int i = 0; i < frags.Length; i++)
                    if (mnField?.GetValue(frags.GetValue(i)) as string == meshName) return;   // already injected on this addon
                // 1) the MeshCollection: already-registered lookup -> Amplitude catalog -> mounted-bundle name fallback
                //    (the catalog misses mod-bundle MeshCollections by GUID — Pawn-Props trap 3); register after a raw load
                //    (RegisterMeshCollection dedupes internally and LoadIFNs the meshes into the GPU content manager).
                var mcType = GameBinding.MeshCollection;
                var guid = MakeGuid(cg[0], cg[1], cg[2], cg[3]);
                if (mcType == null || guid == null) return;
                object mc = null;
                var getMc = animMgr.GetType().GetMethods(BF).FirstOrDefault(m2 => m2.Name == "GetMeshCollection" && m2.GetParameters().Length == 1 && m2.GetParameters()[0].ParameterType == guid.GetType());
                try { mc = getMc?.Invoke(animMgr, new[] { guid }); } catch { }
                bool Dead(object o) => o == null || (o is UnityEngine.Object uo && !uo);
                bool preRegistered = !Dead(mc);   // already encoded (e.g. via [Props] PropCollectionGuids) — angles stamped now would be TOO LATE (guid-cached)
                if (Dead(mc))
                {
                    var adb = GameBinding.AssetDatabase;
                    var loadA = adb?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .FirstOrDefault(m2 => (m2.Name == "TryLoadAsset" || m2.Name == "LoadAsset") && m2.IsGenericMethodDefinition && m2.GetParameters().Length == 1)?.MakeGenericMethod(mcType);
                    try { mc = loadA?.Invoke(null, new[] { guid }); } catch { }
                    if (Dead(mc))
                        foreach (var b in UnityEngine.AssetBundle.GetAllLoadedAssetBundles())
                        { var a = b.LoadAsset(propName + "_Collection"); if (a != null && mcType.IsInstanceOfType(a)) { mc = a; break; } }
                    if (Dead(mc)) { Plugin.Log.LogWarning($"[Props] '{e.resourceName}' hand prop: collection not loadable (guid {e.handPropGuid}, name '{propName}_Collection') — no weapon this session"); return; }
                }
                // 1b) draw-time IMPORT ANGLES — ALWAYS stamped BEFORE RegisterMeshCollection: the encoder DISCARDS
                //     the authored FxMeshContent, rebuilds it from the FxMesh ASSET (rotating vertices by the asset's
                //     ImportAngles), and caches per guid (decompiled: FxMeshLayer.GetFxMeshStructIndex). The baked
                //     angle value does NOT survive the mod bundle — in-game the asset reports the CLASS DEFAULT
                //     (-90,0,0), which silently tipped every prop over vs the preview. So: stamp the registry
                //     override when set, else stamp ZERO — in-game then always matches the baked vertices
                //     (orientation authored via the Prop Lab's Rotation offset).
                {
                    var av = (string.IsNullOrEmpty(e.handPropAngles) ? "0,0,0" : e.handPropAngles).Split(',');
                    if (av.Length == 3
                        && float.TryParse(av[0].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float ax)
                        && float.TryParse(av[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float ay)
                        && float.TryParse(av[2].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float az))
                    {
                        // Stamp BOTH angle carriers before the encoding in Load below: the collection's
                        // FxMeshContent.ImportAngles (harmless; proven ignored by the fragment path in the field)
                        // AND the FxMesh ASSET's own importAngles — the field the district axis proved effective,
                        // read when the mesh geometry is uploaded.
                        bool stamped = false, fxStamped = false;
                        if (GetMember(mc, "skinnedMeshInfos") is Array sis)
                            foreach (var si in sis)
                            {
                                if ((GetMember(si, "MeshName")?.ToString() ?? "") != meshName) continue;
                                var fmc = GetMember(si, "FxMeshContent");
                                if (fmc != null)
                                {
                                    var iaF = AccessTools.Field(fmc.GetType(), "ImportAngles");
                                    if (iaF != null && iaF.FieldType == typeof(UnityEngine.Vector3))
                                    { iaF.SetValue(fmc, new UnityEngine.Vector3(ax, ay, az)); stamped = true; }
                                    // resolve + stamp the FxMesh asset the content entry points at
                                    var fxGuid = GetMember(fmc, "Guid");
                                    var fxType = GameBinding.FxMesh;
                                    if (fxGuid != null && fxType != null)
                                    {
                                        var adb2 = GameBinding.AssetDatabase;
                                        var loadFx = adb2?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                                            .FirstOrDefault(m2 => (m2.Name == "TryLoadAsset" || m2.Name == "LoadAsset") && m2.IsGenericMethodDefinition && m2.GetParameters().Length == 1)?.MakeGenericMethod(fxType);
                                        object fx = null;
                                        try { fx = loadFx?.Invoke(null, new[] { fxGuid }); } catch { }
                                        if (fx != null && !(fx is UnityEngine.Object ufo && !ufo))
                                        {
                                            var iaF2 = AccessTools.Field(fxType, "importAngles");
                                            if (iaF2 != null && iaF2.FieldType == typeof(UnityEngine.Vector3))
                                            { iaF2.SetValue(fx, new UnityEngine.Vector3(ax, ay, az)); fxStamped = true; }
                                        }
                                    }
                                }
                                break;
                            }
                        Plugin.Diag($"[Props] '{e.resourceName}' hand prop import angles ({ax},{ay},{az}) content={(stamped ? "stamped" : "MISS")} fxMeshAsset={(fxStamped ? "stamped" : "MISS")}{(preRegistered ? " — WARNING: collection was already registered/encoded (PropCollectionGuids?), angles may not take this session" : "")}");
                    }
                    else Plugin.Log.LogWarning($"[Props] '{e.resourceName}' hand prop: bad angles '{e.handPropAngles}' (want \"x,y,z\")");
                }
                // register AFTER the stamp (dedupes internally; LoadIFNs the meshes = the encode that reads the angles)
                if (!preRegistered)
                    try { animMgr.GetType().GetMethod("RegisterMeshCollection", BindingFlags.Public | BindingFlags.Instance)?.Invoke(animMgr, new[] { mc }); } catch { }
                // 2) the output layer from the borrowed material ("" = the shared EQ_DLC04_Weapons, sling-verified)
                var mg = Csv(string.IsNullOrEmpty(e.handPropMat) ? "1356489961,1316891353,-864888678,1241300466" : e.handPropMat);
                if (mg == null) { Plugin.Log.LogWarning($"[Props] '{e.resourceName}' hand prop: bad material guid '{e.handPropMat}'"); return; }
                var content = GetMember(animMgr, "Content");
                object fol = null;
                var folM = content?.GetType().GetMethods(BF).FirstOrDefault(m2 => m2.Name == "OutputLayerFromMaterialGuid" && m2.GetParameters().Length == 1);
                try { fol = folM?.Invoke(content, new[] { MakeGuid(mg[0], mg[1], mg[2], mg[3]) }); } catch { }
                if (Dead(fol)) { Plugin.Log.LogWarning($"[Props] '{e.resourceName}' hand prop: no output layer for the borrowed material — no weapon"); return; }
                // 2b) PROP SKIN: give the fragment a PRIVATE CLONE of the borrowed layer, painted with the prop's OWN
                //     baked atlas (<prop>_Atlas, by name from the mounted mod bundles — the mesh's UVs were remapped to
                //     it at bake). Without this the mesh samples the EQ weapon atlas (the M60 rendered leather-tan).
                //     Clone-first mirrors the unit-retexture isolation: the shared EQ layer (real DLC weapons) is never
                //     touched; FragmentEntry.Load below registers the clone via GetLayerIndexAddItIFN = a fresh GPU slot.
                UnityEngine.Texture2D propAtlas = null;
                foreach (var b in UnityEngine.AssetBundle.GetAllLoadedAssetBundles())
                { var a2 = b.LoadAsset(propName + "_Atlas"); if (a2 is UnityEngine.Texture2D t2) { propAtlas = t2; break; } }
                if (propAtlas != null && fol is UnityEngine.Object folObj)
                {
                    var clone = UnityEngine.Object.Instantiate(folObj);
                    clone.name = propName + "_PropLayer";
                    e.handPropLayer = clone;
                    fol = clone;
                }
                else if (propAtlas == null)
                    Plugin.Diag($"[Props] '{e.resourceName}' hand prop: no '{propName}_Atlas' in the mounted bundles — keeping the borrowed layer's skin");
                // 3) OUR bone, by substring (case-insensitive; first match wins)
                string boneSub = string.IsNullOrEmpty(e.handPropBone) ? "R_Hand" : e.handPropBone;
                string boneName = null;
                if (GetMember(skel, "BoneInfos") is Array bones)
                    foreach (var b in bones)
                    {
                        var n = GetMember(b, "Name")?.ToString() ?? "";
                        if (n.IndexOf(boneSub, StringComparison.OrdinalIgnoreCase) >= 0) { boneName = n; break; }
                    }
                if (boneName == null) { Plugin.Log.LogWarning($"[Props] '{e.resourceName}' hand prop: no bone matches '{boneSub}' on our skeleton — no weapon"); return; }
                // 4) construct, Load (encodes the mesh + resolves BoneIndex), append
                var ctor5 = fragType.GetConstructors(BF).FirstOrDefault(c => c.GetParameters().Length == 5);
                if (ctor5 == null) { Plugin.Log.LogWarning("[Props] FragmentEntry ctor not found (game update?)"); return; }
                var item = ctor5.Invoke(new object[] { 0, mc, meshName, fol, boneName });
                var renderer = GetMember(animMgr, "FxComponentRenderer");
                var mcm = GetMember(animMgr, "FxComponentMeshContentManager");
                var layerObj = GetMember(animMgr, "FXMeshLayerIndex");
                int layer = layerObj is int li2 ? li2 : Convert.ToInt32(layerObj ?? 0);
                try { AccessTools.Method(fragType, "Load")?.Invoke(item, new object[] { skel, renderer, mcm, layer }); }
                catch (Exception ex) { Plugin.Log.LogWarning("[Props] hand prop Load: " + (ex.InnerException ?? ex).Message); return; }
                uint enc = 0, bidx = 0;
                try { enc = (uint)AccessTools.Field(fragType, "EncodedMeshAndVisualParticleCount").GetValue(item); bidx = (uint)AccessTools.Field(fragType, "BoneIndex").GetValue(item); } catch { }
                if (enc == 0) { Plugin.Log.LogWarning($"[Props] '{e.resourceName}' hand prop: mesh '{meshName}' encoded to 0 (name not in the collection?) — not appended"); return; }
                // paint OUR atlas onto the private layer clone (post-Load: the clone now owns a registered GPU slot);
                // cached on the entry — TickTexture REPAINTS every frame (cheap ReferenceEquals skip when stable),
                // because the game can reset/recreate the material and a one-shot paint flip-flopped across sessions.
                if (propAtlas != null && e.handPropLayer != null)
                {
                    e.propAtlasTex = propAtlas;
                    PaintLayer(e.handPropLayer, propAtlas, propName);
                }
                var narr = Array.CreateInstance(fragType, frags.Length + 1);
                Array.Copy(frags, narr, frags.Length);
                narr.SetValue(item, frags.Length);
                SetMember(addon, "FragmentEntries", narr);
                // CRITICAL: the GPU pawn DESCRIPTOR (per definition: StartFragment + FragmentCount into the fragment
                // buffer) is snapshotted from FragmentEntries at RegisterPawnDefinition time — an append after that
                // snapshot exists in the array but the renderer still draws the OLD fragment count (the M60 was
                // "glued" yet invisible). Do NOT call UpdateDescriptorBufferContent: that full re-pack SKIPS any
                // definition whose LoadingStatus isn't Loaded yet (common mid-session-load) WITHOUT advancing the
                // descriptor slot, shifting every later pawn type onto the wrong fragments — in the field this drew
                // the recon drones as scattered parts. Instead patch ONLY OUR definition, alignment-safe: copy its
                // existing fragment slots to the tail of the persistent fragment array, append ours there, and
                // repoint just descriptor[defId] at the new contiguous block (its old slots go dead, harmless).
                try
                {
                    var pmType = GameBinding.PawnManager;
                    var pm = pmType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                             ?? AccessTools.Field(pmType, "Instance")?.GetValue(null);
                    int defId = -1;
                    try { defId = Convert.ToInt32(GetMember(addon, "PawnDefinitionId")); } catch { }
                    if (pm == null || defId < 0)
                    {
                        // Not registered yet: RegisterPawnDefinition's own Add() snapshots the appended array — nothing to patch.
                        Plugin.Diag($"[Props] '{e.resourceName}' hand prop appended pre-registration — the registration snapshot carries it");
                    }
                    else
                    {
                        var descF = AccessTools.Field(pmType, "gpuPawnDescriptorEntries");
                        var fragF = AccessTools.Field(pmType, "gpuPawnDescriptorFragmentEntries");
                        var cntF = AccessTools.Field(pmType, "persistentFragmentEntryCount");
                        var dirtyF = AccessTools.Field(pmType, "descriptorBufferDirty");
                        var descs = descF?.GetValue(pm) as Array;
                        var gfrags = fragF?.GetValue(pm) as Array;
                        if (descs == null || gfrags == null || cntF == null || defId >= descs.Length)
                        { Plugin.Log.LogWarning($"[Props] '{e.resourceName}' hand prop: descriptor arrays unreadable (defId {defId}) — prop stays invisible"); }
                        else
                        {
                            var dEntry = descs.GetValue(defId);
                            var dT = dEntry.GetType();
                            uint start = (uint)dT.GetField("StartFragment").GetValue(dEntry);
                            uint count = (uint)dT.GetField("FragmentCount").GetValue(dEntry);
                            int tail = Convert.ToInt32(cntF.GetValue(pm));
                            int need = tail + (int)count + 1;
                            if (gfrags.Length < need)
                            {
                                var grown = Array.CreateInstance(gfrags.GetType().GetElementType(), need + 100);
                                Array.Copy(gfrags, grown, gfrags.Length);
                                fragF.SetValue(pm, grown); gfrags = grown;
                            }
                            for (int k = 0; k < count; k++) gfrags.SetValue(gfrags.GetValue((int)start + k), tail + k);
                            var feType = gfrags.GetType().GetElementType();
                            var ge = Activator.CreateInstance(feType);
                            feType.GetField("SkinnedMeshIndex").SetValue(ge, 0u);
                            feType.GetField("EncodedMeshAndVisualParticleCountFxMeshIndex").SetValue(ge, enc);
                            feType.GetField("BoneIndex").SetValue(ge, bidx);
                            uint folIdx = 0;
                            try { folIdx = (uint)Convert.ToInt32(GetMember(fol, "LayerIndex")); } catch { }
                            feType.GetField("FxOutputLayerIndex").SetValue(ge, folIdx);
                            gfrags.SetValue(ge, tail + (int)count);
                            dT.GetField("StartFragment").SetValue(dEntry, (uint)tail);
                            dT.GetField("FragmentCount").SetValue(dEntry, count + 1);
                            descs.SetValue(dEntry, defId);
                            cntF.SetValue(pm, tail + (int)count + 1);
                            dirtyF?.SetValue(pm, true);
                            Plugin.Diag($"[Props] descriptor[{defId}] repointed: fragments {start}+{count} -> {tail}+{count + 1} (surgical, layer {folIdx})");
                        }
                    }
                }
                catch (Exception ex) { Plugin.Log.LogWarning("[Props] descriptor patch: " + ex.Message); }
                Plugin.Diag($"[Props] '{e.resourceName}' hand prop '{meshName}' glued to bone '{boneName}' (boneIndex {bidx}, encoded {enc})");
            }
            catch (Exception ex) { Plugin.Log.LogError("[Props] InjectHandProp: " + ex); }
        }

    }
}
