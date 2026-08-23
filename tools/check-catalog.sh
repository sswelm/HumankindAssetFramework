#!/usr/bin/env bash
# check-catalog.sh — PROVE the binding catalog covers the binding SURFACE.
#
# `bindcheck` validates every binding IN the catalog against the game DLLs, so its "125/125 clean" is a statement about
# the catalog, not about the code. A review on 2026-08-21 measured the gap: ~80 member names were read BY NAME at
# reflection call sites and were NOT catalogued — `FacingAngleOffset` (battle aim), `IdleAudioEvent`,
# `CurrentTechnologicalEraIndex`, `BonesCount` among them, several behind silent catches, where a game rename degrades
# a feature in silence and no guard says a word. The CHANGELOG had claimed the catalog covered "every non-diagnostic
# by-name site"; that was a HAND sweep, so it drifted the moment the next site was written.
#
# This closes the loop mechanically: extract every string literal passed to a by-name reflection accessor in the
# runtime sources, subtract the catalog, subtract an explicit ALLOWLIST (every entry justified), FAIL on anything left.
# Run in the same gate as bindcheck — together they mean "the catalog COVERS the surface AND RESOLVES against the game".
#
#   bash tools/check-catalog.sh [--list]     # --list: print every extracted literal with its sites, then exit 0
set -uo pipefail
cd "$(dirname "$0")/.." || exit 2

