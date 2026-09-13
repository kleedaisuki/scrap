using System.Text;

namespace Scrap.Domain;

internal static class TextRules
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static DomainError? Validate(
        string? value,
        string field,
        int maximumUtf8Bytes,
        bool allowEmpty)
    {
        if (value is null || (!allowEmpty && value.Length == 0))
        {
            return new DomainError(
                DomainErrorCode.Required,
                $"{field} is required.",
                field);
        }

        int byteCount;
        try
        {
            byteCount = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException)
        {
            return new DomainError(
                DomainErrorCode.InvalidUnicode,
                $"{field} must contain valid Unicode text.",
                field);
        }

        return byteCount <= maximumUtf8Bytes
            ? null
            : new DomainError(
                DomainErrorCode.TextTooLong,
                $"{field} must be at most {maximumUtf8Bytes} UTF-8 bytes.",
                field);
    }
}

/// <summary>
/// 表示 scope 的逐码元、大小写敏感身份；输入不会被裁剪或规范化。<br/>
/// Represents the ordinal, case-sensitive identity of a scope; input is never trimmed or normalized.
/// </summary>
public sealed class ScopeName : IEquatable<ScopeName>
{
    /// <summary>名称允许的最大 UTF-8 字节数。 / Maximum permitted UTF-8 byte count.</summary>
    public const int MaximumUtf8Bytes = 256;

    private ScopeName(string value) => Value = value;

    /// <summary>获取原样保存的名称。 / Gets the name exactly as supplied.</summary>
    public string Value { get; }

    /// <summary>
    /// 创建有效名称，失败时抛出 <see cref="DomainException"/>。<br/>
    /// Creates a valid name, throwing <see cref="DomainException"/> on failure.
    /// </summary>
    /// <param name="value">不经裁剪或规范化的名称。 / Name without trimming or normalization.</param>
    /// <returns>有效名称。 / A valid name.</returns>
    public static ScopeName Create(string value) => TryCreate(value).Value;

    /// <summary>尝试创建有效名称。 / Tries to create a valid name.</summary>
    /// <param name="value">原始名称。 / Raw name.</param>
    /// <returns>名称或结构化校验错误。 / The name or a structured validation error.</returns>
    public static DomainResult<ScopeName> TryCreate(string? value)
    {
        var error = TextRules.Validate(value, "scope", MaximumUtf8Bytes, allowEmpty: false);
        return error is null
            ? DomainResult.Success(new ScopeName(value!))
            : DomainResult.Failure<ScopeName>(error);
    }

    /// <inheritdoc/>
    public bool Equals(ScopeName? other) =>
        other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as ScopeName);

    /// <inheritdoc/>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    /// <inheritdoc/>
    public override string ToString() => Value;
}

/// <summary>
/// 表示 record 在 scope 内的逐码元、大小写敏感身份；输入不会被裁剪或规范化。<br/>
/// Represents a record's ordinal, case-sensitive identity within a scope; input is never trimmed or normalized.
/// </summary>
public sealed class RecordKey : IEquatable<RecordKey>
{
    /// <summary>键允许的最大 UTF-8 字节数。 / Maximum permitted UTF-8 byte count.</summary>
    public const int MaximumUtf8Bytes = 256;

    private RecordKey(string value) => Value = value;

    /// <summary>获取原样保存的键。 / Gets the key exactly as supplied.</summary>
    public string Value { get; }

    /// <summary>创建有效键，失败时抛出 <see cref="DomainException"/>。 / Creates a valid key, throwing <see cref="DomainException"/> on failure.</summary>
    /// <param name="value">不经裁剪或规范化的键。 / Key without trimming or normalization.</param>
    /// <returns>有效键。 / A valid key.</returns>
    public static RecordKey Create(string value) => TryCreate(value).Value;

    /// <summary>尝试创建有效键。 / Tries to create a valid key.</summary>
    /// <param name="value">原始键。 / Raw key.</param>
    /// <returns>键或结构化校验错误。 / The key or a structured validation error.</returns>
    public static DomainResult<RecordKey> TryCreate(string? value)
    {
        var error = TextRules.Validate(value, "key", MaximumUtf8Bytes, allowEmpty: false);
        return error is null
            ? DomainResult.Success(new RecordKey(value!))
            : DomainResult.Failure<RecordKey>(error);
    }

    /// <inheritdoc/>
    public bool Equals(RecordKey? other) =>
        other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as RecordKey);

    /// <inheritdoc/>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    /// <inheritdoc/>
    public override string ToString() => Value;
}

/// <summary>
/// 表示必须加密持久化的 UTF-8 record 值。字符串化始终返回遮盖文本，避免意外日志泄露。<br/>
/// Represents a UTF-8 record value that must be encrypted at rest. String conversion is always redacted to prevent accidental logging.
/// </summary>
public sealed class RecordValue : IEquatable<RecordValue>
{
    /// <summary>值允许的最大 UTF-8 字节数（64 KiB）。 / Maximum permitted UTF-8 byte count (64 KiB).</summary>
    public const int MaximumUtf8Bytes = 64 * 1024;

    private RecordValue(string value) => Value = value;

    /// <summary>获取原始秘密文本；调用者不得将其记录到日志或错误中。 / Gets the original secret text; callers must not log it or include it in errors.</summary>
    public string Value { get; }

    /// <summary>创建有效值，失败时抛出 <see cref="DomainException"/>。 / Creates a valid value, throwing <see cref="DomainException"/> on failure.</summary>
    /// <param name="value">原始值；允许为空且不做裁剪。 / Raw value; empty is allowed and no trimming is applied.</param>
    /// <returns>有效值。 / A valid value.</returns>
    public static RecordValue Create(string value) => TryCreate(value).Value;

    /// <summary>尝试创建有效值。 / Tries to create a valid value.</summary>
    /// <param name="value">原始值。 / Raw value.</param>
    /// <returns>值或结构化校验错误。 / The value or a structured validation error.</returns>
    public static DomainResult<RecordValue> TryCreate(string? value)
    {
        var error = TextRules.Validate(value, "value", MaximumUtf8Bytes, allowEmpty: true);
        return error is null
            ? DomainResult.Success(new RecordValue(value!))
            : DomainResult.Failure<RecordValue>(error);
    }

    /// <inheritdoc/>
    public bool Equals(RecordValue? other) =>
        other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as RecordValue);

    /// <inheritdoc/>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    /// <summary>返回固定遮盖文本，而不是秘密值。 / Returns fixed redacted text rather than the secret value.</summary>
    /// <returns>固定遮盖文本。 / Fixed redacted text.</returns>
    public override string ToString() => "[REDACTED]";
}
