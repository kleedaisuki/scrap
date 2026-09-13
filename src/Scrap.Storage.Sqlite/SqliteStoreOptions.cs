namespace Scrap.Storage.Sqlite;

/// <summary>SQLite store 的稳定配置。 / Stable configuration for the SQLite store.</summary>
public sealed class SqliteStoreOptions
{
    /// <summary>数据库繁忙等待时长；设计默认五秒。 / Database busy wait; the design default is five seconds.</summary>
    public TimeSpan BusyTimeout { get; init; } = TimeSpan.FromSeconds(5);
}
