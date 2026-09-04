namespace Threadsmith.Mutations.Tests;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.DotNet;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Tui;
using Threadsmith.Workspaces;
using Xunit;

/// <summary>Verifies plans 10 and 11 transactional and semantic mutation contracts.</summary>
public static class Milestone5Tests
{
    /// <summary>Preview remains private until approval, can be configured per change, commits, and rolls back.</summary>
    [Fact]
    public static async Task TransactionalWorkspace_PreviewCommitRollback_PreservesBaselineAndEvents()
    {
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string>
        {
            ["src/Example.cs"] = "class Example { string Value = \"old\"; }\n",
        });
        await using var events = new DomainEventStream();
        var projections = new InMemoryProjectionStore();
        await using var subscription = events.Subscribe(projections.ApplyAsync);
        await events.PublishAsync(new SessionCreated(
            repository.SessionId,
            DateTimeOffset.UtcNow,
            "M5"));
        await using var workspace = await TransactionalWorkspace.CreateAsync(repository.Baseline, events);
        var original = await File.ReadAllTextAsync(repository.PathOf("src/Example.cs"));
        var offset = original.IndexOf("old", StringComparison.Ordinal);
        var mutation = CreateReplacement(repository, "src/Example.cs", offset, "old", "new");
        var set = CreateMutationSet(repository, [mutation]);

        var staged = await workspace.StageAsync(set);

        Assert.False(staged.Conflicts.HasConflicts);
        Assert.Contains("-class Example { string Value = \"old\"; }", staged.Preview.UnifiedDiff);
        Assert.Contains("+class Example { string Value = \"new\"; }", staged.Preview.UnifiedDiff);
        Assert.Equal(original, await workspace.ReadBaselineTextAsync("src/Example.cs"));
        var stagedText = await workspace.ReadStagedTextAsync(
            set.MutationSetId,
            "src/Example.cs");
        Assert.Contains("new", stagedText ?? string.Empty);
        Assert.Equal(original, await File.ReadAllTextAsync(repository.PathOf("src/Example.cs")));

        var hidden = await workspace.SetPreviewEnabledAsync(
            set.MutationSetId,
            mutation.MutationId,
            isEnabled: false);
        Assert.False(Assert.Single(hidden.Changes).PreviewEnabled);
        Assert.Same(staged.Preview.UnifiedDiff, hidden.UnifiedDiff);
        var commit = await workspace.CommitAsync(
            set.MutationSetId,
            new MutationApproval
            {
                Level = MutationApprovalLevel.EntireSet,
                ApprovalId = staged.ApprovalId,
            });
        Assert.Contains(mutation.MutationId, commit.AppliedMutations);
        Assert.Contains("new", await File.ReadAllTextAsync(repository.PathOf("src/Example.cs")));

        var projection = await projections.GetAsync<SessionProjection>(new ProjectionKey(
            "session",
            repository.SessionId.Value.ToString("D")));
        Assert.NotNull(projection?.Mutation);
        Assert.True(projection.Mutation.IsApplied);
        Assert.False(Assert.Single(projection.Mutation.Preview.Changes).PreviewEnabled);

