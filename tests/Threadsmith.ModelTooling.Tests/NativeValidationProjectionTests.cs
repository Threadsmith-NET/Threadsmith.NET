namespace Threadsmith.ModelTooling.Tests;

using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Tools;
using Xunit;

/// <summary>Clipped model-facing fields always advertise incomplete evidence.</summary>
public static class NativeValidationProjectionTests
{
    /// <summary>Build and query evidence mark clipped diagnostic text, preserving complete subsequent projections.</summary>
    [Theory]
    [InlineData("code")]
    [InlineData("project")]
    [InlineData("framework")]
    [InlineData("file")]
    [InlineData("message")]
    public static void DiagnosticFieldClipping_IsReportedByBuildAndQuery(string field)
    {
        var projection = new NativeValidationModelProjection(new ValidationResourceLimits
        {
            MaximumModelIdentifierCharacters = 4,
            MaximumModelSummaryCharacters = 4,
            MaximumModelPathCharacters = 4,
            MaximumModelMessageCharacters = 4,
        });
        var original = new Diagnostic
        {
            Id = "id", Code = "code", Project = "app", TargetFramework = "net",
            File = "a.cs", Message = "text", Severity = DiagnosticSeverity.Warning,
            Confidence = SemanticConfidenceLevel.PartialCompilation,
        };
        const string tooLong = "abc😀";
        var clipped = field switch
        {
            "code" => original with { Code = tooLong },
            "project" => original with { Project = tooLong },
            "framework" => original with { TargetFramework = tooLong },
            "file" => original with { File = tooLong },
            "message" => original with { Message = tooLong },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        foreach (var diagnostic in new[] { clipped, original })
        {
            var build = new ValidationToolResult("build", ValidationInvocationKind.Build, ValidationAuthority.Exploratory, true, "app", [], [diagnostic], string.Empty, 0, TimeSpan.Zero, false, false);
            var query = new DiagnosticQueryResult(
                [new DiagnosticQueryItem("build", RunId.New(), "app", DiagnosticOrigin.Compiler, ValidationAuthority.Exploratory, diagnostic)],
                1,
                null);
            foreach (var json in new[] { projection.Create(build), projection.Create(query, false) })
            {
                using var document = JsonDocument.Parse(json);
                Assert.Equal(diagnostic == clipped, document.RootElement.GetProperty("truncated").GetBoolean());
                if (diagnostic == clipped)
                {
                    Assert.Equal("abc", document.RootElement.GetProperty("diagnostics")[0].GetProperty(field).GetString());
                }
            }
        }
    }

    /// <summary>The truncation flag includes attachment paths projected after other result fields.</summary>
    [Fact]
    public static void TestAttachmentClipping_IsReportedAfterProjection()
    {
        var projection = new NativeValidationModelProjection(new ValidationResourceLimits { MaximumModelPathCharacters = 4 });
        var test = new DiscoveredTest(new("test"), "name", "a.cs", null, null, null, new Dictionary<string, string>());
        var result = new TargetedTestResult(test, "filter", ValidationAuthority.Exploratory, TestOutcome.Passed, 1, 0, 0, string.Empty, TimeSpan.Zero, false, false, ["long-path"]);
        using var document = JsonDocument.Parse(projection.Create(result));
        Assert.True(document.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal("long", document.RootElement.GetProperty("attachments")[0].GetString());
    }
}
