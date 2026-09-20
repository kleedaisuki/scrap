using Scrap.Protocol;
using ProtocolSearchRequest = Scrap.Protocol.SearchRequest;

namespace Scrap.Daemon;

/// <summary>
/// 定义协议 handler 可调用的完整 daemon 用例边界。实现是 SQLite、Domain 与 Crypto 的唯一装配点。
/// / Defines the complete daemon use-case boundary consumed by protocol handlers. Its implementation is the sole composition point for SQLite, Domain, and Crypto.
/// </summary>
internal interface IDaemonOperations
{
    /// <summary>初始化唯一 store owner 与全生命周期 record encryptor。 / Initializes the sole store owner and lifetime record encryptor.</summary>
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>列出 scopes。 / Lists scopes.</summary>
    Task<ScopeListResult> ListScopesAsync(ScopeListParams parameters, CancellationToken cancellationToken);

    /// <summary>创建 scope。 / Creates a scope.</summary>
    Task<ScopeCreateResult> CreateScopeAsync(ScopeCreateParams parameters, CancellationToken cancellationToken);

    /// <summary>重命名 scope 并重加密其 records。 / Renames a scope and re-encrypts its records.</summary>
    Task<ScopeRenameResult> RenameScopeAsync(ScopeRenameParams parameters, CancellationToken cancellationToken);

    /// <summary>删除 scope。 / Deletes a scope.</summary>
    Task<ScopeDeleteResult> DeleteScopeAsync(ScopeDeleteParams parameters, CancellationToken cancellationToken);

    /// <summary>读取并解密 record。 / Reads and decrypts a record.</summary>
    Task<RecordGetResult> GetRecordAsync(RecordGetParams parameters, CancellationToken cancellationToken);

    /// <summary>按 scalar 或 whole-list 契约原子写入 record。 / Atomically writes a record under the scalar or whole-list contract.</summary>
    Task<RecordSetResult> SetRecordAsync(RecordSetParams parameters, CancellationToken cancellationToken);

    /// <summary>原子重命名并重加密 record。 / Atomically renames and re-encrypts a record.</summary>
    Task<RecordRenameResult> RenameRecordAsync(RecordRenameParams parameters, CancellationToken cancellationToken);

    /// <summary>删除 record。 / Deletes a record.</summary>
    Task<RecordDeleteResult> DeleteRecordAsync(RecordDeleteParams parameters, CancellationToken cancellationToken);

    /// <summary>列出 record 元数据。 / Lists record metadata.</summary>
    Task<RecordListResult> ListRecordsAsync(RecordListParams parameters, CancellationToken cancellationToken);

    /// <summary>只按 key 搜索 record 元数据。 / Searches record metadata by key only.</summary>
    Task<RecordSearchResult> SearchRecordsAsync(ProtocolSearchRequest parameters, CancellationToken cancellationToken);
}
