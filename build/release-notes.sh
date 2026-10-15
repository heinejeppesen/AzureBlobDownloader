#!/usr/bin/env bash
# Prints the release notes for a version: its CHANGELOG.md section, then .github/release-notes.md.
# Fails when CHANGELOG.md has no section for the version, so every release is described.
#
#   bash build/release-notes.sh 1.0.0 owner/repository > notes.md
set -euo pipefail

version="$1"
repository="$2"
root="$(cd "$(dirname "$0")/.." && pwd)"

# Lines after "## [<version>]" up to the next "## " heading.
section="$(awk -v heading="## [$version]" '
  index($0, heading) == 1 { found = 1; next }
  found && /^## / { exit }
  found { print }
' "$root/CHANGELOG.md")"

if [ -z "$(echo "$section" | tr -d '[:space:]')" ]; then
  echo "CHANGELOG.md has no section \"## [$version] - <date>\". Add one before releasing." >&2
  exit 1
fi

echo "$section" | sed -e '/./,$!d'   # without leading blank lines
echo
sed -e "s|{{VERSION}}|$version|g" -e "s|{{REPOSITORY}}|$repository|g" "$root/.github/release-notes.md"
