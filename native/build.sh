#!/usr/bin/env bash
# =============================================================================
# native/build.sh: builds the native OPC UA library for one Linux runtime id.
#
#   native/build.sh <linux-x64|linux-arm64> [--clean]
#
# Output (both under native/out/<rid>/):
#   libcictna_ua.so   mbedTLS + open62541 + our shim, one self-contained library
#   build-info.txt    what went into it: versions, compiler, options, SHA-256
#
# How it works, in three CMake builds:
#   1. mbedTLS   -> static libraries, installed into native/.build/<rid>/prefix
#   2. open62541 -> static library linked against that mbedTLS, same prefix
#   3. shim      -> shared library that pulls both in and exports only cictna_ua_*
#
# Why static + one shared library: the managed package then ships exactly one
# file per platform with no system dependencies besides libc. Client machines
# need no OpenSSL or mbedTLS installed, and versions cannot drift.
#
# Sources are fetched from GitHub at the tags and commits pinned in
# versions.env. A tag can be moved upstream; the commit hash cannot, so the
# script refuses to build if they disagree (supply-chain protection).
#
# ARM64 is cross-compiled from x64 (toolchains/aarch64-linux-gnu.cmake), so a
# single x64 machine or CI runner builds both Linux targets.
#
# The Windows equivalent is native/build.ps1. Keep the open62541 options in
# both scripts identical. Design notes: docs/native-layer.md.
# =============================================================================
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

deps="$here/.deps"            # fetched sources, shared by all RIDs (git-ignored)
work="$here/.build/$rid"      # CMake build trees, per RID, incremental (git-ignored)
prefix="$work/prefix"         # install prefix for the static libraries and headers
out="$here/out/$rid"          # final library + build-info (git-ignored, CI artifact)

if [[ "$clean" == "--clean" ]]; then rm -rf "$work" "$out"; fi
mkdir -p "$deps" "$work" "$out"

# Clone a pinned tag and refuse to continue if the commit is not the expected one.
# An existing checkout at the right commit is reused, so rebuilds work offline.
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
# mbedTLS 3.6 keeps part of its CMake/build scripts in a 'framework' submodule.
git -C "$deps/mbedtls" submodule update --quiet --init --depth 1 framework
fw="$(git -C "$deps/mbedtls/framework" rev-parse HEAD)"
if [[ "$fw" != "$MBEDTLS_FRAMEWORK_SHA" ]]; then
  echo "error: mbedtls framework is $fw, expected $MBEDTLS_FRAMEWORK_SHA" >&2
  exit 1
fi

common=(
  -G Ninja
  -DCMAKE_BUILD_TYPE=Release
  # Static libraries end up inside a shared library, so they must be PIC.
  -DCMAKE_POSITION_INDEPENDENT_CODE=ON
  -DCMAKE_INSTALL_PREFIX="$prefix"
  # Fixed 'lib' (not lib64 or lib/<triplet>) so the next step finds the libraries.
  -DCMAKE_INSTALL_LIBDIR=lib
  "${toolchain[@]}"
)

echo "== building mbedTLS $MBEDTLS_TAG for $rid"
# Tests and example programs are not needed and roughly double the build time.
# MBEDTLS_FATAL_WARNINGS=OFF: a newer compiler adding a warning must not break
# our build of an unmodified, pinned upstream release.
cmake -S "$deps/mbedtls" -B "$work/mbedtls" "${common[@]}" \
  -DENABLE_TESTING=OFF \
  -DENABLE_PROGRAMS=OFF \
  -DUSE_STATIC_MBEDTLS_LIBRARY=ON \
  -DUSE_SHARED_MBEDTLS_LIBRARY=OFF \
  -DMBEDTLS_FATAL_WARNINGS=OFF >/dev/null
cmake --build "$work/mbedtls" >/dev/null
cmake --install "$work/mbedtls" >/dev/null

echo "== building open62541 $OPEN62541_TAG for $rid"
# Option by option (keep identical in build.ps1):
#   BUILD_SHARED_LIBS=OFF        static, linked into cictna_ua (see header)
#   UA_ENABLE_ENCRYPTION=MBEDTLS Basic256Sha256 and friends for Sign / SignAndEncrypt.
#                                mbedTLS over OpenSSL: smaller, static-friendly, Apache-2.0.
#   UA_MULTITHREADING=100        internal locking of the public API (thread safety net)
#   UA_NAMESPACE_ZERO=REDUCED    standard address space without the full nodeset;
#                                enough for client work and our simulator server.
#                                FULL needs the ua-nodeset submodule; revisit in Phase 5.
#   UA_ENABLE_PUBSUB=OFF         not used; MQTT/Sparkplug will be our own C# code later
#   ..._MDNSD / MQTT / NODESETLOADER OFF   not used; less code, fewer third-party deps
#   UA_LOGLEVEL=300              compile out trace/debug logging (300 = info)
#   UA_BUILD_EXAMPLES / UNIT_TESTS OFF     upstream's own tests are run by upstream CI
#   UA_FORCE_WERROR=OFF          same reason as MBEDTLS_FATAL_WARNINGS above
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

# ---- build-info.txt ----------------------------------------------------------
# One "key: value" per line. Read by scripts/build-and-record.sh to write the
# build record in docs/builds/, uploaded by CI next to the binary.
lib="$out/libcictna_ua.so"
# The compiler CMake actually used (works for native and cross builds).
cc="$(sed -n 's/^set(CMAKE_C_COMPILER "\(.*\)")$/\1/p' "$work"/shim/CMakeFiles/*/CMakeCCompiler.cmake | head -1)"
repo_commit="$(git -C "$here/.." rev-parse --short HEAD 2>/dev/null || echo unknown)"
repo_dirty="$(git -C "$here/.." status --porcelain 2>/dev/null | grep -q . && echo yes || echo no)"
{
  echo "rid: $rid"
  echo "built_utc: $(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo "repo_commit: $repo_commit"
  echo "repo_dirty: $repo_dirty"
  echo "open62541: $OPEN62541_TAG ($OPEN62541_SHA)"
  echo "mbedtls: $MBEDTLS_TAG ($MBEDTLS_SHA)"
  echo "compiler: $("$cc" --version | head -1)"
  echo "cmake: $(cmake --version | head -1 | awk '{print $3}')"
  echo "options: encryption=mbedtls multithreading=100 ns0=reduced pubsub=off loglevel=300"
  echo "file: $(basename "$lib")"
  echo "size_bytes: $(stat -c %s "$lib")"
  echo "sha256: $(sha256sum "$lib" | awk '{print $1}')"
  echo "exports: $(nm -D --defined-only "$lib" | awk '$2=="T"{print $3}' | sort | tr '\n' ' ' | sed 's/ $//')"
  echo "min_glibc: $(objdump -T "$lib" | grep -o 'GLIBC_[0-9.]*' | sort -uV | tail -1)"
} > "$out/build-info.txt"

echo "== done: $lib"
