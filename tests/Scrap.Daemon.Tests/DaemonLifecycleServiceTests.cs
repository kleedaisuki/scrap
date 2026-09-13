using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scrap.Protocol;

namespace Scrap.Daemon.Tests;

/// <summary>
/// 验证 daemon 后台服务的空闲停止和连接排空生命周期。 / Verifies idle shutdown and connection-draining lifecycles of daemon background services.
/// </summary>
public sealed class DaemonLifecycleServiceTests
{
    /// <summary>
    /// 验证活动连接阻止空闲停止；连接结束后的完整空闲窗口会且只会请求一次停止。
    /// / Verifies that an active connection prevents idle shutdown and that one complete idle window after release requests exactly one stop.
    /// </summary>
    [Fact]
    public async Task IdleShutdownServiceWaitsForIdleWindowAfterLastConnection()
    {
        TimeSpan interval = TimeSpan.FromMilliseconds(1);
        var timeProvider = new ManualTimerTimeProvider();
        var activity = new DaemonActivityTracker(timeProvider);
        var state = new DaemonRuntimeState();
        using var lifetime = new RecordingHostApplicationLifetime();
        using IDisposable connection = activity.BeginConnection();
        var service = new IdleShutdownService(
            activity,
            state,
            Options.Create(new DaemonOptions
            {
                IdleTimeout = interval,
                IdlePollInterval = interval,
                ShutdownTimeout = TimeSpan.FromSeconds(1),
            }),
            timeProvider,
            lifetime,
            NullLogger<IdleShutdownService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => timeProvider.TimerCreationCount >= 1);
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => timeProvider.TimerCreationCount >= 2);

        Assert.False(state.IsStopping);
        Assert.Equal(0, lifetime.StopCallCount);

        connection.Dispose();
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await lifetime.StopRequested.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        Assert.True(state.IsStopping);
        Assert.Equal(1, lifetime.StopCallCount);
    }

    /// <summary>
    /// 验证 server 停止时会取消空闲连接读取、等待 handler 结束、释放 stream 与活动租约。
    /// / Verifies that stopping the server cancels an idle connection read, drains its handler, and releases both stream and activity lease.
    /// </summary>
    [Fact]
    public async Task DaemonServerServiceStopDrainsAcceptedConnection()
    {
        var stream = new TestDuplexStream(blockAfterInput: true);
        var acceptor = new SingleConnectionAcceptor(stream);
        using var lifetime = new RecordingHostApplicationLifetime();
        var state = new DaemonRuntimeState();
        var activity = new DaemonActivityTracker(TimeProvider.System);
        using var coordinator = new RequestExecutionCoordinator(state);
        var dispatcher = new DaemonRequestDispatcher(
            new StubDaemonOperations(),
            coordinator,
            NullLogger<DaemonRequestDispatcher>.Instance,
            "server-test");
        var processor = new DaemonConnectionProcessor(
            dispatcher,
            activity,
            state,
            lifetime,
            NullLogger<DaemonConnectionProcessor>.Instance);
        var service = new DaemonServerService(
            acceptor,
            processor,
            activity,
            state,
            NullLogger<DaemonServerService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await stream.ReadStarted.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => acceptor.Calls >= 2);
        Assert.Equal(1L, activity.ActiveConnections);

        await service.StopAsync(new CancellationToken(canceled: true)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(stream.Disposed.IsCompleted);

        Assert.True(state.IsStopping);
        Assert.Equal(0L, activity.ActiveConnections);
        Assert.Equal(0L, activity.ActiveRequests);
        Assert.Equal(0, lifetime.StopCallCount);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(1, timeout.Token);
        }
    }
}
