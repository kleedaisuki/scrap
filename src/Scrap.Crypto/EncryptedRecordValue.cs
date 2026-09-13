namespace Scrap.Crypto;

/// <summary>
/// 表示可直接持久化到 SQLite 的 opaque ciphertext envelope 与 nonce。
/// Represents an opaque ciphertext envelope and nonce ready for SQLite persistence.
/// </summary>
public sealed class EncryptedRecordValue
{
    /// <summary>创建加密值并取得传入数组的所有权。Creates an encrypted value and takes ownership of the supplied arrays.</summary>
    /// <param name="ciphertext">包含格式头、tag 和密文的 opaque envelope。Opaque envelope containing format header, tag, and ciphertext.</param>
    /// <param name="nonce">该次写入唯一的随机 nonce。Random nonce unique to this write.</param>
    public EncryptedRecordValue(byte[] ciphertext, byte[] nonce)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        ArgumentNullException.ThrowIfNull(nonce);
        Ciphertext = ciphertext;
        Nonce = nonce;
    }

    /// <summary>获取版本化 opaque ciphertext envelope。Gets the versioned opaque ciphertext envelope.</summary>
    public byte[] Ciphertext { get; }

    /// <summary>获取随机 nonce。Gets the random nonce.</summary>
    public byte[] Nonce { get; }
}
