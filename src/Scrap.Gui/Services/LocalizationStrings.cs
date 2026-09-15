using Scrap.Gui.Models;

namespace Scrap.Gui.Services;

/// <summary>
/// 一个语言的完整界面文案集。每次切换都替换整个对象，避免混合语言状态。
/// Complete UI copy for one language. Switching replaces the whole object to prevent mixed-language states.
/// </summary>
public sealed class LocalizationStrings
{
    private readonly AppLanguage _language;

    private LocalizationStrings(AppLanguage language) => _language = language;

    /// <summary>为指定语言创建完整文案。Creates the complete copy set for a language.</summary>
    public static LocalizationStrings For(AppLanguage language) => new(language);

    private string Pick(string zh, string en) => _language == AppLanguage.SimplifiedChinese ? zh : en;

    public string WindowTitle => Pick("scrap — 把秘密轻轻收好", "scrap — local secrets, softly kept");
    public string Tagline => Pick("本地优先的轻量保险箱", "A tiny, local-first vault");
    public string Scope => Pick("空间", "Scope");
    public string ChooseScope => Pick("选择空间", "Choose a scope");
    public string NewScope => Pick("＋ 新建空间", "+ New scope");
    public string Rename => Pick("重命名", "Rename");
    public string Delete => Pick("删除", "Delete");
    public string SearchPlaceholder => Pick("搜索记录键…（Ctrl+K 或 /）", "Search record keys… (Ctrl+K or /)");
    public string SearchIn => Pick("检索范围", "Search in");
    public string AllScopes => Pick("所有空间", "All scopes");
    public string ChooseSearchScopes => Pick("选择一个或多个空间", "Choose one or more scopes");
    public string SearchScopesHint => Pick("“所有空间”会检索全部；勾选空间可限定任意组合。", "All scopes searches everything; select scopes to search any combination.");
    public string Exact => Pick("精确", "Exact");
    public string Fuzzy => Pick("模糊", "Fuzzy");
    public string Regex => Pick("正则", "Regex");
    public string CaseSensitive => Pick("区分大小写", "Case-sensitive");
    public string Retry => Pick("重试", "Retry");
    public string Candidates => Pick("候选记录", "RECORD CANDIDATES");
    public string CandidatesHint => Pick("由守护进程排序 · 不加载秘密值", "Daemon-ranked · secret values stay unloaded");
    public string NewRecord => Pick("＋ 新建", "+ New");
    public string NoScopesTitle => Pick("还没有空间", "No scopes yet");
    public string NoScopesBody => Pick("先为秘密准备一个小房间吧。", "Give your secrets a small room of their own.");
    public string CreateFirstScope => Pick("创建第一个空间", "Create first scope");
    public string NoCandidates => Pick("没有找到候选记录 ૮ ˶ᵔ ᵕ ᵔ˶ ა", "No matching records ૮ ˶ᵔ ᵕ ᵔ˶ ა");
    public string CreateRecord => Pick("创建记录", "Create a record");
    public string ChooseKey => Pick("选择一条记录", "Choose a record");
    public string ChooseKeyHint => Pick("使用 ↑ ↓ 和回车键，或点击候选项。\n只有明确选择后才会加载值。", "Use ↑ ↓ and Enter, or click a candidate.\nValues load only after an explicit selection.");
    public string SelectedRecord => Pick("已选记录", "SELECTED RECORD");
    public string LocalOnly => Pick("仅限本机", "Local only");
    public string Value => Pick("值", "VALUE");
    public string Copy => Pick("复制  Ctrl+C", "Copy  Ctrl+C");
    public string Edit => Pick("编辑", "Edit");
    public string Theme => Pick("主题", "Theme");
    public string Language => Pick("语言", "Language");
    public string ScopeNameHint => Pick("名称会按原样保存，并区分大小写。", "Names are preserved verbatim and are case-sensitive.");
    public string ScopeName => Pick("空间名称", "Scope name");
    public string Cancel => Pick("取消", "Cancel");
    public string Save => Pick("保存", "Save");
    public string DeleteScopeTitle => Pick("删除空间？", "Delete scope?");
    public string DeleteScopeHint => Pick("确认后，非空空间及其中记录会一并删除。", "After confirmation, a non-empty scope and its records are deleted together.");
    public string DeleteExactScope => Pick("确认删除空间", "Delete scope");
    public string KeyLabel => Pick("记录键 · 区分大小写", "KEY · case-sensitive identity");
    public string KeyPlaceholder => Pick("记录键", "Record key");
    public string ValueLabel => Pick("记录值 · 仅在选择保存后写入", "VALUE · written only when you choose Save");
    public string ShowWhileEditing => Pick("编辑时显示", "Show while editing");
    public string MaskedTitle => Pick("默认遮罩显示", "Mask after saving");
    public string DefaultDisplay => Pick("默认显示方式", "Default display");
    public string MaskedHint => Pick("详情页默认隐藏；显示是临时的，复制后会尽力清理剪贴板。", "Hidden in details by default; reveal is temporary and the clipboard is cleared when possible.");
    public string VisibleTitle => Pick("直接显示", "Visible");
    public string VisibleHint => Pick("在详情页直接显示。", "Shown directly in record details.");
    public string EncryptionInvariant => Pick("两种方式使用相同的加密存储。", "Both options use the same encrypted storage.");
    public string SaveRecord => Pick("保存记录", "Save record");
    public string DeleteRecordTitle => Pick("删除记录？", "Delete record?");
    public string DeleteExactRecord => Pick("确认删除记录", "Delete record");
    public string SystemTheme => Pick("跟随系统", "System");
    public string LightTheme => Pick("浅色", "Light");
    public string DarkTheme => Pick("深色", "Dark");
    public string Chinese => Pick("简体中文", "简体中文");
    public string English => Pick("English", "English");
    public string Reveal => Pick("显示 15 秒", "Reveal for 15 seconds");
    public string Hide => Pick("立即隐藏", "Hide now");
    public string CreateScopeTitle => Pick("新建空间", "New scope");
    public string RenameScopeTitle => Pick("重命名空间", "Rename scope");
    public string CreateRecordTitle => Pick("新建记录", "New record");
    public string EditRecordTitle => Pick("编辑记录", "Edit record");
    public string ScopeRenamed => Pick("空间已重命名", "Scope renamed");
    public string ScopeCreated => Pick("空间已创建", "Scope created");
    public string ScopeDeleted => Pick("空间已删除", "Scope deleted");
    public string RecordSaved => Pick("记录已保存", "Record saved");
    public string RecordCreated => Pick("记录已创建", "Record created");
    public string RecordDeleted => Pick("记录已删除", "Record deleted");
    public string Copied => Pick("已复制", "Copied");
    public string ClipboardUnavailableTitle => Pick("剪贴板不可用", "Clipboard unavailable");
    public string ClipboardUnavailableBody => Pick("无法访问系统剪贴板，请重试。", "The system clipboard could not be accessed. Please retry.");
    public string RefreshFailedTitle => Pick("操作已完成，但刷新失败", "Saved, but refresh failed");
    public string RefreshFailedBody => Pick("变更已经提交，但界面可能仍是旧状态。请重试刷新；不要重复提交操作。", "The change was committed, but the view may be stale. Retry the refresh; do not repeat the mutation.");
    public string ScopeChangedBody => Pick("空间内容在确认后发生变化，因此没有删除。请重新打开确认窗口并核对最新数量。", "The scope changed after confirmation, so nothing was deleted. Reopen the confirmation and review the latest count.");
    public string RecordConflictEdit => Pick("记录已被其他客户端修改。草稿仍然保留；请刷新后重新核对。", "The record changed in another client. Your draft is preserved; refresh and review it.");
    public string RecordConflictCreate => Pick("该记录键已存在，原记录没有被覆盖。请使用其他记录键，或取消后刷新。", "That key already exists; the record was not overwritten. Choose another key, or cancel and refresh.");
    public string MutationTimedOut => Pick("等待操作完成时超时，最终结果未知。请刷新核对，不要盲目重试。", "The operation timed out and its final outcome is unknown. Refresh to verify before retrying.");
    public string GenericErrorBody => Pick("未能完成操作。请重试；如果问题持续，请检查 scrapd 日志。", "The operation could not be completed. Retry, then inspect the scrapd logs if it persists.");
    public string InvalidSearch => Pick("搜索表达式无效。请检查正则表达式或查询条件。", "The search expression is invalid. Check the regular expression or query options.");

