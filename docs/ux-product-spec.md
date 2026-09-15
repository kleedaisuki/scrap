# scrap UX and Product Release Specification

**Status:** implementation-ready product decision record
**Audience:** desktop, protocol/CLI, release-engineering, web, design, and QA owners
**Product surface:** the `scrap` desktop app, cross-scope search semantics, and the public release page at `https://scrap.moesegfault.dev`

## 1. Problem definition

`scrap` should make small local secrets and short text fields easy to find without making their handling feel mysterious. The smallest complete release must let a person:

1. install a normal desktop product rather than run a repository script;
2. create a record while clearly understanding how it will appear after saving;
3. find a key in one, several, or all scopes;
4. use the app and release page in Simplified Chinese or English, in a readable light or dark theme;
5. recognize the product by a stable icon and obtain the correct signed release from a persuasive product page.

The decision purpose of this document is to remove interaction ambiguity before the implementation is split across UI, protocol, packaging, and web work.

### 1.1 Evidence from the current repository

The following are observations, not design preferences:

| Observation | Repository evidence | User consequence |
|---|---|---|
| The GUI is dark-only. | `App.axaml` sets `RequestedThemeVariant="Dark"`. | Users cannot follow system appearance or choose light mode. |
| The colors are a hand-written pink/purple palette. | `App.axaml` defines `MoePink`, `MoePurple`, and fixed dark surfaces. | It does not match the warm-paper/coral semantic palette published by MoeSegFault Style. |
| The UI is not localized. | `MainWindow.axaml` and `MainWindowViewModel.cs` contain hard-coded English, Chinese, and bilingual strings. | A single sentence can switch language; translation and screen-reader output are inconsistent. |
| Record policy and editor visibility are coupled. | Setting `EditorIsMasked` forcibly sets `EditorValueVisible = !value`. | Choosing how a record behaves *after saving* unexpectedly changes what the person can see *while editing*. |
| Search accepts exactly one scope. | GUI and protocol `RecordSearchRequest`/`SearchRequest` contain one required `Scope`. | The user must remember a scope before they can find a key. |
| Cross-scope identities cannot be presented safely by the current candidate model. | GUI `RecordCandidate` contains `Key` but not `Scope`, although record identity is `(scope, key)`. | Same-named keys from different scopes would be indistinguishable. |
| Installation is still archive-and-script oriented. | `README.md` directs users to extract an archive and run `install.ps1` or `install.sh`. | This is a contributor workflow, not a Windows release experience. |

### 1.2 Product principles

- **Separate durable policy from temporary view state.** “Mask after saving” and “show while editing” are different variables and must never mutate one another.
- **Search first, organize second.** Scopes remain a useful filter and identity boundary, but remembering the right one is not a prerequisite for retrieval.
- **One language at a time.** Translation is selected at the application/page level; never concatenate Chinese and English in one control.
- **Themes are semantic mappings, not alternate component trees.** Components consume roles such as background, surface, text, accent, border, danger, and focus.
- **The release page sells the product; the repository explains it.** The public page leads with outcomes, proof, and download, not architecture or command reference.
- **Never promise what the artifact cannot prove.** “Verified publisher,” “signed,” or “notarized” may appear only when CI verifies the corresponding signature.

## 2. Target users and jobs

| User | Primary job | Failure to prevent |
|---|---|---|
| Developer with many environments | Find and copy a token in seconds without remembering its scope. | Searching the wrong scope and assuming the key does not exist. |
| CLI/automation user | Resolve deterministic `(scope, key)` results and consume structured output. | Ambiguous same-name results or secret values leaking into listings. |
| Desktop-first user | Add a password, ID, endpoint, or note and understand whether it will be visible later. | Believing “masked” changes encryption strength, or accidentally saving the wrong display policy. |
| Prospective user on the website | Understand the product and install the correct build with confidence. | Landing on documentation, downloading the wrong architecture, or encountering an unexplained Windows warning. |

## 3. Desktop information architecture

The main window remains a two-pane retrieval workspace, but the top-level scope control becomes a **search filter**, not a mandatory single workspace.

