#!/usr/bin/env bash
# =============================================================================
# scripts/build-and-record.sh: build everything, verify it, and write a build record.
#
#   scripts/build-and-record.sh "<title>" [--notes notes.md] [--clean]
#
# Every build we deliver gets a numbered record in docs/builds/ (see the README
# there). This script is the only way records are written, so a record always
# describes a real, complete run rather than someone's summary of it.
#
# What it runs, in order, continuing after failures so the record shows all of them:
#   1. native/build.sh linux-x64 and linux-arm64 (with --clean if given)
#   2. dotnet build (Release, warnings are errors)
#   3. dotnet test  (all unit tests)
#   4. the managed smoke test
#   5. the native smoke test, x64 directly and ARM64 under qemu (if installed)
#   6. dotnet pack of the native package
#
# Windows: the win-x64 section is filled from native/out/win-x64/build-info.txt
# when present (built on the PC, or downloaded from a CI run), otherwise marked
# as pending. Windows verification itself happens in CI on the self-hosted runner.
#
# Workflow: commit the code first, run this script, then commit the record:
#   git commit ...; scripts/build-and-record.sh "Phase 1: client read/write"
#   git add docs/builds && git commit -m "Build record NNNN"
#
# Exit code: 0 if every step passed, 1 otherwise (the record is written either way).
# =============================================================================
set -uo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

title=""
notes_file=""
clean=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --notes) notes_file="$2"; shift 2 ;;
    --clean) clean="--clean"; shift ;;
    -*) echo "unknown option $1" >&2; exit 2 ;;
    *) title="$1"; shift ;;
  esac
done
if [[ -z "$title" ]]; then
  echo 'usage: scripts/build-and-record.sh "<title>" [--notes notes.md] [--clean]' >&2
  exit 2
fi

records="$root/docs/builds"
logs="$(mktemp -d)"
mkdir -p "$records"

# ---- step runner -------------------------------------------------------------
# Each step's full output goes to its own log; the record gets PASS/FAIL, the
# duration and the last lines of output for failed steps.
declare -a step_names step_status step_secs
failed=0
run_step() {
  local name="$1"; shift
  local log="$logs/$(echo "$name" | tr ' /' '__').log"
  local start=$SECONDS
  echo "== $name"
  if "$@" >"$log" 2>&1; then
    step_status+=("PASS")
  else
    step_status+=("FAIL")
    failed=1
    tail -20 "$log" | sed 's/^/   /'
  fi
  step_names+=("$name")
  step_secs+=($((SECONDS - start)))
}

run_step "native linux-x64"   native/build.sh linux-x64 $clean
run_step "native linux-arm64" native/build.sh linux-arm64 $clean
run_step "dotnet build"       dotnet build Cictna.Industrial.slnx -c Release
run_step "unit tests"         dotnet test --solution Cictna.Industrial.slnx -c Release --no-build
run_step "managed smoke"      dotnet run --project tests/Smoke -c Release --no-build

smoke_bin="$logs/native-smoke"
run_step "native smoke x64" bash -c "gcc -o '$smoke_bin' native/tests/native_smoke.c -ldl && '$smoke_bin' native/out/linux-x64/libcictna_ua.so 48431"
if command -v qemu-aarch64-static >/dev/null && command -v aarch64-linux-gnu-gcc >/dev/null; then
  run_step "native smoke arm64 (qemu)" bash -c "aarch64-linux-gnu-gcc -o '$smoke_bin-arm64' native/tests/native_smoke.c -ldl && qemu-aarch64-static -L /usr/aarch64-linux-gnu '$smoke_bin-arm64' native/out/linux-arm64/libcictna_ua.so 48432"
fi
run_step "pack" dotnet pack src/Cictna.Industrial.OpcUa.Native -c Release -o "$logs/pkg"

# ---- gather facts --------------------------------------------------------------
n=$(( $(find "$records" -maxdepth 1 -name '[0-9][0-9][0-9][0-9]-*.md' | wc -l) + 1 ))
num=$(printf '%04d' "$n")
today=$(date -u +%Y-%m-%d)
slug=$(echo "$title" | tr '[:upper:]' '[:lower:]' | sed 's/[^a-z0-9]\+/-/g; s/^-//; s/-$//' | cut -c1-50)
file="$records/$num-$today-$slug.md"

commit=$(git rev-parse HEAD)
short=$(git rev-parse --short HEAD)
subject=$(git log -1 --format=%s)
dirty=$(git status --porcelain -- . ':!docs/builds' | grep -q . && echo "yes (uncommitted changes, record is not reproducible)" || echo "no")

# Commits since the previous record (its commit is stored in an HTML comment).
prev_file=$(find "$records" -maxdepth 1 -name '[0-9][0-9][0-9][0-9]-*.md' | sort | tail -1)
prev_commit=""
if [[ -n "$prev_file" ]]; then
  prev_commit=$(sed -n 's/^<!-- commit: \([0-9a-f]*\) -->$/\1/p' "$prev_file")
