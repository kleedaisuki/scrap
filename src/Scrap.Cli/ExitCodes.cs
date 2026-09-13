namespace Scrap.Cli;

/// <summary>
/// 稳定的 CLI 退出码 userspace contract。/ Stable CLI exit-code userspace contract.
/// </summary>
public static class ExitCodes
{
    /// <summary>成功。/ Success.</summary>
    public const int Success = 0;
    /// <summary>参数或用法错误。/ Argument or usage error.</summary>
    public const int Usage = 2;
    /// <summary>scope 或 record 不存在。/ Scope or record not found.</summary>
    public const int NotFound = 3;
    /// <summary>唯一性或并发冲突。/ Uniqueness or concurrency conflict.</summary>
    public const int Conflict = 4;
    /// <summary>daemon、IPC 或协议失败。/ Daemon, IPC, or protocol failure.</summary>
    public const int Protocol = 5;
    /// <summary>store、密钥或解密不可用。/ Store, key, or decryption unavailable.</summary>
    public const int Store = 6;
    /// <summary>输入或查询校验失败。/ Input or query validation failure.</summary>
    public const int Validation = 7;
}
