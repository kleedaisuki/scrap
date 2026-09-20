using Scrap.Gui.Abstractions;
using Scrap.Gui.Models;
using Scrap.Gui.Services;
using Scrap.Gui.ViewModels;

namespace Scrap.Gui.Tests;

/// <summary>
/// 验证用户偏好存储的恢复与持久化契约。
/// Verifies fallback and persistence contracts of the user-preference store.
/// </summary>
public sealed class UserPreferenceStoreTests
{
    /// <summary>
    /// 缺失、损坏或包含无效枚举的文件必须回退到当前平台默认值。
    /// Missing, malformed, or invalid-enum files must fall back to the current platform defaults.
    /// </summary>
    [Fact]
    public void LoadUnusableFileReturnsDefaults()
    {
        using var directory = TestDirectory.Create();
        string path = Path.Combine(directory.Path, "preferences.json");
        var store = new UserPreferenceStore(path);
        UserPreferences expected = UserPreferences.CreateDefault();

        Assert.Equal(expected, store.Load());

        File.WriteAllText(path, "{ definitely not json }");
        Assert.Equal(expected, store.Load());

        File.WriteAllText(path, "{\"Theme\":999,\"Language\":999}");
        Assert.Equal(expected, store.Load());
    }

    /// <summary>
    /// 保存的主题和语言必须可由新的存储实例完整读取。
    /// A new store instance must recover the exact saved theme and language.
    /// </summary>
    [Fact]
    public void SaveThenLoadRoundTripsPreferences()
    {
        using var directory = TestDirectory.Create();
        string path = Path.Combine(directory.Path, "nested", "preferences.json");
        var expected = new UserPreferences(AppTheme.Dark, AppLanguage.English);

        new UserPreferenceStore(path).Save(expected);

        Assert.Equal(expected, new UserPreferenceStore(path).Load());
        Assert.False(File.Exists(path + ".tmp"));
    }
}

/// <summary>
/// 验证主窗口状态机中本地化、主题、编辑器与跨 scope 搜索行为。
/// Verifies localization, theme, editor, and cross-scope search behavior in the main-window state machine.
/// </summary>
public sealed class MainWindowViewModelTests
{
    /// <summary>
    /// 切换语言必须原子替换文案集与所有本地化选项，不能遗留上一语言。
    /// Switching language must atomically replace copy and localized choices without stale strings.
    /// </summary>
    [Fact]
    public async Task LanguageSwitchRebuildsStringsInExactlyTheSelectedLanguage()
    {
        using var directory = TestDirectory.Create();
        string path = Path.Combine(directory.Path, "preferences.json");
        var store = new UserPreferenceStore(path);
        store.Save(new UserPreferences(AppTheme.System, AppLanguage.English));
        await using var viewModel = CreateViewModel(new FakeScrapClient(), store);

        Assert.Equal("Scope", viewModel.L.Scope);
        Assert.Equal("Search in", viewModel.L.SearchIn);
        Assert.Equal("All scopes", viewModel.L.AllScopes);
        Assert.Equal("Choose one or more scopes", viewModel.L.ChooseSearchScopes);
        Assert.Equal("Choose search scopes, current selection: All scopes", viewModel.SearchScopeAccessibleName);

        viewModel.SelectedLanguage = Choice(viewModel.LanguageChoices, AppLanguage.SimplifiedChinese);

        Assert.Equal("空间", viewModel.L.Scope);
        Assert.Equal("检索范围", viewModel.L.SearchIn);
        Assert.Equal("所有空间", viewModel.L.AllScopes);
        Assert.Equal("选择一个或多个空间", viewModel.L.ChooseSearchScopes);
        Assert.Equal("选择检索空间，当前：所有空间", viewModel.SearchScopeAccessibleName);
        Assert.Equal("浅色", Choice(viewModel.ThemeChoices, AppTheme.Light).Label);
        Assert.Equal(AppLanguage.SimplifiedChinese, store.Load().Language);

        viewModel.SelectedLanguage = Choice(viewModel.LanguageChoices, AppLanguage.English);

        Assert.Equal("Scope", viewModel.L.Scope);
        Assert.Equal("Search in", viewModel.L.SearchIn);
        Assert.Equal("All scopes", viewModel.L.AllScopes);
        Assert.Equal("Light", Choice(viewModel.ThemeChoices, AppTheme.Light).Label);
        Assert.Equal(AppLanguage.English, store.Load().Language);
    }

