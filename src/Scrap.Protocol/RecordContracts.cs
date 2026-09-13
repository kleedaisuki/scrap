namespace Scrap.Protocol;

/// <summary>
/// 指定 record value 的展示策略，而非 value 类型。 / Specifies record value presentation policy, not a value type.
/// </summary>
public enum RecordPresentation
{
    /// <summary>默认遮罩展示。 / Mask by default.</summary>
    Masked,

    /// <summary>允许直接展示。 / Allow direct display.</summary>
    Plain,
}

/// <summary>
/// 表示不含敏感 value 的 record 元数据。
/// / Represents record metadata without the sensitive value.
/// </summary>
/// <param name="Scope">所属 scope 的精确名称。 / Exact owning scope name.</param>
/// <param name="Key">scope 内大小写敏感的 key。 / Case-sensitive key within the scope.</param>
/// <param name="Presentation">展示策略。 / Presentation policy.</param>
/// <param name="CreatedAt">创建时间。 / Creation timestamp.</param>
/// <param name="UpdatedAt">最近修改时间。 / Last modification timestamp.</param>
/// <param name="Revision">用于条件 mutation 的单调修订号。 / Monotonic revision used for conditional mutations.</param>
public sealed record RecordSummaryDto(
    string Scope,
    string Key,
    RecordPresentation Presentation,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long Revision);

/// <summary>
/// 表示完整 record；只有必要的 get 响应才应携带 value。
/// / Represents a complete record; only necessary get responses should carry the value.
/// </summary>
/// <param name="Scope">所属 scope 的精确名称。 / Exact owning scope name.</param>
/// <param name="Key">scope 内大小写敏感的 key。 / Case-sensitive key within the scope.</param>
/// <param name="Value">UTF-8 文本 value；不得记录或搜索。 / UTF-8 text value; it must not be logged or searched.</param>
/// <param name="Presentation">展示策略。 / Presentation policy.</param>
/// <param name="CreatedAt">创建时间。 / Creation timestamp.</param>
/// <param name="UpdatedAt">最近修改时间。 / Last modification timestamp.</param>
/// <param name="Revision">用于条件 mutation 的单调修订号。 / Monotonic revision used for conditional mutations.</param>
public sealed record RecordDto(
    string Scope,
    string Key,
    string Value,
    RecordPresentation Presentation,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long Revision);

/// <summary>
/// 表示 <c>record.get</c> 参数。 / Represents <c>record.get</c> parameters.
/// </summary>
/// <param name="Scope">精确 scope 名称。 / Exact scope name.</param>
/// <param name="Key">精确、大小写敏感的 key。 / Exact, case-sensitive key.</param>
public sealed record RecordGetParams(string Scope, string Key);

/// <summary>
/// 表示 <c>record.get</c> 结果。 / Represents the result of <c>record.get</c>.
/// </summary>
/// <param name="Record">包含 value 的完整 record。 / Complete record including the value.</param>
public sealed record RecordGetResult(RecordDto Record);

/// <summary>
/// 表示 upsert 语义的 <c>record.set</c> 参数。
/// / Represents parameters for the upsert-style <c>record.set</c> method.
/// </summary>
/// <param name="Scope">精确 scope 名称。 / Exact scope name.</param>
/// <param name="Key">精确、大小写敏感的 key。 / Exact, case-sensitive key.</param>
/// <param name="Value">整值替换的 UTF-8 文本。 / UTF-8 text used for whole-value replacement.</param>
/// <param name="Presentation">展示策略。 / Presentation policy.</param>
/// <param name="ExpectedRevision">可选乐观并发修订号；不匹配时 mutation 原子失败。 / Optional optimistic-concurrency revision; a mismatch atomically rejects the mutation.</param>
public sealed record RecordSetParams(
    string Scope,
    string Key,
    string Value,
    RecordPresentation Presentation = RecordPresentation.Masked,
    long? ExpectedRevision = null);

