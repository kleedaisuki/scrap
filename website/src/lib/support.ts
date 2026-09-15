import type { Locale } from "./content";

/** One focused support topic. / 一个聚焦的支持主题。 */
export interface SupportTopic {
  id: string;
  number: string;
  title: string;
  paragraphs: string[];
  commands?: string[];
  points?: string[];
}

/** Localized support-page copy contract. / 支持页本地化文案契约。 */
export interface SupportCopy {
  meta: { title: string; description: string };
  eyebrow: string;
  title: string;
  lead: string;
  topics: SupportTopic[];
  contact: {
    kicker: string;
    title: string;
    body: string;
    warning: string;
    sensitive: string;
    action: string;
  };
}

/** Concise product support based on current installers and runtime paths. / 基于当前安装器和运行路径的简明产品支持。 */
export const supportCopy: Record<Locale, SupportCopy> = {
  zh: {
    meta: {
      title: "产品支持 — scrap",
      description: "快速解决 scrap 的安装、启动、数据位置与命令行问题，并了解如何安全地联系项目维护者。",
    },
    eyebrow: "SUPPORT · START HERE",
    title: "先把事情解决掉。",
    lead: "安装、启动和命令行问题通常只需要确认一个路径或重新打开终端。下面是最短排查路线；如果仍然卡住，我们会在公开仓库继续帮你。",
    topics: [
      {
        id: "install",
        number: "01",
        title: "安装与更新",
        paragraphs: [
          "如果你从 Microsoft Store 安装，Windows 负责安装、签名验证与更新；从开始菜单打开 moeSegFault Scrap。不要把商店包解压后手动运行其中的文件。",
          "GitHub Releases 提供 Windows MSI 以及 macOS、Linux 归档。只从项目发布页取得匹配系统架构的资产；普通升级会保留已有 record。",
        ],
      },
      {
        id: "start",
        number: "02",
        title: "启动与本地服务",
        paragraphs: [
          "先启动桌面应用，或在新终端运行 scrap --help。scrapd 是按需启动的本地守护进程，不需要你单独安装服务，也不应手动常驻运行。",
          "如果应用未响应，完整退出桌面端后重试。仍然失败时，记录 Scrap 版本、操作系统版本和不含敏感内容的原始错误文字。",
        ],
        commands: ["scrap --help", "scrap scope list"],
      },
      {
        id: "data",
        number: "03",
        title: "数据在哪里",
        paragraphs: [
          "record 数据与运行状态位于当前用户的 ~/.scrap；Windows 通常是 %USERPROFILE%\\.scrap。普通升级或卸载不会删除这个目录。",
          "数据库在 ~/.scrap/data/scrap.db，daemon 日志在 ~/.scrap/log/scrap.log。不要把数据库、key-reference 或完整日志上传到 issue。需要彻底移除数据时，请先确认备份，再明确删除 ~/.scrap。",
        ],
      },
      {
        id: "cli",
        number: "04",
        title: "找不到 scrap 命令",
        paragraphs: [
          "商店版本通过应用执行别名（App Execution Alias）提供 scrap.exe，通常由 Windows 映射到 %LOCALAPPDATA%\\Microsoft\\WindowsApps。安装后请关闭并重新打开终端；仍找不到时，在 Windows“设置 → 应用 → 高级应用设置 → 应用执行别名”中确认 scrap.exe 已启用。无需进入受保护的包目录。",
          "MSI 版本通常安装到 %LOCALAPPDATA%\\Scrap，并把该目录加入当前用户 PATH；macOS 与 Linux 的归档安装器通常使用 ~/.scrap/bin。PATH 变化只会在新进程中生效。",
        ],
        commands: ["where scrap", "Get-Command scrap", "command -v scrap"],
      },
    ],
    contact: {
      kicker: "还没有解决？",
      title: "带着可复现信息来找我们。",
      body: "GitHub Issues 是本项目的支持入口。请提供 Scrap 版本、操作系统、安装来源、最短复现步骤、预期行为和已经脱敏的实际结果。",
      warning: "Issues 完全公开。绝对不要粘贴 secret、访问令牌、密码、私钥、record value、数据库、原始日志或个人数据。发布前也请移除用户名、主机名和私有路径。",
      sensitive: "如果问题本身敏感，请先只提交一条完全脱敏的联系请求，说明版本和问题类别，并请求维护者提供合适的非公开沟通方式；不要在公开 issue 中解释敏感细节。",
      action: "前往 GitHub Issues",
    },
  },
  en: {
    meta: {
      title: "Product Support — scrap",
      description: "Resolve common scrap installation, startup, data-location, and command-line issues, and learn how to contact the maintainers safely.",
    },
    eyebrow: "SUPPORT · START HERE",
    title: "Let's get you unstuck.",
    lead: "Most installation, startup, and command-line problems come down to one path or a terminal that needs reopening. Start with the shortest checks below; if you are still stuck, we will continue in the public repository.",
    topics: [
      {
        id: "install",
        number: "01",
        title: "Install and update",
        paragraphs: [
          "If you installed from Microsoft Store, Windows handles installation, signature verification, and updates. Open moeSegFault Scrap from Start; do not unpack the Store package and run its files manually.",
          "GitHub Releases provides the Windows MSI and macOS/Linux archives. Download only the asset matching your system architecture from the project release page. Ordinary upgrades preserve existing records.",
        ],
      },
      {
        id: "start",
        number: "02",
        title: "Start and local service",
        paragraphs: [
          "Start the desktop app, or run scrap --help in a fresh terminal. scrapd is an on-demand local daemon; you do not need to install it as a separate service or keep it running manually.",
          "If the app does not respond, exit the desktop app completely and retry. If it still fails, note your Scrap version, operating-system version, and the exact error text after removing sensitive content.",
        ],
        commands: ["scrap --help", "scrap scope list"],
      },
      {
        id: "data",
        number: "03",
        title: "Where your data lives",
        paragraphs: [
          "Records and runtime state live under ~/.scrap for the current user; on Windows this is normally %USERPROFILE%\\.scrap. Ordinary upgrade or uninstall does not remove this directory.",
          "The database is ~/.scrap/data/scrap.db and daemon diagnostics are in ~/.scrap/log/scrap.log. Never attach the database, key-reference, or an entire raw log to an issue. To remove all local data, confirm your backups first and then explicitly delete ~/.scrap.",
        ],
      },
      {
        id: "cli",
        number: "04",
        title: "The scrap command is not found",
        paragraphs: [
          "The Store build exposes scrap.exe through a Windows App Execution Alias, ordinarily mapped through %LOCALAPPDATA%\\Microsoft\\WindowsApps. Close and reopen the terminal after installation. If it is still missing, check that scrap.exe is enabled under Settings → Apps → Advanced app settings → App execution aliases. You do not need to enter the protected package directory.",
          "The MSI normally installs to %LOCALAPPDATA%\\Scrap and adds that directory to the current user's PATH. The macOS and Linux archive installers normally use ~/.scrap/bin. PATH changes become visible only to new processes.",
        ],
        commands: ["where scrap", "Get-Command scrap", "command -v scrap"],
      },
    ],
    contact: {
      kicker: "Still stuck?",
      title: "Bring a reproducible report.",
      body: "GitHub Issues is the project's support channel. Include the Scrap version, operating system, install source, shortest reproduction steps, expected behavior, and a fully redacted actual result.",
      warning: "Issues are entirely public. Never paste secrets, access tokens, passwords, private keys, record values, databases, raw logs, or personal data. Remove user names, host names, and private paths before posting.",
      sensitive: "If the issue itself is sensitive, first open only a fully redacted contact request with the version and broad issue category, asking the maintainer for an appropriate private channel. Do not explain sensitive details in the public issue.",
      action: "Open GitHub Issues",
    },
  },
};
