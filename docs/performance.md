# Scrap performance baseline

- **Status:** measured baseline and bottleneck analysis
- **Measured:** 2026-09-15
- **Code:** `main` at `224dfc3`, plus the removal of SQLite shared-cache mode described below
- **Decision target:** interactive use on one workstation, from hundreds to tens of thousands of records

This document records observations, not portable performance promises. The numbers describe one Windows machine and are intended to answer two questions: whether Scrap is comfortably fast for its dominant personal-use workload, and where work should start if a real profile grows to tens of thousands of records.

## 1. Result in one paragraph

For an already-running daemon, IPC is effectively negligible: ping latency was **0.133 ms p50 / 0.216 ms p95**. End-to-end encrypted record operations through the real daemon were **1.97 ms p50 set**, **0.335 ms get**, and **1.59 ms delete**. Search is the scaling boundary. At 1,000 records, all-scope fuzzy search was **5.26 ms p50**; at 10,000 it was **81.7 ms**; at 50,000 it was **424 ms**. Selecting three of ten scopes reduced the 50,000-record fuzzy case to **119 ms** because only 15,000 candidates were loaded. Controlled phase timings and an EventPipe CPU profile agree that the SQLite metadata-loading/materialization path is dominant, not fuzzy scoring. This is excellent for ordinary personal profiles and still usable at 10,000 records, but an all-scope 50,000-record profile is visibly outside the instant-search regime.

## 2. Environment and method

| Variable | Value |
|---|---|
| OS | Windows NT 10.0.26200, x64 |
| Runtime / SDK | .NET 10.0.11 / SDK 10.0.400, Release build |
| CPU | Intel Core i9-12900H, 14 cores / 20 logical processors |
| Memory | 31.75 GiB |
| Storage | Kingston OM8SEP41024Q-A0, NVMe SSD |
| Power plan | Windows High performance |
| SQLite | `Microsoft.Data.Sqlite` 10.0.12; WAL; `synchronous=FULL`; connection pooling enabled |

The isolated benchmark harness and every generated profile, trace, and JSON result lived under `.temp/` or `.cache/`; no real `~/.scrap` profile was opened. Reported percentiles use nearest-rank p50/p95/p99. Workloads ran sequentially after explicit warm-up, with no intentional competing load. Laptop thermals, Defender scanning, OS scheduling, filesystem cache, and garbage-collection timing remain uncontrolled confounders, so differences of only a few percent should be treated as noise. The methodology is repeatable, but timing values are not exactly reproducible across machines or even consecutive runs on one laptop. Compact raw outputs supporting this baseline are retained in [`docs/performance-data/2026-09-15`](performance-data/2026-09-15/).

### Re-running the maintained harness

Run from the repository root with the locked .NET SDK. Every generated root and output is deliberately restricted to this repository's `.temp/` or `.cache/` trees, so the tool cannot open the real `~/.scrap` profile. Fixture-creating modes (`baseline`, `startup`, and `resources`) require a nonexistent or empty run root and never clear previous data. Give each run a unique root; a complete baseline can take several minutes:

```powershell
dotnet restore tools/Scrap.Perf/Scrap.Perf.csproj --locked-mode
# Fast preflight smoke: proves a non-empty root is rejected without deletion.
pwsh -File tools/Scrap.Perf/Test-RunRootGuard.ps1
$run = ".temp/perf-repro-$([guid]::NewGuid().ToString('N'))"
dotnet run --no-restore -c Release --project tools/Scrap.Perf -- baseline $run "$run/results.json"
```

The explicit modes permit focused reruns. `baseline` first creates the 50,000-record fixture used by the latter commands:

```powershell
# Process readiness and persistent IPC only (Windows uses the real DPAPI provider).
$startupRun = ".temp/perf-startup-$([guid]::NewGuid().ToString('N'))"
dotnet run --no-restore -c Release --project tools/Scrap.Perf -- startup $startupRun "$startupRun/results.json"

# Idle and sustained 10k-search daemon memory/CPU (about two minutes).
$resourceRun = ".temp/perf-resources-$([guid]::NewGuid().ToString('N'))"
dotnet run --no-restore -c Release --project tools/Scrap.Perf -- resources $resourceRun "$resourceRun/results.json"

# Current metadata materialization path against the generated 50k database.
$db = "$run/search/search-50000/data/scrap.db"
dotnet run --no-restore -c Release --project tools/Scrap.Perf -- metadata $db "$run/metadata.json"

# Index A/B requires independent database copies because each run writes fixtures.
Copy-Item $db "$run/index-keep.db"
Copy-Item $db "$run/index-drop.db"
dotnet run --no-restore -c Release --project tools/Scrap.Perf -- index "$run/index-keep.db" keep "$run/index-keep.json"
dotnet run --no-restore -c Release --project tools/Scrap.Perf -- index "$run/index-drop.db" drop "$run/index-drop.json"
```

