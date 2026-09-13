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
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger, null);

        try
        {
            while (!state.IsStopping && !stoppingToken.IsCancellationRequested)
            {
                Stream stream = await acceptor.AcceptAsync(stoppingToken).ConfigureAwait(false);
                if (!activity.TryBeginConnection(state, out IDisposable? lease))
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                    break;
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
