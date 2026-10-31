#!/usr/bin/env bash
# check-parity-stamp.sh — the push's memory of the last Blender-parity PASS (2026-10-31).
#
# WHY THIS EXISTS. Since 2026-10-30 (PR #222) the push runs no parity drill: the drills that hold the C# ports to
# Blender's own output run only in the parity mode (HAF_GATE=parity: tools/blender_parity.sh, the Bake Tests row).
# Nothing then connected the two: a ported script or its C# port could change and be pushed, and the gate stayed green
# with its parity claim untested, in silence (the 2026-10-31 review, P1 "nothing enforces parity"). This guard closes
# that gap WITHOUT Blender in the push: the parity mode, when it PASSES with every drill run, writes tools/parity-stamp.txt
# - a digest of the blob ids of every file the parity drills compile, run or read, as they are in the working tree - and
# the push compares that digest with the same files as they are now. Equal: the ports were held to Blender as they are.
# Not equal: the push SKIPS the claim and names the covered files that changed since, and the verdict treats a SKIP as a
# FAIL unless HAF_ALLOW_SKIP=1 says, meaning it, that the push goes without the parity claim.
#
# THE COVERED SET is derived from the drills, not hand-listed: every parity_step drill in tools/check.sh is read for
# the paths it references under editor/ and tools/ ($E/..., $ROOT/editor/..., $WROOT/tools/..., Tools~/...), plus every
# tracked file of each tools/*-drill folder it names (fixtures, dumps, jobs), plus the Blender scripts under editor/Tools~
# that a referenced script names, plus the drill script itself. Only TRACKED files count; each is hashed as git would
# store it (git hash-object, the clean filter applied), so a checkout with CRLF and one with LF agree (the lesson of
# PR #220). A referenced path under editor/ or tools/ that is NOT tracked fails the guard: either the drill is broken or
# this parser read a token wrong - both need a look. Anchors (rig_anim.py, vehicle_rig.py, BlenderRigAnim.cs,
# GlbReader.cs, hafkernels.dll) must be in the set, or the parser has decayed and the guard FAILS rather than guards nothing.
#
# What it cannot see, said: the inputs that live OUTSIDE this repository (the saved recipes, the registry's GLB sources
# and the fixtures' sample files in the Unity project) are not in the digest; a recipe change does not stale the stamp.
#
#   bash tools/check-parity-stamp.sh              the push's step: 0 current, 3 stale or missing (SKIP), 1 broken, 2 error
#   bash tools/check-parity-stamp.sh --write      the parity mode's PASS: writes tools/parity-stamp.txt (tools/check.sh)
#   bash tools/check-parity-stamp.sh --list       the covered files, one per line
#   bash tools/check-parity-stamp.sh --self-test  in a shared clone: current, three changed covered files, an uncovered one, no stamp
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)" || exit 2
cd "$ROOT" || exit 2
STAMP="tools/parity-stamp.txt"
MODE="${1:-check}"

# ---- the covered set --------------------------------------------------------------------------------------------------
drills() {  # the parity drills as tools/check.sh runs them: tools/<name>.sh
  grep -E '^parity_step run_(bg|lane) ' tools/check.sh | grep -oE '"\$ROOT/tools/[A-Za-z0-9_.-]+\.sh"' | tr -d '"' | sed 's#^\$ROOT/##' | sort -u
}
covered() {  # every file a parity drill compiles, runs or reads, one path per line, sorted (MISSING-DRILL rows for a drill that is gone)
  local d r f s n refs folders scripts tmp
  tmp="$(mktemp)" || return 2
  for d in $(drills); do
    [ -f "$d" ] || { echo "MISSING-DRILL $d" >> "$tmp"; continue; }
    echo "$d" >> "$tmp"
    # path tokens the drill text names under editor/ or tools/ (the drills spell them $E/, $ROOT/editor/, $WROOT/tools/, Tools~/)
    refs=$(grep -oE '(\$E|\$ROOT/editor|\$WROOT/editor|\$ROOT/tools|\$WROOT/tools|Tools~)/[A-Za-z0-9_~/.-]+' "$d" \
           | sed -e 's#^\$E/#editor/#' -e 's#^\$W\?ROOT/##' -e 's#^Tools~/#editor/Tools~/#' | sort -u)
    for r in $refs; do
      case "$r" in */) continue;; esac
      [ -d "$r" ] && continue   # a bare folder reference (a compiler's search directory): not a file
      echo "$r" >> "$tmp"
    done
    # every tracked file of each drill folder the script names (its fixtures, dumps and jobs)
    folders=$(printf '%s\n' $refs | grep -oE '^tools/[A-Za-z0-9_-]+-drill' | sort -u)
    for f in $folders; do git ls-files -- "$f" >> "$tmp"; done
    # the Blender scripts a referenced Tools~ script names by file name (rig_anim.py names rig_anim_glb.py)
    scripts=$(printf '%s\n' $refs | grep -E '^editor/Tools~/.*\.py$')
    for s in $scripts; do
      [ -f "$s" ] || continue
      for n in $(grep -oE '[A-Za-z0-9_]+\.py' "$s" | sort -u); do [ -f "editor/Tools~/$n" ] && echo "editor/Tools~/$n" >> "$tmp"; done
    done
  done
  sort -u "$tmp"; rm -f "$tmp"
}
untracked_refs() {  # <covered list file>: referenced paths that git does not track (parser or drill trouble); one git, not one per file
  local list="$1"
  grep '^MISSING-DRILL' "$list"
  comm -23 <(grep -v '^MISSING-DRILL' "$list" | sort -u) <(git ls-files -- $(grep -v '^MISSING-DRILL' "$list") | sort -u)
}
digest_of() {  # <covered list file> -> "digest files": the working tree's blob ids (git hash-object: filters applied, as git would store them)
  local list="$1" ids rows n
  ids="$(git hash-object --stdin-paths < "$list")" || return 2
  rows="$(paste -d ' ' <(printf '%s\n' "$ids") "$list")"
  n=$(grep -c . "$list")
  printf '%s\n' "$rows" | sha256sum | awk -v n="$n" '{ print $1, n }'
}
ANCHORS="editor/Tools~/rig_anim.py editor/Tools~/vehicle_rig.py editor/Tools~/deploy_convert.py editor/Tools~/prep_model.py
         editor/BlenderRigAnim.cs editor/BlenderVehicleRig.cs editor/BlenderDeploy.cs editor/GlbReader.cs editor/Plugins/x86_64/hafkernels.dll
         tools/riganim-drill/riganim_fixtures.py tools/vehiclerig-drill/vehiclerig_fixtures.py tools/blender_oracle.sh"

