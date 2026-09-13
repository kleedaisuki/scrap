using Scrap.Gui.Abstractions;
using Scrap.Gui.Models;

namespace Scrap.Gui.Services;

/// <summary>
/// 协议适配器尚未装配时的显式占位 client；它不会伪造本地数据。
/// Explicit placeholder client used until the protocol adapter is wired; it never fabricates local data.
/// </summary>
internal sealed class UnavailableScrapClient : IScrapClient
{
    private const string Diagnostic =
        "无法连接 scrapd。请确认 scrapd 已安装，然后使用“重试连接”。 / " +
        "Cannot connect to scrapd. Ensure scrapd is installed, then choose Retry.";

    /// <inheritdoc />
    public Task<IReadOnlyList<ScopeSummary>> ListScopesAsync(CancellationToken cancellationToken) => Failed<IReadOnlyList<ScopeSummary>>();

    /// <inheritdoc />
    public Task CreateScopeAsync(string name, CancellationToken cancellationToken) => Failed();

    /// <inheritdoc />
    public Task RenameScopeAsync(string oldName, string newName, CancellationToken cancellationToken) => Failed();

    /// <inheritdoc />
    public Task DeleteScopeAsync(string name, bool recursive, CancellationToken cancellationToken) => Failed();

    /// <inheritdoc />
    public Task<IReadOnlyList<RecordCandidate>> SearchAsync(RecordSearchRequest request, CancellationToken cancellationToken) => Failed<IReadOnlyList<RecordCandidate>>();

    /// <inheritdoc />
    public Task<RecordDetails> GetRecordAsync(string scope, string key, CancellationToken cancellationToken) => Failed<RecordDetails>();

    /// <inheritdoc />
    public Task SaveRecordAsync(SaveRecordRequest request, CancellationToken cancellationToken) => Failed();

    /// <inheritdoc />
    public Task DeleteRecordAsync(string scope, string key, long? expectedRevision, CancellationToken cancellationToken) => Failed();

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static Task Failed() => Task.FromException(new ScrapClientException(ScrapClientErrorKind.DaemonUnavailable, Diagnostic));

    private static Task<T> Failed<T>() => Task.FromException<T>(new ScrapClientException(ScrapClientErrorKind.DaemonUnavailable, Diagnostic));
}
