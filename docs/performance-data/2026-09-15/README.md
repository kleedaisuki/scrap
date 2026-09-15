# Performance evidence — 2026-09-15

These are the compact raw JSON outputs behind [`docs/performance.md`](../../performance.md). They preserve measured values, not generated profiles, databases, binaries, or traces.

- `baseline-private.json`: complete private-cache CRUD and search run. Its startup section was superseded by `startup-final.json` after startup terminology and sampling were corrected.
- `startup-final.json`: reported process-readiness and persistent-ping distributions.
- `metadata-{shared,private}.json`: focused shared/default-private cache A/B on the same 50,000-record database.
- `index-{keep,drop}-{1,2}.json`: two runs per scope-index condition.
- `resource-footprint.json`: 10 idle-daemon runs and 5 sustained 10,000-record fuzzy-search runs.

The committed harness reproduces current-baseline methodology, but normal machine and OS noise prevents bit-for-bit timing reproduction. The historical shared-cache side requires its corresponding pre-change production revision; current `main` intentionally uses SQLite's default private cache.
