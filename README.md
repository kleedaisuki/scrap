# scrap

`scrap` 是一个 local-first、可脚本化的本地秘密与短文本字段存储：CLI 面向程序组合，Avalonia GUI 面向人类发现，`scrapd` 是唯一数据权威。

> 项目正在按 [`docs/scrap-design.md`](docs/scrap-design.md) 实现。已发布命令、协议和数据格式将作为用户空间兼容契约（userspace compatibility contract）维护。

## 工程结构

| 组件 | 职责 |
|---|---|
| `Scrap.Cli` | 稳定 stdout/stderr/退出码的脚本客户端 |
| `Scrap.Gui` | 跨平台桌面客户端 |
| `Scrap.Daemon` | IPC、领域事务与数据的唯一 owner |
| `Scrap.Domain` | 领域模型与搜索语义 |
| `Scrap.Protocol` | 版本化 IPC envelope 与 framing |
| `Scrap.Storage.Sqlite` | SQLite 持久化 |
| `Scrap.Crypto` / `Scrap.Platform` | 记录加密与平台密钥保护 |

## 构建与测试

需要 `global.json` 指定的 .NET 10 SDK：

```bash
dotnet restore Scrap.slnx
dotnet build Scrap.slnx -c Release --no-restore
dotnet test Scrap.slnx -c Release --no-build
```

GitHub Actions 在 Windows、Linux 和 macOS 上执行相同验证。Tag 发布会产生 `win-x64`、`linux-x64`、`osx-x64`、`osx-arm64` 四个自包含单文件归档。

## 安装

从 [Releases](https://github.com/kleedaisuki/scrap/releases) 下载对应平台资产，验证 `SHA256SUMS` 后执行归档内的安装脚本：

```bash
./install.sh
```

```powershell
.\install.ps1
```

程序安装到 `~/.scrap/bin`。普通卸载保留关键数据；只有 `--purge` / `-Purge` 加明确确认才删除 `.scrap`。完整的资产结构、校验、幂等升级和发布操作见 [`docs/release.md`](docs/release.md)。

## 设计边界

- CLI 与 GUI 只能通过本地 IPC 访问 daemon；
- value 加密保存，不进入搜索、日志或错误信息；
- CLI 的 `get` 保持适合管道的原始 stdout；
- 不提供远程同步、多用户共享、HTTP API 或通用文档数据库能力。

## License

[GNU General Public License v3.0](LICENSE)
