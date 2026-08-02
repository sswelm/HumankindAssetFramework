#!/usr/bin/env bash
# sync_wiki.sh — generate the GitHub Wiki from the repo docs (single source of truth = /docs + README + CHANGELOG).
#
# Mapping:  docs/README.md -> Home   |   README.md -> Overview   |   CHANGELOG.md -> Changelog
#           docs/<Name>.md -> <Name>  (one wiki page each)
# Cross-doc links are flattened to bare wiki page names; links to repo files that aren't wiki pages
# (CREDITS.md, LICENSE, haf-pack.example.json) are rewritten to absolute GitHub blob URLs.
#
# Usage:
#   bash tools/sync_wiki.sh          # PREVIEW: generate pages into ./.wiki-build (no network) so you can inspect them
#   bash tools/sync_wiki.sh --push   # clone the live wiki, copy pages in, commit & push
#
# The wiki must be ENABLED (repo Settings -> Features -> Wikis) and INITIALIZED (create one page in the web UI once)
# before --push can work — an empty GitHub wiki has no git repo to clone.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"
WIKI_URL="https://github.com/sswelm/HumankindAssetFramework.wiki.git"
BLOB="https://github.com/sswelm/HumankindAssetFramework/blob/master"
PUSH=0; [ "${1:-}" = "--push" ] && PUSH=1

if [ "$PUSH" = 1 ]; then
  OUT="$REPO_ROOT/.wiki"
  if [ -d "$OUT/.git" ]; then
    git -C "$OUT" pull --ff-only --quiet
  elif ! git clone --quiet "$WIKI_URL" "$OUT" 2>/dev/null; then
    echo "ERROR: could not clone $WIKI_URL" >&2
    echo "Enable the wiki (Settings -> Features -> Wikis) and create one page in the web UI once, then re-run --push." >&2
    exit 1
  fi
  find "$OUT" -maxdepth 1 -name '*.md' -delete
else
  OUT="$REPO_ROOT/.wiki-build"
  rm -rf "$OUT"; mkdir -p "$OUT"
fi

banner() { printf '> _Auto-generated from the repo docs by `tools/sync_wiki.sh` — edit the source Markdown in the repo, not this wiki page._\n\n'; }

# Links inside a docs/*.md file (siblings have no path prefix; root files are one level up).
rewrite_docs() {
  sed -E \
    -e "s#\]\(\.\./README\.md#](Overview#g" \
    -e "s#\]\(\.\./CHANGELOG\.md#](Changelog#g" \
    -e "s#\]\(CREDITS\.md#]($BLOB/CREDITS.md#g" \
    -e "s#\]\(LICENSE#]($BLOB/LICENSE#g" \
    -e "s#\]\(haf-pack\.example\.json#]($BLOB/docs/haf-pack.example.json#g" \
    -e "s#\]\(README\.md#](Home#g" \
    -e "s#\]\(CHANGELOG\.md#](Changelog#g" \
    -e "s#\]\(([A-Za-z0-9._-]+)\.md#](\1#g"
}

# Links inside a root file (README.md / CHANGELOG.md): docs are one level down.
rewrite_root() {
  sed -E \
    -e "s#\]\(docs/README\.md#](Home#g" \
    -e "s#\]\(docs/haf-pack\.example\.json#]($BLOB/docs/haf-pack.example.json#g" \
    -e "s#\]\(docs/([A-Za-z0-9._-]+)\.md#](\1#g" \
    -e "s#\]\(CHANGELOG\.md#](Changelog#g" \
    -e "s#\]\(README\.md#](Overview#g" \
    -e "s#\]\(CREDITS\.md#]($BLOB/CREDITS.md#g" \
    -e "s#\]\(LICENSE#]($BLOB/LICENSE#g"
}

{ banner; rewrite_docs < docs/README.md; }  > "$OUT/Home.md"
{ banner; rewrite_root < README.md; }        > "$OUT/Overview.md"
{ banner; rewrite_root < CHANGELOG.md; }     > "$OUT/Changelog.md"
for f in docs/*.md; do
  base="$(basename "$f" .md)"
  [ "$base" = "README" ] && continue
  { banner; rewrite_docs < "$f"; } > "$OUT/$base.md"
done

# Sidebar navigation (mirrors the docs index grouping).
cat > "$OUT/_Sidebar.md" <<'EOF'
### HAF Wiki
- [Home](Home)
- [Overview](Overview)
- [Changelog](Changelog)

**Author content**
- [Factory-Manual](Factory-Manual)
- [Animated-Models](Animated-Models)
- [Textures](Textures)
- [Game-Sound-Lab](Game-Sound-Lab)

**Injection axes**
- [District-Visuals](District-Visuals)
- [Pawn-Props](Pawn-Props)
- [Projectiles](Projectiles)
- [Formations](Formations)
- [Unit-Size](Unit-Size)

**Ship a pack**
- [Multi-Mod](Multi-Mod)

**Internals**
- [Code-Map](Code-Map)
- [Animated-Runtime](Animated-Runtime)
- [Unit-Combat-Behavior](Unit-Combat-Behavior)
- [Firing-On-Attack](Firing-On-Attack)
- [Facing-Persistence](Facing-Persistence)
- [Vertex-Budget](Vertex-Budget)
- [Capabilities](Capabilities)
- [Animation-Pitfalls](Animation-Pitfalls)

**Project & roadmap**
- [Framework-Review](Framework-Review)
- [Review-Backlog](Review-Backlog)
- [Testing](Testing)
- [Ecosystem-Survey](Ecosystem-Survey)
- [Building](Building)
- [Backup](Backup)
EOF

echo "Generated $(ls "$OUT"/*.md | wc -l) wiki pages into ${OUT#$REPO_ROOT/}"

if [ "$PUSH" = 1 ]; then
  git -C "$OUT" add -A
  if git -C "$OUT" diff --cached --quiet; then
    echo "Wiki already up to date — nothing to push."
  else
    git -C "$OUT" -c user.name=sswelm -c user.email=sswelm@users.noreply.github.com commit -q -m "Sync wiki from docs/"
    git -C "$OUT" push --quiet
    echo "Pushed wiki update."
  fi
fi
