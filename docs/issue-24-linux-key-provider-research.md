# Issue #24 research: keeping the Linux control plane alive when Secret Service is unavailable

## Scope and decision question

Issue [#24](https://github.com/kleedaisuki/scrap/issues/24) reports a packaged Linux daemon that logged a `key_unavailable` initialization failure and then stopped its IPC listener. The product contract is stronger: `daemon ping` and `daemon version` must remain available, while store-backed requests receive the cached structured failure.

This note answers three implementation questions:

1. What does .NET 10 actually guarantee about `Generic Host`, `IHostedService`, and `BackgroundService` startup and failure handling?
2. What lifecycle invariants are required for the Unix-domain socket that backs `NamedPipeServerStream` on Linux?
3. Which Secret Service/libsecret failure classes can be distinguished without parsing human-readable error text, and how should they be tested?

The conclusions below are based only on versioned upstream source, official project documentation, and protocol/OS specifications. No directly relevant peer-reviewed study was found; this is primarily a framework-contract and operating-system-lifecycle question, for which the versioned implementation is the strongest evidence.

### Evidence-strength scale

| Grade | Meaning |
|---|---|
| **A — normative/direct** | A protocol or OS specification, or version-tagged upstream source that directly determines behavior. |
| **B — official guidance** | Maintainer documentation describing supported behavior or recommended use. |
| **C — upstream practice** | A main project's own tests/examples; useful engineering precedent, but not a public compatibility contract. |

## Executive judgment

A new WSL reproduction materially narrows the immediate cause: with `HOME` on `/mnt/d`, a minimal Python `AF_UNIX` bind at that location fails consistently with `errno 95` (`EOPNOTSUPP`). The pre-fix implementation derived its socket under `~/.scrap/run`, so the listener worker faulted during its first bind. The default Generic Host policy then stopped the host. The concurrent libsecret failure was real, but it was not the reason that `ping` and `version` became unreachable in this reproduction.

The control-plane contract should therefore be represented as two independent state machines, not as one all-or-nothing startup step:

```text
IPC:          unbound -> bound/listening -> draining -> stopped
Store init:   pending -> ready
                      \-> failed(safe category)
```

`ping`, `version`, and shutdown depend only on the IPC state. Business methods depend on both IPC and terminal store initialization. A provider failure is therefore a **data-plane degradation**, not a host-fatal listener failure.

For this repository's .NET 10 target, registering `DaemonServerService` before `DaemonInitializationService` is **not a bind-order guarantee**. `BackgroundService.StartAsync` schedules the whole `ExecuteAsync` with `Task.Run` and returns immediately. The next hosted service may start before the server worker has executed even one instruction. Make socket bind an awaited startup action and expose a real bind-ready invariant; do not infer readiness from registration order or a log line.

On Unix, place the endpoint in a runtime directory, not under the persistent profile/home directory. Prefer `$XDG_RUNTIME_DIR/scrap`; when `XDG_RUNTIME_DIR` is absent or invalid, use a stable private `0700` directory on the local temporary filesystem, such as `/tmp/scrap-<uid-or-stable-identity-hash>`, and emit a safe warning. Never fall back to `HOME` for the socket.

Keep `HostOptions.BackgroundServiceExceptionBehavior` at its default `StopHost`. Changing it to `Ignore` would not restart a dead listener; it would turn a visible failure into a live process with no control plane. Catch expected provider failures at the provider-initialization boundary instead.

## 1. .NET 10 Generic Host and `BackgroundService`

### Directly established behavior

| Claim | Evidence | Strength | Consequence for Scrap |
|---|---|---:|---|
| In .NET 10, **all** of `BackgroundService.ExecuteAsync` runs as a background task. Its synchronous prefix no longer delays later services. | [.NET 10 compatibility note](https://learn.microsoft.com/en-us/dotnet/core/compatibility/extensions/10.0/backgroundservice-executeasync-task); [`BackgroundService.cs` at `v10.0.0`, lines 36–48](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/Microsoft.Extensions.Hosting.Abstractions/src/BackgroundService.cs#L36-L48) | A/B | Code in `DaemonServerService.ExecuteAsync`, including logging and the first `CreateServerStream`, is not a startup barrier. |
| Hosted services start sequentially by default, but the host awaits each service's `StartAsync`, not the completion of a `BackgroundService` body. | [`HostOptions.cs` at `v10.0.0`](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/Microsoft.Extensions.Hosting/src/HostOptions.cs); [`Host.cs` startup sequence at `v10.0.0`](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/Microsoft.Extensions.Hosting/src/Internal/Host.cs#L57-L145) | A | `AddHostedService<Server>()` before `AddHostedService<Initialization>()` orders their `StartAsync` calls only. |
| `IHostedLifecycleService` supplies formal `StartingAsync` and `StartedAsync` phases around all `StartAsync` calls. | [`IHostedLifecycleService` API](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.hosting.ihostedlifecycleservice?view=net-10.0); [versioned interface source](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/Microsoft.Extensions.Hosting.Abstractions/src/IHostedLifecycleService.cs) | A/B | It can express "bind before any initializer starts", although overriding the server's `StartAsync` is simpler here. |
| An unhandled `BackgroundService` exception is logged and stops the host by default. `Ignore` does not restart that service. | [.NET 6 hosting exception change](https://learn.microsoft.com/en-us/dotnet/core/compatibility/core-libraries/6.0/hosting-exception-handling); [`BackgroundServiceExceptionBehavior.cs`](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/Microsoft.Extensions.Hosting/src/BackgroundServiceExceptionBehavior.cs); [`Host.TryExecuteBackgroundServiceAsync`](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/Microsoft.Extensions.Hosting/src/Internal/Host.cs#L174-L205) | A/B | Retain `StopHost`; expected key-provider failures must not escape the initialization boundary, while a genuinely dead listener should remain host-fatal. |
| A normally completed `BackgroundService` does not itself stop the host; short-lived workers must explicitly request shutdown. | [Worker Services: signal completion](https://learn.microsoft.com/en-us/dotnet/core/extensions/workers#signal-completion); [`HostingAbstractionsHostExtensions.RunAsync`](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/Microsoft.Extensions.Hosting.Abstractions/src/HostingAbstractionsHostExtensions.cs#L57-L104) | A/B | A caught initialization failure that returns normally is not, by itself, sufficient to explain host shutdown. |
| `StartAsync` cancellation means startup was aborted. | [`IHostedService.cs` at `v10.0.0`](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/Microsoft.Extensions.Hosting.Abstractions/src/IHostedService.cs) | A | If the startup token is cancelled, rethrow cancellation instead of caching it as `internal_error` and claiming successful degraded startup. |

### Repository-specific observation and inference

**Observed in the pre-fix tree:**

- `DaemonServerService.ExecuteAsync` logs `IpcListenerStarted` before `AcceptAsync` creates the first `NamedPipeServerStream`. The log means the worker body started; it does **not** prove that the socket has bound.
- `DaemonInitializationService.StartAsync` catches provider/storage exceptions, settles `DaemonInitializationState`, and returns normally.
- Framework logging is cleared and the configured filter admits only categories beginning with `Scrap.Daemon`. The Generic Host's own background-service fault log uses a `Microsoft.Extensions.Hosting...` category.

**Confirmed mechanism in the WSL reproduction:** `HOME` is on the Windows-mounted `/mnt/d` filesystem, the endpoint is consequently below `/mnt/d/.../.scrap/run`, and an independent Python `AF_UNIX` bind there returns `EOPNOTSUPP`. The same filesystem/path class is passed to `NamedPipeServerStream`, whose Linux implementation uses an AF_UNIX socket. That bind exception escapes the listener worker; the default `StopHost` path then initiates shutdown and the listener's `finally` logs `IpcListenerStopped`. This accounts for the observed listener lifecycle without making the libsecret initialization failure host-fatal.

The current category filter can still hide the framework's root-cause log. On machines other than this WSL reproduction, external cancellation and ordinary idle shutdown remain competing explanations; retain explicit stop-reason observability rather than generalizing one environment's cause to every Linux report.

### Recommended startup shape

1. **Bind synchronously in the server's awaited startup path.** The least-mechanism option is to override `DaemonServerService.StartAsync`, create/bind the first server stream, record `IpcListenerStarted` only after bind succeeds, then call and await `base.StartAsync`. Microsoft's .NET 10 migration guidance explicitly recommends an overridden `StartAsync` for work that must happen before later hosted services start.
2. Have `ExecuteAsync` consume the already-bound first listener and then continue the accept loop. `host.StartAsync()` returning should imply that a client can connect immediately; no retry should be required to compensate for a scheduling race.
3. Keep initialization as an independently settled result (`pending`, `ready`, or safe `failed` category). Control methods bypass it. Business methods await settlement and then either run or return the cached structured error.
4. Do not globally set `BackgroundServiceExceptionBehavior.Ignore`. Handle only explicitly recoverable accept errors inside the listener with bounded retry/rebind logic. Let an unrecoverable listener failure stop the host rather than leave a zombie daemon.
5. Admit the Generic Host lifecycle/error categories to the same private log, without payloads. Observability of the listener's exception type and host stop reason is necessary to distinguish transport failure, cancellation, and idle timeout.
6. Treat provider initialization timeout separately from host-start cancellation. A platform-provider deadline may settle the data plane as unavailable; a cancelled host startup must abort startup.

### Important hang boundary

The libsecret synchronous functions used by the current provider may block indefinitely, according to the official API documentation for both [`secret_password_lookupv_sync`](https://gnome.pages.gitlab.gnome.org/libsecret/func.password_lookupv_sync.html) and [`secret_password_storev_sync`](https://gnome.pages.gitlab.gnome.org/libsecret/func.password_storev_sync.html). The current P/Invoke passes a null `GCancellable`, so a .NET token is checked only before entering native code and cannot interrupt an in-flight call.

A managed `Task.WaitAsync(timeout)` alone does not stop that native operation. A robust deadline therefore eventually requires libsecret's asynchronous API or a real native `GCancellable`, plus a rule that late completion cannot overwrite a terminal timeout state. Until then, all real-provider test lanes need an outer **process** watchdog so CI cannot hang indefinitely.

## 2. Unix socket listener lifecycle

### Runtime-directory selection is part of socket correctness

The [XDG Base Directory Specification 0.8](https://specifications.freedesktop.org/basedir/0.8/) is unusually explicit about this use case (**A — normative/direct**):

- `$XDG_RUNTIME_DIR` is the base for user-specific runtime objects, expressly including sockets and named pipes;
- it must be absolute, owned by the user, mode `0700`, local rather than shared, and tied to the login lifetime;
- the filesystem must support AF_UNIX sockets, links, permissions, locking, memory mapping, and the other normal Unix facilities;
- if it is unset, applications should choose a replacement with similar capabilities and print a warning.

`HOME` has none of those runtime-filesystem guarantees. It may be NFS, a Windows DrvFS/9P mount, or another shared/restricted filesystem. Microsoft also recommends keeping Linux-tool files in the WSL Linux filesystem instead of mounted Windows paths ([WSL filesystem guidance](https://learn.microsoft.com/en-us/windows/wsl/filesystems)); the upstream WSL tracker contains the exact `/mnt/d` AF_UNIX bind reproduction ([microsoft/WSL #5961](https://github.com/microsoft/WSL/issues/5961)). The local Python reproduction is stronger evidence for this issue than the tracker report, while the XDG specification explains the portable design rule.

#### Recommended resolver

For Unix only:

1. If `XDG_RUNTIME_DIR` is non-empty, absolute, and usable, select `$XDG_RUNTIME_DIR/scrap` and create/validate the application subdirectory as private `0700`.
2. If it is missing, relative, not a directory, or demonstrably lacks the required capabilities, select a deterministic local fallback such as `/tmp/scrap-<uid-or-stable-identity-hash>` and record a safe warning in the private log. A deterministic path is required because independently launched client and daemon processes must derive the same endpoint; a per-process `mkdtemp` directory would break discovery.
3. Create-or-open the fallback without following a symlink; require current-user ownership and `0700` before use. The shared `/tmp` parent is acceptable only because the application uses its own verified private child.
4. Do not use `HOME`, `$XDG_CACHE_HOME`, the persistent profile root, or an arbitrary `TMPDIR` that might itself point back to DrvFS as the final fallback.
5. Derive the endpoint hash from the stable profile identity as today, but store the socket node in the runtime directory. Persistent database/config/log paths and ephemeral IPC paths should not share one base-directory policy.

This is consistent with production ecosystem practice. `pam_systemd` documents `$XDG_RUNTIME_DIR` as the local, user-private location for AF_UNIX sockets and similar runtime objects ([`pam_systemd(8)`](https://man7.org/linux/man-pages/man8/pam_systemd.8.html)), while GLib's [`g_get_user_runtime_dir`](https://docs.gtk.org/glib/func.get_user_runtime_dir.html) provides a fallback when XDG runtime state is unavailable. GLib falls back to its cache directory, but that precedent should not be copied blindly here: the XDG specification requires similar socket capabilities, and the WSL reproduction demonstrates why a home-derived fallback can be functionally wrong. A verified private `/tmp` directory better satisfies the actual socket contract.

The resolver should be covered by pure path-policy tests and Linux capability tests. Trusting a syntactically valid `XDG_RUNTIME_DIR` is standards-conforming; optionally probing AF_UNIX support provides better diagnostics for non-conforming environments, but the probe must use a unique temporary name and clean it up under the same private application directory.

### OS and runtime facts

| Claim | Evidence | Strength | Consequence for Scrap |
|---|---|---:|---|
| .NET named pipes use Unix-domain sockets on Linux. | [Microsoft named-pipe guidance](https://learn.microsoft.com/en-us/dotnet/standard/io/how-to-use-named-pipes-for-network-interprocess-communication) | B | Linux lifecycle and pathname semantics apply even though the public type is `NamedPipeServerStream`. |
| A pathname socket is a filesystem object; closing the socket is not the same as unlinking its pathname. The caller must unlink it. | Linux [`unix(7)`](https://man7.org/linux/man-pages/man7/unix.7.html) | A | Graceful shutdown and crash recovery must both account for the socket file. |
| Binding an existing pathname produces `EADDRINUSE`; AF_UNIX bind can also fail for missing directories, access denial, an overlong name, read-only filesystems, and other path errors. | Linux [`bind(2)`](https://man7.org/linux/man-pages/man2/bind.2.html) (POSIX.1-2024) | A | A bind-ready barrier must propagate real bind failure rather than emit a premature "started" event. |
| Linux `sockaddr_un.sun_path` is 108 bytes, while pathname details differ across implementations. | Linux [`unix(7)`](https://man7.org/linux/man-pages/man7/unix.7.html) | A | The repository's conservative UTF-8 byte-length check is appropriate for cross-platform operation. |
| Directory permissions govern pathname creation; on Linux, write permission on the socket node is also required for stream connection, but POSIX does not make socket-node permissions portable. | Linux [`unix(7)`](https://man7.org/linux/man-pages/man7/unix.7.html) | A | A private `0700` parent directory is the portable primary boundary; `0600` on the socket is useful Linux defense in depth. |
| In .NET 10's Unix implementation, server instances for one path share a listening socket. The last disposed instance unlinks the path. Unless `FirstPipeInstance` is set, creation first attempts to unlink an existing path before binding. | [`NamedPipeServerStream.Unix.cs` at `v10.0.0`](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.IO.Pipes/src/System/IO/Pipes/NamedPipeServerStream.Unix.cs#L175-L287) | A | Single-instance ownership must be acquired before constructing the first stream. Disposal of every accepted/waiting instance is part of path cleanup. |
| `PipeOptions.FirstPipeInstance` is available in .NET 10. | [`PipeOptions` API](https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.pipeoptions?view=net-10.0) | B | It can prevent the runtime's implicit unlink-before-bind, but then stale-path removal must become an explicit, lease-protected operation. |
| In .NET 10, `CurrentUserOnly` checks peer identity but does not itself guarantee `0600` at bind time; the permission behavior is tightened only in .NET 11. | [.NET 11 permission change](https://learn.microsoft.com/en-us/dotnet/core/compatibility/core-libraries/11/namedpipeserverstream-unix-permissions) | B | On net10.0, retain explicit `chmod(0600)` and the private parent directory. |

### Recommended invariants

1. **Ownership before pathname mutation:** acquire the per-profile daemon lease before the first stream construction or stale-path cleanup. Never let a losing contender unlink the winner's live pathname.
2. **Bound means connectable:** publish readiness/log `IpcListenerStarted` only after constructor/bind/listen and permission adjustment succeed.
3. **One owner of cleanup:** the component that owns the bound listener owns shutdown disposal and pathname cleanup. Do not scatter unconditional `File.Delete` calls across client startup and daemon shutdown.
4. **Shutdown order:** stop admitting connections, cancel the pending accept, drain already admitted handlers, dispose all server-stream instances, and only then release the singleton lease. The current registration order already benefits from Generic Host's reverse stop order.
5. **Private directory plus socket mode:** keep the run directory `0700`; set the socket to `0600` on .NET 10. Verify modes in an actual Linux test, not only through mocked paths.
6. **Stale recovery is not live-owner takeover:** after an abrupt death, the lease is released by the OS but the socket pathname may remain. A replacement may remove that stale node only after acquiring the lease. If adopting `FirstPipeInstance`, perform this cleanup deliberately and verify the target is the expected socket path before unlinking.

`FirstPipeInstance` is not automatically superior. It removes a surprising hidden unlink from runtime construction, but requires explicit stale-node recovery. The simplest acceptable design is either (a) keep the current runtime cleanup while strictly enforcing lease-before-construction, or (b) use `FirstPipeInstance` and centralize verified stale cleanup under the lease. Mixing both models creates more edge cases.

### Listener tests worth keeping permanently

- `host.StartAsync()` returns only after immediate client `ping` succeeds.
- Graceful shutdown cancels an outstanding accept, drains an admitted request, removes the socket pathname, and releases the lease.
- `SIGKILL` leaves whatever the platform leaves; the next process acquires the released lease, recovers the stale socket, and becomes connectable.
- A concurrent losing daemon cannot remove or replace the winner's socket.
- A listener failure after startup is observed and stops the host; it is not silently ignored.
- The socket path obeys the byte limit, parent mode is `0700`, and node mode is `0600` on Linux/net10.0.

## 3. Secret Service/libsecret failure classification

### Do not parse `GError.message`

GLib's `GError` has a machine-readable **domain** and **code**. The message is for diagnosis, may vary by implementation/localization, and libsecret explicitly says its errors are not appropriate for direct display to users. Classification should compare the error domain against the domain functions (`secret_error_get_quark`, `g_dbus_error_quark`, and `g_io_error_quark`) and then inspect the numeric enum code. Do not hard-code the runtime-assigned quark value and do not classify by English substrings.

Evidence:

- [GLib error-domain conventions](https://docs.gtk.org/glib/error-reporting.html) — **B**.
- [libsecret `Secret.Error`](https://gnome.pages.gitlab.gnome.org/libsecret/enum.Error.html) — **A/B**; it defines protocol, locked, missing-object, and other codes, and states that applications must handle them appropriately rather than display them.
- [libsecret migration/error guidance](https://gnome.pages.gitlab.gnome.org/libsecret/migrating-libgnome-keyring.html#errors-and-cancellation) — **B**; it maps service absence to D-Bus activation errors and cancellation to `G_IO_ERROR_CANCELLED`.
- [GIO `DBusError`](https://docs.gtk.org/gio/error.DBusError.html) and [`IOErrorEnum`](https://docs.gtk.org/gio/error.IOErrorEnum.html) — **A/B**.

### Proposed safe internal categories

These are diagnostic **subcodes** beneath the public `key_unavailable` contract. They should contain no item labels, attributes, bus addresses, filesystem paths, passwords, or raw native messages.

| Safe category | Primary machine-readable evidence | Semantics and handling |
|---|---|---|
| `native_library_missing` | Managed `DllNotFoundException` before a libsecret result exists | Required SONAME cannot be loaded. Stable degraded state; control plane remains available. |
| `native_abi_incompatible` | `EntryPointNotFoundException` or `BadImageFormatException` | A library was found but its ABI/architecture is incompatible. Keeping this separate makes packaging faults actionable. |
| `session_bus_unavailable` | `G_DBUS_ERROR_BAD_ADDRESS`, `NO_SERVER`, `DISCONNECTED`, or corresponding GIO connection failures while acquiring the session bus | No usable user-session bus. Stable for this daemon lifetime unless reconnect/retry is an explicit feature. |
| `secret_service_unavailable` | `G_DBUS_ERROR_SERVICE_UNKNOWN`, `NAME_HAS_NO_OWNER`, `SPAWN_SERVICE_NOT_FOUND`, or activation failure | A bus exists, but `org.freedesktop.secrets` cannot be owned/started. Keep distinct from "no bus" for actionable diagnostics. |
| `collection_locked` | `SECRET_ERROR_IS_LOCKED` / `org.freedesktop.Secret.Error.IsLocked` | The service exists but the required object is locked. May be recoverable after an interactive unlock; do not report it as a missing library. Caveat: high-level lookup may collapse an unsuccessful unlock to null/no-match, so expose this category only when the native result actually preserves it. |
| `prompt_dismissed` | Secret Service `Prompt.Completed(dismissed = true)` when using the lower-level API | User/client cancelled the prompt. The [Secret Service prompt specification](https://specifications.freedesktop.org/secret-service/latest/prompts.html) defines dismissal as cancellation. The simple password API does not reliably preserve this distinction, so do not promise or infer this subcode from text/null alone. |
| `cancelled` | `G_IO_ERROR_CANCELLED` with the operation's actual `GCancellable` cancelled | Expected during daemon shutdown or a provider deadline. Host-start cancellation should propagate rather than become cached degradation. |
| `timeout` | D-Bus/GIO timeout code, or an application deadline backed by real native cancellation | Avoid indefinite initialization/business-request waits. A managed timeout without native cancellation is incomplete. |
| `access_denied` | `G_DBUS_ERROR_ACCESS_DENIED`, `AUTH_FAILED`, or `G_IO_ERROR_PERMISSION_DENIED` | Bus/service exists, but policy or authentication refused access. |
| `protocol_error` | `SECRET_ERROR_PROTOCOL` or invalid remote data | Provider/service interoperability failure; log only safe category and exception type. |
| `object_missing` | `SECRET_ERROR_NO_SUCH_OBJECT` / Secret Service `NoSuchObject` | Specific referenced object vanished. This should not be confused with a successful password lookup returning null, which libsecret documents as "no match." |
| `unknown_provider_error` | Any unrecognized domain/code | Forward-compatible fallback. GLib explicitly advises treating future unrecognized codes as generic failure. |

The Secret Service standard directly defines [`IsLocked`, `NoSession`, and `NoSuchObject`](https://specifications.freedesktop.org/secret-service/latest/errors.html), and states that locked items/collections may not be read or modified until unlocked ([locking and unlocking](https://specifications.freedesktop.org/secret-service/latest/locking.html)). The D-Bus layer necessarily adds transport, activation, authentication, and timeout failures; those are not Secret Service protocol errors and should remain separate categories.

### Required native-boundary change for reliable classification

The current provider marshals `GError.Domain`, `Code`, and `Message`, but discards the first two and embeds the message in a managed exception. Preserve a normalized enum/category at the native boundary, before `g_error_free`. Comparing domain and code is reliable; transporting only the text upward is not.

For user-facing behavior, keep the existing coarse contract (`key_unavailable`, exit code 6). The subcode is for structured diagnostics/tests and may be exposed only if the protocol has an explicitly non-sensitive diagnostic field. Raw `GError.message` should remain out of CLI output and ordinary logs.

## 4. Reproducible test strategy

### Layer 1 — deterministic product integration test (every PR)

This is the most valuable missing test and should not depend on D-Bus:

```text
deliberately failing IMasterKeyProvider
  + production DaemonHost composition
  + real named-pipe / Unix-socket endpoint
  + real ScrapClient and CLI mapping
```

Assert all of the following in one test:

1. `host.StartAsync()` establishes a connectable endpoint.
2. `daemon ping` and `daemon version` work while initialization is pending and after it fails.
3. A business request receives structured `key_unavailable`; the CLI maps it to exit code 6.
4. The host has not requested `ApplicationStopping` merely because initialization failed.
5. Explicit shutdown works and cleans up the endpoint.

Add separate tests for startup-token cancellation and an injected accept-loop fault. The former must abort startup; the latter must be observed and stop the host under the default exception policy.

### Layer 2 — provider classifier tests (every PR)

Make the domain/code-to-safe-category mapping a small pure function behind a native adapter. Exercise every known code plus an unknown future domain/code. This is more deterministic than trying to coerce each error through a desktop service and makes forward-compatible fallback reviewable.

### Layer 3 — packaged negative Linux matrix (release smoke)

| Environment | Deterministic construction | Expected category/result |
|---|---|---|
| WSL mounted `HOME` | Set `HOME`/the persistent profile to `/mnt/d/...`, leave `XDG_RUNTIME_DIR` unset, and independently confirm AF_UNIX bind at the mounted path returns `EOPNOTSUPP`. | Endpoint resolves to the verified `/tmp` runtime fallback, not `HOME`; immediate ping/version and explicit shutdown succeed. |
| Valid XDG runtime directory | Create a local temporary directory with mode `0700`, export it as absolute `XDG_RUNTIME_DIR`, and keep `HOME` on the unsupported mounted filesystem. | Socket is below `$XDG_RUNTIME_DIR/scrap`; persistent profile data remains below the configured profile/home root. |
| No `libsecret-1.so.0` | Use a minimal container image that genuinely lacks the SONAME. Do not rely on `LD_LIBRARY_PATH` to hide a system library because the dynamic loader may still search default paths. | `native_library_missing`; ping/version remain usable; business request exits 6. |
| No session bus | In a headless container, set `DBUS_SESSION_BUS_ADDRESS` to a fresh nonexistent Unix path and ensure no desktop autolaunch environment. | `session_bus_unavailable`; same control-plane assertions. |
| Session bus, no Secret Service | Run the package under `dbus-run-session` in an image with no `org.freedesktop.secrets` activation provider. | `secret_service_unavailable`; same control-plane assertions. |
| Service disappears | Start against an isolated real service, prove one successful operation, terminate the service, then repeat. | Stable structured provider failure; daemon and control plane remain alive. |

The official [`dbus-run-session`](https://dbus.freedesktop.org/doc/dbus-run-session.1.html) manual explicitly recommends it for regression tests so they neither depend on nor interfere with the user's real session bus (**B**). Always wrap these tests in a process-level timeout because the current synchronous libsecret calls may block indefinitely.

### Layer 4 — isolated real Secret Service lane (nightly/release)

Use a disposable user, temporary `HOME`, `XDG_DATA_HOME`, `XDG_CONFIG_HOME`, `XDG_RUNTIME_DIR`, and `dbus-run-session`. Never touch the developer/runner's real keyring.

Start `gnome-keyring-daemon` with only the secrets component, a temporary control directory, and foreground supervision. This is not an invented harness: GNOME Keyring documents `--foreground`, `--control-directory`, `--components=secrets`, and `--unlock` in its [upstream daemon manual](https://github.com/GNOME/gnome-keyring/blob/f701fa0e3fa25e2789f29dd7b70eff45b1e2daf8/docs/gnome-keyring-daemon.xml), and its own [D-Bus service test](https://github.com/GNOME/gnome-keyring/blob/f701fa0e3fa25e2789f29dd7b70eff45b1e2daf8/daemon/dbus/test-service.c#L75-L82) launches the daemon in foreground with a temporary control directory and only `secrets` (**C**).

Cover:

- successful store/load using a freshly created unlocked collection;
- a locked collection using the standard `Service.Lock` operation, followed by a bounded provider request;
- service termination after successful initialization;
- prompt dismissal only if the harness can observe the standard `Prompt.Completed(dismissed=true)` signal without a graphical desktop.

Do not make prompt automation a blocking requirement for the initial fix. libsecret itself uses an internal mock service in its upstream tests and automatically wraps most tests with `dbus-run-session` ([upstream `meson.build`](https://github.com/GNOME/libsecret/blob/a5cd57f103038c06b64d5f6ebfd0e627bb40af4e/meson.build#L105-L116), [mock-service source](https://github.com/GNOME/libsecret/blob/a5cd57f103038c06b64d5f6ebfd0e627bb40af4e/libsecret/mock-service.c)), but that mock is a project-internal test facility, not a stable consumer API. Scrap should prefer its own provider fake for PR tests and a real isolated service for integration coverage.

### Test artifacts to retain

For reproducibility, record:

- container image digest and distribution;
- `dotnet --info`, package RID, and artifact SHA-256;
- `libsecret`, GLib/GIO, D-Bus, and Secret Service implementation versions;
- which environment variables were set (values may need path redaction);
- command exit codes and structured protocol responses;
- daemon lifecycle events and safe diagnostic categories;
- test timeout and whether the child was terminated by the watchdog.

## 5. Prioritized implementation guidance

| Priority | Change | Why |
|---:|---|---|
| P0 | Move Unix IPC from the persistent profile directory to `$XDG_RUNTIME_DIR/scrap`, with a verified private `/tmp/scrap-<uid-or-stable-identity-hash>` fallback and no `HOME` fallback. | Directly fixes the confirmed WSL `/mnt/d` `EOPNOTSUPP` bind failure and follows the XDG socket-location contract. |
| P0 | Add the failing-provider + real-host + real-IPC + real-client integration test. | Reproduces the missing intersection from issue #24 without environmental nondeterminism. |
| P0 | Establish an awaited bind-ready barrier and move the "listener started" event after successful bind. | Eliminates the .NET 10 scheduling race and makes startup readiness meaningful. |
| P0 | Preserve expected provider failures as terminal initialization state while keeping control methods independent. | Encodes the product contract directly. |
| P0 | Retain default `StopHost` for unhandled listener faults and include safe Generic Host lifecycle fault logs. | Avoids a zombie daemon and restores root-cause observability. |
| P1 | Preserve `GError` domain/code and normalize to safe diagnostic subcodes; never parse/display the message. | Distinguishes dependency, bus, service, lock, cancellation, and protocol failures reliably. |
| P1 | Add packaged negative Linux smoke cases for missing library, no bus, and no service. | Validates native loading and the release artifact rather than only managed injection seams. |
| P1 | Add explicit provider deadline backed by `GCancellable` or the libsecret asynchronous API. | Prevents indefinite native blocking; a managed-only timeout is not cancellation. |
| P2 | Add isolated real Secret Service CRUD/locked/disappearance coverage. | Valuable cross-stack assurance, but more expensive and less deterministic than the P0 regression test. |
| P2 | Decide explicitly between lease-protected runtime stale cleanup and `FirstPipeInstance` plus explicit stale cleanup. | Avoids mixed Unix socket ownership models and destructive races. |

## 6. Falsifiers and remaining uncertainty

The framework conclusion would need revision if the shipped daemon is not actually running the repository's declared .NET 10 hosting assemblies. Capture the loaded runtime/package version in reproduction evidence.

The inference that an accept-loop exception caused the reported shutdown is falsified by any trace showing `StopApplication` came from idle timeout, a signal, parent-process cancellation, or another hosted service. The fix should therefore add lifecycle stop-reason observability rather than assume one cause from the three log lines.

Secret Service implementations are not identical. A particular service may map prompt rejection, locked collections, or activation failure through a more generic D-Bus/GIO error than GNOME Keyring. The classifier must retain an unknown fallback, and tests should assert the public stable contract plus safe category where the upstream API actually guarantees one—not English error text.

Finally, a host-level integration test with a failing fake provider proves lifecycle correctness but not libsecret classification; a real Secret Service lane proves interoperability but may not reproduce missing-library or missing-bus loading paths. Both are necessary because they answer different questions.
