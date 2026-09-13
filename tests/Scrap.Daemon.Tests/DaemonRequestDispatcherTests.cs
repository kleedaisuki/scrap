using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Scrap.Domain;
using Scrap.Protocol;
using Scrap.Storage.Sqlite;
using ProtocolCaseSensitivity = Scrap.Protocol.CaseSensitivity;
using ProtocolSearchRequest = Scrap.Protocol.SearchRequest;
using ProtocolSearchMode = Scrap.Protocol.SearchMode;
using ProtocolScopeRenameResult = Scrap.Protocol.ScopeRenameResult;

namespace Scrap.Daemon.Tests;

/// <summary>
/// 验证 daemon dispatcher 的完整 method 路由、并发边界、错误映射和日志脱敏契约。
/// / Verifies the daemon dispatcher's complete method routing, concurrency boundary, error mapping, and redacted-logging contract.
/// </summary>
public sealed class DaemonRequestDispatcherTests
{
    private const string RequestId = "dispatcher-test-request";
    private const string ApplicationVersion = "9.8.7-test";

    /// <summary>
    /// 验证每个稳定协议 method 都到达唯一的强类型 handler，并生成合法成功响应。
    /// / Verifies that every stable protocol method reaches its unique typed handler and produces a valid success response.
    /// </summary>
    /// <param name="method">待分派的稳定协议 method。 / Stable protocol method to dispatch.</param>
    /// <param name="expectedOperation">预期 operations 调用；daemon 内建 method 使用 <see langword="null"/>。 / Expected operations call, or <see langword="null"/> for built-in daemon methods.</param>
    [Theory]
    [InlineData(ProtocolMethods.ScopeList, ProtocolMethods.ScopeList)]
    [InlineData(ProtocolMethods.ScopeCreate, ProtocolMethods.ScopeCreate)]
    [InlineData(ProtocolMethods.ScopeRename, ProtocolMethods.ScopeRename)]
    [InlineData(ProtocolMethods.ScopeDelete, ProtocolMethods.ScopeDelete)]
    [InlineData(ProtocolMethods.RecordGet, ProtocolMethods.RecordGet)]
    [InlineData(ProtocolMethods.RecordSet, ProtocolMethods.RecordSet)]
    [InlineData(ProtocolMethods.RecordRename, ProtocolMethods.RecordRename)]
    [InlineData(ProtocolMethods.RecordDelete, ProtocolMethods.RecordDelete)]
    [InlineData(ProtocolMethods.RecordList, ProtocolMethods.RecordList)]
    [InlineData(ProtocolMethods.RecordSearch, ProtocolMethods.RecordSearch)]
    [InlineData(ProtocolMethods.DaemonPing, null)]
    [InlineData(ProtocolMethods.DaemonVersion, null)]
    [InlineData(ProtocolMethods.DaemonShutdown, null)]
    public async Task DispatchAsyncRoutesEveryProtocolMethod(string method, string? expectedOperation)
    {
        var operations = new FakeDaemonOperations();
        using var coordinator = new RequestExecutionCoordinator(new DaemonRuntimeState());
        var dispatcher = CreateDispatcher(operations, coordinator);

        DaemonDispatchResult dispatch = await dispatcher.DispatchAsync(CreateRequest(method), CancellationToken.None);

        Assert.Null(dispatch.Response.Error);
        Assert.Equal(RequestId, dispatch.Response.RequestId);
        Assert.NotNull(dispatch.Response.Result);
        Assert.Equal(System.Text.Json.JsonValueKind.Object, dispatch.Response.Result.Value.ValueKind);
        Assert.Equal(method == ProtocolMethods.DaemonShutdown, dispatch.RequestsShutdown);
        AssertSuccessfulResultType(method, dispatch.Response);

        if (expectedOperation is null)
        {
            Assert.Empty(operations.Invocations);
            return;
        }

        Invocation invocation = Assert.Single(operations.Invocations);
        Assert.Equal(expectedOperation, invocation.Method);
        AssertParameters(method, invocation.Parameters);
    }

