# Architecture Decision Record: Product Platform Evolution

- **Status:** Accepted and implemented for the product-polish milestone
- **Date:** 2026-09-15
- **Scope:** desktop interaction, search, product identity, packaging, product site, CI, and releases
- **Compatibility posture:** the unreleased v1 protocol and UI were allowed to change together; the encrypted SQLite record format did not need a migration

## 1. Problem and decision purpose

Scrap already had a sound process boundary—GUI and CLI clients use local IPC, while `scrapd` alone owns storage, encryption, and search—but the first product surface exposed several mismatches:

| Problem | Root cause | Decision |
|---|---|---|
| One dark pink/purple desktop theme | Colors were control-local values rather than semantic resources | Map the pinned MoeSegFault Style palette into Avalonia theme dictionaries |
| Mixed Chinese/English controls | Copy was embedded directly in XAML and view-model branches | Use one typed catalog selected at runtime and persist the language preference |
| “Masked” changed editor visibility | Durable record presentation and transient input visibility shared setter behavior | Model and bind them as independent state |
| Search required one scope | Scope lived outside candidate identity and was a mandatory scalar | Carry `(scope, key)` through the full stack and accept an optional scope set |
| Windows users ran a script | Release archives were being treated as installers | Ship a per-user WiX MSI; keep ZIP only as a portable asset |
| No stable product identity | The app used decorative text glyphs | Introduce one Scrap mark and derived application/site raster assets |
| No public product surface | The repository README was doing both product and engineering work | Build a dedicated Astro release landing page and deploy it through Pages |

The purpose of this milestone is not to add a general credential platform. It is to make the existing local record store coherent, discoverable, installable, and maintainable.

## 2. Preserved system boundary

```text
GUI ──┐
      ├── local IPC ──> scrapd ──> encrypted SQLite
CLI ──┘                   │
                         └── OS-protected master key
```

The following remain hard invariants:

1. clients do not open the database or hold the master key;
2. values never enter search indexes, logs, errors, or website code;
3. `(scope, key)` is the complete, case-sensitive record identity;
4. all values use the same encryption path regardless of presentation;
5. platform presentation and distribution code remain outside the domain layer.

## 3. Multi-scope search

### 3.1 Request and identity

The coordinated breaking contract is:

```text
SearchRequest
  Scopes: [] | [scope, ...]
  Query: string
  Mode: Exact | Fuzzy | Regex
  CaseSensitivity: Insensitive | Sensitive
  Limit: 1..500

SearchCandidate
  Scope
  Key
```

An empty `Scopes` collection is the single representation of **all scopes**. A non-empty collection is validated, ordinal-sorted, and deduplicated at the domain boundary. `null`, wildcard strings, and a separate global-search RPC were rejected because they create overlapping meanings and special branches.

The CLI exposes the full set-valued contract:

```text
scrap find <query> (--exact|--fuzzy|--regex) ([--scope <scope>]... | --all)
```

Omitting `--scope` or using `--all` selects every scope; repeated `--scope` selects an explicit union. Human output is `scope<TAB>key`, and JSON carries both fields.

The desktop uses the dominant two-state interaction—**All scopes** or **Current scope**—without weakening the protocol. An arbitrary-subset picker can be added later without changing daemon or storage semantics.

### 3.2 Execution and complexity

SQLite reads metadata for the chosen scopes in one deferred-transaction snapshot using a join from records to scopes. It does not project ciphertext, nonce, or value. The daemon converts rows to complete candidates, invokes the existing matcher, and maps matches back by `(scope, key)`.

For `n` candidate keys and query length `m`:

- exact matching is `O(nm)` in the worst string-comparison case;
- fuzzy matching keeps the existing bounded in-memory scorer and sorts candidates deterministically;
- regex execution retains cancellation and a 100 ms timeout;
- the final result limit is global, not multiplied by scope count.

Tie-breaking is score, then scope ordinal, then key ordinal. This is stable across database row order and makes duplicate keys unambiguous.

### 3.3 Destructive analysis

The dangerous failure mode is losing scope identity after search. Selecting, saving, copying, editing, and deleting therefore use the candidate's own scope rather than the independently selected management scope. A regression test covers saving a duplicate key in one scope when the all-scope result also contains that key elsewhere.

## 4. Desktop presentation architecture

### 4.1 Theme resources

`App.axaml` maps the exact MoeSegFault Style `v0.1.2` semantic roles into `Light` and `Dark` `ThemeDictionaries`. Controls consume only `DynamicResource` brushes. The preference is the typed value `System | Light | Dark`; `System` maps to Avalonia's default theme variant so live OS changes remain platform-owned.

