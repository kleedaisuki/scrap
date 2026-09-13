namespace Scrap.Protocol;

/// <summary>
/// 表示 <c>daemon.ping</c> 的空参数。 / Represents the empty parameters for <c>daemon.ping</c>.
/// </summary>
public sealed record DaemonPingParams;

/// <summary>
/// 表示 <c>daemon.ping</c> 的空结果；收到合法响应即证明 daemon 活跃。
/// / Represents the empty result of <c>daemon.ping</c>; receiving a valid response proves daemon liveness.
/// </summary>
public sealed record DaemonPingResult;

/// <summary>
/// 表示 <c>daemon.version</c> 的空参数。 / Represents the empty parameters for <c>daemon.version</c>.
/// </summary>
public sealed record DaemonVersionParams;

/// <summary>
/// 表示 daemon 应用版本及其支持的连续主协议版本范围。
/// / Represents the daemon application version and its supported contiguous major-protocol range.
/// </summary>
/// <param name="ApplicationVersion">daemon 应用版本。 / Daemon application version.</param>
/// <param name="MinProtocolVersion">支持的最小主协议版本（含）。 / Minimum supported major protocol version, inclusive.</param>
/// <param name="MaxProtocolVersion">支持的最大主协议版本（含）。 / Maximum supported major protocol version, inclusive.</param>
public sealed record DaemonVersionResult(
    string ApplicationVersion,
    int MinProtocolVersion,
    int MaxProtocolVersion);

/// <summary>
/// 表示 <c>daemon.shutdown</c> 的空参数。 / Represents the empty parameters for <c>daemon.shutdown</c>.
/// </summary>
public sealed record DaemonShutdownParams;

/// <summary>
/// 表示 daemon 已接受优雅关闭请求。 / Represents daemon acceptance of a graceful shutdown request.
/// </summary>
public sealed record DaemonShutdownResult;