    /// <summary>
    /// 构造和主题切换都必须调用主题应用回调，切换还必须持久化。
    /// Construction and selection must invoke the theme callback, and selection must persist.
    /// </summary>
    [Fact]
    public async Task ThemeSelectionAppliesAndPersistsTheme()
    {
        using var directory = TestDirectory.Create();
        string path = Path.Combine(directory.Path, "preferences.json");
        var store = new UserPreferenceStore(path);
        store.Save(new UserPreferences(AppTheme.Light, AppLanguage.English));
        var applied = new List<AppTheme>();
        await using var viewModel = CreateViewModel(new FakeScrapClient(), store, applied.Add);

        Assert.Equal([AppTheme.Light], applied);

        viewModel.SelectedTheme = Choice(viewModel.ThemeChoices, AppTheme.Dark);

        Assert.Equal([AppTheme.Light, AppTheme.Dark], applied);
        Assert.Equal(new UserPreferences(AppTheme.Dark, AppLanguage.English), store.Load());
    }

    /// <summary>
    /// 保存后的遮罩策略与编辑期间的可见性必须是两个正交状态。
    /// Post-save masking policy and during-edit visibility must remain orthogonal states.
    /// </summary>
    [Fact]
    public async Task EditorMaskingAndVisibilityDoNotMutateEachOther()
    {
        using var directory = TestDirectory.Create();
        await using var viewModel = CreateViewModel(
            new FakeScrapClient(),
            new UserPreferenceStore(Path.Combine(directory.Path, "preferences.json")));

        viewModel.EditorValueVisible = true;
        viewModel.EditorIsMasked = false;
        Assert.True(viewModel.EditorValueVisible);

        viewModel.EditorIsMasked = true;
        Assert.True(viewModel.EditorValueVisible);

        viewModel.EditorValueVisible = false;
        Assert.True(viewModel.EditorIsMasked);

        viewModel.EditorValueVisible = true;
        Assert.True(viewModel.EditorIsMasked);
    }

    /// <summary>
    /// 全范围搜索必须发送空 scope 集，且候选详情必须按候选自身的 scope 加载。
    /// All-scope search must send an empty scope set, and details must load from the candidate's own scope.
    /// </summary>
    [Fact]
    public async Task AllScopeSearchUsesEmptyScopesAndLoadsCandidateFromItsOwnScope()
    {
        using var directory = TestDirectory.Create();
        var candidate = new RecordCandidate("other-scope", "api-key", RecordPresentation.Masked);
        var client = new FakeScrapClient
        {
            Scopes = [new ScopeSummary("current-scope", 1)],
            SearchResults = [candidate],
            Record = new RecordDetails(
                candidate.Scope,
                candidate.Key,
                "value",
                candidate.Presentation,
                DateTimeOffset.UnixEpoch,
                7),
        };
        await using var viewModel = CreateViewModel(
            client,
            new UserPreferenceStore(Path.Combine(directory.Path, "preferences.json")));

        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => client.SearchRequests.Count > 0 && viewModel.Candidates.Count == 1);

        RecordSearchRequest request = client.SearchRequests[^1];
        Assert.Empty(request.Scopes);
        Assert.True(viewModel.IsAllSearchScopesSelected);
        Assert.Equal(viewModel.L.AllScopes, viewModel.SearchScopeSummary);

        viewModel.SelectedCandidate = candidate;
        await WaitUntilAsync(() => viewModel.SelectedRecord is not null);

