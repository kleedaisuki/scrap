using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace Scrap.Cli;

/// <summary>
/// 解析、执行并格式化 scrap 命令；所有外部副作用均通过注入接口发生。/
/// Parses, executes, and formats scrap commands; all external side effects cross injected interfaces.
/// </summary>
public static class CliApplication
{
    private const string NoColor = "--no-color";
    private const string Json = "--json";
    private static readonly string[] SearchModeOptions = ["--exact", "--fuzzy", "--regex"];
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    /// <summary>
    /// 执行一次 CLI 调用并返回稳定退出码。/ Executes one CLI invocation and returns a stable exit code.
    /// </summary>
    /// <example>
    /// <code>
    /// var code = await CliApplication.RunAsync(["scope", "list", "--json"], client, environment);
    /// </code>
    /// </example>
    public static async Task<int> RunAsync(
        string[] args,
        IScrapClient client,
        ICliEnvironment environment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(environment);

        try
        {
            return await DispatchAsync(args, client, environment, cancellationToken).ConfigureAwait(false);
        }
        catch (CliUsageException exception)
        {
            await WriteErrorAsync(environment, exception.Message).ConfigureAwait(false);
            return ExitCodes.Usage;
        }
        catch (ScrapClientException exception)
        {
            await WriteErrorAsync(environment, exception.Message).ConfigureAwait(false);
            return MapError(exception.Kind);
        }
        catch (DecoderFallbackException)
        {
            await WriteErrorAsync(environment, "Standard input is not valid UTF-8.").ConfigureAwait(false);
            return ExitCodes.Validation;
        }
        catch (OperationCanceledException)
        {
            await WriteErrorAsync(environment, "Operation cancelled.").ConfigureAwait(false);
            return ExitCodes.Protocol;
        }
        catch (Exception)
        {
            // Never forward exception text: adapters and clipboard libraries can accidentally include payload data.
            await WriteErrorAsync(environment, "The operation failed unexpectedly.").ConfigureAwait(false);
            return ExitCodes.Protocol;
        }
    }

    private static async Task<int> DispatchAsync(
        string[] args,
        IScrapClient client,
        ICliEnvironment environment,
        CancellationToken cancellationToken)
    {
        args = RemoveGlobalNoColor(args);
        if (args.Length == 0 || args is ["--help"] or ["-h"])
        {
            await environment.Output.WriteAsync(HelpText).ConfigureAwait(false);
            return ExitCodes.Success;
        }

        if (args is ["--version"])
        {
            var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
                ?? "unknown";
            await WriteLineAsync(environment.Output, version).ConfigureAwait(false);
            return ExitCodes.Success;
        }

        return args[0] switch
        {
            "scope" => await RunScopeAsync(args[1..], client, environment, cancellationToken).ConfigureAwait(false),
            "set" => await RunSetAsync(args[1..], client, environment, cancellationToken).ConfigureAwait(false),
            "get" => await RunGetAsync(args[1..], client, environment, cancellationToken).ConfigureAwait(false),
            "rename" => await RunRenameAsync(args[1..], client, cancellationToken).ConfigureAwait(false),
            "delete" => await RunDeleteAsync(args[1..], client, cancellationToken).ConfigureAwait(false),
            "list" => await RunListAsync(args[1..], client, environment, cancellationToken).ConfigureAwait(false),
            "find" => await RunFindAsync(args[1..], client, environment, cancellationToken).ConfigureAwait(false),
            "daemon" => await RunDaemonAsync(args[1..], client, environment, cancellationToken).ConfigureAwait(false),
            _ => throw new CliUsageException("Unknown command. Run 'scrap --help' for usage."),
        };
    }

    private static async Task<int> RunScopeAsync(
        string[] args,
        IScrapClient client,
        ICliEnvironment environment,
        CancellationToken cancellationToken)
    {
        var line = CommandLine.Parse(args, Json, NoColor, "--recursive");
        if (line.Operands.Count == 0)
        {
            throw new CliUsageException("A scope subcommand is required.");
        }

        switch (line.Operands[0])
        {
            case "list":
                line.RequireOperands(1);
                Reject(line, "--recursive");
                var scopes = await client.ListScopesAsync(cancellationToken).ConfigureAwait(false);
                await WriteScopesAsync(environment.Output, scopes, line.Has(Json)).ConfigureAwait(false);
                return ExitCodes.Success;
            case "create":
                line.RequireOperands(2);
                Reject(line, Json, "--recursive");
                await client.CreateScopeAsync(line.Operands[1], cancellationToken).ConfigureAwait(false);
                return ExitCodes.Success;
            case "rename":
                line.RequireOperands(3);
                Reject(line, Json, "--recursive");
                await client.RenameScopeAsync(line.Operands[1], line.Operands[2], cancellationToken).ConfigureAwait(false);
                return ExitCodes.Success;
            case "delete":
                line.RequireOperands(2);
                Reject(line, Json);
                var recursive = line.Has("--recursive");
                var deletedCount = await client.DeleteScopeAsync(
                    line.Operands[1],
                    recursive,
                    cancellationToken).ConfigureAwait(false);
                if (recursive)
                {
                    await WriteLineAsync(environment.Output, $"Deleted records: {deletedCount}").ConfigureAwait(false);
                }

                return ExitCodes.Success;
            default:
                throw new CliUsageException("Unknown scope subcommand.");
        }
    }

