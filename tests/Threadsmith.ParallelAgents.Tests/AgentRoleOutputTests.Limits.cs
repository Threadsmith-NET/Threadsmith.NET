namespace Threadsmith.ParallelAgents.Tests;

using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Xunit;

/// <summary>Verifies ordinary child prompts and tool feedback do not impose an answer format or result limits.</summary>
public sealed partial class AgentRoleOutputTests
{
    /// <summary>All six role amendments remain distinct without a response schema or required answer fields.</summary>
    [Fact]
    public static void Prompt_HasDistinctAmendmentsWithoutAnswerContract()
    {
        var amendments = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in Enum.GetValues<AgentRole>())
        {
            var messages = new ChildAgentPrompt(TestPromptLoader.Instance, role)
                .CreateMessages(CreatePromptContext(), CreateInstructions());

            Assert.True(amendments.Add(messages[1].GetModelVisibleContent()));
            Assert.DoesNotContain(messages, message => message.SectionId == "child-output-policy");
            Assert.All(messages, message => AssertNoAnswerContract(message.GetModelVisibleContent()));
        }

        Assert.Equal(Enum.GetValues<AgentRole>().Length, amendments.Count);
    }

    /// <summary>Technical tool errors use the same host feedback for every role without prescribing a reply shape.</summary>
    [Fact]
    public static void ToolFeedback_DoesNotConstrainAnswerFormat()
    {
        const string reason = "read_file: path is required.";
        var prompts = TestPromptLoader.Instance;
        var feedback = new CorrectiveMessageFactory(prompts).CreateDeveloperMessage(
            new MalformedInvocationDiagnostic { Kind = MalformedInvocationFailureKind.ArgumentSchemaMismatch, SafeMessage = reason }, 1, 3);

        Assert.Equal(ModelMessageRole.Developer, feedback.Role);
        Assert.Contains(reason, feedback.GetModelVisibleContent(), StringComparison.Ordinal);
        AssertNoAnswerContract(feedback.GetModelVisibleContent());
    }

    /// <summary>Every tool-progress branch reports telemetry without requesting a structured answer.</summary>
    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(0, 0, 1)]
    [InlineData(0, 0, 0)]
    public static void ToolProgress_DoesNotReintroduceAnswerContract(int newFiles, int newSources, int newPayloads)
    {
        var progress = new ChildAgentEvidenceProgress(newFiles, newSources, newPayloads, 2, 3, 4);
        var feedback = new ChildAgentPrompt(TestPromptLoader.Instance).CreateEvidenceProgressMessage(progress);
        var content = feedback.GetModelVisibleContent();

        Assert.Equal(ModelMessageRole.Developer, feedback.Role);
        Assert.Equal("child-evidence-progress", feedback.SectionId);
        Assert.Contains("2 file(s)", content, StringComparison.Ordinal);
        Assert.Contains("3 source identity or identities", content, StringComparison.Ordinal);
        Assert.Contains("4 evidence item(s)", content, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", content, StringComparison.Ordinal);
        AssertNoAnswerContract(content);
    }

    /// <summary>Planning's retained task asset encourages investigation without requesting a finding envelope.</summary>
    [Fact]
    public static void TaskGuidance_DoesNotRequireStructuredFindings()
    {
        var content = TestPromptLoader.Instance.Get(PromptFileNames.ContextChildAgentStructuredFindingsTask);

        AssertNoAnswerContract(content);
        Assert.DoesNotContain("structured findings", content, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertNoAnswerContract(string content)
    {
        Assert.DoesNotContain("agent-findings/1", content, StringComparison.Ordinal);
        Assert.DoesNotContain("RoleSchema", content, StringComparison.Ordinal);
        Assert.DoesNotContain("evidenceIds", content, StringComparison.Ordinal);
        Assert.DoesNotContain("expectedAssertion", content, StringComparison.Ordinal);
        Assert.DoesNotContain("maxLength", content, StringComparison.Ordinal);
        Assert.DoesNotContain("maxItems", content, StringComparison.Ordinal);
        Assert.DoesNotContain("required JSON", content, StringComparison.OrdinalIgnoreCase);
    }
}
