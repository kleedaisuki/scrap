# Issue 24: Linux IPC and degraded key-provider lifecycle

Status: implemented architecture decision for [GitHub issue #24](https://github.com/kleedaisuki/scrap/issues/24).

## Problem statement

The externally visible failure is not, by itself, evidence that a missing Secret Service terminates the daemon. The release already catches master-key initialization failures in `DaemonInitializationService`, stores a safe `key_unavailable` result in `DaemonInitializationState`, and lets `daemon.ping`, `daemon.version`, and `daemon.shutdown` bypass the store-initialization barrier.

The reproduced failure has two independent platform conditions:

1. the Linux master-key provider is unavailable, so the data plane must enter a stable degraded state; and
2. the profile home is on a filesystem that cannot host Unix-domain sockets, so the control-plane listener cannot bind.

The second condition makes the first one unobservable. The client never reaches the dispatcher and therefore reports a connection/protocol failure rather than the cached `key_unavailable` response.

## Evidence and causal chain

### Repository observations

- Before this change, `IpcEndpointDescriptor.CreatePipeName` placed the Unix socket below `ScrapPathLayout.RunDirectory`, which is below `~/.scrap` for the default profile.
- `NamedPipeConnectionAcceptor.AcceptAsync` creates the `NamedPipeServerStream`; on Unix, construction performs the actual Unix-domain-socket bind before `WaitForConnectionAsync`.
- `DaemonServerService.ExecuteAsync` logs `IpcListenerStarted` **before** its first call to `AcceptAsync`. The message therefore means that the accept loop was scheduled, not that an endpoint was bound and reachable.
- A non-cancellation exception from the initial bind/accept escapes the `BackgroundService`. The Generic Host's default background-service failure behavior stops the application; the server's `finally` block then logs `IpcListenerStopped`.
- `DaemonInitializationService.StartAsync` catches provider failures and settles `DaemonInitializationState`; it does not request host shutdown.
- `DaemonRequestDispatcher` bypasses the initialization barrier for daemon control methods and converts the cached failure to a structured protocol response for store-backed methods.

### Reproduction observations

The failure was reproduced on WSL Ubuntu 24.04 with the self-contained Linux payload, an unavailable D-Bus endpoint, and `HOME` rooted on `/mnt/d`. Every daemon instance logged the sequence:

```text
Scrap daemon IPC listener started.
Daemon initialization failed with key_unavailable (...).
Scrap daemon IPC listener stopped.
```

The CLI timed out and repeatedly attempted to launch a daemon. Independently binding a Python `AF_UNIX` socket on `/mnt/d` hung and then failed with `errno 95` (`Operation not supported`). This isolates listener bind failure from key-provider failure.

### Root-cause model

```text
HOME on a filesystem without AF_UNIX support
  -> endpoint is derived from ~/.scrap/run
  -> NamedPipeServerStream cannot bind its Unix-domain socket
  -> accept-loop BackgroundService faults
  -> Generic Host stops
  -> no IPC request reaches DaemonRequestDispatcher
  -> cached key_unavailable cannot be returned
  -> client retries/relaunches and finally reports a generic connection/protocol error
```

The log ordering is concurrent and misleading: `IpcListenerStarted` precedes the operation that establishes reachability, while key initialization can settle on another hosted-service path before the bind exception stops the host. The sequence does **not** show that `key_unavailable` caused shutdown.

## External platform constraints

- [.NET implements named pipes with Unix-domain sockets on Linux](https://github.com/dotnet/docs/blob/main/docs/standard/io/how-to-use-named-pipes-for-network-interprocess-communication.md#how-to-use-named-pipes-for-network-interprocess-communication). A rooted Unix pipe name is consequently a filesystem capability decision, not merely a naming decision.
- The [XDG Base Directory Specification](https://specifications.freedesktop.org/basedir/latest/) defines `XDG_RUNTIME_DIR` specifically for runtime objects including sockets. It requires the directory to be user-owned, mode `0700`, local, and capable of `AF_UNIX`, locking, and the other normal Unix filesystem operations. When it is unset, applications should use a replacement with similar capabilities.
- In [.NET 10, all of `BackgroundService.ExecuteAsync` runs as a background task](https://learn.microsoft.com/en-us/dotnet/core/compatibility/extensions/10.0/backgroundservice-executeasync-task). Registration order and a log at the top of `ExecuteAsync` are therefore not listener-readiness barriers. Code that must participate in host startup belongs in `StartAsync`, `IHostedLifecycleService`, or a separate `IHostedService`.

## Chosen architecture

### 1. Separate persistent profile state from host-local runtime IPC

Keep the database, configuration, logs, key reference, and per-profile ownership lease in `ScrapPathLayout`. Resolve the Unix IPC root independently:

1. On Windows, retain the existing `LOCAL\\scrap-<hash>` name.
2. On Unix, use an absolute, valid `XDG_RUNTIME_DIR` and create a private application subdirectory below it.
3. When `XDG_RUNTIME_DIR` is unset, empty, or relative, use a stable user-specific directory below `/tmp`, for example `/tmp/scrap-runtime-<effective-uid>`, with mode `0700`.
4. Keep the socket filename derived from both the stable current-user identity and the canonical profile root. This preserves separation between multiple Scrap profiles while keeping the socket path short and independent of `HOME` length or filesystem capabilities.

The runtime-directory resolver must be shared by client and daemon and must have no daemon-only state. A client launched daemon inherits the same environment; within a conforming user session, all processes see the same `XDG_RUNTIME_DIR`. A relative XDG path is invalid by specification and must be ignored rather than interpreted relative to an arbitrary working directory.

The application subdirectory is part of the current-user isolation contract: it must be a real directory, owned by the effective user, and mode `0700`. An absolute but unusable configured runtime directory should produce a precise endpoint-initialization failure rather than silently moving to a different namespace; fallback is for a missing/invalid variable, not for hiding a broken session configuration.

### 2. Make bind completion the listener readiness boundary

`IConnectionAcceptor` should distinguish endpoint activation from accepting connections. A suitable ownership model is:

```text
StartAsync / Bind
  creates and owns the first bound server stream
  returns only after bind and permission checks succeed

AcceptAsync
  waits on the already-bound stream
  transfers the connected stream to the server
  establishes the next listening instance before reporting readiness for another client

StopAsync / DisposeAsync
  closes the pending listener so accept cancellation is deterministic
```

`DaemonServerService.StartAsync` should await/bound this activation step before returning and should emit `IpcListenerStarted` only afterward. This converts Generic Host startup completion into a truthful readiness barrier. The accept loop remains background work.

Initial bind failure is a host-start failure, not a degraded key-provider state. Record a sanitized event with a stable condition (for example, endpoint bind unsupported/unavailable) and exception type, then let `DaemonApplication` return its startup failure code. Do not include IPC payloads or key material. After a successful bind, an unexpected accept failure should also be explicitly logged before controlled host shutdown; otherwise the current logging filter can reduce the diagnosis to a misleading `started -> stopped` pair. Avoid an unbounded retry loop that spins on deterministic errors such as `EOPNOTSUPP`.

### 3. Preserve key initialization as a non-fatal data-plane state

`DaemonInitializationState` is the synchronization boundary between initialization and dispatch. It should have exactly one terminal outcome:

```text
Pending -> Ready
Pending -> Failed(errorCode, safeMessage)
```

The outcome is immutable and cached for the daemon lifetime. Store-backed requests wait while the state is `Pending`, proceed only in `Ready`, and receive the same structured error in `Failed`. `daemon.version`, `daemon.ping`, and `daemon.shutdown` never wait for it.

Prefer representing the terminal outcome as one immutable value completed through a `TaskCompletionSource<InitializationOutcome>` rather than independently mutable `initialized` and `failure` fields. That makes contradictory terminal states unrepresentable and gives waiting requests one atomic snapshot. A repeated attempt to settle the state differently is a programming error; initialization is owned by exactly one service.

Provider unavailability must not call `StopApplication` and must not cancel the listener. This is the central degraded-mode contract:

| Plane | Initialization pending | Initialization failed | Host stopping |
|---|---|---|---|
| `daemon.version` / `daemon.ping` | available | available | reject new work with stable shutdown semantics |
| `daemon.shutdown` | available | available | idempotent completion/connection close |
| Store-backed methods | wait with request cancellation | cached structured error | no new admission |

### 4. Define idle timeout and shutdown against reachability

Idle time must begin from a real lifecycle event, not object construction. The recommended clock boundary is successful listener activation. If initialization is allowed to take longer than the idle window, treat the initialization attempt as activity (or reset activity on settlement) so the daemon does not bind, finish initialization, and immediately disappear without giving a client a full idle window in which to observe the outcome.

The following invariants should remain true:

- A bound listener plus `Pending` initialization is reachable for control requests.
- A failed initialization leaves the daemon reachable until explicit shutdown or one complete configured idle window.
- Connection admission and transition to `Stopping` are atomic with respect to each other.
- Explicit shutdown bypasses initialization, writes its response, stops accepting new connections, cancels idle reads, and drains already-dispatched mutations before disposing storage or releasing the profile lease.
- A listener that never bound is not reported as started and is not governed by idle timeout.

## Files and ownership boundaries

| File/component | Required responsibility |
|---|---|
| `src/Scrap.Platform/Ipc/IpcEndpointDescriptor.cs` | Resolve Windows pipe names versus Unix runtime socket paths; keep profile/user hashing; create client/server streams. |
| `src/Scrap.Platform/Ipc/CurrentUserIdentity.cs` | Supply stable SID/effective-UID identity used for isolation and fallback naming. |
| `src/Scrap.Platform/Paths/PlatformPathPermissions.cs` | Establish/validate private runtime application directory semantics without weakening existing profile permissions. |
| `src/Scrap.Daemon/IConnectionAcceptor.cs` | Express activation, accept, and disposal ownership rather than hiding bind inside the first accept. |
| `src/Scrap.Daemon/NamedPipeConnectionAcceptor.cs` | Own the pending bound server stream and transfer only connected streams. |
| `src/Scrap.Daemon/DaemonServerService.cs` | Make bind part of `StartAsync`, log truthful readiness/failures, run accept loop, and drain admitted handlers on stop. |
| `src/Scrap.Daemon/DaemonInitializationState.cs` | Publish one immutable terminal initialization outcome. |
| `src/Scrap.Daemon/DaemonInitializationService.cs` | Map provider/store/crypto failures to safe cached outcomes without stopping the host. |
| `src/Scrap.Daemon/IdleShutdownService.cs` and `DaemonActivityTracker.cs` | Start/reset the idle window at defined lifecycle boundaries and keep stop/admission atomic. |
| `src/Scrap.Daemon/DaemonHost.cs` | Preserve service ownership and order while relying on an explicit bind barrier, not `BackgroundService.ExecuteAsync` scheduling. |
| `src/Scrap.Client/DaemonConnectionFactory.cs` | Continue retrying genuine startup races; do not reinterpret a structured `key_unavailable` as a connection failure. |

No protocol version change is required: `key_unavailable`, control methods, and exit-code mapping already exist. The endpoint path is private runtime implementation, but release/installer flows should stop a running old daemon during upgrade if an immediate cross-version handoff is required; otherwise its old profile lease can temporarily prevent the new endpoint owner from starting.

## Rejected alternatives

| Alternative | Why rejected |
|---|---|
| Catch the listener exception and keep the host alive | Produces a zombie daemon that owns the profile lease but has no control plane. |
| Fix only the failing key provider | The provider failure is already cached correctly; it cannot repair an unreachable transport. |
| Keep the socket below `HOME` and probe/fallback after bind failure | Runtime sockets do not belong in persistent profile storage, and reactive probing complicates cleanup and creates multiple possible namespaces. |
| Use TCP loopback instead of Unix-domain sockets | Adds port allocation, discovery, and peer-authentication concerns without solving a requirement that XDG runtime directories already address. |
| Treat hosted-service registration order as readiness | .NET 10 explicitly schedules all `BackgroundService.ExecuteAsync` work in the background; order does not prove bind completion. |
| Retry every bind/accept exception forever | Deterministic filesystem capability errors never recover and would create CPU/log churn while retaining the singleton lease. |

## Verification matrix

| Layer | Scenario | Required assertion |
|---|---|---|
| Unit: endpoint resolution | Windows | Existing `LOCAL\\scrap-<hash>` contract is unchanged. |
| Unit: endpoint resolution | Absolute `XDG_RUNTIME_DIR` | Socket is under the private Scrap runtime subdirectory, rooted, short, and not under the profile home. |
| Unit: endpoint resolution | XDG unset, empty, or relative | Resolver uses the deterministic `/tmp` + effective-UID fallback. |
| Unit: endpoint resolution | Same user/profile in client and daemon | Both resolve exactly the same endpoint. |
| Unit: endpoint resolution | Two profiles for one user | Socket names differ; neither depends on profile path length except through its hash. |
| Unit: lifecycle | Initial bind succeeds | `StartAsync` does not complete and `IpcListenerStarted` is not emitted before the bound-listener seam reports success. |
| Unit: lifecycle | Initial bind fails with a platform/IO error | Host startup fails, a sanitized bind event is recorded, and `IpcListenerStarted` is absent. |
| Unit: lifecycle | Initialization transitions | Only `Pending -> Ready` or `Pending -> Failed` is possible; waiters see one stable outcome. |
| Integration: real host/IPC/client | Deliberately failing `IMasterKeyProvider` | Version and ping succeed; a store request receives `key_unavailable`; host remains alive; explicit shutdown succeeds. |
| Integration: idle timeout | Failed provider plus short timeout | Daemon remains reachable for a complete idle window after failure, then stops; a control request extends the window. |
| Integration: Linux filesystem | `HOME`/profile on a mount without `AF_UNIX`, valid XDG runtime dir | Real bind and client negotiation succeed because the socket is outside `HOME`. |
| Integration: Linux fallback | Same unsupported home, XDG unset | Real bind succeeds under the private `/tmp` fallback. |
| Integration: bad runtime path | Absolute XDG path on a filesystem without `AF_UNIX` | Startup fails promptly and logs a safe bind diagnosis; it never logs listener readiness. |
| CLI process test | Failing provider through real host and transport | `daemon version` and `daemon ping` exit `0`; store command exits `6`; diagnostics contain no secrets. |
| Packaged Linux smoke | No Secret Service, normal home | Control commands succeed, store command returns exit `6`, and shutdown succeeds. |
| Packaged Linux smoke | Home on a non-UDS mount | Same behavior, proving artifact, runtime endpoint resolution, host lifecycle, and CLI mapping together. |

Tests that inject only a failing provider while placing the profile under `/tmp` verify degraded initialization but do not regress the filesystem root cause. Conversely, endpoint-name unit tests do not prove that the selected filesystem can perform a real bind. Both intersections are required.

## Implementation order

1. Introduce and unit-test the shared Unix runtime-directory/endpoint resolver.
2. Move acceptor binding into an explicit activation phase and make listener logs reflect it.
3. Add the failing-provider real-host integration test and the unsupported-home Linux bind test.
4. Tighten initialization outcome and idle-window semantics if tests expose contradictory settlement or premature timeout.
5. Add packaged CLI smoke coverage for the complete failure path.

This ordering restores control-plane reachability first, then proves that the existing structured degraded state is observable end to end.
