namespace Threadsmith.ModelTooling.Tests;

using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Models;
using Threadsmith.Models.OpenAiCompatible;
using Xunit;

/// <summary>Verifies frozen role authority and actual-request compatibility without model I/O.</summary>
public static class AgentModelSelectorTests
{
    private static readonly ModelProfileId SmallId = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly ModelProfileId LargeId = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    /// <summary>Every configured role takes precedence over the inherited parent preference.</summary>
    [Theory]
    [InlineData(AgentRole.Explorer)]
    [InlineData(AgentRole.Implementer)]
    [InlineData(AgentRole.SecurityReviewer)]
    [InlineData(AgentRole.TestReviewer)]
    [InlineData(AgentRole.PerformanceReviewer)]
    [InlineData(AgentRole.ArchitectureReviewer)]
    public static void FreezePolicy_RoleConfigurationOverridesInheritance(AgentRole role)
    {
        var selector = CreateSelector(role);
        var assignment = CreateAssignment(role);

        var policy = selector.FreezePolicy(assignment);

        var provenance = Assert.IsType<AgentModelProvenance>(policy.ModelSelection);
        Assert.Equal(SmallId, policy.ModelProfileId);
        Assert.Equal(nameof(ReasoningLevel.High), policy.ReasoningLevel);
        Assert.Equal(AgentModelSelectionSource.RoleConfiguration, provenance.Source);
        Assert.Equal("trusted-small", provenance.ConfiguredProviderId);
        Assert.Equal(SmallId, provenance.ConfiguredProfileId);
        Assert.Equal(nameof(ReasoningLevel.High), provenance.ConfiguredReasoningLevel);
        Assert.True(provenance.UsesTrustedCatalog);
        Assert.Null(provenance.FallbackReason);
    }

    /// <summary>An explicit application pin takes precedence over trusted role configuration.</summary>
    [Fact]
    public static void FreezePolicy_ApplicationPinOverridesRoleConfiguration()
    {
        var selector = CreateSelector(AgentRole.Explorer);

        var policy = selector.FreezePolicy(CreateAssignment(), LargeId, ReasoningLevel.Low);

        Assert.Equal(LargeId, policy.ModelProfileId);
        Assert.Equal(nameof(ReasoningLevel.Low), policy.ReasoningLevel);
        Assert.Equal(AgentModelSelectionSource.ApplicationPin, policy.ModelSelection?.Source);
        Assert.False(policy.ModelSelection?.UsesTrustedCatalog);
    }

    /// <summary>Omitted roles retain inheritance unless the host explicitly requests its default.</summary>
    [Fact]
    public static void FreezePolicy_OmittedRolePreservesInheritanceAndCanDisableIt()
    {
        var selector = CreateSelector(AgentRole.SecurityReviewer);
        var assignment = CreateAssignment();

        var inherited = selector.FreezePolicy(assignment);
        var defaulted = selector.FreezePolicy(assignment, inheritModelPreference: false);

        Assert.Equal(LargeId, inherited.ModelProfileId);
        Assert.Equal(nameof(ReasoningLevel.Low), inherited.ReasoningLevel);
        Assert.Equal(AgentModelSelectionSource.Inherited, inherited.ModelSelection?.Source);
        Assert.Equal(SmallId, defaulted.ModelProfileId);
        Assert.Equal(nameof(ReasoningLevel.Medium), defaulted.ReasoningLevel);
        Assert.Equal(AgentModelSelectionSource.Default, defaulted.ModelSelection?.Source);
        Assert.Null(defaulted.ModelSelection?.ConfiguredProfileId);
    }

    /// <summary>Persisted provenance survives configuration changes without rebinding the role.</summary>
    [Fact]
    public static void FreezePolicy_RestoredProvenanceDoesNotReadChangedRolePreference()
    {
        var selector = CreateSelector(AgentRole.Explorer);
        var assignment = CreateAssignment();
        var frozen = selector.FreezePolicy(assignment);
        var serialized = JsonSerializer.Serialize(frozen);
        var restored = JsonSerializer.Deserialize<AgentPolicySnapshot>(serialized)
            ?? throw new InvalidOperationException("Policy round trip failed.");
        var changed = CreateSelector(AgentRole.Explorer, configuredProfileId: LargeId);

        var result = changed.FreezePolicy(assignment with { Policy = restored });
        var selected = changed.Select(assignment with { Policy = restored });

        Assert.Same(restored, result);
        Assert.Equal(frozen.ModelSelection, selected.Provenance);
        Assert.Equal(SmallId, selected.ProfileId);
    }