        var rollback = await workspace.RollbackAsync(set.MutationSetId);
        Assert.False(rollback.Conflicts.HasConflicts);
        Assert.Equal(original, await File.ReadAllTextAsync(repository.PathOf("src/Example.cs")));
        projection = await projections.GetAsync<SessionProjection>(new ProjectionKey(
            "session",
            repository.SessionId.Value.ToString("D")));
        Assert.True(projection?.Mutation?.IsRolledBack);
    }

    /// <summary>Rollback attributes restored and removed endpoints to the host mutation.</summary>
    [Fact]
    public static async Task TransactionalWorkspace_Rollback_RegistersExactSemanticHostWrites()
    {
        const string original = "class Example { string Value = \"old\"; }\n";
        const string replacement = "class Example { string Value = \"new\"; }\n";
        const string created = "class Added { }\n";
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string>
        {
            ["src/Example.cs"] = original,
        });
        await using var events = new DomainEventStream();
        var attribution = new RecordingSemanticHostMutationAttribution();
        await using var workspace = await TransactionalWorkspace.CreateAsync(
            repository.Baseline,
            events,
            semanticMutationAttribution: attribution);
        var replacementMutation = CreateReplacement(
            repository,
            "src/Example.cs",
            0,
            original,
            replacement);
        var createMutation = new Mutation
        {
            MutationId = MutationId.New(),
            Type = MutationType.CreateFile,
            RelativePath = "src/Added.cs",
            ReplacementText = created,
        };
        var set = CreateMutationSet(repository, [replacementMutation, createMutation]);
        var staged = await workspace.StageAsync(set);
        _ = await workspace.CommitAsync(
            set.MutationSetId,
            new MutationApproval
            {
                Level = MutationApprovalLevel.EntireSet,
                ApprovalId = staged.ApprovalId,
            });

        _ = await workspace.RollbackAsync(set.MutationSetId);

        Assert.Equal(2, attribution.Registrations.Count);
        var rollbackRegistration = attribution.Registrations[1];
        Assert.Equal(repository.SessionId, rollbackRegistration.SessionId);
        Assert.Equal(repository.WorkspaceId, rollbackRegistration.WorkspaceId);
        Assert.Equal(set.MutationSetId, rollbackRegistration.MutationSetId);
        var restoredWrite = Assert.Single(
            rollbackRegistration.Writes,
            write => write.RelativePath == "src/Example.cs");
        Assert.Equal(CreateSemanticContentIdentity(original), restoredWrite.ContentIdentity);
        Assert.False(restoredWrite.AllowMissingTransition);
        Assert.Equal(
            CreateSemanticContentIdentity(replacement),
            restoredWrite.CompensationContentIdentity);
        Assert.True(restoredWrite.ExistedBefore);
        var removedWrite = Assert.Single(
            rollbackRegistration.Writes,
            write => write.RelativePath == "src/Added.cs");
        Assert.Equal("missing", removedWrite.ContentIdentity);
        Assert.False(removedWrite.AllowMissingTransition);
        Assert.Equal(CreateSemanticContentIdentity(created), removedWrite.CompensationContentIdentity);
        Assert.True(removedWrite.ExistedBefore);
        Assert.Equal(2, attribution.Completions.Count);
        var rollbackCompletion = attribution.Completions[1];
        Assert.Equal(rollbackRegistration.Registration, rollbackCompletion.Registration);
        Assert.Equal(2, rollbackCompletion.RelativePaths.Count);
        Assert.Contains("src/Example.cs", rollbackCompletion.RelativePaths);
        Assert.Contains("src/Added.cs", rollbackCompletion.RelativePaths);
        Assert.Equal(original, await File.ReadAllTextAsync(repository.PathOf("src/Example.cs")));
        Assert.False(File.Exists(repository.PathOf("src/Added.cs")));
    }

    /// <summary>External edits block commit and rollback rather than overwriting newer user content.</summary>
    [Fact]
    public static async Task TransactionalWorkspace_ExternalChanges_BlockCommitAndRollback()
    {
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string>
        {
            ["src/Example.cs"] = "old",
        });
        await using var events = new DomainEventStream();
        await using var workspace = await TransactionalWorkspace.CreateAsync(repository.Baseline, events);
        var first = CreateMutationSet(
            repository,
            [CreateReplacement(repository, "src/Example.cs", 0, "old", "first")]);
        var staged = await workspace.StageAsync(first);
        await File.WriteAllTextAsync(repository.PathOf("src/Example.cs"), "external");

        var conflict = await Assert.ThrowsAsync<WorkspaceConflictException>(() => workspace.CommitAsync(
            first.MutationSetId,
            new MutationApproval
            {
                Level = MutationApprovalLevel.EntireSet,
                ApprovalId = staged.ApprovalId,
            }));
        Assert.True(conflict.Report.HasConflicts);
        Assert.Equal("external", await File.ReadAllTextAsync(repository.PathOf("src/Example.cs")));

        await using var secondRepository = await TestRepository.CreateAsync(new Dictionary<string, string>
        {
            ["src/Example.cs"] = "old",
        });
        await using var secondWorkspace = await TransactionalWorkspace.CreateAsync(secondRepository.Baseline, events);
        var second = CreateMutationSet(
            secondRepository,
            [CreateReplacement(secondRepository, "src/Example.cs", 0, "old", "applied")]);
        var secondStaged = await secondWorkspace.StageAsync(second);
        _ = await secondWorkspace.CommitAsync(
            second.MutationSetId,
            new MutationApproval
            {
                Level = MutationApprovalLevel.EntireSet,
                ApprovalId = secondStaged.ApprovalId,
            });
        await File.WriteAllTextAsync(secondRepository.PathOf("src/Example.cs"), "newer-user-change");

        var rollback = await secondWorkspace.RollbackAsync(second.MutationSetId);

        Assert.True(rollback.Conflicts.HasConflicts);
        Assert.Equal(
            "newer-user-change",
            await File.ReadAllTextAsync(secondRepository.PathOf("src/Example.cs")));
    }

    /// <summary>Root confinement and explicit selected-mutation approval prevent unapproved writes.</summary>
    [Fact]
    public static async Task TransactionalWorkspace_ApprovedRootsAndSelection_AreEnforced()
    {
        await using var repository = await TestRepository.CreateAsync(
            new Dictionary<string, string>
            {
                ["src/One.cs"] = "one",
                ["src/Two.cs"] = "two",
            },
            approvedRoots: ["src"]);
        await using var events = new DomainEventStream();
        await using var workspace = await TransactionalWorkspace.CreateAsync(repository.Baseline, events);
        var one = CreateReplacement(repository, "src/One.cs", 0, "one", "ONE");
        var two = CreateReplacement(repository, "src/Two.cs", 0, "two", "TWO");
        var set = CreateMutationSet(repository, [one, two]);
        var staged = await workspace.StageAsync(set);

        var result = await workspace.CommitAsync(
            set.MutationSetId,
            new MutationApproval
            {
                Level = MutationApprovalLevel.SelectedMutations,
                ApprovalId = staged.ApprovalId,
                SelectedMutations = [two.MutationId],
            });

        Assert.Equal([two.MutationId], result.AppliedMutations);
        Assert.Equal("one", await File.ReadAllTextAsync(repository.PathOf("src/One.cs")));
        Assert.Equal("TWO", await File.ReadAllTextAsync(repository.PathOf("src/Two.cs")));

        var escapedMutation = new Mutation
        {
            MutationId = MutationId.New(),
            Type = MutationType.CreateFile,
            RelativePath = "outside/Denied.cs",
            ReplacementText = "denied",
        };
        var escapedSet = CreateMutationSet(repository, [escapedMutation]);
        var escaped = await workspace.StageAsync(escapedSet);
        Assert.True(escaped.Conflicts.HasConflicts);
        Assert.False(File.Exists(repository.PathOf("outside/Denied.cs")));
    }

    /// <summary>Structured mutation output is bounded before any workspace operation executes.</summary>
    [Fact]
    public static async Task MutationModelOutput_IsValidatedAndRenderedThroughProjection()
    {
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string>
        {
            ["src/Example.cs"] = "old",
        });
        var mutation = CreateReplacement(repository, "src/Example.cs", 0, "old", "new");
        var set = CreateMutationSet(repository, [mutation]);

        ModelOutputValidator.Validate(new MutationSetModelOutput(set));
        Assert.Throws<MalformedModelOutputException>(() => ModelOutputValidator.Validate(
            new MutationSetModelOutput(set with
            {
                Mutations = [mutation with { RelativePath = "../escape.cs" }],
            })));
        Assert.Throws<MalformedModelOutputException>(() => ModelOutputValidator.Validate(
            new MutationSetModelOutput(set with
            {
                Mutations = [mutation with { Type = MutationType.ReplaceSyntaxNode }],
            })));

        await using var events = new DomainEventStream();
        var projections = new InMemoryProjectionStore();
        await using var subscription = events.Subscribe(projections.ApplyAsync);
        await events.PublishAsync(new SessionCreated(
            repository.SessionId,
            DateTimeOffset.UtcNow,
            "preview"));
        await using var workspace = await TransactionalWorkspace.CreateAsync(repository.Baseline, events);
        _ = await workspace.StageAsync(set);
        var presenter = new TuiPresenter(
            new CommandDispatcher(Array.Empty<object>()),
            projections);

        var rendered = await presenter.RenderAsync(repository.SessionId);

        Assert.Contains("Mutation set", rendered.Workspace);
        Assert.Contains("--- a/src/Example.cs", rendered.Workspace);
        Assert.Contains("+++ b/src/Example.cs", rendered.Workspace);
        var renderedLines = rendered.Workspace
            .Replace(Environment.NewLine, "\n", StringComparison.Ordinal)
            .Split('\n');
        var hunkIndex = Array.FindIndex(
            renderedLines,
            line => line.StartsWith("@@", StringComparison.Ordinal));
        Assert.True(hunkIndex >= 0);
        Assert.True(hunkIndex + 1 < renderedLines.Length);
        Assert.Equal(string.Empty, renderedLines[hunkIndex + 1]);
    }

    /// <summary>Mutation context is governed by the accepted plan and selected baseline hashes.</summary>
    [Fact]
    public static async Task MutationContext_UsesCodeEditSchemaAndExplicitBaselineState()
    {
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string>
        {
            ["src/Example.cs"] = "old",
            ["src/Unplanned.cs"] = "not selected",
        });
        await using var events = new DomainEventStream();
        var sanitizer = new PassthroughSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var assembler = new ContextAssembler(
            evidence,
            new TokenEstimator(),
            new ContextPolicy(),
            new PromptAppendLoader(sanitizer),
            sanitizer,
            events,
            TestPromptLoader.Instance,
            new ContextAssemblerOptions { MaximumTokens = 32_000 });
        var plan = new ImplementationPlan
        {
            Summary = "Change Example.",
            Steps =
            [
                new ImplementationPlanStep
                {
                    StepId = StepId.New(),
                    Title = "Edit Example",
                    Description = "Replace the value.",
                    FileIntents = ModifyIntents("src/Example.cs"),
                    ExpectedOutcome = "Example is updated.",
                },
            ],
        };

        var assembled = await assembler.AssembleAsync(new ContextAssemblyRequest
        {
            SessionId = repository.SessionId,
            RunId = RunId.New(),
            Phase = RunPhase.MutationPreparation,
            Task = new TaskSpecification("Change Example", []),
            RepositoryPath = repository.Root,
            ApprovedPlan = plan,
            MutationBaseline = repository.Baseline,
        });

        Assert.Equal(WorkloadClass.CodeEdit, assembled.WorkloadClass);
        Assert.Contains("host assigns session, run, workspace, baseline", assembled.ModelInput);
        Assert.Contains("mutationSet.rationale", assembled.ModelInput);
        Assert.Contains("type and relativePath", assembled.ModelInput);
        Assert.Contains("baselineSha256", assembled.ModelInput);
        Assert.Contains("Do not use plan file-intent or legacy synonyms kind, path, baselineHash", assembled.ModelInput);
        Assert.Contains("src/Example.cs", assembled.ModelInput);
        Assert.DoesNotContain("src/Unplanned.cs", assembled.ModelInput);
        Assert.Contains(
            repository.Baseline.CapturedAt.ToString("yyyy-MM-ddTHH:mm:ss"),
            assembled.ModelInput);
    }

    /// <summary>A governed model can propose a bounded set that is staged but never self-applied.</summary>
    [Fact]
    public static async Task ModelMutationProposal_IsValidatedBoundToHostStateAndStagedForApproval()
    {
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string>
        {
            ["src/Example.cs"] = "old",
            ["src/AGENTS.md"] = "nested mutation instructions",
        });
        var runId = RunId.New();
        var mutation = CreateReplacement(repository, "src/Example.cs", 0, "old", "new");
        var proposal = CreateMutationSet(repository, [mutation]) with
        {
            RunId = runId,
            RequiredApproval = MutationApprovalLevel.PolicyAutoApproved,
        };
        var model = new ChunkModelProvider(new ModelChunk
        {
            Output = new MutationSetModelOutput(proposal),
            Usage = new ModelUsage(100, 50),
        });
        await using var events = new DomainEventStream();
        await using var workspaces = new TransactionalWorkspaceCoordinator(events);
        await workspaces.RegisterBaselineAsync(repository.Baseline);
        var sanitizer = new PassthroughSanitizer();
        var innerContext = new ContextAssembler(
            new EvidenceStore(events, sanitizer),
            new TokenEstimator(),
            new ContextPolicy(),
            new PromptAppendLoader(sanitizer),
            sanitizer,
            events,
            TestPromptLoader.Instance,
            new ContextAssemblerOptions(),
            instructionResolver: new RepositoryInstructionResolver(sanitizer));
        var context = new RecordingContextAssembler(innerContext);
        var usage = new SessionUsageProjection();
        var application = new MutationProposalApplication(
            model,
            context,
            workspaces,
            new ExecutionBudget(new BudgetDimensions(
                10_000,
                10,
                TimeSpan.FromMinutes(1),
                1)),
            sanitizer,
            events,
            sessionUsage: usage,
            correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
            prompts: TestPromptLoader.Instance);
        var plan = new ImplementationPlan
        {
            Summary = "Change Example.",
            Steps =
            [
                new ImplementationPlanStep
                {
                    StepId = StepId.New(),
                    Title = "Edit Example",
                    Description = "Replace the value.",
                    FileIntents = ModifyIntents("src/Example.cs"),
                    ExpectedOutcome = "Example is updated.",
                },
            ],
        };

        var staged = await application.HandleAsync(new ProposeMutationSetCommand(
            repository.SessionId,
            runId,
            repository.WorkspaceId,
            new TaskSpecification("Change Example", []),
            plan));

        Assert.False(staged.Conflicts.HasConflicts);
        Assert.Equal(MutationApprovalLevel.EntireSet, staged.MutationSet.RequiredApproval);
        Assert.True(staged.MutationSet.IsWithinApprovedPlan);
        Assert.Contains("+new", staged.Preview.UnifiedDiff);
        Assert.Equal("old", await File.ReadAllTextAsync(repository.PathOf("src/Example.cs")));
        Assert.Equal(
            new SessionUsageSnapshot(100, 50, false),
            usage.GetSnapshot(repository.SessionId));
        var contextRequest = Assert.Single(context.Requests);
        Assert.Equal(repository.PathOf("src"), contextRequest.WorkingScope);
        var modelRequest = Assert.Single(model.Requests);
        Assert.Equal(false, modelRequest.AllowMultipleToolCalls);
        Assert.True(Assert.Single(modelRequest.Tools).PreferStrictArguments);
        var contextTool = Assert.Single(contextRequest.ToolSchemas);
        Assert.True(contextTool.PreferStrictArguments);
        var modelTool = Assert.Single(modelRequest.Tools);
        Assert.Equal(contextTool.Id, modelTool.Name);
        Assert.Equal(contextTool.Description, modelTool.Description);
        Assert.Contains("host already owns the approved plan revision and step identities", modelTool.Description, StringComparison.Ordinal);
        Assert.Contains("operation-specific shape", modelTool.Description, StringComparison.Ordinal);
        Assert.Contains("relativePath and, when advertised, baselineSha256", modelTool.Description, StringComparison.Ordinal);
        Assert.Contains("never use plan file-intent or legacy names kind, path, baselineHash", modelTool.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("planRevision", modelTool.ArgumentsJsonSchema, StringComparison.Ordinal);
        Assert.DoesNotContain("planStepIds", modelTool.ArgumentsJsonSchema, StringComparison.Ordinal);
        var strictMutationSchema = ModelToolStrictSchemaProjector.TryCreateStrictFunctionSchema(
            modelTool.Name,
            modelTool.ArgumentsJsonSchema);
        Assert.NotNull(strictMutationSchema);
        using (var strictDocument = JsonDocument.Parse(strictMutationSchema))
        {
            var strictRoot = strictDocument.RootElement;
            Assert.False(strictRoot.GetProperty("additionalProperties").GetBoolean());
            Assert.Contains(
                strictRoot.GetProperty("required").EnumerateArray(),
                item => item.GetString() == "mutationSet");
            var mutationItem = strictRoot.GetProperty("properties")
                .GetProperty("mutationSet").GetProperty("properties")
                .GetProperty("mutations").GetProperty("items");
            Assert.Equal(5, mutationItem.GetProperty("anyOf").GetArrayLength());
            var replaceText = strictRoot.GetProperty("$defs").GetProperty("replaceText");
            Assert.Contains(
                replaceText.GetProperty("required").EnumerateArray(),
                item => item.GetString() == "type");
            Assert.Contains(
                replaceText.GetProperty("required").EnumerateArray(),
                item => item.GetString() == "relativePath");
            Assert.Contains(
                replaceText.GetProperty("required").EnumerateArray(),
                item => item.GetString() == "expectedText");
            Assert.False(replaceText.GetProperty("properties").TryGetProperty(
                "destinationRelativePath",
                out _));
            Assert.False(strictRoot.GetProperty("$defs").GetProperty("createFile")
                .GetProperty("properties").TryGetProperty("expectedText", out _));
        }

        Assert.Equal(contextTool.JsonSchema, modelTool.ArgumentsJsonSchema);
        Assert.Contains(
            modelRequest.Messages,
            message => message.SectionId == "repository-instructions"
                && message.Content.Any(part => part.Content.Contains(
                    "nested mutation instructions",
                    StringComparison.Ordinal)));

        _ = await workspaces.HandleAsync(new CommitMutationSetCommand(
            repository.SessionId,
            staged.MutationSet.MutationSetId,
            new MutationApproval
            {
                Level = MutationApprovalLevel.EntireSet,
                ApprovalId = staged.ApprovalId,
            }));
        Assert.Equal("new", await File.ReadAllTextAsync(repository.PathOf("src/Example.cs")));
    }

    /// <summary>A native proposal for one exact replacement needs no model-authored host identities.</summary>
    [Fact]
    public static async Task ModelMutationProposal_NativeSimpleReplacement_HostAssignsIdentitiesAndStages()
    {
        const string source = "public override string Name => StandardizerName;";
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string>
        {
            ["src/SectorEntityStandardizer.cs"] = source,
        });
        var runId = RunId.New();
        var stepId = StepId.New();
        var ambiguousStepId = StepId.New();
        var baselineFile = Assert.Single(repository.Baseline.Files);
        var envelope = new MutationProposalEnvelope
        {
            MutationSet = new MutationProposalSet
            {
                Mutations =
                [
                    new ReplaceTextMutationProposal
                    {
                        RelativePath = "src/SectorEntityStandardizer.cs",
                        BaselineSha256 = baselineFile.Sha256,
                        StartOffset = 0,
                        Length = "StandardizerName".Length,
                        ExpectedText = "StandardizerName",
                        ReplacementText = "\"test\"",
                    },
                ],
                Rationale = "Return the requested literal.",
                Risk = MutationRisk.Low,
            },
        };
        var arguments = JsonSerializer.Serialize(
            envelope,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                Converters = { new JsonStringEnumConverter() },
            });
        var model = new ChunkModelProvider(new ModelChunk
        {
            Output = new ToolRequestModelOutput("propose_mutations", arguments),
            Usage = new ModelUsage(100, 50),
        });
        await using var scenario = await MutationScenario.CreateAsync(repository, model);
        var plan = new ImplementationPlan
        {
            Summary = "Change the Name property.",
            Steps =
            [
                new ImplementationPlanStep
                {
                    StepId = stepId,
                    Title = "Edit Name",
                    Description = "Return the requested literal.",
                    FileIntents = ModifyIntents("src/SectorEntityStandardizer.cs"),
                    ExpectedOutcome = "Name returns test.",
                },
                new ImplementationPlanStep
                {
                    StepId = ambiguousStepId,
                    Title = "Document Name behavior",
                    Description = "Document the changed behavior in the same file.",
                    FileIntents = ModifyIntents("src/SectorEntityStandardizer.cs"),
                    ExpectedOutcome = "The behavior is documented.",
                },
            ],
        };

        var staged = await scenario.ProposeAsync(
            runId,
            new TaskSpecification("Change Name", []),
            plan,
            RunPhase.ImplementationModelTurn);

        Assert.Equal(repository.SessionId, staged.MutationSet.SessionId);
        Assert.Equal(runId, staged.MutationSet.RunId);
        Assert.Equal(repository.WorkspaceId, staged.MutationSet.WorkspaceId);
        Assert.NotEqual(default, staged.MutationSet.MutationSetId);
        Assert.Empty(staged.PlanStepIds);
        var stagedMutation = Assert.Single(staged.MutationSet.Mutations);
        Assert.NotEqual(default, stagedMutation.MutationId);
        Assert.Equal(source.IndexOf("StandardizerName", StringComparison.Ordinal), stagedMutation.StartOffset);
        Assert.Contains("+public override string Name => \"test\";", staged.Preview.UnifiedDiff);
        Assert.Equal(source, await File.ReadAllTextAsync(repository.PathOf("src/SectorEntityStandardizer.cs")));
        var schema = Assert.Single(Assert.Single(scenario.ContextRequests).ToolSchemas);
        Assert.Contains("\"mutations\"", schema.JsonSchema, StringComparison.Ordinal);
        Assert.Contains("\"replacementText\"", schema.JsonSchema, StringComparison.Ordinal);
        Assert.Contains("\"RenameSymbol\"", schema.JsonSchema, StringComparison.Ordinal);
        Assert.DoesNotContain("mutationSetId", schema.JsonSchema, StringComparison.Ordinal);
    }

    /// <summary>A native semantic rename proposal is expanded through the semantic mutation engine before staging.</summary>
    [Fact]
    public static async Task ModelMutationProposal_RenameSymbol_UsesSemanticMutationEngine()
    {
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string>
        {
            ["Contracts/IRetriever.cs"] = "namespace Demo;\npublic interface IRetriever { }\n",
            ["Services/RetrieverConsumer.cs"] = "namespace Demo;\npublic sealed class RetrieverConsumer\n{\n    private IRetriever? _retriever;\n}\n",
        });
        var runId = RunId.New();
        var stepId = StepId.New();
        var declaration = Assert.Single(
            repository.Baseline.Files,
            file => file.RelativePath == "Contracts/IRetriever.cs");
        var envelope = new MutationProposalEnvelope
        {
            MutationSet = new MutationProposalSet
            {
                Mutations =
                [
                    new RenameSymbolMutationProposal
                    {
                        RelativePath = "Contracts/IRetriever.cs",
                        RelatedSymbolId = "T:Demo.IRetriever",
                        ReplacementText = "IRetrieval",
                    },
                    new MoveFileMutationProposal
                    {
                        RelativePath = "Contracts/IRetriever.cs",
                        DestinationRelativePath = "Contracts/IRetrieval.cs",
                        ExpectedIdentity = new ExpectedFileIdentity
                        {
                            Sha256 = declaration.Sha256,
                            ByteLength = declaration.Length,
                        },
                    },
                ],
                Rationale = "Rename the interface through Roslyn.",
                Risk = MutationRisk.Medium,
            },
        };
        var arguments = JsonSerializer.Serialize(
            envelope,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                Converters = { new JsonStringEnumConverter() },
            });
        var model = new ChunkModelProvider(new ModelChunk
        {
            Output = new ToolRequestModelOutput("propose_mutations", arguments),
            Usage = new ModelUsage(100, 50),
        });
        var observed = new List<IDomainEvent>();
        await using var events = new DomainEventStream();
        var projections = new InMemoryProjectionStore();
        await using var projectionSubscription = events.Subscribe(projections.ApplyAsync);
        await using var subscription = events.Subscribe((domainEvent, _) =>
        {
            observed.Add(domainEvent);
            return Task.CompletedTask;
        });
        await events.PublishAsync(new SessionCreated(
            repository.SessionId,
            DateTimeOffset.UtcNow,
            "semantic rename"));
        await using var workspaces = new TransactionalWorkspaceCoordinator(events);
        await workspaces.RegisterBaselineAsync(repository.Baseline);
        var sanitizer = new PassthroughSanitizer();
        var context = new ContextAssembler(
            new EvidenceStore(events, sanitizer),
            new TokenEstimator(),
            new ContextPolicy(),
            new PromptAppendLoader(sanitizer),
            sanitizer,
            events,
            TestPromptLoader.Instance,
            new ContextAssemblerOptions());
        var semanticMutations = new FakeSemanticMutationEngine();
        semanticMutations.Warnings.Add("Projects outside the compiled subset were skipped.");
        var preMutationAnalyzer = new FakePreMutationAnalyzer();
        var application = new MutationProposalApplication(
            model,
            context,
            workspaces,
            new ExecutionBudget(new BudgetDimensions(10_000, 10, TimeSpan.FromMinutes(1), 1)),
            sanitizer,
            events,
            semanticMutations: semanticMutations,
            preMutationAnalyzer: preMutationAnalyzer,
            correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
            prompts: TestPromptLoader.Instance);
        var plan = new ImplementationPlan
        {
            Summary = "Rename IRetriever to IRetrieval.",
            Steps =
            [
                new ImplementationPlanStep
                {
                    StepId = stepId,
                    Title = "Rename interface",
                    Description = "Rename the symbol and references.",
                    FileIntents =
                    [
                        new PlanFileIntent { Kind = PlanFileChangeKind.Modify, Path = "Contracts/IRetriever.cs" },
                        new PlanFileIntent
                        {
                            Kind = PlanFileChangeKind.Rename,
                            Path = "Contracts/IRetriever.cs",
                            DestinationPath = "Contracts/IRetrieval.cs",
                        },
                        new PlanFileIntent { Kind = PlanFileChangeKind.Modify, Path = "Services/RetrieverConsumer.cs" },
                    ],
                    ExpectedOutcome = "All references use IRetrieval.",
                },
            ],
        };

        var staged = await application.HandleAsync(new ProposeMutationSetCommand(
            repository.SessionId,
            runId,
            repository.WorkspaceId,
            new TaskSpecification("Rename IRetriever", []),
            plan,
            RunPhase.ImplementationModelTurn));

        var request = Assert.Single(semanticMutations.RenameRequests);
        Assert.Equal("T:Demo.IRetriever", request.SymbolId);
        Assert.Equal("IRetrieval", request.NewName);
        Assert.Equal(3, staged.MutationSet.Mutations.Count);
        Mutation[] semanticMutationResults =
        [
            .. staged.MutationSet.Mutations.Where(mutation => mutation.Type == MutationType.RenameSymbol),
        ];
        Assert.Equal(2, semanticMutationResults.Length);
        Assert.All(semanticMutationResults, mutation => Assert.Equal(
            "T:Demo.IRetriever",
            mutation.RelatedSymbolId));
        Assert.Contains(staged.MutationSet.Mutations, mutation => mutation.Type == MutationType.MoveFile
            && mutation.DestinationRelativePath == "Contracts/IRetrieval.cs");
        Assert.Contains("IRetrieval", staged.Preview.UnifiedDiff, StringComparison.Ordinal);
        var preMutationRequest = Assert.Single(preMutationAnalyzer.Requests);
        Assert.Contains(preMutationRequest.OverlayFiles, file => file.RelativePath == "Contracts/IRetrieval.cs"
            && file.Text?.Contains("interface IRetrieval", StringComparison.Ordinal) == true);
        Assert.Contains(preMutationRequest.OverlayFiles, file => file.RelativePath == "Services/RetrieverConsumer.cs"
            && file.Text?.Contains("IRetrieval? _retriever", StringComparison.Ordinal) == true);
        Assert.Empty(observed.OfType<DiagnosticObserved>());
        SemanticMutationWarningObserved[] warnings = [.. observed.OfType<SemanticMutationWarningObserved>()];
        Assert.Equal(2, warnings.Length);
        Assert.Contains(warnings, warning => warning.Confidence == SemanticConfidenceLevel.PartialCompilation
            && warning.Message.Contains("PartialCompilation confidence", StringComparison.Ordinal));
        Assert.Contains(warnings, warning => warning.Message.Contains(
            "Projects outside the compiled subset were skipped.",
            StringComparison.Ordinal));
        var projection = await projections.GetAsync<SessionProjection>(new ProjectionKey(
            "session",
            repository.SessionId.Value.ToString("D")));
        Assert.Null(projection?.Error);
        Assert.Contains(projection?.Activity ?? [], activity => activity.Contains(
            "Semantic rename warning (PartialCompilation)",
            StringComparison.Ordinal));
        Assert.Equal(
            "namespace Demo;\npublic interface IRetriever { }\n",
            await File.ReadAllTextAsync(repository.PathOf("Contracts/IRetriever.cs")));
    }

    /// <summary>A stale model-authored semantic rename symbol is repairable model output.</summary>
    [Fact]
    public static async Task ModelMutationProposal_RenameSymbolFailure_ReasksWithCorrectiveMessage()
    {
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string>
        {
            ["Contracts/IRetriever.cs"] = "namespace Demo;\npublic interface IRetriever { }\n",
            ["Services/RetrieverConsumer.cs"] = "namespace Demo;\npublic sealed class RetrieverConsumer\n{\n    private IRetriever? _retriever;\n}\n",
        });
        var runId = RunId.New();
        var stepId = StepId.New();
        var badArguments = CreateArguments("T:Demo.MissingRetriever");
        var goodArguments = CreateArguments("T:Demo.IRetriever");
        var model = new QueueModelProvider(
            new ModelChunk
            {
                Output = new ToolRequestModelOutput("propose_mutations", badArguments),
                Usage = new ModelUsage(100, 50),
            },
            new ModelChunk
            {
                Output = new ToolRequestModelOutput("propose_mutations", goodArguments),
                Usage = new ModelUsage(100, 50),
            });
        var semanticMutations = new FakeSemanticMutationEngine();
        semanticMutations.MissingSymbolIds.Add("T:Demo.MissingRetriever");
        await using var scenario = await MutationScenario.CreateWithSemanticMutationEngineAsync(
            repository,
            model,
            semanticMutations);
        var plan = new ImplementationPlan
        {
            Summary = "Rename IRetriever to IRetrieval.",
            Steps =
            [
                new ImplementationPlanStep
                {
                    StepId = stepId,
                    Title = "Rename interface",
                    Description = "Rename the symbol and references.",
                    FileIntents = ModifyIntents("Contracts/IRetriever.cs", "Services/RetrieverConsumer.cs"),
                    ExpectedOutcome = "All references use IRetrieval.",
                },
            ],
        };

        var staged = await scenario.ProposeAsync(
            runId,
            new TaskSpecification("Rename IRetriever", []),
            plan,
            RunPhase.ImplementationModelTurn);

        Assert.Equal(2, model.Requests.Count);
        Assert.Equal(2, semanticMutations.RenameRequests.Count);
        var correction = Assert.Single(scenario.Events<ModelCorrectionAttempted>());
        Assert.Equal(ModelCorrectionCategory.MutationProposal, correction.Category);
        Assert.Empty(scenario.Events<MutationProposalRepairAttempted>());
        Assert.Empty(scenario.ContextRequests[1].Task.UserConstraints ?? []);
        Assert.Contains(
            "RenameSymbol proposal is invalid",
            GetCorrectionMessageText(model.Requests[1], "active-turn-mutation-correction:"),
            StringComparison.Ordinal);
        Assert.Contains("IRetrieval", staged.Preview.UnifiedDiff, StringComparison.Ordinal);

        static string CreateArguments(string relatedSymbolId)
        {
            var envelope = new MutationProposalEnvelope
            {
                MutationSet = new MutationProposalSet
                {
                    Mutations =
                    [
                        new RenameSymbolMutationProposal
                        {
                            RelativePath = "Contracts/IRetriever.cs",
                            RelatedSymbolId = relatedSymbolId,
                            ReplacementText = "IRetrieval",
                        },
                    ],
                    Rationale = "Rename the interface through Roslyn.",
                    Risk = MutationRisk.Medium,
                },
            };
            return JsonSerializer.Serialize(
                envelope,
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                    Converters = { new JsonStringEnumConverter() },
                });
        }
    }

    /// <summary>Pre-mutation diagnostics repair bad C# before staging or approval.</summary>
    [Fact]
    public static async Task ModelMutationProposal_PreMutationDiagnostic_ReasksBeforeStaging()
    {
        const string source = "namespace Demo;\npublic sealed class Example\n{\n}\n";
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string>
        {
            ["src/Example.cs"] = source,
        });
        var runId = RunId.New();
        var stepId = StepId.New();
        var baselineFile = Assert.Single(repository.Baseline.Files);
        var offset = source.IndexOf("}\n", StringComparison.Ordinal);
        var badArguments = CreateArguments("public void Broken( { }\n");
        var goodArguments = CreateArguments("public void Fixed() { }\n");
        var model = new QueueModelProvider(
            new ModelChunk
            {
                Output = new ToolRequestModelOutput("propose_mutations", badArguments),
                Usage = new ModelUsage(100, 50),
            },
            new ModelChunk
            {
                Output = new ToolRequestModelOutput("propose_mutations", goodArguments),
                Usage = new ModelUsage(100, 50),
            });
        var preMutationAnalyzer = new FakePreMutationAnalyzer();
        await using var scenario = await MutationScenario.CreateWithPreMutationAnalyzerAsync(
            repository,
            model,
            preMutationAnalyzer);
        var plan = new ImplementationPlan
        {
            Summary = "Edit Example.",
            Steps =
            [
                new ImplementationPlanStep
                {
                    StepId = stepId,
                    Title = "Edit Example",
                    Description = "Add a member.",
                    FileIntents = ModifyIntents("src/Example.cs"),
                    ExpectedOutcome = "Example has a valid member.",
                },
            ],
        };

        var staged = await scenario.ProposeAsync(
            runId,
            new TaskSpecification("Add member", []),
            plan,
            RunPhase.ImplementationModelTurn);

        Assert.Equal(2, model.Requests.Count);
        Assert.Equal(2, preMutationAnalyzer.Requests.Count);
        Assert.DoesNotContain("Broken", staged.Preview.UnifiedDiff, StringComparison.Ordinal);
        Assert.Contains("Fixed", staged.Preview.UnifiedDiff, StringComparison.Ordinal);
        Assert.Equal(source, await File.ReadAllTextAsync(repository.PathOf("src/Example.cs")));
        var correction = Assert.Single(scenario.Events<ModelCorrectionAttempted>());
        Assert.Equal(ModelCorrectionCategory.PreMutationAnalysis, correction.Category);
        var expectedPreMutationCorrection =
            "Pre-mutation Roslyn analysis found blocking diagnostics before staging or approval. "
            + "Revise the proposed mutation set against the same baseline; no repository files were changed."
            + " - src/Example.cs:4:20 CS1001 (Syntax): Identifier expected. "
            + "[containing MethodDeclaration] Hunk: public void Broken( { }"
            + " Omission: Repository analyzers were not loaded before approval.";
        Assert.Equal(expectedPreMutationCorrection, correction.SafeReason);
        Assert.Empty(scenario.Events<MutationProposalRepairAttempted>());
        Assert.Empty(scenario.ContextRequests[1].Task.UserConstraints ?? []);
        Assert.Contains(
            expectedPreMutationCorrection,
            GetCorrectionMessageText(model.Requests[1], "active-turn-mutation-correction:"),
            StringComparison.Ordinal);
        var analysisEvents = scenario.Events<PreMutationAnalysisCompleted>();
        Assert.Equal(2, analysisEvents.Count);
        Assert.Equal(PreMutationGateDecision.RepairableDiagnostics, analysisEvents[0].Decision);
        Assert.Equal(1, analysisEvents[0].BlockingDiagnosticCount);
        Assert.Equal(PreMutationGateDecision.PassedCheapGates, analysisEvents[1].Decision);
        var proposedEvent = Assert.Single(scenario.Events<MutationSetProposed>());
        Assert.DoesNotContain("Broken", proposedEvent.Preview?.UnifiedDiff ?? string.Empty, StringComparison.Ordinal);

        string CreateArguments(string replacementText)
        {
            var envelope = new MutationProposalEnvelope
            {
                MutationSet = new MutationProposalSet
                {
                    Mutations =
                    [
                        new ReplaceTextMutationProposal
                        {
                            RelativePath = "src/Example.cs",
                            BaselineSha256 = baselineFile.Sha256,
                            StartOffset = offset,
                            Length = 0,
                            ExpectedText = string.Empty,
                            ReplacementText = replacementText,
                        },
                    ],
                    Rationale = "Add the requested member.",
                    Risk = MutationRisk.Low,
                },
            };
            return JsonSerializer.Serialize(
                envelope,
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                    Converters = { new JsonStringEnumConverter() },
                });
        }
    }

    /// <summary>Create-file lifecycle conflicts fail before pre-mutation Roslyn screening.</summary>
    [Fact]
    public static async Task ModelMutationProposal_CreateFileExistingPath_FailsBeforePreMutationAnalysis()
    {
        const string source = "namespace Demo;\npublic sealed class Example\n{\n}\n";
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string>
        {
            ["src/Example.cs"] = source,
        });
        var stepId = StepId.New();
        var arguments = CreateArguments(stepId);
        var model = new QueueModelProvider(new ModelChunk
        {
            Output = new ToolRequestModelOutput("propose_mutations", arguments),
            Usage = new ModelUsage(100, 50),
        });
        await using var scenario = await MutationScenario.CreateWithPreMutationAnalyzerAsync(
            repository,
            model,
            new ThrowingPreMutationAnalyzer());
        var plan = new ImplementationPlan
        {
            Summary = "Create existing file.",
            Steps =
            [
                new ImplementationPlanStep
                {
                    StepId = stepId,
                    Title = "Create existing file",
                    Description = "Attempt to create an existing file.",
                    FileIntents = CreateIntents("src/Example.cs"),
                    ExpectedOutcome = "The lifecycle conflict fails.",
                },
            ],
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.ProposeAsync(
            RunId.New(),
            new TaskSpecification("Create existing file", []),
            plan,
            RunPhase.ImplementationModelTurn));

        Assert.Empty(scenario.Events<PreMutationAnalysisCompleted>());
        Assert.Empty(scenario.Events<MutationSetProposed>());
        Assert.Equal(source, await File.ReadAllTextAsync(repository.PathOf("src/Example.cs")));

        static string CreateArguments(StepId stepId)
        {
            var envelope = new MutationProposalEnvelope
            {
                MutationSet = new MutationProposalSet
                {
                    Mutations =
                    [
                        new CreateFileMutationProposal
                        {
                            RelativePath = "src/Example.cs",
                            Content = new FileContentDescriptor
                            {
                                Text = "namespace Demo; public sealed class Replacement { }\n",
                            },
                        },
                    ],
                    Rationale = "Attempt to create an existing file.",
                    Risk = MutationRisk.Low,
                },
            };
            return JsonSerializer.Serialize(
                envelope,
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                    Converters = { new JsonStringEnumConverter() },
                });
        }
    }

    /// <summary>Pre-mutation overlays preserve case-distinct C# paths when the workspace is case-sensitive.</summary>
    [Fact]
    public static async Task ModelMutationProposal_PreMutationOverlay_PreservesCaseDistinctPaths()
    {
        const string upperSource = "namespace Demo;\npublic sealed class Foo\n{\n}\n";
        const string lowerSource = "namespace Demo;\npublic sealed class foo\n{\n}\n";
        var stepId = StepId.New();
        var repositoryRoot = Path.Combine(Path.GetTempPath(), $"threadsmith-m5-overlay-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repositoryRoot);
        try
        {
            var caseSensitiveProbePath = Path.Combine(repositoryRoot, "CaseSensitiveProbe");
            var workspaceId = WorkspaceId.New();
            var sessionId = SessionId.New();
            var baseline = CreateBaseline(
                workspaceId,
                repositoryRoot,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["src/Foo.cs"] = upperSource,
                    ["src/foo.cs"] = lowerSource,
                });
            var workspace = new FakeTransactionalWorkspace(
                baseline,
                new WorkspaceIsolation(WorkspaceIsolationMode.TrackedInPlace, caseSensitiveProbePath),
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["src/Foo.cs"] = upperSource,
                    ["src/foo.cs"] = lowerSource,
                });
            var workspaces = new FakeTransactionalWorkspaceResolver(workspace);
            var upperOffset = upperSource.IndexOf("}\n", StringComparison.Ordinal);
            var lowerOffset = lowerSource.IndexOf("}\n", StringComparison.Ordinal);
            var arguments = CreateArguments(
                stepId,
                baseline.Files.Single(file => file.RelativePath == "src/Foo.cs").Sha256,
                baseline.Files.Single(file => file.RelativePath == "src/foo.cs").Sha256,
                upperOffset,
                lowerOffset);
            var model = new QueueModelProvider(new ModelChunk
            {
                Output = new ToolRequestModelOutput("propose_mutations", arguments),
                Usage = new ModelUsage(100, 50),
            });
            var preMutationAnalyzer = new FakePreMutationAnalyzer();
            await using var scenario = await MutationScenario.CreateForCaseSensitiveWorkspaceAsync(
                sessionId,
                baseline,
                model,
                workspaces,
                preMutationAnalyzer);
            var plan = new ImplementationPlan
            {
                Summary = "Edit case-distinct files.",
                Steps =
                [
                    new ImplementationPlanStep
                    {
                        StepId = stepId,
                        Title = "Edit files",
                        Description = "Touch both case-distinct files.",
                        FileIntents = ModifyIntents("src/Foo.cs", "src/foo.cs"),
                        ExpectedOutcome = "Both files are analyzed independently.",
                    },
                ],
            };

            await scenario.ProposeAsync(
                RunId.New(),
                new TaskSpecification("Edit case-distinct files", []),
                plan,
                RunPhase.ImplementationModelTurn);

            var request = Assert.Single(preMutationAnalyzer.Requests);
            Assert.Contains(request.OverlayFiles, file => file.RelativePath == "src/Foo.cs"
                && file.Text?.Contains("UpperCaseEdit", StringComparison.Ordinal) == true
                && file.Text.Contains("public sealed class Foo", StringComparison.Ordinal));
            Assert.Contains(request.OverlayFiles, file => file.RelativePath == "src/foo.cs"
                && file.Text?.Contains("LowerCaseEdit", StringComparison.Ordinal) == true
                && file.Text.Contains("public sealed class foo", StringComparison.Ordinal));
            Assert.Equal(2, request.OverlayFiles.Count(file => string.Equals(file.RelativePath, "src/Foo.cs", StringComparison.OrdinalIgnoreCase)));
            Assert.Equal(1, workspaces.StageCalls);
        }
        finally
        {
            Directory.Delete(repositoryRoot, recursive: true);
        }

        static WorkspaceBaseline CreateBaseline(
            WorkspaceId workspaceId,
            string repositoryRoot,
            IReadOnlyDictionary<string, string> files) => new(
                workspaceId,
                repositoryRoot,
                DateTimeOffset.UtcNow,
                files.Select(file => new WorkspaceFileHash(
                        file.Key,
                        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(file.Value))),
                        Encoding.UTF8.GetByteCount(file.Value)))
                    .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
                    .ToArray(),
                ApprovedRoots: ["."],
                TrustLevel: RepositoryTrustLevel.TrustedMutation);

        static string CreateArguments(
            StepId stepId,
            string upperSha256,
            string lowerSha256,
            int upperOffset,
            int lowerOffset)
        {
            var envelope = new MutationProposalEnvelope
            {
                MutationSet = new MutationProposalSet
                {
                    Mutations =
                    [
                        new ReplaceTextMutationProposal
                        {
                            RelativePath = "src/Foo.cs",
                            BaselineSha256 = upperSha256,
                            StartOffset = upperOffset,
                            Length = 0,
                            ExpectedText = string.Empty,
                            ReplacementText = "    public void UpperCaseEdit() { }\n",
                        },
                        new ReplaceTextMutationProposal
                        {
                            RelativePath = "src/foo.cs",
                            BaselineSha256 = lowerSha256,
                            StartOffset = lowerOffset,
                            Length = 0,
                            ExpectedText = string.Empty,
                            ReplacementText = "    public void LowerCaseEdit() { }\n",
                        },
                    ],
                    Rationale = "Edit both case-distinct files.",
                    Risk = MutationRisk.Low,
                },
            };
            return JsonSerializer.Serialize(
                envelope,
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                    Converters = { new JsonStringEnumConverter() },
                });
        }
    }

    /// <summary>Non-repairable pre-mutation decisions fail closed before staging.</summary>
    [Theory]
    [InlineData(PreMutationGateDecision.NonRepairableHostFailure)]
    [InlineData(PreMutationGateDecision.BudgetExhausted)]
    public static async Task ModelMutationProposal_PreMutationFailureDecision_FailsBeforeStaging(
        PreMutationGateDecision decision)
    {
        const string source = "namespace Demo;\npublic sealed class Example\n{\n}\n";
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string>
        {
            ["src/Example.cs"] = source,
        });
        var baselineFile = Assert.Single(repository.Baseline.Files);
        var offset = source.IndexOf("}\n", StringComparison.Ordinal);
        var stepId = StepId.New();
        var arguments = CreateArguments(stepId, baselineFile.Sha256, offset, "public void Fixed() { }\n");
        var model = new QueueModelProvider(new ModelChunk
        {
            Output = new ToolRequestModelOutput("propose_mutations", arguments),
            Usage = new ModelUsage(100, 50),
        });
        await using var scenario = await MutationScenario.CreateWithPreMutationAnalyzerAsync(
            repository,
            model,
            new DecisionPreMutationAnalyzer(decision));
        var plan = new ImplementationPlan
        {
            Summary = "Edit Example.",
            Steps =
            [
                new ImplementationPlanStep
                {
                    StepId = stepId,
                    Title = "Edit Example",
                    Description = "Add a member.",
                    FileIntents = ModifyIntents("src/Example.cs"),
                    ExpectedOutcome = "Example has a valid member.",
                },
            ],
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.ProposeAsync(
            RunId.New(),
            new TaskSpecification("Add member", []),
            plan,
            RunPhase.ImplementationModelTurn));

        Assert.Empty(scenario.Events<MutationSetProposed>());
        Assert.Equal(source, await File.ReadAllTextAsync(repository.PathOf("src/Example.cs")));
        var completed = Assert.Single(scenario.Events<PreMutationAnalysisCompleted>());
        Assert.Equal(decision, completed.Decision);

        static string CreateArguments(
            StepId stepId,
            string baselineSha256,
            int startOffset,
            string replacementText)
        {
            var envelope = new MutationProposalEnvelope
            {
                MutationSet = new MutationProposalSet
                {
                    Mutations =
                    [
                        new ReplaceTextMutationProposal
                        {
                            RelativePath = "src/Example.cs",
                            BaselineSha256 = baselineSha256,
                            StartOffset = startOffset,
                            Length = 0,
                            ExpectedText = string.Empty,
                            ReplacementText = replacementText,
                        },
                    ],
                    Rationale = "Add the requested member.",
                    Risk = MutationRisk.Low,
                },
            };
            return JsonSerializer.Serialize(
                envelope,
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                    Converters = { new JsonStringEnumConverter() },
                });
        }
    }

    /// <summary>Native mutation tool arguments are bounded before JSON parsing.</summary>
    [Fact]
    public static async Task ModelMutationProposal_NativeToolArguments_EnforcesStructuredOutputLimitBeforeParsing()
    {
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string>
        {
            ["src/Example.cs"] = "old",
        });
        var runId = RunId.New();
        var stepId = StepId.New();
        string oversizedInvalidArguments = new('x', 64);
        var model = new ChunkModelProvider(new ModelChunk
        {
            Output = new ToolRequestModelOutput("propose_mutations", oversizedInvalidArguments),
        });
        await using var scenario = await MutationScenario.CreateWithLimitsAsync(
            repository,
            model,
            ExecutionLimits.Default with
            {
                MaxStructuredOutputCharacters = "propose_mutations".Length + 8,
            });
        var plan = new ImplementationPlan
        {
            Summary = "Change Example.",
            Steps =
            [
                new ImplementationPlanStep
                {
                    StepId = stepId,
                    Title = "Edit Example",
                    Description = "Replace the value.",
                    FileIntents = ModifyIntents("src/Example.cs"),
                    ExpectedOutcome = "Example is updated.",
                },
            ],
        };

        var exception = await Assert.ThrowsAsync<MalformedModelOutputException>(() =>
            scenario.ProposeAsync(
                runId,
                new TaskSpecification("Change Example", []),
                plan,
                RunPhase.ImplementationModelTurn));

        Assert.Contains("structured-output limit", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("schema", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A model-authored create-file proposal maps content text into replacement text.</summary>
    [Fact]
    public static async Task ModelMutationProposal_CreateFileContentText_StagesReplacementText()
    {
        const string content = "namespace Demo;\npublic sealed class Added\n{\n}\n";
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string>());
        var runId = RunId.New();
        var stepId = StepId.New();
        var envelope = new MutationProposalEnvelope
        {
            MutationSet = new MutationProposalSet
            {
                Mutations =
                [
                    new CreateFileMutationProposal
                    {
                        RelativePath = "src/Added.cs",
                        Content = new FileContentDescriptor
                        {
                            Text = content,
                        },
                    },
                ],
                Rationale = "Add the requested file.",
                Risk = MutationRisk.Low,
            },
        };
        var arguments = JsonSerializer.Serialize(
            envelope,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                Converters = { new JsonStringEnumConverter() },
            });
        var model = new ChunkModelProvider(new ModelChunk
        {
            Output = new ToolRequestModelOutput("propose_mutations", arguments),
            Usage = new ModelUsage(100, 50),
        });
        await using var scenario = await MutationScenario.CreateAsync(repository, model);
        var plan = new ImplementationPlan
        {
            Summary = "Add a file.",
            Steps =
            [
                new ImplementationPlanStep
                {
                    StepId = stepId,
                    Title = "Add file",
                    Description = "Create the requested file.",
                    FileIntents = CreateIntents("src/Added.cs"),
                    ExpectedOutcome = "File exists.",
                },
            ],
        };

        var staged = await scenario.ProposeAsync(
            runId,
            new TaskSpecification("Add file", []),
            plan,
            RunPhase.ImplementationModelTurn);

        var mutation = Assert.Single(staged.MutationSet.Mutations);
        Assert.Equal(MutationType.CreateFile, mutation.Type);
        Assert.Equal(content, mutation.Content?.Text);
        Assert.Equal(content, mutation.ReplacementText);
    }

    /// <summary>A bad model-authored expectedText receives corrective feedback and can repair.</summary>
    [Fact]
    public static async Task ModelMutationProposal_BadExpectedText_ReasksWithCorrectiveMessage()
    {
        const string source = "public override string Name => StandardizerName;";
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string>
        {
            ["src/SectorEntityStandardizer.cs"] = source,
        });
        var runId = RunId.New();
        var stepId = StepId.New();
        var baselineFile = Assert.Single(repository.Baseline.Files);
        var badArguments = CreateArguments("nameof(IEntityStandardizer).Sector", "\"Test\"");
        var goodArguments = CreateArguments("StandardizerName", "\"Test\"");
        var model = new QueueModelProvider(
            new ModelChunk
            {
                Output = new ToolRequestModelOutput("propose_mutations", badArguments),
                Usage = new ModelUsage(100, 50),
            },
            new ModelChunk
            {
                Output = new ToolRequestModelOutput("propose_mutations", goodArguments),
                Usage = new ModelUsage(100, 50),
            });
        await using var scenario = await MutationScenario.CreateAsync(repository, model);
        var plan = new ImplementationPlan
        {
            Summary = "Change the Name property.",
            Steps =
            [
                new ImplementationPlanStep
                {
                    StepId = stepId,
                    Title = "Edit Name",
                    Description = "Return the requested literal.",
                    FileIntents = ModifyIntents("src/SectorEntityStandardizer.cs"),
                    ExpectedOutcome = "Name returns Test.",
                },
            ],
        };

        var staged = await scenario.ProposeAsync(
            runId,
            new TaskSpecification("Change Name", []),
            plan,
            RunPhase.ImplementationModelTurn);

        Assert.Equal(2, model.Requests.Count);
        var starts = scenario.Events<MutationProposalStarted>();
        Assert.Equal([1, 2], [.. starts.Select(item => item.AttemptNumber)]);
        Assert.All(starts, start => Assert.Equal(4, start.MaximumAttempts));
        var correction = Assert.Single(scenario.Events<ModelCorrectionAttempted>());
        Assert.Equal(runId, correction.RunId);
        Assert.Equal(ModelCorrectionCategory.MutationProposal, correction.Category);
        Assert.Equal(1, correction.AttemptNumber);
        Assert.Equal(3, correction.MaximumAttempts);
        Assert.Contains("expectedText was not found", correction.SafeReason, StringComparison.Ordinal);
        Assert.Empty(scenario.Events<MutationProposalRepairAttempted>());
        Assert.Empty(scenario.ContextRequests[1].Task.UserConstraints ?? []);
        var additionalMessage = Assert.Single(scenario.ContextRequests[1].AdditionalMessages);
        Assert.StartsWith("active-turn-mutation-correction:", additionalMessage.SectionId, StringComparison.Ordinal);
        Assert.Contains(
            "expectedText was not found",
            GetCorrectionMessageText(model.Requests[1], "active-turn-mutation-correction:"),
            StringComparison.Ordinal);
        var initialWireEstimate = model.Requests[0].WireEstimate
            ?? throw new InvalidOperationException("The initial mutation request should have a wire estimate.");
        var retryWireEstimate = model.Requests[1].WireEstimate
            ?? throw new InvalidOperationException("The corrected mutation request should have a wire estimate.");
        Assert.Contains("active-turn-mutation-correction:1", retryWireEstimate.SectionTokens.Keys);
        Assert.True(retryWireEstimate.WireInputTokens > initialWireEstimate.WireInputTokens);
        Assert.Contains("+public override string Name => \"Test\";", staged.Preview.UnifiedDiff);

        string CreateArguments(string expectedText, string replacementText)
        {
            var envelope = new MutationProposalEnvelope
            {
                MutationSet = new MutationProposalSet
                {
                    Mutations =
                    [
                        new ReplaceTextMutationProposal
                        {
                            RelativePath = "src/SectorEntityStandardizer.cs",
                            BaselineSha256 = baselineFile.Sha256,
                            StartOffset = 0,
                            Length = expectedText.Length,
                            ExpectedText = expectedText,
                            ReplacementText = replacementText,
                        },
                    ],
                    Rationale = "Return the requested literal.",
                    Risk = MutationRisk.Low,
                },
            };
            return JsonSerializer.Serialize(
                envelope,
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                    Converters = { new JsonStringEnumConverter() },
                });
        }
    }

    /// <summary>Request-local correction messages participate in assembly-time budget reduction.</summary>
    [Fact]
    public static async Task MutationContextAssembly_CorrectionMessagesReserveBudgetBeforeEvidenceSelection()
    {
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string>
        {
            ["src/Example.cs"] = "old",
        });
        var sessionId = repository.SessionId;
        var runId = RunId.New();
        var stepId = StepId.New();
        var baseline = repository.Baseline;
        var plan = new ImplementationPlan
        {
            Summary = "Change Example.",
            Steps =
            [
                new ImplementationPlanStep
                {
                    StepId = stepId,
                    Title = "Edit Example",
                    Description = "Update the example.",
                    FileIntents = ModifyIntents("src/Example.cs"),
                    ExpectedOutcome = "Example is updated.",
                },
            ],
        };
        var correctionMessage = new ModelMessage
        {
            Role = ModelMessageRole.Developer,
            SectionId = "active-turn-post-apply-correction:1",
            Content =
            [
                new ModelContentPart
                {
                    Content = "Corrective turn 1 of 3: validation failed; propose a bounded correction.",
                },
            ],
        };

        var withoutCorrection = await AssembleMutationContextAsync(
            32_000,
            includeEvidence: true,
            includeCorrection: false);
        var correctionWithoutEvidence = await AssembleMutationContextAsync(
            32_000,
            includeEvidence: false,
            includeCorrection: true);
        var targetBudget = withoutCorrection.WireEstimate?.WireInputTokens
            ?? throw new InvalidOperationException("Expected a wire estimate.");
        var correctionWithoutEvidenceEstimate = correctionWithoutEvidence.WireEstimate
            ?? throw new InvalidOperationException("Expected a correction wire estimate.");
        Assert.True(correctionWithoutEvidenceEstimate.WireInputTokens < targetBudget);

        var reduced = await AssembleMutationContextAsync(
            targetBudget,
            includeEvidence: true,
            includeCorrection: true);

        var reducedEstimate = reduced.WireEstimate
            ?? throw new InvalidOperationException("Expected a reduced wire estimate.");
        Assert.True(reducedEstimate.WireInputTokens <= reduced.Inspection.TokenBudget);
        Assert.Contains(correctionMessage.SectionId, reducedEstimate.SectionTokens.Keys);
        Assert.Contains(reduced.Messages ?? [], message =>
            string.Equals(message.SectionId, correctionMessage.SectionId, StringComparison.Ordinal));
        Assert.Contains(reduced.Inspection.Evidence, evidence => !evidence.Included);

        async Task<ContextAssemblyResult> AssembleMutationContextAsync(
            int maximumTokens,
            bool includeEvidence,
            bool includeCorrection)
        {
            await using var events = new DomainEventStream();
            var sanitizer = new PassthroughSanitizer();
            var evidence = new EvidenceStore(events, sanitizer);
            if (includeEvidence)
            {
                var content = new string('e', 12_000);
                await evidence.AddAsync(new Evidence
                {
                    EvidenceId = EvidenceId.New(),
                    SessionId = sessionId,
                    RunId = runId,
                    Kind = EvidenceKind.SourceExcerpt,
                    Content = content,
                    Provenance = new EvidenceProvenance { Source = "test" },
                    CollectedAt = DateTimeOffset.UtcNow,
                    Relevance = 0.1,
                    EstimatedTokens = TokenEstimator.Estimate(content),
                });
            }

            var assembler = new ContextAssembler(
                evidence,
                new TokenEstimator(),
                new ContextPolicy(),
                new PromptAppendLoader(sanitizer),
                sanitizer,
                events,
                TestPromptLoader.Instance,
                new ContextAssemblerOptions { MaximumTokens = maximumTokens });
            return await assembler.AssembleAsync(new ContextAssemblyRequest
            {
                SessionId = sessionId,
                RunId = runId,
                Phase = RunPhase.CorrectionModelTurn,
                Task = new TaskSpecification("Change Example", []),
                RepositoryPath = repository.Root,
                WorkingScope = "src/Example.cs",
                ApprovedPlan = plan,
                MutationBaseline = baseline,
                ToolSchemas =
                [
                    new ContextToolSchema(
                        "propose_mutations",
                        "Submit a mutation proposal.",
                        "{\"type\":\"object\"}",
                        PreferStrictArguments: true),
                ],
                AdditionalMessages = includeCorrection ? [correctionMessage] : [],
            });
        }
    }

    /// <summary>Repairable mutation proposal failures fail closed when the corrective-turn budget is exhausted.</summary>
    [Fact]
    public static async Task ModelMutationProposal_ExpectedTextExhaustion_FailsWithoutStaging()
    {
        const string source = "public override string Name => StandardizerName;";
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string>
        {
            ["src/SectorEntityStandardizer.cs"] = source,
        });
        var runId = RunId.New();
        var stepId = StepId.New();
        var baselineFile = Assert.Single(repository.Baseline.Files);
        var badArguments = CreateArguments("nameof(IEntityStandardizer).Sector", "\"Test\"");
        var model = new QueueModelProvider(
            new ModelChunk
            {
                Output = new ToolRequestModelOutput("propose_mutations", badArguments),
                Usage = new ModelUsage(100, 50),
            },
            new ModelChunk
            {
                Output = new ToolRequestModelOutput("propose_mutations", badArguments),
                Usage = new ModelUsage(100, 50),
            });
        await using var scenario = await MutationScenario.CreateWithLimitsAsync(
            repository,
            model,
            ExecutionLimits.Default with { MaxCorrectiveTurns = 1 });
        var plan = new ImplementationPlan
        {
            Summary = "Change the Name property.",
            Steps =
            [
                new ImplementationPlanStep
                {
                    StepId = stepId,
                    Title = "Edit Name",
                    Description = "Return the requested literal.",
                    FileIntents = ModifyIntents("src/SectorEntityStandardizer.cs"),
                    ExpectedOutcome = "Name returns Test.",
                },
            ],
        };

        var exception = await Assert.ThrowsAsync<MalformedModelOutputException>(() =>
            scenario.ProposeAsync(
                runId,
                new TaskSpecification("Change Name", []),
                plan,
                RunPhase.ImplementationModelTurn));

        Assert.Contains("corrective-turn budget was exhausted", exception.Message, StringComparison.Ordinal);
        Assert.Contains("expectedText was not found", exception.Message, StringComparison.Ordinal);
        Assert.Equal(2, model.Requests.Count);
        Assert.Single(scenario.Events<ModelCorrectionAttempted>());
        Assert.Empty(scenario.Events<MutationProposalRepairAttempted>());
        Assert.Empty(scenario.Events<MutationSetProposed>());
        Assert.Empty(scenario.ContextRequests[1].Task.UserConstraints ?? []);
        Assert.Contains(
            "expectedText was not found",
            GetCorrectionMessageText(model.Requests[1], "active-turn-mutation-correction:"),
            StringComparison.Ordinal);

        string CreateArguments(string expectedText, string replacementText)
        {
            var envelope = new MutationProposalEnvelope
            {
                MutationSet = new MutationProposalSet
                {
                    Mutations =
                    [
                        new ReplaceTextMutationProposal
                        {
                            RelativePath = "src/SectorEntityStandardizer.cs",
                            BaselineSha256 = baselineFile.Sha256,
                            StartOffset = 0,
                            Length = expectedText.Length,
                            ExpectedText = expectedText,
                            ReplacementText = replacementText,
                        },
                    ],
                    Rationale = "Return the requested literal.",
                    Risk = MutationRisk.Low,
                },
            };
            return JsonSerializer.Serialize(
                envelope,
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                    Converters = { new JsonStringEnumConverter() },
                });
        }
    }

    /// <summary>Legacy-shaped mutation tool arguments receive schema corrective feedback and can repair.</summary>
    [Fact]
    public static async Task ModelMutationProposal_LegacyMutationItemShape_ReasksWithSchemaCorrectiveMessage()
    {
        const string source = "public override string Name => StandardizerName;";
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string>
        {
            ["src/SectorEntityStandardizer.cs"] = source,
        });
        var runId = RunId.New();
        var stepId = StepId.New();
        var baselineFile = Assert.Single(repository.Baseline.Files);
        var badArguments = $$"""
            {
              "schemaVersion": 1,
              "planRevision": 1,
              "planStepIds": ["{{stepId.Value}}"],
              "mutationSet": {
                "mutations": [
                  {
                    "baselineHash": "{{baselineFile.Sha256}}",
                    "expectedText": "public override string Name => StandardizerName;",
                    "kind": "ReplaceText",
                    "path": "src/SectorEntityStandardizer.cs",
                    "rationale": "Return the requested literal.",
                    "replacementText": "public override string Name => \"Test\";",
                    "risk": "Consumers relying on the old value may behave differently.",
                    "validation": "Build the solution."
                  }
                ],
                "sequence": 1
              },
              "expectedOutcomes": ["Name returns Test."],
              "validationExpectations": ["Solution builds."]
            }
            """;
        var goodArguments = CreateArguments();
        var model = new QueueModelProvider(
            new ModelChunk
            {
                Output = new ToolRequestModelOutput("propose_mutations", badArguments),
                Usage = new ModelUsage(100, 50),
            },
            new ModelChunk
            {
                Output = new ToolRequestModelOutput("propose_mutations", goodArguments),
                Usage = new ModelUsage(100, 50),
            });
        await using var scenario = await MutationScenario.CreateAsync(repository, model);
        var plan = new ImplementationPlan
        {
            Summary = "Change the Name property.",
            Steps =
            [
                new ImplementationPlanStep
                {
                    StepId = stepId,
                    Title = "Edit Name",
                    Description = "Return the requested literal.",
                    FileIntents = ModifyIntents("src/SectorEntityStandardizer.cs"),
                    ExpectedOutcome = "Name returns Test.",
                },
            ],
        };

        var staged = await scenario.ProposeAsync(
            runId,
            new TaskSpecification("Change Name", []),
            plan,
            RunPhase.ImplementationModelTurn);

        Assert.Equal(2, model.Requests.Count);
        var correction = Assert.Single(scenario.Events<ModelCorrectionAttempted>());
        Assert.Equal(runId, correction.RunId);
        Assert.Equal(ModelCorrectionCategory.MutationProposal, correction.Category);
        Assert.Equal(1, correction.AttemptNumber);
        Assert.Contains("schema", correction.SafeReason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(scenario.Events<MutationProposalRepairAttempted>());
        Assert.Empty(scenario.ContextRequests[1].Task.UserConstraints ?? []);
        var correctionText = GetCorrectionMessageText(model.Requests[1], "active-turn-mutation-correction:");
        Assert.Contains("propose_mutations", correctionText, StringComparison.Ordinal);
        Assert.Contains("advertised schema", correctionText, StringComparison.Ordinal);
        Assert.Contains("+public override string Name => \"Test\";", staged.Preview.UnifiedDiff);

        string CreateArguments()
        {
            var envelope = new MutationProposalEnvelope
            {
                MutationSet = new MutationProposalSet
                {
                    Mutations =
                    [
                        new ReplaceTextMutationProposal
                        {
                            RelativePath = "src/SectorEntityStandardizer.cs",
                            BaselineSha256 = baselineFile.Sha256,
                            StartOffset = 0,
                            Length = source.Length,
                            ExpectedText = source,
                            ReplacementText = "public override string Name => \"Test\";",
                        },
                    ],
                    Rationale = "Return the requested literal.",
                    Risk = MutationRisk.Low,
                },
            };
            return JsonSerializer.Serialize(
                envelope,
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                    Converters = { new JsonStringEnumConverter() },
                });
        }
    }

    /// <summary>Mutation reasoning is published while serialized structured output remains uncontaminated.</summary>
    [Fact]
    public static async Task ModelMutationProposal_ReasoningThenJson_PublishesReasoningAndStagesCleanPayload()
    {
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string>
        {
            ["src/Example.cs"] = "old",
        });
        var runId = RunId.New();
        var mutation = CreateReplacement(repository, "src/Example.cs", 0, "old", "new");
        var proposal = CreateMutationSet(repository, [mutation]) with { RunId = runId };
        var json = JsonSerializer.Serialize(
            new MutationSetModelOutput(proposal),
            new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } });
        var model = new ChunkModelProvider(
            new ModelChunk { Reasoning = "private <secret> thought" },
            new ModelChunk { Text = json },
            new ModelChunk { Usage = new ModelUsage(100, 50) });
        var sanitizer = new RedactingSanitizer();
        await using var scenario = await MutationScenario.CreateWithSanitizerAsync(
            repository,
            model,
            sanitizer);
        var plan = new ImplementationPlan
        {
            Summary = "Change Example.",
            Steps =
            [
                new ImplementationPlanStep
                {
                    StepId = StepId.New(),
                    Title = "Edit Example",
                    Description = "Replace the value.",
                    FileIntents = ModifyIntents("src/Example.cs"),
                    ExpectedOutcome = "Example is updated.",
                },
            ],
        };

        var staged = await scenario.ProposeAsync(
            runId,
            new TaskSpecification("Change Example", []),
            plan);

        var reasoning = Assert.Single(scenario.Events<ModelReasoningObserved>());
        Assert.Equal(repository.SessionId, reasoning.SessionId);
        Assert.Equal("private [redacted] thought", reasoning.Text);
        Assert.False(staged.Conflicts.HasConflicts);
        Assert.Contains("+new", staged.Preview.UnifiedDiff, StringComparison.Ordinal);
        Assert.DoesNotContain("private", staged.Preview.UnifiedDiff, StringComparison.Ordinal);
    }

    /// <summary>Mutation proposal path confinement failures fail closed without corrective retry.</summary>
    [Fact]
    public static async Task ModelMutationProposal_PathConfinementViolation_FailsWithoutCorrection()
    {
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string>
        {
            ["src/Example.cs"] = "old",
        });
        var runId = RunId.New();
        var stepId = StepId.New();
        var baselineFile = Assert.Single(repository.Baseline.Files);
        var envelope = new MutationProposalEnvelope
        {
            MutationSet = new MutationProposalSet
            {
                Mutations =
                [
                    new ReplaceTextMutationProposal
                    {
                        RelativePath = "../escape.cs",
                        BaselineSha256 = baselineFile.Sha256,
                        StartOffset = 0,
                        Length = 3,
                        ExpectedText = "old",
                        ReplacementText = "new",
                    },
                ],
                Rationale = "Attempt to escape the repository.",
                Risk = MutationRisk.Low,
            },
        };
        var model = new QueueModelProvider(new ModelChunk
        {
            Output = new ToolRequestModelOutput(
                "propose_mutations",
                JsonSerializer.Serialize(
                    envelope,
                    new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                        Converters = { new JsonStringEnumConverter() },
                    })),
        });
        await using var scenario = await MutationScenario.CreateWithLimitsAsync(
            repository,
            model,
            ExecutionLimits.Default with { MaxCorrectiveTurns = 2 });
        var plan = new ImplementationPlan
        {
            Revision = 1,
            Summary = "Change Example.",
            Steps =
            [
                new ImplementationPlanStep
                {
                    StepId = stepId,
                    Title = "Edit Example",
                    Description = "Replace the value.",
                    FileIntents = ModifyIntents("src/Example.cs"),
                    ExpectedOutcome = "Example is updated.",
                },
            ],
        };

        var exception = await Assert.ThrowsAsync<MalformedModelOutputException>(() =>
            scenario.ProposeAsync(
                runId,
                new TaskSpecification("Change Example", []),
                plan,
                RunPhase.ImplementationModelTurn));

        Assert.Contains("path confinement", exception.Message, StringComparison.Ordinal);
        Assert.Single(model.Requests);
        Assert.Empty(scenario.Events<ModelCorrectionAttempted>());
        Assert.Empty(scenario.Events<MutationSetProposed>());
        Assert.Equal("old", await File.ReadAllTextAsync(repository.PathOf("src/Example.cs")));
    }

    /// <summary>Model proposals cannot stage files that are outside the accepted plan.</summary>
    [Fact]
    public static async Task ModelMutationProposal_OutsideApprovedPlan_IsRejectedBeforeStaging()
    {
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string>
        {
            ["src/Example.cs"] = "planned",
            ["src/Unplanned.cs"] = "protected",
        });
        var runId = RunId.New();
        var proposal = CreateMutationSet(
            repository,
            [CreateReplacement(repository, "src/Unplanned.cs", 0, "protected", "changed")]) with
        {
            RunId = runId,
        };
        var model = new FakeModelProvider(new ScriptedSession
        {
            Turns =
            [
                new ScriptedTurn
                {
                    Output = new MutationSetModelOutput(proposal),
                    Usage = new ModelUsage(100, 50),
                },
            ],
        });
        await using var scenario = await MutationScenario.CreateAsync(repository, model);
        var plan = new ImplementationPlan
        {
            Summary = "Change only the planned file.",
            Steps =
            [
                new ImplementationPlanStep
                {
                    StepId = StepId.New(),
                    Title = "Edit Example",
                    Description = "Keep the change bounded.",
                    FileIntents = ModifyIntents("src/Example.cs"),
                    ExpectedOutcome = "Only the planned file changes.",
                },
            ],
        };

        var exception = await Assert.ThrowsAsync<MalformedModelOutputException>(() =>
            scenario.ProposeAsync(
                runId,
                new TaskSpecification("Change Example", []),
                plan));

        Assert.Contains("outside the approved plan", exception.Message, StringComparison.Ordinal);
        Assert.Equal("protected", await File.ReadAllTextAsync(repository.PathOf("src/Unplanned.cs")));
        var commitException = await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            scenario.ExecuteAsync(
                proposal.MutationSetId,
                new MutationApproval
                {
                    Level = MutationApprovalLevel.EntireSet,
                }));
        Assert.Contains("not registered", commitException.Message, StringComparison.Ordinal);
    }

    /// <summary>Approved Modify intents cannot authorize destructive file lifecycle mutations.</summary>
    [Fact]
    public static async Task ModelMutationProposal_ModifyIntentCannotDeletePlannedPath()
    {
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string>
        {
            ["src/Example.cs"] = "planned",
        });
        var runId = RunId.New();
        var baselineFile = Assert.Single(repository.Baseline.Files);
        var destructiveMutation = new Mutation
        {
            MutationId = MutationId.New(),
            Type = MutationType.DeleteFile,
            RelativePath = "src/Example.cs",
            BaselineSha256 = baselineFile.Sha256,
            ExpectedIdentity = new ExpectedFileIdentity
            {
                Sha256 = baselineFile.Sha256,
                ByteLength = baselineFile.Length,
            },
        };
        var proposal = CreateMutationSet(repository, [destructiveMutation]) with
        {
            RunId = runId,
        };
        var model = new FakeModelProvider(new ScriptedSession
        {
            Turns =
            [
                new ScriptedTurn
                {
                    Output = new MutationSetModelOutput(proposal),
                    Usage = new ModelUsage(100, 50),
                },
            ],
        });
        await using var scenario = await MutationScenario.CreateWithPreMutationAnalyzerAsync(
            repository,
            model,
            new ThrowingPreMutationAnalyzer());
        var plan = new ImplementationPlan
        {
            Summary = "Modify only the planned file.",
            Steps =
            [
                new ImplementationPlanStep
                {
                    StepId = StepId.New(),
                    Title = "Edit Example",
                    Description = "Keep the change non-destructive.",
                    FileIntents = ModifyIntents("src/Example.cs"),
                    ExpectedOutcome = "Only content changes.",
                },
            ],
        };

        var exception = await Assert.ThrowsAsync<MalformedModelOutputException>(() =>
            scenario.ProposeAsync(
                runId,
                new TaskSpecification("Change Example", []),
                plan));

        Assert.Contains("outside the approved plan", exception.Message, StringComparison.Ordinal);
        Assert.Equal("planned", await File.ReadAllTextAsync(repository.PathOf("src/Example.cs")));
        var commitException = await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            scenario.ExecuteAsync(
                proposal.MutationSetId,
                new MutationApproval
                {
                    Level = MutationApprovalLevel.EntireSet,
                }));
        Assert.Contains("not registered", commitException.Message, StringComparison.Ordinal);
    }

    /// <summary>Read trust permits review but mutation trust is mandatory at commit.</summary>
    [Fact]
    public static async Task TransactionalWorkspace_ReadTrustPreviewsButCannotCommit()
    {
        await using var repository = await TestRepository.CreateAsync(
            new Dictionary<string, string> { ["src/Example.cs"] = "old" },
            trustLevel: RepositoryTrustLevel.TrustedRead);
        await using var events = new DomainEventStream();
        await using var workspace = await TransactionalWorkspace.CreateAsync(repository.Baseline, events);
        var set = CreateMutationSet(
            repository,
            [CreateReplacement(repository, "src/Example.cs", 0, "old", "new")]);
        var staged = await workspace.StageAsync(set);

        var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            workspace.CommitAsync(
                set.MutationSetId,
                new MutationApproval
                {
                    Level = MutationApprovalLevel.EntireSet,
                    ApprovalId = staged.ApprovalId,
                }));

        Assert.Contains("requires TrustedMutation", exception.Message);
        Assert.Equal("old", await File.ReadAllTextAsync(repository.PathOf("src/Example.cs")));
    }

    /// <summary>Transactional baseline capture honors cancellation before reading repository content.</summary>
    [Fact]
    public static async Task TransactionalWorkspace_CreateAsync_HonorsCancellation()
    {
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string>
        {
            ["src/Example.cs"] = "old",
        });
        await using var events = new DomainEventStream();
        using var cancellationSource = new CancellationTokenSource();
        await cancellationSource.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TransactionalWorkspace.CreateAsync(
                repository.Baseline,
                events,
                cancellationToken: cancellationSource.Token));
    }

    /// <summary>Roslyn rename and syntax replacement emit correlated text mutations at full confidence.</summary>
    [Fact]
    public static async Task SemanticMutations_RenameAndSyntaxReplacement_EmitTransactionalPatches()
    {
        var fixture = Path.Combine(
            AppContext.BaseDirectory,
            "fixtures",
            "semantic",
            "SmallDotNetSolution");
        await using var repository = await TestRepository.CopyAsync(fixture);
        await using var events = new DomainEventStream();
        await using var engines = new SemanticEngineRegistry(
            events,
            NullLoggerFactory.Instance,
            TimeSpan.FromMilliseconds(250));
        var load = await engines.LoadAsync(new SemanticLoadRequest(
            repository.SessionId,
            repository.WorkspaceId,
            repository.Root,
            repository.PathOf("SmallDotNetSolution.sln"),
            RepositoryTrustLevel.TrustedBuild));
        Assert.Equal(SemanticConfidenceLevel.FullSemantic, load.Confidence);
        var symbol = Assert.Single(
            await engines.FindSymbolsAsync(repository.WorkspaceId, "IService"),
            item => item.Symbol.DisplayName.EndsWith("IService", StringComparison.Ordinal));
        var semanticMutations = new SemanticMutationEngine(engines);

        var rename = await semanticMutations.RenameSymbolAsync(new RenameSymbolMutationRequest
        {
            SessionId = repository.SessionId,
            RunId = RunId.New(),
            WorkspaceId = repository.WorkspaceId,
            Baseline = repository.Baseline,
            SymbolId = symbol.Symbol.Id,
            NewName = "IRenamedService",
            Rationale = "Verify semantic rename.",
        });

        Assert.Equal(SemanticConfidenceLevel.FullSemantic, rename.Confidence);
        Assert.True(rename.MutationSet.Mutations.Count >= 2);
        Assert.All(rename.MutationSet.Mutations, mutation =>
        {
            Assert.Equal(MutationType.RenameSymbol, mutation.Type);
            Assert.Equal(symbol.Symbol.Id, mutation.RelatedSymbolId);
            Assert.Contains("IRenamedService", mutation.ReplacementText);
        });
        await using var workspace = await TransactionalWorkspace.CreateAsync(repository.Baseline, events);
        var renameStage = await workspace.StageAsync(rename.MutationSet);
        Assert.False(renameStage.Conflicts.HasConflicts);
        Assert.Contains("IRenamedService", renameStage.Preview.UnifiedDiff);

        var sourcePath = "Contracts/Services.cs";
        var source = await File.ReadAllTextAsync(repository.PathOf(sourcePath));
        var literalOffset = source.IndexOf("\"value\"", StringComparison.Ordinal);
        var replacement = await semanticMutations.ReplaceSyntaxNodeAsync(
            new SyntaxReplacementMutationRequest
            {
                SessionId = repository.SessionId,
                RunId = RunId.New(),
                WorkspaceId = repository.WorkspaceId,
                Baseline = repository.Baseline,
                RelativePath = sourcePath,
                StartOffset = literalOffset,
                Length = "\"value\"".Length,
                ReplacementText = "\"changed\"",
                SymbolId = symbol.Symbol.Id,
                Rationale = "Verify bounded syntax replacement.",
            });
        var syntaxMutation = Assert.Single(replacement.MutationSet.Mutations);
        Assert.Equal(MutationType.ReplaceSyntaxNode, syntaxMutation.Type);
        Assert.Equal(symbol.Symbol.Id, syntaxMutation.RelatedSymbolId);
        Assert.Contains("\"changed\"", syntaxMutation.ReplacementText);
    }

    /// <summary>Semantic mutations fail closed below partial-compilation confidence.</summary>
    [Fact]
    public static async Task SemanticMutations_TextOnlyWorkspace_IsRejectedWithActionableMessage()
    {
        var fixture = Path.Combine(
            AppContext.BaseDirectory,
            "fixtures",
            "semantic",
            "SmallDotNetSolution");
        await using var repository = await TestRepository.CopyAsync(fixture);
        await using var events = new DomainEventStream();
        await using var engines = new SemanticEngineRegistry(events, NullLoggerFactory.Instance);
        var load = await engines.LoadAsync(new SemanticLoadRequest(
            repository.SessionId,
            repository.WorkspaceId,
            repository.Root,
            repository.PathOf("SmallDotNetSolution.sln"),
            RepositoryTrustLevel.TrustedRead));
        Assert.Equal(SemanticConfidenceLevel.TextOnly, load.Confidence);
        var semanticMutations = new SemanticMutationEngine(engines);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            semanticMutations.RenameSymbolAsync(new RenameSymbolMutationRequest
            {
                SessionId = repository.SessionId,
                WorkspaceId = repository.WorkspaceId,
                Baseline = repository.Baseline,
                SymbolId = "T:SmallSolution.Contracts.IService",
                NewName = "IRenamedService",
                Rationale = "Must fail below partial compilation.",
            }));

        Assert.Contains("require PartialCompilation", exception.Message);
        Assert.Contains("text patch", exception.Message);
    }

    /// <summary>Git worktree isolation creates and explicitly removes a detached workspace.</summary>
    [Fact]
    public static async Task GitWorktreeIsolation_CreateAndRemove_IsExplicitAndBounded()
    {
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string>
        {
            ["README.md"] = "baseline",
        });
        _ = await RunGitAsync(repository.Root, ["init"]);
        _ = await RunGitAsync(repository.Root, ["config", "user.email", "threadsmith@example.invalid"]);
        _ = await RunGitAsync(repository.Root, ["config", "user.name", "Threadsmith Tests"]);
        _ = await RunGitAsync(repository.Root, ["add", "README.md"]);
        _ = await RunGitAsync(repository.Root, ["commit", "-m", "baseline"]);
        var manager = new GitWorktreeManager();

        var isolation = await manager.CreateAsync(repository.Root);

        Assert.Equal(WorkspaceIsolationMode.GitWorktree, isolation.Mode);
        Assert.True(Directory.Exists(isolation.RepositoryPath));
        Assert.True(File.Exists(Path.Combine(isolation.RepositoryPath, "README.md")));
        await manager.RemoveAsync(repository.Root, isolation);
        Assert.False(Directory.Exists(isolation.RepositoryPath));
    }

    /// <summary>Every configured policy follows its documented approval decision matrix.</summary>
    [Fact]
    public static async Task MutationApprovalPolicy_RequiresApproval_UsesPolicyAndPlanScope()
    {
        var ordinary = new MutationRiskAssessment();
        var risky = new MutationRiskAssessment { HasDeletions = true };
        var service = new MutationApprovalPolicyService();

        Assert.True(service.RequiresApproval(ordinary, isWithinPlan: true));
        await service.SetPolicyAsync(MutationApprovalPolicy.ReviewRisky);
        Assert.False(service.RequiresApproval(ordinary, isWithinPlan: false));
        Assert.True(service.RequiresApproval(risky, isWithinPlan: true));
        await service.SetPolicyAsync(MutationApprovalPolicy.TrustPlan);
        Assert.False(service.RequiresApproval(risky, isWithinPlan: true));
        Assert.True(service.RequiresApproval(ordinary, isWithinPlan: false));
        await service.SetPolicyAsync(MutationApprovalPolicy.TrustSession);
        Assert.False(service.RequiresApproval(risky, isWithinPlan: false));
        await service.SetPolicyAsync(MutationApprovalPolicy.AlwaysTrustRepo);
        Assert.False(service.RequiresApproval(risky, isWithinPlan: false));
    }

    /// <summary>Risk calculation recognizes destructive, configuration, dependency, size, and confinement indicators.</summary>
    [Fact]
    public static async Task MutationRiskCalculator_ClassifiesExactMutationPreview()
    {
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string>
        {
            ["src/App.csproj"] = "<Project><ItemGroup><PackageReference Include=\"Example\" /></ItemGroup></Project>",
            ["src/Old.cs"] = "old",
        });
        var dependency = CreateReplacement(
            repository,
            "src/App.csproj",
            20,
            "PackageReference",
            "PackageReference");
        var deletion = new Mutation
        {
            MutationId = MutationId.New(),
            Type = MutationType.DeleteFile,
            RelativePath = "src/Old.cs",
            BaselineSha256 = repository.Baseline.Files.Single(file => file.RelativePath == "src/Old.cs").Sha256,
        };
        var set = CreateMutationSet(repository, [dependency, deletion]);
        var preview = new MutationPreview(set.MutationSetId, "diff", [], 300, 201);

        var risk = MutationRiskCalculator.Calculate(
            set,
            preview,
            repository.Root,
            largeDiffThreshold: 500);

        Assert.True(risk.HasDeletions);
        Assert.True(risk.HasConfigChanges);
        Assert.True(risk.HasDependencyChanges);
        Assert.True(risk.HasLargeDiff);
        Assert.False(risk.HasOutsideRepoChanges);
        Assert.Equal(2, risk.FileCount);
        Assert.Equal(501, risk.TotalLinesChanged);
    }

    /// <summary>Persistent repository trust round-trips and selecting another policy revokes it without losing config.</summary>
    [Fact]
    public static async Task MutationApprovalPolicy_AlwaysTrustRepo_PersistsAndRevokesAtomically()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-m5-policy-{Guid.NewGuid():N}");
        var configPath = Path.Combine(root, ".threadsmith", "config.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath) ?? root);
            await File.WriteAllTextAsync(configPath, "{\"unrelated\":{\"kept\":true}}");
            IConfiguration configuration = new ConfigurationBuilder().Build();
            var service = new MutationApprovalPolicyService(configuration, configPath);

            await service.SetPolicyAsync(MutationApprovalPolicy.AlwaysTrustRepo);

            using (var persisted = JsonDocument.Parse(await File.ReadAllTextAsync(configPath)))
            {
                Assert.True(persisted.RootElement.GetProperty("unrelated").GetProperty("kept").GetBoolean());
                Assert.Equal(
                    "alwaysTrustRepo",
                    persisted.RootElement.GetProperty("mutation").GetProperty("approvalPolicy").GetString());
            }

            IConfiguration reloaded = new ConfigurationBuilder().AddJsonFile(configPath).Build();
            var restored = new MutationApprovalPolicyService(reloaded, configPath);
            Assert.Equal(MutationApprovalPolicy.AlwaysTrustRepo, restored.CurrentPolicy);
            await restored.SetPolicyAsync(MutationApprovalPolicy.ReviewAll);
            using var revoked = JsonDocument.Parse(await File.ReadAllTextAsync(configPath));
            Assert.False(revoked.RootElement.TryGetProperty("mutation", out _));
            Assert.True(revoked.RootElement.GetProperty("unrelated").GetProperty("kept").GetBoolean());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>Repository-owned plan config cannot forge persistent plan trust without user-owned grant state.</summary>
    [Fact]
    public static async Task PlanApprovalPolicy_RepositoryConfigAlone_CannotGrantPersistentTrust()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-m5-plan-policy-forge-{Guid.NewGuid():N}");
        var configPath = Path.Combine(root, ".threadsmith", "config.json");
        var userTrustPath = Path.Combine(root, "user", "plan-policy-trust.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath) ?? root);
            var identity = CreatePlanPolicyRepositoryIdentity(root);
            await File.WriteAllTextAsync(
                configPath,
                $"{{\"planning\":{{\"approvalPolicy\":\"alwaysTrustRepo\",\"approvalRepositoryIdentity\":\"{identity}\"}}}}");
            IConfiguration configuration = new ConfigurationBuilder().AddJsonFile(configPath).Build();

            var service = new PlanApprovalPolicyService(configuration, configPath, userTrustPath);

            Assert.Equal(PlanApprovalPolicy.ReviewAll, service.CurrentPolicy);
            await service.BindRepositoryAsync(root);
            Assert.Equal(PlanApprovalPolicy.ReviewAll, service.CurrentPolicy);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>User-owned grant state cannot establish persistent plan trust without a repository marker.</summary>
    [Fact]
    public static async Task PlanApprovalPolicy_UserGrantAlone_CannotGrantPersistentTrust()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-m5-plan-policy-user-alone-{Guid.NewGuid():N}");
        var configPath = Path.Combine(root, ".threadsmith", "config.json");
        var userTrustPath = Path.Combine(root, "user", "plan-policy-trust.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath) ?? root);
            await File.WriteAllTextAsync(configPath, "{}");
            var grantStore = new UserPlanTrustGrantStore(userTrustPath);
            await grantStore.GrantAsync(CreatePlanPolicyRepositoryIdentity(root));
            IConfiguration configuration = new ConfigurationBuilder().AddJsonFile(configPath).Build();

            var service = new PlanApprovalPolicyService(configuration, configPath, userTrustPath);

            Assert.Equal(PlanApprovalPolicy.ReviewAll, service.CurrentPolicy);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>Persistent plan trust requires the repository marker to match the exact granted identity.</summary>
    [Fact]
    public static async Task PlanApprovalPolicy_MismatchedRepositoryIdentity_CannotGrantPersistentTrust()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-m5-plan-policy-mismatch-{Guid.NewGuid():N}");
        var configPath = Path.Combine(root, ".threadsmith", "config.json");
        var userTrustPath = Path.Combine(root, "user", "plan-policy-trust.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath) ?? root);
            await File.WriteAllTextAsync(
                configPath,
                "{\"planning\":{\"approvalPolicy\":\"alwaysTrustRepo\",\"approvalRepositoryIdentity\":\"different\"}}");
            var grantStore = new UserPlanTrustGrantStore(userTrustPath);
            await grantStore.GrantAsync(CreatePlanPolicyRepositoryIdentity(root));
            IConfiguration configuration = new ConfigurationBuilder().AddJsonFile(configPath).Build();

            var service = new PlanApprovalPolicyService(configuration, configPath, userTrustPath);

            Assert.Equal(PlanApprovalPolicy.ReviewAll, service.CurrentPolicy);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>Repository configuration cannot select the session-only plan trust mode.</summary>
    [Fact]
    public static async Task PlanApprovalPolicy_RepositoryTrustSession_FallsBackToTrustedPolicy()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-m5-plan-policy-session-forge-{Guid.NewGuid():N}");
        var configPath = Path.Combine(root, ".threadsmith", "config.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath) ?? root);
            await File.WriteAllTextAsync(
                configPath,
                "{\"planning\":{\"approvalPolicy\":\"trustSession\"}}");
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["planning:approvalPolicy"] = "reviewAll",
                })
                .AddJsonFile(configPath)
                .Build();

            var service = new PlanApprovalPolicyService(configuration, configPath);

            Assert.Equal(PlanApprovalPolicy.ReviewAll, service.CurrentPolicy);
            var decision = service.Decide(
                new PlanSanityCheckResult { Risk = PlanRiskClassification.Moderate },
                RepositoryTrustLevel.UntrustedInspection);
            Assert.Equal(PlanApprovalDecisionKind.RequiresReview, decision.Kind);

            IConfiguration flattened = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["planning:approvalPolicy"] = "trustSession",
                })
                .Build();
            var flattenedService = new PlanApprovalPolicyService(flattened, configPath);
            Assert.Equal(PlanApprovalPolicy.ReviewAll, flattenedService.CurrentPolicy);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>Repository configuration can select the strongest explicit persisted plan policy.</summary>
    [Fact]
    public static void PlanApprovalPolicy_OrdinaryConfigCanSelectAutoApproveAllValid()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["planning:approvalPolicy"] = "autoApproveAllValid",
            })
            .Build();

        var service = new PlanApprovalPolicyService(configuration);

        Assert.Equal(PlanApprovalPolicy.AutoApproveAllValid, service.CurrentPolicy);
    }

    /// <summary>User-owned plan trust restores for the startup repository and survives a same-repository bind.</summary>
    [Fact]
    public static async Task PlanApprovalPolicy_AlwaysTrustRepo_RestoresFromUserOwnedGrant()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-m5-plan-policy-{Guid.NewGuid():N}");
        var configPath = Path.Combine(root, ".threadsmith", "config.json");
        var userTrustPath = Path.Combine(root, "user", "plan-policy-trust.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath) ?? root);
            IConfiguration configuration = new ConfigurationBuilder().Build();
            var service = new PlanApprovalPolicyService(configuration, configPath, userTrustPath);

            await service.SetPolicyAsync(PlanApprovalPolicy.AlwaysTrustRepo);

            IConfiguration reloaded = new ConfigurationBuilder().AddJsonFile(configPath).Build();
            var restored = new PlanApprovalPolicyService(reloaded, configPath, userTrustPath);
            Assert.Equal(PlanApprovalPolicy.AlwaysTrustRepo, restored.CurrentPolicy);
            await restored.BindRepositoryAsync(root);
            Assert.Equal(PlanApprovalPolicy.AlwaysTrustRepo, restored.CurrentPolicy);
            await restored.SetPolicyAsync(PlanApprovalPolicy.ReviewAll);
            IConfiguration revoked = new ConfigurationBuilder().AddJsonFile(configPath, optional: true).Build();
            Assert.Equal("reviewAll", revoked["planning:approvalPolicy"]);
            Assert.Null(revoked["planning:approvalRepositoryIdentity"]);
            Assert.False(await HasPlanPolicyGrantAsync(userTrustPath, CreatePlanPolicyRepositoryIdentity(root)));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>ReviewRisky persists as a safe repository default without user-owned trust state.</summary>
    [Fact]
    public static async Task PlanApprovalPolicy_ReviewRisky_PersistsAsRepositoryDefault()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-m5-plan-policy-review-risky-{Guid.NewGuid():N}");
        var configPath = Path.Combine(root, ".threadsmith", "config.json");
        var userTrustPath = Path.Combine(root, "user", "plan-policy-trust.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath) ?? root);
            IConfiguration configuration = new ConfigurationBuilder().Build();
            var service = new PlanApprovalPolicyService(configuration, configPath, userTrustPath);

            await service.SetPolicyAsync(PlanApprovalPolicy.ReviewRisky);

            IConfiguration reloaded = new ConfigurationBuilder().AddJsonFile(configPath).Build();
            var restored = new PlanApprovalPolicyService(reloaded, configPath, userTrustPath);
            Assert.Equal(PlanApprovalPolicy.ReviewRisky, restored.CurrentPolicy);
            Assert.Equal("reviewRisky", reloaded["planning:approvalPolicy"]);
            Assert.Null(reloaded["planning:approvalRepositoryIdentity"]);
            Assert.False(File.Exists(userTrustPath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>AutoApproveAllValid persists as an explicit repository policy without user-owned trust state.</summary>
    [Fact]
    public static async Task PlanApprovalPolicy_AutoApproveAllValid_PersistsAsRepositoryDefault()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-m5-plan-policy-auto-all-{Guid.NewGuid():N}");
        var configPath = Path.Combine(root, ".threadsmith", "config.json");
        var userTrustPath = Path.Combine(root, "user", "plan-policy-trust.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath) ?? root);
            IConfiguration configuration = new ConfigurationBuilder().Build();
            var service = new PlanApprovalPolicyService(configuration, configPath, userTrustPath);

            await service.SetPolicyAsync(PlanApprovalPolicy.AutoApproveAllValid);

            IConfiguration reloaded = new ConfigurationBuilder().AddJsonFile(configPath).Build();
            var restored = new PlanApprovalPolicyService(reloaded, configPath, userTrustPath);
            Assert.Equal(PlanApprovalPolicy.AutoApproveAllValid, restored.CurrentPolicy);
            Assert.Equal("autoApproveAllValid", reloaded["planning:approvalPolicy"]);
            Assert.Null(reloaded["planning:approvalRepositoryIdentity"]);
            Assert.False(File.Exists(userTrustPath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>Repository plan-policy storage owns marker persistence while preserving unrelated JSON.</summary>
    [Fact]
    public static async Task PlanApprovalRepositoryPolicyStore_PreservesUnrelatedJsonAndIdentityMarker()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-m5-plan-policy-store-{Guid.NewGuid():N}");
        var configPath = Path.Combine(root, ".threadsmith", "config.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath) ?? root);
            await File.WriteAllTextAsync(
                configPath,
                "{\"unrelated\":{\"kept\":true},\"planning\":{\"note\":\"keep\",\"approvalRepositoryIdentity\":\"stale\"}}");
            var binding = PlanApprovalRepositoryBinding.CreateFromRepositoryRoot(root);
            var store = new RepositoryPlanApprovalPolicyStore();

            await store.WritePolicyAsync(binding, PlanApprovalPolicy.AlwaysTrustRepo);

            using (var trusted = JsonDocument.Parse(await File.ReadAllTextAsync(configPath)))
            {
                var planning = trusted.RootElement.GetProperty("planning");
                Assert.True(trusted.RootElement.GetProperty("unrelated").GetProperty("kept").GetBoolean());
                Assert.Equal("keep", planning.GetProperty("note").GetString());
                Assert.Equal("alwaysTrustRepo", planning.GetProperty("approvalPolicy").GetString());
                Assert.Equal(binding.RepositoryIdentity, planning.GetProperty("approvalRepositoryIdentity").GetString());
            }

            await store.WritePolicyAsync(binding, PlanApprovalPolicy.ReviewRisky);

            using var downgraded = JsonDocument.Parse(await File.ReadAllTextAsync(configPath));
            var downgradedPlanning = downgraded.RootElement.GetProperty("planning");
            Assert.Equal("reviewRisky", downgradedPlanning.GetProperty("approvalPolicy").GetString());
            Assert.Equal("keep", downgradedPlanning.GetProperty("note").GetString());
            Assert.False(downgradedPlanning.TryGetProperty("approvalRepositoryIdentity", out _));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>User-owned plan trust storage keeps exact identity grants separate from repository markers.</summary>
    [Fact]
    public static async Task UserPlanTrustGrantStore_ExactGrantRevocationPreservesUnrelatedData()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-m5-plan-trust-store-{Guid.NewGuid():N}");
        var trustPath = Path.Combine(root, "user", "plan-policy-trust.json");
        string identity = new('a', 64);
        string otherIdentity = new('b', 64);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(trustPath) ?? root);
            await File.WriteAllTextAsync(
                trustPath,
                $"{{\"theme\":\"dark\",\"trustedRepositories\":{{\"{otherIdentity}\":true}}}}");
            var store = new UserPlanTrustGrantStore(trustPath);

            await store.GrantAsync(identity);

            Assert.True(store.IsGranted(identity));
            Assert.False(store.IsGranted(identity.ToUpperInvariant()));
            using (var granted = JsonDocument.Parse(await File.ReadAllTextAsync(trustPath)))
            {
                Assert.Equal("dark", granted.RootElement.GetProperty("theme").GetString());
                var repositories = granted.RootElement.GetProperty("trustedRepositories");
                Assert.True(repositories.GetProperty(identity).GetBoolean());
                Assert.True(repositories.GetProperty(otherIdentity).GetBoolean());
            }

            await store.RevokeAsync(identity);
            await store.RevokeAsync(otherIdentity);

            using var revoked = JsonDocument.Parse(await File.ReadAllTextAsync(trustPath));
            Assert.Equal("dark", revoked.RootElement.GetProperty("theme").GetString());
            Assert.False(revoked.RootElement.TryGetProperty("trustedRepositories", out _));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>Malformed user-owned trust data fails closed instead of authorizing repository trust.</summary>
    [Fact]
    public static async Task UserPlanTrustGrantStore_MalformedTrustDataFailsClosed()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-m5-plan-trust-malformed-{Guid.NewGuid():N}");
        var trustPath = Path.Combine(root, "user", "plan-policy-trust.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(trustPath) ?? root);
            await File.WriteAllTextAsync(trustPath, "{\"trustedRepositories\":{\"repo\":\"true\"}}");
            var store = new UserPlanTrustGrantStore(trustPath);

            Assert.Throws<InvalidOperationException>(() => store.IsGranted("repo"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.GrantAsync("other"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>Persistent trust persistence compensates user grants when repository marker writes fail.</summary>
    [Fact]
    public static async Task PlanApprovalPolicyPersistence_RepositoryFailureRevokesNewTrustGrant()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-m5-plan-policy-protocol-fail-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            var binding = PlanApprovalRepositoryBinding.CreateFromRepositoryRoot(root);
            var operations = new List<string>();
            var repositoryStore = new RecordingPlanApprovalRepositoryStore(operations)
            {
                Failure = new IOException("repository marker failed"),
            };
            var trustStore = new RecordingPlanTrustGrantStore(operations);
            var persistence = new PlanApprovalPolicyPersistence(repositoryStore, trustStore);

            var exception = await Assert.ThrowsAnyAsync<Exception>(
                () => persistence.PersistAsync(binding, PlanApprovalPolicy.AlwaysTrustRepo));

            Assert.True(exception is IOException or AggregateException);
            Assert.Equal(["grant:false", "write:AlwaysTrustRepo:false", "revoke:false"], operations);
            Assert.False(trustStore.IsGranted(binding.RepositoryIdentity));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>Policy downgrades revoke user-owned trust before writing the weaker repository marker.</summary>
    [Fact]
    public static async Task PlanApprovalPolicyPersistence_DowngradeRevokesBeforeRepositoryWrite()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-m5-plan-policy-protocol-downgrade-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            var binding = PlanApprovalRepositoryBinding.CreateFromRepositoryRoot(root);
            var operations = new List<string>();
            var repositoryStore = new RecordingPlanApprovalRepositoryStore(operations);
            var trustStore = new RecordingPlanTrustGrantStore(operations);
            await trustStore.GrantAsync(binding.RepositoryIdentity);
            operations.Clear();
            var persistence = new PlanApprovalPolicyPersistence(repositoryStore, trustStore);

            await persistence.PersistAsync(binding, PlanApprovalPolicy.ReviewAll);

            Assert.Equal(["revoke:false", "write:ReviewAll:false"], operations);
            Assert.False(trustStore.IsGranted(binding.RepositoryIdentity));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>TrustSession is session-only and does not rewrite repository policy configuration.</summary>
    [Fact]
    public static async Task PlanApprovalPolicy_TrustSession_DoesNotRewriteRepositoryPolicy()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-m5-plan-policy-session-{Guid.NewGuid():N}");
        var configPath = Path.Combine(root, ".threadsmith", "config.json");
        var userTrustPath = Path.Combine(root, "user", "plan-policy-trust.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath) ?? root);
            await File.WriteAllTextAsync(
                configPath,
                "{\"planning\":{\"approvalPolicy\":\"reviewRisky\",\"approvalRepositoryIdentity\":\"stale\",\"note\":\"keep\"}}");
            IConfiguration configuration = new ConfigurationBuilder().AddJsonFile(configPath).Build();
            var service = new PlanApprovalPolicyService(configuration, configPath, userTrustPath);

            await service.SetPolicyAsync(PlanApprovalPolicy.TrustSession);

            Assert.Equal(PlanApprovalPolicy.TrustSession, service.CurrentPolicy);
            IConfiguration updated = new ConfigurationBuilder().AddJsonFile(configPath).Build();
            Assert.Equal("reviewRisky", updated["planning:approvalPolicy"]);
            Assert.Equal("stale", updated["planning:approvalRepositoryIdentity"]);
            Assert.Equal("keep", updated["planning:note"]);
            Assert.False(File.Exists(userTrustPath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>Reset revokes dormant persistent trust hidden by a higher-precedence session policy.</summary>
    [Fact]
    public static async Task PlanApprovalPolicy_Reset_RevokesDormantPersistentTrust()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-m5-plan-policy-dormant-{Guid.NewGuid():N}");
        var configPath = Path.Combine(root, ".threadsmith", "config.json");
        var userTrustPath = Path.Combine(root, "user", "plan-policy-trust.json");
        var identity = CreatePlanPolicyRepositoryIdentity(root);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath) ?? root);
            var initial = new PlanApprovalPolicyService(
                new ConfigurationBuilder().Build(),
                configPath,
                userTrustPath);
            await initial.SetPolicyAsync(PlanApprovalPolicy.AlwaysTrustRepo);
            IConfiguration overriddenConfiguration = new ConfigurationBuilder()
                .AddJsonFile(configPath)
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["planning:approvalPolicy"] = "reviewRisky",
                })
                .Build();
            var overridden = new PlanApprovalPolicyService(
                overriddenConfiguration,
                configPath,
                userTrustPath);
            Assert.Equal(PlanApprovalPolicy.ReviewRisky, overridden.CurrentPolicy);

            await overridden.SetPolicyAsync(PlanApprovalPolicy.ReviewAll);

            IConfiguration revoked = new ConfigurationBuilder().AddJsonFile(configPath).Build();
            Assert.Equal("reviewAll", revoked["planning:approvalPolicy"]);
            Assert.Null(revoked["planning:approvalRepositoryIdentity"]);
            Assert.False(await HasPlanPolicyGrantAsync(userTrustPath, identity));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>Marker removal failure cannot leave the activating user grant behind.</summary>
    [Fact]
    public static async Task PlanApprovalPolicy_RevocationMarkerFailure_RemovesGrantBeforeMarker()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-m5-plan-policy-revoke-fail-{Guid.NewGuid():N}");
        var configPath = Path.Combine(root, ".threadsmith", "config.json");
        var userTrustPath = Path.Combine(root, "user", "plan-policy-trust.json");
        var identity = CreatePlanPolicyRepositoryIdentity(root);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath) ?? root);
            var service = new PlanApprovalPolicyService(
                new ConfigurationBuilder().Build(),
                configPath,
                userTrustPath);
            await service.SetPolicyAsync(PlanApprovalPolicy.AlwaysTrustRepo);
            Assert.True(await HasPlanPolicyGrantAsync(userTrustPath, identity));
            File.SetAttributes(configPath, File.GetAttributes(configPath) | FileAttributes.ReadOnly);

            var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
                service.SetPolicyAsync(PlanApprovalPolicy.ReviewAll));

            Assert.True(exception is IOException or UnauthorizedAccessException);
            Assert.False(await HasPlanPolicyGrantAsync(userTrustPath, identity));
        }
        finally
        {
            if (File.Exists(configPath))
            {
                File.SetAttributes(configPath, FileAttributes.Normal);
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>A failed repository marker write rolls back the user-owned persistent plan trust grant.</summary>
    [Fact]
    public static async Task PlanApprovalPolicy_AlwaysTrustRepoRepositoryWriteFailure_RollsBackUserGrant()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-m5-plan-policy-fail-{Guid.NewGuid():N}");
        var configPath = Path.Combine(root, ".threadsmith", "config.json");
        var userTrustPath = Path.Combine(root, "user", "plan-policy-trust.json");
        var identity = CreatePlanPolicyRepositoryIdentity(root);
        try
        {
            Directory.CreateDirectory(configPath);
            IConfiguration configuration = new ConfigurationBuilder().Build();
            var service = new PlanApprovalPolicyService(configuration, configPath, userTrustPath);

            var exception = await Assert.ThrowsAnyAsync<Exception>(
                () => service.SetPolicyAsync(PlanApprovalPolicy.AlwaysTrustRepo));

            Assert.True(exception is IOException or UnauthorizedAccessException);
            Assert.Equal(PlanApprovalPolicy.ReviewAll, service.CurrentPolicy);
            Assert.False(await HasPlanPolicyGrantAsync(userTrustPath, identity));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>Repository rebinding falls back to lower-precedence user policy when the new repository omits it.</summary>
    [Fact]
    public static async Task PlanApprovalPolicy_BindRepository_RetainsUserLayerWhenRepositoryOmitsPolicy()
    {
        var firstRoot = Path.Combine(Path.GetTempPath(), $"threadsmith-m5-plan-policy-first-{Guid.NewGuid():N}");
        var secondRoot = Path.Combine(Path.GetTempPath(), $"threadsmith-m5-plan-policy-second-{Guid.NewGuid():N}");
        var firstConfigPath = Path.Combine(firstRoot, ".threadsmith", "config.json");
        var secondConfigPath = Path.Combine(secondRoot, ".threadsmith", "config.json");
        var userTrustPath = Path.Combine(firstRoot, "user", "plan-policy-trust.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(firstConfigPath) ?? firstRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(secondConfigPath) ?? secondRoot);
            await File.WriteAllTextAsync(secondConfigPath, "{}");
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["planning:approvalPolicy"] = "reviewRisky",
                })
                .AddJsonFile(firstConfigPath, optional: true)
                .Build();
            var service = new PlanApprovalPolicyService(configuration, firstConfigPath, userTrustPath);

            await service.BindRepositoryAsync(secondRoot);

            Assert.Equal(PlanApprovalPolicy.ReviewRisky, service.CurrentPolicy);
        }
        finally
        {
            if (Directory.Exists(firstRoot))
            {
                Directory.Delete(firstRoot, recursive: true);
            }

            if (Directory.Exists(secondRoot))
            {
                Directory.Delete(secondRoot, recursive: true);
            }
        }
    }

    /// <summary>Repository rebinding replaces only the repository plan-policy provider.</summary>
    [Fact]
    public static async Task PlanApprovalPolicy_BindRepository_RetainsNonRepositoryLayers()
    {
        var firstRoot = Path.Combine(Path.GetTempPath(), $"threadsmith-m5-plan-policy-first-{Guid.NewGuid():N}");
        var secondRoot = Path.Combine(Path.GetTempPath(), $"threadsmith-m5-plan-policy-second-{Guid.NewGuid():N}");
        var firstConfigPath = Path.Combine(firstRoot, ".threadsmith", "config.json");
        var secondConfigPath = Path.Combine(secondRoot, ".threadsmith", "config.json");
        var userTrustPath = Path.Combine(firstRoot, "user", "plan-policy-trust.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(firstConfigPath) ?? firstRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(secondConfigPath) ?? secondRoot);
            await File.WriteAllTextAsync(secondConfigPath, "{\"planning\":{\"approvalPolicy\":\"reviewAll\"}}");
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["planning:approvalPolicy"] = "reviewRisky",
                })
                .AddJsonFile(firstConfigPath, optional: true)
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["planning:approvalPolicy"] = "trustSession",
                })
                .Build();
            var service = new PlanApprovalPolicyService(configuration, firstConfigPath, userTrustPath);

            await service.BindRepositoryAsync(secondRoot);

            Assert.Equal(PlanApprovalPolicy.TrustSession, service.CurrentPolicy);
        }
        finally
        {
            if (Directory.Exists(firstRoot))
            {
                Directory.Delete(firstRoot, recursive: true);
            }

            if (Directory.Exists(secondRoot))
            {
                Directory.Delete(secondRoot, recursive: true);
            }
        }
    }

    /// <summary>Command-bound plan policy changes publish durable provenance for headless callers.</summary>
    [Fact]
    public static async Task PlanApprovalPolicy_CommandSetter_PublishesPolicyChangedEvent()
    {
        await using var events = new DomainEventStream();
        var observed = new List<IDomainEvent>();
        await using var subscription = events.Subscribe((domainEvent, _) =>
        {
            observed.Add(domainEvent);
            return Task.CompletedTask;
        });
        var service = new PlanApprovalPolicyService(events: events);
        var sessionId = SessionId.New();

        var policy = await service.HandleAsync(new SetPlanApprovalPolicyCommand(
            PlanApprovalPolicy.ReviewRisky,
            sessionId,
            "headless"));

        Assert.Equal(PlanApprovalPolicy.ReviewRisky, policy);
        var changed = Assert.Single(observed.OfType<PlanApprovalPolicyChanged>());
        Assert.Equal(sessionId, changed.SessionId);
        Assert.Equal(PlanApprovalPolicy.ReviewRisky, changed.Policy);
        Assert.Equal("headless", changed.Scope);
    }

    /// <summary>Revocation finds configuration keys case-insensitively and preserves their original spelling.</summary>
    [Fact]
    public static async Task MutationApprovalPolicy_Revocation_HandlesDifferentlyCasedKeys()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-m5-policy-case-{Guid.NewGuid():N}");
        var configPath = Path.Combine(root, ".threadsmith", "config.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath) ?? root);
            await File.WriteAllTextAsync(
                configPath,
                "{\"Mutation\":{\"ApprovalPolicy\":\"alwaysTrustRepo\",\"LargeDiffThreshold\":42}}");
            IConfiguration configuration = new ConfigurationBuilder().AddJsonFile(configPath).Build();
            var service = new MutationApprovalPolicyService(configuration, configPath);

            Assert.Equal(MutationApprovalPolicy.AlwaysTrustRepo, service.CurrentPolicy);
            await service.SetPolicyAsync(MutationApprovalPolicy.ReviewAll);

            using var revoked = JsonDocument.Parse(await File.ReadAllTextAsync(configPath));
            var mutation = revoked.RootElement.GetProperty("Mutation");
            Assert.False(mutation.TryGetProperty("ApprovalPolicy", out _));
            Assert.Equal(42, mutation.GetProperty("LargeDiffThreshold").GetInt32());
            Assert.False(revoked.RootElement.TryGetProperty("mutation", out _));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>A proposed event is observable only after its exact staged review is registered.</summary>
    [Fact]
    public static async Task TransactionalWorkspaceCoordinator_MutationSetProposed_ExposesRegisteredReview()
    {
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string>
        {
            ["src/Example.cs"] = "old",
        });
        await using var events = new DomainEventStream();
        await using var coordinator = new TransactionalWorkspaceCoordinator(events);
        await coordinator.RegisterBaselineAsync(repository.Baseline);
        StagedMutationSet? observedReview = null;
        ApprovalRequested? observedApproval = null;
        await using var subscription = events.Subscribe(async (domainEvent, cancellationToken) =>
        {
            if (domainEvent is MutationSetProposed proposed)
            {
                observedReview = await coordinator.HandleAsync(
                    new GetMutationReviewCommand(proposed.SessionId, proposed.MutationSetId),
                    cancellationToken);
            }
            else if (domainEvent is ApprovalRequested requested)
            {
                observedApproval = requested;
            }
        });
        var set = CreateMutationSet(
            repository,
            [CreateReplacement(repository, "src/Example.cs", 0, "old", "new")]);

        var staged = await coordinator.StageAsync(set);

        Assert.NotNull(observedReview);
        Assert.Equal(staged, observedReview);
        Assert.NotNull(observedApproval);
        Assert.Equal(ApprovalRequestKind.MutationSet, observedApproval.Kind);
        Assert.Equal(2, observedApproval.SchemaVersion);
    }

    /// <summary>Policy-approved ordinary edits skip approval events but retain preview, trust, and commit guardrails.</summary>
    [Fact]
    public static async Task TransactionalWorkspace_ReviewRisky_AutoApprovesOrdinaryEditOnly()
    {
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string>
        {
            ["src/Example.cs"] = "old",
        });
        var service = new MutationApprovalPolicyService();
        await service.SetPolicyAsync(MutationApprovalPolicy.ReviewRisky);
        await using var events = new DomainEventStream();
        var observed = new ConcurrentQueue<IDomainEvent>();
        await using var subscription = events.Subscribe((domainEvent, _) =>
        {
            observed.Enqueue(domainEvent);
            return Task.CompletedTask;
        });
        await using var workspace = await TransactionalWorkspace.CreateAsync(
            repository.Baseline,
            events,
            mutationApprovalPolicy: service);
        var set = CreateMutationSet(
            repository,
            [CreateReplacement(repository, "src/Example.cs", 0, "old", "new")]);

        var staged = await workspace.StageAsync(set);

        Assert.Equal(MutationApprovalLevel.PolicyAutoApproved, staged.MutationSet.RequiredApproval);
        Assert.Contains(observed, domainEvent => domainEvent is MutationSetProposed proposed
            && !string.IsNullOrWhiteSpace(proposed.Preview?.UnifiedDiff));
        Assert.DoesNotContain(observed, domainEvent => domainEvent is ApprovalRequested);
        _ = await workspace.CommitAsync(
            set.MutationSetId,
            new MutationApproval
            {
                Level = MutationApprovalLevel.PolicyAutoApproved,
                ApprovalId = staged.ApprovalId,
            });
        Assert.Equal("new", await File.ReadAllTextAsync(repository.PathOf("src/Example.cs")));

        var gitMetadataMutation = new Mutation
        {
            MutationId = MutationId.New(),
            Type = MutationType.CreateFile,
            RelativePath = ".git/config",
            ReplacementText = "destructive",
        };
        var gitMetadata = CreateMutationSet(repository, [gitMetadataMutation]);
        await Assert.ThrowsAsync<MutationPolicyException>(() => workspace.StageAsync(gitMetadata));
    }

    private sealed class MutationScenario : IAsyncDisposable
    {
        private readonly MutationProposalApplication _application;
        private readonly DomainEventStream _events;
        private readonly IAsyncDisposable _eventSubscription;
        private readonly TransactionalWorkspaceCoordinator? _ownedWorkspaces;
        private readonly SessionId _sessionId;
        private readonly WorkspaceId _workspaceId;
        private readonly ITransactionalWorkspaceResolver _workspaces;
        private readonly ConcurrentQueue<IDomainEvent> _observedEvents;

        private MutationScenario(
            SessionId sessionId,
            WorkspaceId workspaceId,
            MutationProposalApplication application,
            RecordingContextAssembler context,
            DomainEventStream events,
            IAsyncDisposable eventSubscription,
            ConcurrentQueue<IDomainEvent> observedEvents,
            ITransactionalWorkspaceResolver workspaces,
            TransactionalWorkspaceCoordinator? ownedWorkspaces)
        {
            _sessionId = sessionId;
            _workspaceId = workspaceId;
            _application = application;
            ContextRequests = context.Requests;
            _events = events;
            _eventSubscription = eventSubscription;
            _observedEvents = observedEvents;
            _workspaces = workspaces;
            _ownedWorkspaces = ownedWorkspaces;
        }

        public IReadOnlyList<ContextAssemblyRequest> ContextRequests { get; }

        public static Task<MutationScenario> CreateAsync(
            TestRepository repository,
            IModelProvider model)
        {
            return CreateOwnedAsync(
                repository,
                model,
                new PassthroughSanitizer(),
                semanticMutations: null,
                preMutationAnalyzer: null,
                limits: null);
        }

        public static Task<MutationScenario> CreateForCaseSensitiveWorkspaceAsync(
            SessionId sessionId,
            WorkspaceBaseline baseline,
            IModelProvider model,
            FakeTransactionalWorkspaceResolver workspaceResolver,
            FakePreMutationAnalyzer preMutationAnalyzer)
        {
            ArgumentNullException.ThrowIfNull(baseline);
            ArgumentNullException.ThrowIfNull(model);
            ArgumentNullException.ThrowIfNull(workspaceResolver);
            ArgumentNullException.ThrowIfNull(preMutationAnalyzer);
            return CreateCoreAsync(
                sessionId,
                baseline,
                model,
                workspaceResolver,
                semanticMutations: null,
                preMutationAnalyzer,
                new PassthroughSanitizer(),
                limits: null);
        }

        public static Task<MutationScenario> CreateWithSemanticMutationEngineAsync(
            TestRepository repository,
            IModelProvider model,
            FakeSemanticMutationEngine semanticMutations)
        {
            ArgumentNullException.ThrowIfNull(semanticMutations);
            return CreateOwnedAsync(
                repository,
                model,
                new PassthroughSanitizer(),
                semanticMutations,
                preMutationAnalyzer: null,
                limits: null);
        }

        public static Task<MutationScenario> CreateWithPreMutationAnalyzerAsync(
            TestRepository repository,
            IModelProvider model,
            IPreMutationAnalyzer preMutationAnalyzer)
        {
            ArgumentNullException.ThrowIfNull(preMutationAnalyzer);
            return CreateOwnedAsync(
                repository,
                model,
                new PassthroughSanitizer(),
                semanticMutations: null,
                preMutationAnalyzer,
                limits: null);
        }

        public static Task<MutationScenario> CreateWithLimitsAsync(
            TestRepository repository,
            IModelProvider model,
            ExecutionLimits limits)
        {
            ArgumentNullException.ThrowIfNull(limits);
            return CreateOwnedAsync(
                repository,
                model,
                new PassthroughSanitizer(),
                semanticMutations: null,
                preMutationAnalyzer: null,
                limits);
        }

        public static Task<MutationScenario> CreateWithSanitizerAsync(
            TestRepository repository,
            IModelProvider model,
            IOutputSanitizer sanitizer)
        {
            ArgumentNullException.ThrowIfNull(sanitizer);
            return CreateOwnedAsync(
                repository,
                model,
                sanitizer,
                semanticMutations: null,
                preMutationAnalyzer: null,
                limits: null);
        }

        public Task<StagedMutationSet> ProposeAsync(
            RunId runId,
            TaskSpecification task,
            ImplementationPlan plan,
            RunPhase phase = RunPhase.MutationPreparation,
            CancellationToken cancellationToken = default)
        {
            return _application.HandleAsync(
                new ProposeMutationSetCommand(
                    _sessionId,
                    runId,
                    _workspaceId,
                    task,
                    plan,
                    phase),
                cancellationToken);
        }

        public Task<MutationCommitResult> ExecuteAsync(
            MutationSetId mutationSetId,
            MutationApproval approval,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(approval);
            if (_ownedWorkspaces is not null)
            {
                return _ownedWorkspaces.HandleAsync(
                    new CommitMutationSetCommand(_sessionId, mutationSetId, approval),
                    cancellationToken);
            }

            return _workspaces.GetWorkspace(_workspaceId).CommitAsync(
                mutationSetId,
                approval,
                cancellationToken);
        }

        public IReadOnlyList<TEvent> Events<TEvent>()
            where TEvent : IDomainEvent
        {
            return [.. _observedEvents.OfType<TEvent>()];
        }

        public async ValueTask DisposeAsync()
        {
            await _eventSubscription.DisposeAsync();
            if (_ownedWorkspaces is not null)
            {
                await _ownedWorkspaces.DisposeAsync();
            }

            await _events.DisposeAsync();
        }

        private static Task<MutationScenario> CreateOwnedAsync(
            TestRepository repository,
            IModelProvider model,
            IOutputSanitizer sanitizer,
            ISemanticMutationEngine? semanticMutations,
            IPreMutationAnalyzer? preMutationAnalyzer,
            ExecutionLimits? limits = null)
        {
            ArgumentNullException.ThrowIfNull(repository);
            return CreateCoreAsync(
                repository.SessionId,
                repository.Baseline,
                model,
                workspaceResolver: null,
                semanticMutations,
                preMutationAnalyzer,
                sanitizer,
                limits);
        }

        private static async Task<MutationScenario> CreateCoreAsync(
            SessionId sessionId,
            WorkspaceBaseline baseline,
            IModelProvider model,
            ITransactionalWorkspaceResolver? workspaceResolver,
            ISemanticMutationEngine? semanticMutations,
            IPreMutationAnalyzer? preMutationAnalyzer,
            IOutputSanitizer sanitizer,
            ExecutionLimits? limits)
        {
            ArgumentNullException.ThrowIfNull(baseline);
            ArgumentNullException.ThrowIfNull(model);
            var events = new DomainEventStream();
            var observedEvents = new ConcurrentQueue<IDomainEvent>();
            IAsyncDisposable? eventSubscription = null;
            TransactionalWorkspaceCoordinator? ownedWorkspaces = null;
            try
            {
                eventSubscription = events.Subscribe((domainEvent, _) =>
                {
                    observedEvents.Enqueue(domainEvent);
                    return Task.CompletedTask;
                });
                ownedWorkspaces = workspaceResolver is null
                    ? new TransactionalWorkspaceCoordinator(events)
                    : null;
                var workspaces = workspaceResolver ?? ownedWorkspaces
                    ?? throw new InvalidOperationException("A mutation workspace resolver is required.");
                if (ownedWorkspaces is not null)
                {
                    await ownedWorkspaces.RegisterBaselineAsync(baseline);
                }

                var context = new RecordingContextAssembler(new ContextAssembler(
                    new EvidenceStore(events, sanitizer),
                    new TokenEstimator(),
                    new ContextPolicy(),
                    new PromptAppendLoader(sanitizer),
                    sanitizer,
                    events,
                    TestPromptLoader.Instance,
                    new ContextAssemblerOptions()));
                var application = new MutationProposalApplication(
                    model,
                    context,
                    workspaces,
                    new ExecutionBudget(new BudgetDimensions(
                        10_000,
                        10,
                        TimeSpan.FromMinutes(1),
                        1)),
                    sanitizer,
                    events,
                    semanticMutations: semanticMutations,
                    preMutationAnalyzer: preMutationAnalyzer,
                    limits: limits,
                    correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
                    prompts: TestPromptLoader.Instance);
                return new MutationScenario(
                    sessionId,
                    baseline.WorkspaceId,
                    application,
                    context,
                    events,
                    eventSubscription,
                    observedEvents,
                    workspaces,
                    ownedWorkspaces);
            }
            catch
            {
                if (eventSubscription is not null)
                {
                    await eventSubscription.DisposeAsync();
                }

                if (ownedWorkspaces is not null)
                {
                    await ownedWorkspaces.DisposeAsync();
                }

                await events.DisposeAsync();
                throw;
            }
        }
    }

    private sealed class RecordingPlanApprovalRepositoryStore : IRepositoryPlanApprovalPolicyStore
    {
        private readonly List<string> _operations;

        public RecordingPlanApprovalRepositoryStore(List<string> operations)
        {
            ArgumentNullException.ThrowIfNull(operations);
            _operations = operations;
        }

        public Exception? Failure { get; init; }

        public Task WritePolicyAsync(
            PlanApprovalRepositoryBinding binding,
            PlanApprovalPolicy policy,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(binding);
            _operations.Add($"write:{policy}:{cancellationToken.CanBeCanceled.ToString().ToLowerInvariant()}");
            if (Failure is not null)
            {
                throw Failure;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingPlanTrustGrantStore : IUserPlanTrustGrantStore
    {
        private readonly HashSet<string> _grants = new(StringComparer.Ordinal);
        private readonly List<string> _operations;

        public RecordingPlanTrustGrantStore(List<string> operations)
        {
            ArgumentNullException.ThrowIfNull(operations);
            _operations = operations;
        }

        public bool IsGranted(string repositoryIdentity)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(repositoryIdentity);
            return _grants.Contains(repositoryIdentity);
        }

        public Task GrantAsync(
            string repositoryIdentity,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(repositoryIdentity);
            _operations.Add($"grant:{cancellationToken.CanBeCanceled.ToString().ToLowerInvariant()}");
            cancellationToken.ThrowIfCancellationRequested();
            _grants.Add(repositoryIdentity);
            return Task.CompletedTask;
        }

        public Task RevokeAsync(
            string repositoryIdentity,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(repositoryIdentity);
            _operations.Add($"revoke:{cancellationToken.CanBeCanceled.ToString().ToLowerInvariant()}");
            cancellationToken.ThrowIfCancellationRequested();
            _grants.Remove(repositoryIdentity);
            return Task.CompletedTask;
        }
    }

    private static IReadOnlyList<PlanFileIntent> ModifyIntents(params string[] paths)
    {
        return CreateIntents(PlanFileChangeKind.Modify, paths);
    }

    private static IReadOnlyList<PlanFileIntent> CreateIntents(params string[] paths)
    {
        return CreateIntents(PlanFileChangeKind.Create, paths);
    }

    private static IReadOnlyList<PlanFileIntent> CreateIntents(PlanFileChangeKind kind, params string[] paths)
    {
        return [.. paths.Select(path => new PlanFileIntent { Kind = kind, Path = path })];
    }

    private static string CreatePlanPolicyRepositoryIdentity(string repositoryRoot)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        var identityInput = OperatingSystem.IsWindows()
            ? normalized.ToUpperInvariant()
            : normalized;
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identityInput)));
    }

    private static string CreateSemanticContentIdentity(string text)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static async Task<bool> HasPlanPolicyGrantAsync(string userTrustPath, string repositoryIdentity)
    {
        if (!File.Exists(userTrustPath))
        {
            return false;
        }

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(userTrustPath));
        return document.RootElement.TryGetProperty("trustedRepositories", out var repositories)
            && repositories.TryGetProperty(repositoryIdentity, out var grant)
            && grant.ValueKind == JsonValueKind.True;
    }

    private static Mutation CreateReplacement(
        TestRepository repository,
        string relativePath,
        int offset,
        string expected,
        string replacement)
    {
        var baselineFile = Assert.Single(repository.Baseline.Files, item => string.Equals(
            item.RelativePath,
            relativePath,
            StringComparison.Ordinal));
        return new Mutation
        {
            MutationId = MutationId.New(),
            Type = MutationType.ReplaceText,
            RelativePath = relativePath,
            BaselineSha256 = baselineFile.Sha256,
            StartOffset = offset,
            Length = expected.Length,
            ExpectedText = expected,
            ReplacementText = replacement,
        };
    }

    private static MutationSet CreateMutationSet(
        TestRepository repository,
        IReadOnlyList<Mutation> mutations)
    {
        return new()
        {
            MutationSetId = MutationSetId.New(),
            SessionId = repository.SessionId,
            RunId = RunId.New(),
            WorkspaceId = repository.WorkspaceId,
            BaselineCapturedAt = repository.Baseline.CapturedAt,
            BaselineRevision = repository.Baseline.GitRevision,
            Mutations = mutations,
            Rationale = "Milestone 5 acceptance test.",
            Risk = MutationRisk.Low,
            RequiredApproval = MutationApprovalLevel.EntireSet,
        };
    }

    private static async Task<string> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        Assert.True(process.Start());
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;
        Assert.True(process.ExitCode == 0, error);
        return output;
    }

    private sealed class FakeSemanticMutationEngine : ISemanticMutationEngine
    {
        public List<RenameSymbolMutationRequest> RenameRequests { get; } = [];

        public HashSet<string> MissingSymbolIds { get; } = new(StringComparer.Ordinal);

        public List<string> Warnings { get; } = [];

        public async Task<SemanticMutationResult> RenameSymbolAsync(
            RenameSymbolMutationRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            RenameRequests.Add(request);
            if (MissingSymbolIds.Contains(request.SymbolId))
            {
                throw new KeyNotFoundException($"Symbol '{request.SymbolId}' was not found.");
            }

            var mutations = new List<Mutation>();
            foreach (var file in request.Baseline.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fullPath = Path.Combine(
                    request.Baseline.RepositoryPath,
                    file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                var oldText = await File.ReadAllTextAsync(fullPath, cancellationToken);
                var newText = oldText.Replace("IRetriever", request.NewName, StringComparison.Ordinal);
                if (string.Equals(oldText, newText, StringComparison.Ordinal))
                {
                    continue;
                }

                mutations.Add(new Mutation
                {
                    MutationId = MutationId.New(),
                    Type = MutationType.RenameSymbol,
                    RelativePath = file.RelativePath,
                    BaselineSha256 = file.Sha256,
                    StartOffset = 0,
                    Length = oldText.Length,
                    ExpectedText = oldText,
                    ReplacementText = newText,
                    RelatedSymbolId = request.SymbolId,
                });
            }

            return new SemanticMutationResult(
                new MutationSet
                {
                    MutationSetId = MutationSetId.New(),
                    SessionId = request.SessionId,
                    RunId = request.RunId,
                    WorkspaceId = request.WorkspaceId,
                    BaselineCapturedAt = request.Baseline.CapturedAt,
                    BaselineRevision = request.Baseline.GitRevision,
                    Mutations = mutations,
                    Rationale = request.Rationale,
                    AffectedProjects = ["Demo"],
                    Risk = MutationRisk.Medium,
                    RequiredApproval = MutationApprovalLevel.EntireSet,
                    ValidationPolicy = "semantic-rename",
                },
                Warnings,
                Warnings.Count == 0 ? SemanticConfidenceLevel.FullSemantic : SemanticConfidenceLevel.PartialCompilation);
        }

        public Task<SemanticMutationResult> ReplaceSyntaxNodeAsync(
            SyntaxReplacementMutationRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("The test semantic engine supports only rename.");
        }
    }

    private sealed class RecordingSemanticHostMutationAttribution : ISemanticHostMutationAttribution
    {
        public List<SemanticAttributionCompletion> Completions { get; } = [];

        public List<SemanticAttributionRegistration> Registrations { get; } = [];

        public Task<SemanticHostMutationRegistration?> RegisterExpectedWritesAsync(
            SessionId sessionId,
            WorkspaceId workspaceId,
            MutationSetId mutationSetId,
            IReadOnlyList<SemanticHostWriteExpectation> writes,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SemanticHostMutationRegistration registration = new(
                sessionId,
                workspaceId,
                mutationSetId,
                Registrations.Count + 1);
            Registrations.Add(new SemanticAttributionRegistration(
                sessionId,
                workspaceId,
                mutationSetId,
                [.. writes],
                registration));
            return Task.FromResult<SemanticHostMutationRegistration?>(registration);
        }

        public Task CompleteExpectedWritesAsync(
            SemanticHostMutationRegistration registration,
            IReadOnlyList<string> relativePaths,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Completions.Add(new SemanticAttributionCompletion(registration, [.. relativePaths]));
            return Task.CompletedTask;
        }
    }

    private sealed record SemanticAttributionRegistration(
        SessionId SessionId,
        WorkspaceId WorkspaceId,
        MutationSetId MutationSetId,
        IReadOnlyList<SemanticHostWriteExpectation> Writes,
        SemanticHostMutationRegistration Registration);

    private sealed record SemanticAttributionCompletion(
        SemanticHostMutationRegistration Registration,
        IReadOnlyList<string> RelativePaths);

    private sealed class TestRepository : IAsyncDisposable
    {
        private TestRepository(
            string root,
            SessionId sessionId,
            WorkspaceId workspaceId,
            WorkspaceBaseline baseline)
        {
            Root = root;
            SessionId = sessionId;
            WorkspaceId = workspaceId;
            Baseline = baseline;
        }

        public WorkspaceBaseline Baseline { get; }

        public string Root { get; }

        public SessionId SessionId { get; }

        public WorkspaceId WorkspaceId { get; }

        public static async Task<TestRepository> CopyAsync(string source)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(source);
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var sourcePath in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(source, sourcePath).Replace('\\', '/');
                var segments = relativePath.Split('/');
                if (segments.Contains("bin", StringComparer.OrdinalIgnoreCase)
                    || segments.Contains("obj", StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                files[relativePath] = await File.ReadAllTextAsync(sourcePath);
            }

            return await CreateAsync(files);
        }

        public static async Task<TestRepository> CreateAsync(
            IReadOnlyDictionary<string, string> files,
            IReadOnlyList<string>? approvedRoots = null,
            RepositoryTrustLevel trustLevel = RepositoryTrustLevel.TrustedMutation)
        {
            ArgumentNullException.ThrowIfNull(files);
            var root = Path.Combine(Path.GetTempPath(), $"threadsmith-m5-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            foreach (var file in files)
            {
                var path = Path.Combine(root, file.Key.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(
                    Path.GetDirectoryName(path)
                        ?? throw new InvalidOperationException("A test file has no parent directory."));
                await File.WriteAllTextAsync(path, file.Value);
            }

            var hashes = new List<WorkspaceFileHash>();
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var bytes = await File.ReadAllBytesAsync(path);
                hashes.Add(new WorkspaceFileHash(
                    Path.GetRelativePath(root, path).Replace('\\', '/'),
                    Convert.ToHexStringLower(SHA256.HashData(bytes)),
                    bytes.LongLength));
            }

            var sessionId = SessionId.New();
            var workspaceId = WorkspaceId.New();
            var baseline = new WorkspaceBaseline(
                workspaceId,
                root,
                DateTimeOffset.UtcNow,
                hashes.OrderBy(item => item.RelativePath, StringComparer.Ordinal).ToArray(),
                ApprovedRoots: approvedRoots ?? ["."],
                TrustLevel: trustLevel);
            return new TestRepository(root, sessionId, workspaceId, baseline);
        }

        public string PathOf(string relativePath)
        {
            return Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        public ValueTask DisposeAsync()
        {
            var tempRoot = Path.GetFullPath(Path.GetTempPath());
            var normalized = Path.GetFullPath(Root);
            if (!normalized.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(normalized).StartsWith("threadsmith-m5-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Refusing to remove a test directory outside its owned root.");
            }

            foreach (var path in Directory.EnumerateFileSystemEntries(
                normalized,
                "*",
                SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                }
                catch (FileNotFoundException)
                {
                    // Git worktree cleanup may remove administrative paths during enumeration.
                }
            }

            Directory.Delete(normalized, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private static string GetCorrectionMessageText(
        ModelStreamRequest request,
        string sectionPrefix)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionPrefix);
        var message = Assert.Single(request.Messages, candidate =>
            candidate.SectionId?.StartsWith(sectionPrefix, StringComparison.Ordinal) == true);
        return string.Join(" ", message.Content.Select(part => part.Content));
    }

    private sealed class QueueModelProvider : IModelProvider
    {
        private readonly Queue<ModelChunk> _chunks;

        public QueueModelProvider(params ModelChunk[] chunks)
        {
            _chunks = new Queue<ModelChunk>(chunks);
        }

        public List<ModelStreamRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            Requests.Add(request);
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return _chunks.Dequeue();
        }
    }

    private sealed class ChunkModelProvider : IModelProvider
    {
        private readonly ModelChunk[] _chunks;

        public ChunkModelProvider(params ModelChunk[] chunks)
        {
            _chunks = chunks;
        }

        public List<ModelStreamRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            Requests.Add(request);
            await Task.Yield();
            foreach (var chunk in _chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return chunk;
            }
        }
    }

    private sealed class RecordingContextAssembler : IContextAssembler
    {
        private readonly IContextAssembler _inner;

        public RecordingContextAssembler(IContextAssembler inner)
        {
            _inner = inner;
        }

        public List<ContextAssemblyRequest> Requests { get; } = [];

        public async Task<ContextAssemblyResult> AssembleAsync(
            ContextAssemblyRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return await _inner.AssembleAsync(request, cancellationToken);
        }

        public ContextInspectionProjection? GetInspection(RunId runId)
        {
            return _inner.GetInspection(runId);
        }

        public void InvalidateInspections()
        {
            _inner.InvalidateInspections();
        }
    }

    private sealed class ThrowingPreMutationAnalyzer : IPreMutationAnalyzer
    {
        public Task<PreMutationAnalysisResult> AnalyzeAsync(
            PreMutationAnalysisRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            throw new InvalidOperationException("Pre-mutation analysis should not run.");
        }
    }

    private sealed class DecisionPreMutationAnalyzer(PreMutationGateDecision decision) : IPreMutationAnalyzer
    {
        public Task<PreMutationAnalysisResult> AnalyzeAsync(
            PreMutationAnalysisRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            return Task.FromResult(new PreMutationAnalysisResult
            {
                Decision = decision,
                Omissions = ["Synthetic failure for test."],
            });
        }
    }

    private sealed class FakePreMutationAnalyzer : IPreMutationAnalyzer
    {
        public List<PreMutationAnalysisRequest> Requests { get; } = [];

        public Task<PreMutationAnalysisResult> AnalyzeAsync(
            PreMutationAnalysisRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            Requests.Add(request);
            var broken = request.OverlayFiles.FirstOrDefault(file =>
                file.Text?.Contains("Broken(", StringComparison.Ordinal) == true);
            if (broken is null)
            {
                return Task.FromResult(new PreMutationAnalysisResult
                {
                    Decision = PreMutationGateDecision.PassedCheapGates,
                    Confidence = SemanticConfidenceLevel.PartialCompilation,
                    Score = new MutationCandidateScore
                    {
                        SyntaxClean = true,
                        SemanticClean = true,
                        AnalyzerClean = true,
                    },
                });
            }

            return Task.FromResult(new PreMutationAnalysisResult
            {
                Decision = PreMutationGateDecision.RepairableDiagnostics,
                Confidence = SemanticConfidenceLevel.PartialCompilation,
                Diagnostics =
                [
                    new PreMutationDiagnostic
                    {
                        Source = PreMutationDiagnosticSource.Syntax,
                        Code = "CS1001",
                        Severity = DiagnosticSeverity.Error,
                        File = broken.RelativePath,
                        Range = new SourceRange(4, 20, 4, 21),
                        Message = "Identifier expected.",
                        RelatedMutationId = broken.RelatedMutationId,
                        ChangedHunk = "public void Broken( { }",
                        ContainingSymbol = "MethodDeclaration",
                    },
                ],
                Omissions = ["Repository analyzers were not loaded before approval."],
                Score = new MutationCandidateScore
                {
                    BlockingDiagnosticCount = 1,
                },
            });
        }
    }

    private sealed class FakeTransactionalWorkspaceResolver(FakeTransactionalWorkspace workspace) : ITransactionalWorkspaceResolver
    {
        public int StageCalls { get; private set; }

        public ITransactionalWorkspace GetWorkspace(WorkspaceId workspaceId)
        {
            Assert.Equal(workspaceId, workspace.Baseline.WorkspaceId);
            return workspace;
        }

        public Task<StagedMutationSet> StageAsync(
            MutationSet mutationSet,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(mutationSet);
            cancellationToken.ThrowIfCancellationRequested();
            StageCalls++;
            var preview = new MutationPreview(
                mutationSet.MutationSetId,
                string.Empty,
                [],
                AddedLines: 0,
                RemovedLines: 0);
            return Task.FromResult(new StagedMutationSet(
                mutationSet,
                preview,
                new ConflictReport(mutationSet.MutationSetId, []),
                ApprovalId.New()));
        }

        public Task<WorkspaceBaseline> PromoteBaselineAsync(
            WorkspaceId workspaceId,
            IReadOnlyList<string> changedFiles,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(changedFiles);
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(workspaceId, workspace.Baseline.WorkspaceId);
            return Task.FromResult(workspace.Baseline);
        }
    }

    private sealed class FakeTransactionalWorkspace(
        WorkspaceBaseline baseline,
        WorkspaceIsolation isolation,
        IReadOnlyDictionary<string, string> baselineTexts) : ITransactionalWorkspace
    {
        private readonly Dictionary<string, string> _baselineTexts = new(baselineTexts, StringComparer.Ordinal);

        public WorkspaceBaseline Baseline { get; } = baseline;

        public WorkspaceIsolation Isolation { get; } = isolation;

        public Task<string?> ReadBaselineTextAsync(
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_baselineTexts.GetValueOrDefault(relativePath));
        }

        public Task<string?> ReadStagedTextAsync(
            MutationSetId mutationSetId,
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<string?>(null);
        }

        public Task<StagedMutationSet> StageAsync(
            MutationSet mutationSet,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Stage through the resolver in this test.");
        }

        public Task<MutationPreview> SetPreviewEnabledAsync(
            MutationSetId mutationSetId,
            MutationId mutationId,
            bool isEnabled,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Preview changes are not used in this test.");
        }

        public Task<MutationCommitResult> CommitAsync(
            MutationSetId mutationSetId,
            MutationApproval approval,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Commit is not used in this test.");
        }

        public Task<IReadOnlyList<FileLifecycleReconciliation>> ReconcileLifecycleAsync(
            MutationSetId mutationSetId,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Reconciliation is not used in this test.");
        }

        public Task<MutationRollbackResult> RollbackAsync(
            MutationSetId mutationSetId,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Rollback is not used in this test.");
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>StepId deserializes from both bare UUID strings and the canonical object format.</summary>
    [Fact]
    public static void StepIdJsonConverter_BareUuidString_DeserializesCorrectly()
    {
        // Arrange
        var stepId = StepId.New();
        var bareUuidJson = $"\"{stepId.Value}\"";
        var objectJson = $"{{\"value\":\"{stepId.Value}\"}}";
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };
        options.Converters.Add(new StepIdJsonConverter());

        // Act + Assert — bare UUID string
        var fromBare = JsonSerializer.Deserialize<StepId>(bareUuidJson, options);
        Assert.Equal(stepId, fromBare);

        // Act + Assert — canonical object format
        var fromObject = JsonSerializer.Deserialize<StepId>(objectJson, options);
        Assert.Equal(stepId, fromObject);
    }

    /// <summary>The mutation proposal discriminates operation-specific contracts without model-owned plan ids.</summary>
    [Fact]
    public static void MutationProposalEnvelope_OperationDiscriminator_DeserializesSpecificContract()
    {
        // Arrange
        var json = """
            {
              "mutationSet": {
                "mutations": [
                  {
                    "type": "ReplaceText",
                    "relativePath": "src/Example.cs",
                    "startOffset": 0,
                    "length": 3,
                    "expectedText": "old",
                    "replacementText": "new"
                  }
                ],
                "rationale": "test"
              }
            }
            """;
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };

        // Act
        var envelope = JsonSerializer.Deserialize<MutationProposalEnvelope>(json, options);

        // Assert
        Assert.NotNull(envelope);
        var change = Assert.IsType<ReplaceTextMutationProposal>(Assert.Single(envelope.MutationSet.Mutations));
        Assert.Equal("old", change.ExpectedText);
        Assert.Equal("new", change.ReplacementText);
    }

    /// <summary>Operation-specific mutation DTOs reject fields owned by a different operation.</summary>
    [Fact]
    public static void MutationProposalEnvelope_IrrelevantOperationField_RejectsJson()
    {
        // Arrange
        var json = """
            {
              "mutationSet": {
                "mutations": [
                  {
                    "type": "ReplaceText",
                    "relativePath": "src/Example.cs",
                    "startOffset": 0,
                    "length": 3,
                    "expectedText": "old",
                    "replacementText": "new",
                    "destinationRelativePath": "src/Elsewhere.cs"
                  }
                ],
                "rationale": "test"
              }
            }
            """;
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };

        // Act + Assert
        _ = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<MutationProposalEnvelope>(json, options));
    }

    private sealed class RedactingSanitizer : IOutputSanitizer
    {
        public string Sanitize(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            return value.Replace("<secret>", "[redacted]", StringComparison.Ordinal);
        }
    }

    private sealed class PassthroughSanitizer : IOutputSanitizer
    {
        public string Sanitize(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            return value;
        }
    }
}
