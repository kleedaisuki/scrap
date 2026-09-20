using System.IO.Pipes;
using Scrap.Platform.Ipc;
using Scrap.Platform.Paths;
using Scrap.Platform.Processes;
using Scrap.Protocol;

namespace Scrap.Client;

/// <summary>
/// 提供共享的强类型 daemon IPC client；它不访问存储、密钥或领域实现。
/// / Provides the shared typed daemon IPC client; it never accesses storage, keys, or domain implementations.
/// </summary>
/// <remarks>
/// 每个物理连接先执行 <c>daemon.version</c> 协商。请求严格串行，避免 frame 交错；取消已开始的请求会丢弃 pipe，
/// 下一次调用会重新连接，但不会透明重试结果未知的原请求。
/// / Every physical connection starts with <c>daemon.version</c> negotiation. Requests are serialized to prevent frame
/// interleaving. Cancelling an in-flight request discards the pipe; the next call reconnects but never transparently retries
/// the original request whose outcome may be unknown.
/// <example>
/// <code>
/// await using ScrapClient client = await ScrapClient.ConnectAsync(cancellationToken: cancellationToken);
/// ScopeListResult scopes = await client.ListScopesAsync(cancellationToken);
/// </code>
/// </example>
/// </remarks>
public sealed class ScrapClient : IAsyncDisposable
{
    private readonly DaemonConnectionFactory _connectionFactory;
    private readonly ScrapClientOptions _options;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private NamedPipeClientStream? _stream;
    private int _disposed;

    private ScrapClient(
        DaemonConnectionFactory connectionFactory,
        ScrapClientOptions options,
        NamedPipeClientStream stream)
    {
        _connectionFactory = connectionFactory;
        _options = options;
        _stream = stream;
    }

