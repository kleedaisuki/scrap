using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Scrap.Gui.Services;

namespace Scrap.Gui;

/// <summary>
/// 桌面应用 composition root。Desktop application composition root.
/// </summary>
public partial class App : Application
{
    /// <inheritdoc />
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow(new ProtocolScrapClientAdapter());
        }

        base.OnFrameworkInitializationCompleted();
    }
}
