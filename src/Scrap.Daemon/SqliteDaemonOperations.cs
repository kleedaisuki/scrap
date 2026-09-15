using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using Scrap.Crypto;
using Scrap.Domain;
using Scrap.Platform.Paths;
using Scrap.Platform.Secrets;
using Scrap.Protocol;
using Scrap.Storage.Sqlite;
using DomainCaseSensitivity = Scrap.Domain.CaseSensitivity;
using DomainPresentation = Scrap.Domain.Presentation;
using DomainRecord = Scrap.Domain.Record;
using DomainSearchMode = Scrap.Domain.SearchMode;
using DomainSearchRequest = Scrap.Domain.SearchRequest;
using ProtocolPresentation = Scrap.Protocol.RecordPresentation;
using ProtocolScopeRenameResult = Scrap.Protocol.ScopeRenameResult;
using ProtocolSearchRequest = Scrap.Protocol.SearchRequest;

namespace Scrap.Daemon;

/// <summary>
/// 将 Domain 校验、AEAD 与唯一 SQLite owner 组合为 daemon 用例；不把明文交给 storage。
/// / Composes Domain validation, AEAD, and the sole SQLite owner into daemon use cases without giving plaintext to storage.
/// </summary>
internal sealed class SqliteDaemonOperations : IDaemonOperations, IDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly SqliteStore store;
    private readonly IMasterKeyProvider masterKeyProvider;
    private readonly ScrapPathLayout paths;
    private readonly DaemonInitializationState initializationState;
    private readonly TimeProvider timeProvider;
    private IRecordEncryptor? encryptor;
    private int initializationStarted;

    /// <summary>
    /// 初始化 operations；实际 I/O 由 <see cref="InitializeAsync"/> 完成。 / Initializes operations; <see cref="InitializeAsync"/> performs actual I/O.
    /// </summary>
    public SqliteDaemonOperations(
        SqliteStore store,
        IMasterKeyProvider masterKeyProvider,
        ScrapPathLayout paths,
        DaemonInitializationState initializationState,
        TimeProvider timeProvider)
    {
        this.store = store;
        this.masterKeyProvider = masterKeyProvider;
        this.paths = paths;
        this.initializationState = initializationState;
        this.timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref initializationStarted, 1) != 0)
        {
            throw new InvalidOperationException("Daemon operations initialization may run only once.");
        }

        bool existingStore = File.Exists(paths.DatabaseFile)
            || File.Exists($"{paths.DatabaseFile}-wal")
            || File.Exists($"{paths.DatabaseFile}-shm");
        IRecordEncryptor created = await MasterKeyManager.CreateEncryptorAsync(
            masterKeyProvider,
            existingStore ? MasterKeyAccess.OpenExisting : MasterKeyAccess.InitializeNew,
            cancellationToken).ConfigureAwait(false);

        try
        {
            store.Initialize();
            EnsureStoreFilesPrivate();
            Volatile.Write(ref encryptor, created);
        }
        catch
        {
            created.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public Task<ScopeListResult> ListScopesAsync(ScopeListParams parameters, CancellationToken cancellationToken)
    {
        EnsureReady(cancellationToken);
        if (parameters.AfterName is not null)
        {
            _ = ScopeName.Create(parameters.AfterName);
        }

        StoredScope[] page = store.ListScopes(parameters.AfterName, parameters.Limit + 1).ToArray();
        bool hasMore = page.Length > parameters.Limit;
        StoredScope[] selected = hasMore ? page[..parameters.Limit] : page;
        string? next = hasMore ? selected[^1].Name : null;
        return Task.FromResult(new ScopeListResult(selected.Select(ToScopeDto).ToArray(), next));
    }

    /// <inheritdoc />
    public Task<ScopeCreateResult> CreateScopeAsync(ScopeCreateParams parameters, CancellationToken cancellationToken)
    {
        EnsureReady(cancellationToken);
        DateTimeOffset now = timeProvider.GetUtcNow();
        Scope scope = Scope.TryCreate(parameters.Name, now).Value;
        StoredScope stored = store.CreateScope(scope.Name.Value, now);
        return Task.FromResult(new ScopeCreateResult(ToScopeDto(stored)));
    }

    /// <inheritdoc />
    public Task<ProtocolScopeRenameResult> RenameScopeAsync(ScopeRenameParams parameters, CancellationToken cancellationToken)
    {
        EnsureReady(cancellationToken);
        _ = ScopeName.Create(parameters.OldName);
        _ = ScopeName.Create(parameters.NewName);
        StoredScope source = store.GetScope(parameters.OldName)
            ?? throw new StorageNotFoundException(StorageEntityKind.Scope, parameters.OldName);
        Scope restored = RestoreScope(source);
        _ = restored.TryRename(parameters.NewName, timeProvider.GetUtcNow()).Value;
        if (string.Equals(parameters.OldName, parameters.NewName, StringComparison.Ordinal))
        {
            return Task.FromResult(new ProtocolScopeRenameResult(ToScopeDto(source)));
        }

        Scrap.Storage.Sqlite.ScopeRenameResult renamed = store.RenameScope(
            parameters.OldName,
            parameters.NewName,
            (record, target) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Reencrypt(record, target);
            },
            timeProvider.GetUtcNow());
        return Task.FromResult(new ProtocolScopeRenameResult(ToScopeDto(renamed.Scope)));
    }

    /// <inheritdoc />
    public Task<ScopeDeleteResult> DeleteScopeAsync(ScopeDeleteParams parameters, CancellationToken cancellationToken)
    {
        EnsureReady(cancellationToken);
        _ = ScopeName.Create(parameters.Name);
        long deleted = store.DeleteScope(
            parameters.Name,
            parameters.Recursive,
            parameters.ExpectedRecordCount);
        return Task.FromResult(new ScopeDeleteResult(checked((int)deleted)));
    }

    /// <inheritdoc />
    public Task<RecordGetResult> GetRecordAsync(RecordGetParams parameters, CancellationToken cancellationToken)
    {
        EnsureReady(cancellationToken);
        _ = ScopeName.Create(parameters.Scope);
        _ = RecordKey.Create(parameters.Key);
        StoredRecord stored = store.GetRecord(parameters.Scope, parameters.Key)
            ?? throw new StorageNotFoundException(StorageEntityKind.Record, $"{parameters.Scope}/{parameters.Key}");
        return Task.FromResult(new RecordGetResult(ToRecordDto(stored)));
    }

    /// <inheritdoc />
    public Task<RecordSetResult> SetRecordAsync(RecordSetParams parameters, CancellationToken cancellationToken)
    {
        EnsureReady(cancellationToken);
        DateTimeOffset now = timeProvider.GetUtcNow();
        DomainRecord record = DomainRecord.TryCreate(
            parameters.Scope,
            parameters.Key,
            parameters.Value,
            ToDomain(parameters.Presentation),
            now).Value;

        RecordSetPreparation preparation = store.PrepareSetRecord(
            record.Scope.Value,
            record.Key.Value,
            now);
        ProtectedValue protectedValue = Encrypt(record.Value.Value, preparation.Identity);
        StoredRecordSetResult written = store.CommitPreparedRecord(
            preparation,
            protectedValue,
            (int)record.Presentation,
            now,
            parameters.ExpectedRevision);
        return Task.FromResult(new RecordSetResult(ToSummary(written.Record), written.Created));
    }

    /// <inheritdoc />
    public Task<RecordRenameResult> RenameRecordAsync(RecordRenameParams parameters, CancellationToken cancellationToken)
    {
        EnsureReady(cancellationToken);
        _ = ScopeName.Create(parameters.Scope);
        _ = RecordKey.Create(parameters.Key);
        _ = RecordKey.Create(parameters.NewKey);
        StoredRecord source = store.GetRecord(parameters.Scope, parameters.Key)
            ?? throw new StorageNotFoundException(StorageEntityKind.Record, $"{parameters.Scope}/{parameters.Key}");
        if (parameters.ExpectedRevision is not null && parameters.ExpectedRevision != source.Revision)
        {
            throw new StorageConflictException(StorageConflictKind.Concurrency, "Record revision did not match.");
        }

        byte[] plaintext = Decrypt(source);
        try
        {
            string value = StrictUtf8.GetString(plaintext);
            DomainRecord record = RestoreRecord(
                parameters.Scope,
                parameters.Key,
                value,
                ToDomain(source.Presentation),
                source.CreatedAt,
                source.UpdatedAt);
            _ = record.TryReidentify(parameters.Scope, parameters.NewKey, timeProvider.GetUtcNow()).Value;

            RecordIdentity target = source.Identity with { Key = parameters.NewKey };
            ProtectedValue replacement = Encrypt(plaintext, target);
            StoredRecord renamed = store.RenameRecord(
                parameters.Scope,
                parameters.Key,
                parameters.NewKey,
                replacement,
                source.Revision,
                timeProvider.GetUtcNow());
            return Task.FromResult(new RecordRenameResult(ToSummary(renamed)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <inheritdoc />
    public Task<RecordDeleteResult> DeleteRecordAsync(RecordDeleteParams parameters, CancellationToken cancellationToken)
    {
        EnsureReady(cancellationToken);
        _ = ScopeName.Create(parameters.Scope);
        _ = RecordKey.Create(parameters.Key);
        store.DeleteRecord(parameters.Scope, parameters.Key, parameters.ExpectedRevision);
        return Task.FromResult(new RecordDeleteResult());
    }

    /// <inheritdoc />
    public Task<RecordListResult> ListRecordsAsync(RecordListParams parameters, CancellationToken cancellationToken)
    {
        EnsureReady(cancellationToken);
        _ = ScopeName.Create(parameters.Scope);
        if (parameters.AfterKey is not null)
        {
            _ = RecordKey.Create(parameters.AfterKey);
        }

        if (parameters.Limit is < 1 or > ProtocolConstants.MaxRecordListPageSize)
        {
            throw new DomainException(new DomainError(
                DomainErrorCode.OutOfRange,
                $"limit must be between 1 and {ProtocolConstants.MaxRecordListPageSize}.",
                "limit"));
        }

        StoredRecordMetadata[] page = store.ListRecordMetadata(
            parameters.Scope,
            parameters.AfterKey,
            parameters.Limit + 1).ToArray();
        bool hasMore = page.Length > parameters.Limit;
        StoredRecordMetadata[] selected = hasMore ? page[..parameters.Limit] : page;
        string? next = hasMore ? selected[^1].Identity.Key : null;
        return Task.FromResult(new RecordListResult(selected.Select(ToSummary).ToArray(), next));
    }

    /// <inheritdoc />
    public Task<RecordSearchResult> SearchRecordsAsync(ProtocolSearchRequest parameters, CancellationToken cancellationToken)
    {
        EnsureReady(cancellationToken);
        DomainSearchRequest request = DomainSearchRequest.TryCreate(
            parameters.Scopes,
            parameters.Query,
            (DomainSearchMode)parameters.Mode,
            (DomainCaseSensitivity)parameters.CaseSensitivity,
            parameters.Limit).Value;
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<StoredRecordMetadata> metadata = store.ListAllRecordMetadata(
            request.Scopes.Select(scope => scope.Value).ToArray());
        IReadOnlyList<SearchMatch> matches = RecordSearch.Search(
            metadata.Select(item => new SearchCandidate(
                ScopeName.Create(item.Identity.ScopeName),
                RecordKey.Create(item.Identity.Key))),
            request,
            cancellationToken).Value;
        cancellationToken.ThrowIfCancellationRequested();
        var byIdentity = metadata.ToDictionary(
            item => (item.Identity.ScopeName, item.Identity.Key));
        RecordSummaryDto[] results = matches
            .Select(match => ToSummary(byIdentity[(
                match.Candidate.Scope.Value,
                match.Candidate.Key.Value)]))
            .ToArray();
        return Task.FromResult(new RecordSearchResult(results));
    }

    /// <summary>释放并清零 encryptor 持有的主密钥副本。 / Disposes and zeros the encryptor's master-key copy.</summary>
    public void Dispose() => Interlocked.Exchange(ref encryptor, null)?.Dispose();

    private RecordDto ToRecordDto(StoredRecord stored)
    {
        byte[] plaintext = Decrypt(stored);
        try
        {
            string value = StrictUtf8.GetString(plaintext);
            DomainRecord record = RestoreRecord(
                stored.Identity.ScopeName,
                stored.Identity.Key,
                value,
                ToDomain(stored.Presentation),
                stored.CreatedAt,
                stored.UpdatedAt);
            return new(
                record.Scope.Value,
                record.Key.Value,
                record.Value.Value,
                ToProtocol(record.Presentation),
                record.CreatedAt,
                record.UpdatedAt,
                stored.Revision);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private ProtectedValue Reencrypt(StoredRecord source, RecordIdentity target)
    {
        byte[] plaintext = Decrypt(source);
        try
        {
            return Encrypt(plaintext, target);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private ProtectedValue Encrypt(string value, RecordIdentity identity)
    {
        byte[] plaintext = StrictUtf8.GetBytes(value);
        try
        {
            return Encrypt(plaintext, identity);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private ProtectedValue Encrypt(ReadOnlySpan<byte> plaintext, RecordIdentity identity)
    {
        EncryptedRecordValue encrypted = GetEncryptor().Encrypt(plaintext, ToContext(identity));
        return new(encrypted.Ciphertext, encrypted.Nonce);
    }

    private byte[] Decrypt(StoredRecord record) => GetEncryptor().Decrypt(
        record.Value.Ciphertext.Span,
        record.Value.Nonce.Span,
        ToContext(record.Identity));

    private IRecordEncryptor GetEncryptor() => Volatile.Read(ref encryptor)
        ?? throw new DaemonInitializationException(ProtocolErrorCodes.KeyUnavailable, "The record encryptor is unavailable.");

    private void EnsureReady(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        initializationState.EnsureReady();
    }

    private void EnsureStoreFilesPrivate()
    {
        string[] files = [
            paths.DatabaseFile,
            $"{paths.DatabaseFile}-wal",
            $"{paths.DatabaseFile}-shm",
        ];
        foreach (string file in files)
        {
            if (File.Exists(file))
            {
                PlatformPathPermissions.EnsurePrivateFile(file);
            }
        }
    }

    private static ScopeDto ToScopeDto(StoredScope scope)
    {
        Scope restored = RestoreScope(scope);
        return new(restored.Name.Value);
    }

    private static Scope RestoreScope(StoredScope scope)
    {
        DomainResult<Scope> restored = Scope.TryRestore(scope.Name, scope.CreatedAt, scope.UpdatedAt);
        return restored.IsSuccess
            ? restored.Value
            : throw new StorageMigrationException("Stored scope metadata is invalid.");
    }

    private static DomainRecord RestoreRecord(
        string scope,
        string key,
        string value,
        DomainPresentation presentation,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        DomainResult<DomainRecord> restored = DomainRecord.TryRestore(
            scope,
            key,
            value,
            presentation,
            createdAt,
            updatedAt);
        return restored.IsSuccess
            ? restored.Value
            : throw new EncryptedRecordFormatException("Stored record metadata or plaintext encoding is invalid.");
    }

    private static RecordSummaryDto ToSummary(StoredRecord record) => new(
        record.Identity.ScopeName,
        record.Identity.Key,
        ToProtocol(ToDomain(record.Presentation)),
        record.CreatedAt,
        record.UpdatedAt,
        record.Revision);

    private static RecordSummaryDto ToSummary(StoredRecordMetadata record) => new(
        record.Identity.ScopeName,
        record.Identity.Key,
        ToProtocol(ToDomain(record.Presentation)),
        record.CreatedAt,
        record.UpdatedAt,
        record.Revision);

    private static RecordEncryptionContext ToContext(RecordIdentity identity) => new(
        identity.SchemaVersion,
        identity.RecordId,
        identity.ScopeName,
        identity.Key);

    private static DomainPresentation ToDomain(ProtocolPresentation presentation) => presentation switch
    {
        ProtocolPresentation.Masked => DomainPresentation.Masked,
        ProtocolPresentation.Plain => DomainPresentation.Plain,
        _ => throw new DomainException(new DomainError(
            DomainErrorCode.InvalidOption,
            "presentation must be masked or plain.",
            "presentation")),
    };

    private static DomainPresentation ToDomain(int presentation) => presentation switch
    {
        (int)DomainPresentation.Masked => DomainPresentation.Masked,
        (int)DomainPresentation.Plain => DomainPresentation.Plain,
        _ => throw new EncryptedRecordFormatException("Stored presentation metadata is invalid."),
    };

    private static ProtocolPresentation ToProtocol(DomainPresentation presentation) => presentation switch
    {
        DomainPresentation.Masked => ProtocolPresentation.Masked,
        DomainPresentation.Plain => ProtocolPresentation.Plain,
        _ => throw new UnreachableException(),
    };
}
