namespace Scrap.Daemon;

/// <summary>
/// 跟踪连接和请求活动，但不保留任何 IPC payload。 / Tracks connection and request activity without retaining IPC payloads.
/// </summary>
internal sealed class DaemonActivityTracker
{
    private readonly object gate = new();
    private readonly TimeProvider timeProvider;
    private long activeConnections;
    private long activeRequests;
    private long lastActivityTimestamp;

    /// <summary>
    /// 初始化活动跟踪器。 / Initializes an activity tracker.
    /// </summary>
    /// <param name="timeProvider">单调时间来源。 / Monotonic time source.</param>
    public DaemonActivityTracker(TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider;
        lastActivityTimestamp = timeProvider.GetTimestamp();
    }

    /// <summary>
    /// 获取当前活动连接数。 / Gets the number of active connections.
    /// </summary>
    public long ActiveConnections
    {
        get
        {
            lock (gate)
            {
                return activeConnections;
            }
        }
    }

    /// <summary>
    /// 获取当前在途请求数。 / Gets the number of in-flight requests.
    /// </summary>
    public long ActiveRequests
    {
        get
        {
            lock (gate)
            {
                return activeRequests;
            }
        }
    }

    /// <summary>
    /// 开始跟踪一个连接。 / Begins tracking a connection.
    /// </summary>
    /// <returns>释放后结束跟踪的租约。 / A lease that stops tracking when disposed.</returns>
    public IDisposable BeginConnection()
    {
        lock (gate)
        {
            Touch();
            activeConnections++;
            return new Lease(this, isConnection: true);
        }
    }

    /// <summary>
    /// 仅在 daemon 仍运行时原子接纳连接。 / Atomically admits a connection only while the daemon is running.
    /// </summary>
    /// <param name="state">共享运行状态。 / Shared runtime state.</param>
    /// <param name="lease">成功时返回连接租约。 / Receives a connection lease on success.</param>
    /// <returns>连接是否已接纳。 / Whether the connection was admitted.</returns>
    public bool TryBeginConnection(DaemonRuntimeState state, out IDisposable? lease)
    {
        lock (gate)
        {
            if (state.IsStopping)
            {
                lease = null;
                return false;
            }

            lease = BeginConnection();
            return true;
        }
    }

    /// <summary>
    /// 开始跟踪一个请求。 / Begins tracking a request.
    /// </summary>
    /// <returns>释放后结束跟踪的租约。 / A lease that stops tracking when disposed.</returns>
    public IDisposable BeginRequest()
    {
        lock (gate)
        {
            Touch();
            activeRequests++;
            return new Lease(this, isConnection: false);
        }
    }

    /// <summary>
    /// 判断 daemon 是否已连续空闲指定时长。 / Determines whether the daemon has remained idle for a duration.
    /// </summary>
    /// <param name="duration">所需空闲时长。 / Required idle duration.</param>
    /// <returns>没有活动且空闲窗口已到时为 <see langword="true"/>。 / <see langword="true"/> when there is no activity and the idle window elapsed.</returns>
    public bool IsIdleFor(TimeSpan duration)
    {
        lock (gate)
        {
            if (activeConnections != 0 || activeRequests != 0)
            {
                return false;
            }

            return timeProvider.GetElapsedTime(lastActivityTimestamp, timeProvider.GetTimestamp()) >= duration;
        }
    }

    /// <summary>
    /// 若已空闲指定时长，则与新连接接纳互斥地进入停止状态。
    /// / Enters the stopping state when idle, mutually exclusive with new connection admission.
    /// </summary>
    /// <param name="duration">所需空闲时长。 / Required idle duration.</param>
    /// <param name="state">共享运行状态。 / Shared runtime state.</param>
    /// <returns>本次调用是否启动了停止。 / Whether this call initiated shutdown.</returns>
    public bool TryBeginStoppingIfIdle(TimeSpan duration, DaemonRuntimeState state)
    {
        lock (gate)
        {
            return IsIdleFor(duration) && state.BeginStopping();
        }
    }

    /// <summary>
    /// 与连接接纳互斥地进入停止状态。 / Enters the stopping state mutually exclusively with connection admission.
    /// </summary>
    /// <param name="state">共享运行状态。 / Shared runtime state.</param>
    /// <returns>本次调用是否执行了转换。 / Whether this call performed the transition.</returns>
    public bool BeginStopping(DaemonRuntimeState state)
    {
        lock (gate)
        {
            return state.BeginStopping();
        }
    }

    private void End(bool isConnection)
    {
        lock (gate)
        {
            if (isConnection)
            {
                activeConnections--;
                if (activeConnections < 0)
                {
                    throw new InvalidOperationException("Daemon connection lease was released more than once.");
                }
            }
            else
            {
                activeRequests--;
                if (activeRequests < 0)
                {
                    throw new InvalidOperationException("Daemon request lease was released more than once.");
                }
            }

            Touch();
        }
    }

    private void Touch() => lastActivityTimestamp = timeProvider.GetTimestamp();

    private sealed class Lease : IDisposable
    {
        private DaemonActivityTracker? owner;
        private readonly bool isConnection;

        public Lease(DaemonActivityTracker owner, bool isConnection)
        {
            this.owner = owner;
            this.isConnection = isConnection;
        }

        public void Dispose() => Interlocked.Exchange(ref owner, null)?.End(isConnection);
    }
}
