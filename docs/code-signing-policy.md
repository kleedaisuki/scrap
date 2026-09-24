# Code signing policy

This policy defines who may authorize Scrap release signatures, what may be signed, and how users can distinguish a signed release from an unsigned one. The policy itself is not evidence that an artifact is signed; the signature on the downloaded file and the corresponding GitHub Release trust-status section are authoritative.

## Current status and intended provider

Scrap's current direct-download preview artifacts are unsigned. The project intends to apply for the free Open Source Code Signing program operated by SignPath Foundation. Acceptance is an external prerequisite and is not implied by this document.

If the application is accepted, signed releases will carry the required attribution:

> Free code signing provided by [SignPath.io](https://signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).

The maintained fallback is a publicly trusted Authenticode provider whose signature validates under the Windows Authenticode policy. Self-signed certificates are never presented as suitable for public releases.

## Roles

Scrap is currently maintained in the personal repository [`kleedaisuki/scrap`](https://github.com/kleedaisuki/scrap).

| Role | Members | Contract |
|---|---|---|
| Authors, committers, and reviewers | [`@kleedaisuki`](https://github.com/kleedaisuki) | Maintain source and build definitions; review changes from external contributors before merge. |
| Signing approver | [`@kleedaisuki`](https://github.com/kleedaisuki) | Manually approve each production signing request only after the release workflow and artifact identity have been checked. |

Multi-factor authentication is required for GitHub and for any signing-provider account. Signing credentials and approval capability must not be exposed to pull-request workflows or untrusted runners.

## What may be signed

Only artifacts built from this repository by the version-tag release workflow may receive the production signature. The allowed Windows product consists of:

- `scrap.exe`, `scrapd.exe`, and `scrap-gui.exe` built from their corresponding repository projects;
- the WiX MSI containing those three entry points; and
- the Windows portable ZIP containing the same entry points and legal notices.

All public entry points must report `ProductName=Scrap`, `FileDescription=Scrap`, `CompanyName=MoeSegfault`, and one consistent release version before a signing request is created. A managed SignPath configuration should deep-sign the executables inside the MSI, sign the MSI itself, and sign the executable copies in the portable archive. Checksums are generated only after signing.

Release CI must then verify each Authenticode chain, timestamp, MSI install/uninstall behavior, and final SHA-256 sidecars. A workflow run without an accepted signing provider remains valid for testing but must publish an explicit **unsigned** status.

## Privacy and system changes

Scrap does not transfer user data to other networked systems. The desktop app, CLI, and daemon communicate locally; records remain on the user's machine. A network transfer occurs only when the user separately opens the product website, source repository, or release download in a browser.

The Windows installer makes and owns only these integration changes: its program files, Start Menu and Desktop shortcuts, Add/Remove Programs registration, and user `PATH` entry. The download and release surfaces must disclose them before a signed build is requested. Normal uninstall removes those integrations but preserves `%USERPROFILE%\.scrap`; no uninstall path silently deletes user records. An installer-native summary remains a readiness item if the signing-provider review considers the linked pre-download disclosure insufficient.

## Incident response

If a signing credential, signing account, approved artifact, or release workflow is suspected to be compromised, publishing stops until the provider audit trail and affected GitHub Actions run are reviewed. Affected certificates or artifacts are revoked or withdrawn as appropriate, and the incident is disclosed in the repository and release notes.

## External references

- [SignPath Foundation conditions for Open Source projects](https://signpath.org/terms.html)
- [SignPath GitHub trusted-build integration](https://docs.signpath.io/trusted-build-systems/github)
- [SignPath deep-signing artifact configuration](https://docs.signpath.io/artifact-configuration/)
- [Microsoft code-signing options](https://learn.microsoft.com/windows/apps/package-and-deploy/code-signing-options)
