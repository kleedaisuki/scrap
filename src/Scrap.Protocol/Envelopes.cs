using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scrap.Protocol;

/// <summary>
/// 表示结构化的协议错误。错误消息不得包含 record value 或完整 payload。
/// / Represents a structured protocol error. The message must never contain a record value or the full payload.
/// </summary>
/// <param name="Code">稳定的机器可读错误码。 / Stable machine-readable error code.</param>
/// <param name="Message">不含敏感值的诊断消息。 / Diagnostic message without sensitive values.</param>
public sealed record ProtocolError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

/// <summary>
/// 表示一个 v1 RPC 请求 envelope。<paramref name="Params"/> 始终是 JSON 对象，而不是 <see langword="null"/>。
/// / Represents a v1 RPC request envelope. <paramref name="Params"/> is always a JSON object, never <see langword="null"/>.
/// </summary>
/// <param name="ProtocolVersion">主协议版本。 / Major protocol version.</param>
/// <param name="RequestId">由调用方生成的不透明请求标识。 / Caller-generated opaque request identifier.</param>
/// <param name="Method">来自 <see cref="ProtocolMethods"/> 的方法名。 / Method name from <see cref="ProtocolMethods"/>.</param>
/// <param name="Params">方法参数对象。 / Method parameter object.</param>
/// <example>
/// 使用强类型参数创建 request，避免手写 wire JSON。 / Create requests from typed parameters instead of handwritten wire JSON.
/// <code>
/// ProtocolRequest request = ProtocolRequest.Create(
///     "opaque-id",
///     ProtocolMethods.RecordGet,
///     new RecordGetParams("cloudflare", "api_token"));
/// </code>
/// </example>
public sealed record ProtocolRequest(
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("params")] JsonElement Params)
{
    /// <summary>
    /// 从强类型参数创建请求，并复制 JSON 元素的所有权。
    /// / Creates a request from typed parameters and owns an independent JSON element.
    /// </summary>
    /// <typeparam name="TParams">参数 DTO 类型。 / Parameter DTO type.</typeparam>
    /// <param name="requestId">不透明请求标识。 / Opaque request identifier.</param>
    /// <param name="method">RPC 方法名。 / RPC method name.</param>
    /// <param name="parameters">强类型参数。 / Typed parameters.</param>
    /// <param name="protocolVersion">主协议版本。 / Major protocol version.</param>
    /// <returns>可序列化的请求 envelope。 / A serializable request envelope.</returns>
    public static ProtocolRequest Create<TParams>(
        string requestId,
        string method,
        TParams parameters,
        int protocolVersion = ProtocolConstants.CurrentVersion)
        where TParams : notnull
    {
        ArgumentException.ThrowIfNullOrEmpty(requestId);
        ArgumentException.ThrowIfNullOrEmpty(method);
        ArgumentNullException.ThrowIfNull(parameters);

        return new(protocolVersion, requestId, method, ProtocolJson.ToElement(parameters));
    }

    /// <summary>
    /// 校验 envelope 级不变量；业务参数由对应方法处理器校验。
    /// / Validates envelope-level invariants; the method handler validates business parameters.
    /// </summary>
    /// <exception cref="ProtocolException">envelope 非法。 / The envelope is invalid.</exception>
    public void EnsureValid()
    {
        if (ProtocolVersion <= 0 || string.IsNullOrEmpty(RequestId) || string.IsNullOrEmpty(Method))
        {
            throw new ProtocolException(ProtocolErrorCodes.InvalidRequest, "Request envelope is incomplete.");
        }

        if (Params.ValueKind != JsonValueKind.Object)
        {
            throw new ProtocolException(ProtocolErrorCodes.InvalidRequest, "Request params must be a JSON object.");
        }
    }
}

