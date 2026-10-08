#!/usr/bin/env bash
# Build libvgmstream.so / libvgmstream.dylib into BydTools.Audio/3rdParty.
# Clones the commit in scripts/vgmstream.rev unless VGMSTREAM_ROOT is an existing checkout.
# Usage: ./scripts/build-libvgmstream.sh [output_dir]
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/.." && pwd)"
revision="$(tr -d '[:space:]' < "$script_dir/vgmstream.rev")"
output="${1:-$repo_root/BydTools.Audio/3rdParty}"
root="${VGMSTREAM_ROOT:-}"
build="${VGMSTREAM_BUILD_DIR:-}"
origin="https://github.com/vgmstream/vgmstream.git"

if [[ ! "$revision" =~ ^[0-9a-f]{40}$ ]]; then
  echo "scripts/vgmstream.rev must be a 40-character commit SHA." >&2
  exit 1
fi

for tool in git cmake pkg-config; do
  if ! command -v "$tool" >/dev/null 2>&1; then
    echo "Required tool not found: $tool" >&2
    exit 1
  fi
done

# Vorbis and FFmpeg are linked in as static archives. Anything else must be a system library.
check_self_contained() {
  local lib="$1"
  local -a bad=()
  local dep
  if [[ "$(uname -s)" == Darwin ]]; then
    while IFS= read -r dep; do
      dep="${dep%% (*}"
      dep="${dep#"${dep%%[![:space:]]*}"}"
      [[ -z "$dep" ]] && continue
      case "$dep" in
        /usr/lib/*|/System/*|*libvgmstream.dylib) ;;
        *) bad+=("$dep") ;;
      esac
    done < <(otool -L "$lib" | tail -n +2)
  else
    while IFS= read -r dep; do
      [[ -z "$dep" ]] && continue
      case "$dep" in
        linux-vdso.so.1|libc.so.*|libm.so.*|libpthread.so.*|libdl.so.*|librt.so.*|libgcc_s.so.*|libstdc++.so.*|ld-linux*.so.*) ;;
        *) bad+=("$dep") ;;
      esac
    done < <(ldd "$lib" | awk '/=>/ {print $1} !/=>/ && /\.so/ {print $1}')
  fi
  if ((${#bad[@]})); then
    echo "libvgmstream links libraries that are not part of the system runtime:" >&2
    printf '  %s\n' "${bad[@]}" >&2
    exit 1
  fi
}

patch_apple_ld() {
  local cmake_lists="$1/src/CMakeLists.txt"
  python3 - "$cmake_lists" <<'PY'
import pathlib, sys
path = pathlib.Path(sys.argv[1])
text = path.read_text()
old = 'if(CMAKE_C_COMPILER_ID MATCHES "GNU|Clang")'
new = 'if(CMAKE_C_COMPILER_ID MATCHES "GNU|Clang" AND NOT APPLE)'
if "AND NOT APPLE" in text:
    sys.exit(0)
if old not in text:
    sys.exit("Could not patch Apple ld. It rejects -Wl,--exclude-libs.")
path.write_text(text.replace(old, new, 1))
print(f"Patched {path} so Apple ld does not see --exclude-libs.")
PY
}

managed=0
if [[ -z "$root" ]]; then
  base="${RUNNER_TEMP:-${TMPDIR:-/tmp}}"
  base="${base%/}"
  root="$base/vgmstream/src"
  managed=1
elif [[ ! -d "$root/.git" ]]; then
  managed=1
fi

if [[ "$managed" == 1 ]]; then
  mkdir -p "$root"
  if [[ ! -d "$root/.git" ]]; then
    git -C "$root" init
    git -C "$root" remote add origin "$origin"
  fi
  echo "== Fetch vgmstream $revision =="
  git -C "$root" fetch --depth 1 origin "$revision"
  git -C "$root" checkout --force --detach FETCH_HEAD
fi

if [[ "$(uname -s)" == Darwin ]]; then
  if ! command -v python3 >/dev/null 2>&1; then
    echo "Required tool not found: python3" >&2
    exit 1
  fi
  patch_apple_ld "$root"
fi

if [[ -z "$build" ]]; then
  build="$root/build-bydtools"
fi
mkdir -p "$build" "$output"

# Static archives linked into the shared library must be position-independent.
# Hide system opus so vgmstream keeps FFmpeg's built-in Opus decoder.
export CFLAGS="${CFLAGS:-} -fPIC"
export CXXFLAGS="${CXXFLAGS:-} -fPIC"
real_pkg_config="$(command -v pkg-config)"
pkg_config_dir="$(mktemp -d)"
cat > "$pkg_config_dir/pkg-config" <<EOF
#!/bin/sh
for arg in "\$@"; do
  case "\$arg" in
    *opus*) exit 1 ;;
  esac
done
exec $(printf '%q' "$real_pkg_config") "\$@"
EOF
chmod +x "$pkg_config_dir/pkg-config"
export PATH="$pkg_config_dir:$PATH"

case "$(uname -s)" in
  Darwin) rpath="@loader_path"; libname="libvgmstream.dylib" ;;
  *) rpath='$ORIGIN'; libname="libvgmstream.so" ;;
esac

echo "== Configure vgmstream =="
cmake -S "$root" -B "$build" \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_POSITION_INDEPENDENT_CODE=ON \
  -DCMAKE_BUILD_WITH_INSTALL_RPATH=ON \
  -DCMAKE_INSTALL_RPATH="$rpath" \
  -DBUILD_SHARED_LIBS=ON \
  -DBUILD_STATIC=OFF \
  -DBUILD_CLI=OFF \
  -DBUILD_V123=OFF \
  -DBUILD_AUDACIOUS=OFF \
  -DUSE_MPEG=OFF \
  -DUSE_G719=OFF \
  -DUSE_ATRAC9=OFF \
  -DUSE_CELT=OFF \
  -DUSE_SPEEX=OFF \
  -DUSE_VORBIS=ON \
  -DUSE_FFMPEG=ON \
  -DUSE_G7221=ON

echo "== Build libvgmstream_shared =="
cmake --build "$build" --target libvgmstream_shared --parallel

built="$build/src/$libname"
if [[ ! -f "$built" ]]; then
  found="$(find "$build" -name "$libname" -type f | head -n 1 || true)"
  if [[ -z "${found:-}" ]]; then
    echo "Built library not found: $built" >&2
    exit 1
  fi
  built="$found"
fi

cp -f "$built" "$output/$libname"
check_self_contained "$output/$libname"
echo "Copied $libname to $output"
