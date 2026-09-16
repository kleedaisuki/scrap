using System.Globalization;
using Microsoft.Extensions.Logging;
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
    /// 验证启动返回前已 bind，且 started 日志不会早于 bind。
    /// / Verifies that startup binds before returning and never logs started before binding.
    /// </summary>
    [Fact]
    public async Task DaemonServerServiceStartBindsBeforeStartedLogAndAccept()
    {
        var sequence = new List<string>();
        var gate = new object();
        void Observe(string value)
        {
            lock (gate)
            {
                sequence.Add(value);
            }
        }

        var acceptor = new ProgrammableConnectionAcceptor(
            async cancellationToken =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Infinite accept completed unexpectedly.");
            },
            observer: Observe);
        var logger = new CollectingTestLogger<DaemonServerService>(entry => Observe(entry.EventId.Name!));
        using var fixture = CreateServerFixture(acceptor, logger);

        await fixture.Service.StartAsync(CancellationToken.None);

        lock (gate)
        {
            Assert.True(sequence.Count >= 2);
            Assert.Equal("bind", sequence[0]);
            Assert.Equal("IpcListenerStarted", sequence[1]);
        }

        Assert.Equal(1, acceptor.BindCalls);
        await fixture.Service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// 验证首次 bind 失败向 host 传播，且日志只包含安全的异常元数据。
    /// / Verifies that initial bind failure propagates to the host and logs only safe exception metadata.
    /// </summary>
    [Fact]
    public async Task DaemonServerServiceStartPropagatesAndSafelyLogsBindFailure()
    {
        const string secret = "pipe-secret-秘密";
        var failure = new ListenerTestException(secret, unchecked((int)0x81234567));
        var acceptor = new ProgrammableConnectionAcceptor(
            _ => ValueTask.FromException<Stream>(new InvalidOperationException()),
            _ => failure);
        var logger = new CollectingTestLogger<DaemonServerService>();
        using var fixture = CreateServerFixture(acceptor, logger);

        ListenerTestException actual = await Assert.ThrowsAsync<ListenerTestException>(
            () => fixture.Service.StartAsync(CancellationToken.None));

        Assert.Same(failure, actual);
        TestLogEntry entry = Assert.Single(logger.Entries);
        Assert.Equal("IpcListenerFailure", entry.EventId.Name);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains(nameof(ListenerTestException), entry.Message, StringComparison.Ordinal);
        Assert.Contains(failure.HResult.ToString(CultureInfo.InvariantCulture), entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, entry.Message, StringComparison.Ordinal);
        Assert.Null(entry.Exception);
    }

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

    private static ServerFixture CreateServerFixture(
        IConnectionAcceptor acceptor,
        ILogger<DaemonServerService> logger)
    {
        var lifetime = new RecordingHostApplicationLifetime();
        var state = new DaemonRuntimeState();
        var activity = new DaemonActivityTracker(TimeProvider.System);
        var coordinator = new RequestExecutionCoordinator(state);
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
        var service = new DaemonServerService(acceptor, processor, activity, state, logger);
        return new ServerFixture(service, lifetime, coordinator);
    }

    /// <summary>
    /// 提供可控 HResult 的 listener 失败。 / Provides a listener failure with a controlled HResult.
    /// </summary>
    private sealed class ListenerTestException : Exception
    {
        /// <summary>
        /// 初始化具有指定 HResult 的 listener 测试异常。 / Initializes a listener test exception with a specified HResult.
        /// </summary>
        /// <param name="message">用于验证脱敏的异常消息。 / Exception message used to verify redaction.</param>
        /// <param name="hResult">待记录的 HResult。 / HResult to log.</param>
        public ListenerTestException(string message, int hResult)
            : base(message)
        {
            HResult = hResult;
        }
    }

    /// <summary>
    /// 统一持有 server service 测试的可释放依赖。 / Owns disposable dependencies for a server-service test.
    /// </summary>
    private sealed class ServerFixture : IDisposable
    {
        private readonly RecordingHostApplicationLifetime lifetime;
        private readonly RequestExecutionCoordinator coordinator;

        /// <summary>
        /// 初始化并接管 fixture 依赖。 / Initializes and takes ownership of fixture dependencies.
        /// </summary>
        /// <param name="service">待测 service。 / Service under test.</param>
        /// <param name="lifetime">待释放的 host lifetime。 / Host lifetime to dispose.</param>
        /// <param name="coordinator">待释放的 request coordinator。 / Request coordinator to dispose.</param>
        public ServerFixture(
            DaemonServerService service,
            RecordingHostApplicationLifetime lifetime,
            RequestExecutionCoordinator coordinator)
        {
            Service = service;
            this.lifetime = lifetime;
            this.coordinator = coordinator;
        }

        /// <summary>
        /// 获取待测 server service。 / Gets the server service under test.
        /// </summary>
        public DaemonServerService Service { get; }

        /// <inheritdoc />
        public void Dispose()
        {
            Service.Dispose();
            coordinator.Dispose();
            lifetime.Dispose();
        }
    }
}
