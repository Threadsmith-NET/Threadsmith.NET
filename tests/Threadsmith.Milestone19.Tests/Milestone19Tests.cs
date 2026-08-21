namespace Threadsmith.Milestone19.Tests;

using System.Text.Json;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Models;
using Xunit;

/// <summary>Milestone 19 cache-optimized request generation acceptance coverage.</summary>
public sealed class Milestone19Tests
{
    /// <summary>Canonicalization changes encoding and order without erasing explicit null defaults.</summary>
    [Fact]
    public void CanonicalTools_PreserveSchemaSemanticsAndStableOrder()
    {
        ModelToolDefinition[] first =
        [
            CreateTool("mcp:z", "{\"required\":[\"b\",\"a\"],\"properties\":{\"b\":{\"default\":null,\"type\":\"string\"},\"a\":{\"type\":\"string\"}},\"type\":\"object\"}"),
            CreateTool("core:read", "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}}}"),
        ];
        ModelToolDefinition[] reordered = [first[1], first[0]];

        var canonicalFirst = ModelToolCanonicalizer.Canonicalize(first);
        var canonicalSecond = ModelToolCanonicalizer.Canonicalize(reordered);

        Assert.Equal(["core:read", "mcp:z"], canonicalFirst.Select(tool => tool.Name));
        Assert.Equal(
            ModelToolCanonicalizer.ComputeDigest(canonicalFirst),
            ModelToolCanonicalizer.ComputeDigest(canonicalSecond));
        using var schema = JsonDocument.Parse(canonicalFirst[1].ArgumentsJsonSchema);
        var property = schema.RootElement.GetProperty("properties").GetProperty("b");
        Assert.True(property.TryGetProperty("default", out var defaultValue));
        Assert.Equal(JsonValueKind.Null, defaultValue.ValueKind);
        Assert.Equal(["a", "b"], schema.RootElement.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
    }

    /// <summary>Strict tool-schema projection requires every object property and preserves optionality with null.</summary>
    [Fact]
    public void StrictToolSchemaProjection_RequiresAllPropertiesAndConvertsOptionalFields()
    {
        const string schema = """
            {
              "type": "object",
              "properties": {
                "requiredName": { "type": "string" },
                "optionalCount": { "type": "integer", "minimum": 0 },
                "boundedText": { "type": "string", "minLength": 1, "maxLength": 32, "pattern": "^[a-z]+$" },
                "address": { "$ref": "#/$defs/Address" },
                "choice": {
                  "oneOf": [
                    { "type": "string", "format": "uuid" },
                    {
                      "type": "object",
                      "properties": { "value": { "type": "string", "format": "uuid" } },
                      "required": ["value"],
                      "additionalProperties": false
                    }
                  ]
                }
              },
              "$defs": {
                "Address": {
                  "type": "object",
                  "properties": {
                    "street": { "type": "string" },
                    "unit": { "type": "string" }
                  },
                  "required": ["street"],
                  "additionalProperties": false
                }
              },
              "required": ["requiredName"],
              "additionalProperties": false
            }
            """;

        var strictSchema = ModelToolStrictSchemaProjector.TryCreateStrictFunctionSchema(
            "sample",
            schema);
        Assert.NotNull(strictSchema);

        using var document = JsonDocument.Parse(strictSchema);
        var root = document.RootElement;
        Assert.False(root.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            ["address", "boundedText", "choice", "optionalCount", "requiredName"],
            root.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        var properties = root.GetProperty("properties");
        Assert.Contains(
            properties.GetProperty("optionalCount").GetProperty("type").EnumerateArray(),
            item => item.GetString() == "null");
        var address = properties.GetProperty("address");
        Assert.True(address.TryGetProperty("anyOf", out var addressAnyOf));
        Assert.Contains(addressAnyOf.EnumerateArray(), item => item.TryGetProperty("type", out var type)
            && type.GetString() == "null");
        var addressDefinition = root.GetProperty("$defs").GetProperty("Address");
        Assert.Equal(
            ["street", "unit"],
            addressDefinition.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains(
            addressDefinition.GetProperty("properties").GetProperty("unit").GetProperty("type").EnumerateArray(),
            item => item.GetString() == "null");
        var boundedText = properties.GetProperty("boundedText");
        Assert.False(boundedText.TryGetProperty("minLength", out _));
        Assert.False(boundedText.TryGetProperty("maxLength", out _));
        Assert.False(boundedText.TryGetProperty("pattern", out _));
        Assert.Contains(
            boundedText.GetProperty("type").EnumerateArray(),
            item => item.GetString() == "null");
        var choice = properties.GetProperty("choice");
        Assert.True(choice.TryGetProperty("anyOf", out var anyOf));
        Assert.Contains(anyOf.EnumerateArray(), item => item.GetProperty("type").GetString() == "null");
    }

    /// <summary>Open dynamic-object schemas do not opt into strict function calling.</summary>
    [Fact]
    public void StrictToolSchemaProjection_RejectsAdditionalProperties()
    {
        Assert.Null(ModelToolStrictSchemaProjector.TryCreateStrictFunctionSchema(
            "dynamic",
            "{\"type\":\"object\",\"additionalProperties\":true}"));
    }

    /// <summary>Native tools are charged once and never also charged as textual schemas.</summary>
    [Fact]
    public void WireEstimate_NativeToolModeHasNoTextualDuplication()
    {
        IReadOnlyList<ModelMessage> messages =
        [
            TextMessage(ModelMessageRole.System, "host-policy", "stable"),
            TextMessage(ModelMessageRole.User, "current-user", "question"),
        ];
        var tools = ModelToolCanonicalizer.Canonicalize(
        [
            CreateTool("core:read", "{\"type\":\"object\"}"),
        ]);

        var estimate = ModelWireEstimator.Estimate(
            messages,
            tools,
            ToolTransportMode.Native,
            stablePrefixMessageCount: 1,
            outputReserveTokens: 512);

        Assert.True(estimate.NativeToolTokens > 0);
        Assert.Equal(0, estimate.TextToolTokens);
        Assert.Equal((long)estimate.WireInputTokens + 512, estimate.TotalCapacityTokens);
    }

    /// <summary>Applicable AGENTS.md files resolve parent-to-child and revalidate without watcher delivery.</summary>
    [Fact]
    public async Task InstructionResolver_ResolvesHierarchyAndRevalidatesEveryBoundary()
    {
        var root = CreateTemporaryDirectory();
        var child = Path.Combine(root, "src", "feature");
        Directory.CreateDirectory(child);
        await File.WriteAllTextAsync(
            Path.Combine(root, "AGENTS.md"),
            "parent",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(root, "src", "AGENTS.md"),
            "child-v1",
            TestContext.Current.CancellationToken);
        var resolver = new RepositoryInstructionResolver(new PassthroughSanitizer());

        try
        {
            var first = await resolver.ResolveAsync(
                root,
                child,
                [],
                [],
                trustGeneration: 1,
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(root, "src", "AGENTS.md"),
                "child-v2",
                TestContext.Current.CancellationToken);
            var second = await resolver.ResolveAsync(
                root,
                child,
                [],
                [],
                trustGeneration: 1,
                TestContext.Current.CancellationToken);

            Assert.Equal(["AGENTS.md", "src/AGENTS.md"], first.Sources.Select(source => source.RelativePath));
            Assert.NotEqual(first.Digest, second.Digest);
            Assert.Equal("parent", second.Sources[0].Content);
            Assert.Equal("child-v2", second.Sources[1].Content);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Structured requests keep policy first, current input last, and native schemas out of text.</summary>
    [Fact]
    public async Task ContextAssembler_ProducesStableStructuredNativeRequest()
    {
        var root = CreateTemporaryDirectory();
        await File.WriteAllTextAsync(
            Path.Combine(root, "AGENTS.md"),
            "repository instruction",
            TestContext.Current.CancellationToken);
        var sanitizer = new PassthroughSanitizer();
        await using var events = new NullEventStream();
        var assembler = new ContextAssembler(
            new EmptyEvidenceStore(),
            new TokenEstimator(),
            new ContextPolicy(),
            new PromptAppendLoader(sanitizer),
            sanitizer,
            events,
            instructionResolver: new RepositoryInstructionResolver(sanitizer));

        try
        {
            var result = await assembler.AssembleAsync(
                new ContextAssemblyRequest
                {
                    SessionId = SessionId.New(),
                    RunId = RunId.New(),
                    Phase = RunPhase.EvidenceCollection,
                    Task = new TaskSpecification("current question", []),
                    RepositoryPath = root,
                    ToolSchemas =
                    [
                        new ContextToolSchema(
                            "core:read",
                            "Read a file.",
                            "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}}}"),
                    ],
                },
                TestContext.Current.CancellationToken);

            var messages = Assert.IsAssignableFrom<IReadOnlyList<ModelMessage>>(result.Messages);
            Assert.Equal(ModelMessageRole.System, messages[0].Role);
            Assert.Equal("current-user", messages[^1].SectionId);
            Assert.Contains("current question", messages[^1].Content[0].Content, StringComparison.Ordinal);
            Assert.DoesNotContain(
                messages.Take(messages.Count - 1),
                message => message.Content.Any(part =>
                    part.Content.Contains("current question", StringComparison.Ordinal)));
            Assert.DoesNotContain("<available_tools>", result.ModelInput, StringComparison.Ordinal);
            Assert.True(result.WireEstimate?.NativeToolTokens > 0);
            Assert.Equal("Native", result.Inspection.ToolTransportMode);
            Assert.Contains(result.Inspection.PromptAssets, asset => asset.Source == "AGENTS.md");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Explicit cache plans honor capability bounds and include every stable boundary class.</summary>
    [Fact]
    public void CachePlanner_ExplicitControl_ProducesBoundedStableBreakpoints()
    {
        var layout = new ModelRequestLayout
        {
            CacheFamily = "family",
            StablePrefixDigest = "sha256:prefix",
            StablePrefixMessageCount = 3,
        };
        var capabilities = new ModelCacheCapabilities
        {
            ExplicitCacheControl = true,
            MaximumBreakpoints = 4,
            MinimumCacheablePrefixTokens = 100,
        };

        var plan = ModelCachePlanner.CreatePlan(layout, capabilities, stablePrefixTokens: 100);

        Assert.Equal(
            [
                ModelCacheBreakpointClass.HostPolicy,
                ModelCacheBreakpointClass.RepositoryInstructions,
                ModelCacheBreakpointClass.ToolInventory,
                ModelCacheBreakpointClass.PhasePolicy,
            ],
            plan.Breakpoints.Select(item => item.Class));
        Assert.Equal(2, ModelCachePlanner.CreatePlan(
            layout,
            capabilities with { MaximumBreakpoints = 2 },
            stablePrefixTokens: 100).Breakpoints.Count);
        Assert.Empty(ModelCachePlanner.CreatePlan(
            layout,
            capabilities,
            stablePrefixTokens: 99).Breakpoints);
    }

    /// <summary>Continuation validation reports every authoritative generation transition precisely.</summary>
    [Fact]
    public void ContinuationBinding_RequiresExactAuthoritativeGenerations()
    {
        var frozen = CreateBinding();

        Assert.Equal(
            ModelContinuationReassemblyReason.None,
            ModelContinuationValidator.GetReassemblyReason(frozen, frozen with { }));
        Assert.Equal(
            ModelContinuationReassemblyReason.PhaseChanged,
            ModelContinuationValidator.GetReassemblyReason(
                frozen,
                frozen with { Phase = RunPhase.ChangePlanning }));
        Assert.Equal(
            ModelContinuationReassemblyReason.TrustOrPolicyChanged,
            ModelContinuationValidator.GetReassemblyReason(
                frozen,
                frozen with { TrustPolicyGeneration = "trust-2" }));
        Assert.Equal(
            ModelContinuationReassemblyReason.InstructionBundleChanged,
            ModelContinuationValidator.GetReassemblyReason(
                frozen,
                frozen with { InstructionBundleDigest = "sha256:changed" }));
        Assert.Equal(
            ModelContinuationReassemblyReason.ToolInventoryChanged,
            ModelContinuationValidator.GetReassemblyReason(
                frozen,
                frozen with { ToolInventoryDigest = "sha256:changed" }));
        Assert.Equal(
            ModelContinuationReassemblyReason.CompactionChanged,
            ModelContinuationValidator.GetReassemblyReason(
                frozen,
                frozen with { CompactionGeneration = 2 }));
        Assert.Equal(
            ModelContinuationReassemblyReason.TrustOrPolicyChanged,
            ModelContinuationValidator.GetReassemblyReason(
                frozen,
                frozen with { RequestGeneration = "request-2" }));
        Assert.Equal(
            ModelContinuationReassemblyReason.ModelOrLayoutChanged,
            ModelContinuationValidator.GetReassemblyReason(
                frozen,
                frozen with { LayoutVersion = 2 }));
        Assert.Equal(
            ModelContinuationReassemblyReason.ModelOrLayoutChanged,
            ModelContinuationValidator.GetReassemblyReason(
                frozen,
                frozen with { StatelessRequestDigest = "sha256:changed" }));
    }

    private static ModelContinuationBinding CreateBinding()
    {
        return new ModelContinuationBinding
        {
            ProviderId = "provider",
            ProfileId = new ModelProfileId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            RequestGeneration = "request-1",
            Phase = RunPhase.EvidenceCollection,
            TrustPolicyGeneration = "trust-1",
            InstructionBundleDigest = "sha256:instructions",
            ToolInventoryDigest = "sha256:tools",
            CompactionGeneration = 1,
            StatelessRequestDigest = "sha256:request",
        };
    }

    private static ModelToolDefinition CreateTool(string name, string schema)
    {
        return new ModelToolDefinition
        {
            Name = name,
            Description = name,
            ArgumentsJsonSchema = schema,
        };
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"threadsmith-m19-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static ModelMessage TextMessage(ModelMessageRole role, string section, string content)
    {
        return new ModelMessage
        {
            Role = role,
            SectionId = section,
            Content = [new ModelContentPart { Content = content }],
        };
    }

    private sealed class EmptyEvidenceStore : IEvidenceStore
    {
        public Task AddAsync(Evidence evidence, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<int> ApplyInvalidationsAsync(
            SessionId sessionId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(0);
        }

        public void QueueInvalidation(SessionId sessionId, string key, string reason)
        {
        }

        public IReadOnlyList<Evidence> Snapshot(SessionId sessionId)
        {
            return [];
        }

        public void CopySession(SessionId sourceSessionId, SessionId destinationSessionId)
        {
        }
    }

    private sealed class NullEventStream : IDomainEventStream
    {
        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        public Task PublishAsync(IDomainEvent domainEvent, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public IDomainEventSubscription Subscribe(
            Func<IDomainEvent, CancellationToken, Task> handler,
            int capacity = 256)
        {
            return new NullSubscription();
        }
    }

    private sealed class NullSubscription : IDomainEventSubscription
    {
        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PassthroughSanitizer : IOutputSanitizer
    {
        public string Sanitize(string value)
        {
            return value;
        }
    }
}
