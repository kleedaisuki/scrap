namespace Scrap.Daemon.Tests;

/// <summary>
/// 验证请求协调器的并发、取消和停止边界。 / Verifies request coordinator concurrency, cancellation, and shutdown boundaries.
/// </summary>
public sealed class RequestExecutionCoordinatorTests
{
    /// <summary>
    /// 验证无副作用读取无需争用 mutation 门即可同时在途。
    /// / Verifies that side-effect-free reads can be in flight concurrently without contending on the mutation gate.
    /// </summary>
    [Fact]
    public async Task ExecuteReadAsyncAllowsConcurrentReads()
    {
        using var coordinator = new RequestExecutionCoordinator(new DaemonRuntimeState());
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bothEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int inFlight = 0;

        Func<CancellationToken, Task<int>> operation = async cancellationToken =>
        {
            int current = Interlocked.Increment(ref inFlight);
            if (current == 2)
            {
                bothEntered.TrySetResult(true);
            }

            try
            {
                await release.Task.WaitAsync(cancellationToken);
                return current;
            }
            finally
            {
                Interlocked.Decrement(ref inFlight);
            }
        };

        Task<int> first = coordinator.ExecuteReadAsync(operation, CancellationToken.None);
        Task<int> second = coordinator.ExecuteReadAsync(operation, CancellationToken.None);

        try
        {
            await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, Volatile.Read(ref inFlight));
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
        }
        finally
        {
            release.TrySetResult(true);
        }

