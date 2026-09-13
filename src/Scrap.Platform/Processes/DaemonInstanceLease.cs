using System.Globalization;
using System.Text;
using Scrap.Platform.Paths;

namespace Scrap.Platform.Processes;

/// <summary>
/// 通过长期持有独占文件句柄表示当前 profile 的 daemon 所有权。
/// Represents daemon ownership for a profile by retaining an exclusive file handle.
/// </summary>
/// <remarks>
/// 锁文件本身可以遗留；所有权只由打开的句柄决定，进程崩溃时操作系统会释放句柄。
/// The file may remain after shutdown; only the open handle conveys ownership, and the OS releases it on process death.
/// </remarks>
public sealed class DaemonInstanceLease : IDisposable
{
    private FileStream? _stream;

    private DaemonInstanceLease(FileStream stream)
    {
        _stream = stream;
    }

    /// <summary>获取当前对象是否仍持有所有权。Gets whether this instance still owns the lease.</summary>
    public bool IsOwner => _stream is not null;

    /// <summary>
    /// 尝试获取指定布局的 daemon 所有权。Attempts to acquire daemon ownership for a layout.
    /// </summary>
    /// <param name="paths">目标 profile 布局。The target profile layout.</param>
    /// <returns>成功时返回 lease；正常竞争失败时返回 <see langword="null"/>。A lease on success, or <see langword="null"/> on ordinary contention.</returns>
    public static DaemonInstanceLease? TryAcquire(ScrapPathLayout paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        PlatformPathPermissions.EnsurePrivateDirectory(paths.RunDirectory);

        FileStream stream;
        try
        {
            stream = new FileStream(
                paths.DaemonLockFile,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 256,
                FileOptions.WriteThrough);
        }
        catch (IOException exception) when (IsLockContention(exception))
        {
            return null;
        }

        try
        {
            if (!OperatingSystem.IsWindows())
            {
                PlatformPathPermissions.EnsurePrivateFile(paths.DaemonLockFile);
            }

            stream.SetLength(0);
            byte[] owner = Encoding.UTF8.GetBytes(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            stream.Write(owner);
            stream.Flush(flushToDisk: true);
            return new DaemonInstanceLease(stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>释放 daemon 所有权。Releases daemon ownership.</summary>
    public void Dispose()
    {
        FileStream? stream = Interlocked.Exchange(ref _stream, null);
        stream?.Dispose();
    }

    private static bool IsLockContention(IOException exception)
    {
        int nativeError = exception.HResult & 0xffff;
        return OperatingSystem.IsWindows()
            ? nativeError is 32 or 33
            : nativeError is 11;
    }
}
