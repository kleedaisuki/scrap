# Partner Center submission answers

These answers describe the current packaged Windows desktop application. Re-check them whenever capabilities, data handling, dependencies, or business model change.

## Availability and properties

| Partner Center field | Answer |
|---|---|
| Base price | Free |
| Audience | Public |
| Discoverability | Available and discoverable in the Store |
| Publishing hold | Publish manually after certification |
| Device family | Windows Desktop only |
| Primary category | Developer tools |
| Subcategory | Utilities |
| Optional secondary category | Productivity |
| Additional hardware | None |
| Website | `https://scrap.moesegfault.dev/` |
| Support | `https://scrap.moesegfault.dev/support/` |
| Privacy policy | `https://scrap.moesegfault.dev/privacy/` |
| External purchases | No |
| Generative AI | No |
| Pen and ink | No |
| Windows service or driver | No |
| OneDrive application-data backup | Off |
| Install on alternate drives | Off until physically validated |
| Accessibility declaration | Leave unchecked until a documented accessibility test supports it |

Optional listing identity fields:

| Field | Value |
|---|---|
| Developed by | `moeSegFault` |
| Copyright | `© 2026 moeSegFault contributors` |
| License identifier | `GPL-3.0-only` |
| License URL | `https://github.com/kleedaisuki/scrap/blob/main/LICENSE` |

Do not invent a support email. The public support page routes users to the repository issue tracker.

**Owner-only input still required:** provide a monitored non-public contact email in Partner Center wherever certification/privacy contact information is requested. Do not commit that address merely to satisfy the form, and do not direct users to disclose credentials in public issues.

Answer **Yes** when Partner Center asks whether the product accesses, collects, or processes personal information: users may enter credentials and identifiers. Processing is local rather than a network transfer. Record values are encrypted; scope names, record keys, and timestamps are local plaintext metadata. Scrap has no application telemetry, analytics, advertising, cloud account, or application-managed network service.

## Age rating / IARC

- Category: **Utility/Productivity**.
- Can users create content? **Yes**—they create local records.
- Does the product publish or share user-generated content? **No**.
- Violence, sex, language, drugs, gambling, horror, ads, in-app purchases, user communication, online sharing, unrestricted internet, location, camera, and microphone: **No**.

## Restricted capability: `runFullTrust`

Paste the complete text below into the restricted-capability explanation. Do not shorten it to “desktop app”; reviewers need the process, data, privilege, and test boundaries.

```text
Scrap is a packaged Win32 desktop application built with Avalonia and .NET. The package declares runFullTrust because its GUI, command-line client, and on-demand local daemon are full-trust desktop processes.

The GUI and CLI communicate with scrapd through a current-user-only named pipe. scrapd is the sole process that reads and writes the encrypted SQLite data store under %USERPROFILE%\.scrap. The package also exposes scrap.exe through an App Execution Alias and starts the sibling scrapd.exe process only when a client needs it.

On Windows, record values are encrypted locally with AES-GCM and the master key is protected with DPAPI for the current user. Scope names and record keys remain local metadata. Scrap accesses the system clipboard only when the user explicitly copies a value.

The capability is not used to elevate privileges. Scrap does not install a Windows service or driver, run at startup, modify PATH, access arbitrary user libraries, or request administrator rights. The application contains no cloud account, advertising, analytics, telemetry, or application-managed network service. The daemon exits after it has been idle.

Test steps:
1. Launch Scrap from the Start menu.
2. Create a scope named cert-test.
3. Create a record named api_token with a disposable test value and leave Default display set to Masked.
4. Save it and verify that the detail view is masked.
5. Use Reveal and Copy, then search for api_token across all scopes.
6. Open a terminal and run: scrap.exe --help
7. Run: scrap.exe daemon ping

No login or external service is required.
```

## Certification notes

- No account or credentials are required.
- Use only disposable values during testing.
- The local data root is `%USERPROFILE%\.scrap`; normal package removal intentionally preserves it.
- `scrap.exe` is an App Execution Alias. Open a new terminal after installation if an existing terminal has not refreshed alias discovery.
- The package does not modify `PATH`; the daemon is started on demand and later exits when idle.
- Local WACK 10.0.26100.8249 preflight for the `1.0.2.0` candidate completed all 24 tests with `OVERALL_RESULT=PASS`; every mandatory test passed. Its optional Blocked executables analyzer flags process-launch imports and tool-name strings carried by the self-contained .NET payload. This optional static finding is disclosed for reviewer context rather than hidden.

## Submission media and release note gate

- Upload `assets/AppTileIcon-300x300.png` to each locale's 1:1 app-tile icon field.
- Upload the four real, localized 1366×768 screenshots under `screenshots/{zh-CN,en-US}` in the order specified by each locale file.
- Leave **What's new in this version** completely blank for this first submission.
- Do not select Xbox, Holographic, or promotional-art fields that are not supported by this product.
