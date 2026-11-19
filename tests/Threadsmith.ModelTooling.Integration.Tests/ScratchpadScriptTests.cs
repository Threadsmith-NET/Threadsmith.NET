namespace Threadsmith.ModelTooling.Integration.Tests;

using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Scripting.Worker;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

/// <summary>Scoped file access through the real worker and shared reader policy.</summary>
public sealed partial class ScratchpadScriptTests : IDisposable
{
    private readonly string _root;

    /// <summary>Initializes a new instance of the <see cref="ScratchpadScriptTests"/> class.</summary>
    public ScratchpadScriptTests()
    {
        var temporary = Path.GetFullPath(Path.GetTempPath());
        var physical = Path.GetPathRoot(temporary)!;
        foreach (var segment in temporary[physical.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = new DirectoryInfo(Path.Combine(physical, segment));
            physical = directory.ResolveLinkTarget(true)?.FullName ?? directory.FullName;
        }

        _root = Path.Combine(physical, $"threadsmith-script-files-{Guid.NewGuid():N}");
    }

    /// <summary>Ordinary computation, JSON parsing, and hashing use the real worker's default imports and permissions.</summary>
    [Theory]
    [InlineData("using (var doc = JsonDocument.Parse(\"{\\\"value\\\":25}\")) { return Math.Sqrt(doc.RootElement.GetProperty(\"value\").GetInt32()); }", "5")]
    [InlineData("Convert.ToHexStringLower(SHA256.HashData(source: new byte[] { 97, 98, 99 }))", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    public async Task WorkerSupportsDefaultComputation(string code, string expected)
    {
        var scope = CreateScope();
        var engine = new CSharpScriptEngine(new ProcessManager(new PassthroughSanitizer(), NullLogger<ProcessManager>.Instance, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow)), new ToolConfig(new ConfigurationBuilder().Build()), Path.Combine(AppContext.BaseDirectory, "Threadsmith.Scripting.Worker.dll"), workerOptions: new() { NumberOfWarmWorkersInPool = 0 });
        var context = new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), CreateContext(scope)) { ExecutionTimeout = Timeout.InfiniteTimeSpan };

        var result = await engine.ExecuteAsync(code, ScriptKind.Statement, context, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.Equal(expected, result.Output);
    }

    /// <summary>Imports and namespace qualifiers do not grant access to restricted APIs.</summary>
    [Theory]
    [InlineData("Console.WriteLine(25)", null, "Console access is prohibited. Return the result as the script's final expression.")]
    [InlineData("System.Console.WriteLine(25)", null, "Console access is prohibited. Return the result as the script's final expression.")]
    [InlineData("Environment.CurrentDirectory", null, "System.Environment")]
    [InlineData("System.IO.File.Exists(\"input\")", null, "System.IO")]
    [InlineData("new System.Text.StringBuilder()", null, "Namespace 'System.Text' is not permitted")]
    [InlineData("System.Text.Json.JsonDocument.Parse(\"{}\")", "System.Text.Json.Serialization", "Namespace 'System.Text.Json' is not permitted")]
    [InlineData("dynamic value = 25; value", null, "The 'dynamic' construct is prohibited")]
    [InlineData("#r \"System.Console\"\n25", null, "Script directives are prohibited")]
    [InlineData("nameof(CryptoConfig.CreateFromName)", null, "Only SHA256.HashData(byte[]) is permitted for cryptography.")]
    [InlineData("nameof(SHA256.Create)", null, "Only SHA256.HashData(byte[]) is permitted for cryptography.")]
    [InlineData("using Digest = System.Security.Cryptography.SHA256; nameof(Digest.Create)", "System.Security.Cryptography", "Only SHA256.HashData(byte[]) is permitted for cryptography.")]
    [InlineData("nameof(HashAlgorithm.Create)", "System.Security.Cryptography", "Only SHA256.HashData(byte[]) is permitted for cryptography.")]
    public async Task WorkerExplainsRejectedCapabilities(string code, string? allowedAssemblies, string expectedError)
    {
        var scope = CreateScope();
        var builder = new ConfigurationBuilder();
        if (allowedAssemblies is not null)
        {
            builder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["tools:config:csharp_script:allowed_assemblies"] = allowedAssemblies,
                ["tools:config:csharp_script:assembly_config_action"] = "override",
            });
        }

