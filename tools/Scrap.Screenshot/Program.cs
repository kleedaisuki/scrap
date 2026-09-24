using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Skia;
using Avalonia.Threading;
using Scrap.Gui;
using Scrap.Gui.Abstractions;
using Scrap.Gui.Models;
using Scrap.Gui.Services;
using Scrap.Gui.ViewModels;

namespace Scrap.Screenshot;

/// <summary>
/// 在 Avalonia 的无头平台上渲染真实主窗口的发布截图。
/// Renders release screenshots of the real main window on Avalonia's headless platform.
/// </summary>
internal static class Program
{
    private const int Width = 1366;
    private const int Height = 768;

    /// <summary>启动确定性截图任务。Runs the deterministic screenshot job.</summary>
    /// <param name="args">唯一参数为输出目录。The sole argument is the output directory.</param>
    /// <returns>成功时返回零。Zero on success.</returns>
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Usage: Scrap.Screenshot <output-directory>");
            return 2;
        }

        string outputDirectory = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(outputDirectory);
        string preferenceDirectory = Path.Combine(
            outputDirectory,
            $".scrap-screenshot-{Environment.ProcessId}-{Guid.NewGuid():N}");
        if (Directory.Exists(preferenceDirectory))
        {
            throw new IOException("The uniquely named screenshot preference directory already exists.");
        }

        Directory.CreateDirectory(preferenceDirectory);

        try
        {
            AppBuilder.Configure<App>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions
                {
                    UseHeadlessDrawing = false,
                })
                .UseSkia()
                .WithInterFont()
                .SetupWithoutStarting();

            CaptureLanguage(outputDirectory, preferenceDirectory, "zh-CN", AppLanguage.SimplifiedChinese, AppTheme.Light);
            CaptureLanguage(outputDirectory, preferenceDirectory, "en-US", AppLanguage.English, AppTheme.Dark);
            return 0;
        }
        finally
        {
            if (Directory.Exists(preferenceDirectory))
            {
                Directory.Delete(preferenceDirectory, recursive: true);
            }
        }
    }

    /// <summary>为一种语言与主题生成约定的四张截图。Produces the four contracted captures for one language and theme.</summary>
    private static void CaptureLanguage(
        string outputDirectory,
        string preferenceDirectory,
        string directoryName,
        AppLanguage language,
        AppTheme theme)
    {
        string languageDirectory = Path.Combine(outputDirectory, directoryName);
        Directory.CreateDirectory(languageDirectory);
        Capture(languageDirectory, preferenceDirectory, "01-all-scopes-masked.png", language, theme, CaptureScene.AllScopesMasked);
        Capture(languageDirectory, preferenceDirectory, "02-multi-scope-subset.png", language, theme, CaptureScene.MultiScopeSubset);
        Capture(languageDirectory, preferenceDirectory, "03-create-record.png", language, theme, CaptureScene.CreateRecord);
        Capture(languageDirectory, preferenceDirectory, "04-temporary-reveal.png", language, theme, CaptureScene.TemporaryReveal);
    }

    /// <summary>为单个状态使用独立偏好并在失败时关闭窗口。Uses isolated preferences for one scene and closes its window on failure.</summary>
    private static void Capture(
        string outputDirectory,
        string preferenceDirectory,
        string fileName,
        AppLanguage language,
        AppTheme theme,
        CaptureScene scene)
    {
        string preferencePath = Path.Combine(
            preferenceDirectory,
            $"{language}-{Path.GetFileNameWithoutExtension(fileName)}.json");
        var preferenceStore = new UserPreferenceStore(preferencePath);
        preferenceStore.Save(new UserPreferences(theme, language));

        var window = new MainWindow(new ShowcaseScrapClient(), new NoOpClipboardService(), preferenceStore)
        {
            Width = Width,
            Height = Height,
            CanResize = false,
            WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.Manual,
            Position = new Avalonia.PixelPoint(0, 0),
        };

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var viewModel = (MainWindowViewModel)window.DataContext!;
            ComposeScene(window, viewModel, scene);
            Dispatcher.UIThread.RunJobs();

            if (scene != CaptureScene.CreateRecord && viewModel.SelectedRecord is null)
            {
                throw new InvalidOperationException("The selected showcase record did not load before capture.");
            }

            using var frame = window.CaptureRenderedFrame()
                ?? throw new InvalidOperationException("Avalonia did not produce a rendered frame.");
            if (frame.PixelSize != new PixelSize(Width, Height))
            {
                throw new InvalidOperationException($"Expected {Width}x{Height}, got {frame.PixelSize.Width}x{frame.PixelSize.Height}.");
            }

            frame.Save(Path.Combine(outputDirectory, fileName), PngBitmapEncoderOptions.Default);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>通过真实视图模型操作配置截图状态。Configures capture states through real view-model interactions.</summary>
    private static void ComposeScene(MainWindow window, MainWindowViewModel viewModel, CaptureScene scene)
    {
        switch (scene)
        {
            case CaptureScene.AllScopesMasked:
                SelectRecord(viewModel, "registry-token");
                break;
            case CaptureScene.MultiScopeSubset:
                SetSearchScope(viewModel, "staging", selected: true);
                SetSearchScope(viewModel, "production", selected: true);
                SelectRecord(viewModel, "service-url");
                Button scopeButton = window.FindControl<Button>("SearchScopeButton")
                    ?? throw new InvalidOperationException("The search-scope button was not found.");
                scopeButton.Flyout?.ShowAt(scopeButton);
                break;
            case CaptureScene.CreateRecord:
                Execute(viewModel.OpenCreateRecordCommand);
                viewModel.EditorKey = "deploy-note";
                viewModel.EditorValue = viewModel.SelectedLanguage?.Value == AppLanguage.SimplifiedChinese
                    ? "发布窗口结束后轮换。"
                    : "Rotate after the release window.";
                viewModel.EditorValueVisible = true;
                viewModel.EditorIsMasked = true;
                break;
            case CaptureScene.TemporaryReveal:
                SelectRecord(viewModel, "registry-token");
                Execute(viewModel.ToggleRevealCommand);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scene), scene, "Unknown capture scene.");
        }
    }

    /// <summary>选择唯一的展示记录，并处理待执行的 UI 作业。Selects the unique showcase record and drains pending UI jobs.</summary>
    private static void SelectRecord(MainWindowViewModel viewModel, string key)
    {
        viewModel.SelectedCandidate = viewModel.Candidates.Single(candidate => candidate.Key == key);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>更改一个搜索 scope 的选择状态。Changes one search-scope selection.</summary>
    private static void SetSearchScope(MainWindowViewModel viewModel, string name, bool selected)
    {
        viewModel.SearchScopeChoices.Single(choice => choice.Name == name).IsSelected = selected;
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>执行应在展示场景中可用的命令。Executes a command expected to be available in the showcase scene.</summary>
    private static void Execute(System.Windows.Input.ICommand command)
    {
        if (!command.CanExecute(null))
        {
            throw new InvalidOperationException("A showcase command was unexpectedly disabled.");
        }

        command.Execute(null);
    }
}

/// <summary>每张发布截图要展示的真实交互状态。Real interaction state presented by each release screenshot.</summary>
internal enum CaptureScene
{
    /// <summary>全 scope 搜索与默认遮罩。All-scope search with default masking.</summary>
    AllScopesMasked,

    /// <summary>任意两 scope 子集。An arbitrary two-scope subset.</summary>
    MultiScopeSubset,

    /// <summary>新建记录表单的正交可见性选项。Orthogonal visibility choices in the create-record form.</summary>
    CreateRecord,

    /// <summary>已遮罩值的短暂显示状态。Temporary reveal state for a masked value.</summary>
    TemporaryReveal,
}

/// <summary>
/// 提供无秘密、无 I/O 的展示数据，同时保留生产 client 的语义契约。
/// Supplies secret-free, I/O-free showcase data while preserving the production client contract.
/// </summary>
internal sealed class ShowcaseScrapClient : IScrapClient
{
    private static readonly ScopeSummary[] Scopes =
    [
        new("personal", 12),
        new("staging", 8),
        new("production", 6),
    ];

    private static readonly RecordDetails[] Records =
    [
        new("production", "deploy", "pnpm release --filter @moesegfault/scrap", RecordPresentation.Plain, LocalTime(2026, 9, 14, 8, 30), 17),
        new("production", "service-url", "https://scrap.moesegfault.dev", RecordPresentation.Plain, LocalTime(2026, 9, 13, 16, 20), 9),
        new("staging", "publish", "pnpm build && pnpm preview", RecordPresentation.Plain, LocalTime(2026, 9, 12, 9, 15), 11),
        new("staging", "registry-token", "showcase-not-a-real-token", RecordPresentation.Masked, LocalTime(2026, 9, 11, 5, 45), 5),
        new("personal", "ssh-config", "Host scrap-dev\n  HostName dev.local\n  User klee", RecordPresentation.Plain, LocalTime(2026, 9, 10, 14, 10), 3),
    ];

    /// <summary>创建展示记录的本地时区时间戳。Creates a local-time timestamp for a showcase record.</summary>
    private static DateTimeOffset LocalTime(int year, int month, int day, int hour, int minute)
    {
        var wallClock = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(wallClock, TimeZoneInfo.Local.GetUtcOffset(wallClock));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ScopeSummary>> ListScopesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ScopeSummary>>(Scopes);

    /// <inheritdoc />
    public Task<IReadOnlyList<RecordCandidate>> SearchAsync(RecordSearchRequest request, CancellationToken cancellationToken)
    {
        IEnumerable<RecordDetails> records = Records;
        if (request.Scopes.Count > 0)
        {
            records = records.Where(record => request.Scopes.Contains(record.Scope, StringComparer.Ordinal));
        }

        if (request.Query.Length > 0)
        {
            StringComparison comparison = request.CaseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;
            records = records.Where(record => record.Key.Contains(request.Query, comparison));
        }

        IReadOnlyList<RecordCandidate> result = records
            .Take(request.Limit)
            .Select(record => new RecordCandidate(record.Scope, record.Key, record.Presentation))
            .ToArray();
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<RecordDetails> GetRecordAsync(string scope, string key, CancellationToken cancellationToken) =>
        Task.FromResult(Records.Single(record => record.Scope == scope && record.Key == key));

    /// <inheritdoc />
    public Task CreateScopeAsync(string name, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task RenameScopeAsync(string oldName, string newName, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task DeleteScopeAsync(string name, bool recursive, int? expectedRecordCount, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task SaveRecordAsync(SaveRecordRequest request, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task DeleteRecordAsync(string scope, string key, long? expectedRevision, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>确保截图永不读写操作系统剪贴板。Ensures captures never read or write the operating-system clipboard.</summary>
internal sealed class NoOpClipboardService : IClipboardService
{
    /// <inheritdoc />
    public Task SetTextAsync(string text, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task<string?> GetTextAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    /// <inheritdoc />
    public Task ClearAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