build_list() {  # -> LIST (a temp file of covered paths); FAILs on a decayed parser or an untracked reference
  LIST="$(mktemp)" || exit 2
  covered > "$LIST" || { echo "FAIL — cannot derive the covered set"; exit 1; }
  local bad; bad="$(untracked_refs "$LIST")"
  if [ -n "$bad" ]; then
    printf 'FAIL — a parity drill references a path git does not track (a broken drill, or this guard read a token wrong):\n'
    printf '  %s\n' $bad; exit 1
  fi
  local a missing=""
  for a in $ANCHORS; do grep -qxF "$a" "$LIST" || missing="$missing $a"; done
  if [ -n "$missing" ]; then
    printf 'FAIL — the covered set lost an anchor the parity drills are known to compile or run; the parser of this guard has decayed:%s\n' "$missing"; exit 1
  fi
  local n; n=$(grep -c . "$LIST")
  [ "$n" -ge 100 ] || { echo "FAIL — only $n covered files derived; the parity drills compile far more (the parser of this guard has decayed)"; exit 1; }
}
stamp_field() { awk -v k="$1" '$1 == k { sub(/\r$/, "", $2); print $2 }' "$STAMP"; }   # the stamp may check out with CRLF
changed_since() {  # the covered files that differ from the stamp's commit, when that commit is in this clone
  local at; at="$(stamp_field commit | sed 's/+dirty$//')"
  [ -n "$at" ] && git cat-file -e "$at^{commit}" 2>/dev/null || return 0
  git diff --name-only "$at" -- $(cat "$LIST") 2>/dev/null | sort -u
}
remedy() { printf 'Run `bash tools/blender_parity.sh` (or the Bake Tests row "Do the C# ports still match Blender?"), commit the stamp it writes, and push again; or, meaning it: HAF_ALLOW_SKIP=1 git push\n'; }

