namespace Scrap.Platform.Secrets;

/// <summary>
/// 在当前操作系统账户的受保护存储中持久化 opaque 主密钥。
/// Persists an opaque master key in protected storage belonging to the current OS account.
/// </summary>
public interface IMasterKeyProvider
{
    /// <summary>
    /// 读取主密钥；尚未初始化时返回 <see langword="null"/>，provider 不可用时抛出异常。
    /// Reads the master key; returns <see langword="null"/> when uninitialized and throws when the provider is unavailable.
    /// </summary>
    /// <param name="cancellationToken">在可安全取消且不会产生模糊提交状态的位置观察的令牌。Cancellation observed only where cancellation cannot create an ambiguous commit.</param>
    /// <returns>由调用者拥有的密钥副本，或 <see langword="null"/>。A caller-owned key copy, or <see langword="null"/>.</returns>
    ValueTask<byte[]?> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 原子地保存或替换主密钥。Atomically stores or replaces the master key.
    /// </summary>
    /// <param name="key">仅在调用期间借用的密钥。The key, borrowed only for the duration of the call.</param>
    /// <param name="cancellationToken">在可安全取消且不会产生模糊提交状态的位置观察的令牌。Cancellation observed only where cancellation cannot create an ambiguous commit.</param>
    ValueTask StoreAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default);
}
