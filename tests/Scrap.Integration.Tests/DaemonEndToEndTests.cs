using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Scrap.Client;
using Scrap.Daemon;
using Scrap.Platform.Ipc;
using Scrap.Platform.Paths;
using Scrap.Platform.Processes;
using Scrap.Platform.Secrets;
using Scrap.Protocol;
using ProtocolCaseSensitivity = Scrap.Protocol.CaseSensitivity;
using ProtocolSearchMode = Scrap.Protocol.SearchMode;

namespace Scrap.Integration.Tests;

/// <summary>
/// 通过真实 Generic Host、命名管道、typed client、SQLite 与 AES-GCM 验证进程内系统边界。
/// / Verifies the in-process system boundary through a real Generic Host, named pipe, typed client, SQLite, and AES-GCM.
/// </summary>
/// <remarks>
/// 每个测试只使用短路径临时 profile，并显式把同一 endpoint 交给 host 与 client；测试永不解析或访问真实
/// <c>~/.scrap</c>。/ Every test uses a short temporary profile and explicitly gives the same endpoint to the host and
/// client; the real <c>~/.scrap</c> is never resolved or accessed.
/// </remarks>
public sealed class DaemonEndToEndTests
{
    private const string InitialSecret = "SCRAP-E2E-PLAINTEXT-19fba1b88b8c4e7398b7f149c71d152d-秘密-🦊\nline-two";
    private const string ReplacementSecret = "SCRAP-E2E-PLAINTEXT-f07dd44822574bdf92ae6279f047a91d-机密-🌙\r\n终章";
    private static readonly string[] FirstPageKeys = ["A", "a", "api"];

    /// <summary>旧标量 set 的非法身份仍通过 wire 映射为 invalid_params。 / Invalid identity on legacy scalar set still maps to invalid_params over the wire.</summary>
    [Fact]
    public async Task InvalidLegacyScalarIdentityMapsToInvalidParamsAsync()
    {
        await using DaemonTestContext context = await DaemonTestContext.StartAsync();
        RemoteProtocolException exception = await Assert.ThrowsAsync<RemoteProtocolException>(
            () => context.Client.SetRecordAsync("", "key", "value"));
        Assert.Equal(ProtocolErrorCodes.InvalidParams, exception.ErrorCode);
    }

    /// <summary>typed client 的整列表 API 穿过真实 IPC，并由一次 revision/CAS 原子保护。 / The typed client's whole-list API crosses real IPC and is atomically guarded by one revision/CAS.</summary>
    [Fact]
    public async Task MultiValueRoundTripsThroughTypedClientAsync()
    {
        await using DaemonTestContext context = await DaemonTestContext.StartAsync();
        _ = await context.Client.CreateScopeAsync("multi");
        RecordSetResult created = await context.Client.SetRecordAsync(
            "multi", "key", ["first", "", "first", "tail"], RecordPresentation.Plain);
        RecordDto initial = (await context.Client.GetRecordAsync("multi", "key")).Record;
        Assert.Equal(["first", "", "first", "tail"], initial.Values);
        Assert.Equal("first", initial.Value);

        _ = await context.Client.SetRecordAsync(
            "multi", "key", ["replacement", "last"], RecordPresentation.Masked, created.Record.Revision);
        RecordDto replaced = (await context.Client.GetRecordAsync("multi", "key")).Record;
        Assert.Equal(["replacement", "last"], replaced.Values);
        Assert.Equal(created.Record.Revision + 1, replaced.Revision);
    }

