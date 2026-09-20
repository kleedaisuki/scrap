namespace Scrap.Domain.Tests;

/// <summary>验证 exact、fuzzy 与 regex 搜索的契约。 / Verifies exact, fuzzy, and regex search contracts.</summary>
public sealed class SearchTests
{
    /// <summary>空正则不匹配任何 record；只有 fuzzy 空查询可列出候选。 / An empty regex matches no records; only an empty fuzzy query may list candidates.</summary>
    [Fact]
    public void EmptyRegexReturnsNoMatches()
    {
        SearchCandidate[] candidates = [new(ScopeName.Create("scope"), RecordKey.Create("key"))];
        var request = SearchRequest.TryCreate([], string.Empty, SearchMode.Regex, CaseSensitivity.Insensitive, 100).Value;
        Assert.Empty(RecordSearch.Search(candidates, request).Value);
    }
    /// <summary>验证请求默认值、字节上限、limit 与枚举不变量。 / Verifies request defaults, byte limits, limit, and enum invariants.</summary>
    [Fact]
    public void SearchRequestEnforcesBoundariesAndDefaults()
    {
        var defaults = SearchRequest.TryCreate(["scope"], string.Empty).Value;
        var exactLimit = SearchRequest.TryCreate(
            ["scope"],
            new string('界', SearchRequest.MaximumQueryUtf8Bytes / 3),
            SearchMode.Exact,
            CaseSensitivity.Sensitive,
            SearchRequest.MaximumLimit);
        var tooLong = SearchRequest.TryCreate(
            ["scope"],
            new string('界', (SearchRequest.MaximumQueryUtf8Bytes / 3) + 1),
            SearchMode.Fuzzy,
            CaseSensitivity.Insensitive);
        var badLimit = SearchRequest.TryCreate(
            ["scope"], "q", SearchMode.Fuzzy, CaseSensitivity.Insensitive, 0);
        var badMode = SearchRequest.TryCreate(
            ["scope"], "q", (SearchMode)99, CaseSensitivity.Insensitive);

        Assert.Equal(SearchMode.Fuzzy, defaults.Mode);
        Assert.Equal(CaseSensitivity.Insensitive, defaults.CaseSensitivity);
        Assert.Equal(SearchRequest.DefaultLimit, defaults.Limit);
        Assert.Equal(TimeSpan.FromMilliseconds(100), SearchRequest.RegexTimeout);
        Assert.True(exactLimit.IsSuccess);
        Assert.Equal(DomainErrorCode.TextTooLong, tooLong.Error?.Code);
        Assert.Equal(DomainErrorCode.OutOfRange, badLimit.Error?.Code);
        Assert.Equal(DomainErrorCode.InvalidOption, badMode.Error?.Code);
    }

    /// <summary>scope 集合按 ordinal 规范化且空集合保留“全部”语义。 / Scope collections normalize ordinally while empty preserves the “all” semantic.</summary>
    [Fact]
    public void SearchRequestNormalizesScopesAndSupportsAllScopes()
    {
        SearchRequest selected = SearchRequest.TryCreate(["z", "A", "z"], "q").Value;
        SearchRequest all = SearchRequest.TryCreate([], "q").Value;

        Assert.Equal(["A", "z"], selected.Scopes.Select(scope => scope.Value));
        Assert.Empty(all.Scopes);
    }

    /// <summary>跨 scope 同分结果按 scope 后 key 的 ordinal 次序稳定排列。 / Cross-scope ties are stable by ordinal scope and then key.</summary>
    [Fact]
    public void CrossScopeTiesOrderByScopeThenKey()
    {
        SearchCandidate[] candidates =
        [
            new(ScopeName.Create("z"), RecordKey.Create("api")),
            new(ScopeName.Create("A"), RecordKey.Create("api")),
            new(ScopeName.Create("A"), RecordKey.Create("Api")),
        ];

        IReadOnlyList<SearchMatch> matches = RecordSearch.Search(
            candidates,
            SearchRequest.TryCreate([], "api", SearchMode.Exact, CaseSensitivity.Insensitive).Value).Value;

        Assert.Equal(
            [("A", "Api"), ("A", "api"), ("z", "api")],
            matches.Select(match => (match.Candidate.Scope.Value, match.Candidate.Key.Value)));
    }

    /// <summary>显式 scope 集合在评分前过滤候选。 / Explicit scopes filter candidates before scoring.</summary>
    [Fact]
    public void SelectedScopesFilterCandidates()
    {
        SearchCandidate[] candidates =
        [
            new(ScopeName.Create("selected"), RecordKey.Create("api")),
            new(ScopeName.Create("ignored"), RecordKey.Create("api")),
        ];

        SearchMatch match = Assert.Single(RecordSearch.Search(
            candidates,
            SearchRequest.TryCreate(["selected"], "api", SearchMode.Exact, CaseSensitivity.Sensitive).Value).Value);

        Assert.Equal("selected", match.Candidate.Scope.Value);
    }

