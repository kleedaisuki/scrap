using ProtocolClient = Scrap.Client.ScrapClient;
using ClientConnectionException = Scrap.Client.ScrapConnectionException;
using ClientVersionException = Scrap.Client.ScrapProtocolVersionException;
using ProtocolCaseSensitivity = Scrap.Protocol.CaseSensitivity;
using ProtocolErrorCodes = Scrap.Protocol.ProtocolErrorCodes;
using ProtocolException = Scrap.Protocol.ProtocolException;
using ProtocolPresentation = Scrap.Protocol.RecordPresentation;
using ProtocolRemoteException = Scrap.Protocol.RemoteProtocolException;
using ProtocolSearchMode = Scrap.Protocol.SearchMode;

namespace Scrap.Cli;

/// <summary>
/// 将共享 Scrap.Client DTO 映射到稳定 CLI facade，并集中清洗错误。/
/// Maps shared Scrap.Client DTOs to the stable CLI facade and sanitizes failures in one place.
/// </summary>
internal sealed class ProtocolScrapClientAdapter : IScrapClient, IAsyncDisposable
{
    private ProtocolClient? client;

    /// <inheritdoc />
    public Task<IReadOnlyList<ScopeItem>> ListScopesAsync(CancellationToken cancellationToken) =>
        ExecuteAsync(async value =>
        {
            var result = await value.ListScopesAsync(cancellationToken).ConfigureAwait(false);
            return (IReadOnlyList<ScopeItem>)[.. result.Scopes.Select(scope => new ScopeItem(scope.Name))];
        }, cancellationToken);