        Assert.Equal((candidate.Scope, candidate.Key), Assert.Single(client.GetRecordCalls));
        Assert.Equal(candidate.Scope, viewModel.SelectedRecord!.Scope);
    }

    /// <summary>
    /// GUI 必须把任意勾选组合发送为精确 scope 集合，且记录目标选择不得偷偷改写检索集合。
    /// The GUI must send any checked combination as the exact scope set, and the record destination must not rewrite it.
    /// </summary>
    [Fact]
    public async Task SearchScopePickerSendsArbitrarySubsetIndependentOfRecordDestination()
    {
        using var directory = TestDirectory.Create();
        var client = new FakeScrapClient
        {
            Scopes =
            [
                new ScopeSummary("alpha", 1),
                new ScopeSummary("beta", 1),
                new ScopeSummary("gamma", 1),
            ],
        };
        await using var viewModel = CreateViewModel(
            client,
            new UserPreferenceStore(Path.Combine(directory.Path, "preferences.json")));

        await viewModel.InitializeAsync();
        SearchScopeChoice alpha = Assert.Single(viewModel.SearchScopeChoices, choice => choice.Name == "alpha");
        SearchScopeChoice beta = Assert.Single(viewModel.SearchScopeChoices, choice => choice.Name == "beta");

        alpha.IsSelected = true;
        beta.IsSelected = true;
        await WaitUntilAsync(() => client.SearchRequests.Any(request => request.Scopes.SequenceEqual(["alpha", "beta"])));

        Assert.False(viewModel.IsAllSearchScopesSelected);
        Assert.Equal(viewModel.L.SelectedScopes(2), viewModel.SearchScopeSummary);
        Assert.DoesNotContain("gamma", client.SearchRequests[^1].Scopes);

        int requestsBeforeDestinationChange = client.SearchRequests.Count;
        viewModel.SelectedScope = Assert.Single(viewModel.Scopes, scope => scope.Name == "gamma");
        await Task.Delay(50);

        Assert.Equal(requestsBeforeDestinationChange, client.SearchRequests.Count);
        Assert.Equal(["alpha", "beta"], client.SearchRequests[^1].Scopes);
    }

    /// <summary>
    /// 从显式子集切回“所有空间”必须清空协议 scope 集，而不是展开为当前快照。
    /// Returning from an explicit subset to All scopes must clear the protocol set rather than expand the current snapshot.
    /// </summary>
    [Fact]
    public async Task SelectingAllScopesRestoresEmptyProtocolScopeSet()
    {
        using var directory = TestDirectory.Create();
        var client = new FakeScrapClient
        {
            Scopes = [new ScopeSummary("alpha", 1), new ScopeSummary("beta", 1)],
        };
        await using var viewModel = CreateViewModel(
            client,
            new UserPreferenceStore(Path.Combine(directory.Path, "preferences.json")));

        await viewModel.InitializeAsync();
        Assert.Single(viewModel.SearchScopeChoices, choice => choice.Name == "alpha").IsSelected = true;
        await WaitUntilAsync(() => client.SearchRequests[^1].Scopes.SequenceEqual(["alpha"]));

        viewModel.IsAllSearchScopesSelected = true;
        await WaitUntilAsync(() => client.SearchRequests.Count >= 3 && client.SearchRequests[^1].Scopes.Count == 0);

        Assert.True(viewModel.IsAllSearchScopesSelected);
        Assert.All(viewModel.SearchScopeChoices, choice => Assert.False(choice.IsSelected));
        Assert.Empty(client.SearchRequests[^1].Scopes);
    }

    /// <summary>
    /// rename 已提交但刷新失败时，本地目标与检索选择仍必须使用新身份。
    /// After a committed rename whose refresh fails, local destination and search choices must retain the new identity.
    /// </summary>
    [Fact]
    public async Task ScopeRenameKeepsSearchChoiceConsistentWhenRefreshFails()
    {
        using var directory = TestDirectory.Create();
        var client = new FakeScrapClient
        {
            Scopes = [new ScopeSummary("alpha", 1), new ScopeSummary("beta", 1)],
            FailListScopesAfter = 1,
        };
        await using var viewModel = CreateViewModel(
            client,
            new UserPreferenceStore(Path.Combine(directory.Path, "preferences.json")));

        await viewModel.InitializeAsync();
        Assert.Single(viewModel.SearchScopeChoices, choice => choice.Name == "alpha").IsSelected = true;
        await WaitUntilAsync(() => client.SearchRequests[^1].Scopes.SequenceEqual(["alpha"]));

        viewModel.OpenRenameScopeCommand.Execute(null);
        viewModel.ScopeNameInput = "renamed";
        viewModel.SaveScopeCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsBusy && viewModel.HasError);

        Assert.Equal("renamed", viewModel.SelectedScope?.Name);
        Assert.DoesNotContain(viewModel.SearchScopeChoices, choice => choice.Name == "alpha");
        Assert.True(Assert.Single(viewModel.SearchScopeChoices, choice => choice.Name == "renamed").IsSelected);

        Assert.Single(viewModel.SearchScopeChoices, choice => choice.Name == "beta").IsSelected = true;
        await WaitUntilAsync(() => client.SearchRequests[^1].Scopes.SequenceEqual(["beta", "renamed"]));
    }

    /// <summary>
    /// 全范围结果存在同名 key 时，保存后必须按完整身份重新选择目标记录。
    /// After saving among duplicate keys in an all-scope result, the target must be reselected by complete identity.
    /// </summary>
    [Fact]
    public async Task SaveReselectsCandidateByScopeAndKey()
    {
        using var directory = TestDirectory.Create();
        var other = new RecordCandidate("a-scope", "token", RecordPresentation.Masked);
        var target = new RecordCandidate("b-scope", "token", RecordPresentation.Masked);
        var client = new FakeScrapClient
        {
            Scopes = [new ScopeSummary(target.Scope, 0)],
            SearchResults = [other, target],
            Record = new RecordDetails(
                target.Scope,
                target.Key,
                "value",
                target.Presentation,
                DateTimeOffset.UnixEpoch,
                1),
        };
        await using var viewModel = CreateViewModel(
            client,
            new UserPreferenceStore(Path.Combine(directory.Path, "preferences.json")));

        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.Candidates.Count == 2);
        viewModel.OpenCreateRecordCommand.Execute(null);
        viewModel.EditorKey = target.Key;
        viewModel.EditorValue = "value";
        viewModel.SaveRecordCommand.Execute(null);

        await WaitUntilAsync(() => viewModel.SelectedCandidate is not null);

        Assert.Equal(target, viewModel.SelectedCandidate);
    }

    /// <summary>
    /// 精确与正则模式必须在 GUI 边界拒绝 client 意外返回的无关候选。
    /// Exact and regex modes must reject unrelated candidates accidentally returned by the client boundary.
    /// </summary>
    [Theory]
    [InlineData(SearchMode.Exact, "token", "token")]
    [InlineData(SearchMode.Regex, "^tok.*$", "token")]
    public async Task StrictSearchModesDisplayOnlyActualMatches(SearchMode mode, string query, string expected)
    {
        using var directory = TestDirectory.Create();
        var client = new FakeScrapClient
        {
            Scopes = [new ScopeSummary("scope", 2)],
            SearchResults =
            [
                new RecordCandidate("scope", expected, RecordPresentation.Masked),
                new RecordCandidate("scope", "unrelated", RecordPresentation.Masked),
            ],
        };
        await using var viewModel = CreateViewModel(
            client,
            new UserPreferenceStore(Path.Combine(directory.Path, "preferences.json")));

        await viewModel.InitializeAsync();
        viewModel.SelectedMode = mode;
        viewModel.SearchText = query;
        await WaitUntilAsync(() => client.SearchRequests.Any(request => request.Query == query));
        await WaitUntilAsync(() => viewModel.Candidates.Count == 1);

        Assert.Equal(expected, Assert.Single(viewModel.Candidates).Key);
        Assert.Equal(mode, client.SearchRequests[^1].Mode);
    }

    /// <summary>
    /// 空的严格查询不能被当作“匹配一切”，特别是空正则。
    /// A blank strict query must not become match-all, especially for an empty regular expression.
    /// </summary>
    [Theory]
    [InlineData(SearchMode.Exact)]
    [InlineData(SearchMode.Regex)]
    public async Task BlankStrictSearchDisplaysNoCandidates(SearchMode mode)
    {
        using var directory = TestDirectory.Create();
        var client = new FakeScrapClient
        {
            Scopes = [new ScopeSummary("scope", 1)],
            SearchResults = [new RecordCandidate("scope", "anything", RecordPresentation.Masked)],
        };
        await using var viewModel = CreateViewModel(
            client,
            new UserPreferenceStore(Path.Combine(directory.Path, "preferences.json")));

        await viewModel.InitializeAsync();
        viewModel.SelectedMode = mode;
        await WaitUntilAsync(() => client.SearchRequests.Any(request => request.Mode == mode));
        await WaitUntilAsync(() => !viewModel.IsSearching);

        Assert.Empty(viewModel.Candidates);
    }

    /// <summary>
    /// 编辑器必须保留 value 顺序、重复项与空文本，且不能删除最后一项。
    /// The editor must preserve value order, duplicates, and empty text and must not remove its final item.
    /// </summary>
    [Fact]
    public async Task MultiValueEditorSavesOrderedNonEmptyCollection()
    {
        using var directory = TestDirectory.Create();
        var client = new FakeScrapClient { Scopes = [new ScopeSummary("scope", 0)] };
        await using var viewModel = CreateViewModel(
            client,
            new UserPreferenceStore(Path.Combine(directory.Path, "preferences.json")));

        await viewModel.InitializeAsync();
        viewModel.OpenCreateRecordCommand.Execute(null);
        viewModel.EditorKey = "key";
        viewModel.EditorValues[0].Value = "same";
        viewModel.AddEditorValueCommand.Execute(null);
        viewModel.EditorValues[1].Value = "same";
        viewModel.AddEditorValueCommand.Execute(null);
        viewModel.EditorValues[2].Value = "third";
        Assert.Equal(3, viewModel.EditorValues.Count);

        viewModel.MoveEditorValueUpCommand.Execute(viewModel.EditorValues[2]);
        Assert.Equal(["same", "third", "same"], viewModel.EditorValues.Select(value => value.Value));

        viewModel.RemoveEditorValueCommand.Execute(viewModel.EditorValues[2]);
        Assert.Equal(2, viewModel.EditorValues.Count);
        viewModel.RemoveEditorValueCommand.Execute(viewModel.EditorValues[1]);
        Assert.Single(viewModel.EditorValues);
        Assert.False(viewModel.RemoveEditorValueCommand.CanExecute(viewModel.EditorValues[0]));

        viewModel.AddEditorValueCommand.Execute(null);
        viewModel.SaveRecordCommand.Execute(null);
        await WaitUntilAsync(() => client.SaveRequests.Count == 1);

        Assert.Equal(["same", ""], Assert.Single(client.SaveRequests).Values);
    }

    /// <summary>
    /// 详情区必须为每个 value 保留独立行，显示与遮罩都不得改变集合结构。
    /// Details must preserve one row per value; reveal and masking must not change collection structure.
    /// </summary>
    [Fact]
    public async Task MultiValueDetailsRevealEveryValueWithoutConcatenation()
    {
        using var directory = TestDirectory.Create();
        var candidate = new RecordCandidate("scope", "key", RecordPresentation.Masked);
        var client = new FakeScrapClient
        {
            Scopes = [new ScopeSummary("scope", 1)],
            SearchResults = [candidate],
            Record = new RecordDetails(
                "scope",
                "key",
                ["first", "", "third"],
                RecordPresentation.Masked,
                DateTimeOffset.UnixEpoch,
                1),
        };
        await using var viewModel = CreateViewModel(
            client,
            new UserPreferenceStore(Path.Combine(directory.Path, "preferences.json")));

        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.Candidates.Count == 1);
        viewModel.SelectedCandidate = candidate;
        await WaitUntilAsync(() => viewModel.SelectedRecord is not null);

        Assert.Equal(3, viewModel.DisplayValues.Count);
        Assert.All(viewModel.DisplayValues, value => Assert.Equal("••••••••••••", value.Text));

        viewModel.ToggleRevealValueCommand.Execute(viewModel.DisplayValues[0]);
        viewModel.ToggleRevealValueCommand.Execute(viewModel.DisplayValues[1]);

        Assert.Equal(["first", "", "••••••••••••"], viewModel.DisplayValues.Select(value => value.Text));
        Assert.True(viewModel.IsRevealed);

        viewModel.HideAllRevealedValuesCommand.Execute(null);

        Assert.False(viewModel.IsRevealed);
        Assert.All(viewModel.DisplayValues, value => Assert.Equal("••••••••••••", value.Text));
    }

    /// <summary>
    /// GUI 必须在发送前执行集合数量与 UTF-8 总大小限制，不依赖后端驳回。
    /// The GUI must enforce count and aggregate UTF-8 limits before sending rather than relying on backend rejection.
    /// </summary>
    [Fact]
    public async Task MultiValueEditorEnforcesLocalCollectionLimits()
    {
        using var directory = TestDirectory.Create();
        var client = new FakeScrapClient { Scopes = [new ScopeSummary("scope", 0)] };
        await using var viewModel = CreateViewModel(
            client,
            new UserPreferenceStore(Path.Combine(directory.Path, "preferences.json")));

        await viewModel.InitializeAsync();
        viewModel.OpenCreateRecordCommand.Execute(null);
        viewModel.EditorKey = "key";
        for (int index = 1; index < 32; index++)
        {
            viewModel.AddEditorValueCommand.Execute(null);
        }

        Assert.Equal(32, viewModel.EditorValues.Count);
        Assert.False(viewModel.AddEditorValueCommand.CanExecute(null));

        viewModel.EditorValues[0].Value = new string('界', 22_000);
        Assert.True(viewModel.HasEditorValuesValidationError);
        Assert.False(viewModel.SaveRecordCommand.CanExecute(null));
    }

    /// <summary>
    /// 显式逐值操作必须更新活动项，工作区复制不得退回到隐式拼接或错误的第一项。
    /// Explicit per-value actions must update the active item; workspace copy must not concatenate or revert to the wrong first item.
    /// </summary>
    [Fact]
    public async Task PerValueCopySetsTheWorkspaceCopyTarget()
    {
        using var directory = TestDirectory.Create();
        var candidate = new RecordCandidate("scope", "key", RecordPresentation.Plain);
        var client = new FakeScrapClient
        {
            Scopes = [new ScopeSummary("scope", 1)],
            SearchResults = [candidate],
            Record = new RecordDetails("scope", "key", ["first", "second"], RecordPresentation.Plain, DateTimeOffset.UnixEpoch, 1),
        };
        var clipboard = new FakeClipboardService();
        await using var viewModel = CreateViewModel(
            client,
            new UserPreferenceStore(Path.Combine(directory.Path, "preferences.json")),
            clipboard: clipboard);

        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.Candidates.Count == 1);
        viewModel.SelectedCandidate = candidate;
        await WaitUntilAsync(() => viewModel.SelectedRecord is not null);

        viewModel.ActivateValueCommand.Execute(viewModel.DisplayValues[1]);
        viewModel.CopyCommand.Execute(null);
        await WaitUntilAsync(() => clipboard.Writes.Count == 1);
        viewModel.CopyValueCommand.Execute(viewModel.DisplayValues[1]);
        await WaitUntilAsync(() => clipboard.Writes.Count == 2);

        Assert.Equal(["second", "second"], clipboard.Writes);
    }

    private static MainWindowViewModel CreateViewModel(
        FakeScrapClient client,
        UserPreferenceStore store,
        Action<AppTheme>? applyTheme = null,
        FakeClipboardService? clipboard = null) =>
        new(
            client,
            clipboard ?? new FakeClipboardService(),
            debounce: TimeSpan.Zero,
            preferenceStore: store,
            applyTheme: applyTheme);

    private static LocalizedChoice<T> Choice<T>(IEnumerable<LocalizedChoice<T>> choices, T value)
        where T : struct, Enum => Assert.Single(choices, choice => EqualityComparer<T>.Default.Equals(choice.Value, value));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The expected asynchronous GUI state was not reached within five seconds.");
            }

            await Task.Delay(10);
        }
    }
}