case "$MODE" in
  --list) build_list; cat "$LIST"; rm -f "$LIST"; exit 0;;
  --write)
    build_list
    read -r dg n < <(digest_of "$LIST") || exit 2
    commit="$(git rev-parse HEAD 2>/dev/null || echo unknown)"
    git diff --quiet HEAD -- $(cat "$LIST") 2>/dev/null || commit="$commit+dirty"
    {
      printf '# tools/parity-stamp.txt - written by tools/check.sh after a PASS of the parity mode (HAF_GATE=parity:\n'
      printf '# tools/blender_parity.sh or the Bake Tests row), not by hand. The digest is over the blob ids of every file\n'
      printf '# the parity drills compile, run or read (tools/check-parity-stamp.sh --list); the push compares it.\n'
      printf 'digest %s\nfiles %s\ncommit %s\ndate %s\noracle %s\n' "$dg" "$n" "$commit" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "${HAF_ORACLE:-live}"
    } > "$STAMP" || { echo "FAIL — cannot write $STAMP"; rm -f "$LIST"; exit 2; }
    printf 'parity stamp written: %s covered files, digest %s, at %s (commit the stamp with the change it covers)\n' "$n" "${dg:0:12}" "$commit"
    rm -f "$LIST"; exit 0;;
  --self-test)
    TMPD="$(mktemp -d)" || exit 2
    trap 'rm -rf "$TMPD"' EXIT
    git clone --quiet --shared --no-hardlinks "$ROOT" "$TMPD/repo" || { echo "FAIL — cannot clone for the self-test"; exit 2; }
    # the clone carries HEAD; the guard and the gate under test are the working tree's
    cp tools/check-parity-stamp.sh tools/check.sh "$TMPD/repo/tools/" || exit 2
    cd "$TMPD/repo" || exit 2
    git config user.email parity@self.test; git config user.name parity-self-test
    rc_of() { bash tools/check-parity-stamp.sh > "$TMPD/out" 2>&1; echo $?; }
    expect() {  # <case> <want rc> <got rc> [<needle in output>]
      local case="$1" want="$2" got="$3" needle="${4:-}"
      if [ "$got" != "$want" ] || { [ -n "$needle" ] && ! grep -qF -- "$needle" "$TMPD/out"; }; then
        printf 'FAIL — self-test %s: wanted rc %s%s, got rc %s:\n' "$case" "$want" "${needle:+ and \"$needle\"}" "$got"; sed 's/^/    /' "$TMPD/out"; exit 1
      fi
      printf 'PASS self-test %s (rc %s)\n' "$case" "$got"
    }
    bash tools/check-parity-stamp.sh --write > "$TMPD/out" 2>&1 || { echo "FAIL — self-test cannot write the stamp:"; cat "$TMPD/out"; exit 1; }
    git -c core.safecrlf=false add -A tools && git commit -q -m "stamp" || exit 2
    expect "a fresh stamp is current" 0 "$(rc_of)" "parity stamp current"
    printf '\n# self-test touch\n' >> editor/Tools~/rig_anim.py
    expect "a changed Blender script is named" 3 "$(rc_of)" "editor/Tools~/rig_anim.py"
    git checkout -q HEAD -- editor/Tools~/rig_anim.py
    printf '\n// self-test touch\n' >> editor/BlenderVehicleRig.Stage5.cs
    expect "a changed C# port is named" 3 "$(rc_of)" "editor/BlenderVehicleRig.Stage5.cs"
    git checkout -q HEAD -- editor/BlenderVehicleRig.Stage5.cs
    printf '\n# self-test touch\n' >> tools/vehiclerig-drill/vehiclerig_fixtures.py
    expect "a changed fixture is named" 3 "$(rc_of)" "tools/vehiclerig-drill/vehiclerig_fixtures.py"
    git checkout -q HEAD -- tools/vehiclerig-drill/vehiclerig_fixtures.py
    printf '\nself-test touch\n' >> README.md
    expect "an uncovered file leaves the stamp current" 0 "$(rc_of)" "parity stamp current"
    git checkout -q HEAD -- README.md
    rm -f tools/parity-stamp.txt
    expect "no stamp is a SKIP" 3 "$(rc_of)" "no parity stamp"
    echo "PASS — parity stamp self-test: current, three changed covered files named, an uncovered file ignored, no stamp refused"
    exit 0;;
  check) ;;
  *) echo "FAIL — unknown mode '$MODE' (check | --write | --list | --self-test)"; exit 2;;
esac

# ---- the push's step ----------------------------------------------------------------------------------------------------
build_list
read -r dg n < <(digest_of "$LIST") || exit 2
if [ ! -f "$STAMP" ]; then
  printf 'SKIP — no parity stamp (%s): the %s files the parity drills cover have never been held to Blender by a parity run that wrote one.\n' "$STAMP" "$n"
  remedy; rm -f "$LIST"; exit 3
fi
if [ "$(stamp_field digest)" = "$dg" ]; then
  printf 'PASS — parity stamp current: the %s files the parity drills compile, run or read are as the last parity PASS held them to Blender (%s, %s)\n' \
    "$n" "$(stamp_field date)" "$(stamp_field commit | cut -c1-12)"
  rm -f "$LIST"; exit 0
fi
changed="$(changed_since)"
printf 'SKIP — parity stamp STALE: a file the parity drills compile, run or read changed since the last parity PASS (%s, %s), so the claim that the C# ports still match Blender is untested for this push.\n' \
  "$(stamp_field date)" "$(stamp_field commit | cut -c1-12)"
if [ -n "$changed" ]; then printf 'Changed since that commit, among the covered files:\n'; printf '  %s\n' $changed
else printf '(the stamp'"'"'s commit is not in this clone, so the changed files cannot be listed; `bash tools/check-parity-stamp.sh --list` prints the covered set)\n'; fi
remedy; rm -f "$LIST"; exit 3
