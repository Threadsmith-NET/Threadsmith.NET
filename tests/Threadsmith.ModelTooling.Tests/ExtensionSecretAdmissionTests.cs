namespace Threadsmith.ModelTooling.Tests;

using Threadsmith.Core;
using Threadsmith.Tools;
using Xunit;

/// <summary>Checks execution identity at the policy-to-tool boundary.</summary>
public static class ExtensionSecretAdmissionTests
{
    /// <summary>Only the original invocation can consume its grant once.</summary>
    [Fact]
    public static void SecretAdmission_RejectsCopiedContextAndChangedInput()
    {
        var tool = new ReadFileTool(TestPromptLoader.Instance, new Threadsmith.Telemetry.SecretOutputSanitizer());
        var input = new object();
        var invocation = new ToolInvocationContext { RepositoryPath = ".", RequestedBy = "test" };
        var context = new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), invocation);
        context.AdmitSecretReferences(tool, input, new HashSet<string> { "secrets:test" });

        Assert.Null((context with { }).TakeSecretReferences(tool, input));
        Assert.Null(context.TakeSecretReferences(tool, new object()));
        Assert.Equal("secrets:test", Assert.Single(context.TakeSecretReferences(tool, input)!));
        Assert.Null(context.TakeSecretReferences(tool, input));
    }
}