    /// <inheritdoc />
    public Task CreateScopeAsync(string scope, CancellationToken cancellationToken) =>
        ExecuteAsync(async value =>
        {
            _ = await value.CreateScopeAsync(scope, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    /// <inheritdoc />
    public Task RenameScopeAsync(string oldName, string newName, CancellationToken cancellationToken) =>
        ExecuteAsync(async value =>
        {
            _ = await value.RenameScopeAsync(oldName, newName, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    /// <inheritdoc />
    public Task<int> DeleteScopeAsync(string scope, bool recursive, CancellationToken cancellationToken) =>
        ExecuteAsync(async value =>
        {
            var result = await value.DeleteScopeAsync(scope, recursive, cancellationToken).ConfigureAwait(false);
            return result.DeletedRecordCount;
        }, cancellationToken);

    /// <inheritdoc />
    public Task SetRecordAsync(
        string scope,
        string key,
        string value,
        RecordPresentation presentation,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async clientValue =>
        {
            _ = await clientValue.SetRecordAsync(
                scope,
                key,
                value,
                ToProtocol(presentation),
                expectedRevision: null,
                cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    /// <inheritdoc />
    public Task<RecordValue> GetRecordAsync(string scope, string key, CancellationToken cancellationToken) =>
        ExecuteAsync(async value =>
        {
            var result = await value.GetRecordAsync(scope, key, cancellationToken).ConfigureAwait(false);
            return new RecordValue(result.Record.Value, FromProtocol(result.Record.Presentation));
        }, cancellationToken);

    /// <inheritdoc />
    public Task RenameRecordAsync(string scope, string oldKey, string newKey, CancellationToken cancellationToken) =>
        ExecuteAsync(async value =>
        {
            _ = await value.RenameRecordAsync(
                scope,
                oldKey,
                newKey,
                expectedRevision: null,
                cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    /// <inheritdoc />
    public Task DeleteRecordAsync(string scope, string key, CancellationToken cancellationToken) =>
        ExecuteAsync(async value =>
        {
            _ = await value.DeleteRecordAsync(
                scope,
                key,
                expectedRevision: null,
                cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<RecordItem>> ListRecordsAsync(string scope, CancellationToken cancellationToken) =>
        ExecuteAsync(async value =>
        {
            var result = await value.ListRecordsAsync(scope, cancellationToken).ConfigureAwait(false);
            return (IReadOnlyList<RecordItem>)[.. result.Records.Select(FromProtocol)];
        }, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<RecordItem>> SearchRecordsAsync(RecordSearch search, CancellationToken cancellationToken) =>
        ExecuteAsync(async value =>
        {
            var request = new Scrap.Protocol.SearchRequest(
                search.Scopes,
                search.Query,
                ToProtocol(search.Mode),
                search.CaseSensitive ? ProtocolCaseSensitivity.Sensitive : ProtocolCaseSensitivity.Insensitive,
                search.Limit);
            var result = await value.SearchRecordsAsync(request, cancellationToken).ConfigureAwait(false);
            return (IReadOnlyList<RecordItem>)[.. result.Records.Select(FromProtocol)];
        }, cancellationToken);

    /// <inheritdoc />
    public Task PingAsync(CancellationToken cancellationToken) =>
        ExecuteAsync(async value =>
        {
            _ = await value.PingAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    /// <inheritdoc />
    public Task<DaemonVersion> GetDaemonVersionAsync(CancellationToken cancellationToken) =>
        ExecuteAsync(async value =>
        {
            var result = await value.GetDaemonVersionAsync(cancellationToken).ConfigureAwait(false);
            return new DaemonVersion(result.ApplicationVersion, result.MinProtocolVersion, result.MaxProtocolVersion);
        }, cancellationToken);

    /// <inheritdoc />
    public Task ShutdownDaemonAsync(CancellationToken cancellationToken) =>
        ExecuteAsync(async value =>
        {
            _ = await value.ShutdownAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    /// <inheritdoc />
    public async Task<bool> ShutdownIfRunningAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using ProtocolClient? existing = await ProtocolClient.TryConnectExistingAsync(
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                return false;
            }

            _ = await existing.ShutdownAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception)
        {
            throw Translate(exception);
        }
    }

    /// <summary>释放共享协议 client 与 pipe。/ Releases the shared protocol client and pipe.</summary>
    public async ValueTask DisposeAsync()
    {
        if (client is not null)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ExecuteAsync(Func<ProtocolClient, Task> action, CancellationToken cancellationToken)
    {
        try
        {
            await action(await GetClientAsync(cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw Translate(exception);
        }
    }

    private async Task<T> ExecuteAsync<T>(Func<ProtocolClient, Task<T>> action, CancellationToken cancellationToken)
    {
        try
        {
            return await action(await GetClientAsync(cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw Translate(exception);
        }
    }

    private async Task<ProtocolClient> GetClientAsync(CancellationToken cancellationToken) =>
        client ??= await ProtocolClient.ConnectAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

    private static Exception Translate(Exception exception) => exception switch
    {
        OperationCanceledException => exception,
        ScrapClientException => exception,
        ProtocolRemoteException remote => new ScrapClientException(MapRemoteError(remote.Error.Code), remote.Error.Message),
        ClientConnectionException connection => new ScrapClientException(ScrapErrorKind.Protocol, connection.Message),
        ClientVersionException version => new ScrapClientException(ScrapErrorKind.Protocol, version.Message),
        ProtocolException protocol => new ScrapClientException(ScrapErrorKind.Protocol, protocol.Message),
        _ => new ScrapClientException(ScrapErrorKind.Protocol, "The daemon request failed unexpectedly."),
    };

    private static ScrapErrorKind MapRemoteError(string code) => code switch
    {
        ProtocolErrorCodes.ScopeNotFound or ProtocolErrorCodes.RecordNotFound => ScrapErrorKind.NotFound,
        ProtocolErrorCodes.ScopeAlreadyExists or ProtocolErrorCodes.ScopeNotEmpty or
            ProtocolErrorCodes.RecordAlreadyExists or ProtocolErrorCodes.Conflict => ScrapErrorKind.Conflict,
        ProtocolErrorCodes.StoreUnavailable or ProtocolErrorCodes.KeyUnavailable or ProtocolErrorCodes.CryptoError =>
            ScrapErrorKind.Store,
        ProtocolErrorCodes.InvalidParams or ProtocolErrorCodes.QueryInvalid or ProtocolErrorCodes.QueryTimeout =>
            ScrapErrorKind.Validation,
        _ => ScrapErrorKind.Protocol,
    };

    private static RecordItem FromProtocol(Scrap.Protocol.RecordSummaryDto record) =>
        new(
            record.Scope,
            record.Key,
            FromProtocol(record.Presentation),
            record.CreatedAt,
            record.UpdatedAt,
            record.Revision);

    private static RecordPresentation FromProtocol(ProtocolPresentation presentation) => presentation switch
    {
        ProtocolPresentation.Masked => RecordPresentation.Masked,
        ProtocolPresentation.Plain => RecordPresentation.Plain,
        _ => throw new ScrapClientException(ScrapErrorKind.Protocol, "The daemon returned an unknown presentation."),
    };

    private static ProtocolPresentation ToProtocol(RecordPresentation presentation) => presentation switch
    {
        RecordPresentation.Masked => ProtocolPresentation.Masked,
        RecordPresentation.Plain => ProtocolPresentation.Plain,
        _ => throw new ScrapClientException(ScrapErrorKind.Validation, "The requested presentation is invalid."),
    };

    private static ProtocolSearchMode ToProtocol(SearchMode mode) => mode switch
    {
        SearchMode.Exact => ProtocolSearchMode.Exact,
        SearchMode.Fuzzy => ProtocolSearchMode.Fuzzy,
        SearchMode.Regex => ProtocolSearchMode.Regex,
        _ => throw new ScrapClientException(ScrapErrorKind.Validation, "The requested search mode is invalid."),
    };
}
