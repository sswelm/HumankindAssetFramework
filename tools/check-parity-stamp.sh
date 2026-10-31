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
# FAIL unless HAF_ALLOW_SKIP=1 says, meaning it, that the push goes without the parity claim. The parity mode takes the
# digest BEFORE its drills start and the write refuses a different one: a covered file edited during the run is not
# what the drills tested, and gets no stamp.
#
# THE COVERED SET is derived from the drills, not hand-listed, and followed transitively: every parity_step drill in
# tools/check.sh is read for the paths it names under editor/ and tools/ ($E/..., $ROOT/editor/..., $WROOT/tools/...,
# Tools~/...); every tools/<folder> a path names contributes all its tracked files (fixtures, dumps, jobs, the oracle's
# oracle.py beside tools/blender_oracle.sh); every Blender script under editor/Tools~ contributes the scripts it names by
# file name; and every .sh or .py so reached is read the same way, until nothing new appears. Only TRACKED files count;
# each is hashed as git would store it (git hash-object, the clean filter applied), so a checkout with CRLF and one with
# LF agree (the lesson of PR #220). A referenced path under editor/ or tools/ that is NOT tracked fails the guard: either
# the drill is broken or this parser read a token wrong - both need a look. Anchors (rig_anim.py, vehicle_rig.py,
# BlenderRigAnim.cs, GlbReader.cs, hafkernels.dll, oracle.py) must be in the set, or the parser has decayed and the guard
# FAILS rather than guards nothing.
#
# What it cannot see, said: the inputs that live OUTSIDE this repository (the saved recipes, the registry's GLB sources
# and the fixtures' sample files in the Unity project) are not in the digest; a recipe change does not stale the stamp.
#
# The pre-push hook exports GIT_DIR and GIT_WORK_TREE; inherited, they make every git command of the self-test's clone
# act on the AUTHOR'S repository (2026-10-31: a stray "stamp" commit, the repo's identity overwritten, a dirty checkout,
# found by the review of PR #228). They are dropped here, first thing, and the self-test proves the author's repository
# is as it found it.
#
#   bash tools/check-parity-stamp.sh                the push's step: 0 current, 3 stale or missing (SKIP), 1 broken, 2 error
#   bash tools/check-parity-stamp.sh --digest       "<digest> <files>" of the covered files as they are now
#   bash tools/check-parity-stamp.sh --write [<digest taken before the run>]   the parity mode's PASS (tools/check.sh)
#   bash tools/check-parity-stamp.sh --list         the covered files, one per line
#   bash tools/check-parity-stamp.sh --self-test    in a shared clone: current, changed covered files refused, an uncovered
#                                                   one ignored, no stamp refused, a change during the run refused
set -uo pipefail
unset GIT_DIR GIT_WORK_TREE GIT_INDEX_FILE GIT_PREFIX GIT_COMMON_DIR
ROOT="$(cd "$(dirname "$0")/.." && pwd)" || exit 2
cd "$ROOT" || exit 2
STAMP="tools/parity-stamp.txt"
MODE="${1:-check}"

