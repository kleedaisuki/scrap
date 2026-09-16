using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Scrap.Platform.Paths;

namespace Scrap.Platform.Ipc;

/// <summary>
/// 描述并创建仅当前用户可访问的 .NET named-pipe endpoint。
/// Describes and creates a current-user-only .NET named-pipe endpoint.
/// </summary>
/// <remarks>
/// Windows 使用打包应用可访问的 <c>LOCAL\</c> namespace 内的稳定哈希名称；Unix 使用
/// <c>XDG_RUNTIME_DIR</c> 或按用户隔离的临时目录中的 rooted pipe name，.NET 将其绑定为 Unix domain socket。
/// Windows uses a stable hashed name in the packaged-app-compatible <c>LOCAL\</c> namespace. Unix uses a rooted pipe name below
/// <c>XDG_RUNTIME_DIR</c> or a per-user temporary directory, which .NET binds as a Unix domain socket.
/// daemon 必须先取得 <see cref="Processes.DaemonInstanceLease"/>，再创建 server stream，以免并发 bind 竞争删除 winner 的 socket。
/// The daemon must acquire <see cref="Processes.DaemonInstanceLease"/> before creating the server stream so a competing bind cannot unlink the winner's socket.
/// </remarks>
public sealed class IpcEndpointDescriptor
{
    private const int UnixSocketPathConservativeLimit = 100;

    private IpcEndpointDescriptor(string pipeName, ScrapPathLayout paths)
    {
        PipeName = pipeName;
        Paths = paths;
    }

    /// <summary>获取传给 .NET named-pipe API 的名称。Gets the name passed to the .NET named-pipe API.</summary>
    public string PipeName { get; }

    /// <summary>获取 endpoint 所属路径布局。Gets the path layout that owns this endpoint.</summary>
    public ScrapPathLayout Paths { get; }

    /// <summary>
    /// 为指定 profile 创建确定性 endpoint。Creates a deterministic endpoint for a profile.
    /// </summary>
    /// <param name="paths">已共享给 client 与 daemon 的布局。The layout shared by client and daemon.</param>
    /// <returns>endpoint 描述符。The endpoint descriptor.</returns>
    public static IpcEndpointDescriptor Create(ScrapPathLayout paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        string identity = CurrentUserIdentity.GetStableId();
        bool isWindows = OperatingSystem.IsWindows();
        string pipeName = CreatePipeName(
            paths,
            identity,
            isWindows,
            Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"),
            isWindows ? Path.GetTempPath() : "/tmp");
        return new IpcEndpointDescriptor(pipeName, paths);
    }

    /// <summary>
    /// 为显式平台构造可测试的确定性 pipe name。
    /// Creates a testable deterministic pipe name for an explicit platform.
    /// </summary>
    /// <param name="paths">已规范化的 profile 布局。The normalized profile layout.</param>
    /// <param name="identity">稳定的当前用户标识。The stable current-user identity.</param>
    /// <param name="isWindows">是否应生成 Windows endpoint。Whether to generate a Windows endpoint.</param>
    /// <returns>传给 .NET named-pipe API 的名称。The name passed to the .NET named-pipe API.</returns>
    internal static string CreatePipeName(ScrapPathLayout paths, string identity, bool isWindows)
        => CreatePipeName(paths, identity, isWindows, xdgRuntimeDirectory: null, Path.GetTempPath());