    /// <summary>
    /// 验证不受支持的主协议版本在调用任何业务 handler 前被拒绝。
    /// / Verifies that an unsupported major protocol version is rejected before any business handler is called.
    /// </summary>
    [Fact]
    public async Task DispatchAsyncRejectsUnsupportedProtocolVersion()
    {
        var operations = new FakeDaemonOperations();
        using var coordinator = new RequestExecutionCoordinator(new DaemonRuntimeState());
        var dispatcher = CreateDispatcher(operations, coordinator);
        ProtocolRequest request = ProtocolRequest.Create(
            RequestId,
            ProtocolMethods.ScopeList,
            new ScopeListParams(),
            ProtocolConstants.CurrentVersion + 1);

        DaemonDispatchResult dispatch = await dispatcher.DispatchAsync(request, CancellationToken.None);

        AssertError(dispatch, ProtocolErrorCodes.ProtocolVersionUnsupported);
        Assert.Empty(operations.Invocations);
    }

    /// <summary>
    /// 验证未知 method 返回稳定的 <c>method_not_found</c> 错误，而不是内部错误。
    /// / Verifies that an unknown method returns the stable <c>method_not_found</c> error rather than an internal error.
    /// </summary>
    [Fact]
    public async Task DispatchAsyncRejectsUnknownMethod()
    {
        var operations = new FakeDaemonOperations();
        using var coordinator = new RequestExecutionCoordinator(new DaemonRuntimeState());
        var dispatcher = CreateDispatcher(operations, coordinator);

        DaemonDispatchResult dispatch = await dispatcher.DispatchAsync(
            ProtocolRequest.Create(RequestId, "record.unknown", new EmptyParameters()),
            CancellationToken.None);

        AssertError(dispatch, ProtocolErrorCodes.MethodNotFound);
        Assert.Empty(operations.Invocations);
    }

    /// <summary>
    /// 验证与 method DTO 不匹配的 object params 返回 <c>invalid_params</c>，且 handler 不会运行。
    /// / Verifies that object params incompatible with the method DTO return <c>invalid_params</c> without running the handler.
    /// </summary>
    [Fact]
    public async Task DispatchAsyncRejectsInvalidMethodParameters()
    {
        var operations = new FakeDaemonOperations();
        using var coordinator = new RequestExecutionCoordinator(new DaemonRuntimeState());
        var dispatcher = CreateDispatcher(operations, coordinator);
        var request = new ProtocolRequest(
            ProtocolConstants.CurrentVersion,
            RequestId,
            ProtocolMethods.RecordSet,
            ProtocolJson.ToElement(new
            {
                scope = "工作",
                key = "口令",
                value = "机密值",
                expectedRevision = "not-an-integer",
            }));

        DaemonDispatchResult dispatch = await dispatcher.DispatchAsync(request, CancellationToken.None);

        AssertError(dispatch, ProtocolErrorCodes.InvalidParams);
        Assert.Empty(operations.Invocations);
    }

    /// <summary>
    /// 验证 raw wire 中违反 DTO 数值不变量的参数统一返回 invalid_params，而不会泄漏构造异常。
    /// / Verifies that raw-wire parameters violating DTO numeric invariants uniformly return invalid_params without leaking constructor exceptions.
    /// </summary>
    [Theory]
    [InlineData(ProtocolMethods.ScopeDelete, "{\"name\":\"scope\",\"recursive\":true,\"expectedRecordCount\":-1}")]
    [InlineData(ProtocolMethods.RecordSet, "{\"scope\":\"scope\",\"key\":\"key\",\"value\":\"secret\",\"expectedRevision\":-1}")]
    [InlineData(ProtocolMethods.RecordRename, "{\"scope\":\"scope\",\"key\":\"key\",\"newKey\":\"new\",\"expectedRevision\":0}")]
    [InlineData(ProtocolMethods.RecordDelete, "{\"scope\":\"scope\",\"key\":\"key\",\"expectedRevision\":0}")]
    public async Task DispatchAsyncMapsRawNumericInvariantFailuresToInvalidParams(
        string method,
        string rawParameters)
    {
        var operations = new FakeDaemonOperations();
        using var coordinator = new RequestExecutionCoordinator(new DaemonRuntimeState());
        var dispatcher = CreateDispatcher(operations, coordinator);
        using JsonDocument document = JsonDocument.Parse(rawParameters);
        var request = new ProtocolRequest(
            ProtocolConstants.CurrentVersion,
            RequestId,
            method,
            document.RootElement.Clone());

        DaemonDispatchResult dispatch = await dispatcher.DispatchAsync(request, CancellationToken.None);

        AssertError(dispatch, ProtocolErrorCodes.InvalidParams);
        Assert.Empty(operations.Invocations);
    }

