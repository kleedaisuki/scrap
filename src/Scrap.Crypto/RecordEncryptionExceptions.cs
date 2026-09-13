using System.Security.Cryptography;

namespace Scrap.Crypto;

/// <summary>
/// 表示持久化 ciphertext/nonce 格式无效或不受支持。
/// Indicates malformed or unsupported persisted ciphertext/nonce format.
/// </summary>
public sealed class EncryptedRecordFormatException : CryptographicException
{
    /// <summary>创建不含 record 元数据的格式异常。Creates a format exception without record metadata.</summary>
    /// <param name="message">安全诊断消息。Safe diagnostic message.</param>
    public EncryptedRecordFormatException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// 表示 ciphertext、nonce 或 AAD 上下文未通过鉴权，即数据已损坏或绑定错误。
/// Indicates that ciphertext, nonce, or AAD context failed authentication and is corrupted or incorrectly bound.
/// </summary>
public sealed class RecordAuthenticationException : CryptographicException
{
    /// <summary>创建不泄露 value/scope/key 的鉴权异常。Creates an authentication exception that reveals no value, scope, or key.</summary>
    /// <param name="innerException">底层平台加密异常。Underlying platform cryptographic exception.</param>
    public RecordAuthenticationException(Exception innerException)
        : base("The encrypted record failed authentication and must be treated as corrupted.", innerException)
    {
    }
}
