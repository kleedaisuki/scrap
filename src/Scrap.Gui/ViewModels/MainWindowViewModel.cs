using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Input;
using Scrap.Gui.Abstractions;
using Scrap.Gui.Infrastructure;
using Scrap.Gui.Models;
using Scrap.Gui.Services;

namespace Scrap.Gui.ViewModels;

/// <summary>
/// 主窗口的交互状态机；协议、剪贴板和计时均通过可替换边界注入。
/// Interaction state machine for the main window; protocol, clipboard, and timing enter through replaceable boundaries.
/// </summary>
public sealed class MainWindowViewModel : ViewModelBase, IAsyncDisposable
{
    /// <summary>默认搜索候选上限。Default search candidate limit.</summary>
    public const int DefaultSearchLimit = 200;
    private const int MaxRecordValues = 32;
    private const int MaxRecordValueBytes = 64 * 1024;

    private static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan DefaultRevealDuration = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DefaultClipboardDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultMutationDeadline = TimeSpan.FromSeconds(60);

    private readonly IScrapClient _client;
    private readonly IClipboardService _clipboard;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _debounce;
    private readonly TimeSpan _revealDuration;
    private readonly TimeSpan _clipboardDuration;
    private readonly TimeSpan _mutationDeadline;
    private readonly UserPreferenceStore _preferenceStore;
    private readonly Action<AppTheme> _applyTheme;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _mutationLifetimeGate = new(1, 1);
    private readonly SemaphoreSlim _clipboardLifetimeGate = new(1, 1);
    private CancellationTokenSource? _searchCancellation;
    private CancellationTokenSource? _detailCancellation;
    private readonly Dictionary<int, CancellationTokenSource> _revealCancellations = [];
    private readonly HashSet<int> _revealedValueIndexes = [];
    private CancellationTokenSource? _clipboardCancellation;
    private CancellationTokenSource? _toastCancellation;
    private ScopeSummary? _selectedScope;
    private RecordCandidate? _selectedCandidate;
    private RecordDetails? _selectedRecord;
    private string _searchText = string.Empty;
    private SearchMode _selectedMode = SearchMode.Fuzzy;
    private bool _caseSensitive;
    private readonly HashSet<string> _selectedSearchScopes = new(StringComparer.Ordinal);
    private bool _synchronizingSearchScopes;
    private AppTheme _selectedTheme;
    private AppLanguage _selectedLanguage;
    private LocalizationStrings _localization;
    private bool _isSearching;
    private bool _isLoadingScopes;
    private bool _isLoadingRecord;
    private bool _isRevealed;
    private int _activeValueIndex;
    private string? _searchError;
    private string? _errorTitle;
    private string? _errorMessage;
    private string? _toastMessage;
    private bool _isScopeEditorOpen;
    private string _scopeEditorTitle = string.Empty;
    private string _scopeNameInput = string.Empty;
    private bool _isRenamingScope;
    private string? _scopeEditorOriginalName;
    private ScopeSummary? _scopeDeleteTarget;
    private bool _isScopeDeleteOpen;
    private bool _isRecordEditorOpen;
    private bool _isEditingRecord;
    private string? _recordEditorScope;
    private RecordDetails? _recordEditorOriginal;
    private string _editorKey = string.Empty;
    private bool _editorIsMasked = true;
    private bool _editorValueVisible;
    private bool _isRecordDeleteOpen;
    private RecordDetails? _recordDeleteTarget;
    private bool _isBusy;
    private long _searchRevision;
    private long _detailRevision;
    private int _shutdownRequested;
    private int _clientDisposed;
    private long _clipboardGeneration;
    private string? _ownedMaskedClipboardValue;
    private RecordValueEditor? _requestedEditorValueFocus;

    /// <summary>
    /// 创建主窗口状态机。Creates the main-window state machine.
    /// </summary>
    /// <param name="client">与 daemon 通信的抽象 client。Abstract client communicating with the daemon.</param>
    /// <param name="clipboard">可测试剪贴板服务。Testable clipboard service.</param>
    /// <param name="timeProvider">用于 debounce 与短暂状态的时钟。Clock for debounce and temporary states.</param>
    /// <param name="debounce">搜索 debounce；测试可注入零时长。Search debounce; tests may inject zero.</param>
    /// <param name="revealDuration">遮罩值显示时长。Duration for revealing a masked value.</param>
    /// <param name="clipboardDuration">遮罩值的剪贴板保留时长。Clipboard lifetime for a masked value.</param>
    /// <param name="mutationDeadline">单次 mutation 的最长等待时间；关闭窗口不会缩短它。Maximum wait for one mutation; closing the window does not shorten it.</param>
    public MainWindowViewModel(
        IScrapClient client,
        IClipboardService clipboard,
        TimeProvider? timeProvider = null,
        TimeSpan? debounce = null,
        TimeSpan? revealDuration = null,
        TimeSpan? clipboardDuration = null,
        TimeSpan? mutationDeadline = null,
        UserPreferenceStore? preferenceStore = null,
        Action<AppTheme>? applyTheme = null)
    {
        _client = client;
        _clipboard = clipboard;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _debounce = debounce ?? DefaultDebounce;
        _revealDuration = revealDuration ?? DefaultRevealDuration;
        _clipboardDuration = clipboardDuration ?? DefaultClipboardDuration;
        _mutationDeadline = mutationDeadline ?? DefaultMutationDeadline;
        _preferenceStore = preferenceStore ?? new UserPreferenceStore();
        _applyTheme = applyTheme ?? (_ => { });
        UserPreferences preferences = _preferenceStore.Load();
        _selectedTheme = preferences.Theme;
        _selectedLanguage = preferences.Language;
        _localization = LocalizationStrings.For(_selectedLanguage);
        RebuildLocalizedChoices();
        _applyTheme(_selectedTheme);

        RetryCommand = new AsyncRelayCommand(
            RetryRefreshAsync,
            () => !IsLoadingScopes && !IsBusy);
        OpenCreateScopeCommand = new RelayCommand(OpenCreateScope, () => !IsBusy && !IsLoadingScopes && !HasOpenModal);
        OpenRenameScopeCommand = new RelayCommand(OpenRenameScope, () => !IsBusy && !HasOpenModal && SelectedScope is not null);
        OpenDeleteScopeCommand = new AsyncRelayCommand(OpenDeleteScopeAsync, () => !IsBusy && !HasOpenModal && SelectedScope is not null);
        SaveScopeCommand = new AsyncRelayCommand(SaveScopeAsync, CanSaveScope);
        ConfirmDeleteScopeCommand = new AsyncRelayCommand(DeleteScopeAsync, () => !IsBusy && SelectedScope is not null);
        OpenCreateRecordCommand = new RelayCommand(OpenCreateRecord, () => !IsBusy && !HasOpenModal && SelectedScope is not null);
        OpenEditRecordCommand = new RelayCommand(OpenEditRecord, () => !IsBusy && !HasOpenModal && SelectedRecord is not null);
        OpenDeleteRecordCommand = new RelayCommand(OpenDeleteRecord, () => !IsBusy && !HasOpenModal && SelectedRecord is not null);
        SaveRecordCommand = new AsyncRelayCommand(SaveRecordAsync, CanSaveRecord);
        ConfirmDeleteRecordCommand = new AsyncRelayCommand(DeleteRecordAsync, () => !IsBusy && SelectedRecord is not null);
        CopyCommand = new AsyncRelayCommand(CopyAsync, () => !IsBusy && SelectedRecord is not null);
        CopyValueCommand = new AsyncRelayCommand(
            parameter => CopyDisplayValueAsync(parameter as RecordValueDisplay),
            parameter => !IsBusy && SelectedRecord is not null && parameter is RecordValueDisplay);
        ActivateValueCommand = new RelayCommand(
            parameter => ActivateValue(parameter as RecordValueDisplay),
            parameter => SelectedRecord is not null && parameter is RecordValueDisplay);
        AddEditorValueCommand = new RelayCommand(
            AddEditorValue,
            () => !IsBusy && IsRecordEditorOpen && EditorValues.Count < MaxRecordValues);
        RemoveEditorValueCommand = new RelayCommand(
            parameter => RemoveEditorValue(parameter as RecordValueEditor),
            parameter => !IsBusy && IsRecordEditorOpen && EditorValues.Count > 1 && parameter is RecordValueEditor);
        MoveEditorValueUpCommand = new RelayCommand(
            parameter => MoveEditorValue(parameter as RecordValueEditor, -1),
            parameter => CanMoveEditorValue(parameter as RecordValueEditor, -1));
        MoveEditorValueDownCommand = new RelayCommand(
            parameter => MoveEditorValue(parameter as RecordValueEditor, 1),
            parameter => CanMoveEditorValue(parameter as RecordValueEditor, 1));
        ToggleRevealCommand = new RelayCommand(ToggleReveal, () => !IsBusy && SelectedRecord?.Presentation == RecordPresentation.Masked);
        ToggleRevealValueCommand = new RelayCommand(
            parameter => ToggleRevealValue(parameter as RecordValueDisplay),
            parameter => !IsBusy && SelectedRecord?.Presentation == RecordPresentation.Masked && parameter is RecordValueDisplay);
        HideAllRevealedValuesCommand = new RelayCommand(CancelReveal, () => !IsBusy && IsRevealed);
        CloseModalCommand = new RelayCommand(CloseModals, () => !IsBusy && HasOpenModal);
        DismissErrorCommand = new RelayCommand(ClearError);
    }