    /// <summary>
    /// 验证 shutdown 仅返回关闭意图；dispatcher 本身不提前改变共享运行状态。
    /// / Verifies that shutdown returns only a shutdown intent and does not prematurely mutate shared runtime state.
    /// </summary>
    [Fact]
    public async Task DispatchAsyncReturnsShutdownIntentWithoutChangingRuntimeState()
    {
        var state = new DaemonRuntimeState();
        var operations = new FakeDaemonOperations();
        using var coordinator = new RequestExecutionCoordinator(state);
        var dispatcher = CreateDispatcher(operations, coordinator);

        DaemonDispatchResult dispatch = await dispatcher.DispatchAsync(
            ProtocolRequest.Create(RequestId, ProtocolMethods.DaemonShutdown, new DaemonShutdownParams()),
            CancellationToken.None);

        Assert.True(dispatch.RequestsShutdown);
        Assert.False(state.IsStopping);
        Assert.Null(dispatch.Response.Error);
        Assert.IsType<DaemonShutdownResult>(dispatch.Response.GetResult<DaemonShutdownResult>());
        Assert.Empty(operations.Invocations);
    }

    /// <summary>
    /// 验证两个经 dispatcher 提交的 mutation 不会同时进入 operations 层。
    /// / Verifies that two mutations submitted through the dispatcher never enter the operations layer concurrently.
    /// </summary>
    [Fact]
    public async Task DispatchAsyncSerializesMutationOperations()
    {
        var operations = new FakeDaemonOperations();
        using var coordinator = new RequestExecutionCoordinator(new DaemonRuntimeState());
        var dispatcher = CreateDispatcher(operations, coordinator);
        var firstEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int invocationCount = 0;
        int inFlight = 0;
        int maximumInFlight = 0;

        operations.OnInvokeAsync = async (method, _, _) =>
        {
            Assert.Equal(ProtocolMethods.RecordSet, method);
            int ordinal = Interlocked.Increment(ref invocationCount);
            int current = Interlocked.Increment(ref inFlight);
            InterlockedExtensions.Max(ref maximumInFlight, current);
            try
            {
                if (ordinal == 1)
                {
                    firstEntered.SetResult(true);
                    await releaseFirst.Task;
                }
                else
                {
                    secondEntered.SetResult(true);
                }

                return FakeDaemonOperations.SuccessfulRecordSetResult;
            }
            finally
            {
                Interlocked.Decrement(ref inFlight);
            }
        };

        Task<DaemonDispatchResult> first = dispatcher.DispatchAsync(
            CreateRecordSetRequest("第一"),
            CancellationToken.None);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task<DaemonDispatchResult> second = dispatcher.DispatchAsync(
            CreateRecordSetRequest("第二"),
            CancellationToken.None);

        try
        {
            Assert.False(secondEntered.Task.IsCompleted);
            Assert.False(second.IsCompleted);
        }
        finally
        {
            releaseFirst.TrySetResult(true);
        }

        DaemonDispatchResult[] results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(results, result => Assert.Null(result.Response.Error));
        Assert.True(secondEntered.Task.IsCompletedSuccessfully);
        Assert.Equal(2, Volatile.Read(ref invocationCount));
        Assert.Equal(1, Volatile.Read(ref maximumInFlight));
    }

