namespace Threadsmith.ModelTooling.Tests;

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Policies.Configuration;
using Threadsmith.Policies.Contracts;
using Threadsmith.Policies.Evaluation;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

/// <summary>Checks isolated candidate validation through shared preparation and policy owners.</summary>
public static class ValidateCommandPolicyToolTests
{
    /// <summary>Evaluates every supported candidate outcome without a real shell.</summary>
    [Theory]
    [InlineData(PolicyOutcome.Allow)]
    [InlineData(PolicyOutcome.AlwaysDeny)]
    [InlineData(PolicyOutcome.ApprovalRequired)]
    [InlineData(PolicyOutcome.ApprovalRequiredSession)]
    [InlineData(PolicyOutcome.ApprovalAlwaysRequired)]
    public static void AllOutcomesUseSuppliedCandidateOnly(PolicyOutcome outcome)
    {
        using var fixture = new Fixture();
        var result = ValidateCommandPolicyTool.Evaluate(fixture.Request(fixture.Policy(Fixture.Rule("one", fixture.Name, outcome)), new("one", fixture.Name + " argument", outcome.ToString())));

        Assert.True(result.IsValidated);
        Assert.Equal("Succeeded", result.ParseStatus);
        Assert.Equal("Succeeded", result.CompileStatus);
        var example = Assert.Single(result.Results);
        Assert.Equal(outcome.ToString(), example.ActualOutcome);
        Assert.True(example.Matched);
        Assert.Equal("Unassessed", example.OuterEligibility);
        Assert.Equal(CommandPolicyNames.ShellCommand, Assert.Single(example.Actions).CommandKind);
        Assert.Contains("ActivePoliciesAndSavedGrantsNotRead", result.Warnings);
        Assert.Contains("InstalledLayerIntegrationNotChecked", result.Warnings);
        Assert.Equal(0, new FileInfo(fixture.ShellPath).Length);
    }

    /// <summary>Keeps preparation distinct from candidate validation.</summary>
    [Fact]
    public static void PreparationOnlyCannotClaimValidation()
    {
        using var fixture = new Fixture();
        var result = ValidateCommandPolicyTool.Evaluate(fixture.Request(null, new("one", fixture.Name, "Allow")));

        Assert.Equal("PreparationOnly", result.Status);
        Assert.False(result.IsValidated);
        Assert.Null(result.CandidateDigest);
        Assert.Null(Assert.Single(result.Results).ActualOutcome);
        Assert.False(Assert.Single(result.Results).Matched);
    }

    /// <summary>Reports strict parsing failures with candidate diagnostics.</summary>
    [Theory]
    [InlineData("{")]
    [InlineData("{\"schemaVersion\":1,\"unknown\":true}")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1}")]
    public static void StrictParsingReturnsDiagnostics(string candidate)
    {
        using var fixture = new Fixture();
        var result = ValidateCommandPolicyTool.Evaluate(fixture.Request(candidate, new("one", fixture.Name, "Allow")));

        Assert.Equal("ParsingFailed", result.Status);
        Assert.False(result.IsValidated);
        Assert.NotEmpty(result.Diagnostics);
        Assert.Null(Assert.Single(result.Results).ActualOutcome);
    }

    /// <summary>Does not supply installed override targets.</summary>
    [Fact]
    public static void MissingOverrideTargetFailsIsolatedCompilation()
    {
        using var fixture = new Fixture();
        var rule = Fixture.Rule("one", fixture.Name, PolicyOutcome.Allow) with { Overrides = ["not-supplied"] };
        var result = ValidateCommandPolicyTool.Evaluate(fixture.Request(fixture.Policy(rule), new("one", fixture.Name, "Allow")));

        Assert.Equal("CompilationFailed", result.Status);
        Assert.NotEmpty(result.Diagnostics);
        Assert.False(result.IsValidated);
    }

