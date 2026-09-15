namespace Scrap.Cli;

/// <summary>
/// 已解析的无值选项与位置参数；解析器绝不回显原始参数。/
/// Parsed flag options and operands; the parser never echoes raw arguments.
/// </summary>
internal sealed class CommandLine
{
    private readonly HashSet<string> options;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> optionValues;

    private CommandLine(
        IReadOnlyList<string> operands,
        HashSet<string> options,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? optionValues = null)
    {
        Operands = operands;
        this.options = options;
        this.optionValues = optionValues ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
    }

    public IReadOnlyList<string> Operands { get; }
    public bool Has(string option) => options.Contains(option);

    /// <summary>获取重复的有值选项，保持命令行顺序。 / Gets repeated valued options in command-line order.</summary>
    public IReadOnlyList<string> Values(string option) =>
        optionValues.TryGetValue(option, out var values) ? values : [];

    /// <summary>
    /// 解析仅含 flag 的命令行，拒绝重复和可能意外承载 secret 的未知选项。/
    /// Parses a flag-only command line, rejecting duplicates and unknown options that might accidentally carry secrets.
    /// </summary>
    public static CommandLine Parse(string[] args, params string[] allowedOptions)
    {
        var allowed = new HashSet<string>(allowedOptions, StringComparer.Ordinal);
        var found = new HashSet<string>(StringComparer.Ordinal);
        var operands = new List<string>();
        var optionsEnded = false;

        foreach (var argument in args)
        {
            if (!optionsEnded && argument == "--")
            {
                optionsEnded = true;
                continue;
            }

            if (optionsEnded || !argument.StartsWith('-') || argument == "-")
            {
                operands.Add(argument);
                continue;
            }

            if (!allowed.Contains(argument))
            {
                throw new CliUsageException("Unknown option.");
            }

            if (!found.Add(argument))
            {
                throw new CliUsageException("An option was specified more than once.");
            }
        }

        return new CommandLine(operands, found);
    }

    /// <summary>
    /// 解析 flag 与可重复的有值选项；有值选项消费紧随其后的一个参数。 /
    /// Parses flags and repeatable valued options; a valued option consumes the immediately following argument.
    /// </summary>
    public static CommandLine ParseWithValues(
        string[] args,
        IReadOnlyCollection<string> allowedFlags,
        params string[] valuedOptions)
    {
        var flags = new HashSet<string>(allowedFlags, StringComparer.Ordinal);
        var valued = new HashSet<string>(valuedOptions, StringComparer.Ordinal);
        var found = new HashSet<string>(StringComparer.Ordinal);
        var values = valued.ToDictionary(
            option => option,
            _ => new List<string>(),
            StringComparer.Ordinal);
        var operands = new List<string>();
        var optionsEnded = false;

        for (var index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            if (!optionsEnded && argument == "--")
            {
                optionsEnded = true;
                continue;
            }

            if (optionsEnded || !argument.StartsWith('-') || argument == "-")
            {
                operands.Add(argument);
                continue;
            }

            if (valued.Contains(argument))
            {
                if (++index >= args.Length)
                {
                    throw new CliUsageException("An option value is missing.");
                }

                values[argument].Add(args[index]);
                continue;
            }

            if (!flags.Contains(argument))
            {
                throw new CliUsageException("Unknown option.");
            }

            if (!found.Add(argument))
            {
                throw new CliUsageException("An option was specified more than once.");
            }
        }

        return new CommandLine(
            operands,
            found,
            values.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value, StringComparer.Ordinal));
    }

    public void RequireOperands(int count)
    {
        if (Operands.Count != count)
        {
            var message = Operands.Count > count
                ? "Too many positional arguments. Secret values must not be passed on the command line."
                : "A required positional argument is missing.";
            throw new CliUsageException(message);
        }
    }
}

/// <summary>不包含用户参数内容的安全用法错误。/ Safe usage error containing no user argument content.</summary>
internal sealed class CliUsageException : Exception
{
    public CliUsageException(string message)
        : base(message)
    {
    }
}

/// <summary>本地输入校验错误。/ A local input-validation error.</summary>
internal sealed class CliValidationException : Exception
{
    public CliValidationException(string message)
        : base(message)
    {
    }
}