    /// <summary>可用 scope。Available scopes.</summary>
    public ObservableCollection<ScopeSummary> Scopes { get; } = [];

    /// <summary>由 daemon 排序的 key 候选。Key candidates ranked by the daemon.</summary>
    public ObservableCollection<RecordCandidate> Candidates { get; } = [];

    /// <summary>编辑器中的非空有序 value 集合。Non-empty ordered value collection in the editor.</summary>
    public ObservableCollection<RecordValueEditor> EditorValues { get; } = [];

    /// <summary>请求视图聚焦的新建 value 编辑项。New value editor item that the view should focus.</summary>
    public RecordValueEditor? RequestedEditorValueFocus
    {
        get => _requestedEditorValueFocus;
        private set => SetProperty(ref _requestedEditorValueFocus, value);
    }

    /// <summary>可独立多选的检索 scope；空选择在协议边界表示所有 scope。Independently selectable search scopes; an empty selection means all scopes at the protocol boundary.</summary>
    public ObservableCollection<SearchScopeChoice> SearchScopeChoices { get; } = [];

    /// <summary>本地化的主题选项。Localized theme choices.</summary>
    public ObservableCollection<LocalizedChoice<AppTheme>> ThemeChoices { get; } = [];

    /// <summary>本地化的语言选项。Localized language choices.</summary>
    public ObservableCollection<LocalizedChoice<AppLanguage>> LanguageChoices { get; } = [];

    /// <summary>当前语言的完整文案。Complete copy for the active language.</summary>
    public LocalizationStrings L => _localization;

    /// <summary>是否搜索所有 scope。Whether every scope is searched.</summary>
    public bool IsAllSearchScopesSelected
    {
        get => _selectedSearchScopes.Count == 0;
        set
        {
            if (!value || _selectedSearchScopes.Count == 0)
            {
                return;
            }

            SetSearchScopesToAll();
        }
    }

    /// <summary>用于折叠选择器的本地化检索范围摘要。Localized search-scope summary for the collapsed picker.</summary>
    public string SearchScopeSummary => _selectedSearchScopes.Count switch
    {
        0 => L.AllScopes,
        1 => _selectedSearchScopes.Single(),
        int count => L.SelectedScopes(count),
    };

    /// <summary>包含当前选择摘要的无障碍名称。Accessible name including the current selection summary.</summary>
    public string SearchScopeAccessibleName => L.SearchScopeAccessibleName(SearchScopeSummary);

    /// <summary>持久化的主题选择。Persisted theme selection.</summary>
    public LocalizedChoice<AppTheme>? SelectedTheme
    {
        get => ThemeChoices.FirstOrDefault(choice => choice.Value == _selectedTheme);
        set
        {
            if (value is null || value.Value == _selectedTheme)
            {
                return;
            }

            _selectedTheme = value.Value;
            OnPropertyChanged();
            _applyTheme(_selectedTheme);
            PersistPreferences();
        }
    }

    /// <summary>持久化的界面语言。Persisted user-interface language.</summary>
    public LocalizedChoice<AppLanguage>? SelectedLanguage
    {
        get => LanguageChoices.FirstOrDefault(choice => choice.Value == _selectedLanguage);
        set
        {
            if (value is null || value.Value == _selectedLanguage)
            {
                return;
            }

            _selectedLanguage = value.Value;
            _localization = LocalizationStrings.For(_selectedLanguage);
            RebuildLocalizedChoices();
            NotifyLocalizedProperties();
            PersistPreferences();
        }
    }

    /// <summary>记录管理和新建记录的目标 scope；它不改变独立的检索 scope 集合。Target scope for record management and creation; it does not alter the independent search-scope set.</summary>
    public ScopeSummary? SelectedScope
    {
        get => _selectedScope;
        set
        {
            if (!SetProperty(ref _selectedScope, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasSelectedScope));
            OnPropertyChanged(nameof(ScopeStatus));
            NotifyCommandStates();
        }
    }

    /// <summary>当前候选。Current candidate.</summary>
    public RecordCandidate? SelectedCandidate
    {
        get => _selectedCandidate;
        set
        {
            if (!SetProperty(ref _selectedCandidate, value))
            {
                return;
            }

            _ = LoadSelectedRecordAsync();
        }
    }

    /// <summary>用户明确选择后加载的记录。Record loaded after explicit selection.</summary>
    public RecordDetails? SelectedRecord
    {
        get => _selectedRecord;
        private set
        {
            if (!SetProperty(ref _selectedRecord, value))
            {
                return;
            }

            CancelReveal();
            _activeValueIndex = 0;
            OnPropertyChanged(nameof(HasSelectedRecord));
            OnPropertyChanged(nameof(HasNoSelectedRecord));
            OnPropertyChanged(nameof(SelectedIsMasked));
            OnPropertyChanged(nameof(DisplayValues));
            OnPropertyChanged(nameof(RevealButtonText));
            OnPropertyChanged(nameof(RecordIdentity));
            OnPropertyChanged(nameof(UpdatedText));
            NotifyCommandStates();
        }
    }

