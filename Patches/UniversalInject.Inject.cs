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
                        // MUST run BEFORE RegisterMeshCollection + Apply below: Apply's GPU build snapshots
                        // BoneInfos into gpuSkeletonBoneEntiesBuffer, so a later rebase never reaches the GPU
                        // (proven 2026-08-04: rebasing in Repoint — post-Apply — changed nothing on screen).
                        if (e.useDonorClip) RebaseRootIdentity(e.skeleton, e.resourceName);
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
                    foreach (var r in ClipRoles.All)   // every loaded role: animation id + real duration (the table, not nine hand-written lines)
                    {
                        var b = e.Role(r);
                        if (b.coll == null) continue;
                        b.animId = ResolveCollAnimId(animMgr, b.coll, e.resourceName + ClipRoles.Tag(r), out float d);
                        // PRIMARY keeps its previous duration on a failed resolve (the pose hook normalizes by it every frame);
                        // the state roles always take the resolved value (1f default) — both exactly as before the table.
                        if (r == ClipRole.Primary) { if (b.animId >= 0 && d > 0.001f) b.dur = d; }
                        else b.dur = d;
                    }
                }
                registered = true;
                Plugin.Diag($"[Uni] registered {n} skeleton(s) + re-Apply'd; " + string.Join(", ", entries.Select(x => $"{x.resourceName}(skel {x.skeletonId}, anim {x.animId})")));
            }
            catch (Exception e) { NoteInjectionError("register"); Plugin.Log.LogError("[Uni] register: " + e); }
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
                        int prof = MemberInt(def, "AnimationCapabilityProfile", -1);
                        bool humanClass = prof == 1 || prof == 2 || prof == 4 || prof == 6 || prof == 9 || prof == 10 || prof == 12 || prof == 13 || prof == 16;
                        if (unitScaleLogged == null) unitScaleLogged = new HashSet<string>();
                        if (humanClass)
                        {
                            if (unitScaleLogged.Add(name)) Plugin.Diag($"[Resize] '{name}' SKIPPED (human-presentation profile {prof}) — humans/mounts/chariots are excluded from scaling");
                        }
                        else
                        {
                            int sdefId = MemberInt(addon, "PawnDefinitionId", -1);
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
                // TURN EASE for VANILLA units (Formation Lab links, docs/Turn-Ease.md): record EVERY addon's
                // name -> PawnDefinitionId (session-scoped, same id space the pose hook reads as ctx.descId),
                // then sweep the turn links over the WHOLE record. Recording unconditionally makes the mapping
                // independent of parse-vs-load ordering — the SiegeHowitzers MAIN pawn's addon loads BEFORE the
                // formation registry parses (its horse/car/servant satellites load later, on first view), so a
                // map-at-load-only approach permanently missed the one descriptor that actually renders.
                int adProfCat = -1;
                try
                {
                    int adId = MemberInt(addon, "PawnDefinitionId", -1);   // -1 = unknown; the `adId >= 0` test below is what that is for
                    // base TYPE category off the capability profile (category turn ease, docs/Turn-Ease.md) —
                    // same read the Resize human-exclusion uses; turret refinement is learned later from pawns
                    // classification is by CHARACTERISTIC only (user rule — never by name): helicopters and
                    // hovercraft carry the generic vehicle profile (StealthHelicopters measured at 5) and are
                    // refined to HOVER later via the game's own UnitTagAsAbility.Hover flag (the class scan).
                    // -1 THROUGH the classifier, not 0. CategoryFromProfile's `prof >= 0 ? CatHuman : -1` branch
                    // exists to say "unknown" — and was UNREACHABLE while this read went through
                    // Convert.ToInt32(null), which is 0, which classifies as CatHuman. A renamed
                    // AnimationCapabilityProfile would have registered every descriptor as HUMAN and applied human
                    // turn rates to ships, planes and vehicles, with the `adProfCat >= 0` guards below — the very
                    // guards written for this case — unable to fire.
                    adProfCat = CategoryFromProfile(MemberInt(def, "AnimationCapabilityProfile", -1));
                    if (adId >= 0)
                    {
                        addonDefIds[name] = adId;
                        if (adProfCat >= 0 && !vanillaCatByDesc.ContainsKey(adId)) vanillaCatByDesc[adId] = adProfCat;
                    }
                }
                catch { }
                SweepTurnLinks();

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
                if (adProfCat >= 0) e.profCat = adProfCat;   // the entry's TYPE category (per-model rate still wins over it)
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
                // DONOR-CLIP DIAGNOSTIC (2026-08-04): dump the DONOR skeleton's per-bone REST frames (Local + BindPose
                // TRS) alongside OURS — the ground truth behind the donor-clip engine contract (all rests identity;
                // see docs/Donor-Clip-Flight.md). The rebase itself runs earlier, inside EnsureRegistered.
                if (e.useDonorClip && restDumped.Add(e.resourceName))
                {
                    DumpBoneRests(GetMember(addon, "Skeleton"), e.resourceName + " DONOR");
                    DumpBoneRests(e.skeleton, e.resourceName + " OURS");
                }

                EnsureRegistered(animMgr);
                if (e.skeleton == null) return;

                var bodyName = DiscoverBodyMeshName(addon);
                var donorSkel0 = GetMember(addon, "Skeleton");   // pre-swap donor skeleton — Fx-index resolution needs it
                if (!string.IsNullOrEmpty(bodyName)) { RenameBodyMesh(e.skeleton, bodyName); e.layerHint = bodyName; }
                EnsureUploaded(e, animMgr);
                SetMember(addon, "Skeleton", e.skeleton);
                SetMember(addon, "MeshCollection", e.skeleton);
                ReloadFragments(addon, animMgr, e.skeleton, e);
                InjectHandProp(addon, animMgr, e.skeleton, e);
                ApplyTexture(e, animMgr);
                DumpFxIndices(donorSkel0, e, bodyName, animMgr);   // ghost hunt: donor vs our FxMeshIndex + StartIndex needle + descriptor scan
                DumpLayerBudget(e, bodyName, animMgr);             // render-ceiling report: PPC + 255xPPC vs this mesh's PrimitiveCount
                if (!e.repointed) { e.repointed = true; anyRescuable = null; MarkSubPawnsDirty(); Plugin.Diag($"[Uni] repointed '{name}' -> {e.resourceName} (mesh '{bodyName}', layer '{e.layerHint}')"); }
            }
            catch (Exception ex) { NoteInjectionError("repoint"); Plugin.Log.LogError("[Uni] repoint: " + ex); }
        }

        static int texTickFrame;
        internal static void TickTexture()
        {
            if (entries == null) return;
            // RECOVERY path, not a per-frame need: the game resets a material only on its own rebuild events, and a
            // 5-frame re-apply latency is invisible — while the per-frame walk (every entry × output × 3 material
            // fields, reflection + native GetTexture) was a steady ~0.1-0.2 ms (perf pass 2026-08-21).
            if ((++texTickFrame % 5) != 0) return;
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
        [ProcessLived("diagnostic once-per-name dump dedup")] static readonly HashSet<string> rigDumped = new HashSet<string>();   // once per pawn NAME (a broad filter can match several)
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
        [ProcessLived("diagnostic once-per-name dump dedup")] static readonly HashSet<string> hierDumped = new HashSet<string>();
        static float hierNextAt;
        internal static void ProcessSubPawnVisuals()
        {
            var list = entries;
            if (list == null || !Plugin.UniversalInjectOn.Value) return;
            float now = UnityEngine.Time.time;
            if (now < hierNextAt) return;
            hierNextAt = now + 3f;
            bool anyWanted = false;
            for (int i = 0; i < list.Count; i++) if (list[i].hideSubPawns) { anyWanted = true; break; }   // keep polling: a respawn re-caches the donor struct, the source fix must re-repair
            if (!anyWanted) return;
            try
            {
                // the SHARED sub-pawn source (SubPawnScan.cs: a targeted presentation walk, scene-scan verified) — this poll used to run its own full FindObjectsOfType every 3 s
                foreach (var pr in OurSubPawns(list, now, out _))
                {
                    if (!(pr.Key is UnityEngine.Component c) || c == null) continue;
                    var e = pr.Value;
                    if (!e.hideSubPawns) continue;
                    // SOURCE FIX (2026-08-03): the SubPawn caches a private PawnEntry STRUCT at init and re-posts it
                    // every frame — after a respawn it re-cached the DONOR SkeletonId (40) + donor clip, so the game
                    // fights our per-frame overwrite forever (the ghost's engine). Repair the CACHED struct once:
                    // our skeleton + our clip on Pose0. From then on the game itself posts the right state.
                    try
                    {
                        var peF = AccessTools.Field(c.GetType(), "pawnEntry");
                        if (peF != null && e.skeletonId >= 0)
                        {
                            var pe = peF.GetValue(c);
                            int cachedSkel = Convert.ToInt32(GetMember(pe, "SkeletonId"));
                            float cachedHide = MemberFloat(pe, "HideFactor", 0f);   // was correct only by coincidence: 0f == Convert.ToSingle(null)
                            // THE SANDWICH (2026-08-03, the ghost kill): the ghost renders from this CACHED struct's
                            // state (proven: repairing its skeleton reoriented the ghost; offsetting OUR post-hook
                            // position moved the model but not the ghost). So poison the source: the cached struct is
                            // permanently HideFactor=1 — whatever draws the pre-hook state draws nothing — while the
                            // pose hook sets HideFactor=0 on the real pawn every frame, so OUR model stays visible.
                            if (cachedSkel != e.skeletonId || cachedHide < 1f)
                            {
                                SetMember(pe, "SkeletonId", e.skeletonId);
                                SetMember(pe, "HideFactor", 1f);
                                var p0 = GetMember(pe, "Pose0");
                                if (p0 != null && e.animId >= 0)
                                {
                                    SetMember(p0, "AnimationId", e.animId);
                                    SetMember(pe, "Pose0", p0);
                                }
                                peF.SetValue(c, pe);
                                Plugin.Diag($"[Uni][SRCFIX] '{e.resourceName}' sub-pawn '{c.gameObject.name}': cached PawnEntry poisoned (skel {cachedSkel} -> {e.skeletonId}, HideFactor 1, Pose0 -> {e.animId})");
                            }
                        }
                    }
                    catch (Exception sx) { Plugin.Log.LogWarning("[Uni] SRCFIX: " + sx.Message); }
                    // UNITY-SIDE RENDERER CENSUS (2026-08-03, the last ghost): with every pawn accounted for, the giant
                    // translucent rotor must be ordinary Unity geometry positioned at the unit (the GetBoneTRS('Base')
                    // caller — a RotationTransformInfo-driven attachment living OUTSIDE the SubPawn's own GameObject).
                    // Log every Renderer within 15 units (path, mesh, material, size); auto-disable those with
                    // donor-specific names (Gunship/Helix/Rotor/Blur — NOT "Helicopter", which matches our own assets).
                    if (UnityEngine.Time.time >= e.rendererCensusNextAt)
                    {
                        e.rendererCensusNextAt = UnityEngine.Time.time + 15f;
                        var origin = c.transform.position;
                        int found = 0;
                        foreach (var r in UnityEngine.Object.FindObjectsOfType<UnityEngine.Renderer>())
                        {
                            if (r == null || !r.enabled) continue;
                            if ((r.bounds.center - origin).sqrMagnitude > 225f) continue;   // within 15 units
                            string path = r.transform.name;
                            for (var t = r.transform.parent; t != null && path.Length < 200; t = t.parent) path = t.name + "/" + path;
                            string mesh = r is UnityEngine.SkinnedMeshRenderer smr2 && smr2.sharedMesh != null ? smr2.sharedMesh.name
                                        : r.GetComponent<UnityEngine.MeshFilter>()?.sharedMesh?.name ?? "-";
                            string mat = r.sharedMaterial != null ? r.sharedMaterial.name : "-";
                            string hay = path + "|" + mesh + "|" + mat;
                            bool donorish = new[] { "Gunship", "Helix", "Rotor", "Blur" }.Any(k => hay.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
                            Plugin.Diag($"[Uni][REND] '{e.resourceName}' {r.GetType().Name} '{path}' mesh='{mesh}' mat='{mat}' size={r.bounds.size} {(donorish ? "<<< DONOR-ISH — DISABLING" : "")}");
                            if (donorish) r.enabled = false;
                            if (++found >= 25) break;
                        }
                        Plugin.Diag($"[Uni][REND] '{e.resourceName}': {found} renderer(s) within 15 units");
                    }
                    if (!hierDumped.Add(e.resourceName)) continue;
                    Plugin.Diag($"[Uni][HIER] '{e.resourceName}' pawn GO '{c.gameObject.name}' hierarchy:");
                    DumpTransformTree(c.transform, 0);
                    // OUTPUT-LAYER AUTOPSY (2026-08-03, endgame): the donor mesh carries a 53-prim TRANSPARENT slice in
                    // ContentLayer 0 (the rotor blur disc) drawn via the output layer's visual-particle channel — and our
                    // texture-isolation CLONE of the donor's FxOutputLayer inherited that channel: we draw the ghost
                    // ourselves. Dump every field of the clone so the disc channel can be identified and cut.
                    if (e.isolatedLayer != null)
                    {
                        Plugin.Diag($"[Uni][LAYER] '{e.resourceName}' cloned output layer '{(e.isolatedLayer as UnityEngine.Object)?.name}' fields:");
                        DumpFields(e.isolatedLayer, e.resourceName + " outputLayer");
                    }
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
            Plugin.Diag($"[Uni][HIER] {new string(' ', depth * 2)}{t.name} (active={t.gameObject.activeSelf}){rinfo}");
            for (int i = 0; i < t.childCount; i++) DumpTransformTree(t.GetChild(i), depth + 1);
        }

        // CRUSH THE GHOST SLICE (2026-08-03, the geometry kill): the ghost rotor's geometry is the donor mesh's slice
        // in ContentLayer 0 (53 prims at start 0x006400) — drawn by a unit-level pass no pawn/descriptor lever reaches.
        // So stop fighting the drawer and destroy the GEOMETRY: scale that slice's vertex positions to ~0 in the
        // layer's CPU WriteContent (the mesh-scale engine's proven write path) and Apply. A degenerate point renders
        // as nothing regardless of who draws it. Self-verifying probe (first vertex) makes it idempotent and
        // reload-resilient; re-run from the NEAR tick. Affects ONLY the layer-0 blur slice — bodies live in layer 2.
        internal static void CrushGhostSlice(object animMgr, ModelEntry e, uint donorFxIdx)
        {
            try
            {
                if (animMgr == null || donorFxIdx == 0) return;
                var mcm = GetMember(animMgr, "FxComponentMeshContentManager");
                var layers = GetMember(mcm, "Layers") as Array ?? AccessTools.Field(mcm.GetType(), "layers")?.GetValue(mcm) as Array;
                if (layers == null || layers.Length == 0) return;
                var layer = layers.GetValue(0);   // layer 0 = the slice's home
                var meshTable = GetMember(layer, "HxFxOneMeshComputeBufferData") as Array;
                var vertBufObj = AccessTools.Field(layer.GetType(), "vertexBuffer")?.GetValue(layer);
                var verts = vertBufObj == null ? null : GetMember(vertBufObj, "WriteContent") as Array;
                if (meshTable == null || verts == null || donorFxIdx >= meshTable.Length) return;
                // FORMAT-AGNOSTIC DEGENERATION: layer 0 uses the static quantized vertex format (no raw Pos), so
                // instead of zeroing positions, copy the FIRST vertex record over the whole slice — identical
                // vertices make every triangle zero-area, which renders as nothing in any encoding.
                var mEntry = meshTable.GetValue((int)donorFxIdx);
                var msType = mEntry.GetType();
                uint sv = Convert.ToUInt32(msType.GetField("StartVertex").GetValue(mEntry));
                int vc = Convert.ToInt32(msType.GetField("VertexCount").GetValue(mEntry));
                if (vc <= 1 || sv + 1 >= verts.Length) return;
                var first = verts.GetValue((int)sv);
                if (Equals(verts.GetValue((int)sv + 1), first)) return;   // already degenerated (write persisted)
                for (uint v = sv + 1; v < sv + vc && v < verts.Length; v++)
                    verts.SetValue(first, (int)v);
                AccessTools.Method(vertBufObj.GetType(), "Apply", Type.EmptyTypes)?.Invoke(vertBufObj, null);
                Plugin.Diag($"[Uni][CRUSH] '{e.resourceName}': DEGENERATED donor mesh {donorFxIdx}'s layer-0 slice ({vc} verts @ {sv}, format {verts.GetType().GetElementType().Name}) — every triangle is now zero-area");
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[Uni] CrushGhostSlice: " + ex.Message); }
        }
        static uint ghostDonorFxIdx;   // stashed for periodic re-crush from the NEAR tick

        // LIVE GHOST BISECT (2026-08-03 23:00): the ghost's mesh is SOME layer-0 FxMesh we can't name — so find it by
        // elimination WITHOUT relaunching. Poll BepInEx/config/haf_ghostbisect.txt every ~2s:
        //     crush <from> <to>    degenerate layer-0 meshes [from..to] (originals saved on first touch)
        //     restore              restore every saved mesh
        // The operator edits the file while the player watches the ghost; halving the range pins the mesh in ~8 rounds.
        static string lastBisectCmd = "";
        [ProcessLived("per-bisect scratch, cleared per run")] static readonly Dictionary<long, Array> bisectSaved = new Dictionary<long, Array>();   // (layer<<32)|meshIdx -> original vertex records (animMgr manager)
        // manager-aware storage: key -> [mcm, layerIdx, meshIdx, savedArray]. The ghost's mesh proved to live outside
        // the AnimationManager's content manager entirely — other FxManager Behaviours in the scene own their own.
        [ProcessLived("per-bisect scratch, cleared per run")] static readonly Dictionary<string, object[]> bisectSavedM = new Dictionary<string, object[]>();
        static List<object> ListFxManagers()
        {
            var outp = new List<object>();
            var t = GameBinding.FxManager;
            if (t == null) return outp;
            foreach (var o in UnityEngine.Object.FindObjectsOfType(t)) outp.Add(o);
            outp.Sort((a, b) => string.CompareOrdinal((a as UnityEngine.Object)?.name, (b as UnityEngine.Object)?.name));
            return outp;
        }
        static object McmOf(object fxManager)
        {
            try
            {
                // fallback changed from a literal TypeByName to the DERIVED accessor: the primary here already IS the
                // literal (ContentLayer is Cached("…FxComponentMeshContentManager+ContentLayer"), so .DeclaringType is
                // that type), which means the fallback only runs in a world where that name is gone — exactly where
                // reading the type off AnimationManager's field is the more robust probe, not the less.
                var mcmType = GameBinding.ContentLayer?.DeclaringType ?? GameBinding.FxComponentMeshContentManager;
                var g = fxManager.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "GetFxComponent" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                return g == null || mcmType == null ? null : g.MakeGenericMethod(mcmType).Invoke(fxManager, null);
            }
            catch { return null; }
        }
        internal static void PollGhostBisect()
        {
            try
            {
                if (ghostAnimMgr == null) return;
                var path = Path.Combine(Paths.ConfigPath, "haf_ghostbisect.txt");
                if (!File.Exists(path)) return;
                string cmd = File.ReadAllText(path).Trim();
                if (cmd.Length == 0 || cmd == lastBisectCmd) return;
                lastBisectCmd = cmd;
                var mcm = GetMember(ghostAnimMgr, "FxComponentMeshContentManager");
                var layers = GetMember(mcm, "Layers") as Array ?? AccessTools.Field(mcm.GetType(), "layers")?.GetValue(mcm) as Array;
                if (layers == null) return;

                object LayerObjs(int li, out Array table, out object vb, out Array vts)
                {
                    table = null; vb = null; vts = null;
                    if (li < 0 || li >= layers.Length) return null;
                    var lay = layers.GetValue(li);
                    table = GetMember(lay, "HxFxOneMeshComputeBufferData") as Array;
                    vb = AccessTools.Field(lay.GetType(), "vertexBuffer")?.GetValue(lay);
                    vts = vb == null ? null : GetMember(vb, "WriteContent") as Array;
                    return lay;
                }

                var parts = cmd.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                // mgrs — enumerate every FxManager Behaviour + its mesh content manager's layer/mesh population
                if (parts[0].Equals("mgrs", StringComparison.OrdinalIgnoreCase))
                {
                    var ms = ListFxManagers();
                    Plugin.Log.LogInfo($"[Uni][BISECT] {ms.Count} FxManager(s) in scene:");
                    for (int m = 0; m < ms.Count; m++)
                    {
                        var mcm2 = McmOf(ms[m]);
                        var lay2 = mcm2 == null ? null : GetMember(mcm2, "Layers") as Array ?? AccessTools.Field(mcm2.GetType(), "layers")?.GetValue(mcm2) as Array;
                        string desc = mcm2 == null ? "no mesh content manager" : $"{lay2?.Length ?? 0} layer(s)";
                        if (lay2 != null)
                            for (int li = 0; li < lay2.Length; li++)
                            {
                                var tb = GetMember(lay2.GetValue(li), "HxFxOneMeshComputeBufferData") as Array;
                                int pop = 0;
                                if (tb != null)
                                    for (int i = 1; i < tb.Length; i++)
                                        if (Convert.ToInt32(tb.GetValue(i).GetType().GetField("VertexCount").GetValue(tb.GetValue(i))) > 0) pop++;
                                desc += $" | L{li}:{pop} meshes";
                            }
                        Plugin.Log.LogInfo($"[Uni][BISECT]   mgr[{m}] '{(ms[m] as UnityEngine.Object)?.name}' — {desc}");
                    }
                    return;
                }
                // crushm <mgr> <layer> <from> <to> — crush in ANY manager's content layer
                if (parts[0].Equals("crushm", StringComparison.OrdinalIgnoreCase) && parts.Length >= 5
                    && int.TryParse(parts[1], out int mIdx) && int.TryParse(parts[2], out int mLayer)
                    && int.TryParse(parts[3], out int mFrom) && int.TryParse(parts[4], out int mTo))
                {
                    var ms = ListFxManagers();
                    if (mIdx < 0 || mIdx >= ms.Count) { Plugin.Log.LogWarning($"[Uni][BISECT] mgr {mIdx} out of range ({ms.Count})"); return; }
                    var mcm2 = McmOf(ms[mIdx]);
                    var lay2 = mcm2 == null ? null : GetMember(mcm2, "Layers") as Array ?? AccessTools.Field(mcm2.GetType(), "layers")?.GetValue(mcm2) as Array;
                    if (lay2 == null || mLayer < 0 || mLayer >= lay2.Length) { Plugin.Log.LogWarning($"[Uni][BISECT] mgr {mIdx} layer {mLayer} unreachable"); return; }
                    var lobj = lay2.GetValue(mLayer);
                    var tb = GetMember(lobj, "HxFxOneMeshComputeBufferData") as Array;
                    var vb2 = AccessTools.Field(lobj.GetType(), "vertexBuffer")?.GetValue(lobj);
                    var vts2 = vb2 == null ? null : GetMember(vb2, "WriteContent") as Array;
                    if (tb == null || vts2 == null) { Plugin.Log.LogWarning($"[Uni][BISECT] mgr {mIdx} L{mLayer} buffers unreachable"); return; }
                    var svF2 = tb.GetType().GetElementType().GetField("StartVertex");
                    var vcF2 = tb.GetType().GetElementType().GetField("VertexCount");
                    int crushed2 = 0;
                    for (int mi = Math.Max(1, mFrom); mi <= mTo && mi < tb.Length; mi++)
                    {
                        var mE = tb.GetValue(mi);
                        uint sv = Convert.ToUInt32(svF2.GetValue(mE));
                        int vc = Convert.ToInt32(vcF2.GetValue(mE));
                        if (vc <= 1 || sv + 1 >= vts2.Length) continue;
                        string key = $"{mIdx}:{mLayer}:{mi}";
                        if (!bisectSavedM.ContainsKey(key))
                        {
                            var save = Array.CreateInstance(vts2.GetType().GetElementType(), vc);
                            for (int i = 0; i < vc && sv + i < vts2.Length; i++) save.SetValue(vts2.GetValue((int)sv + i), i);
                            bisectSavedM[key] = new object[] { mcm2, mLayer, mi, save };
                        }
                        var first = vts2.GetValue((int)sv);
                        for (uint v = sv + 1; v < sv + vc && v < vts2.Length; v++) vts2.SetValue(first, (int)v);
                        crushed2++;
                    }
                    AccessTools.Method(vb2.GetType(), "Apply", Type.EmptyTypes)?.Invoke(vb2, null);
                    Plugin.Log.LogInfo($"[Uni][BISECT] mgr[{mIdx}] L{mLayer}: crushed [{mFrom}..{mTo}] ({crushed2} touched)");
                    return;
                }
                // rend <x> <y> <z> <radius> — census every Unity Renderer near a WORLD point (the earlier census
                // centered on the SubPawn transform, which sits at origin for these bare GOs — it scanned nothing).
                if (parts[0].Equals("rend", StringComparison.OrdinalIgnoreCase) && parts.Length >= 5
                    && float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float rx)
                    && float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float ry)
                    && float.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float rz)
                    && float.TryParse(parts[4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float rr))
                {
                    var origin = new UnityEngine.Vector3(rx, ry, rz);
                    int found = 0;
                    foreach (var r in UnityEngine.Object.FindObjectsOfType<UnityEngine.Renderer>())
                    {
                        if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                        if ((r.bounds.center - origin).sqrMagnitude > rr * rr) continue;
                        string rpath = r.transform.name;
                        for (var t2 = r.transform.parent; t2 != null && rpath.Length < 220; t2 = t2.parent) rpath = t2.name + "/" + rpath;
                        string mesh = r is UnityEngine.SkinnedMeshRenderer smr3 && smr3.sharedMesh != null ? smr3.sharedMesh.name
                                    : r.GetComponent<UnityEngine.MeshFilter>()?.sharedMesh?.name ?? "-";
                        Plugin.Log.LogInfo($"[Uni][REND2] {r.GetType().Name} '{rpath}' mesh='{mesh}' mat='{r.sharedMaterial?.name}' center={r.bounds.center} size={r.bounds.size}");
                        if (++found >= 40) break;
                    }
                    Plugin.Log.LogInfo($"[Uni][REND2] {found} renderer(s) within {rr} of {origin}");
                    return;
                }
                // kill <substring> — disable every Renderer whose path/mesh/material matches; revive <substring> undoes
                if ((parts[0].Equals("kill", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("revive", StringComparison.OrdinalIgnoreCase)) && parts.Length >= 2)
                {
                    bool on = parts[0].Equals("revive", StringComparison.OrdinalIgnoreCase);
                    string needle = cmd.Substring(parts[0].Length).Trim();
                    int hit = 0;
                    foreach (var r in UnityEngine.Object.FindObjectsOfType<UnityEngine.Renderer>())
                    {
                        if (r == null) continue;
                        string rpath = r.transform.name;
                        for (var t2 = r.transform.parent; t2 != null && rpath.Length < 220; t2 = t2.parent) rpath = t2.name + "/" + rpath;
                        string mesh = r is UnityEngine.SkinnedMeshRenderer smr4 && smr4.sharedMesh != null ? smr4.sharedMesh.name
                                    : r.GetComponent<UnityEngine.MeshFilter>()?.sharedMesh?.name ?? "-";
                        string hay = rpath + "|" + mesh + "|" + (r.sharedMaterial?.name ?? "");
                        if (hay.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        r.enabled = on; hit++;
                        Plugin.Log.LogInfo($"[Uni][REND2] {(on ? "REVIVED" : "KILLED")} '{rpath}' mesh='{mesh}'");
                    }
                    Plugin.Log.LogInfo($"[Uni][REND2] {(on ? "revived" : "killed")} {hit} renderer(s) matching '{needle}'");
                    return;
                }
                if (parts[0].Equals("restore", StringComparison.OrdinalIgnoreCase))
                {
                    int restored = 0;
                    var touchedLayers = new HashSet<int>();
                    foreach (var kv in bisectSaved)
                    {
                        int li = (int)(kv.Key >> 32); int mi = (int)(kv.Key & 0xFFFFFFFF);
                        if (LayerObjs(li, out var table, out var vb, out var vts) == null || table == null || vts == null) continue;
                        var mE = table.GetValue(mi);
                        uint sv = Convert.ToUInt32(mE.GetType().GetField("StartVertex").GetValue(mE));
                        for (int i = 0; i < kv.Value.Length && sv + i < vts.Length; i++)
                            vts.SetValue(kv.Value.GetValue(i), (int)sv + i);
                        touchedLayers.Add(li); restored++;
                    }
                    foreach (var li in touchedLayers)
                        if (LayerObjs(li, out _, out var vb2, out _) != null && vb2 != null)
                            AccessTools.Method(vb2.GetType(), "Apply", Type.EmptyTypes)?.Invoke(vb2, null);
                    bisectSaved.Clear();
                    // manager-aware saves too
                    var touchedBufs = new HashSet<object>();
                    foreach (var kv in bisectSavedM)
                    {
                        var mcm3 = kv.Value[0]; int li3 = (int)kv.Value[1]; int mi3 = (int)kv.Value[2]; var save3 = kv.Value[3] as Array;
                        var lay3 = GetMember(mcm3, "Layers") as Array ?? AccessTools.Field(mcm3.GetType(), "layers")?.GetValue(mcm3) as Array;
                        if (lay3 == null || li3 >= lay3.Length) continue;
                        var lobj3 = lay3.GetValue(li3);
                        var tb3 = GetMember(lobj3, "HxFxOneMeshComputeBufferData") as Array;
                        var vb3 = AccessTools.Field(lobj3.GetType(), "vertexBuffer")?.GetValue(lobj3);
                        var vts3 = vb3 == null ? null : GetMember(vb3, "WriteContent") as Array;
                        if (tb3 == null || vts3 == null || save3 == null) continue;
                        var mE3 = tb3.GetValue(mi3);
                        uint sv3 = Convert.ToUInt32(mE3.GetType().GetField("StartVertex").GetValue(mE3));
                        for (int i = 0; i < save3.Length && sv3 + i < vts3.Length; i++) vts3.SetValue(save3.GetValue(i), (int)sv3 + i);
                        touchedBufs.Add(vb3); restored++;
                    }
                    foreach (var vb4 in touchedBufs) AccessTools.Method(vb4.GetType(), "Apply", Type.EmptyTypes)?.Invoke(vb4, null);
                    bisectSavedM.Clear();
                    Plugin.Log.LogInfo($"[Uni][BISECT] restored {restored} mesh(es) across {touchedLayers.Count + touchedBufs.Count} buffer(s)");
                    return;
                }
                // crush <layer> <from> <to>   (legacy 3-arg form = layer 0)
                if (parts[0].Equals("crush", StringComparison.OrdinalIgnoreCase) && parts.Length >= 3)
                {
                    int layerIdx = 0, from, to;
                    if (parts.Length >= 4 && int.TryParse(parts[1], out layerIdx) && int.TryParse(parts[2], out from) && int.TryParse(parts[3], out to)) { }
                    else if (int.TryParse(parts[1], out from) && int.TryParse(parts[2], out to)) layerIdx = 0;
                    else return;
                    if (LayerObjs(layerIdx, out var table, out var vb, out var vts) == null || table == null || vts == null)
                    { Plugin.Log.LogWarning($"[Uni][BISECT] layer {layerIdx} unreachable ({layers.Length} layers exist)"); return; }
                    var svF = table.GetType().GetElementType().GetField("StartVertex");
                    var vcF = table.GetType().GetElementType().GetField("VertexCount");
                    int crushed = 0;
                    for (int mi = Math.Max(1, from); mi <= to && mi < table.Length; mi++)
                    {
                        var mE = table.GetValue(mi);
                        uint sv = Convert.ToUInt32(svF.GetValue(mE));
                        int vc = Convert.ToInt32(vcF.GetValue(mE));
                        if (vc <= 1 || sv + 1 >= vts.Length) continue;
                        long key = ((long)layerIdx << 32) | (uint)mi;
                        if (!bisectSaved.ContainsKey(key))
                        {
                            var save = Array.CreateInstance(vts.GetType().GetElementType(), vc);
                            for (int i = 0; i < vc && sv + i < vts.Length; i++) save.SetValue(vts.GetValue((int)sv + i), i);
                            bisectSaved[key] = save;
                        }
                        var first = vts.GetValue((int)sv);
                        for (uint v = sv + 1; v < sv + vc && v < vts.Length; v++) vts.SetValue(first, (int)v);
                        crushed++;
                    }
                    AccessTools.Method(vb.GetType(), "Apply", Type.EmptyTypes)?.Invoke(vb, null);
                    Plugin.Log.LogInfo($"[Uni][BISECT] layer {layerIdx}: crushed meshes [{from}..{to}] ({crushed} touched; {bisectSaved.Count} saved total; {layers.Length} layers exist)");
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[Uni] PollGhostBisect: " + ex.Message); }
        }

        // Cut the transparent rotor-blur pass out of a freshly-cloned FxOutputLayer, keeping the opaque body pass.
        // Dumps every RenderOutput's fields first so a wrong keep-choice is visible in the log (if the body vanishes
        // and the disc stays, the indices are swapped — flip the kept index). Runs ONLY at clone time, pre-registration.
        static void PruneCloneRenderOutputs(UnityEngine.Object clone, ModelEntry e)
        {
            try
            {
                var roF = AccessTools.Field(clone.GetType(), "renderOutputs");
                var ros = roF?.GetValue(clone) as Array;
                if (ros == null || ros.Length <= 1) { Plugin.Diag($"[Uni][LAYER] '{e.resourceName}': clone has {(ros?.Length ?? -1)} renderOutput(s) — nothing to prune"); return; }
                for (int i = 0; i < ros.Length; i++)
                {
                    Plugin.Diag($"[Uni][LAYER] '{e.resourceName}' renderOutput[{i}]:");
                    DumpFields(ros.GetValue(i), $"{e.resourceName} renderOutput[{i}]");
                }
                var kept = Array.CreateInstance(ros.GetType().GetElementType(), 1);
                kept.SetValue(ros.GetValue(0), 0);
                roF.SetValue(clone, kept);
                Plugin.Diag($"[Uni][LAYER] '{e.resourceName}': PRUNED clone renderOutputs {ros.Length} -> 1 (kept [0] — the opaque body pass; the transparent blur pass is gone)");
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[Uni] PruneCloneRenderOutputs: " + ex.Message); }
        }

        // UNIT MESH RENDER BUDGET (2026-09-05, the Great Galley's vanishing masts): a unit mesh renders as
        // sub-particles, count = ceil(PrimitiveCount / the layer's primitivePerParticleCount), and that count is
        // packed into 8 bits -> HARD-CLAMPED at 255. Primitives past 255 x PPC are silently never drawn — the
        // district grove class (see DistrictMeshDensityBoost), one pipeline over. The galley bracketed the unit
        // layer's ceiling at just under 39,279 quads by four bakes and five screenshots; this dump replaces that
        // archaeology with one log line per injected unit: every layer the mesh occupies, its PPC, the 255xPPC
        // ceiling, and a LOUD "OVER by N" when the clamp is eating geometry.
        [ProcessLived("diagnostic once-per-name dump dedup")] static readonly HashSet<string> budgetDumped = new HashSet<string>();
        [ProcessLived("diagnostic once-per-layer-type dump dedup")] static readonly HashSet<string> budgetTypesDumped = new HashSet<string>();
        static void DumpLayerBudget(ModelEntry e, string bodyName, object animMgr)
        {
            try
            {
                if (e?.skeleton == null || string.IsNullOrEmpty(bodyName) || !budgetDumped.Add(e.resourceName)) return;
                var mi = AccessTools.Method(e.skeleton.GetType(), "GetFxMeshIndex", new[] { typeof(string) });
                var idxObj = mi?.Invoke(e.skeleton, new object[] { bodyName });
                if (idxObj == null) return;
                uint fxIdx = Convert.ToUInt32(idxObj);
                var mcm = GetMember(animMgr, "FxComponentMeshContentManager");
                var layers = GetMember(mcm, "Layers") as Array ?? AccessTools.Field(mcm.GetType(), "layers")?.GetValue(mcm) as Array;
                if (layers == null) { Plugin.Log.LogWarning("[Uni][BUDGET] mesh content layers not found — cannot report the render ceiling"); return; }
                for (int li = 0; li < layers.Length; li++)
                {
                    var lay = layers.GetValue(li);
                    var buf = GetMember(lay, "HxFxOneMeshComputeBufferData") as Array;
                    if (buf == null || fxIdx >= buf.Length) continue;
                    uint prim = MemberUInt(buf.GetValue((int)fxIdx), "PrimitiveCount", 0);
                    if (prim == 0) continue;   // mesh not present in this layer
                    if (!TryMemberInt(lay, "primitivePerParticleCount", out int ppc) || ppc <= 0)
                    {
                        // The unit ContentLayer's class doesn't carry the district layer's field name. DISCOVER it:
                        // dump every numeric field/property once per layer type+index, so the real PPC-like member
                        // (and its value — the ceiling divisor) is identified from one log instead of guessed.
                        Plugin.Log.LogWarning($"[Uni][BUDGET] '{e.resourceName}' layer {li}: prim={prim} but primitivePerParticleCount unreadable — type {lay.GetType().FullName}");
                        if (budgetTypesDumped.Add(lay.GetType().FullName + "#" + li))
                        {
                            var sb = new System.Text.StringBuilder($"[Uni][BUDGET] layer {li} ({lay.GetType().Name}) numeric members:\n");
                            const BindingFlags BFA = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                            foreach (var f in lay.GetType().GetFields(BFA))
                                if (f.FieldType == typeof(int) || f.FieldType == typeof(uint) || f.FieldType == typeof(long) || f.FieldType == typeof(ushort) || f.FieldType == typeof(byte))
                                    try { sb.Append($"  field {f.Name} = {f.GetValue(lay)}\n"); } catch { }
                            foreach (var p in lay.GetType().GetProperties(BFA))
                                if (p.CanRead && p.GetIndexParameters().Length == 0 && (p.PropertyType == typeof(int) || p.PropertyType == typeof(uint) || p.PropertyType == typeof(long)))
                                    try { sb.Append($"  prop  {p.Name} = {p.GetValue(lay)}\n"); } catch { }
                            Plugin.Log.LogInfo(sb.ToString().TrimEnd());
                        }
                        continue;
                    }
                    long ceiling = 255L * ppc;
                    Plugin.Log.LogInfo($"[Uni][BUDGET] '{e.resourceName}' layer {li}: mesh prim={prim}, PPC={ppc}, ceiling=255x{ppc}={ceiling}"
                        + (prim > ceiling ? $" — OVER by {prim - ceiling}: that geometry is silently NOT DRAWN" : " — fits"));
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[Uni] layer budget dump: " + ex.Message); }
        }

        // FX-INDEX RESOLUTION (ghost hunt): the GPU descriptor encodes an FxMeshIndex in ITS OWN numbering (not
        // skinnedMeshInfos.MeshIndex). GetFxMeshIndex(name) on a mesh collection resolves name -> that numbering
        // (the formation clone path uses it). Log the DONOR skeleton's index for the donor body mesh and OURS for the
        // same (renamed) mesh — two known (descriptor, index) pairs per entry crack the encoding's bit layout, and the
        // donor's index is the needle to find in whichever descriptor still draws the ghost.
        [ProcessLived("diagnostic once-per-name dump dedup")] static readonly HashSet<string> fxDumped = new HashSet<string>();
        static void DumpFxIndices(object donorSkel, ModelEntry e, string bodyName, object animMgr)
        {
            try
            {
                if (e == null || donorSkel == null || string.IsNullOrEmpty(bodyName)) return;
                if (ReferenceEquals(donorSkel, e.skeleton) || !fxDumped.Add(e.resourceName)) return;   // repeat Load: addon already ours
                object dIdx = null, oIdx = null;
                var mi = AccessTools.Method(donorSkel.GetType(), "GetFxMeshIndex", new[] { typeof(string) });
                if (mi != null) dIdx = mi.Invoke(donorSkel, new object[] { bodyName });
                if (e.skeleton != null)
                {
                    var mi2 = AccessTools.Method(e.skeleton.GetType(), "GetFxMeshIndex", new[] { typeof(string) });
                    if (mi2 != null) oIdx = mi2.Invoke(e.skeleton, new object[] { bodyName });
                }
                // DECODED ENCODING (Amplitude.Graphics.GetEncodedMeshAndVisualParticleCount):
                //   encoded = ContentLayer.HxFxOneMeshComputeBufferData[fxMeshIndex].StartIndex | (particleCount << 24)
                // The low 24 bits are the mesh's START INDEX in the layer buffer — per-mesh unique. The donor mesh's
                // StartIndex is therefore the NEEDLE that identifies any descriptor fragment still drawing the donor.
                uint dStart = 0, oStart = 0;
                try { if (dIdx != null) dStart = ReadFxStart(animMgr, Convert.ToUInt32(dIdx)); } catch { }
                try { if (oIdx != null) oStart = ReadFxStart(animMgr, Convert.ToUInt32(oIdx)); } catch { }
                Plugin.Diag($"[Uni][FX] '{e.resourceName}': FxMeshIndex for '{bodyName}' — donor={dIdx} (start=0x{dStart:X6}) ours={oIdx} (start=0x{oStart:X6})");
                if (e.hideSubPawns)
                {
                    ghostNeedle = dStart; ghostOurStart = oStart; ghostEntryName = e.resourceName;
                    // PER-LAYER NEEDLES (the transparent-layer test): the blur disc is TRANSLUCENT — its geometry
                    // plausibly lives in a TRANSPARENT ContentLayer whose StartIndex for the same mesh index differs
                    // from layer 2's. Collect the donor mesh's start in EVERY layer and scan against all of them.
                    ghostNeedles.Clear();
                    if (dStart != 0) ghostNeedles.Add(dStart);
                    try
                    {
                        if (dIdx != null)
                            foreach (var t in ReadFxStartsAllLayers(animMgr, Convert.ToUInt32(dIdx)))
                            {
                                Plugin.Diag($"[Uni][FX]   donor mesh {dIdx} in layer {t.Item1}: start=0x{t.Item2:X6} prim={t.Item3}");
                                if (t.Item2 != 0 && t.Item2 != oStart) ghostNeedles.Add(t.Item2);
                            }
                    }
                    catch (Exception lex) { Plugin.Log.LogWarning("[Uni] per-layer needles: " + lex.Message); }
                    DumpDescriptorTable();
                    ScanGhostDescriptors();
                    DumpFxMeshTable(animMgr);   // name every mesh in the layer — the rotor/blur mesh will be findable BY NAME
                    if (dIdx != null) { ghostDonorFxIdx = Convert.ToUInt32(dIdx); CrushGhostSlice(animMgr, e, ghostDonorFxIdx); }   // the geometry kill
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[Uni] FX index dump: " + ex.Message); }
        }

        // The donor mesh's StartIndex in EVERY ContentLayer (layer, start, prim) — the transparent-layer needles.
        static List<Tuple<int, uint, uint>> ReadFxStartsAllLayers(object animMgr, uint fxMeshIdx)
        {
            var outp = new List<Tuple<int, uint, uint>>();
            var mcm = GetMember(animMgr, "FxComponentMeshContentManager");
            var layers = GetMember(mcm, "Layers") as Array ?? AccessTools.Field(mcm.GetType(), "layers")?.GetValue(mcm) as Array;
            if (layers == null) return outp;
            for (int li = 0; li < layers.Length; li++)
            {
                try
                {
                    var buf = GetMember(layers.GetValue(li), "HxFxOneMeshComputeBufferData") as Array;
                    if (buf == null || fxMeshIdx >= buf.Length) continue;
                    var be = buf.GetValue((int)fxMeshIdx);
                    uint st = Convert.ToUInt32(GetMember(be, "StartIndex"));
                    uint pr = Convert.ToUInt32(GetMember(be, "PrimitiveCount"));
                    if (st != 0 || pr != 0) outp.Add(Tuple.Create(li, st, pr));
                }
                catch { }
            }
            return outp;
        }
        [ProcessLived("per-inject scratch")] internal static readonly HashSet<uint> ghostNeedles = new HashSet<uint>();

        // StartIndex of a mesh in the unit ContentLayer buffer (the low-24-bit half of the fragment encoding).
        static uint ReadFxStart(object animMgr, uint fxMeshIdx)
        {
            var mcm = GetMember(animMgr, "FxComponentMeshContentManager");
            var layerObj = GetMember(animMgr, "FXMeshLayerIndex");
            int layer = layerObj is int li ? li : Convert.ToInt32(layerObj ?? 0);
            var layers = GetMember(mcm, "Layers") as Array ?? AccessTools.Field(mcm.GetType(), "layers")?.GetValue(mcm) as Array;
            var buf = GetMember(layers.GetValue(layer), "HxFxOneMeshComputeBufferData") as Array;
            return Convert.ToUInt32(GetMember(buf.GetValue((int)fxMeshIdx), "StartIndex"));
        }

        // FX MESH TABLE (ghost hunt): name every mesh in the unit ContentLayer. FxMesh assets are Unity objects with
        // names + Guids; the layer maps index -> guid (FindGuidAssociatedToIndex). Join the two and print index, name,
        // StartIndex, PrimitiveCount for the whole table — the donor's separate rotor/blur mesh (its OWN FxMesh, its own
        // translucent output layer; the reason the skinned-mesh needle found nothing) becomes identifiable BY NAME.
        static bool fxMeshTableDumped;
        internal static void ResetFxMeshTableDump() => fxMeshTableDumped = false;
        internal static object ghostAnimMgr;   // stashed at repoint so the late trigger can re-dump
        internal static void DumpFxMeshTable(object animMgr)
        {
            if (fxMeshTableDumped || animMgr == null) return;
            fxMeshTableDumped = true;
            ghostAnimMgr = animMgr;
            try
            {
                // guid -> name via Amplitude's own AssetDatabase.LoadAsset<FxMesh>(guid) — the proven districts path
                // (Resources.FindObjectsOfTypeAll returned nothing: the FxMesh assets aren't loose Unity objects).
                var fxMeshType = GameBinding.FxMesh;
                var adb = GameBinding.AssetDatabase;
                var loadG = adb?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => (m.Name == "LoadAsset" || m.Name == "TryLoadAsset") && m.IsGenericMethodDefinition && m.GetParameters().Length >= 1);
                var loadFx = fxMeshType != null && loadG != null ? loadG.MakeGenericMethod(fxMeshType) : null;
                var mcm = GetMember(animMgr, "FxComponentMeshContentManager");
                var layerObj = GetMember(animMgr, "FXMeshLayerIndex");
                int layer = layerObj is int li ? li : Convert.ToInt32(layerObj ?? 0);
                var layers = GetMember(mcm, "Layers") as Array ?? AccessTools.Field(mcm.GetType(), "layers")?.GetValue(mcm) as Array;
                var lay = layers.GetValue(layer);
                var buf = GetMember(lay, "HxFxOneMeshComputeBufferData") as Array;
                var find = AccessTools.Method(lay.GetType(), "FindGuidAssociatedToIndex");
                var sb = new System.Text.StringBuilder($"[Uni][FXMESHES] layer {layer}: {buf?.Length ?? 0} slots (loader={(loadFx != null ? "OK" : "MISSING")})\n");
                for (int i = 0; buf != null && i < buf.Length && i < 300; i++)
                {
                    var slotv = buf.GetValue(i);
                    uint start = MemberUInt(slotv, "StartIndex", 0), prim = MemberUInt(slotv, "PrimitiveCount", 0);
                    if (start == 0 && prim == 0) continue;   // empty slot
                    string nm = "?";
                    if (find != null)
                    {
                        var args = new object[] { i, null };
                        try
                        {
                            if ((bool)find.Invoke(lay, args) && args[1] != null)
                            {
                                if (loadFx != null)
                                {
                                    var fx = loadFx.Invoke(null, loadFx.GetParameters().Length == 1 ? new[] { args[1] } : new object[] { args[1], null });
                                    nm = (fx as UnityEngine.Object)?.name ?? args[1].ToString();
                                }
                                else nm = args[1].ToString();
                            }
                        }
                        catch { }
                    }
                    sb.Append($"  mesh[{i}] start=0x{start:X6} prim={prim} name='{nm}'\n");
                }
                Plugin.Log.LogInfo(sb.ToString());
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[Uni] FXMESHES dump: " + ex.Message); }
        }

        // THE KILL: scan every GPU descriptor fragment for the donor mesh's StartIndex needle and zero the match in
        // place (same mechanism as the hideMeshes descriptor patch). Our own descriptor encodes OUR StartIndex, so it
        // can't match. Re-run periodically (from the NEAR tick) — the ghost's descriptor may register after repoint.
        static uint ghostNeedle, ghostOurStart; static string ghostEntryName;
        internal static void ScanGhostDescriptors()
        {
            if (ghostNeedle == 0) return;
            try
            {
                var pmType = GameBinding.PawnManager;
                var pm = pmType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                         ?? AccessTools.Field(pmType, "Instance")?.GetValue(null);
                var descs = AccessTools.Field(pmType, "gpuPawnDescriptorEntries")?.GetValue(pm) as Array;
                var gfrags = AccessTools.Field(pmType, "gpuPawnDescriptorFragmentEntries")?.GetValue(pm) as Array;
                var dirtyF = AccessTools.Field(pmType, "descriptorBufferDirty");
                if (descs == null || gfrags == null) return;
                var dT = descs.GetType().GetElementType();
                var sfF = dT.GetField("StartFragment"); var fcF = dT.GetField("FragmentCount");
                var encF = gfrags.GetType().GetElementType().GetField("EncodedMeshAndVisualParticleCountFxMeshIndex");
                bool dirty = false;
                for (int d = 0; d < descs.Length; d++)
                {
                    var de = descs.GetValue(d);
                    uint fc = Convert.ToUInt32(fcF.GetValue(de));
                    if (fc == 0) continue;
                    uint sf = Convert.ToUInt32(sfF.GetValue(de));
                    for (uint fi = 0; fi < fc && sf + fi < gfrags.Length; fi++)
                    {
                        var ge = gfrags.GetValue((int)(sf + fi));
                        uint enc = Convert.ToUInt32(encF.GetValue(ge));
                        if (enc == 0) continue;
                        uint lo = enc & 0xFFFFFF;
                        if (lo != ghostNeedle && !ghostNeedles.Contains(lo)) continue;
                        Plugin.Diag($"[Uni][GHOST] descriptor[{d}] frag[{fi}] encodes the DONOR mesh (0x{enc:X8}, needle start=0x{ghostNeedle:X6}, '{ghostEntryName}') — ZEROED");
                        encF.SetValue(ge, 0u);
                        gfrags.SetValue(ge, (int)(sf + fi));
                        dirty = true;
                    }
                }
                if (dirty) dirtyF?.SetValue(pm, true);
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[Uni] ghost scan: " + ex.Message); }
        }

        // One-shot: every GPU descriptor that draws fragments, with raw fragment encodings. Cross-referenced against the
        // [FX] known pairs this identifies WHICH descriptor still encodes the donor gunship's FxMesh = the ghost's body.
        static bool descTableDumped;
        internal static void ResetDescTableDump() => descTableDumped = false;
        internal static void DumpDescriptorTable()
        {
            if (descTableDumped) return;
            descTableDumped = true;
            try
            {
                var pmType = GameBinding.PawnManager;
                var pm = pmType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                         ?? AccessTools.Field(pmType, "Instance")?.GetValue(null);
                var descs = AccessTools.Field(pmType, "gpuPawnDescriptorEntries")?.GetValue(pm) as Array;
                var gfrags = AccessTools.Field(pmType, "gpuPawnDescriptorFragmentEntries")?.GetValue(pm) as Array;
                if (descs == null || gfrags == null) return;
                var dT = descs.GetType().GetElementType();
                var sfF = dT.GetField("StartFragment"); var fcF = dT.GetField("FragmentCount");
                var encF = gfrags.GetType().GetElementType().GetField("EncodedMeshAndVisualParticleCountFxMeshIndex");
                var sb = new System.Text.StringBuilder($"[Uni][FXTABLE] descriptors with fragments (of {descs.Length}):\n");
                int rows = 0;
                for (int d = 0; d < descs.Length && rows < 500; d++)
                {
                    var de = descs.GetValue(d);
                    uint fc = Convert.ToUInt32(fcF.GetValue(de));
                    if (fc == 0) continue;
                    uint sf = Convert.ToUInt32(sfF.GetValue(de));
                    sb.Append($"  desc[{d}] start={sf} n={fc}:");
                    for (uint fi = 0; fi < fc && fi < 6 && sf + fi < gfrags.Length; fi++)
                    { sb.Append($" 0x{Convert.ToUInt32(encF.GetValue(gfrags.GetValue((int)(sf + fi)))):X8}"); rows++; }
                    sb.Append('\n');
                }
                Plugin.Log.LogInfo(sb.ToString());
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[Uni] FXTABLE dump: " + ex.Message); }
        }

        // The donor definition's SubPawnDefinitions: log what's attached (once per entry), and clear the array when the
        // entry sets hideSubPawns. The definition asset is per-unit-type, so clearing only affects OUR overridden unit;
        // clearing is idempotent (an emptied array stays empty across re-Loads).
        [ProcessLived("diagnostic once-per-name log dedup")] static readonly HashSet<string> subPawnsLogged = new HashSet<string>();
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

        // Donor-clip diagnostic: every bone's rest frames — Local + BindPose TRS (T + R quaternion) — plain LogInfo.
        [ProcessLived("diagnostic once-per-name dump dedup")] static readonly HashSet<string> restDumped = new HashSet<string>();
        static void DumpBoneRests(object skel, string label)
        {
            try
            {
                var bones = skel == null ? null : GetMember(skel, "BoneInfos") as Array;
                if (bones == null) { Plugin.Log.LogInfo($"[Rest] {label}: no BoneInfos"); return; }
                Plugin.Log.LogInfo($"[Rest] {label}: {bones.Length} bone(s)");
                for (int i = 0; i < bones.Length && i < 12; i++)
                {
                    var bi = bones.GetValue(i);
                    var nm = GetMember(bi, "Name");
                    string TR(string field)
                    {
                        var trs = GetMember(bi, field);
                        if (trs == null) return "-";
                        var t = GetMember(trs, "Translation"); var r = GetMember(trs, "Rotation");
                        string rq = "-";
                        try { rq = $"({Convert.ToSingle(GetMember(r, "x")):0.####},{Convert.ToSingle(GetMember(r, "y")):0.####},{Convert.ToSingle(GetMember(r, "z")):0.####},{Convert.ToSingle(GetMember(r, "w")):0.####})"; } catch { }
                        return $"T={t} R={rq}";
                    }
                    Plugin.Log.LogInfo($"[Rest] {label}[{i}] '{nm}' Local: {TR("Local")} | Bind: {TR("BindPose")}");
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[Rest] " + label + ": " + ex.Message); }
        }

        // PLAN-A STEP 2 (2026-08-04): rebase our skeleton to an IDENTITY root, donor-convention.
        // Blender's glTF export leaves the armature OBJECT as bone 0 carrying the Z-up->Y-up -90X conversion;
        // donor rigs are Y-up native (bone 0 identity). The donor clip has NO track on channel 0, so that -90X
        // rest SURVIVES playback and CONJUGATES every animated child rotation — the donor's vertical rotor yaw
        // lands as a ROLL ("the rotor is now rolling"). Fix: clear bone 0's Local rotation and fold it into every
        // bone's BindPose rotation (BindPose' = BindPose x R0; matrix algebra leaves the Bind TRANSLATION
        // unchanged, confirmed by the [Rest] dump where Bind.T is already R0-free). Static render stays
        // pixel-identical (W'.Bind' == W.Bind == I at rest); animated deviations land in donor space.
        // Idempotent: skips when bone 0 is already identity, so re-injection is safe.
        // v3 (2026-08-04): GENERAL identity-rest rebase — EVERY bone's rest rotation folds away, not just bone 0's.
        // v2 cleared only the root (glTF -90X) and worked until the leveled rebake put the Factory's FACING rotation
        // on b000_Root's rest (R=-90 about game-Z): any rest rotation anywhere in the chain conjugates the animated
        // deviations below it (that run: vertical rotor loop off the mast). Donor convention is ALL rests identity
        // with world-space translations (verified in the [Rest] dump: donor Bind.T == -worldPos, R == identity), so
        // rebuild ours the same way: world rest POSITIONS preserved exactly, every Local/Bind rotation -> identity,
        // Local.T -> world offset from parent, Bind.T -> -worldPos (x bind scale). Static render is unchanged
        // (W'.Bind' == I at rest); animated deviations land in donor space regardless of bake-time orientation.
        // Idempotent: skips when every rest is already identity.
        static void RebaseRootIdentity(object skel, string label)
        {
            try
            {
                var bones = skel == null ? null : GetMember(skel, "BoneInfos") as Array;
                if (bones == null || bones.Length == 0) return;
                int n = bones.Length;
                float[] Q(object o) => new[] { Convert.ToSingle(GetMember(o, "x")), Convert.ToSingle(GetMember(o, "y")),
                                               Convert.ToSingle(GetMember(o, "z")), Convert.ToSingle(GetMember(o, "w")) };
                float[] V(object o) => new[] { Convert.ToSingle(GetMember(o, "x")), Convert.ToSingle(GetMember(o, "y")),
                                               Convert.ToSingle(GetMember(o, "z")) };
                float[] QMul(float[] a, float[] b) => new[] {
                    a[3] * b[0] + a[0] * b[3] + a[1] * b[2] - a[2] * b[1],
                    a[3] * b[1] - a[0] * b[2] + a[1] * b[3] + a[2] * b[0],
                    a[3] * b[2] + a[0] * b[1] - a[1] * b[0] + a[2] * b[3],
                    a[3] * b[3] - a[0] * b[0] - a[1] * b[1] - a[2] * b[2] };
                float[] QRot(float[] q, float x, float y, float z)
                {   // v' = v + 2*q.xyz x (q.xyz x v + w*v)
                    float cx = q[1] * z - q[2] * y + q[3] * x, cy = q[2] * x - q[0] * z + q[3] * y, cz = q[0] * y - q[1] * x + q[3] * z;
                    return new[] { x + 2f * (q[1] * cz - q[2] * cy), y + 2f * (q[2] * cx - q[0] * cz), z + 2f * (q[0] * cy - q[1] * cx) };
                }
                var wq = new float[n][]; var wp = new float[n][]; var ws = new float[n];
                var par = new int[n]; var hasChild = new bool[n];
                for (int i = 0; i < n; i++)
                {
                    var bi = bones.GetValue(i);
                    par[i] = Convert.ToInt32(GetMember(bi, "ParentIndex"));
                    if (par[i] >= 0 && par[i] < n) hasChild[par[i]] = true;
                    var lc = GetMember(bi, "Local");
                    var r = Q(GetMember(lc, "Rotation")); var t = V(GetMember(lc, "Translation"));
                    float s = Convert.ToSingle(GetMember(lc, "Scale"));
                    int p = par[i];
                    if (p < 0 || p >= i) { wq[i] = r; wp[i] = t; ws[i] = s; }
                    else
                    {
                        wq[i] = QMul(wq[p], r);
                        var off = QRot(wq[p], t[0] * ws[p], t[1] * ws[p], t[2] * ws[p]);
                        wp[i] = new[] { wp[p][0] + off[0], wp[p][1] + off[1], wp[p][2] + off[2] };
                        ws[i] = ws[p] * s;
                    }
                }
                // v4: only ANCESTOR (has-children) rests need flattening — LEAF bones (the rotors) keep their
                // world rest orientation, so a bake-authored axle frame (tail-fan cant, mast tilt) survives and
                // the donor channel's fixed-axis spin gets CONJUGATED into the rotor's real plane. If every
                // ancestor is already identity, leaves are already world==local and there is nothing to do.
                bool any = false;
                for (int i = 0; i < n; i++)
                    if (hasChild[i])
                    {
                        var r = Q(GetMember(GetMember(bones.GetValue(i), "Local"), "Rotation"));
                        if (Math.Abs(r[0]) + Math.Abs(r[1]) + Math.Abs(r[2]) > 1e-4f) { any = true; break; }
                    }
                if (!any) return;
                for (int i = 0; i < n; i++)
                {
                    var bi = bones.GetValue(i);
                    int p = par[i];
                    float inv = (p >= 0 && p < i && Math.Abs(ws[p]) > 1e-8f) ? 1f / ws[p] : 1f;
                    float lx = p < 0 || p >= i ? wp[i][0] : (wp[i][0] - wp[p][0]) * inv;
                    float ly = p < 0 || p >= i ? wp[i][1] : (wp[i][1] - wp[p][1]) * inv;
                    float lz = p < 0 || p >= i ? wp[i][2] : (wp[i][2] - wp[p][2]) * inv;
                    var lc = GetMember(bi, "Local");
                    var lt = GetMember(lc, "Translation");
                    SetMember(lt, "x", lx); SetMember(lt, "y", ly); SetMember(lt, "z", lz);
                    SetMember(lc, "Translation", lt);
                    // leaf keeps world orientation (local == world once parents are identity); ancestors flatten
                    float[] nr = hasChild[i] ? new[] { 0f, 0f, 0f, 1f } : wq[i];
                    var lr = GetMember(lc, "Rotation");
                    SetMember(lr, "x", nr[0]); SetMember(lr, "y", nr[1]); SetMember(lr, "z", nr[2]); SetMember(lr, "w", nr[3]);
                    SetMember(lc, "Rotation", lr);
                    SetMember(bi, "Local", lc);
                    // Bind = W'^-1: rotation = conj(world rest R), translation = -(conj . worldPos) x bindScale
                    var bp = GetMember(bi, "BindPose");
                    float bs = Convert.ToSingle(GetMember(bp, "Scale"));
                    float[] cj = { -nr[0], -nr[1], -nr[2], nr[3] };
                    var bT = QRot(cj, wp[i][0], wp[i][1], wp[i][2]);
                    var bt = GetMember(bp, "Translation");
                    SetMember(bt, "x", -bT[0] * bs); SetMember(bt, "y", -bT[1] * bs); SetMember(bt, "z", -bT[2] * bs);
                    SetMember(bp, "Translation", bt);
                    var brr = GetMember(bp, "Rotation");
                    SetMember(brr, "x", cj[0]); SetMember(brr, "y", cj[1]); SetMember(brr, "z", cj[2]); SetMember(brr, "w", cj[3]);
                    SetMember(bp, "Rotation", brr);
                    SetMember(bi, "BindPose", bp);
                    bones.SetValue(bi, i);
                }
                SetMember(skel, "BoneInfos", bones);   // no-op if BoneInfos is the live array; covers a copy-returning property
                Plugin.Log.LogInfo($"[Rest] {label}: rests rebased ({n} bone(s), ancestors -> identity, leaf orientations + world positions preserved)");
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[Rest] rebase " + label + ": " + ex.Message); }
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
                // There used to be a second half here that mirrored the new name into a parallel `allMeshNames` string[]
                // on the skeleton. REMOVED 2026-08-23: `typeprobe --exact allMeshNames` says NO type in ANY game
                // assembly declares that field, and `Skeleton` (base `MeshCollection`) carries only `skinnedMeshInfos`.
                // So the probe missed every time — 36 HarmonyX warnings in one session — and the fallback branch it fell
                // into rebuilt the whole names array by reflection and then dropped it on the floor, because the
                // `amnField?.SetValue(...)` that was supposed to store it was null too. Work done, discarded, logged.
                // The rename that actually lands is the `skinnedMeshInfos[0].MeshName` write above.
                //
                // Worth recording HOW this was found: not by the binding catalog, which is exactly the guard meant to
                // catch a by-name read of a member the game does not have. `AccessTools.Field(x.GetType(), "name")` was
                // invisible to check-catalog.sh — 146 sites over 70 names — so the gate reported "all 331 catalogued"
                // while never looking. A log line found it. The gate has been widened; see that script's pass 1.
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
                            // GHOST ROTOR FIX (2026-08-03): the donor gunship's FxOutputLayer carries TWO RenderOutputs —
                            // the opaque body pass AND a transparent rotor-blur pass (donor mesh 74's 53-prim slice in
                            // ContentLayer 0). Our texture-isolation clone inherited both, so WE drew the ghost disc on
                            // every pawn. Prune the blur pass from the clone HERE — before fragment.Load registers the
                            // layer (registration copies the outputs; post-registration edits never reach the live pass).
                            // (SHADOW_PASS prune removed 2026-08-04: the ghost was the donor VFX sprite, not the shadow pass — the clone keeps both outputs)
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
                    int defId = MemberInt(addon, "PawnDefinitionId", -1);
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
                                uint ourBones = MemberUInt(skel, "BonesCount", 0);
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
                            // GPU-DESCRIPTOR DUMP (ghost hunt 2026-08-03): print the snapshot's raw fragment entries.
                            // The encoded field packs Mesh + VisualParticle count + FxMeshIndex — if any fragment still
                            // encodes the DONOR's FxMesh index (the gunship body+rotor mesh), the ghost renders straight
                            // from the descriptor snapshot regardless of everything pawn-side. Compare against ourMeshIndex.
                            try
                            {
                                uint ourMeshIdx = 0;
                                if (GetMember(skel, "skinnedMeshInfos") is Array smi && smi.Length > 0)
                                    try { ourMeshIdx = Convert.ToUInt32(GetMember(smi.GetValue(0), "MeshIndex")); } catch { }
                                Plugin.Diag($"[Uni][DESC] '{e?.resourceName}' descriptor[{defId}]: StartFragment={start} FragmentCount={count} BonesCount={dT.GetField("BonesCount")?.GetValue(dEntry)} ourMeshIndex={ourMeshIdx}");
                                for (uint fi = 0; fi < count && fi < 8; fi++)
                                {
                                    var ge = gfrags.GetValue((int)(start + fi));
                                    uint enc = (uint)encGpuF.GetValue(ge);
                                    Plugin.Diag($"[Uni][DESC]   frag[{fi}] encoded=0x{enc:X8} (lo16={enc & 0xFFFF} hi16={enc >> 16})");
                                }
                            }
                            catch (Exception dex) { Plugin.Log.LogWarning("[Uni] descriptor dump: " + dex.Message); }
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
            catch (Exception ex) { NoteInjectionError("fragments"); Plugin.Log.LogError("[Uni] ReloadFragments: " + ex); }
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
                    // free a prior clone first: a LOD/save-load/respawn rebuild can drop the prop fragment, so InjectHandProp
                    // re-runs and would orphan the previous handPropLayer (RearmModelRegistration only frees the CURRENT one).
                    if (e.handPropLayer is UnityEngine.Object oldHpl && oldHpl) UnityEngine.Object.Destroy(oldHpl);
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
                    int defId = MemberInt(addon, "PawnDefinitionId", -1);
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
