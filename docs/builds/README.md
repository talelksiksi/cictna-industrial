# Build records

One file per build we deliver: `NNNN-YYYY-MM-DD-<title>.md`, numbered in order, never edited
afterwards. Together they are the history of what was built, from which commit, with which
versions, and what was verified on which platform.

## How a record is made

Records are written by `scripts/build-and-record.sh`, never by hand. The script runs the whole
pipeline (native builds, managed build, unit tests, smoke tests, packaging) and writes the record
from the real results, including failures.

```bash
git commit ...                                   # the code being recorded
scripts/build-and-record.sh "Phase 1: client read and write" --notes /tmp/notes.md
git add docs/builds && git commit -m "Build record NNNN"
```

`--notes` takes a short markdown file with what the build is for, decisions taken and known
issues. `--clean` rebuilds the native libraries from scratch.

## What a record contains

- result, date, commit, toolchain and pinned native versions;
- the commits since the previous record;
- every step with PASS/FAIL and duration, unit test counts, the smoke test output;
- each native binary with size, SHA-256 and compiler, so a shipped file can be traced back;
- per-platform verification status. Windows is verified by CI on the self-hosted runner; until
  that has run for a build, the record says so.

CI writes its own record for every run (job summary and the `build-record` artifact), covering
the Windows and Linux jobs of that run.
