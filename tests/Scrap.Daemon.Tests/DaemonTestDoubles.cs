using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scrap.Protocol;
using ProtocolSearchRequest = Scrap.Protocol.SearchRequest;

namespace Scrap.Daemon.Tests;

/// <summary>
/// 为 daemon 单元测试提供可观察的 Generic Host 生命周期。 / Provides an observable Generic Host lifetime for daemon unit tests.
/// </summary>
internal sealed class RecordingHostApplicationLifetime : IHostApplicationLifetime, IDisposable
{
    private readonly CancellationTokenSource started = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly CancellationTokenSource stopped = new();
    private readonly TaskCompletionSource<bool> stopRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int stopCallCount;

    /// <summary>
    /// 获取在 <see cref="StopApplication"/> 执行时调用的观察回调。 / Gets an observer invoked by <see cref="StopApplication"/>.
    /// </summary>
    public Action? StoppingObserver { get; init; }

    /// <summary>
    /// 获取停止请求次数。 / Gets the number of stop requests.
    /// </summary>
    public int StopCallCount => Volatile.Read(ref stopCallCount);

    /// <summary>
    /// 获取首次停止请求完成信号。 / Gets a signal completed by the first stop request.
    /// </summary>
    public Task StopRequested => stopRequested.Task;

    /// <inheritdoc />
    public CancellationToken ApplicationStarted => started.Token;

    /// <inheritdoc />
    public CancellationToken ApplicationStopping => stopping.Token;

    /// <inheritdoc />
    public CancellationToken ApplicationStopped => stopped.Token;

    /// <inheritdoc />
    public void StopApplication()
    {
        Interlocked.Increment(ref stopCallCount);
        StoppingObserver?.Invoke();
        stopping.Cancel();
        stopRequested.TrySetResult(true);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        started.Dispose();
        stopping.Dispose();
        stopped.Dispose();
    }
}

/// <summary>
/// 提供独立的输入和输出缓冲区，以模拟单条双向 IPC 连接。 / Simulates one duplex IPC connection with independent input and output buffers.
/// </summary>
internal sealed class TestDuplexStream : Stream
{
    private readonly byte[] input;
    private readonly int maximumReadSize;
    private readonly bool blockAfterInput;
    private readonly MemoryStream output = new();
    private readonly TaskCompletionSource<bool> readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int inputOffset;
    private bool isDisposed;

