# 发布与安装 / Release and installation

本文描述首版发布工程的可复现流程、资产契约与卸载数据语义。This document defines the reproducible release flow, asset contract, and uninstall data semantics for the first release.

## 发布资产 / Release assets

Tag `vX.Y.Z` 会构建四个自包含部署（Self-contained deployment），且不启用裁剪（trimming）或预先编译（Ahead-of-Time compilation, AOT）：

| 运行时标识（Runtime Identifier, RID） | Runner | 资产 |
|---|---|---|
| `win-x64` | Windows | `scrap-vX.Y.Z-win-x64.zip` |
| `linux-x64` | Linux | `scrap-vX.Y.Z-linux-x64.tar.gz` |
| `osx-x64` | Intel macOS | `scrap-vX.Y.Z-osx-x64.tar.gz` |
| `osx-arm64` | Apple Silicon macOS | `scrap-vX.Y.Z-osx-arm64.tar.gz` |

每个归档只包含对应平台的 `scrap`、`scrapd`、`scrap-gui` 单文件程序、安装/卸载脚本和许可证。GitHub Release 同时提供每个资产的 `.sha256` 文件以及汇总的 `SHA256SUMS`。Unix 使用 `tar.gz`，因为工作流制品（Workflow artifact）的传输 ZIP 不保证保留可执行位。

自包含不等于不依赖操作系统：GUI、密钥存储等功能仍可能需要平台原生组件。自包含应用也不会自动获取后续 .NET Runtime 安全修复，因此 Runtime 更新后应重新发布。参见 [.NET 应用发布](https://learn.microsoft.com/dotnet/core/deploying/) 与 [RID 目录](https://learn.microsoft.com/dotnet/core/rid-catalog)。

## 安装 / Install

从 [GitHub Releases](https://github.com/kleedaisuki/scrap/releases) 下载与机器匹配的归档并先验证 SHA-256：

```bash
sha256sum --check scrap-vX.Y.Z-linux-x64.tar.gz.sha256
tar -xzf scrap-vX.Y.Z-linux-x64.tar.gz
cd scrap-vX.Y.Z-linux-x64
./install.sh
```

Windows PowerShell：

```powershell
Get-FileHash .\scrap-vX.Y.Z-win-x64.zip -Algorithm SHA256
Expand-Archive .\scrap-vX.Y.Z-win-x64.zip
cd .\scrap-vX.Y.Z-win-x64\scrap-vX.Y.Z-win-x64
.\install.ps1
```

安装器仅管理当前用户的 `~/.scrap/bin` 中三个已知程序及自己添加的 PATH 项，不需要管理员权限，也不会创建或迁移数据库。重复运行是幂等的（idempotent），可用于原地升级。新 PATH 只对新开的终端生效。

Unix 安装器向适用的 `.profile`、`.bashrc`、`.zshrc` 或 `.zprofile` 写入有明确标记的块；Windows 安装器使用用户级环境变量 API，并记录 PATH 项所有权。`--no-path`（PowerShell 为 `-NoPath`）可跳过 PATH 集成。

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
