# Scrap release site maintenance

This Astro site publishes six static pages at `https://scrap.moesegfault.dev`: product, privacy, and support, each in Simplified Chinese and English. The Chinese pages live at `/`, `/privacy/`, and `/support/`; the English equivalents live under `/en/`. Preserve those URLs and the existing rendered product experience when changing internals.

## Source of truth

- `src/lib/` contains localized copy. Page components render that copy; `BaseLayout.astro` owns shared navigation, language alternatives, metadata, and theme preference.
- `src/pages/` defines public routes. `public/sitemap-0.xml` is deliberately static, so update it when routes change. `tests/publication.test.js` checks route coverage and the Pages `CNAME` host.
- `assets/branding/` is the canonical icon source. The four `public/scrap-icon-*.png` files are exact published copies, not independently edited designs.
- `store-listing/screenshots/{zh-CN,en-US}/` contains the canonical desktop captures. The two product-page screenshots per locale are exact published copies of `02-multi-scope-subset.png` and `03-create-record.png`. Preserve locale and dimensions; regenerate source captures through the documented capture workflow rather than painting over website copies.
- The root `pnpm-lock.yaml` and `website/package.json` are a single dependency contract. Do not update one without the other.

## Verification / 验证

Run `corepack pnpm install --frozen-lockfile` from the repository root, followed by `corepack pnpm --dir website run verify`. The latter performs Astro diagnostics, unit/invariant tests, and a production build. Run it before submitting site changes; CI remains the cross-platform acceptance gate.

在仓库根目录运行 `corepack pnpm install --frozen-lockfile`，随后运行 `corepack pnpm --dir website run verify`。后者依次检查 Astro 类型、测试发布资源不变量，并构建生产站点。修改网站后应执行；跨平台验收仍以 CI 为准。
