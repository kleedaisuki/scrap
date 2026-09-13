using System.Text.RegularExpressions;

namespace Scrap.Domain;

/// <summary>指定 key 搜索算法。 / Specifies the key-search algorithm.</summary>
public enum SearchMode
{
    /// <summary>完全相等。 / Exact equality.</summary>
    Exact,

    /// <summary>启发式候选评分。 / Heuristic candidate scoring.</summary>
    Fuzzy,

    /// <summary>.NET 正则表达式匹配。 / .NET regular-expression matching.</summary>
    Regex,
}

/// <summary>指定搜索匹配的大小写语义。 / Specifies case semantics for search matching.</summary>
public enum CaseSensitivity
{
    /// <summary>使用稳定的 ordinal ignore-case 语义。 / Uses stable ordinal ignore-case semantics.</summary>
    Insensitive,

    /// <summary>使用 ordinal 大小写敏感语义。 / Uses ordinal case-sensitive semantics.</summary>
    Sensitive,
}

/// <summary>
/// 表示在一个明确 scope 内进行的有界 key 搜索。query 原样保留。<br/>
/// Represents a bounded key search within one explicit scope. The query is preserved verbatim.
/// </summary>
public sealed class SearchRequest
{
    /// <summary>query/pattern 允许的最大 UTF-8 字节数。 / Maximum permitted UTF-8 bytes for a query or pattern.</summary>
    public const int MaximumQueryUtf8Bytes = 4 * 1024;

    /// <summary>默认返回数量。 / Default result count.</summary>
    public const int DefaultLimit = 100;

    /// <summary>最小返回数量。 / Minimum result count.</summary>
    public const int MinimumLimit = 1;

    /// <summary>最大返回数量。 / Maximum result count.</summary>
    public const int MaximumLimit = 500;

    /// <summary>每次正则匹配的固定执行期限。 / Fixed execution deadline for each regex match.</summary>
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    private SearchRequest(
        ScopeName scope,
        string query,
        SearchMode mode,
        CaseSensitivity caseSensitivity,
        int limit)
    {
        Scope = scope;
        Query = query;
        Mode = mode;
        CaseSensitivity = caseSensitivity;
        Limit = limit;
    }

    /// <summary>获取明确的目标 scope。 / Gets the explicit target scope.</summary>
    public ScopeName Scope { get; }

    /// <summary>获取原样 query 或 pattern。 / Gets the verbatim query or pattern.</summary>
    public string Query { get; }

    /// <summary>获取搜索模式。 / Gets the search mode.</summary>
    public SearchMode Mode { get; }

    /// <summary>获取大小写语义。 / Gets case semantics.</summary>
    public CaseSensitivity CaseSensitivity { get; }

    /// <summary>获取最大返回数量。 / Gets the maximum result count.</summary>
    public int Limit { get; }

    /// <summary>创建搜索请求，使用 fuzzy、不区分大小写与默认 limit。 / Creates a request using fuzzy, case-insensitive matching and the default limit.</summary>
    /// <param name="scope">目标 scope。 / Target scope.</param>
    /// <param name="query">原样 query。 / Verbatim query.</param>
    /// <returns>请求或结构化错误。 / The request or a structured error.</returns>
    public static DomainResult<SearchRequest> TryCreate(string? scope, string? query) =>
        TryCreate(scope, query, SearchMode.Fuzzy, CaseSensitivity.Insensitive, DefaultLimit);

    /// <summary>创建搜索请求并验证全部边界。 / Creates a search request and validates every boundary.</summary>
    /// <param name="scope">目标 scope。 / Target scope.</param>
    /// <param name="query">原样 query 或 pattern；允许为空。 / Verbatim query or pattern; empty is allowed.</param>
    /// <param name="mode">搜索模式。 / Search mode.</param>
    /// <param name="caseSensitivity">大小写语义。 / Case semantics.</param>
    /// <param name="limit">1 到 500 的最大返回数量。 / Maximum result count from 1 through 500.</param>
    /// <returns>请求或首个结构化校验错误。 / The request or the first structured validation error.</returns>
    public static DomainResult<SearchRequest> TryCreate(
        string? scope,
        string? query,
        SearchMode mode,
        CaseSensitivity caseSensitivity,
        int limit = DefaultLimit)
    {
        var scopeResult = ScopeName.TryCreate(scope);
        if (scopeResult.IsFailure)
        {
            return DomainResult.Failure<SearchRequest>(scopeResult.Error!);
        }

        var queryError = TextRules.Validate(query, "query", MaximumQueryUtf8Bytes, allowEmpty: true);
        if (queryError is not null)
        {
            return DomainResult.Failure<SearchRequest>(queryError);
        }

        if (!Enum.IsDefined(mode))
        {
            return InvalidOption("mode", "mode must be exact, fuzzy, or regex.");
        }

        if (!Enum.IsDefined(caseSensitivity))
        {
            return InvalidOption("caseSensitivity", "caseSensitivity must be insensitive or sensitive.");
        }

        if (limit is < MinimumLimit or > MaximumLimit)
        {
            return DomainResult.Failure<SearchRequest>(new DomainError(
                DomainErrorCode.OutOfRange,
                $"limit must be between {MinimumLimit} and {MaximumLimit}.",
                "limit"));
        }

        return DomainResult.Success(new SearchRequest(
            scopeResult.Value,
            query!,
            mode,
            caseSensitivity,
            limit));
    }