    /// <summary>
    /// 初始化测试 stream。 / Initializes the test stream.
    /// </summary>
    /// <param name="input">client 已写入的输入字节。 / Input bytes already written by the client.</param>
    /// <param name="maximumReadSize">每次读取允许返回的最大字节数。 / Maximum bytes returned by each read.</param>
    /// <param name="blockAfterInput">输入耗尽后是否等待取消，而不是返回 EOF。 / Whether to wait for cancellation instead of returning EOF after consuming input.</param>
    public TestDuplexStream(byte[]? input = null, int maximumReadSize = int.MaxValue, bool blockAfterInput = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumReadSize);
        this.input = input ?? [];
        this.maximumReadSize = maximumReadSize;
        this.blockAfterInput = blockAfterInput;
    }

    /// <summary>
    /// 获取首次读取开始信号。 / Gets a signal completed when the first read begins.
    /// </summary>
    public Task ReadStarted => readStarted.Task;

    /// <summary>
    /// 获取 stream 被释放的信号。 / Gets a signal completed when the stream is disposed.
    /// </summary>
    public Task Disposed => disposed.Task;

    /// <summary>
    /// 获取目前完整写出的字节副本。 / Gets a snapshot of all bytes written so far.
    /// </summary>
    public byte[] WrittenBytes
    {
        get
        {
            lock (output)
            {
                return output.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public override bool CanRead => !isDisposed;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => !isDisposed;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);
        readStarted.TrySetResult(true);
        int read = CopyInput(buffer.AsSpan(offset, count));
        if (read == 0 && blockAfterInput)
        {
            throw new NotSupportedException("Blocking reads must use the asynchronous API in these tests.");
        }

        return read;
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);
        readStarted.TrySetResult(true);
        int read = CopyInput(buffer.Span);
        if (read != 0 || !blockAfterInput)
        {
            return read;
        }

        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        return 0;
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);
        lock (output)
        {
            output.Write(buffer, offset, count);
        }
    }

    /// <inheritdoc />
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        lock (output)
        {
            output.Write(buffer.Span);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (!isDisposed)
        {
            isDisposed = true;
            disposed.TrySetResult(true);
            if (disposing)
            {
                output.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    private int CopyInput(Span<byte> destination)
    {
        int remaining = input.Length - inputOffset;
        int count = Math.Min(Math.Min(remaining, destination.Length), maximumReadSize);
        if (count == 0)
        {
            return 0;
        }

        input.AsSpan(inputOffset, count).CopyTo(destination);
        inputOffset += count;
        return count;
    }
}

/// <summary>
/// 首次返回预置 stream，随后等待 server 的停止取消。 / Returns one configured stream, then waits for server cancellation.
/// </summary>
internal sealed class SingleConnectionAcceptor : IConnectionAcceptor
{
    private readonly Stream stream;
    private int calls;

    /// <summary>
    /// 初始化单连接 acceptor。 / Initializes a single-connection acceptor.
    /// </summary>
    /// <param name="stream">首次接纳的 stream。 / Stream accepted by the first call.</param>
    public SingleConnectionAcceptor(Stream stream) => this.stream = stream;

    /// <summary>
    /// 获取 accept 调用次数。 / Gets the number of accept calls.
    /// </summary>
    public int Calls => Volatile.Read(ref calls);

    /// <inheritdoc />
    public void Bind()
    {
    }

    /// <inheritdoc />
    public async ValueTask<Stream> AcceptAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref calls) == 1)
        {
            return stream;
        }

        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        throw new InvalidOperationException("An infinite delay unexpectedly completed without cancellation.");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// 为 listener 生命周期测试提供可编程的 bind 与 accept 行为。
/// / Provides programmable bind and accept behavior for listener lifecycle tests.
/// </summary>
internal sealed class ProgrammableConnectionAcceptor : IConnectionAcceptor
{
    private readonly Func<int, Exception?> bindFailure;
    private readonly Func<CancellationToken, ValueTask<Stream>> accept;
    private readonly Action<string>? observer;
    private int bindCalls;

    /// <summary>
    /// 初始化可编程 acceptor。 / Initializes a programmable acceptor.
    /// </summary>
    /// <param name="accept">accept 实现。 / Accept implementation.</param>
    /// <param name="bindFailure">根据调用次数返回 bind 异常。 / Returns a bind exception for a call number.</param>
    /// <param name="observer">调用顺序观察器。 / Call-order observer.</param>
    public ProgrammableConnectionAcceptor(
        Func<CancellationToken, ValueTask<Stream>> accept,
        Func<int, Exception?>? bindFailure = null,
        Action<string>? observer = null)
    {
        this.accept = accept;
        this.bindFailure = bindFailure ?? (_ => null);
        this.observer = observer;
    }

    /// <summary>
    /// 获取 bind 调用次数。 / Gets the number of bind calls.
    /// </summary>
    public int BindCalls => Volatile.Read(ref bindCalls);

    /// <inheritdoc />
    public void Bind()
    {
        int call = Interlocked.Increment(ref bindCalls);
        observer?.Invoke("bind");
        Exception? failure = bindFailure(call);
        if (failure is not null)
        {
            throw failure;
        }
    }

    /// <inheritdoc />
    public ValueTask<Stream> AcceptAsync(CancellationToken cancellationToken)
    {
        observer?.Invoke("accept");
        return accept(cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// 收集已渲染日志，并保留传给 logging pipeline 的异常引用。
/// / Collects rendered logs and preserves exception references passed to the logging pipeline.
/// </summary>
internal sealed class CollectingTestLogger<T> : ILogger<T>
{
    private readonly Action<TestLogEntry>? observer;

    /// <summary>
    /// 初始化 logger。 / Initializes the logger.
    /// </summary>
    /// <param name="observer">每条日志的可选观察器。 / Optional observer for each log entry.</param>
    public CollectingTestLogger(Action<TestLogEntry>? observer = null) => this.observer = observer;

    /// <summary>
    /// 获取收集的日志。 / Gets the collected log entries.
    /// </summary>
    public ConcurrentQueue<TestLogEntry> Entries { get; } = new();

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var entry = new TestLogEntry(logLevel, eventId, formatter(state, exception), exception);
        Entries.Enqueue(entry);
        observer?.Invoke(entry);
    }
}

/// <summary>
/// 表示一条收集的测试日志。 / Represents one collected test log entry.
/// </summary>
internal sealed record TestLogEntry(LogLevel Level, EventId EventId, string Message, Exception? Exception);

/// <summary>
/// 为 dispatcher 提供无存储副作用的可配置 daemon 操作。 / Provides configurable daemon operations without storage side effects.
/// </summary>
internal sealed class StubDaemonOperations : IDaemonOperations
{
    /// <summary>
    /// 获取或设置 scope 创建实现。 / Gets or sets the scope-create implementation.
    /// </summary>
    public Func<ScopeCreateParams, CancellationToken, Task<ScopeCreateResult>>? CreateScope { get; init; }

    /// <inheritdoc />
    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task<ScopeListResult> ListScopesAsync(ScopeListParams parameters, CancellationToken cancellationToken) =>
        Task.FromResult(new ScopeListResult([]));

    /// <inheritdoc />
    public Task<ScopeCreateResult> CreateScopeAsync(ScopeCreateParams parameters, CancellationToken cancellationToken) =>
        CreateScope?.Invoke(parameters, cancellationToken)
        ?? Task.FromResult(new ScopeCreateResult(new ScopeDto(parameters.Name)));

    /// <inheritdoc />
    public Task<ScopeRenameResult> RenameScopeAsync(ScopeRenameParams parameters, CancellationToken cancellationToken) =>
        Task.FromResult(new ScopeRenameResult(new ScopeDto(parameters.NewName)));

    /// <inheritdoc />
    public Task<ScopeDeleteResult> DeleteScopeAsync(ScopeDeleteParams parameters, CancellationToken cancellationToken) =>
        Task.FromResult(new ScopeDeleteResult(0));

    /// <inheritdoc />
    public Task<RecordGetResult> GetRecordAsync(RecordGetParams parameters, CancellationToken cancellationToken) =>
        Task.FromResult(new RecordGetResult(CreateRecord(parameters.Scope, parameters.Key)));

    /// <inheritdoc />
    public Task<RecordSetResult> SetRecordAsync(RecordSetParams parameters, CancellationToken cancellationToken) =>
        Task.FromResult(new RecordSetResult(CreateSummary(parameters.Scope, parameters.Key), true));

    /// <inheritdoc />
    public Task<RecordRenameResult> RenameRecordAsync(RecordRenameParams parameters, CancellationToken cancellationToken) =>
        Task.FromResult(new RecordRenameResult(CreateSummary(parameters.Scope, parameters.NewKey)));

    /// <inheritdoc />
    public Task<RecordDeleteResult> DeleteRecordAsync(RecordDeleteParams parameters, CancellationToken cancellationToken) =>
        Task.FromResult(new RecordDeleteResult());

    /// <inheritdoc />
    public Task<RecordListResult> ListRecordsAsync(RecordListParams parameters, CancellationToken cancellationToken) =>
        Task.FromResult(new RecordListResult([]));

    /// <inheritdoc />
    public Task<RecordSearchResult> SearchRecordsAsync(ProtocolSearchRequest parameters, CancellationToken cancellationToken) =>
        Task.FromResult(new RecordSearchResult([]));

    private static RecordDto CreateRecord(string scope, string key) =>
        new(scope, key, string.Empty, RecordPresentation.Masked, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1);

    private static RecordSummaryDto CreateSummary(string scope, string key) =>
        new(scope, key, RecordPresentation.Masked, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1);
}

/// <summary>
/// 提供可手动推进且支持 <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> 的单调时钟。
/// / Provides a manually advancing monotonic clock that supports <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>.
/// </summary>
internal sealed class ManualTimerTimeProvider : TimeProvider
{
    private readonly object gate = new();
    private readonly List<ManualTimer> timers = [];
    private long timestamp;
    private int timerCreationCount;

    /// <inheritdoc />
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <inheritdoc />
    public override long GetTimestamp() => Interlocked.Read(ref timestamp);

    /// <summary>
    /// 获取创建过的 timer 数量。 / Gets the number of timers created so far.
    /// </summary>
    public int TimerCreationCount => Volatile.Read(ref timerCreationCount);

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new ManualTimer(this, callback, state);
        lock (gate)
        {
            timers.Add(timer);
            timer.ChangeUnderLock(dueTime, period);
            Interlocked.Increment(ref timerCreationCount);
        }

        return timer;
    }

    /// <summary>
    /// 推进时钟，并同步触发在新时间点前到期的 timer。 / Advances the clock and synchronously fires timers due by the new timestamp.
    /// </summary>
    /// <param name="duration">非负推进时长。 / Non-negative duration to advance.</param>
    public void Advance(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "Time cannot move backwards.");
        }

        List<(TimerCallback Callback, object? State)> callbacks = [];
        lock (gate)
        {
            timestamp = checked(timestamp + duration.Ticks);
            foreach (ManualTimer timer in timers.ToArray())
            {
                if (timer.TryTakeCallbackUnderLock(timestamp, out TimerCallback? callback, out object? state))
                {
                    callbacks.Add((callback!, state));
                }
            }
        }

        foreach ((TimerCallback callback, object? state) in callbacks)
        {
            callback(state);
        }
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimerTimeProvider owner;
        private readonly TimerCallback callback;
        private readonly object? state;
        private long dueTimestamp = long.MaxValue;
        private long periodTicks = Timeout.InfiniteTimeSpan.Ticks;
        private bool disposed;

        public ManualTimer(ManualTimerTimeProvider owner, TimerCallback callback, object? state)
        {
            this.owner = owner;
            this.callback = callback;
            this.state = state;
        }

        /// <inheritdoc />
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner.gate)
            {
                if (disposed)
                {
                    return false;
                }

                ChangeUnderLock(dueTime, period);
                return true;
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            lock (owner.gate)
            {
                disposed = true;
                owner.timers.Remove(this);
            }
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public void ChangeUnderLock(TimeSpan dueTime, TimeSpan period)
        {
            ValidateTimeout(dueTime, nameof(dueTime));
            ValidateTimeout(period, nameof(period));
            periodTicks = period.Ticks;
            dueTimestamp = dueTime == Timeout.InfiniteTimeSpan
                ? long.MaxValue
                : checked(owner.timestamp + dueTime.Ticks);
        }

        public bool TryTakeCallbackUnderLock(long now, out TimerCallback? dueCallback, out object? dueState)
        {
            if (disposed || dueTimestamp > now)
            {
                dueCallback = null;
                dueState = null;
                return false;
            }

            dueCallback = callback;
            dueState = state;
            dueTimestamp = periodTicks == Timeout.InfiniteTimeSpan.Ticks
                ? long.MaxValue
                : checked(now + periodTicks);
            return true;
        }

        private static void ValidateTimeout(TimeSpan timeout, string parameterName)
        {
            if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            {
                throw new ArgumentOutOfRangeException(parameterName, timeout, "Timeout must be non-negative or infinite.");
            }
        }
    }
}
