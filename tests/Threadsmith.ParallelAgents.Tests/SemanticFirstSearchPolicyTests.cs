namespace Threadsmith.ParallelAgents.Tests;

using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Tools;
using Xunit;

/// <summary>Verifies semantic-first corrections require evidence that a search targets C# files.</summary>
public sealed class SemanticFirstSearchPolicyTests
{
    /// <summary>MSBuild text searches remain available while C# discovery uses semantic inspection.</summary>
    [Theory]
    [InlineData("PackageReference", "*.{csproj,props,targets}", false)]
    [InlineData("PackageVersion", "*.{csproj,props,targets}", false)]
    [InlineData("PackageReference", null, false)]
    [InlineData("PackageReference", "*", false)]
    [InlineData("SectorEntityStandardizer", "*.cs", true)]
    [InlineData("SectorEntityStandardizer", "*.{cs,csproj}", true)]
    [InlineData("SectorEntityStandardizer", "*.csproj", false)]
    [InlineData("src/SectorEntityStandardizer.cs", null, true)]
    [InlineData("src/SectorEntityStandardizer.cs", "*.props", false)]
    public static void CorrectionRequiresCSharpSearchScope(string query, string? glob, bool expectedCorrection)
    {
        var arguments = glob is null
            ? JsonSerializer.Serialize(new { query, path = "." })
            : JsonSerializer.Serialize(new { query, glob, path = "." });
        var context = new ToolInvocationContext
        {
            WorkspaceId = WorkspaceId.New(),
            RepositoryPath = Environment.CurrentDirectory,
            TrustLevel = RepositoryTrustLevel.TrustedRead,
            RequestedBy = "model",
        };
        ModelToolDefinition[] tools =
        [
            new() { Name = "find_symbol", Description = "Find a symbol", ArgumentsJsonSchema = "{}" },
            new() { Name = "read_file", Description = "Read a file", ArgumentsJsonSchema = "{}" },
        ];

        var rejected = SemanticFirstSearchPolicy.TryCreateCorrection(
            new ToolRequestModelOutput("search", arguments),
            context,
            semanticToolAttempted: false,
            tools,
            new CorrectiveMessageFactory(TestPromptLoader.Instance),
            out var correction);

        Assert.Equal(expectedCorrection, rejected);
        Assert.Equal(expectedCorrection, correction is not null);
    }
}
