namespace Scrap.Domain;

/// <summary>
/// 指定 record 值的展示策略；它不改变加密或值类型。<br/>
/// Specifies the presentation policy for a record value; it does not change encryption or the value type.
/// </summary>
public enum Presentation
{
    /// <summary>默认遮罩显示。 / Mask by default.</summary>
    Masked = 0,

    /// <summary>允许直接显示。 / Allow direct display.</summary>
    Plain = 1,
}

/// <summary>
/// 表示扁平的用户命名空间。实例不可变，重命名返回新实例。<br/>
/// Represents a flat user namespace. Instances are immutable; rename returns a new instance.
/// </summary>
public sealed class Scope
{
    private Scope(ScopeName name, DateTimeOffset createdAt, DateTimeOffset updatedAt)
    {
        Name = name;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    /// <summary>获取名称。 / Gets the name.</summary>
    public ScopeName Name { get; }

    /// <summary>获取创建时间。 / Gets the creation time.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>获取最近修改时间。 / Gets the most recent modification time.</summary>
    public DateTimeOffset UpdatedAt { get; }

    /// <summary>创建 scope，创建与修改时间相同。 / Creates a scope with equal creation and modification timestamps.</summary>
    /// <param name="name">原样名称。 / Name as supplied.</param>
    /// <param name="createdAt">创建时间。 / Creation timestamp.</param>
    /// <returns>scope 或结构化错误。 / The scope or a structured error.</returns>
    public static DomainResult<Scope> TryCreate(string? name, DateTimeOffset createdAt)
    {
        var parsed = ScopeName.TryCreate(name);
        return parsed.IsSuccess
            ? DomainResult.Success(new Scope(parsed.Value, createdAt, createdAt))
            : DomainResult.Failure<Scope>(parsed.Error!);
    }

    /// <summary>
    /// 从持久化状态重建 scope，同时执行与新建相同的名称校验。<br/>
    /// Reconstitutes a scope from persisted state while applying the same name validation as creation.
    /// </summary>
    /// <param name="name">持久化名称。 / Persisted name.</param>
    /// <param name="createdAt">原创建时间。 / Original creation timestamp.</param>
    /// <param name="updatedAt">原修改时间。 / Original modification timestamp.</param>
    /// <returns>scope 或结构化错误。 / The scope or a structured error.</returns>
    public static DomainResult<Scope> TryRestore(
        string? name,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        var parsed = ScopeName.TryCreate(name);
        return parsed.IsSuccess
            ? DomainResult.Success(new Scope(parsed.Value, createdAt, updatedAt))
            : DomainResult.Failure<Scope>(parsed.Error!);
    }

    /// <summary>重命名 scope，不修改原实例。 / Renames the scope without modifying the original instance.</summary>
    /// <param name="name">新名称。 / New name.</param>
    /// <param name="updatedAt">修改时间。 / Modification timestamp.</param>
    /// <returns>新 scope 或结构化错误。 / A new scope or a structured error.</returns>
    public DomainResult<Scope> TryRename(string? name, DateTimeOffset updatedAt)
    {
        var parsed = ScopeName.TryCreate(name);
        return parsed.IsSuccess
            ? DomainResult.Success(new Scope(parsed.Value, CreatedAt, updatedAt))
            : DomainResult.Failure<Scope>(parsed.Error!);
    }
}

/// <summary>
/// 表示最小读写单位；其身份为大小写敏感的 <c>(scope, key)</c>。实例不可变。<br/>
/// Represents the smallest read/write unit; its identity is the case-sensitive <c>(scope, key)</c>. Instances are immutable.
/// </summary>
public sealed class Record
{
    private Record(
        ScopeName scope,
        RecordKey key,
        RecordValues values,
        Presentation presentation,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        Scope = scope;
        Key = key;
        Values = values;
        Presentation = presentation;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    /// <summary>获取所属 scope。 / Gets the owning scope.</summary>
    public ScopeName Scope { get; }

    /// <summary>获取 scope 内的键。 / Gets the key within the scope.</summary>
    public RecordKey Key { get; }

    /// <summary>获取首个秘密值，供旧调用方兼容使用。 / Gets the first secret value for compatibility with legacy callers.</summary>
    public RecordValue Value => Values[0];

    /// <summary>获取非空、有序秘密值列表。 / Gets the non-empty ordered list of secret values.</summary>
    public RecordValues Values { get; }

    /// <summary>获取展示策略。 / Gets the presentation policy.</summary>
    public Presentation Presentation { get; }

    /// <summary>获取创建时间。 / Gets the creation time.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>获取最近修改时间。 / Gets the most recent modification time.</summary>
    public DateTimeOffset UpdatedAt { get; }

    /// <summary>从原始文本创建 record。 / Creates a record from raw text.</summary>
    /// <param name="scope">scope 名称。 / Scope name.</param>
    /// <param name="key">record 键。 / Record key.</param>
    /// <param name="value">record 值。 / Record value.</param>
    /// <param name="presentation">展示策略。 / Presentation policy.</param>
    /// <param name="createdAt">创建时间。 / Creation timestamp.</param>
    /// <returns>record 或首个结构化校验错误。 / The record or the first structured validation error.</returns>
    public static DomainResult<Record> TryCreate(
        string? scope,
        string? key,
        string? value,
        Presentation presentation,
        DateTimeOffset createdAt)
    {
        var scopeResult = ScopeName.TryCreate(scope);
        if (scopeResult.IsFailure)
        {
            return DomainResult.Failure<Record>(scopeResult.Error!);
        }

        var keyResult = RecordKey.TryCreate(key);
        if (keyResult.IsFailure)
        {
            return DomainResult.Failure<Record>(keyResult.Error!);
        }

        return TryCreate(scope, key, value is null ? null : [value], presentation, createdAt);
    }

    /// <summary>从有序值列表创建 record。 / Creates a record from an ordered value list.</summary>
    public static DomainResult<Record> TryCreate(
        string? scope,
        string? key,
        IReadOnlyList<string>? values,
        Presentation presentation,
        DateTimeOffset createdAt)
    {
        var scopeResult = ScopeName.TryCreate(scope);
        if (scopeResult.IsFailure) return DomainResult.Failure<Record>(scopeResult.Error!);
        var keyResult = RecordKey.TryCreate(key);
        if (keyResult.IsFailure) return DomainResult.Failure<Record>(keyResult.Error!);
        var valuesResult = RecordValues.TryCreate(values);
        if (valuesResult.IsFailure) return DomainResult.Failure<Record>(valuesResult.Error!);

        var presentationError = ValidatePresentation(presentation);
        return presentationError is null
            ? DomainResult.Success(new Record(
                scopeResult.Value,
                keyResult.Value,
                valuesResult.Value,
                presentation,
                createdAt,
                createdAt))
            : DomainResult.Failure<Record>(presentationError);
    }

    /// <summary>
    /// 从持久化状态重建 record，同时执行与新建相同的全部校验。<br/>
    /// Reconstitutes a record from persisted state while applying all creation validations.
    /// </summary>
    /// <param name="scope">持久化 scope 名称。 / Persisted scope name.</param>
    /// <param name="key">持久化 key。 / Persisted key.</param>
    /// <param name="value">解密后的完整值。 / Complete decrypted value.</param>
    /// <param name="presentation">持久化展示策略。 / Persisted presentation policy.</param>
    /// <param name="createdAt">原创建时间。 / Original creation timestamp.</param>
    /// <param name="updatedAt">原修改时间。 / Original modification timestamp.</param>
    /// <returns>record 或首个结构化错误。 / The record or the first structured error.</returns>
    public static DomainResult<Record> TryRestore(
        string? scope,
        string? key,
        string? value,
        Presentation presentation,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        var created = TryCreate(scope, key, value, presentation, createdAt);
        return created.IsSuccess
            ? DomainResult.Success(new Record(
                created.Value.Scope,
                created.Value.Key,
                created.Value.Values,
                created.Value.Presentation,
                createdAt,
                updatedAt))
            : DomainResult.Failure<Record>(created.Error!);
    }

    /// <summary>从持久化的有序列表重建 record。 / Restores a record from a persisted ordered list.</summary>
    public static DomainResult<Record> TryRestore(
        string? scope, string? key, IReadOnlyList<string>? values, Presentation presentation,
        DateTimeOffset createdAt, DateTimeOffset updatedAt)
    {
        var created = TryCreate(scope, key, values, presentation, createdAt);
        return created.IsSuccess
            ? DomainResult.Success(new Record(created.Value.Scope, created.Value.Key, created.Value.Values,
                created.Value.Presentation, createdAt, updatedAt))
            : DomainResult.Failure<Record>(created.Error!);
    }

    /// <summary>整值替换并更新展示策略，不修改原实例。 / Replaces the whole value and presentation policy without modifying the original instance.</summary>
    /// <param name="value">新值。 / New value.</param>
    /// <param name="presentation">新展示策略。 / New presentation policy.</param>
    /// <param name="updatedAt">修改时间。 / Modification timestamp.</param>
    /// <returns>新 record 或结构化错误。 / A new record or a structured error.</returns>
    public DomainResult<Record> TryReplace(
        string? value,
        Presentation presentation,
        DateTimeOffset updatedAt)
    {
        return TryReplace(value is null ? null : [value], presentation, updatedAt);
    }

    /// <summary>原子替换完整有序值列表。 / Atomically replaces the complete ordered value list.</summary>
    public DomainResult<Record> TryReplace(
        IReadOnlyList<string>? values,
        Presentation presentation,
        DateTimeOffset updatedAt)
    {
        var valuesResult = RecordValues.TryCreate(values);
        if (valuesResult.IsFailure) return DomainResult.Failure<Record>(valuesResult.Error!);

        var presentationError = ValidatePresentation(presentation);
        return presentationError is null
            ? DomainResult.Success(new Record(
                Scope,
                Key,
                valuesResult.Value,
                presentation,
                CreatedAt,
                updatedAt))
            : DomainResult.Failure<Record>(presentationError);
    }

    /// <summary>
    /// 原子地更换 record 身份的 scope、key 或两者，不修改值或原实例。该统一操作覆盖 key rename 与 scope rename 重建。<br/>
    /// Atomically changes the scope, key, or both parts of record identity without changing the value or original instance. This single operation covers key rename and scope-rename reconstitution.
    /// </summary>
    /// <param name="scope">新 scope 名称。 / New scope name.</param>
    /// <param name="key">新 key。 / New key.</param>
    /// <param name="updatedAt">修改时间。 / Modification timestamp.</param>
    /// <returns>新 record 或首个结构化错误。 / A new record or the first structured error.</returns>
    public DomainResult<Record> TryReidentify(
        string? scope,
        string? key,
        DateTimeOffset updatedAt)
    {
        var scopeResult = ScopeName.TryCreate(scope);
        if (scopeResult.IsFailure)
        {
            return DomainResult.Failure<Record>(scopeResult.Error!);
        }

        var keyResult = RecordKey.TryCreate(key);
        return keyResult.IsSuccess
            ? DomainResult.Success(new Record(
                scopeResult.Value,
                keyResult.Value,
                Values,
                Presentation,
                CreatedAt,
                updatedAt))
            : DomainResult.Failure<Record>(keyResult.Error!);
    }

    private static DomainError? ValidatePresentation(Presentation presentation) =>
        Enum.IsDefined(presentation)
            ? null
            : new DomainError(
                DomainErrorCode.InvalidOption,
                "presentation must be masked or plain.",
                "presentation");
}
