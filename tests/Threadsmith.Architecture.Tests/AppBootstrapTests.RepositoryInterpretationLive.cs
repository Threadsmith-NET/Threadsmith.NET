namespace Threadsmith.Architecture.Tests;

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Threadsmith.App;
using Threadsmith.Core;
using Threadsmith.Interaction.Coordination;
using Threadsmith.Models;
using Threadsmith.RepositoryIntelligence;
using Threadsmith.Tools;
using Xunit;

public static partial class AppBootstrapTests
{
    private static readonly JsonSerializerOptions InterpretationReportJsonOptions = new() { WriteIndented = true };

    /// <summary>Exercises Task 6 using the requested real model and host execution path.</summary>
    [Fact]
    [Trait("Category", "LiveIntegration")]
    public static async Task RepositoryInterpretationUsesSharedRealModelExecutionAsync()
    {
        if (Environment.GetEnvironmentVariable("THREADSMITH_LIVE_REPOSITORY_INTERPRETATION") != "1")
        {
            Assert.Skip("Set THREADSMITH_LIVE_REPOSITORY_INTERPRETATION=1 for Task 6 GPT-6.1-Sol low verification.");
        }

        var token = TestContext.Current.CancellationToken;
        using var temporary = new TemporaryDirectory("repository-interpretation-live");
        var root = temporary.Root;
        var reports = Path.GetFullPath(Environment.GetEnvironmentVariable("THREADSMITH_LIVE_REPOSITORY_INTERPRETATION_REPORT_DIRECTORY")
            ?? Path.Combine(Path.GetTempPath(), "threadsmith-interpretation-live-reports", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(reports);
        await File.WriteAllTextAsync(temporary.GetPath("App.csproj"), "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>", token);
        await File.WriteAllTextAsync(temporary.GetPath("README.md"), "# Cache decision\nThe cache lifetime is 60 seconds because frequent refresh prevents stale credentials.\nThe invariant is to never serve credentials beyond the cache lifetime.\n", token);
        await RunProfileFixtureGitAsync(root, ["init", "-b", "main"], token);
        await RunProfileFixtureGitAsync(root, ["add", "."], token);
        await RunProfileFixtureGitAsync(root, ["-c", "user.name=Live Test", "-c", "user.email=test@example.invalid", "-c", "commit.gpgSign=false", "commit", "-m", "Document bounded cache lifetime"], token);
        try
        {
            await WithComposedHostAsync(
                ConfigurationBootstrap.ResolvePaths(root),
                async (foundation, applications, cancellationToken) =>
            {
                var events = new ConcurrentQueue<IDomainEvent>();
                await using var subscription = foundation.Events.Subscribe((item, _) =>
                {
                    events.Enqueue(item);
                    return Task.CompletedTask;
                });
                var adapter = new LiveInterpretationTool(foundation.ToolPipeline, applications.BoundedInference, foundation.PromptLoader, foundation.OperationalLimits.Git);
                foundation.ToolRegistry.RegisterOrReplace(adapter, new ToolActivitySource(ToolActivitySourceKind.BuiltIn, "task6-live-test"));
                var presenter = new InteractionPresenter(applications.Dispatcher, foundation.Projections);
                await presenter.SelectActiveModelAsync(new ModelProfileId(Guid.Parse("33049c94-3795-eb5f-abc6-42c1dbca57ff")), cancellationToken);
                await presenter.SetActiveReasoningAsync(new ReasoningLevel("low"), cancellationToken);
                var selected = await presenter.GetActiveModelSelectionAsync(cancellationToken);
                Assert.Equal("GPT-6.1-Sol", selected.Profile.Name);
                Assert.Equal(new ReasoningLevel("low"), selected.ReasoningLevel);
                var session = await presenter.CreateSessionAsync("Task 6 live interpretation", cancellationToken);
                await presenter.OpenRepositoryAsync(session, root, RepositoryTrustLevel.TrustedRead, cancellationToken);
                var run = await presenter.SubmitAsync(session, "Synthetic live verification: invoke repository_interpretation_live exactly once with {}. The adapter uses the host's bounded internal interpreter. Do not invoke other tools, builds, tests, or delegation. Report the bounded result and its limitations.", cancellationToken);
                var succeeded = await presenter.WaitAsync(run, cancellationToken);
                var report = Path.Combine(reports, "task6.json");
                await File.WriteAllTextAsync(
                    report,
                    JsonSerializer.Serialize(
                        new
                        {
                            Succeeded = succeeded,
                            Selection = selected,
                            adapter.Result,
                            adapter.Responses,
                            Events = events.Select(item => new { Type = item.GetType().Name, Data = JsonSerializer.SerializeToElement(item, item.GetType()) }),
                        },
                        InterpretationReportJsonOptions),
                    cancellationToken);
                TestContext.Current.TestOutputHelper?.WriteLine($"Task 6 live report: {report}");
                Assert.True(succeeded, report);
                var result = Assert.IsType<RepositoryInterpretationResult>(adapter.Result);
                Assert.Equal("Findings", result.Response.Outcome);
                Assert.Equal("low", result.Model!.Reasoning, ignoreCase: true);
                Assert.Equal(selected.Profile.Id, result.Model.ProfileId);
                Assert.InRange(result.ModelCalls, 2, 6);
                Assert.Contains(result.Evidence, item => item.Source.Path == "README.md");
                Assert.NotEmpty(result.Response.SupportingEvidenceIds);
                Assert.All(result.Response.SupportingEvidenceIds, id => Assert.Contains(result.Evidence, item => item.Id == id));
                Assert.All(result.Evidence, item => Assert.NotNull(item.Source.Revision));
                Assert.Equal("NotRequested", result.LastPacket.HistoryCoverage);
                Assert.Contains(events.OfType<DiagnosticObserved>(), item => item.Code == "HostInferenceStarted");
                Assert.Contains(events.OfType<ToolInvocationStarted>(), item => item.ParentToolInvocationId is not null && item.ToolName == "git_show");
                Assert.False(Directory.Exists(temporary.GetPath(Path.Combine(".threadsmith", "repository-intelligence"))));
                return true;
            },
                token);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in Directory.EnumerateFiles(temporary.GetPath(".git"), "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
        }
    }

    private sealed record LiveInterpretationInput;

    private sealed class LiveInterpretationTool : Tool<LiveInterpretationInput, RepositoryInterpretationResult>
    {
        private readonly IToolInvocationPipeline _pipeline;
        private readonly RepositoryInterpreter _interpreter;
        private readonly RecordingLiveInference _recording;
        private readonly RepositoryIntelligenceResourceLimits _limits;

        public LiveInterpretationTool(IToolInvocationPipeline pipeline, IBoundedModelInference inference, IPromptLoader prompts, GitResourceLimits git)
        {
            _pipeline = pipeline;
            _recording = new RecordingLiveInference(inference);
            _interpreter = new RepositoryInterpreter(_recording, prompts);
            _limits = new(32, 4, 6) { Git = git };
            Definition = ToolDefinitionFactory.Create<LiveInterpretationInput, RepositoryInterpretationResult>(
                "repository_interpretation_live", "Test-only bounded interpretation verification.", ToolCategory.RepositoryInspection, RepositoryTrustLevel.TrustedRead, ApprovalLevel.None, ToolSideEffect.ReadOnly, TimeSpan.FromMinutes(5), 262144) with
            { SubagentAvailable = false };
        }

        public RepositoryInterpretationResult? Result { get; private set; }

        public IReadOnlyList<string> Responses => _recording.Responses;

        public override ToolDefinition Definition { get; }

        public override async Task<ToolExecution<RepositoryInterpretationResult>> ExecuteAsync(LiveInterpretationInput input, ToolExecutionContext context, CancellationToken cancellationToken = default)
        {
            var profile = await new RepositoryProfileCollector(_pipeline, _limits.Evidence, _limits.Git).CaptureAsync(new RepositoryProfileSelection(), context, cancellationToken);
            var selection = new RepositoryEvidenceSelection
            {
                Question = "What cache lifetime and surviving invariant are documented in README.md, and what rationale is directly supported? Request an Inspect expansion for README.md lines 2..3 before concluding; App.csproj alone does not answer the question.",
                Mode = RepositoryEvidenceMode.CurrentSnapshot,
                MaximumFiles = 32,
                MaximumCommits = 0,
                MaximumInputBytes = _limits.Evidence.MaximumInputBytes,
                MaximumPacketBytes = _limits.Evidence.MaximumPacketBytes,
                MaximumOutputBytes = _limits.Evidence.MaximumOutputBytes,
            };
            using var collector = new RepositoryEvidenceCollector(_pipeline, profile, selection, _limits, context, cancellationToken);
            var packet = await collector.CollectAsync(selection, cancellationToken: cancellationToken);
            Result = await _interpreter.InterpretAsync(RepositoryInterpretationKind.Intent, collector, selection, packet, context, 6, cancellationToken: cancellationToken);
            return new ToolExecution<RepositoryInterpretationResult>(Result, [], false);
        }

        protected override void ValidateInput(LiveInterpretationInput input) => ArgumentNullException.ThrowIfNull(input);
    }

    private sealed class RecordingLiveInference : IBoundedModelInference
    {
        private readonly IBoundedModelInference _inner;

        public RecordingLiveInference(IBoundedModelInference inner) => _inner = inner;

        public List<string> Responses { get; } = [];

        public IBoundedModelOperation Open(ToolExecutionContext context, int maximumCalls, int maximumResultBytes)
            => new RecordingLiveOperation(_inner.Open(context, maximumCalls, maximumResultBytes), Responses);
    }

    private sealed class RecordingLiveOperation : IBoundedModelOperation
    {
        private readonly IBoundedModelOperation _inner;
        private readonly List<string> _responses;

        public RecordingLiveOperation(IBoundedModelOperation inner, List<string> responses)
        {
            _inner = inner;
            _responses = responses;
        }

        public BoundedModelSelection Selection => _inner.Selection;

        public void Dispose() => _inner.Dispose();

        public async Task<string> ExecuteAsync(BoundedModelRequest request, CancellationToken cancellationToken = default)
        {
            var response = await _inner.ExecuteAsync(request, cancellationToken);
            _responses.Add(response);
            return response;
        }
    }
}
