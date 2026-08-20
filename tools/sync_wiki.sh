#!/usr/bin/env bash
# Sync the GitHub wiki from the repo docs (the wiki pages' header has referenced this script since the
# first sync — this is now actually it). Usage:
#   git clone https://github.com/sswelm/HumankindAssetFramework.wiki.git <dir>
#   tools/sync_wiki.sh <dir>     # then commit+push inside <dir>
# Mapping: docs/README.md -> Home, repo README.md -> Overview, CHANGELOG.md -> Changelog, docs/X.md -> X.
# Links: docs/ prefixes stripped, .md suffixes stripped (GitHub wiki page names), repo-home deep link -> Overview.
# _Sidebar.md is CURATED BY HAND in the wiki repo — new pages must be added there deliberately; this
# script never touches it.
set -euo pipefail
cd "$(dirname "$0")/.."
WIKI="${1:?usage: tools/sync_wiki.sh <wiki-clone-dir>}"
# GUARDS (2026-08-20): a relative <dir> (".") used to resolve against the repo root after the cd above — and on a
# case-insensitive filesystem the emitted Changelog.md then OVERWROTE the repo's CHANGELOG.md and strewed 42 wiki
# pages through the repo root. The destination must be an existing wiki clone OUTSIDE this repo.
WIKI="$(cd "$WIKI" 2>/dev/null && pwd)" || { echo "FAIL: wiki dir does not exist: $1" >&2; exit 1; }
REPO_ROOT="$(pwd)"
case "$WIKI/" in "$REPO_ROOT"/*) echo "FAIL: wiki dir resolves inside the repo ($WIKI) — pass the wiki CLONE dir" >&2; exit 1;; esac
[ -d "$WIKI/.git" ] || { echo "FAIL: $WIKI is not a git clone (no .git) — clone the wiki repo there first" >&2; exit 1; }
HDR='> _Auto-generated from the repo docs by `tools/sync_wiki.sh` — edit the source Markdown in the repo, not this wiki page._'
REPO='https://github.com/sswelm/HumankindAssetFramework'   # repo-only files (CREDITS/LICENSE/llms.txt/examples) can't be wiki pages

emit() {  # emit <src> <dst-basename>
  { echo "$HDR"; echo; cat "$1"; } | sed \
    -e 's|](https://github\.com/sswelm/HumankindAssetFramework#readme)|](Overview)|g' \
    -e 's#](docs/README\.md#](Home#g' \
    -e 's#](docs/#](#g' \
    -e 's#](README\.md#](Overview#g' \
    -e 's#](CHANGELOG\.md#](Changelog#g' \
    -e "s#](\(CREDITS\.md\|LICENSE\|llms\.txt\|haf-pack\.example\.json\))#]($REPO/blob/master/\1)#g" \
    -e 's#](\([A-Za-z0-9._-]*\)\.md)#](\1)#g' \
    -e 's#](\([A-Za-z0-9._-]*\)\.md\##](\1\##g' \
    > "$WIKI/$2"
}

emit README.md Overview.md
emit CHANGELOG.md Changelog.md
emit docs/README.md Home.md
for f in docs/*.md; do
  b="$(basename "$f")"
  [ "$b" = "README.md" ] && continue
  emit "$f" "$b"
done
echo "Synced README + CHANGELOG + $(ls docs/*.md | grep -vc 'README') doc page(s) -> $WIKI"
