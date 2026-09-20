using System.Windows.Input;

namespace Scrap.Gui.Infrastructure;

/// <summary>
/// 小型同步命令，避免把 UI 框架类型泄漏到 ViewModel 行为中。
/// Small synchronous command that keeps UI framework types out of ViewModel behavior.
/// </summary>
internal sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    /// <summary>创建命令。Creates a command.</summary>
    internal RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = _ => execute();
        _canExecute = canExecute is null ? null : _ => canExecute();
    }

    /// <summary>创建接受绑定参数的命令。Creates a command that accepts a binding parameter.</summary>
    internal RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    /// <inheritdoc />
    public void Execute(object? parameter) => _execute(parameter);

    /// <summary>通知控件重新计算可执行状态。Notifies controls to recompute command availability.</summary>
    internal void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// 防止重入并把异步异常交由 ViewModel 处理的命令。
/// Command that prevents reentrancy and leaves asynchronous error handling to the ViewModel.
/// </summary>
internal sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private bool _isRunning;

    /// <summary>创建异步命令。Creates an asynchronous command.</summary>
    internal AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = _ => execute();
        _canExecute = canExecute is null ? null : _ => canExecute();
    }

    /// <summary>创建接受绑定参数的异步命令。Creates an asynchronous command that accepts a binding parameter.</summary>
    internal AsyncRelayCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => !_isRunning && (_canExecute?.Invoke(parameter) ?? true);

    /// <inheritdoc />
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _isRunning = true;
        NotifyCanExecuteChanged();

        try
        {
            await _execute(parameter);
        }
        finally
        {
            _isRunning = false;
            NotifyCanExecuteChanged();
        }
    }

    /// <summary>通知控件重新计算可执行状态。Notifies controls to recompute command availability.</summary>
    internal void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
