#!/bin/sh
# User-scope Unix installer for the versioned AOT bundle.
set -eu
fail() { echo "atf installer: $*" >&2; exit 1; }
usage() { echo 'usage: install.sh [--version VERSION] [--archive FILE] [--checksum FILE] [--release-url URL] [--state-dir DIR] | --uninstall [--purge] [--state-dir DIR]' >&2; exit 2; }
version= archive= checksum= release_url= uninstall= purge= state_dir=
while [ "$#" -gt 0 ]; do
  case "$1" in
    --version|--archive|--checksum|--release-url|--state-dir)
      [ "$#" -ge 2 ] || usage
      case "$1" in
        --version) version=$2;; --archive) archive=$2;; --checksum) checksum=$2;;
        --release-url) release_url=$2;; --state-dir) state_dir=$2;;
      esac
      shift 2;;
    --uninstall) uninstall=1; shift;;
    --purge) purge=1; shift;;
    *) usage;;
  esac
done
case "$(uname -s)-$(uname -m)" in
  Linux-x86_64) rid=linux-x64; hash_tool=sha256sum; sums=SHA256SUMS;;
  Darwin-arm64) rid=osx-arm64; hash_tool=shasum; sums=SHA256SUMS-osx-arm64;;
  *) fail 'only linux-x64 and osx-arm64 bundles are prepared';;
esac
for tool in tar "$hash_tool" mktemp readlink; do command -v "$tool" >/dev/null 2>&1 || fail "missing $tool"; done
sha_file() { if [ "$hash_tool" = shasum ]; then shasum -a 256 "$@"; else sha256sum "$@"; fi; }
[ -n "${HOME:-}" ] || fail 'HOME is required'
root=$HOME/.local/share/agentteamforge
releases=$root/releases
bin=$HOME/.local/bin/atf
state=${state_dir:-${XDG_STATE_HOME:-$HOME/.local/state}/agentteamforge}
case "$state" in /*) ;; *) fail 'state directory must be absolute';; esac
owned_bin=$root/current/atf
check_current() {
  if [ -e "$root/current" ] || [ -L "$root/current" ]; then
    [ -L "$root/current" ] || fail "refusing unmanaged $root/current"
    current_target=$(readlink "$root/current")
    case "$current_target" in releases/*) ;; *) fail "refusing unmanaged $root/current";; esac
    current_version=${current_target#releases/}
    case "$current_version" in ''|*/*|*[!0-9A-Za-z.+-]*) fail "refusing unmanaged $root/current";; esac
    [ -f "$root/current/.atf-files" ] || fail "refusing unmarked $root/current"
  fi
}

stop_current() {
  if [ -L "$root/current" ] && [ -x "$root/current/atf" ] && [ -d "$state" ]; then
    "$root/current/atf" stop --state-dir "$state" || fail 'daemon did not stop; installation left unchanged'
  fi
}

if [ -n "$uninstall" ]; then
  [ -z "$version$archive$checksum$release_url" ] || usage
  if [ -e "$bin" ] || [ -L "$bin" ]; then
    [ -L "$bin" ] && [ "$(readlink "$bin")" = "$owned_bin" ] || fail "refusing to remove unmanaged $bin"
  fi
  check_current
  if [ -n "$purge" ]; then
    [ -d "$state" ] && [ ! -L "$state" ] || fail 'state directory missing or symlink; nothing purged'
    [ -f "$state/profile.json" ] && [ -f "$state/operator.key" ] || fail 'state directory has no ATF profile and key'
  fi
  stop_current
  if [ -L "$bin" ]; then rm -- "$bin"; fi
  if [ -L "$root/current" ]; then rm -- "$root/current"; fi
  if [ -d "$releases" ]; then
    for dir in "$releases"/*; do
      [ -d "$dir" ] && [ ! -L "$dir" ] && [ -f "$dir/.atf-files" ] || continue
      while read -r expected path; do
        case "$path" in ./*) ;; *) fail "invalid manifest path in $dir";; esac
        case "$path" in *'/../'*|*'/./'*|*/..|*/.) fail "invalid manifest path in $dir";; esac
        file=$dir/$path
        if [ -f "$file" ] && [ ! -L "$file" ] && [ "$(sha_file "$file" | cut -d ' ' -f 1)" = "$expected" ]; then
          rm -- "$file"
        else
          echo "atf installer: kept modified $file" >&2
        fi
      done < "$dir/.atf-files"
      rm -- "$dir/.atf-files"
      find "$dir" -depth -type d -exec rmdir -- {} \; 2>/dev/null || :
    done
    rmdir "$releases" "$root" 2>/dev/null || :
  fi
  if [ -n "$purge" ]; then
    rm -rf -- "$state"
    echo "purged state: $state"
  else
    echo "state kept: $state"
  fi
  echo 'atf uninstalled'
  exit 0
