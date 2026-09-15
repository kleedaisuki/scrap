namespace Scrap.Cli;

/// <summary>
/// 展示策略；它不改变 value 的存储或加密语义。/
/// Presentation policy; it does not change value storage or encryption semantics.
/// </summary>
public enum RecordPresentation
{
    /// <summary>默认遮罩。/ Mask by default.</summary>
    Masked,
    /// <summary>允许直接展示。/ Allow direct display.</summary>
    Plain,
}

/// <summary>
/// record 搜索模式。/ Record search mode.
/// </summary>
public enum SearchMode
{
    /// <summary>精确匹配。/ Exact matching.</summary>
    Exact,
    /// <summary>模糊候选排序。/ Fuzzy candidate ranking.</summary>
    Fuzzy,
    /// <summary>.NET 正则匹配。/ .NET regular-expression matching.</summary>
    Regex,
}

/// <summary>
/// 可映射为稳定 CLI 退出码的客户端错误类别。/
/// Client error categories that map to stable CLI exit codes.
/// </summary>
public enum ScrapErrorKind
{
    /// <summary>对象不存在。/ Object not found.</summary>
    NotFound,
    /// <summary>唯一性或并发冲突。/ Uniqueness or concurrency conflict.</summary>
    Conflict,
    /// <summary>daemon、连接或协议失败。/ Daemon, connection, or protocol failure.</summary>
    Protocol,
    /// <summary>持久化、密钥或解密失败。/ Persistence, key, or decryption failure.</summary>
    Store,
    /// <summary>输入或查询无效。/ Invalid input or query.</summary>
    Validation,
}

/// <summary>
/// daemon 返回的安全、结构化错误；Message 不得包含 value 或 IPC payload。/
/// A safe structured daemon error; Message must never contain a value or IPC payload.
/// </summary>
public sealed class ScrapClientException : Exception
{
    /// <summary>初始化结构化客户端错误。/ Initializes a structured client error.</summary>
    public ScrapClientException(ScrapErrorKind kind, string message)
        : base(message)
    {
        Kind = kind;
    }

    /// <summary>获取稳定错误类别。/ Gets the stable error category.</summary>
    public ScrapErrorKind Kind { get; }
}

/// <summary>scope 列表项。/ A scope-list item.</summary>
public sealed record ScopeItem(string Name);

/// <summary>不包含 secret value 的 record 元数据。/ Record metadata without the secret value.</summary>
public sealed record RecordItem(
    string Scope,
    string Key,
    RecordPresentation Presentation,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long Revision);

/// <summary>record 读取结果。/ A record read result.</summary>
public sealed record RecordValue(string Value, RecordPresentation Presentation);

/// <summary>
/// 搜索请求；大小写是模式修饰符而不是独立模式。/
/// Search request; case sensitivity modifies rather than replaces the search mode.
/// </summary>
/// <param name="Scopes">精确 scope 名称；空集合检索全部。 / Exact scope names; empty searches all.</param>
/// <param name="Query">key 查询文本。 / Key query text.</param>
/// <param name="Mode">匹配模式。 / Matching mode.</param>
/// <param name="CaseSensitive">是否使用 ordinal 大小写敏感匹配。 / Whether matching uses ordinal case sensitivity.</param>
/// <param name="Limit">全局结果上限。 / Global result limit.</param>
public sealed record RecordSearch(
    IReadOnlyList<string> Scopes,
    string Query,
    SearchMode Mode,
    bool CaseSensitive,
    int Limit = 100);

/// <summary>daemon 版本协商结果。/ Daemon version-negotiation result.</summary>
public sealed record DaemonVersion(string ApplicationVersion, int MinProtocolVersion, int MaxProtocolVersion);

/// <summary>
/// CLI 与 IPC 实现之间的窄接口；实现负责 bootstrap、协议协商和 DTO 转换。/
/// Narrow boundary between CLI and IPC; implementations own bootstrap, negotiation, and DTO conversion.
/// </summary>
public interface IScrapClient
{
    /// <summary>列出 scope。/ Lists scopes.</summary>
    Task<IReadOnlyList<ScopeItem>> ListScopesAsync(CancellationToken cancellationToken);
    /// <summary>创建 scope。/ Creates a scope.</summary>
    Task CreateScopeAsync(string scope, CancellationToken cancellationToken);
    /// <summary>原子重命名 scope。/ Atomically renames a scope.</summary>
    Task RenameScopeAsync(string oldName, string newName, CancellationToken cancellationToken);
    /// <summary>删除 scope。/ Deletes a scope.</summary>
    Task<int> DeleteScopeAsync(string scope, bool recursive, CancellationToken cancellationToken);
    /// <summary>创建或整值替换 record。/ Creates or replaces a complete record value.</summary>
    Task SetRecordAsync(string scope, string key, string value, RecordPresentation presentation, CancellationToken cancellationToken);
    /// <summary>精确读取 record。/ Reads a record by exact identity.</summary>
    Task<RecordValue> GetRecordAsync(string scope, string key, CancellationToken cancellationToken);
    /// <summary>原子重命名 record key。/ Atomically renames a record key.</summary>
    Task RenameRecordAsync(string scope, string oldKey, string newKey, CancellationToken cancellationToken);
    /// <summary>精确删除 record。/ Deletes a record by exact identity.</summary>
    Task DeleteRecordAsync(string scope, string key, CancellationToken cancellationToken);
    /// <summary>列出 scope 中的 record 元数据。/ Lists record metadata in a scope.</summary>
    Task<IReadOnlyList<RecordItem>> ListRecordsAsync(string scope, CancellationToken cancellationToken);
    /// <summary>使用 daemon 的统一语义搜索 record。/ Searches records using daemon-owned semantics.</summary>
    Task<IReadOnlyList<RecordItem>> SearchRecordsAsync(RecordSearch search, CancellationToken cancellationToken);
    /// <summary>探测 daemon。/ Pings the daemon.</summary>
    Task PingAsync(CancellationToken cancellationToken);
    /// <summary>读取 daemon 与协议版本。/ Reads daemon and protocol versions.</summary>
    Task<DaemonVersion> GetDaemonVersionAsync(CancellationToken cancellationToken);
    /// <summary>请求 daemon 有序退出。/ Requests an orderly daemon shutdown.</summary>
    Task ShutdownDaemonAsync(CancellationToken cancellationToken);
    /// <summary>仅在 daemon 已运行时请求退出；不存在是成功 no-op。/ Requests shutdown only when already running; absence is a successful no-op.</summary>
    Task<bool> ShutdownIfRunningAsync(CancellationToken cancellationToken);
}
