# Store visual assets

`AppTileIcon-300x300.png` is the 1:1 listing logo. It and every package icon are generated from `assets/branding/scrap-icon-source.png` without redrawing the mark:

```powershell
./build/generate-store-assets.ps1
```

The generator writes 100%, 200%, and 400% package scale variants, the recommended intermediate StoreLogo scales, and every taskbar/app-list target size in default, dark unplated, and light unplated forms. It verifies exact pixel dimensions, PNG format, visible content, transparency, and the absence of stale package PNG variants. `-ValidateOnly` performs the same assertions without modifying files and is the mode used during packaging.

## Screenshot capture contract

The checked-in `../screenshots/{zh-CN,en-US}` directories contain four **real** 1366×768 PNG screenshots per locale, matching the ordered captions in the locale files:

1. all-scope search with full scope/key identities;
2. an arbitrary multi-scope subset;
3. the record editor demonstrating independent visibility and saved masking policy;
4. masked detail reveal/copy plus a clearly legible alternate theme.

Regenerate them with `./build/capture-screenshots.ps1 -OutputDirectory store-listing/screenshots`. The harness uses disposable in-memory values, includes the complete application surface, and adds no marketing text or extra logo overlay. Do not reuse one locale's screenshot for the other locale. Each committed image is exactly 1366×768 PNG and below Microsoft's 50 MB limit.
