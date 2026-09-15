# Microsoft Store full-package MSIX architecture

Status: implemented packaging and CI validation; production Partner Center identity and submission remain owner-operated external steps.

## Decision

Scrap uses a **full-package MSIX**, not an MSI/EXE Store listing, sparse package, or package-with-external-location. The package owns this flat application root:

```text
AppxManifest.xml
scrap-gui.exe       # manifest application entry point
scrap.exe           # App Execution Alias target
scrapd.exe          # sibling process started by clients
Assets/*
LICENSE.txt
THIRD-PARTY-NOTICES.txt
```

This preserves the existing process architecture: the GUI and CLI are clients; `scrapd` remains the sole storage/transaction owner. A flat root also makes `AppContext.BaseDirectory/scrapd.exe` the ordinary case for both clients, without Store-only path discovery. User state remains outside package ownership at `%USERPROFILE%\.scrap`, so package update and removal preserve records.

The manifest exposes one full-trust GUI application and one `scrap.exe` **App Execution Alias**. The alias has `desktop4:Subsystem="console"`, so terminal input/output retain console semantics. `desktop4:SupportsMultipleInstances="true"` is required by current manifest validation when a console alias declares that subsystem. The only declared restricted capability is `runFullTrust`; no broad filesystem or network capability is added.

## Identity and version invariants

`build/package-store.ps1` requires these values copied exactly from **Partner Center → Product management → Product identity**:

| Parameter | Contract |
|---|---|
| `IdentityName` | Exact package identity Name, including casing and publisher prefix |
| `Publisher` | Exact publisher distinguished name (DN) |
| `PublisherDisplayName` | Exact display value assigned by Partner Center |
| `PackageVersion` | Four unsigned 16-bit integers; major is greater than `0` and revision is `0` for Store submission |

No guessed production identity is checked into source. CI uses an explicitly non-production identity and a certificate whose subject exactly matches that CI Publisher. Store package version `1.0.1.0` is checked into `installer/Scrap.Installer.Store/StoreVersion.txt`; it is independent of SemVer because Store version ordering is a monotonic four-part numeric contract. A production submission must increment it before replacing a previously submitted package.

Push and pull-request runs always use the explicit CI identity. A manual workflow run reads exact production values from the `STORE_IDENTITY_NAME`, `STORE_PUBLISHER`, and `STORE_PUBLISHER_DISPLAY_NAME` repository variables and refuses to build if any are absent. Its required inputs select executable SemVer and numeric package version, so producing a real candidate requires configuration, not a source edit.

## Build and validation flow

1. Restore locked dependencies for `win-x64`.
2. Publish GUI, CLI, and daemon as self-contained, untrimmed single-file executables.
3. Verify consistent Windows product metadata.
4. Inject exact identity fields into a temporary manifest through an XML DOM (not string substitution).
5. Run Windows SDK `MakeAppx pack` with SHA-256 and full schema/content validation.
6. Unpack the resulting container and inspect its flat payload and exact identity.
7. Hash the unsigned package, then upload it only as a short-lived workflow artifact.

CI copies—not mutates—the unsigned artifact and creates a non-exportable ephemeral code-signing key in the runner's current-user `My` store. Current Windows AppX deployment does not accept `CurrentUser\TrustedPeople` for this package (observed as `0x800B0109`); Microsoft's current troubleshooting guidance likewise says App Installer checks the machine store. The workflow therefore imports only the public test certificate into `LocalMachine\TrustedPeople` on a disposable administrator runner, signs and verifies the copy, installs it, resolves the alias through a fresh `where.exe` lookup, exercises the CLI and daemon, activates the GUI through `shell:AppsFolder`, removes the package, and deletes both certificate entries in `finally`. It checks the user PATH before and after because MSIX must rely on alias registration, not PATH mutation. A sentinel under `~/.scrap` demonstrates removal persistence. The ephemeral package is never a Release asset and is not suitable for users or persistent development machines.

## Failure and destructive analysis

| Failure | Detection or containment |
|---|---|
| Partner identity typo/case drift | Required explicit inputs; post-pack exact comparison |
| Manifest/schema drift | `MakeAppx` validation on every relevant PR and main push |
| Nested or missing executables | Post-pack unpack plus flat-path assertions |
| CLI alias silently loses console behavior | Manifest declaration plus installed `scrap.exe --help` smoke |
| Packaged client cannot find daemon | Installed alias runs `daemon ping`, then orderly shutdown |
| Installer mutates PATH | Byte-equivalent user PATH checks across install/removal |
| Removal deletes records | Sentinel persistence check outside package root |
| CI test certificate leaks to users | Signed copy stays in runner temp; only unsigned original is uploaded |
| Store package version reused | Human-visible checked-in counter; pre-submission checklist requires increment |

The installed smoke is intentionally performed on a disposable hosted Windows user. It does not claim Store certification. Before production submission, run the Windows App Certification Kit (WACK) on the exact candidate and review Partner Center's ingestion report.

## External evidence

- Microsoft, [App package requirements for MSIX apps](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/app-package-requirements): exact Partner Center identity values, Store target/version constraints, SHA-256, and WACK guidance.
- Microsoft, [Generating MSIX package components](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-manual-conversion): full-trust desktop manifest structure and `runFullTrust`.
- Microsoft, [Create an MSIX package with MakeAppx](https://learn.microsoft.com/en-us/windows/msix/package/create-app-package-with-makeappx-tool): validated command-line packing and unpacking.
- Microsoft, [`uap5:AppExecutionAlias` schema](https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-uap5-appexecutionalias): alias hierarchy and console subsystem contract.
- Microsoft, [Sign an app package using SignTool](https://learn.microsoft.com/en-us/windows/msix/package/sign-app-package-using-signtool): package signing and SHA-256 matching requirements.
- Microsoft, [MSIX troubleshooting guide](https://learn.microsoft.com/en-us/windows/msix/msix-troubleshooting-guide): `0x800B0109` diagnosis and the current machine `TrustedPeople` requirement for App Installer.
- Microsoft, [Reserve an MSIX app name](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/reserve-your-apps-name): name reservation precedes access to production identity.

## Readiness boundary

Repository automation can prove that a structurally valid package builds and works after trusted test signing. It cannot reserve the product name, choose the owner account, accept Partner Center agreements, obtain the assigned production identity, create listing media, or submit for certification. Those are external authority steps, not reasons to weaken or guess package identity.
