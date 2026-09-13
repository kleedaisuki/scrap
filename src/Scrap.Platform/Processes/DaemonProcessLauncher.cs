using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;

namespace Scrap.Platform.Processes;

/// <summary>
/// 使用绝对可执行文件路径和无 shell 参数启动 daemon。
/// Starts the daemon by absolute executable path with shell-free arguments.
/// </summary>
public sealed class DaemonProcessLauncher : IDaemonProcessLauncher
{
    private static readonly ConcurrentDictionary<int, Task> BackgroundProcesses = new();
    private readonly string _executablePath;
    private readonly string[] _arguments;
    private readonly string? _workingDirectory;

    /// <summary>创建 launcher。Creates a launcher.</summary>
    /// <param name="executablePath">daemon 的绝对路径。Absolute path to the daemon executable.</param>
    /// <param name="arguments">不包含秘密的独立参数。Separate arguments that contain no secrets.</param>
    /// <param name="workingDirectory">可选工作目录。Optional working directory.</param>
    public DaemonProcessLauncher(string executablePath, IEnumerable<string>? arguments = null, string? workingDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (!Path.IsPathFullyQualified(executablePath))
        {
            throw new ArgumentException("The daemon executable path must be absolute.", nameof(executablePath));
        }

        _executablePath = Path.GetFullPath(executablePath);
        _arguments = arguments?.ToArray() ?? [];
        _workingDirectory = workingDirectory is null ? null : Path.GetFullPath(workingDirectory);
    }

    /// <inheritdoc />
    public void Start()
    {
        if (OperatingSystem.IsWindows())
        {
            StartWindows();
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _workingDirectory ?? Path.GetDirectoryName(_executablePath)!,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (string argument in _arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process? process = null;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The operating system returned no process handle.");
            process.StandardInput.Close();
            int processId = process.Id;
            Task observation = DrainAndDisposeAsync(process);
            process = null;
            BackgroundProcesses[processId] = observation;
            _ = observation.ContinueWith(
                static (completed, state) =>
                {
                    _ = BackgroundProcesses.TryRemove((int)state!, out Task? _);
                },
                processId,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or IOException or UnauthorizedAccessException)
        {
            process?.Dispose();
            throw new DaemonProcessException("The Scrap daemon process could not be started.", exception);
        }
    }

    /// <summary>
    /// 通过 Windows ShellExecute 启动隐藏 daemon，避免继承 CLI 的重定向标准句柄。
    /// Starts the hidden daemon through Windows ShellExecute so redirected CLI standard handles are not inherited.
    /// </summary>
    private void StartWindows()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _executablePath,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = _workingDirectory ?? Path.GetDirectoryName(_executablePath)!,
        };

        foreach (string argument in _arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The operating system returned no process handle.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or IOException or UnauthorizedAccessException)
        {
            throw new DaemonProcessException("The Scrap daemon process could not be started.", exception);
        }
    }

    private static async Task DrainAndDisposeAsync(Process process)
    {
        try
        {
            Task output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            Task error = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            await Task.WhenAll(output, error, process.WaitForExitAsync()).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            // The child may close redirected handles during normal shutdown; there is no caller awaiting diagnostics here.
        }
        finally
        {
            process.Dispose();
        }
    }
}
