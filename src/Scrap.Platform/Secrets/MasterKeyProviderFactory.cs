using System.Security.Cryptography;
using System.Text;
using Scrap.Platform.Ipc;
using Scrap.Platform.Paths;

namespace Scrap.Platform.Secrets;

/// <summary>为当前 OS 选择唯一受支持的主密钥 provider。Selects the single supported master-key provider for the current OS.</summary>
public static class MasterKeyProviderFactory
{
    /// <summary>
    /// 创建绑定当前用户和 profile 的 provider。Creates a provider bound to the current user and profile.
    /// </summary>
    /// <param name="paths">profile 路径。Profile paths.</param>
    /// <param name="serviceName">OS secret store 中的稳定服务名。Stable service name in the OS secret store.</param>
    /// <returns>平台 provider；不会退化为明文文件。The platform provider; it never falls back to a plaintext file.</returns>
    public static IMasterKeyProvider Create(ScrapPathLayout paths, string serviceName = "io.scrap.master-key")
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        string material = $"{CurrentUserIdentity.GetStableId()}\0{Path.GetFullPath(paths.RootDirectory)}";
        string account = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)).AsSpan(0, 16));

        if (OperatingSystem.IsWindows())
        {
            return new WindowsDpapiMasterKeyProvider(paths.KeyReferenceFile, serviceName, account);
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacKeychainMasterKeyProvider(serviceName, account);
        }

        if (OperatingSystem.IsLinux())
        {
            return new LinuxLibSecretMasterKeyProvider(serviceName, account);
        }

        throw new PlatformNotSupportedException("Scrap master-key storage supports Windows, macOS, and Linux only.");
    }
}
