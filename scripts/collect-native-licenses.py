#!/usr/bin/env python3
"""Collect licence texts of the native dependencies into native/licenses/.

Run after native/build.sh has fetched the sources (native/.deps), and again whenever
native/versions.env changes. The output is committed and shipped in the NuGet package.

  python3 scripts/collect-native-licenses.py
"""
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
DEPS = ROOT / "native" / ".deps"
OUT = ROOT / "native" / "licenses"

# open62541 bundled dependencies compiled into cictna_ua with our build options
# (see native/build.sh). mdnsd, mqtt-c, nodesetLoader and ua-nodeset are not built.
BUNDLED = [
    "base64", "cj5", "dtoa", "itoa", "libc_time", "mp_printf", "musl_inet_pton",
    "open62541_queue", "parse_num", "pcg_basic", "utf8", "yxml", "ziptree",
]


def leading_comment(path: pathlib.Path) -> str:
    """Return the comment blocks at the top of a C file that carry copyright or licence text."""
    text = path.read_text(encoding="utf-8", errors="replace")
    blocks = []
    pos = 0
    # Walk the comments before the first line of code.
    for m in re.finditer(r"\s*(/\*.*?\*/|(?://[^\n]*\n)+)", text, re.S):
        if m.start() != pos:
            break
        blocks.append(m.group(1).rstrip())
        pos = m.end()
    keep = [b for b in blocks if re.search(r"(?i)licen[cs]e|copyright|permission", b)]
    return "\n".join(keep)


def main() -> int:
    deps_dir = DEPS / "open62541" / "deps"
    if not deps_dir.is_dir():
        print("native/.deps/open62541 not found, run native/build.sh first", file=sys.stderr)
        return 1
    OUT.mkdir(parents=True, exist_ok=True)

    parts = [
        "Licence headers of the third-party files that open62541 bundles in its deps/ folder\n"
        "and that are compiled into cictna_ua. Collected from the pinned open62541 sources by\n"
        "scripts/collect-native-licenses.py.\n"
    ]
    for name in BUNDLED:
        header = deps_dir / f"{name}.h"
        source = deps_dir / f"{name}.c"
        block = leading_comment(header) if header.exists() else ""
        if not re.search(r"(?i)licen[cs]e|copyright|permission", block) and source.exists():
            block = leading_comment(source)
        if not block.strip():
            print(f"no licence header found for {name}", file=sys.stderr)
            return 1
        parts.append(f"\n{'=' * 78}\n{name}\n{'=' * 78}\n{block}\n")

    (OUT / "open62541-bundled-deps.txt").write_text("".join(parts), encoding="utf-8")
    (OUT / "open62541-MPL-2.0.txt").write_bytes((DEPS / "open62541" / "LICENSE").read_bytes())
    (OUT / "mbedtls-LICENSE.txt").write_bytes((DEPS / "mbedtls" / "LICENSE").read_bytes())
    print(f"wrote {len(BUNDLED)} dependency headers to {OUT}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