    /// <summary>Does not confuse unavailable evaluation with explicit denial.</summary>
    [Fact]
    public static void DefaultDenialUnsupportedSyntaxAndConflictCannotSatisfyDenial()
    {
        using var fixture = new Fixture();
        var unmatched = ValidateCommandPolicyTool.Evaluate(fixture.Request(fixture.Policy(), new("one", fixture.Name, "AlwaysDeny"), new("two", fixture.Name + " $(other-command)", "AlwaysDeny")));
        var conflict = ValidateCommandPolicyTool.Evaluate(fixture.Request(fixture.Policy(Fixture.Rule("allow", fixture.Name, PolicyOutcome.Allow), Fixture.Rule("deny", fixture.Name, PolicyOutcome.AlwaysDeny)), new("one", fixture.Name, "AlwaysDeny")));

        Assert.All(unmatched.Results, result =>
        {
            Assert.False(result.Matched);
            Assert.Null(result.ActualOutcome);
        });
        Assert.Equal("Succeeded", unmatched.Results[0].PreparationStatus);
        Assert.Equal("Failed", unmatched.Results[1].PreparationStatus);
        Assert.False(conflict.IsValidated);
        Assert.Null(Assert.Single(conflict.Results).ActualOutcome);
        Assert.Equal(PolicyReasons.Conflict, Assert.Single(conflict.Results).ReasonCode);
    }

    /// <summary>Combines approvals and checks every compound action.</summary>
    [Fact]
    public static void EveryCompoundActionContributesToCompleteOutcome()
    {
        using var fixture = new Fixture();
        var second = fixture.Name + "-second";
        var candidate = fixture.Policy(Fixture.Rule("one", fixture.Name, PolicyOutcome.Allow), Fixture.Rule("two", second, PolicyOutcome.ApprovalRequiredSession));
        var result = ValidateCommandPolicyTool.Evaluate(fixture.Request(candidate, new("sequence", fixture.Name + "; " + second, "ApprovalRequiredSession"), new("pipeline", fixture.Name + " | " + second, "ApprovalRequiredSession")));
        var denial = ValidateCommandPolicyTool.Evaluate(fixture.Request(fixture.Policy(Fixture.Rule("deny", fixture.Name, PolicyOutcome.AlwaysDeny)), new("compound", fixture.Name + "; " + second, "AlwaysDeny")));

        Assert.True(result.IsValidated);
        Assert.All(result.Results, example =>
        {
            Assert.Equal(2, example.Actions.Count);
            Assert.Equal("Allow", example.Actions[0].ActualOutcome);
            Assert.Equal("ApprovalRequiredSession", example.Actions[1].ActualOutcome);
        });
        Assert.False(denial.IsValidated);
        Assert.Null(Assert.Single(denial.Results).ActualOutcome);
    }

    /// <summary>Checks separate script actions while script contents remain inaccessible.</summary>
    [Fact]
    public static void ScriptsKeepSeparateInterpreterOutcomesWithoutReadingBodies()
    {
        using var fixture = new Fixture();
        var name = OperatingSystem.IsWindows() ? "fixture.ps1" : "fixture.sh";
        var path = Path.Combine(fixture.Root, name);
        File.WriteAllText(path, "THIS IS NOT EXECUTABLE SCRIPT CONTENT");
        using var heldScript = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var candidate = fixture.Policy(Fixture.Rule("interpreter", fixture.ShellName, PolicyOutcome.Allow), Fixture.Rule("script", name, PolicyOutcome.ApprovalAlwaysRequired));
        var result = ValidateCommandPolicyTool.Evaluate(fixture.Request(candidate, new("one", "./" + name, "ApprovalAlwaysRequired")));

        Assert.True(result.IsValidated);
        var example = Assert.Single(result.Results);
        Assert.Equal(2, example.Actions.Count);
        Assert.Equal("Allow", example.Actions[0].ActualOutcome);
        Assert.Equal(CommandPolicyNames.Script, example.Actions[1].CommandKind);
        Assert.Equal(path, example.Actions[1].ResolvedExecutable);
        Assert.Equal("ApprovalAlwaysRequired", example.Actions[1].ActualOutcome);
    }

