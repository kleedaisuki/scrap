using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Scrap.Client;
using Scrap.Crypto;
using Scrap.Daemon;
using Scrap.Domain;
using Scrap.Platform.Ipc;
using Scrap.Platform.Paths;
using Scrap.Platform.Processes;
using Scrap.Platform.Secrets;
using Scrap.Protocol;
using Scrap.Storage.Sqlite;
using DomainCase = Scrap.Domain.CaseSensitivity;
using DomainMode = Scrap.Domain.SearchMode;
using ProtocolCase = Scrap.Protocol.CaseSensitivity;
using ProtocolMode = Scrap.Protocol.SearchMode;

/// <summary>Search scaling measurements / 搜索扩展性测量.</summary>
internal static partial class Benchmarks
{
    /// <summary>Measures exact, fuzzy, and regex IPC searches at three fixture sizes and two scope selections. / 在三种夹具规模和两种 scope 选择下测量 exact、fuzzy 与 regex IPC 搜索。</summary>
    internal static async Task<IReadOnlyList<SearchScaleResults>> MeasureSearchAsync(string root)
    {
        var results = new List<SearchScaleResults>();
        foreach (int count in new[] { 1_000, 10_000, 50_000 })
        {
            string profile = Path.Combine(root, $"search-{count}");
            var paths = new ScrapPathLayout(profile);
            SeedSearchDatabase(paths, count);
            using Process process = StartChild(profile, "fixed");
            await using ScrapClient client = await ScrapClient.ConnectAsync(IpcEndpointDescriptor.Create(paths), NoOpLauncher.Instance, FastOptions());
            _ = await client.ListScopesAsync();

            string exactKey = $"service-{count - 1:D6}-{(((count - 1) % 10 == 0) ? "api-token" : "credential")}";
            string[] selected = ["scope-01", "scope-03", "scope-07"];
            var scenarios = new List<SearchScenarioResults>();
            foreach ((string name, IReadOnlyList<string> scopes, string query, ProtocolMode mode) in new[]
            {
                ("all/exact", (IReadOnlyList<string>)Array.Empty<string>(), exactKey, ProtocolMode.Exact),
                ("all/fuzzy", Array.Empty<string>(), "api-token", ProtocolMode.Fuzzy),
                ("all/regex", Array.Empty<string>(), "api-token$", ProtocolMode.Regex),
                ("three-scopes/exact", selected, "service-000777-credential", ProtocolMode.Exact),
                ("three-scopes/fuzzy", selected, "api-token", ProtocolMode.Fuzzy),
                ("three-scopes/regex", selected, "api-token$", ProtocolMode.Regex),
            })
            {
                var request = new Scrap.Protocol.SearchRequest(scopes, query, mode, ProtocolCase.Sensitive, 100);
                for (int i = 0; i < 8; i++)
                {
                    try { _ = await client.SearchRecordsAsync(request); }
                    catch (RemoteProtocolException exception) when (exception.ErrorCode == ProtocolErrorCodes.QueryTimeout) { }
                }
                var samples = new List<double>(60);
                int resultCount = 0;
                int timeouts = 0;
                for (int i = 0; i < 60; i++)
                {
                    long tick = Stopwatch.GetTimestamp();
                    try
                    {
                        RecordSearchResult response = await client.SearchRecordsAsync(request);
                        samples.Add(Stopwatch.GetElapsedTime(tick).TotalMilliseconds);
                        resultCount = response.Records.Count;
                    }
                    catch (RemoteProtocolException exception) when (exception.ErrorCode == ProtocolErrorCodes.QueryTimeout)
                    {
                        samples.Add(Stopwatch.GetElapsedTime(tick).TotalMilliseconds);
                        timeouts++;
                    }
                }
                scenarios.Add(new SearchScenarioResults(name, scopes.Count == 0 ? count : count * 3 / ScopeCount, resultCount, timeouts, Summarize(samples)));
            }

            await client.ShutdownAsync();
            if (!process.WaitForExit(10_000)) throw new TimeoutException("Search daemon child did not exit.");
            results.Add(new SearchScaleResults(count, scenarios, MeasureSearchPhases(paths, count)));
        }
        return results;
    }

