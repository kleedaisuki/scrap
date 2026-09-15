using System.Globalization;
using System.Text.Json;

/// <summary>
/// Dispatches explicit benchmark modes without opening the user's real Scrap profile.
/// 分派显式基准模式，且不会打开用户真实的 Scrap 配置目录。
/// </summary>
internal static class Program
{
    /// <summary>Runs the selected benchmark mode. / 运行所选基准模式。</summary>
    public static async Task<int> Main(string[] args)
    {
        try
        {
            return await DispatchAsync(args);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            Console.Error.WriteLine($"Scrap.Perf: {exception.Message}");
            return 2;
        }
    }

    /// <summary>Routes a validated argument shape to one explicit mode. / 将有效的参数形状路由到一个显式模式。</summary>
    private static async Task<int> DispatchAsync(string[] args)
    {
        if (args.Length == 0)
        {
            return InvalidArguments();
        }

        return args[0] switch
        {
            "baseline" when args.Length == 3 => await RunBaselineAsync(args[1], args[2]),
            "startup" when args.Length == 3 => await RunStartupAsync(args[1], args[2]),
            "metadata" when args.Length == 3 => await RunMetadataAsync(args[1], args[2]),
            "resources" when args.Length == 3 => await RunResourcesAsync(args[1], args[2]),
            "index" when args.Length == 4 => await RunIndexAsync(args[1], args[2], args[3]),
            "profile-search" when args.Length == 3 => RunSearchProfile(args[1], args[2]),
            "daemon-child" when args.Length == 3 => await Benchmarks.RunDaemonChildAsync(Benchmarks.RequireIsolatedRoot(args[1]), args[2]),
            _ => InvalidArguments(),
        };
    }

    /// <summary>Runs the complete isolated baseline. / 运行完整的隔离基线。</summary>
    private static async Task<int> RunBaselineAsync(string rootArgument, string outputArgument)
    {
        string root = Benchmarks.RequireIsolatedRoot(rootArgument);
        string output = Benchmarks.RequireIsolatedFile(outputArgument);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var result = new BenchmarkReport(
            DateTimeOffset.UtcNow,
            Environment.OSVersion.ToString(),
            System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            Environment.ProcessorCount,
            await Benchmarks.MeasureStartupsAsync(Path.Combine(root, "startup")),
            await Benchmarks.MeasureCrudAsync(Path.Combine(root, "crud")),
            await Benchmarks.MeasureSearchAsync(Path.Combine(root, "search")));
        await Benchmarks.WriteJsonAsync(output, result);
        Console.WriteLine(JsonSerializer.Serialize(result, Benchmarks.JsonOptions));
        return 0;
    }

    /// <summary>Runs only process readiness and IPC measurements. / 仅运行进程就绪与 IPC 测量。</summary>
    private static async Task<int> RunStartupAsync(string root, string output)
    {
        StartupResults result = await Benchmarks.MeasureStartupsAsync(Benchmarks.RequireIsolatedRoot(root));
        await Benchmarks.WriteJsonAsync(output, result);
        return 0;
    }

    /// <summary>Measures idle and sustained-search daemon resources. / 测量空闲与持续搜索时的守护进程资源。</summary>
    private static async Task<int> RunResourcesAsync(string rootArgument, string output)
    {
        string root = Benchmarks.RequireIsolatedRoot(rootArgument);
        ResourceFootprintReport result = await Benchmarks.MeasureResourceFootprintAsync(root);
        await Benchmarks.WriteJsonAsync(output, result);
        return 0;
    }

    /// <summary>Runs focused metadata materialization measurements. / 运行元数据物化专项测量。</summary>
    private static async Task<int> RunMetadataAsync(string database, string output)
    {
        await Benchmarks.RunMetadataFocusAsync(Benchmarks.RequireIsolatedFile(database), Benchmarks.RequireIsolatedFile(output));
        return 0;
    }

    /// <summary>Runs one side of the scope-index A/B. / 运行 scope 索引 A/B 的一侧。</summary>
    private static async Task<int> RunIndexAsync(string database, string variant, string output)
    {
        bool drop = variant switch
        {
            "keep" => false,
            "drop" => true,
            _ => throw new ArgumentException("Index variant must be 'keep' or 'drop'.", nameof(variant)),
        };
        await Benchmarks.RunIndexFocusAsync(Benchmarks.RequireIsolatedFile(database), drop, Benchmarks.RequireIsolatedFile(output));
        return 0;
    }

    /// <summary>Runs the profiler workload for a fixed duration. / 在固定时长内运行分析器负载。</summary>
    private static int RunSearchProfile(string database, string seconds)
    {
        Benchmarks.RunSearchProfile(Benchmarks.RequireIsolatedFile(database), TimeSpan.FromSeconds(int.Parse(seconds, CultureInfo.InvariantCulture)));
        return 0;
    }

    /// <summary>Reports invalid arguments with actionable usage. / 输出可操作的参数用法。</summary>
    private static int InvalidArguments()
    {
        PrintUsage();
        return 2;
    }

    /// <summary>Prints supported modes. / 输出支持的模式。</summary>
    private static void PrintUsage() => Console.Error.WriteLine(
        """
        Usage:
          dotnet run -c Release --project tools/Scrap.Perf -- baseline <isolated-root> <output.json>
          dotnet run -c Release --project tools/Scrap.Perf -- startup <isolated-root> <output.json>
          dotnet run -c Release --project tools/Scrap.Perf -- resources <isolated-root> <output.json>
          dotnet run -c Release --project tools/Scrap.Perf -- metadata <database> <output.json>
          dotnet run -c Release --project tools/Scrap.Perf -- index <database> <keep|drop> <output.json>
          dotnet run -c Release --project tools/Scrap.Perf -- profile-search <database> <seconds>
        """);
}
