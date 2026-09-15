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

只配置其中一个会使发布失败；两者都缺失时工作流明确告知 Windows 资产未签名，但仍会验证 MSI 结构与 checksum。两者齐全时，工作流调用 `signtool`，签名 executable 与 MSI，并执行 Authenticode verification。

不要承诺“签了就永不弹窗”：SmartScreen 同时评估发布者和文件信誉。Microsoft Artifact Signing Public Trust 当前的实体地域资格不一定覆盖中国大陆发布者；选择它之前必须核实实际签约实体资格。否则采用合格商业 CA 的 Authenticode 证书或 Microsoft Store。详细来源见 [`research-platform-style.md`](research-platform-style.md#5-installer-and-github-release-workflow)。

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
  └─ Ubuntu: frozen pnpm install -> website check -> test -> build

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

## 6. 发布前检查清单

- [ ] `.NET` 三平台 test 与 website `ci` 全部通过；
- [ ] 四个 RID 的应用从打包产物启动，而非只验证源码 build；
- [ ] Windows MSI fresh install、upgrade、GUI/CLI 启动、PATH 与 normal uninstall 已验证；
- [ ] 若配置签名，executable 和 MSI 的 publisher、RFC 3161 timestamp 与 `signtool verify /pa /all` 均正确；若未配置，Release notes 不声称已签名；
- [ ] 每个资产的 sidecar 与 `SHA256SUMS` 在签名完成后生成并验证；
- [ ] `/`、`/en/`、主题切换、产品图标与平台下载链接正确；
- [ ] GitHub Pages custom domain 和 HTTPS 生效。
