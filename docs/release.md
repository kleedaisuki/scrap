# 发布、安装与站点 / Release, installation, and site

本文是发布工程的操作契约：说明 GitHub Release 里应当出现什么、用户怎样安装、签名能够和不能够证明什么，以及产品发布页如何验证与部署。产品交互决策见 [`ux-product-spec.md`](ux-product-spec.md)，外部证据与平台研究见 [`research-platform-style.md`](research-platform-style.md)，本文不重复其论证。

## 1. GitHub Release 资产契约

外部 push 严格语义化版本（Semantic Versioning, SemVer）tag `vX.Y.Z` 会触发 `.github/workflows/release.yml`。例如 `v1.2.3-beta+build.7` 标为 prerelease；稳定 tag 才参与 Latest。

| 运行时标识（Runtime Identifier, RID） | 面向用户的资产 | 便携/备用资产 |
|---|---|---|
| `win-x64` | `scrap-vX.Y.Z-win-x64.msi` | `scrap-vX.Y.Z-win-x64.zip` |
| `linux-x64` | `scrap-vX.Y.Z-linux-x64.tar.gz` | — |
| `osx-x64` | `scrap-vX.Y.Z-osx-x64.tar.gz` | — |
| `osx-arm64` | `scrap-vX.Y.Z-osx-arm64.tar.gz` | — |

每个上述资产都有同名 `.sha256`；Release 还包含汇总 `SHA256SUMS`。归档内含对应平台的 `scrap`、`scrapd`、`scrap-gui` 自包含单文件程序，以及 `LICENSE` 与完整 `THIRD-PARTY-NOTICES.txt`。ZIP 继续存在是为了便携和排障，不应在网站上冒充 Windows 安装程序。

自包含部署（Self-contained deployment）不启用 trimming 或预先编译（Ahead-of-Time compilation, AOT），也不等于没有操作系统依赖。GUI 与平台密钥设施仍需要原生组件；自包含应用不会自动获取后续 .NET Runtime 修复，因此 Runtime 更新后必须重发。依赖或 Runtime 升级时，要以实际 RID publish 的 `.deps.json` 和上游锁定版本重新核验第三方 notices。

## 2. Windows MSI

`installer/Scrap.Installer.Windows` 使用 WiX Toolset 生成真正的当前用户 MSI：

- 安装 GUI、CLI 与 daemon 到用户的 Local AppData，不请求管理员权限；
- 提供产品图标、开始菜单入口以及“应用和功能”卸载入口；
- 把安装目录加入当前用户 PATH，新终端会获得更新；
- 使用稳定 `UpgradeCode` 支持 major upgrade，并阻止旧版本覆盖新版本；
- MSI component 只拥有程序文件、快捷方式与自己写入的注册表/PATH 项；用户数据 `~/.scrap` 从不属于 MSI component，升级和普通卸载都不会删除它。

MSI 的安装目标与旧的 `~/.scrap/bin` 脚本布局不同。不要把脚本安装器的路径所有权或 purge 语义套到 MSI；需要彻底删除用户数据时，在确认备份后由用户明确删除 `~/.scrap`。

### 2.1 SmartScreen 与签名事实

**MSI 改善安装体验，但不会自动绕过 Microsoft Defender SmartScreen。** SHA-256 只能检查下载是否损坏，不能证明发布者身份；自签名证书也不能建立公众信任。可靠的分发路径是：

1. 用公众信任的 Authenticode 证书签名 Windows executable payload 和最终 MSI；
2. 使用 RFC 3161 时间戳，并在生成 hash 之前验证签名；
3. 以稳定发布者身份逐步建立信誉，或转向 Microsoft Store 分发。

GitHub Actions 的签名是**可选且诚实可见**的。仓库 secrets 必须成对配置：

| Secret | 内容 |
|---|---|
| `WINDOWS_SIGNING_CERTIFICATE_BASE64` | Base64 编码的 PFX |
| `WINDOWS_SIGNING_CERTIFICATE_PASSWORD` | PFX 密码 |

只配置其中一个会使发布失败；两者都缺失时工作流明确告知 Windows 资产未签名，但仍会验证 MSI 结构与 checksum。两者齐全时，工作流调用 `signtool`，签名 executable 与 MSI，并执行 Authenticode verification。最终 Release notes 会根据同一配置自动写入中英文签名状态；重跑时通过稳定标题去重，不让事实只留在 CI 日志里。