    /// <summary>
    /// 验证 Domain 和 Storage 异常按其稳定类别映射为协议错误码。
    /// / Verifies that Domain and Storage exceptions map to protocol error codes by their stable categories.
    /// </summary>
    /// <param name="scenario">用于构造异常和选择 method 的测试场景。 / Scenario used to construct the exception and select a method.</param>
    /// <param name="expectedCode">预期稳定协议错误码。 / Expected stable protocol error code.</param>
    [Theory]
    [InlineData("domain-required", ProtocolErrorCodes.InvalidParams)]
    [InlineData("domain-invalid-regex", ProtocolErrorCodes.QueryInvalid)]
    [InlineData("domain-regex-timeout", ProtocolErrorCodes.QueryTimeout)]
    [InlineData("storage-scope-not-found", ProtocolErrorCodes.ScopeNotFound)]
    [InlineData("storage-record-not-found", ProtocolErrorCodes.RecordNotFound)]
    [InlineData("storage-scope-exists", ProtocolErrorCodes.ScopeAlreadyExists)]
    [InlineData("storage-record-exists", ProtocolErrorCodes.RecordAlreadyExists)]
    [InlineData("storage-concurrency", ProtocolErrorCodes.Conflict)]
    [InlineData("storage-scope-not-empty", ProtocolErrorCodes.ScopeNotEmpty)]
    [InlineData("storage-migration", ProtocolErrorCodes.StoreUnavailable)]
    [InlineData("storage-unavailable", ProtocolErrorCodes.StoreUnavailable)]
    public async Task DispatchAsyncMapsDomainAndStorageFailures(string scenario, string expectedCode)
    {
        var operations = new FakeDaemonOperations { Failure = CreateFailure(scenario) };
        using var coordinator = new RequestExecutionCoordinator(new DaemonRuntimeState());
        var dispatcher = CreateDispatcher(operations, coordinator);

        DaemonDispatchResult dispatch = await dispatcher.DispatchAsync(
            CreateRequest(MethodForFailure(scenario)),
            CancellationToken.None);

        AssertError(dispatch, expectedCode);
        Assert.Single(operations.Invocations);
    }

    /// <summary>
    /// 验证 daemon 开始停止后新请求返回 <c>daemon_shutting_down</c>，且 operations 层不被调用。
    /// / Verifies that new requests return <c>daemon_shutting_down</c> after daemon shutdown starts without calling operations.
    /// </summary>
    [Fact]
    public async Task DispatchAsyncMapsStoppingStateToDaemonShuttingDown()
    {
        var state = new DaemonRuntimeState();
        Assert.True(state.BeginStopping());
        var operations = new FakeDaemonOperations();
        using var coordinator = new RequestExecutionCoordinator(state);
        var dispatcher = CreateDispatcher(operations, coordinator);

        DaemonDispatchResult dispatch = await dispatcher.DispatchAsync(
            CreateRequest(ProtocolMethods.RecordGet),
            CancellationToken.None);

        AssertError(dispatch, ProtocolErrorCodes.DaemonShuttingDown);
        Assert.Empty(operations.Invocations);
    }

    /// <summary>
    /// 验证 request payload 和异常消息中的多语言 UTF-8 secret 都不会进入 dispatcher 日志。
    /// / Verifies that a multilingual UTF-8 secret in both the request payload and exception message never enters dispatcher logs.
    /// </summary>
    [Fact]
    public async Task DispatchAsyncDoesNotLogUtf8SecretPayload()
    {
        const string secret = "秘密🔐-пароль-كلمة-contraseña";
        var logger = new CollectingLogger<DaemonRequestDispatcher>();
        var operations = new FakeDaemonOperations
        {
            Failure = new InvalidOperationException($"provider accidentally echoed {secret}"),
        };
        using var coordinator = new RequestExecutionCoordinator(new DaemonRuntimeState());
        var dispatcher = CreateDispatcher(operations, coordinator, logger);
        ProtocolRequest request = ProtocolRequest.Create(
            RequestId,
            ProtocolMethods.RecordSet,
            new RecordSetParams("工作区", "令牌", secret));

        DaemonDispatchResult dispatch = await dispatcher.DispatchAsync(request, CancellationToken.None);

        AssertError(dispatch, ProtocolErrorCodes.InternalError);
        Assert.NotEmpty(logger.Entries);
        Assert.Contains(logger.Entries, entry => entry.Message.Contains(ProtocolMethods.RecordSet, StringComparison.Ordinal));
        Assert.All(logger.Entries, entry =>
        {
            Assert.DoesNotContain(secret, entry.Message);
            Assert.DoesNotContain(secret, entry.ExceptionText);
        });
    }

