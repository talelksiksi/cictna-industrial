#!/usr/bin/env bash
# Builds the native OPC UA library for one runtime id:
# mbedTLS (static) + open62541 (static) + our shim, linked into one shared library.
#
# Usage: native/build.sh <linux-x64|linux-arm64> [--clean]
# Output: native/out/<rid>/libcictna_ua.so
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=versions.env
source "$here/versions.env"

rid="${1:-}"
clean="${2:-}"
case "$rid" in
  linux-x64)   toolchain=() ;;
  linux-arm64) toolchain=(-DCMAKE_TOOLCHAIN_FILE="$here/toolchains/aarch64-linux-gnu.cmake") ;;
  *) echo "usage: build.sh <linux-x64|linux-arm64> [--clean]" >&2; exit 2 ;;
esac

deps="$here/.deps"
work="$here/.build/$rid"
prefix="$work/prefix"
out="$here/out/$rid"

if [[ "$clean" == "--clean" ]]; then rm -rf "$work" "$out"; fi
mkdir -p "$deps" "$work" "$out"

# Clone a pinned tag and refuse to continue if the commit is not the expected one.
fetch() {
  local name="$1" url="$2" tag="$3" sha="$4" dir="$deps/$1"
  if [[ -d "$dir/.git" && "$(git -C "$dir" rev-parse HEAD)" == "$sha" ]]; then
    return
  fi
  rm -rf "$dir"
  echo "== fetching $name $tag"
  git -c advice.detachedHead=false clone --quiet --depth 1 --branch "$tag" "$url" "$dir"
  local got
  got="$(git -C "$dir" rev-parse HEAD)"
  if [[ "$got" != "$sha" ]]; then
    echo "error: $name $tag is $got, expected $sha" >&2
    exit 1
  fi
}

fetch open62541 "$OPEN62541_URL" "$OPEN62541_TAG" "$OPEN62541_SHA"
fetch mbedtls "$MBEDTLS_URL" "$MBEDTLS_TAG" "$MBEDTLS_SHA"
git -C "$deps/mbedtls" submodule update --quiet --init --depth 1 framework
fw="$(git -C "$deps/mbedtls/framework" rev-parse HEAD)"
if [[ "$fw" != "$MBEDTLS_FRAMEWORK_SHA" ]]; then
  echo "error: mbedtls framework is $fw, expected $MBEDTLS_FRAMEWORK_SHA" >&2
  exit 1
fi

common=(
  -G Ninja
  -DCMAKE_BUILD_TYPE=Release
  -DCMAKE_POSITION_INDEPENDENT_CODE=ON
  -DCMAKE_INSTALL_PREFIX="$prefix"
  -DCMAKE_INSTALL_LIBDIR=lib
  "${toolchain[@]}"
)

echo "== building mbedTLS $MBEDTLS_TAG for $rid"
cmake -S "$deps/mbedtls" -B "$work/mbedtls" "${common[@]}" \
  -DENABLE_TESTING=OFF \
  -DENABLE_PROGRAMS=OFF \
  -DUSE_STATIC_MBEDTLS_LIBRARY=ON \
  -DUSE_SHARED_MBEDTLS_LIBRARY=OFF \
  -DMBEDTLS_FATAL_WARNINGS=OFF >/dev/null
cmake --build "$work/mbedtls" >/dev/null
cmake --install "$work/mbedtls" >/dev/null

echo "== building open62541 $OPEN62541_TAG for $rid"
cmake -S "$deps/open62541" -B "$work/open62541" "${common[@]}" \
  -DBUILD_SHARED_LIBS=OFF \
  -DUA_ENABLE_ENCRYPTION=MBEDTLS \
  -DMBEDTLS_FOLDER_INCLUDE="$prefix/include" \
  -DMBEDTLS_FOLDER_LIBRARY="$prefix/lib" \
  -DUA_MULTITHREADING=100 \
  -DUA_NAMESPACE_ZERO=REDUCED \
  -DUA_ENABLE_PUBSUB=OFF \
  -DUA_ENABLE_DISCOVERY_MULTICAST_MDNSD=OFF \
  -DUA_ENABLE_MQTT=OFF \
  -DUA_ENABLE_NODESETLOADER=OFF \
  -DUA_LOGLEVEL=300 \
  -DUA_BUILD_EXAMPLES=OFF \
  -DUA_BUILD_UNIT_TESTS=OFF \
  -DUA_FORCE_WERROR=OFF >/dev/null
cmake --build "$work/open62541" >/dev/null
cmake --install "$work/open62541" >/dev/null

echo "== building shim for $rid"
cmake -S "$here/shim" -B "$work/shim" "${common[@]}" \
  -DCMAKE_PREFIX_PATH="$prefix" \
  -DCICTNA_UA_OUT_DIR="$out" >/dev/null
cmake --build "$work/shim" >/dev/null

echo "== done: $(ls "$out")"
