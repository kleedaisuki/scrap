using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Scrap.Protocol;

/// <summary>
/// 提供协议唯一的 JSON 序列化配置。配置接受新增字段以维持向后兼容，但拒绝注释、尾随逗号、重复属性和整数枚举。
/// / Provides the protocol's canonical JSON configuration. It accepts added fields for backward compatibility, but rejects comments, trailing commas, duplicate properties, and integer enums.
/// </summary>
public static class ProtocolJson
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateOptions();

    /// <summary>
    /// 获取只读的规范序列化配置。 / Gets the read-only canonical serializer options.
    /// </summary>
    public static JsonSerializerOptions Options => SerializerOptions;

    /// <summary>
    /// 将 DTO 序列化为不含 BOM 的 UTF-8 JSON。
    /// / Serializes a DTO to UTF-8 JSON without a byte-order mark.
    /// </summary>
    /// <typeparam name="T">DTO 类型。 / DTO type.</typeparam>
    /// <param name="value">待序列化值。 / Value to serialize.</param>
    /// <returns>UTF-8 JSON 字节。 / UTF-8 JSON bytes.</returns>
    public static byte[] Serialize<T>(T value) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(value);
        return JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions);
    }

    /// <summary>
    /// 从已验证 UTF-8 的 JSON 字节反序列化 DTO。
    /// / Deserializes a DTO from JSON bytes whose UTF-8 has already been validated.
    /// </summary>
    /// <typeparam name="T">DTO 类型。 / DTO type.</typeparam>
    /// <param name="utf8Json">JSON 字节。 / JSON bytes.</param>
    /// <returns>非空 DTO。 / Non-null DTO.</returns>
    /// <exception cref="JsonException">JSON 语法或 DTO contract 非法。 / JSON syntax or the DTO contract is invalid.</exception>
    public static T Deserialize<T>(ReadOnlySpan<byte> utf8Json) where T : notnull
    {
        T? value = JsonSerializer.Deserialize<T>(utf8Json, SerializerOptions);
        return value ?? throw new JsonException("JSON payload cannot be null.");
    }

    /// <summary>
    /// 将强类型值转换为具有独立所有权的 JSON 元素。
    /// / Converts a typed value into an independently owned JSON element.
    /// </summary>
    /// <typeparam name="T">DTO 类型。 / DTO type.</typeparam>
    /// <param name="value">待转换值。 / Value to convert.</param>
    /// <returns>独立 JSON 元素。 / Independently owned JSON element.</returns>
    public static JsonElement ToElement<T>(T value) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(value);
        return JsonSerializer.SerializeToElement(value, SerializerOptions);
    }

    /// <summary>
    /// 从 envelope 中的 JSON 元素读取强类型 DTO。
    /// / Reads a typed DTO from an envelope JSON element.
    /// </summary>
    /// <typeparam name="T">DTO 类型。 / DTO type.</typeparam>
    /// <param name="element">JSON 元素。 / JSON element.</param>
    /// <returns>非空 DTO。 / Non-null DTO.</returns>
    /// <exception cref="ProtocolException">元素无法反序列化为目标 DTO。 / The element cannot be deserialized as the target DTO.</exception>
    public static T DeserializeElement<T>(JsonElement element) where T : notnull
    {
        try
        {
            T? value = element.Deserialize<T>(SerializerOptions);
            return value ?? throw new JsonException("JSON value cannot be null.");
        }
        catch (JsonException exception)
        {
            throw new ProtocolException(
                ProtocolErrorCodes.InvalidJson,
                "JSON does not match the expected protocol contract.",
                exception);
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            AllowDuplicateProperties = false,
            AllowTrailingCommas = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
            WriteIndented = false,
        };

        options.Converters.Add(new ProtocolStringConverter());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.MakeReadOnly();
        return options;
    }
}