```text
+--------------------------------------------------------------------+
| scrap | [Scope filter: All scopes ▾]       [Theme] [Language] [⚙] |
|        [Search keys…                 ] [Fuzzy ▾] [Aa] [New]     |
+-------------------------------+------------------------------------+
| key                    scope  | Selected record                    |
| API_TOKEN              prod   | prod / API_TOKEN                   |
| API_TOKEN              staging| [masked value] [Reveal] [Copy]     |
| endpoint               local  |                         [Edit] [...]|
+-------------------------------+------------------------------------+
```

- Scope creation, rename, and deletion move behind a labeled **Manage scopes** action. They must not compete visually with daily search.
- **New record** is always available when at least one scope exists.
- Results always display both key and scope. This eliminates a special-case layout when a single scope happens to be selected.
- The exact identity shown in details, edit, and delete confirmation remains `scope / key`.

## 4. Record creation: masked policy versus editor visibility

### 4.1 Vocabulary and control model

Do not present two peer checkboxes named “masked” and “hidden.” Use these two independent controls:

1. A password-style value field with an eye button whose accessible label toggles between **Show value while editing** and **Hide value while editing**.
2. A two-option **Default display** radio group below the field:
   - **Masked** — “Hide the value in record details. Reveal is temporary; copied text is cleared when possible.”
   - **Visible** — “Show the value in record details.”

Below the group, show the invariant: **“Both options use the same encrypted storage.”** This prevents “Visible” from being read as “stored in plaintext.”

Recommended localized strings:

| Semantic key | `zh-CN` | `en` |
|---|---|---|
| `record.display.label` | 默认显示方式 | Default display |
| `record.display.masked` | 遮罩显示 | Masked |
| `record.display.masked.help` | 详情页默认隐藏；显示是临时的，复制后会尽力清理剪贴板。 | Hidden in details by default; reveal is temporary and the clipboard is cleared when possible. |
| `record.display.visible` | 直接显示 | Visible |
| `record.display.visible.help` | 在详情页直接显示。 | Shown directly in record details. |
| `record.display.encryption` | 两种方式使用相同的加密存储。 | Both options use the same encrypted storage. |
| `record.editor.show` | 编辑时显示值 | Show value while editing |
| `record.editor.hide` | 编辑时隐藏值 | Hide value while editing |

### 4.2 State model

`editorVisibility` is transient dialog state. `recordPresentation` is saved record policy.

| Event | Editor visibility | Saved presentation |
|---|---|---|
| Open **New record** | Hidden | Masked (recommended default) |
| Open **Edit** for masked record | Hidden | Masked |
| Open **Edit** for visible record | Visible | Visible |
| Press eye button | Toggle only this dialog's editor visibility | **No change** |
| Choose Masked/Visible | **No change** | Change pending policy only |
| Cancel | Discard both pending changes | Existing record unchanged |
| Save | Dialog closes | Persist selected presentation |

This independence is the central acceptance invariant. No setter, binding, or event handler may assign one variable as a side effect of changing the other.

### 4.3 Detail and clipboard behavior

- A masked record displays a constant mask, not a length-correlated mask.
- **Reveal** shows the value for 15 seconds and then remasks it. Selecting another record, minimizing/locking the app, or manually hiding ends reveal immediately.
- Copy is always explicit and does not require reveal first.
- After copying a masked record, attempt to clear the clipboard after 30 seconds only if it still contains the copied value. This remains best-effort and is explained without claiming clipboard security.
- A visible record stays visible and does not offer a redundant Reveal button. Copying it does not schedule cleanup.
- Search results and list surfaces never include values.

## 5. Optional multi-scope search

### 5.1 Scope filter

The first complete GUI supports two explicit states, while the protocol and CLI retain the more general set-valued target:

```text
GUI search coverage = All | Current(selected scope)
Protocol scopes     = [] (all) | [scope, ...] (explicit non-empty set)
```

The GUI selector contains **All scopes** and **Current scope**. The existing scope selector supplies the current explicit scope, so daily scope management and record creation remain available without a second modal. First launch uses **All scopes**. Results always show both scope and key, including in Current mode, which keeps identity and layout stable.

The daemon protocol accepts any explicit non-empty scope set and the CLI exposes it through repeated `--scope`; therefore a later checkbox-based subset picker requires no storage or protocol redesign. There is no third “no scopes selected” state: an empty protocol array means All, while the GUI disables Current mode when no current scope exists.