/// <summary>
/// 为状态机测试记录协议调用并返回确定性结果。
/// Records protocol calls and returns deterministic results for state-machine tests.
/// </summary>
internal sealed class FakeScrapClient : IScrapClient
{
    private readonly object _gate = new();
    private readonly List<RecordSearchRequest> _searchRequests = [];
    private readonly List<(string Scope, string Key)> _getRecordCalls = [];
    private readonly List<SaveRecordRequest> _saveRequests = [];
    private int _listScopesCalls;

    /// <summary>列举结果。Scope-list result.</summary>
    internal IReadOnlyList<ScopeSummary> Scopes { get; init; } = [];

    /// <summary>搜索结果。Search result.</summary>
    internal IReadOnlyList<RecordCandidate> SearchResults { get; init; } = [];

    /// <summary>详情结果。Record-detail result.</summary>
    internal RecordDetails? Record { get; init; }

    /// <summary>此调用次数之后让 scope 列举失败；默认永不失败。Fail scope listing after this many calls; never fail by default.</summary>
    internal int FailListScopesAfter { get; init; } = int.MaxValue;

    /// <summary>线程安全的搜索请求快照。Thread-safe snapshot of search requests.</summary>
    internal IReadOnlyList<RecordSearchRequest> SearchRequests
    {
        get
        {
            lock (_gate)
            {
                return [.. _searchRequests];
            }
        }
    }

