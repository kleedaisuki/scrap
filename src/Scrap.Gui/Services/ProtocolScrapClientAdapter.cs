using Scrap.Client;
using Scrap.Gui.Abstractions;
using Scrap.Gui.Models;
using Scrap.Protocol;
using SharedClient = global::Scrap.Client.ScrapClient;
using GuiPresentation = Scrap.Gui.Models.RecordPresentation;
using GuiSearchMode = Scrap.Gui.Models.SearchMode;
using ProtocolPresentation = Scrap.Protocol.RecordPresentation;
using ProtocolSearchMode = Scrap.Protocol.SearchMode;

namespace Scrap.Gui.Services;

/// <summary>
/// 将共享强类型 client 映射为 GUI 专用语义，隔离协议 DTO 与错误码。
/// Maps the shared typed client into GUI-specific semantics, isolating protocol DTOs and error codes.
/// </summary>
internal sealed class ProtocolScrapClientAdapter : IScrapClient
{
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private SharedClient? _client;
    private int _disposed;

    /// <inheritdoc />
    public async Task<IReadOnlyList<ScopeSummary>> ListScopesAsync(CancellationToken cancellationToken)
    {
        try
        {
            SharedClient client = await GetClientAsync(cancellationToken);
            ScopeListResult result = await client.ListScopesAsync(cancellationToken);
            var scopes = new List<ScopeSummary>(result.Scopes.Count);
            foreach (ScopeDto scope in result.Scopes)
            {
                int count = await CountRecordsAsync(client, scope.Name, cancellationToken);
                scopes.Add(new ScopeSummary(scope.Name, count));
            }

            return scopes;
        }
        catch (Exception exception)
        {
            throw Map(exception, mutation: false);
        }
    }