/// <summary>
/// 表示 <c>record.set</c> 结果；为避免不必要的 secret 副本，结果不回显 value。
/// / Represents the result of <c>record.set</c>; it does not echo the value, avoiding an unnecessary secret copy.
/// </summary>
/// <param name="Record">写入后的元数据。 / Metadata after the write.</param>
/// <param name="Created">若本次 upsert 创建新 record 则为 true。 / True when this upsert created a new record.</param>
public sealed record RecordSetResult(RecordSummaryDto Record, bool Created);

/// <summary>
/// 表示原子的 <c>record.rename</c> 扩展参数。不得用 client 端 set+delete 模拟。
/// / Represents parameters for the atomic <c>record.rename</c> extension. Clients must not emulate it with set+delete.
/// </summary>
/// <param name="Scope">精确 scope 名称。 / Exact scope name.</param>
/// <param name="Key">当前精确 key。 / Current exact key.</param>
/// <param name="NewKey">目标精确 key。 / Target exact key.</param>
/// <param name="ExpectedRevision">可选乐观并发修订号。 / Optional optimistic-concurrency revision.</param>
public sealed record RecordRenameParams(
    string Scope,
    string Key,
    string NewKey,
    long? ExpectedRevision = null);

/// <summary>
/// 表示 <c>record.rename</c> 结果。 / Represents the result of <c>record.rename</c>.
/// </summary>
/// <param name="Record">重命名并重新加密后的元数据。 / Metadata after rename and re-encryption.</param>
public sealed record RecordRenameResult(RecordSummaryDto Record);

/// <summary>
/// 表示 <c>record.delete</c> 参数。 / Represents <c>record.delete</c> parameters.
/// </summary>
/// <param name="Scope">精确 scope 名称。 / Exact scope name.</param>
/// <param name="Key">精确、大小写敏感的 key。 / Exact, case-sensitive key.</param>
/// <param name="ExpectedRevision">可选乐观并发修订号。 / Optional optimistic-concurrency revision.</param>
public sealed record RecordDeleteParams(
    string Scope,
    string Key,
    long? ExpectedRevision = null);

/// <summary>
/// 表示 <c>record.delete</c> 的空成功结果。 / Represents the empty success result of <c>record.delete</c>.
/// </summary>
public sealed record RecordDeleteResult;

/// <summary>
/// 表示 <c>record.list</c> 参数。 / Represents <c>record.list</c> parameters.
/// </summary>
/// <param name="Scope">要列出的精确 scope 名称。 / Exact scope name to list.</param>
/// <param name="AfterKey">上一页最后一个 key；下一页从其 ordinal 后继项开始。 / Last key of the previous page; the next page starts at its ordinal successor.</param>
/// <param name="Limit">本页最多返回的 record 数量。 / Maximum records to return in this page.</param>
/// <remarks>
/// daemon 必须按 key 的 ordinal 顺序执行 keyset pagination；不得把 <paramref name="AfterKey"/> 当作数值 offset。
/// / The daemon must perform keyset pagination in ordinal key order; <paramref name="AfterKey"/> is not a numeric offset.
/// </remarks>
public sealed record RecordListParams(
    string Scope,
    string? AfterKey = null,
    int Limit = ProtocolConstants.DefaultRecordListPageSize);

/// <summary>
/// 表示 <c>record.list</c> 结果，始终不含 value。
/// / Represents the result of <c>record.list</c>, which never contains values.
/// </summary>
/// <param name="Records">record 元数据。 / Record metadata.</param>
/// <param name="NextCursor">存在下一页时应作为后续 <see cref="RecordListParams.AfterKey"/> 传回的 key；末页为 null。 / Key to pass back as <see cref="RecordListParams.AfterKey"/> when another page exists; null on the final page.</param>
public sealed record RecordListResult(
    IReadOnlyList<RecordSummaryDto> Records,
    string? NextCursor = null);