The upstream light `accent/on-accent` pair measures only about 3.11:1 for normal text. Primary controls deliberately use `accent-strong/on-accent` (about 5.00:1) instead of copying that accessibility defect. This is a local semantic correction, documented in `research-platform-style.md`.

### 4.2 Localization and preferences

The first complete implementation uses a typed `LocalizationStrings` catalog for `zh-CN` and `en`. Switching replaces the entire catalog and rebuilds localized choice objects, so a control never concatenates two languages. A notifying view-model reference makes open views update without restart.

Theme and language are the only persisted GUI preferences. `UserPreferenceStore` writes them atomically under platform Local Application Data and validates enum values on load. Missing, malformed, or unwritable preference files fall back to OS locale plus System theme; preference failure never blocks record access. Search queries and secret data are never written there.

RESX remains a reasonable future migration if more locales require translator tooling. It was not introduced for two compact catalogs because a notifying wrapper would still be required and the current typed surface is smaller.

### 4.3 Record editor state

```text
EditorValueVisible : transient dialog state
EditorIsMasked     : persisted record presentation policy
```

Neither setter writes the other. The editor displays the distinction explicitly:

- **Show while editing** changes only the current password-style text box;
- **Mask after saving** or **Visible** changes only how record details open later;
- both options state that storage encryption is identical.

The radio option content is part of the radio control, not an adjacent inert label, so the whole label is clickable and has an accessible name.

## 5. Product identity

The current reviewed source artwork is `assets/branding/scrap-icon-source.png`. Deterministic raster exports live beside it at 16, 32, 48, 64, 128, 256, and 512 px. Consumers use:

| Consumer | Asset |
|---|---|
| Windows apphost and installer | `src/Scrap.Gui/Assets/scrap.ico` |
| Avalonia window on all platforms | `src/Scrap.Gui/Assets/scrap.png` |
| Product site | optimized PNGs copied under `website/public/` |

The mark combines a folded paper scrap, keyhole, and small sparkle while using the platform coral/cream language. It is distinct from the MoeSegFault owner brand.

The remaining brand-maintenance gap is an editable vector master. Raster generation is therefore reproducible from the checked-in source image, but geometric editing is not yet source-native.

## 6. Windows installation and trust

### 6.1 Installer decision

`installer/Scrap.Installer.Windows` uses WiX 6 to build a per-user MSI. It installs `scrap.exe`, `scrapd.exe`, and `scrap-gui.exe` under `%LOCALAPPDATA%\Scrap`, adds that directory to user PATH, creates a Start Menu shortcut, registers Add/Remove Programs metadata, and uses the product icon throughout.

A stable `UpgradeCode` enables major upgrades. `AllowSameVersionUpgrades` deliberately permits a SemVer prerelease such as `1.2.3-beta` to upgrade to stable `1.2.3`, because Windows Installer compares only the numeric product version. The MSI owns program files and its integration records; it never owns `%USERPROFILE%\.scrap`, so ordinary upgrade and uninstall preserve user data.

Windows ZIP remains a portable/diagnostic asset and contains binaries plus legal notices—no PowerShell installer. Linux and macOS retain their platform archive and shell-script flow.

### 6.2 Signing is not packaging

The build signs Windows executables first and the final MSI second when a trusted PFX or certificate thumbprint is configured, applies an RFC 3161 timestamp, verifies signing in the release workflow, and only then computes SHA-256 sidecars.

An unsigned MSI is still a valid installer but may be blocked or warned about by SmartScreen. Even a newly signed direct download may need publisher reputation. Repository code cannot manufacture a public-trust publisher identity; Microsoft Store distribution is the documented warning-free path. The release workflow therefore makes unsigned state explicit instead of instructing users to bypass Windows protection.

MSIX was considered but not selected for this milestone. Its immutable package and Store integration are attractive, but direct sideloading still requires trusted signing, CLI aliases add manifest/version machinery, and the familiar per-user MSI satisfies the current three-binary installation contract with less special-case infrastructure.

## 7. Product release site

The site is an isolated pnpm workspace package at `website/`:

```text
website/
  astro.config.ts
  public/CNAME
  src/layouts/BaseLayout.astro
  src/components/ProductPage.astro
  src/lib/content.ts
  src/pages/index.astro       # zh-CN
  src/pages/en/index.astro    # English
```

