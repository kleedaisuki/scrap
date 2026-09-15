import type { Locale } from "./content";

/** A policy section with paragraphs and optional points. / 包含正文与可选要点的政策章节。 */
export interface PrivacySection {
  id: string;
  title: string;
  paragraphs: string[];
  points?: string[];
}

/** Localized privacy-policy copy contract. / 隐私政策本地化文案契约。 */
export interface PrivacyCopy {
  meta: { title: string; description: string };
  eyebrow: string;
  title: string;
  lead: string;
  effectiveDate: string;
  summaryTitle: string;
  summary: string[];
  contentsLabel: string;
  sections: PrivacySection[];
  contactLabel: string;
}

/** Policy text reflects the observable behavior of the current Scrap release. / 政策文本反映当前 Scrap 版本的可观察行为。 */
export const privacyCopy: Record<Locale, PrivacyCopy> = {
  zh: {
    meta: {
      title: "隐私政策 — scrap",
      description: "了解 scrap 在本机处理、保存和删除数据的方式，以及加密、剪贴板、网站和操作系统边界。",
    },
    eyebrow: "PRIVACY · LOCAL-FIRST",
    title: "你的内容留在你的设备上。边界也应当说清楚。",
    lead: "scrap 没有账户、云同步、遥测或由开发者运营的数据服务。本政策具体说明应用会在本机处理什么、哪些内容经过加密，以及操作系统、剪贴板和备份不属于 scrap 能独自控制的边界。",
    effectiveDate: "生效日期：2026 年 9 月 15 日",
    summaryTitle: "先说结论",
    summary: [
      "scrap 项目开发者不会通过应用接收、收集或传输你保存的内容。",
      "你输入的内容仍可能属于个人数据；scrap 会在你的设备上处理这些内容。",
      "record 的 value 加密保存，但 scope、key、时间戳和显示策略等元数据不加密。",
    ],
    contentsLabel: "本政策涵盖",
    sections: [
      {
        id: "scope",
        title: "1. 适用范围与处理方式",
        paragraphs: [
          "本政策适用于 scrap 桌面应用、命令行界面（Command-line Interface, CLI）和本地守护进程 scrapd。它们在当前操作系统用户的设备上协同工作；开发者没有为这些组件运营接收 record、搜索内容或使用数据的后端服务。",
          "这不表示 scrap“不处理个人数据”。如果你把姓名、访问令牌或其他可识别信息保存为 record，应用会为提供存储、检索、显示、复制和删除功能而在本机处理它们。你应当只保存自己有权处理的内容。",
        ],
      },
      {
        id: "local-data",
        title: "2. 本机保存的数据",
        paragraphs: ["主要数据和运行状态位于当前用户主目录下的 ~/.scrap。平台会使用等价的用户路径；Windows 通常为 %USERPROFILE%\\.scrap。"],
        points: [
          "~/.scrap/data/scrap.db：SQLite 数据库。record value 以认证加密（Authenticated Encryption, AEAD）产生的密文保存。",
          "未加密元数据：scope 名称、record key、创建与更新时间、revision、plain/masked 显示策略，以及数据库内部标识。能读取数据库文件的人可能看到这些信息。",
          "~/.scrap/data/key-reference：操作系统保护密钥的引用；数据库本身不包含可直接使用的明文主密钥。",
          "~/.scrap/run 与 config.json：本地进程间通信（Inter-process Communication, IPC）、锁和配置等运行信息；run 目录中的内容通常可以再生。",
          "~/.scrap/log/scrap.log：守护进程的运行诊断。实现不会有意记录 record value 或 IPC 请求载荷，但日志仍会保存时间、事件类型和错误类别等运行信息。",
          "图形界面的主题与语言偏好保存在操作系统的 Local Application Data/MoeSegFault/Scrap/preferences.json；搜索词与 record 内容不会写入该偏好文件。网站主题另保存在浏览器 localStorage 中。",
        ],
      },
      {
        id: "transmission",
        title: "3. 收集、传输与共享",
        paragraphs: [
          "scrap 应用不包含账户系统、广告、分析遥测或云同步，也不会把你的 record、搜索词、偏好或运行日志发送给项目开发者。开发者不会出售或共享其没有收到的应用数据。",
          "如果你主动通过终端重定向、管道、剪贴板、备份工具或其他应用传递内容，数据会进入你所选择的外部组件；该传递由你的操作和相应组件控制，而不是发送给 scrap 开发者。",
        ],
      },
      {
        id: "clipboard",
        title: "4. 显示与剪贴板",
        paragraphs: [
          "masked 是显示与剪贴板策略，不是另一种加密强度。所有 record value 使用同一套静态加密方式；plain/masked 元数据只决定默认呈现。",
          "在图形界面复制 masked record 后，scrap 会等待 30 秒；只有剪贴板届时仍包含该次复制的相同内容时，才尝试清除它。清除是尽力而为：剪贴板管理器、同步功能或其他进程可能已保留副本。复制 plain record 不会自动清除。",
          "CLI 的 get 默认把 value 写入标准输出；--clipboard 会按你的明确要求写入系统剪贴板。CLI 不会自动清除输出或剪贴板内容。终端、shell 历史、管道接收方和剪贴板工具可能另行保存它们。",
        ],
      },
      {
        id: "retention",
        title: "5. 保留、删除与卸载",
        paragraphs: [
          "record 或 scope 删除会使内容在 scrap 中逻辑上不可访问，但不承诺物理安全擦除。SQLite 的空闲页、预写式日志（Write-ahead Log, WAL）、文件系统快照、SSD 行为或备份中仍可能暂时或长期存在历史副本。",
          "普通升级和卸载只移除应用拥有的程序与集成项，并会保留 ~/.scrap 中的数据。若要移除本机数据，你需要在确认不再需要备份后明确删除 ~/.scrap；图形界面的偏好文件位于平台 Local Application Data 中，可能也需单独删除。即使执行清理，scrap 也无法保证擦除操作系统密钥存储、备份、快照或存储设备中的历史副本。",
        ],
      },
      {
        id: "boundaries",
        title: "6. 操作系统与外部边界",
        paragraphs: [
          "scrap 依赖操作系统提供用户账户隔离、文件权限、密钥保护、进程间通信和剪贴板。管理员权限、已被入侵的账户或设备、恶意软件、调试器、内存转储、休眠与交换文件，都可能越过应用自身能够提供的保护。静态加密不能替代设备安全。",
          "备份必须同时考虑加密数据库和操作系统保护的密钥材料。只恢复数据库可能无法解密 value；而云备份、企业管理、杀毒软件、文件索引、虚拟机快照和剪贴板同步可能按照各自政策处理本机文件或明文。它们不是 scrap 或其开发者控制的服务。",
        ],
      },
      {
        id: "website",
        title: "7. 产品网站与下载",
        paragraphs: [
          "本网站没有账户、广告、分析脚本或联系表单，也不设置用于跟踪的 Cookie。明暗主题选择仅保存在你的浏览器 localStorage 中。",
          "网站由 GitHub Pages 托管，并从 style.moesegfault.dev 加载样式；下载链接指向 GitHub Releases。访问这些资源时，你的 IP 地址、User-Agent 和请求时间等常规网络数据会由相应托管或网络服务处理，并受其各自政策约束。项目开发者没有通过网站添加独立的数据收集端点。",
        ],
      },
      {
        id: "changes",
        title: "8. 变更与联系",
        paragraphs: [
          "如果产品的数据流、存储格式或外部服务发生实质变化，本政策会随发布更新，并修改页面上的生效日期。历史变更可在公开仓库中检查。",
          "如对本政策或实现行为有疑问，请在 GitHub 仓库提交 issue。你提交给 GitHub 的内容会由 GitHub 按其政策处理；请勿在 issue 中粘贴 secret 或其他敏感内容。",
        ],
      },
    ],
    contactLabel: "前往 GitHub 仓库",
  },
  en: {
    meta: {
      title: "Privacy Policy — scrap",
      description: "How scrap processes, stores, and deletes data locally, including the boundaries of encryption, clipboard handling, the website, and the operating system.",
    },
    eyebrow: "PRIVACY · LOCAL-FIRST",
    title: "Your content stays on your device. The boundaries deserve equal clarity.",
    lead: "scrap has no account, cloud sync, telemetry, or developer-operated data service. This policy explains what the app processes locally, what is encrypted, and where the operating system, clipboard, and backups sit outside scrap's sole control.",
    effectiveDate: "Effective September 15, 2026",
    summaryTitle: "The short version",
    summary: [
      "The scrap developers do not receive, collect, or transmit content you store through the app.",
      "Content you enter may still be personal data, and scrap processes it on your device.",
      "Record values are encrypted; scope names, keys, timestamps, and display metadata are not.",
    ],
    contentsLabel: "In this policy",
    sections: [
      {
        id: "scope",
        title: "1. Scope and processing",
        paragraphs: [
          "This policy covers the scrap desktop app, command-line interface (CLI), and local scrapd daemon. They work together on the current operating-system user's device. The developers operate no backend that receives records, searches, or usage data from these components.",
          "This does not mean that scrap never processes personal data. If you store a name, access token, or other identifiable information in a record, the app processes it locally to store, retrieve, display, copy, and delete it. You should store only content you have the right to process.",
        ],
      },
      {
        id: "local-data",
        title: "2. Data stored on your device",
        paragraphs: ["Primary data and runtime state live under ~/.scrap in the current user's home directory. The platform uses the equivalent user path; on Windows this is normally %USERPROFILE%\\.scrap."],
        points: [
          "~/.scrap/data/scrap.db: the SQLite database. Record values are stored as ciphertext produced by authenticated encryption (AEAD).",
          "Unencrypted metadata: scope names, record keys, creation and update timestamps, revisions, plain/masked display policies, and internal database identifiers. Someone who can read the database file may see this information.",
          "~/.scrap/data/key-reference: a reference to the operating-system-protected key; the database itself does not contain a directly usable plaintext master key.",
          "~/.scrap/run and config.json: runtime information such as local inter-process communication (IPC), locks, and configuration. Contents of run are ordinarily reproducible.",
          "~/.scrap/log/scrap.log: daemon diagnostics. The implementation does not intentionally log record values or IPC request payloads, but the log does retain operational information such as times, event types, and error categories.",
          "The GUI stores theme and language preferences in the operating system's Local Application Data/MoeSegFault/Scrap/preferences.json. Searches and record content are not written there. The website separately stores its theme choice in browser localStorage.",
        ],
      },
      {
        id: "transmission",
        title: "3. Collection, transmission, and sharing",
        paragraphs: [
          "The scrap app contains no account system, advertising, analytics telemetry, or cloud sync. It does not send records, search terms, preferences, or runtime logs to the project developers. The developers cannot sell or share app data they do not receive.",
          "If you intentionally pass content through terminal redirection, a pipe, the clipboard, backup software, or another application, it enters the external component you selected. That transfer is controlled by your action and that component; it is not a transmission to the scrap developers.",
        ],
      },
      {
        id: "clipboard",
        title: "4. Display and clipboard behavior",
        paragraphs: [
          "Masked is a display and clipboard policy, not a different encryption strength. Every record value uses the same at-rest encryption model; plain/masked metadata controls only its default presentation.",
          "After the GUI copies a masked record, scrap waits 30 seconds and attempts to clear the clipboard only if it still contains the exact value from that copy. Clearing is best-effort: a clipboard manager, sync feature, or other process may already have retained a copy. Copying a plain record is not cleared automatically.",
          "CLI get writes the value to standard output by default; --clipboard writes it to the system clipboard only at your explicit request. The CLI does not automatically clear its output or clipboard content. A terminal, shell history, pipeline recipient, or clipboard tool may retain it separately.",
        ],
      },
      {
        id: "retention",
        title: "5. Retention, deletion, and uninstall",
        paragraphs: [
          "Deleting a record or scope makes it logically inaccessible through scrap, but does not promise physical secure erasure. SQLite free pages, its write-ahead log (WAL), filesystem snapshots, SSD behavior, or backups may retain historical copies temporarily or indefinitely.",
          "Ordinary upgrades and uninstall remove only program files and integrations owned by the package; they preserve data under ~/.scrap. To remove local data, you must explicitly delete ~/.scrap after confirming you no longer need its backups. GUI preferences live under platform Local Application Data and may need separate removal. Even then, scrap cannot guarantee erasure from the operating-system key store, backups, snapshots, or storage media.",
        ],
      },
      {
        id: "boundaries",
        title: "6. Operating-system and external boundaries",
        paragraphs: [
          "scrap relies on the operating system for user-account isolation, file permissions, key protection, inter-process communication, and the clipboard. Administrator access, a compromised account or device, malware, debuggers, memory dumps, hibernation, and swap files can cross protections the app can provide. Encryption at rest is not a substitute for device security.",
          "Backups must account for both the encrypted database and operating-system-protected key material. Restoring only the database may leave values undecryptable. Cloud backup, enterprise management, antivirus software, file indexing, virtual-machine snapshots, and clipboard sync may process local files or plaintext under their own policies. They are not services controlled by scrap or its developers.",
        ],
      },
      {
        id: "website",
        title: "7. Product website and downloads",
        paragraphs: [
          "This website has no accounts, advertising, analytics scripts, or contact forms, and sets no tracking cookies. Its light/dark theme choice is stored only in your browser's localStorage.",
          "GitHub Pages hosts the site, style.moesegfault.dev supplies its stylesheet, and download links lead to GitHub Releases. When you request those resources, the relevant host or network provider processes ordinary network data such as your IP address, User-Agent, and request time under its own policies. The project developers have added no separate collection endpoint to the site.",
        ],
      },
      {
        id: "changes",
        title: "8. Changes and contact",
        paragraphs: [
          "If the product's data flow, storage format, or external services change materially, this policy will be updated with a revised effective date. Its history remains inspectable in the public repository.",
          "For questions about this policy or the implementation, open an issue in the GitHub repository. GitHub processes anything you submit under its own policy; never paste a secret or other sensitive content into an issue.",
        ],
      },
    ],
    contactLabel: "Visit the GitHub repository",
  },
};
