namespace Scrap.Platform.Processes;

/// <summary>
/// 抽象 daemon 进程启动，便于 bootstrap 的确定性测试。
/// Abstracts daemon process launch for deterministic bootstrap tests.
/// </summary>
public interface IDaemonProcessLauncher
{
    /// <summary>
    /// 启动 daemon 后立即返回，不等待 ready 或进程退出。
    /// Starts the daemon and returns immediately without waiting for readiness or process exit.
    /// </summary>
    void Start();
}
