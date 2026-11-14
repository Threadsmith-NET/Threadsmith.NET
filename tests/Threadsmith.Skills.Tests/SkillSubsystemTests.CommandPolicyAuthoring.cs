namespace Threadsmith.Skills.Tests;

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Skills;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public sealed partial class SkillSubsystemTests
{
    /// <summary>Maintained discovery verifies package integrity and declared schemas.</summary>
    [Fact]
    public static async Task CommandPolicyAuthoring_PackageAndSchemasUseOrdinaryContracts()
    {
        var token = TestContext.Current.CancellationToken;
        var catalog = new SkillCatalog([new SkillCatalogSource(SkillScope.Maintained, MaintainedRoot(), "maintained", IsMaintained: true)]);
        await catalog.RefreshAsync(token);
        var candidate = await new SkillPackageVerifier(new SkillTrustPolicySnapshot()).VerifyAsync(catalog.Resolve("command-policy-authoring"), token);
        Assert.True(candidate.Enabled, candidate.VerificationReason);
        Assert.Equal(SkillVerificationState.Maintained, candidate.Verification);
        Assert.Collection(
            candidate.Metadata.Requirements.RequiredTools,
            item => Assert.Equal("validate_command_policy", item),
            item => Assert.Equal("ask_user", item));
        Assert.Empty(candidate.Metadata.Requirements.OptionalTools);
        Assert.False(candidate.Metadata.Requirements.InheritAvailableTools);
        var step = Assert.Single(candidate.Metadata.Workflow.Steps);
        Assert.Equal("instructions", step.ResponseProperty);
        Assert.Null(step.SuccessProperty);
        Assert.Null(step.HostAction);
        var validator = new BoundedJsonSchemaValidator();
        var input = validator.Compile(await SkillSchemaAssets.ReadAsync(candidate, step.InputSchemaAsset!, token));
        var output = validator.Compile(await SkillSchemaAssets.ReadAsync(candidate, step.OutputSchemaAsset!, token));
        _ = validator.Validate(input, """{"request":"Draft a policy"}""");
        _ = validator.Validate(output, AuthoringIncomplete);
        foreach (var invalid in new[]
        {
            """{"request":""}""", """{"request":"x","unknown":true}""",
            """{"request":"x","layer":"repository"}""", """{"request":"x","shell":null}""",
            """{"request":"x","examples":[{"command":"x","expectedOutcome":"Deny"}]}""",
            """{"request":"x","examples":[{"command":"x","expectedOutcome":"Allow","extra":1}]}""",
            JsonSerializer.Serialize(new { request = new string('x', 8193) }),
            JsonSerializer.Serialize(new { request = "x", examples = Enumerable.Repeat(new { command = "x", expectedOutcome = "Allow" }, 33) }),
        })
        {
            Assert.Throws<InvalidDataException>(() => validator.Validate(input, invalid));
        }

        foreach (var invalid in new[]
        {
            """{"status":"validated"}""",
            """{"status":"incomplete","warnings":[],"outstandingItems":[],"instructions":"x","policyJson":null}""",
            """{"status":"incomplete","warnings":[],"outstandingItems":[],"instructions":"x","extra":1}""",
            """{"status":"incomplete","warnings":[],"outstandingItems":[],"instructions":""}""",
        })
        {
            Assert.Throws<InvalidDataException>(() => validator.Validate(output, invalid));
        }

        Assert.Contains("If omitted, use the active repository root.", await SkillSchemaAssets.ReadAsync(candidate, step.InputSchemaAsset!, token), StringComparison.Ordinal);
        var content = await new SkillContentLoader(new SecretOutputSanitizer(), TestPromptLoader.Instance).LoadAsync(candidate, step, 8000, token);
        Assert.Contains(content, segment => segment.AssetPath == "instructions/author.md");
    }

    /// <summary>Each declared asset fails closed after modification.</summary>
    [Theory]
    [InlineData("instructions/author.md")]
    [InlineData("schemas/input.json")]
    [InlineData("schemas/output.json")]
    public static async Task CommandPolicyAuthoring_TamperingFailsClosed(string asset)
    {
        using var package = TemporaryPackage.CopyMaintained("command-policy-authoring");
        var candidate = Assert.Single((await package.CreateCatalog(SkillScope.Maintained).RefreshAsync(TestContext.Current.CancellationToken)).Candidates);
        await File.AppendAllTextAsync(Path.Combine(package.PackageRoot, asset), "tampered", TestContext.Current.CancellationToken);
        var verified = await new SkillPackageVerifier(new SkillTrustPolicySnapshot()).VerifyAsync(candidate, TestContext.Current.CancellationToken);
        Assert.Equal(SkillVerificationState.Invalid, verified.Verification);
    }

    /// <summary>Host context supplies defaults without introducing policy scope.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static void CommandPolicyAuthoring_ContextUsesRepositoryNotProcessDirectory(bool supplied)
    {
        using var package = TemporaryPackage.CopyMaintained("command-policy-authoring");
        var explicitPath = package.PackageRoot;
        var input = supplied ? JsonSerializer.Serialize(new { request = "x", workingDirectory = explicitPath }) : """{"request":"x"}""";
        var policy = new CommandPolicyAuthoringPolicy(input, package.Root);
        using var bound = JsonDocument.Parse(policy.BindArguments("""{"workingDirectory":"/model/guess","platform":"invented"}"""));
        Assert.Equal(supplied ? explicitPath : package.Root, bound.RootElement.GetProperty("workingDirectory").GetString());
        Assert.Equal(CommandPolicyAuthoringPolicy.HostPlatform, bound.RootElement.GetProperty("platform").GetString());
        Assert.NotEqual(Environment.CurrentDirectory, package.Root);
        Assert.DoesNotContain("matchers", bound.RootElement.GetRawText(), StringComparison.Ordinal);
        var missing = new CommandPolicyAuthoringPolicy("""{"request":"x"}""", null);
        Assert.Throws<InvalidDataException>(() => missing.BindArguments("{}"));
        Assert.Throws<InvalidDataException>(() => missing.BindArguments(JsonSerializer.Serialize(new { workingDirectory = package.Root })));
        Assert.Throws<InvalidDataException>(() => missing.BindArguments(JsonSerializer.Serialize(new { WorkingDirectory = package.Root })));
        missing.ValidateOutput(AuthoringIncomplete);
        Assert.Throws<InvalidDataException>(() => new CommandPolicyAuthoringPolicy("""{"request":"x","workingDirectory":"relative"}""", package.Root));
        Assert.Throws<InvalidDataException>(() => new CommandPolicyAuthoringPolicy("""{"request":" "}""", package.Root));
        var oversized = JsonSerializer.Serialize(new { request = "x", examples = Enumerable.Repeat(new { command = new string('x', 8192), expectedOutcome = "Allow" }, 9) });
        Assert.Throws<InvalidDataException>(() => new CommandPolicyAuthoringPolicy(oversized, package.Root));
    }

    /// <summary>Final claims must match the latest complete tool observation exactly.</summary>
    [Theory]
    [InlineData("exact")]
    [InlineData("fabricated")]
    [InlineData("candidate")]
    [InlineData("digest")]
    [InlineData("examples")]
    [InlineData("outcome")]
    [InlineData("warnings")]
    [InlineData("outstanding")]
    [InlineData("stale")]
    [InlineData("coverage")]
    [InlineData("mismatch")]
    public static async Task CommandPolicyAuthoring_FinalClaimsRequireExactObservedEvidence(string alteration)
    {
        using var package = TemporaryPackage.CopyMaintained("command-policy-authoring");
        var request = AuthoringRequest(package.Root, "Allow");
        var observation = await AuthoringEvaluateAsync(request);
        var policy = new CommandPolicyAuthoringPolicy("""{"request":"x"}""", package.Root);
        var output = JsonNode.Parse(AuthoringOutput(request, observation))!.AsObject();
        policy.Observe(JsonSerializer.Serialize(request), new ToolInvocationResult { ToolInvocationId = ToolInvocationId.New(), ToolId = "validate_command_policy", Succeeded = true, ResultJson = JsonSerializer.Serialize(observation) });
        switch (alteration)
        {
            case "fabricated":
                {
                    policy = new CommandPolicyAuthoringPolicy("""{"request":"x"}""", package.Root);
                    break;
                }

            case "candidate":
                {
                    output["policyJson"] = request.PolicyJson + " ";
                    break;
                }

            case "digest":
                {
                    output["validation"]!["candidateDigest"] = "invented";
                    break;
                }

            case "examples":
                {
                    output["validation"]!["results"] = new JsonArray();
                    break;
                }

            case "outcome":
                {
                    output["validation"]!["results"]![0]!["actualOutcome"] = "AlwaysDeny";
                    break;
                }

            case "warnings":
                {
                    output["warnings"] = new JsonArray();
                    break;
                }

            case "outstanding":
                {
                    output["outstandingItems"] = new JsonArray("Unresolved");
                    break;
                }

            case "stale":
                {
                    policy.Observe("{}", null);
                    break;
                }

            case "coverage":
            case "mismatch":
                {
                    policy.Observe(JsonSerializer.Serialize(request), new ToolInvocationResult
                    {
                        ToolInvocationId = ToolInvocationId.New(),
                        ToolId = "validate_command_policy",
                        Succeeded = true,
                        ResultJson = JsonSerializer.Serialize(observation with { CoverageStatus = alteration == "coverage" ? "OutputLimitExceeded" : "Complete", IsValidated = false }),
                    });
                    break;
                }
        }

        if (alteration == "exact")
        {
            policy.ValidateOutput(output.ToJsonString());
            Assert.Throws<InvalidDataException>(() => policy.ValidateOutput(AuthoringIncomplete));
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => policy.ValidateOutput(output.ToJsonString()));
        }
    }

    /// <summary>Incomplete checked drafts retain known outcomes, warnings, and explanations.</summary>
    [Theory]
    [InlineData("exact")]
    [InlineData("omitted")]
    [InlineData("omitted-draft")]
    [InlineData("outcome")]
    [InlineData("warnings")]
    [InlineData("outstanding")]
    [InlineData("changed-draft")]
    [InlineData("unavailable")]
    [InlineData("invalidated")]
    public static async Task CommandPolicyAuthoring_IncompleteDraftPreservesCurrentEvidence(string alteration)
    {
        // Arrange a real observed mismatch, not a fabricated validator result.
        using var package = TemporaryPackage.CopyMaintained("command-policy-authoring");
        var request = AuthoringRequest(package.Root, "AlwaysDeny");
        var observation = await AuthoringEvaluateAsync(request);
        Assert.False(observation.IsValidated);
        Assert.False(Assert.Single(observation.Results).Matched);
        Assert.Equal("AlwaysDeny", observation.Results[0].ActualOutcome);
        Assert.NotEmpty(observation.Warnings);
        var policy = new CommandPolicyAuthoringPolicy("""{"request":"x"}""", package.Root);
        if (alteration != "unavailable")
        {
            policy.Observe(JsonSerializer.Serialize(request), new ToolInvocationResult
            {
                ToolInvocationId = ToolInvocationId.New(),
                ToolId = "validate_command_policy",
                Succeeded = true,
                ResultJson = JsonSerializer.Serialize(observation),
            });
        }

        var output = JsonNode.Parse(AuthoringOutput(request, observation))!.AsObject();
        output["status"] = "incomplete";
        output["outstandingItems"] = new JsonArray("The observed outcome differs from the requested outcome.");
        switch (alteration)
        {
            case "omitted":
            case "omitted-draft":
            case "changed-draft":
            case "unavailable":
            case "invalidated":
                {
                    output.Remove("validation");
                    output["warnings"] = new JsonArray();
                    if (alteration == "changed-draft")
                    {
                        output["policyJson"] = request.PolicyJson + " ";
                    }
                    else if (alteration == "omitted-draft")
                    {
                        output.Remove("policyJson");
                    }
                    else if (alteration == "invalidated")
                    {
                        policy.Observe("{}", null);
                    }

                    break;
                }

            case "outcome":
                {
                    output["validation"]!["results"]![0]!["actualOutcome"] = "Allow";
                    output["validation"]!["results"]![0]!["matched"] = true;
                    break;
                }

            case "warnings":
                {
                    output["warnings"] = new JsonArray();
                    break;
                }

            case "outstanding":
                {
                    output["outstandingItems"] = new JsonArray();
                    break;
                }
        }

        // Act and assert: only current, unchanged evidence must be retained exactly.
        if (alteration is "exact" or "changed-draft" or "unavailable" or "invalidated")
        {
            policy.ValidateOutput(output.ToJsonString());
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => policy.ValidateOutput(output.ToJsonString()));
        }
    }

    /// <summary>Manual and nested authoring use the shared question and tool pipeline.</summary>
    [Theory]
    [InlineData(false, "normal")]
    [InlineData(true, "normal")]
    [InlineData(false, "repository")]
    [InlineData(false, "cancel")]
    [InlineData(false, "budget")]
    [InlineData(false, "decline")]
    [InlineData(false, "stop")]
    [InlineData(false, "unsupported")]
    [InlineData(false, "correction")]
    [InlineData(false, "invalid-json")]
    [InlineData(false, "invalid-array")]
    [InlineData(false, "invalid-scalar")]
    [InlineData(false, "invalid-duplicate")]
    public async Task CommandPolicyAuthoring_SharedPipelineRevisesAndClarifiesInOneExecution(bool nested, string ending)
    {
        var token = TestContext.Current.CancellationToken;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var package = TemporaryPackage.CopyMaintained("command-policy-authoring");
        var catalog = package.CreateCatalog(SkillScope.Maintained);
        var candidate = await new SkillPackageVerifier(new SkillTrustPolicySnapshot()).VerifyAsync(Assert.Single((await catalog.RefreshAsync(token)).Candidates), token);
        var step = Assert.Single(candidate.Metadata.Workflow.Steps);
        var plan = PermissionPlan() with
        {
            Package = candidate.Identity,
            Scope = SkillScope.Maintained,
            RepositoryPath = package.Root,
            AvailableToolIds = ["validate_command_policy", "ask_user"],
            Compatibility = new SkillCompatibilityResult { IsCompatible = true, AvailableRequiredTools = ["validate_command_policy", "ask_user"] },
            EffectiveBudget = new SkillBudget { ModelTurns = ending == "budget" ? 2 : 8, ToolCalls = 8 },
        };
        await using var events = new DomainEventStream();
        var steering = new RunSteeringCoordinator();
        if (nested)
        {
            steering.RegisterRun(plan.Request.SessionId, plan.Request.RunId);
        }

        using var questions = new UserQuestionCoordinator(events, steering);
        var presentations = 0;
        questions.Attach((request, _) =>
        {
            presentations++;
            return Task.FromResult(new UserQuestionResult(
                ending == "decline" ? UserQuestionStatus.Cancelled : UserQuestionStatus.Answered,
                ending == "decline" ? [] : [new(request.Questions[0].Id, null, ending == "stop" ? "Stop" : "Use Allow, user layer, the supplied example and shell.")]));
        });
        var context = PermissionContext() with { RepositoryPath = package.Root, UserQuestions = questions };
        var registry = new ToolRegistry([new ValidateCommandPolicyTool(TestPromptLoader.Instance), new AskUserTool(TestPromptLoader.Instance)]);
        var sanitizer = new SecretOutputSanitizer();
        var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new DenyApprovalPolicy(), events, sanitizer, NullLogger<ToolInvocationPipeline>.Instance, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
        var snapshots = new ConversationToolSnapshotStore();
        if (nested)
        {
            var snapshot = snapshots.Capture(plan.Request.SessionId, plan.Request.RunId, [], context);
            plan = plan with { Request = plan.Request with { CallerToolSnapshotId = snapshot } };
        }

        var model = new AuthoringLoopModel(package.Root)
        {
            Ending = ending,
            BeforeFinal = () =>
            {
                if (ending == "repository")
                {
                    context = context with { RepositoryPath = package.PackageRoot };
                }
                else if (ending == "cancel")
                {
                    cancellation.Cancel();
                }
            },
        };
        var runner = new ModelSkillProcedureRunner(model, registry, pipeline, sanitizer, (_, _) => Task.FromResult(context), TestPromptLoader.Instance, snapshots: snapshots, questions: questions);
        var content = await new SkillContentLoader(sanitizer, TestPromptLoader.Instance).LoadAsync(candidate, step, 8000, token);
        if (ending is "repository" or "budget")
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(plan, step, 1, content, """{"request":"Draft a command policy"}""", cancellation.Token));
            return;
        }

        if (ending == "cancel")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(plan, step, 1, content, """{"request":"Draft a command policy"}""", cancellation.Token));
            return;
        }

        var result = await runner.RunAsync(plan, step, 1, content, """{"request":"Draft a command policy"}""", cancellation.Token);
        using var output = JsonDocument.Parse(result.OutputJson);
        var incomplete = ending is "decline" or "stop" or "unsupported";
        Assert.Equal(incomplete ? "incomplete" : "validated", output.RootElement.GetProperty("status").GetString());
        Assert.Equal(incomplete ? 2 : ending == "correction" ? 5 : 4, result.ModelTurns);
        Assert.Equal(incomplete ? 1 : 3, result.ToolCalls);
        Assert.Equal(1, presentations);
        Assert.Contains(package.Root.Replace("\\", "\\\\", StringComparison.Ordinal), model.Requests[0].Input, StringComparison.Ordinal);
        Assert.Empty(result.SideEffects ?? []);
        if (!incomplete)
        {
            Assert.DoesNotContain("WorkingDirectory", output.RootElement.GetProperty("policyJson").GetString(), StringComparison.Ordinal);
        }

        if (plan.Request.CallerToolSnapshotId is { } snapshotId)
        {
            snapshots.Release(snapshotId);
            await questions.EndRunAsync(plan.Request.SessionId, plan.Request.RunId, token);
        }
    }

    private const string AuthoringIncomplete = """{"status":"incomplete","warnings":["Active policy was not read; installed integration was not checked."],"outstandingItems":["User answer unavailable."],"instructions":"Supply the missing answer to continue."}""";

    /// <summary>Supplied examples cannot be omitted, weakened, or replaced by model-generated calls.</summary>
    [Theory]
    [InlineData("omit")]
    [InlineData("weaken")]
    [InlineData("replace")]
    [InlineData("user-revision")]
    [InlineData("declined")]
    public static async Task CommandPolicyAuthoring_RetainsUserExamplesUntilExplicitRevision(string change)
    {
        using var package = TemporaryPackage.CopyMaintained("command-policy-authoring");
        var request = AuthoringRequest(package.Root, "Allow");
        var initialExamples = new[]
        {
            new { command = request.Examples[0].Command, expectedOutcome = "Allow" },
            new { command = "echo private", expectedOutcome = "AlwaysDeny" },
        };
        var policy = new CommandPolicyAuthoringPolicy(JsonSerializer.Serialize(new { request = "x", examples = initialExamples }), package.Root);
        if (change == "weaken")
        {
            request = request with { Examples = [.. request.Examples, new("example-2", "echo private", "Allow")] };
        }
        else if (change == "replace")
        {
            request = request with { Examples = [.. request.Examples, new("example-2", "echo harmless", "Allow")] };
        }

        var observation = await AuthoringEvaluateAsync(request);
        var toolResult = new ToolInvocationResult { ToolInvocationId = ToolInvocationId.New(), ToolId = "validate_command_policy", Succeeded = true, ResultJson = JsonSerializer.Serialize(observation) };
        policy.Observe(JsonSerializer.Serialize(request), toolResult);
        Assert.Throws<InvalidDataException>(() => policy.ValidateOutput(AuthoringOutput(request, observation)));
        if (change is "user-revision" or "declined")
        {
            var schemaJson = await File.ReadAllTextAsync(Path.Combine(MaintainedRoot(), "command-policy-authoring", "schemas", "input.json"), TestContext.Current.CancellationToken);
            var schemas = new BoundedJsonSchemaValidator();
            var schema = schemas.Compile(schemaJson);
            var answer = new UserQuestionResult(
                change == "declined" ? UserQuestionStatus.Cancelled : UserQuestionStatus.Answered,
                [new("examples", null, JsonSerializer.Serialize(new { examples = initialExamples.Take(1).ToArray() }))]);
            policy.ObserveAnswer(toolResult with { ToolId = "ask_user", ResultJson = JsonSerializer.Serialize(answer) }, value => schemas.Validate(schema, value));
            Assert.Throws<InvalidDataException>(() => policy.ValidateOutput(AuthoringOutput(request, observation)));
            policy.Observe(JsonSerializer.Serialize(request), toolResult);
            if (change == "user-revision")
            {
                policy.ValidateOutput(AuthoringOutput(request, observation));
            }
            else
            {
                Assert.Throws<InvalidDataException>(() => policy.ValidateOutput(AuthoringOutput(request, observation)));
            }
        }
    }

    /// <summary>Each exact outcome is supported at both isolated candidate layers.</summary>
    [Theory]
    [InlineData("Allow", "user")]
    [InlineData("AlwaysDeny", "machine")]
    [InlineData("ApprovalRequired", "user")]
    [InlineData("ApprovalRequiredSession", "machine")]
    [InlineData("ApprovalAlwaysRequired", "user")]
    public static async Task CommandPolicyAuthoring_AllOutcomesUseObservedResults(string outcome, string layer)
    {
        using var package = TemporaryPackage.CopyMaintained("command-policy-authoring");
        var request = AuthoringRequest(package.Root, outcome) with
        {
            Layer = layer,
            Examples = [new("example-1", "echo policy-authoring-example", outcome)],
        };
        var observation = await AuthoringEvaluateAsync(request);
        Assert.True(observation.IsValidated);
        var policy = new CommandPolicyAuthoringPolicy("""{"request":"x"}""", package.Root);
        policy.Observe(JsonSerializer.Serialize(request), new ToolInvocationResult
        {
            ToolInvocationId = ToolInvocationId.New(),
            ToolId = "validate_command_policy",
            Succeeded = true,
            ResultJson = JsonSerializer.Serialize(observation),
        });
        policy.ValidateOutput(AuthoringOutput(request, observation));
    }

    private static readonly JsonSerializerOptions AuthoringJsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static CommandPolicyValidationRequest AuthoringRequest(string directory, string outcome) => new()
    {
        Layer = "user",
        Platform = CommandPolicyAuthoringPolicy.HostPlatform,
        Shell = OperatingSystem.IsWindows() ? "cmd" : "sh",
        WorkingDirectory = directory,
        PolicyJson = JsonSerializer.Serialize(new { schemaVersion = 1, common = new { rules = new[] { new { id = "authored", policy = outcome, matchers = new[] { new { type = "executable", value = "echo" } } } } }, platforms = new Dictionary<string, object> { [CommandPolicyAuthoringPolicy.HostPlatform] = new { shells = new Dictionary<string, object> { [OperatingSystem.IsWindows() ? "cmd" : "sh"] = new { } } } } }),
        Examples = [new("example-1", "echo policy-authoring-example", "Allow")],
    };

    private static async Task<CommandPolicyValidationResult> AuthoringEvaluateAsync(CommandPolicyValidationRequest request)
    {
        var context = new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), PermissionContext());
        return (await new ValidateCommandPolicyTool(TestPromptLoader.Instance).ExecuteAsync(request, context, TestContext.Current.CancellationToken)).Value;
    }

    private static string AuthoringOutput(CommandPolicyValidationRequest request, CommandPolicyValidationResult observation) => JsonSerializer.Serialize(new
    {
        status = "validated",
        policyJson = request.PolicyJson,
        validation = new { candidateDigest = observation.CandidateDigest, exampleSetId = observation.ExampleSetId, results = observation.Results.Select(item => new { command = item.Command, expectedOutcome = item.ExpectedOutcome, actualOutcome = item.ActualOutcome, matched = item.Matched }) },
        warnings = observation.Warnings,
        outstandingItems = Array.Empty<string>(),
        instructions = "Isolated candidate only. Merge schemaVersion 1 under tools.runProcess.commandPolicy after manual review. No active policy or saved grants read; installed integration unchecked; no execution or writes.",
    });

    private sealed class AuthoringLoopModel : IModelProvider
    {
        private readonly string _directory;

        public AuthoringLoopModel(string directory) => _directory = directory;

        public List<ModelStreamRequest> Requests { get; } = [];

        public string Ending { get; init; } = "normal";

        public Action? BeforeFinal { get; init; }

        public bool ClarifyAfterMismatch { get; init; }

        public async IAsyncEnumerable<ModelChunk> StreamAsync(ModelStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            if (Requests.Count == (ClarifyAfterMismatch ? 2 : 1))
            {
                yield return new ModelChunk { Output = new ToolRequestModelOutput("ask_user", ClarificationArguments) };
            }
            else if (Ending is "decline" or "stop" or "unsupported")
            {
                yield return new ModelChunk { Text = AuthoringIncomplete };
            }
            else if (Requests.Count <= 3)
            {
                if (Requests.Count == 2 && Ending.StartsWith("invalid-", StringComparison.Ordinal))
                {
                    var malformed = Ending switch
                    {
                        "invalid-json" => "{",
                        "invalid-array" => "[]",
                        "invalid-scalar" => "42",
                        _ => """{"workingDirectory":"x","WorkingDirectory":"y"}""",
                    };
                    yield return new ModelChunk { Output = new ToolRequestModelOutput("validate_command_policy", malformed) };
                    yield break;
                }

                var arguments = JsonSerializer.SerializeToNode(AuthoringRequest(_directory, Requests.Count < 3 ? "AlwaysDeny" : "Allow"))!.AsObject();
                arguments.Remove("WorkingDirectory");
                arguments.Remove("Platform");
                yield return new ModelChunk { Output = new ToolRequestModelOutput("validate_command_policy", arguments.ToJsonString()) };
            }
            else
            {
                var toolResult = request.Messages.Last(message => message.ToolName == "validate_command_policy" && message.Role == ModelMessageRole.Tool).GetModelVisibleContent();
                Assert.StartsWith("{", toolResult, StringComparison.Ordinal);
                var observation = JsonSerializer.Deserialize<CommandPolicyValidationResult>(toolResult, AuthoringJsonOptions)!;
                BeforeFinal?.Invoke();
                var output = AuthoringOutput(AuthoringRequest(_directory, "Allow"), observation);
                if (Ending == "correction" && Requests.Count == 4)
                {
                    var altered = JsonNode.Parse(output)!;
                    altered["validation"]!["candidateDigest"] = "fabricated";
                    output = altered.ToJsonString();
                }

                yield return new ModelChunk { Text = output };
            }
        }
    }
}
