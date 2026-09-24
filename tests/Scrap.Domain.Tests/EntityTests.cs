namespace Scrap.Domain.Tests;

/// <summary>验证 scope 与 record 的不可变更新及展示不变量。 / Verifies immutable updates and presentation invariants for scopes and records.</summary>
public sealed class EntityTests
{
    /// <summary>多值 record 保留顺序、重复项与空字符串，并把展示策略应用于整体。 / Multi-value records preserve order, duplicates, and empty strings while applying presentation to the whole record.</summary>
    [Fact]
    public void RecordPreservesOrderedValuesAndReplacesWholeList()
    {
        DateTimeOffset createdAt = DateTimeOffset.UnixEpoch;
        var record = Record.TryCreate("scope", "key", ["first", "", "first"], Presentation.Masked, createdAt).Value;
        var replaced = record.TryReplace(["next", "tail"], Presentation.Plain, createdAt.AddMinutes(1)).Value;

        Assert.Equal(["first", "", "first"], record.Values.Select(value => value.Value));
        Assert.Equal("first", record.Value.Value);
        Assert.Equal(["next", "tail"], replaced.Values.Select(value => value.Value));
        Assert.Equal(Presentation.Plain, replaced.Presentation);
    }

    /// <summary>列表必须非空，最多 32 项且总 UTF-8 大小不超过 64 KiB。 / Lists are non-empty, capped at 32 items, and at most 64 KiB aggregate UTF-8.</summary>
    [Fact]
    public void RecordValuesEnforceCountAndAggregateBounds()
    {
        Assert.True(RecordValues.TryCreate([new string('a', RecordValues.MaximumAggregateUtf8Bytes)]).IsSuccess);
        Assert.True(RecordValues.TryCreate([]).IsFailure);
        Assert.True(RecordValues.TryCreate(Enumerable.Repeat(string.Empty, RecordValues.MaximumCount + 1).ToArray()).IsFailure);
        Assert.True(RecordValues.TryCreate([new string('a', RecordValues.MaximumAggregateUtf8Bytes), "b"]).IsFailure);
    }

    /// <summary>多字节值按严格 UTF-8 字节数累计，且单项无效 Unicode 的错误仍优先。 / Multibyte values use strict UTF-8 aggregate size, while an item's invalid Unicode error still takes precedence.</summary>
    [Fact]
    public void RecordValuesUseValidatedUtf8SizesForAggregateBoundary()
    {
        var nearlyFull = new string('界', RecordValues.MaximumAggregateUtf8Bytes / 3);
        var atLimit = RecordValues.TryCreate([nearlyFull, "a"]);
        var overLimit = RecordValues.TryCreate([nearlyFull, "ab"]);
        var invalidItem = RecordValues.TryCreate([nearlyFull, "\ud800"]);

        Assert.True(atLimit.IsSuccess);
        Assert.Equal(DomainErrorCode.TextTooLong, overLimit.Error?.Code);
        Assert.Equal("values", overLimit.Error?.Field);
        Assert.Equal(DomainErrorCode.InvalidUnicode, invalidItem.Error?.Code);
        Assert.Equal("values[1]", invalidItem.Error?.Field);
    }

    /// <summary>值列表按顺序而非对象引用比较，且不会混淆不同顺序或重复项。 / Value lists compare by order rather than reference without conflating reorderings or duplicates.</summary>
    [Fact]
    public void RecordValuesHaveOrderedValueEquality()
    {
        var first = RecordValues.TryCreate(["a", "", "a"]).Value;
        var same = RecordValues.TryCreate(["a", "", "a"]).Value;
        var reordered = RecordValues.TryCreate(["", "a", "a"]).Value;
        var changed = RecordValues.TryCreate(["a", "", "b"]).Value;

        Assert.NotSame(first, same);
        Assert.True(first.Equals(same));
        Assert.Equal(first, same);
        Assert.Equal(first.GetHashCode(), same.GetHashCode());
        Assert.NotEqual(first, reordered);
        Assert.NotEqual(first, changed);
        Assert.False(first.Equals(null));
        Assert.Equal("[REDACTED:3]", first.ToString());
    }

    /// <summary>值对象快照不受调用方后续列表修改影响。 / The value-object snapshot is unaffected by later caller-list mutations.</summary>
    [Fact]
    public void RecordValuesCopyCallerList()
    {
        string[] input = ["before"];
        var values = RecordValues.TryCreate(input).Value;

        input[0] = "after";

        Assert.Equal("before", values[0].Value);
    }

    /// <summary>单值创建委托给列表创建，但仍先报告 scope、key 错误。 / Scalar creation delegates to list creation while retaining scope/key error precedence.</summary>
    [Fact]
    public void ScalarCreateSharesValidationAndErrorPrecedenceWithList()
    {
        var now = DateTimeOffset.UnixEpoch;
        var scalar = Record.TryCreate("scope", "key", "secret", Presentation.Masked, now).Value;
        var list = Record.TryCreate("scope", "key", ["secret"], Presentation.Masked, now).Value;

        Assert.Equal(list.Values, scalar.Values);
        Assert.Equal("scope", Record.TryCreate("", "", (string?)null, Presentation.Masked, now).Error?.Field);
        Assert.Equal("key", Record.TryCreate("scope", "", (string?)null, Presentation.Masked, now).Error?.Field);
        Assert.Equal("values", Record.TryCreate("scope", "key", (string?)null, Presentation.Masked, now).Error?.Field);
    }
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

    /// <summary>重建与新建共享验证顺序，且多值重建不改动值、时间戳或旧标量错误字段。 / Restoration shares creation validation order and preserves multi-values, timestamps, and the legacy scalar error field.</summary>
    [Fact]
    public void RestoreSharesCreationValidationAndPreservesMultiValues()
    {
        var createdAt = DateTimeOffset.UnixEpoch;
        var updatedAt = createdAt.AddHours(1);
        var restored = Record.TryRestore(
            "scope", "key", ["one", "", "one"], Presentation.Plain, createdAt, updatedAt).Value;

        Assert.Equal(["one", "", "one"], restored.Values.Select(value => value.Value));
        Assert.Equal(createdAt, restored.CreatedAt);
        Assert.Equal(updatedAt, restored.UpdatedAt);
        Assert.Equal("scope", Record.TryRestore("", "", (string?)null, Presentation.Masked, createdAt, updatedAt).Error?.Field);
        Assert.Equal("key", Record.TryRestore("scope", "", (string?)null, Presentation.Masked, createdAt, updatedAt).Error?.Field);
        Assert.Equal("values", Record.TryRestore("scope", "key", (string?)null, Presentation.Masked, createdAt, updatedAt).Error?.Field);
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