    private static async Task<int> RunSetAsync(
        string[] args,
        IScrapClient client,
        ICliEnvironment environment,
        CancellationToken cancellationToken)
    {
        var line = CommandLine.Parse(args, "--raw-stdin", "--masked", "--plain", NoColor);
        line.RequireOperands(2);
        if (line.Has("--masked") && line.Has("--plain"))
        {
            throw new CliUsageException("--masked and --plain are mutually exclusive.");
        }

        var value = await ReadValueAsync(environment, line.Has("--raw-stdin"), cancellationToken).ConfigureAwait(false);
        var presentation = line.Has("--plain") ? RecordPresentation.Plain : RecordPresentation.Masked;
        await client.SetRecordAsync(line.Operands[0], line.Operands[1], value, presentation, cancellationToken).ConfigureAwait(false);
        return ExitCodes.Success;
    }

    private static async Task<int> RunGetAsync(
        string[] args,
        IScrapClient client,
        ICliEnvironment environment,
        CancellationToken cancellationToken)
    {
        var line = CommandLine.Parse(args, "--clipboard", NoColor);
        line.RequireOperands(2);
        var result = await client.GetRecordAsync(line.Operands[0], line.Operands[1], cancellationToken).ConfigureAwait(false);
        if (line.Has("--clipboard"))
        {
            await environment.SetClipboardTextAsync(result.Value, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // Deliberately no newline: stdout bytes are the value contract.
            await environment.Output.WriteAsync(result.Value).ConfigureAwait(false);
        }

        return ExitCodes.Success;
    }

    private static async Task<int> RunRenameAsync(string[] args, IScrapClient client, CancellationToken cancellationToken)
    {
        var line = CommandLine.Parse(args, NoColor);
        line.RequireOperands(3);
        await client.RenameRecordAsync(line.Operands[0], line.Operands[1], line.Operands[2], cancellationToken).ConfigureAwait(false);
        return ExitCodes.Success;
    }

    private static async Task<int> RunDeleteAsync(string[] args, IScrapClient client, CancellationToken cancellationToken)
    {
        var line = CommandLine.Parse(args, NoColor);
        line.RequireOperands(2);
        await client.DeleteRecordAsync(line.Operands[0], line.Operands[1], cancellationToken).ConfigureAwait(false);
        return ExitCodes.Success;
    }

    private static async Task<int> RunListAsync(
        string[] args,
        IScrapClient client,
        ICliEnvironment environment,
        CancellationToken cancellationToken)
    {
        var line = CommandLine.Parse(args, Json, NoColor);
        line.RequireOperands(1);
        var records = await client.ListRecordsAsync(line.Operands[0], cancellationToken).ConfigureAwait(false);
        await WriteRecordsAsync(environment.Output, records, line.Has(Json)).ConfigureAwait(false);
        return ExitCodes.Success;
    }

    private static async Task<int> RunFindAsync(
        string[] args,
        IScrapClient client,
        ICliEnvironment environment,
        CancellationToken cancellationToken)
    {
        var line = CommandLine.Parse(
            args,
            "--exact",
            "--fuzzy",
            "--regex",
            "--case-sensitive",
            "--case-insensitive",
            Json,
            NoColor);
        line.RequireOperands(2);

        var modes = SearchModeOptions.Where(line.Has).ToArray();
        if (modes.Length != 1)
        {
            throw new CliUsageException("Exactly one of --exact, --fuzzy, or --regex is required.");
        }

        if (line.Has("--case-sensitive") && line.Has("--case-insensitive"))
        {
            throw new CliUsageException("Case-sensitivity options are mutually exclusive.");
        }

        var mode = modes[0] switch
        {
            "--exact" => SearchMode.Exact,
            "--fuzzy" => SearchMode.Fuzzy,
            _ => SearchMode.Regex,
        };
        var search = new RecordSearch(line.Operands[0], line.Operands[1], mode, line.Has("--case-sensitive"));
        var records = await client.SearchRecordsAsync(search, cancellationToken).ConfigureAwait(false);
        await WriteRecordsAsync(environment.Output, records, line.Has(Json)).ConfigureAwait(false);
        return ExitCodes.Success;
    }

    private static async Task<int> RunDaemonAsync(
        string[] args,
        IScrapClient client,
        ICliEnvironment environment,
        CancellationToken cancellationToken)
    {
        var line = CommandLine.Parse(args, NoColor);
        line.RequireOperands(1);

        switch (line.Operands[0])
        {
            case "ping":
                await client.PingAsync(cancellationToken).ConfigureAwait(false);
                await WriteLineAsync(environment.Output, "pong").ConfigureAwait(false);
                return ExitCodes.Success;
            case "version":
                var version = await client.GetDaemonVersionAsync(cancellationToken).ConfigureAwait(false);
                await WriteLineAsync(
                    environment.Output,
                    $"{version.ApplicationVersion}\tprotocol {version.MinProtocolVersion}..{version.MaxProtocolVersion}").ConfigureAwait(false);
                return ExitCodes.Success;
            case "shutdown":
                await client.ShutdownDaemonAsync(cancellationToken).ConfigureAwait(false);
                return ExitCodes.Success;
            default:
                throw new CliUsageException("Unknown daemon subcommand.");
        }
    }

    private static async Task<string> ReadValueAsync(
        ICliEnvironment environment,
        bool raw,
        CancellationToken cancellationToken)
    {
        if (!environment.IsInputRedirected)
        {
            await environment.ErrorWriter.WriteAsync("Value: ").ConfigureAwait(false);
            var secret = await environment.ReadSecretAsync(cancellationToken).ConfigureAwait(false);
            await environment.ErrorWriter.WriteAsync("\n").ConfigureAwait(false);
            return secret;
        }

        var value = await environment.Input.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        if (raw)
        {
            return value;
        }

        if (value.EndsWith("\r\n", StringComparison.Ordinal))
        {
            return value[..^2];
        }

        return value.EndsWith('\n') ? value[..^1] : value;
    }

    private static async Task WriteScopesAsync(TextWriter output, IReadOnlyList<ScopeItem> scopes, bool json)
    {
        if (json)
        {
            await WriteJsonAsync(output, scopes).ConfigureAwait(false);
            return;
        }

        foreach (var scope in scopes)
        {
            await WriteLineAsync(output, scope.Name).ConfigureAwait(false);
        }
    }

    private static async Task WriteRecordsAsync(TextWriter output, IReadOnlyList<RecordItem> records, bool json)
    {
        if (json)
        {
            await WriteJsonAsync(output, records).ConfigureAwait(false);
            return;
        }

        foreach (var record in records)
        {
            await WriteLineAsync(output, record.Key).ConfigureAwait(false);
        }
    }

    private static async Task WriteJsonAsync<T>(TextWriter output, T value)
    {
        await output.WriteAsync(JsonSerializer.Serialize(value, JsonOptions)).ConfigureAwait(false);
        await output.WriteAsync("\n").ConfigureAwait(false);
    }

    private static Task WriteLineAsync(TextWriter writer, string value) => writer.WriteAsync(value + "\n");

    private static Task WriteErrorAsync(ICliEnvironment environment, string message) =>
        WriteLineAsync(environment.ErrorWriter, $"scrap: {message}");

    private static void Reject(CommandLine line, params string[] options)
    {
        if (options.Any(line.Has))
        {
            throw new CliUsageException("An option is not valid for this command.");
        }
    }

    private static int MapError(ScrapErrorKind kind) => kind switch
    {
        ScrapErrorKind.NotFound => ExitCodes.NotFound,
        ScrapErrorKind.Conflict => ExitCodes.Conflict,
        ScrapErrorKind.Protocol => ExitCodes.Protocol,
        ScrapErrorKind.Store => ExitCodes.Store,
        ScrapErrorKind.Validation => ExitCodes.Validation,
        _ => ExitCodes.Protocol,
    };

    private static string[] RemoveGlobalNoColor(string[] args)
    {
        var normalized = new List<string>(args.Length);
        var optionsEnded = false;
        var found = false;
        foreach (var argument in args)
        {
            optionsEnded |= argument == "--";
            if (!optionsEnded && argument == NoColor)
            {
                if (found)
                {
                    throw new CliUsageException("An option was specified more than once.");
                }

                found = true;
                continue;
            }

            normalized.Add(argument);
        }

        return [.. normalized];
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private const string HelpText = """
        Usage: scrap <command> [options]

        Scope commands:
          scrap scope list [--json]
          scrap scope create <scope>
          scrap scope rename <old> <new>
          scrap scope delete <scope> [--recursive]

        Record commands:
          scrap set <scope> <key> [--raw-stdin] [--masked|--plain]
          scrap get <scope> <key> [--clipboard]
          scrap rename <scope> <old-key> <new-key>
          scrap delete <scope> <key>
          scrap list <scope> [--json]
          scrap find <scope> <query> (--exact|--fuzzy|--regex) [--case-sensitive] [--json]

        Daemon commands:
          scrap daemon ping
          scrap daemon version
          scrap daemon shutdown

        Global options:
          --no-color   Disable ANSI color output.
          -h, --help   Show this help.
          --version    Show the CLI version.
        """;
}
