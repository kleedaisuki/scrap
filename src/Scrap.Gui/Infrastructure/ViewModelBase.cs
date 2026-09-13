using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Scrap.Gui.Infrastructure;

/// <summary>
/// 提供最小、可测试属性通知的 ViewModel 基类。
/// Minimal ViewModel base providing testable property notification.
/// </summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 仅在值变化时更新字段并通知绑定。
    /// Updates a field and notifies bindings only when the value changes.
    /// </summary>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    /// <summary>通知一个派生属性变化。Notifies that a derived property changed.</summary>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
