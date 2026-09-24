using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Scrap.Crypto;
using Scrap.Daemon;
using Scrap.Domain;
using Scrap.Platform.Paths;
using Scrap.Platform.Processes;
using Scrap.Storage.Sqlite;
using DomainCase = Scrap.Domain.CaseSensitivity;
using DomainMode = Scrap.Domain.SearchMode;

/// <summary>Focused storage experiments / 存储专项实验.</summary>
internal static partial class Benchmarks
{
    /// <summary>Measures metadata materialization for all scopes and a three-scope subset. / 测量全 scope 与三 scope 子集的元数据物化。</summary>
    internal static async Task RunMetadataFocusAsync(string database, string output)
    {
        var store = new SqliteStore(Path.GetFullPath(database));
        store.Initialize();
        string[] selected = ["scope-01", "scope-03", "scope-07"];
        var result = new Dictionary<string, Distribution>();
        foreach ((string name, string[] scopes) in new[] { ("all", Array.Empty<string>()), ("three-scopes", selected) })
        {
            for (int i = 0; i < 15; i++) _ = store.ListAllRecordMetadata(scopes);
            var samples = new List<double>(120);
            for (int i = 0; i < 120; i++)
            {
                long tick = Stopwatch.GetTimestamp();
                _ = store.ListAllRecordMetadata(scopes);
                samples.Add(Stopwatch.GetElapsedTime(tick).TotalMilliseconds);
            }
            result[name] = Summarize(samples);
        }
        await WriteJsonAsync(output, result);
    }

    /// <summary>Measures CRUD and scope-count behavior with the scope index kept or dropped. / 测量保留或删除 scope 索引时的 CRUD 与 scope 计数行为。</summary>
    internal static async Task RunIndexFocusAsync(string database, bool dropIndex, string output)
    {
        var store = new SqliteStore(Path.GetFullPath(database));
        store.Initialize();
        store.CreateScope("index-write");
        string cs = new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadWrite, Pooling = true }.ToString();
        if (dropIndex)
        {
            using var connection = new SqliteConnection(cs);
            connection.Open();
            using var drop = connection.CreateCommand();
            drop.CommandText = "DROP INDEX records_by_scope";
            drop.ExecuteNonQuery();
        }

        using var encryptor = new AesGcmRecordEncryptor(FixedKey);
        ProtectedValue Protect(RecordIdentity identity)
        {
            EncryptedRecordValue value = encryptor.Encrypt("index benchmark value"u8, new(1, identity.RecordId, identity.ScopeName, identity.Key));
            return new(value.Ciphertext, value.Nonce);
        }

        const int count = 500;
        var set = new List<double>(count);
        var get = new List<double>(1000);
        var delete = new List<double>(count);
        long phase = Stopwatch.GetTimestamp();
        for (int i = 0; i < count; i++)
        {
            long tick = Stopwatch.GetTimestamp();
            _ = store.SetRecord("index-write", $"record-{i:D5}", 0, Protect);
            set.Add(Stopwatch.GetElapsedTime(tick).TotalMilliseconds);
        }
        double setSeconds = Stopwatch.GetElapsedTime(phase).TotalSeconds;
        phase = Stopwatch.GetTimestamp();
        for (int i = 0; i < 1000; i++)
        {
            long tick = Stopwatch.GetTimestamp();
            _ = store.GetRecord("index-write", $"record-{i % count:D5}");
            get.Add(Stopwatch.GetElapsedTime(tick).TotalMilliseconds);
        }
        double getSeconds = Stopwatch.GetElapsedTime(phase).TotalSeconds;
        phase = Stopwatch.GetTimestamp();
        for (int i = 0; i < count; i++)
        {
            long tick = Stopwatch.GetTimestamp();
            store.DeleteRecord("index-write", $"record-{i:D5}");
            delete.Add(Stopwatch.GetElapsedTime(tick).TotalMilliseconds);
        }
        double deleteSeconds = Stopwatch.GetElapsedTime(phase).TotalSeconds;

        var countSamples = new List<double>(2000);
        using (var connection = new SqliteConnection(cs))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM records WHERE scope_id=(SELECT id FROM scopes WHERE name='scope-03')";
            for (int i = 0; i < 100; i++) _ = command.ExecuteScalar();
            for (int i = 0; i < 2000; i++)
            {
                long tick = Stopwatch.GetTimestamp();
                _ = command.ExecuteScalar();
                countSamples.Add(Stopwatch.GetElapsedTime(tick).TotalMilliseconds);
            }
        }

        var result = new
        {
            DropIndex = dropIndex,
            Set = new OperationStats(count / setSeconds, Summarize(set)),
            Get = new OperationStats(1000 / getSeconds, Summarize(get)),
            Delete = new OperationStats(count / deleteSeconds, Summarize(delete)),
            CountScopeMs = Summarize(countSamples),
        };
        await WriteJsonAsync(output, result);
    }

    /// <summary>Repeats the production metadata-and-fuzzy path for external sampling profilers. / 重复生产元数据与模糊搜索路径，供外部采样分析器使用。</summary>
    internal static void RunSearchProfile(string database, TimeSpan duration)
    {
        var store = new SqliteStore(Path.GetFullPath(database));
        store.Initialize();
        Scrap.Domain.SearchRequest request = Scrap.Domain.SearchRequest.TryCreate(Array.Empty<string>(), "api-token", DomainMode.Fuzzy, DomainCase.Sensitive, 100).Value;
        long deadline = Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency);
        int iterations = 0;
        while (Stopwatch.GetTimestamp() < deadline)
        {
            IReadOnlyList<StoredRecordMetadata> metadata = store.ListAllRecordMetadata([]);
            SearchCandidate[] candidates = metadata.Select(x => new SearchCandidate(ScopeName.Create(x.Identity.ScopeName), RecordKey.Create(x.Identity.Key))).ToArray();
            _ = RecordSearch.Search(candidates, request).Value;
            iterations++;
        }
        Console.WriteLine($"profile iterations={iterations}");
    }

    /// <summary>Hosts one isolated production daemon using either the OS or deterministic fixture key provider. / 使用操作系统或确定性夹具密钥提供器托管一个隔离的生产守护进程。</summary>
    internal static async Task<int> RunDaemonChildAsync(string root, string provider)
    {
        var paths = new ScrapPathLayout(root);
        PrepareProfileRoot(root);
        paths.Initialize();
        using DaemonInstanceLease? lease = DaemonInstanceLease.TryAcquire(paths);
        if (lease is null) return 9;
        using IHost host = provider == "fixed"
            ? DaemonHost.Build([], paths, new FixedMasterKeyProvider(FixedKey))
            : DaemonHost.Build([], paths);
        await host.RunAsync();
        return 0;
    }

}
