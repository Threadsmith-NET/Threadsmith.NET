namespace Threadsmith.ModelTooling.Tests;

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
public sealed class ScratchpadScriptTests : IDisposable
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

    /// <summary>Executes the reference's exact code fences so documentation cannot silently drift.</summary>
    [Theory]
    [InlineData(0, ScriptKind.Expression)]
    [InlineData(1, ScriptKind.Statement)]
    public async Task ExecutionReferenceExamplesRunInWorker(int exampleIndex, ScriptKind kind)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var reference = await File.ReadAllTextAsync(
            Path.Combine(TestPromptLoader.FindRepositoryRoot(), "docs", "operations", "csharp-scripting-execution.md"),
            cancellationToken);
        var examples = reference.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split("```csharp\n", StringSplitOptions.None).Skip(1)
            .Select(block => block[..block.IndexOf("\n```", StringComparison.Ordinal)]).ToArray();
        Assert.Equal(2, examples.Length);
        var scope = CreateScope();
        var messages = Enumerable.Range(0, 20).Select(index => $"error-{index}" + new string('x', 220)).ToArray();
        var input = new[] { JsonSerializer.Serialize(new { level = "info", message = "ignore" }) }
            .Concat(messages.Select(message => JsonSerializer.Serialize(new { level = "error", message })))
            .Append("not JSON: the script must stop before reading this line");
        await File.WriteAllLinesAsync(Path.Combine(scope.ScratchpadPath, "events.jsonl"), input, cancellationToken);
        var engine = new CSharpScriptEngine(new ProcessManager(new PassthroughSanitizer(), NullLogger<ProcessManager>.Instance, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow)), new ToolConfig(new ConfigurationBuilder().Build()), Path.Combine(AppContext.BaseDirectory, "Threadsmith.Scripting.Worker.dll"), workerOptions: new() { NumberOfWarmWorkersInPool = 0 });
        var context = new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), CreateContext(scope)) { ExecutionTimeout = Timeout.InfiniteTimeSpan };

        var result = await engine.ExecuteAsync(examples[exampleIndex], kind, context, cancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.False(result.IsTruncated);
        Assert.Equal(exampleIndex == 0 ? "5" : string.Join("\n", messages.Select(message => message[..200])), result.Output);
    }

    /// <summary>Ordinary computation and JSON parsing use the real worker's default imports and permissions.</summary>
    [Theory]
    [InlineData("Math.Sqrt(25)", "5")]
    [InlineData("string.Equals(\"a\", \"A\", StringComparison.OrdinalIgnoreCase)", "True")]
    [InlineData("using (var doc = JsonDocument.Parse(\"{\\\"value\\\":25}\")) { return Math.Sqrt(doc.RootElement.GetProperty(\"value\").GetInt32()); }", "5")]
    [InlineData("using (var doc = System.Text.Json.JsonDocument.Parse(\"[1,2,3]\")) { return doc.RootElement.GetArrayLength(); }", "3")]
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
    public async Task WorkerExplainsRejectedCapabilities(string code, string? allowedAssemblies, string expectedError)
    {
        var scope = CreateScope();
        var builder = new ConfigurationBuilder();
        if (allowedAssemblies is not null)
        {
            builder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["tools:config:csharp_script:allowed_assemblies"] = allowedAssemblies,
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
    public async Task WorkerReadsLargeInputAndWritesInspectableResult()
    {
        var scope = CreateScope();
        var input = Path.Combine(scope.ScratchpadPath, "input.log");
        await File.WriteAllLinesAsync(input, Enumerable.Range(0, 100000).Select(index => index % 10 == 0 ? "ERROR" : "ok"), TestContext.Current.CancellationToken);
        var config = new ConfigurationBuilder().Build();
        var engine = new CSharpScriptEngine(new ProcessManager(new PassthroughSanitizer(), NullLogger<ProcessManager>.Instance, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow)), new ToolConfig(config), Path.Combine(AppContext.BaseDirectory, "Threadsmith.Scripting.Worker.dll"), workerOptions: new() { NumberOfWarmWorkersInPool = 0 });
        var context = new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), CreateContext(scope)) { ExecutionTimeout = Timeout.InfiniteTimeSpan };
        var result = await engine.ExecuteAsync(
            "var count = Scratchpad.ReadLines(\"input.log\").Count(line => line.Contains(\"ERROR\")); await Scratchpad.WriteAllTextAsync(\"results/count.txt\", count.ToString()); return count;",
            ScriptKind.Statement,
            context,
            TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error);
        Assert.Equal("10000", result.Output);
        Assert.Equal("10000", await File.ReadAllTextAsync(Path.Combine(scope.ScratchpadPath, "results", "count.txt"), TestContext.Current.CancellationToken));
        Assert.True(File.Exists(result.ScriptPath));

        var inputs = Directory.CreateDirectory(Path.Combine(_root, "authorized-inputs")).FullName;
        var externalInput = Path.Combine(inputs, "external.log");
        await File.WriteAllLinesAsync(externalInput, ["one", "two", "three"], TestContext.Current.CancellationToken);
        var filePolicy = new EffectiveSessionPolicy
        {
            BindingId = Guid.NewGuid(),
            Generation = 1,
            Fingerprint = "configured-read-folders",
            RepositoryPath = scope.RepositoryPath,
            ReadFolders = [inputs],
            ScriptAssemblies = ["System.Linq", "System.Collections", "System.Collections.Generic"],
            ResourceLimits = new Dictionary<string, int>
            {
                ["tools:config:csharp_script:timeout_ms"] = 5000,
                ["tools:config:csharp_script:max_output_bytes"] = 65536,
                ["tools:config:csharp_script:max_code_characters"] = 65536,
                ["tools:config:csharp_script:max_assemblies"] = 32,
                ["tools:config:csharp_script:max_assembly_name_characters"] = 128,
                ["tools:config:csharp_script:max_assembly_setting_characters"] = 4096,
            }.ToImmutableDictionary(),
        };
        var externalCode = $"var count = Scratchpad.ReadLines({JsonSerializer.Serialize(externalInput)}).Count(); await Scratchpad.WriteAllTextAsync(\"results/external-count.txt\", count.ToString()); return count;";
        var permitted = await engine.ExecuteAsync(externalCode, ScriptKind.Statement, context with { Invocation = context.Invocation with { SecurityPolicy = filePolicy } }, TestContext.Current.CancellationToken);
        Assert.True(permitted.Success, permitted.Error);
        Assert.Equal("3", permitted.Output);
        Assert.Equal("3", await File.ReadAllTextAsync(Path.Combine(scope.ScratchpadPath, "results", "external-count.txt"), TestContext.Current.CancellationToken));
        var unconfigured = await engine.ExecuteAsync(externalCode, ScriptKind.Statement, context with { Invocation = context.Invocation with { SecurityPolicy = filePolicy with { ReadFolders = [] } } }, TestContext.Current.CancellationToken);
        Assert.False(unconfigured.Success);
        Assert.Contains("authorized read locations", unconfigured.Error, StringComparison.Ordinal);

        var rejected = await engine.ExecuteAsync("System.IO.File.ReadAllText(\"input.log\")", ScriptKind.Expression, context, TestContext.Current.CancellationToken);
        Assert.False(rejected.Success);
        Assert.True(File.Exists(rejected.ScriptPath));
        foreach (var probe in new[]
        {
            "var writer = Threadsmith.Core.AtomicFileWriter.WriteAsync; return writer is not null;",
            "new Threadsmith.Scripting.Worker.ScriptGlobals(new Threadsmith.Core.ScriptFileScope { RepositoryPath = \".\", ScratchpadPath = \".\" }, default).Scratchpad.ReadLines(\"AGENTS.md\").First()",
        })
        {
            var bypass = await engine.ExecuteAsync(probe, ScriptKind.Statement, context, TestContext.Current.CancellationToken);
            Assert.False(bypass.Success);
            Assert.Contains("host implementation types", bypass.Error, StringComparison.Ordinal);
        }
    }

    /// <summary>Named arguments on the supplied API compile and execute in the real worker.</summary>
    [Fact]
    public async Task WorkerAcceptsNamedScratchpadArguments()
    {
        var scope = CreateScope();
        await File.WriteAllBytesAsync(Path.Combine(scope.ScratchpadPath, "input.bin"), [1, 2, 3, 4, 5], TestContext.Current.CancellationToken);
        var config = new ConfigurationBuilder().Build();
        var engine = new CSharpScriptEngine(new ProcessManager(new PassthroughSanitizer(), NullLogger<ProcessManager>.Instance, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow)), new ToolConfig(config), Path.Combine(AppContext.BaseDirectory, "Threadsmith.Scripting.Worker.dll"), workerOptions: new() { NumberOfWarmWorkersInPool = 0 });
        var context = new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), CreateContext(scope)) { ExecutionTimeout = Timeout.InfiniteTimeSpan };

        var result = await engine.ExecuteAsync(
            """
            var sizes = string.Join(",", Scratchpad.ReadChunks("input.bin", chunkSize: 2).Select(chunk => chunk.Length));
            Scratchpad.CreateDirectory(path: "results");
            await Scratchpad.WriteAllTextAsync(path: "results/text.txt", text: "original", cancellationToken: default);
            await Scratchpad.WriteAllTextAsync("results/text.txt", sizes, overwrite: true);
            await Scratchpad.AppendTextAsync(text: "!", path: "results/text.txt", cancellationToken: default);
            await Scratchpad.WriteAllBytesAsync(path: "results/bytes.bin", bytes: new byte[] { 9 }, cancellationToken: default);
            await Scratchpad.WriteAllBytesAsync("results/bytes.bin", new byte[] { 1, 2 }, overwrite: true);
            await Scratchpad.AppendBytesAsync(bytes: new byte[] { 3 }, path: "results/bytes.bin", cancellationToken: default);
            return Scratchpad.ReadLines(path: "results/text.txt").Single();
            """,
            ScriptKind.Statement,
            context,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.Equal("2,2,1!", result.Output);
        Assert.Equal("2,2,1!", await File.ReadAllTextAsync(Path.Combine(scope.ScratchpadPath, "results", "text.txt"), TestContext.Current.CancellationToken));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(Path.Combine(scope.ScratchpadPath, "results", "bytes.bin"), TestContext.Current.CancellationToken));
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

    /// <summary>The unconfigured script budget stays at five seconds and explicit overrides reach the worker.</summary>
    [Theory]
    [InlineData(null, null, null, 5000)]
    [InlineData(120000, null, null, 120000)]
    [InlineData(120000, 300000, null, 300000)]
    [InlineData(120000, 300000, 2000, 2000)]
    [InlineData(120000, 300000, 600000, 600000)]
    [InlineData(120000, null, -1, -1)]
    public async Task ScriptPipeline_PropagatesFallbackOrOverrideToWorker(int? general, int? configured, int? specific, int expected)
    {
        var scope = CreateScope();
        var process = new RecordingProcessManager();
        var engine = new CSharpScriptEngine(process, new ToolConfig(new ConfigurationBuilder().Build()), Path.Combine(AppContext.BaseDirectory, "Threadsmith.Scripting.Worker.dll"), workerOptions: new() { NumberOfWarmWorkersInPool = 0 });
        var tool = new CSharpScriptTool(engine, TestPromptLoader.Instance);
        var registry = new ToolRegistry(
            [tool],
            runtimeOptions: new()
            {
                Defaults = new() { TimeoutMilliseconds = general },
                ByTool = specific is { } timeout ? [new() { ToolId = "csharp_script", TimeoutMilliseconds = timeout }] : [],
            },
            configuredTimeouts: configured is { } setting
                ? new Dictionary<string, TimeSpan> { ["csharp_script"] = TimeSpan.FromMilliseconds(setting) } : null);
        await using var events = new DomainEventStream();
        var pipeline = new ToolInvocationPipeline(
            registry,
            new DefaultPolicyEngine(),
            new AllowApprovalPolicy(),
            events,
            new PassthroughSanitizer(),
            NullLogger<ToolInvocationPipeline>.Instance,
            timeProvider: new FixedClock());

        var result = await pipeline.InvokeAsync(
            new ToolInvocationRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                ToolId = "csharp_script",
                ArgumentsJson = "{\"code\":\"6 * 7\"}",
                Context = CreateContext(scope) with { AllowedExecutables = ["dotnet"] },
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        Assert.NotNull(process.Request);
        Assert.Equal(TimeSpan.FromMilliseconds(expected), process.Request.Timeout);
        using var payload = JsonDocument.Parse(process.Request.StandardInput!);
        Assert.Equal(expected, payload.RootElement.GetProperty("TimeoutMilliseconds").GetInt32());
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
