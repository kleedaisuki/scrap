using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Scrap.Storage.Sqlite;

/// <summary>
/// 使用短 ADO.NET 事务实现 scrap 的 SQLite 持久化边界。每个操作自行打开连接，且不泄露连接或 row。<br/>
/// Implements scrap's SQLite persistence boundary with short ADO.NET transactions. Each operation owns its connection and exposes neither connections nor rows.
/// </summary>
/// <remarks>
/// 调用 CRUD 前必须执行 <see cref="Initialize"/>。加密回调在事务中同步执行，不得进行 IPC、UI 或 key-store I/O。<br/>
/// Call <see cref="Initialize"/> before CRUD. Encryption callbacks run synchronously in a transaction and must not perform IPC, UI, or key-store I/O.
/// </remarks>
public sealed class SqliteStore
{
    /// <summary>当前可读写的 schema 版本。 / Current readable and writable schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    private const string SchemaVersionKey = "schema_version";
    private const string OrdinalCollation = "SCRAP_ORDINAL";
    private readonly string connectionString;
    private readonly int busyTimeoutMilliseconds;
    private readonly object initializationGate = new();
    private volatile bool initialized;

    /// <summary>
    /// 创建 store；构造函数不会创建文件。<br/>Creates a store; the constructor creates no files.
    /// </summary>
    /// <param name="databasePath">数据库文件路径。 / Database file path.</param>
    /// <param name="options">可选 SQLite 配置。 / Optional SQLite settings.</param>
    public SqliteStore(string databasePath, SqliteStoreOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        options ??= new SqliteStoreOptions();
        if (options.BusyTimeout < TimeSpan.Zero || options.BusyTimeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "BusyTimeout must fit in a non-negative 32-bit millisecond value.");
        }

