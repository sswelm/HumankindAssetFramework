#!/usr/bin/env bash
# check-docs.sh — the docs guard. Part of tools/check.sh (pre-push), runnable alone: bash tools/check-docs.sh
#
# Added 2026-08-20 with the docs/notes/ archive split. The docs are published three ways (the repo, the GitHub
# Pages site via jekyll-relative-links, and the wiki via tools/sync_wiki.sh) and every one of them resolves
# RELATIVE links — so one moved file breaks all three at once, silently. This guards what that split can break:
#   1. every relative Markdown link resolves to a file that exists;
#   2. every page in docs/notes/ carries the ARCHIVED NOTE banner (the convention that makes the split mean something);
#   3. no basename collides across docs/ and docs/notes/ (the wiki page namespace is FLAT — one would overwrite the other);
#   4. schema version and shared-field count agree with code;
#   5. maintained Pages links do not escape to repo-root relative URLs;
#   6. the generated wiki has complete navigation and valid page targets;
#   7. retired architecture claims do not return to current guides.
# Anchors (#section) are NOT checked — only the file half of each link.
set -uo pipefail
cd "$(dirname "$0")/.."
fail=0
broken="$(mktemp)"
wiki_tmp="$(mktemp -d)"
trap 'rm -f "$broken"; rm -rf "$wiki_tmp"' EXIT

# ---- 1. relative links resolve -------------------------------------------------------------------------------
for f in $(git ls-files '*.md'); do
  # This source becomes GitHub wiki's _Sidebar.md; its extensionless targets are wiki page names, not repo files.
  # The generated-wiki phase below validates every one against the emitted page set.
  [ "$f" = "tools/wiki-sidebar.md" ] && continue
  d="$(dirname "$f")"
  for target in $(grep -oE '\]\([^) ]+\)' "$f" | sed -e 's/^](//' -e 's/)$//'); do
    case "$target" in http:*|https:*|mailto:*|'#'*) continue ;; esac
    path="${target%%#*}"                       # drop the anchor
    [ -z "$path" ] && continue                 # was a pure anchor
    [ -e "$d/$path" ] || printf '  BROKEN LINK  %s -> %s\n' "$f" "$target" >> "$broken"
  done
done
if [ -s "$broken" ]; then cat "$broken"; fail=1; fi

