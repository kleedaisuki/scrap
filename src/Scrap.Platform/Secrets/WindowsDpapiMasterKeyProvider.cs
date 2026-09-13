using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Scrap.Platform.Paths;

namespace Scrap.Platform.Secrets;

/// <summary>
/// 使用 Windows DPAPI <see cref="DataProtectionScope.CurrentUser"/> 保护主密钥，并仅持久化密文 blob。
/// Protects the master key with Windows DPAPI <see cref="DataProtectionScope.CurrentUser"/> and persists only the ciphertext blob.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDpapiMasterKeyProvider : IMasterKeyProvider
{
    private const int MaximumProtectedBlobLength = 64 * 1024;
    private readonly string _protectedKeyFile;
    private readonly byte[] _entropy;

    /// <summary>创建 DPAPI provider。Creates a DPAPI provider.</summary>
    /// <param name="protectedKeyFile">DPAPI 密文 blob 文件。File holding the DPAPI-protected blob.</param>
    /// <param name="serviceName">域分离服务名。Domain-separation service name.</param>
    /// <param name="accountName">profile 的非秘密稳定标识。Non-secret stable profile identifier.</param>
    public WindowsDpapiMasterKeyProvider(string protectedKeyFile, string serviceName, string accountName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedKeyFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        _protectedKeyFile = Path.GetFullPath(protectedKeyFile);
        _entropy = SHA256.HashData(Encoding.UTF8.GetBytes($"scrap-dpapi-v1\0{serviceName}\0{accountName}"));
    }

    /// <inheritdoc />
    public async ValueTask<byte[]?> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(_protectedKeyFile))
        {
            return null;
        }

        try
        {
            PlatformPathPermissions.EnsurePrivateFile(_protectedKeyFile);
            var info = new FileInfo(_protectedKeyFile);
            if (info.Length is <= 0 or > MaximumProtectedBlobLength)
            {
                throw new CryptographicException("The protected master-key blob has an invalid length.");
            }

            byte[] protectedKey = await File.ReadAllBytesAsync(_protectedKeyFile, cancellationToken).ConfigureAwait(false);
            try
            {
                return ProtectedData.Unprotect(protectedKey, _entropy, DataProtectionScope.CurrentUser);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedKey);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or PlatformPathException)
        {
            throw new MasterKeyProviderException("Windows DPAPI", "load", "The DPAPI-protected Scrap master key could not be loaded.", exception);
        }
    }

    /// <inheritdoc />
    public async ValueTask StoreAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default)
    {
        if (key.IsEmpty)
        {
            throw new ArgumentException("The master key cannot be empty.", nameof(key));
        }

        cancellationToken.ThrowIfCancellationRequested();
        byte[] protectedKey;
        byte[] rawKey = key.ToArray();
        try
        {
            protectedKey = ProtectedData.Protect(rawKey, _entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException exception)
        {
            throw new MasterKeyProviderException("Windows DPAPI", "store", "Windows DPAPI could not protect the Scrap master key.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rawKey);
        }

        string? directory = Path.GetDirectoryName(_protectedKeyFile);
        string temporaryFile = $"{_protectedKeyFile}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            PlatformPathPermissions.EnsurePrivateDirectory(directory!);
            await using (var stream = new FileStream(temporaryFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(protectedKey, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            PlatformPathPermissions.EnsurePrivateFile(temporaryFile);
            File.Move(temporaryFile, _protectedKeyFile, overwrite: true);
            PlatformPathPermissions.EnsurePrivateFile(_protectedKeyFile);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformPathException)
        {
            throw new MasterKeyProviderException("Windows DPAPI", "store", "The DPAPI-protected Scrap master key could not be persisted.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedKey);
            try
            {
                File.Delete(temporaryFile);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The successfully moved file no longer exists; a failed temporary cleanup is non-destructive.
            }
        }
    }
}