        int[] results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Array.Sort(results);
        Assert.Equal(2, results.Length);
        Assert.Equal(1, results[0]);
        Assert.Equal(2, results[1]);
        Assert.Equal(0, Volatile.Read(ref inFlight));
    }

    /// <summary>
    /// 验证 mutation 操作严格串行执行，后继操作只能在前驱完成后进入。
    /// / Verifies strict mutation serialization: a successor cannot enter until its predecessor completes.
    /// </summary>
    [Fact]
    public async Task ExecuteMutationAsyncSerializesMutations()
    {
        using var coordinator = new RequestExecutionCoordinator(new DaemonRuntimeState());
        var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var executionOrder = new List<string>();

        Task<int> first = coordinator.ExecuteMutationAsync(
            async cancellationToken =>
            {
                Assert.Equal(CancellationToken.None, cancellationToken);
                executionOrder.Add("first-enter");
                firstEntered.SetResult(true);
                await releaseFirst.Task;
                executionOrder.Add("first-exit");
                return 1;
            },
            CancellationToken.None);

        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task<int> second = coordinator.ExecuteMutationAsync(
            cancellationToken =>
            {
                Assert.Equal(CancellationToken.None, cancellationToken);
                executionOrder.Add("second-enter");
                secondEntered.SetResult(true);
                return Task.FromResult(2);
            },
            CancellationToken.None);

        try
        {
            Assert.False(secondEntered.Task.IsCompleted);
        }
        finally
        {
            releaseFirst.TrySetResult(true);
        }

        int[] results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, results.Length);
        Assert.Equal(1, results[0]);
        Assert.Equal(2, results[1]);
        Assert.True(secondEntered.Task.IsCompletedSuccessfully);
        Assert.Equal(3, executionOrder.Count);
        Assert.Equal("first-enter", executionOrder[0]);
        Assert.Equal("first-exit", executionOrder[1]);
        Assert.Equal("second-enter", executionOrder[2]);
    }

    /// <summary>
    /// 验证等待 mutation 提交权的调用可被其调用者取消，且被取消的操作绝不启动。
    /// / Verifies that a caller can cancel while waiting for mutation ownership and that the canceled operation never starts.
    /// </summary>
    [Fact]
    public async Task ExecuteMutationAsyncCancellationWhileWaitingDoesNotStartOperation()
    {
        using var coordinator = new RequestExecutionCoordinator(new DaemonRuntimeState());
        using var cancellation = new CancellationTokenSource();
        var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int secondInvocationCount = 0;

        Task<int> first = coordinator.ExecuteMutationAsync(
            async cancellationToken =>
            {
                Assert.Equal(CancellationToken.None, cancellationToken);
                firstEntered.SetResult(true);
                await releaseFirst.Task;
                return 1;
            },
            CancellationToken.None);

        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<int> waiting = coordinator.ExecuteMutationAsync(
            _ =>
            {
                Interlocked.Increment(ref secondInvocationCount);
                return Task.FromResult(2);
            },
            cancellation.Token);

        try
        {
            cancellation.Cancel();
            OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => waiting.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(cancellation.Token, exception.CancellationToken);
            Assert.Equal(0, Volatile.Read(ref secondInvocationCount));
        }
        finally
        {
            releaseFirst.TrySetResult(true);
        }

        Assert.Equal(1, await first.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    /// <summary>
    /// 验证 mutation 一旦取得提交权便使用不可取消令牌完成，不受调用者随后取消的影响。
    /// / Verifies that a mutation completes with a non-cancelable token once it owns the commit gate, despite later caller cancellation.
    /// </summary>
    [Fact]
    public async Task ExecuteMutationAsyncAfterOperationStartsIgnoresCallerCancellation()
    {
        using var coordinator = new RequestExecutionCoordinator(new DaemonRuntimeState());
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken operationToken = new(canceled: true);

        Task<int> mutation = coordinator.ExecuteMutationAsync(
            async cancellationToken =>
            {
                operationToken = cancellationToken;
                entered.SetResult(true);
                await release.Task;
                return 42;
            },
            cancellation.Token);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            cancellation.Cancel();
            Assert.Equal(CancellationToken.None, operationToken);
            Assert.False(mutation.IsCompleted);
            Assert.False(mutation.IsCanceled);
        }
        finally
        {
            release.TrySetResult(true);
        }

        Assert.Equal(42, await mutation.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    /// <summary>
    /// 验证停止开始后新的 read 和 mutation 均被拒绝，且用户委托不会被调用。
    /// / Verifies that new reads and mutations are rejected after shutdown begins without invoking user delegates.
    /// </summary>
    [Fact]
    public async Task ExecuteAsyncAfterShutdownBeginsRejectsNewRequests()
    {
        var state = new DaemonRuntimeState();
        using var coordinator = new RequestExecutionCoordinator(state);
        int invocationCount = 0;

        Assert.True(state.BeginStopping());

        Assert.Throws<DaemonStoppingException>(() =>
        {
            _ = coordinator.ExecuteReadAsync(
                _ =>
                {
                    Interlocked.Increment(ref invocationCount);
                    return Task.FromResult(1);
                },
                CancellationToken.None);
        });

        await Assert.ThrowsAsync<DaemonStoppingException>(() =>
            coordinator.ExecuteMutationAsync(
                _ =>
                {
                    Interlocked.Increment(ref invocationCount);
                    return Task.FromResult(2);
                },
                CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(0, Volatile.Read(ref invocationCount));
    }

    /// <summary>
    /// 验证等待提交权的 mutation 在 daemon 停止后不会越过第二次状态检查。
    /// / Verifies that a mutation waiting for commit ownership cannot pass the second state check after shutdown begins.
    /// </summary>
    [Fact]
    public async Task ExecuteMutationAsyncShutdownWhileWaitingRejectsBeforeOperationStarts()
    {
        var state = new DaemonRuntimeState();
        using var coordinator = new RequestExecutionCoordinator(state);
        var firstEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int waitingInvocationCount = 0;

        Task<int> first = coordinator.ExecuteMutationAsync(
            async _ =>
            {
                firstEntered.SetResult(true);
                await releaseFirst.Task;
                return 1;
            },
            CancellationToken.None);

        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<int> waiting = coordinator.ExecuteMutationAsync(
            _ =>
            {
                Interlocked.Increment(ref waitingInvocationCount);
                return Task.FromResult(2);
            },
            CancellationToken.None);

        Assert.False(waiting.IsCompleted);
        Assert.True(state.BeginStopping());
        releaseFirst.SetResult(true);

        Assert.Equal(1, await first.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<DaemonStoppingException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, Volatile.Read(ref waitingInvocationCount));
    }
}
