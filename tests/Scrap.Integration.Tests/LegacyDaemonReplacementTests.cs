using Scrap.Client;
using Scrap.Platform.Ipc;
using Scrap.Platform.Paths;
using Scrap.Platform.Processes;
using Scrap.Protocol;

namespace Scrap.Integration.Tests;

/// <summary>验证升级 client 自动替换仍在运行的 v1 daemon。 / Verifies that an upgraded client automatically replaces a still-running v1 daemon.</summary>
public sealed class LegacyDaemonReplacementTests
{
    private static readonly ScrapClientOptions FixtureOptions = new()
    {
        InitialConnectTimeout = TimeSpan.FromSeconds(5),
        StartupTimeout = TimeSpan.FromSeconds(5),
    };

    /// <summary>existing-only client 重连时仍保持无替换能力，不会关闭后来出现的 v1 daemon。 / An existing-only client remains unable to replace daemons on reconnect and does not stop a later v1 daemon.</summary>
    [Fact]
    public async Task ExistingOnlyReconnectDoesNotReplaceLegacyDaemonAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrap-reconnect-tests", Guid.NewGuid().ToString("N"));
        var paths = new ScrapPathLayout(root);
        paths.Initialize();
        IpcEndpointDescriptor endpoint = IpcEndpointDescriptor.Create(paths);
        var currentReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeCurrent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task current = RunCurrentHandshakeAndCloseAsync(endpoint, currentReady, closeCurrent);

