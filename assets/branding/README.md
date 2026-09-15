# Scrap brand assets

The Scrap mark combines a folded paper scrap with a keyhole and a small sparkle. It is intended to remain recognizable at application-icon sizes while fitting the warm-paper and coral palette published by MoeSegFault Style.

## Files

- `scrap-icon-source.png`: generated source artwork with transparency.
- `scrap-icon-{size}.png`: raster exports for web and platform packaging.
- `src/Scrap.Gui/Assets/scrap.ico`: Windows multi-resolution application icon.
- `src/Scrap.Gui/Assets/scrap.png`: Avalonia window/application resource.

## Usage contract / 使用约定

- Preserve the original aspect ratio and transparent background. / 保持原始宽高比与透明背景。
- Keep enough clear space around the mark; do not place text over it. / 图标四周保留留白，不要叠加文字。
- Prefer the 32 px or larger raster export; use the ICO for Windows executables and installers. / 位图优先使用 32 px 以上版本；Windows 可执行文件和安装器使用 ICO。

The source artwork was generated specifically for this project with OpenAI's image-generation tool on 2026-09-15. It contains no third-party logo or typeface.
