using System.Text.RegularExpressions;
using System.Text;
using System.Diagnostics;

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
/// 表示跨可选 scope 集合进行的有界 key 搜索。空集合表示全部 scope，query 原样保留。<br/>
/// Represents a bounded key search across optional scopes. An empty collection means all scopes, and the query is preserved verbatim.
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
        IReadOnlyList<ScopeName> scopes,
        string query,
        SearchMode mode,
        CaseSensitivity caseSensitivity,
        int limit)
    {
        Scopes = scopes;
        Query = query;
        Mode = mode;
        CaseSensitivity = caseSensitivity;
        Limit = limit;
    }

    /// <summary>获取按 ordinal 排序且去重的目标 scope；空集合表示全部。 / Gets ordinal-sorted, distinct target scopes; empty means all.</summary>
    public IReadOnlyList<ScopeName> Scopes { get; }

    /// <summary>获取原样 query 或 pattern。 / Gets the verbatim query or pattern.</summary>
    public string Query { get; }

    /// <summary>获取搜索模式。 / Gets the search mode.</summary>
    public SearchMode Mode { get; }

    /// <summary>获取大小写语义。 / Gets case semantics.</summary>
    public CaseSensitivity CaseSensitivity { get; }

    /// <summary>获取最大返回数量。 / Gets the maximum result count.</summary>
    public int Limit { get; }

    /// <summary>创建搜索请求，使用 fuzzy、不区分大小写与默认 limit。 / Creates a request using fuzzy, case-insensitive matching and the default limit.</summary>
    /// <param name="scopes">目标 scope；空集合表示全部。 / Target scopes; empty means all.</param>
    /// <param name="query">原样 query。 / Verbatim query.</param>
    /// <returns>请求或结构化错误。 / The request or a structured error.</returns>
    public static DomainResult<SearchRequest> TryCreate(IEnumerable<string?>? scopes, string? query) =>
        TryCreate(scopes, query, SearchMode.Fuzzy, CaseSensitivity.Insensitive, DefaultLimit);

    /// <summary>创建搜索请求并验证全部边界。 / Creates a search request and validates every boundary.</summary>
    /// <param name="scopes">目标 scope；null 非法，空集合表示全部。 / Target scopes; null is invalid and empty means all.</param>
    /// <param name="query">原样 query 或 pattern；允许为空。 / Verbatim query or pattern; empty is allowed.</param>
    /// <param name="mode">搜索模式。 / Search mode.</param>
    /// <param name="caseSensitivity">大小写语义。 / Case semantics.</param>
    /// <param name="limit">1 到 500 的最大返回数量。 / Maximum result count from 1 through 500.</param>
    /// <returns>请求或首个结构化校验错误。 / The request or the first structured validation error.</returns>
    public static DomainResult<SearchRequest> TryCreate(
        IEnumerable<string?>? scopes,
        string? query,
        SearchMode mode,
        CaseSensitivity caseSensitivity,
        int limit = DefaultLimit)
    {
        if (scopes is null)
        {
            return DomainResult.Failure<SearchRequest>(new DomainError(
                DomainErrorCode.Required,
                "scopes is required; use an empty collection to search all scopes.",
                "scopes"));
        }

        var normalizedScopes = new SortedDictionary<string, ScopeName>(StringComparer.Ordinal);
        foreach (var scope in scopes)
        {
            var scopeResult = ScopeName.TryCreate(scope);
            if (scopeResult.IsFailure)
            {
                return DomainResult.Failure<SearchRequest>(scopeResult.Error! with { Field = "scopes" });
            }

            normalizedScopes.TryAdd(scopeResult.Value.Value, scopeResult.Value);
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
            normalizedScopes.Values.ToArray(),
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
/// 表示待搜索的完整 record 身份。 / Represents the complete identity of a record to search.
/// </summary>
/// <param name="Scope">所属 scope。 / Owning scope.</param>
/// <param name="Key">record key。 / Record key.</param>
public sealed record SearchCandidate(ScopeName Scope, RecordKey Key);

/// <summary>
/// 表示匹配的 scope+key 身份及内部排序分数；分数只可排序，不是概率。<br/>
/// Represents a matched scope+key identity and its internal ranking score; the score is ordinal, never a probability.
/// </summary>
/// <param name="Candidate">匹配的完整身份。 / Complete matched identity.</param>
/// <param name="Score">内部排序分数。 / Internal ranking score.</param>
public sealed record SearchMatch(SearchCandidate Candidate, int Score);

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
    private const int MaximumEditDistance = 8;

    /// <summary>
    /// 搜索 key 并确定性排序。相同分数按 scope、key 的 ordinal 顺序排列。<br/>
    /// Searches keys and sorts deterministically. Equal scores are ordered by ordinal scope and key.
    /// </summary>
    /// <param name="candidates">带完整 scope+key 身份的候选。 / Candidates with complete scope+key identities.</param>
    /// <param name="request">已验证请求。 / Validated request.</param>
    /// <returns>有界结果或正则查询错误。 / Bounded results or a regex query error.</returns>
    public static DomainResult<IReadOnlyList<SearchMatch>> Search(
        IEnumerable<SearchCandidate> candidates,
        SearchRequest request) => Search(candidates, request, CancellationToken.None);

    /// <summary>
    /// 搜索 key 并确定性排序，同时允许 daemon 取消已过时或正在关闭的查询。取消会抛出 <see cref="OperationCanceledException"/>。<br/>
    /// Searches keys and sorts deterministically while allowing the daemon to cancel stale or shutting-down queries. Cancellation throws <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <param name="candidates">带完整 scope+key 身份的候选。 / Candidates with complete scope+key identities.</param>
    /// <param name="request">已验证请求。 / Validated request.</param>
    /// <param name="cancellationToken">批次与高成本评分工作的取消信号。 / Cancellation signal checked between batches and expensive scoring work.</param>
    /// <returns>有界结果或正则查询错误。 / Bounded results or a regex query error.</returns>
    public static DomainResult<IReadOnlyList<SearchMatch>> Search(
        IEnumerable<SearchCandidate> candidates,
        SearchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        IEnumerable<SearchCandidate> eligibleCandidates = candidates;
        if (request.Scopes.Count != 0)
        {
            var selectedScopes = request.Scopes
                .Select(scope => scope.Value)
                .ToHashSet(StringComparer.Ordinal);
            eligibleCandidates = candidates.Where(candidate => selectedScopes.Contains(candidate.Scope.Value));
        }

        return request.Mode switch
        {
            SearchMode.Exact => DomainResult.Success<IReadOnlyList<SearchMatch>>(
                SearchExact(eligibleCandidates, request, cancellationToken)),
            SearchMode.Fuzzy => DomainResult.Success<IReadOnlyList<SearchMatch>>(
                SearchFuzzy(eligibleCandidates, request, cancellationToken)),
            SearchMode.Regex => SearchRegex(eligibleCandidates, request, cancellationToken),
            _ => DomainResult.Failure<IReadOnlyList<SearchMatch>>(new DomainError(
                DomainErrorCode.InvalidOption,
                "mode must be exact, fuzzy, or regex.",
                "mode")),
        };
    }

    private static SearchMatch[] SearchExact(
        IEnumerable<SearchCandidate> candidates,
        SearchRequest request,
        CancellationToken cancellationToken)
    {
        var comparison = ToComparison(request.CaseSensitivity);
        var matches = new List<SearchCandidate>();
        var index = 0;
        foreach (var candidate in candidates)
        {
            if ((index++ & 63) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (string.Equals(candidate.Key.Value, request.Query, comparison))
            {
                matches.Add(candidate);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return matches
            .OrderBy(candidate => candidate.Scope.Value, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Key.Value, StringComparer.Ordinal)
            .Take(request.Limit)
            .Select(candidate => new SearchMatch(candidate, ExactScore))
            .ToArray();
    }

    private static SearchMatch[] SearchFuzzy(
        IEnumerable<SearchCandidate> candidates,
        SearchRequest request,
        CancellationToken cancellationToken)
    {
        var matches = new List<SearchMatch>();
        var index = 0;
        foreach (var candidate in candidates)
        {
            if ((index++ & 63) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            matches.Add(new SearchMatch(
                candidate,
                ScoreFuzzy(candidate.Key.Value, request, cancellationToken)));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return matches
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Candidate.Scope.Value, StringComparer.Ordinal)
            .ThenBy(match => match.Candidate.Key.Value, StringComparer.Ordinal)
            .Take(request.Limit)
            .ToArray();
    }

    private static DomainResult<IReadOnlyList<SearchMatch>> SearchRegex(
        IEnumerable<SearchCandidate> candidates,
        SearchRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Query.Length == 0)
        {
            return DomainResult.Success<IReadOnlyList<SearchMatch>>([]);
        }

        var elapsed = Stopwatch.StartNew();
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
            var matchedCandidates = new List<SearchCandidate>();
            var index = 0;
            foreach (var candidate in candidates)
            {
                if ((index++ & 15) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (elapsed.Elapsed >= SearchRequest.RegexTimeout)
                {
                    return RegexFailure(DomainErrorCode.RegexTimeout, "regex search exceeded 100 ms.");
                }

                if (regex.IsMatch(candidate.Key.Value))
                {
                    matchedCandidates.Add(candidate);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (elapsed.Elapsed >= SearchRequest.RegexTimeout)
            {
                return RegexFailure(DomainErrorCode.RegexTimeout, "regex search exceeded 100 ms.");
            }

            IReadOnlyList<SearchMatch> matches = matchedCandidates
                .OrderBy(candidate => candidate.Scope.Value, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.Key.Value, StringComparer.Ordinal)
                .Take(request.Limit)
                .Select(candidate => new SearchMatch(candidate, ExactScore))
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

    private static int ScoreFuzzy(
        string candidate,
        SearchRequest request,
        CancellationToken cancellationToken)
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

        cancellationToken.ThrowIfCancellationRequested();
        var distance = EditDistance(
            candidate,
            request.Query,
            request.CaseSensitivity,
            cancellationToken);
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
            if (index == 0 || !IsLetterOrDigitBefore(candidate, index))
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

    private static bool IsLetterOrDigitBefore(string candidate, int index)
    {
        var last = candidate[index - 1];
        if (char.IsLowSurrogate(last) && index >= 2 && char.IsHighSurrogate(candidate[index - 2]))
        {
            return Rune.IsLetterOrDigit(new Rune(candidate[index - 2], last));
        }

        return Rune.IsLetterOrDigit(new Rune(last));
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
        for (var queryIndex = 0; queryIndex < query.Length;)
        {
            var queryRune = Rune.GetRuneAt(query, queryIndex);
            queryIndex += queryRune.Utf16SequenceLength;
            var found = false;
            while (candidateIndex < candidate.Length)
            {
                var candidateRune = Rune.GetRuneAt(candidate, candidateIndex);
                candidateIndex += candidateRune.Utf16SequenceLength;
                if (RunesEqual(candidateRune, queryRune, comparison))
                {
                    found = true;
                    break;
                }

                gapCost++;
            }

            if (!found)
            {
                return -1;
            }
        }

        return gapCost;
    }

    private static int EditDistance(
        string candidate,
        string query,
        CaseSensitivity caseSensitivity,
        CancellationToken cancellationToken)
    {
        Span<Rune> candidateRunes = stackalloc Rune[candidate.Length];
        Span<Rune> queryRunes = stackalloc Rune[query.Length];
        var candidateLength = CopyRunes(candidate, candidateRunes);
        var queryLength = CopyRunes(query, queryRunes);
        candidateRunes = candidateRunes[..candidateLength];
        queryRunes = queryRunes[..queryLength];

        // Edit similarity only resolves nearby candidates. A bounded band avoids turning a
        // legitimate 256-byte query over thousands of keys into unbounded quadratic work.
        // 编辑相似度只用于区分邻近候选；有界带状计算避免合法 256 字节查询在数千 key 上产生无界二次开销。
        if (Math.Abs(candidateRunes.Length - queryRunes.Length) > MaximumEditDistance)
        {
            return MaximumEditDistance + 1;
        }

        var columns = candidateRunes.Length <= queryRunes.Length ? candidateRunes : queryRunes;
        var rows = candidateRunes.Length <= queryRunes.Length ? queryRunes : candidateRunes;
        Span<int> previous = stackalloc int[columns.Length + 1];
        Span<int> current = stackalloc int[columns.Length + 1];
        var beyondBound = MaximumEditDistance + 1;

        for (var column = 0; column <= columns.Length; column++)
        {
            previous[column] = column <= MaximumEditDistance ? column : beyondBound;
        }

        for (var row = 1; row <= rows.Length; row++)
        {
            if ((row & 31) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            current.Fill(beyondBound);
            current[0] = row <= MaximumEditDistance ? row : beyondBound;
            var firstColumn = Math.Max(1, row - MaximumEditDistance);
            var lastColumn = Math.Min(columns.Length, row + MaximumEditDistance);
            for (var column = firstColumn; column <= lastColumn; column++)
            {
                var substitution = CharactersEqual(
                    rows[row - 1],
                    columns[column - 1],
                    caseSensitivity) ? 0 : 1;
                current[column] = Math.Min(
                    Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + substitution);
            }

            var temporary = previous;
            previous = current;
            current = temporary;
        }

        return Math.Min(previous[columns.Length], beyondBound);
    }

    private static int CopyRunes(string value, Span<Rune> destination)
    {
        var count = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            destination[count++] = rune;
        }

        return count;
    }

    private static bool CharactersEqual(
        Rune left,
        Rune right,
        CaseSensitivity caseSensitivity) => caseSensitivity == CaseSensitivity.Sensitive
            ? left == right
            : Rune.ToUpperInvariant(left) == Rune.ToUpperInvariant(right);

    private static bool RunesEqual(
        Rune left,
        Rune right,
        StringComparison comparison) => comparison == StringComparison.Ordinal
            ? left == right
            : Rune.ToUpperInvariant(left) == Rune.ToUpperInvariant(right);

    private static StringComparison ToComparison(CaseSensitivity caseSensitivity) =>
        caseSensitivity == CaseSensitivity.Sensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
}