        var engine = new CSharpScriptEngine(new ProcessManager(new PassthroughSanitizer(), NullLogger<ProcessManager>.Instance, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow)), new ToolConfig(builder.Build()), Path.Combine(AppContext.BaseDirectory, "Threadsmith.Scripting.Worker.dll"), workerOptions: new() { NumberOfWarmWorkersInPool = 0 });
        var context = new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), CreateContext(scope)) { ExecutionTimeout = Timeout.InfiniteTimeSpan };

        var result = await engine.ExecuteAsync(code, ScriptKind.Statement, context, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains(expectedError, result.Error, StringComparison.Ordinal);
    }

    /// <summary>Verifies the file execution contract through observable effects.</summary>
    [Fact]
    public async Task FilesReadAuthorizedInputsAndConfineAllWrites()
    {
        var scope = CreateScope();
        var inputRoot = Path.Combine(_root, "inputs");
        Directory.CreateDirectory(inputRoot);
        var input = Path.Combine(inputRoot, "data.bin");
        await File.WriteAllBytesAsync(input, [1, 2, 3, 4, 5], TestContext.Current.CancellationToken);
        scope = scope with { ReadRoots = [inputRoot], MaximumWriteBytes = 64 };
        var files = new ScriptGlobals(scope, TestContext.Current.CancellationToken).Scratchpad;
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, files.ReadChunks(input, 2).SelectMany(bytes => bytes));
        await files.WriteAllBytesAsync("nested/output.bin", [1, 2], cancellationToken: TestContext.Current.CancellationToken);
        await files.AppendBytesAsync("nested/output.bin", [3], TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 1, 2, 3 }, files.ReadChunks("nested/output.bin").Single());
        await Assert.ThrowsAsync<IOException>(() => files.WriteAllTextAsync("nested/output.bin", "replacement", cancellationToken: TestContext.Current.CancellationToken));
        await files.WriteAllTextAsync("nested/output.bin", "replaced", overwrite: true, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("replaced", files.ReadLines("nested/output.bin").Single());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => files.WriteAllTextAsync(input, "no", cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => files.WriteAllTextAsync("../escape.txt", "no", cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => files.WriteAllTextAsync("script.PY", "no", cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<IOException>(() => files.AppendBytesAsync("large.bin", new byte[64], TestContext.Current.CancellationToken));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, await File.ReadAllBytesAsync(input, TestContext.Current.CancellationToken));
    }

    /// <summary>Verifies the file execution contract through observable effects.</summary>
    [Fact]
    public void ReadsShareConfiguredAuthorityAndRejectUnconfiguredOrProhibitedInputs()
    {
        var scope = CreateScope();
        var external = Path.Combine(_root, "inputs");
        Directory.CreateDirectory(external);
        var input = Path.Combine(external, "input.txt");
        File.WriteAllText(input, "input");
        var policy = new EffectiveSessionPolicy
        {
            BindingId = Guid.NewGuid(),
            Generation = 1,
            Fingerprint = "test",
            RepositoryPath = scope.RepositoryPath,
            ReadFolders = [external],
        };
        var context = CreateContext(scope) with { SecurityPolicy = policy };
        Assert.Equal(input, ScriptFilePolicy.ValidateRead(input, context));
        var files = new ScriptGlobals(ScriptFilePolicy.CreateScope(context, 100), TestContext.Current.CancellationToken).Scratchpad;
        Assert.Equal("input", files.ReadLines(input).Single());
        Assert.Throws<UnauthorizedAccessException>(() => ScriptFilePolicy.ValidateRead(input, context with { SecurityPolicy = policy with { ReadFolders = [], WriteFolders = [external] } }));
        Assert.Throws<UnauthorizedAccessException>(() => ScriptFilePolicy.ValidateRead(input, context with { ProhibitedPaths = ["../inputs/**"] }));
        Assert.Throws<UnauthorizedAccessException>(() => files.ReadLines(Path.Combine(_root, "unconfigured.txt")).ToArray());
    }

    /// <summary>Verifies the file execution contract through observable effects.</summary>
    [Fact]
    public async Task CanceledWritesDoNotCreateOutputs()
    {
        var scope = CreateScope();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var files = new ScriptGlobals(scope, cancellation.Token).Scratchpad;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => files.WriteAllTextAsync("cancelled.txt", "no", cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(scope.ScratchpadPath, "cancelled.txt")));
    }

    /// <summary>Linked installation ancestors are trusted, but links inside authority roots are rejected.</summary>
    [Fact]
    public async Task FileAuthorityChecksLinksWithinRepositoryBoundary()
    {
        var scope = CreateScope();
        var alias = Path.Combine(_root, "parent-alias");
        try
        {
            Directory.CreateSymbolicLink(alias, _root);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Skip("Creating symbolic links is unavailable on this runner.");
            return;
        }

        try
        {
            var repository = Path.Combine(alias, "repo");
            var aliasedScope = scope with { RepositoryPath = repository, ScratchpadPath = Path.Combine(repository, ".threadsmith", ".scratchpad"), ReadRoots = [repository] };
            var files = new ScriptGlobals(aliasedScope, TestContext.Current.CancellationToken).Scratchpad;
            await files.WriteAllTextAsync("result.txt", "written", cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("written", files.ReadLines("result.txt").Single());
            Assert.Equal(Path.Combine(aliasedScope.ScratchpadPath, "result.txt"), ScriptFilePolicy.ValidateRead(Path.Combine(aliasedScope.ScratchpadPath, "result.txt"), CreateContext(aliasedScope)));

            var input = Path.Combine(scope.RepositoryPath, "input.txt");
            await File.WriteAllTextAsync(input, "repository", TestContext.Current.CancellationToken);
            Assert.Equal(Path.Combine(repository, "input.txt"), ScriptFilePolicy.ValidateRead("input.txt", CreateContext(aliasedScope)));
            var source = Path.Combine(repository, "source.csx");
            await File.WriteAllTextAsync(source, "42", TestContext.Current.CancellationToken);
            var process = new RecordingProcessManager();
            await using var events = new DomainEventStream();
            var pipeline = FileInputPipeline(events, process);
            var first = await pipeline.InvokeAsync(FileRequest(aliasedScope, JsonSerializer.Serialize(new { scriptPath = source })), TestContext.Current.CancellationToken);
            Assert.True(first.Succeeded, first.Error);
            var output = JsonSerializer.Deserialize<CSharpScriptOutput>(first.ResultJson!)!;
            var rerun = await pipeline.InvokeAsync(FileRequest(aliasedScope, JsonSerializer.Serialize(new { scriptPath = output.ScriptPath })), TestContext.Current.CancellationToken);
            Assert.True(rerun.Succeeded, rerun.Error);
            Assert.Equal("42", rerun.SourceDetail!.Source);

            var innerLink = Path.Combine(scope.RepositoryPath, "linked");
            Directory.CreateSymbolicLink(innerLink, scope.RepositoryPath);
            try
            {
                var narrowed = Path.Combine(repository, "linked", ".threadsmith");
                Assert.Throws<UnauthorizedAccessException>(() => ScopedFilePaths.ValidateRead(Path.Combine(narrowed, ".scratchpad", "result.txt"), repository, repository, [narrowed], []));
            }
            finally
            {
                Directory.Delete(innerLink);
            }

            var control = Path.Combine(scope.RepositoryPath, ".threadsmith");
            var movedControl = Path.Combine(_root, "moved-control");
            Directory.Move(control, movedControl);
            Directory.CreateSymbolicLink(control, movedControl);
            try
            {
                Assert.Throws<UnauthorizedAccessException>(() => ToolPathRules.NormalizeScratchpadPath(Path.Combine(aliasedScope.ScratchpadPath, "result.txt"), CreateContext(aliasedScope)));
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => files.WriteAllTextAsync("escaped.txt", "no", cancellationToken: TestContext.Current.CancellationToken));
            }
            finally
            {
                Directory.Delete(control);
                Directory.Move(movedControl, control);
            }
        }
        finally
        {
            Directory.Delete(alias);
        }
    }

    /// <summary>Verifies the file execution contract through observable effects.</summary>
    [Fact]
    public void ProcessScratchpadDirectoryDoesNotBypassTrustOrApproval()
    {
        var scope = CreateScope();
        var shell = OperatingSystem.IsWindows() ? "powershell" : "bash";
        var tool = new RunProcessTool(new UnusedProcessManager(), TestPromptLoader.Instance, shellExecutable: shell);
        var input = new RunProcessInput { Command = "dotnet --version", WorkingDirectory = scope.ScratchpadPath };
        var policy = ShellCommandPolicyTests.Policy(scope.RepositoryPath, shell) with { ReviewTools = ["run_process"] };
        var context = CreateContext(scope) with { AllowedExecutables = policy.AllowedExecutables, SecurityPolicy = policy };
        var decision = new DefaultPolicyEngine().Evaluate(tool, input, context);
        Assert.True(decision.IsAllowed, decision.Reason);
        Assert.Equal(ApprovalLevel.User, decision.RequiredApproval);
        Assert.False(new DefaultPolicyEngine().Evaluate(tool, input, context with { TrustLevel = RepositoryTrustLevel.TrustedRead }).IsAllowed);
    }

    /// <summary>Snippet persistence obeys byte limits before starting a worker.</summary>
    [Fact]
    public async Task SnippetByteLimitPreventsPersistenceAndExecution()
    {
        var scope = CreateScope();
        var engine = new CSharpScriptEngine(new UnusedProcessManager(), new ToolConfig(new ConfigurationBuilder().Build()), Path.Combine(AppContext.BaseDirectory, "Threadsmith.Scripting.Worker.dll"), maximumWriteBytes: 4);
        var context = new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), CreateContext(scope)) { ExecutionTimeout = Timeout.InfiniteTimeSpan };
        await Assert.ThrowsAsync<ToolArgumentValidationException>(() => engine.ExecuteAsync("6 * 7", ScriptKind.Expression, context, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFiles(scope.ScratchpadPath));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private ScriptFileScope CreateScope()
    {
        var repository = Path.Combine(_root, "repo");
        var scratchpad = Path.Combine(repository, ".threadsmith", ".scratchpad");
        Directory.CreateDirectory(scratchpad);
        return new ScriptFileScope { RepositoryPath = repository, ScratchpadPath = scratchpad, ReadRoots = [repository] };
    }

    private static ToolInvocationContext CreateContext(ScriptFileScope scope) => new()
    {
        RepositoryPath = scope.RepositoryPath,
        RequestedBy = "test",
        TrustLevel = RepositoryTrustLevel.FullyTrustedAutomation,
        Scratchpad = new ScratchpadSessionCapability { IsActive = true, RootPath = scope.ScratchpadPath, DisabledReason = ScratchpadDisabledReason.None },
    };

    private sealed class PassthroughSanitizer : IOutputSanitizer
    {
        public string Sanitize(string value) => value;
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;

        public override long GetTimestamp() => 0;
    }

    private sealed class RecordingProcessManager : IProcessManager
    {
        public ProcessExecutionRequest? Request { get; private set; }

        public IReadOnlyList<ActiveProcessInfo> ActiveProcesses => [];

        public Task<ProcessExecutionResult> RunAsync(ProcessExecutionRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            return Task.FromResult(new ProcessExecutionResult(
                1,
                0,
                JsonSerializer.Serialize(new CSharpScriptOutput(true, "42", null, 0, false)),
                string.Empty,
                false,
                false,
                false,
                TimeSpan.Zero));
        }
    }

    private sealed class UnusedProcessManager : IProcessManager
    {
        public IReadOnlyList<ActiveProcessInfo> ActiveProcesses => [];

        public Task<ProcessExecutionResult> RunAsync(ProcessExecutionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
