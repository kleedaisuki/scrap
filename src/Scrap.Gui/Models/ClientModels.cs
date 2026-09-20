namespace Scrap.Gui.Models;

/// <summary>
/// 搜索匹配模式。Search matching mode.
/// </summary>
public enum SearchMode
{
    /// <summary>精确匹配。Exact matching.</summary>
    Exact,

    /// <summary>模糊候选排序。Fuzzy candidate ranking.</summary>
    Fuzzy,

    /// <summary>正则表达式匹配。Regular-expression matching.</summary>
    Regex,
}

/// <summary>界面主题选择。User-facing theme preference.</summary>
public enum AppTheme
{
    /// <summary>跟随操作系统。Follow the operating system.</summary>
    System,

    /// <summary>浅色主题。Light theme.</summary>
    Light,

    /// <summary>深色主题。Dark theme.</summary>
    Dark,
}

/// <summary>支持的界面语言。Supported user-interface language.</summary>
public enum AppLanguage
{
    /// <summary>简体中文。Simplified Chinese.</summary>
    SimplifiedChinese,

    /// <summary>英语。English.</summary>
    English,
}

/// <summary>带稳定值和本地化标签的选择项。A choice with a stable value and localized label.</summary>
/// <typeparam name="T">稳定设置值的类型。Stable setting value type.</typeparam>
/// <param name="Value">稳定设置值。Stable setting value.</param>
/// <param name="Label">当前语言下的标签。Label in the active language.</param>
public sealed record LocalizedChoice<T>(T Value, string Label) where T : struct, Enum;

/// <summary>
/// 记录值的呈现策略；它不改变存储或加密语义。
/// Presentation policy for a record value; it does not change storage or encryption semantics.
/// </summary>
public enum RecordPresentation
{
    /// <summary>默认遮罩。Masked by default.</summary>
    Masked,

    /// <summary>可以直接显示。May be shown directly.</summary>
    Plain,
}

/// <summary>
/// 一个可见的 scope 摘要。A visible scope summary.
/// </summary>
/// <param name="Name">大小写敏感且不隐式裁剪的名称。Case-sensitive name that is not implicitly trimmed.</param>
/// <param name="RecordCount">scope 中的记录数量。Number of records in the scope.</param>
public sealed record ScopeSummary(string Name, int RecordCount);

/// <summary>
/// 搜索请求。Search request.
/// </summary>
/// <param name="Scopes">精确 scope 集合；空集合表示所有 scope。Exact scopes; empty means all scopes.</param>
/// <param name="Query">只匹配 key 的查询。Query matched only against keys.</param>
/// <param name="Mode">匹配模式。Matching mode.</param>
/// <param name="CaseSensitive">是否使用大小写敏感匹配。Whether matching is case-sensitive.</param>
/// <param name="Limit">最大候选数量。Maximum candidate count.</param>
public sealed record RecordSearchRequest(
    IReadOnlyList<string> Scopes,
    string Query,
    SearchMode Mode,
    bool CaseSensitive,
    int Limit);

/// <summary>
/// 搜索返回的轻量候选；score 仅用于排序，界面不会伪装成百分比。
/// Lightweight search candidate; the score is only for ordering and is never presented as a percentage.
/// </summary>
/// <param name="Scope">候选所属 scope。Scope owning the candidate.</param>
/// <param name="Key">记录 key。Record key.</param>
/// <param name="Presentation">呈现策略。Presentation policy.</param>
/// <param name="Score">可选的不透明排序分数。Optional opaque ranking score.</param>
public sealed record RecordCandidate(string Scope, string Key, RecordPresentation Presentation, double? Score = null);

/// <summary>
/// 用户明确选中后加载的完整记录。Full record loaded only after explicit user selection.
/// </summary>
/// <param name="Scope">所属 scope。Owning scope.</param>
/// <param name="Key">记录 key。Record key.</param>
/// <param name="Values">非空、有序的 UTF-8 文本值。Non-empty ordered UTF-8 text values.</param>
/// <param name="Presentation">呈现策略。Presentation policy.</param>
/// <param name="UpdatedAt">最近修改时间。Last modification time.</param>
/// <param name="Revision">条件 mutation 使用的修订号。Revision used for conditional mutations.</param>
public sealed record RecordDetails(
    string Scope,
    string Key,
    IReadOnlyList<string> Values,
    RecordPresentation Presentation,
    DateTimeOffset UpdatedAt,
    long Revision)
{
    /// <summary>非空的值快照。Non-empty immutable value snapshot.</summary>
    public IReadOnlyList<string> Values { get; init; } = Values.Count > 0
        ? Values.ToArray()
        : throw new ArgumentException("A record must contain at least one value.", nameof(Values));

    /// <summary>从旧版单 value 构造记录。Constructs a record from a legacy single value.</summary>
    public RecordDetails(
        string scope,
        string key,
        string value,
        RecordPresentation presentation,
        DateTimeOffset updatedAt,
        long revision)
        : this(scope, key, [value], presentation, updatedAt, revision)
    {
    }

    /// <summary>
    /// 旧版单值访问器；新代码应遍历 <see cref="Values" />。
    /// Legacy single-value accessor; new code should enumerate <see cref="Values" />.
    /// </summary>
    public string Value => Values[0];
}

