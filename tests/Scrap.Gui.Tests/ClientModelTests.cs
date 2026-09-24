using Scrap.Gui.Models;

namespace Scrap.Gui.Tests;

/// <summary>
/// 验证 GUI 客户端模型在异步边界保留独立、只读的记录值快照。
/// Verifies that GUI client models retain independent, read-only value snapshots across asynchronous boundaries.
/// </summary>
public sealed class ClientModelTests
{
    /// <summary>
    /// 从客户端读到的值不能由原始集合或只读接口的强制转换在事后修改。
    /// Values received from the client cannot be changed later through the source collection or a cast of the read-only interface.
    /// </summary>
    [Fact]
    public void RecordDetailsOwnsReadOnlyValues()
    {
        string[] source = ["first", "second"];
        var record = new RecordDetails("scope", "key", source, RecordPresentation.Masked, DateTimeOffset.UnixEpoch, 1);

        source[0] = "changed";

        Assert.Equal(["first", "second"], record.Values);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)record.Values)[0] = "changed");
    }

    /// <summary>
    /// 待保存请求必须保持用户提交时的顺序与内容，而非共享可变编辑器数组。
    /// A pending save request must retain the order and content at submission rather than share a mutable editor array.
    /// </summary>
    [Fact]
    public void SaveRecordRequestOwnsReadOnlyValues()
    {
        string[] source = ["first", "second"];
        var request = new SaveRecordRequest("scope", "key", source, RecordPresentation.Plain, null, null);

        source[1] = "changed";

        Assert.Equal(["first", "second"], request.Values);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)request.Values)[1] = "changed");
    }
}
