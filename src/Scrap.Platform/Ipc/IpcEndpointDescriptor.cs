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
/// Windows 使用 <c>LOCAL\</c> app-container namespace 内的稳定哈希名称；Unix 使用 <c>.scrap/run</c> 内的
/// rooted pipe name，.NET 将其绑定为 Unix domain socket。
/// Windows uses a stable hashed name in the <c>LOCAL\</c> app-container namespace. Unix uses a rooted pipe name below
/// <c>.scrap/run</c>, which .NET binds as a Unix domain socket.
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
        string pipeName = CreatePipeName(paths, identity, isWindows);
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

        string socketPath = Path.Combine(paths.RunDirectory, $"ipc-{suffix[..8]}.sock");
        int socketPathBytes = Encoding.UTF8.GetByteCount(socketPath);
        if (socketPathBytes > UnixSocketPathConservativeLimit)
        {
            throw new PlatformPathException($"The Unix IPC endpoint path is too long ({socketPathBytes} bytes; maximum supported is {UnixSocketPathConservativeLimit}).");
        }

        return socketPath;
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