    /// <inheritdoc />
    public async Task CreateScopeAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            SharedClient client = await GetMutationClientAsync(cancellationToken);
            await client.CreateScopeAsync(name, cancellationToken);
        }
        catch (Exception exception)
        {
            throw Map(exception, mutation: true);
        }
    }

    /// <inheritdoc />
    public async Task RenameScopeAsync(string oldName, string newName, CancellationToken cancellationToken)
    {
        try
        {
            SharedClient client = await GetMutationClientAsync(cancellationToken);
            await client.RenameScopeAsync(oldName, newName, cancellationToken);
        }
        catch (Exception exception)
        {
            throw Map(exception, mutation: true);
        }
    }

    /// <inheritdoc />
    public async Task DeleteScopeAsync(
        string name,
        bool recursive,
        int? expectedRecordCount,
        CancellationToken cancellationToken)
    {
        try
        {
            SharedClient client = await GetMutationClientAsync(cancellationToken);
            await client.DeleteScopeAsync(name, recursive, expectedRecordCount, cancellationToken);
        }
        catch (Exception exception)
        {
            throw Map(exception, mutation: true);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RecordCandidate>> SearchAsync(
        RecordSearchRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            SharedClient client = await GetClientAsync(cancellationToken);
            var protocolRequest = new SearchRequest(
                request.Scopes,
                request.Query,
                ToProtocol(request.Mode),
                request.CaseSensitive ? CaseSensitivity.Sensitive : CaseSensitivity.Insensitive,
                request.Limit);
            RecordSearchResult result = await client.SearchRecordsAsync(protocolRequest, cancellationToken);

            return result.Records
                .Select(record => new RecordCandidate(record.Scope, record.Key, ToGui(record.Presentation)))
                .ToArray();
        }
        catch (Exception exception)
        {
            throw Map(exception, mutation: false);
        }
    }

    /// <inheritdoc />
    public async Task<RecordDetails> GetRecordAsync(
        string scope,
        string key,
        CancellationToken cancellationToken)
    {
        try
        {
            SharedClient client = await GetClientAsync(cancellationToken);
            RecordGetResult result = await client.GetRecordAsync(scope, key, cancellationToken);
            RecordDto record = result.Record;
            return new RecordDetails(
                record.Scope,
                record.Key,
                record.Value,
                ToGui(record.Presentation),
                record.UpdatedAt,
                record.Revision);
        }
        catch (Exception exception)
        {
            throw Map(exception, mutation: false);
        }
    }

    /// <inheritdoc />
    public async Task SaveRecordAsync(SaveRecordRequest request, CancellationToken cancellationToken)
    {
        if (request.OriginalKey is not null &&
            !string.Equals(request.OriginalKey, request.Key, StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                "Record rename must be performed as its own atomic daemon operation; the editor never emulates rename with set plus delete.");
        }

        try
        {
            SharedClient client = await GetMutationClientAsync(cancellationToken);
            await client.SetRecordAsync(
                request.Scope,
                request.Key,
                request.Value,
                ToProtocol(request.Presentation),
                request.ExpectedRevision,
                cancellationToken);
        }
        catch (Exception exception)
        {
            throw Map(exception, mutation: true);
        }
    }

    /// <inheritdoc />
    public async Task DeleteRecordAsync(
        string scope,
        string key,
        long? expectedRevision,
        CancellationToken cancellationToken)
    {
        try
        {
            SharedClient client = await GetMutationClientAsync(cancellationToken);
            await client.DeleteRecordAsync(scope, key, expectedRevision, cancellationToken);
        }
        catch (Exception exception)
        {
            throw Map(exception, mutation: true);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _connectionGate.WaitAsync(CancellationToken.None);
        try
        {
            if (_client is not null)
            {
                await _client.DisposeAsync();
                _client = null;
            }
        }
        finally
        {
            _connectionGate.Release();
            _connectionGate.Dispose();
        }
    }

    private async Task<SharedClient> GetClientAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_client is not null)
        {
            return _client;
        }

        await _connectionGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            _client ??= await SharedClient.ConnectAsync(cancellationToken: cancellationToken);
            return _client;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private async Task<SharedClient> GetMutationClientAsync(CancellationToken cancellationToken)
    {
        try
        {
            SharedClient client = await GetClientAsync(cancellationToken);
            await client.PingAsync(cancellationToken);
            return client;
        }
        catch (OperationCanceledException exception)
        {
            throw new ScrapClientException(
                ScrapClientErrorKind.DaemonUnavailable,
                "Timed out while preparing the daemon connection; no mutation was sent.",
                exception);
        }
        catch (Exception exception)
        {
            throw Map(exception, mutation: false);
        }
    }

    private static async Task<int> CountRecordsAsync(
        SharedClient client,
        string scope,
        CancellationToken cancellationToken)
    {
        int count = 0;
        string? cursor = null;
        do
        {
            RecordListResult page = await client.ListRecordsAsync(scope, cursor, cancellationToken: cancellationToken);
            count = checked(count + page.Records.Count);
            EnsureProgressingCursor(cursor, page);
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        return count;
    }

    private static void EnsureProgressingCursor(string? previousCursor, RecordListResult page)
    {
        if (page.NextCursor is not null &&
            (page.Records.Count == 0 ||
             (previousCursor is not null && string.CompareOrdinal(page.NextCursor, previousCursor) <= 0)))
        {
            throw new ProtocolException(
                ProtocolErrorCodes.InvalidJson,
                "The daemon returned a non-progressing record-list cursor.");
        }
    }

    private static ProtocolSearchMode ToProtocol(GuiSearchMode mode) => mode switch
    {
        GuiSearchMode.Exact => ProtocolSearchMode.Exact,
        GuiSearchMode.Fuzzy => ProtocolSearchMode.Fuzzy,
        GuiSearchMode.Regex => ProtocolSearchMode.Regex,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static ProtocolPresentation ToProtocol(GuiPresentation presentation) => presentation switch
    {
        GuiPresentation.Masked => ProtocolPresentation.Masked,
        GuiPresentation.Plain => ProtocolPresentation.Plain,
        _ => throw new ArgumentOutOfRangeException(nameof(presentation)),
    };

    private static GuiPresentation ToGui(ProtocolPresentation presentation) => presentation switch
    {
        ProtocolPresentation.Masked => GuiPresentation.Masked,
        ProtocolPresentation.Plain => GuiPresentation.Plain,
        _ => throw new ArgumentOutOfRangeException(nameof(presentation)),
    };

    private static Exception Map(Exception exception, bool mutation)
    {
        if (exception is ScrapClientException or OperationCanceledException)
        {
            return exception;
        }

        if (exception is RemoteProtocolException remote)
        {
            ScrapClientErrorKind kind = remote.ErrorCode switch
            {
                ProtocolErrorCodes.QueryInvalid or ProtocolErrorCodes.QueryTimeout => ScrapClientErrorKind.InvalidQuery,
                ProtocolErrorCodes.ScopeAlreadyExists or ProtocolErrorCodes.RecordAlreadyExists or
                    ProtocolErrorCodes.ScopeNotEmpty or ProtocolErrorCodes.Conflict => ScrapClientErrorKind.Conflict,
                ProtocolErrorCodes.ScopeNotFound or ProtocolErrorCodes.RecordNotFound => ScrapClientErrorKind.NotFound,
                ProtocolErrorCodes.KeyUnavailable => ScrapClientErrorKind.KeyProviderUnavailable,
                ProtocolErrorCodes.StoreUnavailable => ScrapClientErrorKind.StoreUnavailable,
                ProtocolErrorCodes.CryptoError => ScrapClientErrorKind.CorruptStore,
                ProtocolErrorCodes.DaemonShuttingDown => ScrapClientErrorKind.DaemonUnavailable,
                _ => ScrapClientErrorKind.Unknown,
            };
            return new ScrapClientException(kind, remote.Message, remote);
        }

        if (exception is ScrapConnectionException)
        {
            ScrapClientErrorKind kind = mutation
                ? ScrapClientErrorKind.OutcomeUnknown
                : ScrapClientErrorKind.DaemonUnavailable;
            return new ScrapClientException(kind, exception.Message, exception);
        }

        if (exception is ScrapProtocolVersionException)
        {
            return new ScrapClientException(
                ScrapClientErrorKind.DaemonUnavailable,
                "The installed scrap GUI and scrapd protocol versions are incompatible. Update both applications together.",
                exception);
        }

        if (exception is ProtocolException)
        {
            return new ScrapClientException(
                ScrapClientErrorKind.DaemonUnavailable,
                "The daemon returned an invalid protocol response. Restart scrapd, then retry.",
                exception);
        }

        return new ScrapClientException(
            ScrapClientErrorKind.Unknown,
            "The operation could not be completed. Retry, then inspect scrapd logs if it persists.",
            exception);
    }
}