The historical shared-cache A/B cannot be rerun from current `main` alone because that variant was intentionally removed. Its raw outputs are retained beside the baseline; recreating that comparison requires the corresponding pre-change source with `Cache=Shared`. Do not compare a new run directly with the retained numbers as if the host environment were controlled.

### Startup and IPC

Each startup sample launched a fresh `dotnet` child process containing the production daemon host, created a real named-pipe client, completed protocol negotiation, and issued `ListScopes`; unlike `Ping`, `ListScopes` waits for storage and key initialization. Two distinct cases were measured:

- **New-profile process ready:** a new process plus a new isolated profile, real Windows DPAPI master-key creation, SQLite schema creation, connection, and the first storage-backed request; 20 samples.
- **Existing-profile process ready:** still a **new process and new client connection**, but reusing an existing database and loading its master key through real Windows DPAPI; 30 samples. This is not the latency of an ordinary operation on a persistent GUI connection.
- **Persistent-client IPC ping:** 1,000 requests over an already-negotiated pipe after 50 warm-ups.

### CRUD

CRUD used an isolated production daemon, named-pipe IPC, SQLite, AES-256-GCM, and the real Windows DPAPI-backed master-key provider. DPAPI runs at daemon initialization; record values use AES-GCM per operation. After 25 complete warm-up cycles, the measured phases performed 500 unique sets, 1,000 gets over those records, and 500 deletes, using a 65-character ASCII fixture value. Throughput is sequential operations per wall-clock second, not a concurrency claim.

### Search

Search databases contained 1,000, 10,000, or 50,000 valid AES-GCM records spread evenly across ten scopes. Setup inserted these fixtures in one transaction and was excluded from timing. Every measured query crossed the typed client, named pipe, production daemon dispatcher, SQLite metadata query, domain matcher, result mapping, JSON framing, and response read. Each scenario used 8 warm-ups and 60 samples with a global result limit of 100.

The search-only daemon used a deterministic in-memory test master-key provider so large fixtures could be reproduced without creating thousands of OS keychain entries. It is **not** an OS-keychain benchmark. The database contains real authenticated ciphertext, but search never loads or decrypts values, so the provider does not participate in timed queries. Startup and CRUD numbers above do use Windows DPAPI.

Keys were shaped like `service-000123-credential`, with every tenth record ending in `api-token`. Exact used one existing full key, fuzzy used `api-token`, and regex used `api-token$`. The three-scope case selected `scope-01`, `scope-03`, and `scope-07` (30% of the records). Its regex intentionally has no match but still scans the selected candidate set.

## 3. Measurements

The CRUD, search, and phase tables come from [`baseline-private.json`](performance-data/2026-09-15/baseline-private.json). The readiness and ping table uses the corrected follow-up run in [`startup-final.json`](performance-data/2026-09-15/startup-final.json); the startup object embedded in the main baseline is retained as raw history but is superseded. Focused cache and index tables map to the correspondingly named JSON files in the same directory.

### 3.1 Process readiness and persistent IPC

All values are milliseconds.

| Operation | n | p50 | p95 | p99 |
|---|---:|---:|---:|---:|
| New-profile daemon process to storage-ready | 20 | 340.8 | 364.2 | 426.8 |
| Existing-profile **new process/client** to storage-ready | 30 | 342.3 | 358.5 | 364.0 |
| Persistent-client IPC ping | 1,000 | 0.133 | 0.216 | 0.452 |

The daemon is designed to persist for five idle minutes, so normal GUI activity pays the sub-millisecond IPC path, not a 342 ms process start for every action.

### 3.2 Encrypted end-to-end CRUD

| Operation | samples | operations/s | p50 ms | p95 ms | p99 ms |
|---|---:|---:|---:|---:|---:|
| Set | 500 | 438 | 1.97 | 4.31 | 5.80 |
| Get + decrypt | 1,000 | 2,563 | 0.335 | 0.709 | 1.10 |
| Delete | 500 | 524 | 1.59 | 3.66 | 5.42 |

Set and delete deliberately pay one durable `synchronous=FULL` transaction apiece. The write figures therefore favor data integrity over synthetic batch throughput and match the actual single-record product contract.

### 3.3 Multi-scope search scaling

All values are complete IPC round-trip milliseconds. No regex timeout occurred in this final 1,080-query run; an earlier exploratory 50,000-record run did produce one timeout under system jitter, so the existing 100 ms regex-evaluation deadline should still be treated as a meaningful tail guard rather than proof that every 50,000-record regex will succeed.

