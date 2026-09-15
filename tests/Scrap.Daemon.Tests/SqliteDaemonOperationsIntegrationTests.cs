using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Scrap.Platform.Paths;
using Scrap.Platform.Secrets;
using Scrap.Protocol;
using Scrap.Storage.Sqlite;
using ProtocolCaseSensitivity = Scrap.Protocol.CaseSensitivity;
using ProtocolScopeRenameResult = Scrap.Protocol.ScopeRenameResult;
using ProtocolSearchMode = Scrap.Protocol.SearchMode;
using ProtocolSearchRequest = Scrap.Protocol.SearchRequest;

namespace Scrap.Daemon.Tests;

/// <summary>
/// 通过真实 SQLite、主密钥管理与 AEAD 路径验证 daemon operations 的端到端契约。
/// / Verifies daemon-operation contracts end to end through real SQLite, master-key management, and AEAD.
/// </summary>
public sealed class SqliteDaemonOperationsIntegrationTests
{
    private static readonly string[] CaseSensitiveScopeOrder = ["Vault", "vault"];
    private static readonly string[] FirstKeysetPage = ["A", "a"];

    /// <summary>UTF-8 的 CJK、emoji 与多行文本能够完整 CRUD。 / UTF-8 CJK, emoji, and multiline text survive complete CRUD.</summary>
    [Fact]
    public async Task UnicodeAndMultilineValuesRoundTripAcrossCrudAsync()
    {
        using var context = await OperationsContext.CreateAsync();
        const string scope = "研发 🧪";
        const string key = "发布说明 🚀";
        const string initial = "第一行：你好，世界 🌏\nsecond line\r\n终章 🦄";
        const string replacement = "更新后 🔐\n保留换行\n끝";

        ScopeCreateResult createdScope = await context.Operations.CreateScopeAsync(new(scope), default);
        RecordSetResult created = await context.Operations.SetRecordAsync(
            new(scope, key, initial, RecordPresentation.Plain),
            default);
        RecordDto first = (await context.Operations.GetRecordAsync(new(scope, key), default)).Record;
        RecordSetResult updated = await context.Operations.SetRecordAsync(
            new(scope, key, replacement, RecordPresentation.Masked, created.Record.Revision),
            default);
        RecordDto second = (await context.Operations.GetRecordAsync(new(scope, key), default)).Record;

        Assert.Equal(scope, createdScope.Scope.Name);
        Assert.True(created.Created);
        Assert.Equal(initial, first.Value);
        Assert.Equal(RecordPresentation.Plain, first.Presentation);
        Assert.False(updated.Created);
        Assert.Equal(created.Record.Revision + 1, updated.Record.Revision);
        Assert.Equal(replacement, second.Value);
        Assert.Equal(RecordPresentation.Masked, second.Presentation);

        await context.Operations.DeleteRecordAsync(
            new(scope, key, updated.Record.Revision),
            default);
        await Assert.ThrowsAsync<StorageNotFoundException>(
            () => context.Operations.GetRecordAsync(new(scope, key), default));
    }

    /// <summary>scope 与 key 的身份均使用 ordinal 大小写敏感语义。 / Scope and key identities both use ordinal case-sensitive semantics.</summary>
    [Fact]
    public async Task ScopeAndRecordIdentityAreCaseSensitiveAsync()
    {
        using var context = await OperationsContext.CreateAsync();
        await context.Operations.CreateScopeAsync(new("Vault"), default);
        await context.Operations.CreateScopeAsync(new("vault"), default);
        await context.Operations.SetRecordAsync(new("Vault", "Token", "upper key"), default);
        await context.Operations.SetRecordAsync(new("Vault", "token", "lower key"), default);
        await context.Operations.SetRecordAsync(new("vault", "Token", "lower scope"), default);

        ScopeListResult scopes = await context.Operations.ListScopesAsync(new(), default);

        Assert.Equal(CaseSensitiveScopeOrder, scopes.Scopes.Select(item => item.Name));
        Assert.Equal("upper key", (await context.Operations.GetRecordAsync(new("Vault", "Token"), default)).Record.Value);
        Assert.Equal("lower key", (await context.Operations.GetRecordAsync(new("Vault", "token"), default)).Record.Value);
        Assert.Equal("lower scope", (await context.Operations.GetRecordAsync(new("vault", "Token"), default)).Record.Value);
    }

