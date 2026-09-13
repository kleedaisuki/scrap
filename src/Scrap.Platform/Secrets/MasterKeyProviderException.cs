namespace Scrap.Platform.Secrets;

/// <summary>
/// 表示平台密钥 provider 已安装但不可用、被锁定或操作失败。
/// Indicates that a platform key provider is unavailable, locked, or failed an operation.
/// </summary>
public sealed class MasterKeyProviderException : InvalidOperationException
{
    /// <summary>创建 provider 异常。Creates a provider exception.</summary>
    /// <param name="providerName">稳定 provider 名称。Stable provider name.</param>
    /// <param name="operation">失败操作。The failed operation.</param>
    /// <param name="message">不含密钥材料的消息。A message containing no key material.</param>
    /// <param name="innerException">可选底层异常。Optional underlying exception.</param>
    public MasterKeyProviderException(string providerName, string operation, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ProviderName = providerName;
        Operation = operation;
    }

    /// <summary>获取 provider 名称。Gets the provider name.</summary>
    public string ProviderName { get; }

    /// <summary>获取失败操作。Gets the failed operation.</summary>
    public string Operation { get; }
}