    /// <summary>验证 exact 使用 ordinal 大小写策略且 insensitive 可返回多个候选。 / Verifies ordinal exact matching and multiple insensitive candidates.</summary>
    [Fact]
    public void ExactHonorsCaseSensitivityAndKeepsAmbiguity()
    {
        var keys = Keys("api", "API", "Api", "apı");
        var sensitive = Request("api", SearchMode.Exact, CaseSensitivity.Sensitive);
        var insensitive = Request("api", SearchMode.Exact, CaseSensitivity.Insensitive);

        var sensitiveMatches = RecordSearch.Search(keys, sensitive).Value;
        var insensitiveMatches = RecordSearch.Search(keys, insensitive).Value;

        Assert.Equal(["api"], Values(sensitiveMatches));
        Assert.Equal(["API", "Api", "api"], Values(insensitiveMatches));
    }

    /// <summary>验证 fuzzy 类别优先级和相同分数的 ordinal 稳定排序。 / Verifies fuzzy category priority and ordinal tie-breaking for equal scores.</summary>
    [Fact]
    public void FuzzyRanksByCategoryThenOrdinalKey()
    {
        var request = Request("api", SearchMode.Fuzzy, CaseSensitivity.Sensitive);
        var keys = Keys("yapi", "apx", "a_p_i", "my-api", "xapi", "api-token", "api");

        var result = RecordSearch.Search(keys, request).Value;

        Assert.Equal(
            ["api", "api-token", "my-api", "xapi", "yapi", "a_p_i", "apx"],
            Values(result));
        Assert.True(result.Zip(result.Skip(1)).All(pair => pair.First.Score >= pair.Second.Score));
    }

    /// <summary>验证空 fuzzy query 按 ordinal 列出 key，而不是按长度制造特殊排序。 / Verifies that an empty fuzzy query lists keys ordinally instead of inventing a length-based special order.</summary>
    [Fact]
    public void EmptyFuzzyQueryListsKeysOrdinally()
    {
        var result = RecordSearch.Search(
            Keys("zz", "a-long", "A"),
            Request(string.Empty, SearchMode.Fuzzy, CaseSensitivity.Insensitive)).Value;

        Assert.Equal(["A", "a-long", "zz"], Values(result));
        Assert.All(result, match => Assert.Equal(0, match.Score));
    }

    /// <summary>验证 fuzzy 大小写策略与 limit。 / Verifies fuzzy case semantics and result limit.</summary>
    [Fact]
    public void FuzzyHonorsCaseAndLimit()
    {
        var sensitive = SearchRequest.TryCreate(
            ["scope"], "API", SearchMode.Fuzzy, CaseSensitivity.Sensitive, 1).Value;
        var insensitive = SearchRequest.TryCreate(
            ["scope"], "API", SearchMode.Fuzzy, CaseSensitivity.Insensitive, 1).Value;
        var keys = Keys("api", "API-token");

        Assert.Equal("API-token", RecordSearch.Search(keys, sensitive).Value.Single().Candidate.Key.Value);
        Assert.Equal("api", RecordSearch.Search(keys, insensitive).Value.Single().Candidate.Key.Value);
    }

    /// <summary>验证 regex 使用 .NET 语义、支持回退构造且确定性排序。 / Verifies .NET regex semantics, fallback constructs, and deterministic ordering.</summary>
    [Fact]
    public void RegexSupportsDotNetConstructsAndStableOrdering()
    {
        var keys = Keys("beta-12", "Alpha-34", "alpha-x", "alpha-12");
        var request = Request("(?<=alpha-)\\d+$", SearchMode.Regex, CaseSensitivity.Insensitive);

        var result = RecordSearch.Search(keys, request);

        Assert.True(result.IsSuccess);
        Assert.Equal(["Alpha-34", "alpha-12"], Values(result.Value));
    }

    /// <summary>验证非法 regex 返回查询错误而不抛出。 / Verifies that invalid regex returns a query error rather than throwing.</summary>
    [Fact]
    public void InvalidRegexReturnsStructuredError()
    {
        var result = RecordSearch.Search(
            Keys("anything"),
            Request("[", SearchMode.Regex, CaseSensitivity.Sensitive));

        Assert.True(result.IsFailure);
        Assert.Equal(DomainErrorCode.InvalidRegex, result.Error?.Code);
        Assert.Equal("query", result.Error?.Field);
    }

    /// <summary>验证需要 backtracking 回退的病态 regex 被固定期限中止。 / Verifies that a pathological regex requiring the backtracking fallback is stopped by the fixed deadline.</summary>
    [Fact]
    public void BacktrackingRegexTimeoutReturnsStructuredError()
    {
        var pathologicalKey = new string('a', RecordKey.MaximumUtf8Bytes - 1) + "!";
        var result = RecordSearch.Search(
            Keys(pathologicalKey),
            Request("(?=a)(a+)+$", SearchMode.Regex, CaseSensitivity.Sensitive));

        Assert.True(result.IsFailure);
        Assert.Equal(DomainErrorCode.RegexTimeout, result.Error?.Code);
        Assert.Equal("query", result.Error?.Field);
    }

