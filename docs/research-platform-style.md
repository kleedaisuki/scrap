# Platform style, theming, i18n, Pages, and release research

**Research date:** 2026-09-15
**Scope:** directly inspect `style.moesegfault.dev` and its upstream repository, then translate the findings into implementation decisions for the Scrap desktop application, product page, and release pipeline. Observations below are separated from recommendations where that distinction matters.

## 1. Executive decisions

1. **Pin MoeSegFault Style `v0.1.2`** rather than consuming `/latest/`. It is the current default/latest version in the [published manifest](https://style.moesegfault.dev/manifest.json), and the design-system site itself recommends exact versions for production.
2. **Use semantic tokens, not copied control-level colors.** For the web page, consume or vendor the exact-version `tokens.css`; for Avalonia, generate/check in a `ThemeDictionaries` resource dictionary from the exact-version JSON token artifact. This keeps the semantic contract shared while allowing native controls to remain native.
3. **Offer `System`, `Light`, and `Dark`, defaulting to `System`.** Store the preference, not only the currently resolved color scheme. Use Avalonia `DynamicResource` for theme-varying resources.
4. **Ship Simplified Chinese and English as complete locale sets.** On desktop, use strongly typed `.resx` plus an observable localization service if switching without restart is required. On the website, use stable locale URLs: Chinese at `/`, English at `/en/`, with an explicit switcher and alternate metadata.
5. **Do not reuse the MoeSegFault brand mark unchanged as Scrap's product identity.** Create a Scrap-specific icon in the same coral/gold/warm-paper visual language. Reusing the gold sparkle as a secondary motif is consistent; the existing `brand.svg` specifically identifies MoeSegFault.
6. **Publish an actual signed Windows installer.** Package first-class per-user install/uninstall behavior, Authenticode-sign executable payloads and the final installer, RFC 3161 timestamp them, verify signatures in CI, and only then produce hashes/attestations and upload release assets. An unsigned installer cannot reliably eliminate Windows warnings.
7. **Deploy the static Astro page with GitHub Pages Actions.** Configure `site: "https://scrap.moesegfault.dev"` and no `base`; set the Pages custom domain in repository settings and point the DNS CNAME to `kleedaisuki.github.io` (not a repository path).

## 2. MoeSegFault Style: observed source of truth

The live site describes the system as a **warm-paper editorial style**: warm cream backgrounds, brown ink, coral emphasis, and a restrained gold sparkle. The current site and upstream repository expose CSS, flattened JSON, and Design Tokens Community Group (DTCG) JSON. DTCG is useful as an exchange format, but its 2025.10 document is a Candidate Recommendation rather than a W3C Standard ([DTCG format status](https://www.designtokens.org/tr/2025.10/format/)).

### Stable production resources

| Purpose | Exact-version resource |
| --- | --- |
| Full CSS | [`/v0.1.2/css/all.css`](https://style.moesegfault.dev/v0.1.2/css/all.css) |
| Tokens only | [`/v0.1.2/css/tokens.css`](https://style.moesegfault.dev/v0.1.2/css/tokens.css) |
| Foundations | [`/v0.1.2/css/foundation.css`](https://style.moesegfault.dev/v0.1.2/css/foundation.css) |
| Components | [`/v0.1.2/css/components.css`](https://style.moesegfault.dev/v0.1.2/css/components.css) |
| Flattened tokens | [`/v0.1.2/tokens/tokens.json`](https://style.moesegfault.dev/v0.1.2/tokens/tokens.json) |
| DTCG tokens | [`/v0.1.2/tokens/tokens.dtcg.json`](https://style.moesegfault.dev/v0.1.2/tokens/tokens.dtcg.json) |
| Brand SVG | [`/v0.1.2/assets/icons/brand.svg`](https://style.moesegfault.dev/v0.1.2/assets/icons/brand.svg) |
| Sparkle SVG | [`/v0.1.2/assets/icons/sparkle.svg`](https://style.moesegfault.dev/v0.1.2/assets/icons/sparkle.svg) |

The [distribution page](https://style.moesegfault.dev/distribution/) distinguishes immutable exact-version paths from moving `/latest/`, `/css`, and `/tokens` aliases. Use exact-version assets in production and make an explicit dependency update to adopt later versions.

**Package availability caveat:** the public npm registry returned `404` for `@moesegfault/style` on the research date, even though the [integration guide](https://style.moesegfault.dev/guides/) documents `pnpm add @moesegfault/style`. Therefore, do not make the current build depend on a successful public npm install until registry availability is verified. The safe immediate choices are (a) a pinned CDN stylesheet or (b) committed, license-compliant vendored token/foundation artifacts with recorded version and checksum. Vendoring only the small token/foundation layers avoids importing unused rich-text/font assets.

### Semantic color contract

Values below come directly from the flattened `v0.1.2` token artifact.

| Semantic role | Light | Dark | Intended use |
| --- | ---: | ---: | --- |
| `background` | `#fff6ea` | `#21130f` | Window/page canvas |
| `background-end` | `#fff1e2` | `#19100e` | Optional background gradient stop |
| `surface` | `#fffbf4eb` | `#2f1c17f0` | Translucent/elevated surface |
| `surface-strong` | `#fffdf8` | `#30201b` | Opaque cards, inputs, dialogs |
| `surface-muted` | `#fff1dd` | `#3c241d` | Secondary selected/quiet surface |
| `text` | `#4b2a1e` | `#f6e7dc` | Primary body text |
| `text-soft` | `#805648` | `#d9b8a6` | Secondary text, not disabled text |
| `heading` | `#26110b` | `#fff6ef` | Headings/high-emphasis text |
| `accent` | `#e66a3f` | `#f27a50` | Emphasis, fills, state accents |
| `accent-strong` | `#bd4525` | `#ffb18c` | Links/strong emphasis |
| `accent-wash` | `#ffe1cccc` | `#853e2a6b` | Tinted background |
| `on-accent` | `#fffaf2` | `#2b1711` | Foreground on accent fills |
| `border` | `#f3bf98bd` | `#a06345b3` | Dividers and boundaries |
| `focus` | `#542516` | `#ffe0a3` | Keyboard focus indication |
| `success` | `#4daf76` | `#4daf76` | Success state |
| `danger` | `#d65345` | `#d65345` | Destructive/error state |
| `neutral` | `#9c8d86` | `#9c8d86` | Neutral status/disabled support |

Base primitives:

- Cream palette: `#fffdf8`, `#fffaf2`, `#fff6ea`, `#fff1e2`, `#ffe4c3`.
- Brown/ink palette: `#d9b8a6`, `#805648`, `#4b2a1e`, `#30201b`, `#21130f`, `#19100e`, ink `#26110b`.
- Coral palette: `#ffb18c`, `#ff8569`, `#f27a50`, `#e66a3f`, `#bd4525`; gold `#ffb55e`.
- Spacing: `0, 4, 8, 12, 16, 24, 32, 48, 64px`; radii: `10, 18, 26px`, plus pill.
- Motion: `160/220/420ms`, standard easing `cubic-bezier(0.2, 0, 0, 1)`, emphasized easing `cubic-bezier(0.2, 0.8, 0.2, 1)`.
- Layout: `1180px` content maximum and `720px` reading width.
- Font tokens: Atkinson Hyperlegible/Noto Sans SC/system sans for UI; Georgia/Noto Serif SC for display; IBM Plex Mono/Cascadia Mono/system mono. The component package separately recommends a self-hosted JetBrains Mono for code, so the UI token and code-block override should not be conflated.

### Accessibility check and required local correction

Independent WCAG relative-luminance calculations over the opaque token pairs gave:

| Pair | Ratio | WCAG 2.2 AA normal text |
| --- | ---: | --- |
| Light `text` / `background` | `11.90:1` | Pass |
| Light `text-soft` / `background` | `5.87:1` | Pass |
| Light `accent-strong` / `background` | `4.85:1` | Pass |
| Light `on-accent` / `accent` | `3.11:1` | **Fail** |
| Dark `text` / `background` | `14.92:1` | Pass |
| Dark `text-soft` / `background` | `9.75:1` | Pass |
| Dark `on-accent` / `accent` | `6.22:1` | Pass |

WCAG 2.2 requires `4.5:1` for normal text and `3:1` only for qualifying large text ([W3C explanation](https://www.w3.org/WAI/WCAG22/Understanding/contrast-minimum)). The shipped component CSS uses `on-accent` over an `accent` to `accent-strong` gradient, so the light endpoint is insufficient for ordinary button text even though the main reading colors are strong. **Platform consistency does not justify copying this defect.** For normal-size light-theme CTAs, use an opaque `accent-strong` fill with `on-accent` (`5.00:1`), or have the upstream palette revise the paired semantic values. Re-test alpha surfaces after compositing against their actual backgrounds; raw eight-digit hex values cannot be evaluated in isolation.

Also keep meaning independent of color (label/icon/shape in addition to red/green), preserve an obvious focus ring, and test at 200% text scaling and in high-contrast/forced-color modes.

### Brand assets and product icon direction

The upstream [`brand.svg`](https://github.com/kleedaisuki/moesegfault-style/blob/1de514bfb53731fd4eda76ab10ab41aff41dc136/packages/style/src/icons/brand.svg) is a 64x64 rounded coral-gradient tile with a white **K** and a gold sparkle. [`sparkle.svg`](https://github.com/kleedaisuki/moesegfault-style/blob/1de514bfb53731fd4eda76ab10ab41aff41dc136/packages/style/src/icons/sparkle.svg) extracts the sparkle and uses `currentColor`. The [asset README](https://github.com/kleedaisuki/moesegfault-style/blob/1de514bfb53731fd4eda76ab10ab41aff41dc136/packages/style/src/icons/README.md) says both retain GPL-3.0-or-later and that `brand.svg` preserves the existing MoeSegFault/blog identity.

For Scrap, design a distinct square master SVG using the same coral gradient, cream foreground, and optional gold sparkle—e.g. an abstract clipped scrap/card or scoped key-value glyph—rather than relabeling the K mark. Export at least a multi-resolution Windows `.ico` (including 16, 24, 32, 48, and 256 px), application/window assets for every desktop target, web favicon(s), and an Open Graph/social image. Embed the icon into executables and the installer/Add-or-Remove-Programs entry, not only the window chrome.

## 3. Desktop theme and localization implementation

### Avalonia theme architecture

The official Avalonia guidance defines `Default` as following the operating system and recommends `ThemeDictionaries` plus `DynamicResource` for values that must change at runtime ([theme switching](https://docs.avaloniaui.net/docs/how-to/theme-switching-how-to), [resource behavior](https://docs.avaloniaui.net/docs/app-development/resources)). Implement one semantic layer:

```text
Moe token artifact (pinned v0.1.2)
          |
          v  build-time generation / reviewed mapping
ScrapTheme.axaml
  ThemeDictionaries: Light, Dark
          |
          v  DynamicResource only for theme-dependent values
native Avalonia controls and Scrap component styles
```

Suggested stable Avalonia keys are `ScrapBackgroundBrush`, `ScrapSurfaceBrush`, `ScrapSurfaceStrongBrush`, `ScrapTextBrush`, `ScrapTextSoftBrush`, `ScrapHeadingBrush`, `ScrapAccentBrush`, `ScrapAccentStrongBrush`, `ScrapOnAccentBrush`, `ScrapBorderBrush`, `ScrapFocusBrush`, `ScrapSuccessBrush`, and `ScrapDangerBrush`. Do not scatter raw hex values through views. Keep structural resources/templates static; only theme-varying resources need dynamic lookup.

Persist a typed preference enum `{ System, Light, Dark }`; map `System` to `ThemeVariant.Default`. Persisting only the resolved light/dark value would prevent the application from following later OS changes. Apply the preference at application scope so window decorations follow where supported, and test both startup and live switching.

Windows guidance independently reinforces the same architecture: use semantic theme resources rather than hard-coded colors, and do not let custom styling block high-contrast overrides ([Windows theming](https://learn.microsoft.com/en-us/windows/apps/develop/ui/theming), [contrast themes](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/high-contrast-themes)).

### Desktop i18n architecture

Avalonia's official localization guide supports strongly typed `.resx`, culture-specific resources, runtime culture changes, RTL `FlowDirection`, and culture-aware formatting ([Avalonia ResX guide](https://docs.avaloniaui.net/docs/app-development/localizing)). Recommended contract:

- `Resources.resx`: invariant English fallback; `Resources.zh-CN.resx`: complete Simplified Chinese translation.
- A typed user preference `{ System, zh-CN, en }`, separate from number/date formatting culture where necessary.
- An `ILocalizer`/view-model facade implementing `INotifyPropertyChanged` for runtime switching. `x:Static` resolves once and therefore does not live-update without view recreation.
- Whole-message resource strings with translator context; do not assemble sentences by concatenating fragments.
- No localized text baked into icons/screenshots. Allow controls and dialogs to expand; test long English/Chinese strings, clipping, keyboard traversal, screen-reader names, and future RTL even if RTL is not a first-release locale.
- Keep protocol/CLI machine tokens stable and locale-neutral; localize human-facing GUI strings and opt-in CLI diagnostics without changing parsable output contracts.

## 4. Astro product page and GitHub Pages

### Product-page structure

This is a launch page, not documentation. A useful static outline is: promise-led hero; a short “why Scrap” problem statement; three concrete product moments (scoped retrieval, masked local records, native desktop workflow); trust/local-first explanation; platform downloads; a final concise call to action. Avoid API tables and exhaustive command references on the landing page.

Use Astro's built-in i18n routing or a small typed content dictionary. Astro supports configured locales/default locale and localized file routes ([official i18n routing](https://docs.astro.build/en/guides/internationalization/)). For this product:

- `/` is `zh-CN`; `/en/` is English.
- Each page emits the correct `<html lang>`, localized title/description, canonical URL, and `rel="alternate" hreflang="zh-CN|en|x-default"`.
- Use a visible text language switcher (“EN” / “简体中文”), not national flags.
- Do not silently redirect a static visitor based on browser language; stable shareable URLs and an explicit choice are more predictable.
- Theme initialization should run in `<head>` before stylesheet evaluation: read `light|dark|auto`, resolve `auto` from `prefers-color-scheme`, set `data-moe-theme` and `color-scheme`, and keep the `theme-color` meta synchronized. The upstream library exposes the same preference model in [`theme.ts`](https://github.com/kleedaisuki/moesegfault-style/blob/1de514bfb53731fd4eda76ab10ab41aff41dc136/packages/style/src/theme.ts).
- Respect `prefers-reduced-motion`; the upstream foundation already suppresses animation/transition durations in reduced-motion mode.

### Pages deployment contract

Astro recommends its official Pages action, with a committed package-manager lockfile, followed by `actions/deploy-pages` ([Astro Pages guide](https://docs.astro.build/en/guides/deploy/github/)). Configure:

```js
export default defineConfig({
  site: "https://scrap.moesegfault.dev",
  // No base: a custom domain serves from its root.
});
```

Recommended workflow split:

1. Pull requests and pushes: frozen `pnpm install`, typecheck/lint/test, `astro build`, and a broken-link/accessibility smoke check.
2. Main-branch deployment: the same build output is uploaded as the Pages artifact; deploy in a separate `github-pages` environment job.
3. Minimum permissions: build job `contents: read`; deployment job `pages: write` and `id-token: write`. Pin actions to reviewed full commit SHAs; GitHub identifies full-SHA pinning as the immutable reference mechanism ([action pinning](https://docs.github.com/en/actions/how-tos/write-workflows/choose-what-workflows-do/find-and-customize-actions), [token permissions](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax)).

For the custom domain, set `scrap.moesegfault.dev` in **Settings -> Pages**, create a DNS `CNAME` for `scrap` pointing to `kleedaisuki.github.io`, then enable HTTPS after DNS validation. GitHub explicitly says the CNAME target excludes the repository name and that a custom Actions workflow does not require/rely on a source-branch `CNAME` file ([GitHub custom-domain guide](https://docs.github.com/en/pages/configuring-a-custom-domain-for-your-github-pages-site/managing-a-custom-domain-for-your-github-pages-site)). Astro's guide still suggests `public/CNAME`; including it may document intent, but repository Pages settings and DNS are authoritative for Actions deployments.

## 5. Installer and GitHub Release workflow

### Windows distribution reality

Microsoft documents that SmartScreen evaluates both publisher reputation and file-hash reputation. Unsigned and self-signed builds start without transferable publisher reputation; even a newly signed build may warn until reputation develops. EV certificates no longer receive automatic initial reputation. Microsoft Store distribution is the reliable no-SmartScreen-warning route, while Microsoft's Artifact Signing is the recommended signing service for non-Store distribution ([SmartScreen reputation guidance](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation)).

Therefore “make Windows never warn” is not achievable merely by wrapping the current binaries in an installer. The production sequence should be:

```text
test -> publish native payloads -> build installer
     -> Authenticode-sign payloads + installer
     -> RFC 3161 timestamp -> verify signatures
     -> installer smoke install/launch/uninstall
     -> hashes + provenance attestation
     -> draft GitHub Release -> attach all assets -> publish
```

Use SHA-256 for both file digest and RFC 3161 timestamp digest. Microsoft states that Authenticode timestamps keep signatures verifiable after certificate expiry ([timestamping guidance](https://learn.microsoft.com/en-us/windows/win32/seccrypto/time-stamping-authenticode-signatures)). Keep one stable verified publisher identity across releases and never modify binaries after signing.

**Eligibility constraint:** as of the research date, Artifact Signing Public Trust eligibility lists the US, Canada, EU, UK, Australia, New Zealand, Japan, South Korea, Singapore, Switzerland, Norway, and Israel—not mainland China ([setup prerequisites](https://learn.microsoft.com/en-us/azure/artifact-signing/quickstart)). Verify the actual publisher entity's eligibility before designing the CI workflow around that service. If ineligible, obtain a publicly trusted Authenticode certificate from an eligible commercial CA or pursue Microsoft Store distribution. Do not publish a nominal “signed” release using a self-signed certificate; Microsoft treats that like unsigned software for SmartScreen reputation.

### Release mechanics

- Keep ordinary CI read-only. Grant `contents: write` only to the release job; add `id-token: write` and `attestations: write` only if generating GitHub artifact attestations. GitHub's `actions/attest` supports build provenance for binary subjects ([artifact-attestation guide](https://docs.github.com/en/actions/how-tos/secure-your-work/use-artifact-attestations/use-artifact-attestations)).
- Build each OS/RID on its native runner, test the produced application rather than only the source build, and upload immutable intermediate artifacts. Assemble the release only after every required platform succeeds.
- Produce SHA-256 sidecars and a complete `SHA256SUMS`, but treat checksums as corruption detection, not publisher identity; signing and provenance provide identity.
- Prefer immutable Releases. GitHub recommends creating a draft, attaching all assets, then publishing; immutable releases lock assets and the tag and generate a release attestation ([GitHub immutable releases](https://docs.github.com/en/code-security/concepts/supply-chain-security/immutable-releases)). Consequently, do not design “rerun and overwrite published assets” as the normal recovery model.
- Test the Windows installer as a standard user: fresh install, repeat/upgrade install, launch GUI and CLI, PATH behavior in a new process, normal uninstall preserving user data, and explicit purge separately. Verify the displayed publisher, executable and installer signatures, version metadata, application icon, and Add/Remove Programs entry.

## 6. Acceptance checklist

| Area | Minimum evidence before release |
| --- | --- |
| Theme | Light, dark, and system-following startup/live-switch tests; no raw view colors; contrast/high-contrast review |
| i18n | Complete `zh-CN` and `en` key parity; live switching or documented restart; layout/keyboard tests in both locales |
| Desktop icon | Window/taskbar/executable/installer/ARP icons visible at small and large sizes |
| Product page | Both locale URLs build; correct `lang`, canonical, alternate links; theme has no visible startup flash |
| Pages | PR build test; main deployment; DNS CNAME to `kleedaisuki.github.io`; HTTPS enforced |
| Windows installer | Standard-user install/upgrade/uninstall smoke test; publisher signature and RFC 3161 timestamp verification |
| Release | Native artifacts tested; hashes generated after signing; attestations match final assets; draft assembled before publication |

## 7. Remaining external dependencies and uncertainty

1. A trusted Windows publisher identity/signing credential (or Store account) is an external prerequisite; code alone cannot manufacture SmartScreen reputation.
2. GitHub repository Pages settings and DNS must be changed outside the source tree. The source can prepare the correct `site` configuration and workflow but cannot prove the custom domain live without those settings.
3. The MoeSegFault package's npm publication status should be rechecked before choosing package installation over exact-version CDN/vendoring.
4. The light `on-accent` pairing should ideally be corrected upstream so all platform consumers receive an accessible semantic pair; until then, Scrap should apply the documented local CTA treatment and keep a regression test for the ratio.
