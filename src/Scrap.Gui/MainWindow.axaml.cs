using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Scrap.Gui.Abstractions;
using Scrap.Gui.Services;
using Scrap.Gui.ViewModels;

namespace Scrap.Gui;

/// <summary>
/// 主窗口视图；仅负责平台剪贴板、焦点与键盘路由。
/// Main window view responsible only for platform clipboard, focus, and keyboard routing.
/// </summary>
public partial class MainWindow : Window
{
    private bool _initialized;
    private bool _allowClose;
    private int _closeDrainStarted;

    /// <summary>
    /// 使用明确的不可用诊断 client 创建设计时窗口。
    /// Creates a design-time window with an explicit unavailable diagnostic client.
    /// </summary>
    public MainWindow()
        : this(new UnavailableScrapClient())
    {
    }

    /// <summary>
    /// 使用指定 client 创建窗口，供 composition root 与 UI 测试使用。
    /// Creates a window with the specified client for the composition root and UI tests.
    /// </summary>
    /// <param name="client">GUI 应用 client。GUI application client.</param>
    public MainWindow(IScrapClient client)
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel(
            client,
            new AvaloniaClipboardService(() => TopLevel.GetTopLevel(this)?.Clipboard));
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += OnLoaded;
        Closing += OnClosing;
        KeyDown += OnKeyDown;
    }

    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext!;

    private async void OnLoaded(object? sender, RoutedEventArgs eventArgs)
    {
        SearchBox.Focus();
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        await ViewModel.InitializeAsync();
        SearchBox.Focus();
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs eventArgs)
    {
        if (_allowClose)
        {
            return;
        }

        eventArgs.Cancel = true;
        if (Interlocked.Exchange(ref _closeDrainStarted, 1) != 0)
        {
            return;
        }

        IsEnabled = false;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        try
        {
            await ViewModel.DisposeAsync();
        }
        finally
        {
            _allowClose = true;
            Close();
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        bool control = eventArgs.KeyModifiers.HasFlag(KeyModifiers.Control);
        IInputElement? focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        bool editingText = focused is TextBox;

        if (eventArgs.Key == Key.Escape)
        {
            if (ViewModel.HasOpenModal)
            {
                Execute(ViewModel.CloseModalCommand);
            }
            else if (ViewModel.IsRevealed)
            {
                Execute(ViewModel.ToggleRevealCommand);
            }

            eventArgs.Handled = true;
            return;
        }

        if (ViewModel.HasOpenModal)
        {
            return;
        }

        if (control && eventArgs.Key == Key.K)
        {
            FocusSearch();
            eventArgs.Handled = true;
            return;
        }

        if (string.Equals(eventArgs.KeySymbol, "/", StringComparison.Ordinal) && !editingText)
        {
            FocusSearch();
            eventArgs.Handled = true;
            return;
        }

        if (control && eventArgs.Key == Key.C && !editingText)
        {
            Execute(ViewModel.CopyCommand);
            eventArgs.Handled = true;
            return;
        }

        if (control && eventArgs.Key == Key.N)
        {
            Execute(ViewModel.OpenCreateRecordCommand);
            eventArgs.Handled = true;
            return;
        }

        if (eventArgs.Key == Key.Down && ReferenceEquals(focused, SearchBox) && CandidateList.ItemCount > 0)
        {
            CandidateList.SelectedIndex = Math.Max(CandidateList.SelectedIndex, 0);
            CandidateList.Focus();
            eventArgs.Handled = true;
            return;
        }

        if (eventArgs.Key == Key.Enter && ReferenceEquals(focused, SearchBox) && CandidateList.ItemCount > 0)
        {
            CandidateList.SelectedIndex = CandidateList.SelectedIndex >= 0 ? CandidateList.SelectedIndex : 0;
            CandidateList.Focus();
            eventArgs.Handled = true;
            return;
        }

    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(MainWindowViewModel.IsScopeEditorOpen) && ViewModel.IsScopeEditorOpen)
        {
            Dispatcher.UIThread.Post(() => FocusAndSelect(ScopeNameBox));
        }
        else if (eventArgs.PropertyName == nameof(MainWindowViewModel.IsScopeDeleteOpen) && ViewModel.IsScopeDeleteOpen)
        {
            Dispatcher.UIThread.Post(() => ScopeDeleteConfirmButton.Focus());
        }
        else if (eventArgs.PropertyName == nameof(MainWindowViewModel.IsRecordEditorOpen) && ViewModel.IsRecordEditorOpen)
        {
            Dispatcher.UIThread.Post(() => FocusAndSelect(ViewModel.IsEditorKeyReadOnly ? RecordValueBox : RecordKeyBox));
        }
        else if (eventArgs.PropertyName == nameof(MainWindowViewModel.IsRecordDeleteOpen) && ViewModel.IsRecordDeleteOpen)
        {
            Dispatcher.UIThread.Post(() => RecordDeleteConfirmButton.Focus());
        }
        else if (eventArgs.PropertyName == nameof(MainWindowViewModel.HasOpenModal) && !ViewModel.HasOpenModal)
        {
            Dispatcher.UIThread.Post(FocusSearch);
        }
    }

    private void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private static void FocusAndSelect(TextBox textBox)
    {
        textBox.Focus();
        textBox.SelectAll();
    }

    private static void Execute(System.Windows.Input.ICommand command)
    {
        if (command.CanExecute(null))
        {
            command.Execute(null);
        }
    }
}
