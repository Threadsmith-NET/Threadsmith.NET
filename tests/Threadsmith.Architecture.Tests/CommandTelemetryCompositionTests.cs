// Threadsmith.NET Milestone 23.1 — Plan 64: Production command telemetry activation.
namespace Threadsmith.Architecture.Tests;

using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

/// <summary>Production composition wiring tests for Plan 64 (AR-01) command telemetry.</summary>
public static class CommandTelemetryCompositionTests
{
    /// <summary>Production composition supplies exactly one telemetry middleware to the dispatcher pipeline.</summary>
    [Fact]
    public static void ApplicationComposition_ProductionMiddleware_SuppliesExactlyOneTelemetryMiddleware()
    {
        var middleware = Threadsmith.App.ApplicationComposition.CreateProductionMiddleware(
            NullLoggerFactory.Instance);

        Assert.Single(middleware);
        Assert.IsType<CommandTelemetryMiddleware>(middleware[0]);
    }

    /// <summary>Validation request defaults preserve semantic, build, diagnostics, and test gates.</summary>
    [Fact]
    public static void BuildValidationRequest_DefaultValidationStages_PreserveBuildAndTests()
    {
        var request = new BuildValidationRequest
        {
            SessionId = SessionId.New(),
            RunId = RunId.New(),
            Baseline = new WorkspaceBaseline(
                WorkspaceId.New(),
                AppContext.BaseDirectory,
                DateTimeOffset.UtcNow,
                []),
            Confidence = SemanticConfidenceLevel.FullSemantic,
        };

        Assert.Equal(
            [
                MutationValidationStage.Semantic,
                MutationValidationStage.Compile,
                MutationValidationStage.Diagnostics,
                MutationValidationStage.Tests,
            ],
            request.Stages);
    }

    /// <summary>Plan sanity composition uses the active workspace policy after repository rebinding.</summary>
    [Fact]
    public static void ApplicationComposition_PlanSanityRequest_UsesActiveWorkspacePolicy()
    {
        var plan = new ImplementationPlan { Summary = "test" };
        var startupContext = new ToolInvocationContext
        {
            RepositoryPath = "startup",
            ProhibitedPaths = ["startup/**"],
            TrustLevel = RepositoryTrustLevel.UntrustedInspection,
            RequestedBy = "test",
        };
        var baseline = new WorkspaceBaseline(
            WorkspaceId.New(),
            "active",
            DateTimeOffset.UtcNow,
            [],
            TrustLevel: RepositoryTrustLevel.TrustedMutation,
            ProhibitedPaths: ["active/**"]);

        var request = Threadsmith.App.ApplicationComposition.CreatePlanSanityCheckRequest(
            plan,
            startupContext,
            baseline);

        Assert.Equal("active", request.RepositoryRoot);
        Assert.Equal(RepositoryTrustLevel.TrustedMutation, request.TrustLevel);
        Assert.Equal(["active/**"], request.ProhibitedPaths);
    }

    /// <summary>Missing mutation baselines degrade plan sanity composition to unavailable evidence.</summary>
    [Fact]
    public static void ApplicationComposition_MissingPlanSanityBaseline_ReturnsNull()
    {
        var baseline = Threadsmith.App.ApplicationComposition.TryGetWorkspaceBaseline(
            new MissingWorkspaceResolver(),
            WorkspaceId.New());

        Assert.Null(baseline);
    }

    /// <summary>Absent validation configuration preserves semantic, build, diagnostics, and test gates.</summary>
    [Fact]
    public static void ApplicationComposition_DefaultValidationStages_PreserveBuildAndTests()
    {
        var configuration = new ConfigurationBuilder().Build();
        var method = typeof(Threadsmith.App.ApplicationComposition).GetMethod(
            "GetValidationStages",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("GetValidationStages was not found.");

        var stages = Assert.IsAssignableFrom<IReadOnlyList<MutationValidationStage>>(
            method.Invoke(null, [configuration]));

        Assert.Equal(
            [
                MutationValidationStage.Semantic,
                MutationValidationStage.Compile,
                MutationValidationStage.Diagnostics,
                MutationValidationStage.Tests,
            ],
            stages);
    }

    private sealed class MissingWorkspaceResolver : ITransactionalWorkspaceResolver
    {
        public ITransactionalWorkspace GetWorkspace(WorkspaceId workspaceId)
        {
            throw new KeyNotFoundException("No mutation baseline was registered.");
        }

        public Task<StagedMutationSet> StageAsync(
            MutationSet mutationSet,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<WorkspaceBaseline> PromoteBaselineAsync(
            WorkspaceId workspaceId,
            IReadOnlyList<string> changedFiles,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}