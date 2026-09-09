namespace Threadsmith.ContextCaching.Tests;

using System.Text.Json;
using System.Text.Json.Nodes;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Models;
using Xunit;

public sealed partial class Milestone19Tests
{
    /// <summary>Aliases and repeated appends keep one source per file without suppressing different files or loading sibling instructions.</summary>
    [Fact]
    public async Task InstructionResolver_DeduplicatesPathsAndPreservesScope()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src"));
            Directory.CreateDirectory(Path.Combine(root, "sibling"));
            await File.WriteAllTextAsync(Path.Combine(root, "AGENTS.md"), "parent");
            await File.WriteAllTextAsync(Path.Combine(root, "src", "AGENTS.md"), "child");
            await File.WriteAllTextAsync(Path.Combine(root, "sibling", "AGENTS.md"), "sibling");
            await File.WriteAllTextAsync(Path.Combine(root, "append.md"), "parent");
            var sanitizer = new PassthroughSanitizer();
            var loader = new PromptAppendLoader(sanitizer, new PromptAppendLimits(MaximumFileBytes: 6, MaximumTotalBytes: 17));
            var appends = await loader.LoadAsync(new PromptAppendLoadRequest(root, ["AGENTS.md", "./AGENTS.md", "src/AGENTS.md", "append.md"], []));
            var resolver = new RepositoryInstructionResolver(sanitizer);

            var nested = await resolver.ResolveAsync(root, "src", appends, [], 0);
            var withoutDuplicates = await resolver.ResolveAsync(root, "src", [appends[3]], [], 0);
            var rootOnly = await resolver.ResolveAsync(root, null, [], [], 0);

            Assert.Equal(4, appends.Count);
            Assert.Equal(new[] { 0, 1, 2, 3 }, appends.Select(item => item.Position));
            Assert.Equal(new[] { "AGENTS.md", "src/AGENTS.md", "append.md" }, nested.Sources.Select(item => item.RelativePath));
            Assert.Equal(new[] { 0, 1, 2 }, nested.Sources.Select(item => item.Position));
            Assert.Equal(RepositoryInstructionSourceKind.Agents, nested.Sources[0].Kind);
            Assert.Equal(nested.Digest, withoutDuplicates.Digest);
            Assert.Equal("AGENTS.md", Assert.Single(rootOnly.Sources).RelativePath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Two incompatible snapshots of one instruction cannot be silently merged.</summary>
    [Fact]
    public async Task InstructionResolver_ChangedBetweenAppendAndAgentsRead_IsRejected()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "AGENTS.md");
            await File.WriteAllTextAsync(path, "before");
            var sanitizer = new PassthroughSanitizer();
            var appends = await new PromptAppendLoader(sanitizer).LoadAsync(new PromptAppendLoadRequest(root, ["AGENTS.md"], []));
            await File.WriteAllTextAsync(path, "after");