    private static DaemonRequestDispatcher CreateDispatcher(
        FakeDaemonOperations operations,
        RequestExecutionCoordinator coordinator,
        ILogger<DaemonRequestDispatcher>? logger = null) =>
        new(operations, coordinator, logger ?? new CollectingLogger<DaemonRequestDispatcher>(), ApplicationVersion);

    private static ProtocolRequest CreateRequest(string method) => method switch
    {
        ProtocolMethods.ScopeList => ProtocolRequest.Create(RequestId, method, new ScopeListParams("游标🧭", 17)),
        ProtocolMethods.ScopeCreate => ProtocolRequest.Create(RequestId, method, new ScopeCreateParams("工作区")),
        ProtocolMethods.ScopeRename => ProtocolRequest.Create(RequestId, method, new ScopeRenameParams("旧工作区", "新工作区")),
        ProtocolMethods.ScopeDelete => ProtocolRequest.Create(RequestId, method, new ScopeDeleteParams("工作区", true, 2)),
        ProtocolMethods.RecordGet => ProtocolRequest.Create(RequestId, method, new RecordGetParams("工作区", "令牌")),
        ProtocolMethods.RecordSet => CreateRecordSetRequest("秘密值🔐"),
        ProtocolMethods.RecordRename => ProtocolRequest.Create(RequestId, method, new RecordRenameParams("工作区", "旧令牌", "新令牌", 4)),
        ProtocolMethods.RecordDelete => ProtocolRequest.Create(RequestId, method, new RecordDeleteParams("工作区", "令牌", 4)),
        ProtocolMethods.RecordList => ProtocolRequest.Create(RequestId, method, new RecordListParams("工作区")),
        ProtocolMethods.RecordSearch => ProtocolRequest.Create(
            RequestId,
            method,
            new ProtocolSearchRequest("工作区", "令", ProtocolSearchMode.Fuzzy, ProtocolCaseSensitivity.Sensitive, 7)),
        ProtocolMethods.DaemonPing => ProtocolRequest.Create(RequestId, method, new DaemonPingParams()),
        ProtocolMethods.DaemonVersion => ProtocolRequest.Create(RequestId, method, new DaemonVersionParams()),
        ProtocolMethods.DaemonShutdown => ProtocolRequest.Create(RequestId, method, new DaemonShutdownParams()),
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, "No test request is defined for this method."),
    };

    private static ProtocolRequest CreateRecordSetRequest(string value) => ProtocolRequest.Create(
        RequestId,
        ProtocolMethods.RecordSet,
        new RecordSetParams("工作区", "令牌", value, RecordPresentation.Plain, 4));

    private static void AssertParameters(string method, object? parameters)
    {
        switch (method)
        {
            case ProtocolMethods.ScopeList:
                var scopeList = Assert.IsType<ScopeListParams>(parameters);
                Assert.Equal(("游标🧭", 17), (scopeList.AfterName, scopeList.Limit));
                break;
            case ProtocolMethods.ScopeCreate:
                Assert.Equal("工作区", Assert.IsType<ScopeCreateParams>(parameters).Name);
                break;
            case ProtocolMethods.ScopeRename:
                var scopeRename = Assert.IsType<ScopeRenameParams>(parameters);
                Assert.Equal(("旧工作区", "新工作区"), (scopeRename.OldName, scopeRename.NewName));
                break;
            case ProtocolMethods.ScopeDelete:
                var scopeDelete = Assert.IsType<ScopeDeleteParams>(parameters);
                Assert.Equal(("工作区", true, 2), (scopeDelete.Name, scopeDelete.Recursive, scopeDelete.ExpectedRecordCount));
                break;
            case ProtocolMethods.RecordGet:
                var recordGet = Assert.IsType<RecordGetParams>(parameters);
                Assert.Equal(("工作区", "令牌"), (recordGet.Scope, recordGet.Key));
                break;
            case ProtocolMethods.RecordSet:
                var recordSet = Assert.IsType<RecordSetParams>(parameters);
                Assert.Equal(("工作区", "令牌", "秘密值🔐"), (recordSet.Scope, recordSet.Key, recordSet.Value));
                Assert.Equal(RecordPresentation.Plain, recordSet.Presentation);
                Assert.Equal(4, recordSet.ExpectedRevision);
                break;
            case ProtocolMethods.RecordRename:
                var recordRename = Assert.IsType<RecordRenameParams>(parameters);
                Assert.Equal(("工作区", "旧令牌", "新令牌"), (recordRename.Scope, recordRename.Key, recordRename.NewKey));
                Assert.Equal(4, recordRename.ExpectedRevision);
                break;
            case ProtocolMethods.RecordDelete:
                var recordDelete = Assert.IsType<RecordDeleteParams>(parameters);
                Assert.Equal(("工作区", "令牌"), (recordDelete.Scope, recordDelete.Key));
                Assert.Equal(4, recordDelete.ExpectedRevision);
                break;
            case ProtocolMethods.RecordList:
                Assert.Equal("工作区", Assert.IsType<RecordListParams>(parameters).Scope);
                break;
            case ProtocolMethods.RecordSearch:
                var search = Assert.IsType<ProtocolSearchRequest>(parameters);
                Assert.Equal(("工作区", "令"), (search.Scope, search.Query));
                Assert.Equal(ProtocolSearchMode.Fuzzy, search.Mode);
                Assert.Equal(ProtocolCaseSensitivity.Sensitive, search.CaseSensitivity);
                Assert.Equal(7, search.Limit);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(method), method, "No operation parameters are defined for this method.");
        }
    }

    private static void AssertSuccessfulResultType(string method, ProtocolResponse response)
    {
        object result = method switch
        {
            ProtocolMethods.ScopeList => response.GetResult<ScopeListResult>(),
            ProtocolMethods.ScopeCreate => response.GetResult<ScopeCreateResult>(),
            ProtocolMethods.ScopeRename => response.GetResult<ProtocolScopeRenameResult>(),
            ProtocolMethods.ScopeDelete => response.GetResult<ScopeDeleteResult>(),
            ProtocolMethods.RecordGet => response.GetResult<RecordGetResult>(),
            ProtocolMethods.RecordSet => response.GetResult<RecordSetResult>(),
            ProtocolMethods.RecordRename => response.GetResult<RecordRenameResult>(),
            ProtocolMethods.RecordDelete => response.GetResult<RecordDeleteResult>(),
            ProtocolMethods.RecordList => response.GetResult<RecordListResult>(),
            ProtocolMethods.RecordSearch => response.GetResult<RecordSearchResult>(),
            ProtocolMethods.DaemonPing => response.GetResult<DaemonPingResult>(),
            ProtocolMethods.DaemonVersion => AssertVersionResult(response.GetResult<DaemonVersionResult>()),
            ProtocolMethods.DaemonShutdown => response.GetResult<DaemonShutdownResult>(),
            _ => throw new ArgumentOutOfRangeException(nameof(method), method, "No result DTO is defined for this method."),
        };

        Assert.NotNull(result);
    }

    private static DaemonVersionResult AssertVersionResult(DaemonVersionResult result)
    {
        Assert.Equal(ApplicationVersion, result.ApplicationVersion);
        Assert.Equal(ProtocolConstants.CurrentVersion, result.MinProtocolVersion);
        Assert.Equal(ProtocolConstants.CurrentVersion, result.MaxProtocolVersion);
        return result;
    }

    private static void AssertError(DaemonDispatchResult dispatch, string expectedCode)
    {
        Assert.False(dispatch.RequestsShutdown);
        Assert.Null(dispatch.Response.Result);
        Assert.NotNull(dispatch.Response.Error);
        Assert.Equal(expectedCode, dispatch.Response.Error.Code);
        Assert.Equal(RequestId, dispatch.Response.RequestId);
    }

    private static string MethodForFailure(string scenario) => scenario switch
    {
        "domain-required" or "domain-invalid-regex" or "domain-regex-timeout" => ProtocolMethods.RecordSearch,
        "storage-scope-not-found" or "storage-scope-not-empty" => ProtocolMethods.ScopeDelete,
        "storage-scope-exists" => ProtocolMethods.ScopeCreate,
        "storage-record-not-found" => ProtocolMethods.RecordGet,
        "storage-record-exists" => ProtocolMethods.RecordRename,
        "storage-concurrency" => ProtocolMethods.RecordDelete,
        "storage-migration" or "storage-unavailable" => ProtocolMethods.ScopeList,
        _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown error-mapping scenario."),
    };

    private static Exception CreateFailure(string scenario) => scenario switch
    {
        "domain-required" => new DomainException(new DomainError(DomainErrorCode.Required, "A value is required.", "query")),
        "domain-invalid-regex" => new DomainException(new DomainError(DomainErrorCode.InvalidRegex, "Regex is invalid.", "query")),
        "domain-regex-timeout" => new DomainException(new DomainError(DomainErrorCode.RegexTimeout, "Regex timed out.", "query")),
        "storage-scope-not-found" => new StorageNotFoundException(StorageEntityKind.Scope, "工作区"),
        "storage-record-not-found" => new StorageNotFoundException(StorageEntityKind.Record, "工作区/令牌"),
        "storage-scope-exists" => new StorageConflictException(StorageConflictKind.ScopeExists, "Scope exists."),
        "storage-record-exists" => new StorageConflictException(StorageConflictKind.RecordExists, "Record exists."),
        "storage-concurrency" => new StorageConflictException(StorageConflictKind.Concurrency, "Revision conflicts."),
        "storage-scope-not-empty" => new ScopeNotEmptyException("工作区", 2),
        "storage-migration" => new StorageMigrationException("Schema is unavailable."),
        "storage-unavailable" => new TestStorageException("Database is unavailable."),
        _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown error-mapping scenario."),
    };

    private sealed class FakeDaemonOperations : IDaemonOperations
    {
        private static readonly DateTimeOffset Timestamp = new(2026, 9, 13, 1, 2, 3, TimeSpan.Zero);

        /// <summary>获取用于测试 mutation 的无敏感值成功结果。 / Gets the value-free success result used by mutation tests.</summary>
        public static RecordSetResult SuccessfulRecordSetResult { get; } = new(CreateSummary("令牌"), true);

        /// <summary>获取所有已进入 operations 边界的调用。 / Gets every call that entered the operations boundary.</summary>
        public ConcurrentQueue<Invocation> Invocations { get; } = new();

        /// <summary>获取或设置所有 operations 应返回的失败。 / Gets or sets the failure returned by every operation.</summary>
        public Exception? Failure { get; set; }

        /// <summary>获取或设置可控制 operations 完成时机的测试回调。 / Gets or sets a test callback that controls operation completion.</summary>
        public Func<string, object?, CancellationToken, Task<object>>? OnInvokeAsync { get; set; }

        /// <inheritdoc />
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        /// <inheritdoc />
        public Task<ScopeListResult> ListScopesAsync(ScopeListParams parameters, CancellationToken cancellationToken) =>
            InvokeAsync(ProtocolMethods.ScopeList, parameters, new ScopeListResult([new ScopeDto("工作区")]), cancellationToken);

        /// <inheritdoc />
        public Task<ScopeCreateResult> CreateScopeAsync(ScopeCreateParams parameters, CancellationToken cancellationToken) =>
            InvokeAsync(ProtocolMethods.ScopeCreate, parameters, new ScopeCreateResult(new ScopeDto(parameters.Name)), cancellationToken);

        /// <inheritdoc />
        public Task<ProtocolScopeRenameResult> RenameScopeAsync(ScopeRenameParams parameters, CancellationToken cancellationToken) =>
            InvokeAsync(ProtocolMethods.ScopeRename, parameters, new ProtocolScopeRenameResult(new ScopeDto(parameters.NewName)), cancellationToken);

        /// <inheritdoc />
        public Task<ScopeDeleteResult> DeleteScopeAsync(ScopeDeleteParams parameters, CancellationToken cancellationToken) =>
            InvokeAsync(ProtocolMethods.ScopeDelete, parameters, new ScopeDeleteResult(1), cancellationToken);

        /// <inheritdoc />
        public Task<RecordGetResult> GetRecordAsync(RecordGetParams parameters, CancellationToken cancellationToken) =>
            InvokeAsync(
                ProtocolMethods.RecordGet,
                parameters,
                new RecordGetResult(new RecordDto(
                    parameters.Scope,
                    parameters.Key,
                    "仅用于结果的值",
                    RecordPresentation.Masked,
                    Timestamp,
                    Timestamp,
                    4)),
                cancellationToken);

        /// <inheritdoc />
        public Task<RecordSetResult> SetRecordAsync(RecordSetParams parameters, CancellationToken cancellationToken) =>
            InvokeAsync(ProtocolMethods.RecordSet, parameters, SuccessfulRecordSetResult, cancellationToken);

        /// <inheritdoc />
        public Task<RecordRenameResult> RenameRecordAsync(RecordRenameParams parameters, CancellationToken cancellationToken) =>
            InvokeAsync(ProtocolMethods.RecordRename, parameters, new RecordRenameResult(CreateSummary(parameters.NewKey)), cancellationToken);

        /// <inheritdoc />
        public Task<RecordDeleteResult> DeleteRecordAsync(RecordDeleteParams parameters, CancellationToken cancellationToken) =>
            InvokeAsync(ProtocolMethods.RecordDelete, parameters, new RecordDeleteResult(), cancellationToken);

        /// <inheritdoc />
        public Task<RecordListResult> ListRecordsAsync(RecordListParams parameters, CancellationToken cancellationToken) =>
            InvokeAsync(ProtocolMethods.RecordList, parameters, new RecordListResult([CreateSummary("令牌")]), cancellationToken);

        /// <inheritdoc />
        public Task<RecordSearchResult> SearchRecordsAsync(ProtocolSearchRequest parameters, CancellationToken cancellationToken) =>
            InvokeAsync(ProtocolMethods.RecordSearch, parameters, new RecordSearchResult([CreateSummary("令牌")]), cancellationToken);

        private static RecordSummaryDto CreateSummary(string key) =>
            new("工作区", key, RecordPresentation.Masked, Timestamp, Timestamp, 4);

        private async Task<TResult> InvokeAsync<TResult>(
            string method,
            object? parameters,
            TResult defaultResult,
            CancellationToken cancellationToken)
            where TResult : notnull
        {
            Invocations.Enqueue(new Invocation(method, parameters, cancellationToken));
            if (Failure is not null)
            {
                throw Failure;
            }

            if (OnInvokeAsync is null)
            {
                return defaultResult;
            }

            object result = await OnInvokeAsync(method, parameters, cancellationToken);
            return Assert.IsType<TResult>(result);
        }
    }

    private sealed class CollectingLogger<T> : ILogger<T>
    {
        /// <summary>获取所有已渲染日志项。 / Gets all rendered log entries.</summary>
        public ConcurrentQueue<LogEntry> Entries { get; } = new();

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
            Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue(new LogEntry(logLevel, eventId, formatter(state, exception), exception?.ToString() ?? string.Empty));
    }

    private sealed class TestStorageException : StorageException
    {
        /// <summary>初始化通用测试存储异常。 / Initializes a generic test storage exception.</summary>
        public TestStorageException(string message)
            : base(message)
        {
        }
    }

    private sealed record Invocation(string Method, object? Parameters, CancellationToken CancellationToken);

    private sealed record LogEntry(LogLevel Level, EventId EventId, string Message, string ExceptionText);

    private static class InterlockedExtensions
    {
        /// <summary>以原子方式保存目标值与候选值中的较大者。 / Atomically stores the greater of the target and candidate values.</summary>
        public static void Max(ref int target, int candidate)
        {
            int observed = Volatile.Read(ref target);
            while (candidate > observed)
            {
                int previous = Interlocked.CompareExchange(ref target, candidate, observed);
                if (previous == observed)
                {
                    return;
                }

                observed = previous;
            }
        }
    }
}
