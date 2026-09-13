using System.Security.Cryptography;

namespace Scrap.Crypto;

/// <summary>
/// 表示既有 store 的 OS-protected 主密钥缺失或格式损坏。
/// Indicates that an existing store's OS-protected master key is missing or malformed.
/// </summary>
public sealed class MasterKeyUnavailableException : CryptographicException
{
    /// <summary>创建不泄露密钥材料的异常。Creates an exception that reveals no key material.</summary>
    /// <param name="message">可操作诊断消息。Actionable diagnostic message.</param>
    public MasterKeyUnavailableException(string message)
        : base(message)
    {
    }
}
