using Scrap.Gui.Models;

namespace Scrap.Gui.Abstractions;

/// <summary>
/// GUI 与具体 IPC 协议之间的小型防腐层（anti-corruption layer）。
/// Small anti-corruption layer between the GUI and the concrete IPC protocol.
/// </summary>
/// <remarks>
/// 实现必须尊重取消令牌，并且不得在异常消息中包含记录 value。
/// Implementations must honor cancellation and must not include record values in exception messages.
/// </remarks>
public interface IScrapClient : IAsyncDisposable
{
    /// <summary>列出 scope。Lists scopes.</summary>
    Task<IReadOnlyList<ScopeSummary>> ListScopesAsync(CancellationToken cancellationToken);

    /// <summary>创建 scope。Creates a scope.</summary>
    Task CreateScopeAsync(string name, CancellationToken cancellationToken);

    /// <summary>原子重命名 scope。Atomically renames a scope.</summary>
    Task RenameScopeAsync(string oldName, string newName, CancellationToken cancellationToken);

    /// <summary>
    /// 删除 scope；非空 scope 只有在 recursive 为 true 时才可删除。
    /// Deletes a scope; a non-empty scope may only be deleted when recursive is true.
    /// </summary>
    Task DeleteScopeAsync(string name, bool recursive, CancellationToken cancellationToken);

    /// <summary>搜索 key 候选。Searches key candidates.</summary>
    Task<IReadOnlyList<RecordCandidate>> SearchAsync(
        RecordSearchRequest request,
        CancellationToken cancellationToken);

    /// <summary>读取一条被明确选择的记录。Reads an explicitly selected record.</summary>
    Task<RecordDetails> GetRecordAsync(string scope, string key, CancellationToken cancellationToken);

    /// <summary>创建或原子完整替换记录。Creates or atomically wholly replaces a record.</summary>
    Task SaveRecordAsync(SaveRecordRequest request, CancellationToken cancellationToken);

    /// <summary>删除精确的 scope/key。Deletes an exact scope/key.</summary>
    Task DeleteRecordAsync(string scope, string key, long? expectedRevision, CancellationToken cancellationToken);
}

/// <summary>
/// 可测试的剪贴板边界。Testable clipboard boundary.
/// </summary>
public interface IClipboardService
{
    /// <summary>写入文本。Writes text.</summary>
    Task SetTextAsync(string text, CancellationToken cancellationToken);

    /// <summary>读取当前文本；无文本时返回 null。Reads current text, or null when unavailable.</summary>
    Task<string?> GetTextAsync(CancellationToken cancellationToken);

    /// <summary>清除剪贴板。Clears the clipboard.</summary>
    Task ClearAsync(CancellationToken cancellationToken);
}
