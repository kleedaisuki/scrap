using System.Text;

namespace Scrap.Cli;

/// <summary>
/// 对 CLI 边界执行有界、严格的 UTF-8 计数与 Unicode 校验。/
/// Performs bounded strict UTF-8 counting and Unicode validation at the CLI boundary.
/// </summary>
internal static class Utf8Text
{
    private const int ReadBufferChars = 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// 流式读取文本，最多保留指定 UTF-8 字节数，并在第一个越界 scalar 立即停止。/
    /// Streams text up to the UTF-8 byte limit and stops at the first overflowing scalar.
    /// </summary>
    public static async Task<BoundedText> ReadBoundedAsync(
        TextReader reader,
        int maximumUtf8Bytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumUtf8Bytes);

        var result = new StringBuilder(Math.Min(maximumUtf8Bytes, ReadBufferChars));
        var buffer = new char[ReadBufferChars];
        var byteCount = 0;
        char? pendingHighSurrogate = null;

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (pendingHighSurrogate is not null)
                {
                    throw InvalidUnicode();
                }

                return new BoundedText(result.ToString(), byteCount);
            }

            for (var index = 0; index < read; index++)
            {
                var character = buffer[index];
                if (pendingHighSurrogate is not null)
                {
                    if (!char.IsLowSurrogate(character))
                    {
                        throw InvalidUnicode();
                    }

                    EnsureFits(byteCount, 4, maximumUtf8Bytes);
                    result.Append(pendingHighSurrogate.Value);
                    result.Append(character);
                    byteCount += 4;
                    pendingHighSurrogate = null;
                    continue;
                }

                if (char.IsHighSurrogate(character))
                {
                    pendingHighSurrogate = character;
                    continue;
                }

                if (char.IsLowSurrogate(character))
                {
                    throw InvalidUnicode();
                }

                var characterBytes = GetBmpByteCount(character);
                EnsureFits(byteCount, characterBytes, maximumUtf8Bytes);
                result.Append(character);
                byteCount += characterBytes;
            }
        }
    }

    /// <summary>校验字符串良构且不超过字节上限。/ Validates well-formed text within a byte limit.</summary>
    public static void Validate(string value, int maximumUtf8Bytes)
    {
        ArgumentNullException.ThrowIfNull(value);
        int byteCount;
        try
        {
            byteCount = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException)
        {
            throw InvalidUnicode();
        }

        if (byteCount > maximumUtf8Bytes)
        {
            throw ValueTooLong();
        }
    }

    /// <summary>拒绝 API 注入时可能出现的孤立 UTF-16 代理项。/ Rejects isolated UTF-16 surrogates possible through API injection.</summary>
    public static void ValidateArguments(IEnumerable<string> arguments)
    {
        foreach (var argument in arguments)
        {
            try
            {
                _ = StrictUtf8.GetByteCount(argument);
            }
            catch (EncoderFallbackException)
            {
                throw new CliValidationException("Command-line arguments must contain valid Unicode text.");
            }
        }
    }

    /// <summary>返回 BMP scalar 的 UTF-8 宽度；代理项须由调用方先处理。/ Returns UTF-8 width for a BMP scalar; callers handle surrogates first.</summary>
    public static int GetBmpByteCount(char character) => character switch
    {
        <= '\u007f' => 1,
        <= '\u07ff' => 2,
        _ => 3,
    };

    /// <summary>构造不含 value 的稳定超限错误。/ Creates a stable oversize error without the value.</summary>
    public static CliValidationException ValueTooLong() =>
        new("Value must be at most 65536 UTF-8 bytes.");

    private static void EnsureFits(int currentBytes, int additionalBytes, int maximumUtf8Bytes)
    {
        if (currentBytes > maximumUtf8Bytes - additionalBytes)
        {
            throw ValueTooLong();
        }
    }

    private static CliValidationException InvalidUnicode() =>
        new("Value must contain valid Unicode text.");
}

/// <summary>有界文本及其 UTF-8 字节数。/ Bounded text and its UTF-8 byte count.</summary>
internal sealed record BoundedText(string Value, int Utf8ByteCount);
