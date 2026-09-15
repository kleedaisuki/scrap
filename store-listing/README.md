# Microsoft Store submission kit

This directory is the copy/paste source of truth for the first **moeSegFault Scrap** Store submission. It intentionally separates customer-facing copy from certification answers so neither is improvised in Partner Center.

## Contents

| Path | Use |
|---|---|
| [`locales/zh-CN.md`](locales/zh-CN.md) | Simplified Chinese listing copy, features, and captions |
| [`locales/en-US.md`](locales/en-US.md) | English listing copy, features, and captions |
| [`submission.md`](submission.md) | Pricing, properties, age rating, privacy, and certification answers |
| [`assets/AppTileIcon-300x300.png`](assets/AppTileIcon-300x300.png) | 1:1 Store listing icon generated from the canonical brand source |
| [`assets/README.md`](assets/README.md) | Asset generation and screenshot capture contract |

## First-submission order

1. Upload the exact unsigned production MSIX produced by the manual Store workflow.
2. Complete pricing/properties/age-rating fields from `submission.md`.
3. Add both `zh-CN` and `en-US` listings, then paste text without Markdown decoration.
4. Upload the four localized 1366×768 PNG screenshots and the 300×300 listing icon for each locale.
5. Leave **What's new in this version** empty for both locales.
6. Paste the complete `runFullTrust` explanation and test steps into Submission options.
7. Run WACK on the exact candidate and inspect its XML/HTML report before submission.

Do not claim accessibility conformance, Store certification, telemetry-free third-party infrastructure, or signed direct downloads beyond what the current build and listing actually establish.

## Authoritative Microsoft guidance

- [Add and edit Store listing info for an MSIX app](https://learn.microsoft.com/windows/apps/publish/publish-your-app/msix/add-and-edit-store-listing-info)
- [App screenshots, images, and trailers for an MSIX app](https://learn.microsoft.com/windows/apps/publish/publish-your-app/msix/screenshots-and-images)
- [App capability declarations](https://learn.microsoft.com/windows/apps/package-and-deploy/app-capability-declarations)
- [Windows App Certification Kit](https://learn.microsoft.com/windows/uwp/debug-test-perf/windows-app-certification-kit)
