namespace Scrap.Client;

/// <summary>
/// 表示本地 daemon 连接或启动失败。 / Represents a failure to connect to or start the local daemon.
/// </summary>
public sealed class ScrapConnectionException : Exception
{
    /// <summary>
    /// 使用诊断消息初始化异常。 / Initializes the exception with a diagnostic message.
    /// </summary>
    /// <param name="message">不含秘密值的诊断消息。 / A diagnostic message that contains no secret value.</param>
    public ScrapConnectionException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// 使用诊断消息和根因初始化异常。 / Initializes the exception with a diagnostic message and root cause.
    /// </summary>
    /// <param name="message">不含秘密值的诊断消息。 / A diagnostic message that contains no secret value.</param>
    /// <param name="innerException">底层连接或进程错误。 / The underlying connection or process error.</param>
    public ScrapConnectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// 表示 client 与 daemon 没有共同支持的协议版本。 / Represents the absence of a protocol version shared by the client and daemon.
/// </summary>
public sealed class ScrapProtocolVersionException : Exception
{
    /// <summary>
    /// 初始化协议版本不兼容异常。 / Initializes a protocol-version incompatibility exception.
    /// </summary>
    /// <param name="clientVersion">client 使用的协议版本。 / The protocol version used by the client.</param>
    /// <param name="minimumDaemonVersion">daemon 支持的最低版本。 / The minimum version supported by the daemon.</param>
    /// <param name="maximumDaemonVersion">daemon 支持的最高版本。 / The maximum version supported by the daemon.</param>
    public ScrapProtocolVersionException(int clientVersion, int minimumDaemonVersion, int maximumDaemonVersion)
        : base($"Protocol version {clientVersion} is outside daemon range [{minimumDaemonVersion}, {maximumDaemonVersion}].")
    {
        ClientVersion = clientVersion;
        MinimumDaemonVersion = minimumDaemonVersion;
        MaximumDaemonVersion = maximumDaemonVersion;
    }

    /// <summary>获取 client 协议版本。 / Gets the client protocol version.</summary>
    public int ClientVersion { get; }

    /// <summary>获取 daemon 支持的最低版本。 / Gets the minimum protocol version supported by the daemon.</summary>
    public int MinimumDaemonVersion { get; }

    /// <summary>获取 daemon 支持的最高版本。 / Gets the maximum protocol version supported by the daemon.</summary>
    public int MaximumDaemonVersion { get; }
}
