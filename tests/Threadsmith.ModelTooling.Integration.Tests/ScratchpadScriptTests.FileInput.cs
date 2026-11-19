namespace Threadsmith.ModelTooling.Integration.Tests;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public sealed partial class ScratchpadScriptTests
{
    /// <summary>Invalid exclusive choices fail before any source capture or execution.</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"code\":null,\"scriptPath\":null}")]
    [InlineData("{\"code\":\"\"}")]
    [InlineData("{\"scriptPath\":\"  \"}")]
    [InlineData("{\"code\":\"42\",\"scriptPath\":\"x.csx\"}")]
    [InlineData("{\"code\":\"\",\"scriptPath\":\"x.csx\"}")]
    public async Task InvalidSourceChoiceNeverExecutes(string json)
    {
        var scope = CreateScope();
        var process = new RecordingProcessManager();
        await using var events = new DomainEventStream();
        var pipeline = FileInputPipeline(events, process);

        var result = await pipeline.InvokeAsync(FileRequest(scope, json), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Null(process.Request);
        Assert.Null(result.SourceDetail);
        Assert.Empty(Directory.EnumerateFiles(scope.ScratchpadPath));
    }

    /// <summary>The advertised schema and runtime agree on optional source fields.</summary>
    [Fact]
    public void ScriptSchemaDescribesOptionalExclusiveSources()
    {
        var tool = new CSharpScriptTool(new FileProbeEngine(), TestPromptLoader.Instance);
        using var schema = JsonDocument.Parse(tool.Definition.InputSchema.JsonSchema);
        var properties = schema.RootElement.GetProperty("properties");
        Assert.True(properties.TryGetProperty("scriptPath", out var path));
        Assert.Contains("Exclusive", path.GetProperty("description").GetString(), StringComparison.Ordinal);
        if (schema.RootElement.TryGetProperty("required", out var required))
        {
            Assert.DoesNotContain(required.EnumerateArray(), value => value.GetString() is "code" or "scriptPath");
        }

        Assert.IsType<CSharpScriptInput>(tool.DeserializeInput("{\"scriptPath\":\"saved.csx\"}"));
        Assert.IsType<CSharpScriptInput>(tool.DeserializeInput("{\"code\":\"42\"}"));
    }

    /// <summary>Package integrity applies to the exact captured bytes, including BOM, before worker or source evidence.</summary>
    [Theory]
    [InlineData("unchanged")]
    [InlineData("hash")]
    [InlineData("length")]
    public async Task PackagedFileSourceIntegrityUsesTheExecutionSnapshot(string scenario)
    {
        var token = TestContext.Current.CancellationToken;
        var scope = CreateScope();
        var path = Path.Combine(scope.RepositoryPath, "packaged.csx");
        const string source = "// café\r\n42";
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(source)).ToArray();
        await File.WriteAllBytesAsync(path, bytes, token);
        var expected = new ScriptSourceIntegrity(bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        if (scenario != "unchanged")
        {
            await File.WriteAllTextAsync(path, scenario == "hash" ? source.Replace("42", "43", StringComparison.Ordinal) : "1", new UTF8Encoding(true), token);
        }

        var process = new SnapshotProcessManager(path);
        await using var events = new DomainEventStream();
        var pipeline = FileInputPipeline(events, process);
        var request = FileRequest(scope, JsonSerializer.Serialize(new { scriptPath = "./packaged.csx" }));
        request = request with { Context = request.Context with { ScriptSourceIntegrity = new Dictionary<string, ScriptSourceIntegrity> { [path] = expected } } };
        var result = await pipeline.InvokeAsync(request, token);

        Assert.Equal(scenario == "unchanged", result.Succeeded);
        if (result.Succeeded)
        {
            Assert.Equal(source, process.Submitted);
            Assert.Equal(source, result.SourceDetail!.Source);
            Assert.Equal("changed after capture", await File.ReadAllTextAsync(path, token));
        }
        else
        {
            Assert.Contains("package integrity", result.Error, StringComparison.Ordinal);
            Assert.Null(process.Submitted);
            Assert.Null(result.SourceDetail);
            Assert.Empty(Directory.EnumerateFiles(scope.ScratchpadPath));
        }
    }

    /// <summary>Path authority, encoding and size failures never start a worker.</summary>
    [Theory]
    [InlineData("missing-bare")]
    [InlineData("extension")]
    [InlineData("directory")]
    [InlineData("external")]
    [InlineData("traversal")]
    [InlineData("prohibited")]
    [InlineData("child-scope")]
    [InlineData("scratchpad-prohibited")]
    [InlineData("utf8")]
    [InlineData("characters")]
    [InlineData("bytes")]
    [InlineData("drive-relative")]
    [InlineData("device")]
    [InlineData("ads")]
    [InlineData("uri")]
    public async Task InvalidFileSourceFailsBeforeWorker(string failure)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scope = CreateScope();
        var path = Path.Combine(scope.RepositoryPath, "input.csx");
        await File.WriteAllTextAsync(path, "42", cancellationToken);
        var context = CreateContext(scope);
        var selected = path;
        var maximumCharacters = 65536;
        switch (failure)
        {
            case "missing-bare":
                {
                    selected = "input.csx";
                    break;
                }

            case "extension":
                {
                    selected = Path.ChangeExtension(path, ".txt");
                    await File.WriteAllTextAsync(selected, "42", cancellationToken);
                    break;
                }

            case "directory":
                {
                    Directory.CreateDirectory(Path.Combine(scope.ScratchpadPath, "folder.csx"));
                    selected = "folder.csx";
                    break;
                }

            case "external":
            case "traversal":
                {
                    selected = Path.Combine(_root, "external.csx");
                    await File.WriteAllTextAsync(selected, "42", cancellationToken);
                    context = context with { ApprovedRoots = [scope.RepositoryPath, _root] };
                    if (failure == "traversal")
                    {
                        selected = "../external.csx";
                    }

                    break;
                }

            case "prohibited":
                {
                    context = context with { ProhibitedPaths = ["input.csx"] };
                    break;
                }

            case "child-scope":
                {
                    Directory.CreateDirectory(Path.Combine(scope.RepositoryPath, "child"));
                    context = context with { ApprovedRoots = ["child"], RequestedBy = "agent:child" };
                    break;
                }

            case "scratchpad-prohibited":
                {
                    selected = Path.Combine(scope.ScratchpadPath, "input.csx");
                    await File.WriteAllTextAsync(selected, "42", cancellationToken);
                    context = context with { ProhibitedPaths = [".threadsmith/.scratchpad/**"] };
                    break;
                }

            case "utf8":
                {
                    await File.WriteAllBytesAsync(path, [0xC3, 0x28], cancellationToken);
                    break;
                }

            case "characters":
                {
                    maximumCharacters = 1;
                    break;
                }

            case "bytes":
                {
                    await File.WriteAllTextAsync(path, new string('x', (1024 * 1024) + 1), cancellationToken);
                    break;
                }

            case "drive-relative":
                {
                    selected = "C:input.csx";
                    break;
                }

            case "device":
                {
                    selected = "\\\\?\\C:\\input.csx";
                    break;
                }

            case "ads":
                {
                    selected = "input.csx:stream.csx";
                    break;
                }

            case "uri":
                {
                    selected = "file:///input.csx";
                    break;
                }
        }

        var process = new RecordingProcessManager();
        await using var events = new DomainEventStream();
        var pipeline = FileInputPipeline(events, process, maximumCharacters);
        var result = await pipeline.InvokeAsync(FileRequest(scope, JsonSerializer.Serialize(new { scriptPath = selected })) with { Context = context }, cancellationToken);

        Assert.False(result.Succeeded, failure);
        Assert.Null(process.Request);
        Assert.Null(result.SourceDetail);
    }

    /// <summary>Links at either the leaf or an in-repository parent are denied.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinkedFileOrParentNeverExecutes(bool parent)
    {
        var scope = CreateScope();
        var target = Path.Combine(scope.RepositoryPath, "target.csx");
        await File.WriteAllTextAsync(target, "42", TestContext.Current.CancellationToken);
        var link = Path.Combine(scope.RepositoryPath, parent ? "linked" : "linked.csx");
        try
        {
            if (parent)
            {
                Directory.CreateSymbolicLink(link, scope.RepositoryPath);
            }
            else
            {
                File.CreateSymbolicLink(link, target);
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Skip("Creating symbolic links is unavailable on this runner.");
            return;
        }

        try
        {
            var process = new RecordingProcessManager();
            await using var events = new DomainEventStream();
            var result = await FileInputPipeline(events, process).InvokeAsync(FileRequest(scope, JsonSerializer.Serialize(new { scriptPath = parent ? Path.Combine(link, "target.csx") : link })), TestContext.Current.CancellationToken);
            Assert.False(result.Succeeded);
            Assert.Null(process.Request);
        }
        finally
        {
            if (parent)
            {
                Directory.Delete(link);
            }
            else
            {
                File.Delete(link);
            }
        }
    }

    /// <summary>Inline execution copies can be rerun by either documented path form.</summary>
    [Theory]
    [InlineData("expression")]
    [InlineData("statement")]
    public async Task InlineSavedCopyCanBeRerun(string kind)
    {
        var scope = CreateScope();
        var process = new RecordingProcessManager();
        await using var events = new DomainEventStream();
        var pipeline = FileInputPipeline(events, process);
        var code = kind == "expression" ? "42" : "return 42;";
        var first = await pipeline.InvokeAsync(FileRequest(scope, JsonSerializer.Serialize(new { code, kind })), TestContext.Current.CancellationToken);
        Assert.True(first.Succeeded, first.Error);
        var output = JsonSerializer.Deserialize<CSharpScriptOutput>(first.ResultJson!)!;
        foreach (var selected in new[] { output.ScriptPath, Path.GetFileName(output.ScriptPath) })
        {
            var rerun = await pipeline.InvokeAsync(FileRequest(scope, JsonSerializer.Serialize(new { scriptPath = selected, kind })), TestContext.Current.CancellationToken);
            Assert.True(rerun.Succeeded, rerun.Error);
            Assert.Equal(code, rerun.SourceDetail!.Source);
            var repeated = JsonSerializer.Deserialize<CSharpScriptOutput>(rerun.ResultJson!)!;
            Assert.NotEqual(output.ScriptPath, repeated.ScriptPath);
        }
    }

    /// <summary>Large captured source is not retained in a second output or evidence field.</summary>
    [Fact]
    public async Task FileSourcePreservesRetentionCeiling()
    {
        var scope = CreateScope();
        var code = "// private-retention-marker" + new string('x', ToolSourceDetail.MaximumCharacters);
        var original = Path.Combine(scope.RepositoryPath, "large.csx");
        await File.WriteAllTextAsync(original, code, TestContext.Current.CancellationToken);
        var tool = new CSharpScriptTool(new FileProbeEngine(), TestPromptLoader.Instance, code.Length);
        var registry = new ToolRegistry([tool], runtimeOptions: new() { Defaults = new() { TimeoutMilliseconds = -1 } });
        await using var events = new DomainEventStream();
        var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new AllowApprovalPolicy(), events, new PassthroughSanitizer(), NullLogger<ToolInvocationPipeline>.Instance);

        var result = await pipeline.InvokeAsync(FileRequest(scope, JsonSerializer.Serialize(new { scriptPath = original })), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(ToolSourceAvailability.RetentionLimitExceeded, result.SourceDetail!.Availability);
        Assert.Null(result.SourceDetail.Source);
        Assert.DoesNotContain("private-retention-marker", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    /// <summary>Ordinary UNC syntax is not confused with Windows device namespaces.</summary>
    [Fact]
    public void SourcePathGrammarAllowsUncButRejectsDevices()
    {
        ScriptFilePolicy.ValidateSourcePathForm(@"\\server\share\repo\source.csx");
        ScriptFilePolicy.ValidateSourcePathForm("//server/share/repo/source.csx");
        Assert.Throws<ToolArgumentValidationException>(() => ScriptFilePolicy.ValidateSourcePathForm(@"\\?\UNC\server\share\repo\source.csx"));
        Assert.Throws<ToolArgumentValidationException>(() => ScriptFilePolicy.ValidateSourcePathForm(@"\\.\C:\repo\source.csx"));
        Assert.Throws<ToolArgumentValidationException>(() => ScriptFilePolicy.ValidateSourcePathForm("//?/C:/repo/source.csx"));
        Assert.Throws<ToolArgumentValidationException>(() => ScriptFilePolicy.ValidateSourcePathForm("C:source.csx"));
    }

    private static ToolInvocationPipeline FileInputPipeline(DomainEventStream events, IProcessManager process, int maximumCharacters = 65536, int maximumWriteBytes = 1024 * 1024)
    {
        var engine = new CSharpScriptEngine(process, new ToolConfig(new ConfigurationBuilder().Build()), Path.Combine(AppContext.BaseDirectory, "Threadsmith.Scripting.Worker.dll"), maximumWriteBytes, workerOptions: new() { NumberOfWarmWorkersInPool = 0 });
        var registry = new ToolRegistry([new CSharpScriptTool(engine, TestPromptLoader.Instance, maximumCharacters, maximumWriteBytes)], runtimeOptions: new() { Defaults = new() { TimeoutMilliseconds = -1 } });
        return new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new AllowApprovalPolicy(), events, new PassthroughSanitizer(), NullLogger<ToolInvocationPipeline>.Instance, timeProvider: new FixedClock());
    }

    private static ToolInvocationRequest FileRequest(ScriptFileScope scope, string json) => new()
    {
        SessionId = SessionId.New(),
        RunId = RunId.New(),
        ToolId = "csharp_script",
        ArgumentsJson = json,
        Context = CreateContext(scope) with { AllowedExecutables = ["dotnet"] },
    };

    private sealed class SnapshotProcessManager : IProcessManager
    {
        private readonly string _original;

        public SnapshotProcessManager(string original)
        {
            _original = original;
        }

        public string? Submitted { get; private set; }

        public IReadOnlyList<ActiveProcessInfo> ActiveProcesses => [];

        public async Task<ProcessExecutionResult> RunAsync(ProcessExecutionRequest request, CancellationToken cancellationToken = default)
        {
            using var payload = JsonDocument.Parse(request.StandardInput!);
            Submitted = payload.RootElement.GetProperty("Code").GetString();
            await File.WriteAllTextAsync(_original, "changed after capture", cancellationToken);
            return new ProcessExecutionResult(1, 0, JsonSerializer.Serialize(new CSharpScriptOutput(true, "42", null, 0, false)), string.Empty, false, false, false, TimeSpan.Zero);
        }
    }

    private sealed class FileProbeEngine : ICSharpScriptEngine
    {
        public Task<CSharpScriptOutput> ExecuteAsync(string code, ScriptKind kind, ToolExecutionContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new CSharpScriptOutput(true, "42", null, 0, false));
        }
    }
}