    private static DomainResult<SearchRequest> InvalidOption(string field, string message) =>
        DomainResult.Failure<SearchRequest>(new DomainError(
            DomainErrorCode.InvalidOption,
            message,
            field));
}

/// <summary>
/// 表示一个匹配的 key 及其内部排序分数。分数只可排序，不是概率。<br/>
/// Represents a matched key and its internal ranking score. The score is only ordinal, never a probability.
/// </summary>
/// <param name="Key">匹配的 key。 / Matched key.</param>
/// <param name="Score">内部排序分数。 / Internal ranking score.</param>
public sealed record SearchMatch(RecordKey Key, int Score);

/// <summary>
/// 对 key 元数据执行 exact、fuzzy 或 regex 搜索；此类型不能接触 record value。<br/>
/// Executes exact, fuzzy, or regex search over key metadata; this type cannot access record values.
/// </summary>
public static class RecordSearch
{
    private const int ExactScore = 1_000_000;
    private const int PrefixScore = 900_000;
    private const int BoundaryScore = 800_000;
    private const int SubstringScore = 700_000;
    private const int SubsequenceScore = 600_000;
    private const int EditScore = 500_000;

    /// <summary>
    /// 搜索 key 并确定性排序。相同分数按 key 的 ordinal 顺序排列。<br/>
    /// Searches keys and sorts deterministically. Equal scores are ordered by ordinal key value.
    /// </summary>
    /// <param name="keys">同一 scope 的 key 元数据。 / Key metadata from one scope.</param>
    /// <param name="request">已验证请求。 / Validated request.</param>
    /// <returns>有界结果或正则查询错误。 / Bounded results or a regex query error.</returns>
    public static DomainResult<IReadOnlyList<SearchMatch>> Search(
        IEnumerable<RecordKey> keys,
        SearchRequest request)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(request);

