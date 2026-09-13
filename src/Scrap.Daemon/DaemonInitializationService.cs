using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using Scrap.Crypto;
using Scrap.Platform.Secrets;
using Scrap.Protocol;
using Scrap.Storage.Sqlite;

namespace Scrap.Daemon;

/// <summary>
/// 在 listener 启动前尝试初始化 operations，但缓存失败而不是让 client 只得到连接超时。
/// / Attempts operations initialization before the listener starts, caching failures instead of exposing only a connection timeout.
/// </summary>
internal sealed class DaemonInitializationService : IHostedService
{
    private static readonly Action<ILogger, string, string, Exception?> LogFailure =
        LoggerMessage.Define<string, string>(
            LogLevel.Error,
            new EventId(1401, "DaemonInitializationFailure"),
            "Daemon initialization failed with {ErrorCode} ({ExceptionType}); sensitive details were not logged.");

    private static readonly Action<ILogger, Exception?> LogSuccess = LoggerMessage.Define(
        LogLevel.Information,
        new EventId(1402, "DaemonInitializationSuccess"),
        "Daemon store and record encryption initialized.");

    private readonly IDaemonOperations operations;
    private readonly DaemonInitializationState state;
    private readonly ILogger<DaemonInitializationService> logger;

    /// <summary>初始化 hosted service。 / Initializes the hosted service.</summary>
    public DaemonInitializationService(
        IDaemonOperations operations,
        DaemonInitializationState state,
        ILogger<DaemonInitializationService> logger)
    {
        this.operations = operations;
        this.state = state;
        this.logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await operations.InitializeAsync(cancellationToken).ConfigureAwait(false);
            state.SetReady();
            LogSuccess(logger, null);
        }
        catch (Exception exception)
        {
            var safe = Map(exception);
            state.SetFailure(safe);
            LogFailure(logger, safe.ErrorCode, exception.GetType().Name, null);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static DaemonInitializationException Map(Exception exception) => exception switch
    {
        MasterKeyUnavailableException or MasterKeyProviderException => new(
            ProtocolErrorCodes.KeyUnavailable,
            "The OS-protected master key is unavailable."),
        RecordAuthenticationException or EncryptedRecordFormatException => new(
            ProtocolErrorCodes.CryptoError,
            "Encrypted record data is unavailable."),
        StorageException or SqliteException => new(
            ProtocolErrorCodes.StoreUnavailable,
            "The SQLite store is unavailable."),
        _ => new(
            ProtocolErrorCodes.InternalError,
            "Daemon initialization failed."),
    };
}
