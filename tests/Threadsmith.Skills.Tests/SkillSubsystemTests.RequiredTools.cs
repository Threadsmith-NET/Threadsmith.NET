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
    /// <summary>Dependencies are scoped while optional and inherited tools keep ordinary enablement.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RequiredTools_DisabledDependencyIsScopedAndOptionalToolsRemainOrdinary(bool nested, bool optionalEnabled)
    {
        var token = TestContext.Current.CancellationToken;
        using var package = TemporaryPackage.CopyMaintained("review");
        var catalog = package.CreateCatalog(SkillScope.Maintained);
        var discovered = (await catalog.RefreshAsync(token)).Candidates.Single();
        var verified = await new SkillPackageVerifier(new SkillTrustPolicySnapshot()).VerifyAsync(discovered, token);
        var candidate = verified with
        {
            Metadata = verified.Metadata with
            {
                Requirements = verified.Metadata.Requirements with
                {
                    RequiredTools = ["permission_probe"],
                    OptionalTools = ["permission_probe", "batch_optional"],
                    InheritAvailableTools = true,
                },
            },
        };
        var probe = new PermissionProbeTool();
        var optional = new SkillBatchProbeTool("batch_optional");
        var configurationPath = Path.Combine(package.Root, ".threadsmith", "config.json");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["tools:disabled:0"] = "permission_probe",
            ["tools:disabled:1"] = optionalEnabled ? "not-registered" : "batch_optional",
        }).Build();
        var state = new ToolStateManager(
            [probe.Definition, optional.Definition],
            configuration,
            configurationPath,
            Path.Combine(package.Root, "consent.json"),
            Path.Combine(package.Root, "mcp.json"));
        var registry = new ToolRegistry([probe, optional], state);
        var request = PermissionPlan().Request;
        var profile = RoutingProfile(ModelProfileId.New(), "required-test", 64_000, 1024, true);
        var compatibility = new SkillCompatibilityEvaluator(registry, new ConfiguredModelCatalog([profile]), "1.0.0").Evaluate(candidate, request);
        Assert.True(compatibility.IsCompatible, string.Join(", ", compatibility.DenialReasons));
        Assert.Equal("permission_probe", Assert.Single(compatibility.AvailableRequiredTools));
        Assert.DoesNotContain("permission_probe", compatibility.InheritedTools);
        Assert.DoesNotContain("permission_probe", compatibility.UnavailableOptionalTools);
        Assert.Equal(optionalEnabled, compatibility.InheritedTools.Contains("batch_optional"));
        var context = PermissionContext();
        var snapshots = new ConversationToolSnapshotStore();
        Guid? caller = nested ? snapshots.Capture(request.SessionId, request.RunId, registry.GetRegistrations(request.SessionId, request.RunId), context) : null;
        var plan = PermissionPlan() with
        {
            Request = request with { CallerToolSnapshotId = caller },
            Compatibility = compatibility,
            AvailableToolIds = optionalEnabled ? ["permission_probe", "batch_optional"] : ["permission_probe"],
        };
        var model = new PermissionModelProvider();
        var sanitizer = new SecretOutputSanitizer();
        await using var events = new DomainEventStream();
        var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new DenyApprovalPolicy(), events, sanitizer, NullLogger<ToolInvocationPipeline>.Instance, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
        var runner = new ModelSkillProcedureRunner(model, registry, pipeline, sanitizer, (_, _) => Task.FromResult(context), TestPromptLoader.Instance, snapshots: snapshots);

        try
        {
            await runner.RunAsync(plan, PermissionStep(), 1, [], "{}", token);
        }
        finally
        {
            if (caller is { } snapshot)
            {
                snapshots.Release(snapshot);
            }
        }

        Assert.Equal(1, probe.Executions);
        Assert.All(model.Requests, modelRequest => Assert.Contains(modelRequest.Tools, tool => tool.Name == "permission_probe"));
        Assert.Equal(optionalEnabled, model.Requests[0].Tools.Any(tool => tool.Name == "batch_optional"));
        Assert.False(optional.Started.Task.IsCompleted);
        Assert.False(state.IsEnabled("permission_probe"));
        Assert.DoesNotContain(registry.Definitions, tool => tool.Id == "permission_probe");
        Assert.False(File.Exists(configurationPath));
    }

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
