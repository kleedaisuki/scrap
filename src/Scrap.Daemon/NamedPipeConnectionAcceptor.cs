using Scrap.Platform.Ipc;

namespace Scrap.Daemon;

/// <summary>
/// 为每个 client 创建独立的 current-user-only named-pipe server 实例。
/// / Creates an independent current-user-only named-pipe server instance for each client.
/// </summary>
internal sealed class NamedPipeConnectionAcceptor : IConnectionAcceptor
{
    private readonly IpcEndpointDescriptor endpoint;

    /// <summary>
    /// 初始化 named-pipe acceptor。调用方必须已取得单实例租约。
    /// / Initializes the named-pipe acceptor. The caller must already hold the singleton lease.
    /// </summary>
    /// <param name="endpoint">当前 profile endpoint。 / Endpoint for the current profile.</param>
    public NamedPipeConnectionAcceptor(IpcEndpointDescriptor endpoint) => this.endpoint = endpoint;

    /// <inheritdoc />
    public async ValueTask<Stream> AcceptAsync(CancellationToken cancellationToken)
    {
        var stream = endpoint.CreateServerStream();
        try
        {
            await stream.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