# ---- the covered set --------------------------------------------------------------------------------------------------
drills() {  # the parity drills as tools/check.sh runs them: tools/<name>.sh
  grep -E '^parity_step run_(bg|lane) ' tools/check.sh | grep -oE '"\$ROOT/tools/[A-Za-z0-9_.-]+\.sh"' | tr -d '"' | sed 's#^\$ROOT/##' | sort -u
}
refs_of() {  # <a .sh or .py file>: the editor/ and tools/ paths its text names (the drills spell them $E/, $ROOT/editor/, $WROOT/tools/, Tools~/)
  grep -oE '(\$E|\$ROOT/editor|\$WROOT/editor|\$ROOT/tools|\$WROOT/tools|Tools~)/[A-Za-z0-9_~/.-]+' "$1" \
    | sed -e 's#^\$E/#editor/#' -e 's#^\$W\?ROOT/##' -e 's#^Tools~/#editor/Tools~/#' | sort -u
}
expand() {  # <a covered file> -> the files it brings in, one per line (one grep per file, one git for its folders: forks are slow here)
  local p="$1" r n folders=""
  case "$p" in *.sh|*.py) ;; *) return 0;; esac
  [ -f "$p" ] || return 0
  for r in $(refs_of "$p"); do
    case "$r" in */) continue;; esac
    [ -d "$r" ] || echo "$r"                          # a bare folder reference (a compiler's search directory) is not a file
    [[ "$r" =~ ^tools/[A-Za-z0-9_-]+ ]] && [ -d "${BASH_REMATCH[0]}" ] && folders="$folders ${BASH_REMATCH[0]}"
  done
  [ -z "$folders" ] || git ls-files -- $folders         # every tracked file of each tools/<folder> a path names
  case "$p" in editor/Tools~/*.py)                    # a Blender script names its helpers by file name (rig_anim.py: rig_anim_glb.py)
    for n in $(grep -oE '[A-Za-z0-9_]+\.py' "$p" | sort -u); do [ -f "editor/Tools~/$n" ] && echo "editor/Tools~/$n"; done;;
  esac
  return 0
}
covered() {  # every file a parity drill compiles, runs or reads, followed to a fixed point; MISSING-DRILL rows for a drill that is gone
  local all d p new; local -A scanned=()
  all="$(mktemp)" || return 2
  for d in $(drills); do [ -f "$d" ] && echo "$d" >> "$all" || echo "MISSING-DRILL $d" >> "$all"; done
  while :; do
    new=0
    for p in $(grep -v '^MISSING-DRILL' "$all" | sort -u); do
      [ -z "${scanned[$p]:-}" ] || continue
      scanned[$p]=1; expand "$p" >> "$all"; new=1
    done
    [ "$new" -eq 1 ] || break
  done
  sort -u "$all"; rm -f "$all"
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
         tools/riganim-drill/riganim_fixtures.py tools/vehiclerig-drill/vehiclerig_fixtures.py tools/blender_oracle.sh tools/blender-oracle/oracle.py"

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
changed_since() {  # the covered files that differ from the stamp's commit; "?" when that commit is not in this clone
  local at; at="$(stamp_field commit | sed 's/+dirty$//')"
  if ! { [ -n "$at" ] && git cat-file -e "$at^{commit}" 2>/dev/null; }; then echo "?"; return 0; fi
  git diff --name-only "$at" -- $(cat "$LIST") 2>/dev/null | sort -u
}
remedy() { printf 'Run `bash tools/blender_parity.sh` (or the Bake Tests row "Do the C# ports still match Blender?"), commit the stamp it writes, and push again; or, meaning it: HAF_ALLOW_SKIP=1 git push\n'; }

case "$MODE" in
  --list) build_list; cat "$LIST"; rm -f "$LIST"; exit 0;;
  --digest) build_list; digest_of "$LIST"; rc=$?; rm -f "$LIST"; exit $rc;;
  --write)
    build_list
    read -r dg n < <(digest_of "$LIST") || exit 2
    before="${2:-}"
    if [ -n "$before" ] && [ "${before%% *}" != "$dg" ]; then
      printf 'FAIL — a covered file changed DURING the parity run (digest %s before the drills, %s now): the drills tested the old text, so no stamp is written. Run the parity mode again on the files as they are:\n' "${before:0:12}" "${dg:0:12}"
      git diff --name-only -- $(cat "$LIST") | sed 's/^/  /'
      rm -f "$LIST"; exit 1
    fi
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
    # the author's repository as the self-test found it: HEAD, the working tree's status, the identity - compared at the end
    author_state() { printf '%s\n%s\n%s %s\n' "$(git -C "$ROOT" rev-parse HEAD)" "$(git -C "$ROOT" status --porcelain=v1 --untracked-files=no)" "$(git -C "$ROOT" config user.name)" "$(git -C "$ROOT" config user.email)"; }
    found="$(author_state)"
    git clone --quiet --shared --no-hardlinks "$ROOT" "$TMPD/repo" || { echo "FAIL — cannot clone for the self-test"; exit 2; }
    # the clone carries HEAD; the guard and the gate under test are the working tree's
    cp tools/check-parity-stamp.sh tools/check.sh "$TMPD/repo/tools/" || exit 2
    # the hook's environment, deliberately: the guard under test must drop these, or every git below acts on the author's repository
    export GIT_DIR="$(git -C "$ROOT" rev-parse --absolute-git-dir)" GIT_WORK_TREE="$ROOT"
    cd "$TMPD/repo" || exit 2
    guard() { bash tools/check-parity-stamp.sh "$@"; }
    in_clone() { env -u GIT_DIR -u GIT_WORK_TREE git -c core.safecrlf=false -c user.email=parity@self.test -c user.name=parity-self-test "$@"; }
    [ "$(in_clone rev-parse --show-toplevel)" != "$(git -C "$ROOT" rev-parse --show-toplevel)" ] || { echo "FAIL — self-test: the clone's git is the author's repository"; exit 1; }
    rc_of() { guard > "$TMPD/out" 2>&1; echo $?; }
    expect() {  # <case> <want rc> <got rc> [<needle in output>]
      local case="$1" want="$2" got="$3" needle="${4:-}"
      if [ "$got" != "$want" ] || { [ -n "$needle" ] && ! grep -qF -- "$needle" "$TMPD/out"; }; then
        printf 'FAIL — self-test %s: wanted rc %s%s, got rc %s:\n' "$case" "$want" "${needle:+ and \"$needle\"}" "$got"; sed 's/^/    /' "$TMPD/out"; exit 1
      fi
      printf 'PASS self-test %s (rc %s)\n' "$case" "$got"
    }
    guard --write > "$TMPD/out" 2>&1 || { echo "FAIL — self-test cannot write the stamp:"; cat "$TMPD/out"; exit 1; }
    in_clone add -A tools && in_clone commit -q -m "stamp" || exit 2
    expect "a fresh stamp is current" 0 "$(rc_of)" "parity stamp current"
    for touched in editor/Tools~/rig_anim.py editor/BlenderVehicleRig.Stage5.cs tools/vehiclerig-drill/vehiclerig_fixtures.py tools/blender-oracle/oracle.py; do
      printf '\n# self-test touch\n' >> "$touched"
      expect "a changed covered file is named ($touched)" 3 "$(rc_of)" "$touched"
      in_clone checkout -q HEAD -- "$touched"
    done
    printf '\nself-test touch\n' >> README.md
    expect "an uncovered file leaves the stamp current" 0 "$(rc_of)" "parity stamp current"
    in_clone checkout -q HEAD -- README.md
    # a covered file edited DURING a parity run: the digest taken before the drills differs from the one at the write
    before="$(guard --digest)" || exit 2
    printf '\n# self-test touch\n' >> editor/Tools~/vehicle_rig.py
    guard --write "$before" > "$TMPD/out" 2>&1; rc=$?
    expect "a change during the run gets no stamp" 1 "$rc" "changed DURING the parity run"
    grep -qF "editor/Tools~/vehicle_rig.py" "$TMPD/out" || { echo "FAIL — self-test: the file changed during the run is not named"; cat "$TMPD/out"; exit 1; }
    in_clone checkout -q HEAD -- editor/Tools~/vehicle_rig.py
    guard --write "$before" > "$TMPD/out" 2>&1; rc=$?
    expect "the same digest before and after writes" 0 "$rc" "parity stamp written"
    rm -f tools/parity-stamp.txt
    expect "no stamp is a SKIP" 3 "$(rc_of)" "no parity stamp"
    unset GIT_DIR GIT_WORK_TREE
    if [ "$(author_state)" != "$found" ]; then
      printf 'FAIL — the self-test touched the author'"'"'s repository (HEAD, working tree or identity changed):\n--- found\n%s\n--- now\n%s\n' "$found" "$(author_state)"; exit 1
    fi
    echo "PASS — parity stamp self-test: current, four changed covered files named, an uncovered file ignored, a change during the run refused, no stamp refused; the author's repository untouched"
    exit 0;;
  check) ;;
  *) echo "FAIL — unknown mode '$MODE' (check | --digest | --write [digest] | --list | --self-test)"; exit 2;;
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
if [ "$changed" = "?" ]; then printf '(the stamp'"'"'s commit is not in this clone, so the changed files cannot be listed; `bash tools/check-parity-stamp.sh --list` prints the covered set)\n'
elif [ -n "$changed" ]; then printf 'Changed since that commit, among the covered files:\n'; printf '  %s\n' $changed
else printf 'No covered file differs from that commit: the covered SET changed (%s files at the stamp, %s now: a drill names other files) or the stamp was written from uncommitted edits (%s).\n' "$(stamp_field files)" "$n" "$(stamp_field commit)"; fi
remedy; rm -f "$LIST"; exit 3
