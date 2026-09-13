namespace Threadsmith.App;

using Microsoft.Extensions.Configuration;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Skills;
using Threadsmith.Tools;

/// <summary>Composes the focused review entry without introducing Skills/Execution implementation dependencies.</summary>
internal static class FocusedReviewComposition
{
    /// <summary>Registers only the exact shipped recipe when the ordinary role runner is available.</summary>
    internal static ISkillReviewActionHandler? Create(
        HostCompositionInputs host,
        ToolPolicyCompositionInputs tools,
        ModelExplorerAssignmentRunnerFactory? runners,
        DelegationCoordinator coordinator,
        AgentModelSelector models,
        SessionModelPreferences preferences,
        DelegateAgentsOptions options,
        IConversationToolSnapshotStore snapshots,
        ISkillPackageVerifier verifier,
        SkillSchemaOptions schemaOptions,
        SkillCatalogOptions catalogOptions)
    {
        if (runners is null)
        {
            return null;
        }

        async Task<ToolInvocationContext> AuthorityAsync(
            SkillInvocationRequest request,
            CancellationToken cancellationToken)
        {
            var state = await host.Projections.GetAsync<SessionProjection>(
                new ProjectionKey("session", request.SessionId.Value.ToString("D")),
                cancellationToken)
                ?? throw new InvalidOperationException("Review invocation session is unavailable.");
            return ApplicationComposition.CreateToolInvocationContext(host, state) with
            {
                RequestedBy = request.ModelVisibleToolSnapshotId is null ? "explicit-skill-user" : "explicit-skill-model",
                Sensitivity = request.Sensitivity,
                ModelVisibleToolSnapshotId = request.ModelVisibleToolSnapshotId,
            };
        }

        var stateRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Threadsmith", "reviews");
        var schemas = new BoundedJsonSchemaValidator(schemaOptions);
        var privateResolver = new FocusedReviewPrivateResolver(
            AppContext.BaseDirectory,
            verifier,
            new SkillContentLoader(host.Sanitizer),
            schemas,
            catalogOptions);
        var capture = new FocusedReviewTargetCapture(tools.ProcessManager, host.Sanitizer, Path.Combine(stateRoot, "objects"));
        var executor = new FocusedReviewExecutor(
            runners,
            coordinator,
            models,
            preferences,
            options,
            snapshots,
            tools.ToolRegistry,
            tools.ToolPipeline,
            AuthorityAsync,
            host.PromptLoader);
        return new FocusedReviewWorkflow(
            privateResolver,
            capture,
            executor,
            AuthorityAsync,
            Path.Combine(stateRoot, "runs"),
            host.TrustedConfiguration.GetValue("skills:review:maximumFormatCorrections", 0));
    }
}
