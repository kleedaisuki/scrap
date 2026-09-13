namespace Scrap.Protocol;

/// <summary>
/// 表示用户可见的扁平 scope。名称按 ordinal、大小写敏感语义标识。
/// / Represents a user-visible flat scope. Its name has ordinal, case-sensitive identity semantics.
/// </summary>
/// <param name="Name">不经隐式 trim 的 Unicode 名称。 / Unicode name without implicit trimming.</param>
public sealed record ScopeDto(string Name);

/// <summary>
/// 表示 <c>scope.list</c> 的 ordinal keyset pagination 参数。
/// / Represents ordinal keyset-pagination parameters for <c>scope.list</c>.
/// </summary>
/// <param name="AfterName">上一页最后一个 scope 名称；null 表示第一页。 / Last scope name from the previous page; null selects the first page.</param>
/// <param name="Limit">本页最多返回的 scope 数量。 / Maximum scopes to return in this page.</param>
public sealed record ScopeListParams(
    string? AfterName = null,
    int Limit = ProtocolConstants.DefaultScopeListPageSize)
{
    /// <summary>
    /// 获取范围为 1 到 <see cref="ProtocolConstants.MaxScopeListPageSize"/> 的页大小。
    /// / Gets a page size from 1 through <see cref="ProtocolConstants.MaxScopeListPageSize"/>.
    /// </summary>
    public int Limit { get; init; } = Limit is > 0 and <= ProtocolConstants.MaxScopeListPageSize
        ? Limit
        : throw new ArgumentOutOfRangeException(
            nameof(Limit),
            Limit,
            $"Scope list limit must be between 1 and {ProtocolConstants.MaxScopeListPageSize}.");
}

/// <summary>
/// 表示 <c>scope.list</c> 的结果。 / Represents the result of <c>scope.list</c>.
/// </summary>
/// <param name="Scopes">按 daemon 定义的稳定顺序返回的 scope。 / Scopes in the daemon-defined stable order.</param>
/// <param name="NextCursor">存在下一页时应作为后续 <see cref="ScopeListParams.AfterName"/> 传回的名称；末页为 null。 / Name to pass back as <see cref="ScopeListParams.AfterName"/> when another page exists; null on the final page.</param>
/// <remarks>
/// daemon 必须按名称的 ordinal 顺序分页。为保证响应不超过 frame 上限，即使尚未达到请求 limit，也可提前结束本页并返回 cursor。
/// / The daemon must paginate names in ordinal order. To keep the response below the frame limit, it may end a page early and return a cursor before reaching the requested limit.
/// </remarks>
public sealed record ScopeListResult(
    IReadOnlyList<ScopeDto> Scopes,
    string? NextCursor = null);

/// <summary>
/// 表示 <c>scope.create</c> 参数。 / Represents <c>scope.create</c> parameters.
/// </summary>
/// <param name="Name">要创建的精确名称。 / Exact name to create.</param>
public sealed record ScopeCreateParams(string Name);

/// <summary>
/// 表示 <c>scope.create</c> 结果。 / Represents the result of <c>scope.create</c>.
/// </summary>
/// <param name="Scope">已创建 scope。 / Created scope.</param>
public sealed record ScopeCreateResult(ScopeDto Scope);

/// <summary>
/// 表示 <c>scope.rename</c> 参数；操作必须在 daemon 中原子完成。
/// / Represents <c>scope.rename</c> parameters; the daemon must perform the operation atomically.
/// </summary>
/// <param name="OldName">当前精确名称。 / Current exact name.</param>
/// <param name="NewName">目标精确名称。 / Target exact name.</param>
public sealed record ScopeRenameParams(string OldName, string NewName);

/// <summary>
/// 表示 <c>scope.rename</c> 结果。 / Represents the result of <c>scope.rename</c>.
/// </summary>
/// <param name="Scope">重命名后的 scope。 / Renamed scope.</param>
public sealed record ScopeRenameResult(ScopeDto Scope);

/// <summary>
/// 表示 <c>scope.delete</c> 参数。 / Represents <c>scope.delete</c> parameters.
/// </summary>
/// <param name="Name">待删除的精确 scope 名称。 / Exact scope name to delete.</param>
/// <param name="Recursive">是否显式允许级联删除 record。 / Whether cascading record deletion is explicitly allowed.</param>
/// <param name="ExpectedRecordCount">递归删除的可选事务前置条件；实际数量不同则返回 conflict。 / Optional transactional precondition for recursive deletion; a different actual count produces a conflict.</param>
public sealed record ScopeDeleteParams(
    string Name,
    bool Recursive = false,
    int? ExpectedRecordCount = null)
{
    /// <summary>
    /// 获取非负的预期 record 数量，或在调用方未指定时返回 null。
    /// / Gets the non-negative expected record count, or null when the caller did not specify one.
    /// </summary>
    public int? ExpectedRecordCount { get; init; } =
        ExpectedRecordCount is null or >= 0
            ? ExpectedRecordCount
            : throw new ArgumentOutOfRangeException(
                nameof(ExpectedRecordCount),
                ExpectedRecordCount,
                "Expected record count cannot be negative.");
}

/// <summary>
/// 表示 <c>scope.delete</c> 结果。 / Represents the result of <c>scope.delete</c>.
/// </summary>
/// <param name="DeletedRecordCount">随 scope 逻辑删除的 record 数量。 / Number of records logically deleted with the scope.</param>
public sealed record ScopeDeleteResult(int DeletedRecordCount);
