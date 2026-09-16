using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Scrap.Daemon;

/// <summary>
/// 接纳并发 client 连接，在停止时先关闭 accept，再排空已接纳处理。
/// / Accepts concurrent clients, stops accepting first, then drains admitted handlers during shutdown.
/// </summary>
internal sealed class DaemonServerService : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogStarted = LoggerMessage.Define(
        LogLevel.Information,
        new EventId(1201, "IpcListenerStarted"),
        "Scrap daemon IPC listener started.");

    private static readonly Action<ILogger, Exception?> LogStopped = LoggerMessage.Define(
        LogLevel.Information,
        new EventId(1202, "IpcListenerStopped"),
        "Scrap daemon IPC listener stopped.");

    private static readonly Action<ILogger, string, Exception?> LogHandlerFailure =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(1203, "IpcHandlerFailure"),
            "IPC connection handler failed ({ExceptionType}); payload was not logged.");

    private static readonly Action<ILogger, string, string, int, Exception?> LogListenerFailure =
        LoggerMessage.Define<string, string, int>(
            LogLevel.Error,
            new EventId(1204, "IpcListenerFailure"),
            "IPC listener failed during {Phase} ({ExceptionType}, HResult {HResult}); exception details were not logged.");

    private readonly IConnectionAcceptor acceptor;
    private readonly DaemonConnectionProcessor processor;
    private readonly DaemonActivityTracker activity;
    private readonly DaemonRuntimeState state;
    private readonly ILogger<DaemonServerService> logger;
    private readonly object tasksGate = new();
    private readonly HashSet<Task> connectionTasks = [];

    /// <summary>
    /// 初始化 server service。 / Initializes the server service.
    /// </summary>
    public DaemonServerService(
        IConnectionAcceptor acceptor,
        DaemonConnectionProcessor processor,
        DaemonActivityTracker activity,
        DaemonRuntimeState state,
        ILogger<DaemonServerService> logger)
    {
        this.acceptor = acceptor;
        this.processor = processor;
        this.activity = activity;
        this.state = state;
        this.logger = logger;
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // .NET 10 runs all of ExecuteAsync in the background. Bind here so successful host startup
            // guarantees that the IPC endpoint is already reachable, independent of hosted-service order.
            acceptor.Bind();
        }
        catch (Exception exception)
        {
            LogListenerError("startup-bind", exception);
            throw;
        }

        LogStarted(logger, null);
        return base.StartAsync(cancellationToken);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                Stream stream = await AcceptAsync(stoppingToken).ConfigureAwait(false);
                if (!activity.TryBeginConnection(state, out IDisposable? lease))
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                    break;
                }

                try
                {
                    acceptor.Bind();
                }
                catch (Exception exception)
                {
                    lease!.Dispose();
                    await stream.DisposeAsync().ConfigureAwait(false);
                    LogListenerError("replacement-bind", exception);
                    throw;
                }

                Track(HandleConnectionAsync(stream, lease!, stoppingToken));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected shutdown path.
        }
        finally
        {
            activity.BeginStopping(state);
            await DrainConnectionsAsync().ConfigureAwait(false);
            LogStopped(logger, null);
        }
    }

    private async ValueTask<Stream> AcceptAsync(CancellationToken stoppingToken)
    {
        try
        {
            return await acceptor.AcceptAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogListenerError("accept", exception);
            throw;
        }
    }

    private void LogListenerError(string phase, Exception exception) =>
        LogListenerFailure(logger, phase, exception.GetType().Name, exception.HResult, null);

    /// <inheritdoc />
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        activity.BeginStopping(state);
        // Accepted mutations own the database outcome. The generic host timeout must not dispose
        // storage/crypto or release the singleton lease underneath them.
        return base.StopAsync(CancellationToken.None);
    }

    private async Task HandleConnectionAsync(Stream stream, IDisposable lease, CancellationToken stoppingToken)
    {
        // Do not let synchronously-completing SQLite/domain work delay creation of the next pipe instance.
        await Task.Yield();
        await using (stream.ConfigureAwait(false))
        {
            try
            {
                await processor.ProcessAsync(stream, lease, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                lease.Dispose();
                LogHandlerFailure(logger, exception.GetType().Name, null);
            }
        }
    }

    private void Track(Task task)
    {
        lock (tasksGate)
        {
            connectionTasks.Add(task);
        }

        _ = task.ContinueWith(
            completed =>
            {
                lock (tasksGate)
                {
                    connectionTasks.Remove(completed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task DrainConnectionsAsync()
    {
        Task[] snapshot;
        lock (tasksGate)
        {
            snapshot = [.. connectionTasks];
        }

        // Every idle read observes the stopping token and every response write has its own timeout.
        // Do not dispose crypto/storage underneath an already accepted mutation.
        await Task.WhenAll(snapshot).ConfigureAwait(false);
    }
}
