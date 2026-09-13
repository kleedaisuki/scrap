using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Scrap.Domain;
using Scrap.Crypto;
using Scrap.Platform.Secrets;
using Scrap.Protocol;
using Scrap.Storage.Sqlite;
using ProtocolSearchRequest = Scrap.Protocol.SearchRequest;

namespace Scrap.Daemon;

/// <summary>
/// 将稳定的 method 名映射到强类型 handler，并统一执行并发和错误语义。
/// / Maps stable method names to typed handlers and centralizes concurrency and error semantics.
/// </summary>
internal sealed class DaemonRequestDispatcher
{
    private static readonly Action<ILogger, string, string, string, Exception?> LogMethodFailure =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Warning,
            new EventId(1001, "DaemonMethodFailure"),
            "Daemon method {Method} failed with {ErrorCode} ({ExceptionType}); request payload was not logged.");

    private static readonly Action<ILogger, string, double, string, Exception?> LogMethodCompletion =
        LoggerMessage.Define<string, double, string>(
            LogLevel.Information,
            new EventId(1002, "DaemonMethodCompletion"),
            "Daemon method {Method} completed in {ElapsedMilliseconds} ms with {Outcome}; request payload was not logged.");

    private static readonly HashSet<string> MutationMethods = new(StringComparer.Ordinal)
    {
        ProtocolMethods.ScopeCreate,
        ProtocolMethods.ScopeRename,
        ProtocolMethods.ScopeDelete,
        ProtocolMethods.RecordSet,
        ProtocolMethods.RecordRename,
        ProtocolMethods.RecordDelete,
    };

    private static readonly HashSet<string> KnownMethods = new(StringComparer.Ordinal)
    {
        ProtocolMethods.ScopeList,
        ProtocolMethods.ScopeCreate,
        ProtocolMethods.ScopeRename,
        ProtocolMethods.ScopeDelete,
        ProtocolMethods.RecordGet,
        ProtocolMethods.RecordSet,
        ProtocolMethods.RecordRename,
        ProtocolMethods.RecordDelete,
        ProtocolMethods.RecordList,
        ProtocolMethods.RecordSearch,
        ProtocolMethods.DaemonPing,
        ProtocolMethods.DaemonVersion,
        ProtocolMethods.DaemonShutdown,
    };

    private readonly IDaemonOperations operations;
    private readonly RequestExecutionCoordinator coordinator;
    private readonly ILogger<DaemonRequestDispatcher> logger;
    private readonly string applicationVersion;
    private readonly DaemonInitializationState initializationState;

    /// <summary>
    /// 初始化 dispatcher。 / Initializes the dispatcher.
    /// </summary>
    public DaemonRequestDispatcher(
        IDaemonOperations operations,
        RequestExecutionCoordinator coordinator,
        ILogger<DaemonRequestDispatcher> logger,
        string applicationVersion)
        : this(operations, coordinator, logger, applicationVersion, CreateReadyInitializationState())
    {
    }

    /// <summary>
    /// 使用共享初始化状态初始化 dispatcher。 / Initializes the dispatcher with shared initialization state.
    /// </summary>
    public DaemonRequestDispatcher(
        IDaemonOperations operations,
        RequestExecutionCoordinator coordinator,
        ILogger<DaemonRequestDispatcher> logger,
        string applicationVersion,
        DaemonInitializationState initializationState)
    {
        this.operations = operations;
        this.coordinator = coordinator;
        this.logger = logger;
        this.applicationVersion = applicationVersion;
        this.initializationState = initializationState;
    }

    /// <summary>
    /// 执行一个已完成 framing 的请求。 / Executes one fully framed request.
    /// </summary>
    /// <param name="request">协议请求；payload 不会被记录。 / Protocol request; its payload is never logged.</param>
    /// <param name="cancellationToken">读取请求可观察的取消令牌。 / Cancellation token observed by reads.</param>
    /// <returns>响应和可选关闭意图。 / Response and optional shutdown intent.</returns>
    public async Task<DaemonDispatchResult> DispatchAsync(ProtocolRequest request, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        string outcome = "success";
        string loggedMethod = SafeMethodForLog(request.Method);

        try
        {
            request.EnsureValid();
            if (request.ProtocolVersion != ProtocolConstants.CurrentVersion)
            {
                outcome = ProtocolErrorCodes.ProtocolVersionUnsupported;
                return Failure(request.RequestId, ProtocolErrorCodes.ProtocolVersionUnsupported, "Protocol version is not supported.");
            }

            if (request.Method == ProtocolMethods.DaemonShutdown)
            {
                Deserialize<DaemonShutdownParams>(request);
                return new(
                    ProtocolResponse.Success(request.RequestId, new DaemonShutdownResult()),
                    RequestsShutdown: true);
            }

            if (!IsDaemonControlMethod(request.Method))
            {
                await initializationState.WaitUntilSettledAsync(cancellationToken).ConfigureAwait(false);
                initializationState.EnsureReady();
            }

            object result = MutationMethods.Contains(request.Method)
                ? await coordinator.ExecuteMutationAsync(
                    token => ExecuteAsync(request, token),
                    cancellationToken).ConfigureAwait(false)
                : await coordinator.ExecuteReadAsync(
                    token => ExecuteAsync(request, token),
                    cancellationToken).ConfigureAwait(false);

            return new(ProtocolResponse.Success(request.RequestId, result));
        }
        catch (Exception exception) when (TryMapException(exception, out ProtocolError? error))
        {
            outcome = error!.Code;
            LogMethodFailure(
                logger,
                loggedMethod,
                error.Code,
                exception.GetType().Name,
                null);
            return new(ProtocolResponse.Failure(SafeRequestId(request.RequestId), error));
        }
        finally
        {
            TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
            LogMethodCompletion(
                logger,
                loggedMethod,
                elapsed.TotalMilliseconds,
                outcome,
                null);
        }
    }

    private Task<object> ExecuteAsync(ProtocolRequest request, CancellationToken cancellationToken) => request.Method switch
    {
        ProtocolMethods.DaemonPing => BoxAsync(ValidateAndReturn<DaemonPingParams, DaemonPingResult>(request, new())),
        ProtocolMethods.DaemonVersion => BoxAsync(ValidateAndReturn<DaemonVersionParams, DaemonVersionResult>(
            request,
            new(
                applicationVersion,
                ProtocolConstants.CurrentVersion,
                ProtocolConstants.CurrentVersion))),
        ProtocolMethods.ScopeList => BoxAsync(ValidateAndRun<ScopeListParams, ScopeListResult>(
            request,
            () => operations.ListScopesAsync(cancellationToken))),
        ProtocolMethods.ScopeCreate => BoxAsync(operations.CreateScopeAsync(Deserialize<ScopeCreateParams>(request), cancellationToken)),
        ProtocolMethods.ScopeRename => BoxAsync(operations.RenameScopeAsync(Deserialize<ScopeRenameParams>(request), cancellationToken)),
        ProtocolMethods.ScopeDelete => BoxAsync(operations.DeleteScopeAsync(Deserialize<ScopeDeleteParams>(request), cancellationToken)),
        ProtocolMethods.RecordGet => BoxAsync(operations.GetRecordAsync(Deserialize<RecordGetParams>(request), cancellationToken)),
        ProtocolMethods.RecordSet => BoxAsync(operations.SetRecordAsync(Deserialize<RecordSetParams>(request), cancellationToken)),
        ProtocolMethods.RecordRename => BoxAsync(operations.RenameRecordAsync(Deserialize<RecordRenameParams>(request), cancellationToken)),
        ProtocolMethods.RecordDelete => BoxAsync(operations.DeleteRecordAsync(Deserialize<RecordDeleteParams>(request), cancellationToken)),
        ProtocolMethods.RecordList => BoxAsync(operations.ListRecordsAsync(Deserialize<RecordListParams>(request), cancellationToken)),
        ProtocolMethods.RecordSearch => BoxAsync(operations.SearchRecordsAsync(Deserialize<ProtocolSearchRequest>(request), cancellationToken)),
        _ => throw new ProtocolException(ProtocolErrorCodes.MethodNotFound, "Method is not recognized."),
    };

    private static T Deserialize<T>(ProtocolRequest request) where T : notnull
    {
        try
        {
            return ProtocolJson.DeserializeElement<T>(request.Params);
        }
        catch (ProtocolException exception)
        {
            throw new ProtocolException(ProtocolErrorCodes.InvalidParams, "Method parameters are invalid.", exception);
        }
        catch (ArgumentException exception)
        {
            throw new ProtocolException(ProtocolErrorCodes.InvalidParams, "Method parameters are invalid.", exception);
        }
        catch (JsonException exception)
        {
            throw new ProtocolException(ProtocolErrorCodes.InvalidParams, "Method parameters are invalid.", exception);
        }
    }

    private static async Task<object> BoxAsync<T>(Task<T> task) where T : notnull =>
        await task.ConfigureAwait(false);

    private static Task<object> BoxAsync<T>(T value) where T : notnull => Task.FromResult<object>(value);

    private static TResult ValidateAndReturn<TParams, TResult>(ProtocolRequest request, TResult result)
        where TParams : notnull
        where TResult : notnull
    {
        Deserialize<TParams>(request);
        return result;
    }

    private static Task<TResult> ValidateAndRun<TParams, TResult>(
        ProtocolRequest request,
        Func<Task<TResult>> operation)
        where TParams : notnull
        where TResult : notnull
    {
        Deserialize<TParams>(request);
        return operation();
    }

    private static DaemonDispatchResult Failure(string requestId, string code, string message) =>
        new(ProtocolResponse.Failure(SafeRequestId(requestId), new ProtocolError(code, message)));

    private static string SafeRequestId(string? requestId) => string.IsNullOrEmpty(requestId) ? "invalid" : requestId;

    private static bool TryMapException(Exception exception, out ProtocolError? error)
    {
        error = exception switch
        {
            ProtocolException protocol => new(protocol.ErrorCode, protocol.Message),
            DaemonStoppingException => new(ProtocolErrorCodes.DaemonShuttingDown, "The daemon is shutting down."),
            DaemonInitializationException initialization => new(initialization.ErrorCode, initialization.Message),
            StorageNotFoundException storage => MapNotFound(storage),
            ScopeNotEmptyException => new(ProtocolErrorCodes.ScopeNotEmpty, "Scope is not empty."),
            StorageConflictException storage => MapConflict(storage),
            StorageMigrationException => new(ProtocolErrorCodes.StoreUnavailable, "The store schema is unavailable."),
            StorageException => new(ProtocolErrorCodes.StoreUnavailable, "The store is unavailable."),
            SqliteException => new(ProtocolErrorCodes.StoreUnavailable, "The SQLite store is unavailable."),
            MasterKeyUnavailableException or MasterKeyProviderException => new(ProtocolErrorCodes.KeyUnavailable, "The OS-protected master key is unavailable."),
            RecordAuthenticationException or EncryptedRecordFormatException or CryptographicException or DecoderFallbackException => new(ProtocolErrorCodes.CryptoError, "Encrypted record data is unavailable."),
            DomainException domain => MapDomainError(domain.Error),
            JsonException => new(ProtocolErrorCodes.InvalidParams, "Method parameters are invalid."),
            OperationCanceledException => new(ProtocolErrorCodes.InvalidRequest, "Request was cancelled."),
            _ => new(ProtocolErrorCodes.InternalError, "An internal daemon error occurred."),
        };

        return true;
    }

    private static ProtocolError MapNotFound(StorageNotFoundException exception) => exception.Entity switch
    {
        StorageEntityKind.Scope => new(ProtocolErrorCodes.ScopeNotFound, "Scope does not exist."),
        StorageEntityKind.Record => new(ProtocolErrorCodes.RecordNotFound, "Record does not exist."),
        _ => new(ProtocolErrorCodes.StoreUnavailable, "The store is unavailable."),
    };

    private static ProtocolError MapConflict(StorageConflictException exception) => exception.Kind switch
    {
        StorageConflictKind.ScopeExists => new(ProtocolErrorCodes.ScopeAlreadyExists, "Scope already exists."),
        StorageConflictKind.RecordExists => new(ProtocolErrorCodes.RecordAlreadyExists, "Record already exists."),
        StorageConflictKind.Concurrency => new(ProtocolErrorCodes.Conflict, "A concurrency condition conflicted."),
        _ => new(ProtocolErrorCodes.Conflict, "A uniqueness or concurrency condition conflicted."),
    };

    private static string SafeMethodForLog(string? method) => string.IsNullOrEmpty(method)
        ? "<invalid>"
        : KnownMethods.Contains(method) ? method : "<unknown>";

    private static bool IsDaemonControlMethod(string method) => method is
        ProtocolMethods.DaemonPing or ProtocolMethods.DaemonVersion or ProtocolMethods.DaemonShutdown;

    private static DaemonInitializationState CreateReadyInitializationState()
    {
        var state = new DaemonInitializationState();
        state.SetReady();
        return state;
    }

    private static ProtocolError MapDomainError(DomainError error) => error.Code switch
    {
        DomainErrorCode.InvalidRegex => new(ProtocolErrorCodes.QueryInvalid, "Search expression is invalid."),
        DomainErrorCode.RegexTimeout => new(ProtocolErrorCodes.QueryTimeout, "Search evaluation timed out."),
        _ => new(ProtocolErrorCodes.InvalidParams, error.Message),
    };
}