不要承诺“签了就永不弹窗”：SmartScreen 同时评估发布者和文件信誉。对中国大陆个人维护者，Azure Artifact Signing 当前不开放；新商业 OV 证书通常还要求硬件密钥或 HSM，因此上面的 PFX lane 只适用于已经持有的可导出合格凭据。当前优先路线是申请免费的 SignPath Foundation 开源签名，同时为无需自购证书、由 Microsoft 重签的 Store MSIX 保留打包路线；两者都需要所有者完成外部身份或项目审批。具体策略与当前资格证据见 [`code-signing-policy.md`](code-signing-policy.md) 和 [`research-platform-style.md`](research-platform-style.md#5-installer-and-github-release-workflow)。

### 2.2 Microsoft Store MSIX

`build/package-store.ps1` 生成真正的完整包 MSIX（full-package MSIX），包含 GUI、CLI、daemon、图标与许可证清单。CLI 通过应用执行别名（App Execution Alias）`scrap.exe` 暴露，不修改用户 PATH；应用数据仍位于 `%USERPROFILE%\.scrap`，不属于包，更新和卸载都会保留。

先在 Partner Center 保留产品名，再从 **Product management → Product identity** 原样复制三个区分大小写的字段。不得猜测 Publisher，也不得把 CI 占位身份提交到 Store：

```powershell
dotnet restore Scrap.slnx --locked-mode
./build/package-store.ps1 `
  -IdentityName '<Partner Center Package/Identity/Name>' `
  -Publisher '<Partner Center Package/Identity/Publisher>' `
  -PublisherDisplayName '<Partner Center PublisherDisplayName>' `
  -ProductDisplayName 'moeSegFault Scrap' `
  -ReleaseVersion '1.0.0'
```

Store 数字版本独立记录在 `installer/Scrap.Installer.Store/StoreVersion.txt`，每次提交必须单调递增，且第四段保持 `0`。脚本用 Windows SDK `MakeAppx` 做 schema/content validation，解包后复核平坦载荷与精确身份，并只生成**未签名**候选；Microsoft Store ingestion 会对接受的 MSIX 重签。仓库不会把 CI 自签测试包发布给用户。

`.github/workflows/store-package.yml` 在一次性 Windows runner 中复制候选、创建临时证书、签名测试副本、安装并验证 `scrap.exe --help`、daemon 自动启动/IPC、PATH 不变、卸载与 `~/.scrap` 保留，最后无条件清理包与证书。AppX deployment 当前只认可计算机级 `TrustedPeople`，所以测试公钥短暂导入 `LocalMachine\TrustedPeople`，私钥仍为不可导出的当前用户密钥；该步骤只运行在临时管理员 runner，绝不用于开发机或用户设备。提交前还必须在**完全相同的候选包**上运行 Windows 应用认证工具包（Windows App Certification Kit, WACK），并审阅 Partner Center ingestion 结果。架构、不变量和官方证据见 [`store-msix-architecture.md`](store-msix-architecture.md)。

普通 push/PR 使用明确的 CI 假身份与 `Scrap CI Package` 显示名；手动运行工作流才生成生产候选。手动运行前必须配置 repository variables `STORE_IDENTITY_NAME`、`STORE_PUBLISHER`、`STORE_PUBLISHER_DISPLAY_NAME`、`STORE_PRODUCT_DISPLAY_NAME`；最后一项必须是保留名称 `moeSegFault Scrap`。脚本把它同时写入包级 DisplayName 与应用 VisualElements DisplayName，并在解包后精确复核。缺少任何变量会直接拒绝构建，而不会退回 CI 占位值。

手动工作流的 `package_version` 是可选覆盖项；留空时读取受版本控制的 `installer/Scrap.Installer.Store/StoreVersion.txt`。不要在工作流 UI 中复制一个会与版本计数器漂移的默认值。

`build/generate-store-assets.ps1` 从唯一品牌源图生成并验证 MSIX 的 scale/targetsize/unplated 变体及 `store-listing/assets/AppTileIcon-300x300.png`；Store workflow 会拒绝未提交的生成差异。双语一览文案、功能项、截图说明、隐私/年龄分级答案与 `runFullTrust` 审核说明集中在 [`store-listing/`](../store-listing/README.md)。首次提交的 **What's new** 必须完全留空。

在相同候选上运行 WACK，不要用重新构建的“等价包”替代：

```powershell
./build/run-store-certification.ps1 -Candidate ./artifacts/store/scrap-store-v1.0.2.0-win-x64.msix
```

脚本只在仓库 `.temp/wack/` 保存可审阅报告与候选 hash，不提权、不安装证书；缺少/不完整报告、工具失败或任何必需测试未通过都会明确失败。若 WACK 汇总为 `WARNING` 但所有非通过项均明确标记为 optional，可人工审阅后显式添加 `-AllowOptionalWarnings`；脚本仍会醒目标出“不是 PASS”。WACK 需要不被中断的交互式 Windows 用户会话，仍不能替代 Partner Center ingestion。

当前 `1.0.2.0` / `0.2.0-preview.8` 本地候选的 WACK 结果是完整运行、24 项测试、`OVERALL_RESULT=PASS`，所有必需项通过。可选的 **Blocked executables** 静态分析仍因 self-contained .NET 载荷包含进程启动 API 与运行时工具名字符串而报 `FAIL`；脚本不会隐藏该项。此前的 DPI awareness warning 已通过 GUI executable 的 Per-Monitor V2 manifest 修复并在本次报告中通过。

## 3. Linux 与 macOS 安装

这两个平台当前提供归档，而非图形安装器：

```bash
sha256sum --check scrap-vX.Y.Z-linux-x64.tar.gz.sha256
tar -xzf scrap-vX.Y.Z-linux-x64.tar.gz
cd scrap-vX.Y.Z-linux-x64
./install.sh
```

安装到 `~/.scrap/bin`。`install.sh` 对适用 shell profile 写入带明确 marker 的 PATH block；`--no-path` 跳过集成。重复运行是幂等升级。普通卸载只删除三个程序与安装器拥有的 PATH block，并保留数据：

```bash
./uninstall.sh
```

显式 purge 才删除整个 `.scrap`；非交互自动化还必须同时给出确认：

```bash
./uninstall.sh --purge --yes
```

Purge 不是物理安全擦除（secure erasure）：SSD、备份、macOS Keychain 或 Linux Secret Service 仍可能保留历史副本。普通卸载不得删除外部密钥，否则保留的数据库可能永久无法解密。

Linux 需要 `libsecret-1`、GLib/GIO、用户会话 D-Bus 和已解锁的 Secret Service。Debian/Ubuntu 通常安装 `libsecret-1-0`，Fedora/Arch 通常安装 `libsecret`。安装器只做非阻塞预检；SSH、WSL 或 headless 会话没有 keyring 时仍可部署，但使用 store 的命令会以退出码 `6` 报告不可用。`daemon ping/version` 成功只说明 IPC 生命周期正常，不证明 key provider 可用。

## 4. CI 与 Release 流程

```text
push main / pull request / manual
  ├─ Windows + Linux + macOS: locked restore -> build -> test
  ├─ Ubuntu: frozen pnpm install -> website check -> test -> build
  └─ Windows Store lane: locked restore -> unsigned full-package MSIX -> unpack inspection
      └─ sign disposable copy -> install -> alias/daemon/persistence smoke -> uninstall/cleanup

push vX.Y.Z tag
  └─ three-platform tests
      └─ four native RIDs: publish -> smoke -> archive
          └─ Windows: build MSI -> optional Authenticode -> verify
              └─ verify all sidecars -> SHA256SUMS -> GitHub Release
```

Tag smoke 在全部 RID 上验证 CLI 与 daemon 生命周期，并在 Windows DPAPI 环境验证 scope/record CRUD。Linux lifecycle smoke 不冒充 Secret Service integration test；正式 Linux 业务验证需要用户会话 D-Bus 与已解锁 Secret Service。

Actions 使用完整 commit SHA 固定，普通任务只有 `contents: read`，只有最终 Release job 使用 `contents: write`。Release 重跑当前会对同名资产执行 `--clobber`；因此恢复失败发布时必须确认 tag 和源码完全一致。若仓库未来启用 immutable releases，应改为 draft assemble-and-publish，不能继续依赖覆盖已发布资产。

创建发布：

```bash
git tag -a vX.Y.Z -m "scrap vX.Y.Z"
git push origin vX.Y.Z
```

Tag 必须由外部 push；使用 `GITHUB_TOKEN` 的工作流创建 tag 后，不能假定另一个普通 push workflow 会被触发。

## 5. 产品发布页与 GitHub Pages

发布页是 `website/` 下的 Astro 7 + TypeScript 静态站点，不是文档站。它使用 MoeSegFault Style `v0.1.2` exact-version CSS，并保留本地 fallback tokens；主题选择为 `auto | light | dark`，保存到浏览器 `localStorage`。根路径 `/` 是完整、可直接分享的简体中文页，`/en/` 是英文页；不根据浏览器语言强制重定向。

本地开发与完整验证：

```bash
pnpm --dir website dev
pnpm --dir website run verify
```

`verify` 顺序执行 Astro check、Vitest 和静态 build，输出为 `website/dist/`。不要直接编辑 `dist/`。

Pages workflow 在影响站点、workspace 或 workflow 的 main push（以及 manual dispatch）时：

1. frozen install 并运行与 CI 相同的验证；
2. 检查生成资产中的 `CNAME` 精确等于 `scrap.moesegfault.dev`；
3. 上传 `website/dist/` 为 Pages artifact；
4. 在 `github-pages` environment 中使用最小的 `pages: write` 与 `id-token: write` 权限部署。

仓库内配置不能独自完成域名启用。GitHub **Settings → Pages** 必须设置 `scrap.moesegfault.dev` 并启用 HTTPS；DNS 的 `scrap` CNAME 应指向 `kleedaisuki.github.io`，不包含 repository path。`website/public/CNAME` 记录构建意图，仓库设置和 DNS 才是实际权威。

GitHub 签发 Pages 源站证书期间，Cloudflare 上的该 CNAME 应先设为 **DNS only**。如果记录保持代理状态，公共 DNS 只暴露 Cloudflare 的 A/AAAA 地址，GitHub API 可能持续返回 `The certificate does not exist yet`，从而无法启用 Pages 的 `https_enforced`。Cloudflare 边缘能够建立 HTTPS 并不等于 Pages 已强制 HTTPS：必须分别验证 `https://` 可访问、`http://` 会重定向，以及 Pages API 的 `https_enforced: true`。源站证书签发并启用强制 HTTPS 后，再决定是否恢复 Cloudflare 代理。参见 [GitHub 的 Pages HTTPS 排障](https://docs.github.com/en/pages/getting-started-with-github-pages/securing-your-github-pages-site-with-https#troubleshooting-certificate-provisioning-certificate-not-yet-created-error) 与 [Cloudflare 的代理状态说明](https://developers.cloudflare.com/dns/proxy-status/)。

## 6. 发布前检查清单

- [ ] `.NET` 三平台 test 与 website `ci` 全部通过；
- [ ] 四个 RID 的应用从打包产物启动，而非只验证源码 build；
- [ ] Windows MSI fresh install、upgrade、GUI/CLI 启动、PATH 与 normal uninstall 已验证；
- [ ] Store candidate 使用 Partner Center 精确身份、`moeSegFault Scrap` 双层显示名与递增四段版本；图标已重新生成且无 diff；unsigned candidate 经 MakeAppx、installed CI smoke、WACK 与 Partner Center ingestion 验证；
- [ ] `zh-CN`/`en-US` 文案、300×300 图标、每语言四张真实桌面截图、隐私 URL 和 `runFullTrust` 说明均已填写；首次 What's new 留空；
- [ ] 若配置签名，executable 和 MSI 的 publisher、RFC 3161 timestamp 与 `signtool verify /pa /all` 均正确；若未配置，Release notes 不声称已签名；
- [ ] 每个资产的 sidecar 与 `SHA256SUMS` 在签名完成后生成并验证；
- [ ] `/`、`/en/`、主题切换、产品图标与平台下载链接正确；
- [ ] GitHub Pages custom domain 和 HTTPS 生效。
