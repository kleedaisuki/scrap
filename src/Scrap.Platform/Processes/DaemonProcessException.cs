namespace Scrap.Platform.Processes;

/// <summary>表示 daemon 子进程无法启动。Indicates that the daemon child process could not be started.</summary>
public sealed class DaemonProcessException : InvalidOperationException
{
    /// <summary>创建进程启动异常。Creates a process-launch exception.</summary>
    /// <param name="message">不含秘密的诊断消息。A diagnostic message without secret material.</param>
    /// <param name="innerException">底层进程异常。The underlying process exception.</param>
    public DaemonProcessException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
