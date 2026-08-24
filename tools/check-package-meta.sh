#!/usr/bin/env bash
# check-package-meta.sh — EVERY file and folder in editor/ must have a .meta committed beside it.
#
# WHY THIS EXISTS, and why it is a gate rather than a fixed bug. This exact class broke the package install THREE
# times in one afternoon (2026-08-24), each time a file added under Assets/ where Unity generates the .meta
# silently, then shipped in a package folder where it CANNOT:
#
#   1. package.json + the .asmdef            -> "The asset will be ignored." The ignored ASMDEF is the fatal one:
#                                               package scripts are not added to the predefined assemblies, so with
#                                               no assembly definition NOTHING compiled. The package installed,
#                                               resolved, and showed its version and description while being
#                                               functionally empty.
#   2. HafPackageContext.cs                  -> caught before shipping only because its .meta was written by hand.
#   3. the Plugins/ FOLDER                   -> the DLL had a .meta; the directory containing it did not. Unity
#                                               needs one for folders too, and ignored the whole folder.
#
# The failure is invisible from the authoring side by construction: under Assets/ Unity writes the missing .meta
# the moment the editor regains focus, so the working copy is always correct and only the COMMIT is short. That is
# precisely the shape a source-only gate catches and a human review does not.
#
# Pure filesystem check over the git index (not the working tree) — an untracked .meta is exactly the bug.
set -uo pipefail
cd "$(dirname "$0")/.." || exit 2

PKG="editor"
[ -d "$PKG" ] || { echo "check-package-meta: no $PKG/ directory — nothing to check."; exit 0; }

missing=0
checked=0

# Every TRACKED path under editor/, plus every directory on the way to it. Unity wants a .meta for both.
tracked=$(git ls-files "$PKG")
[ -n "$tracked" ] || { echo "check-package-meta: nothing tracked under $PKG/ — is the package committed?"; exit 1; }

# collect files (excluding .meta themselves) and every ancestor directory below editor/
paths=$(
  printf '%s\n' "$tracked" | grep -v '\.meta$'
  printf '%s\n' "$tracked" | grep -v '\.meta$' | while IFS= read -r f; do
    d=$(dirname "$f")
    while [ "$d" != "$PKG" ] && [ "$d" != "." ] && [ "$d" != "/" ]; do printf '%s\n' "$d"; d=$(dirname "$d"); done
  done
)

for p in $(printf '%s\n' "$paths" | sort -u); do
  checked=$((checked + 1))
  if ! git ls-files --error-unmatch "${p}.meta" >/dev/null 2>&1; then
    # distinguish "exists locally but never committed" from "absent entirely" — the first is the common mistake
    if [ -e "${p}.meta" ]; then echo "FAIL — ${p}.meta exists on disk but is NOT COMMITTED (git add it)"
    else echo "FAIL — ${p}.meta is missing entirely"; fi
    missing=$((missing + 1))
  fi
done

if [ "$missing" -gt 0 ]; then
  echo
  echo "$missing path(s) in $PKG/ have no committed .meta. Unity CANNOT generate them in a package folder — it is"
  echo "immutable — so it logs \"has no meta file, but it's in an immutable folder. The asset will be ignored.\""
  echo "and drops the asset. An ignored .asmdef means the whole package compiles to nothing."
  echo
  echo "Fix: open the package's source folder in a Unity project so the .meta files are generated, or hand-write"
  echo "them (fileFormatVersion: 2 + a unique 32-hex guid + the importer stanza), then commit them."
  exit 1
fi

echo "package meta: OK — all $checked tracked path(s) under $PKG/ have a committed .meta."
