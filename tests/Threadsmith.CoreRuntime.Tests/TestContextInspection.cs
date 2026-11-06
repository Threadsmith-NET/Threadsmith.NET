namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Core;

internal static class TestContextInspection
{
    internal static ContextInspectionProjection Create()
    {
        var memory = new RepositoryMemoryContextItemProjection
        {
            Id = RepositoryMemoryId.New(),
            Included = true,
            Rationale = "selected",
            EstimatedTokens = 7,
            Text = "guidance",
            Revision = 3,
            Supports = new List<RepositoryMemorySupport>
            {
                new() { Path = "src/sample.cs", ContentHash = "hash", ObservedAt = DateTimeOffset.UtcNow },
            },
        };
        var search = new RepositoryMemorySearchDetails
        {
            Candidates = new RepositoryMemoryCandidateSearchEvidence[]
            {
                new(memory.Id, "selected", 1, 2, true, 0.75, 4, 7) { MatchedTerms = new List<string> { "first", "second" } },
            },
            LexicalExpansions = new List<MemoryTermMatch> { new("fist", "first", 1) },
            ElapsedMilliseconds = 1.25,
        };
        return new ContextInspectionProjection
        {
            RunId = RunId.New(),
            Phase = RunPhase.EvidenceCollection,
            EstimatedTokens = 123,
            TokenBudget = 456,
            TokensByCategory = new Dictionary<string, int>(StringComparer.Ordinal) { ["first"] = 1, ["second"] = 2 },
            Evidence = new ContextEvidenceProjection[] { new(EvidenceId.New(), "source", true, "selected", 5, false) },
            PromptAssets = new List<PromptAssetReference> { new("system", "version", "host", 0, 20) },
            ModelRationale = new List<string> { "first", "second" },
            Reductions = new[] { "trimmed" },
            ConversationItems = new ConversationContextItemProjection[]
            {
                new()
                {
                    Id = "conversation",
                    Kind = "memory",
                    Included = true,
                    Rationale = "recent",
                    EstimatedTokens = 8,
                    Images = new ImageArtifact[]
                    {
                        new() { Id = "image", ContentHash = "hash", MediaType = "image/png", ByteLength = 1, Width = 1, Height = 1, Source = "test" },
                    },
                    SourceMessageIds = new[] { ConversationMessageId.New() },
                    SourceRunIds = new List<RunId> { RunId.New() },
                    SourceEvidenceIds = new[] { EvidenceId.New() },
                },
            },
            RepositoryMemoryItems = new[] { memory },
            RepositoryMemorySearch = search,
            RepositoryIntelligenceSearch = search with { },
            RepositoryMemoryDispatch = new(new List<RepositoryMemoryInclusion> { new(memory.Id, memory.Revision) }, "recorded", 2.5),
            VisibleSourceFrontier = new(1, 2, 3, 4, "frontier"),
            RequestUsage = new()
            {
                RunId = RunId.New(),
                InvocationId = Guid.NewGuid(),
                Stage = "primary",
                EstimationBasis = "estimate",
                Memories = new[] { memory },
                Components = new List<ContextUsageComponent>
                {
                    new("parent", "history", "History", "request", 8)
                    {
                        Children = new ContextUsageComponent[]
                        {
                            new("child", "history", "Child", "request", 8)
                            {
                                Children = new[] { new ContextUsageComponent("leaf", "history", "Leaf", "request", 8) },
                            },
                        },
                    },
                },
            },
        };
    }
}
