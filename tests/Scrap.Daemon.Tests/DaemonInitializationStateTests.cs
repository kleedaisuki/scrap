using Microsoft.Extensions.Logging.Abstractions;
using Scrap.Protocol;

namespace Scrap.Daemon.Tests;

/// <summary>
/// 验证 listener 先绑定时的初始化 barrier 与稳定失败诊断。 / Verifies the initialization barrier and stable failure diagnostics when the listener binds first.
/// </summary>
public sealed class DaemonInitializationStateTests
{
    /// <summary>
    /// 业务请求等待初始化成功而不是观察瞬态不可用。 / A business request waits for successful initialization instead of observing transient unavailability.
    /// </summary>
    [Fact]
    public async Task BusinessRequestWaitsUntilInitializationSettles()
    {
        var initialization = new DaemonInitializationState();
        var state = new DaemonRuntimeState();
        using var coordinator = new RequestExecutionCoordinator(state);
        var dispatcher = new DaemonRequestDispatcher(
            new StubDaemonOperations(),
            coordinator,
            NullLogger<DaemonRequestDispatcher>.Instance,
            "test",
            initialization);
        ProtocolRequest request = ProtocolRequest.Create(
            "ready-请求",
            ProtocolMethods.ScopeList,
            new ScopeListParams());

        Task<DaemonDispatchResult> pending = dispatcher.DispatchAsync(request, CancellationToken.None);
        await Task.Yield();
        Assert.False(pending.IsCompleted);

        initialization.SetReady();
        DaemonDispatchResult result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(result.Response.Error);
    }

    /// <summary>
    /// 初始化失败在 barrier 后以原稳定错误码返回。 / An initialization failure is returned with its stable code after the barrier.
    /// </summary>
    [Fact]
    public async Task BusinessRequestReceivesCachedInitializationFailure()
    {
        var initialization = new DaemonInitializationState();
        initialization.SetFailure(new DaemonInitializationException(
            ProtocolErrorCodes.KeyUnavailable,
            "The OS-protected master key is unavailable."));
        var state = new DaemonRuntimeState();
        using var coordinator = new RequestExecutionCoordinator(state);
        var dispatcher = new DaemonRequestDispatcher(
            new StubDaemonOperations(),
            coordinator,
            NullLogger<DaemonRequestDispatcher>.Instance,
            "test",
            initialization);

        DaemonDispatchResult result = await dispatcher.DispatchAsync(
            ProtocolRequest.Create("failed-init", ProtocolMethods.ScopeList, new ScopeListParams()),
            CancellationToken.None);

        Assert.Equal(ProtocolErrorCodes.KeyUnavailable, result.Response.Error?.Code);
    }

    /// <summary>
    /// version control 请求在初始化期间仍可完成。 / A version control request remains available during initialization.
    /// </summary>
    [Fact]
    public async Task VersionDoesNotWaitForStoreInitialization()
    {
        var initialization = new DaemonInitializationState();
        var state = new DaemonRuntimeState();
        using var coordinator = new RequestExecutionCoordinator(state);
        var dispatcher = new DaemonRequestDispatcher(
            new StubDaemonOperations(),
            coordinator,
            NullLogger<DaemonRequestDispatcher>.Instance,
            "test",
            initialization);

        DaemonDispatchResult result = await dispatcher.DispatchAsync(
            ProtocolRequest.Create("version", ProtocolMethods.DaemonVersion, new DaemonVersionParams()),
            CancellationToken.None);

        Assert.Null(result.Response.Error);
    }

    /// <summary>
    /// 未知 method 不能绕过业务请求的初始化屏障；完成初始化后仍返回原有 method_not_found。
    /// An unknown method cannot bypass the business initialization barrier and still returns method_not_found afterward.
    /// </summary>
    [Fact]
    public async Task UnknownMethodWaitsForInitializationBeforeMethodNotFound()
    {
        var initialization = new DaemonInitializationState();
        using var coordinator = new RequestExecutionCoordinator(new DaemonRuntimeState());
        var dispatcher = new DaemonRequestDispatcher(
            new StubDaemonOperations(),
            coordinator,
            NullLogger<DaemonRequestDispatcher>.Instance,
            "test",
            initialization);

        Task<DaemonDispatchResult> pending = dispatcher.DispatchAsync(
            ProtocolRequest.Create("unknown", "record.unknown", new { }),
            CancellationToken.None);
        await Task.Yield();
        Assert.False(pending.IsCompleted);

        initialization.SetReady();
        DaemonDispatchResult result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ProtocolErrorCodes.MethodNotFound, result.Response.Error?.Code);
    }
}