/// <summary>
/// 创建或完整替换一条记录的请求。Request to create or wholly replace a record.
/// </summary>
/// <param name="Scope">目标 scope。Target scope.</param>
/// <param name="Key">新 key。New key.</param>
/// <param name="Values">完整、非空的新 value 集合。Complete non-empty replacement value collection.</param>
/// <param name="Presentation">呈现策略。Presentation policy.</param>
/// <param name="OriginalKey">编辑时的原 key；新建时为 null。Original key while editing; null while creating.</param>
/// <param name="ExpectedRevision">编辑时的乐观并发修订号。Optimistic-concurrency revision while editing.</param>
public sealed record SaveRecordRequest(
    string Scope,
    string Key,
    IReadOnlyList<string> Values,
    RecordPresentation Presentation,
    string? OriginalKey,
    long? ExpectedRevision)
{
    /// <summary>要原子替换的非空值快照。Non-empty value snapshot to replace atomically.</summary>
    public IReadOnlyList<string> Values { get; init; } = Values.Count > 0
        ? Values.ToArray()
        : throw new ArgumentException("A record must contain at least one value.", nameof(Values));

    /// <summary>从旧版单 value 构造保存请求。Constructs a save request from a legacy single value.</summary>
    public SaveRecordRequest(
        string scope,
        string key,
        string value,
        RecordPresentation presentation,
        string? originalKey,
        long? expectedRevision)
        : this(scope, key, [value], presentation, originalKey, expectedRevision)
    {
    }

    /// <summary>
    /// 旧版单值访问器；协议边界应传输完整的有序集合。
    /// Legacy single-value accessor; protocol boundaries should transmit the complete ordered collection.
    /// </summary>
    public string Value => Values[0];
}

/// <summary>
/// 记录编辑器中的一个有序 value。An ordered value in the record editor.
/// </summary>
public sealed class RecordValueEditor : Infrastructure.ViewModelBase
{
    private string _value;
    private int _position;
    private readonly Func<int, string> _valueName;
    private readonly Func<int, string> _moveUpName;
    private readonly Func<int, string> _moveDownName;

    /// <summary>创建 value 编辑项。Creates a value editor item.</summary>
    internal RecordValueEditor(
        string value,
        int position,
        Func<int, string> valueName,
        Func<int, string> moveUpName,
        Func<int, string> moveDownName)
    {
        _value = value;
        _position = position;
        _valueName = valueName;
        _moveUpName = moveUpName;
        _moveDownName = moveDownName;
    }

    /// <summary>面向用户的一基位置。User-facing one-based position.</summary>
    public int Position
    {
        get => _position;
        private set
        {
            if (SetProperty(ref _position, value))
            {
                OnPropertyChanged(nameof(AccessibleName));
                OnPropertyChanged(nameof(MoveUpAccessibleName));
                OnPropertyChanged(nameof(MoveDownAccessibleName));
            }
        }
    }

    /// <summary>可编程的 value 名称。Programmatic value name.</summary>
    public string AccessibleName => _valueName(Position);

    /// <summary>带 value 编号的上移辅助名称。Move-up accessible name including the value number.</summary>
    public string MoveUpAccessibleName => _moveUpName(Position);

    /// <summary>带 value 编号的下移辅助名称。Move-down accessible name including the value number.</summary>
    public string MoveDownAccessibleName => _moveDownName(Position);

    /// <summary>在集合变更后更新一基位置。Updates the one-based position after a collection change.</summary>
    internal void SetPosition(int position) => Position = position;

    /// <summary>语言变更后刷新本地化辅助名称。Refreshes localized accessible names after a language change.</summary>
    internal void RefreshAccessibleNames()
    {
        OnPropertyChanged(nameof(AccessibleName));
        OnPropertyChanged(nameof(MoveUpAccessibleName));
        OnPropertyChanged(nameof(MoveDownAccessibleName));
    }

    /// <summary>完整的 UTF-8 文本。Complete UTF-8 text.</summary>
    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }
}

/// <summary>
/// 一个 value 的显示投影；原值仅作为显式复制命令参数。
/// Display projection for one value; the raw value is used only by an explicit copy command.
/// </summary>
public sealed record RecordValueDisplay(
    int Position,
    string Text,
    string RawValue,
    bool IsMasked,
    string RevealButtonText);

/// <summary>
/// GUI 可理解的 client 失败类别。Client failure categories understood by the GUI.
/// </summary>
public enum ScrapClientErrorKind
{
    /// <summary>daemon 不可用。Daemon is unavailable.</summary>
    DaemonUnavailable,

    /// <summary>平台密钥提供器不可用。Platform key provider is unavailable.</summary>
    KeyProviderUnavailable,

    /// <summary>存储损坏。Store is corrupt.</summary>
    CorruptStore,

    /// <summary>持久化存储暂不可用。Persistent store is temporarily unavailable.</summary>
    StoreUnavailable,

    /// <summary>查询表达式无效。Query expression is invalid.</summary>
    InvalidQuery,

    /// <summary>唯一性或并发冲突。Uniqueness or concurrency conflict.</summary>
    Conflict,

    /// <summary>目标不存在。Target was not found.</summary>
    NotFound,

    /// <summary>mutation 已发送但最终结果未知。Mutation was sent but its final outcome is unknown.</summary>
    OutcomeUnknown,

    /// <summary>其他不含秘密值的失败。Other failure whose message contains no secret value.</summary>
    Unknown,
}

/// <summary>
/// 由协议适配器归一化、且消息不得含 value 的异常。
/// Exception normalized by the protocol adapter whose message must never contain a value.
/// </summary>
public sealed class ScrapClientException : Exception
{
    /// <summary>
    /// 创建 client 异常。Creates a client exception.
    /// </summary>
    /// <param name="kind">可供 UI 决策的类别。Category used for UI decisions.</param>
    /// <param name="message">不包含秘密值的诊断。Diagnostic containing no secret value.</param>
    /// <param name="innerException">可选的底层异常。Optional underlying exception.</param>
    public ScrapClientException(
        ScrapClientErrorKind kind,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    /// <summary>
    /// 可供 UI 决策的失败类别。Failure category used for UI decisions.
    /// </summary>
    public ScrapClientErrorKind Kind { get; }
}