    /// <summary>查询文本；保留空文本以允许列出 scope 内容。Query text; empty text is preserved to allow listing a scope.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                _ = ScheduleSearchAsync(immediate: false);
            }
        }
    }

    /// <summary>搜索模式，默认为 fuzzy。Search mode, fuzzy by default.</summary>
    public SearchMode SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (SetProperty(ref _selectedMode, value))
            {
                OnPropertyChanged(nameof(IsExactMode));
                OnPropertyChanged(nameof(IsFuzzyMode));
                OnPropertyChanged(nameof(IsRegexMode));
                _ = ScheduleSearchAsync(immediate: true);
            }
        }
    }

    /// <summary>exact 模式是否选中。Whether exact mode is selected.</summary>
    public bool IsExactMode
    {
        get => SelectedMode == SearchMode.Exact;
        set
        {
            if (value)
            {
                SelectedMode = SearchMode.Exact;
            }
        }
    }

    /// <summary>fuzzy 模式是否选中。Whether fuzzy mode is selected.</summary>
    public bool IsFuzzyMode
    {
        get => SelectedMode == SearchMode.Fuzzy;
        set
        {
            if (value)
            {
                SelectedMode = SearchMode.Fuzzy;
            }
        }
    }

    /// <summary>regex 模式是否选中。Whether regex mode is selected.</summary>
    public bool IsRegexMode
    {
        get => SelectedMode == SearchMode.Regex;
        set
        {
            if (value)
            {
                SelectedMode = SearchMode.Regex;
            }
        }
    }

    /// <summary>大小写敏感匹配修饰符。Case-sensitive matching modifier.</summary>
    public bool CaseSensitive
    {
        get => _caseSensitive;
        set
        {
            if (SetProperty(ref _caseSensitive, value))
            {
                _ = ScheduleSearchAsync(immediate: true);
            }
        }
    }

    /// <summary>是否正在搜索。Whether a search is running.</summary>
    public bool IsSearching
    {
        get => _isSearching;
        private set
        {
            if (SetProperty(ref _isSearching, value))
            {
                OnPropertyChanged(nameof(HasNoCandidates));
            }
        }
    }

    /// <summary>是否正在加载 scope。Whether scopes are loading.</summary>
    public bool IsLoadingScopes
    {
        get => _isLoadingScopes;
        private set
        {
            if (SetProperty(ref _isLoadingScopes, value))
            {
                OnPropertyChanged(nameof(HasNoScopes));
                NotifyCommandStates();
            }
        }
    }

    /// <summary>是否正在加载记录。Whether a record is loading.</summary>
    public bool IsLoadingRecord
    {
        get => _isLoadingRecord;
        private set
        {
            if (SetProperty(ref _isLoadingRecord, value))
            {
                OnPropertyChanged(nameof(HasNoSelectedRecord));
            }
        }
    }

    /// <summary>是否没有任何 scope。Whether there are no scopes.</summary>
    public bool HasNoScopes => !IsLoadingScopes && Scopes.Count == 0 && !HasError;

    /// <summary>是否有已选 scope。Whether a scope is selected.</summary>
    public bool HasSelectedScope => SelectedScope is not null;

    /// <summary>是否有已选记录。Whether a record is selected.</summary>
    public bool HasSelectedRecord => SelectedRecord is not null;

    /// <summary>是否尚未选中记录。Whether no record is selected yet.</summary>
    public bool HasNoSelectedRecord => SelectedRecord is null && !IsLoadingRecord;

    /// <summary>选中记录是否采用 masked 呈现。Whether the selected record uses masked presentation.</summary>
    public bool SelectedIsMasked => SelectedRecord?.Presentation == RecordPresentation.Masked;

    /// <summary>当前搜索是否没有候选。Whether the current search has no candidates.</summary>
    public bool HasNoCandidates => HasSelectedScope && !IsSearching && Candidates.Count == 0 && SearchError is null;

    /// <summary>scope 选择器辅助文本。Scope selector helper text.</summary>
    public string ScopeStatus => SelectedScope is null
        ? L.ChooseScope
        : L.RecordCount(SelectedScope.RecordCount);

    /// <summary>搜索表达式附近的结构化错误。Structured error shown beside the search expression.</summary>
    public string? SearchError
    {
        get => _searchError;
        private set => SetProperty(ref _searchError, value);
    }

    /// <summary>是否有阻止正常工作的错误。Whether an error is blocking normal work.</summary>
    public bool HasError => ErrorMessage is not null;

    /// <summary>错误标题。Error title.</summary>
    public string? ErrorTitle
    {
        get => _errorTitle;
        private set => SetProperty(ref _errorTitle, value);
    }

    /// <summary>不包含 value 的可操作错误详情。Actionable error details containing no value.</summary>
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
                OnPropertyChanged(nameof(HasNoScopes));
            }
        }
    }

    /// <summary>非阻塞轻量反馈。Non-blocking lightweight feedback.</summary>
    public string? ToastMessage
    {
        get => _toastMessage;
        private set
        {
            if (SetProperty(ref _toastMessage, value))
            {
                OnPropertyChanged(nameof(HasToast));
            }
        }
    }

    /// <summary>是否显示反馈条。Whether the feedback toast is shown.</summary>
    public bool HasToast => ToastMessage is not null;

    /// <summary>详情区的逐值显示投影；绝不隐式拼接。Per-value detail projections; values are never implicitly concatenated.</summary>
    public IReadOnlyList<RecordValueDisplay> DisplayValues => SelectedRecord is null
        ? []
        : SelectedRecord.Values.Select((value, index) => new RecordValueDisplay(
            index + 1,
            SelectedRecord.Presentation == RecordPresentation.Plain || _revealedValueIndexes.Contains(index)
                ? value
                : "••••••••••••",
            value,
            SelectedRecord.Presentation == RecordPresentation.Masked,
            _revealedValueIndexes.Contains(index) ? L.Hide : L.Reveal)).ToArray();

    /// <summary>遮罩值当前是否已明确显示。Whether a masked value is currently explicitly revealed.</summary>
    public bool IsRevealed
    {
        get => _isRevealed;
        private set
        {
            if (SetProperty(ref _isRevealed, value))
            {
                OnPropertyChanged(nameof(DisplayValues));
                OnPropertyChanged(nameof(RevealButtonText));
            }
        }
    }

    /// <summary>显示/隐藏按钮文字。Reveal/hide button text.</summary>
    public string RevealButtonText => IsRevealed ? L.Hide : L.Reveal;

    /// <summary>准确的选中记录身份。Exact identity of the selected record.</summary>
    public string RecordIdentity => SelectedRecord is null
        ? string.Empty
        : L.RecordIdentity(SelectedRecord.Scope, SelectedRecord.Key);

    /// <summary>适合 UI 的修改时间。Modification time suitable for UI.</summary>
    public string UpdatedText => SelectedRecord is null
        ? string.Empty
        : L.Updated(SelectedRecord.UpdatedAt);

    /// <summary>scope 编辑层是否打开。Whether the scope editor overlay is open.</summary>
    public bool IsScopeEditorOpen
    {
        get => _isScopeEditorOpen;
        private set
        {
            if (SetProperty(ref _isScopeEditorOpen, value))
            {
                OnPropertyChanged(nameof(HasOpenModal));
                OnPropertyChanged(nameof(IsWorkspaceEnabled));
            }
        }
    }

    /// <summary>scope 编辑层标题。Scope editor title.</summary>
    public string ScopeEditorTitle
    {
        get => _scopeEditorTitle;
        private set => SetProperty(ref _scopeEditorTitle, value);
    }

    /// <summary>scope 名称输入，保持用户原文且不隐式 trim。Scope name input preserved verbatim without implicit trimming.</summary>
    public string ScopeNameInput
    {
        get => _scopeNameInput;
        set
        {
            if (SetProperty(ref _scopeNameInput, value))
            {
                NotifyCommandStates();
            }
        }
    }

    /// <summary>scope 删除确认层是否打开。Whether scope deletion confirmation is open.</summary>
    public bool IsScopeDeleteOpen
    {
        get => _isScopeDeleteOpen;
        private set
        {
            if (SetProperty(ref _isScopeDeleteOpen, value))
            {
                OnPropertyChanged(nameof(HasOpenModal));
                OnPropertyChanged(nameof(IsWorkspaceEnabled));
            }
        }
    }

    /// <summary>准确的 scope 删除确认文案。Exact scope deletion confirmation.</summary>
    public string ScopeDeleteMessage => _scopeDeleteTarget is null
        ? string.Empty
        : L.DeleteScope(_scopeDeleteTarget.Name, _scopeDeleteTarget.RecordCount);

    /// <summary>记录编辑层是否打开。Whether the record editor overlay is open.</summary>
    public bool IsRecordEditorOpen
    {
        get => _isRecordEditorOpen;
        private set
        {
            if (SetProperty(ref _isRecordEditorOpen, value))
            {
                OnPropertyChanged(nameof(HasOpenModal));
                OnPropertyChanged(nameof(IsWorkspaceEnabled));
            }
        }
    }

    /// <summary>记录编辑标题。Record editor title.</summary>
    public string RecordEditorTitle => _isEditingRecord ? L.EditRecordTitle : L.CreateRecordTitle;

    /// <summary>编辑器 key；编辑已有记录时不可改名。Editor key; existing records cannot be renamed here.</summary>
    public string EditorKey
    {
        get => _editorKey;
        set
        {
            if (SetProperty(ref _editorKey, value))
            {
                NotifyCommandStates();
            }
        }
    }

    /// <summary>旧版第一 value 编辑器访问器。Legacy first-value editor accessor.</summary>
    public string EditorValue
    {
        get => EditorValues.Count == 0 ? string.Empty : EditorValues[0].Value;
        set
        {
            EnsureEditorValue();
            EditorValues[0].Value = value;
        }
    }

    /// <summary>编辑器呈现策略是否为 masked。Whether the editor presentation is masked.</summary>
    public bool EditorIsMasked
    {
        get => _editorIsMasked;
        set
        {
            if (SetProperty(ref _editorIsMasked, value))
            {
                OnPropertyChanged(nameof(EditorIsPlain));
            }
        }
    }

    /// <summary>保存后直接显示；仅是遮罩呈现策略的互补选项。Visible after saving; the complementary presentation-policy option.</summary>
    public bool EditorIsPlain
    {
        get => !EditorIsMasked;
        set
        {
            if (value)
            {
                EditorIsMasked = false;
            }
        }
    }

    /// <summary>编辑器是否显示 value 原文。Whether the editor reveals the value text.</summary>
    public bool EditorValueVisible
    {
        get => _editorValueVisible;
        set => SetProperty(ref _editorValueVisible, value);
    }

    /// <summary>编辑器是否允许切换 value 可见性。Whether editor value visibility can be toggled.</summary>
    public bool EditorCanToggleVisibility => EditorValues.Count > 0;

    /// <summary>本地 value 集合验证消息。Local validation message for the value collection.</summary>
    public string? EditorValuesValidationMessage => EditorValues.Count > MaxRecordValues
        ? L.TooManyValues(MaxRecordValues)
        : EditorValueByteCount > MaxRecordValueBytes
            ? L.ValuesTooLarge(MaxRecordValueBytes / 1024)
            : null;

    /// <summary>value 集合是否超出可保存界限。Whether the value collection exceeds save limits.</summary>
    public bool HasEditorValuesValidationError => EditorValuesValidationMessage is not null;

    private int EditorValueByteCount => EditorValues.Sum(item => Encoding.UTF8.GetByteCount(item.Value));

    /// <summary>编辑已有记录时是否锁定 key。Whether key editing is locked for an existing record.</summary>
    public bool IsEditorKeyReadOnly => _isEditingRecord;

    /// <summary>记录删除确认层是否打开。Whether record deletion confirmation is open.</summary>
    public bool IsRecordDeleteOpen
    {
        get => _isRecordDeleteOpen;
        private set
        {
            if (SetProperty(ref _isRecordDeleteOpen, value))
            {
                OnPropertyChanged(nameof(HasOpenModal));
                OnPropertyChanged(nameof(IsWorkspaceEnabled));
            }
        }
    }

    /// <summary>任一显式编辑或确认层是否打开。Whether any explicit editor or confirmation overlay is open.</summary>
    public bool HasOpenModal => IsScopeEditorOpen || IsScopeDeleteOpen || IsRecordEditorOpen || IsRecordDeleteOpen;

    /// <summary>准确的记录删除确认文案。Exact record deletion confirmation.</summary>
    public string RecordDeleteMessage => _recordDeleteTarget is null
        ? string.Empty
        : L.DeleteRecord(_recordDeleteTarget.Scope, _recordDeleteTarget.Key);

    /// <summary>mutation 是否进行中。Whether a mutation is running.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsWorkspaceEnabled));
                NotifyCommandStates();
            }
        }
    }

    /// <summary>底层工作区是否可交互；模态操作期间保持禁用。Whether the underlying workspace is interactive; it stays disabled during modal work.</summary>
    public bool IsWorkspaceEnabled => !HasOpenModal && !IsBusy;

    /// <summary>重试加载命令。Retry loading command.</summary>
    public ICommand RetryCommand { get; }

    /// <summary>打开新建 scope。Opens scope creation.</summary>
    public ICommand OpenCreateScopeCommand { get; }

    /// <summary>打开 scope 重命名。Opens scope rename.</summary>
    public ICommand OpenRenameScopeCommand { get; }

    /// <summary>打开 scope 删除确认。Opens scope deletion confirmation.</summary>
    public ICommand OpenDeleteScopeCommand { get; }

    /// <summary>保存 scope mutation。Saves a scope mutation.</summary>
    public ICommand SaveScopeCommand { get; }

    /// <summary>确认删除 scope。Confirms scope deletion.</summary>
    public ICommand ConfirmDeleteScopeCommand { get; }

    /// <summary>打开新建记录。Opens record creation.</summary>
    public ICommand OpenCreateRecordCommand { get; }

    /// <summary>打开记录编辑。Opens record editing.</summary>
    public ICommand OpenEditRecordCommand { get; }

    /// <summary>打开记录删除确认。Opens record deletion confirmation.</summary>
    public ICommand OpenDeleteRecordCommand { get; }

    /// <summary>显式保存记录。Explicitly saves a record.</summary>
    public ICommand SaveRecordCommand { get; }

    /// <summary>确认删除记录。Confirms record deletion.</summary>
    public ICommand ConfirmDeleteRecordCommand { get; }

    /// <summary>复制当前 value。Copies the current value.</summary>
    public ICommand CopyCommand { get; }

    /// <summary>复制显式选定的单个 value。Copies one explicitly selected value.</summary>
    public ICommand CopyValueCommand { get; }

    /// <summary>将聚焦的 value 设为工作区快捷键目标。Makes the focused value the workspace-shortcut target.</summary>
    public ICommand ActivateValueCommand { get; }

    /// <summary>在末尾添加 value。Appends a value.</summary>
    public ICommand AddEditorValueCommand { get; }

    /// <summary>删除指定 value，但保持集合非空。Removes a value while preserving a non-empty collection.</summary>
    public ICommand RemoveEditorValueCommand { get; }

    /// <summary>将 value 向前移动一位。Moves a value one position earlier.</summary>
    public ICommand MoveEditorValueUpCommand { get; }

    /// <summary>将 value 向后移动一位。Moves a value one position later.</summary>
    public ICommand MoveEditorValueDownCommand { get; }

    /// <summary>切换短暂显示。Toggles temporary reveal.</summary>
    public ICommand ToggleRevealCommand { get; }

    /// <summary>切换单个 value 的短暂显示。Toggles temporary reveal for one value.</summary>
    public ICommand ToggleRevealValueCommand { get; }

    /// <summary>立即重新遮罩所有已显示的 value。Immediately re-masks every revealed value.</summary>
    public ICommand HideAllRevealedValuesCommand { get; }

    /// <summary>取消编辑或确认，不提交。Cancels an editor or confirmation without committing.</summary>
    public ICommand CloseModalCommand { get; }

    /// <summary>关闭错误提示。Dismisses the error presentation.</summary>
    public ICommand DismissErrorCommand { get; }

    /// <summary>
    /// 加载初始 scope；可由视图 Loaded 事件或测试直接调用。
    /// Loads initial scopes; may be called by the view Loaded event or directly by tests.
    /// </summary>
    public async Task InitializeAsync() => await LoadScopesAsync();

    private void CancelPendingOperations()
    {
        if (Interlocked.Exchange(ref _shutdownRequested, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        _searchCancellation?.Cancel();
        _detailCancellation?.Cancel();
        foreach (CancellationTokenSource cancellation in _revealCancellations.Values)
        {
            cancellation.Cancel();
        }
        _clipboardCancellation?.Cancel();
        _toastCancellation?.Cancel();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        CancelPendingOperations();
        if (Interlocked.Exchange(ref _clientDisposed, 1) == 0)
        {
            await _clipboardLifetimeGate.WaitAsync(CancellationToken.None);
            try
            {
                await ClearOwnedClipboardOnShutdownAsync(_ownedMaskedClipboardValue);
            }
            finally
            {
                _clipboardLifetimeGate.Release();
                _clipboardLifetimeGate.Dispose();
            }

            await _mutationLifetimeGate.WaitAsync(CancellationToken.None);
            try
            {
                await _client.DisposeAsync();
            }
            finally
            {
                _mutationLifetimeGate.Release();
                _mutationLifetimeGate.Dispose();
                _lifetime.Dispose();
                _searchCancellation?.Dispose();
                _detailCancellation?.Dispose();
                foreach (CancellationTokenSource cancellation in _revealCancellations.Values)
                {
                    cancellation.Dispose();
                }
                _clipboardCancellation?.Dispose();
                _toastCancellation?.Dispose();
            }
        }
    }

    private async Task<bool> LoadScopesAsync()
    {
        bool succeeded = false;
        IsLoadingScopes = true;
        ClearError();
        string? previousName = SelectedScope?.Name;

        try
        {
            var scopes = await _client.ListScopesAsync(_lifetime.Token);
            Scopes.Clear();
            foreach (ScopeSummary scope in scopes)
            {
                Scopes.Add(scope);
            }

            SynchronizeSearchScopeChoices(scopes);

            SelectedScope = scopes.FirstOrDefault(scope => string.Equals(scope.Name, previousName, StringComparison.Ordinal))
                ?? (scopes.Count > 0 ? scopes[0] : null);
            await ScheduleSearchAsync(immediate: true);
            succeeded = true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Closing the window owns this cancellation.
        }
        catch (Exception exception)
        {
            SetError(exception);
        }
        finally
        {
            IsLoadingScopes = false;
            NotifyCollectionState();
        }

        return succeeded;
    }

    private async Task<bool> ScheduleSearchAsync(bool immediate)
    {
        bool succeeded = false;
        long revision = Interlocked.Increment(ref _searchRevision);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        CancellationTokenSource? previous = Interlocked.Exchange(ref _searchCancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();

        SelectedCandidate = null;
        SelectedRecord = null;
        SearchError = null;
        if (SelectedMode is SearchMode.Exact or SearchMode.Regex)
        {
            // Strict modes must never leave broader fuzzy rows visible while their request is in flight.
            Candidates.Clear();
            NotifyCollectionState();
        }

        if (SelectedScope is null)
        {
            Candidates.Clear();
            IsSearching = false;
            NotifyCollectionState();
            return true;
        }

        try
        {
            IsSearching = true;
            if (!immediate && _debounce > TimeSpan.Zero)
            {
                await Task.Delay(_debounce, _timeProvider, cancellation.Token);
            }

            IReadOnlyList<RecordCandidate> results = await SearchSelectedScopesAsync(cancellation.Token);

            if (revision != Volatile.Read(ref _searchRevision) || cancellation.IsCancellationRequested)
            {
                return false;
            }

            Candidates.Clear();
            foreach (RecordCandidate candidate in FilterStrictMatches(results))
            {
                Candidates.Add(candidate);
            }

            SelectedCandidate = null;
            ClearError();
            succeeded = true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Cooperative cancellation is expected; revision also rejects non-cooperative stale responses.
        }
        catch (Exception exception)
        {
            if (revision == Volatile.Read(ref _searchRevision))
            {
                Candidates.Clear();
                if (exception is ScrapClientException { Kind: ScrapClientErrorKind.InvalidQuery })
                {
                    SearchError = L.InvalidSearch;
                }
                else
                {
                    SetError(exception);
                }
            }
        }
        finally
        {
            if (revision == Volatile.Read(ref _searchRevision))
            {
                IsSearching = false;
                NotifyCollectionState();
            }
        }

        return succeeded;
    }

    private IEnumerable<RecordCandidate> FilterStrictMatches(IEnumerable<RecordCandidate> candidates)
    {
        if (SelectedMode is SearchMode.Exact or SearchMode.Regex && SearchText.Length == 0)
        {
            return [];
        }

        StringComparison comparison = CaseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        if (SelectedMode == SearchMode.Exact)
        {
            return candidates.Where(candidate => string.Equals(candidate.Key, SearchText, comparison));
        }

        if (SelectedMode != SearchMode.Regex)
        {
            return candidates;
        }

        var options = RegexOptions.CultureInvariant;
        if (!CaseSensitive)
        {
            options |= RegexOptions.IgnoreCase;
        }

        var expression = new Regex(SearchText, options, TimeSpan.FromMilliseconds(100));
        return candidates.Where(candidate => expression.IsMatch(candidate.Key)).ToArray();
    }

    private async Task<IReadOnlyList<RecordCandidate>> SearchSelectedScopesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<string> scopes = _selectedSearchScopes.Count == 0
            ? []
            : _selectedSearchScopes.Order(StringComparer.Ordinal).ToArray();
        var request = new RecordSearchRequest(
            scopes,
            SearchText,
            SelectedMode,
            CaseSensitive,
            DefaultSearchLimit);
        return await _client.SearchAsync(request, cancellationToken);
    }

    private async Task LoadSelectedRecordAsync()
    {
        long revision = Interlocked.Increment(ref _detailRevision);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        CancellationTokenSource? previous = Interlocked.Exchange(ref _detailCancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();

        RecordCandidate? candidate = SelectedCandidate;
        SelectedRecord = null;
        if (candidate is null)
        {
            IsLoadingRecord = false;
            return;
        }

        IsLoadingRecord = true;
        try
        {
            RecordDetails record = await _client.GetRecordAsync(candidate.Scope, candidate.Key, cancellation.Token);
            if (revision == Volatile.Read(ref _detailRevision) && !cancellation.IsCancellationRequested)
            {
                SelectedRecord = record;
                ClearError();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Selection changed or the window closed.
        }
        catch (Exception exception)
        {
            if (revision == Volatile.Read(ref _detailRevision))
            {
                SetError(exception);
            }
        }
        finally
        {
            if (revision == Volatile.Read(ref _detailRevision))
            {
                IsLoadingRecord = false;
            }
        }
    }

    private void OpenCreateScope()
    {
        CloseModals();
        _isRenamingScope = false;
        _scopeEditorOriginalName = null;
        ScopeEditorTitle = L.CreateScopeTitle;
        ScopeNameInput = string.Empty;
        IsScopeEditorOpen = true;
        NotifyCommandStates();
    }

    private void OpenRenameScope()
    {
        if (SelectedScope is null)
        {
            return;
        }

        CloseModals();
        _isRenamingScope = true;
        _scopeEditorOriginalName = SelectedScope.Name;
        ScopeEditorTitle = L.RenameScopeTitle;
        ScopeNameInput = SelectedScope.Name;
        IsScopeEditorOpen = true;
        NotifyCommandStates();
    }

    private async Task OpenDeleteScopeAsync()
    {
        ScopeSummary? selected = SelectedScope;
        if (selected is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            IReadOnlyList<ScopeSummary> scopes = await _client.ListScopesAsync(_lifetime.Token);
            ScopeSummary? refreshed = scopes.FirstOrDefault(scope =>
                string.Equals(scope.Name, selected.Name, StringComparison.Ordinal));
            if (refreshed is null)
            {
                throw new ScrapClientException(
                    ScrapClientErrorKind.NotFound,
                    "The selected scope no longer exists. Refresh and choose another scope.");
            }

            CloseModals();
            _scopeDeleteTarget = refreshed;
            OnPropertyChanged(nameof(ScopeDeleteMessage));
            IsScopeDeleteOpen = true;
            NotifyCommandStates();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Window shutdown.
        }
        catch (Exception exception)
        {
            SetError(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSaveScope() =>
        !IsBusy &&
        IsScopeEditorOpen &&
        ScopeNameInput.Length > 0 &&
        (!_isRenamingScope || _scopeEditorOriginalName is not null);

    private async Task SaveScopeAsync()
    {
        string? originalName = _scopeEditorOriginalName;
        bool isRename = _isRenamingScope;
        string newName = ScopeNameInput;
        if (newName.Length == 0 || (isRename && originalName is null))
        {
            return;
        }

        await _mutationLifetimeGate.WaitAsync(CancellationToken.None);
        IsBusy = true;
        using var mutationCancellation = new CancellationTokenSource(_mutationDeadline, _timeProvider);
        try
        {
            if (isRename)
            {
                await _client.RenameScopeAsync(originalName!, newName, mutationCancellation.Token);
            }
            else
            {
                await _client.CreateScopeAsync(newName, mutationCancellation.Token);
            }

            if (isRename)
            {
                ApplyKnownScopeRename(originalName!, newName);
            }

            CloseModals();
            bool refreshed = await LoadScopesAsync();
            if (refreshed)
            {
                SelectedScope = Scopes.FirstOrDefault(scope => string.Equals(scope.Name, newName, StringComparison.Ordinal));
            }

            CompleteMutationRefresh(
                refreshed,
                isRename ? L.ScopeRenamed : L.ScopeCreated);
        }
        catch (Exception exception)
        {
            HandleMutationError(exception);
        }
        finally
        {
            IsBusy = false;
            NotifyCommandStates();
            _mutationLifetimeGate.Release();
        }
    }

    private async Task DeleteScopeAsync()
    {
        ScopeSummary? scope = _scopeDeleteTarget;
        if (scope is null)
        {
            return;
        }

        await _mutationLifetimeGate.WaitAsync(CancellationToken.None);
        IsBusy = true;
        using var mutationCancellation = new CancellationTokenSource(_mutationDeadline, _timeProvider);
        try
        {
            bool recursive = scope.RecordCount > 0;
            await _client.DeleteScopeAsync(
                scope.Name,
                recursive,
                recursive ? scope.RecordCount : null,
                mutationCancellation.Token);
            CloseModals();
            bool refreshed = await LoadScopesAsync();
            CompleteMutationRefresh(refreshed, L.ScopeDeleted);
        }
        catch (Exception exception)
        {
            Exception mutationError = NormalizeMutationError(exception);
            SetError(mutationError);
            if (mutationError is ScrapClientException { Kind: ScrapClientErrorKind.Conflict })
            {
                CloseModals();
                ErrorMessage = L.ScopeChangedBody;
            }
            else if (mutationError is ScrapClientException { Kind: ScrapClientErrorKind.OutcomeUnknown })
            {
                CloseModals();
            }
        }
        finally
        {
            IsBusy = false;
            _mutationLifetimeGate.Release();
        }
    }

    private void OpenCreateRecord()
    {
        if (SelectedScope is null)
        {
            return;
        }

        CloseModals();
        ClearError();
        _isEditingRecord = false;
        _recordEditorScope = SelectedScope.Name;
        _recordEditorOriginal = null;
        EditorKey = string.Empty;
        ReplaceEditorValues([string.Empty]);
        EditorIsMasked = true;
        EditorValueVisible = false;
        OnPropertyChanged(nameof(RecordEditorTitle));
        OnPropertyChanged(nameof(IsEditorKeyReadOnly));
        IsRecordEditorOpen = true;
        NotifyCommandStates();
    }

    private void OpenEditRecord()
    {
        if (SelectedRecord is not { } record)
        {
            return;
        }

        CloseModals();
        ClearError();
        _isEditingRecord = true;
        _recordEditorScope = record.Scope;
        _recordEditorOriginal = record;
        EditorKey = record.Key;
        ReplaceEditorValues(record.Values);
        EditorIsMasked = record.Presentation == RecordPresentation.Masked;
        EditorValueVisible = record.Presentation == RecordPresentation.Plain;
        OnPropertyChanged(nameof(RecordEditorTitle));
        OnPropertyChanged(nameof(IsEditorKeyReadOnly));
        IsRecordEditorOpen = true;
        NotifyCommandStates();
    }

    private void OpenDeleteRecord()
    {
        if (SelectedRecord is null)
        {
            return;
        }

        CloseModals();
        _recordDeleteTarget = SelectedRecord;
        OnPropertyChanged(nameof(RecordDeleteMessage));
        IsRecordDeleteOpen = true;
        NotifyCommandStates();
    }

    private bool CanSaveRecord() =>
        !IsBusy && IsRecordEditorOpen && _recordEditorScope is not null &&
        EditorKey.Length > 0 && EditorValues.Count is > 0 and <= MaxRecordValues &&
        EditorValueByteCount <= MaxRecordValueBytes;

    private async Task SaveRecordAsync()
    {
        string? editorScope = _recordEditorScope;
        RecordDetails? original = _recordEditorOriginal;
        bool isEdit = _isEditingRecord;
        if (editorScope is null || EditorKey.Length == 0)
        {
            return;
        }

        string key = EditorKey;
        await _mutationLifetimeGate.WaitAsync(CancellationToken.None);
        IsBusy = true;
        using var mutationCancellation = new CancellationTokenSource(_mutationDeadline, _timeProvider);
        try
        {
            var request = new SaveRecordRequest(
                editorScope,
                key,
                EditorValues.Select(item => item.Value).ToArray(),
                EditorIsMasked ? RecordPresentation.Masked : RecordPresentation.Plain,
                isEdit ? original?.Key : null,
                isEdit ? original?.Revision : 0);
            await _client.SaveRecordAsync(request, mutationCancellation.Token);
            CloseModals();
            bool searchRefreshed = await ScheduleSearchAsync(immediate: true);
            bool countRefreshed = await RefreshScopeCountAsync(editorScope);
            if (searchRefreshed && countRefreshed)
            {
                SelectedCandidate = Candidates.FirstOrDefault(candidate =>
                    string.Equals(candidate.Scope, editorScope, StringComparison.Ordinal) &&
                    string.Equals(candidate.Key, key, StringComparison.Ordinal));
            }

            CompleteMutationRefresh(
                searchRefreshed && countRefreshed,
                isEdit ? L.RecordSaved : L.RecordCreated);
        }
        catch (Exception exception)
        {
            HandleRecordSaveError(exception, isEdit);
        }
        finally
        {
            IsBusy = false;
            NotifyCommandStates();
            _mutationLifetimeGate.Release();
        }
    }

    private async Task DeleteRecordAsync()
    {
        RecordDetails? record = _recordDeleteTarget;
        if (record is null)
        {
            return;
        }

        await _mutationLifetimeGate.WaitAsync(CancellationToken.None);
        IsBusy = true;
        using var mutationCancellation = new CancellationTokenSource(_mutationDeadline, _timeProvider);
        try
        {
            await _client.DeleteRecordAsync(record.Scope, record.Key, record.Revision, mutationCancellation.Token);
            CloseModals();
            SelectedCandidate = null;
            bool searchRefreshed = await ScheduleSearchAsync(immediate: true);
            bool countRefreshed = await RefreshScopeCountAsync(record.Scope);
            CompleteMutationRefresh(searchRefreshed && countRefreshed, L.RecordDeleted);
        }
        catch (Exception exception)
        {
            HandleMutationError(exception);
        }
        finally
        {
            IsBusy = false;
            _mutationLifetimeGate.Release();
        }
    }

    /// <summary>
    /// 刷新受影响空间的计数，但不改变独立的记录管理目标。
    /// Refreshes the affected scope count without changing the independent record-management destination.
    /// </summary>
    private async Task<bool> RefreshScopeCountAsync(string scopeName)
    {
        bool succeeded = false;
        try
        {
            IReadOnlyList<ScopeSummary> scopes = await _client.ListScopesAsync(_lifetime.Token);
            ScopeSummary? refreshed = scopes.FirstOrDefault(scope => string.Equals(scope.Name, scopeName, StringComparison.Ordinal));
            if (refreshed is null)
            {
                return false;
            }

            ScopeSummary? existing = Scopes.FirstOrDefault(scope => string.Equals(scope.Name, scopeName, StringComparison.Ordinal));
            if (existing is null)
            {
                return false;
            }

            int index = Scopes.IndexOf(existing);
            Scopes[index] = refreshed;
            if (string.Equals(SelectedScope?.Name, scopeName, StringComparison.Ordinal))
            {
                SelectedScope = refreshed;
            }

            succeeded = true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Closing.
        }
        catch (Exception exception)
        {
            SetError(exception);
        }

        return succeeded;
    }

    private async Task RetryRefreshAsync()
    {
        await LoadScopesAsync();
    }

    private async Task CopyAsync()
    {
        if (SelectedRecord is not { } record)
        {
            return;
        }

        int index = Math.Clamp(_activeValueIndex, 0, record.Values.Count - 1);
        await CopyValueAsync(record.Values[index]);
    }

    private async Task CopyDisplayValueAsync(RecordValueDisplay? display)
    {
        if (display is null)
        {
            return;
        }

        _activeValueIndex = display.Position - 1;
        await CopyValueAsync(display.RawValue);
    }

    private void ActivateValue(RecordValueDisplay? display)
    {
        if (display is not null)
        {
            _activeValueIndex = display.Position - 1;
        }
    }

    private async Task CopyValueAsync(string? value)
    {
        if (SelectedRecord is not { } record || value is null)
        {
            return;
        }

        try
        {
            await _clipboardLifetimeGate.WaitAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }

        try
        {
            string? previouslyOwnedValue = _ownedMaskedClipboardValue;
            _clipboardCancellation?.Cancel();
            _clipboardCancellation?.Dispose();
            _clipboardCancellation = null;
            long generation = Interlocked.Increment(ref _clipboardGeneration);

            try
            {
                await _clipboard.SetTextAsync(value, _lifetime.Token);
            }
            catch
            {
                if (previouslyOwnedValue is not null)
                {
                    ScheduleClipboardCleanup(previouslyOwnedValue, generation);
                }

                throw;
            }

            if (record.Presentation == RecordPresentation.Masked)
            {
                ScheduleClipboardCleanup(value, generation);
            }
            else
            {
                _ownedMaskedClipboardValue = null;
            }

            ClearError();
            ShowToast(L.Copied);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            ErrorTitle = L.ClipboardUnavailableTitle;
            ErrorMessage = L.ClipboardUnavailableBody;
            return;
        }
        finally
        {
            _clipboardLifetimeGate.Release();
        }
    }

    private void ReplaceEditorValues(IEnumerable<string> values)
    {
        foreach (RecordValueEditor item in EditorValues)
        {
            item.PropertyChanged -= OnEditorValueChanged;
        }

        EditorValues.Clear();
        foreach (string value in values)
        {
            AddEditorValueItem(value);
        }

        EnsureEditorValue();
        OnPropertyChanged(nameof(EditorCanToggleVisibility));
        NotifyEditorValuesChanged();
    }

    private void EnsureEditorValue()
    {
        if (EditorValues.Count == 0)
        {
            AddEditorValueItem(string.Empty);
        }
    }

    private void AddEditorValue()
    {
        if (EditorValues.Count < MaxRecordValues)
        {
            AddEditorValueItem(string.Empty);
            RequestEditorValueFocus(EditorValues[^1]);
            NotifyEditorValuesChanged();
        }
    }

    private void RemoveEditorValue(RecordValueEditor? item)
    {
        if (item is null || EditorValues.Count <= 1)
        {
            return;
        }

        item.PropertyChanged -= OnEditorValueChanged;
        EditorValues.Remove(item);
        for (int index = 0; index < EditorValues.Count; index++)
        {
            EditorValues[index].SetPosition(index + 1);
        }

        NotifyEditorValuesChanged();
    }

    private bool CanMoveEditorValue(RecordValueEditor? item, int offset)
    {
        if (IsBusy || !IsRecordEditorOpen || item is null)
        {
            return false;
        }

        int index = EditorValues.IndexOf(item);
        int destination = index + offset;
        return index >= 0 && destination >= 0 && destination < EditorValues.Count;
    }

    private void MoveEditorValue(RecordValueEditor? item, int offset)
    {
        if (!CanMoveEditorValue(item, offset) || item is null)
        {
            return;
        }

        int source = EditorValues.IndexOf(item);
        EditorValues.Move(source, source + offset);
        for (int index = 0; index < EditorValues.Count; index++)
        {
            EditorValues[index].SetPosition(index + 1);
        }

        RequestEditorValueFocus(item);
        NotifyEditorValuesChanged();
    }

    private void RequestEditorValueFocus(RecordValueEditor item)
    {
        RequestedEditorValueFocus = null;
        RequestedEditorValueFocus = item;
    }

    private void AddEditorValueItem(string value)
    {
        var item = new RecordValueEditor(
            value,
            EditorValues.Count + 1,
            position => L.ValueNumber(position),
            position => L.MoveValueNumberUp(position),
            position => L.MoveValueNumberDown(position));
        item.PropertyChanged += OnEditorValueChanged;
        EditorValues.Add(item);
    }

    private void OnEditorValueChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(RecordValueEditor.Value))
        {
            NotifyEditorValuesChanged();
        }
    }

    private void NotifyEditorValuesChanged()
    {
        OnPropertyChanged(nameof(EditorValue));
        OnPropertyChanged(nameof(EditorValuesValidationMessage));
        OnPropertyChanged(nameof(HasEditorValuesValidationError));
        NotifyCommandStates();
    }

    private async Task ClearClipboardIfUnchangedAsync(
        string copiedValue,
        long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_clipboardDuration, _timeProvider, cancellationToken);
            await _clipboardLifetimeGate.WaitAsync(cancellationToken);
            try
            {
                if (generation != Volatile.Read(ref _clipboardGeneration))
                {
                    return;
                }

                string? current = await _clipboard.GetTextAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (generation == Volatile.Read(ref _clipboardGeneration) &&
                    string.Equals(current, copiedValue, StringComparison.Ordinal))
                {
                    await _clipboard.ClearAsync(cancellationToken);
                }
            }
            finally
            {
                _clipboardLifetimeGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer copy or application shutdown owns cancellation.
        }
        catch
        {
            // Clipboard cleanup is explicitly best effort and must not disrupt the primary workflow.
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested &&
                generation == Volatile.Read(ref _clipboardGeneration))
            {
                _ownedMaskedClipboardValue = null;
            }
        }
    }

    private void ScheduleClipboardCleanup(string copiedValue, long generation)
    {
        _ownedMaskedClipboardValue = copiedValue;
        _clipboardCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _ = ClearClipboardIfUnchangedAsync(copiedValue, generation, _clipboardCancellation.Token);
    }

    private async Task ClearOwnedClipboardOnShutdownAsync(string? copiedValue)
    {
        _ownedMaskedClipboardValue = null;
        if (copiedValue is null)
        {
            return;
        }

        try
        {
            string? current = await _clipboard.GetTextAsync(CancellationToken.None);
            if (string.Equals(current, copiedValue, StringComparison.Ordinal))
            {
                await _clipboard.ClearAsync(CancellationToken.None);
            }
        }
        catch
        {
            // The platform may detach its clipboard before Closed; cleanup remains best effort.
        }
    }

    private void ToggleReveal()
    {
        if (SelectedRecord is not { Presentation: RecordPresentation.Masked } record)
        {
            return;
        }

        int index = Math.Clamp(_activeValueIndex, 0, record.Values.Count - 1);
        ToggleRevealIndex(index);
    }

    private void ToggleRevealValue(RecordValueDisplay? display)
    {
        if (display is null || SelectedRecord?.Presentation != RecordPresentation.Masked)
        {
            return;
        }

        _activeValueIndex = display.Position - 1;
        ToggleRevealIndex(_activeValueIndex);
    }

    private void ToggleRevealIndex(int index)
    {
        if (_revealedValueIndexes.Remove(index))
        {
            if (_revealCancellations.Remove(index, out CancellationTokenSource? existing))
            {
                existing.Cancel();
                existing.Dispose();
            }

            RefreshRevealState();
            return;
        }

        _revealedValueIndexes.Add(index);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _revealCancellations[index] = cancellation;
        RefreshRevealState();
        _ = HideAfterDelayAsync(index, cancellation);
    }

    private async Task HideAfterDelayAsync(int index, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(_revealDuration, _timeProvider, cancellation.Token);
            _revealedValueIndexes.Remove(index);
            _revealCancellations.Remove(index);
            RefreshRevealState();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Explicit hide, selection change, or shutdown.
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private void CancelReveal()
    {
        foreach (CancellationTokenSource cancellation in _revealCancellations.Values)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }

        _revealCancellations.Clear();
        _revealedValueIndexes.Clear();
        RefreshRevealState();
    }

    private void RefreshRevealState()
    {
        IsRevealed = _revealedValueIndexes.Count > 0;
        OnPropertyChanged(nameof(DisplayValues));
    }

    private void CloseModals()
    {
        IsScopeEditorOpen = false;
        IsScopeDeleteOpen = false;
        IsRecordEditorOpen = false;
        IsRecordDeleteOpen = false;
        _scopeEditorOriginalName = null;
        _scopeDeleteTarget = null;
        _recordEditorScope = null;
        _recordEditorOriginal = null;
        _recordDeleteTarget = null;
        _isRenamingScope = false;
        _isEditingRecord = false;
        NotifyCommandStates();
    }

    private void HandleMutationError(Exception exception)
    {
        Exception mutationError = NormalizeMutationError(exception);
        SetError(mutationError);
        if (mutationError is ScrapClientException { Kind: ScrapClientErrorKind.OutcomeUnknown })
        {
            CloseModals();
        }
    }

    private void CompleteMutationRefresh(bool refreshed, string successMessage)
    {
        ShowToast(successMessage);
        if (refreshed)
        {
            ClearError();
            return;
        }

        ErrorTitle = L.RefreshFailedTitle;
        ErrorMessage = L.RefreshFailedBody;
    }

    private void HandleRecordSaveError(Exception exception, bool isEdit)
    {
        Exception mutationError = NormalizeMutationError(exception);
        SetError(mutationError);
        if (mutationError is ScrapClientException { Kind: ScrapClientErrorKind.Conflict })
        {
            ErrorMessage = isEdit ? L.RecordConflictEdit : L.RecordConflictCreate;
        }
        else if (mutationError is ScrapClientException { Kind: ScrapClientErrorKind.OutcomeUnknown })
        {
            CloseModals();
        }
    }

    private Exception NormalizeMutationError(Exception exception) => exception is OperationCanceledException
        ? new ScrapClientException(
            ScrapClientErrorKind.OutcomeUnknown,
            L.MutationTimedOut,
            exception)
        : exception;

    private void SetError(Exception exception)
    {
        SearchError = null;
        if (exception is ScrapClientException clientException)
        {
            ErrorTitle = L.ErrorTitle(clientException.Kind);
            ErrorMessage = clientException.Kind == ScrapClientErrorKind.OutcomeUnknown
                ? clientException.Message
                : L.GenericErrorBody;
        }
        else
        {
            ErrorTitle = L.ErrorTitle(ScrapClientErrorKind.Unknown);
            ErrorMessage = L.GenericErrorBody;
        }
    }

    private void ClearError()
    {
        ErrorTitle = null;
        ErrorMessage = null;
    }

    private void ShowToast(string message)
    {
        ToastMessage = message;
        _toastCancellation?.Cancel();
        _toastCancellation?.Dispose();
        _toastCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _ = ClearToastAsync(_toastCancellation.Token);
    }

    private async Task ClearToastAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), _timeProvider, cancellationToken);
            ToastMessage = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Replaced toast or shutdown.
        }
    }

    private void RebuildLocalizedChoices()
    {
        ThemeChoices.Clear();
        ThemeChoices.Add(new(AppTheme.System, L.SystemTheme));
        ThemeChoices.Add(new(AppTheme.Light, L.LightTheme));
        ThemeChoices.Add(new(AppTheme.Dark, L.DarkTheme));

        LanguageChoices.Clear();
        LanguageChoices.Add(new(AppLanguage.SimplifiedChinese, L.Chinese));
        LanguageChoices.Add(new(AppLanguage.English, L.English));
    }

    private void NotifyLocalizedProperties()
    {
        foreach (RecordValueEditor item in EditorValues)
        {
            item.RefreshAccessibleNames();
        }

        ScopeEditorTitle = _isRenamingScope ? L.RenameScopeTitle : L.CreateScopeTitle;
        ClearError();
        ToastMessage = null;
        OnPropertyChanged(nameof(L));
        OnPropertyChanged(nameof(SelectedLanguage));
        OnPropertyChanged(nameof(SelectedTheme));
        OnPropertyChanged(nameof(SearchScopeSummary));
        OnPropertyChanged(nameof(SearchScopeAccessibleName));
        OnPropertyChanged(nameof(ScopeStatus));
        OnPropertyChanged(nameof(ScopeDeleteMessage));
        OnPropertyChanged(nameof(RecordEditorTitle));
        OnPropertyChanged(nameof(RecordDeleteMessage));
        OnPropertyChanged(nameof(RevealButtonText));
        OnPropertyChanged(nameof(DisplayValues));
        OnPropertyChanged(nameof(RecordIdentity));
        OnPropertyChanged(nameof(UpdatedText));
    }

    private void PersistPreferences() =>
        _preferenceStore.Save(new UserPreferences(_selectedTheme, _selectedLanguage));

    private void SynchronizeSearchScopeChoices(IReadOnlyList<ScopeSummary> scopes)
    {
        var availableNames = scopes.Select(scope => scope.Name).ToHashSet(StringComparer.Ordinal);
        _selectedSearchScopes.IntersectWith(availableNames);

        _synchronizingSearchScopes = true;
        try
        {
            SearchScopeChoices.Clear();
            foreach (ScopeSummary scope in scopes)
            {
                SearchScopeChoices.Add(new SearchScopeChoice(
                    scope.Name,
                    _selectedSearchScopes.Contains(scope.Name),
                    OnSearchScopeSelectionChanged));
            }
        }
        finally
        {
            _synchronizingSearchScopes = false;
        }

        NotifySearchScopeSelectionChanged();
    }

    /// <summary>
    /// 在远端 rename 已成功后立即更新本地身份，使后续列表刷新失败也不会留下幽灵筛选项。
    /// Applies a confirmed remote rename locally so a later list-refresh failure cannot leave a ghost filter identity.
    /// </summary>
    private void ApplyKnownScopeRename(string originalName, string newName)
    {
        ScopeSummary? original = Scopes.FirstOrDefault(scope =>
            string.Equals(scope.Name, originalName, StringComparison.Ordinal));
        if (original is not null)
        {
            var renamed = new ScopeSummary(newName, original.RecordCount);
            int oldIndex = Scopes.IndexOf(original);
            Scopes.RemoveAt(oldIndex);
            int newIndex = 0;
            while (newIndex < Scopes.Count &&
                   StringComparer.Ordinal.Compare(Scopes[newIndex].Name, newName) < 0)
            {
                newIndex++;
            }

            Scopes.Insert(newIndex, renamed);
            if (string.Equals(SelectedScope?.Name, originalName, StringComparison.Ordinal))
            {
                SelectedScope = renamed;
            }
        }

        if (_selectedSearchScopes.Remove(originalName))
        {
            _selectedSearchScopes.Add(newName);
        }

        SynchronizeSearchScopeChoices(Scopes);
    }

    private void OnSearchScopeSelectionChanged(SearchScopeChoice choice, bool selected)
    {
        if (_synchronizingSearchScopes)
        {
            return;
        }

        if (selected)
        {
            _selectedSearchScopes.Add(choice.Name);
        }
        else if (_selectedSearchScopes.Count > 1)
        {
            _selectedSearchScopes.Remove(choice.Name);
        }
        else
        {
            choice.SetSelectedSilently(true);
            return;
        }

        NotifySearchScopeSelectionChanged();
        SelectedCandidate = null;
        _ = ScheduleSearchAsync(immediate: true);
    }

    private void SetSearchScopesToAll()
    {
        _selectedSearchScopes.Clear();
        foreach (SearchScopeChoice choice in SearchScopeChoices)
        {
            choice.SetSelectedSilently(false);
        }

        NotifySearchScopeSelectionChanged();
        SelectedCandidate = null;
        _ = ScheduleSearchAsync(immediate: true);
    }

    private void NotifySearchScopeSelectionChanged()
    {
        OnPropertyChanged(nameof(IsAllSearchScopesSelected));
        OnPropertyChanged(nameof(SearchScopeSummary));
        OnPropertyChanged(nameof(SearchScopeAccessibleName));
    }

    private void NotifyCollectionState()
    {
        OnPropertyChanged(nameof(HasNoScopes));
        OnPropertyChanged(nameof(HasNoCandidates));
    }

    private void NotifyCommandStates()
    {
        foreach (ICommand command in new[]
        {
            RetryCommand,
            OpenRenameScopeCommand,
            OpenCreateScopeCommand,
            OpenDeleteScopeCommand,
            SaveScopeCommand,
            ConfirmDeleteScopeCommand,
            OpenCreateRecordCommand,
            OpenEditRecordCommand,
            OpenDeleteRecordCommand,
            SaveRecordCommand,
            ConfirmDeleteRecordCommand,
            CopyCommand,
            CopyValueCommand,
            ActivateValueCommand,
            AddEditorValueCommand,
            RemoveEditorValueCommand,
            MoveEditorValueUpCommand,
            MoveEditorValueDownCommand,
            ToggleRevealCommand,
            ToggleRevealValueCommand,
            HideAllRevealedValuesCommand,
            CloseModalCommand,
        })
        {
            switch (command)
            {
                case RelayCommand relay:
                    relay.NotifyCanExecuteChanged();
                    break;
                case AsyncRelayCommand asyncRelay:
                    asyncRelay.NotifyCanExecuteChanged();
                    break;
            }
        }
    }
}
