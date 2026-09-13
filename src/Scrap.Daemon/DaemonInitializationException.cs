namespace Scrap.Daemon;

/// <summary>
/// 表示已缓存且可通过稳定协议错误码报告的 daemon 初始化失败。
/// / Represents a cached daemon initialization failure reportable through a stable protocol error code.
/// </summary>
internal sealed class DaemonInitializationException : InvalidOperationException
{
    /// <summary>
    /// 初始化安全且不含底层 payload 的失败。 / Initializes a safe failure containing no underlying payload.
    /// </summary>
    /// <param name="errorCode">稳定协议错误码。 / Stable protocol error code.</param>
    /// <param name="message">安全诊断。 / Safe diagnostic.</param>
    public DaemonInitializationException(string errorCode, string message)
        : base(message) => ErrorCode = errorCode;

    /// <summary>获取稳定协议错误码。 / Gets the stable protocol error code.</summary>
    public string ErrorCode { get; }
}