| Total records | Selection / candidates | Mode | p50 | p95 | p99 |
|---:|---|---|---:|---:|---:|
| 1,000 | all / 1,000 | exact | 3.35 | 6.17 | 16.1 |
| 1,000 | all / 1,000 | fuzzy | 5.26 | 6.89 | 12.3 |
| 1,000 | all / 1,000 | regex | 7.25 | 10.9 | 14.2 |
| 1,000 | 3 scopes / 300 | exact | 1.71 | 2.85 | 8.67 |
| 1,000 | 3 scopes / 300 | fuzzy | 2.93 | 7.12 | 11.1 |
| 1,000 | 3 scopes / 300 | regex | 2.50 | 4.53 | 5.94 |
| 10,000 | all / 10,000 | exact | 46.5 | 76.7 | 91.1 |
| 10,000 | all / 10,000 | fuzzy | 81.7 | 108 | 129 |
| 10,000 | all / 10,000 | regex | 79.7 | 109 | 132 |
| 10,000 | 3 scopes / 3,000 | exact | 22.0 | 29.5 | 35.1 |
| 10,000 | 3 scopes / 3,000 | fuzzy | 19.5 | 31.1 | 37.4 |
| 10,000 | 3 scopes / 3,000 | regex | 20.0 | 30.4 | 43.2 |
| 50,000 | all / 50,000 | exact | 383 | 434 | 482 |
| 50,000 | all / 50,000 | fuzzy | 424 | 485 | 505 |
| 50,000 | all / 50,000 | regex | 379 | 419 | 477 |
| 50,000 | 3 scopes / 15,000 | exact | 110 | 130 | 147 |
| 50,000 | 3 scopes / 15,000 | fuzzy | 119 | 142 | 159 |
| 50,000 | 3 scopes / 15,000 | regex | 117 | 143 | 149 |

The GUI waits for a 220 ms debounce after ordinary query typing. A rough, non-rendering estimate of perceived fuzzy-result arrival is therefore debounce plus backend: approximately **225 ms at 1,000**, **302 ms at 10,000**, and **644 ms at 50,000** records for all-scope p50. Scope filtering reduces the last estimate to about **339 ms**. These sums are an inference from the measured backend and the configured timer, not a GUI frame-time measurement. Scope changes and other explicitly immediate refresh paths do not necessarily pay the typing debounce.

No defensible automated GUI cold-start measurement was made: the production GUI has no benchmark-only profile argument, and redirecting it to a synthetic profile would either touch user state or change the shipped startup path. The deterministic screenshot harness measures correctness, not interactive startup performance.

### 3.4 Runtime resource footprint

`WorkingSet64`, `PrivateMemorySize64`, and processor time were sampled from fresh daemon child processes after `Process.Refresh()`. Idle values summarize 10 runs; loaded values summarize 5 runs of continuous serial, all-scope fuzzy search over 10,000 records.

| Daemon state | Working set (median of run medians) | Private bytes | CPU | Throughput |
|---|---:|---:|---:|---:|
| Storage-ready, idle | 45.81 MiB | 12.49 MiB | effectively idle | n/a |
| Sustained 10k fuzzy search | 83.54 MiB | 46.45 MiB | 95.83% of one logical core (4.79% of this 20-thread host) | 12.69 searches/s median |

Four loaded runs produced 12.42–12.89 searches/s; one noisy workstation run fell to 7.46 searches/s. Loaded samples were taken after completed requests, so they can miss shorter intra-request memory peaks. The workload shows one continuously active client, not parallel scalability. GUI resources remain deliberately unmeasured because the shipped GUI lacks an isolated profile-root seam and launching it would touch the real profile. Raw runs are in [`resource-footprint.json`](performance-data/2026-09-15/resource-footprint.json).

## 4. Bottleneck evidence

Controlled in-process phases separated SQLite metadata loading from fuzzy matching while retaining the production implementations:

| Records / selection | Metadata load p50 / p95 ms | Fuzzy matcher p50 / p95 ms |
|---|---:|---:|
| 1,000 / all | 4.75 / 5.83 | 1.20 / 2.83 |
| 10,000 / all | 63.3 / 82.3 | 2.56 / 12.6 |
| 50,000 / all | 369 / 480 | 12.3 / 34.4 |
| 50,000 / 3 scopes | 107 / 129 | 3.50 / 14.2 |