    /// <summary>
    /// 使用当前用户的标准 profile 连接 daemon；连接失败时从应用目录或 <c>~/.scrap/bin</c> 启动 <c>scrapd</c>。
    /// / Connects to the daemon for the current user's standard profile; on connection failure, starts <c>scrapd</c>
    /// from the application directory or <c>~/.scrap/bin</c>.
    /// </summary>
    /// <param name="options">可选连接与启动策略。 / Optional connection and startup policy.</param>
    /// <param name="cancellationToken">取消 bootstrap 的标记。 / Token that cancels bootstrapping.</param>
    /// <returns>已连接并完成版本协商的 client。 / A connected client with version negotiation completed.</returns>
    /// <exception cref="ScrapConnectionException">无法初始化 profile、启动 daemon 或在时限内连接。 / The profile cannot be initialized, or the daemon cannot be started or reached in time.</exception>
    /// <exception cref="ScrapProtocolVersionException">client 与 daemon 版本不兼容。 / Client and daemon protocol versions are incompatible.</exception>
    public static async Task<ScrapClient> ConnectAsync(
        ScrapClientOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ScrapClientOptions validatedOptions = (options ?? new ScrapClientOptions()).Validate();

        try
        {
            ScrapPathLayout paths = ScrapPathLayout.ForCurrentUser();
            paths.Initialize();
            IpcEndpointDescriptor endpoint = IpcEndpointDescriptor.Create(paths);
            string executablePath = ResolveDaemonExecutablePath(paths, validatedOptions);
            var launcher = new DaemonProcessLauncher(executablePath);
            return await ConnectAsync(endpoint, launcher, validatedOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ScrapConnectionException("The local Scrap profile or IPC endpoint could not be initialized.", exception);
        }
    }

    /// <summary>
    /// 仅连接已运行的当前用户 daemon；未运行或正在关闭时返回 null，绝不启动进程或创建 profile 目录。
    /// / Connects only to an already-running daemon for the current user; returns null when absent or shutting down,
    /// and never launches a process or creates profile directories.
    /// </summary>
    /// <param name="options">可选的有界连接与 framing 策略。 / Optional bounded connection and framing policy.</param>
    /// <param name="cancellationToken">取消单次探测的标记。 / Token that cancels the single probe.</param>
    /// <returns>已连接 client；daemon 未运行时为 null。 / A connected client, or null when the daemon is not running.</returns>
    /// <remarks>
    /// 此入口适合 <c>shutdown --if-running</c> 等观察性操作。返回的 client 在连接随后丢失时仍不会启动 daemon。
    /// / This entry point suits observational operations such as <c>shutdown --if-running</c>. The returned client
    /// still never launches a daemon if its connection is subsequently lost.
    /// </remarks>
    public static async Task<ScrapClient?> TryConnectExistingAsync(
        ScrapClientOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ScrapClientOptions validatedOptions = (options ?? new ScrapClientOptions()).Validate();

        try
        {
            ScrapPathLayout paths = ScrapPathLayout.ForCurrentUser();
            IpcEndpointDescriptor endpoint = IpcEndpointDescriptor.Create(paths);
            return await TryConnectExistingAsync(endpoint, validatedOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ScrapConnectionException("The existing Scrap IPC endpoint could not be inspected.", exception);
        }
    }

    /// <summary>
    /// 仅探测显式 endpoint，不初始化其目录且永不启动进程。
    /// / Probes only an explicit endpoint, without initializing its directories or ever launching a process.
    /// </summary>
    /// <param name="endpoint">要探测的现有 endpoint。 / Existing endpoint to probe.</param>
    /// <param name="options">可选的有界连接与 framing 策略。 / Optional bounded connection and framing policy.</param>
    /// <param name="cancellationToken">取消单次探测的标记。 / Token that cancels the single probe.</param>
    /// <returns>已连接 client；daemon 未运行时为 null。 / A connected client, or null when the daemon is not running.</returns>
    public static async Task<ScrapClient?> TryConnectExistingAsync(
        IpcEndpointDescriptor endpoint,
        ScrapClientOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ScrapClientOptions validatedOptions = (options ?? new ScrapClientOptions()).Validate();
        var connectionFactory = new DaemonConnectionFactory(endpoint, validatedOptions);
        NamedPipeClientStream? stream = await connectionFactory.TryConnectExistingAsync(
            (candidate, token) => NegotiateVersionAsync(candidate, validatedOptions.MaxFrameSize, token),
            cancellationToken).ConfigureAwait(false);
        return stream is null ? null : new ScrapClient(connectionFactory, validatedOptions, stream);
    }

    /// <summary>
    /// 使用显式 endpoint 与 launcher 连接，便于自定义 profile 和集成测试。
    /// / Connects through an explicit endpoint and launcher, supporting custom profiles and integration tests.
    /// </summary>
    /// <param name="endpoint">共享给 client 与 daemon 的 endpoint。 / Endpoint shared by client and daemon.</param>
    /// <param name="launcher">首次连接失败及旧 owner 关闭竞争时按退避调用的 launcher。 / Launcher invoked with backoff after initial failure and during an old-owner shutdown race.</param>
    /// <param name="options">可选连接与启动策略。 / Optional connection and startup policy.</param>
    /// <param name="cancellationToken">取消 bootstrap 的标记。 / Token that cancels bootstrapping.</param>
    /// <returns>已连接并完成版本协商的 client。 / A connected client with version negotiation completed.</returns>
    public static async Task<ScrapClient> ConnectAsync(
        IpcEndpointDescriptor endpoint,
        IDaemonProcessLauncher launcher,
        ScrapClientOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(launcher);
        ScrapClientOptions validatedOptions = (options ?? new ScrapClientOptions()).Validate();
        endpoint.Paths.Initialize();

        var connectionFactory = new DaemonConnectionFactory(endpoint, launcher, validatedOptions);
        NamedPipeClientStream stream = await connectionFactory.ConnectAsync(
            (candidate, token) => NegotiateVersionAsync(candidate, validatedOptions.MaxFrameSize, token),
            cancellationToken).ConfigureAwait(false);
        return new ScrapClient(connectionFactory, validatedOptions, stream);
    }

    /// <summary>
    /// 自动遍历所有 keyset page 并列出全部 scope。 / Automatically traverses every keyset page and lists all scopes.
    /// </summary>
    /// <param name="cancellationToken">取消请求的标记。 / Token that cancels the request.</param>
    /// <returns>全部 scope，且 <see cref="ScopeListResult.NextCursor"/> 为 null。 / All scopes with a null <see cref="ScopeListResult.NextCursor"/>.</returns>
    public async Task<ScopeListResult> ListScopesAsync(CancellationToken cancellationToken = default)
    {
        List<ScopeDto> scopes = [];
        string? cursor = null;

        do
        {
            ScopeListResult page = await ListScopesAsync(
                cursor,
                ProtocolConstants.DefaultScopeListPageSize,
                cancellationToken).ConfigureAwait(false);
            scopes.AddRange(page.Scopes);

            if (page.NextCursor is not null &&
                (page.Scopes.Count == 0 ||
                 (cursor is not null && string.CompareOrdinal(page.NextCursor, cursor) <= 0)))
            {
                throw new ProtocolException(
                    ProtocolErrorCodes.InvalidJson,
                    "The daemon returned a non-progressing scope-list cursor.");
            }

            cursor = page.NextCursor;
        }
        while (cursor is not null);

        return new ScopeListResult(scopes);
    }

    /// <summary>
    /// 读取单个 ordinal keyset scope page。 / Reads one ordinal-keyset scope page.
    /// </summary>
    /// <param name="afterName">上一页的 <see cref="ScopeListResult.NextCursor"/>；首页为 null。 / Previous page's <see cref="ScopeListResult.NextCursor"/>; null for the first page.</param>
    /// <param name="limit">本页 scope 上限。 / Maximum scopes in this page.</param>
    /// <param name="cancellationToken">取消请求的标记。 / Token that cancels the request.</param>
    /// <returns>一页 scope 与可选的下一游标。 / One scope page and an optional next cursor.</returns>
    public Task<ScopeListResult> ListScopesAsync(
        string? afterName,
        int limit = ProtocolConstants.DefaultScopeListPageSize,
        CancellationToken cancellationToken = default) =>
        SendAsync<ScopeListParams, ScopeListResult>(
            ProtocolMethods.ScopeList,
            new(afterName, limit),
            cancellationToken);

    /// <summary>创建 scope。 / Creates a scope.</summary>
    /// <param name="name">精确且不隐式 trim 的名称。 / Exact name without implicit trimming.</param>
    /// <param name="cancellationToken">取消请求的标记。 / Token that cancels the request.</param>
    /// <returns>创建结果。 / The creation result.</returns>
    public Task<ScopeCreateResult> CreateScopeAsync(string name, CancellationToken cancellationToken = default) =>
        SendAsync<ScopeCreateParams, ScopeCreateResult>(ProtocolMethods.ScopeCreate, new(name), cancellationToken);

    /// <summary>原子重命名 scope。 / Atomically renames a scope.</summary>
    /// <param name="oldName">当前精确名称。 / Current exact name.</param>
    /// <param name="newName">目标精确名称。 / Target exact name.</param>
    /// <param name="cancellationToken">取消请求的标记。 / Token that cancels the request.</param>
    /// <returns>重命名结果。 / The rename result.</returns>
    public Task<ScopeRenameResult> RenameScopeAsync(
        string oldName,
        string newName,
        CancellationToken cancellationToken = default) =>
        SendAsync<ScopeRenameParams, ScopeRenameResult>(
            ProtocolMethods.ScopeRename,
            new(oldName, newName),
            cancellationToken);

    /// <summary>删除 scope。 / Deletes a scope.</summary>
    /// <param name="name">精确 scope 名称。 / Exact scope name.</param>
    /// <param name="recursive">是否明确允许级联删除 record。 / Whether cascading record deletion is explicitly allowed.</param>
    /// <param name="expectedRecordCount">递归删除的可选事务前置条件。 / Optional transactional precondition for recursive deletion.</param>
    /// <param name="cancellationToken">取消请求的标记。 / Token that cancels the request.</param>
    /// <returns>删除结果及 record 数量。 / The deletion result and record count.</returns>
    public Task<ScopeDeleteResult> DeleteScopeAsync(
        string name,
        bool recursive = false,
        int? expectedRecordCount = null,
        CancellationToken cancellationToken = default) =>
        SendAsync<ScopeDeleteParams, ScopeDeleteResult>(
            ProtocolMethods.ScopeDelete,
            new(name, recursive, expectedRecordCount),
            cancellationToken);

    /// <summary>
    /// 使用旧的位置参数顺序删除 scope。 / Deletes a scope using the original positional-argument order.
    /// </summary>
    /// <param name="name">精确 scope 名称。 / Exact scope name.</param>
    /// <param name="recursive">是否明确允许级联删除 record。 / Whether cascading record deletion is explicitly allowed.</param>
    /// <param name="cancellationToken">取消请求的标记。 / Token that cancels the request.</param>
    /// <returns>删除结果及 record 数量。 / The deletion result and record count.</returns>
    public Task<ScopeDeleteResult> DeleteScopeAsync(
        string name,
        bool recursive,
        CancellationToken cancellationToken) =>
        DeleteScopeAsync(name, recursive, expectedRecordCount: null, cancellationToken);

    /// <summary>精确读取包含 value 的 record。 / Gets an exact record including its value.</summary>
    /// <param name="scope">精确 scope 名称。 / Exact scope name.</param>
    /// <param name="key">大小写敏感的精确 key。 / Exact case-sensitive key.</param>
    /// <param name="cancellationToken">取消请求的标记。 / Token that cancels the request.</param>
    /// <returns>包含 value 的 record。 / The record including its value.</returns>
    public Task<RecordGetResult> GetRecordAsync(
        string scope,
        string key,
        CancellationToken cancellationToken = default) =>
        SendAsync<RecordGetParams, RecordGetResult>(ProtocolMethods.RecordGet, new(scope, key), cancellationToken);

    /// <summary>使用旧标量 API 新增 record，或只替换现有 record 的索引 0。 / Creates a record through the legacy scalar API, or replaces only index zero of an existing record.</summary>
    /// <param name="scope">精确 scope 名称。 / Exact scope name.</param>
    /// <param name="key">大小写敏感的精确 key。 / Exact case-sensitive key.</param>
    /// <param name="value">UTF-8 文本 value；client 不记录它。 / UTF-8 text value, which the client never logs.</param>
    /// <param name="presentation">展示策略。 / Presentation policy.</param>
    /// <param name="expectedRevision">可选的 revision 乐观并发前置条件。 / Optional revision-based optimistic-concurrency precondition.</param>
    /// <param name="cancellationToken">取消请求的标记。 / Token that cancels the request.</param>
    /// <returns>不回显 value 的写入结果。 / A write result that does not echo the value.</returns>
    public Task<RecordSetResult> SetRecordAsync(
        string scope,
        string key,
        string value,
        RecordPresentation presentation = RecordPresentation.Masked,
        long? expectedRevision = null,
        CancellationToken cancellationToken = default) =>
        SendAsync<RecordSetParams, RecordSetResult>(
            ProtocolMethods.RecordSet,
            new(scope, key, value, presentation, expectedRevision),
            cancellationToken);

    /// <summary>新增或原子替换 record 的完整有序 value 列表。 / Creates or atomically replaces a record's complete ordered value list.</summary>
    /// <param name="scope">精确 scope 名称。 / Exact scope name.</param>
    /// <param name="key">大小写敏感的精确 key。 / Exact case-sensitive key.</param>
    /// <param name="values">非空有序列表；保留重复项与空字符串。 / Non-empty ordered list; duplicates and empty strings are preserved.</param>
    /// <param name="presentation">应用于所有 value 的展示策略。 / Presentation policy applied to every value.</param>
    /// <param name="expectedRevision">完整列表写入的可选 revision 前置条件。 / Optional revision precondition for the whole-list write.</param>
    /// <param name="cancellationToken">取消请求的标记。 / Token that cancels the request.</param>
    /// <returns>不回显 value 的写入结果。 / A write result that does not echo values.</returns>
    public Task<RecordSetResult> SetRecordAsync(
        string scope,
        string key,
        IReadOnlyList<string> values,
        RecordPresentation presentation = RecordPresentation.Masked,
        long? expectedRevision = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        return SendAsync<RecordSetParams, RecordSetResult>(
            ProtocolMethods.RecordSet,
            new RecordSetParams(scope, key, null, presentation, expectedRevision) { Values = values },
            cancellationToken);
    }

    /// <summary>在 daemon 内原子重命名并重新加密 record。 / Atomically renames and re-encrypts a record in the daemon.</summary>
    /// <param name="scope">精确 scope 名称。 / Exact scope name.</param>
    /// <param name="key">当前精确 key。 / Current exact key.</param>
    /// <param name="newKey">目标精确 key。 / Target exact key.</param>
    /// <param name="expectedRevision">可选的 revision 乐观并发前置条件。 / Optional revision-based optimistic-concurrency precondition.</param>
    /// <param name="cancellationToken">取消请求的标记。 / Token that cancels the request.</param>
    /// <returns>重命名结果。 / The rename result.</returns>
    public Task<RecordRenameResult> RenameRecordAsync(
        string scope,
        string key,
        string newKey,
        long? expectedRevision = null,
        CancellationToken cancellationToken = default) =>
        SendAsync<RecordRenameParams, RecordRenameResult>(
            ProtocolMethods.RecordRename,
            new(scope, key, newKey, expectedRevision),
            cancellationToken);

    /// <summary>删除精确 record。 / Deletes an exact record.</summary>
    /// <param name="scope">精确 scope 名称。 / Exact scope name.</param>
    /// <param name="key">大小写敏感的精确 key。 / Exact case-sensitive key.</param>
    /// <param name="expectedRevision">可选的 revision 乐观并发前置条件。 / Optional revision-based optimistic-concurrency precondition.</param>
    /// <param name="cancellationToken">取消请求的标记。 / Token that cancels the request.</param>
    /// <returns>删除结果。 / The deletion result.</returns>
    public Task<RecordDeleteResult> DeleteRecordAsync(
        string scope,
        string key,
        long? expectedRevision = null,
        CancellationToken cancellationToken = default) =>
        SendAsync<RecordDeleteParams, RecordDeleteResult>(
            ProtocolMethods.RecordDelete,
            new(scope, key, expectedRevision),
            cancellationToken);

    /// <summary>
    /// 自动遍历所有 keyset page，列出 scope 内不含 value 的全部 record 元数据。
    /// / Automatically traverses every keyset page and lists all value-free record metadata in a scope.
    /// </summary>
    /// <param name="scope">精确 scope 名称。 / Exact scope name.</param>
    /// <param name="cancellationToken">取消请求的标记。 / Token that cancels the request.</param>
    /// <returns>所有 record 元数据，且 <see cref="RecordListResult.NextCursor"/> 为 null。 / All record metadata with a null <see cref="RecordListResult.NextCursor"/>.</returns>
    public async Task<RecordListResult> ListRecordsAsync(
        string scope,
        CancellationToken cancellationToken = default)
    {
        List<RecordSummaryDto> records = [];
        string? cursor = null;

        do
        {
            RecordListResult page = await ListRecordsAsync(
                scope,
                cursor,
                ProtocolConstants.DefaultRecordListPageSize,
                cancellationToken).ConfigureAwait(false);
            records.AddRange(page.Records);

            if (page.NextCursor is not null &&
                (page.Records.Count == 0 ||
                 (cursor is not null && string.CompareOrdinal(page.NextCursor, cursor) <= 0)))
            {
                throw new ProtocolException(
                    ProtocolErrorCodes.InvalidJson,
                    "The daemon returned a non-progressing record-list cursor.");
            }

            cursor = page.NextCursor;
        }
        while (cursor is not null);

        return new RecordListResult(records);
    }

    /// <summary>
    /// 读取 scope 内单个 ordinal keyset page，供流式 UI 或大规模调用方控制内存。
    /// / Reads one ordinal-keyset page from a scope, allowing streaming UIs or large callers to control memory.
    /// </summary>
    /// <param name="scope">精确 scope 名称。 / Exact scope name.</param>
    /// <param name="afterKey">上一页的 <see cref="RecordListResult.NextCursor"/>；首页为 null。 / Previous page's <see cref="RecordListResult.NextCursor"/>; null for the first page.</param>
    /// <param name="limit">本页 record 上限。 / Maximum records in this page.</param>
    /// <param name="cancellationToken">取消请求的标记。 / Token that cancels the request.</param>
    /// <returns>一页 record 元数据与可选的下一游标。 / One metadata page and an optional next cursor.</returns>
    public Task<RecordListResult> ListRecordsAsync(
        string scope,
        string? afterKey,
        int limit = ProtocolConstants.DefaultRecordListPageSize,
        CancellationToken cancellationToken = default) =>
        SendAsync<RecordListParams, RecordListResult>(
            ProtocolMethods.RecordList,
            new(scope, afterKey, limit),
            cancellationToken);

    /// <summary>由 daemon 搜索 record key，结果永不包含 value。 / Searches record keys in the daemon; results never contain values.</summary>
    /// <param name="request">完整搜索意图。 / Complete search intent.</param>
    /// <param name="cancellationToken">取消可过时查询的标记。 / Token that cancels a stale query.</param>
    /// <returns>daemon 排序后的候选。 / Candidates ranked by the daemon.</returns>
    public Task<RecordSearchResult> SearchRecordsAsync(
        SearchRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync<SearchRequest, RecordSearchResult>(ProtocolMethods.RecordSearch, request, cancellationToken);

    /// <summary>探测 daemon 活性。 / Probes daemon liveness.</summary>
    /// <param name="cancellationToken">取消请求的标记。 / Token that cancels the request.</param>
    /// <returns>合法响应即表示存活。 / A result whose valid receipt proves liveness.</returns>
    public Task<DaemonPingResult> PingAsync(CancellationToken cancellationToken = default) =>
        SendAsync<DaemonPingParams, DaemonPingResult>(ProtocolMethods.DaemonPing, new(), cancellationToken);

    /// <summary>读取 daemon 应用与协议版本。 / Gets daemon application and protocol versions.</summary>
    /// <param name="cancellationToken">取消请求的标记。 / Token that cancels the request.</param>
    /// <returns>daemon 版本信息。 / Daemon version information.</returns>
    public Task<DaemonVersionResult> GetDaemonVersionAsync(CancellationToken cancellationToken = default) =>
        SendAsync<DaemonVersionParams, DaemonVersionResult>(ProtocolMethods.DaemonVersion, new(), cancellationToken);

    /// <summary>请求 daemon 优雅关闭；成功后当前物理连接不再复用。 / Requests graceful daemon shutdown; the physical connection is not reused after success.</summary>
    /// <param name="cancellationToken">取消请求的标记。 / Token that cancels the request.</param>
    /// <returns>daemon 接受关闭的结果。 / The daemon's shutdown acceptance.</returns>
    public async Task<DaemonShutdownResult> ShutdownAsync(CancellationToken cancellationToken = default)
    {
        DaemonShutdownResult result = await SendAsync<DaemonShutdownParams, DaemonShutdownResult>(
            ProtocolMethods.DaemonShutdown,
            new(),
            cancellationToken).ConfigureAwait(false);

        await _requestGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await DropConnectionAsync().ConfigureAwait(false);
        }
        finally
        {
            _requestGate.Release();
        }

        return result;
    }

    /// <summary>关闭 pipe 并释放 client。 / Closes the pipe and releases the client.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lifetimeCancellation.CancelAsync().ConfigureAwait(false);
        await _requestGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await DropConnectionAsync().ConfigureAwait(false);
        }
        finally
        {
            _requestGate.Release();
            _lifetimeCancellation.Dispose();
        }
    }

