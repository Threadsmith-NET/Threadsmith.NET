namespace Threadsmith.NativeTools.Tests;

using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

/// <summary>Verifies configured bounds across admission and execution layers.</summary>
public sealed class ConfigurableResourceLimitsTests
{
    /// <summary>Process timeout ceilings fit the runtime timer without changing the default.</summary>
    [Fact]
    public void ProcessTimeoutRejectsUnrepresentableTimerValues()
    {
        var maximumSeconds = (int)((uint.MaxValue - 1L) / 1000);
        new ToolLimits { RunProcessMaxTimeoutSeconds = maximumSeconds }.Validate();
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromSeconds(maximumSeconds));
        var invalid = new ToolLimits { RunProcessMaxTimeoutSeconds = maximumSeconds + 1 };
        Assert.Throws<ArgumentOutOfRangeException>(invalid.Validate);
        Assert.Equal(60, new ToolLimits().RunProcessMaxTimeoutSeconds);
    }

    /// <summary>Regex timeout admission matches the regex engine's finite millisecond range.</summary>
    [Fact]
    public void RegexTimeoutRejectsUnrepresentableValues()
    {
        new ToolLimits { SearchRegexTimeoutMilliseconds = int.MaxValue - 1 }.Validate();
        _ = new System.Text.RegularExpressions.Regex(
            "x",
            System.Text.RegularExpressions.RegexOptions.None,
            TimeSpan.FromMilliseconds(int.MaxValue - 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ToolLimits { SearchRegexTimeoutMilliseconds = int.MaxValue }.Validate());
        Assert.Equal(250, new ToolLimits().SearchRegexTimeoutMilliseconds);
    }

    /// <summary>Verifies the configured resource policy is honored.</summary>
    [Fact]
    public void RuntimeOverridesPreserveDynamicRegistrationIdentity()
    {
        var options = new ToolRuntimeOptions
        {
            Defaults = new() { MaximumOutputBytes = 1000000, TimeoutMilliseconds = 70000 },
            ByTool = [new() { ToolId = "read_file", MaximumOutputBytes = 2000000, MaximumSourceConcurrency = 20 }],
        };
        var registry = new ToolRegistry([], runtimeOptions: options);
        var original = new ReadFileTool(TestPromptLoader.Instance);
        var replacement = new ReadFileTool(TestPromptLoader.Instance);
        var source = new ToolActivitySource(ToolActivitySourceKind.Mcp, "configured test");
        registry.RegisterOrReplace(original, source);
        var first = registry.GetRegistration("read_file");
        Assert.Same(first.Tool, registry.Get("read_file"));
        Assert.Same(original, first.Implementation);
        Assert.Equal(TimeSpan.FromSeconds(70), first.Tool.Definition.Timeout);
        Assert.Equal(2000000, first.Tool.Definition.MaximumOutputBytes);
        Assert.Equal(20, first.Tool.Definition.Scheduling.MaximumSourceConcurrency);
        Assert.Equal(source, first.Source);
        registry.RegisterOrReplace(replacement, source, original);
        Assert.NotSame(first.Tool, registry.Get("read_file"));
        Assert.False(registry.Remove("read_file", original));
        Assert.True(registry.Remove("read_file", replacement));
    }

    /// <summary>MCP identifiers survive configuration binding and duplicate IDs fail explicitly.</summary>
    [Fact]
    public void RuntimeConfigurationPreservesColonQualifiedIds()
    {
        using var json = new MemoryStream(Encoding.UTF8.GetBytes("""
            {"tools":{"runtime":{"byTool":[{"toolId":"server:search","maximumOutputBytes":1000000}]}}}
            """));
        var configuration = new ConfigurationBuilder().AddJsonStream(json).Build();
        var options = configuration.GetSection("tools:runtime").Get<ToolRuntimeOptions>(binder => binder.ErrorOnUnknownConfiguration = true)!;
        Assert.Equal("server:search", Assert.Single(options.ByTool).ToolId);
        Assert.Equal(1000000, options.ByTool[0].MaximumOutputBytes);
        options.ByTool.Add(options.ByTool[0] with { ToolId = "SERVER:search" });
        Assert.Throws<ArgumentException>(() => new ToolRegistry([], runtimeOptions: options));
    }

    /// <summary>Verifies the configured resource policy is honored.</summary>
    [Fact]
    public async Task ReadWindowAndRuntimeOutputLimitsAreBothEnforcedAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "threadsmith-limits-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllLinesAsync(Path.Combine(root, "large.txt"), Enumerable.Repeat(new string('a', 30), 2100));
            var tool = new ReadFileTool(TestPromptLoader.Instance, new ToolLimits
            {
                ReadFileDefaultLines = 3000,
                ReadFileMaxLines = 3000,
                ReadFileMaximumContentBytes = 100000,
            });
            var invocation = new ToolInvocationContext
            {
                RepositoryPath = root,
                TrustLevel = RepositoryTrustLevel.TrustedRead,
                RequestedBy = "test",
            };
            var result = await tool.ExecuteAsync(
                new ReadFileInput { Path = "large.txt" },
                new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), invocation));
            Assert.Equal(2100, result.Value.Lines.Count);
            Assert.False(result.Value.IsTruncated);

            await using var events = new DomainEventStream();
            var registry = new ToolRegistry([tool], runtimeOptions: new()
            {
                Defaults = new() { MaximumOutputBytes = 100 },
            });
            var pipeline = new ToolInvocationPipeline(
                registry,
                new DefaultPolicyEngine(),
                new AllowApprovalPolicy(),
                events,
                new SecretOutputSanitizer(),
                NullLogger<ToolInvocationPipeline>.Instance);
            var bounded = await pipeline.InvokeAsync(new ToolInvocationRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                ToolId = "read_file",
                ArgumentsJson = "{\"path\":\"large.txt\"}",
                Context = invocation,
            });
            Assert.False(bounded.Succeeded);
            Assert.Equal(ToolErrorClassification.OutputLimitExceeded, bounded.ErrorClassification);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Rejects invalid configuration before work starts.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void InvalidRuntimeLimitsAreRejected(int timeout)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ToolRegistry([], runtimeOptions: new()
        {
            Defaults = new() { TimeoutMilliseconds = timeout },
        }));
    }
}
