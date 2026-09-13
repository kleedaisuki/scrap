namespace Scrap.Daemon;

/// <summary>
/// 缓存后台初始化结果，使 IPC 可在 store/key 失败时仍给 client 精确诊断。
/// / Caches background initialization outcome so IPC can diagnose store/key failures precisely.
/// </summary>
internal sealed class DaemonInitializationState
{
    private readonly TaskCompletionSource settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private InitializationFailure? failure;
    private int initialized;

    /// <summary>记录初始化成功。 / Records successful initialization.</summary>
    public void SetReady()
    {
        Volatile.Write(ref initialized, 1);
        settled.TrySetResult();
    }

    /// <summary>记录安全的初始化失败。 / Records a safe initialization failure.</summary>
    public void SetFailure(DaemonInitializationException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Volatile.Write(ref failure, new InitializationFailure(exception.ErrorCode, exception.Message));
        settled.TrySetResult();
    }

    /// <summary>
    /// 等待初始化得到成功或稳定失败，避免首个 client 观察瞬态未初始化状态。
    /// / Waits for initialization to settle successfully or with a stable failure so the first client cannot observe a transient state.
    /// </summary>
    /// <param name="cancellationToken">等待取消令牌。 / Wait cancellation token.</param>
    public Task WaitUntilSettledAsync(CancellationToken cancellationToken) =>
        settled.Task.WaitAsync(cancellationToken);

    /// <summary>若 daemon 尚不可服务，则抛出缓存失败。 / Throws the cached failure when the daemon is not serviceable.</summary>
    public void EnsureReady()
    {
        if (Volatile.Read(ref initialized) != 0)
        {
            return;
        }

        InitializationFailure? current = Volatile.Read(ref failure);
        throw current is null
            ? new DaemonInitializationException("store_unavailable", "The daemon store is not initialized.")
            : new DaemonInitializationException(current.ErrorCode, current.Message);
    }

    private sealed record InitializationFailure(string ErrorCode, string Message);
}