# ---- 2. every archived note carries the banner ---------------------------------------------------------------
for f in docs/notes/*.md; do
  [ -e "$f" ] || continue
  head -6 "$f" | grep -q 'ARCHIVED NOTE' || {
    printf '  MISSING BANNER  %s (every docs/notes/ page must open with the "ARCHIVED NOTE — frozen <date>" block)\n' "$f"
    fail=1; }
done

# ---- 3. flat-wiki basename collisions -------------------------------------------------------------------------
dupes="$(for f in docs/*.md docs/notes/*.md; do basename "$f"; done 2>/dev/null | sort | uniq -d)"
[ -z "$dupes" ] || {
  printf '  NAME COLLISION  %s exists in BOTH docs/ and docs/notes/ — the wiki namespace is flat; rename one\n' $dupes
  fail=1; }

# ---- 4. the schema version is the SAME number in the code and in the docs ------------------------------------
# Added 2026-08-23 with the schema contract. `schemaVersion` was decorative for as long as its only definition was
# a sentence in Multi-Mod.md ("Currently 1") and a literal in the example pack — nothing in the code knew the
# number, so nothing could drift-check it. Now Haf.Schema.HafSchema.Version is the single definition, and the two
# places the docs quote it to pack authors have to agree, or the contract is a lie in exactly the file authors copy.
code_ver="$(grep -oE 'public const int Version = [0-9]+' Haf.Schema/HafSchema.cs | grep -oE '[0-9]+$')"
if [ -z "$code_ver" ]; then
  printf '  SCHEMA VERSION  cannot read Haf.Schema.HafSchema.Version from Haf.Schema/HafSchema.cs\n'; fail=1
else
  ex_ver="$(grep -oE '"schemaVersion"[[:space:]]*:[[:space:]]*[0-9]+' docs/haf-pack.example.json | grep -oE '[0-9]+$')"
  [ "$ex_ver" = "$code_ver" ] || {
    printf '  SCHEMA VERSION  docs/haf-pack.example.json says %s, HafSchema.Version says %s — pack authors copy that file\n' "${ex_ver:-<none>}" "$code_ver"
    fail=1; }
  # Multi-Mod.md states it in prose ("Currently `N`") — the sentence a pack author reads before writing the file.
  grep -qE "Currently \`$code_ver\`" docs/Multi-Mod.md || {
    printf '  SCHEMA VERSION  docs/Multi-Mod.md does not say "Currently `%s`" — its schemaVersion row has drifted from the code\n' "$code_ver"
    fail=1; }
fi

# ---- 5. the documented shared-field count agrees with its code owner -----------------------------------------
code_fields="$(grep -cE '^[[:space:]]+public ' Haf.Schema/HafModelSchema.cs)"
code_fields=$((code_fields - 1)) # the public class declaration is not a schema field
for claim in "holds the **$code_fields fields stored identically**" \
             "| $code_fields identical fields" \
             "all $code_fields shared"; do
  grep -Fq "$claim" docs/Shared-Schema.md || {
    printf '  FIELD COUNT  docs/Shared-Schema.md is missing the code-derived claim "%s"\n' "$claim"
    fail=1
  }
done

# ---- 6. maintained Pages docs may not use ../ links to repo-root files ---------------------------------------
# Jekyll publishes docs/ at the site root, so ../CREDITS.md resolves outside this project and 404s. Archived notes
# may use ../ to reach maintained docs and are checked by the ordinary file resolver above.
for f in docs/*.md; do
  [ -e "$f" ] || continue
  if grep -nE '\]\(\.\./' "$f"; then
    printf '  PAGES LINK  %s uses ../; link repo-root files with an absolute GitHub URL\n' "$f"
    fail=1
  fi
done

# ---- 7. generated wiki and tracked sidebar are complete ------------------------------------------------------
mkdir "$wiki_tmp/wiki"
# Under a git HOOK, git exports GIT_DIR (and sometimes GIT_WORK_TREE/GIT_INDEX_FILE) - and `git init <dir>` then
# initialises THAT repo instead of creating <dir>/.git, so sync_wiki.sh refused the target ("not a git clone").
# The pre-push gate was broken this way from 2026-08-29 (this step's arrival) until the first push after it.
env -u GIT_DIR -u GIT_WORK_TREE -u GIT_INDEX_FILE git init -q "$wiki_tmp/wiki"
bash tools/sync_wiki.sh "$wiki_tmp/wiki" >/dev/null || {
  printf '  WIKI OUTPUT  tools/sync_wiki.sh rejected its generated output\n'
  fail=1
}
for f in docs/*.md; do
  b="$(basename "$f" .md)"
  [ "$b" = "README" ] && continue
  grep -Fq "($b)" tools/wiki-sidebar.md || {
    printf '  WIKI SIDEBAR  docs/%s.md is missing from tools/wiki-sidebar.md\n' "$b"
    fail=1
  }
done

# ---- 8. retired claims stay out of current guides ------------------------------------------------------------
current_guides=(README.md docs/Architecture.md docs/Building.md docs/Code-Map.md docs/Decisions.md
                docs/Editor-Tools.md docs/Factory-Manual.md docs/Installation.md docs/Multi-Mod.md
                docs/Shared-Schema.md docs/Testing.md)
retired='Blender helpers aren.t in the package yet|Editor tooling — in ENCReload, not here|across two separate repos|check the sibling repo out|one hardcoded pack identity|ENCReload/Assets/Scripts/Editor|ENCReload/Assets/Plugins/HafSchema'
if grep -nEi "$retired" "${current_guides[@]}"; then
  printf '  RETIRED CLAIM  a current guide reintroduced a pre-package architecture statement\n'
  fail=1
fi

[ "$fail" -eq 0 ] && echo "docs: OK — repo/Pages/wiki links, sidebar coverage, archive rules, schema claims, and current architecture agree"
exit "$fail"
