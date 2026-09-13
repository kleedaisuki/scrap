namespace Scrap.Protocol;

/// <summary>
/// 表示用户可见的扁平 scope。名称按 ordinal、大小写敏感语义标识。
/// / Represents a user-visible flat scope. Its name has ordinal, case-sensitive identity semantics.
/// </summary>
/// <param name="Name">不经隐式 trim 的 Unicode 名称。 / Unicode name without implicit trimming.</param>
public sealed record ScopeDto(string Name);

/// <summary>
/// 表示 <c>scope.list</c> 的空参数。 / Represents the empty parameters for <c>scope.list</c>.
/// </summary>
public sealed record ScopeListParams;

/// <summary>
/// 表示 <c>scope.list</c> 的结果。 / Represents the result of <c>scope.list</c>.
/// </summary>
/// <param name="Scopes">按 daemon 定义的稳定顺序返回的 scope。 / Scopes in the daemon-defined stable order.</param>
public sealed record ScopeListResult(IReadOnlyList<ScopeDto> Scopes);

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
public sealed record ScopeDeleteParams(string Name, bool Recursive = false);

/// <summary>
/// 表示 <c>scope.delete</c> 结果。 / Represents the result of <c>scope.delete</c>.
/// </summary>
/// <param name="DeletedRecordCount">随 scope 逻辑删除的 record 数量。 / Number of records logically deleted with the scope.</param>
public sealed record ScopeDeleteResult(int DeletedRecordCount);
