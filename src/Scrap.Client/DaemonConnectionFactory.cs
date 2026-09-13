using System.Diagnostics;
using System.IO.Pipes;
using Scrap.Platform.Ipc;
using Scrap.Platform.Processes;

namespace Scrap.Client;

/// <summary>
/// 建立当前用户的 daemon pipe，并在首次连接失败后执行一次按需启动。
/// / Opens the current user's daemon pipe and performs one on-demand launch after the initial connection fails.
/// </summary>
internal sealed class DaemonConnectionFactory
{
    private readonly IpcEndpointDescriptor _endpoint;
    private readonly IDaemonProcessLauncher _launcher;
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
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// 连接 daemon；只在首次快速探测失败后启动一次进程，之后等待 owner 就绪。
    /// / Connects to the daemon; launches once only after the fast initial probe fails, then waits for the owner to become ready.
    /// </summary>
    /// <param name="cancellationToken">取消整个 bootstrap 的标记。 / Token that cancels the complete bootstrap.</param>
    /// <returns>已连接且由调用方拥有的 pipe。 / A connected pipe owned by the caller.</returns>
    public async ValueTask<NamedPipeClientStream> ConnectAsync(CancellationToken cancellationToken)
    {
        Exception? lastFailure;
        try
        {
            return await ConnectOnceAsync(_options.InitialConnectTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRetryableConnectionFailure(exception))
        {
            lastFailure = exception;
        }

        try
        {
            _launcher.Start();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new ScrapConnectionException("The local scrap daemon could not be started.", exception);
        }

        long startedAt = Stopwatch.GetTimestamp();
        TimeSpan delay = _options.InitialRetryDelay;

        while (Stopwatch.GetElapsedTime(startedAt) < _options.StartupTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan remaining = _options.StartupTimeout - Stopwatch.GetElapsedTime(startedAt);
            TimeSpan attemptTimeout = Min(_options.InitialConnectTimeout, remaining);

            try
            {
                return await ConnectOnceAsync(attemptTimeout, cancellationToken).ConfigureAwait(false);
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

            await Task.Delay(Min(delay, remaining), cancellationToken).ConfigureAwait(false);
            delay = Min(delay * 2, _options.MaxRetryDelay);
        }

        throw new ScrapConnectionException(
            "The local scrap daemon did not become ready before the startup timeout.",
            lastFailure);
    }

    private static bool IsRetryableConnectionFailure(Exception exception) =>
        exception is IOException or TimeoutException;

    private async ValueTask<NamedPipeClientStream> ConnectOnceAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        NamedPipeClientStream stream = _endpoint.CreateClientStream();
        try
        {
            await stream.ConnectAsync(timeout, cancellationToken).ConfigureAwait(false);
            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;
}
