using System.Buffers.Binary;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Scrap.Protocol;

namespace Scrap.Daemon.Tests;

/// <summary>
/// 验证持久 IPC 连接的 framing、协商、顺序与关闭语义。 / Verifies framing, negotiation, ordering, and shutdown semantics for persistent IPC connections.
/// </summary>
public sealed class DaemonConnectionProcessorTests
{
    /// <summary>
    /// 验证严格 UTF-8 校验失败后立即断开，且不会尝试读取紧随其后的合法 frame。
    /// / Verifies that strict UTF-8 failure immediately disconnects without reading a following valid frame.
    /// </summary>
    [Fact]
    public async Task ProcessAsyncInvalidUtf8ClosesConnectionWithoutResponse()
    {
        byte[] validFrame = await EncodeFramesAsync(VersionRequest("after-invalid"));
        byte[] input = new byte[ProtocolConstants.FrameHeaderSize + 1 + validFrame.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(input, 1);
        input[ProtocolConstants.FrameHeaderSize] = 0xff;
        validFrame.CopyTo(input, ProtocolConstants.FrameHeaderSize + 1);

        var stream = new TestDuplexStream(input, maximumReadSize: 1);
        using var lifetime = new RecordingHostApplicationLifetime();
        var state = new DaemonRuntimeState();
        var activity = new DaemonActivityTracker(TimeProvider.System);
        using var coordinator = new RequestExecutionCoordinator(state);
        DaemonConnectionProcessor processor = CreateProcessor(activity, state, lifetime, coordinator);

        await processor.ProcessAsync(stream, activity.BeginConnection(), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(stream.WrittenBytes);
        Assert.Equal(0L, activity.ActiveConnections);
        Assert.Equal(0L, activity.ActiveRequests);
        Assert.False(state.IsStopping);
        Assert.Equal(0, lifetime.StopCallCount);
    }

    /// <summary>
    /// 验证非 version 首帧收到版本错误，随后成功协商后同一连接可继续服务。
    /// / Verifies that a non-version first frame gets a version error and the same connection remains usable after successful negotiation.
    /// </summary>
    [Fact]
    public async Task ProcessAsyncRequiresVersionAsFirstSuccessfulRequest()
    {
        byte[] input = await EncodeFramesAsync(
            PingRequest("ping-before-version"),
            VersionRequest("version"),
            PingRequest("ping-after-version"));
        var stream = new TestDuplexStream(input, maximumReadSize: 3);
        using var lifetime = new RecordingHostApplicationLifetime();
        var state = new DaemonRuntimeState();
        var activity = new DaemonActivityTracker(TimeProvider.System);
        using var coordinator = new RequestExecutionCoordinator(state);
        DaemonConnectionProcessor processor = CreateProcessor(activity, state, lifetime, coordinator);

        await processor.ProcessAsync(stream, activity.BeginConnection(), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        ProtocolResponse[] responses = await DecodeResponsesAsync(stream.WrittenBytes, 3);
        Assert.Equal("ping-before-version", responses[0].RequestId);
        Assert.Equal(ProtocolErrorCodes.ProtocolVersionUnsupported, responses[0].Error?.Code);
        Assert.Equal("version", responses[1].RequestId);
        Assert.Null(responses[1].Error);
        Assert.Equal("ping-after-version", responses[2].RequestId);
        Assert.Null(responses[2].Error);
    }

    /// <summary>
    /// 验证多个 request 的 response 按 wire 顺序逐帧写回。 / Verifies that responses to multiple requests are written frame-by-frame in wire order.
    /// </summary>
    [Fact]
    public async Task ProcessAsyncWritesResponsesInRequestOrder()
    {
        byte[] input = await EncodeFramesAsync(
            VersionRequest("one"),
            PingRequest("two"),
            ProtocolRequest.Create("three", ProtocolMethods.ScopeList, new ScopeListParams()),
            PingRequest("four"));
        var stream = new TestDuplexStream(input, maximumReadSize: 2);
        using var lifetime = new RecordingHostApplicationLifetime();
        var state = new DaemonRuntimeState();
        var activity = new DaemonActivityTracker(TimeProvider.System);
        using var coordinator = new RequestExecutionCoordinator(state);
        DaemonConnectionProcessor processor = CreateProcessor(activity, state, lifetime, coordinator);

        await processor.ProcessAsync(stream, activity.BeginConnection(), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        ProtocolResponse[] responses = await DecodeResponsesAsync(stream.WrittenBytes, 4);
        Assert.Equal("one", responses[0].RequestId);
        Assert.Equal("two", responses[1].RequestId);
        Assert.Equal("three", responses[2].RequestId);
        Assert.Equal("four", responses[3].RequestId);
        Assert.All(responses, response => Assert.Null(response.Error));
    }

    /// <summary>
    /// 验证 CJK 与 emoji 在请求和响应的严格 UTF-8 frame 中逐字往返。
    /// / Verifies verbatim CJK and emoji round-tripping through strict UTF-8 request and response frames.
    /// </summary>
    [Fact]
    public async Task ProcessAsyncRoundTripsCjkAndEmojiUtf8()
    {
        const string scopeName = "研发机密🔐-雪山🏔️";
        string? receivedName = null;
        var operations = new StubDaemonOperations
        {
            CreateScope = (parameters, _) =>
            {
                receivedName = parameters.Name;
                return Task.FromResult(new ScopeCreateResult(new ScopeDto(parameters.Name)));
            },
        };
        byte[] input = await EncodeFramesAsync(
            VersionRequest("版本✨"),
            ProtocolRequest.Create("创建请求🧨", ProtocolMethods.ScopeCreate, new ScopeCreateParams(scopeName)));
        var stream = new TestDuplexStream(input, maximumReadSize: 1);
        using var lifetime = new RecordingHostApplicationLifetime();
        var state = new DaemonRuntimeState();
        var activity = new DaemonActivityTracker(TimeProvider.System);
        using var coordinator = new RequestExecutionCoordinator(state);
        DaemonConnectionProcessor processor = CreateProcessor(activity, state, lifetime, coordinator, operations);

        await processor.ProcessAsync(stream, activity.BeginConnection(), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        ProtocolResponse[] responses = await DecodeResponsesAsync(stream.WrittenBytes, 2);
        ScopeCreateResult result = responses[1].GetResult<ScopeCreateResult>();
        Assert.Equal(scopeName, receivedName);
        Assert.Equal("创建请求🧨", responses[1].RequestId);
        Assert.Equal(scopeName, result.Scope.Name);
    }

    /// <summary>
    /// 验证 shutdown response 完整写出后才改变 host 生命周期。 / Verifies that the complete shutdown response is written before the host lifetime is changed.
    /// </summary>
    [Fact]
    public async Task ProcessAsyncShutdownStopsApplicationAfterWritingResponse()
    {
        byte[] input = await EncodeFramesAsync(
            VersionRequest("version"),
            ProtocolRequest.Create("shutdown", ProtocolMethods.DaemonShutdown, new DaemonShutdownParams()));
        var stream = new TestDuplexStream(input);
        byte[]? bytesObservedAtStop = null;
        using var lifetime = new RecordingHostApplicationLifetime
        {
            StoppingObserver = () => bytesObservedAtStop = stream.WrittenBytes,
        };
        var state = new DaemonRuntimeState();
        var activity = new DaemonActivityTracker(TimeProvider.System);
        using var coordinator = new RequestExecutionCoordinator(state);
        DaemonConnectionProcessor processor = CreateProcessor(activity, state, lifetime, coordinator);

        await processor.ProcessAsync(stream, activity.BeginConnection(), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(state.IsStopping);
        Assert.Equal(1, lifetime.StopCallCount);
        Assert.NotNull(bytesObservedAtStop);
        Assert.Equal(stream.WrittenBytes, bytesObservedAtStop);
        ProtocolResponse[] responses = await DecodeResponsesAsync(bytesObservedAtStop!, 2);
        Assert.Equal("shutdown", responses[1].RequestId);
        Assert.NotNull(responses[1].Result);
        Assert.Null(responses[1].Error);
    }

    private static DaemonConnectionProcessor CreateProcessor(
        DaemonActivityTracker activity,
        DaemonRuntimeState state,
        IHostApplicationLifetime lifetime,
        RequestExecutionCoordinator coordinator,
        IDaemonOperations? operations = null)
    {
        var dispatcher = new DaemonRequestDispatcher(
            operations ?? new StubDaemonOperations(),
            coordinator,
            NullLogger<DaemonRequestDispatcher>.Instance,
            "9.8.7-test");
        return new DaemonConnectionProcessor(
            dispatcher,
            activity,
            state,
            lifetime,
            NullLogger<DaemonConnectionProcessor>.Instance);
    }

    private static ProtocolRequest VersionRequest(string requestId) =>
        ProtocolRequest.Create(requestId, ProtocolMethods.DaemonVersion, new DaemonVersionParams());

    private static ProtocolRequest PingRequest(string requestId) =>
        ProtocolRequest.Create(requestId, ProtocolMethods.DaemonPing, new DaemonPingParams());

    private static async Task<byte[]> EncodeFramesAsync(params ProtocolRequest[] requests)
    {
        await using var stream = new MemoryStream();
        foreach (ProtocolRequest request in requests)
        {
            await LengthPrefixedJsonFraming.WriteAsync(stream, request);
        }

        return stream.ToArray();
    }

    private static async Task<ProtocolResponse[]> DecodeResponsesAsync(byte[] bytes, int count)
    {
        await using var stream = new MemoryStream(bytes, writable: false);
        var responses = new ProtocolResponse[count];
        for (int index = 0; index < responses.Length; index++)
        {
            responses[index] = await LengthPrefixedJsonFraming.ReadAsync<ProtocolResponse>(stream);
        }

        Assert.Equal(stream.Length, stream.Position);
        return responses;
    }
}