SRC=$(ls Patches/*.cs Plugin.cs Prober.cs 2>/dev/null | grep -v "Patches/GameBinding.cs")
CATALOG=Patches/GameBinding.cs
[ -f "$CATALOG" ] || { echo "[FAIL] $CATALOG not found"; exit 2; }

# ---- ALLOWLIST — a by-name literal that CANNOT be catalogued, each with the reason ----
# `NAME`            : never a game member anywhere (Unity/BCL/shader names).
# `NAME@File.cs`    : a game-shaped name that at THIS file's site(s) is a tolerant probe — the code already tries
#                     several names and copes with all of them missing, so a rename cannot silently break it.
# Adding a line here is a promise. Prefer cataloguing.
ALLOW=$(cat <<'EOF'
x	Unity Vector3/Quaternion component
y	Unity Vector3/Quaternion component
z	Unity Vector3/Quaternion component
w	Unity Quaternion component
name	UnityEngine.Object.name — Unity API, versioned with Unity, not the game
Count	BCL collection member
Item	BCL indexer
get_Item	BCL indexer
min	UnityEngine.Bounds.min
max	UnityEngine.Bounds.max
LoadImage	UnityEngine.ImageConversion
GetTexture	UnityEngine.Material
SetTexture	UnityEngine.Material
mainTexture	UnityEngine.Material
_MainTex	shader property NAME, not a managed member — a shader rename shows up as the smoke test's texture health
Add	BCL collection method
Clear	BCL collection method
TryDequeue	BCL ConcurrentQueue drain (SessionState.ClearerFor)
TryTake	BCL ConcurrentBag drain (SessionState.ClearerFor)
ToString	BCL
Length	BCL array member
Value@Patches/DistrictInject.cs	tolerant probe: the code tries Guid ?? Value ?? guid on an unknown pair type and copes with all three absent
guid@Patches/DistrictInject.cs	tolerant probe: same chain as Value above
# ---- the GF( family, made visible 2026-08-22 when this gate learned to see it (it had been blind to 16 sites) ----
# GFA( joined it 2026-08-23 (the memoized AccessTools probe). It was added and the gate still said OK — because a
# shape it cannot see is a shape it stops counting, not one it reports. Drilled both ways: a bogus name inside a
# GFA( call passes the un-taught gate and fails the taught one. ANY new accessor helper must be added here.

# GF(type, "name") is the district axis's tolerant field probe over a type resolved AT RUNTIME (mat.GetType(),
# voBox.GetType(), a clone's type). The catalog binds member-to-DECLARING-TYPE, and these sites genuinely do not
# know the type statically — so they are allowlisted with the reason rather than faked into the catalog. Every one
# is site-scoped: the same name read anywhere else still has to be catalogued.
# DIAGNOSTIC DUMPS (DistrictDebug-gated, tolerant, never on a render path — a rename degrades a debug line):
visualOutput@Patches/DistrictInject.cs	DumpMatTree diagnostic (DistrictDebug-gated)
layerEntryCount@Patches/DistrictInject.cs	DumpMatTree diagnostic
levelBuildDecalRenderDataEntryIndex@Patches/DistrictInject.cs	DumpMatTree diagnostic
loadedStatus@Patches/DistrictInject.cs	DumpMatTree diagnostic
lodData@Patches/DistrictInject.cs	DumpMatTree diagnostic
meshIndexLod0@Patches/DistrictInject.cs	DumpMatTree diagnostic
meshIndexLod1@Patches/DistrictInject.cs	DumpMatTree diagnostic
useCustomBBox@Patches/DistrictInject.cs	DumpMatTree diagnostic
decalMesh@Patches/DistrictInject.cs	DumpDecalDescriptor diagnostic (DistrictDebug-gated)
elements@Patches/DistrictInject.Scoped.cs	DumpGroundMatchers diagnostic (DistrictDebug-gated)
levelBuildMatchElements@Patches/DistrictInject.Scoped.cs	DumpGroundMatchers diagnostic
Exploitation@Patches/DistrictInject.Scoped.cs	DumpGroundMatchers diagnostic
District@Patches/DistrictInject.Scoped.cs	DumpGroundMatchers diagnostic
emitter@Patches/DistrictInject.Scoped.cs	DumpGroundMatchers diagnostic
# FUNCTIONAL, on a runtime-resolved type. These DO carry silent-degradation risk if the game renames them — the
# `?.` chains simply stop applying. Promoting them to derived catalog bindings (the A6 CachedDerived mechanism,
# anchored on the type that produced the instance) is in docs/Review-Backlog.md; until then the risk is named here
# rather than hidden by a gate that could not see the site at all.
mesh@Patches/DistrictInject.cs	tolerant alternate spelling: every site reads GF(t,"fxMesh") ?? GF(t,"mesh") — fxMesh IS catalogued, this is the fallback
fadeInOutMode@Patches/DistrictInject.cs	functional read on a runtime-resolved material type (clone path); no static declaring type to bind
fadeInOutMode@Patches/DistrictInject.Scoped.cs	same read on the scoped path's recursive material walk (mat.GetType(), depth-limited)
bbox@Patches/DistrictInject.cs	functional read on a runtime-resolved clone type; also read by DumpMatTree in the same file
loadedOutputLayer@Patches/DistrictInject.Scoped.cs	footprint injection writes the cloned output layer through voBox.GetType() — type known only at runtime
loadedOutputLayerGUID@Patches/DistrictInject.Scoped.cs	written beside loadedOutputLayer above, same runtime-resolved box
# ---- surfaced 2026-08-22 by the NESTED-call extraction pass (outer literals were invisible before) ----
# The footprint/mask injection block (Scoped.cs ~1006-1067) clones a visual-output box and its evolver material,
# every type resolved from the instance the game handed back. Same reason as the GF( block above.
visualOutput@Patches/DistrictInject.Scoped.cs	footprint injection, runtime-resolved host/output-layer box
maskedByTerrain@Patches/DistrictInject.Scoped.cs	footprint mask, runtime-resolved decal type
maskTexture@Patches/DistrictInject.Scoped.cs	footprint mask, runtime-resolved decal type
layer0@Patches/DistrictInject.Scoped.cs	footprint mask, runtime-resolved output-layer clone
bboxOverride@Patches/DistrictInject.Scoped.cs	footprint sizing, runtime-resolved clone
defaultSize@Patches/DistrictInject.Scoped.cs	footprint sizing, runtime-resolved clone
loadedEvolverMaterialGuid@Patches/DistrictInject.Scoped.cs	evolver-material clone, runtime-resolved
LocalScale@Patches/DistrictInject.Scoped.cs	evolver-material clone, runtime-resolved
AxeY@Patches/DistrictInject.Scoped.cs	evolver-material clone, runtime-resolved
AxeZ@Patches/DistrictInject.Scoped.cs	evolver-material clone, runtime-resolved
StartSkeletonBoneEntry@Patches/UniversalInject.Pose.cs	read off an element of the engine's skeleton BUFFER (skelBuf.GetValue(id)); the element type is not resolved statically — see Review-Backlog for promoting it to a derived binding
Data@Patches/UniversalInject.ScaleEra.cs	generic collection walker — tries an indexer first, then Data, then gives up; no single game type owns it
definition@Patches/DistrictInject.Scoped.cs	tolerant probe: tries Definition ?? definition on a database matrix and copes with both absent
EOF
)

# ---- extract: string literals in a by-name reflection call, across the runtime sources ----
# A literal immediately followed by `+` is a CONCATENATED family ("Pose" + i, "BoneRotation" + i): the real member is
# <name>0, which is what the catalog holds, so check that instead of the bare prefix.
extract() {
  # PASS 2 — the NESTED shape, added 2026-08-22. `GetMember(GetMember(x, "Inner"), "Outer")` gave up only "Inner":
  # pass 1 stops at the first `)`, so every OUTER literal in a nested call was invisible. That was not theoretical —
  # `TagAsAbilities` (Combat.cs, read that way and only that way) was missing from the catalog AND from this gate.
  # Deliberately narrow: accessor( identifier( …no nested parens… ), "NAME" — it cannot wander into a neighbouring
  # call on the same line, which a general "allow any )" relaxation would.
  grep -onE "(GetMember|SetMember|GetMemberOrNull|CallMethod|CachedField|CachedProp|GFA|GF)\([A-Za-z_][A-Za-z0-9_.]*\([^()]*\), *\"[A-Za-z_][A-Za-z0-9_.]*\"" $SRC 2>/dev/null \
  | sed -E 's/^([^:]+):([0-9]+):.*"([A-Za-z_][A-Za-z0-9_.]*)"$/\3\t\1:\2/' \
  | awk -F'\t' '{n=split($1,p,"."); for(i=1;i<=n;i++) if (p[i] != "") print p[i] "\t" $2}'
  grep -onE "(GetMember|SetMember|GetMemberOrNull|CallMethod|CachedField|CachedProp|GFA|GF|FastMember\.(Getter|Setter)<[^>]*>|AccessTools\.(Field|Property|Method|PropertyGetter|PropertySetter|DeclaredField|DeclaredProperty|DeclaredMethod)|\.Get(Field|Property|Method|Event|Member)|Traverse\.Field|Traverse\.Property|Traverse\.Method)\([^)]*?\"[A-Za-z_][A-Za-z0-9_.]*\" *\+?" $SRC 2>/dev/null \
  | sed -E 's/^([^:]+):([0-9]+):.*"([A-Za-z_][A-Za-z0-9_.]*)"( *\+)?$/\3\4\t\1:\2/' \
  | sed -E 's/^([A-Za-z_][A-Za-z0-9_.]*) *\+\t/\10\t/' \
  | awk -F'\t' '{n=split($1,p,"."); for(i=1;i<=n;i++) if (p[i] != "") print p[i] "\t" $2}'
}

SITES=$(extract | sort -u)
[ -n "$SITES" ] || { echo "[FAIL] extracted 0 by-name literals — the extraction regex broke (were the accessors renamed?)"; exit 2; }

if [ "${1:-}" = "--list" ]; then
  echo "$SITES" | awk -F'\t' '{a[$1]=a[$1]" "$2} END{for(k in a) print k a[k]}' | sort
  exit 0
fi

# ---- TYPE RESOLUTION must go through GameBinding (2026-08-23) ----
# AccessTools.TypeByName memoises NOTHING. Measured on this box: 1,032 ns on a HIT, and 7.85 MILLISECONDS on a MISS —
# every single call, forever. GameBinding.Cached is 19 ns on the hit and re-resolves a miss in 13.7 µs. The damage is
# worst exactly where the code looks most careful: a `?? TypeByName(other)` fallback chain spends a FULL miss on each
# probe that is MEANT to fail, and a lookup that never resolves (a game rename) turns into a permanent per-call stall
# with no exception and no log line — SchematicVis() was re-probing two types on every call and would have paid
# 15.7 ms a call, every 10 frames, if either name ever broke.
# So: no raw TypeByName outside the catalog. It also keeps the promise GameBinding.cs makes in its own header — that
# it is the ONE place each game type NAME lives — which a call-site literal quietly breaks.
RAWTBN=$(grep -nE '(HarmonyLib\.)?AccessTools\.TypeByName *\(' $SRC 2>/dev/null | grep -vE '^\s*[^:]+:[0-9]+: *(//|\*|/\*)' | grep -vE ':[0-9]+:.*//.*AccessTools\.TypeByName' || true)
if [ -n "$RAWTBN" ]; then
  echo "[FAIL] raw AccessTools.TypeByName at $(printf '%s\n' "$RAWTBN" | grep -c .) call site(s) outside Patches/GameBinding.cs:"
  printf '%s\n' "$RAWTBN" | sed 's/^/  /'
  echo
  echo "A MISS costs 7.85 ms and repeats on every call — a renamed type becomes a silent per-frame stall, not a"
  echo "graceful degradation. Add an accessor to Patches/GameBinding.cs and call it, or for a name computed at"
  echo "runtime call GameBinding.Cached(name) directly (it takes fallbacks: Cached(primary, alt1, alt2))."
  exit 1
fi

CAT=$(grep -oE '"[A-Za-z_][A-Za-z0-9_.+`]*"' "$CATALOG" | tr -d '"' | awk -F'.' '{print $NF}' | sort -u)
GLOBAL_ALLOW=$(printf '%s\n' "$ALLOW" | cut -f1 | grep -v '@' | sort -u)
SITE_ALLOW=$(printf '%s\n' "$ALLOW" | cut -f1 | grep '@' | sort -u)

# drop site-allowlisted (name@file) occurrences, then names that are catalogued or globally allowlisted
FILTERED=$(echo "$SITES" | awk -F'\t' -v sa="$SITE_ALLOW" 'BEGIN{n=split(sa,rows,"\n"); for(i=1;i<=n;i++){split(rows[i],p,"@"); key[p[1]"|"p[2]]=1}}
                                                          { split($2,s,":"); if (!((($1)"|"(s[1])) in key)) print }')
MISSING=$(echo "$FILTERED" | awk -F'\t' '{a[$1]=a[$1]" "$2} END{for(k in a) print k "\t" a[k]}' | sort \
          | join -t$'\t' -v1 - <(printf '%s\n' "$CAT") \
          | join -t$'\t' -v1 - <(printf '%s\n' "$GLOBAL_ALLOW"))

n=$(printf '%s' "$MISSING" | grep -c . || true)
total=$(echo "$SITES" | cut -f1 | sort -u | wc -l)
if [ "$n" -eq 0 ]; then
  echo "catalog surface: OK — all $total by-name literal(s) at reflection sites are catalogued or allowlisted."
  exit 0
fi
echo "[FAIL] $n by-name member(s) read at a reflection site are NOT in the GameBinding catalog (of $total):"
printf '%s\n' "$MISSING" | sed 's/^/  /'
echo
echo "Each is a game rename that would degrade a feature SILENTLY. Add it to the catalog with its declaring type"
echo "(bindcheck then validates it against the DLLs — use 'typeprobe --exact <name>' to find the owner), or, if it is"
echo "not a bindable game member, allowlist it in this script WITH the reason."
exit 1
