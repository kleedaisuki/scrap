namespace Scrap.Daemon.Tests;

/// <summary>
/// 验证 daemon 停止状态的一次性原子转换。 / Verifies the daemon stopping state's one-way atomic transition.
/// </summary>
public sealed class DaemonRuntimeStateTests
{
    /// <summary>
    /// 验证并发调用中恰有一个调用者执行运行到停止的状态转换。
    /// / Verifies that exactly one concurrent caller performs the running-to-stopping transition.
    /// </summary>
    [Fact]
    public async Task BeginStoppingConcurrentCallersOnlyOnePerformsTransition()
    {
        var state = new DaemonRuntimeState();
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        const int callerCount = 32;

        Task<bool>[] transitions = Enumerable.Range(0, callerCount)
            .Select(_ => Task.Run(async () =>
            {
                await start.Task;
                return state.BeginStopping();
            }))
            .ToArray();

        start.SetResult(true);
        bool[] results = await Task.WhenAll(transitions).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(results, result => result);
        Assert.True(state.IsStopping);
        Assert.False(state.BeginStopping());
    }
}