Astro emits static HTML. The root Chinese page and `/en/` page are both canonical, indexable, and connected by `hreflang`. A visible language link changes route; there is no forced locale redirect. A small inline head script applies `auto | light | dark` before paint, while CSS media queries keep System mode usable without JavaScript. Storage access is best-effort so a disabled `localStorage` does not break theme switching.

The page is intentionally a product narrative—outcome, interactive-looking product preview, trust boundary, features, architecture, and platform downloads—not a copied command reference. Windows is described as MSI; macOS and Linux are described as archives. CTAs use GitHub's `/releases` index so preview-only repositories never send users to a missing `/releases/latest` page.

The pinned stylesheet URL is `https://style.moesegfault.dev/v0.1.2/css/all.css`. Local semantic tokens preserve the page if the external component stylesheet is slow, and pinning avoids silent visual drift.

## 8. CI, Pages, and Release topology

```text
pull request / main push
  ├─ .NET locked restore -> Release build -> 179 tests on 3 OSes
  ├─ frozen pnpm install -> Astro check -> Vitest -> static build
  └─ Windows: clean dummy-payload WiX build -> MSI metadata open

main site change
  └─ repeat site verification -> assert CNAME -> Pages artifact -> deploy

vX.Y.Z tag
  └─ 3-OS test -> 4-RID publish/smoke
      ├─ Windows MSI build -> optional Authenticode -> real install/uninstall smoke
      └─ archive + sidecar verification -> SHA256SUMS -> GitHub Release
```

Actions are pinned to full commit SHAs. Ordinary jobs have `contents: read`; Pages deployment alone receives `pages: write` and `id-token: write`, and Release publication alone receives `contents: write`. The MSI smoke verifies three installed programs, PATH, shortcut lifecycle, and preservation of a sentinel in `.scrap`.

`website/public/CNAME` records the intended domain but is not DNS configuration. Repository administrators must still select GitHub Actions as the Pages source, set `scrap.moesegfault.dev` in Pages settings, point its CNAME to `kleedaisuki.github.io`, and enable HTTPS.

## 9. Alternatives rejected

| Alternative | Why rejected now |
|---|---|
| Separate global-search RPC | Duplicates matching, cancellation, and ranking semantics |
| `null`, `*`, and empty array as different scope modes | Creates three representations of the same intent |
| Client-side fan-out per scope | Multiplies limits, loses one snapshot, complicates cancellation and stable ranking |
| Bind detail actions to the header scope | Breaks duplicate-key identities in all-scope results |
| Couple masking to encryption strength | False model: every value already follows one encryption path |
| Event bus or general state framework | Adds indirection without reducing the current product state |
| Force dark mode | Ignores OS/user preference and is not universally more readable |
| Self-signed public release | Establishes no public trust and can make installation worse |
| Website GitHub API calls at runtime | Adds rate-limit/failure state to a static product page |

## 10. Evidence and open external work

Observed validation for this milestone:

- locked .NET restore, Release build, and 179 tests pass;
- Astro check reports zero diagnostics, Vitest passes, and both locale routes build;
- all root-relative site assets resolve and CNAME content is exact;
- clean WiX build produces an MSI that Windows Installer can open;
- a full `win-x64` build produces MSI and portable ZIP with matching SHA-256 sidecars;
- the built GUI executable and Avalonia window both resolve the Scrap icon.

External state that code alone cannot prove:

1. a public-trust Authenticode credential and its SmartScreen reputation;
2. GitHub Pages repository settings, DNS propagation, and HTTPS activation;
3. an editable vector master for future icon geometry changes.

These are deployment inputs, not reasons to reintroduce scripts, hidden fallback behavior, or misleading trust claims.

## 11. References

- [MoeSegFault Style foundations](https://style.moesegfault.dev/foundations/)
- [Avalonia theme variants](https://docs.avaloniaui.net/docs/styling/theme-variants)
- [Avalonia localization](https://docs.avaloniaui.net/docs/app-development/localizing)
- [Microsoft Windows code-signing options](https://learn.microsoft.com/windows/apps/package-and-deploy/code-signing-options)
- [WiX major upgrades](https://docs.firegiant.com/wix3/howtos/updates/major_upgrade/)
- [Astro internationalization](https://docs.astro.build/en/guides/internationalization/)
- [GitHub Pages custom workflows](https://docs.github.com/pages/getting-started-with-github-pages/using-custom-workflows-with-github-pages)
- [GitHub Release links](https://docs.github.com/repositories/releasing-projects-on-github/linking-to-releases)