### 5.2 Search semantics

- Query matches **record keys only**, never values. Scope names are filtered only inside the scope picker.
- Exact, fuzzy, regex, and case-sensitivity semantics remain consistent across any filter size.
- The result identity always includes `scope` and `key`.
- The candidate limit is global across the selected scope set, not multiplied per scope.
- Stable tie-breaking for a non-empty query is: match rank/score, then scope ordinal, then key ordinal.
- An empty query is browse mode and sorts scope ordinal, then key ordinal.
- A malformed regex shows one inline query error and leaves the query/filter intact.
- Requests are debounced; applying a different scope filter cancels/rejects stale results just as changing query text does.
- If the current scope is renamed or deleted elsewhere, refresh the selector against the current scope list and re-run the search. Never silently display results labeled with a stale scope.

### 5.3 New record while searching multiple scopes

Destination scope is inherently required by the domain. The first complete GUI keeps one explicit current scope selected in the header even while search coverage is **All scopes**, and **New record** saves into that current scope. It never guesses a first scope when none is selected. A future arbitrary-subset picker should add an explicit destination field rather than infer one from several checked scopes.

### 5.4 CLI observable contract

Because compatibility with the unreleased implementation is not required, the coherent CLI search is:

```text
scrap find <query> [--scope <name>]... [--exact|--fuzzy|--regex] [--case-sensitive]
```

- no `--scope` means all scopes;
- repeated `--scope` means the explicit union of those scopes;
- an unknown explicitly named scope is an input/not-found error rather than being ignored;
- text output includes unambiguous `scope<TAB>key` rows;
- JSON returns `scope`, `key`, `presentation`, and stable metadata, but never `value` or an exposed pseudo-probability score.

## 6. Theme and visual system

### 6.1 One source of visual truth

MoeSegFault Style is the visual source of truth. Pin a concrete released version rather than following `latest` in production.

- The website imports the version-pinned `@moesegfault/style` package or `/vX.Y.Z/css/all.css`.
- The public npm package was not available when the accompanying platform research was performed. The immediate build must therefore use an exact-version CDN asset or a license-compliant vendored snapshot with its version/checksum recorded; it must not depend on a package that CI cannot resolve.
- The desktop app consumes a checked-in/generated Avalonia mapping from the same versioned token JSON/DTCG source. It must not approximate the palette by eye.
- Preserve semantic names in the mapping: background, background-end, surface, surface-strong, surface-muted, text, text-soft, heading, accent, accent-strong, accent-wash, on-accent, border, focus, success, danger, and neutral.
- Record the upstream style version and source URL next to the generated mapping so future upgrades are reviewable.

The current design system's visual character is **warm paper, brown ink, and coral/berry emphasis**. Pink/purple “Dracula-like” colors are not substitutes.

### 6.2 Theme selection and readability

Both desktop and website expose a three-state control: **System / Light / Dark**.

- First run: System.
- Explicit user choice persists locally and overrides later OS changes.
- System mode reacts to OS/browser color-scheme changes while the app/page is open.
- Theme choice is independent of language.
- The website applies the resolved theme before first paint to avoid a light/dark flash.
- Every foreground/background pair meets WCAG 2.2 AA: 4.5:1 for normal text and 3:1 for large text and essential UI boundaries. Decorative accent colors are not automatically valid body-text colors; use `accent-strong` or ordinary text tokens where required.
- In the pinned v0.1.2 light palette, `on-accent` over `accent` is only about 3.11:1. Normal-size primary-button text therefore uses the opaque `accent-strong` fill with `on-accent` (about 5.00:1), pending an upstream semantic-pair correction. Platform consistency must not copy a measured contrast defect.
- Keyboard focus is clearly visible in both themes and never communicated only by color.
- Motion respects the platform/browser reduced-motion preference.

## 7. Internationalization

### 7.1 Supported locales and selection

The initial complete locale set is `zh-CN` and `en`.

| Surface | First-use locale | Persistence | Fallback |
|---|---|---|---|
| Desktop | Exact OS match, then language match; otherwise English | User config | `en` key; missing-key tests fail CI |
| Website | `/` is Simplified Chinese; `/en/` is English; no forced redirect | URL is canonical; remember preference only for the switcher's next target | English content source |
| Installer | Windows UI language when supported | Installer session | English |

