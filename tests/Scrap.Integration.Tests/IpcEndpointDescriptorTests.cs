using System.Text.RegularExpressions;
using Scrap.Platform.Ipc;
using Scrap.Platform.Paths;

namespace Scrap.Integration.Tests;

/// <summary>
/// 验证 Windows 打包应用与 Unix socket 的确定性 endpoint 命名合约。
/// Verifies deterministic endpoint naming contracts for packaged Windows apps and Unix sockets.
/// </summary>
public sealed class IpcEndpointDescriptorTests
{
    /// <summary>
    /// 获取不受宿主 TMPDIR 长度影响的短绝对测试目录。 / Gets a short absolute test directory independent of the host TMPDIR length.
    /// </summary>
    private static string ShortRuntimeDirectory(string name) =>
        Path.Combine(Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()))!, name);

    /// <summary>
    /// Windows endpoint 使用 MSIX 可访问的 <c>LOCAL\</c> namespace，且 profile 大小写不影响名称。
    /// Windows endpoints use the MSIX-accessible <c>LOCAL\</c> namespace and ignore profile casing.
    /// </summary>
    [Fact]
    public void WindowsPipeNameUsesLocalNamespaceAndStableHash()
    {
        var lowerPaths = new ScrapPathLayout(Path.Combine(Path.GetTempPath(), "scrap-ipc-tests", "profile"));
        var upperPaths = new ScrapPathLayout(lowerPaths.RootDirectory.ToUpperInvariant());

        string lowerName = IpcEndpointDescriptor.CreatePipeName(lowerPaths, "user-42", isWindows: true);
        string upperName = IpcEndpointDescriptor.CreatePipeName(upperPaths, "user-42", isWindows: true);
        string nameWithUnixInputs = IpcEndpointDescriptor.CreatePipeName(
            lowerPaths,
            "user-42",
            isWindows: true,
            xdgRuntimeDirectory: "ignored-relative-xdg",
            temporaryDirectory: "ignored-relative-temp");

        Assert.Matches(new Regex(@"^LOCAL\\scrap-[0-9a-f]{32}$", RegexOptions.CultureInvariant), lowerName);
        Assert.Equal(lowerName, upperName);
        Assert.Equal(lowerName, nameWithUnixInputs);
    }

    /// <summary>
    /// Unix endpoint 保持 rooted socket path，不得引入 Windows namespace prefix。
    /// Unix endpoints remain rooted socket paths and never gain a Windows namespace prefix.
    /// </summary>
    [Fact]
    public void UnixPipeNameUsesPrivateXdgRuntimeSubdirectory()
    {
        var paths = new ScrapPathLayout(Path.Combine(Path.GetTempPath(), "scrap-ipc-tests", "profile"));
        string xdg = ShortRuntimeDirectory("scrap-xdg-tests");

        string pipeName = IpcEndpointDescriptor.CreatePipeName(paths, "user-42", isWindows: false, xdg, Path.GetTempPath());

        Assert.StartsWith(Path.Combine(xdg, "scrap") + Path.DirectorySeparatorChar, pipeName, StringComparison.Ordinal);
        Assert.True(Path.IsPathFullyQualified(pipeName));
        Assert.Matches(new Regex(@"ipc-[0-9a-f]{8}\.sock$", RegexOptions.CultureInvariant), pipeName);
        Assert.DoesNotContain(@"LOCAL\", pipeName, StringComparison.Ordinal);
    }

    /// <summary>
    /// XDG 缺失时，Unix endpoint 使用按稳定用户标识隔离的临时目录。
    /// When XDG is absent, Unix endpoints use a temporary directory isolated by stable user identity.
    /// </summary>
    [Fact]
    public void UnixPipeNameFallsBackToIdentityIsolatedTemporaryDirectory()
    {
        var paths = new ScrapPathLayout(Path.Combine(Path.GetTempPath(), "scrap-ipc-tests", "profile"));
        string temporaryDirectory = ShortRuntimeDirectory("scrap-runtime-tests");

        string first = IpcEndpointDescriptor.CreatePipeName(paths, "user-42", isWindows: false, null, temporaryDirectory);
        string sameUser = IpcEndpointDescriptor.CreatePipeName(paths, "user-42", isWindows: false, " ", temporaryDirectory);
        string otherUser = IpcEndpointDescriptor.CreatePipeName(paths, "user-43", isWindows: false, null, temporaryDirectory);

        Assert.Equal(first, sameUser);
        Assert.StartsWith(temporaryDirectory + Path.DirectorySeparatorChar + "scrap-", first, StringComparison.Ordinal);
        Assert.NotEqual(Path.GetDirectoryName(first), Path.GetDirectoryName(otherUser));
    }

    /// <summary>
    /// 相对 XDG 路径无效，必须回退到绝对临时目录。
    /// Relative XDG paths are invalid and must fall back to the absolute temporary directory.
    /// </summary>
    [Fact]
    public void UnixPipeNameRejectsRelativeXdgByUsingFallback()
    {
        var paths = new ScrapPathLayout(Path.Combine(Path.GetTempPath(), "scrap-ipc-tests", "profile"));
        string temporaryDirectory = ShortRuntimeDirectory("scrap-runtime-tests");

        string pipeName = IpcEndpointDescriptor.CreatePipeName(paths, "user-42", isWindows: false, "relative/runtime", temporaryDirectory);

        Assert.StartsWith(temporaryDirectory + Path.DirectorySeparatorChar, pipeName, StringComparison.Ordinal);
        Assert.True(Path.IsPathFullyQualified(pipeName));
    }

    /// <summary>
    /// 同一 runtime 目录中的不同 profile 必须获得不同 socket endpoint。
    /// Different profiles in one runtime directory must receive distinct socket endpoints.
    /// </summary>
    [Fact]
    public void UnixPipeNameKeepsProfilesIsolated()
    {
        string xdg = ShortRuntimeDirectory("scrap-xdg-tests");
        var firstPaths = new ScrapPathLayout(Path.Combine(Path.GetTempPath(), "scrap-ipc-tests", "profile-a"));
        var secondPaths = new ScrapPathLayout(Path.Combine(Path.GetTempPath(), "scrap-ipc-tests", "profile-b"));

        string first = IpcEndpointDescriptor.CreatePipeName(firstPaths, "user-42", isWindows: false, xdg, Path.GetTempPath());
        string second = IpcEndpointDescriptor.CreatePipeName(secondPaths, "user-42", isWindows: false, xdg, Path.GetTempPath());

        Assert.Equal(Path.GetDirectoryName(first), Path.GetDirectoryName(second));
        Assert.NotEqual(first, second);
    }

    /// <summary>
    /// Unix socket 路径不得超过跨平台保守的 UTF-8 字节上限。
    /// Unix socket paths must not exceed the conservative cross-platform UTF-8 byte limit.
    /// </summary>
    [Fact]
    public void UnixPipeNameFallsBackWhenXdgWouldExceedSocketLimit()
    {
        var paths = new ScrapPathLayout(Path.Combine(Path.GetTempPath(), "scrap-ipc-tests", "profile"));
        string longXdg = Path.Combine(Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()))!, new string('x', 110));
        string temporaryDirectory = ShortRuntimeDirectory("short-runtime");

        string pipeName = IpcEndpointDescriptor.CreatePipeName(paths, "user-42", isWindows: false, longXdg, temporaryDirectory);

        Assert.StartsWith(temporaryDirectory + Path.DirectorySeparatorChar, pipeName, StringComparison.Ordinal);
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(pipeName), 1, 100);
    }

    /// <summary>
    /// 当 XDG 不可用且回退路径也过长时，应在 bind 前拒绝 endpoint。
    /// When XDG is unavailable and even the fallback path is too long, the endpoint is rejected before bind.
    /// </summary>
    [Fact]
    public void UnixPipeNameRejectsOverlongFallbackPath()
    {
        var paths = new ScrapPathLayout(Path.Combine(Path.GetTempPath(), "scrap-ipc-tests", "profile"));
        string longTemporaryDirectory = Path.Combine(Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()))!, new string('x', 110));

        PlatformPathException exception = Assert.Throws<PlatformPathException>(() =>
            IpcEndpointDescriptor.CreatePipeName(paths, "user-42", isWindows: false, null, longTemporaryDirectory));

        Assert.Contains("too long", exception.Message, StringComparison.Ordinal);
    }
}
