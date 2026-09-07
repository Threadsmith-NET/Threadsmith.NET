namespace Threadsmith.Architecture.Tests;

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.App;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Models.OpenAiCompatible;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

/// <summary>Opt-in real-provider runs recording unrestricted role responses and inspection efficiency.</summary>
public sealed partial class SubagentRoleLiveTests
{
    private static readonly JsonSerializerOptions ReportJson = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Exercises every role against synthetic issue cases and controls using real file tools and models.</summary>
    [Fact]
    [Trait("Category", "LiveIntegration")]
    public async Task AllRoles_RealProvider_RecordResponsesAndEfficiency()
    {
        if (Environment.GetEnvironmentVariable("THREADSMITH_LIVE_AGENT_TESTS") != "1")
        {
            Assert.Skip("Set THREADSMITH_LIVE_AGENT_TESTS=1 to send synthetic fixtures to the trusted configured model.");
        }

        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "threadsmith-agent-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = ConfigurationBootstrap.ResolvePaths(root);
            var configuration = ConfigurationBootstrap.BuildTrusted(paths);
            using var ownedConfiguration = configuration as IDisposable;
            var providers = ModelProviderConfigurationLoader.Load(
                paths.UserProviderCatalog,
                paths.RepositoryProviderCatalog,
                new ModelProviderRegistry([new OpenAiCompatibleProviderRegistration()]),
                enforceHttps: configuration.GetValue("model:enforceModelEndpointHttps", true),
                includeRepository: false);
            var profileId = Environment.GetEnvironmentVariable("THREADSMITH_LIVE_AGENT_PROFILE") is { Length: > 0 } configuredId
                ? new ModelProfileId(Guid.Parse(configuredId))
                : providers.DefaultModelId ?? throw new InvalidOperationException("Configure a default provider model first.");
            var providerId = Environment.GetEnvironmentVariable("THREADSMITH_LIVE_AGENT_PROVIDER")
                ?? providers.Get(profileId).ProviderId;
            var roleSettings = new Dictionary<string, string?>();
            foreach (var role in Enum.GetValues<AgentRole>())
            {
                var key = "agents:roleModels:" + AgentRoleNames.GetName(role);
                roleSettings[key + ":providerId"] = providerId;
                roleSettings[key + ":profileId"] = profileId.Value.ToString("D");
            }

            var configured = new ConfigurationBuilder().AddConfiguration(configuration).AddInMemoryCollection(roleSettings).Build();
            using var ownedConfigured = configured as IDisposable;
            var userDirectory = Path.GetDirectoryName(paths.UserConfiguration)
                ?? throw new InvalidOperationException("The user configuration has no directory.");
            var secrets = new SecretResolver(
                [new EnvironmentSecretProvider(), new UserFileSecretProvider(Path.Combine(userDirectory, "secrets", "config.json"))]);
            using var models = await ModelComposition.CreateAsync(
                configured,
                paths,
                secrets,
                NullLoggerFactory.Instance,
                trustedConfiguration: configured);
            var profile = models.TrustedCatalog.Get(profileId);
            var reportDirectory = Path.GetFullPath(
                Environment.GetEnvironmentVariable("THREADSMITH_LIVE_AGENT_REPORT_DIRECTORY")
                ?? Path.Combine(Path.GetTempPath(), "threadsmith-agent-live-reports", DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss")));
            Directory.CreateDirectory(reportDirectory);
            var failures = new List<string>();
            bool[] variants = [false, true];
            foreach (var role in Enum.GetValues<AgentRole>())
            {
                foreach (var control in variants)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var fixture = CreateFixture(role, control);
                    var caseName = AgentRoleNames.GetName(role) + (control ? "-control" : "-issue");
                    TestContext.Current.TestOutputHelper?.WriteLine($"Starting {caseName} on {profile.Name}.");
                    var caseRoot = Path.Combine(root, caseName);
                    Directory.CreateDirectory(caseRoot);
                    foreach (var file in fixture.Files)
                    {
                        var path = Path.Combine(caseRoot, file.Key);
                        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? caseRoot);
                        await File.WriteAllTextAsync(path, file.Value, cancellationToken);
                    }

                    var report = await RunCaseAsync(
                        role,
                        control,
                        fixture,
                        caseRoot,
                        models,
                        providerId,
                        profile,
                        models.TrustedProvider,
                        cancellationToken);
                    await File.WriteAllTextAsync(
                        Path.Combine(reportDirectory, caseName + ".json"),
                        JsonSerializer.Serialize(report, ReportJson),
                        cancellationToken);
                    failures.AddRange(report.Failures.Select(failure => caseName + ": " + failure));
                    TestContext.Current.TestOutputHelper?.WriteLine(
                        $"Finished {caseName}: {report.Outcome.Status}, {report.ModelRequests} model requests, "
                        + $"{report.ToolCalls} tool calls, {report.Failures.Count} evaluation failures.");
                }
            }

            Assert.True(failures.Count == 0, $"Live reports: {reportDirectory}{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
        }
        finally
        {
            var normalized = Path.GetFullPath(root);
            var parent = Path.GetFullPath(Path.GetTempPath());
            if (Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(normalized)) != Path.TrimEndingDirectorySeparator(parent)
                || !Path.GetFileName(normalized).StartsWith("threadsmith-agent-live-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Refusing to clean an unowned live fixture directory.");
            }

            Directory.Delete(normalized, recursive: true);
        }
    }

    private static async Task<LiveReport> RunCaseAsync(
        AgentRole role,
        bool control,
        LiveFixture fixture,
        string root,
        ModelServices models,
        string providerId,
        ModelProfile profile,
        IModelProvider provider,
        CancellationToken cancellationToken)
    {
        await using var events = new DomainEventStream();
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var prompts = TestPromptLoader.Instance;
        var registry = new ToolRegistry([new ReadFileTool(prompts), new ListFilesTool(prompts)]);
        var pipeline = new ToolInvocationPipeline(
            registry,
            new DefaultPolicyEngine(),
            new DenyApprovalPolicy(),
            events,
            sanitizer,
            NullLogger<ToolInvocationPipeline>.Instance,
            UnboundedBudget.Instance);
        var recorded = new RecordingProvider(provider);
        var selection = new AgentModelSelector(
            models.Catalog,
            new DefaultModelSelectionPolicy(models.Catalog),
            new ModelProviderInstructionResolver(models.Catalog, prompts),
            models.RoleModels,
            new ModelProviderInstructionResolver(models.TrustedCatalog, prompts));
        var options = new DelegateAgentsOptions { EnforceOperationalLimits = false };
        var assignment = new AgentAssignment
        {
            AssignmentId = AgentAssignmentId.New(),
            ChildRunId = RunId.New(),
            Role = role,
            Mode = AgentRunMode.ReadOnlyBaseline,
            Objective = fixture.Objective,
            Tasks = [fixture.Objective],
            InitialContext = "The complete relevant fixture consists of: " + string.Join(", ", fixture.Files.Keys)
                + ". Inspect these files before answering. No benchmark or test execution has been performed. Do not invent additional requirements.",
            OutputSchema = DelegateAgentsContract.ResponseSchema,
            StoppingCondition = "Return when the assigned question is supported by cited file evidence; report any genuine uncertainty.",
            Deadline = DateTimeOffset.MaxValue,
            Scope = new AgentAssignmentScope { IsOwnershipProven = true },
            Policy = new AgentPolicySnapshot
            {
                AllowedToolIds = ["read_file", "list_files"],
                DeniedToolIds = [DelegateAgentsContract.ToolId],
                TrustCeiling = RepositoryTrustLevel.TrustedRead,
                ModelSelectionRationale = "Live role assignment from the trusted configured catalog",
                ContextPolicyVersion = "agent-context/2",
                ToolPolicyVersion = "delegate-agents-read-only/1",
                ResultLimits = options.ResultLimits,
            },
            Budget = AgentResourceBudget.CreateTelemetryOnly(TimeSpan.Zero),
        };
        assignment = assignment with { Policy = selection.FreezePolicy(assignment) };
        var plan = new DelegationPlan
        {
            DelegationId = DelegationId.New(),
            Provenance = new DelegationProvenance
            {
                SessionId = SessionId.New(),
                ParentRunId = RunId.New(),
                RepositoryIdentity = root,
                BaselineIdentity = "synthetic-live-fixture/1",
                WorkspaceId = WorkspaceId.New(),
            },
            Assignments = [assignment],
            ParentBudget = assignment.Budget,
            AcceptedAt = DateTimeOffset.UtcNow,
        };
        var parent = new ToolExecutionContext(
            ToolInvocationId.New(),
            plan.Provenance.SessionId,
            plan.Provenance.ParentRunId,
            new ToolInvocationContext
            {
                WorkspaceId = plan.Provenance.WorkspaceId,
                RepositoryPath = root,
                TrustLevel = RepositoryTrustLevel.TrustedRead,
                ApprovedRoots = ["."],
                AllowedToolIds = ["read_file", "list_files"],
                RequestedBy = "model:parent",
            })
        { Phase = RunPhase.EvidenceCollection };
        var runner = new ModelExplorerAssignmentRunner(
            new AgentContextAssembler(evidence),
            new AgentFindingAdmission(evidence),
            selection,
            recorded,
            pipeline,
            evidence,
            new ChildAgentInstructionProvider(new RepositoryInstructionResolver(sanitizer), new PromptAppendLoader(sanitizer), []),
            sanitizer,
            options,
            parent,
            registry.GetRegistrations(plan.Provenance.SessionId, plan.Provenance.ParentRunId),
            prompts,
            trustedModels: recorded);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (double.TryParse(Environment.GetEnvironmentVariable("THREADSMITH_LIVE_AGENT_TIMEOUT_SECONDS"), out var seconds) && seconds > 0)
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        }

        var stopwatch = Stopwatch.StartNew();
        AgentRunOutcome outcome;
        try
        {
            outcome = await runner.RunAsync(plan, assignment, timeout.Token);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested
            && exception is ModelProviderException or InvalidDataException or InvalidOperationException
                or UnauthorizedAccessException or OperationCanceledException)
        {
            outcome = new AgentRunOutcome
            {
                AssignmentId = assignment.AssignmentId,
                ChildRunId = assignment.ChildRunId,
                Role = role,
                Generation = plan.Provenance.Generation,
                Status = exception is OperationCanceledException ? AgentRunStatus.Cancelled : AgentRunStatus.Failed,
                Usage = new AgentResourceUsage(),
                Reason = sanitizer.Sanitize(exception.Message),
                ModelProfileId = profile.Id,
                ModelSelection = assignment.Policy.ModelSelection,
            };
        }

        var joined = outcome.Status == AgentRunStatus.Completed && await runner.JoinAsync(plan, [outcome], static () => true, cancellationToken);
        var failures = new List<string>();
        var observations = new List<string>();
        var payload = outcome.Response ?? string.Empty;
        var inspectedPaths = evidence.Snapshot(plan.Provenance.SessionId)
            .Where(item => item.RunId == assignment.ChildRunId && item.Provenance.SourcePath is not null)
            .Select(item => item.Provenance.SourcePath ?? string.Empty).Distinct(StringComparer.Ordinal).ToArray();
        if (outcome.Status != AgentRunStatus.Completed || !joined)
        {
            failures.Add("The role did not complete and join successfully.");
        }

        if (outcome.ModelSelection is not { Source: AgentModelSelectionSource.RoleConfiguration } provenance
            || provenance.EffectiveProfileId != profile.Id || provenance.EffectiveProviderId != providerId)
        {
            failures.Add("The effective model did not match the configured role assignment.");
        }

        foreach (var path in fixture.Files.Keys)
        {
            if (!inspectedPaths.Any(inspected => inspected.Replace('\\', '/').EndsWith(path, StringComparison.Ordinal)))
            {
                observations.Add("Source not inspected: " + path);
            }

            if (await File.ReadAllTextAsync(Path.Combine(root, path), cancellationToken) != fixture.Files[path])
            {
                failures.Add("Read-only role modified a fixture file: " + path);
            }
        }

        if (role is AgentRole.Explorer or AgentRole.Implementer || !control)
        {
            foreach (var expected in fixture.ExpectedTerms)
            {
                if (!payload.Contains(expected, StringComparison.OrdinalIgnoreCase))
                {
                    observations.Add("Review manually for the expected concept: " + expected);
                }
            }
        }

        var repeated = recorded.ToolCalls.Count - recorded.ToolCalls.Distinct(StringComparer.Ordinal).Count();
        return new LiveReport(
            AgentRoleNames.GetName(role),
            control,
            providerId,
            profile.Name,
            profile.Id.Value,
            stopwatch.Elapsed.TotalSeconds,
            recorded.RequestCount,
            recorded.MaximumInputTokens,
            recorded.ToolCalls.Count,
            repeated,
            inspectedPaths,
            recorded.ToolCalls,
            recorded.ToolErrors.ToArray(),
            evidence.Snapshot(plan.Provenance.SessionId)
                .Where(item => item.RunId == assignment.ChildRunId)
                .Select(item => new LiveEvidence(item.EvidenceId.Value, item.Provenance.SourcePath, item.Content)).ToArray(),
            outcome,
            joined,
            failures,
            observations);
    }

    private sealed record LiveFixture(string Objective, IReadOnlyDictionary<string, string> Files, IReadOnlyList<string> ExpectedTerms);

    private sealed record LiveReport(
        string Role,
        bool Control,
        string Provider,
        string Model,
        Guid ProfileId,
        double Seconds,
        int ModelRequests,
        long MaximumInputTokens,
        int ToolCalls,
        int RepeatedToolCalls,
        IReadOnlyList<string> InspectedPaths,
        IReadOnlyList<string> ToolRequests,
        IReadOnlyList<string> ToolErrors,
        IReadOnlyList<LiveEvidence> Evidence,
        AgentRunOutcome Outcome,
        bool Joined,
        IReadOnlyList<string> Failures,
        IReadOnlyList<string> Observations);

    private sealed record LiveEvidence(Guid Id, string? Path, string Content);

    private sealed class RecordingProvider : IModelProvider
    {
        private readonly IModelProvider _inner;

        public RecordingProvider(IModelProvider inner)
        {
            _inner = inner;
        }

        public int RequestCount { get; private set; }

        public long MaximumInputTokens { get; private set; }

        public List<string> ToolCalls { get; } = [];

        public HashSet<string> ToolErrors { get; } = [];

        public async IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            RequestCount++;
            TestContext.Current.TestOutputHelper?.WriteLine($"Model request {RequestCount}; round {request.ToolContinuationRound}.");
            MaximumInputTokens = Math.Max(MaximumInputTokens, request.WireEstimate?.WireInputTokens ?? 0);
            ToolErrors.UnionWith(request.Messages.Where(message => message.SectionId == "child-tool-error")
                .Select(message => message.GetModelVisibleContent()));
            await foreach (var chunk in _inner.StreamAsync(request, cancellationToken))
            {
                if (chunk.Output is ToolRequestModelOutput tool)
                {
                    ToolCalls.Add(tool.ToolName + ":" + tool.ArgumentsJson);
                }

                yield return chunk;
            }
        }
    }
}