- Changing desktop language applies immediately to the current window and open dialogs; it must not require restart.
- User data (scope names, keys, values), paths, commands, and error diagnostics from the daemon are not translated as content. UI wrappers and known error codes are translated.
- Dates, counts, plurals, and list conjunctions use locale-aware formatting; do not interpolate English sentence fragments.
- Controls contain one locale only. Delete all combined strings such as `"隐藏 Hide"` and `"记录已保存 / Record saved"`.
- Controls must tolerate at least 40% text expansion without clipping. Keyboard access uses stable gestures rather than translated letter mnemonics.
- Website pages set `lang`, canonical URL, and reciprocal `hreflang` (`zh-CN`, `en`, `x-default`) metadata.

### 7.2 String quality

Voice is calm, concise, and technically honest. Chinese should read as native product prose, not a word-for-word English mirror. English should not retain untranslated “scope” where “scope” is not the explicit domain term. Cute touches may appear in empty states or illustration, never in destructive confirmations, errors, or accessibility names.

## 8. Public release landing page

### 8.1 Page goal and non-goals

The page has one primary conversion: **download the latest suitable release**. Secondary conversion is visiting the GitHub repository.

It is **not** the architecture document, command reference, API reference, changelog archive, or contribution guide. Those remain linked unobtrusively in the footer/GitHub repository.

### 8.2 Content hierarchy

Use the following single-page structure in both locales:

1. Header: icon + `scrap`; anchors for Highlights and Download; GitHub; language and theme controls.
2. Hero: outcome-led headline, short lead, Windows primary CTA, Other platforms secondary CTA, actual product screenshot.
3. Trust strip: Local-first / Encrypted values / GUI + CLI / Open source.
4. Three product stories: multi-scope retrieval, understandable masking, GUI plus scriptable CLI.
5. Light/dark screenshot pair showing the real shipped application and real localized strings.
6. Download section populated from release metadata, with version, platform, architecture, file type, size, and checksum/signature affordance.
7. Final CTA and restrained footer: license, GitHub, technical details, privacy statement.

Do not add a documentation sidebar, table of contents, API cards, or an installation-script tutorial to the main page.

### 8.3 Approved core copy

| Block | `zh-CN` | `en` |
|---|---|---|
| Eyebrow | 本地优先 · GUI + CLI | LOCAL-FIRST · GUI + CLI |
| Hero title | 把零散的秘密，收好，也随手可取。 | Keep small secrets close—and find them fast. |
| Hero lead | `scrap` 在你的电脑上保存令牌、密码与短文本。用图形界面快速检索和复制，用 CLI 接进脚本；值经过加密，不送往云端。 | `scrap` keeps tokens, passwords, and short text on your computer. Find and copy them in the desktop app, or compose them into scripts with the CLI—encrypted at rest, without a cloud account. |
| Primary CTA | 下载 Windows 版 | Download for Windows |
| Secondary CTA | 查看其他平台 | Other platforms |
| Story 1 title | 找得到，才算收好了。 | Stored is only useful when it is findable. |
| Story 1 body | 一次检索一个、多个或全部 scope；相同 key 也会带着归属清楚出现。 | Search one, several, or every scope. Duplicate keys remain clear because their scope travels with them. |
| Story 2 title | 该藏的藏，该看的清楚。 | Mask what should stay quiet. |
| Story 2 body | 遮罩只决定默认怎么显示，不会假装成另一种加密。编辑时想看就看，保存策略互不干扰。 | Masking controls the default view, not a different kind of encryption. Showing a value while editing never changes how it is saved. |
| Story 3 title | 写给人，也写给工具。 | Friendly to people. Predictable for tools. |
| Story 3 body | 桌面端适合发现与复制；CLI 保持干净的输出、明确的退出码和可组合的行为。 | The desktop app is made for finding and copying; the CLI keeps output clean, exits explicit, and pipelines composable. |
| Final CTA | 从下一次找 token 开始，少翻一个角落。 | Spend less time remembering where that token went. |

Claims are constrained deliberately: say **local-first**, not “offline forever”; **encrypted at rest**, not “unhackable”; and **best-effort clipboard clearing**, not “secure clipboard.”

