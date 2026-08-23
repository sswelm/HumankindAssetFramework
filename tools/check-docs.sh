#!/usr/bin/env bash
# check-docs.sh — the docs guard. Part of tools/check.sh (pre-push), runnable alone: bash tools/check-docs.sh
#
# Added 2026-08-20 with the docs/notes/ archive split. The docs are published three ways (the repo, the GitHub
# Pages site via jekyll-relative-links, and the wiki via tools/sync_wiki.sh) and every one of them resolves
# RELATIVE links — so one moved file breaks all three at once, silently. This guards what that split can break:
#   1. every relative Markdown link resolves to a file that exists;
#   2. every page in docs/notes/ carries the ARCHIVED NOTE banner (the convention that makes the split mean something);
#   3. no basename collides across docs/ and docs/notes/ (the wiki page namespace is FLAT — one would overwrite the other).
# Anchors (#section) are NOT checked — only the file half of each link.
set -uo pipefail
cd "$(dirname "$0")/.."
fail=0
broken="$(mktemp)"; trap 'rm -f "$broken"' EXIT

# ---- 1. relative links resolve -------------------------------------------------------------------------------
for f in $(git ls-files '*.md'); do
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

[ "$fail" -eq 0 ] && echo "docs: OK — links resolve, notes banners present, no flat-namespace collisions, schema version agrees with the code"
exit "$fail"
