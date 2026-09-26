#!/usr/bin/env bash
# Build a local Unix Native AOT release bundle. Uploading/publishing is separate.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
VERSION="${1:?usage: release-build.sh VERSION [OUTPUT_DIR]}"
[[ "$VERSION" =~ ^[0-9][0-9A-Za-z.+-]*$ ]] || { echo "invalid version: $VERSION" >&2; exit 2; }
case "$(uname -s)-$(uname -m)" in
  Linux-x86_64) RID=linux-x64; LIB_EXT=so; HASH=(sha256sum);;
  Darwin-arm64) RID=osx-arm64; LIB_EXT=dylib; HASH=(shasum -a 256);;
  *) echo 'linux-x64 or osx-arm64 required' >&2; exit 2;;
esac
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

"$DOTNET" publish src/AgentTeamForge.Host/AgentTeamForge.Host.csproj -m:1 -c Release -r "$RID" \
  --self-contained true -p:PublishAot=true -p:TreatWarningsAsErrors=true \
  -p:Version="$VERSION" -p:InformationalVersion="$VERSION" -o "$work/publish"
mkdir -p "$work/package/extensions/pi-wake"
cp "$work/publish/atf" "$work/package/atf"
cp "$work/publish"/*."$LIB_EXT" "$work/package/"
cp install.sh "$work/package/install.sh"
cp extensions/pi-wake/{index.ts,package.json,README.md,PROVENANCE.md} "$work/package/extensions/pi-wake/"
cp -R extensions/pi-wake/src "$work/package/extensions/pi-wake/"
for license in LICENSE LICENSE.md COPYING; do
  if [[ -f "$license" ]]; then cp "$license" "$work/package/"; fi
done
"$work/package/atf" --version | grep -Fx "atf $VERSION"
archive="atf-$VERSION-$RID.tar.gz"
tar -C "$work/package" -czf "$OUTPUT/$archive" .
mkdir "$work/extracted"
tar -C "$work/extracted" -xzf "$OUTPUT/$archive"
"$work/extracted/atf" --version | grep -Fx "atf $VERSION"
"$DOTNET" build AgentTeamForge.slnx -m:1 -c Release -warnaserror
if [[ "$RID" == linux-x64 ]]; then DOTNET="$DOTNET" "$ROOT/scripts/published-smoke.sh" "$work/extracted/atf"; fi
if [[ -f "$work/publish/atf.dbg" ]]; then cp "$work/publish/atf.dbg" "$OUTPUT/atf-$VERSION-$RID.dbg"; fi
cp install.sh "$OUTPUT/install.sh"
(
  cd "$OUTPUT"
  "${HASH[@]}" "$archive" install.sh > SHA256SUMS
  for symbol in atf-*.dbg; do
    if [[ -f "$symbol" ]]; then "${HASH[@]}" "$symbol" >> SHA256SUMS; fi
  done
)
echo "release bundle: $OUTPUT/$archive"
