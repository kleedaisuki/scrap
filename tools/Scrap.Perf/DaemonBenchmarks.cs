using System.Diagnostics;
using Scrap.Client;
using Scrap.Platform.Ipc;
using Scrap.Platform.Paths;

/// <summary>Daemon startup and CRUD measurements / 守护进程启动与 CRUD 测量.</summary>
internal static partial class Benchmarks
{
    /// <summary>Measures new-profile readiness, existing-profile process readiness, and persistent IPC ping. / 测量新配置就绪、已有配置进程就绪和持久 IPC ping。</summary>
    internal static async Task<StartupResults> MeasureStartupsAsync(string root)
    {
        var cold = new List<double>();
        for (int i = 0; i < 20; i++)
        {
            string profile = Path.Combine(root, $"cold-{i:D2}");
            cold.Add(await StartReadyStopAsync(profile, "os", collectPings: null));
        }

        string warmProfile = Path.Combine(root, "warm");
        _ = await StartReadyStopAsync(warmProfile, "os", collectPings: null);
        var warm = new List<double>();
        var ping = new List<double>();
        for (int i = 0; i < 30; i++)
        {
            warm.Add(await StartReadyStopAsync(warmProfile, "os", i == 0 ? ping : null));
        }

        return new StartupResults(Summarize(cold), Summarize(warm), Summarize(ping));
    }

    /// <summary>Starts a daemon, waits for the first storage-backed request, optionally samples ping, then shuts it down. / 启动守护进程、等待首个存储请求，可选采样 ping，随后关闭。</summary>
    internal static async Task<double> StartReadyStopAsync(string root, string provider, List<double>? collectPings)
    {
        var paths = new ScrapPathLayout(root);
        PrepareProfileRoot(root);
        paths.Initialize();
        IpcEndpointDescriptor endpoint = IpcEndpointDescriptor.Create(paths);
        long started = Stopwatch.GetTimestamp();
        using Process process = StartChild(root, provider);
        await using ScrapClient client = await ScrapClient.ConnectAsync(endpoint, NoOpLauncher.Instance, FastOptions());
        _ = await client.ListScopesAsync();
        double readyMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (collectPings is not null)
        {
            for (int i = 0; i < 50; i++) _ = await client.PingAsync();
            for (int i = 0; i < 1000; i++)
            {
                long tick = Stopwatch.GetTimestamp();
                _ = await client.PingAsync();
                collectPings.Add(Stopwatch.GetElapsedTime(tick).TotalMilliseconds);
            }
        }

        await StopAsync(process, client, "Daemon child did not exit.");
        if (process.ExitCode != 0) throw new InvalidOperationException($"Daemon child exited {process.ExitCode}: {process.StandardError.ReadToEnd()}");
        return readyMs;
    }

    /// <summary>Measures sequential encrypted Set, Get, and Delete through the real daemon IPC path. / 通过真实守护进程 IPC 路径测量顺序加密 Set、Get 和 Delete。</summary>
    internal static async Task<CrudResults> MeasureCrudAsync(string root)
    {
        string profile = Path.Combine(root, "crud");
        var paths = new ScrapPathLayout(profile);
        PrepareProfileRoot(profile);
        paths.Initialize();
        using Process process = StartChild(profile, "os");
        await using ScrapClient client = await ScrapClient.ConnectAsync(IpcEndpointDescriptor.Create(paths), NoOpLauncher.Instance, FastOptions());
        await client.CreateScopeAsync("bench");
        const string value = "benchmark-value-64-bytes-0123456789-abcdefghijklmnopqrstuvwxyz-AB";

        for (int i = 0; i < 25; i++)
        {
            string key = $"warm-{i:D3}";
            await client.SetRecordAsync("bench", key, value);
            _ = await client.GetRecordAsync("bench", key);
            await client.DeleteRecordAsync("bench", key);
        }

        const int count = 500;
        var set = new List<double>(count);
        var get = new List<double>(1000);
        var delete = new List<double>(count);
        long phase = Stopwatch.GetTimestamp();
        for (int i = 0; i < count; i++)
        {
            long tick = Stopwatch.GetTimestamp();
            await client.SetRecordAsync("bench", $"record-{i:D5}", value);
            set.Add(Stopwatch.GetElapsedTime(tick).TotalMilliseconds);
        }
        double setSeconds = Stopwatch.GetElapsedTime(phase).TotalSeconds;

        phase = Stopwatch.GetTimestamp();
        for (int i = 0; i < 1000; i++)
        {
            long tick = Stopwatch.GetTimestamp();
            _ = await client.GetRecordAsync("bench", $"record-{i % count:D5}");
            get.Add(Stopwatch.GetElapsedTime(tick).TotalMilliseconds);
        }
        double getSeconds = Stopwatch.GetElapsedTime(phase).TotalSeconds;

        phase = Stopwatch.GetTimestamp();
        for (int i = 0; i < count; i++)
        {
            long tick = Stopwatch.GetTimestamp();
            await client.DeleteRecordAsync("bench", $"record-{i:D5}");
            delete.Add(Stopwatch.GetElapsedTime(tick).TotalMilliseconds);
        }
        double deleteSeconds = Stopwatch.GetElapsedTime(phase).TotalSeconds;

        await StopAsync(process, client, "CRUD daemon child did not exit.");
        return new CrudResults(
            new OperationStats(count / setSeconds, Summarize(set)),
            new OperationStats(1000 / getSeconds, Summarize(get)),
            new OperationStats(count / deleteSeconds, Summarize(delete)),
            value.Length);
    }

}
