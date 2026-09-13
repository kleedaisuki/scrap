namespace Scrap.Daemon;

/// <summary>
/// 表示 daemon 是否仍接受新请求。 / Represents whether the daemon still accepts new requests.
/// </summary>
internal sealed class DaemonRuntimeState
{
    private int stopping;

    /// <summary>
    /// 获取 daemon 是否已开始停止。 / Gets whether daemon shutdown has begun.
    /// </summary>
    public bool IsStopping => Volatile.Read(ref stopping) != 0;

    /// <summary>
    /// 原子地进入停止状态。 / Atomically enters the stopping state.
    /// </summary>
    /// <returns>本次调用是否执行了状态转换。 / Whether this call performed the transition.</returns>
    public bool BeginStopping() => Interlocked.Exchange(ref stopping, 1) == 0;
}
