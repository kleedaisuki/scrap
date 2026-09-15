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
        Assert.Equal("All scopes", Choice(viewModel.SearchCoverageChoices, SearchCoverage.AllScopes).Label);

        viewModel.SelectedLanguage = Choice(viewModel.LanguageChoices, AppLanguage.SimplifiedChinese);

        Assert.Equal("空间", viewModel.L.Scope);
        Assert.Equal("检索范围", viewModel.L.SearchIn);
        Assert.Equal("所有空间", Choice(viewModel.SearchCoverageChoices, SearchCoverage.AllScopes).Label);
        Assert.Equal("浅色", Choice(viewModel.ThemeChoices, AppTheme.Light).Label);
        Assert.Equal(AppLanguage.SimplifiedChinese, store.Load().Language);

        viewModel.SelectedLanguage = Choice(viewModel.LanguageChoices, AppLanguage.English);

        Assert.Equal("Scope", viewModel.L.Scope);
        Assert.Equal("Search in", viewModel.L.SearchIn);
        Assert.Equal("All scopes", Choice(viewModel.SearchCoverageChoices, SearchCoverage.AllScopes).Label);
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
        Assert.Equal(SearchCoverage.AllScopes, viewModel.SelectedSearchCoverage?.Value);

        viewModel.SelectedCandidate = candidate;
        await WaitUntilAsync(() => viewModel.SelectedRecord is not null);

        Assert.Equal((candidate.Scope, candidate.Key), Assert.Single(client.GetRecordCalls));
        Assert.Equal(candidate.Scope, viewModel.SelectedRecord!.Scope);
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

    private static MainWindowViewModel CreateViewModel(
        FakeScrapClient client,
        UserPreferenceStore store,
        Action<AppTheme>? applyTheme = null) =>
        new(
            client,
            new FakeClipboardService(),
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

    /// <summary>列举结果。Scope-list result.</summary>
    internal IReadOnlyList<ScopeSummary> Scopes { get; init; } = [];

    /// <summary>搜索结果。Search result.</summary>
    internal IReadOnlyList<RecordCandidate> SearchResults { get; init; } = [];

    /// <summary>详情结果。Record-detail result.</summary>
    internal RecordDetails? Record { get; init; }

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

    /// <inheritdoc />
    public Task<IReadOnlyList<ScopeSummary>> ListScopesAsync(CancellationToken cancellationToken) => Task.FromResult(Scopes);

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
    public Task SaveRecordAsync(SaveRecordRequest request, CancellationToken cancellationToken) => Task.CompletedTask;

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
    /// <inheritdoc />
    public Task SetTextAsync(string text, CancellationToken cancellationToken) => Task.CompletedTask;

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
