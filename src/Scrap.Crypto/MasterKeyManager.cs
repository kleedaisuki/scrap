using System.Security.Cryptography;
using Scrap.Platform.Secrets;

namespace Scrap.Crypto;

/// <summary>
/// 协调 OS-protected 256-bit 主密钥的打开或首次初始化。
/// Coordinates opening or first-time initialization of an OS-protected 256-bit master key.
/// </summary>
public static class MasterKeyManager
{
    /// <summary>
    /// 加载主密钥并创建 record encryptor；只有 <paramref name="access"/> 明确为新 store 时才生成 key。
    /// Loads the master key and creates a record encryptor; a key is generated only when <paramref name="access"/> explicitly denotes a fresh store.
    /// </summary>
    /// <param name="provider">平台主密钥 provider。Platform master-key provider.</param>
    /// <param name="access">由 daemon 根据 store 初始化状态做出的决定。Decision made by the daemon from store initialization state.</param>
    /// <param name="cancellationToken">取消令牌。Cancellation token.</param>
    /// <returns>拥有主密钥副本、必须释放的 encryptor。A disposable encryptor owning a master-key copy.</returns>
    /// <remarks>
    /// daemon 必须先取得 profile 单实例 lease，并确认没有数据库/WAL/schema，再传入 <see cref="MasterKeyAccess.InitializeNew"/>。
    /// The daemon must first own the profile lease and confirm there is no database/WAL/schema before passing <see cref="MasterKeyAccess.InitializeNew"/>.
    /// </remarks>
    public static async ValueTask<IRecordEncryptor> CreateEncryptorAsync(
        IMasterKeyProvider provider,
        MasterKeyAccess access,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (access is not MasterKeyAccess.OpenExisting and not MasterKeyAccess.InitializeNew)
        {
            throw new ArgumentOutOfRangeException(nameof(access));
        }

        byte[]? key = await provider.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (key is null)
        {
            if (access != MasterKeyAccess.InitializeNew)
            {
                throw new MasterKeyUnavailableException("The existing Scrap store has no OS-protected master key; restore its platform key material before opening it.");
            }

            key = RandomNumberGenerator.GetBytes(AesGcmRecordEncryptor.KeySize);
            try
            {
                await provider.StoreAsync(key, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                CryptographicOperations.ZeroMemory(key);
                throw;
            }
        }

        try
        {
            if (key.Length != AesGcmRecordEncryptor.KeySize)
            {
                throw new MasterKeyUnavailableException($"The OS-protected Scrap master key has an invalid length; expected {AesGcmRecordEncryptor.KeySize} bytes.");
            }

            return new AesGcmRecordEncryptor(key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