    /// <summary>异步写入前复制调用方列表，读取结果不可通过数组转换或集合接口改写。 / Writes snapshot the caller's list before asynchronous work, and reads cannot be changed through an array cast or collection interface.</summary>
    [Fact]
    public async Task TypedClientSnapshotsValuesAcrossAsyncBoundaryAsync()
    {
        await using DaemonTestContext context = await DaemonTestContext.StartAsync();
        _ = await context.Client.CreateScopeAsync("snapshot");
        string[] source = ["original", "tail"];

        Task<RecordSetResult> write = context.Client.SetRecordAsync("snapshot", "key", source);
        source[0] = "mutated";
        await write;

        RecordDto record = (await context.Client.GetRecordAsync("snapshot", "key")).Record;
        Assert.Equal(["original", "tail"], record.Values);
        Assert.False(record.Values is string[]);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)record.Values)[0] = "mutated");
        Assert.Equal("original", record.Values[0]);
    }

    /// <summary>
    /// 主密钥 provider 失效只会禁用业务操作，不会杀死 daemon 的控制面。
    /// / A failed master-key provider disables business operations without killing the daemon control plane.
    /// </summary>
    [Fact]
    public async Task MasterKeyProviderFailureKeepsControlPlaneAliveUntilExplicitShutdownAsync()
    {
        await using DaemonTestContext context = await DaemonTestContext.StartAsync(AlwaysFailingMasterKeyProvider.Instance);

        _ = await context.Client.PingAsync();
        DaemonVersionResult version = await context.Client.GetDaemonVersionAsync();
        Assert.InRange(
            ProtocolConstants.CurrentVersion,
            version.MinProtocolVersion,
            version.MaxProtocolVersion);

        RemoteProtocolException unavailable = await Assert.ThrowsAsync<RemoteProtocolException>(
            () => context.Client.ListScopesAsync());
        Assert.Equal(ProtocolErrorCodes.KeyUnavailable, unavailable.ErrorCode);
        Assert.False(context.HasStopped, "The host exited after a recoverable master-key provider failure.");

        await context.ShutdownAsync();
    }

    /// <summary>
    /// Unicode CRUD、三种搜索、keyset 分页及两级 rename 均穿过真实 IPC，并保持密文身份绑定。
    /// / Unicode CRUD, all three search modes, keyset pagination, and both rename levels cross real IPC while preserving
    /// ciphertext identity binding.
    /// </summary>
    [Fact]
    public async Task UnicodeCrudSearchPaginationAndRenameRoundTripThroughRealHostAsync()
    {
        await using DaemonTestContext context = await DaemonTestContext.StartAsync();
        ScrapClient client = context.Client;
        const string originalScope = "研发空间 🧪";
        const string renamedScope = "生产空间 🚀";
        const string originalKey = "发布凭据 🔑";
        const string renamedKey = "部署凭据 🗝️";

        _ = await client.PingAsync();
        DaemonVersionResult version = await client.GetDaemonVersionAsync();
        Assert.InRange(
            ProtocolConstants.CurrentVersion,
            version.MinProtocolVersion,
            version.MaxProtocolVersion);

        ScopeCreateResult createdScope = await client.CreateScopeAsync(originalScope);
        Assert.Equal(originalScope, createdScope.Scope.Name);
        Assert.Contains(
            (await client.ListScopesAsync()).Scopes,
            scope => string.Equals(scope.Name, originalScope, StringComparison.Ordinal));

        RecordSetResult created = await client.SetRecordAsync(
            originalScope,
            originalKey,
            InitialSecret,
            RecordPresentation.Plain);
        Assert.True(created.Created);
        RecordDto initial = (await client.GetRecordAsync(originalScope, originalKey)).Record;
        Assert.Equal(InitialSecret, initial.Value);
        Assert.Equal(RecordPresentation.Plain, initial.Presentation);
        AssertPlaintextAbsent(context.Paths.DatabaseFile, InitialSecret);

        RecordSetResult updated = await client.SetRecordAsync(
            originalScope,
            originalKey,
            ReplacementSecret,
            RecordPresentation.Masked,
            created.Record.Revision);
        Assert.False(updated.Created);
        Assert.Equal(created.Record.Revision + 1, updated.Record.Revision);
        AssertPlaintextAbsent(context.Paths.DatabaseFile, InitialSecret, ReplacementSecret);
        CiphertextSnapshot beforeRename = ReadCiphertext(context.Paths.DatabaseFile, originalScope, originalKey);

        foreach (string key in new[] { "b", "a", "A", "api", "api-token", "my-api", "xapi" })
        {
            await client.SetRecordAsync(originalScope, key, $"non-secret fixture value for {key}");
        }

        RecordRenameResult recordRename = await client.RenameRecordAsync(
            originalScope,
            originalKey,
            renamedKey,
            updated.Record.Revision);
        Assert.Equal(renamedKey, recordRename.Record.Key);
        CiphertextSnapshot afterRecordRename = ReadCiphertext(context.Paths.DatabaseFile, originalScope, renamedKey);
        AssertFreshCiphertext(beforeRename, afterRecordRename);
        Assert.Equal(ReplacementSecret, (await client.GetRecordAsync(originalScope, renamedKey)).Record.Value);

        ScopeRenameResult scopeRename = await client.RenameScopeAsync(originalScope, renamedScope);
        Assert.Equal(renamedScope, scopeRename.Scope.Name);
        CiphertextSnapshot afterScopeRename = ReadCiphertext(context.Paths.DatabaseFile, renamedScope, renamedKey);
        AssertFreshCiphertext(afterRecordRename, afterScopeRename);
        RecordDto renamed = (await client.GetRecordAsync(renamedScope, renamedKey)).Record;
        Assert.Equal((renamedScope, renamedKey, ReplacementSecret), (renamed.Scope, renamed.Key, renamed.Value));

        RecordListResult firstPage = await client.ListRecordsAsync(renamedScope, afterKey: null, limit: 3);
        RecordListResult secondPage = await client.ListRecordsAsync(renamedScope, firstPage.NextCursor, limit: 3);
        Assert.Equal(FirstPageKeys, firstPage.Records.Select(record => record.Key));
        Assert.Equal("api", firstPage.NextCursor);
        Assert.Equal(["api-token", "b", "my-api"], secondPage.Records.Select(record => record.Key));
        Assert.Equal("my-api", secondPage.NextCursor);
        RecordListResult allRecords = await client.ListRecordsAsync(renamedScope);
        Assert.Equal(8, allRecords.Records.Count);
        Assert.Null(allRecords.NextCursor);

        RecordSearchResult exact = await client.SearchRecordsAsync(new(
            [renamedScope],
            "api",
            ProtocolSearchMode.Exact,
            ProtocolCaseSensitivity.Sensitive));
        RecordSearchResult fuzzy = await client.SearchRecordsAsync(new(
            [renamedScope],
            "api",
            ProtocolSearchMode.Fuzzy,
            ProtocolCaseSensitivity.Sensitive));
        RecordSearchResult regex = await client.SearchRecordsAsync(new(
            [renamedScope],
            "(?<=api-)token$",
            ProtocolSearchMode.Regex,
            ProtocolCaseSensitivity.Sensitive));
        Assert.Equal("api", Assert.Single(exact.Records).Key);
        Assert.Equal("api", fuzzy.Records[0].Key);
        Assert.Contains(fuzzy.Records, record => string.Equals(record.Key, "my-api", StringComparison.Ordinal));
        Assert.Equal("api-token", Assert.Single(regex.Records).Key);

        AssertPlaintextAbsent(context.Paths.DatabaseFile, InitialSecret, ReplacementSecret);
        Assert.Equal(1, context.KeyProvider.StoreCount);
        Assert.Equal(32, context.KeyProvider.StoredKeyLength);
        await context.ShutdownAsync();
    }

    /// <summary>
    /// revision 与递归删除计数前置条件的冲突不会提交，并可在随后执行完整 record/scope 删除。
    /// / Revision and recursive-delete count conflicts do not commit, after which complete record/scope deletion succeeds.
    /// </summary>
    [Fact]
    public async Task RevisionAndCountConflictsRemainAtomicThroughRealHostAsync()
    {
        await using DaemonTestContext context = await DaemonTestContext.StartAsync();
        ScrapClient client = context.Client;
        const string scope = "concurrency";
        await client.CreateScopeAsync(scope);
        RecordSetResult first = await client.SetRecordAsync(scope, "first", "winner");
        RecordSetResult second = await client.SetRecordAsync(scope, "second", "survivor");

        RemoteProtocolException revisionConflict = await Assert.ThrowsAsync<RemoteProtocolException>(() =>
            client.SetRecordAsync(scope, "first", "loser", expectedRevision: first.Record.Revision + 99));
        Assert.Equal(ProtocolErrorCodes.Conflict, revisionConflict.ErrorCode);
        RecordDto unchanged = (await client.GetRecordAsync(scope, "first")).Record;
        Assert.Equal(("winner", first.Record.Revision), (unchanged.Value, unchanged.Revision));

        RemoteProtocolException nonRecursive = await Assert.ThrowsAsync<RemoteProtocolException>(() =>
            client.DeleteScopeAsync(scope));
        Assert.Equal(ProtocolErrorCodes.ScopeNotEmpty, nonRecursive.ErrorCode);
        RemoteProtocolException countConflict = await Assert.ThrowsAsync<RemoteProtocolException>(() =>
            client.DeleteScopeAsync(scope, recursive: true, expectedRecordCount: 1));
        Assert.Equal(ProtocolErrorCodes.Conflict, countConflict.ErrorCode);
        Assert.Equal(2, (await client.ListRecordsAsync(scope)).Records.Count);

        await client.DeleteRecordAsync(scope, "second", second.Record.Revision);
        RemoteProtocolException deletedRecord = await Assert.ThrowsAsync<RemoteProtocolException>(() =>
            client.GetRecordAsync(scope, "second"));
        Assert.Equal(ProtocolErrorCodes.RecordNotFound, deletedRecord.ErrorCode);
        ScopeDeleteResult deletedScope = await client.DeleteScopeAsync(
            scope,
            recursive: true,
            expectedRecordCount: 1);
        Assert.Equal(1, deletedScope.DeletedRecordCount);
        Assert.DoesNotContain(
            (await client.ListScopesAsync()).Scopes,
            candidate => string.Equals(candidate.Name, scope, StringComparison.Ordinal));

        await context.ShutdownAsync();
    }

    /// <summary>读取指定行的 AEAD 密文与 nonce。 / Reads the AEAD ciphertext and nonce for one row.</summary>
    private static CiphertextSnapshot ReadCiphertext(string databasePath, string scope, string key)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT records.value_ciphertext, records.nonce
            FROM records
            INNER JOIN scopes ON scopes.id = records.scope_id
            WHERE scopes.name = $scope AND records.key = $key;
            """;
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$key", key);
        using SqliteDataReader reader = command.ExecuteReader();
        Assert.True(reader.Read(), $"Missing persisted row for {scope}/{key}.");
        return new CiphertextSnapshot((byte[])reader[0], (byte[])reader[1]);
    }

    /// <summary>
    /// 验证身份变化生成新的 nonce 与密文。 / Verifies that an identity change produces a fresh nonce and ciphertext.
    /// </summary>
    private static void AssertFreshCiphertext(CiphertextSnapshot before, CiphertextSnapshot after)
    {
        Assert.NotEmpty(before.Ciphertext);
        Assert.Equal(12, before.Nonce.Length);
        Assert.NotEqual(before.Nonce, after.Nonce);
        Assert.NotEqual(before.Ciphertext, after.Ciphertext);
    }

    /// <summary>
    /// 扫描数据库、WAL 与 SHM，保证高熵 UTF-8 value 不以明文落盘。
    /// / Scans the database, WAL, and SHM to ensure high-entropy UTF-8 values never land as plaintext.
    /// </summary>
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
                Assert.True(
                    contents.AsSpan().IndexOf(Encoding.UTF8.GetBytes(plaintext)) < 0,
                    $"Plaintext value appeared in {Path.GetFileName(path)}.");
            }
        }
    }

    /// <summary>以与活跃 SQLite 连接兼容的共享模式读取文件。 / Reads a file with sharing compatible with active SQLite connections.</summary>
    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>持久化 AEAD 输出快照。 / Snapshot of persisted AEAD output.</summary>
    /// <param name="Ciphertext">密文。 / Ciphertext.</param>
    /// <param name="Nonce">随机 nonce。 / Random nonce.</param>
    private sealed record CiphertextSnapshot(byte[] Ciphertext, byte[] Nonce);

    /// <summary>
    /// 拥有一个隔离 profile、真实 host/client 连接和可注入密钥边界。
    /// / Owns an isolated profile, real host/client connection, and injectable key boundary.
    /// </summary>
    private sealed class DaemonTestContext : IAsyncDisposable
    {
        private readonly IHost host;
        private readonly DaemonInstanceLease lease;
        private readonly IMasterKeyProvider keyProvider;
        private readonly string rootDirectory;
        private readonly Task shutdownObservation;
        private int shutdownCompleted;

        private DaemonTestContext(
            string rootDirectory,
            ScrapPathLayout paths,
            IMasterKeyProvider keyProvider,
            DaemonInstanceLease lease,
            IHost host,
            ScrapClient client)
        {
            this.rootDirectory = rootDirectory;
            this.host = host;
            this.lease = lease;
            this.keyProvider = keyProvider;
            shutdownObservation = host.WaitForShutdownAsync();
            Paths = paths;
            Client = client;
        }

        /// <summary>隔离 profile 路径。 / Isolated profile paths.</summary>
        public ScrapPathLayout Paths { get; }

        /// <summary>内存 master-key provider。 / In-memory master-key provider.</summary>
        public MemoryMasterKeyProvider KeyProvider => (MemoryMasterKeyProvider)keyProvider;

        /// <summary>已完成版本协商的真实 client。 / Real client with completed version negotiation.</summary>
        public ScrapClient Client { get; }

        /// <summary>host 是否已停止。 / Whether the host has stopped.</summary>
        public bool HasStopped => shutdownObservation.IsCompleted;

        /// <summary>
        /// 在短临时路径启动 host 并连接相同 named-pipe endpoint。
        /// / Starts a host under a short temporary path and connects to the same named-pipe endpoint.
        /// </summary>
        public static async Task<DaemonTestContext> StartAsync(IMasterKeyProvider? keyProvider = null)
        {
            // macOS 常把 Path.GetTempPath() 展开到 /var/folders 下；/tmp 可使 UDS 路径低于字节上限。
            // macOS often expands Path.GetTempPath() below /var/folders; /tmp keeps the UDS path below its byte limit.
            string tempRoot = OperatingSystem.IsWindows() ? Path.GetTempPath() : "/tmp";
            string root = Path.Combine(tempRoot, $"scrap-e2e-{Guid.NewGuid():N}");
            var paths = new ScrapPathLayout(root);
            keyProvider ??= new MemoryMasterKeyProvider();
            DaemonInstanceLease? lease = null;
            IHost? host = null;
            ScrapClient? client = null;
            try
            {
                paths.Initialize();
                lease = DaemonInstanceLease.TryAcquire(paths)
                    ?? throw new InvalidOperationException("The unique integration-test profile lease was unexpectedly unavailable.");
                host = DaemonHost.Build([], paths, keyProvider);
                await host.StartAsync();
                IpcEndpointDescriptor endpoint = IpcEndpointDescriptor.Create(paths);
                client = await ScrapClient.ConnectAsync(
                    endpoint,
                    NoOpDaemonLauncher.Instance,
                    new ScrapClientOptions
                    {
                        InitialConnectTimeout = TimeSpan.FromSeconds(2),
                        StartupTimeout = TimeSpan.FromSeconds(5),
                    });
                return new DaemonTestContext(root, paths, keyProvider, lease, host, client);
            }
            catch
            {
                try
                {
                    if (client is not null)
                    {
                        await client.DisposeAsync();
                    }

                    if (host is not null)
                    {
                        await host.StopAsync();
                    }
                }
                finally
                {
                    host?.Dispose();
                    lease?.Dispose();
                    SqliteConnection.ClearAllPools();
                    DeleteDirectory(root);
                }

                throw;
            }
        }

        /// <summary>
        /// 经 typed client 请求优雅关闭，并观察 host 的真实 application lifetime。
        /// / Requests graceful shutdown through the typed client and observes the real host application lifetime.
        /// </summary>
        public async Task ShutdownAsync()
        {
            await Client.ShutdownAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await shutdownObservation.WaitAsync(timeout.Token);
            Volatile.Write(ref shutdownCompleted, 1);
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            try
            {
                await Client.DisposeAsync();
                if (Volatile.Read(ref shutdownCompleted) == 0)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await host.StopAsync(timeout.Token);
                }
            }
            finally
            {
                host.Dispose();
                lease.Dispose();
                SqliteConnection.ClearAllPools();
                DeleteDirectory(rootDirectory);
            }
        }

        /// <summary>在 Windows 延迟释放文件句柄时短暂重试临时目录清理。 / Briefly retries temporary-directory cleanup for delayed Windows handles.</summary>
        private static void DeleteDirectory(string path)
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (Directory.Exists(path))
                    {
                        Directory.Delete(path, recursive: true);
                    }

                    return;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(TimeSpan.FromMilliseconds(50 * (attempt + 1)));
                }
                catch (UnauthorizedAccessException) when (attempt < 4)
                {
                    Thread.Sleep(TimeSpan.FromMilliseconds(50 * (attempt + 1)));
                }
            }
        }
    }

    /// <summary>
    /// 连接重试时保持进程内边界，不允许测试意外启动真实 daemon。
    /// / Keeps retries inside the process and prevents a test from accidentally launching a real daemon.
    /// </summary>
    private sealed class NoOpDaemonLauncher : IDaemonProcessLauncher
    {
        /// <summary>共享无状态实例。 / Shared stateless instance.</summary>
        public static NoOpDaemonLauncher Instance { get; } = new();

        /// <inheritdoc />
        public void Start()
        {
            // Host 由 DaemonTestContext 拥有；连接重试绝不能启动外部进程。
            // Host ownership belongs to DaemonTestContext; connection retries must never spawn an external process.
        }
    }

    /// <summary>
    /// 复制输入/输出以模拟 provider 所有权边界，并仅暴露非敏感测试观测值。
    /// / Copies inputs/outputs to model provider ownership and exposes only non-sensitive test observations.
    /// </summary>
    private sealed class MemoryMasterKeyProvider : IMasterKeyProvider
    {
        private byte[]? key;

        /// <summary>成功存储次数。 / Number of successful stores.</summary>
        public int StoreCount { get; private set; }

        /// <summary>已保存 key 的字节长度，不公开 key material。 / Byte length of the stored key without exposing key material.</summary>
        public int StoredKeyLength => key?.Length ?? 0;

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
    }

    /// <summary>
    /// 对所有操作都报告平台密钥不可用的确定性 provider。
    /// / Deterministic provider that reports platform-key unavailability for every operation.
    /// </summary>
    private sealed class AlwaysFailingMasterKeyProvider : IMasterKeyProvider
    {
        /// <summary>共享无状态实例。 / Shared stateless instance.</summary>
        public static AlwaysFailingMasterKeyProvider Instance { get; } = new();

        /// <inheritdoc />
        public ValueTask<byte[]?> LoadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromException<byte[]?>(CreateException("load"));

        /// <inheritdoc />
        public ValueTask StoreAsync(ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(CreateException("store"));

        /// <summary>创建不包含敏感材料的稳定测试异常。 / Creates a stable test exception without sensitive material.</summary>
        private static MasterKeyProviderException CreateException(string operation) =>
            new("integration-test", operation, "The integration-test master-key provider is unavailable.");
    }
}
