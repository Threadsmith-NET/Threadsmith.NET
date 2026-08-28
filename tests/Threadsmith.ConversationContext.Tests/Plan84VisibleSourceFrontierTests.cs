namespace Threadsmith.ConversationContext.Tests;

using System.Text.Json;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Models;
using Xunit;

/// <summary>Context-visible source frontier tests for Plan 84.</summary>
public static class Plan84VisibleSourceFrontierTests
{
    /// <summary>Only complete sanitized code-explore tool results with digests become visible-source entries.</summary>
    [Fact]
    public static void Build_IncludesOnlyCompleteCodeExploreSourceWithDigest()
    {
        var workspaceId = WorkspaceId.New();
        var complete = CreateCodeExploreResult(
            "src/A.cs",
            new SourceRange(10, 1, 18, 1),
            CodeExploreSourceCompleteness.Complete,
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var partial = CreateCodeExploreResult(
            "src/B.cs",
            new SourceRange(1, 1, 4, 1),
            CodeExploreSourceCompleteness.Partial,
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var messages = new[]
        {
            CreateToolResult("holder-a", "code_explore", complete),
            CreateToolResult("holder-b", "code_explore", partial),
            CreateToolResult("holder-c", "read_file", complete),
            CreateMalformedToolResult("holder-d"),
        };

        var frontier = ModelVisibleSourceFrontierBuilder.Build(
            messages,
            "C:\\repo",
            workspaceId,
            7);

        var entry = Assert.Single(frontier.Entries);
        Assert.Equal("C:\\repo", frontier.RepositoryPath);
        Assert.Equal(workspaceId, frontier.WorkspaceId);
        Assert.Equal(7, frontier.FrontierGeneration);
        Assert.Equal("tool-result", entry.HolderId);
        Assert.Equal("holder-a", entry.ToolCallId);
        Assert.Equal("src/A.cs", entry.FilePath);
        Assert.Equal(new SourceRange(10, 1, 18, 1), entry.Range);
        Assert.Equal(1, frontier.RangeCount);
        Assert.True(frontier.SourceCharacters > 0);
    }

    /// <summary>Host-only structured sidecars preserve code-explore source frontier in Markdown output mode.</summary>
    [Fact]
    public static void Build_ReadsHostOnlyCodeExploreSidecarButIgnoresVisibleMarkdown()
    {
        var workspaceId = WorkspaceId.New();
        var result = CreateCodeExploreResult(
            "src/Markdown.cs",
            new SourceRange(4, 1, 9, 1),
            CodeExploreSourceCompleteness.Complete,
            "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc");
        var messages = new[]
        {
            new ModelMessage
            {
                Role = ModelMessageRole.Tool,
                SectionId = "tool-result",
                ToolCallId = "holder-markdown",
                ToolName = "code_explore",
                Content =
                [
                    new ModelContentPart
                    {
                        Kind = ModelContentPartKind.Text,
                        Content = "# code_explore result\nvisible markdown only",
                    },
                    new ModelContentPart
                    {
                        Kind = ModelContentPartKind.Json,
                        Content = JsonSerializer.Serialize(result),
                        IsModelVisible = false,
                    },
                ],
            },
        };

        var frontier = ModelVisibleSourceFrontierBuilder.Build(
            messages,
            "C:\\repo",
            workspaceId,
            11);

        var entry = Assert.Single(frontier.Entries);
        Assert.Equal("holder-markdown", entry.ToolCallId);
        Assert.Equal("src/Markdown.cs", entry.FilePath);
        Assert.Equal(new SourceRange(4, 1, 9, 1), entry.Range);
        Assert.Equal(1, frontier.RangeCount);
    }

    /// <summary>Reduced host-only sidecars remain structurally incomplete and do not create stale frontier entries.</summary>
    [Fact]
    public static void Build_SkipsStructurallyIncompleteHostOnlyCodeExploreSidecar()
    {
        var frontier = ModelVisibleSourceFrontierBuilder.Build(
            [new ModelMessage
            {
                Role = ModelMessageRole.Tool,
                SectionId = "tool-result",
                ToolCallId = "holder-reduced",
                ToolName = "code_explore",
                Content =
                [
                    new ModelContentPart
                    {
                        Kind = ModelContentPartKind.Text,
                        Content = "# reduced markdown",
                    },
                    new ModelContentPart
                    {
                        Kind = ModelContentPartKind.Json,
                        Content = "{\"isTruncated\":true}",
                        IsModelVisible = false,
                    },
                ],
            },
            ],
            "C:\\repo",
            WorkspaceId.New(),
            12);

        Assert.Empty(frontier.Entries);
        Assert.Equal(0, frontier.RangeCount);
        Assert.Equal(0, frontier.SourceCharacters);
    }

    /// <summary>Valid but structurally incomplete reduced JSON is ignored instead of aborting context assembly.</summary>
    [Fact]
    public static void Build_SkipsStructurallyIncompleteCodeExploreJson()
    {
        var frontier = ModelVisibleSourceFrontierBuilder.Build(
            [CreateRawToolResult("holder-reduced", "code_explore", "{\"isTruncated\":true}")],
            "C:\\repo",
            WorkspaceId.New(),
            9);

        Assert.Empty(frontier.Entries);
        Assert.Equal(0, frontier.RangeCount);
        Assert.Equal(0, frontier.SourceCharacters);
    }

    private static ModelMessage CreateToolResult(
        string toolCallId,
        string toolName,
        CodeExploreResult result)
    {
        return new ModelMessage
        {
            Role = ModelMessageRole.Tool,
            SectionId = "tool-result",
            ToolCallId = toolCallId,
            ToolName = toolName,
            Content =
            [
                new ModelContentPart
                {
                    Kind = ModelContentPartKind.Json,
                    Content = JsonSerializer.Serialize(result),
                },
            ],
        };
    }

    private static ModelMessage CreateMalformedToolResult(string toolCallId)
    {
        return CreateRawToolResult(toolCallId, "code_explore", "not-json");
    }

    private static ModelMessage CreateRawToolResult(
        string toolCallId,
        string toolName,
        string content)
    {
        return new ModelMessage
        {
            Role = ModelMessageRole.Tool,
            SectionId = "tool-result",
            ToolCallId = toolCallId,
            ToolName = toolName,
            Content =
            [
                new ModelContentPart
                {
                    Kind = ModelContentPartKind.Json,
                    Content = content,
                },
            ],
        };
    }

    private static CodeExploreResult CreateCodeExploreResult(
        string filePath,
        SourceRange range,
        CodeExploreSourceCompleteness completeness,
        string fileSha256)
    {
        return new CodeExploreResult(
            3,
            SemanticConfidenceLevel.FullSemantic,
            [],
            [
                new CodeExploreFileSection(
                    filePath,
                    "Example",
                    "net10.0",
                    [],
                    new CodeExploreSourceRange(
                        range,
                        ["10: public void Execute() { }"],
                        fileSha256,
                        null,
                        completeness,
                        [],
                        null),
                    false,
                    false,
                    "test"),
            ],
            new CodeExploreCoverage(true, true, completeness == CodeExploreSourceCompleteness.Complete, true, []),
            [],
            []);
    }
}