    /// <summary>
    /// 使用显式运行时路径输入构造确定性 pipe name，以便无环境变量地测试 Unix 策略。
    /// Creates a deterministic pipe name from explicit runtime-path inputs so the Unix policy can be tested without environment variables.
    /// </summary>
    /// <param name="paths">已规范化的 profile 布局。The normalized profile layout.</param>
    /// <param name="identity">稳定的当前用户标识。The stable current-user identity.</param>
    /// <param name="isWindows">是否应生成 Windows endpoint。Whether to generate a Windows endpoint.</param>
    /// <param name="xdgRuntimeDirectory">可选的 XDG 运行时目录；只接受绝对路径。Optional XDG runtime directory; only absolute paths are accepted.</param>
    /// <param name="temporaryDirectory">XDG 不可用时的绝对临时目录。The absolute temporary directory used when XDG is unavailable.</param>
    /// <returns>传给 .NET named-pipe API 的名称。The name passed to the .NET named-pipe API.</returns>
    internal static string CreatePipeName(
        ScrapPathLayout paths,
        string identity,
        bool isWindows,
        string? xdgRuntimeDirectory,
        string temporaryDirectory)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        string profile = Path.GetFullPath(paths.RootDirectory);
        if (isWindows)
        {
            profile = profile.ToUpperInvariant();
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"scrap-ipc-v1\0{identity}\0{profile}"));
        string suffix = Convert.ToHexStringLower(hash.AsSpan(0, 16));

        if (isWindows)
        {
            return $@"LOCAL\scrap-{suffix}";
        }

        string runtimeDirectory = ResolveUnixRuntimeDirectory(identity, xdgRuntimeDirectory, temporaryDirectory);
        string socketPath = Path.Combine(runtimeDirectory, $"ipc-{suffix[..8]}.sock");
        int socketPathBytes = Encoding.UTF8.GetByteCount(socketPath);

        // A valid but deeply nested XDG directory is unusable for AF_UNIX. Falling back keeps the
        // common short-path case normal instead of making callers understand socket ABI limits.
        // 有效但层级过深的 XDG 目录无法用于 AF_UNIX。回退可避免让调用者理解 socket ABI 限制。
        if (socketPathBytes > UnixSocketPathConservativeLimit &&
            !string.IsNullOrWhiteSpace(xdgRuntimeDirectory) &&
            Path.IsPathFullyQualified(xdgRuntimeDirectory))
        {
            runtimeDirectory = ResolveUnixRuntimeDirectory(identity, xdgRuntimeDirectory: null, temporaryDirectory);
            socketPath = Path.Combine(runtimeDirectory, $"ipc-{suffix[..8]}.sock");
            socketPathBytes = Encoding.UTF8.GetByteCount(socketPath);
        }

        if (socketPathBytes > UnixSocketPathConservativeLimit)
        {
            throw new PlatformPathException($"The Unix IPC endpoint path is too long ({socketPathBytes} bytes; maximum supported is {UnixSocketPathConservativeLimit}).");
        }

        return socketPath;
    }

    /// <summary>
    /// 选择 Unix 运行时目录：优先私有 XDG 子目录，否则使用按稳定用户标识隔离的临时目录。
    /// Selects the Unix runtime directory: a private XDG child first, otherwise a temporary directory isolated by stable user identity.
    /// </summary>
    /// <param name="identity">稳定的当前用户标识。The stable current-user identity.</param>
    /// <param name="xdgRuntimeDirectory">可选的 XDG 运行时目录。The optional XDG runtime directory.</param>
    /// <param name="temporaryDirectory">回退临时目录。The fallback temporary directory.</param>
    /// <returns>规范化的绝对运行时目录。A normalized absolute runtime directory.</returns>
    internal static string ResolveUnixRuntimeDirectory(string identity, string? xdgRuntimeDirectory, string temporaryDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryDirectory);

        if (!string.IsNullOrWhiteSpace(xdgRuntimeDirectory) && Path.IsPathFullyQualified(xdgRuntimeDirectory))
        {
            return Path.Combine(Path.GetFullPath(xdgRuntimeDirectory), "scrap");
        }

        if (!Path.IsPathFullyQualified(temporaryDirectory))
        {
            throw new PlatformPathException("The temporary directory used for Unix IPC must be an absolute path.");
        }

        byte[] identityHash = SHA256.HashData(Encoding.UTF8.GetBytes($"scrap-runtime-v1\0{identity}"));
        string identitySuffix = Convert.ToHexStringLower(identityHash.AsSpan(0, 8));
        return Path.Combine(Path.GetFullPath(temporaryDirectory), $"scrap-{identitySuffix}");
    }

    /// <summary>
    /// 创建尚未连接的客户端 stream。Creates an unconnected client stream.
    /// </summary>
    /// <returns>调用者拥有且必须释放的 stream。A caller-owned stream that must be disposed.</returns>
    public NamedPipeClientStream CreateClientStream() =>
        new(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.None, HandleInheritability.None);

    /// <summary>
    /// 创建已绑定、等待连接的 server stream。Creates a bound server stream ready to await a connection.
    /// </summary>
    /// <param name="maxInstances">并发 server 实例上限。Maximum concurrent server instances.</param>
    /// <returns>调用者拥有且必须释放的 stream。A caller-owned stream that must be disposed.</returns>
    public NamedPipeServerStream CreateServerStream(int maxInstances = NamedPipeServerStream.MaxAllowedServerInstances)
    {
        Paths.Initialize();
        if (!OperatingSystem.IsWindows())
        {
            PlatformPathPermissions.EnsurePrivateDirectory(Path.GetDirectoryName(PipeName)!);
        }

        var stream = new NamedPipeServerStream(
            PipeName,
            PipeDirection.InOut,
            maxInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(PipeName, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        return stream;
    }
}
