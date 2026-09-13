using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Scrap.Daemon;

/// <summary>
/// 在无 client 和无在途请求超过配置窗口后请求 Generic Host 优雅停止。
/// / Requests graceful Generic Host shutdown after the configured period with no clients or in-flight requests.
/// </summary>
internal sealed class IdleShutdownService : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogIdleShutdown = LoggerMessage.Define(
        LogLevel.Information,
        new EventId(1301, "IdleShutdown"),
        "Scrap daemon idle window elapsed; beginning graceful shutdown.");

    private readonly DaemonActivityTracker activity;
    private readonly DaemonRuntimeState state;
    private readonly DaemonOptions options;
    private readonly TimeProvider timeProvider;
    private readonly IHostApplicationLifetime lifetime;
    private readonly ILogger<IdleShutdownService> logger;

    /// <summary>
    /// 初始化 idle monitor。 / Initializes the idle monitor.
    /// </summary>
    public IdleShutdownService(
        DaemonActivityTracker activity,
        DaemonRuntimeState state,
        IOptions<DaemonOptions> options,
        TimeProvider timeProvider,
        IHostApplicationLifetime lifetime,
        ILogger<IdleShutdownService> logger)
    {
        this.activity = activity;
        this.state = state;
        this.options = options.Value.Validate();
        this.timeProvider = timeProvider;
        this.lifetime = lifetime;
        this.logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!state.IsStopping && !stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(options.IdlePollInterval, timeProvider, stoppingToken).ConfigureAwait(false);
            if (!activity.TryBeginStoppingIfIdle(options.IdleTimeout, state))
            {
                continue;
            }

            LogIdleShutdown(logger, null);
            lifetime.StopApplication();
            return;
        }
    }
}
