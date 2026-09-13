using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scrap.Platform.Ipc;
using Scrap.Platform.Paths;
using Scrap.Platform.Secrets;
using Scrap.Storage.Sqlite;

namespace Scrap.Daemon;

/// <summary>
/// 组装不含 ASP.NET Core 的 Generic Host 与唯一数据 owner。
/// / Composes the non-ASP.NET Generic Host and sole data owner.
/// </summary>
internal static class DaemonHost
{
    /// <summary>
    /// 为已取得单实例租约的 profile 创建 host。 / Creates a host for a profile whose singleton lease is already held.
    /// </summary>
    /// <param name="args">非敏感 daemon 参数。 / Non-sensitive daemon arguments.</param>
    /// <param name="paths">已初始化的 profile 路径。 / Initialized profile paths.</param>
    /// <returns>已完成依赖装配但尚未运行的 host。 / A composed host that has not started.</returns>
    public static IHost Build(string[] args, ScrapPathLayout paths)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(paths);
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
        builder.Configuration.AddJsonFile(paths.ConfigurationFile, optional: true, reloadOnChange: false);

        // A spawned daemon must never inherit Generic Host console logging and corrupt CLI data streams.
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new ScrapFileLoggerProvider(paths.LogFile));
        builder.Logging.AddFilter(static (category, level) =>
            level >= LogLevel.Information
            && category is not null
            && category.StartsWith("Scrap.Daemon", StringComparison.Ordinal));

        var daemonOptions = new DaemonOptions();
        builder.Configuration.GetSection("daemon").Bind(daemonOptions);
        daemonOptions.Validate();

        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton(IpcEndpointDescriptor.Create(paths));
        builder.Services.AddSingleton(MasterKeyProviderFactory.Create(paths));
        builder.Services.AddSingleton(new SqliteStore(paths.DatabaseFile));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(Options.Create(daemonOptions));
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = daemonOptions.ShutdownTimeout);

        builder.Services.AddSingleton<DaemonRuntimeState>();
        builder.Services.AddSingleton<DaemonActivityTracker>();
        builder.Services.AddSingleton<DaemonInitializationState>();
        builder.Services.AddSingleton<RequestExecutionCoordinator>();
        builder.Services.AddSingleton<IDaemonOperations, SqliteDaemonOperations>();
        builder.Services.AddSingleton<IConnectionAcceptor, NamedPipeConnectionAcceptor>();
        builder.Services.AddSingleton<DaemonConnectionProcessor>();
        builder.Services.AddSingleton<DaemonRequestDispatcher>(services => new(
            services.GetRequiredService<IDaemonOperations>(),
            services.GetRequiredService<RequestExecutionCoordinator>(),
            services.GetRequiredService<ILogger<DaemonRequestDispatcher>>(),
            GetApplicationVersion(),
            services.GetRequiredService<DaemonInitializationState>()));

        // Bind the pipe first; initialization failures are cached and served as structured RPC errors.
        builder.Services.AddHostedService<DaemonServerService>();
        builder.Services.AddHostedService<DaemonInitializationService>();
        builder.Services.AddHostedService<IdleShutdownService>();
        return builder.Build();
    }

    private static string GetApplicationVersion() =>
        typeof(DaemonHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(DaemonHost).Assembly.GetName().Version?.ToString()
        ?? "unknown";
}
