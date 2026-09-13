using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scrap.Protocol;

namespace Scrap.Daemon;

/// <summary>
/// 在一个持久 IPC 连接上顺序执行 request-response，并要求第一帧协商版本。
/// / Executes sequential request-response exchanges on one persistent IPC connection and requires version negotiation first.
/// </summary>
internal sealed class DaemonConnectionProcessor
{
    private static readonly Action<ILogger, string, Exception?> LogFramingError =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1101, "IpcFramingError"),
            "Closing IPC connection after framing error {ErrorCode}; payload was not logged.");

    private static readonly Action<ILogger, string, Exception?> LogIncompleteResponse =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(1102, "IpcIncompleteResponse"),
            "IPC client disconnected before response completion ({ExceptionType}); payload was not logged.");

    private static readonly Action<ILogger, Exception?> LogResponseTimeout = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(1103, "IpcResponseTimeout"),
        "Closing IPC connection after response write timeout; payload was not logged.");

    private readonly DaemonRequestDispatcher dispatcher;
    private readonly DaemonActivityTracker activity;
    private readonly DaemonRuntimeState state;
    private readonly IHostApplicationLifetime lifetime;
    private readonly ILogger<DaemonConnectionProcessor> logger;
    private readonly TimeSpan responseWriteTimeout;

    /// <summary>
    /// 初始化连接处理器。 / Initializes the connection processor.
    /// </summary>
    public DaemonConnectionProcessor(
        DaemonRequestDispatcher dispatcher,
        DaemonActivityTracker activity,
        DaemonRuntimeState state,
        IHostApplicationLifetime lifetime,
        ILogger<DaemonConnectionProcessor> logger)
        : this(dispatcher, activity, state, lifetime, logger, TimeSpan.FromSeconds(5))
    {
    }

    /// <summary>
    /// 使用显式 daemon 选项初始化连接处理器。 / Initializes the processor with explicit daemon options.
    /// </summary>
    public DaemonConnectionProcessor(
        DaemonRequestDispatcher dispatcher,
        DaemonActivityTracker activity,
        DaemonRuntimeState state,
        IHostApplicationLifetime lifetime,
        ILogger<DaemonConnectionProcessor> logger,
        IOptions<DaemonOptions> options)
        : this(dispatcher, activity, state, lifetime, logger, options.Value.Validate().ResponseWriteTimeout)
    {
    }

    private DaemonConnectionProcessor(
        DaemonRequestDispatcher dispatcher,
        DaemonActivityTracker activity,
        DaemonRuntimeState state,
        IHostApplicationLifetime lifetime,
        ILogger<DaemonConnectionProcessor> logger,
        TimeSpan responseWriteTimeout)
    {
        this.dispatcher = dispatcher;
        this.activity = activity;
        this.state = state;
        this.lifetime = lifetime;
        this.logger = logger;
        this.responseWriteTimeout = responseWriteTimeout;
    }

    /// <summary>
    /// 处理连接直到 client 断开、frame 非法或 daemon 停止。 / Processes a connection until disconnect, invalid framing, or daemon shutdown.
    /// </summary>
    /// <param name="stream">已接纳的双向 stream。 / Accepted duplex stream.</param>
    /// <param name="connectionLease">已由 server 原子取得的活动租约。 / Activity lease atomically acquired by the server.</param>
    /// <param name="stoppingToken">停止未在处理请求的连接读取。 / Stops reads on connections not currently handling a request.</param>
    public async Task ProcessAsync(Stream stream, IDisposable connectionLease, CancellationToken stoppingToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(connectionLease);
        using (connectionLease)
        {
            bool negotiated = false;

            while (!state.IsStopping)
            {
                ProtocolRequest request;
                try
                {
                    request = await LengthPrefixedJsonFraming.ReadAsync<ProtocolRequest>(
                        stream,
                        cancellationToken: stoppingToken).ConfigureAwait(false);
                }
                catch (EndOfStreamException)
                {
                    return;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (ProtocolException exception)
                {
                    LogFramingError(logger, exception.ErrorCode, null);
                    return;
                }

                using IDisposable requestLease = activity.BeginRequest();
                DaemonDispatchResult dispatch;
                if (!negotiated && request.Method != ProtocolMethods.DaemonVersion)
                {
                    dispatch = new(ProtocolResponse.Failure(
                        string.IsNullOrEmpty(request.RequestId) ? "invalid" : request.RequestId,
                        new ProtocolError(
                            ProtocolErrorCodes.ProtocolVersionUnsupported,
                            "daemon.version must be the first request on a connection.")));
                }
                else
                {
                    dispatch = await dispatcher.DispatchAsync(request, stoppingToken).ConfigureAwait(false);
                    negotiated |= request.Method == ProtocolMethods.DaemonVersion && dispatch.Response.Error is null;
                }

                if (dispatch.RequestsShutdown)
                {
                    activity.BeginStopping(state);
                }

                try
                {
                    // An already-dispatched mutation has a definite result. Attempt its response even during host stop.
                    using var writeTimeout = new CancellationTokenSource(responseWriteTimeout);
                    await LengthPrefixedJsonFraming.WriteAsync(
                        stream,
                        dispatch.Response,
                        cancellationToken: writeTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    LogResponseTimeout(logger, null);
                    if (dispatch.RequestsShutdown)
                    {
                        lifetime.StopApplication();
                    }

                    return;
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException)
                {
                    LogIncompleteResponse(logger, exception.GetType().Name, null);
                    if (dispatch.RequestsShutdown)
                    {
                        lifetime.StopApplication();
                    }

                    return;
                }

                if (dispatch.RequestsShutdown)
                {
                    lifetime.StopApplication();
                    return;
                }
            }
        }
    }
}