        DatabasePath = Path.GetFullPath(databasePath);
        busyTimeoutMilliseconds = checked((int)options.BusyTimeout.TotalMilliseconds);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            DefaultTimeout = Math.Max(1, (int)Math.Ceiling(options.BusyTimeout.TotalSeconds)),
            Pooling = true,
        }.ToString();
    }

    /// <summary>数据库文件的规范绝对路径。 / Canonical absolute path of the database file.</summary>
    public string DatabasePath { get; }

    /// <summary>
    /// 建立或迁移 schema v1，并配置 WAL、FULL synchronous、foreign keys 与 busy timeout；可重复调用。<br/>
    /// Creates or migrates schema v1 and configures WAL, FULL synchronous, foreign keys, and busy timeout; safe to repeat.
    /// </summary>
    public void Initialize()
    {
        lock (initializationGate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
            using var connection = OpenConnection(requireInitialization: false);
            ConfigureJournal(connection);
            Migrate(connection);
            initialized = true;
        }
    }

    /// <summary>返回持久化 schema 版本。 / Returns the persisted schema version.</summary>
    public int GetSchemaVersion()
    {
        using var connection = OpenConnection();
        return ReadSchemaVersion(connection, transaction: null);
    }

    /// <summary>读取当前连接实际生效的非敏感 SQLite 配置，供健康检查与诊断使用。 / Reads effective non-sensitive SQLite settings for health checks and diagnostics.</summary>
    public SqliteStoreStatus GetStatus()
    {
        using var connection = OpenConnection();
        return new SqliteStoreStatus(
            ReadSchemaVersion(connection, transaction: null),
            ReadPragmaString(connection, "journal_mode"),
            ReadPragmaInt32(connection, "synchronous"),
            ReadPragmaInt32(connection, "foreign_keys") != 0,
            ReadPragmaInt32(connection, "busy_timeout"));
    }

    /// <summary>创建大小写敏感且不会隐式 trim 的 scope。 / Creates a case-sensitive scope without implicit trimming.</summary>
    public StoredScope CreateScope(string name, DateTimeOffset? now = null)
    {
        ValidateName(name, nameof(name));
        var timestamp = NormalizeTimestamp(now);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO scopes(name, created_at, updated_at)
            VALUES ($name, $createdAt, $updatedAt);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$createdAt", FormatTimestamp(timestamp));
        command.Parameters.AddWithValue("$updatedAt", FormatTimestamp(timestamp));
        try
        {
            var id = (long)(command.ExecuteScalar() ?? throw new InvalidOperationException("SQLite did not return a scope ID."));
            transaction.Commit();
            return new StoredScope(id, name, timestamp, timestamp);
        }
        catch (SqliteException exception) when (IsConstraintViolation(exception))
        {
            throw new StorageConflictException(StorageConflictKind.ScopeExists, $"Scope already exists: {name}", exception);
        }
    }

    /// <summary>精确读取 scope，不存在时返回 null。 / Reads an exact scope, returning null when absent.</summary>
    public StoredScope? GetScope(string name)
    {
        ValidateName(name, nameof(name));
        using var connection = OpenConnection();
        return FindScope(connection, transaction: null, name);
    }

    /// <summary>以 ordinal/BINARY 顺序列出 scopes。 / Lists scopes in ordinal/BINARY order.</summary>
    public IReadOnlyList<StoredScope> ListScopes()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, created_at, updated_at FROM scopes ORDER BY name COLLATE SCRAP_ORDINAL;";
        using var reader = command.ExecuteReader();
        var scopes = new List<StoredScope>();
        while (reader.Read())
        {
            scopes.Add(ReadScope(reader));
        }

        return scopes;
    }

    /// <summary>
    /// 删除 scope；非空 scope 仅在 <paramref name="recursive"/> 为 true 时级联删除。<br/>
    /// Deletes a scope; a non-empty scope is cascaded only when <paramref name="recursive"/> is true.
    /// </summary>
    /// <returns>删除的 record 数量。 / Number of deleted records.</returns>
    public long DeleteScope(string name, bool recursive = false)
    {
        ValidateName(name, nameof(name));
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var scope = FindScope(connection, transaction, name) ?? throw new StorageNotFoundException(StorageEntityKind.Scope, name);
        var count = CountRecords(connection, transaction, scope.Id);
        if (count != 0 && !recursive)
        {
            throw new ScopeNotEmptyException(name, count);
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM scopes WHERE id = $id;";
        command.Parameters.AddWithValue("$id", scope.Id);
        command.ExecuteNonQuery();
        transaction.Commit();
        return count;
    }

    /// <summary>
    /// upsert record。回调获得最终永久 ID，因而可正确构造 AAD。<br/>
    /// Upserts a record. The callback receives the final permanent ID and can therefore construct correct AAD.
    /// </summary>
    public StoredRecord SetRecord(
        string scopeName,
        string key,
        int presentation,
        Func<RecordIdentity, ProtectedValue> protect,
        DateTimeOffset? now = null,
        long? expectedRevision = null,
        DateTimeOffset? expectedUpdatedAt = null) => SetRecordDetailed(
            scopeName,
            key,
            presentation,
            protect,
            now,
            expectedRevision,
            expectedUpdatedAt).Record;

    /// <summary>
    /// upsert record 并返回事务内确定的 created 标志。<br/>
    /// Upserts a record and returns the created flag determined inside the transaction.
    /// </summary>
    public StoredRecordSetResult SetRecordDetailed(
        string scopeName,
        string key,
        int presentation,
        Func<RecordIdentity, ProtectedValue> protect,
        DateTimeOffset? now = null,
        long? expectedRevision = null,
        DateTimeOffset? expectedUpdatedAt = null)
    {
        ValidateName(scopeName, nameof(scopeName));
        ValidateName(key, nameof(key));
        ArgumentNullException.ThrowIfNull(protect);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var scope = FindScope(connection, transaction, scopeName) ?? throw new StorageNotFoundException(StorageEntityKind.Scope, scopeName);
        var existing = FindRecord(connection, transaction, scope.Id, scopeName, key);
        CheckConcurrency(existing, expectedRevision, expectedUpdatedAt, $"{scopeName}/{key}");
        var identity = new RecordIdentity(
            CurrentSchemaVersion,
            existing?.Identity.RecordId ?? Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            scopeName,
            key);
        var value = ValidateProtectedValue(protect(identity));
        var timestamp = NormalizeTimestamp(now);
        var revision = (existing?.Revision ?? 0) + 1;
        if (existing is null)
        {
            InsertRecord(connection, transaction, scope.Id, identity, value, presentation, revision, timestamp);
        }
        else
        {
            UpdateRecord(connection, transaction, identity.RecordId, value, presentation, revision, timestamp);
        }

        transaction.Commit();
        return new StoredRecordSetResult(
            new StoredRecord(identity, scope.Id, value, presentation, revision, existing?.CreatedAt ?? timestamp, timestamp),
            existing is null);
    }

    /// <summary>精确读取 `(scope, key)`；不存在返回 null。 / Reads exact `(scope, key)`, returning null when absent.</summary>
    public StoredRecord? GetRecord(string scopeName, string key)
    {
        ValidateName(scopeName, nameof(scopeName));
        ValidateName(key, nameof(key));
        using var connection = OpenConnection();
        var scope = FindScope(connection, transaction: null, scopeName);
        return scope is null ? null : FindRecord(connection, transaction: null, scope.Id, scopeName, key);
    }

    /// <summary>以 ordinal/BINARY key 顺序列出 scope 的加密 records。 / Lists encrypted records by ordinal/BINARY key.</summary>
    public IReadOnlyList<StoredRecord> ListRecords(string scopeName)
    {
        ValidateName(scopeName, nameof(scopeName));
        using var connection = OpenConnection();
        var scope = FindScope(connection, transaction: null, scopeName) ?? throw new StorageNotFoundException(StorageEntityKind.Scope, scopeName);
        return ReadRecords(connection, transaction: null, scope.Id, scopeName);
    }

    /// <summary>
    /// 分页列出不含密文/nonce 的 record 元数据，避免 list/search 无谓加载秘密 blob。<br/>
    /// Pages through record metadata without ciphertext/nonces, avoiding secret-blob loads for list/search.
    /// </summary>
    public IReadOnlyList<StoredRecordMetadata> ListRecordMetadata(string scopeName, int offset = 0, int limit = 1000)
    {
        ValidateName(scopeName, nameof(scopeName));
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 10_000);
        using var connection = OpenConnection();
        var scope = FindScope(connection, transaction: null, scopeName)
            ?? throw new StorageNotFoundException(StorageEntityKind.Scope, scopeName);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, key, presentation, revision, created_at, updated_at
            FROM records
            WHERE scope_id = $scopeId
            ORDER BY key COLLATE SCRAP_ORDINAL
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$scopeId", scope.Id);
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        using var reader = command.ExecuteReader();
        var records = new List<StoredRecordMetadata>();
        while (reader.Read())
        {
            records.Add(ReadRecordMetadata(reader, scope.Id, scopeName));
        }

        return records;
    }

    /// <summary>
    /// 使用 ordinal keyset 游标列出不含密文的 metadata；daemon 可请求 `limit + 1` 判断下一页。<br/>
    /// Lists ciphertext-free metadata using an ordinal keyset cursor; the daemon may request `limit + 1` to detect a next page.
    /// </summary>
    /// <param name="scopeName">精确 scope 名称。 / Exact scope name.</param>
    /// <param name="afterKey">排除该 key 及之前 key；null 从首页开始。 / Excludes this key and preceding keys; null starts at the first page.</param>
    /// <param name="limit">读取上限，允许协议最大值 1000 加一。 / Read limit, allowing the protocol maximum of 1000 plus one.</param>
    public IReadOnlyList<StoredRecordMetadata> ListRecordMetadata(string scopeName, string? afterKey, int limit = 101)
    {
        ValidateName(scopeName, nameof(scopeName));
        if (afterKey is not null)
        {
            ValidateName(afterKey, nameof(afterKey));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 1001);
        using var connection = OpenConnection();
        var scope = FindScope(connection, transaction: null, scopeName)
            ?? throw new StorageNotFoundException(StorageEntityKind.Scope, scopeName);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, key, presentation, revision, created_at, updated_at
            FROM records
            WHERE scope_id = $scopeId
              AND ($afterKey IS NULL OR key COLLATE SCRAP_ORDINAL > $afterKey COLLATE SCRAP_ORDINAL)
            ORDER BY key COLLATE SCRAP_ORDINAL
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$scopeId", scope.Id);
        command.Parameters.AddWithValue("$afterKey", (object?)afterKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var records = new List<StoredRecordMetadata>();
        while (reader.Read())
        {
            records.Add(ReadRecordMetadata(reader, scope.Id, scopeName));
        }

        return records;
    }

    /// <summary>
    /// 在一个连接的一条 SELECT snapshot 中读取 scope 的全部 metadata，且不读取密文/nonce；供 search 内存评分使用。<br/>
    /// Reads all scope metadata without ciphertext/nonces in one SELECT snapshot on one connection; intended for in-memory search scoring.
    /// </summary>
    public IReadOnlyList<StoredRecordMetadata> ListAllRecordMetadata(string scopeName)
    {
        ValidateName(scopeName, nameof(scopeName));
        using var connection = OpenConnection();
        var scope = FindScope(connection, transaction: null, scopeName)
            ?? throw new StorageNotFoundException(StorageEntityKind.Scope, scopeName);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, key, presentation, revision, created_at, updated_at
            FROM records
            WHERE scope_id = $scopeId
            ORDER BY key COLLATE SCRAP_ORDINAL;
            """;
        command.Parameters.AddWithValue("$scopeId", scope.Id);
        using var reader = command.ExecuteReader();
        var records = new List<StoredRecordMetadata>();
        while (reader.Read())
        {
            records.Add(ReadRecordMetadata(reader, scope.Id, scopeName));
        }

        return records;
    }

    /// <summary>删除精确 record；不存在时报告 not-found。 / Deletes an exact record, reporting not-found when absent.</summary>
    public void DeleteRecord(
        string scopeName,
        string key,
        long? expectedRevision = null,
        DateTimeOffset? expectedUpdatedAt = null)
    {
        ValidateName(scopeName, nameof(scopeName));
        ValidateName(key, nameof(key));
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var scope = FindScope(connection, transaction, scopeName) ?? throw new StorageNotFoundException(StorageEntityKind.Scope, scopeName);
        var record = FindRecord(connection, transaction, scope.Id, scopeName, key)
            ?? throw new StorageNotFoundException(StorageEntityKind.Record, $"{scopeName}/{key}");
        CheckConcurrency(record, expectedRevision, expectedUpdatedAt, $"{scopeName}/{key}");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM records WHERE id = $id;";
        command.Parameters.AddWithValue("$id", record.Identity.RecordId);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    /// <summary>
    /// 原子重命名 record 并以新 key 重加密；回调失败或目标冲突时没有部分更新。<br/>
    /// Atomically renames and re-encrypts a record for its new key; callback failure or target conflict causes no partial update.
    /// </summary>
    public StoredRecord RenameRecord(
        string scopeName,
        string oldKey,
        string newKey,
        Func<StoredRecord, RecordIdentity, ProtectedValue> reencrypt,
        DateTimeOffset? now = null,
        long? expectedRevision = null,
        DateTimeOffset? expectedUpdatedAt = null)
    {
        ValidateName(scopeName, nameof(scopeName));
        ValidateName(oldKey, nameof(oldKey));
        ValidateName(newKey, nameof(newKey));
        ArgumentNullException.ThrowIfNull(reencrypt);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var scope = FindScope(connection, transaction, scopeName) ?? throw new StorageNotFoundException(StorageEntityKind.Scope, scopeName);
        var source = FindRecord(connection, transaction, scope.Id, scopeName, oldKey)
            ?? throw new StorageNotFoundException(StorageEntityKind.Record, $"{scopeName}/{oldKey}");
        CheckConcurrency(source, expectedRevision, expectedUpdatedAt, $"{scopeName}/{oldKey}");
        if (string.Equals(oldKey, newKey, StringComparison.Ordinal))
        {
            transaction.Commit();
            return source;
        }

        if (FindRecord(connection, transaction, scope.Id, scopeName, newKey) is not null)
        {
            throw new StorageConflictException(StorageConflictKind.RecordExists, $"Record already exists: {scopeName}/{newKey}");
        }

        var identity = source.Identity with { Key = newKey };
        var value = ValidateProtectedValue(reencrypt(source, identity));
        var timestamp = NormalizeTimestamp(now);
        var revision = source.Revision + 1;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE records
            SET key = $key, value_ciphertext = $ciphertext, nonce = $nonce,
                crypto_version = $cryptoVersion, revision = $revision, updated_at = $updatedAt
            WHERE id = $id AND revision = $expectedRevision;
            """;
        command.Parameters.AddWithValue("$key", newKey);
        AddProtectedValueParameters(command, value);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$updatedAt", FormatTimestamp(timestamp));
        command.Parameters.AddWithValue("$id", source.Identity.RecordId);
        command.Parameters.AddWithValue("$expectedRevision", source.Revision);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new StorageConflictException(StorageConflictKind.Concurrency, $"Record changed concurrently: {scopeName}/{oldKey}");
        }

        transaction.Commit();
        return source with { Identity = identity, Value = value, Revision = revision, UpdatedAt = timestamp };
    }

    /// <summary>
    /// 提交已在事务外重加密的 record rename，并在事务中校验 revision。<br/>
    /// Commits a record rename re-encrypted outside the transaction and checks its revision inside the transaction.
    /// </summary>
    public StoredRecord RenameRecord(
        string scopeName,
        string oldKey,
        string newKey,
        ProtectedValue replacement,
        long expectedRevision,
        DateTimeOffset? now = null,
        DateTimeOffset? expectedUpdatedAt = null) => RenameRecord(
            scopeName,
            oldKey,
            newKey,
            (_, _) => replacement,
            now,
            expectedRevision,
            expectedUpdatedAt);

    /// <summary>
    /// 原子重命名 scope 并全量重加密；任一回调或 revision 检查失败会回滚全部变更。<br/>
    /// Atomically renames a scope and re-encrypts every record; any callback or revision failure rolls back the entire change.
    /// </summary>
    public ScopeRenameResult RenameScope(
        string oldName,
        string newName,
        Func<StoredRecord, RecordIdentity, ProtectedValue> reencrypt,
        DateTimeOffset? now = null)
    {
        ValidateName(oldName, nameof(oldName));
        ValidateName(newName, nameof(newName));
        ArgumentNullException.ThrowIfNull(reencrypt);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var source = FindScope(connection, transaction, oldName) ?? throw new StorageNotFoundException(StorageEntityKind.Scope, oldName);
        if (string.Equals(oldName, newName, StringComparison.Ordinal))
        {
            transaction.Commit();
            return new ScopeRenameResult(source, 0);
        }

        if (FindScope(connection, transaction, newName) is not null)
        {
            throw new StorageConflictException(StorageConflictKind.ScopeExists, $"Scope already exists: {newName}");
        }

        var records = ReadRecords(connection, transaction, source.Id, oldName);
        var replacements = new List<(StoredRecord Record, ProtectedValue Value)>(records.Count);
        foreach (var record in records)
        {
            replacements.Add((record, ValidateProtectedValue(reencrypt(record, record.Identity with { ScopeName = newName }))));
        }

        var timestamp = NormalizeTimestamp(now);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE scopes SET name = $name, updated_at = $updatedAt WHERE id = $id;";
            command.Parameters.AddWithValue("$name", newName);
            command.Parameters.AddWithValue("$updatedAt", FormatTimestamp(timestamp));
            command.Parameters.AddWithValue("$id", source.Id);
            command.ExecuteNonQuery();
        }

        foreach (var replacement in replacements)
        {
            UpdateRecordValue(connection, transaction, replacement.Record, replacement.Value, timestamp);
        }

        transaction.Commit();
        return new ScopeRenameResult(new StoredScope(source.Id, newName, source.CreatedAt, timestamp), replacements.Count);
    }

    /// <summary>
    /// 提交事务外准备的全量 scope 重加密结果；集合必须与事务内 snapshot 的 ID/revision 精确一致。<br/>
    /// Commits a full scope re-encryption prepared outside the transaction; the set must exactly match IDs/revisions in the transactional snapshot.
    /// </summary>
    public ScopeRenameResult RenameScope(
        string oldName,
        string newName,
        IReadOnlyCollection<RecordReencryption> replacements,
        DateTimeOffset? now = null)
    {
        ValidateName(oldName, nameof(oldName));
        ValidateName(newName, nameof(newName));
        ArgumentNullException.ThrowIfNull(replacements);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var source = FindScope(connection, transaction, oldName)
            ?? throw new StorageNotFoundException(StorageEntityKind.Scope, oldName);
        if (string.Equals(oldName, newName, StringComparison.Ordinal))
        {
            transaction.Commit();
            return new ScopeRenameResult(source, 0);
        }

        if (FindScope(connection, transaction, newName) is not null)
        {
            throw new StorageConflictException(StorageConflictKind.ScopeExists, $"Scope already exists: {newName}");
        }

        var current = ReadRecords(connection, transaction, source.Id, oldName);
        Dictionary<string, RecordReencryption> byId;
        try
        {
            byId = replacements.ToDictionary(replacement => replacement.RecordId, StringComparer.Ordinal);
        }
        catch (ArgumentException exception)
        {
            throw new StorageConflictException(StorageConflictKind.Concurrency, "Scope rename replacements contain duplicate record IDs.", exception);
        }

        if (byId.Count != current.Count || current.Any(record =>
                !byId.TryGetValue(record.Identity.RecordId, out var item) || item.ExpectedRevision != record.Revision))
        {
            throw new StorageConflictException(StorageConflictKind.Concurrency, "Scope contents changed while rename ciphertext was prepared.");
        }

        var timestamp = NormalizeTimestamp(now);
        using (var rename = connection.CreateCommand())
        {
            rename.Transaction = transaction;
            rename.CommandText = "UPDATE scopes SET name = $name, updated_at = $updatedAt WHERE id = $id;";
            rename.Parameters.AddWithValue("$name", newName);
            rename.Parameters.AddWithValue("$updatedAt", FormatTimestamp(timestamp));
            rename.Parameters.AddWithValue("$id", source.Id);
            rename.ExecuteNonQuery();
        }

        foreach (var record in current)
        {
            UpdateRecordValue(connection, transaction, record, ValidateProtectedValue(byId[record.Identity.RecordId].Value), timestamp);
        }

        transaction.Commit();
        return new ScopeRenameResult(new StoredScope(source.Id, newName, source.CreatedAt, timestamp), current.Count);
    }

    /// <summary>
    /// 打开单次操作连接。foreign_keys、synchronous 与 busy_timeout 为连接级设置，故每次应用。<br/>
    /// Opens one operation connection. foreign_keys, synchronous, and busy_timeout are connection-scoped and applied each time.
    /// </summary>
    private SqliteConnection OpenConnection(bool requireInitialization = true)
    {
        if (requireInitialization && !initialized)
        {
            throw new InvalidOperationException("The SQLite store must be initialized before use.");
        }

        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            connection.CreateCollation(OrdinalCollation, static (left, right) => string.CompareOrdinal(left, right));
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA foreign_keys = ON; PRAGMA synchronous = FULL; PRAGMA busy_timeout = {busyTimeoutMilliseconds};";
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>设置持久化 WAL 并验证 SQLite 接受配置。 / Sets persistent WAL and verifies SQLite accepted it.</summary>
    private static void ConfigureJournal(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = WAL;";
        var mode = Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
        {
            throw new StorageMigrationException($"SQLite refused WAL journal mode and returned '{mode}'.");
        }
    }

    /// <summary>
    /// 从空库原子迁移至 v1；未知或残缺 schema 不会被重建掩盖。<br/>
    /// Atomically migrates an empty database to v1; unknown or partial schemas are never hidden by rebuilding them.
    /// </summary>
    private static void Migrate(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction(deferred: false);
        if (!TableExists(connection, transaction, "meta"))
        {
            if (HasUserTables(connection, transaction))
            {
                throw new StorageMigrationException("Database has user tables but no meta schema version; refusing to overwrite it.");
            }

            CreateVersionOne(connection, transaction);
            transaction.Commit();
            return;
        }

        var version = ReadSchemaVersion(connection, transaction);
        if (version > CurrentSchemaVersion)
        {
            throw new StorageMigrationException($"Database schema version {version} is newer than supported version {CurrentSchemaVersion}.");
        }

        if (version != CurrentSchemaVersion)
        {
            throw new StorageMigrationException($"No migration path exists from schema version {version} to {CurrentSchemaVersion}.");
        }

        EnsureVersionOneTables(connection, transaction);
        transaction.Commit();
    }

    /// <summary>创建设计 schema 及不改变领域身份的并发/crypto 元数据列。 / Creates the designed schema plus concurrency/crypto metadata columns.</summary>
    private static void CreateVersionOne(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE meta (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE scopes (
                id         INTEGER PRIMARY KEY,
                name       TEXT NOT NULL COLLATE BINARY UNIQUE,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE records (
                id               TEXT PRIMARY KEY,
                scope_id         INTEGER NOT NULL REFERENCES scopes(id) ON DELETE CASCADE,
                key              TEXT NOT NULL COLLATE BINARY,
                value_ciphertext BLOB NOT NULL,
                nonce            BLOB NOT NULL,
                crypto_version   INTEGER NOT NULL,
                presentation     INTEGER NOT NULL,
                revision         INTEGER NOT NULL,
                created_at       TEXT NOT NULL,
                updated_at       TEXT NOT NULL,
                UNIQUE(scope_id, key)
            );
            CREATE INDEX records_by_scope ON records(scope_id);
            INSERT INTO meta(key, value) VALUES ('schema_version', '1');
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>验证标记 v1 的库具有契约表与列。 / Verifies a database marked v1 has its contract tables and columns.</summary>
    private static void EnsureVersionOneTables(SqliteConnection connection, SqliteTransaction transaction)
    {
        foreach (var table in new[] { "meta", "scopes", "records" })
        {
            if (!TableExists(connection, transaction, table))
            {
                throw new StorageMigrationException($"Schema version 1 is missing required table '{table}'.");
            }
        }

        foreach (var column in new[] { "crypto_version", "revision" })
        {
            if (!ColumnExists(connection, transaction, "records", column))
            {
                throw new StorageMigrationException($"Schema version 1 is missing required records column '{column}'.");
            }
        }
    }

    private static bool TableExists(SqliteConnection connection, SqliteTransaction transaction, string tableName)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = $name);";
        command.Parameters.AddWithValue("$name", tableName);
        return (long)(command.ExecuteScalar() ?? 0L) != 0;
    }

    private static bool ColumnExists(SqliteConnection connection, SqliteTransaction transaction, string tableName, string columnName)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info({tableName});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasUserTables(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name NOT LIKE 'sqlite_%');";
        return (long)(command.ExecuteScalar() ?? 0L) != 0;
    }

    private static int ReadSchemaVersion(SqliteConnection connection, SqliteTransaction? transaction)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT value FROM meta WHERE key = $key;";
            command.Parameters.AddWithValue("$key", SchemaVersionKey);
            var raw = command.ExecuteScalar() as string;
            if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version < 1)
            {
                throw new StorageMigrationException("The meta table contains no valid schema_version.");
            }

            return version;
        }
        catch (SqliteException exception)
        {
            throw new StorageMigrationException("Unable to read schema_version from the meta table.", exception);
        }
    }

    private static StoredScope? FindScope(SqliteConnection connection, SqliteTransaction? transaction, string name)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, name, created_at, updated_at FROM scopes WHERE name = $name COLLATE BINARY;";
        command.Parameters.AddWithValue("$name", name);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadScope(reader) : null;
    }

    private static StoredScope ReadScope(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        ParseTimestamp(reader.GetString(2)),
        ParseTimestamp(reader.GetString(3)));

    private static long CountRecords(SqliteConnection connection, SqliteTransaction transaction, long scopeId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM records WHERE scope_id = $scopeId;";
        command.Parameters.AddWithValue("$scopeId", scopeId);
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    private static StoredRecord? FindRecord(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long scopeId,
        string scopeName,
        string key)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, key, value_ciphertext, nonce, crypto_version, presentation, revision, created_at, updated_at
            FROM records
            WHERE scope_id = $scopeId AND key = $key COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$scopeId", scopeId);
        command.Parameters.AddWithValue("$key", key);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRecord(reader, scopeId, scopeName) : null;
    }

    private static List<StoredRecord> ReadRecords(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long scopeId,
        string scopeName)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, key, value_ciphertext, nonce, crypto_version, presentation, revision, created_at, updated_at
            FROM records
            WHERE scope_id = $scopeId
            ORDER BY key COLLATE SCRAP_ORDINAL;
            """;
        command.Parameters.AddWithValue("$scopeId", scopeId);
        using var reader = command.ExecuteReader();
        var records = new List<StoredRecord>();
        while (reader.Read())
        {
            records.Add(ReadRecord(reader, scopeId, scopeName));
        }

        return records;
    }

    private static StoredRecord ReadRecord(SqliteDataReader reader, long scopeId, string scopeName)
    {
        var identity = new RecordIdentity(CurrentSchemaVersion, reader.GetString(0), scopeName, reader.GetString(1));
        var value = new ProtectedValue((byte[])reader[2], (byte[])reader[3], reader.GetInt32(4));
        return new StoredRecord(
            identity,
            scopeId,
            value,
            reader.GetInt32(5),
            reader.GetInt64(6),
            ParseTimestamp(reader.GetString(7)),
            ParseTimestamp(reader.GetString(8)));
    }

    private static StoredRecordMetadata ReadRecordMetadata(SqliteDataReader reader, long scopeId, string scopeName) => new(
        new RecordIdentity(CurrentSchemaVersion, reader.GetString(0), scopeName, reader.GetString(1)),
        scopeId,
        reader.GetInt32(2),
        reader.GetInt64(3),
        ParseTimestamp(reader.GetString(4)),
        ParseTimestamp(reader.GetString(5)));

    private static void InsertRecord(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long scopeId,
        RecordIdentity identity,
        ProtectedValue value,
        int presentation,
        long revision,
        DateTimeOffset timestamp)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO records(
                id, scope_id, key, value_ciphertext, nonce, crypto_version,
                presentation, revision, created_at, updated_at)
            VALUES (
                $id, $scopeId, $key, $ciphertext, $nonce, $cryptoVersion,
                $presentation, $revision, $createdAt, $updatedAt);
            """;
        command.Parameters.AddWithValue("$id", identity.RecordId);
        command.Parameters.AddWithValue("$scopeId", scopeId);
        command.Parameters.AddWithValue("$key", identity.Key);
        AddProtectedValueParameters(command, value);
        command.Parameters.AddWithValue("$presentation", presentation);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$createdAt", FormatTimestamp(timestamp));
        command.Parameters.AddWithValue("$updatedAt", FormatTimestamp(timestamp));
        command.ExecuteNonQuery();
    }

    private static void UpdateRecord(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string recordId,
        ProtectedValue value,
        int presentation,
        long revision,
        DateTimeOffset timestamp)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE records
            SET value_ciphertext = $ciphertext, nonce = $nonce, crypto_version = $cryptoVersion,
                presentation = $presentation, revision = $revision, updated_at = $updatedAt
            WHERE id = $id;
            """;
        AddProtectedValueParameters(command, value);
        command.Parameters.AddWithValue("$presentation", presentation);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$updatedAt", FormatTimestamp(timestamp));
        command.Parameters.AddWithValue("$id", recordId);
        command.ExecuteNonQuery();
    }

    private static void UpdateRecordValue(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredRecord record,
        ProtectedValue value,
        DateTimeOffset timestamp)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE records
            SET value_ciphertext = $ciphertext, nonce = $nonce, crypto_version = $cryptoVersion,
                revision = $revision, updated_at = $updatedAt
            WHERE id = $id AND revision = $expectedRevision;
            """;
        AddProtectedValueParameters(command, value);
        command.Parameters.AddWithValue("$revision", record.Revision + 1);
        command.Parameters.AddWithValue("$updatedAt", FormatTimestamp(timestamp));
        command.Parameters.AddWithValue("$id", record.Identity.RecordId);
        command.Parameters.AddWithValue("$expectedRevision", record.Revision);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new StorageConflictException(StorageConflictKind.Concurrency, $"Record changed concurrently: {record.Identity.ScopeName}/{record.Identity.Key}");
        }
    }

    private static void AddProtectedValueParameters(SqliteCommand command, ProtectedValue value)
    {
        command.Parameters.Add("$ciphertext", SqliteType.Blob).Value = value.Ciphertext.ToArray();
        command.Parameters.Add("$nonce", SqliteType.Blob).Value = value.Nonce.ToArray();
        command.Parameters.AddWithValue("$cryptoVersion", value.CryptoVersion);
    }

    /// <summary>
    /// 在 mutation 的 IMMEDIATE 事务中检查 revision/timestamp 条件，避免 daemon 先查后写的 TOCTOU。<br/>
    /// Checks revision/timestamp conditions inside the mutation's IMMEDIATE transaction, avoiding a daemon read-then-write TOCTOU.
    /// </summary>
    private static void CheckConcurrency(
        StoredRecord? record,
        long? expectedRevision,
        DateTimeOffset? expectedUpdatedAt,
        string identity)
    {
        if (expectedRevision is not null && (record is null || record.Revision != expectedRevision.Value))
        {
            throw new StorageConflictException(StorageConflictKind.Concurrency, $"Record revision does not match: {identity}");
        }

        if (expectedUpdatedAt is not null &&
            (record is null || record.UpdatedAt != expectedUpdatedAt.Value.ToUniversalTime()))
        {
            throw new StorageConflictException(StorageConflictKind.Concurrency, $"Record updated_at does not match: {identity}");
        }
    }

    private static ProtectedValue ValidateProtectedValue(ProtectedValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Ciphertext.IsEmpty)
        {
            throw new ArgumentException("Ciphertext must not be empty.", nameof(value));
        }

        if (value.Nonce.IsEmpty)
        {
            throw new ArgumentException("Nonce must not be empty.", nameof(value));
        }

        if (value.CryptoVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "CryptoVersion must be positive.");
        }

        return value;
    }

    private static void ValidateName(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length == 0)
        {
            throw new ArgumentException("Names must not be empty and are never implicitly trimmed.", parameterName);
        }
    }

    private static bool IsConstraintViolation(SqliteException exception) => exception.SqliteErrorCode == 19;

    private static int ReadPragmaInt32(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {name};";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string ReadPragmaString(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {name};";
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static DateTimeOffset NormalizeTimestamp(DateTimeOffset? value) => (value ?? DateTimeOffset.UtcNow).ToUniversalTime();

    private static string FormatTimestamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) => DateTimeOffset.ParseExact(
        value,
        "O",
        CultureInfo.InvariantCulture,
        DateTimeStyles.RoundtripKind);
}

