namespace Scrap.Crypto;

/// <summary>
/// 对 record value 提供带上下文绑定的 authenticated encryption。
/// Provides context-bound authenticated encryption for record values.
/// </summary>
public interface IRecordEncryptor : IDisposable
{
    /// <summary>加密完整明文。Encrypts an entire plaintext value.</summary>
    /// <param name="plaintext">仅在调用期间借用的明文。Plaintext borrowed only during the call.</param>
    /// <param name="context">参与 AAD 的 record 上下文。Record context included in AAD.</param>
    /// <returns>具有新随机 nonce 的加密值。An encrypted value with a fresh random nonce.</returns>
    EncryptedRecordValue Encrypt(ReadOnlySpan<byte> plaintext, in RecordEncryptionContext context);

    /// <summary>在完整鉴权成功后返回明文。Returns plaintext only after complete authentication succeeds.</summary>
    /// <param name="encryptedValue">版本化 opaque ciphertext envelope。Versioned opaque ciphertext envelope.</param>
    /// <param name="nonce">持久化的 nonce。Persisted nonce.</param>
    /// <param name="context">必须与加密时逐字节等价的上下文。Context that must be byte-equivalent to that used for encryption.</param>
    /// <returns>由调用者拥有并应尽快清零的明文。Caller-owned plaintext that should be zeroed promptly.</returns>
    byte[] Decrypt(ReadOnlySpan<byte> encryptedValue, ReadOnlySpan<byte> nonce, in RecordEncryptionContext context);
}