    /// <summary>
    /// 超过默认页大小的 Unicode scopes 使用 .NET ordinal cursor 无重复分页。
    /// / More than one default page of Unicode scopes is paged without duplicates using a .NET ordinal cursor.
    /// </summary>
    [Fact]
    public async Task ScopeListUsesOrdinalUnicodeKeysetPaginationAsync()
    {
        using var context = await OperationsContext.CreateAsync();
        string[] names = Enumerable.Range(0, 105)
            .Select(index => $"scope-{index:D3}")
            .Append("scope-\U00010000")
            .Append("scope-\uE000")
            .Order(StringComparer.Ordinal)
            .ToArray();
        foreach (string name in names)
        {
            await context.Operations.CreateScopeAsync(new(name), default);
        }

        ScopeListResult first = await context.Operations.ListScopesAsync(new(), default);
        ScopeListResult second = await context.Operations.ListScopesAsync(
            new(first.NextCursor),
            default);
        string[] actual = first.Scopes.Concat(second.Scopes).Select(scope => scope.Name).ToArray();

        Assert.Equal(100, first.Scopes.Count);
        Assert.Equal("scope-099", first.NextCursor);
        Assert.Null(second.NextCursor);
        Assert.Equal(names, actual);
        Assert.Equal(actual.Length, actual.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// 重复 set 必须生成新 nonce，SQLite 文件不得出现 value 明文。
    /// / Repeated set operations must produce fresh nonces, and SQLite files must not contain plaintext values.
    /// </summary>
    [Fact]
    public async Task SetRefreshesNonceAndNeverPersistsPlaintextAsync()
    {
        using var context = await OperationsContext.CreateAsync();
        const string firstSecret = "SCRAP-PLAINTEXT-ONE-秘密-🦊-8aa716f3";
        const string secondSecret = "SCRAP-PLAINTEXT-TWO-机密-🌙-d1fd425a";
        await context.Operations.CreateScopeAsync(new("encrypted"), default);

        RecordSetResult firstSet = await context.Operations.SetRecordAsync(
            new("encrypted", "credential", firstSecret),
            default);
        StoredRecord firstStored = Assert.IsType<StoredRecord>(context.Store.GetRecord("encrypted", "credential"));
        byte[] firstNonce = firstStored.Value.Nonce.ToArray();

        await context.Operations.SetRecordAsync(
            new("encrypted", "credential", secondSecret, ExpectedRevision: firstSet.Record.Revision),
            default);
        StoredRecord secondStored = Assert.IsType<StoredRecord>(context.Store.GetRecord("encrypted", "credential"));

        Assert.NotEqual(firstNonce, secondStored.Value.Nonce.ToArray());
        Assert.Equal(secondSecret, (await context.Operations.GetRecordAsync(new("encrypted", "credential"), default)).Record.Value);
        Assert.Equal(1, context.KeyProvider.StoreCount);
        Assert.NotNull(context.KeyProvider.ExportKey());

        SqliteConnection.ClearAllPools();
        AssertPlaintextAbsent(context.Paths.DatabaseFile, firstSecret, secondSecret);
    }

    /// <summary>
    /// record 与 scope rename 必须在身份变化后重新加密，并仍可解密原值。
    /// / Record and scope rename must re-encrypt after identity changes while preserving decryptability.
    /// </summary>
    [Fact]
    public async Task RecordAndScopeRenameReencryptAndRemainDecryptableAsync()
    {
        using var context = await OperationsContext.CreateAsync();
        const string value = "跨身份绑定的 secret 🔐\nline two";
        await context.Operations.CreateScopeAsync(new("旧空间"), default);
        RecordSetResult set = await context.Operations.SetRecordAsync(new("旧空间", "旧键", value), default);
        byte[] initialCiphertext = GetStored(context, "旧空间", "旧键").Value.Ciphertext.ToArray();

        RecordRenameResult renamedRecord = await context.Operations.RenameRecordAsync(
            new("旧空间", "旧键", "新键", set.Record.Revision),
            default);
        byte[] recordRenameCiphertext = GetStored(context, "旧空间", "新键").Value.Ciphertext.ToArray();
        ProtocolScopeRenameResult renamedScope = await context.Operations.RenameScopeAsync(new("旧空间", "新空间"), default);
        StoredRecord finalStored = GetStored(context, "新空间", "新键");
        RecordDto roundTrip = (await context.Operations.GetRecordAsync(new("新空间", "新键"), default)).Record;

        Assert.Equal("新键", renamedRecord.Record.Key);
        Assert.Equal("新空间", renamedScope.Scope.Name);
        Assert.NotEqual(initialCiphertext, recordRenameCiphertext);
        Assert.NotEqual(recordRenameCiphertext, finalStored.Value.Ciphertext.ToArray());
        Assert.Null(context.Store.GetRecord("旧空间", "旧键"));
        Assert.Equal(value, roundTrip.Value);
        Assert.Equal(("新空间", "新键"), (roundTrip.Scope, roundTrip.Key));
    }

    /// <summary>
    /// 过期 revision 对 set、rename、delete 均不得产生部分提交。
    /// / A stale revision must not partially commit set, rename, or delete.
    /// </summary>
    [Fact]
    public async Task RevisionConflictsDoNotCommitMutationsAsync()
    {
        using var context = await OperationsContext.CreateAsync();
        await context.Operations.CreateScopeAsync(new("concurrency"), default);
        RecordSetResult original = await context.Operations.SetRecordAsync(
            new("concurrency", "stable", "winner"),
            default);
        StoredRecord persisted = GetStored(context, "concurrency", "stable");
        long staleRevision = original.Record.Revision + 41;

        await Assert.ThrowsAsync<StorageConflictException>(() => context.Operations.SetRecordAsync(
            new("concurrency", "stable", "loser", ExpectedRevision: staleRevision),
            default));
        AssertStoredRecordUnchanged(persisted, GetStored(context, "concurrency", "stable"));

        await Assert.ThrowsAsync<StorageConflictException>(() => context.Operations.RenameRecordAsync(
            new("concurrency", "stable", "moved", staleRevision),
            default));
        Assert.Null(context.Store.GetRecord("concurrency", "moved"));
        AssertStoredRecordUnchanged(persisted, GetStored(context, "concurrency", "stable"));

        await Assert.ThrowsAsync<StorageConflictException>(() => context.Operations.DeleteRecordAsync(
            new("concurrency", "stable", staleRevision),
            default));
        RecordDto winner = (await context.Operations.GetRecordAsync(new("concurrency", "stable"), default)).Record;
        Assert.Equal("winner", winner.Value);
        Assert.Equal(original.Record.Revision, winner.Revision);
    }

    /// <summary>record.list 使用 ordinal keyset cursor 而非 offset。 / record.list uses an ordinal keyset cursor rather than an offset.</summary>
    [Fact]
    public async Task ListRecordsUsesOrdinalKeysetPaginationAsync()
    {
        using var context = await OperationsContext.CreateAsync();
        await context.Operations.CreateScopeAsync(new("paging"), default);
        foreach (string key in new[] { "b", "a", "A" })
        {
            await context.Operations.SetRecordAsync(new("paging", key, $"value for {key}"), default);
        }

        RecordListResult first = await context.Operations.ListRecordsAsync(new("paging", Limit: 2), default);
        RecordListResult second = await context.Operations.ListRecordsAsync(
            new("paging", AfterKey: first.NextCursor, Limit: 2),
            default);

        Assert.Equal(FirstKeysetPage, first.Records.Select(item => item.Key));
        Assert.Equal("a", first.NextCursor);
        Assert.Equal("b", Assert.Single(second.Records).Key);
        Assert.Null(second.NextCursor);
    }

    /// <summary>搜索只匹配 key，且结果投影绝不携带 value。 / Search matches keys only, and its result projection never carries values.</summary>
    [Fact]
    public async Task SearchNeverMatchesOrReturnsRecordValuesAsync()
    {
        using var context = await OperationsContext.CreateAsync();
        const string secret = "SEARCH-MUST-NOT-SEE-THIS-VALUE-秘密-💥";
        await context.Operations.CreateScopeAsync(new("search"), default);
        await context.Operations.SetRecordAsync(new("search", "VisibleKey", secret), default);

        RecordSearchResult valueSearch = await context.Operations.SearchRecordsAsync(
            new ProtocolSearchRequest(
                ["search"],
                secret,
                ProtocolSearchMode.Exact,
                ProtocolCaseSensitivity.Sensitive),
            default);
        RecordSearchResult keySearch = await context.Operations.SearchRecordsAsync(
            new ProtocolSearchRequest(
                ["search"],
                "VisibleKey",
                ProtocolSearchMode.Exact,
                ProtocolCaseSensitivity.Sensitive),
            default);

        Assert.Empty(valueSearch.Records);
        RecordSummaryDto summary = Assert.Single(keySearch.Records);
        Assert.Equal("VisibleKey", summary.Key);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(keySearch), StringComparison.Ordinal);
        Assert.DoesNotContain(
            typeof(RecordSummaryDto).GetProperties(),
            property => string.Equals(property.Name, "Value", StringComparison.Ordinal));
    }

    /// <summary>空 scope 集合检索全部，显式集合仅检索选定 scope，且同分排名确定。 / Empty scopes search all, explicit scopes filter, and tied ranking is deterministic.</summary>
    [Fact]
    public async Task SearchSupportsSelectedAndAllScopesAsync()
    {
        using var context = await OperationsContext.CreateAsync();
        foreach (var scope in new[] { "z", "A", "ignored" })
        {
            await context.Operations.CreateScopeAsync(new(scope), default);
            await context.Operations.SetRecordAsync(new(scope, "api", "value"), default);
        }

        RecordSearchResult selected = await context.Operations.SearchRecordsAsync(
            new ProtocolSearchRequest(["z", "A"], "api", ProtocolSearchMode.Exact),
            default);
        RecordSearchResult all = await context.Operations.SearchRecordsAsync(
            new ProtocolSearchRequest([], "api", ProtocolSearchMode.Exact),
            default);

        Assert.Equal(["A", "z"], selected.Records.Select(record => record.Scope));
        Assert.Equal(["A", "ignored", "z"], all.Records.Select(record => record.Scope));
        Assert.All(all.Records, record => Assert.Equal("api", record.Key));
    }

    /// <summary>非空 scope 仅在显式 recursive 时删除并报告级联数量。 / A non-empty scope is deleted only with explicit recursion and reports its cascade count.</summary>
    [Fact]
    public async Task DeleteScopeRequiresRecursiveAndCascadesRecordsAsync()
    {
        using var context = await OperationsContext.CreateAsync();
        await context.Operations.CreateScopeAsync(new("cleanup"), default);
        await context.Operations.SetRecordAsync(new("cleanup", "one", "1"), default);
        await context.Operations.SetRecordAsync(new("cleanup", "two", "2"), default);

        await Assert.ThrowsAsync<ScopeNotEmptyException>(
            () => context.Operations.DeleteScopeAsync(new("cleanup"), default));
        Assert.Equal(2, (await context.Operations.ListRecordsAsync(new("cleanup"), default)).Records.Count);

        StorageConflictException conflict = await Assert.ThrowsAsync<StorageConflictException>(
            () => context.Operations.DeleteScopeAsync(
                new("cleanup", Recursive: true, ExpectedRecordCount: 1),
                default));
        Assert.Equal(StorageConflictKind.Concurrency, conflict.Kind);
        Assert.NotNull(context.Store.GetScope("cleanup"));

        ScopeDeleteResult deleted = await context.Operations.DeleteScopeAsync(
            new("cleanup", Recursive: true, ExpectedRecordCount: 2),
            default);

        Assert.Equal(2, deleted.DeletedRecordCount);
        Assert.DoesNotContain(
            (await context.Operations.ListScopesAsync(new(), default)).Scopes,
            scope => string.Equals(scope.Name, "cleanup", StringComparison.Ordinal));
        Assert.Null(context.Store.GetScope("cleanup"));
    }

    /// <summary>读取已持久化 record，若缺失则使测试立即失败。 / Reads a persisted record and fails the test immediately when absent.</summary>
    private static StoredRecord GetStored(OperationsContext context, string scope, string key) =>
        Assert.IsType<StoredRecord>(context.Store.GetRecord(scope, key));

    /// <summary>验证失败 mutation 没有改变任何持久化字段。 / Verifies that a failed mutation changed no persisted field.</summary>
    private static void AssertStoredRecordUnchanged(StoredRecord expected, StoredRecord actual)
    {
        Assert.Equal(expected.Identity, actual.Identity);
        Assert.Equal(expected.ScopeId, actual.ScopeId);
        Assert.Equal(expected.Value.Ciphertext.ToArray(), actual.Value.Ciphertext.ToArray());
        Assert.Equal(expected.Value.Nonce.ToArray(), actual.Value.Nonce.ToArray());
        Assert.Equal(expected.Presentation, actual.Presentation);
        Assert.Equal(expected.Revision, actual.Revision);
        Assert.Equal(expected.CreatedAt, actual.CreatedAt);
        Assert.Equal(expected.UpdatedAt, actual.UpdatedAt);
    }

    /// <summary>扫描数据库、WAL 与 SHM，确认其中不含 UTF-8 明文。 / Scans the database, WAL, and SHM for absent UTF-8 plaintext.</summary>
    private static void AssertPlaintextAbsent(string databasePath, params string[] plaintextValues)
    {
        foreach (string path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
        {
            if (!File.Exists(path))
            {
                continue;
            }

            byte[] contents = ReadShared(path);
            foreach (string plaintext in plaintextValues)
            {
                byte[] needle = Encoding.UTF8.GetBytes(plaintext);
                Assert.True(
                    contents.AsSpan().IndexOf(needle) < 0,
                    $"Plaintext value appeared in {Path.GetFileName(path)}.");
            }
        }
    }

    /// <summary>以允许 SQLite 共存的共享方式读取文件。 / Reads a file with sharing compatible with SQLite.</summary>
    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// 持有隔离 profile、真实 store、真实 operations 与可控内存 key provider。
    /// / Owns an isolated profile, real store, real operations, and controllable in-memory key provider.
    /// </summary>
    private sealed class OperationsContext : IDisposable
    {
        private readonly string rootDirectory;

        /// <summary>创建已初始化的 fixture。 / Creates an initialized fixture.</summary>
        private OperationsContext(
            string rootDirectory,
            ScrapPathLayout paths,
            SqliteStore store,
            MemoryMasterKeyProvider keyProvider,
            SqliteDaemonOperations operations)
        {
            this.rootDirectory = rootDirectory;
            Paths = paths;
            Store = store;
            KeyProvider = keyProvider;
            Operations = operations;
        }

        /// <summary>标准 profile 路径。 / Canonical profile paths.</summary>
        public ScrapPathLayout Paths { get; }

        /// <summary>真实 SQLite store，供观察密文持久化状态。 / Real SQLite store used to observe ciphertext persistence state.</summary>
        public SqliteStore Store { get; }

        /// <summary>真实 daemon operations。 / Real daemon operations.</summary>
        public SqliteDaemonOperations Operations { get; }

        /// <summary>可控内存 master-key provider。 / Controllable in-memory master-key provider.</summary>
        public MemoryMasterKeyProvider KeyProvider { get; }

        /// <summary>
        /// 创建临时 profile，并通过真实 MasterKeyManager 初始化 operations。
        /// / Creates a temporary profile and initializes operations through the real MasterKeyManager.
        /// </summary>
        public static async Task<OperationsContext> CreateAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), "scrap-daemon-tests", Guid.NewGuid().ToString("N"));
            var paths = new ScrapPathLayout(root);
            paths.Initialize();
            var store = new SqliteStore(paths.DatabaseFile);
            var keyProvider = new MemoryMasterKeyProvider();
            var state = new DaemonInitializationState();
            var operations = new SqliteDaemonOperations(store, keyProvider, paths, state, TimeProvider.System);
            try
            {
                await operations.InitializeAsync(default);
                state.SetReady();
                return new OperationsContext(root, paths, store, keyProvider, operations);
            }
            catch
            {
                operations.Dispose();
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }

                throw;
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Operations.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(rootDirectory))
            {
                Directory.Delete(rootDirectory, recursive: true);
            }
        }
    }

    /// <summary>
    /// 复制输入与输出以模拟 provider 的所有权边界，并允许测试观察初始化。
    /// / Copies inputs and outputs to model provider ownership boundaries and lets tests observe initialization.
    /// </summary>
    private sealed class MemoryMasterKeyProvider : IMasterKeyProvider
    {
        private byte[]? key;

        /// <summary>StoreAsync 的调用次数。 / Number of StoreAsync calls.</summary>
        public int StoreCount { get; private set; }

        /// <inheritdoc />
        public ValueTask<byte[]?> LoadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(key?.ToArray());
        }

        /// <inheritdoc />
        public ValueTask StoreAsync(ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            key = value.ToArray();
            StoreCount++;
            return ValueTask.CompletedTask;
        }

        /// <summary>导出只供断言使用的 key 副本。 / Exports a key copy for assertions only.</summary>
        public byte[]? ExportKey() => key?.ToArray();
    }
}
