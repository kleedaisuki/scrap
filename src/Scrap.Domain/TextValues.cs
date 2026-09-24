using System.Text;

namespace Scrap.Domain;

/// <summary>
/// 统一执行不规范化文本的严格 UTF-8 边界校验；不能用默认替换式编码掩盖非法代理项。<br/>
/// Centralizes strict UTF-8 validation for unnormalized text; replacement encoding must not hide invalid surrogates.
/// </summary>
internal static class TextRules
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static DomainError? Validate(
        string? value,
        string field,
        int maximumUtf8Bytes,
        bool allowEmpty) => Validate(value, field, maximumUtf8Bytes, allowEmpty, out _);

    /// <summary>
    /// 严格校验文本并返回已验证的 UTF-8 大小，使总量限制无需再次编码秘密值。<br/>
    /// Strictly validates text and returns its verified UTF-8 size so aggregate limits need not re-encode secret values.
    /// </summary>
    internal static DomainError? Validate(
        string? value,
        string field,
        int maximumUtf8Bytes,
        bool allowEmpty,
        out int byteCount)
    {
        byteCount = 0;
        if (value is null || (!allowEmpty && value.Length == 0))
        {
            return new DomainError(
                DomainErrorCode.Required,
                $"{field} is required.",
                field);
        }

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
        => TryCreate(value, out _);

    /// <summary>
    /// 为有序值列表复用严格校验得到的字节数；失败时该输出不可使用。<br/>
    /// Reuses the strictly validated byte count for ordered value lists; the output is not meaningful on failure.
    /// </summary>
    internal static DomainResult<RecordValue> TryCreate(string? value, out int utf8Bytes)
    {
        var error = TextRules.Validate(value, "value", MaximumUtf8Bytes, allowEmpty: true, out utf8Bytes);
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

/// <summary>
/// 表示 record 的非空、有序秘密值列表；保留重复项与空字符串，并按顺序比较。<br/>
/// Represents a non-empty ordered list of secret record values; duplicates and empty strings are preserved, and equality respects order.
/// </summary>
public sealed class RecordValues : IReadOnlyList<RecordValue>, IEquatable<RecordValues>
{
    /// <summary>单个 record 允许的最大 value 数量。 / Maximum number of values in one record.</summary>
    public const int MaximumCount = 32;

    /// <summary>
    /// 所有 value 的 UTF-8 字节总上限（64 KiB）；该界限为 JSON framing 留出转义开销。<br/>
    /// Aggregate UTF-8 byte limit for all values (64 KiB), leaving room for JSON framing expansion.
    /// </summary>
    public const int MaximumAggregateUtf8Bytes = 64 * 1024;

    private readonly RecordValue[] values;

    private RecordValues(RecordValue[] values) => this.values = values;

    /// <inheritdoc />
    public int Count => values.Length;

    /// <inheritdoc />
    public RecordValue this[int index] => values[index];

    /// <summary>
    /// 校验并复制输入列表，避免调用方在创建后改变 record 内容。<br/>
    /// Validates and copies the input list so callers cannot mutate record contents after creation.
    /// </summary>
    /// <param name="values">按语义顺序排列的原始文本。 / Raw text in semantic order.</param>
    /// <returns>不可变列表或首个结构化错误。 / An immutable list or the first structured error.</returns>
    public static DomainResult<RecordValues> TryCreate(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0)
        {
            return DomainResult.Failure<RecordValues>(new DomainError(
                DomainErrorCode.Required,
                "values must contain at least one item.",
                "values"));
        }

        if (values.Count > MaximumCount)
        {
            return DomainResult.Failure<RecordValues>(new DomainError(
                DomainErrorCode.OutOfRange,
                $"values must contain at most {MaximumCount} items.",
                "values"));
        }

        var parsed = new RecordValue[values.Count];
        var aggregateBytes = 0;
        for (var index = 0; index < values.Count; index++)
        {
            DomainResult<RecordValue> item = RecordValue.TryCreate(values[index], out var utf8Bytes);
            if (item.IsFailure)
            {
                return DomainResult.Failure<RecordValues>(item.Error! with { Field = $"values[{index}]" });
            }

            parsed[index] = item.Value;
            aggregateBytes += utf8Bytes;
            if (aggregateBytes > MaximumAggregateUtf8Bytes)
            {
                return DomainResult.Failure<RecordValues>(new DomainError(
                    DomainErrorCode.TextTooLong,
                    $"values must total at most {MaximumAggregateUtf8Bytes} UTF-8 bytes.",
                    "values"));
            }
        }

        return DomainResult.Success(new RecordValues(parsed));
    }

    /// <inheritdoc />
    public IEnumerator<RecordValue> GetEnumerator() => ((IEnumerable<RecordValue>)values).GetEnumerator();

    /// <inheritdoc />
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => values.GetEnumerator();

    /// <summary>
    /// 按原始顺序比较每个值；重复项和空值也参与身份比较。<br/>
    /// Compares every value in order; duplicates and empty values also contribute to equality.
    /// </summary>
    /// <param name="other">待比较列表。 / The list to compare.</param>
    /// <returns>两个有序列表是否相等。 / Whether the ordered lists are equal.</returns>
    public bool Equals(RecordValues? other) =>
        other is not null &&
        (ReferenceEquals(this, other) ||
         (values.Length == other.values.Length && values.SequenceEqual(other.values)));

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as RecordValues);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var value in values)
        {
            hash.Add(value.Value, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    /// <summary>返回固定遮盖文本。 / Returns fixed redacted text.</summary>
    public override string ToString() => $"[REDACTED:{Count}]";
}
