using Scrap.Gui.Infrastructure;

namespace Scrap.Gui.ViewModels;

/// <summary>
/// 多选检索器中的一个 scope；选择变化由主窗口状态机统一转换为协议请求。
/// One scope in the multi-select search picker; the main-window state machine maps changes to protocol requests.
/// </summary>
public sealed class SearchScopeChoice : ViewModelBase
{
    private readonly Action<SearchScopeChoice, bool> _selectionChanged;
    private bool _isSelected;

    /// <summary>创建一个可绑定的 scope 选择项。Creates a bindable scope choice.</summary>
    /// <param name="name">大小写敏感的 scope 名称。Case-sensitive scope name.</param>
    /// <param name="isSelected">初始选择状态。Initial selection state.</param>
    /// <param name="selectionChanged">用户选择变化回调。Callback for a user selection change.</param>
    public SearchScopeChoice(
        string name,
        bool isSelected,
        Action<SearchScopeChoice, bool> selectionChanged)
    {
        Name = name;
        _isSelected = isSelected;
        _selectionChanged = selectionChanged;
    }

    /// <summary>大小写敏感的 scope 名称。Case-sensitive scope name.</summary>
    public string Name { get; }

    /// <summary>该 scope 是否包含在显式检索集合中。Whether this scope belongs to the explicit search set.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!SetProperty(ref _isSelected, value))
            {
                return;
            }

            _selectionChanged(this, value);
        }
    }

    /// <summary>
    /// 从集合级状态同步选择，不重复触发回调。
    /// Synchronizes selection from collection-level state without recursively invoking the callback.
    /// </summary>
    /// <param name="value">新的选择状态。New selection state.</param>
    internal void SetSelectedSilently(bool value) => SetProperty(ref _isSelected, value, nameof(IsSelected));
}
