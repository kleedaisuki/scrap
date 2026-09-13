namespace Scrap.Protocol;

/// <summary>
/// 定义稳定的 IPC 协议常量。 / Defines stable IPC protocol constants.
/// </summary>
public static class ProtocolConstants
{
    /// <summary>
    /// 当前主协议版本。 / Gets the current major protocol version.
    /// </summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// 默认最大 JSON payload 大小（1 MiB）。 / Gets the default maximum JSON payload size (1 MiB).
    /// </summary>
    public const int DefaultMaxFrameSize = 1024 * 1024;

    /// <summary>
    /// 长度前缀的固定字节数。 / Gets the fixed length-prefix size in bytes.
    /// </summary>
    public const int FrameHeaderSize = sizeof(uint);

    /// <summary>
    /// <c>scope.list</c> 未指定 limit 时使用的默认页大小。
    /// / Gets the default page size used when <c>scope.list</c> does not specify a limit.
    /// </summary>
    public const int DefaultScopeListPageSize = 100;

    /// <summary>
    /// <c>scope.list</c> 单页允许的最大 scope 数量。
    /// / Gets the maximum number of scopes permitted in one <c>scope.list</c> page.
    /// </summary>
    public const int MaxScopeListPageSize = 500;

    /// <summary>
    /// <c>record.list</c> 未指定 limit 时使用的默认页大小。
    /// / Gets the default page size used when <c>record.list</c> does not specify a limit.
    /// </summary>
    public const int DefaultRecordListPageSize = 100;

    /// <summary>
    /// <c>record.list</c> 单页允许的最大 record 数量。
    /// / Gets the maximum number of records permitted in one <c>record.list</c> page.
    /// </summary>
    public const int MaxRecordListPageSize = 500;
}
