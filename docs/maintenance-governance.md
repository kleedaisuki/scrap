# Maintenance and dependency governance

**Status:** current repository maintenance contract, 2026-09-24. This document governs change procedure, not product behavior. The product contract lives in code, tests, and the versioned design records; `scrap-design.md` contains historical v1 examples.

## Boundaries and evidence

| Area | Source of truth | Change check |
|---|---|---|
| Runtime and NuGet | `global.json`, `Directory.Build.props`, project files, each `packages.lock.json` | `dotnet restore Scrap.slnx --locked-mode`, then build and tests |
| Release site | `website/package.json`, root `pnpm-lock.yaml`, `pnpm-workspace.yaml` | `pnpm install --frozen-lockfile`, then `pnpm --dir website run verify` |
| CI and release | `.github/workflows/`, `build/`, installer projects | Test the affected OS/package path; preserve signing and data-retention contracts |
| Domain and IPC | `src/Scrap.Domain/`, `src/Scrap.Protocol/`, daemon and client integration tests | Review old-client behavior, persisted data, and CLI output before changing a public contract |

The .NET SDK is pinned to `10.0.400` without roll-forward. `Directory.Build.props` enables package lock files and CI locked-mode restore. The site pins direct package versions and pnpm `12.4.1`; CI uses a frozen pnpm lockfile. These are intentional reproducibility constraints, not invitations to edit only one version number. Microsoft documents that locked restore fails when dependency declarations and lock files disagree; pnpm similarly treats a frozen lockfile as immutable during installation. [NuGet locked restore](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-restore), [pnpm install](https://pnpm.io/cli/install).

## Dependency changes

1. State the reason: compatibility, security advisory with a reachable affected path, measured defect, or a feature required by the current product. An available newer version alone is not a reason.
2. Change the smallest coherent dependency family together (for example, all directly referenced Avalonia packages), then regenerate **only** the corresponding tracked lock files with the pinned toolchain. Inspect transitive changes, licenses, and platform-specific assets before committing.
3. Run locked/frozen restore **after** regeneration. Never use `--force-evaluate` together with locked restore, bypass CI checks, or manually patch integrity hashes.
4. Test the affected integration path, including Windows packaging for installer dependencies and all three desktop OS jobs for portable runtime changes. Record any platform not exercised locally so CI is the explicit remaining gate.
5. Do not centralize every version merely to reduce repeated text. Central package management becomes worthwhile only if version drift or update burden is demonstrated; migration would touch every project and lock graph without changing product behavior.

## Code and documentation changes

- Preserve user data, protocol compatibility, CLI stdout/stderr/exit codes, installer identity, and GUI behavior unless a separately approved product change says otherwise. Internal abstractions may evolve freely inside those boundaries.
- Start from the owning layer and its tests. A domain invariant belongs in a domain type; daemon/storage enforce transactions and persistence; clients adapt protocol data; GUI/CLI own presentation. Avoid revalidating already-typed data at each internal hop. Keep validation at actual trust boundaries.
- Public C# APIs and important implementation decisions use bilingual XML documentation (`///`) explaining contracts, invariants, ownership, and non-obvious rationale. Shell/config comments use the native syntax, also bilingual. Examples belong on complex or user-facing APIs. Update a design document when a contract or architectural decision changes; do not append a stale forward-looking plan as if it were current behavior.
- Security checks require a named attacker capability, trust boundary, protected asset, and failure mode. Prefer one effective check at the right boundary over duplicated defensive branches. Removing a check requires evidence that another boundary owns it or the asserted threat is out of scope.
- A public `IReadOnlyList<T>` is not proof that a value snapshot is immutable: an exposed array remains castable and mutable, while a read-only wrapper reflects changes to its backing collection. Copy first, then wrap, when requests or results cross asynchronous/client boundaries; test both source mutation and cast-based mutation. See [Microsoft's `Array.AsReadOnly` contract](https://learn.microsoft.com/en-us/dotnet/api/system.array.asreadonly?view=net-10.0).
- Keep experiment artifacts under root `.cache/` or `.temp/`; they are ignored. Do not add test scripts or scratch files outside the repository.

## Repository skills

The focused [`scrap-change-gate` skill](../skills/scrap-change-gate/SKILL.md) captures the repeatable Scrap-specific verification workflow, including standalone tools omitted from the product solution and the GitHub Actions cross-platform gate. The developer-tool CI matrix runs on both Windows and Linux because benchmark path containment depends on OS path semantics; the product matrix also covers macOS. This document remains the source for policy and dependency decisions; the skill links here rather than duplicating the rationale. Add another skill only for a distinct workflow with a clear trigger and evidence that ordinary documentation is insufficient.

## Milestone gates

For a behavior-preserving refactor, use a focused test first, then locked restore/build/test for touched .NET code and site verification for touched website code. GitHub Actions is the cross-platform acceptance gate; local success is not evidence for untested OS behavior. Commit coherent, reviewed milestones with source, tests, and documentation together. If a gate fails, investigate the failure rather than weakening assertions or changing lockfiles opportunistically.