### 8.4 Download behavior

- Windows is the primary CTA only on Windows; on other platforms the primary CTA reflects the detected OS but always leaves all platforms visible. Detection is a convenience, never a gate.
- The page uses build-time or checked-in release metadata rather than fragile client-side GitHub API calls. A failed metadata refresh must leave the last known valid download section, not an empty hero.
- Each asset card states actual support. Do not call an archive an installer.
- The Windows CTA targets the signed per-user installer. It does not ask the user to open PowerShell.
- “Latest” and displayed version must refer to the same GitHub Release. Prereleases appear only behind an explicit prerelease choice.
- Checksums remain available as a secondary verification action; they are not the primary onboarding path.

### 8.5 Visual and responsive behavior

- Consume MoeSegFault Style rather than recreating a parallel design system.
- Use the product icon, warm-paper visual field, one restrained sparkle motif, and real UI imagery. Do not use stock “cybersecurity” shields, locks, or neon gradients.
- Hero copy remains readable at approximately 45–65 Latin characters per line (appropriate Chinese visual measure), and the overall reading column does not span the full 1180px container.
- At 320 CSS px width there is no horizontal page scroll; download cards and screenshots stack; tap targets are at least 24×24 CSS px and have adequate spacing.
- The page remains useful with JavaScript disabled: copy, product story, screenshots, GitHub link, and platform download links are server-rendered/static. Theme/language enhancement may use minimal script.

### 8.6 Product icon direction

Use one distinctive mark everywhere rather than a different favicon, executable icon, and installer graphic:

- a simple warm-paper scrap silhouette with a coral folded/torn corner and one cocoa key/value stroke;
- no letterform, padlock, shield, terminal prompt, or fine detail that disappears at 16px;
- light and dark contexts change the surrounding tile/outline treatment, not the core silhouette;
- the MoeSegFault sparkle may accompany the mark in hero art but is not part of the 16px core glyph.

The canonical editable source is SVG. Derived assets include Windows ICO sizes (16, 20, 24, 32, 48, 64, and 256px), macOS/Linux/app PNG sizes as required, website favicon, 180px Apple touch icon, 192/512px web icons, and an Open Graph image. Raster exports must be generated from the canonical source and visually inspected at 16, 32, and 256px.

### 8.7 Domain and metadata

- Astro declares the exact production `site` as `https://scrap.moesegfault.dev`; GitHub Pages repository settings and DNS are the authoritative custom-domain configuration and enforce HTTPS after verification. A `public/CNAME` may document intent but is not treated as the control plane for an Actions deployment.
- Localized title, description, Open Graph, and social-card metadata use the copy and icon from this specification.
- `robots.txt` and a sitemap include both locale URLs. Preview deployments and noncanonical GitHub-host URLs are not indexed as competing pages.
- A deployment smoke test requests the custom-domain URL over HTTPS and verifies the expected page title after Pages deployment.

## 9. Installer and Windows trust: product-facing requirements

An `.exe`/`.msix` wrapper alone does not solve Windows trust prompts. Microsoft reputation systems consider signing and reputation; the following is therefore a release dependency, not UI polish.

### 9.1 Required Windows experience

- A per-user graphical installer installs the GUI, daemon, CLI, icon, Start menu entry, and uninstall entry without administrator elevation.
- CLI PATH integration is an explicit installer option and explains that new terminals are required.
- Upgrade is in-place and preserves data. Ordinary uninstall preserves data; a separately confirmed purge removes it.
- Installer UI supports `zh-CN` and `en` and uses the product name/icon consistently.
- Release CI verifies Authenticode signature, publisher identity, timestamp, installer start, clean install, upgrade, launch, and uninstall on Windows.
- The website may display “Verified publisher” only after that verification passes.

### 9.2 Consequential external decision

A trusted code-signing route and publisher identity must be supplied by the project owner (for example, an appropriate certificate/signing service or a store distribution route). CI can integrate credentials, but it cannot manufacture reputation. Until this exists, the release page must state the artifact is unsigned rather than instruct users to bypass SmartScreen or Smart App Control.

## 10. Failure and edge-state behavior

