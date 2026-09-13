namespace Threadsmith.ParallelAgents.Tests;

using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public sealed partial class ModelExplorerAssignmentRunnerTests
{
    /// <summary>Verifies the focused review boundary and its observable result.</summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public async Task FocusedCompletionIsOptInFreshAndUsesSameConversation(
        int corrections,
        bool succeeds)
    {
        await using var events = new DomainEventStream();
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var profile = CreateRoleProfile();
        var binding = new FocusedReviewBinding(
            SkillInvocationId.New(),
            1,
            "recipe",
            new SkillPackageIdentity(new SkillId("private-test"), "package", "1.0.0", new SkillDigest("sha256", "digest"), "test"),
            "inspect",
            "instructions",
            "schema",
            "plan91-baseline",
            AgentRole.SecurityReviewer);
        var assignment = CreateAssignment(profile.Id, []) with { Role = AgentRole.SecurityReviewer, FocusedReview = binding, OutputSchema = "focused-review/1" };
        var plan = CreatePlan(assignment);
        plan = plan with { Provenance = plan.Provenance with { ReviewInvocationId = binding.InvocationId } };
        assignment = plan.Assignments[0];
        await evidence.AddAsync(CreateParentEvidence(plan, EvidenceId.New(), "PARENT_IMPLEMENTATION_CANARY", EvidenceSensitivity.None));
        var provider = new FindingSequenceProvider("invalid", "accepted");
        var registry = new ToolRegistry([]);
        var policy = new TestFocusedPolicy(binding, corrections);
        var runner = new ModelExplorerAssignmentRunner(
            new AgentContextAssembler(evidence),
            new AgentFindingAdmission(evidence),
            CreateSelector(profile),
            provider,
            CreatePipeline(registry, events, sanitizer),
            evidence,
            new StubInstructionProvider(),
            sanitizer,
            CreateOptions(),
            CreateParentContext(plan, []),
            [],
            TestPromptLoader.Instance,
            focused: policy);
        if (succeeds)
        {
            var result = await runner.RunAsync(plan, assignment);
            Assert.True(result.FocusedReviewValidated);
            Assert.Equal("accepted", result.Response);
            Assert.Equal(1, result.Usage.Corrections);
            Assert.Equal(2, provider.Requests.Count);
            Assert.Contains(provider.Requests[1].Messages, message => message.SectionId == "focused-review-correction");
        }
        else
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => runner.RunAsync(plan, assignment));
            Assert.Single(provider.Requests);
        }

        Assert.All(
            provider.Requests,
            request =>
        {
            var content = string.Join('\n', request.Messages.Select(message => message.GetModelVisibleContent()));
            Assert.DoesNotContain("PARENT_IMPLEMENTATION_CANARY", content, StringComparison.Ordinal);
            Assert.Contains("OWN_SPECIALIST_CANARY", content, StringComparison.Ordinal);
            Assert.False(request.RequiredCapabilities.StructuredOutput);
            Assert.DoesNotContain(request.Tools, tool => tool.Name is "invoke_skill" or "delegate_agents");
        });
    }

    /// <summary>Verifies the focused review boundary and its observable result.</summary>
    [Fact]
    public async Task FocusedBindingCannotFallBackToOrdinaryRunner()
    {
        await using var events = new DomainEventStream();
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var profile = CreateRoleProfile();
        var assignment = CreateAssignment(profile.Id, []) with
        {
            FocusedReview = new FocusedReviewBinding(
            SkillInvocationId.New(),
            1,
            "recipe",
            new SkillPackageIdentity(new SkillId("private"), "package", "1", new SkillDigest("sha256", "digest"), "test"),
            "inspect",
            "instructions",
            "schema",
            "snapshot",
            AgentRole.SecurityReviewer),
        };
        var plan = CreatePlan(assignment);
        var provider = new FindingSequenceProvider(string.Empty);
        var registry = new ToolRegistry([]);
        var runner = CreateRunner(provider, CreatePipeline(registry, events, sanitizer), evidence, sanitizer, profile, CreateParentContext(plan, []), []);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => runner.RunAsync(plan, plan.Assignments[0]));
        Assert.Empty(provider.Requests);
    }

    private sealed class TestFocusedPolicy : IFocusedReviewCompletionPolicy
    {
        public TestFocusedPolicy(FocusedReviewBinding binding, int maximumCorrections)
        {
            Binding = binding;
            MaximumCorrections = maximumCorrections;
        }

        public FocusedReviewBinding Binding { get; }

        public string Instructions => "OWN_SPECIALIST_CANARY";

        public string OutputSchema => "{\"type\":\"object\"}";

        public int MaximumCorrections { get; }

        public void AdmitModelTurn()
        {
        }

        public void AdmitToolCalls(int count)
        {
        }

        public void RecordRead(string path, int startLine, int endLine)
        {
        }

        public string Validate(AgentAssignment assignment, string response) => response == "accepted" ? response : throw new InvalidDataException("invalid");
    }
}
