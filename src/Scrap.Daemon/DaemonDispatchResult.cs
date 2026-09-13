using Scrap.Protocol;

namespace Scrap.Daemon;

/// <summary>
/// 表示一个响应以及写回响应后是否应启动优雅关闭。 / Represents a response and whether graceful shutdown should begin after it is written.
/// </summary>
/// <param name="Response">结构化协议响应。 / Structured protocol response.</param>
/// <param name="RequestsShutdown">是否在响应写回后停止。 / Whether to stop after writing the response.</param>
internal sealed record DaemonDispatchResult(ProtocolResponse Response, bool RequestsShutdown = false);