        try
        {
            await currentReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await using ScrapClient client = Assert.IsType<ScrapClient>(
                await ScrapClient.TryConnectExistingAsync(endpoint, FixtureOptions));
            closeCurrent.TrySetResult();
            await current.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<ScrapConnectionException>(() => client.PingAsync());

            var receivedExtraRequest = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var legacyReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task legacy = RunLegacyProbeDaemonAsync(endpoint, receivedExtraRequest, legacyReady);
            await legacyReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<ScrapProtocolVersionException>(() => client.PingAsync());
            await legacy.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(await receivedExtraRequest.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            closeCurrent.TrySetResult();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>existing-only probe 对 v1 daemon 返回版本错误，且不发送 shutdown。 / An existing-only probe reports a version error for a v1 daemon without sending shutdown.</summary>
    [Fact]
    public async Task TryConnectExistingDoesNotMutateLegacyDaemonAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrap-probe-tests", Guid.NewGuid().ToString("N"));
        var paths = new ScrapPathLayout(root);
        paths.Initialize();
        IpcEndpointDescriptor endpoint = IpcEndpointDescriptor.Create(paths);
        var receivedExtraRequest = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var legacyReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task legacy = RunLegacyProbeDaemonAsync(endpoint, receivedExtraRequest, legacyReady);

        try
        {
            await legacyReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<ScrapProtocolVersionException>(
                () => ScrapClient.TryConnectExistingAsync(endpoint, FixtureOptions));
            await legacy.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(await receivedExtraRequest.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>v2 client 在同一旧连接上完成 v1 shutdown，再通过 launcher 连接 v2 daemon。 / A v2 client performs v1 shutdown on the legacy connection, then connects to a v2 daemon through its launcher.</summary>
    [Fact]
    public async Task ConnectAsyncAutomaticallyReplacesLegacyDaemonAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrap-upgrade-tests", Guid.NewGuid().ToString("N"));
        var paths = new ScrapPathLayout(root);
        paths.Initialize();
        IpcEndpointDescriptor endpoint = IpcEndpointDescriptor.Create(paths);
        var legacyShutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var legacyReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task legacy = RunLegacyDaemonAsync(endpoint, legacyShutdown, legacyReady);
        var launcher = new CurrentDaemonLauncher(endpoint);

        try
        {
            await legacyReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await using ScrapClient client = await ScrapClient.ConnectAsync(
                endpoint,
                launcher,
                FixtureOptions);
            await legacyShutdown.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(launcher.StartCount >= 1);
        }
        finally
        {
            await legacy.WaitAsync(TimeSpan.FromSeconds(5));
            await launcher.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task RunLegacyDaemonAsync(
        IpcEndpointDescriptor endpoint,
        TaskCompletionSource shutdown,
        TaskCompletionSource ready)
    {
        await using var server = endpoint.CreateServerStream();
        Task waitForConnection = server.WaitForConnectionAsync();
        ready.TrySetResult();
        await waitForConnection;

        ProtocolRequest v2 = await LengthPrefixedJsonFraming.ReadAsync<ProtocolRequest>(server);
        await LengthPrefixedJsonFraming.WriteAsync(server, ProtocolResponse.Failure(
            v2.RequestId,
            new ProtocolError(ProtocolErrorCodes.ProtocolVersionUnsupported, "Protocol version is not supported."),
            protocolVersion: 1));

        ProtocolRequest negotiate = await LengthPrefixedJsonFraming.ReadAsync<ProtocolRequest>(server);
        await LengthPrefixedJsonFraming.WriteAsync(server, ProtocolResponse.Success(
            negotiate.RequestId,
            new DaemonVersionResult("0.2.0", 1, 1),
            protocolVersion: 1));

        ProtocolRequest stop = await LengthPrefixedJsonFraming.ReadAsync<ProtocolRequest>(server);
        Assert.Equal(ProtocolMethods.DaemonShutdown, stop.Method);
        await LengthPrefixedJsonFraming.WriteAsync(server, ProtocolResponse.Success(
            stop.RequestId,
            new DaemonShutdownResult(),
            protocolVersion: 1));
        shutdown.TrySetResult();
    }

    private static async Task RunLegacyProbeDaemonAsync(
        IpcEndpointDescriptor endpoint,
        TaskCompletionSource<bool> receivedExtraRequest,
        TaskCompletionSource ready)
    {
        await using var server = endpoint.CreateServerStream();
        Task waitForConnection = server.WaitForConnectionAsync();
        ready.TrySetResult();
        await waitForConnection;
        ProtocolRequest request = await LengthPrefixedJsonFraming.ReadAsync<ProtocolRequest>(server);
        await LengthPrefixedJsonFraming.WriteAsync(server, ProtocolResponse.Failure(
            request.RequestId,
            new ProtocolError(ProtocolErrorCodes.ProtocolVersionUnsupported, "Protocol version is not supported."),
            protocolVersion: 1));

        try
        {
            _ = await LengthPrefixedJsonFraming.ReadAsync<ProtocolRequest>(server);
            receivedExtraRequest.TrySetResult(true);
        }
        catch (EndOfStreamException)
        {
            receivedExtraRequest.TrySetResult(false);
        }
        catch (IOException)
        {
            receivedExtraRequest.TrySetResult(false);
        }
    }

    private static async Task RunCurrentHandshakeAndCloseAsync(
        IpcEndpointDescriptor endpoint,
        TaskCompletionSource ready,
        TaskCompletionSource close)
    {
        await using var server = endpoint.CreateServerStream();
        Task waitForConnection = server.WaitForConnectionAsync();
        ready.TrySetResult();
        await waitForConnection;
        ProtocolRequest request = await LengthPrefixedJsonFraming.ReadAsync<ProtocolRequest>(server);
        await LengthPrefixedJsonFraming.WriteAsync(server, ProtocolResponse.Success(
            request.RequestId,
            new DaemonVersionResult("0.3.0", 1, ProtocolConstants.CurrentVersion),
            request.ProtocolVersion));
        await close.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class CurrentDaemonLauncher(IpcEndpointDescriptor endpoint) : IDaemonProcessLauncher, IAsyncDisposable
    {
        private Task? serverTask;
        private int startCount;

        public int StartCount => Volatile.Read(ref startCount);

        public void Start()
        {
            Interlocked.Increment(ref startCount);
            serverTask ??= RunAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (serverTask is not null) await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        }

        private async Task RunAsync()
        {
            await using var server = endpoint.CreateServerStream();
            await server.WaitForConnectionAsync();
            ProtocolRequest request = await LengthPrefixedJsonFraming.ReadAsync<ProtocolRequest>(server);
            Assert.Equal(ProtocolMethods.DaemonVersion, request.Method);
            await LengthPrefixedJsonFraming.WriteAsync(server, ProtocolResponse.Success(
                request.RequestId,
                new DaemonVersionResult("0.3.0", 1, ProtocolConstants.CurrentVersion),
                request.ProtocolVersion));
        }
    }
}
