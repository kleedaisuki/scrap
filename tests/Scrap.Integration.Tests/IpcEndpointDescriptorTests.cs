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

        Assert.Matches(new Regex(@"^LOCAL\\scrap-[0-9a-f]{32}$", RegexOptions.CultureInvariant), lowerName);
        Assert.Equal(lowerName, upperName);
    }

    /// <summary>
    /// Unix endpoint 保持 rooted socket path，不得引入 Windows namespace prefix。
    /// Unix endpoints remain rooted socket paths and never gain a Windows namespace prefix.
    /// </summary>
    [Fact]
    public void UnixPipeNameRemainsRootedSocketPath()
    {
        var paths = new ScrapPathLayout(Path.Combine(Path.GetTempPath(), "scrap-ipc-tests", "profile"));

        string pipeName = IpcEndpointDescriptor.CreatePipeName(paths, "user-42", isWindows: false);

        Assert.StartsWith(paths.RunDirectory + Path.DirectorySeparatorChar, pipeName, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"ipc-[0-9a-f]{8}\.sock$", RegexOptions.CultureInvariant), pipeName);
        Assert.DoesNotContain(@"LOCAL\", pipeName, StringComparison.Ordinal);
    }
}
