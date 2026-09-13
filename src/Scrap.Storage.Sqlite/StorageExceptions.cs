namespace Scrap.Storage.Sqlite;

/// <summary>存储实体类别，用于稳定映射协议错误。 / Storage entity kind used for stable protocol-error mapping.</summary>
public enum StorageEntityKind
{
    /// <summary>scope 实体。 / A scope entity.</summary>
    Scope,
    /// <summary>record 实体。 / A record entity.</summary>
    Record,
}

/// <summary>写入冲突类别，用于稳定映射协议错误。 / Write-conflict kind used for stable protocol-error mapping.</summary>
public enum StorageConflictKind
{
    /// <summary>scope 已存在。 / Scope already exists.</summary>
    ScopeExists,
    /// <summary>record 已存在。 / Record already exists.</summary>
    RecordExists,
    /// <summary>乐观并发条件不匹配。 / Optimistic-concurrency condition mismatch.</summary>
    Concurrency,
}

/// <summary>
/// SQLite 存储契约异常的基类；消息只描述元数据，绝不包含 value。<br/>
/// Base class for SQLite storage-contract failures; messages describe metadata only and never contain values.
/// </summary>
public abstract class StorageException : Exception
{
    /// <summary>初始化异常。 / Initializes the exception.</summary>
    protected StorageException(string message, Exception? innerException = null) : base(message, innerException) { }
}

/// <summary>请求的 scope 或 record 不存在。 / The requested scope or record does not exist.</summary>
public sealed class StorageNotFoundException : StorageException
{
    /// <summary>初始化不存在异常。 / Initializes a not-found failure.</summary>
    public StorageNotFoundException(StorageEntityKind entity, string identity)
        : base($"{entity} was not found: {identity}")
    {
        Entity = entity;
        Identity = identity;
    }

    /// <summary>不存在的实体类别。 / Kind of entity that was not found.</summary>
    public StorageEntityKind Entity { get; }

    /// <summary>安全的 scope/key 元数据身份；不含 value。 / Safe scope/key metadata identity; never contains a value.</summary>
    public string Identity { get; }
}

/// <summary>唯一性或条件写入冲突。 / A uniqueness or conditional-write conflict.</summary>
public sealed class StorageConflictException : StorageException
{
    /// <summary>初始化冲突异常。 / Initializes a conflict failure.</summary>
    public StorageConflictException(StorageConflictKind kind, string message, Exception? innerException = null)
        : base(message, innerException) => Kind = kind;

    /// <summary>冲突类别。 / Conflict kind.</summary>
    public StorageConflictKind Kind { get; }
}

/// <summary>未请求递归删除时目标 scope 仍含 records。 / The scope contains records when recursive deletion was not requested.</summary>
public sealed class ScopeNotEmptyException : StorageException
{
    /// <summary>初始化非空 scope 异常。 / Initializes the non-empty-scope failure.</summary>
    public ScopeNotEmptyException(string scopeName, long recordCount)
        : base($"Scope is not empty: {scopeName} ({recordCount} records)") => RecordCount = recordCount;

    /// <summary>scope 中的 record 数量。 / Number of records in the scope.</summary>
    public long RecordCount { get; }
}

/// <summary>数据库 schema 缺失、损坏或比本程序新。 / The schema is missing, malformed, or newer than this program.</summary>
public sealed class StorageMigrationException : StorageException
{
    /// <summary>初始化迁移异常。 / Initializes a migration failure.</summary>
    public StorageMigrationException(string message, Exception? innerException = null) : base(message, innerException) { }
}
