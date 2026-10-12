namespace Threadsmith.Execution;

using System.Text.Json;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Models;
using Threadsmith.Tools;

/// <summary>Supplies onboarding batches through normal tool policy, activity, and compactable conversation state.</summary>
public sealed partial class SessionApplication
{
    private async Task SupplyIntelligenceHistoryAsync(
        RunId runId,
        RunRegistration registration,
        RunPhase phase,
        ToolInvocationContext context,
        ConversationLoopState loopState,
        int modelRound,
        CancellationToken cancellationToken)
    {
        var intelligence = registration.IntelligenceRun ?? throw new InvalidOperationException("No collection owns this history request.");
        var page = intelligence.PendingHistoryPage ?? throw new InvalidOperationException("History is already complete.");
        for (var replayPage = 0; ; replayPage++)
        {
            var current = await CreateToolInvocationContextAsync(registration, cancellationToken)
                ?? throw new InvalidOperationException("The repository tool context is unavailable.");
            if (RepositoryIdentity.Create(current.RepositoryPath) != RepositoryIdentity.Create(context.RepositoryPath))
            {
                throw new InvalidOperationException("The repository changed during history collection.");
            }

            context = current with { IntelligenceRun = intelligence, OperationScope = context.OperationScope };
            var snapshot = ConversationToolAvailability.CreateSnapshot(
                _toolPipeline, _toolRegistry, registration.SessionId, runId, context, toolsWithheld: false);
            var tool = snapshot.Registrations.SingleOrDefault(item => item.Implementation is GitLogTool)
                ?? throw new InvalidOperationException("The built-in history tool is unavailable. The prior collection was preserved.");
            var pipeline = _toolPipeline ?? throw new InvalidOperationException("The tool pipeline is unavailable.");
            var result = await pipeline.InvokeAsync(
                new ToolInvocationRequest
                {
                    SessionId = registration.SessionId,
                    RunId = runId,
                    Phase = phase,
                    ToolId = "git_log",
                    ExpectedRegistration = tool,
                    InvocationKey = $"intelligence-history:{page}:{replayPage}",
                    ArgumentsJson = JsonSerializer.Serialize(new GitLogRequest { MaximumCommits = 16 }),
                    Context = context with { RequestedBy = GitLogRequest.CollectionRequestor, ModelVisibleToolSnapshotId = null },
                },
                cancellationToken);
            if (!result.Succeeded || result.IsTruncated || result.ResultJson is null || result.ModelResultContent is null)
            {
                throw new InvalidOperationException($"Onboarding history could not be delivered. The prior collection was preserved. {result.Error}");
            }

            var history = JsonSerializer.Deserialize<GitLogResult>(result.ResultJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("The history tool returned no page.");
            if (intelligence.ResumeHistoryRevision == history.HistoryRevision && history.CollectionPage < page)
            {
                continue;
            }

            var sources = new List<ActiveTurnSourceReference>
            {
                new(ActiveTurnSourceKind.ToolInvocation, result.ToolInvocationId.Value.ToString("D"), 0),
            };
            sources.AddRange(result.Sources.Select(source => new ActiveTurnSourceReference(ActiveTurnSourceKind.ToolProvenance, JsonSerializer.Serialize(source), 0)));
            if (_evidenceStore is not null)
            {
                var evidence = await ToolEvidenceAdmission.AdmitEvidenceAsync(
                    _evidenceStore, registration.SessionId, runId, context.RepositoryPath, result, result.ModelResultContent, cancellationToken);
                sources.Add(new ActiveTurnSourceReference(ActiveTurnSourceKind.Evidence, evidence.Value.ToString("D"), 0));
            }

            var historyInstructions = await intelligence.AcceptHistoryPageAsync(page, history, result.ModelResultContent, cancellationToken);
            loopState.CommitStandaloneMessage(
                modelRound,
                new ModelMessage
                {
                    Role = ModelMessageRole.HostContext,
                    SectionId = $"repository-intelligence-history-{history.CollectionPage}",
                    Content = [new ModelContentPart { Content = historyInstructions }],
                },
                purgeAfterCorrection: false,
                sources);
            break;
        }
    }
}
