#!/usr/bin/env bash
# org-cli installer
#
# Usage:
#   curl -fsSL https://raw.githubusercontent.com/dcprevere/org-cli/master/scripts/install.sh | bash
#
# Options (env vars or flags):
#   ORG_VERSION / --version <ver>   Install a specific version (default: latest). Accepts "1.0.0" or "v1.0.0".
#   ORG_PREFIX  / --prefix  <dir>   Install directory (default: $HOME/.local/bin).
#                                   Use /usr/local/bin for a system-wide install (sudo required).
#   ORG_NO_VERIFY / --no-verify     Skip sha256 verification (not recommended).
#
# Examples:
#   curl -fsSL .../install.sh | bash
#   curl -fsSL .../install.sh | ORG_VERSION=1.0.0 bash
#   curl -fsSL .../install.sh | bash -s -- --prefix /usr/local/bin

set -euo pipefail

REPO="dcprevere/org-cli"
VERSION="${ORG_VERSION:-latest}"
PREFIX="${ORG_PREFIX:-$HOME/.local/bin}"
VERIFY=1
[[ -n "${ORG_NO_VERIFY:-}" ]] && VERIFY=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --version)    VERSION="$2"; shift 2 ;;
    --prefix)     PREFIX="$2";  shift 2 ;;
    --no-verify)  VERIFY=0;      shift ;;
    -h|--help)
      sed -n '2,20p' "$0" 2>/dev/null || true
      exit 0
      ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done

err()  { printf 'error: %s\n' "$*" >&2; exit 1; }
info() { printf '%s\n' "$*"; }

need() { command -v "$1" >/dev/null 2>&1 || err "required command not found: $1"; }
need curl
need tar
need uname
need install

os="$(uname -s)"
arch="$(uname -m)"

case "$os" in
  Linux)  os_slug=linux ;;
  Darwin) os_slug=osx ;;
  *) err "unsupported OS: $os (this installer handles Linux and macOS)" ;;
esac

case "$arch" in
  x86_64|amd64)      arch_slug=x64 ;;
  aarch64|arm64)     arch_slug=arm64 ;;
  *) err "unsupported architecture: $arch" ;;
esac

asset="org-${os_slug}-${arch_slug}.tar.gz"

if [[ "$VERSION" == "latest" ]]; then
  base_url="https://github.com/${REPO}/releases/latest/download"
else
  tag="${VERSION#v}"
  tag="v${tag}"
  base_url="https://github.com/${REPO}/releases/download/${tag}"
fi

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

info "Downloading ${asset} from ${base_url}"
curl -fsSL --retry 3 -o "$tmp/$asset" "$base_url/$asset" \
  || err "failed to download $base_url/$asset"

if [[ "$VERIFY" -eq 1 ]]; then
  if command -v sha256sum >/dev/null 2>&1; then
    sha_tool=sha256sum
  elif command -v shasum >/dev/null 2>&1; then
    sha_tool="shasum -a 256"
  else
    err "no sha256 tool found (install coreutils or pass --no-verify)"
  fi

  info "Verifying sha256"
  curl -fsSL --retry 3 -o "$tmp/sha256sums.txt" "$base_url/sha256sums.txt" \
    || err "failed to download sha256sums.txt (pass --no-verify to skip)"

  expected="$(awk -v a="$asset" '$2 == a || $2 == "*"a {print $1}' "$tmp/sha256sums.txt")"
  [[ -n "$expected" ]] || err "no checksum entry for $asset in sha256sums.txt"

  actual="$($sha_tool "$tmp/$asset" | awk '{print $1}')"
  [[ "$actual" == "$expected" ]] \
    || err "sha256 mismatch: expected $expected, got $actual"
fi

info "Extracting"
tar -xzf "$tmp/$asset" -C "$tmp"
[[ -f "$tmp/org" ]] || err "archive did not contain expected 'org' binary"

use_sudo=""
if [[ ! -d "$PREFIX" ]]; then
  if ! mkdir -p "$PREFIX" 2>/dev/null; then
    command -v sudo >/dev/null 2>&1 || err "cannot create $PREFIX and sudo not available"
    sudo mkdir -p "$PREFIX"
    use_sudo="sudo"
  fi
fi
if [[ -z "$use_sudo" && ! -w "$PREFIX" ]]; then
  command -v sudo >/dev/null 2>&1 || err "$PREFIX is not writable and sudo not available"
  use_sudo="sudo"
fi

info "Installing to $PREFIX/org"
$use_sudo install -m 0755 "$tmp/org" "$PREFIX/org"

installed_version="$("$PREFIX/org" --version 2>/dev/null || true)"
info "Installed: ${installed_version:-org (version unknown)}"

case ":$PATH:" in
  *":$PREFIX:"*) ;;
  *)
    cat <<EOF

Note: $PREFIX is not on your PATH. Add this to your shell rc file:

  export PATH="$PREFIX:\$PATH"

EOF
    ;;
esac