fi
if [[ -n "$prev_commit" ]] && git cat-file -e "$prev_commit" 2>/dev/null; then
  changes=$(git log --format='- `%h` %s' "$prev_commit..HEAD")
  changes_from="since build $(basename "$prev_file" | cut -c1-4) (\`${prev_commit:0:7}\`)"
else
  changes=$(git log --format='- `%h` %s' HEAD)
  changes_from="full history (first record)"
fi
[[ -z "$changes" ]] && changes="- (no new commits)"

test_log="$logs/unit_tests.log"
tests_total=$(sed -n 's/^ *total: *//p' "$test_log" | tail -1)
tests_failed=$(sed -n 's/^ *failed: *//p' "$test_log" | tail -1)
tests_passed=$(sed -n 's/^ *succeeded: *//p' "$test_log" | tail -1)

info() { sed -n "s/^$2: //p" "native/out/$1/build-info.txt" 2>/dev/null; }

native_row() {
  local rid="$1"
  if [[ -f "native/out/$rid/build-info.txt" ]]; then
    echo "| $rid | \`$(info "$rid" file)\` | $(info "$rid" size_bytes) | \`$(info "$rid" sha256)\` | $(info "$rid" compiler) | $(info "$rid" built_utc) |"
  else
    echo "| $rid | not built here | | | | |"
  fi
}

# ---- write the record ------------------------------------------------------------
{
  echo "# Build $num: $title"
  echo
  echo "<!-- commit: $commit -->"
  echo
  echo "| | |"
  echo "|---|---|"
  echo "| Result | **$([[ $failed -eq 0 ]] && echo PASS || echo FAIL)** |"
  echo "| Date (UTC) | $(date -u +'%Y-%m-%d %H:%M') |"
  echo "| Commit | \`$short\` $subject |"
  echo "| Uncommitted changes | $dirty |"
  echo "| Built on | $(uname -s) $(uname -m), $(. /etc/os-release 2>/dev/null && echo "$PRETTY_NAME") |"
  echo "| .NET SDK | $(dotnet --version) |"
  echo "| CMake | $(cmake --version | head -1 | awk '{print $3}') |"
  echo "| open62541 | $(sed -n 's/^OPEN62541_TAG=//p' native/versions.env) (\`$(sed -n 's/^OPEN62541_SHA=//p' native/versions.env | cut -c1-12)\`) |"
  echo "| mbedTLS | $(sed -n 's/^MBEDTLS_TAG=//p' native/versions.env) (\`$(sed -n 's/^MBEDTLS_SHA=//p' native/versions.env | cut -c1-12)\`) |"
  echo
  echo "## Notes"
  echo
  if [[ -n "$notes_file" && -f "$notes_file" ]]; then cat "$notes_file"; else echo "(none)"; fi
  echo
  echo "## Changes ($changes_from)"
  echo
  echo "$changes"
  echo
  echo "## Steps"
  echo
  echo "| Step | Result | Seconds |"
  echo "|---|---|---|"
  for i in "${!step_names[@]}"; do
    echo "| ${step_names[$i]} | ${step_status[$i]} | ${step_secs[$i]} |"
  done
  echo
  echo "Unit tests: ${tests_passed:-?} passed, ${tests_failed:-?} failed, ${tests_total:-?} total."
  echo
  echo "Managed smoke test output:"
  echo
  echo '```'
  cat "$logs/managed_smoke.log" 2>/dev/null
  echo '```'
  echo
  echo "## Native binaries"
  echo
  echo "| RID | File | Bytes | SHA-256 | Compiler | Built (UTC) |"
  echo "|---|---|---|---|---|---|"
  native_row linux-x64
  native_row linux-arm64
  native_row win-x64
  echo
  echo "Exported symbols (linux-x64): $(info linux-x64 exports)"
  echo
  echo "Minimum glibc: $(info linux-x64 min_glibc)"
  echo
  echo "## Platform verification"
  echo
  echo "| Platform | Status |"
  echo "|---|---|"
  echo "| linux-x64 | $([[ ${step_status[0]} == PASS && ${step_status[3]} == PASS && ${step_status[4]} == PASS ]] && echo 'verified here: native build, unit tests, managed and native smoke' || echo 'FAILED here, see steps') |"
  arm_status="cross-built"
  for i in "${!step_names[@]}"; do
    [[ "${step_names[$i]}" == "native smoke arm64 (qemu)" ]] && arm_status="cross-built, native smoke ${step_status[$i]} under qemu; managed tests not run on ARM64 hardware"
  done
  echo "| linux-arm64 | $arm_status |"
  if [[ -f native/out/win-x64/build-info.txt ]]; then
    echo "| win-x64 | binary present (built $(info win-x64 built_utc) from \`$(info win-x64 repo_commit)\`); verify the CI run for this commit |"
  else
    echo "| win-x64 | **pending**: needs the self-hosted runner (docs/pc-steps.md) |"
  fi
} > "$file"

rm -rf "$logs"
echo
echo "record written: ${file#$root/}"
exit $failed
