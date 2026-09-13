namespace Scrap.Domain.Tests;

/// <summary>验证 scope 与 record 的不可变更新及展示不变量。 / Verifies immutable updates and presentation invariants for scopes and records.</summary>
public sealed class EntityTests
{
    /// <summary>验证 scope 重命名不改变原实例或创建时间。 / Verifies that scope rename changes neither the original instance nor creation time.</summary>
    [Fact]
    public void ScopeRenameIsImmutable()
    {
        var createdAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var updatedAt = createdAt.AddMinutes(1);
        var original = Scope.TryCreate("work", createdAt).Value;

        var renamed = original.TryRename("Work", updatedAt).Value;

        Assert.Equal("work", original.Name.Value);
        Assert.Equal("Work", renamed.Name.Value);
        Assert.Equal(createdAt, renamed.CreatedAt);
        Assert.Equal(updatedAt, renamed.UpdatedAt);
    }

    /// <summary>验证 record 的身份大小写敏感且整值替换保留身份。 / Verifies case-sensitive record identity and whole-value replacement preserving identity.</summary>
    [Fact]
    public void RecordReplacementPreservesIdentity()
    {
        var createdAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var updatedAt = createdAt.AddMinutes(1);
        var original = Record.TryCreate("scope", "Key", "old", Presentation.Masked, createdAt).Value;

        var replaced = original.TryReplace("new\nvalue", Presentation.Plain, updatedAt).Value;

        Assert.Same(original.Scope, replaced.Scope);
        Assert.Same(original.Key, replaced.Key);
        Assert.Equal("new\nvalue", replaced.Value.Value);
        Assert.Equal(Presentation.Plain, replaced.Presentation);
        Assert.Equal(createdAt, replaced.CreatedAt);
        Assert.Equal(updatedAt, replaced.UpdatedAt);
    }

    /// <summary>验证未定义的展示值不会进入实体。 / Verifies that undefined presentation values cannot enter an entity.</summary>
    [Fact]
    public void UndefinedPresentationReturnsStructuredError()
    {
        const string secret = "must-not-appear";
        var result = Record.TryCreate(
            "scope",
            "key",
            secret,
            (Presentation)99,
            DateTimeOffset.UtcNow);

        Assert.Equal(DomainErrorCode.InvalidOption, result.Error?.Code);
        Assert.Equal("presentation", result.Error?.Field);
        Assert.DoesNotContain(secret, result.Error?.Message, StringComparison.Ordinal);
    }

    /// <summary>验证持久化重建保留两个时间戳。 / Verifies persisted reconstitution preserves both timestamps.</summary>
    [Fact]
    public void RestorePreservesPersistedTimestamps()
    {
        var createdAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var updatedAt = createdAt.AddDays(2);

        var scope = Scope.TryRestore("scope", createdAt, updatedAt).Value;
        var record = Record.TryRestore(
            "scope", "key", "value", Presentation.Masked, createdAt, updatedAt).Value;

        Assert.Equal(createdAt, scope.CreatedAt);
        Assert.Equal(updatedAt, scope.UpdatedAt);
        Assert.Equal(createdAt, record.CreatedAt);
        Assert.Equal(updatedAt, record.UpdatedAt);
    }

    /// <summary>验证统一身份变更可覆盖 key 与 scope rename，并保持值。 / Verifies unified reidentification covers key and scope rename while preserving the value.</summary>
    [Fact]
    public void ReidentifyChangesBothIdentityPartsWithoutChangingValue()
    {
        var createdAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var original = Record.TryCreate("old-scope", "old-key", "secret", Presentation.Masked, createdAt).Value;

        var changed = original.TryReidentify("new-scope", "new-key", createdAt.AddMinutes(1)).Value;

        Assert.Equal("new-scope", changed.Scope.Value);
        Assert.Equal("new-key", changed.Key.Value);
        Assert.Same(original.Value, changed.Value);
        Assert.Equal(createdAt, changed.CreatedAt);
    }
}
