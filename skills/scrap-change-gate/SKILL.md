---
name: scrap-change-gate
description: Validate behavior-preserving Scrap code, dependency, and release-tool changes across the product solution, standalone tools, and GitHub Actions.
---

# Scrap change gate

Use this skill for a Scrap maintenance/refactor milestone, not for a product-feature decision. Read [maintenance governance](../../docs/maintenance-governance.md) for compatibility and dependency policy; consult the owning architecture document only when the affected contract needs it.

1. Inspect `git status` and the focused diff first. Preserve unrelated dirty files. In particular, never stage generated `bin/obj` paths or let them enter `Scrap.slnx`.
2. Match checks to the changed boundary. `Scrap.slnx` covers the product and test projects, **not** `tools/Scrap.Perf` or `tools/Scrap.Screenshot`. Use locked .NET restore and Release build/test for affected product code; use `tools/Scrap.Perf/Test-RunRootGuard.ps1` for benchmark-path changes and locked restore/build of `tools/Scrap.Screenshot/Scrap.Screenshot.csproj` for screenshot-tool changes. Use `pnpm --dir website run verify` for site changes.
3. When removing a validation or security check, name the caller, trust boundary, protected asset, and existing owner of the invariant. Keep distinct wire, domain, and transactional checks when they serve different contracts.
4. Stage only intended files and commit a working, coherent milestone. Push the branch and dispatch `.github/workflows/ci.yml` for cross-platform acceptance when the change warrants it. Check the product matrix on Linux, Windows, and macOS, and the developer-tool matrix on Linux and Windows (the benchmark path guard has OS-dependent semantics). Inspect each job and its failed step; fix the cause and dispatch a new run rather than treating a local pass or an expected child-process failure as CI success.

Do not change product functionality, UI, persisted data, CLI output, or v1/v2 IPC behavior merely to simplify a maintenance change.