/// <summary>
/// 表示一个 v1 RPC 响应 envelope；成功时仅含 result，失败时仅含 error。
/// / Represents a v1 RPC response envelope; success has only a result and failure has only an error.
/// </summary>
/// <param name="ProtocolVersion">主协议版本。 / Major protocol version.</param>
/// <param name="RequestId">逐字回显的请求标识。 / Verbatim echoed request identifier.</param>
/// <param name="Result">成功结果对象。 / Success result object.</param>
/// <param name="Error">失败错误对象。 / Failure error object.</param>
public sealed record ProtocolResponse(
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("result"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Result = null,
    [property: JsonPropertyName("error"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ProtocolError? Error = null)
{
    /// <summary>
    /// 创建成功响应。 / Creates a successful response.
    /// </summary>
    /// <typeparam name="TResult">结果 DTO 类型。 / Result DTO type.</typeparam>
    /// <param name="requestId">要回显的请求标识。 / Request identifier to echo.</param>
    /// <param name="result">强类型结果。 / Typed result.</param>
    /// <param name="protocolVersion">主协议版本。 / Major protocol version.</param>
    /// <returns>仅设置 result 的响应。 / A response with only result set.</returns>
    public static ProtocolResponse Success<TResult>(
        string requestId,
        TResult result,
        int protocolVersion = ProtocolConstants.CurrentVersion)
        where TResult : notnull
    {
        ArgumentException.ThrowIfNullOrEmpty(requestId);
        ArgumentNullException.ThrowIfNull(result);

        return new(protocolVersion, requestId, ProtocolJson.ToElement(result), null);
    }

    /// <summary>
    /// 创建失败响应。 / Creates a failed response.
    /// </summary>
    /// <param name="requestId">要回显的请求标识。 / Request identifier to echo.</param>
    /// <param name="error">结构化错误。 / Structured error.</param>
    /// <param name="protocolVersion">主协议版本。 / Major protocol version.</param>
    /// <returns>仅设置 error 的响应。 / A response with only error set.</returns>
    public static ProtocolResponse Failure(
        string requestId,
        ProtocolError error,
        int protocolVersion = ProtocolConstants.CurrentVersion)
    {
        ArgumentException.ThrowIfNullOrEmpty(requestId);
        ArgumentNullException.ThrowIfNull(error);

        return new(protocolVersion, requestId, null, error);
    }

    /// <summary>
    /// 校验版本、request ID 以及 result/error 异或不变量。
    /// / Validates the version, request ID, and result/error exclusive-or invariant.
    /// </summary>
    /// <exception cref="ProtocolException">响应 envelope 非法。 / The response envelope is invalid.</exception>
    public void EnsureValid()
    {
        bool hasResult = Result is not null;
        bool hasError = Error is not null;

        if (ProtocolVersion <= 0 || string.IsNullOrEmpty(RequestId) || hasResult == hasError)
        {
            throw new ProtocolException(ProtocolErrorCodes.InvalidRequest, "Response envelope is invalid.");
        }

        if (hasResult && Result!.Value.ValueKind != JsonValueKind.Object)
        {
            throw new ProtocolException(ProtocolErrorCodes.InvalidRequest, "Response result must be a JSON object.");
        }
    }

    /// <summary>
    /// 读取强类型成功结果，或将远端错误转换为 <see cref="RemoteProtocolException"/>。
    /// / Reads the typed success result, or converts a remote error to <see cref="RemoteProtocolException"/>.
    /// </summary>
    /// <typeparam name="TResult">预期结果 DTO。 / Expected result DTO.</typeparam>
    /// <returns>反序列化后的结果。 / Deserialized result.</returns>
    /// <exception cref="RemoteProtocolException">远端返回结构化错误。 / The remote endpoint returned a structured error.</exception>
    /// <exception cref="ProtocolException">响应 envelope 或结果 JSON 非法。 / The response envelope or result JSON is invalid.</exception>
    public TResult GetResult<TResult>() where TResult : notnull
    {
        EnsureValid();

        if (Error is not null)
        {
            throw new RemoteProtocolException(Error);
        }

        return ProtocolJson.DeserializeElement<TResult>(Result!.Value);
    }
}

/// <summary>
/// 表示无参数 RPC 的空 JSON 对象。 / Represents the empty JSON object used by parameterless RPCs.
/// </summary>
public sealed record EmptyParameters
{
    /// <summary>获取共享实例。 / Gets the shared instance.</summary>
    [JsonIgnore]
    public static EmptyParameters Instance { get; } = new();
}

/// <summary>
/// 表示不携带额外数据的成功结果。 / Represents a successful result with no additional data.
/// </summary>
public sealed record EmptyResult
{
    /// <summary>获取共享实例。 / Gets the shared instance.</summary>
    [JsonIgnore]
    public static EmptyResult Instance { get; } = new();
}
