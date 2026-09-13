namespace Scrap.Domain;

/// <summary>
/// 标识稳定的领域错误类别。<br/>
/// Identifies stable categories of domain errors.
/// </summary>
public enum DomainErrorCode
{
    /// <summary>缺少必填文本。 / Required text is missing.</summary>
    Required,

    /// <summary>文本不是有效的 UTF-16，因而不能无损编码为 UTF-8。 / Text is not valid UTF-16 and cannot be encoded losslessly as UTF-8.</summary>
    InvalidUnicode,

    /// <summary>文本的 UTF-8 表示超过固定上限。 / The UTF-8 representation exceeds its fixed limit.</summary>
    TextTooLong,

    /// <summary>数值超出允许范围。 / A numeric value is outside its permitted range.</summary>
    OutOfRange,

    /// <summary>枚举值不是已定义的领域值。 / An enum value is not a defined domain value.</summary>
    InvalidOption,

    /// <summary>正则表达式无法编译。 / A regular expression cannot be compiled.</summary>
    InvalidRegex,

    /// <summary>正则表达式匹配超过执行期限。 / Regular-expression matching exceeded its execution deadline.</summary>
    RegexTimeout,
}

/// <summary>
/// 表示可跨应用层边界传递、且不携带秘密值的领域错误。<br/>
/// Represents a domain error that can cross application-layer boundaries without carrying secret values.
/// </summary>
/// <param name="Code">稳定的机器可读代码。 / Stable machine-readable code.</param>
/// <param name="Message">不含秘密数据的人类可读说明。 / Human-readable description that contains no secret data.</param>
/// <param name="Field">相关输入字段；不适用时为 <see langword="null"/>。 / Related input field, or <see langword="null"/> when not applicable.</param>
public sealed record DomainError(DomainErrorCode Code, string Message, string? Field = null);

/// <summary>
/// 表示由结构化 <see cref="DomainError"/> 导致的领域异常。<br/>
/// Represents a domain exception backed by a structured <see cref="DomainError"/>.
/// </summary>
public sealed class DomainException : Exception
{
    /// <summary>
    /// 使用结构化错误创建异常。<br/>
    /// Creates an exception from a structured error.
    /// </summary>
    /// <param name="error">错误详情。 / Error details.</param>
    public DomainException(DomainError error)
        : base((error ?? throw new ArgumentNullException(nameof(error))).Message)
    {
        Error = error;
    }

    /// <summary>获取结构化错误。 / Gets the structured error.</summary>
    public DomainError Error { get; }
}

/// <summary>
/// 封装领域操作的成功值或结构化失败；两种状态严格互斥。<br/>
/// Encapsulates either a successful domain value or a structured failure; the states are mutually exclusive.
/// </summary>
/// <typeparam name="T">成功值类型。 / Success value type.</typeparam>
public sealed class DomainResult<T>
{
    private readonly T? value;

    internal DomainResult(T value)
    {
        this.value = value;
        IsSuccess = true;
    }

    internal DomainResult(DomainError error)
    {
        Error = error;
        IsSuccess = false;
    }

    /// <summary>获取结果是否成功。 / Gets whether the result succeeded.</summary>
    public bool IsSuccess { get; }

    /// <summary>获取结果是否失败。 / Gets whether the result failed.</summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>获取失败详情；成功时为 <see langword="null"/>。 / Gets failure details, or <see langword="null"/> on success.</summary>
    public DomainError? Error { get; }

    /// <summary>
    /// 获取成功值；失败时抛出携带同一结构化错误的 <see cref="DomainException"/>。<br/>
    /// Gets the success value; on failure, throws <see cref="DomainException"/> carrying the same structured error.
    /// </summary>
    public T Value => IsSuccess ? value! : throw new DomainException(Error!);

}

/// <summary>创建强类型领域结果。 / Creates strongly typed domain results.</summary>
public static class DomainResult
{
    /// <summary>创建成功结果。 / Creates a successful result.</summary>
    /// <typeparam name="T">成功值类型。 / Success value type.</typeparam>
    /// <param name="value">非空成功值。 / Non-null success value.</param>
    /// <returns>成功结果。 / A successful result.</returns>
    public static DomainResult<T> Success<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new DomainResult<T>(value);
    }

    /// <summary>创建失败结果。 / Creates a failed result.</summary>
    /// <typeparam name="T">预期成功值类型。 / Expected success value type.</typeparam>
    /// <param name="error">结构化错误。 / Structured error.</param>
    /// <returns>失败结果。 / A failed result.</returns>
    public static DomainResult<T> Failure<T>(DomainError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new DomainResult<T>(error);
    }
}