    public string ErrorTitle(ScrapClientErrorKind kind) => kind switch
    {
        ScrapClientErrorKind.DaemonUnavailable => Pick("未连接 scrapd", "Not connected to scrapd"),
        ScrapClientErrorKind.KeyProviderUnavailable => Pick("密钥服务不可用", "Key provider unavailable"),
        ScrapClientErrorKind.CorruptStore => Pick("存储需要处理", "Store needs attention"),
        ScrapClientErrorKind.StoreUnavailable => Pick("存储不可用", "Store unavailable"),
        ScrapClientErrorKind.Conflict => Pick("记录冲突", "Record conflict"),
        ScrapClientErrorKind.NotFound => Pick("项目已变化", "Item changed"),
        ScrapClientErrorKind.OutcomeUnknown => Pick("结果未知", "Outcome unknown"),
        _ => Pick("操作失败", "Operation failed"),
    };

    public string RecordCount(int count) => Pick($"{count} 条记录", count == 1 ? "1 record" : $"{count} records");
    public string SelectedScopes(int count) => Pick($"已选 {count} 个空间", $"{count} scopes selected");
    public string RecordIdentity(string scope, string key) => Pick($"空间“{scope}” · 记录键“{key}”", $"Scope “{scope}” · key “{key}”");
    public string Updated(DateTimeOffset value) => Pick($"更新于 {value.ToLocalTime():yyyy-MM-dd HH:mm}", $"Updated {value.ToLocalTime():yyyy-MM-dd HH:mm}");
    public string DeleteScope(string name, int count) => count == 0
        ? Pick($"要删除空间“{name}”吗？", $"Delete scope “{name}”?" )
        : Pick($"要删除空间“{name}”及其中 {count} 条记录吗？", $"Delete scope “{name}” and its {count} records?");
    public string DeleteRecord(string scope, string key) => Pick($"删除空间“{scope}”中的记录“{key}”？", $"Delete record “{key}” from scope “{scope}”?" );
    public string Masked => Pick("遮罩", "Masked");
    public string Plain => Pick("直接显示", "Plain");
}
