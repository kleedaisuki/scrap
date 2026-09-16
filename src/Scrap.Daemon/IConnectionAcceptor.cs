namespace Scrap.Daemon;

/// <summary>
/// 抽象当前用户 IPC 的连接接纳动作。 / Abstracts connection acceptance for current-user IPC.
/// </summary>
internal interface IConnectionAcceptor : IDisposable
{
    /// <summary>
    /// 同步创建首个已绑定的 listener。成功返回后 endpoint 必须可被 client 连接。
    /// / Synchronously creates one bound listener. After a successful return, clients must be able to connect to the endpoint.
    /// </summary>
    /// <remarks>
    /// 每次成功的 bind 必须在下一次 accept 前唯一；释放 acceptor 必须关闭尚未 accept 的 listener。
    /// / Each successful bind must be the only bind before the next accept; disposing the acceptor must close an unaccepted listener.
    /// </remarks>
    void Bind();

    /// <summary>
    /// 使用之前由 <see cref="Bind"/> 创建的 listener 接纳一个连接，并把 stream 所有权交给调用方。
    /// / Accepts one connection from the listener previously created by <see cref="Bind"/> and transfers stream ownership to the caller.
    /// </summary>
    /// <param name="cancellationToken">取消等待的令牌。 / Token that cancels the wait.</param>
    /// <returns>已连接的双向 stream。 / A connected duplex stream.</returns>
    ValueTask<Stream> AcceptAsync(CancellationToken cancellationToken);
}
