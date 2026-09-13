namespace Scrap.Daemon.Tests;

/// <summary>
/// 验证 daemon 活动租约的计数和空闲窗口语义。 / Verifies daemon activity lease counters and idle-window semantics.
/// </summary>
public sealed class DaemonActivityTrackerTests
{
    /// <summary>
    /// 验证连接和请求租约分别计数，且重复释放同一租约不会破坏计数。
    /// / Verifies that connection and request leases are counted independently and that repeated disposal is harmless.
    /// </summary>
    [Fact]
    public void ActivityLeasesTrackTheirOwnCountersAndAreIdempotent()
    {
        var timeProvider = new ManualTimeProvider();
        var tracker = new DaemonActivityTracker(timeProvider);

        IDisposable connection = tracker.BeginConnection();
        IDisposable request = tracker.BeginRequest();

        Assert.Equal(1L, tracker.ActiveConnections);
        Assert.Equal(1L, tracker.ActiveRequests);

        connection.Dispose();
        connection.Dispose();

        Assert.Equal(0L, tracker.ActiveConnections);
        Assert.Equal(1L, tracker.ActiveRequests);

        request.Dispose();
        request.Dispose();

        Assert.Equal(0L, tracker.ActiveConnections);
        Assert.Equal(0L, tracker.ActiveRequests);
    }

    /// <summary>
    /// 验证只要租约仍活动，即使空闲阈值已经过去，daemon 也不会被视为空闲。
    /// / Verifies that an active lease prevents idle detection even after the idle threshold has elapsed.
    /// </summary>
    [Fact]
    public void IsIdleForReturnsFalseWhileAnyLeaseIsActive()
    {
        var timeProvider = new ManualTimeProvider();
        var tracker = new DaemonActivityTracker(timeProvider);
        using IDisposable connection = tracker.BeginConnection();
        using IDisposable request = tracker.BeginRequest();

        timeProvider.Advance(TimeSpan.FromHours(1));

        Assert.False(tracker.IsIdleFor(TimeSpan.FromSeconds(1)));
    }

    /// <summary>
    /// 验证每次活动开始和结束都会重置空闲窗口，并且阈值边界包含等号。
    /// / Verifies that starting and ending activity reset the idle window and that the threshold boundary is inclusive.
    /// </summary>
    [Fact]
    public void IsIdleForRestartsWindowWhenLeaseEnds()
    {
        var timeProvider = new ManualTimeProvider();
        var tracker = new DaemonActivityTracker(timeProvider);
        TimeSpan threshold = TimeSpan.FromSeconds(30);

        timeProvider.Advance(threshold - TimeSpan.FromTicks(1));
        Assert.False(tracker.IsIdleFor(threshold));

        timeProvider.Advance(TimeSpan.FromTicks(1));
        Assert.True(tracker.IsIdleFor(threshold));

        IDisposable request = tracker.BeginRequest();
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        Assert.False(tracker.IsIdleFor(threshold));

        request.Dispose();
        Assert.False(tracker.IsIdleFor(threshold));

        timeProvider.Advance(threshold);
        Assert.True(tracker.IsIdleFor(threshold));
    }

    /// <summary>
    /// 为测试提供可确定推进的单调时钟。 / Provides a deterministically advancing monotonic clock for tests.
    /// </summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private long timestamp;

        /// <inheritdoc />
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        /// <inheritdoc />
        public override long GetTimestamp() => Interlocked.Read(ref timestamp);

        /// <summary>
        /// 将单调时钟向前推进指定时长。 / Advances the monotonic clock by the specified duration.
        /// </summary>
        /// <param name="duration">非负的推进时长。 / A non-negative duration to advance.</param>
        public void Advance(TimeSpan duration)
        {
            if (duration < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(duration), duration, "Time cannot move backwards.");
            }

            Interlocked.Add(ref timestamp, duration.Ticks);
        }
    }
}
