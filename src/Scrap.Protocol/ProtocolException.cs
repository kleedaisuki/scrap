namespace Scrap.Protocol;

/// <summary>
/// 表示本地检测到的协议或 framing 错误。
/// / Represents a locally detected protocol or framing error.
/// </summary>
public class ProtocolException : Exception
{
    /// <summary>
    /// 初始化协议异常。 / Initializes a protocol exception.
    /// </summary>
    /// <param name="errorCode">来自 <see cref="ProtocolErrorCodes"/> 的稳定错误码。 / Stable error code from <see cref="ProtocolErrorCodes"/>.</param>
    /// <param name="message">不得包含完整 payload 的诊断消息。 / Diagnostic message that must not contain the full payload.</param>
    public ProtocolException(string errorCode, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrEmpty(errorCode);
        ErrorCode = errorCode;
    }

    /// <summary>
    /// 初始化带底层原因的协议异常。 / Initializes a protocol exception with an underlying cause.
    /// </summary>
    /// <param name="errorCode">稳定错误码。 / Stable error code.</param>
    /// <param name="message">不含敏感内容的诊断消息。 / Non-sensitive diagnostic message.</param>
    /// <param name="innerException">底层异常。 / Underlying exception.</param>
    public ProtocolException(string errorCode, string message, Exception innerException)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrEmpty(errorCode);
        ArgumentNullException.ThrowIfNull(innerException);
        ErrorCode = errorCode;
    }

    /// <summary>
    /// 获取稳定的机器可读错误码。 / Gets the stable machine-readable error code.
    /// </summary>
    public string ErrorCode { get; }
}

/// <summary>
/// 表示远端返回的结构化协议错误。 / Represents a structured error returned by the remote endpoint.
/// </summary>
public sealed class RemoteProtocolException : ProtocolException
{
    /// <summary>
    /// 初始化远端协议异常。 / Initializes a remote protocol exception.
    /// </summary>
    /// <param name="error">远端结构化错误。 / Structured remote error.</param>
    public RemoteProtocolException(ProtocolError error)
        : base(
            error?.Code ?? throw new ArgumentNullException(nameof(error)),
            error.Message)
    {
        Error = error;
    }

    /// <summary>
    /// 获取原始结构化错误。 / Gets the original structured error.
    /// </summary>
    public ProtocolError Error { get; }
}
