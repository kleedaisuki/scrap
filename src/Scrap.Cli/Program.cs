namespace Scrap.Cli;

/// <summary>
/// scrap 命令行入口。/ The scrap command-line entry point.
/// </summary>
internal static class Program
{
    /// <summary>
    /// 启动 CLI 并保持所有面向用户的失败都由统一出口映射。/
    /// Starts the CLI and routes every user-facing failure through one mapping point.
    /// </summary>
    private static async Task<int> Main(string[] args)
    {
        await using var client = new ProtocolScrapClientAdapter();
        return await CliApplication.RunAsync(args, client, SystemCliEnvironment.Instance).ConfigureAwait(false);
    }
}
