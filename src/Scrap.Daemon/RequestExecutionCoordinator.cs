namespace Scrap.Daemon;

/// <summary>
/// 允许 read 并发执行，并把 mutation 的提交路径串行化。 / Allows concurrent reads while serializing mutation commit paths.
/// </summary>
internal sealed class RequestExecutionCoordinator : IDisposable
{
    private readonly SemaphoreSlim mutationGate = new(1, 1);
    private readonly DaemonRuntimeState state;
    private bool disposed;

    /// <summary>
    /// 初始化请求执行协调器。 / Initializes a request execution coordinator.
    /// </summary>
    /// <param name="state">共享运行状态。 / Shared runtime state.</param>
    public RequestExecutionCoordinator(DaemonRuntimeState state) => this.state = state;

    /// <summary>
    /// 执行无副作用 read。 / Executes a side-effect-free read.
    /// </summary>
    /// <typeparam name="T">结果类型。 / Result type.</typeparam>
    /// <param name="operation">读取操作。 / Read operation.</param>
    /// <param name="cancellationToken">取消读取的令牌。 / Token that cancels the read.</param>
    /// <returns>读取结果。 / The read result.</returns>
    public Task<T> ExecuteReadAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ThrowIfStopping();
        return operation(cancellationToken);
    }

    /// <summary>
    /// 串行执行 mutation；获取提交权后不会因 client 取消而产生未知的半提交状态。
    /// / Serially executes a mutation; client cancellation cannot interrupt it after commit ownership is acquired.
    /// </summary>
    /// <typeparam name="T">结果类型。 / Result type.</typeparam>
    /// <param name="operation">mutation 操作。 / Mutation operation.</param>
    /// <param name="cancellationToken">仅在等待提交权时生效的令牌。 / Token observed only while waiting for commit ownership.</param>
    /// <returns>mutation 结果。 / The mutation result.</returns>
    public async Task<T> ExecuteMutationAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ThrowIfStopping();
        try
        {
            await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (state.IsStopping)
        {
            throw new DaemonStoppingException();
        }

        try
        {
            ThrowIfStopping();
            return await operation(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            mutationGate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        mutationGate.Dispose();
    }

    private void ThrowIfStopping()
    {
        if (state.IsStopping)
        {
            throw new DaemonStoppingException();
        }
    }
}

/// <summary>
/// 表示请求因 daemon 正在停止而被拒绝。 / Indicates that a request was rejected because daemon shutdown is in progress.
/// </summary>
internal sealed class DaemonStoppingException : InvalidOperationException
{
    /// <summary>
    /// 初始化异常，且不携带请求 payload。 / Initializes the exception without request payload data.
    /// </summary>
    public DaemonStoppingException()
        : base("The daemon is shutting down.")
    {
    }
}
