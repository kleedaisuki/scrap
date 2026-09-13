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
}