            await Assert.ThrowsAsync<IOException>(() => new RepositoryInstructionResolver(sanitizer).ResolveAsync(root, null, appends, [], 0));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Instruction excerpts are omitted only when their exact path and complete shape prove current visibility.</summary>
    [Theory]
    [InlineData("exact", false)]
    [InlineData("partial", false)]
    [InlineData("changed", true)]
    [InlineData("wrong-path", true)]
    [InlineData("out-of-scope", true)]
    [InlineData("wrong-range", true)]
    [InlineData("wrong-total", true)]
    [InlineData("wrong-tool", true)]
    [InlineData("malformed", true)]
    [InlineData("unknown-field", true)]
    [InlineData("bad-metadata", true)]
    public async Task ContextAssembler_InstructionReadDeduplication_RequiresExactProof(string variant, bool included)
    {
        var root = CreateTemporaryDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src"));
            Directory.CreateDirectory(Path.Combine(root, "sibling"));
            await File.WriteAllTextAsync(Path.Combine(root, "AGENTS.md"), "root-marker\n");
            await File.WriteAllTextAsync(Path.Combine(root, "src", "AGENTS.md"), "first\r\nchild-marker\r\nlast\r\n");
            await File.WriteAllTextAsync(Path.Combine(root, "sibling", "AGENTS.md"), "first\nchild-marker\nlast\n");
            var sessionId = SessionId.New();
            var runId = RunId.New();
            var sourcePath = variant == "out-of-scope" ? "sibling/AGENTS.md" : "src/AGENTS.md";
            var evidence = CreateInstructionReadEvidence(sessionId, runId, sourcePath, variant == "partial" ? ["child-marker"] : ["first", "child-marker", "last"], variant == "partial" ? 2 : 1, 3);
            var payload = JsonNode.Parse(evidence.Content)?.AsObject() ?? throw new InvalidOperationException("Missing test payload.");
            switch (variant)
            {
                case "changed": payload["Lines"] = JsonSerializer.SerializeToNode(new[] { "first", "different", "last" }); break;
                case "wrong-path": payload["Path"] = "sibling/AGENTS.md"; break;
                case "wrong-range": payload["StartLine"] = 0; break;
                case "wrong-total": payload["TotalLines"] = 4; break;
                case "unknown-field": payload["Diagnostic"] = "additional evidence"; break;
                case "bad-metadata": payload["IsTruncated"] = "invalid"; break;
            }

            evidence = evidence with
            {
                Content = variant == "malformed" ? "not JSON" : payload.ToJsonString(),
                Provenance = evidence.Provenance with { Source = variant == "wrong-tool" ? "tool:search" : "tool:read_file" },
            };
            var sanitizer = new PassthroughSanitizer();
            await using var events = new NullEventStream();
            var store = new EvidenceStore(events, sanitizer);
            await store.AddAsync(evidence);
            var assembler = new ContextAssembler(store, new TokenEstimator(), new ContextPolicy(), new PromptAppendLoader(sanitizer), sanitizer, events, TestPromptLoader.Instance, instructionResolver: new RepositoryInstructionResolver(sanitizer));

            var assembled = await assembler.AssembleAsync(new ContextAssemblyRequest
            {
                SessionId = sessionId, RunId = runId, RepositoryPath = root, WorkingScope = "src", Phase = RunPhase.ImplementationModelTurn,
                Task = new TaskSpecification("Update the scoped file.", []),
            });

            var projection = Assert.Single(assembled.Inspection.Evidence);
            Assert.Equal(included, projection.Included);
            Assert.Equal(evidence.Content, Assert.Single(store.Snapshot(sessionId)).Content);
            if (!included)
            {
                Assert.Contains("current repository instruction bundle", projection.Rationale, StringComparison.Ordinal);
                Assert.True(assembled.ModelConstraints.ContainsSensitiveData);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Actual assembly includes root once, keeps scoped children, and makes omitted reads visible again after leaving their scope.</summary>
    [Fact]
    public async Task ContextAssembler_DeduplicatesRootAndChildReadsPerCurrentRequest()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src"));
            await File.WriteAllTextAsync(Path.Combine(root, "AGENTS.md"), "root-once-marker\n");
            await File.WriteAllTextAsync(Path.Combine(root, "src", "AGENTS.md"), "child-once-marker\n");
            var sanitizer = new PassthroughSanitizer();
            await using var events = new NullEventStream();
            var store = new EvidenceStore(events, sanitizer);
            var sessionId = SessionId.New();
            var runId = RunId.New();
            var rootRead = CreateInstructionReadEvidence(sessionId, runId, "AGENTS.md", ["root-once-marker"], 1, 1);
            var childRead = CreateInstructionReadEvidence(sessionId, runId, "src/AGENTS.md", ["child-once-marker"], 1, 1);
            await store.AddBatchAsync([rootRead, childRead]);
            var assembler = new ContextAssembler(
                store,
                new TokenEstimator(),
                new ContextPolicy(),
                new PromptAppendLoader(sanitizer),
                sanitizer,
                events,
                TestPromptLoader.Instance,
                options: new ContextAssemblerOptions { PromptAppendFiles = ["AGENTS.md", "./AGENTS.md"] },
                instructionResolver: new RepositoryInstructionResolver(sanitizer));
            var request = new ContextAssemblyRequest
            {
                SessionId = sessionId, RunId = runId, RepositoryPath = root, WorkingScope = "src", Phase = RunPhase.ImplementationModelTurn,
                Task = new TaskSpecification("Update the scoped file.", []),
            };

            var nested = await assembler.AssembleAsync(request);
            var rootOnly = await assembler.AssembleAsync(request with { WorkingScope = null });

            Assert.All(nested.Inspection.Evidence, item => Assert.False(item.Included));
            var messages = Assert.IsAssignableFrom<IReadOnlyList<ModelMessage>>(nested.Messages);
            var text = string.Join('\n', messages.SelectMany(message => message.Content).Select(part => part.Content));
            Assert.Equal(1, text.Split("root-once-marker", StringSplitOptions.None).Length - 1);
            Assert.Equal(1, text.Split("child-once-marker", StringSplitOptions.None).Length - 1);
            Assert.Single(nested.Inspection.PromptAssets, item => item.Source == "AGENTS.md");
            Assert.Single(nested.Inspection.PromptAssets, item => item.Source == "src/AGENTS.md");
            Assert.True(Assert.Single(rootOnly.Inspection.Evidence, item => item.EvidenceId == childRead.EvidenceId).Included);
            Assert.DoesNotContain(rootOnly.Inspection.PromptAssets, item => item.Source == "src/AGENTS.md");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Source reads render actual code lines once while preserving literal escapes, metadata, and provenance.</summary>
    [Fact]
    public static async Task FileReadEvidence_RendersSourceLinesWithoutJsonEscapes()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-source-context-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var sanitizer = new PassthroughSanitizer();
            await using var events = new NullEventStream();
            var store = new EvidenceStore(events, sanitizer);
            var sessionId = SessionId.New();
            var runId = RunId.New();
            var evidence = CreateInstructionReadEvidence(sessionId, runId, "source.cs", ["class Example", "{", "    string value = \"a\\nb\";", "}"], 1, 10);
            await store.AddBatchAsync([evidence]);
            var assembler = new ContextAssembler(store, new TokenEstimator(), new ContextPolicy(), new PromptAppendLoader(sanitizer), sanitizer, events, TestPromptLoader.Instance);
            var context = await assembler.AssembleAsync(new ContextAssemblyRequest
            {
                SessionId = sessionId, RunId = runId, RepositoryPath = root, Phase = RunPhase.ImplementationModelTurn,
                Task = new TaskSpecification("Edit source", []),
            });
            var text = System.Net.WebUtility.HtmlDecode(context.ModelInput);
            Assert.Contains("class Example\n{\n    string value = \"a\\nb\";\n}", text, StringComparison.Ordinal);
            Assert.DoesNotContain("\\u0022a", text, StringComparison.Ordinal);
            Assert.Contains("\"IsTruncated\":true", text, StringComparison.Ordinal);
            Assert.Contains("\"NextStartLine\":5", text, StringComparison.Ordinal);
            Assert.Contains(evidence.Provenance.ToolInvocationId?.Value.ToString("D") ?? string.Empty, text, StringComparison.Ordinal);
            Assert.True(context.ModelConstraints.ContainsSensitiveData);
            Assert.Equal(evidence.Content, Assert.Single(store.Snapshot(sessionId)).Content);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Evidence CreateInstructionReadEvidence(SessionId sessionId, RunId runId, string path, string[] lines, int startLine, int totalLines)
    {
        var endLine = startLine + lines.Length - 1;
        return new Evidence
        {
            EvidenceId = EvidenceId.New(), SessionId = sessionId, RunId = runId, Kind = EvidenceKind.ToolResult, Sensitivity = EvidenceSensitivity.Sensitive,
            Provenance = new EvidenceProvenance { Source = "tool:read_file", SourcePath = path, ToolInvocationId = ToolInvocationId.New() },
            Content = JsonSerializer.Serialize(new
            {
                Path = path, StartLine = startLine, EndLine = endLine, TotalLines = totalLines, Lines = lines,
                IsTruncated = endLine < totalLines, NextStartLine = endLine < totalLines ? (int?)(endLine + 1) : null,
                TruncationReason = endLine < totalLines ? "LineLimit" : null,
            }),
        };
    }
}
