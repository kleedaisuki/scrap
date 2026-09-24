using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Scrap.Client;
using Scrap.Crypto;
using Scrap.Daemon;
using Scrap.Domain;
using Scrap.Platform.Ipc;
using Scrap.Platform.Paths;
using Scrap.Platform.Processes;
using Scrap.Platform.Secrets;
using Scrap.Protocol;
using Scrap.Storage.Sqlite;
using DomainCase = Scrap.Domain.CaseSensitivity;
using DomainMode = Scrap.Domain.SearchMode;
using ProtocolCase = Scrap.Protocol.CaseSensitivity;
using ProtocolMode = Scrap.Protocol.SearchMode;

/// <summary>Provides shared benchmark infrastructure and isolation guards. / 提供共享基准基础设施与隔离保护。</summary>
internal static partial class Benchmarks
{
    /// <summary>Number of deterministic fixture scopes. / 确定性夹具的 scope 数量。</summary>
    internal const int ScopeCount = 10;

    /// <summary>Non-secret deterministic AES fixture key; never used with user data. / 非秘密的确定性 AES 夹具密钥；绝不用于用户数据。</summary>
    internal static readonly byte[] FixedKey = Enumerable.Range(1, AesGcmRecordEncryptor.KeySize).Select(i => (byte)i).ToArray();

    /// <summary>Stable human-readable JSON settings for raw outputs. / 用于原始输出的稳定可读 JSON 设置。</summary>
    internal static JsonSerializerOptions JsonOptions { get; } = new() { WriteIndented = true };

    /// <summary>Rejects a benchmark root outside this repository's .temp or .cache directory. / 拒绝仓库 .temp 或 .cache 之外的基准根目录。</summary>
    internal static string RequireIsolatedRoot(string root)
    {
        string candidate = RequireIsolatedPath(root, nameof(root));
        Directory.CreateDirectory(candidate);
        return candidate;
    }

    /// <summary>Creates or accepts an empty isolated run root, rejecting prior contents without deleting them. / 创建或接受空的隔离运行根目录；发现既有内容时拒绝且不删除。</summary>
    internal static string RequireFreshRunRoot(string root)
    {
        string candidate = RequireIsolatedRoot(root);
        if (Directory.EnumerateFileSystemEntries(candidate).Any())
        {
            throw new ArgumentException(
                $"Run root '{candidate}' is not empty. Choose a new .temp/.cache run root; existing data was left untouched.",
                nameof(root));
        }

        return candidate;
    }

    /// <summary>Rejects a benchmark file outside this repository's .temp or .cache directory. / 拒绝仓库 .temp 或 .cache 之外的基准文件。</summary>
    internal static string RequireIsolatedFile(string path)
    {
        return RequireIsolatedPath(path, nameof(path));
    }

    /// <summary>Applies the single repository-local path policy to input and output paths. / 对输入和输出路径统一应用仓库本地路径策略。</summary>
    private static string RequireIsolatedPath(string path, string parameterName)
    {
        string candidate = Path.GetFullPath(path);
        string repository = FindRepositoryRoot();
        if (!IsWithin(candidate, Path.Combine(repository, ".temp")) &&
            !IsWithin(candidate, Path.Combine(repository, ".cache")))
        {
            throw new ArgumentException("Benchmark paths must stay under the repository .temp or .cache directory.", parameterName);
        }

        return candidate;
    }