    /// <summary>Separates metadata loading from fuzzy matching to localize scaling cost. / 分离元数据加载与模糊匹配，以定位扩展成本。</summary>
    internal static List<SearchPhaseResults> MeasureSearchPhases(ScrapPathLayout paths, int total)
    {
        var store = new SqliteStore(paths.DatabaseFile);
        store.Initialize();
        var results = new List<SearchPhaseResults>();
        foreach ((string name, string[] scopes) in new[] { ("all", Array.Empty<string>()), ("three-scopes", new[] { "scope-01", "scope-03", "scope-07" }) })
        {
            for (int i = 0; i < 5; i++) _ = store.ListAllRecordMetadata(scopes);
            var load = new List<double>(30);
            IReadOnlyList<StoredRecordMetadata> metadata = [];
            for (int i = 0; i < 30; i++)
            {
                long tick = Stopwatch.GetTimestamp();
                metadata = store.ListAllRecordMetadata(scopes);
                load.Add(Stopwatch.GetElapsedTime(tick).TotalMilliseconds);
            }
            Scrap.Domain.SearchRequest request = Scrap.Domain.SearchRequest.TryCreate(scopes, "api-token", DomainMode.Fuzzy, DomainCase.Sensitive, 100).Value;
            SearchCandidate[] candidates = metadata.Select(x => new SearchCandidate(ScopeName.Create(x.Identity.ScopeName), RecordKey.Create(x.Identity.Key))).ToArray();
            for (int i = 0; i < 5; i++) _ = RecordSearch.Search(candidates, request).Value;
            var match = new List<double>(30);
            for (int i = 0; i < 30; i++)
            {
                long tick = Stopwatch.GetTimestamp();
                _ = RecordSearch.Search(candidates, request).Value;
                match.Add(Stopwatch.GetElapsedTime(tick).TotalMilliseconds);
            }
            results.Add(new SearchPhaseResults(name, metadata.Count, Summarize(load), Summarize(match)));
        }
        return results;
    }

    /// <summary>Creates valid encrypted search fixtures in one untimed SQLite transaction. / 在一个不计时的 SQLite 事务中创建有效加密搜索夹具。</summary>
    internal static void SeedSearchDatabase(ScrapPathLayout paths, int count)
    {
        PrepareProfileRoot(paths.RootDirectory);
        paths.Initialize();
        var store = new SqliteStore(paths.DatabaseFile);
        store.Initialize();
        for (int i = 0; i < ScopeCount; i++) store.CreateScope($"scope-{i:D2}");
        var cs = new SqliteConnectionStringBuilder { DataSource = paths.DatabaseFile, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
        using var connection = new SqliteConnection(cs);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        var scopeIds = new long[ScopeCount];
        for (int i = 0; i < ScopeCount; i++)
        {
            using var scopeCommand = connection.CreateCommand();
            scopeCommand.Transaction = transaction;
            scopeCommand.CommandText = "SELECT id FROM scopes WHERE name=$name";
            scopeCommand.Parameters.AddWithValue("$name", $"scope-{i:D2}");
            scopeIds[i] = (long)scopeCommand.ExecuteScalar()!;
        }

        using var encryptor = new AesGcmRecordEncryptor(FixedKey);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO records(id,scope_id,key,value_ciphertext,nonce,crypto_version,presentation,revision,created_at,updated_at) VALUES($id,$scope,$key,$cipher,$nonce,1,0,1,$created,$updated)";
        SqliteParameter id = command.Parameters.Add("$id", SqliteType.Text);
        SqliteParameter scope = command.Parameters.Add("$scope", SqliteType.Integer);
        SqliteParameter key = command.Parameters.Add("$key", SqliteType.Text);
        SqliteParameter cipher = command.Parameters.Add("$cipher", SqliteType.Blob);
        SqliteParameter nonce = command.Parameters.Add("$nonce", SqliteType.Blob);
        command.Parameters.AddWithValue("$created", "2026-01-01T00:00:00.0000000+00:00");
        command.Parameters.AddWithValue("$updated", "2026-01-01T00:00:00.0000000+00:00");
        byte[] plaintext = Encoding.UTF8.GetBytes("non-secret benchmark fixture value");
        for (int i = 0; i < count; i++)
        {
            int scopeIndex = i % ScopeCount;
            string recordId = $"bench{i:D8}";
            string recordKey = $"service-{i:D6}-{(i % 10 == 0 ? "api-token" : "credential")}";
            EncryptedRecordValue encrypted = encryptor.Encrypt(plaintext, new(1, recordId, $"scope-{scopeIndex:D2}", recordKey));
            id.Value = recordId;
            scope.Value = scopeIds[scopeIndex];
            key.Value = recordKey;
            cipher.Value = encrypted.Ciphertext;
            nonce.Value = encrypted.Nonce;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

}
