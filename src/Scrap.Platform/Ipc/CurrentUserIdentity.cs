using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace Scrap.Platform.Ipc;

/// <summary>
/// 提供适合本地 endpoint 隔离的当前用户稳定标识。
/// Provides a stable current-user identity suitable for local endpoint isolation.
/// </summary>
public static class CurrentUserIdentity
{
    /// <summary>
    /// 获取 Windows SID 或 Unix 有效用户 ID。Gets the Windows SID or Unix effective user ID.
    /// </summary>
    /// <returns>带平台前缀的稳定标识；它不是身份验证令牌。A platform-prefixed stable identifier; it is not an authentication token.</returns>
    public static string GetStableId()
    {
        if (OperatingSystem.IsWindows())
        {
            return GetWindowsSid();
        }

        return $"uid:{GetEffectiveUserId()}";
    }

    [SupportedOSPlatform("windows")]
    private static string GetWindowsSid()
    {
        string sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("The current Windows user has no SID.");
        return $"sid:{sid}";
    }

    [UnsupportedOSPlatform("windows")]
    private static uint GetEffectiveUserId() => GetEuid();

    [DllImport("libc", EntryPoint = "geteuid")]
    [UnsupportedOSPlatform("windows")]
    private static extern uint GetEuid();
}
