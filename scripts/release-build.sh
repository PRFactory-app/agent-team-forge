#!/usr/bin/env bash
# Build a local Linux x64 release bundle. Uploading/publishing is separate.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
VERSION="${1:?usage: release-build.sh VERSION [OUTPUT_DIR]}"
[[ "$VERSION" =~ ^[0-9][0-9A-Za-z.+-]*$ ]] || { echo "invalid version: $VERSION" >&2; exit 2; }
[[ "$(uname -s)" == Linux && "$(uname -m)" == x86_64 ]] || { echo 'linux-x64 required' >&2; exit 2; }
if [[ -z "${DOTNET:-}" ]]; then
  common="$(git rev-parse --path-format=absolute --git-common-dir)"
  if [[ -x "$common/../.tools/dotnet11/dotnet" ]]; then
    DOTNET="$common/../.tools/dotnet11/dotnet"
  else
    DOTNET="$(command -v dotnet)"
  fi
fi
if [[ "$DOTNET" != */* ]]; then DOTNET="$(command -v "$DOTNET")"; fi
DOTNET="$(realpath "$DOTNET")"
export DOTNET_ROOT="$(dirname "$DOTNET")"
[[ "$("$DOTNET" --version)" == "$(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' global.json)" ]] || { echo 'pinned SDK required' >&2; exit 2; }
OUTPUT="${2:-$ROOT/artifacts/release-$VERSION}"
mkdir -p "$OUTPUT"
OUTPUT="$(realpath "$OUTPUT")"
work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT

"$DOTNET" publish src/AgentTeamForge.Host/AgentTeamForge.Host.csproj -m:1 -c Release -r linux-x64 \
  --self-contained true -p:PublishAot=true -p:TreatWarningsAsErrors=true \
  -p:Version="$VERSION" -p:InformationalVersion="$VERSION" -o "$work/publish"
mkdir -p "$work/package/extensions/pi-wake"
cp "$work/publish/atf" "$work/package/atf"
cp "$work/publish"/*.so "$work/package/"
cp install.sh "$work/package/install.sh"
cp extensions/pi-wake/{index.ts,package.json,README.md,PROVENANCE.md} "$work/package/extensions/pi-wake/"
cp -R extensions/pi-wake/src "$work/package/extensions/pi-wake/"
for license in LICENSE LICENSE.md COPYING; do
  if [[ -f "$license" ]]; then cp "$license" "$work/package/"; fi
done
"$work/package/atf" --version | grep -Fx "atf $VERSION"
archive="atf-$VERSION-linux-x64.tar.gz"
tar -C "$work/package" -czf "$OUTPUT/$archive" .
mkdir "$work/extracted"
tar -C "$work/extracted" -xzf "$OUTPUT/$archive"
"$work/extracted/atf" --version | grep -Fx "atf $VERSION"
"$DOTNET" build AgentTeamForge.slnx -m:1 -c Release -warnaserror
DOTNET="$DOTNET" "$ROOT/scripts/published-smoke.sh" "$work/extracted/atf"
if [[ -f "$work/publish/atf.dbg" ]]; then cp "$work/publish/atf.dbg" "$OUTPUT/atf-$VERSION-linux-x64.dbg"; fi
cp install.sh "$OUTPUT/install.sh"
(
  cd "$OUTPUT"
  sha256sum "$archive" install.sh > SHA256SUMS
  for symbol in atf-*.dbg; do
    if [[ -f "$symbol" ]]; then sha256sum "$symbol" >> SHA256SUMS; fi
  done
)
echo "release bundle: $OUTPUT/$archive"
