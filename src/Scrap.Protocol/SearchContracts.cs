namespace Scrap.Protocol;

/// <summary>
/// 指定 key 搜索模式。 / Specifies the key-search mode.
/// </summary>
public enum SearchMode
{
    /// <summary>精确匹配。 / Exact matching.</summary>
    Exact,

    /// <summary>模糊候选生成与稳定排序。 / Fuzzy candidate generation and stable ranking.</summary>
    Fuzzy,

    /// <summary>带超时的 .NET 正则匹配。 / Timeout-bounded .NET regular-expression matching.</summary>
    Regex,
}

/// <summary>
/// 指定 key 搜索的大小写修饰符。 / Specifies the case-sensitivity modifier for key search.
/// </summary>
public enum CaseSensitivity
{
    /// <summary>使用稳定、culture-invariant 的不敏感匹配。 / Uses stable, culture-invariant insensitive matching.</summary>
    Insensitive,

    /// <summary>使用 ordinal 大小写敏感匹配。 / Uses ordinal case-sensitive matching.</summary>
    Sensitive,
}

/// <summary>
/// 表示 <c>record.search</c> 的完整请求。搜索只针对 key，绝不针对 value。
/// / Represents the complete <c>record.search</c> request. Search targets keys only, never values.
/// </summary>
/// <param name="Scope">目标精确 scope 名称。 / Exact target scope name.</param>
/// <param name="Query">查询文本或 regex pattern。 / Query text or regular-expression pattern.</param>
/// <param name="Mode">搜索模式。 / Search mode.</param>
/// <param name="CaseSensitivity">大小写修饰符。 / Case-sensitivity modifier.</param>
/// <param name="Limit">daemon 排序后返回的最大候选数。 / Maximum candidates returned after daemon ranking.</param>
public sealed record SearchRequest(
    string Scope,
    string Query,
    SearchMode Mode = SearchMode.Fuzzy,
    CaseSensitivity CaseSensitivity = CaseSensitivity.Insensitive,
    int Limit = 100);

/// <summary>
/// 表示 <c>record.search</c> 结果。顺序即 daemon 的最终排名，不暴露伪概率分数。
/// / Represents the result of <c>record.search</c>. Order is the daemon's final ranking; no pseudo-probability score is exposed.
/// </summary>
/// <param name="Records">已排序且不含 value 的候选。 / Ranked candidates without values.</param>
public sealed record RecordSearchResult(IReadOnlyList<RecordSummaryDto> Records);