    /// <summary>Binds receipts to supplied inputs and preserves actual mismatches.</summary>
    [Fact]
    public static void IdentitiesAndMismatchesRetainExactInputs()
    {
        using var fixture = new Fixture();
        var request = fixture.Request(fixture.Policy(Fixture.Rule("one", fixture.Name, PolicyOutcome.Allow)), new("one", fixture.Name, "ApprovalAlwaysRequired"));
        var first = ValidateCommandPolicyTool.Evaluate(request);
        var repeated = ValidateCommandPolicyTool.Evaluate(request);
        var candidateChanged = ValidateCommandPolicyTool.Evaluate(request with { PolicyJson = request.PolicyJson + " " });
        var examplesChanged = ValidateCommandPolicyTool.Evaluate(request with { Examples = [new("one", fixture.Name + " other", "Allow")] });

        Assert.Equal(first.CandidateDigest, repeated.CandidateDigest);
        Assert.Equal(first.ExampleSetId, repeated.ExampleSetId);
        Assert.NotEqual(first.CandidateDigest, candidateChanged.CandidateDigest);
        Assert.Equal(first.ExampleSetId, candidateChanged.ExampleSetId);
        Assert.NotEqual(first.ExampleSetId, examplesChanged.ExampleSetId);
        Assert.Equal("Allow", Assert.Single(first.Results).ActualOutcome);
        Assert.False(Assert.Single(first.Results).Matched);
    }

    /// <summary>Checks the outer shell without adding contained commands to its list.</summary>
    [Fact]
    public static void OuterEligibilityUsesOnlySuppliedShellList()
    {
        using var fixture = new Fixture();
        var request = fixture.Request(fixture.Policy(Fixture.Rule("one", fixture.Name, PolicyOutcome.Allow)), new("one", fixture.Name, "Allow"));
        var eligible = ValidateCommandPolicyTool.Evaluate(request with { AllowedExecutables = [fixture.ShellName] });
        var ineligible = ValidateCommandPolicyTool.Evaluate(request with { AllowedExecutables = [] });

        Assert.True(eligible.IsValidated);
        Assert.Equal("Eligible", Assert.Single(eligible.Results).OuterEligibility);
        Assert.False(ineligible.IsValidated);
        Assert.Equal("Ineligible", Assert.Single(ineligible.Results).OuterEligibility);
    }

