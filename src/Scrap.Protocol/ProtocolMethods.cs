namespace Scrap.Protocol;

/// <summary>
/// 定义 v1 RPC 方法名。调用方必须使用这些常量，避免协议字符串漂移。
/// / Defines v1 RPC method names. Callers should use these constants to prevent wire-name drift.
/// </summary>
public static class ProtocolMethods
{
    /// <summary>列出 scope。 / Lists scopes.</summary>
    public const string ScopeList = "scope.list";

    /// <summary>创建 scope。 / Creates a scope.</summary>
    public const string ScopeCreate = "scope.create";

    /// <summary>重命名 scope。 / Renames a scope.</summary>
    public const string ScopeRename = "scope.rename";

    /// <summary>删除 scope。 / Deletes a scope.</summary>
    public const string ScopeDelete = "scope.delete";

    /// <summary>读取 record。 / Gets a record.</summary>
    public const string RecordGet = "record.get";

    /// <summary>新增或替换 record。 / Creates or replaces a record.</summary>
    public const string RecordSet = "record.set";

    /// <summary>原子重命名 record。 / Atomically renames a record.</summary>
    public const string RecordRename = "record.rename";

    /// <summary>删除 record。 / Deletes a record.</summary>
    public const string RecordDelete = "record.delete";

    /// <summary>列出 scope 内的 record 元数据。 / Lists record metadata in a scope.</summary>
    public const string RecordList = "record.list";

    /// <summary>搜索 scope 内的 record key。 / Searches record keys in a scope.</summary>
    public const string RecordSearch = "record.search";

    /// <summary>探测 daemon 活性。 / Probes daemon liveness.</summary>
    public const string DaemonPing = "daemon.ping";

    /// <summary>协商协议与应用版本。 / Negotiates protocol and application versions.</summary>
    public const string DaemonVersion = "daemon.version";

    /// <summary>请求 daemon 优雅关闭。 / Requests graceful daemon shutdown.</summary>
    public const string DaemonShutdown = "daemon.shutdown";
}
