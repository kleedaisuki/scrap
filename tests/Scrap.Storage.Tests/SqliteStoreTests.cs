using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Scrap.Storage.Sqlite;

namespace Scrap.Storage.Tests;

/// <summary>对真实临时 SQLite 文件执行存储契约测试。 / Exercises the storage contract against real temporary SQLite files.</summary>
public sealed class SqliteStoreTests
{
    private static readonly string[] ContractTables = ["meta", "records", "scopes"];
    private static readonly string[] ExtensionColumns = ["crypto_version", "revision"];
    private static readonly string[] OrderedScopes = [" alpha ", "Alpha", "alpha"];
    private static readonly string[] RenamedKeys = ["a", "b"];
    private static readonly string[] FirstKeysetPage = ["A", "a"];
    private static readonly string[] OrdinalEdgeCaseOrder = ["\U00010000", "\uE000", "\uFFFF"];

    [Fact]
    public void InitializeCreatesVersionOneSchemaAndRequiredPragmas()
    {
        using var database = TestDatabase.Create();
        var store = database.Store;

        Assert.Equal(1, store.GetSchemaVersion());
        var status = store.GetStatus();
        Assert.Equal("wal", status.JournalMode, ignoreCase: true);
        Assert.Equal(2, status.Synchronous); // SQLITE_SYNC_FULL
        Assert.True(status.ForeignKeys);
        Assert.Equal(5000, status.BusyTimeoutMilliseconds);

        using var connection = database.OpenRawConnection();
        Assert.Equal(ContractTables, ReadNames(
            connection,
            "SELECT name FROM sqlite_schema WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;"));
        Assert.Contains("records_by_scope", ReadNames(
            connection,
            "SELECT name FROM sqlite_schema WHERE type = 'index' AND name = 'records_by_scope';"));
        Assert.Equal(ExtensionColumns, ReadNames(
            connection,
            "SELECT name FROM pragma_table_info('records') WHERE name IN ('crypto_version', 'revision') ORDER BY name;"));
    }

