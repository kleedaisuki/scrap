/** Supported website locales. / 网站支持的语言区域。 */
export type Locale = "zh" | "en";

/** Localized product-page copy contract. / 产品页本地化文案契约。 */
export interface SiteCopy {
  meta: { title: string; description: string };
  nav: { features: string; design: string; download: string; github: string };
  theme: { label: string; system: string; light: string; dark: string };
  hero: {
    eyebrow: string;
    title: string;
    lead: string;
    primary: string;
    secondary: string;
    availability: string;
  };
  demo: {
    scope: string;
    scopes: string[];
    search: string;
    mode: string;
    keys: string[];
    selected: string;
    value: string;
    copy: string;
    reveal: string;
  };
  problem: { kicker: string; title: string; body: string };
  features: Array<{ number: string; title: string; body: string; tag: string }>;
  architecture: {
    kicker: string;
    title: string;
    body: string;
    nodes: string[];
    notes: Array<{ title: string; body: string }>;
  };
  download: {
    kicker: string;
    title: string;
    body: string;
    platforms: Array<{ name: string; detail: string; action: string }>;
    footnote: string;
    signingPolicy: string;
  };
  footer: { line: string; license: string };
}

export const copy: Record<Locale, SiteCopy> = {
  zh: {
    meta: {
      title: "scrap — 把零散的秘密，收好，也随手可取",
      description: "scrap 是面向开发者的本地优先秘密与字段存储：原生桌面体验、可组合 CLI、加密 SQLite，以及跨 scope 检索。",
    },
    nav: { features: "为什么是 scrap", design: "如何运作", download: "下载", github: "GitHub" },
    theme: { label: "切换外观", system: "跟随系统", light: "浅色", dark: "深色" },
    hero: {
      eyebrow: "LOCAL-FIRST · GUI + CLI",
      title: "把零散的秘密，收好，也随手可取。",
      lead: "scrap 在你的电脑上保存令牌、密码与短文本。用图形界面快速检索和复制，用 CLI 接进脚本；值经过加密，不送往云端。",
      primary: "下载 Windows 版",
      secondary: "查看其他平台",
      availability: "Windows · macOS · Linux",
    },
    demo: {
      scope: "检索范围",
      scopes: ["全部 scope", "cloudflare", "github", "staging"],
      search: "搜索 key…",
      mode: "模糊匹配",
      keys: ["api_token", "deploy_hook", "account_id"],
      selected: "cloudflare / api_token",
      value: "••••••••••••••••••••",
      copy: "复制",
      reveal: "显示",
    },
    problem: {
      kicker: "少一点零散，多一点确定",
      title: "不是又一个密码管理器。是开发工作流里缺失的那一小块。",
      body: "当一个值需要被人找到、被脚本读取，又不该进入云端或仓库时，scrap 给它一个简单、明确的位置。没有账户体系，没有同步焦虑，也没有把本地工具包装成远程服务。",
    },
    features: [
      { number: "01", title: "找得到，才算收好了。", body: "一次检索一个、多个或全部 scope；相同 key 也会带着归属清楚出现。value 从不进入搜索索引。", tag: "多 scope 检索" },
      { number: "02", title: "该藏的藏，该看的清楚。", body: "遮罩只决定默认怎么显示，不会假装成另一种加密。编辑时想看就看，保存策略互不干扰。", tag: "一致的加密语义" },
      { number: "03", title: "写给人，也写给工具。", body: "桌面端适合发现与复制；CLI 保持干净的输出、明确的退出码和可组合的行为。", tag: "原生 GUI + CLI" },
      { number: "04", title: "本地，就是边界。", body: "数据不会绕路经过 Web 服务。守护进程是唯一数据权威，SQLite 中的 value 加密保存，主密钥交给操作系统保护。", tag: "Local-first" },
    ],
    architecture: {
      kicker: "小系统，硬边界",
      title: "看得懂的架构，才值得长期信任。",
      body: "客户端只表达意图。scrapd 统一处理检索、事务与加密，让 GUI 和脚本观察到同一套领域语义。内部可以持续演进，对外契约保持稳定。",
      nodes: ["桌面 GUI", "本地 IPC", "scrapd", "AEAD + OS 密钥保护", "加密 SQLite"],
      notes: [
        { title: "单一数据权威", body: "GUI 与 CLI 都不直接打开数据库，也不持有主密钥。" },
        { title: "按需启动", body: "无需常驻系统服务；客户端需要时唤起，空闲后安静退出。" },
        { title: "可检查的实现", body: "开放源代码，版本化协议，确定的错误语义与发布校验和。" },
      ],
    },
    download: {
      kicker: "准备好收拢那些散落的值了吗？",
      title: "选择你的平台，下载方式一目了然。",
      body: "Windows MSI 一次安装桌面端、CLI 与守护进程；macOS 与 Linux 提供自包含归档。无论选择哪种资产，升级都保留已有数据。",
      platforms: [
        { name: "Windows", detail: "x64 · MSI 安装程序", action: "下载 Windows 版" },
        { name: "macOS", detail: "Apple Silicon / Intel", action: "查看 macOS 资产" },
        { name: "Linux", detail: "x64 · tar.gz", action: "查看 Linux 资产" },
      ],
      footnote: "所有下载均由 GitHub Releases 提供，并附版本说明、SHA-256 校验和与签名状态。未签名版本可能触发 Windows SmartScreen 提示。",
      signingPolicy: "Code signing policy",
    },
    footer: { line: "在本机，安静地保存重要的值。", license: "GPL-3.0 开源软件" },
  },
  en: {
    meta: {
      title: "scrap — Keep small secrets close and find them fast",
      description: "A local-first secret and field store for developers, with a native desktop experience, composable CLI, encrypted SQLite, and multi-scope search.",
    },
    nav: { features: "Why scrap", design: "How it works", download: "Download", github: "GitHub" },
    theme: { label: "Change appearance", system: "System", light: "Light", dark: "Dark" },
    hero: {
      eyebrow: "LOCAL-FIRST · GUI + CLI",
      title: "Keep small secrets close—and find them fast.",
      lead: "scrap keeps tokens, passwords, and short text on your computer. Find and copy them in the desktop app, or compose them into scripts with the CLI—encrypted at rest, without a cloud account.",
      primary: "Download for Windows",
      secondary: "Other platforms",
      availability: "Windows · macOS · Linux",
    },
    demo: {
      scope: "Search scope",
      scopes: ["All scopes", "cloudflare", "github", "staging"],
      search: "Search keys…",
      mode: "Fuzzy",
      keys: ["api_token", "deploy_hook", "account_id"],
      selected: "cloudflare / api_token",
      value: "••••••••••••••••••••",
      copy: "Copy",
      reveal: "Reveal",
    },
    problem: {
      kicker: "Less scattered. More certain.",
      title: "Not another password manager. The missing piece in a developer workflow.",
      body: "When a value must be found by a person, read by a script, and never sent to a cloud or repository, scrap gives it one clear home. No accounts. No sync anxiety. No remote service disguised as a local tool.",
    },
    features: [
      { number: "01", title: "Stored is only useful when it is findable.", body: "Search one, several, or every scope. Duplicate keys remain clear because their scope travels with them; values never enter the search index.", tag: "Multi-scope search" },
      { number: "02", title: "Mask what should stay quiet.", body: "Masking controls the default view, not a different kind of encryption. Showing a value while editing never changes how it is saved.", tag: "One encryption model" },
      { number: "03", title: "Friendly to people. Predictable for tools.", body: "The desktop app is made for finding and copying; the CLI keeps output clean, exits explicit, and pipelines composable.", tag: "Native GUI + CLI" },
      { number: "04", title: "Local is the boundary.", body: "Data never detours through a web service. The daemon is the sole authority, values are encrypted in SQLite, and the OS protects the master key.", tag: "Local-first" },
    ],
    architecture: {
      kicker: "A small system with hard boundaries",
      title: "Architecture you can understand is architecture you can trust.",
      body: "Clients express intent. scrapd owns search, transactions, and encryption, so the GUI and scripts observe the same domain semantics. Internals can evolve; external contracts stay stable.",
      nodes: ["Desktop GUI", "Local IPC", "scrapd", "AEAD + OS key protection", "Encrypted SQLite"],
      notes: [
        { title: "One data authority", body: "Neither GUI nor CLI opens the database or holds the master key." },
        { title: "Starts on demand", body: "No permanent system service. A client wakes it; an idle daemon leaves quietly." },
        { title: "Inspectable by design", body: "Open source, versioned protocol, deterministic errors, and release checksums." },
      ],
    },
    download: {
      kicker: "Ready to gather the values that matter?",
      title: "Choose a platform. Know exactly what you get.",
      body: "The Windows MSI installs the desktop app, CLI, and daemon together; macOS and Linux ship as self-contained archives. Every upgrade preserves existing data.",
      platforms: [
        { name: "Windows", detail: "x64 · MSI installer", action: "Download for Windows" },
        { name: "macOS", detail: "Apple Silicon / Intel", action: "View macOS assets" },
        { name: "Linux", detail: "x64 · tar.gz", action: "View Linux assets" },
      ],
      footnote: "GitHub Releases provides every download with release notes, SHA-256 checksums, and signing status. Unsigned builds may trigger Windows SmartScreen.",
      signingPolicy: "Code signing policy",
    },
    footer: { line: "Keep the values that matter, quietly, on your machine.", license: "Open source under GPL-3.0" },
  },
};

/** Returns copy for a supported locale. / 返回受支持语言的页面文案。 */
export function getCopy(locale: Locale): SiteCopy {
  return copy[locale];
}
