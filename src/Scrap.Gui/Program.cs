using System;
using Avalonia;

namespace Scrap.Gui;

/// <summary>
/// 应用程序入口与 Avalonia 启动配置。Application entry point and Avalonia bootstrap configuration.
/// </summary>
internal static class Program
{
    /// <summary>
    /// 启动桌面应用。Starts the desktop application.
    /// </summary>
    /// <param name="args">进程参数。Process arguments.</param>
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    /// <summary>
    /// 创建由运行时和设计器共享的 Avalonia 配置。
    /// Creates the Avalonia configuration shared by the runtime and designer.
    /// </summary>
    /// <returns>已配置的应用构建器。The configured application builder.</returns>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
#if DEBUG
        .WithDeveloperTools()
#endif
        .WithInterFont()
        .LogToTrace();
}