    private async Task<TResult> SendAsync<TParams, TResult>(
        string method,
        TParams parameters,
        CancellationToken cancellationToken)
        where TParams : notnull
        where TResult : notnull
    {
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeCancellation.Token);
            CancellationToken requestToken = requestCancellation.Token;

            try
            {
                _stream ??= await OpenNegotiatedConnectionAsync(requestToken).ConfigureAwait(false);
                return await ExchangeAsync<TParams, TResult>(
                    _stream,
                    method,
                    parameters,
                    _options.MaxFrameSize,
                    requestToken).ConfigureAwait(false);
            }
            catch (RemoteProtocolException)
            {
                // A structured remote error is a complete, aligned response; the pipe remains reusable.
                throw;
            }
            catch (OperationCanceledException) when (
                _lifetimeCancellation.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested)
            {
                await DropConnectionAsync().ConfigureAwait(false);
                throw new ObjectDisposedException(nameof(ScrapClient));
            }
            catch (OperationCanceledException)
            {
                await DropConnectionAsync().ConfigureAwait(false);
                throw;
            }
            catch (Exception exception) when (exception is IOException or ProtocolException)
            {
                await DropConnectionAsync().ConfigureAwait(false);
                if (exception is IOException)
                {
                    throw new ScrapConnectionException(
                        "The daemon IPC request failed; a mutation outcome may be unknown.",
                        exception);
                }

                throw;
            }
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private async ValueTask<NamedPipeClientStream> OpenNegotiatedConnectionAsync(CancellationToken cancellationToken)
        => await _connectionFactory.ConnectAsync(
            (candidate, token) => NegotiateVersionAsync(candidate, _options.MaxFrameSize, token),
            cancellationToken).ConfigureAwait(false);