    /// <summary>线程安全的详情调用快照。Thread-safe snapshot of detail calls.</summary>
    internal IReadOnlyList<(string Scope, string Key)> GetRecordCalls
    {
        get
        {
            lock (_gate)
            {
                return [.. _getRecordCalls];
            }
        }
    }

    /// <summary>线程安全的保存请求快照。Thread-safe snapshot of save requests.</summary>
    internal IReadOnlyList<SaveRecordRequest> SaveRequests
    {
        get
        {
            lock (_gate)
            {
                return [.. _saveRequests];
            }
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ScopeSummary>> ListScopesAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _listScopesCalls) > FailListScopesAfter)
        {
            return Task.FromException<IReadOnlyList<ScopeSummary>>(new ScrapClientException(
                ScrapClientErrorKind.DaemonUnavailable,
                "Injected scope refresh failure."));
        }

        return Task.FromResult(Scopes);
    }

    /// <inheritdoc />
    public Task CreateScopeAsync(string name, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task RenameScopeAsync(string oldName, string newName, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task DeleteScopeAsync(string name, bool recursive, int? expectedRecordCount, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task<IReadOnlyList<RecordCandidate>> SearchAsync(RecordSearchRequest request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _searchRequests.Add(request);
        }

        return Task.FromResult(SearchResults);
    }

    /// <inheritdoc />
    public Task<RecordDetails> GetRecordAsync(string scope, string key, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _getRecordCalls.Add((scope, key));
        }

        return Task.FromResult(Record ?? throw new InvalidOperationException("No record result was configured."));
    }

    /// <inheritdoc />
    public Task SaveRecordAsync(SaveRecordRequest request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _saveRequests.Add(request);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteRecordAsync(string scope, string key, long? expectedRevision, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// 提供无需操作系统剪贴板的测试边界。
/// Provides a test boundary without touching the operating-system clipboard.
/// </summary>
internal sealed class FakeClipboardService : IClipboardService
{
    private readonly List<string> _writes = [];

    /// <summary>剪贴板写入快照。Clipboard write snapshot.</summary>
    internal IReadOnlyList<string> Writes
    {
        get
        {
            lock (_writes)
            {
                return [.. _writes];
            }
        }
    }

    /// <inheritdoc />
    public Task SetTextAsync(string text, CancellationToken cancellationToken)
    {
        lock (_writes)
        {
            _writes.Add(text);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<string?> GetTextAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    /// <inheritdoc />
    public Task ClearAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// 在仓库的 .temp 目录下管理单个测试的隔离文件。
/// Manages isolated per-test files under the repository's .temp directory.
/// </summary>
internal sealed class TestDirectory : IDisposable
{
    private TestDirectory(string path)
    {
        Path = path;
        Directory.CreateDirectory(path);
    }

    /// <summary>测试目录绝对路径。Absolute test-directory path.</summary>
    internal string Path { get; }

    /// <summary>创建隔离目录。Creates an isolated directory.</summary>
    internal static TestDirectory Create()
    {
        string? root = FindRepositoryRoot(AppContext.BaseDirectory);
        if (root is null)
        {
            throw new DirectoryNotFoundException("Could not locate Scrap.slnx from the test output directory.");
        }

        return new TestDirectory(System.IO.Path.Combine(root, ".temp", "gui-tests", Guid.NewGuid().ToString("N")));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }

    private static string? FindRepositoryRoot(string start)
    {
        for (DirectoryInfo? directory = new(start); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(directory.FullName, "Scrap.slnx")))
            {
                return directory.FullName;
            }
        }

        return null;
    }
}
