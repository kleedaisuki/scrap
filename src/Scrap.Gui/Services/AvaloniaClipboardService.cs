using Avalonia.Input.Platform;
using Scrap.Gui.Abstractions;

namespace Scrap.Gui.Services;

/// <summary>
/// 将 Avalonia 系统剪贴板适配为可测试边界。
/// Adapts the Avalonia system clipboard to the testable boundary.
/// </summary>
internal sealed class AvaloniaClipboardService : IClipboardService
{
    private readonly Func<IClipboard?> _getClipboard;

    /// <summary>
    /// 创建延迟解析剪贴板的适配器，使窗口尚未 attach 时不会缓存 null。
    /// Creates an adapter that resolves the clipboard lazily instead of caching null before window attachment.
    /// </summary>
    internal AvaloniaClipboardService(Func<IClipboard?> getClipboard)
    {
        _getClipboard = getClipboard;
    }

    /// <inheritdoc />
    public async Task SetTextAsync(string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await GetClipboard().SetTextAsync(text);
    }

    /// <inheritdoc />
    public async Task<string?> GetTextAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? text = await GetClipboard().TryGetTextAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return text;
    }

    /// <inheritdoc />
    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await GetClipboard().ClearAsync();
        cancellationToken.ThrowIfCancellationRequested();
    }

    private IClipboard GetClipboard() => _getClipboard()
        ?? throw new InvalidOperationException("The window is not attached to a platform clipboard.");
}
