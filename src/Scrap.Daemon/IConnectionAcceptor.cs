namespace Scrap.Daemon;

/// <summary>
/// 抽象当前用户 IPC 的连接接纳动作。 / Abstracts connection acceptance for current-user IPC.
/// </summary>
internal interface IConnectionAcceptor
{
    /// <summary>
    /// 接纳一个连接并把 stream 所有权交给调用方。 / Accepts one connection and transfers stream ownership to the caller.
    /// </summary>
    /// <param name="cancellationToken">取消等待的令牌。 / Token that cancels the wait.</param>
    /// <returns>已连接的双向 stream。 / A connected duplex stream.</returns>
    ValueTask<Stream> AcceptAsync(CancellationToken cancellationToken);
}
