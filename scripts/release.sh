#!/usr/bin/env bash
set -euo pipefail

usage() {
  echo "Usage: $0 <version>"
  echo "  version: semver without v prefix (e.g. 2.0.0 or 2.0.0-rc.1)"
  exit 1
}

[[ $# -eq 1 ]] || usage

version="$1"

if ! [[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?$ ]]; then
  echo "Error: version must be semver (e.g. 2.0.0-rc.1), got: $version"
  exit 1
fi

tag="v$version"

if git rev-parse "$tag" >/dev/null 2>&1; then
  echo "Error: tag $tag already exists"
  exit 1
fi

if [[ -n "$(git status --porcelain)" ]]; then
  echo "Error: working tree is dirty — commit or stash first"
  exit 1
fi

root="$(git rev-parse --show-toplevel)"
cd "$root"

echo "Bumping version to $version ..."

# Directory.Build.props (.NET)
sed -i "s|<Version>[^<]*</Version>|<Version>$version</Version>|" \
  "$root/Directory.Build.props"

echo "Building ..."
dotnet build OrgCli.slnx --warnaserror

echo "Testing ..."
dotnet test OrgCli.slnx --no-build

echo "Testing API and MCP ..."
python3 tests/server_smoke.py

echo "Committing ..."
git add "$root/Directory.Build.props"
git commit -m "chore: bump version to $version"

echo "Tagging $tag ..."
git tag "$tag"

echo "Pushing ..."
git push
git push origin "$tag"

echo "Released $tag"
