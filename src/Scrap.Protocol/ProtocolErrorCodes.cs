namespace Scrap.Protocol;

/// <summary>
/// 定义版本化协议使用的结构化错误码。错误码适合机器判断；消息只用于诊断。
/// / Defines structured error codes for the versioned protocol. Codes are for machines; messages are diagnostic only.
/// </summary>
public static class ProtocolErrorCodes
{
    /// <summary>请求 envelope 非法。 / The request envelope is invalid.</summary>
    public const string InvalidRequest = "invalid_request";

    /// <summary>方法参数非法。 / Method parameters are invalid.</summary>
    public const string InvalidParams = "invalid_params";

    /// <summary>方法不存在。 / The method is not recognized.</summary>
    public const string MethodNotFound = "method_not_found";

    /// <summary>协议版本不兼容。 / The protocol version is incompatible.</summary>
    public const string ProtocolVersionUnsupported = "protocol_version_unsupported";

    /// <summary>frame 超过允许上限。 / The frame exceeds the configured limit.</summary>
    public const string FrameTooLarge = "frame_too_large";

    /// <summary>payload 不是合法 UTF-8。 / The payload is not valid UTF-8.</summary>
    public const string InvalidUtf8 = "invalid_utf8";

    /// <summary>payload 不是合法 JSON。 / The payload is not valid JSON.</summary>
    public const string InvalidJson = "invalid_json";

    /// <summary>scope 不存在。 / The scope does not exist.</summary>
    public const string ScopeNotFound = "scope_not_found";

    /// <summary>scope 名称冲突。 / A scope with the same name already exists.</summary>
    public const string ScopeAlreadyExists = "scope_already_exists";

    /// <summary>非递归删除要求 scope 为空。 / Non-recursive deletion requires an empty scope.</summary>
    public const string ScopeNotEmpty = "scope_not_empty";

    /// <summary>record 不存在。 / The record does not exist.</summary>
    public const string RecordNotFound = "record_not_found";

    /// <summary>record 名称冲突。 / A record with the same identity already exists.</summary>
    public const string RecordAlreadyExists = "record_already_exists";

    /// <summary>搜索表达式非法。 / The search expression is invalid.</summary>
    public const string QueryInvalid = "query_invalid";

    /// <summary>搜索执行超时。 / Search evaluation timed out.</summary>
    public const string QueryTimeout = "query_timeout";

    /// <summary>条件写入或并发前置条件冲突。 / A conditional write or concurrency precondition conflicted.</summary>
    public const string Conflict = "conflict";

    /// <summary>持久化存储不可用。 / Persistent storage is unavailable.</summary>
    public const string StoreUnavailable = "store_unavailable";

    /// <summary>主密钥或平台密钥提供器不可用。 / The master key or platform key provider is unavailable.</summary>
    public const string KeyUnavailable = "key_unavailable";

    /// <summary>加解密操作失败。 / A cryptographic operation failed.</summary>
    public const string CryptoError = "crypto_error";

    /// <summary>daemon 正在关闭，无法接收请求。 / The daemon is shutting down and cannot accept the request.</summary>
    public const string DaemonShuttingDown = "daemon_shutting_down";

    /// <summary>未分类的 daemon 内部错误。 / An unclassified daemon internal error occurred.</summary>
    public const string InternalError = "internal_error";
}
