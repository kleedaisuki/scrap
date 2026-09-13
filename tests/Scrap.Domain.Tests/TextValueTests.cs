using System.Text;

namespace Scrap.Domain.Tests;

/// <summary>验证领域文本值的 UTF-8 边界与身份语义。 / Verifies UTF-8 boundaries and identity semantics of domain text values.</summary>
public sealed class TextValueTests
{
    /// <summary>验证名称按 UTF-8 字节而非 UTF-16 长度限制。 / Verifies that names are limited by UTF-8 bytes rather than UTF-16 length.</summary>
    [Fact]
    public void NamesUseUtf8ByteLimit()
    {
        var exactAscii = ScopeName.TryCreate(new string('a', ScopeName.MaximumUtf8Bytes));
        var oversizedAscii = ScopeName.TryCreate(new string('a', ScopeName.MaximumUtf8Bytes + 1));
        var exactMultibyte = RecordKey.TryCreate(new string('界', RecordKey.MaximumUtf8Bytes / 3));
        var oversizedMultibyte = RecordKey.TryCreate(new string('界', (RecordKey.MaximumUtf8Bytes / 3) + 1));

        Assert.True(exactAscii.IsSuccess);
        Assert.True(exactMultibyte.IsSuccess);
        Assert.Equal(DomainErrorCode.TextTooLong, oversizedAscii.Error?.Code);
        Assert.Equal(DomainErrorCode.TextTooLong, oversizedMultibyte.Error?.Code);
    }

    /// <summary>验证 identity 大小写敏感且不裁剪、不规范化。 / Verifies case-sensitive identity without trimming or normalization.</summary>
    [Fact]
    public void IdentityIsOrdinalAndPreservesInput()
    {
        var padded = ScopeName.Create(" cloud ");
        var upper = RecordKey.Create("API_TOKEN");
        var lower = RecordKey.Create("api_token");
        var composed = RecordKey.Create("caf\u00e9");
        var decomposed = RecordKey.Create("cafe\u0301");

        Assert.Equal(" cloud ", padded.Value);
        Assert.NotEqual(upper, lower);
        Assert.NotEqual(composed, decomposed);
        Assert.Equal("caf\u00e9", composed.Value);
        Assert.Equal("cafe\u0301", decomposed.Value);
    }

    /// <summary>验证空名称与不可编码的代理项返回结构化错误。 / Verifies structured errors for empty names and unencodable surrogates.</summary>
    [Fact]
    public void InvalidTextReturnsStructuredError()
    {
        var empty = ScopeName.TryCreate(string.Empty);
        var invalidUnicode = RecordKey.TryCreate("broken\ud800");

        Assert.Equal(new DomainError(DomainErrorCode.Required, "scope is required.", "scope"), empty.Error);
        Assert.Equal(DomainErrorCode.InvalidUnicode, invalidUnicode.Error?.Code);
        Assert.Equal("key", invalidUnicode.Error?.Field);
    }

    /// <summary>验证抛出式工厂保留结构化错误。 / Verifies that the throwing factory retains the structured error.</summary>
    [Fact]
    public void ThrowingFactoryCarriesStructuredError()
    {
        var exception = Assert.Throws<DomainException>(() => ScopeName.Create(string.Empty));

        Assert.Equal(DomainErrorCode.Required, exception.Error.Code);
        Assert.Equal("scope", exception.Error.Field);
    }

    /// <summary>验证值允许空白和换行，但不会通过 ToString 泄露。 / Verifies that values allow whitespace and newlines but are never leaked through ToString.</summary>
    [Fact]
    public void ValuePreservesTextAndRedactsStringConversion()
    {
        const string secret = "  first\r\nsecond\n";
        var value = RecordValue.Create(secret);
        var empty = RecordValue.TryCreate(string.Empty);

        Assert.Equal(secret, value.Value);
        Assert.Equal("[REDACTED]", value.ToString());
        Assert.True(empty.IsSuccess);
    }

    /// <summary>验证 64 KiB 值边界。 / Verifies the 64-KiB value boundary.</summary>
    [Fact]
    public void ValueEnforcesExactUtf8Boundary()
    {
        var exact = RecordValue.TryCreate(new string('x', RecordValue.MaximumUtf8Bytes));
        var oversized = RecordValue.TryCreate(new string('x', RecordValue.MaximumUtf8Bytes + 1));

        Assert.True(exact.IsSuccess);
        Assert.Equal(DomainErrorCode.TextTooLong, oversized.Error?.Code);
        Assert.DoesNotContain(new string('x', 32), oversized.Error?.Message, StringComparison.Ordinal);
    }
}