    /// <summary>Actual request incompatibilities select a trusted fallback and retain configured provenance.</summary>
    [Theory]
    [InlineData("context", "context window")]
    [InlineData("sensitivity", "sensitive data")]
    [InlineData("tools", "tool calls")]
    [InlineData("cost", "token cost")]
    [InlineData("output", "output")]
    public static void SelectForRequest_IncompatibilityUsesTrustedFallbackAndRetainsConfiguredRoute(
        string constraint,
        string reason)
    {
        var selector = CreateSelector(AgentRole.Explorer);
        var assignment = CreateAssignment();
        assignment = assignment with { Policy = selector.FreezePolicy(assignment) };
        var request = CreateRequest(assignment);
        request = constraint switch
        {
            "context" => request with { WireEstimate = new ModelWireEstimate { WireInputTokens = 8_000, OutputReserveTokens = 1_024 } },
            "sensitivity" => request with { ContainsSensitiveData = true },
            "tools" => request with { RequiredCapabilities = new ModelCapabilitySet { ToolCalls = true } },
            "cost" => request with { SelectionConstraints = new ModelSelectionConstraints { MaximumCombinedCostPerMillionTokens = 1 } },
            "output" => request with { MaximumOutputTokens = 2_048 },
            _ => throw new ArgumentOutOfRangeException(nameof(constraint)),
        };

        var selected = selector.SelectForRequest(assignment, request);

        var provenance = Assert.IsType<AgentModelProvenance>(selected.Provenance);
        Assert.Equal(LargeId, selected.ProfileId);
        Assert.Equal(ReasoningLevel.Low, selected.ReasoningLevel);
        Assert.True(selected.UsesTrustedCatalog);
        Assert.Equal("trusted-large", provenance.EffectiveProviderId);
        Assert.Equal(SmallId, provenance.ConfiguredProfileId);
        Assert.Equal(nameof(ReasoningLevel.High), provenance.ConfiguredReasoningLevel);
        Assert.Equal(AgentModelSelectionSource.RoleConfiguration, provenance.Source);
        Assert.Contains(reason, provenance.FallbackReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Restored routes require streaming but structured output only when explicitly requested.</summary>
    [Theory]
    [InlineData(false, true, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, true, false)]
    public static void SelectForRequest_RestoredProfileHonorsExplicitCapabilities(
        bool streaming,
        bool structuredOutput,
        bool requireStructuredOutput,
        bool expectsFallback)
    {
        var original = CreateSelector(AgentRole.Explorer);
        var assignment = CreateAssignment();
        assignment = assignment with { Policy = original.FreezePolicy(assignment) };
        var changed = CreateSelector(AgentRole.Explorer, streaming: streaming, structuredOutput: structuredOutput);

        var request = CreateRequest(assignment) with
        {
            RequiredCapabilities = new ModelCapabilitySet { StructuredOutput = requireStructuredOutput },
        };
        var selection = changed.SelectForRequest(assignment, request);

        Assert.Equal(expectsFallback ? LargeId : SmallId, selection.ProfileId);
        Assert.True(selection.UsesTrustedCatalog);
        Assert.Equal(expectsFallback, selection.Provenance?.FallbackReason is not null);
    }

    /// <summary>Initial selection for every ordinary role needs streaming and authorized tools, not JSON mode.</summary>
    [Theory]
    [InlineData(AgentRole.Explorer)]
    [InlineData(AgentRole.Implementer)]
    [InlineData(AgentRole.SecurityReviewer)]
    [InlineData(AgentRole.TestReviewer)]
    [InlineData(AgentRole.PerformanceReviewer)]
    [InlineData(AgentRole.ArchitectureReviewer)]
    public static void FreezePolicy_OrdinaryRolesDoNotRequireStructuredOutput(AgentRole role)
    {
        var selector = CreateSelector(role, structuredOutput: false, toolCalls: true);
        var assignment = CreateAssignment(role);
        assignment = assignment with { Policy = assignment.Policy with { AllowedToolIds = ["read_file"] } };

        var policy = selector.FreezePolicy(assignment);

        Assert.Equal(SmallId, policy.ModelProfileId);
        Assert.Null(policy.ModelSelection?.FallbackReason);
        if (role == AgentRole.Explorer)
        {
            Assert.True(selector.CanSelectExplorer(assignment.Budget, assignment.Policy.Sensitivity));
        }
    }

    /// <summary>A review-only model supports TestReviewer without JSON mode while Explorer remains unavailable.</summary>
    [Fact]
    public static void CanSelectRole_ReviewOnlyProfileAdmitsTestReviewerButNotExplorer()
    {
        var model = CreateModel(SmallId, "review-only", 8_192, 1_024) with
        {
            IntendedWorkloadClasses = [WorkloadClass.Review],
            Capabilities = new ModelCapabilitySet
            {
                Streaming = true,
                ToolCalls = true,
                StructuredOutput = false,
            },
        };
        var catalog = CreateCatalog(CreateProvider("review-provider", model)).ModelCatalog;
        var selector = new AgentModelSelector(catalog, new DefaultModelSelectionPolicy(catalog));
        var budget = AgentResourceBudget.CreateTelemetryOnly(TimeSpan.FromMinutes(1));

        Assert.True(selector.CanSelectRole(AgentRole.TestReviewer, budget, ConversationSensitivity.None));
        Assert.False(selector.CanSelectRole(AgentRole.Explorer, budget, ConversationSensitivity.None));
        Assert.False(selector.CanSelectExplorer(budget, ConversationSensitivity.None));
    }

    /// <summary>A repository profile with the same identity cannot replace a trusted route.</summary>
    [Fact]
    public static void SelectForRequest_RepositoryOverrideCannotReplaceTrustedBinding()
    {
        var selector = CreateSelector(AgentRole.Explorer);
        var assignment = CreateAssignment();
        assignment = assignment with { Policy = selector.FreezePolicy(assignment) };

        var result = selector.SelectForRequest(assignment, CreateRequest(assignment));

        Assert.Equal("trusted-small", result.Provenance?.EffectiveProviderId);
        Assert.Equal(8_192, result.ContextWindowTokens);
        Assert.True(result.UsesTrustedCatalog);
    }

    /// <summary>Trusted requests resolve provider instructions only from the trusted catalog.</summary>
    [Fact]
    public static void Select_TrustedInstructionResolverIsIndependentOfRepositoryResolver()
    {
        var selector = CreateSelector(
            AgentRole.Explorer,
            trustedInstructions: new DummyInstructionResolver("trusted instructions"));

        var trusted = selector.Select(CreateAssignment());
        var inherited = selector.Select(CreateAssignment(AgentRole.TestReviewer));

        Assert.Equal("trusted instructions", trusted.ProviderInstructions?.Content);
        Assert.Equal("repository instructions", inherited.ProviderInstructions?.Content);
    }

    /// <summary>Missing trusted instruction resolution cannot promote repository instructions.</summary>
    [Fact]
    public static void Select_MissingTrustedInstructionResolverCannotUseRepositoryInstructions()
    {
        var selector = CreateSelector(AgentRole.Explorer);

        Assert.Null(selector.Select(CreateAssignment()).ProviderInstructions);
    }

    /// <summary>The approved private proposal tool requires capability even without public child tools.</summary>
    [Fact]
    public static void Select_PrivateMutationToolCanRequireToolsWithoutAllowedToolIds()
    {
        var selector = CreateSelector(AgentRole.Implementer);
        var assignment = CreateAssignment(AgentRole.Implementer);

        var selection = selector.Select(assignment, requireToolCalls: true);
        var policy = selector.FreezePolicy(assignment, requireToolCalls: true);

        Assert.Empty(assignment.Policy.AllowedToolIds);
        Assert.Equal(LargeId, selection.ProfileId);
        Assert.Equal(selection.Provenance, policy.ModelSelection);
    }

    /// <summary>Unsatisfied trusted requirements fail without using repository-only alternatives.</summary>
    [Fact]
    public static void SelectForRequest_IncompatibleTrustedCatalogDoesNotFallBackToRepositoryOnlyProfile()
    {
        var selector = CreateSelector(AgentRole.Explorer);
        var assignment = CreateAssignment();
        assignment = assignment with { Policy = selector.FreezePolicy(assignment) };
        var request = CreateRequest(assignment) with
        {
            WireEstimate = new ModelWireEstimate { WireInputTokens = 20_000, OutputReserveTokens = 1_024 },
        };

        Assert.Throws<InvalidOperationException>(() => selector.SelectForRequest(assignment, request));
    }

    /// <summary>Reassembled requests retain the effective fallback and original configured route.</summary>
    [Fact]
    public static void SelectForRequest_PreservesFallbackAfterReassembly()
    {
        var selector = CreateSelector(AgentRole.Explorer);
        var assignment = CreateAssignment();
        assignment = assignment with { Policy = selector.FreezePolicy(assignment) };
        var selected = selector.SelectForRequest(assignment, CreateRequest(assignment) with { ContainsSensitiveData = true });
        assignment = assignment with
        {
            Policy = assignment.Policy with
            {
                ModelProfileId = selected.ProfileId,
                ReasoningLevel = selected.ReasoningLevel.ToString(),
                ModelSelection = selected.Provenance,
            },
        };

        var rechecked = selector.SelectForRequest(assignment, CreateRequest(assignment));

        Assert.Equal(selected.Provenance, rechecked.Provenance);
        Assert.Equal(LargeId, rechecked.ProfileId);
    }

    /// <summary>Candidate-specific reserves admit smaller-output fallbacks without dropping input.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static void SelectForRequest_ProfileReserveAllowsLowerOutputFallbackWithoutDroppingInput(bool sensitive)
    {
        var selector = CreateSelector(AgentRole.Explorer, smallOutputTokens: 4_096, largeOutputTokens: 1_024);
        var assignment = CreateAssignment();
        assignment = assignment with { Policy = selector.FreezePolicy(assignment) };
        var estimate = new ModelWireEstimate { WireInputTokens = sensitive ? 100 : 14_000, OutputReserveTokens = 4_096 };
        var request = CreateRequest(assignment) with
        {
            ContainsSensitiveData = sensitive,
            MaximumOutputTokens = 4_096,
            WireEstimate = estimate,
        };

        Assert.Throws<InvalidOperationException>(() => selector.SelectForRequest(assignment, request));
        var selected = selector.SelectForRequest(assignment, request, useProfileOutputReserve: true);

        Assert.Equal(LargeId, selected.ProfileId);
        Assert.Equal(1_024, selected.OutputReserveTokens);
        Assert.True(selected.UsesTrustedCatalog);
        Assert.Equal(SmallId, selected.Provenance?.ConfiguredProfileId);
        Assert.NotNull(selected.Provenance?.FallbackReason);

        assignment = assignment with { Policy = assignment.Policy with { ModelSelection = selected.Provenance } };
        var rebuilt = request with
        {
            ResolvedProfileId = selected.ProfileId,
            MaximumOutputTokens = selected.OutputReserveTokens,
            ReasoningLevel = selected.ReasoningLevel,
            WireEstimate = estimate with { OutputReserveTokens = selected.OutputReserveTokens },
        };
        Assert.Equal(selected.Provenance, selector.SelectForRequest(assignment, rebuilt, useProfileOutputReserve: true).Provenance);
    }

    /// <summary>Adaptive output reserves do not weaken independent policy or complete-input requirements.</summary>
    [Theory]
    [InlineData("context")]
    [InlineData("cost")]
    [InlineData("input")]
    public static void SelectForRequest_ProfileReserveRetainsHardPolicyAndCompleteInput(string constraint)
    {
        var selector = CreateSelector(AgentRole.Explorer, smallOutputTokens: 4_096, largeOutputTokens: 1_024);
        var assignment = CreateAssignment();
        assignment = assignment with { Policy = selector.FreezePolicy(assignment) };
        var request = CreateRequest(assignment) with
        {
            ContainsSensitiveData = true,
            MaximumOutputTokens = 4_096,
            WireEstimate = new ModelWireEstimate { WireInputTokens = constraint == "input" ? 16_000 : 100, OutputReserveTokens = 4_096 },
            SelectionConstraints = constraint switch
            {
                "context" => new ModelSelectionConstraints { MinimumContextWindow = 32_000 },
                "cost" => new ModelSelectionConstraints { MaximumCombinedCostPerMillionTokens = 0 },
                _ => new ModelSelectionConstraints(),
            },
        };

        Assert.Throws<InvalidOperationException>(() => selector.SelectForRequest(assignment, request, useProfileOutputReserve: true));
    }

    /// <summary>Adaptive admission substitutes trusted candidate instructions while retaining full messages and tools.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public static void SelectForRequest_AdaptiveCapacityUsesCandidateTrustedInstructions(bool shorterFallback)
    {
        var longInstructions = new string('p', 70_000);
        var trusted = new TrackingInstructionResolver(
            shorterFallback ? longInstructions : "role instructions",
            shorterFallback ? "fallback instructions" : longInstructions);
        var ordinary = new TrackingInstructionResolver(
            "repository role instructions",
            shorterFallback ? longInstructions : "repository fallback instructions");
        var selector = CreateSelector(AgentRole.Explorer, trustedInstructions: trusted, ordinaryInstructions: ordinary);
        var assignment = CreateAssignment();
        assignment = assignment with { Policy = selector.FreezePolicy(assignment) };
        var initial = selector.Select(assignment);
        ModelMessage[] messages =
        [
            new()
            {
                Role = ModelMessageRole.User,
                SectionId = "complete-evidence",
                Content = [new ModelContentPart { Content = new string('e', 8_000) }],
            },
        ];
        ModelToolDefinition[] tools =
        [
            new() { Name = "inspect", Description = "Inspect source.", ArgumentsJsonSchema = "{\"type\":\"object\"}" },
        ];
        var toolEstimate = ModelWireEstimator.EstimateTools(tools, ToolTransportMode.Native);
        var request = CreateRequest(assignment) with
        {
            ContainsSensitiveData = true,
            Messages = messages,
            Tools = tools,
            ProviderInstructions = initial.ProviderInstructions,
            MaximumOutputTokens = initial.OutputReserveTokens,
            WireEstimate = ModelWireEstimator.Estimate(messages, toolEstimate, 0, initial.OutputReserveTokens, initial.ProviderInstructions),
        };

        if (shorterFallback)
        {
            var selected = selector.SelectForRequest(assignment, request, useProfileOutputReserve: true);
            Assert.Equal(LargeId, selected.ProfileId);
            Assert.Equal("fallback instructions", selected.ProviderInstructions?.Content);
            Assert.True(selected.UsesTrustedCatalog);
            var rebuiltEstimate = ModelWireEstimator.Estimate(messages, toolEstimate, 0, selected.OutputReserveTokens, selected.ProviderInstructions);
            var rebuilt = request with
            {
                ProviderInstructions = selected.ProviderInstructions,
                MaximumOutputTokens = selected.OutputReserveTokens,
                WireEstimate = rebuiltEstimate,
            };
            Assert.Same(messages, rebuilt.Messages);
            Assert.Same(tools, rebuilt.Tools);
            Assert.True(rebuiltEstimate.TotalCapacityTokens <= selected.ContextWindowTokens);
            var retained = selector.SelectForRequest(
                assignment with { Policy = assignment.Policy with { ModelSelection = selected.Provenance } },
                rebuilt,
                useProfileOutputReserve: true);
            Assert.Equal(selected.Provenance, retained.Provenance);
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => selector.SelectForRequest(assignment, request, useProfileOutputReserve: true));
        }

        Assert.Contains(LargeId, trusted.ResolvedProfiles);
        Assert.Empty(ordinary.ResolvedProfiles);
    }

    /// <summary>Adaptive instruction replacement rejects estimates that cannot account for their current provider contribution.</summary>
    [Fact]
    public static void SelectForRequest_AdaptiveCapacityRejectsInconsistentProviderEstimate()
    {
        var selector = CreateSelector(AgentRole.Explorer);
        var assignment = CreateAssignment();
        var request = CreateRequest(assignment) with
        {
            ProviderInstructions = new ModelProviderInstructions { SectionId = "provider", Content = "unaccounted provider instructions" },
        };

        Assert.Throws<InvalidOperationException>(() => selector.SelectForRequest(assignment, request, useProfileOutputReserve: true));
    }

    /// <summary>Expired assignments and mismatched child identities are rejected before provider dispatch.</summary>
    [Fact]
    public static void SelectForRequest_ExpiredDeadlineOrWrongRunFailsBeforeDispatch()
    {
        var selector = CreateSelector(AgentRole.Explorer);
        var assignment = CreateAssignment();

        Assert.Throws<InvalidOperationException>(() => selector.SelectForRequest(
            assignment with { Deadline = DateTimeOffset.UtcNow.AddSeconds(-1) }, CreateRequest(assignment)));
        Assert.Throws<InvalidOperationException>(() => selector.SelectForRequest(
            assignment, CreateRequest(assignment) with { RunId = RunId.New() }));
    }

    /// <summary>Missing and invalid wire estimates cannot bypass request capacity validation.</summary>
    [Fact]
    public static void SelectForRequest_MissingOrInvalidCapacityFailsClosed()
    {
        var selector = CreateSelector(AgentRole.Explorer);
        var assignment = CreateAssignment();
        var request = CreateRequest(assignment);

        Assert.Throws<InvalidOperationException>(() => selector.SelectForRequest(assignment, request with { WireEstimate = null }));
        Assert.Throws<InvalidOperationException>(() => selector.SelectForRequest(assignment, request with
        {
            WireEstimate = new ModelWireEstimate { WireInputTokens = -1, OutputReserveTokens = 100 },
        }));
        Assert.Throws<InvalidOperationException>(() => selector.SelectForRequest(assignment, request with
        {
            WireEstimate = new ModelWireEstimate { WireInputTokens = int.MaxValue, OutputReserveTokens = 100 },
        }));
    }

    private static AgentModelSelector CreateSelector(
        AgentRole configuredRole,
        ModelProfileId? configuredProfileId = null,
        bool streaming = true,
        bool structuredOutput = true,
        IModelProviderInstructionResolver? trustedInstructions = null,
        int smallOutputTokens = 1_024,
        int largeOutputTokens = 4_096,
        IModelProviderInstructionResolver? ordinaryInstructions = null,
        bool toolCalls = false)
    {
        var small = CreateModel(SmallId, "small", 8_192, smallOutputTokens) with
        {
            Capabilities = new ModelCapabilitySet { Streaming = streaming, StructuredOutput = structuredOutput, ToolCalls = toolCalls },
            DefaultReasoningLevel = ReasoningLevel.Medium,
            Cost = new ModelCostMetadata { InputPerMillionTokens = 2 },
        };
        var large = CreateModel(LargeId, "large", 16_384, largeOutputTokens) with
        {
            SensitiveDataPolicy = ModelSensitiveDataPolicy.Allowed,
            DefaultReasoningLevel = ReasoningLevel.Low,
            Cost = new ModelCostMetadata { InputPerMillionTokens = 1 },
        };
        var trusted = CreateCatalog(CreateProvider("trusted-small", small), CreateProvider("trusted-large", large));
        var ordinary = CreateCatalog(
            CreateProvider("repository-small", small with { ContextWindow = 32_768, Cost = new ModelCostMetadata() }),
            CreateProvider("repository-large", large));
        var configured = configuredProfileId ?? SmallId;
        var policy = new AgentRoleModelPolicy(
            trusted,
            [new AgentRoleModelPreference(configuredRole, configured == SmallId ? "trusted-small" : "trusted-large", configured, ReasoningLevel.High)],
            ordinary);
        return new AgentModelSelector(
            ordinary.ModelCatalog,
            new DefaultModelSelectionPolicy(ordinary.ModelCatalog),
            ordinaryInstructions ?? new DummyInstructionResolver("repository instructions"),
            policy,
            trustedInstructions);
    }

    private static OpenAiCompatibleModelConfiguration CreateModel(ModelProfileId id, string name, int context, int output)
    {
        return new OpenAiCompatibleModelConfiguration
        {
            Id = id,
            Name = name,
            ModelId = name,
            ContextWindow = context,
            MaximumOutputTokens = output,
            Capabilities = new ModelCapabilitySet { Streaming = true, StructuredOutput = true, ToolCalls = true },
            SupportedReasoningLevels = [ReasoningLevel.None, ReasoningLevel.Low, ReasoningLevel.Medium, ReasoningLevel.High],
        };
    }

    private static OpenAiCompatibleProviderConfiguration CreateProvider(string id, OpenAiCompatibleModelConfiguration model)
    {
        return new OpenAiCompatibleProviderConfiguration
        {
            Id = id,
            Name = id,
            BaseUri = new Uri("https://" + id + ".example/v1"),
            Models = [model],
        };
    }

    private static EffectiveModelProviderCatalog CreateCatalog(params OpenAiCompatibleProviderConfiguration[] providers)
    {
        return new EffectiveModelProviderCatalog(
            new ModelProviderCatalogConfiguration { Providers = providers },
            new ModelProviderRegistry([new OpenAiCompatibleProviderRegistration()]));
    }

    private static AgentAssignment CreateAssignment(AgentRole role = AgentRole.Explorer)
    {
        return new AgentAssignment
        {
            AssignmentId = AgentAssignmentId.New(),
            ChildRunId = RunId.New(),
            Role = role,
            Mode = AgentRunMode.ReadOnlyBaseline,
            Objective = "Inspect the selected scope.",
            OutputSchema = AgentAssignment.ResponseSchema,
            StoppingCondition = "Return your response.",
            Deadline = DateTimeOffset.UtcNow.AddMinutes(5),
            Scope = new AgentAssignmentScope(),
            Policy = new AgentPolicySnapshot
            {
                ModelProfileId = LargeId,
                ReasoningLevel = nameof(ReasoningLevel.Low),
                ModelSelectionRationale = "Inherited test preference.",
                ContextPolicyVersion = "agent-context/2",
                ToolPolicyVersion = "agent-tools/1",
            },
            Budget = AgentResourceBudget.CreateTelemetryOnly(TimeSpan.FromMinutes(5)),
        };
    }

    private static ModelStreamRequest CreateRequest(AgentAssignment assignment)
    {
        return new ModelStreamRequest
        {
            RunId = assignment.ChildRunId,
            Input = "Inspect the selected scope.",
            WireEstimate = new ModelWireEstimate { WireInputTokens = 100, OutputReserveTokens = 1_024 },
        };
    }

    private sealed class TrackingInstructionResolver : IModelProviderInstructionResolver
    {
        private readonly string _small;
        private readonly string _large;

        public TrackingInstructionResolver(string small, string large)
        {
            _small = small;
            _large = large;
        }

        public List<ModelProfileId> ResolvedProfiles { get; } = [];

        public ModelProviderInstructions Resolve(ModelProfileId profileId)
        {
            ResolvedProfiles.Add(profileId);
            return new ModelProviderInstructions
            {
                SectionId = "provider-instructions",
                Content = profileId == SmallId ? _small : _large,
            };
        }
    }

    private sealed class DummyInstructionResolver : IModelProviderInstructionResolver
    {
        private readonly string _content;

        public DummyInstructionResolver(string content)
        {
            _content = content;
        }

        public ModelProviderInstructions Resolve(ModelProfileId profileId)
        {
            return new ModelProviderInstructions { SectionId = "provider-instructions", Content = _content };
        }
    }
}
