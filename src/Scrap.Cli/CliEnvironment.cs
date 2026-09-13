using System.Text;
using TextCopy;

namespace Scrap.Cli;

/// <summary>
/// 隔离终端与剪贴板副作用，以便逐字节验证 CLI contract。/
/// Isolates terminal and clipboard side effects so the CLI contract can be verified byte-for-byte.
/// </summary>
public interface ICliEnvironment
{
    /// <summary>获取标准输入。/ Gets standard input.</summary>
    TextReader Input { get; }
    /// <summary>获取标准输出。/ Gets standard output.</summary>
    TextWriter Output { get; }
    /// <summary>获取诊断输出。/ Gets diagnostic output.</summary>
    TextWriter ErrorWriter { get; }
    /// <summary>指示标准输入是否重定向。/ Indicates whether standard input is redirected.</summary>
    bool IsInputRedirected { get; }
    /// <summary>指示标准输出是否重定向。/ Indicates whether standard output is redirected.</summary>
    bool IsOutputRedirected { get; }
    /// <summary>从 TTY 无回显且有界地读取 secret。/ Reads a bounded secret from a TTY without echo.</summary>
    Task<string> ReadSecretAsync(int maximumUtf8Bytes, CancellationToken cancellationToken);
    /// <summary>显式写入平台剪贴板。/ Explicitly writes to the platform clipboard.</summary>
    Task SetClipboardTextAsync(string value, CancellationToken cancellationToken);
}

/// <summary>
/// 使用严格 UTF-8 的 System.Console 和平台剪贴板适配器。/
/// System.Console and platform clipboard adapter using strict UTF-8.
/// </summary>
internal sealed class SystemCliEnvironment : ICliEnvironment
{
    public static SystemCliEnvironment Instance { get; } = new();

    private SystemCliEnvironment()
    {
        var strictUtf8 = new UTF8Encoding(false, true);
        Console.InputEncoding = strictUtf8;
        Console.OutputEncoding = strictUtf8;
    }

    public TextReader Input => Console.In;
    public TextWriter Output => Console.Out;
    public TextWriter ErrorWriter => Console.Error;
    public bool IsInputRedirected => Console.IsInputRedirected;
    public bool IsOutputRedirected => Console.IsOutputRedirected;

    public Task<string> ReadSecretAsync(int maximumUtf8Bytes, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumUtf8Bytes);
        cancellationToken.ThrowIfCancellationRequested();
        var value = new StringBuilder();
        var utf8Bytes = 0;
        var pendingHighSurrogate = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = Console.ReadKey(intercept: true);
            if (key.Key is ConsoleKey.Enter)
            {
                if (pendingHighSurrogate)
                {
                    throw new CliValidationException("Value must contain valid Unicode text.");
                }

                return Task.FromResult(value.ToString());
            }

            if (key.Key is ConsoleKey.Backspace)
            {
                RemoveLastRune(value, ref utf8Bytes, ref pendingHighSurrogate);

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                AppendKeyChar(value, key.KeyChar, maximumUtf8Bytes, ref utf8Bytes, ref pendingHighSurrogate);
            }
        }
    }

    public Task SetClipboardTextAsync(string value, CancellationToken cancellationToken) =>
        ClipboardService.SetTextAsync(value, cancellationToken);

    private static void AppendKeyChar(
        StringBuilder value,
        char character,
        int maximumUtf8Bytes,
        ref int utf8Bytes,
        ref bool pendingHighSurrogate)
    {
        if (char.IsHighSurrogate(character))
        {
            if (pendingHighSurrogate)
            {
                throw new CliValidationException("Value must contain valid Unicode text.");
            }

            value.Append(character);
            pendingHighSurrogate = true;
            return;
        }

        if (char.IsLowSurrogate(character))
        {
            if (!pendingHighSurrogate)
            {
                throw new CliValidationException("Value must contain valid Unicode text.");
            }

            EnsureFits(utf8Bytes, 4, maximumUtf8Bytes);
            value.Append(character);
            utf8Bytes += 4;
            pendingHighSurrogate = false;
            return;
        }

        if (pendingHighSurrogate)
        {
            throw new CliValidationException("Value must contain valid Unicode text.");
        }

        var characterBytes = Utf8Text.GetBmpByteCount(character);
        EnsureFits(utf8Bytes, characterBytes, maximumUtf8Bytes);
        value.Append(character);
        utf8Bytes += characterBytes;
    }

    private static void RemoveLastRune(
        StringBuilder value,
        ref int utf8Bytes,
        ref bool pendingHighSurrogate)
    {
        if (value.Length == 0)
        {
            return;
        }

        var last = value[^1];
        if (pendingHighSurrogate)
        {
            value.Length--;
            pendingHighSurrogate = false;
            return;
        }

        if (char.IsLowSurrogate(last) && value.Length >= 2 && char.IsHighSurrogate(value[^2]))
        {
            value.Length -= 2;
            utf8Bytes -= 4;
            return;
        }

        value.Length--;
        utf8Bytes -= Utf8Text.GetBmpByteCount(last);
    }

    private static void EnsureFits(int currentBytes, int additionalBytes, int maximumUtf8Bytes)
    {
        if (currentBytes > maximumUtf8Bytes - additionalBytes)
        {
            throw Utf8Text.ValueTooLong();
        }
    }
}
