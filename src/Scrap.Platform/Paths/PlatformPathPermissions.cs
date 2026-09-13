using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Scrap.Platform.Paths;

/// <summary>
/// 为 Scrap 的本地状态施加最小的用户私有权限。
/// Applies minimal user-private permissions to Scrap local state.
/// </summary>
public static class PlatformPathPermissions
{
    private const UnixFileMode PrivateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>
    /// 创建目录、拒绝链接，并在 Unix 上设置 <c>0700</c>，在 Windows 上设置当前用户/SYSTEM ACL。
    /// Creates a directory, rejects links, and sets <c>0700</c> on Unix or a current-user/SYSTEM ACL on Windows.
    /// </summary>
    /// <param name="path">待保护目录。The directory to protect.</param>
    public static void EnsurePrivateDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            RejectLinkIfPresent(path);
            Directory.CreateDirectory(path);
            RejectLinkIfPresent(path);

            if (OperatingSystem.IsWindows())
            {
                ApplyWindowsDirectoryAcl(path);
                return;
            }

            File.SetUnixFileMode(path, PrivateDirectoryMode);
        }
        catch (PlatformPathException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new PlatformPathException($"Could not create or secure private directory '{path}'.", exception);
        }
    }

    /// <summary>
    /// 拒绝链接，并在 Unix 上设置文件 <c>0600</c>，在 Windows 上设置当前用户/SYSTEM ACL。
    /// Rejects links and sets file mode <c>0600</c> on Unix or a current-user/SYSTEM ACL on Windows.
    /// </summary>
    /// <param name="path">必须已存在的文件。The file, which must already exist.</param>
    public static void EnsurePrivateFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            RejectLinkIfPresent(path);
            if (!File.Exists(path))
            {
                throw new PlatformPathException($"Private file '{path}' does not exist.");
            }

            if (OperatingSystem.IsWindows())
            {
                ApplyWindowsFileAcl(path);
                return;
            }

            File.SetUnixFileMode(path, PrivateFileMode);
        }
        catch (PlatformPathException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new PlatformPathException($"Could not secure private file '{path}'.", exception);
        }
    }

    private static void RejectLinkIfPresent(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return;
        }

        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new PlatformPathException($"Refusing linked or reparse-point path '{path}'.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyWindowsDirectoryAcl(string path)
    {
        SecurityIdentifier user = WindowsIdentity.GetCurrent().User
            ?? throw new PlatformPathException("The current Windows user has no security identifier.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(user);
        security.AddAccessRule(CreateRule(user, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit));
        security.AddAccessRule(CreateRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyWindowsFileAcl(string path)
    {
        SecurityIdentifier user = WindowsIdentity.GetCurrent().User
            ?? throw new PlatformPathException("The current Windows user has no security identifier.");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(user);
        security.AddAccessRule(CreateRule(user, InheritanceFlags.None));
        security.AddAccessRule(CreateRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), InheritanceFlags.None));
        new FileInfo(path).SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static FileSystemAccessRule CreateRule(SecurityIdentifier identity, InheritanceFlags inheritanceFlags) =>
        new(identity, FileSystemRights.FullControl, inheritanceFlags, PropagationFlags.None, AccessControlType.Allow);
}
