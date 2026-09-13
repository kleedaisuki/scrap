namespace Scrap.Platform.Paths;

/// <summary>
/// 表示平台路径无法安全创建或使用。Indicates that a platform path cannot be safely created or used.
/// </summary>
public sealed class PlatformPathException : IOException
{
    /// <summary>使用诊断消息创建异常。Creates an exception with a diagnostic message.</summary>
    /// <param name="message">不含秘密的诊断消息。A diagnostic message that contains no secret material.</param>
    public PlatformPathException(string message)
        : base(message)
    {
    }

    /// <summary>使用诊断消息和底层异常创建异常。Creates an exception with a diagnostic message and underlying exception.</summary>
    /// <param name="message">不含秘密的诊断消息。A diagnostic message that contains no secret material.</param>
    /// <param name="innerException">底层平台异常。The underlying platform exception.</param>
    public PlatformPathException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
