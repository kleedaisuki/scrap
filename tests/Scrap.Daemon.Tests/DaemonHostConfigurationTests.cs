using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Data.Sqlite;
using Scrap.Platform.Paths;
using Scrap.Platform.Secrets;
using Scrap.Protocol;

namespace Scrap.Daemon.Tests;

/// <summary>
/// 验证无效可选配置不会阻止 daemon 启动或版本协商。 / Verifies that invalid optional configuration cannot prevent daemon startup or version negotiation.
/// </summary>
public sealed class DaemonHostConfigurationTests
{
    /// <summary>
    /// 无效 JSON 或非法 duration 均回退默认值，且日志不包含原配置内容。
    /// / Invalid JSON or durations fall back to defaults without copying configuration contents into logs.
    /// </summary>
    [Theory]
    [InlineData("{malformed-秘密🔐")]
    [InlineData("{\"daemon\":{\"idleTimeout\":\"not-a-duration-秘密🔐\",\"idlePollInterval\":\"00:00:00\",\"shutdownTimeout\":-1,\"responseWriteTimeout\":null}}")]
    public async Task InvalidConfigurationFallsBackAndVersionRemainsAvailable(string configuration)
    {
        // macOS limits Unix-domain-socket paths to roughly 100 bytes; keep the test profile short.
        // macOS 的 Unix 域套接字路径约限 100 字节，因此测试 profile 必须保持短小。
        string temporaryRoot = OperatingSystem.IsWindows() ? Path.GetTempPath() : "/tmp";
        string root = Path.Combine(temporaryRoot, $"sdc-{Guid.NewGuid():N}");
        var paths = new ScrapPathLayout(root);
        paths.Initialize();
        await File.WriteAllTextAsync(paths.ConfigurationFile, configuration);

        try
        {
            using IHost host = DaemonHost.Build([], paths, new MemoryMasterKeyProvider());
            DaemonOptions options = host.Services.GetRequiredService<IOptions<DaemonOptions>>().Value;
            Assert.Equal(TimeSpan.FromMinutes(5), options.IdleTimeout);
            Assert.Equal(TimeSpan.FromSeconds(1), options.IdlePollInterval);
            Assert.Equal(TimeSpan.FromSeconds(15), options.ShutdownTimeout);
            Assert.Equal(TimeSpan.FromSeconds(5), options.ResponseWriteTimeout);

            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var dispatcher = host.Services.GetRequiredService<DaemonRequestDispatcher>();
            DaemonDispatchResult result = await dispatcher.DispatchAsync(
                ProtocolRequest.Create("config-version", ProtocolMethods.DaemonVersion, new DaemonVersionParams()),
                CancellationToken.None);
            Assert.Null(result.Response.Error);
            await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            host.Dispose();

            string log = await File.ReadAllTextAsync(paths.LogFile);
            Assert.Contains("ConfigurationFallback", log, StringComparison.Ordinal);
            Assert.DoesNotContain("秘密🔐", log, StringComparison.Ordinal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class MemoryMasterKeyProvider : IMasterKeyProvider
    {
        private byte[]? key;

        /// <inheritdoc />
        public ValueTask<byte[]?> LoadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(key?.ToArray());
        }

        /// <inheritdoc />
        public ValueTask StoreAsync(ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            key = value.ToArray();
            return ValueTask.CompletedTask;
        }
    }
}
