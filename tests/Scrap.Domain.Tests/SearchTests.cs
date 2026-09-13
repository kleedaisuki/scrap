namespace Scrap.Domain.Tests;

/// <summary>验证 exact、fuzzy 与 regex 搜索的契约。 / Verifies exact, fuzzy, and regex search contracts.</summary>
public sealed class SearchTests
{
    /// <summary>验证请求默认值、字节上限、limit 与枚举不变量。 / Verifies request defaults, byte limits, limit, and enum invariants.</summary>
    [Fact]
    public void SearchRequestEnforcesBoundariesAndDefaults()
    {
        var defaults = SearchRequest.TryCreate("scope", string.Empty).Value;
        var exactLimit = SearchRequest.TryCreate(
            "scope",
            new string('界', SearchRequest.MaximumQueryUtf8Bytes / 3),
            SearchMode.Exact,
            CaseSensitivity.Sensitive,
            SearchRequest.MaximumLimit);
        var tooLong = SearchRequest.TryCreate(
            "scope",
            new string('界', (SearchRequest.MaximumQueryUtf8Bytes / 3) + 1),
            SearchMode.Fuzzy,
            CaseSensitivity.Insensitive);
        var badLimit = SearchRequest.TryCreate(
            "scope", "q", SearchMode.Fuzzy, CaseSensitivity.Insensitive, 0);
        var badMode = SearchRequest.TryCreate(
            "scope", "q", (SearchMode)99, CaseSensitivity.Insensitive);

        Assert.Equal(SearchMode.Fuzzy, defaults.Mode);
        Assert.Equal(CaseSensitivity.Insensitive, defaults.CaseSensitivity);
        Assert.Equal(SearchRequest.DefaultLimit, defaults.Limit);
        Assert.Equal(TimeSpan.FromMilliseconds(100), SearchRequest.RegexTimeout);
        Assert.True(exactLimit.IsSuccess);
        Assert.Equal(DomainErrorCode.TextTooLong, tooLong.Error?.Code);
        Assert.Equal(DomainErrorCode.OutOfRange, badLimit.Error?.Code);
        Assert.Equal(DomainErrorCode.InvalidOption, badMode.Error?.Code);
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
            "scope", "API", SearchMode.Fuzzy, CaseSensitivity.Sensitive, 1).Value;
        var insensitive = SearchRequest.TryCreate(
            "scope", "API", SearchMode.Fuzzy, CaseSensitivity.Insensitive, 1).Value;
        var keys = Keys("api", "API-token");

        Assert.Equal("API-token", RecordSearch.Search(keys, sensitive).Value.Single().Key.Value);
        Assert.Equal("api", RecordSearch.Search(keys, insensitive).Value.Single().Key.Value);
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
            .Select(index => RecordKey.Create(
                index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) +
                new string('x', RecordKey.MaximumUtf8Bytes - 4)))
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
            .Select(index => RecordKey.Create(
                new string('a', 18) + "!" +
                index.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .ToArray();

        var result = RecordSearch.Search(
            keys,
            Request("(?=a)(a+)+$", SearchMode.Regex, CaseSensitivity.Sensitive));

        Assert.True(result.IsFailure);
        Assert.Equal(DomainErrorCode.RegexTimeout, result.Error?.Code);
    }

    private static SearchRequest Request(
        string query,
        SearchMode mode,
        CaseSensitivity caseSensitivity) => SearchRequest.TryCreate(
            "scope",
            query,
            mode,
            caseSensitivity).Value;

    private static RecordKey[] Keys(params string[] values) =>
        values.Select(RecordKey.Create).ToArray();

    private static string[] Values(IEnumerable<SearchMatch> matches) =>
        matches.Select(match => match.Key.Value).ToArray();
}