    private static async ValueTask NegotiateVersionAsync(
        NamedPipeClientStream stream,
        int maxFrameSize,
        CancellationToken cancellationToken)
    {
        DaemonVersionResult version;
        try
        {
            version = await ExchangeAsync<DaemonVersionParams, DaemonVersionResult>(
                stream,
                ProtocolMethods.DaemonVersion,
                new(),
                maxFrameSize,
                cancellationToken).ConfigureAwait(false);
        }
        catch (ProtocolException exception) when (
            exception.ErrorCode == ProtocolErrorCodes.ProtocolVersionUnsupported)
        {
            await ReplaceLegacyDaemonAsync(stream, maxFrameSize, cancellationToken).ConfigureAwait(false);
            throw new IOException("A legacy daemon was stopped and will be replaced.", exception);
        }

        if (version.MinProtocolVersion <= 0 || version.MaxProtocolVersion < version.MinProtocolVersion)
        {
            throw new ProtocolException(
                ProtocolErrorCodes.InvalidJson,
                "The daemon returned an invalid supported protocol-version range.");
        }

        if (ProtocolConstants.CurrentVersion < version.MinProtocolVersion ||
            ProtocolConstants.CurrentVersion > version.MaxProtocolVersion)
        {
            throw new ScrapProtocolVersionException(
                ProtocolConstants.CurrentVersion,
                version.MinProtocolVersion,
                version.MaxProtocolVersion);
        }
    }