    /// <summary>Tests a normalized path against a normalized repository-owned root using the host's case rules. / 按宿主系统的大小写规则，检查规范化路径是否位于仓库所属根目录内。</summary>
    private static bool IsWithin(string candidate, string root)
    {
        string canonicalRoot = Path.GetFullPath(root);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return candidate.Equals(canonicalRoot, comparison) ||
            candidate.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, comparison);
    }

    /// <summary>Writes indented JSON and creates its parent directory. / 写入缩进 JSON，并创建父目录。</summary>
    internal static async Task WriteJsonAsync<T>(string output, T value)
    {
        string path = RequireIsolatedFile(output);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions));
    }

    /// <summary>Finds the repository root from the current directory. / 从当前目录查找仓库根目录。</summary>
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, ".git")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Run Scrap.Perf from inside the Scrap repository.");
    }
    /// <summary>Starts this tool in its hidden daemon-child mode with an explicit isolated root and key-provider variant. / 使用显式隔离根目录和密钥提供器变体，以隐藏的 daemon-child 模式启动本工具。</summary>
    internal static Process StartChild(string root, string provider)
    {
        string dll = Assembly.GetExecutingAssembly().Location;
        var info = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        info.ArgumentList.Add(dll);
        info.ArgumentList.Add("daemon-child");
        info.ArgumentList.Add(root);
        info.ArgumentList.Add(provider);
        return Process.Start(info) ?? throw new InvalidOperationException("Could not start daemon child.");
    }

    /// <summary>Creates a fixture profile root and applies the same Windows ACL precondition as production paths. / 创建夹具配置根目录，并应用与生产路径相同的 Windows ACL 前置条件。</summary>
    internal static void PrepareProfileRoot(string root)
    {
        Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows()) return;
        string user = $"{Environment.UserDomainName}\\{Environment.UserName}:(OI)(CI)F";
        var info = new ProcessStartInfo("icacls") { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add(root);
        info.ArgumentList.Add("/inheritance:r");
        info.ArgumentList.Add("/grant:r");
        info.ArgumentList.Add(user);
        info.ArgumentList.Add("SYSTEM:(OI)(CI)F");
        using Process process = Process.Start(info) ?? throw new InvalidOperationException("Could not prepare benchmark ACL.");
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("Could not prepare benchmark profile ACL.");
    }

    /// <summary>Returns short polling intervals without changing the production protocol or request path. / 返回较短轮询间隔，而不改变生产协议或请求路径。</summary>
    internal static ScrapClientOptions FastOptions() => new()
    {
        InitialConnectTimeout = TimeSpan.FromMilliseconds(25),
        StartupTimeout = TimeSpan.FromSeconds(15),
        InitialRetryDelay = TimeSpan.FromMilliseconds(5),
        MaxRetryDelay = TimeSpan.FromMilliseconds(25),
    };

    /// <summary>Computes mean and nearest-rank percentiles from one sample set. / 从一个样本集计算均值与最近秩百分位数。</summary>
    internal static Distribution Summarize(IEnumerable<double> values)
    {
        double[] sorted = values.Order().ToArray();
        if (sorted.Length == 0) return new(0, 0, 0, 0, 0);
        double P(double q) => sorted[Math.Clamp((int)Math.Ceiling(q * sorted.Length) - 1, 0, sorted.Length - 1)];
        return new(sorted.Length, sorted.Average(), P(.50), P(.95), P(.99));
    }


    /// <summary>Prevents the client from launching anything outside the harness. / 防止客户端在工具之外启动任何进程。</summary>
    private sealed class NoOpLauncher : IDaemonProcessLauncher
    {
        internal static readonly NoOpLauncher Instance = new();

        /// <summary>Intentionally does nothing because the harness owns the child process. / 故意不执行操作，因为子进程由工具管理。</summary>
        public void Start() { }
    }

    /// <summary>Supplies a deterministic test-only master key. / 提供确定性的仅测试主密钥。</summary>
    private sealed class FixedMasterKeyProvider(byte[] key) : IMasterKeyProvider
    {
        /// <summary>Returns a defensive copy of the fixture key. / 返回基准密钥的防御性副本。</summary>
        public ValueTask<byte[]?> LoadAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<byte[]?>(key.ToArray());

        /// <summary>Ignores writes because the fixture key is immutable. / 忽略写入，因为基准密钥不可变。</summary>
        public ValueTask StoreAsync(ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
