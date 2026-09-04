namespace Threadsmith.Planning.Tests;

using Threadsmith.Core;
using Threadsmith.Tools;

/// <summary>Returns caller-configured structured output through the production tool pipeline.</summary>
internal sealed class TestDeterministicOutputTool : Tool<TestDeterministicOutputInput, TestDeterministicOutput>
{
    private static readonly ToolDefinition _definition = new()
    {
        Id = "deterministic_output",
        Version = "1.0",
        Description = "Returns deterministic test output.",
        Category = ToolCategory.FileRead,
        InputSchema = new ToolSchema(
            nameof(TestDeterministicOutputInput),
            1,
            "{\"type\":\"object\",\"properties\":{\"sequence\":{\"type\":\"integer\"}},\"required\":[\"sequence\"],\"additionalProperties\":false}"),
        OutputSchema = new ToolSchema(nameof(TestDeterministicOutput), 1, "{\"type\":\"object\"}"),
        RequiredTrust = RepositoryTrustLevel.UntrustedInspection,
        SideEffect = ToolSideEffect.ReadOnly,
        Idempotency = ToolIdempotency.Idempotent,
        SupportsCancellation = true,
        Timeout = TimeSpan.FromSeconds(5),
        MaximumOutputBytes = 8_192,
    };

    private readonly string _content;

    /// <summary>Initializes a new instance of the <see cref="TestDeterministicOutputTool"/> class.</summary>
    internal TestDeterministicOutputTool(string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        _content = content;
    }

    /// <inheritdoc />
    public override ToolDefinition Definition => _definition;

    /// <inheritdoc />
    public override Task<ToolExecution<TestDeterministicOutput>> ExecuteAsync(
        TestDeterministicOutputInput input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ToolExecution<TestDeterministicOutput>(
            new TestDeterministicOutput(_content),
            []));
    }

    protected override void ValidateInput(TestDeterministicOutputInput input)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(input.Sequence);
    }
}

/// <summary>Distinguishes otherwise identical calls so duplicate-call prevention remains real.</summary>
internal sealed record TestDeterministicOutputInput(int Sequence);

/// <summary>Short deterministic structured output used to cross an injected context budget.</summary>
internal sealed record TestDeterministicOutput(string Content);