    private static async ValueTask ReplaceLegacyDaemonAsync(
        Stream stream,
        int maxFrameSize,
        CancellationToken cancellationToken)
    {
        DaemonVersionResult legacy = await ExchangeAsync<DaemonVersionParams, DaemonVersionResult>(
            stream,
            ProtocolMethods.DaemonVersion,
            new(),
            maxFrameSize,
            cancellationToken,
            ProtocolConstants.MinimumSupportedVersion).ConfigureAwait(false);
        if (legacy.MinProtocolVersion > ProtocolConstants.MinimumSupportedVersion ||
            legacy.MaxProtocolVersion < ProtocolConstants.MinimumSupportedVersion)
        {
            throw new ScrapProtocolVersionException(
                ProtocolConstants.CurrentVersion,
                legacy.MinProtocolVersion,
                legacy.MaxProtocolVersion);
        }

        _ = await ExchangeAsync<DaemonShutdownParams, DaemonShutdownResult>(
            stream,
            ProtocolMethods.DaemonShutdown,
            new(),
            maxFrameSize,
            cancellationToken,
            ProtocolConstants.MinimumSupportedVersion).ConfigureAwait(false);
    }

    private static async ValueTask<TResult> ExchangeAsync<TParams, TResult>(
        Stream stream,
        string method,
        TParams parameters,
        int maxFrameSize,
        CancellationToken cancellationToken,
        int protocolVersion = ProtocolConstants.CurrentVersion)
        where TParams : notnull
        where TResult : notnull
    {
        string requestId = Guid.CreateVersion7().ToString("N");
        ProtocolRequest request = ProtocolRequest.Create(requestId, method, parameters, protocolVersion);
        await LengthPrefixedJsonFraming.WriteAsync(
            stream,
            request,
            maxFrameSize,
            cancellationToken).ConfigureAwait(false);

        ProtocolResponse response = await LengthPrefixedJsonFraming.ReadAsync<ProtocolResponse>(
            stream,
            maxFrameSize,
            cancellationToken).ConfigureAwait(false);
        response.EnsureValid();

        if (!string.Equals(response.RequestId, requestId, StringComparison.Ordinal))
        {
            throw new ProtocolException(
                ProtocolErrorCodes.InvalidRequest,
                "The daemon response request ID does not match the request.");
        }

        if (response.ProtocolVersion != protocolVersion)
        {
            throw new ProtocolException(
                ProtocolErrorCodes.ProtocolVersionUnsupported,
                "The daemon response protocol version does not match the negotiated version.");
        }

        return response.GetResult<TResult>();
    }

    private async ValueTask DropConnectionAsync()
    {
        NamedPipeClientStream? stream = _stream;
        _stream = null;
        if (stream is not null)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static string ResolveDaemonExecutablePath(ScrapPathLayout paths, ScrapClientOptions options)
    {
        if (options.DaemonExecutablePath is not null)
        {
            return Path.GetFullPath(options.DaemonExecutablePath);
        }

        string executableName = OperatingSystem.IsWindows() ? "scrapd.exe" : "scrapd";
        string applicationCandidate = Path.Combine(AppContext.BaseDirectory, executableName);
        if (File.Exists(applicationCandidate))
        {
            return applicationCandidate;
        }

        return Path.Combine(paths.BinDirectory, executableName);
    }
}