fi
[ -z "$purge" ] || usage
case "$version" in ''|*[!0-9A-Za-z.+-]*) [ -z "$version" ] || fail 'invalid version';; esac
if [ -n "$archive" ]; then
  [ -f "$archive" ] || fail "archive not found: $archive"
  if [ -z "$version" ]; then
    name=${archive##*/}
    case "$name" in atf-*-$rid.tar.gz) version=${name#atf-}; version=${version%-$rid.tar.gz};; *) fail 'use --version with this archive name';; esac
  fi
  checksum=${checksum:-$(dirname "$archive")/$sums}
  [ -f "$checksum" ] || fail "checksum not found: $checksum"
else
  command -v curl >/dev/null 2>&1 || fail 'curl is required for downloads'
  repo=https://github.com/PRFactory-app/agent-team-forge/releases
  if [ -z "$version" ]; then
    latest=$(curl -fsSL -o /dev/null -w '%{url_effective}' "$repo/latest") || fail 'could not resolve latest release'
    tag=${latest##*/}
    case "$tag" in v*) version=${tag#v};; *) fail 'latest release tag must begin with v';; esac
  fi
  release_url=${release_url:-$repo/download/v$version}
fi
case "$version" in ''|*[!0-9A-Za-z.+-]*) fail 'invalid version';; esac
case "$version" in [0-9]*) ;; *) fail 'version must start with a digit';; esac
name=atf-$version-$rid.tar.gz
stage=
scratch=$(mktemp -d)
trap 'rm -rf -- "$scratch" ${stage:+"$stage"}' EXIT
trap 'exit 130' HUP INT TERM
if [ -z "$archive" ]; then
  archive=$scratch/$name
  checksum=$scratch/SHA256SUMS
  curl -fsSL "$release_url/$name" -o "$archive" || fail 'archive download failed'
  curl -fsSL "$release_url/$sums" -o "$checksum" || fail 'checksum download failed'
fi
expected=$(awk -v name="$name" '$2 == name && $1 ~ /^[0-9a-fA-F]+$/ {print $1}' "$checksum")
[ "${#expected}" -eq 64 ] || fail "checksum entry missing for $name"
actual=$(sha_file "$archive" | cut -d ' ' -f 1)
[ "$actual" = "$expected" ] || fail 'archive checksum mismatch'
[ ! -L "$releases" ] && [ ! -L "$root" ] || fail 'install directory is a symlink'
if [ -e "$bin" ] || [ -L "$bin" ]; then
  [ -L "$bin" ] && [ "$(readlink "$bin")" = "$owned_bin" ] || fail "refusing to replace unmanaged $bin"
fi
check_current
mkdir -p "$releases" "$HOME/.local/bin"
target=$releases/$version
[ ! -e "$target" ] && [ ! -L "$target" ] || fail "version already installed: $version"
stage=$(mktemp -d "$releases/.stage.XXXXXX")
if [ "$rid" = linux-x64 ]; then
  tar -xzf "$archive" -C "$stage" --no-same-owner --no-same-permissions
else
  tar -xzf "$archive" -C "$stage"
fi
[ -x "$stage/atf" ] && [ -f "$stage/install.sh" ] || fail 'archive missing atf or install.sh'
[ "$("$stage/atf" --version)" = "atf $version" ] || fail 'archive version mismatch'
(cd "$stage" && find . -type f ! -name .atf-files | sort | while IFS= read -r path; do sha_file "$path"; done) > "$stage/.atf-files"
stop_current
mv "$stage" "$target"
stage=
ln -s "releases/$version" "$root/.current.$$"
if [ "$rid" = linux-x64 ]; then
  mv -Tf "$root/.current.$$" "$root/current"
else
  # BSD mv needs -h to replace a symlink to a directory itself.
  mv -fh "$root/.current.$$" "$root/current"
fi
if [ ! -L "$bin" ]; then
  ln -s "$owned_bin" "$HOME/.local/bin/.atf.$$"
  mv -f "$HOME/.local/bin/.atf.$$" "$bin"
fi
echo "installed atf $version: $bin"
quoted_bin=$(printf '%s' "$bin" | sed "s/'/'\\\\''/g")
printf "Run: '%s' setup\n" "$quoted_bin"
echo 'Reload installed clients after setup; the daemon starts on first use.'
case ":$PATH:" in
  *":$HOME/.local/bin:"*) ;;
  *) echo 'Optional: add "$HOME/.local/bin" to PATH for the shorter atf command.' ;;
esac