| State | Required behavior |
|---|---|
| No scopes | Search area explains that records need a scope; primary action creates the first scope. |
| Scopes exist, no records | Keep filter/search available; New Record is the primary empty-state action. |
| Multi-scope duplicate keys | Show each row with scope; selection/get/delete always carries exact `(scope, key)`. |
| No matches | Preserve query and filter; distinguish “no keys match” from “there are no records.” |
| Invalid regex | Inline localized error near the query; no stale candidates; no modal. |
| Daemon unavailable | Localized actionable retry state; theme/language/settings remain usable. |
| Missing translation | CI failure, not a visible fallback key in a release build. |
| Unsupported system locale | English UI; user can choose Simplified Chinese manually. |
| Website release metadata unavailable at build time | Publish last-known valid metadata with a visible version, or fail the deployment before replacing the good site. |
| Unsupported/unknown visitor OS | Show neutral “Choose a download” CTA and all asset cards. |

## 11. Acceptance criteria

### 11.1 Record editor

- **Given** a new-record dialog, **when** the user toggles the eye button, **then** only editor visibility changes and the pending saved policy remains Masked.
- **Given** any editor visibility, **when** the user chooses Visible or Masked, **then** the characters currently shown/hidden do not change.
- **Given** a masked record, **when** it is revealed, **then** it remasks after 15 seconds and immediately on selection change or app deactivation.
- **Given** either presentation, **then** helper text explicitly says storage encryption is identical.
- Automated view-model tests cover the full state table in section 4.2.

### 11.2 Multi-scope search

- Search works with All and Current in the GUI, and with All, one, and at least two explicit scopes through the protocol/CLI, for exact, fuzzy, and regex modes.
- Two records with the same key in different scopes render as distinguishable rows and open/delete the correct identity.
- Results outside the explicit scope set never appear.
- Empty query ordering and non-empty tie-breaking are deterministic across repeated runs.
- Applying a new filter cannot be overwritten by a slower response from the previous filter.
- CLI tests cover absent and repeated `--scope`, unknown scopes, text identity, and secret-free JSON.

### 11.3 Theme and locale

- All desktop screens and website sections render in Light and Dark and follow a live System-mode change.
- A semantic-token snapshot test detects accidental drift from the pinned MoeSegFault Style version.
- Automated contrast checks cover all text/interactive semantic pairings; keyboard-only smoke tests show visible focus.
- Every user-facing string appears from a locale resource, and locale-key parity tests pass for `zh-CN` and `en`.
- Switching language does not modify user data, search mode/filter, theme, editor contents, or the open dialog's pending choices.
- No release UI displays concatenated bilingual copy.

### 11.4 Landing and release

- `https://scrap.moesegfault.dev/` serves Simplified Chinese and `/en/` serves English with correct canonical/hreflang metadata.
- Both routes pass a production build, internal-link check, and automated accessibility smoke check in GitHub Actions.
- Theme is correct at first paint for a saved/system preference; the language switch keeps the equivalent page/anchor.
- The primary CTA and download cards resolve to assets in the same latest stable GitHub Release.
- The page uses real app screenshots and the same product icon shipped in the installer/executable.
- The icon source and all required raster/ICO outputs exist, and 16/32/256px visual checks show no clipped or indistinguishable mark.
- A Windows release is installable by normal GUI interaction; no product-page onboarding step asks the user to run a script.
- Signature/trust claims are conditioned on CI verification and never hard-coded.

## 12. Priority and scope control

### P0: release-blocking

1. Independent masked-policy/editor-visibility model and localized copy.
2. All/Current GUI search plus all/explicit-set protocol and CLI search identity end to end.
3. `zh-CN`/`en` resources and System/Light/Dark themes in desktop and site.
4. Version-pinned MoeSegFault semantic tokens.
5. Product icon across app, installer, site, and release assets.
6. Normal Windows installer plus honest signing/trust handling.
7. Product landing page, GitHub Pages custom domain, release-driven downloads, and CI checks.

### P1: desirable after the complete release

- Remembered recent destination scopes in the New Record picker.
- Arbitrary-subset checkbox picker in the GUI, reusing the existing set-valued protocol.
- Dedicated keyboard shortcut for opening the scope filter.
- macOS signed/notarized `.app`/`.dmg` and native Linux packages.
- Locale expansion beyond `zh-CN` and `en`.

