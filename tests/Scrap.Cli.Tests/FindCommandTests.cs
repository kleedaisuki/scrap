namespace Scrap.Cli.Tests;

/// <summary>验证多 scope find 的命令契约与无歧义输出。 / Verifies the multi-scope find command contract and unambiguous output.</summary>
public sealed class FindCommandTests
{
    /// <summary>重复 --scope 保持选择，文本结果始终输出 scope+key。 / Repeated --scope preserves selection and text output always emits scope+key.</summary>
    [Fact]
    public async Task FindAcceptsRepeatedScopesAndPrintsFullIdentityAsync()
    {
        var client = new CapturingClient();
        var environment = new TestEnvironment();

        int exitCode = await CliApplication.RunAsync(
            ["find", "api", "--fuzzy", "--scope", "z", "--scope", "A"],
            client,
            environment);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(["z", "A"], client.Search!.Scopes);
        Assert.Equal("api", client.Search.Query);
        Assert.Equal("A\tapi\nz\tapi\n", environment.Output.ToString());
    }

    /// <summary>无 --scope 与显式 --all 都使用空集合，且 --all 不可和 --scope 混用。 / Omitted scopes and explicit --all both use an empty collection, while --all cannot be mixed with --scope.</summary>
    [Fact]
    public async Task FindAllScopesUsesEmptyCollectionAndRejectsAmbiguityAsync()
    {
        var client = new CapturingClient();
        var environment = new TestEnvironment();

        Assert.Equal(
            ExitCodes.Success,
            await CliApplication.RunAsync(["find", "api", "--exact", "--all"], client, environment));
        Assert.Empty(client.Search!.Scopes);

        Assert.Equal(
            ExitCodes.Usage,
            await CliApplication.RunAsync(
                ["find", "api", "--exact", "--all", "--scope", "A"],
                client,
                new TestEnvironment()));
    }

    private sealed class CapturingClient : IScrapClient
    {
        public RecordSearch? Search { get; private set; }

        public Task<IReadOnlyList<RecordItem>> SearchRecordsAsync(RecordSearch search, CancellationToken cancellationToken)
        {
            Search = search;
            IReadOnlyList<RecordItem> records =
            [
                new("A", "api", RecordPresentation.Masked, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1),
                new("z", "api", RecordPresentation.Masked, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1),
            ];
            return Task.FromResult(records);
        }

        public Task<IReadOnlyList<ScopeItem>> ListScopesAsync(CancellationToken cancellationToken) => throw Unused();
        public Task CreateScopeAsync(string scope, CancellationToken cancellationToken) => throw Unused();
        public Task RenameScopeAsync(string oldName, string newName, CancellationToken cancellationToken) => throw Unused();
        public Task<int> DeleteScopeAsync(string scope, bool recursive, CancellationToken cancellationToken) => throw Unused();
        public Task SetRecordAsync(string scope, string key, string value, RecordPresentation presentation, CancellationToken cancellationToken) => throw Unused();
        public Task<RecordValue> GetRecordAsync(string scope, string key, CancellationToken cancellationToken) => throw Unused();
        public Task RenameRecordAsync(string scope, string oldKey, string newKey, CancellationToken cancellationToken) => throw Unused();
        public Task DeleteRecordAsync(string scope, string key, CancellationToken cancellationToken) => throw Unused();
        public Task<IReadOnlyList<RecordItem>> ListRecordsAsync(string scope, CancellationToken cancellationToken) => throw Unused();
        public Task PingAsync(CancellationToken cancellationToken) => throw Unused();
        public Task<DaemonVersion> GetDaemonVersionAsync(CancellationToken cancellationToken) => throw Unused();
        public Task ShutdownDaemonAsync(CancellationToken cancellationToken) => throw Unused();
        public Task<bool> ShutdownIfRunningAsync(CancellationToken cancellationToken) => throw Unused();

        private static NotSupportedException Unused() => new("Unused by this focused test.");
    }

    private sealed class TestEnvironment : ICliEnvironment
    {
        public TextReader Input { get; } = new StringReader(string.Empty);
        public StringWriter Output { get; } = new();
        TextWriter ICliEnvironment.Output => Output;
        public TextWriter ErrorWriter { get; } = new StringWriter();
        public bool IsInputRedirected => true;
        public bool IsOutputRedirected => true;
        public Task<string> ReadSecretAsync(int maximumUtf8Bytes, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetClipboardTextAsync(string value, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
