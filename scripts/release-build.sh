#!/usr/bin/env bash
# Build a local Unix Native AOT release bundle for the host (linux-x64 or
# osx-arm64, the platforms install.sh and the release workflow support), run the
# published scenario smoke on it, and package debug symbols as a separate asset.
# Uploading/publishing is separate.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
VERSION="${1:?usage: release-build.sh VERSION [OUTPUT_DIR]}"
[[ "$VERSION" =~ ^[0-9][0-9A-Za-z.+-]*$ ]] || { echo "invalid version: $VERSION" >&2; exit 2; }
case "$(uname -s)-$(uname -m)" in
  Linux-x86_64) RID=linux-x64; LIB_EXT=so; HASH=(sha256sum); SUMS=SHA256SUMS;;
  Darwin-arm64) RID=osx-arm64; LIB_EXT=dylib; HASH=(shasum -a 256); SUMS=SHA256SUMS-osx-arm64;;
  *) echo 'linux-x64 or osx-arm64 host required (the bundles install.sh and the release workflow ship)' >&2; exit 2;;
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
PUBLISH_ARGS=()
# On a Mac with only the Command Line Tools, the ILCompiler's `xcodebuild -version`
# probe prints an error that fails the publish; skip it (it only selects -ld_classic for Xcode 15/16).
if [[ "$RID" == osx-* ]] && ! xcodebuild -version >/dev/null 2>&1; then PUBLISH_ARGS=(-p:UseLdClassicXCodeLinker=false); fi
work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT

"$DOTNET" publish src/AgentTeamForge.Host/AgentTeamForge.Host.csproj -m:1 -c Release -r "$RID" \
  --self-contained true -p:PublishAot=true -p:TreatWarningsAsErrors=true \
  -p:Version="$VERSION" -p:InformationalVersion="$VERSION" -o "$work/publish" ${PUBLISH_ARGS[@]+"${PUBLISH_ARGS[@]}"}
mkdir -p "$work/package/extensions/pi-wake"
cp "$work/publish/atf" "$work/package/atf"
cp "$work/publish"/*."$LIB_EXT" "$work/package/"
cp install.sh "$work/package/install.sh"
cp extensions/pi-wake/{index.ts,README.md,PROVENANCE.md} "$work/package/extensions/pi-wake/"
# The bundled extension carries the release version, not the source tree's placeholder.
sed -e 's/^  "version": "[^"]*",$/  "version": "'"$VERSION"'",/' extensions/pi-wake/package.json > "$work/package/extensions/pi-wake/package.json"
grep -Fqx "  \"version\": \"$VERSION\"," "$work/package/extensions/pi-wake/package.json" || { echo 'pi-wake version stamp failed' >&2; exit 1; }
cp -R extensions/pi-wake/src "$work/package/extensions/pi-wake/"
for license in LICENSE LICENSE.md COPYING; do
  if [[ -f "$license" ]]; then cp "$license" "$work/package/"; fi
done
"$work/package/atf" --version | grep -Fx "atf $VERSION"
archive="atf-$VERSION-$RID.tar.gz"
# macOS bsdtar otherwise records the build host's extended attributes (com.apple.provenance) as pax headers.
tar --no-xattrs -C "$work/package" -czf "$OUTPUT/$archive" .
mkdir "$work/extracted"
tar -C "$work/extracted" -xzf "$OUTPUT/$archive"
"$work/extracted/atf" --version | grep -Fx "atf $VERSION"
"$DOTNET" build AgentTeamForge.slnx -m:1 -c Release -warnaserror
# Debug symbols ship as a separate asset: atf.dbg on Linux, the atf.dSYM bundle (a directory) on macOS.
symbols=()
if [[ -f "$work/publish/atf.dbg" ]]; then
  cp "$work/publish/atf.dbg" "$OUTPUT/atf-$VERSION-$RID.dbg"
  symbols+=("atf-$VERSION-$RID.dbg")
fi
if [[ -d "$work/publish/atf.dSYM" ]]; then
  tar --no-xattrs -C "$work/publish" -czf "$OUTPUT/atf-$VERSION-$RID.dSYM.tar.gz" atf.dSYM
  symbols+=("atf-$VERSION-$RID.dSYM.tar.gz")
fi
cp install.sh "$OUTPUT/install.sh"
# The checksum file is written only after the smoke passes: its presence marks a gated bundle.
rm -f -- "$OUTPUT/$SUMS"
DOTNET="$DOTNET" "$ROOT/scripts/published-smoke.sh" "$work/extracted/atf"
(
  cd "$OUTPUT"
  "${HASH[@]}" "$archive" install.sh ${symbols[@]+"${symbols[@]}"} > "$SUMS"
)
echo "release bundle: $OUTPUT/$archive"
