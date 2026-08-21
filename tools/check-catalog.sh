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
ToString	BCL
Length	BCL array member
Value@Patches/DistrictInject.cs	tolerant probe: the code tries Guid ?? Value ?? guid on an unknown pair type and copes with all three absent
guid@Patches/DistrictInject.cs	tolerant probe: same chain as Value above
Data@Patches/UniversalInject.ScaleEra.cs	generic collection walker — tries an indexer first, then Data, then gives up; no single game type owns it
definition@Patches/DistrictInject.Scoped.cs	tolerant probe: tries Definition ?? definition on a database matrix and copes with both absent
EOF
)

# ---- extract: string literals in a by-name reflection call, across the runtime sources ----
# A literal immediately followed by `+` is a CONCATENATED family ("Pose" + i, "BoneRotation" + i): the real member is
# <name>0, which is what the catalog holds, so check that instead of the bare prefix.
extract() {
  grep -onE "(GetMember|SetMember|GetMemberOrNull|CallMethod|FastMember\.(Getter|Setter)<[^>]*>|AccessTools\.(Field|Property|Method|PropertyGetter|PropertySetter|DeclaredField|DeclaredProperty|DeclaredMethod)|\.Get(Field|Property|Method|Event|Member)|Traverse\.Field|Traverse\.Property|Traverse\.Method)\([^)]*?\"[A-Za-z_][A-Za-z0-9_.]*\" *\+?" $SRC 2>/dev/null \
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
