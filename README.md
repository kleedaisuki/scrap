# scrap

`scrap` 是面向开发者的 local-first 本地秘密与短文本字段存储：桌面端负责快速发现与复制，CLI 负责稳定的脚本组合，`scrapd` 则是数据与加密操作的唯一权威。

产品发布页：<https://scrap.moesegfault.dev> · 下载：[GitHub Releases](https://github.com/kleedaisuki/scrap/releases)

## 为什么是 scrap

- **跨 scope 找到记录**：精确、模糊或正则检索可以覆盖一个、多个或全部 scope；结果始终携带完整的 `scope / key` 身份。
- **遮罩语义清楚**：Masked/Visible 只决定详情页默认怎样展示；编辑框的“显示值”只是临时状态。两者使用完全相同的加密存储。
- **适合人，也适合管道**：GUI 提供检索、显示与复制；CLI 保持干净的 stdout、明确的 stderr 和稳定退出码。
- **不依赖云账户**：value 加密后进入本机 SQLite，主密钥交由操作系统凭据设施保护。
- **可读的桌面体验**：界面采用 MoeSegFault Style `v0.1.2` 语义色，支持跟随系统/浅色/深色主题，以及简体中文和英文即时切换。

## 安装

### Windows

从 [Releases](https://github.com/kleedaisuki/scrap/releases) 下载 `scrap-vX.Y.Z-win-x64.msi`，双击完成当前用户安装；不需要运行 PowerShell，也不需要管理员权限。MSI 安装桌面端、CLI 与 daemon，并添加开始菜单和卸载入口。普通卸载保留 `~/.scrap` 中的用户数据。

> **Windows 信任提示：** MSI 是真实安装程序，但安装程序格式本身不能消除 Microsoft Defender SmartScreen。只有使用公众信任证书的 Authenticode 签名并逐步建立发布者信誉，或通过 Microsoft Store 分发，才能系统性改善提示。Release 工作流在配置签名凭据时签名并验证产物；没有凭据时会明确发布未签名产物，而不会伪装成“受信任”。下载后可用同名 `.sha256` 或 `SHA256SUMS` 检查文件完整性。

### Linux 与 macOS

下载对应的 `tar.gz`，校验同名 `.sha256` 后解压，并运行归档中的 `install.sh`。当前这两个平台仍是归档安装；它们不是图形安装包。平台依赖、卸载和签名状态详见 [`docs/release.md`](docs/release.md)。

## CLI 检索示例

```bash
# 默认检索全部 scope
scrap find api_token --fuzzy

# 一个或多个明确 scope；--scope 可以重复
scrap find api_token --fuzzy --scope cloudflare --scope staging

# 显式全部 scope（不能与 --scope 同用）
scrap find '^deploy_' --regex --all --case-sensitive
```

搜索只匹配 key，永不匹配 value。文本结果使用无歧义的 scope/key 行；`--json` 返回结构化元数据但不返回 value。完整命令与领域语义见 [`docs/scrap-design.md`](docs/scrap-design.md)。

## 构建与测试

桌面产品需要 `global.json` 指定的 .NET 10 SDK：

```bash
dotnet restore Scrap.slnx --locked-mode
dotnet build Scrap.slnx -c Release --no-restore
dotnet test Scrap.slnx -c Release --no-build
```

发布页位于 `website/`，需要 Node.js 22.12+ 与仓库声明的 pnpm：

```bash
pnpm --dir website dev
pnpm --dir website run verify
```

站点生成到 `website/dist/`。GitHub Actions 验证桌面端和网站；主分支部署 GitHub Pages，`vX.Y.Z` tag 构建平台产物并创建 GitHub Release。

## 工程结构

| 组件 | 职责 |
|---|---|
| `Scrap.Cli` / `Scrap.Gui` | 稳定脚本客户端与跨平台桌面客户端 |
| `Scrap.Daemon` | IPC、领域事务、搜索与数据的唯一 owner |
| `Scrap.Domain` / `Scrap.Protocol` | 领域不变量与版本化 IPC contract |
| `Scrap.Client` | 连接、版本协商与 daemon 按需启动 |
| `Scrap.Storage.Sqlite` | SQLite 持久化 |
| `Scrap.Crypto` / `Scrap.Platform` | 记录加密与平台密钥保护 |
| `installer/` | Windows per-user MSI |
| `website/` | Astro + TypeScript 产品发布页 |

## 设计与维护知识

- 系统契约：[`docs/scrap-design.md`](docs/scrap-design.md)
- 发布、安装、签名与资产：[`docs/release.md`](docs/release.md)
- 产品与交互决策：[`docs/ux-product-spec.md`](docs/ux-product-spec.md)
- 平台样式、i18n、Pages 与分发研究：[`docs/research-platform-style.md`](docs/research-platform-style.md)
- 架构演进：[`docs/architecture-evolution.md`](docs/architecture-evolution.md)

## License

[GNU General Public License v3.0](LICENSE)
