namespace Threadsmith.Planning.Tests;

using System.Text.Json.Nodes;
using Threadsmith.Core;
using Threadsmith.Models;
using Xunit;

/// <summary>Plan proposals contain reasoning content; the host owns identity and version bookkeeping.</summary>
public static class PlanContentTests
{
    private const string Proposal = """
        {
          "summary": "Remove the test field.",
          "steps": [{
            "title": "Remove field",
            "description": "Remove the previously added field.",
            "fileIntents": [{"kind": "Modify", "path": "src/Example.cs"}],
            "expectedOutcome": "The field is absent.",
            "validation": ["Build the project."]
          }],
          "risks": [],
          "outstandingQuestions": []
        }
        """;

    /// <summary>Plan parsing accepts model prose around a complete proposal.</summary>
    [Fact]
    public static void PlanContentAcceptsFramedJson()
    {
        var output = ModelOutputValidator.ParsePlan("Proposed plan:\n```json\n" + Proposal + "\n```\nReady.");

        Assert.Equal("Remove the test field.", output.Plan.Summary);
    }

    /// <summary>Content-only JSON becomes a versioned plan with host-generated identities.</summary>
    [Fact]
    public static void PlanContentReceivesHostVersionRevisionAndUniqueStepIdentities()
    {
        var json = JsonNode.Parse(Proposal)!;
        json["steps"]!.AsArray().Add(json["steps"]![0]!.DeepClone());
        var first = ModelOutputValidator.ParsePlan(json.ToJsonString()).Plan;
        var second = ModelOutputValidator.ParsePlan(json.ToJsonString()).Plan;

        Assert.Equal(2, first.SchemaVersion);
        Assert.Equal(1, first.Revision);
        Assert.All(first.Steps, step => Assert.NotEqual(default, step.StepId));
        Assert.Equal(2, first.Steps.Select(step => step.StepId).Distinct().Count());
        Assert.Empty(first.Steps.Select(step => step.StepId).Intersect(second.Steps.Select(step => step.StepId)));
        Assert.Equal("src/Example.cs", first.Steps[0].FileIntents[0].Path);
    }

    /// <summary>The current proposal contract rejects old bookkeeping fields without a compatibility fallback.</summary>
    [Theory]
    [InlineData("schemaVersion")]
    [InlineData("revision")]
    [InlineData("stepId")]
    public static void ModelAuthoredBookkeepingIsNotPartOfTheNewContract(string field)
    {
        var json = JsonNode.Parse(Proposal)!;
        var target = field == "stepId" ? json["steps"]![0]! : json;
        target[field] = "model-authored";

        var failure = Assert.Throws<MalformedInvocationException>(() => ModelOutputValidator.ParsePlan(json.ToJsonString()));

        Assert.Equal(MalformedInvocationFailureKind.PlanSchemaMismatch, failure.Diagnostic.Kind);
        Assert.Contains("field names and value types", failure.Diagnostic.SafeMessage, StringComparison.Ordinal);
    }

    /// <summary>Configured resource bounds remain validated before accepting plan content.</summary>
    [Fact]
    public static void HostPlanValidationStillRejectsInvalidResourceLimits()
    {
        var output = ModelOutputValidator.ParsePlan(Proposal);

        Assert.ThrowsAny<ArgumentException>(() => ModelOutputValidator.Validate(output, planLimits: new PlanResourceLimits { MaximumMetadataItems = 0 }));
    }

    /// <summary>Correction feedback identifies the invalid field while excluding rejected content.</summary>
    [Theory]
    [InlineData("summary", "summary")]
    [InlineData("title", "steps[0].title")]
    [InlineData("path", "steps[0].fileIntents[0].path")]
    [InlineData("destination", "steps[0].fileIntents[0].destinationPath")]
    [InlineData("validation", "steps[0].validation")]
    [InlineData("steps", "steps")]
    public static void ContentFailuresIdentifyTheFieldWithoutEchoingItsValue(string defect, string expectedField)
    {
        var json = JsonNode.Parse(Proposal)!;
        switch (defect)
        {
            case "summary":
                json["summary"] = string.Empty;
                break;
            case "title":
                json["steps"]![0]!["title"] = string.Empty;
                break;
            case "path":
                json["steps"]![0]!["fileIntents"]![0]!["path"] = "../private-value";
                break;
            case "destination":
                json["steps"]![0]!["fileIntents"]![0]!["kind"] = "Move";
                break;
            case "validation":
                json["steps"]![0]!["validation"] = null;
                break;
            case "steps":
                json["steps"] = new JsonArray();
                break;
        }

        var failure = Assert.Throws<MalformedInvocationException>(() => ModelOutputValidator.ParsePlan(json.ToJsonString()));

        Assert.StartsWith(expectedField, failure.Diagnostic.SafeMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("private-value", failure.Diagnostic.SafeMessage, StringComparison.Ordinal);
    }
}
