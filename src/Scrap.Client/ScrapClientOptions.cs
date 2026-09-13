namespace Scrap.Client;

/// <summary>
/// 配置客户端的连接、启动与 framing 行为。 / Configures client connection, startup, and framing behavior.
/// </summary>
public sealed record ScrapClientOptions
{
    /// <summary>
    /// 获取可选的 daemon 可执行文件路径；为空时依次探测应用目录与 <c>~/.scrap/bin</c>。
    /// / Gets an optional daemon executable path; when absent, the application directory and <c>~/.scrap/bin</c> are probed in order.
    /// </summary>
    public string? DaemonExecutablePath { get; init; }

    /// <summary>
    /// 获取首次探测已运行 daemon 的连接超时。 / Gets the connection timeout used to probe an already-running daemon.
    /// </summary>
    public TimeSpan InitialConnectTimeout { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// 获取启动 daemon 后等待就绪的总时限。 / Gets the total time allowed for a spawned daemon to become ready.
    /// </summary>
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 获取就绪探测之间的初始延迟。 / Gets the initial delay between readiness probes.
    /// </summary>
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// 获取就绪探测之间的最大延迟。 / Gets the maximum delay between readiness probes.
    /// </summary>
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// 获取单个 JSON frame 的最大 payload 字节数。 / Gets the maximum JSON payload size, in bytes, for one frame.
    /// </summary>
    public int MaxFrameSize { get; init; } = Scrap.Protocol.ProtocolConstants.DefaultMaxFrameSize;

    /// <summary>
    /// 验证选项并返回当前实例。 / Validates the options and returns this instance.
    /// </summary>
    /// <returns>已验证的选项。 / The validated options.</returns>
    /// <exception cref="ArgumentOutOfRangeException">任一时限、延迟或 frame 大小无效。 / A timeout, delay, or frame size is invalid.</exception>
    internal ScrapClientOptions Validate()
    {
        if (DaemonExecutablePath is not null && string.IsNullOrWhiteSpace(DaemonExecutablePath))
        {
            throw new ArgumentException("The daemon executable path cannot be empty.", nameof(DaemonExecutablePath));
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(InitialConnectTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(StartupTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(InitialRetryDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxRetryDelay, InitialRetryDelay);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MaxFrameSize, 0);
        return this;
    }
}