    /// <summary>验证补充平面 Unicode 字母不会被误判成单词分隔符。 / Verifies that a supplementary-plane Unicode letter is not mistaken for a word boundary.</summary>
    [Fact]
    public void FuzzyBoundaryRecognizesSupplementaryUnicodeLetters()
    {
        var result = RecordSearch.Search(
            Keys("xapi", "\U00010400api", "-api"),
            Request("api", SearchMode.Fuzzy, CaseSensitivity.Sensitive)).Value;

        Assert.Equal(["-api", "xapi", "\U00010400api"], Values(result));
        Assert.True(result[1].Score > result[2].Score);
    }

    /// <summary>验证最坏等长 edit 候选不会产生按 DP 单元增长的托管分配。 / Verifies that worst-case equal-length edit candidates do not allocate per dynamic-programming cell.</summary>
    [Fact]
    public void FuzzyEditDistanceHasBoundedAllocation()
    {
        var keys = Enumerable.Range(0, 2_000)
            .Select(index => new SearchCandidate(ScopeName.Create("scope"), RecordKey.Create(
                index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) +
                new string('x', RecordKey.MaximumUtf8Bytes - 4))))
            .ToArray();
        var request = Request(
            new string('y', RecordKey.MaximumUtf8Bytes),
            SearchMode.Fuzzy,
            CaseSensitivity.Sensitive);
        _ = RecordSearch.Search(keys, request);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = RecordSearch.Search(keys, request);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(result.IsSuccess);
        Assert.True(allocated < 10_000_000, $"Allocated {allocated} bytes.");
    }

    /// <summary>验证 edit 与 subsequence 在 insensitive 模式按 Unicode scalar 折叠大小写。 / Verifies Unicode-scalar case folding in insensitive edit and subsequence matching.</summary>
    [Fact]
    public void FuzzyEditDistanceFoldsSupplementaryUnicodeCase()
    {
        var result = RecordSearch.Search(
            Keys("z\U00010428", "x\U00010400"),
            Request("y\U00010428", SearchMode.Fuzzy, CaseSensitivity.Insensitive)).Value;

        Assert.Equal(["x\U00010400", "z\U00010428"], Values(result));
        Assert.Equal(result[0].Score, result[1].Score);
    }

    /// <summary>验证 regex timeout 是整次搜索预算，而非可按候选重复消耗。 / Verifies that the regex timeout is a whole-search budget rather than a per-candidate renewable allowance.</summary>
    [Fact]
    public void RegexTimeoutCoversWholeSearch()
    {
        var keys = Enumerable.Range(0, 100)
            .Select(index => new SearchCandidate(ScopeName.Create("scope"), RecordKey.Create(
                new string('a', 18) + "!" +
                index.ToString(System.Globalization.CultureInfo.InvariantCulture))))
            .ToArray();

        var result = RecordSearch.Search(
            keys,
            Request("(?=a)(a+)+$", SearchMode.Regex, CaseSensitivity.Sensitive));

        Assert.True(result.IsFailure);
        Assert.Equal(DomainErrorCode.RegexTimeout, result.Error?.Code);
    }

    /// <summary>验证大量 fuzzy 候选可在批次边界确定性取消。 / Verifies deterministic cancellation of a large fuzzy candidate set at a batch boundary.</summary>
    [Fact]
    public void LargeFuzzySearchObservesCancellationBetweenBatches()
    {
        using var cancellation = new CancellationTokenSource();
        var request = Request(
            new string('y', RecordKey.MaximumUtf8Bytes),
            SearchMode.Fuzzy,
            CaseSensitivity.Sensitive);

        var exception = Assert.Throws<OperationCanceledException>(() => RecordSearch.Search(
            CancelAfterBatch(cancellation),
            request,
            cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    private static SearchRequest Request(
        string query,
        SearchMode mode,
        CaseSensitivity caseSensitivity) => SearchRequest.TryCreate(
            ["scope"],
            query,
            mode,
            caseSensitivity).Value;

    private static SearchCandidate[] Keys(params string[] values) =>
        values.Select(key => new SearchCandidate(ScopeName.Create("scope"), RecordKey.Create(key))).ToArray();

    private static string[] Values(IEnumerable<SearchMatch> matches) =>
        matches.Select(match => match.Candidate.Key.Value).ToArray();

    private static IEnumerable<SearchCandidate> CancelAfterBatch(CancellationTokenSource cancellation)
    {
        for (var index = 0; index < 10_000; index++)
        {
            if (index == 64)
            {
                cancellation.Cancel();
            }

            yield return new SearchCandidate(ScopeName.Create("scope"), RecordKey.Create(
                index.ToString("D5", System.Globalization.CultureInfo.InvariantCulture) +
                new string('x', RecordKey.MaximumUtf8Bytes - 5)));
        }
    }
}
