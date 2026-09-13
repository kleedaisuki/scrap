namespace Scrap.Platform.Paths;

/// <summary>
/// 定义单个用户 profile 的标准 <c>.scrap</c> 路径布局。
/// Defines the canonical <c>.scrap</c> path layout for one user profile.
/// </summary>
/// <remarks>
/// 所有派生路径都是规范化绝对路径；调用 <see cref="Initialize"/> 会创建私有目录，但不会创建数据库或配置文件。
/// Every derived path is a normalized absolute path. <see cref="Initialize"/> creates private directories,
/// but does not create the database or configuration file.
/// </remarks>
public sealed class ScrapPathLayout
{
    /// <summary>
    /// 使用显式根目录创建布局。Creates a layout rooted at an explicit directory.
    /// </summary>
    /// <param name="rootDirectory">profile 根目录。The profile root directory.</param>
    public ScrapPathLayout(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
        BinDirectory = Path.Combine(RootDirectory, "bin");
        DataDirectory = Path.Combine(RootDirectory, "data");
        RunDirectory = Path.Combine(RootDirectory, "run");
        LogDirectory = Path.Combine(RootDirectory, "log");
        ConfigurationFile = Path.Combine(RootDirectory, "config.json");
        DatabaseFile = Path.Combine(DataDirectory, "scrap.db");
        KeyReferenceFile = Path.Combine(DataDirectory, "key-reference");
        DaemonLockFile = Path.Combine(RunDirectory, "daemon.lock");
        IpcMetadataFile = Path.Combine(RunDirectory, "ipc.endpoint");
        LogFile = Path.Combine(LogDirectory, "scrap.log");
    }

    /// <summary>获取 profile 根目录。Gets the profile root directory.</summary>
    public string RootDirectory { get; }

    /// <summary>获取可再生二进制目录。Gets the reproducible binary directory.</summary>
    public string BinDirectory { get; }

    /// <summary>获取关键数据目录。Gets the critical data directory.</summary>
    public string DataDirectory { get; }

    /// <summary>获取临时运行状态目录。Gets the transient runtime-state directory.</summary>
    public string RunDirectory { get; }

    /// <summary>获取日志目录。Gets the log directory.</summary>
    public string LogDirectory { get; }

    /// <summary>获取配置文件路径。Gets the configuration file path.</summary>
    public string ConfigurationFile { get; }

    /// <summary>获取 SQLite 数据库路径。Gets the SQLite database path.</summary>
    public string DatabaseFile { get; }

    /// <summary>获取平台密钥引用路径。Gets the platform-key reference path.</summary>
    public string KeyReferenceFile { get; }

    /// <summary>获取 daemon 单实例锁路径。Gets the daemon single-instance lock path.</summary>
    public string DaemonLockFile { get; }

    /// <summary>获取 IPC endpoint 元数据路径。Gets the IPC endpoint metadata path.</summary>
    public string IpcMetadataFile { get; }

    /// <summary>获取日志文件路径。Gets the log file path.</summary>
    public string LogFile { get; }

    /// <summary>
    /// 为当前操作系统用户创建默认布局。Creates the default layout for the current OS user.
    /// </summary>
    /// <param name="userProfileDirectory">
    /// 可测试的用户主目录替代值；为空时读取操作系统 profile。
    /// Test seam for the user home directory; when omitted, the OS profile is used.
    /// </param>
    /// <returns>当前用户的路径布局。The current user's path layout.</returns>
    public static ScrapPathLayout ForCurrentUser(string? userProfileDirectory = null)
    {
        string home = userProfileDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
        {
            throw new PlatformPathException("The operating system did not provide a user profile directory.");
        }

        return new ScrapPathLayout(Path.Combine(home, ".scrap"));
    }

    /// <summary>
    /// 创建布局目录并施加平台私有权限。Creates layout directories and applies platform-private permissions.
    /// </summary>
    /// <exception cref="PlatformPathException">路径是链接、权限无法设置或目录无法创建。The path is a link, cannot be secured, or cannot be created.</exception>
    public void Initialize()
    {
        PlatformPathPermissions.EnsurePrivateDirectory(RootDirectory);
        PlatformPathPermissions.EnsurePrivateDirectory(BinDirectory);
        PlatformPathPermissions.EnsurePrivateDirectory(DataDirectory);
        PlatformPathPermissions.EnsurePrivateDirectory(RunDirectory);
        PlatformPathPermissions.EnsurePrivateDirectory(LogDirectory);
    }
}
