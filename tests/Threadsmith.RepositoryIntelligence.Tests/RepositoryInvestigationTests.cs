namespace Threadsmith.RepositoryIntelligence.Tests;

using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Telemetry;
using Xunit;

/// <summary>Verifies investigation admission and configured inference limits through the shared host.</summary>
public sealed class RepositoryInvestigationTests
{
    /// <summary>Lower output ceilings remain usable on every inference call, including evidence expansion.</summary>
    [Theory]
    [InlineData(1024, 1024)]
    [InlineData(2048, 2048)]
    [InlineData(4096, 4096)]
    [InlineData(8192, 4096)]
    public async Task InvestigationUsesSelectedProfileOutputCeilingAcrossExpansion(int profileCeiling, int expectedAllowance)
    {
        // Arrange: use the real host admission boundary and governed evidence readers.
        var token = TestContext.Current.CancellationToken;
        await using var host = await TestProfileHost.CreateAsync();
        await host.WriteAsync("README.md", "# Cache decision\nThe cache lifetime is 60 seconds.\n");
        await host.CommitAsync();
        var profile = new ModelProfile
        {
            Id = ModelProfileId.New(),
            Name = "bounded-investigation",
            Provider = "test",
            Endpoint = new Uri("https://model.example.invalid/v1/chat/completions"),
            ModelId = "structured-model",
            ContextWindow = 65536,
            MaximumOutputTokens = profileCeiling,
            Capabilities = new() { Streaming = true, StructuredOutput = true },
            SensitiveDataPolicy = ModelSensitiveDataPolicy.Allowed,
        };
        var provider = new ExpandingInterpretationProvider();
        await using var events = new DomainEventStream();
        var application = new SessionApplication(
            events,
            provider,
            new ExecutionBudget(new BudgetDimensions(100000, 10, TimeSpan.FromMinutes(5))),
            new SecretOutputSanitizer(),
            NullLogger<SessionApplication>.Instance,
            defaultModelProfileId: profile.Id,
            correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
            prompts: TestPromptLoader.Instance,
            inferenceCatalog: new ConfiguredModelCatalog([profile]));
        var session = await application.HandleAsync(new CreateSessionCommand("Output ceiling regression"), token);
        await using var feature = new RepositoryIntelligenceFeature(host.Settings, host.Repository, new(8, 0, 2));

        // Act: the investigation pre-admits the model, then performs an inspection expansion.
        var result = await host.RunEvidenceAsync(
            (context, cancellationToken) => new RepositoryInvestigation(host.Reads, application, TestPromptLoader.Instance)
                .ExecuteAsync(
                    feature,
                    new()
                    {
                        Question = "What cache lifetime is documented?",
                        Paths = ["README.md"],
                        MaximumFiles = 8,
                        MaximumModelCalls = 2,
                    },
                    context,
                    cancellationToken),
            sessionId: session);

        // Assert: inference actually reached the provider and produced validated findings.
        Assert.Equal("Findings", result.Interpretation.Outcome);
        Assert.Equal("The cache lifetime is 60 seconds.", result.Interpretation.Answer);
        Assert.NotEmpty(result.Interpretation.SupportingEvidenceIds);
        Assert.Equal(profile.Id, result.Model?.ProfileId);
        Assert.Equal(2, result.ModelCalls);
        Assert.Equal(2, provider.Requests.Count);
        Assert.All(provider.Requests, request =>
        {
            Assert.Equal(expectedAllowance, request.MaximumOutputTokens);
            Assert.Equal(profile.Id, request.ResolvedProfileId);
            Assert.NotNull(request.ResponseFormat);
            Assert.Empty(request.Tools);
        });
        Assert.False(Directory.Exists(Path.Combine(host.Repository, ".threadsmith", "repository-intelligence")));
    }

    private sealed class ExpandingInterpretationProvider : IModelProvider
    {
        public List<ModelStreamRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            using var data = JsonDocument.Parse(request.Input);
            var operationId = data.RootElement.GetProperty("OperationId").GetString();
            var evidenceId = data.RootElement.GetProperty("Packets")[0].GetProperty("Evidence")
                .EnumerateArray().First(item => item.GetProperty("Source").GetProperty("Path").GetString() == "README.md")
                .GetProperty("Id").GetString();
            var expand = Requests.Count == 1;
            var response = new
            {
                operationId,
                outcome = expand ? "InsufficientEvidence" : "Findings",
                answer = expand ? null : "The cache lifetime is 60 seconds.",
                answerAssessment = expand ? null : new
                {
                    applicability = "Current",
                    confidence = "High",
                    evidenceClass = "Documented",
                    uncertainty = (string?)null,
                    then = (string?)null,
                    replacement = (string?)null,
                    atTarget = "The pinned revision documents a 60-second cache lifetime.",
                    causalClaim = false,
                    rationaleQuote = (string?)null,
                },
                supportingEvidenceIds = expand ? Array.Empty<string?>() : [evidenceId],
                conflictingEvidenceIds = Array.Empty<string>(),
                limitations = Array.Empty<string>(),
                candidates = Array.Empty<object>(),
                relationships = Array.Empty<object>(),
                expansion = expand ? new { kind = "Inspect", evidenceId, startLine = 1, endLine = 2 } : null,
            };
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelChunk { Text = JsonSerializer.Serialize(response) };
        }
    }
}
