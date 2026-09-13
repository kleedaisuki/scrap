using System.Diagnostics;
using System.IO.Pipes;
using Scrap.Platform.Ipc;
using Scrap.Platform.Processes;
using Scrap.Protocol;

namespace Scrap.Client;

/// <summary>
/// 建立当前用户的 daemon pipe，并在首次连接失败后按需启动，必要时节流重启。
/// / Opens the current user's daemon pipe, launching on demand and throttling relaunches when necessary.
/// </summary>
internal sealed class DaemonConnectionFactory
{
    private static readonly TimeSpan InitialRelaunchDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaximumRelaunchDelay = TimeSpan.FromSeconds(2);

    private readonly IpcEndpointDescriptor _endpoint;
    private readonly IDaemonProcessLauncher? _launcher;
    private readonly ScrapClientOptions _options;

    /// <summary>
    /// 初始化连接工厂。 / Initializes the connection factory.
    /// </summary>
    /// <param name="endpoint">当前用户 IPC endpoint。 / The current user's IPC endpoint.</param>
    /// <param name="launcher">daemon 启动器。 / The daemon launcher.</param>
    /// <param name="options">已验证的连接选项。 / Validated connection options.</param>
    public DaemonConnectionFactory(
        IpcEndpointDescriptor endpoint,
        IDaemonProcessLauncher launcher,
        ScrapClientOptions options)
        : this(endpoint, options)
    {
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
    }

    /// <summary>
    /// 初始化永不启动进程的 existing-only 连接工厂。 / Initializes an existing-only connection factory that never launches a process.
    /// </summary>
    /// <param name="endpoint">当前用户 IPC endpoint。 / The current user's IPC endpoint.</param>
    /// <param name="options">已验证的连接选项。 / Validated connection options.</param>
    public DaemonConnectionFactory(IpcEndpointDescriptor endpoint, ScrapClientOptions options)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// 对现有 daemon 执行一次有界的连接与 readiness 探测，绝不启动进程。
    /// / Performs one bounded connection and readiness probe against an existing daemon and never launches a process.
    /// </summary>
    /// <param name="readinessProbe">在返回前验证协议就绪状态的回调。 / Callback that verifies protocol readiness before returning.</param>
    /// <param name="cancellationToken">取消探测的标记。 / Token that cancels the probe.</param>
    /// <returns>daemon 就绪时返回连接，否则返回 null。 / A connection when the daemon is ready; otherwise null.</returns>
    public async ValueTask<NamedPipeClientStream?> TryConnectExistingAsync(
        Func<NamedPipeClientStream, CancellationToken, ValueTask> readinessProbe,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readinessProbe);
        try
        {
            return await ConnectOnceAsync(
                readinessProbe,
                _options.InitialConnectTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRetryableConnectionFailure(exception))
        {
            return null;
        }
    }

    /// <summary>
    /// 连接 daemon；首次快速探测失败后启动进程，并在旧 owner 关闭竞争期间按退避节流重新启动。
    /// / Connects to the daemon; launches after the fast initial probe fails and throttles relaunches during an old-owner shutdown race.
    /// </summary>
    /// <param name="readinessProbe">在返回前验证协议就绪状态的回调。 / Callback that verifies protocol readiness before returning.</param>
    /// <param name="cancellationToken">取消整个 bootstrap 的标记。 / Token that cancels the complete bootstrap.</param>
    /// <returns>已连接且由调用方拥有的 pipe。 / A connected pipe owned by the caller.</returns>
    public async ValueTask<NamedPipeClientStream> ConnectAsync(
        Func<NamedPipeClientStream, CancellationToken, ValueTask> readinessProbe,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readinessProbe);
        Exception? lastFailure;
        try
        {
            return await ConnectOnceAsync(
                readinessProbe,
                _options.InitialConnectTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRetryableConnectionFailure(exception))
        {
            lastFailure = exception;
        }

        if (_launcher is null)
        {
            throw new ScrapConnectionException("The local scrap daemon is not running.", lastFailure);
        }

        long startedAt = Stopwatch.GetTimestamp();
        TimeSpan delay = _options.InitialRetryDelay;
        TimeSpan relaunchDelay = InitialRelaunchDelay;
        TimeSpan nextRelaunchAt = relaunchDelay;
        StartDaemon();

        while (Stopwatch.GetElapsedTime(startedAt) < _options.StartupTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan remaining = _options.StartupTimeout - Stopwatch.GetElapsedTime(startedAt);
            TimeSpan attemptTimeout = Min(_options.InitialConnectTimeout, remaining);

            try
            {
                return await ConnectOnceAsync(readinessProbe, attemptTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsRetryableConnectionFailure(exception))
            {
                lastFailure = exception;
            }

            remaining = _options.StartupTimeout - Stopwatch.GetElapsedTime(startedAt);
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            TimeSpan elapsed = Stopwatch.GetElapsedTime(startedAt);
            if (elapsed >= nextRelaunchAt)
            {
                StartDaemon();
                relaunchDelay = Min(relaunchDelay * 2, MaximumRelaunchDelay);
                nextRelaunchAt = elapsed + relaunchDelay;
            }

            await Task.Delay(Min(delay, remaining), cancellationToken).ConfigureAwait(false);
            delay = Min(delay * 2, _options.MaxRetryDelay);
        }

        throw new ScrapConnectionException(
            "The local scrap daemon did not become ready before the startup timeout.",
            lastFailure);
    }

    private static bool IsRetryableConnectionFailure(Exception exception) =>
        exception is IOException or TimeoutException ||
        exception is RemoteProtocolException { ErrorCode: ProtocolErrorCodes.DaemonShuttingDown };

    /// <summary>
    /// 启动候选 daemon；单实例 lease 负责把并发 loser 变为正常退出。
    /// / Starts a daemon candidate; the single-instance lease turns concurrent losers into normal exits.
    /// </summary>
    private void StartDaemon()
    {
        Debug.Assert(_launcher is not null, "Existing-only connection factories must never launch a daemon.");
        try
        {
            _launcher!.Start();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new ScrapConnectionException("The local scrap daemon could not be started.", exception);
        }
    }

    private async ValueTask<NamedPipeClientStream> ConnectOnceAsync(
        Func<NamedPipeClientStream, CancellationToken, ValueTask> readinessProbe,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attemptCancellation.CancelAfter(timeout);
        NamedPipeClientStream stream = _endpoint.CreateClientStream();
        try
        {
            await stream.ConnectAsync(timeout, attemptCancellation.Token).ConfigureAwait(false);
            await readinessProbe(stream, attemptCancellation.Token).ConfigureAwait(false);
            return stream;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw new TimeoutException("The daemon readiness probe timed out.", exception);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;
}
