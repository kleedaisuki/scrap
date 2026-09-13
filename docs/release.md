# 发布与安装 / Release and installation

本文描述首版发布工程的可复现流程、资产契约与卸载数据语义。This document defines the reproducible release flow, asset contract, and uninstall data semantics for the first release.

## 发布资产 / Release assets

Tag `vX.Y.Z` 会构建四个自包含部署（Self-contained deployment），且不启用裁剪（trimming）或预先编译（Ahead-of-Time compilation, AOT）：

Tag 使用严格语义化版本（Semantic Versioning, SemVer）；例如 `v1.2.3-beta+build.7` 会标记为 GitHub prerelease 且不会成为 Latest。稳定版由 GitHub 的版本规则决定 Latest，维护分支重跑不会强制降级现有 Latest。

| 运行时标识（Runtime Identifier, RID） | Runner | 资产 |
|---|---|---|
| `win-x64` | Windows | `scrap-vX.Y.Z-win-x64.zip` |
| `linux-x64` | Linux | `scrap-vX.Y.Z-linux-x64.tar.gz` |
| `osx-x64` | Intel macOS | `scrap-vX.Y.Z-osx-x64.tar.gz` |
| `osx-arm64` | Apple Silicon macOS | `scrap-vX.Y.Z-osx-arm64.tar.gz` |

每个归档只包含对应平台的 `scrap`、`scrapd`、`scrap-gui` 单文件程序、安装/卸载脚本、项目许可证及完整的 `THIRD-PARTY-NOTICES.txt`。GitHub Release 同时提供每个资产的 `.sha256` 文件以及汇总的 `SHA256SUMS`。Unix 使用 `tar.gz`，因为工作流制品（Workflow artifact）的传输 ZIP 不保证保留可执行位。

自包含不等于不依赖操作系统：GUI、密钥存储等功能仍可能需要平台原生组件。自包含应用也不会自动获取后续 .NET Runtime 安全修复，因此 Runtime 更新后应重新发布。参见 [.NET 应用发布](https://learn.microsoft.com/dotnet/core/deploying/) 与 [RID 目录](https://learn.microsoft.com/dotnet/core/rid-catalog)。

## 安装 / Install

从 [GitHub Releases](https://github.com/kleedaisuki/scrap/releases) 下载与机器匹配的归档并先验证 SHA-256：

```bash
sha256sum --check scrap-vX.Y.Z-linux-x64.tar.gz.sha256
tar -xzf scrap-vX.Y.Z-linux-x64.tar.gz
cd scrap-vX.Y.Z-linux-x64
./install.sh
```

Windows 安装脚本同时兼容系统自带的 Windows PowerShell 5.1 与 PowerShell 7：

```powershell
$expected = (Get-Content .\scrap-vX.Y.Z-win-x64.zip.sha256).Split()[0]
$actual = (Get-FileHash .\scrap-vX.Y.Z-win-x64.zip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $expected) { throw "SHA-256 mismatch" }
Expand-Archive .\scrap-vX.Y.Z-win-x64.zip -DestinationPath .
cd .\scrap-vX.Y.Z-win-x64
.\install.ps1
```

安装器仅管理当前用户的 `~/.scrap/bin` 中三个已知程序及自己添加的 PATH 项，不需要管理员权限，也不会创建或迁移数据库。重复运行是幂等的（idempotent），可用于原地升级。新 PATH 只对新开的终端生效。

Unix 安装器向适用的 `.profile`、`.bashrc`、`.zshrc` 或 `.zprofile` 写入有明确标记的块；Windows 安装器使用用户级环境变量 API，并记录 PATH 项所有权。`--no-path`（PowerShell 为 `-NoPath`）可跳过 PATH 集成。

Linux 还需要 `libsecret-1`、GLib/GIO、用户会话 D-Bus，以及实现 Secret Service 的登录会话（例如 GNOME Keyring 或支持 Secret Service 的 KWallet）。Debian/Ubuntu 通常安装 `libsecret-1-0`，Fedora/Arch 通常安装 `libsecret`。`install.sh` 只做非阻塞预检并给出警告，不会因当前 SSH、WSL 或 headless 会话未启动 keyring 而拒绝部署；但此时需要 store 的命令会返回退出码 `6`。`daemon version` / `ping` 成功只证明 IPC 生命周期正常，**不代表** key provider 已可用。

## 卸载与数据 / Uninstall and data

普通卸载只删除三个程序与安装器拥有的 PATH 集成，保留 `.scrap/data`、配置、日志以及平台密钥材料：

```bash
./uninstall.sh
```

```powershell
.\uninstall.ps1
```

只有显式 purge 才删除整个 `.scrap`。交互式执行时必须输入 `PURGE`；自动化还必须同时给出 `--yes` / `-Yes`：

```bash
./uninstall.sh --purge --yes
```

```powershell
.\uninstall.ps1 -Purge -Yes
```

Purge 不是物理安全擦除（secure erasure）：SSD、备份、macOS Keychain 或 Linux Secret Service 等外部系统仍可能保留历史副本。普通卸载绝不删除外部密钥，否则保留下来的数据库可能永久不可解密。

## CI 与 Release 流程

```text
push / pull request
  └─ Windows + Linux + macOS：restore → build → test → script parse

push vX.Y.Z tag
  └─ 三平台测试
      └─ 四个原生 RID：single-file publish → CLI smoke test → archive
          └─ 下载并核验 workflow artifacts → SHA256SUMS → GitHub Release
```

Tag smoke 在全部 RID 上验证 `daemon version` / `ping` / `shutdown`，并在 Windows DPAPI 环境验证 scope 与 record CRUD。当前 Linux 生命周期 smoke 不冒充 Secret Service 集成测试；正式 Linux 业务验证必须在带用户会话 D-Bus 与已解锁 Secret Service 的环境执行。

Actions 使用完整提交 SHA 固定，`.github/dependabot.yml` 每周提出更新。普通任务只有 `contents: read`；只有最终发布任务拥有 `contents: write`。Release 重跑会覆盖同名资产，因此不会产生重复 Release。该结构遵循 [GitHub 的 .NET CI 指南](https://docs.github.com/actions/tutorials/build-and-test-code/net)、[工作流权限](https://docs.github.com/actions/reference/workflows-and-actions/workflow-syntax) 与 [`gh release create`](https://cli.github.com/manual/gh_release_create)。

### 创建发布 / Create a release

```bash
git tag -a vX.Y.Z -m "scrap vX.Y.Z"
git push origin vX.Y.Z
```

必须由外部 push Tag；不要让一个使用 `GITHUB_TOKEN` 的工作流创建 Tag 后期待另一工作流被普通 push 事件触发。参见 [`GITHUB_TOKEN` 事件行为](https://docs.github.com/actions/concepts/security/github_token)。

当前无签名时不需要额外仓库 Secret。面向正式桌面分发仍建议后续增加：

- Windows Authenticode 证书及其密码；
- Apple Developer ID 证书、证书密码、Team ID 与 notarization 凭据；
- 可选的 Linux 包签名密钥。

在这些凭据和受测流程就绪前，工作流不会假装产物已签名或已公证。macOS 当前资产是命令行可启动的裸可执行文件，不是签名并公证的 `.app` / `.dmg` 安装体验。

升级 `global.json` 中的 SDK、Avalonia、SQLite、Skia/HarfBuzz 或其他发布依赖时，必须同步用实际 RID publish 的 `.deps.json`、NuGet 包内许可证及对应上游锁定提交重新核验 `THIRD-PARTY-NOTICES.txt`；不能假设旧 Runtime 的 notices 自动覆盖新二进制。