        return request.Mode switch
        {
            SearchMode.Exact => DomainResult.Success<IReadOnlyList<SearchMatch>>(
                SearchExact(keys, request)),
            SearchMode.Fuzzy => DomainResult.Success<IReadOnlyList<SearchMatch>>(
                SearchFuzzy(keys, request)),
            SearchMode.Regex => SearchRegex(keys, request),
            _ => DomainResult.Failure<IReadOnlyList<SearchMatch>>(new DomainError(
                DomainErrorCode.InvalidOption,
                "mode must be exact, fuzzy, or regex.",
                "mode")),
        };
    }

    private static SearchMatch[] SearchExact(
        IEnumerable<RecordKey> keys,
        SearchRequest request)
    {
        var comparison = ToComparison(request.CaseSensitivity);
        return keys
            .Where(key => string.Equals(key.Value, request.Query, comparison))
            .OrderBy(key => key.Value, StringComparer.Ordinal)
            .Take(request.Limit)
            .Select(key => new SearchMatch(key, ExactScore))
            .ToArray();
    }

    private static SearchMatch[] SearchFuzzy(
        IEnumerable<RecordKey> keys,
        SearchRequest request) => keys
        .Select(key => new SearchMatch(key, ScoreFuzzy(key.Value, request)))
        .OrderByDescending(match => match.Score)
        .ThenBy(match => match.Key.Value, StringComparer.Ordinal)
        .Take(request.Limit)
        .ToArray();

    private static DomainResult<IReadOnlyList<SearchMatch>> SearchRegex(
        IEnumerable<RecordKey> keys,
        SearchRequest request)
    {
        Regex regex;
        try
        {
            regex = CreateRegex(request, preferNonBacktracking: true);
        }
        catch (NotSupportedException)
        {
            try
            {
                regex = CreateRegex(request, preferNonBacktracking: false);
            }
            catch (ArgumentException)
            {
                return RegexFailure(DomainErrorCode.InvalidRegex, "regex pattern is invalid.");
            }
        }
        catch (ArgumentException)
        {
            return RegexFailure(DomainErrorCode.InvalidRegex, "regex pattern is invalid.");
        }

        try
        {
            IReadOnlyList<SearchMatch> matches = keys
                .Where(key => regex.IsMatch(key.Value))
                .OrderBy(key => key.Value, StringComparer.Ordinal)
                .Take(request.Limit)
                .Select(key => new SearchMatch(key, ExactScore))
                .ToArray();
            return DomainResult.Success<IReadOnlyList<SearchMatch>>(matches);
        }
        catch (RegexMatchTimeoutException)
        {
            return RegexFailure(DomainErrorCode.RegexTimeout, "regex matching exceeded 100 ms.");
        }
    }

    private static Regex CreateRegex(SearchRequest request, bool preferNonBacktracking)
    {
        var options = RegexOptions.CultureInvariant;
        if (request.CaseSensitivity == CaseSensitivity.Insensitive)
        {
            options |= RegexOptions.IgnoreCase;
        }

        if (preferNonBacktracking)
        {
            options |= RegexOptions.NonBacktracking;
        }

        return new Regex(request.Query, options, SearchRequest.RegexTimeout);
    }

    private static DomainResult<IReadOnlyList<SearchMatch>> RegexFailure(
        DomainErrorCode code,
        string message) => DomainResult.Failure<IReadOnlyList<SearchMatch>>(
            new DomainError(code, message, "query"));

    private static int ScoreFuzzy(string candidate, SearchRequest request)
    {
        if (request.Query.Length == 0)
        {
            return 0;
        }

        var comparison = ToComparison(request.CaseSensitivity);
        if (string.Equals(candidate, request.Query, comparison))
        {
            return ExactScore;
        }

        var lengthPenalty = Math.Abs(candidate.Length - request.Query.Length);
        if (candidate.StartsWith(request.Query, comparison))
        {
            return PrefixScore - lengthPenalty;
        }

        var boundaryIndex = FindBoundaryMatch(candidate, request.Query, comparison);
        if (boundaryIndex >= 0)
        {
            return BoundaryScore - boundaryIndex - lengthPenalty;
        }

        var substringIndex = candidate.IndexOf(request.Query, comparison);
        if (substringIndex >= 0)
        {
            return SubstringScore - substringIndex - lengthPenalty;
        }

        var subsequence = ScoreSubsequence(candidate, request.Query, comparison);
        if (subsequence >= 0)
        {
            return SubsequenceScore - subsequence - lengthPenalty;
        }

        var distance = EditDistance(candidate, request.Query, request.CaseSensitivity);
        return Math.Max(0, EditScore - (distance * 1_000) - lengthPenalty);
    }

    private static int FindBoundaryMatch(
        string candidate,
        string query,
        StringComparison comparison)
    {
        if (query.Length == 0)
        {
            return -1;
        }

        var index = candidate.IndexOf(query, comparison);
        while (index >= 0)
        {
            if (index == 0 || !char.IsLetterOrDigit(candidate[index - 1]))
            {
                return index;
            }

            var nextStart = index + 1;
            index = nextStart < candidate.Length
                ? candidate.IndexOf(query, nextStart, comparison)
                : -1;
        }

        return -1;
    }

    private static int ScoreSubsequence(
        string candidate,
        string query,
        StringComparison comparison)
    {
        if (query.Length == 0)
        {
            return 0;
        }

        var candidateIndex = 0;
        var gapCost = 0;
        foreach (var queryCharacter in query)
        {
            var found = candidate.IndexOf(
                queryCharacter.ToString(),
                candidateIndex,
                comparison);
            if (found < 0)
            {
                return -1;
            }

            gapCost += found - candidateIndex;
            candidateIndex = found + 1;
        }

        return gapCost;
    }

    private static int EditDistance(
        string candidate,
        string query,
        CaseSensitivity caseSensitivity)
    {
        // 500 is the first distance whose score is clamped to zero. Avoid quadratic work
        // when the length difference alone proves the candidate cannot score above zero.
        // 500 是分数首次被截断为零的距离；若仅长度差就能证明结果为零，则避免二次复杂度计算。
        if (Math.Abs(candidate.Length - query.Length) >= EditScore / 1_000)
        {
            return EditScore / 1_000;
        }

        var previous = new int[query.Length + 1];
        var current = new int[query.Length + 1];
        for (var column = 0; column <= query.Length; column++)
        {
            previous[column] = column;
        }

        for (var row = 1; row <= candidate.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= query.Length; column++)
            {
                var substitution = CharactersEqual(
                    candidate[row - 1],
                    query[column - 1],
                    caseSensitivity) ? 0 : 1;
                current[column] = Math.Min(
                    Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[query.Length];
    }

    private static bool CharactersEqual(
        char left,
        char right,
        CaseSensitivity caseSensitivity) => string.Equals(
            left.ToString(),
            right.ToString(),
            ToComparison(caseSensitivity));

    private static StringComparison ToComparison(CaseSensitivity caseSensitivity) =>
        caseSensitivity == CaseSensitivity.Sensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
}
