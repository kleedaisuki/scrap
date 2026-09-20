using Scrap.Client;
using Scrap.Platform.Ipc;
using Scrap.Platform.Paths;
using Scrap.Platform.Processes;
using Scrap.Protocol;

namespace Scrap.Integration.Tests;

/// <summary>验证升级 client 自动替换仍在运行的 v1 daemon。 / Verifies that an upgraded client automatically replaces a still-running v1 daemon.</summary>
public sealed class LegacyDaemonReplacementTests
{
    /// <summary>v2 client 在同一旧连接上完成 v1 shutdown，再通过 launcher 连接 v2 daemon。 / A v2 client performs v1 shutdown on the legacy connection, then connects to a v2 daemon through its launcher.</summary>
    [Fact]
    public async Task ConnectAsyncAutomaticallyReplacesLegacyDaemonAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrap-upgrade-tests", Guid.NewGuid().ToString("N"));
        var paths = new ScrapPathLayout(root);
        paths.Initialize();
        IpcEndpointDescriptor endpoint = IpcEndpointDescriptor.Create(paths);
        var legacyShutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task legacy = RunLegacyDaemonAsync(endpoint, legacyShutdown);
        var launcher = new CurrentDaemonLauncher(endpoint);

        try
        {
            await using ScrapClient client = await ScrapClient.ConnectAsync(
                endpoint,
                launcher,
                new ScrapClientOptions { StartupTimeout = TimeSpan.FromSeconds(5) });
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
        TaskCompletionSource shutdown)
    {
        await using var server = endpoint.CreateServerStream();
        await server.WaitForConnectionAsync();

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
