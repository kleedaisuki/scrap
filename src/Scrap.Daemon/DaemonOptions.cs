namespace Scrap.Daemon;

/// <summary>
/// 配置 daemon 的本地生命周期与 IPC 执行限制。 / Configures local daemon lifetime and IPC execution limits.
/// </summary>
public sealed class DaemonOptions
{
    /// <summary>
    /// 获取或设置最后一个 client 断开后的空闲退出窗口。 / Gets or sets the idle exit window after the last client disconnects.
    /// </summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 获取或设置空闲状态的检查间隔。 / Gets or sets the interval used to inspect idle state.
    /// </summary>
    public TimeSpan IdlePollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 获取或设置 Generic Host 的停止期限；已接纳的 mutation 仍须完成，不能被该期限中断。<br/>
    /// Gets or sets the Generic Host shutdown timeout; admitted mutations must still finish and are not interrupted by this bound.
    /// </summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 获取或设置单个响应写入的上限，防止不读取的 client 阻塞关闭。
    /// / Gets or sets the response-write bound so a non-reading client cannot block shutdown.
    /// </summary>
    public TimeSpan ResponseWriteTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 验证选项并返回当前实例。 / Validates the options and returns this instance.
    /// </summary>
    /// <returns>经过验证的选项。 / The validated options.</returns>
    /// <exception cref="ArgumentOutOfRangeException">任一时间范围不是正数。 / A duration is not positive.</exception>
    public DaemonOptions Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(IdleTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(IdlePollInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ShutdownTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ResponseWriteTimeout, TimeSpan.Zero);
        return this;
    }
}