A 15-second EventPipe sample profile over repeated 50,000-record all-scope fuzzy searches supports the same causal interpretation. The trace's largest non-idle exclusive frames were `SqliteDataReader.NextResult` (24.6% of all samples), `GetString` (4.86%), `Read` (4.14%), and native `sqlite3_step` (2.07%). `RecordSearch.SearchFuzzy` was only 0.70% exclusive. `Gen2GcCallback.Finalize` accounted for 5.37%; this suggests, but does not prove, allocation pressure from materializing complete metadata rows because the sample did not measure allocations directly. `WaitHandle` represented 47.2% and is not productive search CPU. The 50,000-record database was 12.9 MiB, so raw disk capacity is not the explanation; row conversion and object materialization dominate.

**Observed:** latency scales approximately with the number of selected candidates, and exact search is also linear because the daemon loads all selected metadata before matching. **Inferred mechanism:** the current data path parses `presentation`, `revision`, and two timestamps for every candidate even though only the top results need complete summaries. **Falsifier:** a future profile showing matcher or SQLite stepping dominant after a projection change would revise this diagnosis.

## 5. Storage A/B decisions

### 5.1 Remove shared-cache mode under WAL

The previous connection string explicitly selected `Cache=Shared` while initialization selected WAL. Microsoft.Data.Sqlite explicitly advises removing `Cache=Shared` with WAL, and SQLite describes shared-cache as an obsolete feature whose locking semantics are usually better served by WAL. The production connection now uses SQLite's default private cache while retaining pooling and WAL.

A focused A/B used the same 50,000-record database, identical binaries except for the cache flag, 15 warm-ups, and 120 samples per selection:

| Metadata query | Shared p50 / mean / p95 ms | Default-private p50 / mean / p95 ms |
|---|---:|---:|
| All 50,000 | 388.6 / 388.9 / 412.4 | 381.7 / 385.5 / 433.4 |
| Three scopes / 15,000 | 114.0 / 115.1 / 126.3 | 115.2 / 114.3 / 129.0 |

The means differ by less than 1%; mixed percentile movement is noise-sized. Thus the change is justified by simpler, documented locking semantics and absence of a reliable regression—not by a claimed speed-up.

References:

- [Microsoft.Data.Sqlite connection strings](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/connection-strings)
- [SQLite shared-cache mode](https://www.sqlite.org/sharedcache.html)

### 5.2 Keep `records_by_scope` for now

Schema v1 contains both `UNIQUE(scope_id, key)` and `records_by_scope(scope_id)`. With `records_by_scope` removed, `EXPLAIN QUERY PLAN` showed that selected-scope search, ordered scope listing, and scope count fall back to the implicit unique `(scope_id, key)` index through its leftmost prefix. With the schema unchanged, SQLite selects the narrower `records_by_scope` index, including as a covering index for `COUNT(*) WHERE scope_id = ?`.

Two runs per condition on cloned 50,000-record databases compared keeping versus dropping the index. Median set was 1.39–1.40 ms with it and 1.38–1.39 ms without it; delete was 1.35–1.37 ms with it and 1.35–1.39 ms without it; scope count was 0.09–0.10 ms in both conditions. There is no demonstrated material win, while removing it would change schema validation and discard the count-specific covering index. It remains in place. This is a measured rejection of speculative cleanup, not proof that the index is universally optimal.

## 6. Recommendations and thresholds

1. **Ship the current path for personal use.** Sub-millisecond persistent IPC and low-single-digit-millisecond CRUD leave ample headroom. Do not add a cache layer or background index for ordinary profiles.
2. **Encourage scope selection for large profiles.** It is not merely organizational UI: reducing the candidate set produced a roughly proportional search improvement.
3. **If real profiles approach 20,000–50,000 records, change the projection before changing the search algorithm.** Load only `(scope, key)` for candidate generation, rank, then fetch full summary metadata for at most the global result limit. Exact search should use an indexed SQL equality lookup. This preserves value isolation and the existing protocol while eliminating the measured dominant work.
4. **Re-measure before adopting FTS, trigram indexes, or a search service.** Those mechanisms add schema, migration, ranking, and Unicode semantics. Current evidence points to over-projection, not an inadequate fuzzy scorer.
5. **Add cross-platform measurements only on real target hosts.** DPAPI results say nothing about macOS Keychain or Linux Secret Service startup. A virtualized or substituted provider must remain labeled as such.

Suggested provisional regression gates for the same class of Windows/NVMe host are: persistent ping p95 below 1 ms; encrypted get p95 below 2 ms; set/delete p95 below 10 ms; all-scope fuzzy p95 below 150 ms at 10,000 records. They are engineering tripwires, not user-facing service-level objectives, and should be normalized or replaced when CI gains stable performance runners.
