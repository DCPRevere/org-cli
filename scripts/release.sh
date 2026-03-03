#!/usr/bin/env bash
set -euo pipefail

usage() {
  echo "Usage: $0 <version>"
  echo "  version: semver without v prefix (e.g. 0.5.0)"
  exit 1
}

[[ $# -eq 1 ]] || usage

version="$1"

if ! [[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "Error: version must be semver (e.g. 0.5.0), got: $version"
  exit 1
fi

tag="v$version"

if git rev-parse "$tag" >/dev/null 2>&1; then
  echo "Error: tag $tag already exists"
  exit 1
fi

if ! git diff --quiet || ! git diff --cached --quiet; then
  echo "Error: working tree is dirty — commit or stash first"
  exit 1
fi

root="$(git rev-parse --show-toplevel)"

echo "Bumping version to $version ..."

# Directory.Build.props (.NET)
sed -i "s|<Version>[^<]*</Version>|<Version>$version</Version>|" \
  "$root/Directory.Build.props"

# OpenClaw plugin
plugin="$root/integrations/openclaw/plugin"
sed -i "s|\"version\": \"[^\"]*\"|\"version\": \"$version\"|" \
  "$plugin/openclaw.plugin.json" \
  "$plugin/package.json"

# package-lock.json: only replace the top-level project version lines,
# not dependency versions deeper in the file
sed -i '1,10 s|"version": "[^"]*"|"version": "'"$version"'"|' \
  "$plugin/package-lock.json"

echo "Building ..."
dotnet build --warnaserror -q

echo "Testing ..."
dotnet test --no-build -q

echo "Committing ..."
git add \
  "$root/Directory.Build.props" \
  "$plugin/openclaw.plugin.json" \
  "$plugin/package.json" \
  "$plugin/package-lock.json"

git commit -m "chore: bump version to $version"

echo "Tagging $tag ..."
git tag "$tag"

echo "Pushing ..."
git push
git push origin "$tag"

echo "Released $tag"
