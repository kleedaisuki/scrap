using System.Reflection;
using System.Globalization;
using System.Text.Json;
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
        return Build(args, paths, MasterKeyProviderFactory.Create(paths));
    }

    /// <summary>
    /// 使用显式主密钥提供器组装 host，供隔离的进程内集成测试使用。
    /// / Composes the host with an explicit master-key provider for isolated in-process integration tests.
    /// </summary>
    /// <remarks>
    /// 此重载保持为 internal，避免把测试注入面暴露为生产 API；其余装配与生产入口完全相同。
    /// / This overload remains internal so the test injection seam is not a production API; every other registration is
    /// identical to the production entry point.
    /// </remarks>
    /// <param name="args">非敏感 daemon 参数。 / Non-sensitive daemon arguments.</param>
    /// <param name="paths">已初始化的隔离 profile 路径。 / Initialized isolated-profile paths.</param>
    /// <param name="masterKeyProvider">由 host 使用但不拥有的主密钥持久化边界。 / Master-key persistence boundary used but not owned by the host.</param>
    /// <returns>已完成依赖装配但尚未运行的 host。 / A composed host that has not started.</returns>
    internal static IHost Build(
        string[] args,
        ScrapPathLayout paths,
        IMasterKeyProvider masterKeyProvider)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(masterKeyProvider);
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

        // A spawned daemon must never inherit Generic Host console logging and corrupt CLI data streams.
        builder.Logging.ClearProviders();
        // 工厂注册使 host 拥有并释放文件句柄；实例注册会泄露句柄。
        // Factory registration makes the host own and dispose the file handle; an instance registration would leak it.
        var fileLogger = new ScrapFileLoggerProvider(paths.LogFile);
        builder.Services.AddSingleton<ILoggerProvider>(_ => fileLogger);
        builder.Logging.AddFilter(static (category, level) =>
            level >= LogLevel.Information
            && category is not null
            && category.StartsWith("Scrap.Daemon", StringComparison.Ordinal));

        DaemonOptions daemonOptions = ReadOptions(builder.Configuration.GetSection("daemon"), paths, fileLogger);

        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton(IpcEndpointDescriptor.Create(paths));
        builder.Services.AddSingleton(masterKeyProvider);
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
        try
        {
            return builder.Build();
        }
        catch
        {
            fileLogger.Dispose();
            throw;
        }
    }

    private static string GetApplicationVersion() =>
        typeof(DaemonHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(DaemonHost).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    private static DaemonOptions ReadOptions(
        IConfigurationSection section,
        ScrapPathLayout paths,
        ScrapFileLoggerProvider logger)
    {
        var options = new DaemonOptions();
        ApplyDuration(section["idleTimeout"], "idleTimeout", options.IdleTimeout, value => options.IdleTimeout = value, logger);
        ApplyDuration(section["idlePollInterval"], "idlePollInterval", options.IdlePollInterval, value => options.IdlePollInterval = value, logger);
        ApplyDuration(section["shutdownTimeout"], "shutdownTimeout", options.ShutdownTimeout, value => options.ShutdownTimeout = value, logger);
        ApplyDuration(section["responseWriteTimeout"], "responseWriteTimeout", options.ResponseWriteTimeout, value => options.ResponseWriteTimeout = value, logger);
        ApplyProfileConfiguration(paths.ConfigurationFile, options, logger);
        return options.Validate();
    }

    private static void ApplyProfileConfiguration(
        string configurationFile,
        DaemonOptions options,
        ScrapFileLoggerProvider logger)
    {
        if (!File.Exists(configurationFile))
        {
            return;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(configurationFile));
            if (!TryGetProperty(document.RootElement, "daemon", out JsonElement daemon))
            {
                return;
            }

            if (daemon.ValueKind != JsonValueKind.Object)
            {
                logger.WriteConfigurationFallback("config.json");
                return;
            }

            ApplyJsonDuration(daemon, "idleTimeout", options.IdleTimeout, value => options.IdleTimeout = value, logger);
            ApplyJsonDuration(daemon, "idlePollInterval", options.IdlePollInterval, value => options.IdlePollInterval = value, logger);
            ApplyJsonDuration(daemon, "shutdownTimeout", options.ShutdownTimeout, value => options.ShutdownTimeout = value, logger);
            ApplyJsonDuration(daemon, "responseWriteTimeout", options.ResponseWriteTimeout, value => options.ResponseWriteTimeout = value, logger);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.WriteConfigurationFallback("config.json");
        }
    }

    private static void ApplyJsonDuration(
        JsonElement section,
        string propertyName,
        TimeSpan fallback,
        Action<TimeSpan> assign,
        ScrapFileLoggerProvider logger)
    {
        if (!TryGetProperty(section, propertyName, out JsonElement element))
        {
            return;
        }

        string? raw = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        ApplyDuration(raw, propertyName, fallback, assign, logger, isSpecified: true);
    }

    private static void ApplyDuration(
        string? raw,
        string propertyName,
        TimeSpan fallback,
        Action<TimeSpan> assign,
        ScrapFileLoggerProvider logger,
        bool isSpecified = false)
    {
        if (raw is not null
            && TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out TimeSpan parsed)
            && parsed > TimeSpan.Zero)
        {
            assign(parsed);
            return;
        }

        if (raw is not null || isSpecified)
        {
            assign(fallback);
            logger.WriteConfigurationFallback(propertyName);
        }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