    /// <summary>Rejects excess input and reports omitted result coverage explicitly.</summary>
    [Fact]
    public static void BoundsRejectOrReportMissingCoverage()
    {
        using var fixture = new Fixture();
        var request = fixture.Request(null, new("one", fixture.Name, "Allow"));
        Assert.Throws<ToolArgumentValidationException>(() => ValidateCommandPolicyTool.Evaluate(request with { WorkingDirectory = "." }));
        Assert.Throws<ToolArgumentValidationException>(() => ValidateCommandPolicyTool.Evaluate(request with { Examples = [] }));
        Assert.Throws<ToolArgumentValidationException>(() => ValidateCommandPolicyTool.Evaluate(request with { Examples = [new("one", " ", "Allow")] }));
        Assert.Throws<ToolArgumentValidationException>(() => ValidateCommandPolicyTool.Evaluate(request with { Examples = [new("one", new string('x', 8193), "Allow")] }));
        Assert.Throws<ToolArgumentValidationException>(() => ValidateCommandPolicyTool.Evaluate(request with { Examples = Enumerable.Range(0, 9).Select(index => new CommandPolicyValidationExample(index.ToString(), new string('é', 4096), "Allow")).ToArray() }));
        Assert.Throws<ToolArgumentValidationException>(() => ValidateCommandPolicyTool.Evaluate(request with { Examples = [new("one", "x", "Allow"), new("one", "y", "Allow")] }));
        var limited = ValidateCommandPolicyTool.Evaluate(request with { Examples = [new("one", fixture.Name + " " + new string('x', 7000), "Allow")] }, maximumOutputBytes: 4096);
        Assert.False(limited.IsValidated);
        Assert.Empty(limited.Results);
        Assert.Equal("OutputLimitExceeded", limited.CoverageStatus);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(limited).Length <= 4096);
        var actions = ValidateCommandPolicyTool.Evaluate(request with { Examples = Enumerable.Range(0, 5).Select(index => new CommandPolicyValidationExample(index.ToString(), string.Join("; ", Enumerable.Repeat("x", 60)), "Allow")).ToArray() });
        Assert.Empty(actions.Results);
        Assert.False(actions.IsValidated);
        Assert.Equal("ActionLimitExceeded", actions.CoverageStatus);
    }

    /// <summary>Uses the ordinary pipeline and observes explicit cancellation.</summary>
    [Fact]
    public static async Task OrdinaryPipelineUsesReadOnlyDisabledByDefaultTool()
    {
        using var fixture = new Fixture();
        var tool = new ValidateCommandPolicyTool(TestPromptLoader.Instance);
        Assert.False(tool.Definition.EnabledByDefault);
        Assert.False(tool.Definition.Essential);
        Assert.Equal(ToolSideEffect.ReadOnly, tool.Definition.SideEffect);
        Assert.Equal(ToolCategory.NativeSkillTools, tool.Definition.Category);
        await using var events = new DomainEventStream();
        var pipeline = new ToolInvocationPipeline(new ToolRegistry([tool]), new DefaultPolicyEngine(), new CommandPolicyCoordinator(), events, new SecretOutputSanitizer(), NullLogger<ToolInvocationPipeline>.Instance, timeProvider: new FakeTimeProvider());
        var request = new ToolInvocationRequest
        {
            SessionId = SessionId.New(),
            RunId = RunId.New(),
            Phase = RunPhase.EvidenceCollection,
            ToolId = tool.Definition.Id,
            ArgumentsJson = JsonSerializer.Serialize(fixture.Request(fixture.Policy(Fixture.Rule("one", fixture.Name, PolicyOutcome.Allow)), new("one", fixture.Name, "Allow"))),
            Context = new ToolInvocationContext { RepositoryPath = fixture.Root, TrustLevel = RepositoryTrustLevel.TrustedRead, RequestedBy = "test" },
        };

        var result = await pipeline.InvokeAsync(request, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        var output = JsonSerializer.Deserialize<CommandPolicyValidationResult>(result.ResultJson!);
        Assert.NotNull(output);
        Assert.True(output.IsValidated);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        Assert.Throws<OperationCanceledException>(() => ValidateCommandPolicyTool.Evaluate(fixture.Request(null, new("one", fixture.Name, "Allow")), cancellationToken: cancellation.Token));
    }

    /// <summary>Checks installed shell and Git bindings without running either executable.</summary>
    [Fact]
    public static void InstalledShellAndGitUseRealPreparationWithoutLaunch()
    {
        using var fixture = new Fixture();
        var shell = OperatingSystem.IsWindows() ? "powershell" : "sh";
        var candidate = PolicyJson.Serialize(new PolicyDocument
        {
            Platforms = ImmutableDictionary<string, PolicyProfile>.Empty.Add(Fixture.Platform(), new PolicyProfile
            {
                Shells = ImmutableDictionary<string, PolicyProfile>.Empty.Add(shell, new PolicyProfile
                {
                    Rules = [Fixture.Rule("git-inspection", "git", PolicyOutcome.ApprovalRequired)],
                }),
            }),
        });
        var request = fixture.Request(candidate, new("git-status", "git status", "ApprovalRequired")) with { Shell = shell };

        var result = ValidateCommandPolicyTool.Evaluate(request, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsValidated, JsonSerializer.Serialize(result));
        var example = Assert.Single(result.Results);
        var action = Assert.Single(example.Actions);
        Assert.NotNull(example.OuterResolvedExecutable);
        Assert.True(Path.IsPathFullyQualified(example.OuterResolvedExecutable));
        Assert.True(Path.IsPathFullyQualified(action.ResolvedExecutable));
        Assert.Equal("git", Path.GetFileNameWithoutExtension(action.ResolvedExecutable));
        Assert.Equal(CommandPolicyNames.External, action.CommandKind);
        Assert.Equal("ApprovalRequired", example.ActualOutcome);
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(fixture.Root), path => path != fixture.ShellPath);
    }

    /// <summary>Does not compare or disclose active policy and sanitizes supplied command text normally.</summary>
    [Fact]
    public static async Task PipelineDoesNotReadActivePolicyCanaries()
    {
        using var fixture = new Fixture();
        const string activeCanary = "active-policy-canary-must-not-enter-output";
        var activeDocument = PolicyJson.Parse(fixture.Policy(Fixture.Rule(activeCanary, fixture.Name, PolicyOutcome.AlwaysDeny)));
        var activeSnapshot = PolicyCompiler.Compile(new PolicyDocument(), activeDocument, null, Fixture.Platform(), fixture.ShellName);
        var active = new EffectiveSessionPolicy
        {
            BindingId = Guid.NewGuid(),
            Generation = 1,
            RepositoryPath = fixture.Root,
            Fingerprint = "fixture",
            MaximumTrust = RepositoryTrustLevel.TrustedRead,
            CommandPolicies = ImmutableDictionary<string, PolicySnapshot>.Empty.Add(fixture.ShellName, activeSnapshot),
        };
        var secretCanary = "sk-" + new string('x', 32);
        var tool = new ValidateCommandPolicyTool(TestPromptLoader.Instance);
        var clock = new FakeTimeProvider();
        await using var events = new DomainEventStream(timeProvider: clock);
        var retained = new ConcurrentQueue<string>();
        await using var subscription = events.Subscribe((domainEvent, _) =>
        {
            retained.Enqueue(JsonSerializer.SerializeToElement(domainEvent, domainEvent.GetType()).GetRawText());
            return Task.CompletedTask;
        });
        var pipeline = new ToolInvocationPipeline(new ToolRegistry([tool]), new DefaultPolicyEngine(), new CommandPolicyCoordinator(), events, new SecretOutputSanitizer(), NullLogger<ToolInvocationPipeline>.Instance, timeProvider: clock);
        var input = fixture.Request(fixture.Policy(Fixture.Rule("candidate", fixture.Name, PolicyOutcome.Allow)), new("one", fixture.Name + " " + secretCanary, "Allow"));
        var request = new ToolInvocationRequest
        {
            SessionId = SessionId.New(),
            RunId = RunId.New(),
            Phase = RunPhase.EvidenceCollection,
            ToolId = tool.Definition.Id,
            ArgumentsJson = JsonSerializer.Serialize(input),
            Context = new ToolInvocationContext { RepositoryPath = fixture.Root, RequestedBy = "test", TrustLevel = RepositoryTrustLevel.TrustedRead, SecurityPolicy = active },
        };

        var result = await pipeline.InvokeAsync(request, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        var output = JsonSerializer.Deserialize<CommandPolicyValidationResult>(result.ResultJson!);
        Assert.NotNull(output);
        Assert.True(output.IsValidated);
        Assert.Equal("Allow", Assert.Single(output.Results).ActualOutcome);
        Assert.NotEmpty(retained);
        foreach (var payload in retained.Append(result.ResultJson ?? string.Empty).Append(result.ModelResultContent ?? string.Empty))
        {
            Assert.DoesNotContain(activeCanary, payload, StringComparison.Ordinal);
            Assert.DoesNotContain(secretCanary, payload, StringComparison.Ordinal);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "threadsmith-validator-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            ShellName = OperatingSystem.IsWindows() ? "pwsh" : "sh";
            ShellPath = Path.Combine(Root, ShellName + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
            File.WriteAllBytes(ShellPath, []);
            Name = "threadsmith-validator-command-" + Guid.NewGuid().ToString("N");
        }

        public string Root { get; }

        public string ShellName { get; }

        public string ShellPath { get; }

        public string Name { get; }

        public static PolicyRule Rule(string id, string name, PolicyOutcome outcome) => new()
        {
            Id = id,
            Policy = outcome,
            Matchers = [new PolicyMatcher { Type = PolicyMatcherType.Exact, Fact = PolicyFact.ExecutableName, Value = name }],
        };

        public CommandPolicyValidationRequest Request(string? policy, CommandPolicyValidationExample example, CommandPolicyValidationExample? second = null) => new()
        {
            PolicyJson = policy,
            Layer = "user",
            Platform = Platform(),
            Shell = ShellPath,
            WorkingDirectory = Root,
            Examples = second is null ? [example] : [example, second],
        };

        public string Policy(params PolicyRule[] rules) => PolicyJson.Serialize(new PolicyDocument
        {
            Platforms = ImmutableDictionary<string, PolicyProfile>.Empty.Add(Platform(), new PolicyProfile
            {
                Shells = ImmutableDictionary<string, PolicyProfile>.Empty.Add(ShellName, new PolicyProfile { Rules = [.. rules] }),
            }),
        });

        public void Dispose() => Directory.Delete(Root, recursive: true);

        public static string Platform() => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
    }
}
