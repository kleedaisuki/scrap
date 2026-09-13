namespace Scrap.Storage.Sqlite;

/// <summary>
/// 表示 SQLite 中持久化的 scope 元数据。<br/>
/// Represents scope metadata persisted in SQLite.
/// </summary>
/// <param name="Id">数据库内部标识；用户不可见。 / Database-internal identifier; not user-visible.</param>
/// <param name="Name">按 ordinal、大小写敏感语义保存的名称。 / Name stored with ordinal, case-sensitive semantics.</param>
/// <param name="CreatedAt">UTC 创建时间。 / UTC creation time.</param>
/// <param name="UpdatedAt">UTC 最近修改时间。 / UTC last-modified time.</param>
public sealed record StoredScope(long Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>
/// 表示传给加密层的 record 身份；这些字段应进入 AAD。<br/>
/// Represents the record identity supplied to the crypto layer; these fields should be included in AAD.
/// </summary>
/// <param name="SchemaVersion">当前持久化 schema 版本。 / Current persistence schema version.</param>
/// <param name="RecordId">永久、随机且不随重命名改变的记录 ID。 / Permanent random record ID that survives renames.</param>
/// <param name="ScopeName">scope 的精确名称。 / Exact scope name.</param>
/// <param name="Key">记录的精确 key。 / Exact record key.</param>
public sealed record RecordIdentity(int SchemaVersion, string RecordId, string ScopeName, string Key);

/// <summary>
/// 保存认证加密产生的不透明密文、nonce 与算法格式版本。存储层不会接触明文。<br/>
/// Holds opaque authenticated ciphertext, nonce, and algorithm-format version. The storage layer never handles plaintext.
/// </summary>
/// <remarks>
/// 当前 Crypto 契约把 authentication tag 附加在 <see cref="Ciphertext"/> 中，而不是单独存列。<br/>
/// The current Crypto contract appends the authentication tag to <see cref="Ciphertext"/> instead of storing it in a separate column.
/// </remarks>
/// <param name="Ciphertext">包含 authentication tag 的认证密文。 / Authenticated ciphertext including its authentication tag.</param>
/// <param name="Nonce">本次加密唯一的 nonce。 / Nonce unique to this encryption operation.</param>
/// <param name="CryptoVersion">密文格式/算法版本。 / Ciphertext format/algorithm version.</param>
public sealed record ProtectedValue(ReadOnlyMemory<byte> Ciphertext, ReadOnlyMemory<byte> Nonce, int CryptoVersion = 1);

/// <summary>
/// 表示完整的加密 record 行。调用方只能在 Crypto 模块验证后解释密文。<br/>
/// Represents a complete encrypted record row. Callers may interpret ciphertext only after verification by the Crypto module.
/// </summary>
/// <param name="Identity">与密文 AAD 对应的逻辑身份。 / Logical identity corresponding to ciphertext AAD.</param>
/// <param name="ScopeId">数据库内部 scope ID。 / Database-internal scope ID.</param>
/// <param name="Value">不透明的加密值。 / Opaque protected value.</param>
/// <param name="Presentation">展示策略编码；存储层原样持久化。 / Presentation-policy code persisted without interpretation.</param>
/// <param name="Revision">每次 value 或身份变更均递增的乐观并发版本。 / Optimistic-concurrency version incremented for each value or identity change.</param>
/// <param name="CreatedAt">UTC 创建时间。 / UTC creation time.</param>
/// <param name="UpdatedAt">UTC 最近修改时间。 / UTC last-modified time.</param>
public sealed record StoredRecord(
    RecordIdentity Identity,
    long ScopeId,
    ProtectedValue Value,
    int Presentation,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>不加载密文的 record 元数据投影，用于 list/search。 / Ciphertext-free record metadata projection for list/search.</summary>
/// <param name="Identity">record 身份。 / Record identity.</param>
/// <param name="ScopeId">数据库内部 scope ID。 / Database-internal scope ID.</param>
/// <param name="Presentation">展示策略编码。 / Presentation-policy code.</param>
/// <param name="Revision">乐观并发版本。 / Optimistic-concurrency version.</param>
/// <param name="CreatedAt">UTC 创建时间。 / UTC creation time.</param>
/// <param name="UpdatedAt">UTC 最近修改时间。 / UTC last-modified time.</param>
public sealed record StoredRecordMetadata(
    RecordIdentity Identity,
    long ScopeId,
    int Presentation,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>事务外完成重加密后用于 scope rename 提交的 CAS replacement。 / CAS replacement used to commit a scope rename after re-encryption outside the transaction.</summary>
/// <param name="RecordId">永久 record ID。 / Permanent record ID.</param>
/// <param name="ExpectedRevision">准备 replacement 时观察到的 revision。 / Revision observed while preparing the replacement.</param>
/// <param name="Value">绑定新 scope 名称的密文。 / Ciphertext bound to the new scope name.</param>
public sealed record RecordReencryption(string RecordId, long ExpectedRevision, ProtectedValue Value);

/// <summary>record upsert 的事务结果。 / Transactional result of a record upsert.</summary>
/// <param name="Record">最终持久化 record。 / Final persisted record.</param>
/// <param name="Created">true 表示插入，false 表示替换。 / True for insertion; false for replacement.</param>
public sealed record StoredRecordSetResult(StoredRecord Record, bool Created);

/// <summary>
/// staged set 在事务外取得的 snapshot 与永久 ID；密文可据 <see cref="Identity"/> 在锁外生成。<br/>
/// Snapshot and permanent ID obtained outside a write transaction for staged set; ciphertext can be generated from <see cref="Identity"/> without holding the lock.
/// </summary>
/// <param name="Identity">用于 AAD 的最终 record 身份。 / Final record identity used for AAD.</param>
/// <param name="ScopeId">准备时观察到的内部 scope ID。 / Internal scope ID observed during preparation.</param>
/// <param name="IsNew">准备时 record 是否不存在。 / Whether the record was absent during preparation.</param>
/// <param name="CurrentRevision">已有 record 的 revision；新 record 为 null。 / Existing record revision; null for a new record.</param>
/// <param name="CurrentPresentation">已有展示策略；新 record 为 null。 / Existing presentation policy; null for a new record.</param>
/// <param name="CreatedAt">已有创建时间或为新 record 预留的创建时间。 / Existing creation time or reserved creation time for a new record.</param>
/// <param name="CurrentUpdatedAt">已有更新时间；新 record 为 null。 / Existing update time; null for a new record.</param>
public sealed record RecordSetPreparation(
    RecordIdentity Identity,
    long ScopeId,
    bool IsNew,
    long? CurrentRevision,
    int? CurrentPresentation,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CurrentUpdatedAt);

/// <summary>
/// scope 重命名的原子结果。<br/>Atomic result of a scope rename.
/// </summary>
/// <param name="Scope">重命名后的 scope。 / Renamed scope.</param>
/// <param name="ReencryptedRecordCount">同一事务中重写的 record 数量。 / Number of records rewritten in the same transaction.</param>
public sealed record ScopeRenameResult(StoredScope Scope, int ReencryptedRecordCount);

/// <summary>当前连接实际生效的非敏感 SQLite 状态。 / Non-sensitive SQLite state effective on the current connection.</summary>
/// <param name="SchemaVersion">持久化 schema 版本。 / Persisted schema version.</param>
/// <param name="JournalMode">journal mode，例如 wal。 / Journal mode, such as wal.</param>
/// <param name="Synchronous">SQLite synchronous 数值级别。 / Numeric SQLite synchronous level.</param>
/// <param name="ForeignKeys">外键约束是否启用。 / Whether foreign-key enforcement is enabled.</param>
/// <param name="BusyTimeoutMilliseconds">busy timeout 毫秒数。 / Busy timeout in milliseconds.</param>
public sealed record SqliteStoreStatus(
    int SchemaVersion,
    string JournalMode,
    int Synchronous,
    bool ForeignKeys,
    int BusyTimeoutMilliseconds);
