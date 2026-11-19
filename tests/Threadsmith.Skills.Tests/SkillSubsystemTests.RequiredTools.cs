namespace Threadsmith.Skills.Tests;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Skills;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public sealed partial class SkillSubsystemTests
{
    /// <summary>A dependency cannot silently rebind to a replacement or missing registration.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequiredTools_ChangedIdentityStopsWithoutExecution(bool remove)
    {
        var original = new PermissionProbeTool();
        var replacement = new PermissionProbeTool();
        var registry = new ToolRegistry([]);
        registry.RegisterOrReplace(original, new(ToolActivitySourceKind.BuiltIn, "original"));
        var model = new PermissionModelProvider
        {
            BeforeToolRequest = () =>
            {
                if (remove)
                {
                    Assert.True(registry.Remove("permission_probe", original));
                }
                else
                {
                    registry.RegisterOrReplace(replacement, new(ToolActivitySourceKind.BuiltIn, "replacement"), original);
                }
            },
        };
        var plan = PermissionPlan() with { Compatibility = new SkillCompatibilityResult { IsCompatible = true, AvailableRequiredTools = ["permission_probe"] } };
        await using var events = new DomainEventStream();
        var sanitizer = new SecretOutputSanitizer();
        var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new DenyApprovalPolicy(), events, sanitizer, NullLogger<ToolInvocationPipeline>.Instance, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
        var runner = new ModelSkillProcedureRunner(model, registry, pipeline, sanitizer, (_, _) => Task.FromResult(PermissionContext()), TestPromptLoader.Instance);

        var error = await Record.ExceptionAsync(() => runner.RunAsync(plan, PermissionStep(), 1, [], "{}", TestContext.Current.CancellationToken));

        Assert.NotNull(error);
        Assert.True(error is InvalidOperationException or KeyNotFoundException, error.ToString());
        Assert.Equal(0, original.Executions);
        Assert.Equal(0, replacement.Executions);
        Assert.Single(model.Requests);
    }
}
