using Microsoft.Extensions.Hosting;
using Scrap.Platform.Paths;
using Scrap.Platform.Processes;

namespace Scrap.Daemon;

/// <summary>
/// 获取每-profile 单实例所有权并运行 daemon host。 / Acquires per-profile singleton ownership and runs the daemon host.
/// </summary>
internal static class DaemonApplication
{
    /// <summary>
    /// 运行默认当前用户 profile。 / Runs the default current-user profile.
    /// </summary>
    /// <param name="args">daemon 参数。 / Daemon arguments.</param>
    /// <param name="cancellationToken">外部停止令牌。 / External shutdown token.</param>
    /// <returns>成功或正常锁竞争为 0，启动失败为 6。 / Zero for success or ordinary contention; six for startup failure.</returns>
    public static Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default) =>
        RunAsync(args, ScrapPathLayout.ForCurrentUser(), cancellationToken);

    /// <summary>
    /// 使用显式 profile 运行，作为集成测试 seam。 / Runs an explicit profile as an integration-test seam.
    /// </summary>
    internal static async Task<int> RunAsync(
        string[] args,
        ScrapPathLayout paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(paths);

        try
        {
            paths.Initialize();
            using DaemonInstanceLease? lease = DaemonInstanceLease.TryAcquire(paths);
            if (lease is null)
            {
                return 0;
            }

            using IHost host = DaemonHost.Build(args, paths);
            await host.RunAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch
        {
            // Startup has no reliable IPC channel yet. Avoid emitting details or inheriting client data streams.
            return 6;
        }
    }
}
