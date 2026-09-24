# Security and validation simplification audit

Date: 2026-09-24. Scope: local code inspection of `Scrap.Protocol`, `Scrap.Daemon`, `Scrap.Storage.Sqlite`, and `Scrap.Platform` on the working tree. This is an architectural review, not a penetration test; no production code or tests were changed.

## Decision frame

The accepted product threat model (`docs/scrap-design.md`, §2.4) trusts the current OS account, excludes resistance to malicious code already running as that account, and protects database disclosure to other accounts. The compatibility constraint is unchanged wire behavior, error codes, persisted data, and UI. A check is not redundant merely because another layer checks a related condition: DTO/wire validation, domain errors, and SQLite transaction invariants have different callers and error contracts.

## Prioritized actionable findings

| Priority | Location | Evidence and impact | Recommendation and test |
|---|---|---|---|
| P3, high confidence, cleanup | `src/Scrap.Daemon/DaemonRequestDispatcher.cs:221-230` | `ValidateAndRun<TParams,TResult>` has no call sites (`rg ValidateAndRun src tests` finds only its definition). It does not defend any executed path and adds a misleading second pattern next to `ValidateAndReturn`. | Delete the unused helper, preserving `Deserialize` and `ValidateAndReturn`. Compile and run dispatcher tests. No wire impact. |
| P3, high confidence, narrow cleanup | `src/Scrap.Daemon/DaemonRequestDispatcher.cs:231-240` | `Failure` invokes `SafeRequestId`, but its only call is the protocol-version branch at line 114, after `request.EnsureValid()` has rejected empty request IDs. The catch path at line 150 legitimately needs its own `SafeRequestId`, since it handles invalid envelopes. | If simplifying this helper, pass the validated ID through without a second fallback; keep the catch-path fallback. Test unsupported-version response ID and malformed-envelope fallback. This is a tiny duplication, not a security flaw. |

## Non-findings: checks to retain

1. `ScrapClient.ExchangeAsync` calls `response.EnsureValid()` at `src/Scrap.Client/ScrapClient.cs:650`, and `ProtocolResponse.GetResult` repeats it at `src/Scrap.Protocol/Envelopes.cs:165`. This is a real duplicate *per exchange*, but the client must validate the envelope **before** trusting request ID or version, whereas the public `GetResult` method must validate when called independently. Removing one check without a new validated-response API changes malformed-response precedence or weakens the public API. The optimization is not worth new machinery.
2. Domain name validation in `SqliteDaemonOperations` and `SqliteStore.ValidateName` is not the same contract. The daemon maps malformed wire input to `invalid_params`; the independently public storage API rejects null/empty names, and transactional checks remain necessary. In particular, `SqliteStore.CheckPreparationConcurrency` at lines 1450-1469 avoids expensive AEAD work, while `EnsurePreparationStillCurrent` at lines 1499-1517 protects the commit against concurrent updates. Do not remove either as a supposed duplicate.
3. The Unix socket `File.SetUnixFileMode(..., 0600)` at `IpcEndpointDescriptor.cs:183` is **not** redundant with `PipeOptions.CurrentUserOnly` on the pinned .NET 10 target (`Directory.Build.props`, `global.json`). Microsoft documents that the Unix socket-file `0600` behavior is only introduced in .NET 11 Preview 4; before that, `CurrentUserOnly` rejects cross-user connections at connect time but does not necessarily tighten the socket inode mode. The private parent directory also protects path traversal. Keep both until the minimum runtime changes and compatibility is tested. [Microsoft .NET breaking-change note](https://learn.microsoft.com/en-us/dotnet/core/compatibility/core-libraries/11/namedpipeserverstream-unix-permissions).
4. The link/reparse-point checks in `PlatformPathPermissions.cs:82-93` should not be removed solely because same-user malicious code is out of scope. They also prevent accidental redirection of profile files and preserve an explicit path contract. Conversely, they are not a comprehensive race-free defense against same-user attackers: `RejectLinkIfPresent` checks a path before opening it. Avoid describing them as such. Reconsider only with an explicit policy for linked profile roots and a migration/compatibility test.
5. `EnsureStoreFilesPrivate` at `SqliteDaemonOperations.cs:424-436` runs only at initialization and cannot alone guarantee mode on future SQLite WAL/SHM files. SQLite can create those sidecars as needed; the private data directory is the durable boundary. This does not justify removing the existing tightening of pre-existing files, especially for older or user-moved profiles. [SQLite WAL documentation](https://www.sqlite.org/wal.html). If a future change claims guaranteed per-file modes, test actual SQLite sidecar lifecycle rather than inferring it from this function.

## Verification checklist for a production cleanup

- `dotnet test tests/Scrap.Daemon.Tests/Scrap.Daemon.Tests.csproj` (dispatcher response/error contracts).
- `dotnet test tests/Scrap.Protocol.Tests/Scrap.Protocol.Tests.csproj` (envelope validity and version behavior).
- For any path-permission change, run OS-specific integration tests on Windows, Linux, and macOS; include a restrictive existing profile, a deliberately linked profile path, and SQLite WAL creation/recreation. Do not infer cross-platform behavior from a Windows-only run.
- Preserve malformed-envelope `invalid_request`, malformed method DTO `invalid_params`, unsupported protocol version, and unchanged request-ID echo behavior over real IPC.

## Evidence status

Observed: call-site search and control flow described above. Inferred: benefit of removing the dead helper and unnecessary fallback is maintenance clarity, not measurable performance or increased security. The socket inode-mode distinction is documented for the runtime version boundary; actual packaged-app behavior was not measured in this audit.