    [Fact]
    public void InitializeIsIdempotentAndRejectsNewerSchema()
    {
        using var database = TestDatabase.Create();
        database.Store.Initialize();
        Assert.Equal(1, database.Store.GetSchemaVersion());

        using (var connection = database.OpenRawConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE meta SET value = '99' WHERE key = 'schema_version';";
            command.ExecuteNonQuery();
        }

        var exception = Assert.Throws<StorageMigrationException>(database.Store.Initialize);
        Assert.Contains("newer", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScopesAreOrdinalCaseSensitiveAndPreserveWhitespace()
    {
        using var database = TestDatabase.Create();
        var timestamp = new DateTimeOffset(2026, 9, 13, 1, 2, 3, TimeSpan.FromHours(8));
        database.Store.CreateScope("Alpha", timestamp);
        database.Store.CreateScope("alpha", timestamp);
        database.Store.CreateScope(" alpha ", timestamp);

        Assert.Equal(OrderedScopes, database.Store.ListScopes().Select(scope => scope.Name));
        Assert.Equal("Alpha", database.Store.GetScope("Alpha")?.Name);
        Assert.Equal("alpha", database.Store.GetScope("alpha")?.Name);
        Assert.Throws<StorageConflictException>(() => database.Store.CreateScope("Alpha"));
        Assert.Equal(timestamp.ToUniversalTime(), database.Store.GetScope("Alpha")?.CreatedAt);
    }

    [Fact]
    public void SetRecordUpsertsAndPreservesPermanentIdentity()
    {
        using var database = TestDatabase.Create();
        database.Store.CreateScope("dev");
        RecordIdentity? firstIdentity = null;
        var first = database.Store.SetRecord("dev", "TOKEN", 0, identity =>
        {
            firstIdentity = identity;
            return Protected("first", 7);
        });
        var second = database.Store.SetRecord("dev", "TOKEN", 1, identity =>
        {
            Assert.Equal(firstIdentity, identity);
            return Protected("second", 8);
        }, expectedRevision: first.Revision);

        Assert.Equal(first.Identity.RecordId, second.Identity.RecordId);
        Assert.Equal(first.CreatedAt, second.CreatedAt);
        Assert.Equal(first.Revision + 1, second.Revision);
        Assert.Equal(8, second.Value.CryptoVersion);
        Assert.Equal(1, second.Presentation);
        AssertRecordEquivalent(second, database.Store.GetRecord("dev", "TOKEN"));
        Assert.Null(database.Store.GetRecord("dev", "token"));
    }

    [Fact]
    public void DetailedSetAndMetadataExposeTransactionalFactsWithoutCiphertext()
    {
        using var database = TestDatabase.Create();
        database.Store.CreateScope("dev");
        var created = database.Store.SetRecordDetailed("dev", "a", 0, _ => Protected("one"));
        var updated = database.Store.SetRecordDetailed(
            "dev", "a", 1, _ => Protected("two"), expectedRevision: created.Record.Revision);

        Assert.True(created.Created);
        Assert.False(updated.Created);
        var metadata = Assert.Single(database.Store.ListRecordMetadata("dev", limit: 1));
        Assert.Equal(updated.Record.Identity, metadata.Identity);
        Assert.Equal(updated.Record.Revision, metadata.Revision);
        Assert.Equal(updated.Record.UpdatedAt, metadata.UpdatedAt);
    }

    [Fact]
    public void MetadataKeysetPaginationUsesOrdinalOrdering()
    {
        using var database = TestDatabase.Create();
        database.Store.CreateScope("dev");
        database.Store.SetRecord("dev", "A", 0, _ => Protected("one"));
        database.Store.SetRecord("dev", "a", 0, _ => Protected("two"));
        database.Store.SetRecord("dev", "b", 0, _ => Protected("three"));

        var firstPage = database.Store.ListRecordMetadata("dev", afterKey: null, limit: 2);
        var secondPage = database.Store.ListRecordMetadata("dev", afterKey: firstPage[^1].Identity.Key, limit: 2);
        Assert.Equal(FirstKeysetPage, firstPage.Select(record => record.Identity.Key));
        Assert.Equal("b", Assert.Single(secondPage).Identity.Key);
    }

    [Fact]
    public void MetadataKeysetPaginationDoesNotBreakBetweenSupplementaryAndBmpKeys()
    {
        using var database = TestDatabase.Create();
        database.Store.CreateScope("unicode");
        foreach (var key in OrdinalEdgeCaseOrder.Reverse())
        {
            database.Store.SetRecord("unicode", key, 0, _ => Protected(key));
        }

        var observed = new List<string>();
        string? afterKey = null;
        while (true)
        {
            var page = database.Store.ListRecordMetadata("unicode", afterKey, limit: 1);
            if (page.Count == 0)
            {
                break;
            }

            afterKey = page[0].Identity.Key;
            observed.Add(afterKey);
        }

        Assert.Equal(OrdinalEdgeCaseOrder, observed);
        Assert.Equal(OrdinalEdgeCaseOrder, database.Store.ListRecords("unicode").Select(record => record.Identity.Key));
    }

    [Fact]
    public void ExpectedUpdatedAtIsCheckedInsideMutation()
    {
        using var database = TestDatabase.Create();
        database.Store.CreateScope("dev");
        var first = database.Store.SetRecord("dev", "key", 0, _ => Protected("one"));
        database.Store.SetRecord("dev", "key", 0, _ => Protected("two"), expectedUpdatedAt: first.UpdatedAt);

        Assert.Throws<StorageConflictException>(() => database.Store.SetRecord(
            "dev",
            "key",
            0,
            _ => Protected("stale"),
            expectedUpdatedAt: first.UpdatedAt));
    }

    [Fact]
    public void RenameRecordIsAtomicAndKeepsId()
    {
        using var database = TestDatabase.Create();
        database.Store.CreateScope("dev");
        var source = database.Store.SetRecord("dev", "old", 0, _ => Protected("old"));
        database.Store.SetRecord("dev", "occupied", 0, _ => Protected("occupied"));

        Assert.Throws<StorageConflictException>(() => database.Store.RenameRecord(
            "dev", "old", "occupied", (_, _) => Protected("must-not-write")));
        Assert.NotNull(database.Store.GetRecord("dev", "old"));

        RecordIdentity? newIdentity = null;
        var renamed = database.Store.RenameRecord("dev", "old", "new", (record, identity) =>
        {
            AssertRecordEquivalent(source, record);
            newIdentity = identity;
            return Protected("new");
        }, expectedRevision: source.Revision);
        Assert.Equal(source.Identity.RecordId, renamed.Identity.RecordId);
        Assert.Equal("new", newIdentity?.Key);
        Assert.Null(database.Store.GetRecord("dev", "old"));
        AssertRecordEquivalent(renamed, database.Store.GetRecord("dev", "new"));
    }

    [Fact]
    public void RenameScopeReencryptsEveryRecordAtomically()
    {
        using var database = TestDatabase.Create();
        database.Store.CreateScope("old");
        database.Store.SetRecord("old", "a", 0, _ => Protected("a"));
        database.Store.SetRecord("old", "b", 1, _ => Protected("b"));
        var seen = new List<RecordIdentity>();

        var result = database.Store.RenameScope("old", "new", (_, identity) =>
        {
            seen.Add(identity);
            return Protected($"new-{identity.Key}");
        });

        Assert.Equal(2, result.ReencryptedRecordCount);
        Assert.All(seen, identity => Assert.Equal("new", identity.ScopeName));
        Assert.Null(database.Store.GetScope("old"));
        Assert.Equal(RenamedKeys, database.Store.ListRecords("new").Select(record => record.Identity.Key));
    }

    [Fact]
    public void RenameScopeCallbackFailureRollsBackEveryChange()
    {
        using var database = TestDatabase.Create();
        database.Store.CreateScope("old");
        var a = database.Store.SetRecord("old", "a", 0, _ => Protected("a"));
        var b = database.Store.SetRecord("old", "b", 0, _ => Protected("b"));

        Assert.Throws<InvalidOperationException>(() => database.Store.RenameScope("old", "new", (_, identity) =>
            identity.Key == "b" ? throw new InvalidOperationException("synthetic crypto failure") : Protected("replacement")));

        Assert.NotNull(database.Store.GetScope("old"));
        Assert.Null(database.Store.GetScope("new"));
        AssertRecordEquivalent(a, database.Store.GetRecord("old", "a"));
        AssertRecordEquivalent(b, database.Store.GetRecord("old", "b"));
    }

    [Fact]
    public void StagedScopeRenameRejectsAStaleSnapshot()
    {
        using var database = TestDatabase.Create();
        database.Store.CreateScope("old");
        var record = database.Store.SetRecord("old", "a", 0, _ => Protected("a"));
        var stale = new RecordReencryption(record.Identity.RecordId, record.Revision, Protected("renamed"));
        database.Store.SetRecord("old", "a", 0, _ => Protected("changed"));

        var exception = Assert.Throws<StorageConflictException>(() => database.Store.RenameScope("old", "new", [stale]));
        Assert.Equal(StorageConflictKind.Concurrency, exception.Kind);
        Assert.NotNull(database.Store.GetScope("old"));
        Assert.Null(database.Store.GetScope("new"));
    }

    [Fact]
    public void DeleteScopeRequiresExplicitRecursiveAndUsesForeignKeyCascade()
    {
        using var database = TestDatabase.Create();
        database.Store.CreateScope("dev");
        database.Store.SetRecord("dev", "key", 0, _ => Protected("value"));
        var exception = Assert.Throws<ScopeNotEmptyException>(() => database.Store.DeleteScope("dev"));
        Assert.Equal(1, exception.RecordCount);
        Assert.Equal(1, database.Store.DeleteScope("dev", recursive: true));

        using var connection = database.OpenRawConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM records;";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    [Fact]
    public void DatabaseAndWalDoNotContainPlaintextValue()
    {
        using var database = TestDatabase.Create();
        database.Store.CreateScope("scan");
        const string plaintext = "DO-NOT-LEAK-this-unique-plaintext-秘密";
        database.Store.SetRecord("scan", "safe-key", 0, _ => Protected(plaintext));
        var needle = Encoding.UTF8.GetBytes(plaintext);
        SqliteConnection.ClearAllPools();

        foreach (var path in new[] { database.Path, database.Path + "-wal", database.Path + "-shm" })
        {
            if (File.Exists(path))
            {
                Assert.False(Contains(ReadShared(path), needle), $"Plaintext appeared in {Path.GetFileName(path)}");
            }
        }
    }

    private static ProtectedValue Protected(string plaintext, int cryptoVersion = 1) => new(
        SHA256.HashData(Encoding.UTF8.GetBytes(plaintext)),
        RandomNumberGenerator.GetBytes(12),
        cryptoVersion);

    private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;

    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static void AssertRecordEquivalent(StoredRecord expected, StoredRecord? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Identity, actual.Identity);
        Assert.Equal(expected.ScopeId, actual.ScopeId);
        Assert.Equal(expected.Value.Ciphertext.ToArray(), actual.Value.Ciphertext.ToArray());
        Assert.Equal(expected.Value.Nonce.ToArray(), actual.Value.Nonce.ToArray());
        Assert.Equal(expected.Value.CryptoVersion, actual.Value.CryptoVersion);
        Assert.Equal(expected.Presentation, actual.Presentation);
        Assert.Equal(expected.Revision, actual.Revision);
        Assert.Equal(expected.CreatedAt, actual.CreatedAt);
        Assert.Equal(expected.UpdatedAt, actual.UpdatedAt);
    }

    private static string[] ReadNames(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }

        return result.ToArray();
    }
}

/// <summary>拥有临时目录及真实 SQLite store 的测试夹具。 / Test fixture owning a temporary directory and real SQLite store.</summary>
internal sealed class TestDatabase : IDisposable
{
    private readonly string directory;

    private TestDatabase(string directory, string path, SqliteStore store)
    {
        this.directory = directory;
        Path = path;
        Store = store;
    }

    /// <summary>数据库文件路径。 / Database file path.</summary>
    public string Path { get; }

    /// <summary>已初始化的 store。 / Initialized store.</summary>
    public SqliteStore Store { get; }

    /// <summary>创建隔离的真实数据库。 / Creates an isolated real database.</summary>
    public static TestDatabase Create()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "scrap-storage-tests", Guid.NewGuid().ToString("N"));
        var path = System.IO.Path.Combine(directory, "scrap.db");
        var store = new SqliteStore(path);
        store.Initialize();
        return new TestDatabase(directory, path, store);
    }

    /// <summary>打开绕过 store 的探查连接，仅供验证 schema。 / Opens a raw inspection connection used only for schema verification.</summary>
    public SqliteConnection OpenRawConnection()
    {
        var connection = new SqliteConnection($"Data Source={Path};Mode=ReadWrite");
        connection.Open();
        return connection;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

