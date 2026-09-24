using System.Diagnostics;
using Scrap.Client;
using Scrap.Platform.Ipc;
using Scrap.Platform.Paths;
using Scrap.Protocol;
using ProtocolCase = Scrap.Protocol.CaseSensitivity;
using ProtocolMode = Scrap.Protocol.SearchMode;

/// <summary>Measures daemon memory and CPU under idle and sustained-search workloads. / 测量守护进程空闲与持续搜索负载下的内存和 CPU。</summary>
internal static partial class Benchmarks
{
    /// <summary>Collects repeated idle and 10k fuzzy-search resource samples. / 收集重复的空闲与一万条模糊搜索资源样本。</summary>
    internal static async Task<ResourceFootprintReport> MeasureResourceFootprintAsync(string root)
    {
        Directory.CreateDirectory(root);
        IReadOnlyList<ResourceRun> idleRuns = await MeasureIdleResourcesAsync(root);
        IReadOnlyList<ResourceRun> searchRuns = await MeasureSearchResourcesAsync(root);
        return new ResourceFootprintReport(
            DateTimeOffset.UtcNow,
            Environment.OSVersion.ToString(),
            System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            Environment.ProcessorCount,
            "WorkingSet64 and PrivateMemorySize64 sampled from the daemon child via System.Diagnostics.Process after Refresh(). CPU is TotalProcessorTime delta / wall time; EquivalentCorePercent may exceed 100 only if multiple cores are used.",
            idleRuns,
            searchRuns,
            "GUI was not measured: the shipped GUI has no profile-root argument and ScrapClient.ConnectAsync() always resolves ScrapPathLayout.ForCurrentUser(), so launching it would touch the real ~/.scrap profile.");
    }

    /// <summary>Measures ten fresh storage-ready daemons after a two-second settling interval. / 在两秒稳定期后测量十个全新且存储就绪的守护进程。</summary>
    private static async Task<IReadOnlyList<ResourceRun>> MeasureIdleResourcesAsync(string root)
    {
        var runs = new List<ResourceRun>();
        for (int run = 0; run < 10; run++)
        {
            string profile = Path.Combine(root, "profiles", $"idle-{run:D2}");
            var paths = new ScrapPathLayout(profile);
            PrepareProfileRoot(profile);
            paths.Initialize();
            using Process process = StartChild(profile, "os");
            await using ScrapClient client = await ConnectAsync(paths);
            await Task.Delay(TimeSpan.FromSeconds(2));
            runs.Add(await SampleResourcesAsync(process, TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(100), null));
            await StopAsync(process, client, "Idle resource daemon did not exit.");
        }

        return runs;
    }

    /// <summary>Measures five fresh daemons continuously serving serial fuzzy searches over 10k records. / 测量五个全新守护进程持续顺序处理一万条记录模糊搜索时的资源。</summary>
    private static async Task<IReadOnlyList<ResourceRun>> MeasureSearchResourcesAsync(string root)
    {
        var paths = new ScrapPathLayout(Path.Combine(root, "profiles", "search-10000"));
        SeedSearchDatabase(paths, 10_000);
        var request = new SearchRequest([], "api-token", ProtocolMode.Fuzzy, ProtocolCase.Sensitive, 100);
        var runs = new List<ResourceRun>();
        for (int run = 0; run < 5; run++)
        {
            using Process process = StartChild(paths.RootDirectory, "fixed");
            await using ScrapClient client = await ConnectAsync(paths);
            for (int i = 0; i < 10; i++) _ = await client.SearchRecordsAsync(request);
            int completed = 0;
            async Task SearchAsync()
            {
                _ = await client.SearchRecordsAsync(request);
                completed++;
            }

            ResourceRun resources = await SampleResourcesAsync(process, TimeSpan.FromSeconds(12), TimeSpan.Zero, SearchAsync);
            runs.Add(resources with { CompletedOperations = completed });
            await StopAsync(process, client, "Search resource daemon did not exit.");
        }

        return runs;
    }

    /// <summary>Connects to a harness-owned daemon and verifies storage readiness. / 连接到工具所属守护进程并验证存储就绪。</summary>
    private static async Task<ScrapClient> ConnectAsync(ScrapPathLayout paths)
    {
        ScrapClient client = await ScrapClient.ConnectAsync(IpcEndpointDescriptor.Create(paths), NoOpLauncher.Instance, FastOptions());
        _ = await client.ListScopesAsync();
        return client;
    }

    /// <summary>Samples post-operation process memory and CPU time for one fixed-duration run. / 在一个固定时长运行中采样操作后的进程内存与 CPU 时间。</summary>
    private static async Task<ResourceRun> SampleResourcesAsync(
        Process process,
        TimeSpan duration,
        TimeSpan samplingInterval,
        Func<Task>? operation)
    {
        process.Refresh();
        TimeSpan cpuStart = process.TotalProcessorTime;
        long started = Stopwatch.GetTimestamp();
        long deadline = started + (long)(duration.TotalSeconds * Stopwatch.Frequency);
        var workingSet = new List<double>();
        var privateBytes = new List<double>();
        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (operation is not null) await operation();
            process.Refresh();
            workingSet.Add(process.WorkingSet64 / 1048576d);
            privateBytes.Add(process.PrivateMemorySize64 / 1048576d);
            if (samplingInterval > TimeSpan.Zero) await Task.Delay(samplingInterval);
        }

        process.Refresh();
        double wallSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        double cpuSeconds = (process.TotalProcessorTime - cpuStart).TotalSeconds;
        return new ResourceRun(
            workingSet.Count,
            wallSeconds,
            0,
            cpuSeconds / wallSeconds * 100,
            cpuSeconds / wallSeconds / Environment.ProcessorCount * 100,
            SummarizeRange(workingSet),
            SummarizeRange(privateBytes));
    }

    /// <summary>Computes mean, range, and nearest-rank percentiles for process-resource samples. / 计算进程资源样本的均值、范围与最近秩百分位数。</summary>
    private static RangeDistribution SummarizeRange(IEnumerable<double> values)
    {
        double[] sorted = values.Order().ToArray();
        if (sorted.Length == 0) return new(0, 0, 0, 0, 0, 0, 0);
        double Percentile(double fraction) => sorted[Math.Clamp((int)Math.Ceiling(fraction * sorted.Length) - 1, 0, sorted.Length - 1)];
        return new(sorted.Length, sorted.Average(), sorted[0], Percentile(.50), Percentile(.95), Percentile(.99), sorted[^1]);
    }
}