### Explicit non-goals

- Searching record values.
- Cloud sync, accounts, or web access to stored data.
- A web documentation portal disguised as the release page.
- Automatic locale redirects based on IP or browser language.
- A different encryption scheme for Visible records.
- Telling users to disable or bypass Windows protection.

## 13. Ambiguities resolved and remaining decision

| Question | Product decision |
|---|---|
| Does “masked” mean more encryption? | No. It is a saved presentation/clipboard policy only. |
| Does showing text in the editor make a record Visible later? | No. The states are independent. |
| Is scope optional because the record no longer belongs to one? | No. Scope remains required for identity; the *search restriction* is optional. |
| What does an omitted search scope mean? | All scopes. |
| Should results hide scope when only one is selected? | No. Always showing it keeps identity and layout stable. |
| Which language owns `/`? | Simplified Chinese, consistent with the MoeSegFault Style site; English is `/en/`. |
| Is dark mode the accessible mode? | No. System, Light, and Dark are user choices; each must independently meet readability requirements. |
| Can an unsigned installer be described as fixing Windows blocking? | No. Packaging and trusted signing/reputation are separate requirements. |
| Remaining owner decision | Select/provision the Windows code-signing publisher and credential route. This changes the achievable trust claim and cannot be inferred from repository code. |

## 14. External evidence and implementation references

These references support the design constraints; they do not replace product validation with real users.

- [MoeSegFault Style foundations](https://style.moesegfault.dev/foundations/) defines the semantic roles, typography/rhythm, and root theme attribute. [Its distribution page](https://style.moesegfault.dev/distribution/) recommends pinning a released version for production.
- [MoeSegFault Style v0.1.2 token JSON](https://style.moesegfault.dev/v0.1.2/tokens/tokens.json) is the concrete cross-platform palette source inspected for this specification.
- [Avalonia theme variants](https://docs.avaloniaui.net/docs/guides/styles-and-resources/how-to-use-theme-variants) and [Avalonia localization guidance](https://docs.avaloniaui.net/docs/guides/implementation-guides/localizing) provide the production framework mechanisms; they should be used instead of parallel hard-coded views.
- [W3C WCAG 2.2 contrast guidance](https://www.w3.org/WAI/WCAG22/Understanding/contrast-minimum.html), [Focus Appearance](https://www.w3.org/WAI/WCAG22/Understanding/focus-appearance.html), and [Target Size](https://www.w3.org/WAI/WCAG22/Understanding/target-size-minimum.html) support the measurable readability and input criteria.
- [Astro internationalization routing](https://docs.astro.build/en/guides/internationalization/) and [GitHub Pages custom-domain guidance](https://docs.github.com/en/pages/configuring-a-custom-domain-for-your-github-pages-site/about-custom-domains-and-github-pages) cover the chosen static-site delivery shape.
- [Microsoft Smart App Control and code signing](https://learn.microsoft.com/en-us/windows/apps/develop/smart-app-control/overview) explains why an installer filename alone cannot establish Windows trust.
- A 134-participant visualization study found substantial individual variation: each polarity benefited comparable proportions of participants, and measured performance did not always match preference. Zack While and Ali Sarvghad, *Dark Mode or Light Mode? Exploring the Impact of Contrast Polarity on Visualization Performance Between Age Groups*, IEEE VIS 2024, [DOI: 10.1109/VIS55277.2024.00050](https://doi.org/10.1109/VIS55277.2024.00050). This supports offering preference while validating both themes; it does **not** establish that either theme is categorically more accessible.

### Evidence strength and falsification

- **Strong:** repository observations, formal identity model, published style tokens, platform/framework behavior, and testable WCAG criteria.
- **Moderate:** the proposed control labels and All/Explicit filter model follow established separation-of-concerns and faceted-search practice but still need usability testing.
- **Hypothesis to test:** users will understand “Default display” plus a separate eye button better than two checkboxes. A five-user moderated test should require creating one masked token and one visible endpoint, then editing each. Revise if more than one participant changes the wrong saved policy or cannot predict post-save appearance.
- **Hypothesis to test:** All scopes is the best first-use default. Instrument only local, opt-in usability sessions (not production secret telemetry) and revise if users consistently find the global result set noisy or slower than scope-first retrieval.
