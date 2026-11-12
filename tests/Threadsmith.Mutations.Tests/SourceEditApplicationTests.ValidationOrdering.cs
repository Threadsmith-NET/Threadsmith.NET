namespace Threadsmith.Mutations.Tests;

using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Policies.Commands;
using Threadsmith.Policies.Contracts;
using Threadsmith.Policies.Evaluation;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public sealed partial class SourceEditApplicationTests
{
    /// <summary>Every applied edit supplies completed feedback before validation, including shell commands and mixed batches.</summary>
    [Theory]
    [InlineData("dotnet_build", false, false)]
    [InlineData("dotnet_build", true, false)]
    [InlineData("test_run_targeted", false, false)]
    [InlineData("test_run_targeted", true, false)]
    [InlineData("dotnet build Example.csproj", false, false)]
    [InlineData("dotnet build Example.csproj", true, false)]
    [InlineData("dotnet test Example.csproj", false, false)]
    [InlineData("dotnet test Example.csproj", true, false)]
    [InlineData("dotnet_build", false, true)]
    [InlineData("test_run_targeted", true, true)]
    [InlineData("dotnet build Example.csproj", false, true)]
    [InlineData("dotnet test Example.csproj", true, true)]
    public async Task EditFeedbackPrecedesValidationDecisions(string validation, bool analyzerOnly, bool mixedBatch)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct);
        var analyzer = new AdvisoryAnalyzer([], pending: !analyzerOnly) { PendingAnalyzers = analyzerOnly };
        var edits = fixture.CreateApplication(analyzer: analyzer);
        var process = new RecordingValidationProcessManager();
        var provider = new ValidationOrderingProvider(validation, mixedBatch);
        var sanitizer = new SecretOutputSanitizer();
        var shell = OperatingSystem.IsWindows() ? "pwsh" : "bash";
        var platform = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
        var policy = new EffectiveSessionPolicy
        {
            BindingId = Guid.NewGuid(),
            Generation = 1,
            Fingerprint = "validation-ordering-fixture",
            RepositoryPath = fixture.Repository,
            MaximumTrust = RepositoryTrustLevel.TrustedMutation,
            ReadFolders = [fixture.Repository],
            EditableRoots = [fixture.Repository],
            MutationRoots = [fixture.Repository],
            AllowedExecutables = [shell, "dotnet"],
            RequireShellApproval = false,
            ResourceLimits = ImmutableDictionary<string, int>.Empty.Add("tools:parallel:maximumConcurrency", 4),
            CommandPolicies = ImmutableDictionary<string, PolicySnapshot>.Empty.Add(shell, PolicyCompiler.Compile(ShippedCommandPolicy.CreateDefaults(), null, null, platform, shell)),
        };
        var registry = new ToolRegistry([
            new SourceEditTool(edits, TestPromptLoader.Instance),
            new DotNetBuildTool(new ReceiptValidationService(), TestPromptLoader.Instance),
            new TargetedTestTool(new ReceiptValidationService(), TestPromptLoader.Instance),
            new RunProcessTool(process, TestPromptLoader.Instance, requireApproval: false, shellExecutable: shell),
        ]);
        var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new UnexpectedApproval(), fixture.Events, sanitizer, NullLogger<ToolInvocationPipeline>.Instance, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
        var evidence = new EvidenceStore(fixture.Events, sanitizer);
        var assembler = new ContextAssembler(evidence, new TokenEstimator(), new ContextPolicy(), new PromptAppendLoader(sanitizer), sanitizer, fixture.Events, TestPromptLoader.Instance);
        var waits = 0;
        var validationStarted = false;
        await using var subscription = fixture.Events.Subscribe((item, _) =>
        {
            if (item is ToolInvocationStarted started && started.ToolName == provider.ValidationTool)
            {
                validationStarted = true;
            }

            if (item is DiagnosticObserved { PendingSemanticEffectId: not null })
            {
                waits++;
                Assert.False(validationStarted);
                Assert.Empty(process.Requests);
                Assert.Equal(waits + (mixedBatch ? 1 : 0), provider.Requests.Count);
                var pending = analyzer.Latest!;
                analyzer.Publish(pending with
                {
                    Revision = pending.Revision + 1,
                    Pending = false,
                    Analyzers = analyzerOnly ? new() { Status = "completed", DocumentsInScope = 1, DocumentsAnalyzed = 1 } : new(),
                    Diagnostics = [new("TEST001", $"Feedback for edit {waits}", "Example.cs", 1, "Example", "net10.0", "initial")],
                });
            }

            return Task.CompletedTask;
        });
        var sessions = new SessionApplication(
            fixture.Events,
            provider,
            UnboundedBudget.Instance,
            sanitizer,
            NullLogger<SessionApplication>.Instance,
            pipeline,
            (_, _) => Task.FromResult(new ToolInvocationContext
            {
                RepositoryPath = fixture.Repository,
                WorkspaceId = fixture.WorkspaceId,
                TrustLevel = RepositoryTrustLevel.TrustedMutation,
                RequestedBy = "model",
                AllowedExecutables = policy.AllowedExecutables,
                SecurityPolicy = policy,
            }),
            contextAssembler: assembler,
            evidenceStore: evidence,
            toolRegistry: registry,
            correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
            prompts: TestPromptLoader.Instance,
            sourceEdits: edits);
        sessions.RegisterRestoredSession(fixture.SessionId);

        var run = await sessions.HandleAsync(new SubmitRequestCommand(fixture.SessionId, "Edit and validate."), ct);
        Assert.True(await sessions.HandleAsync(new WaitForRunCommand(run), ct));

        Assert.Equal(2, waits);
        Assert.True(validationStarted);
        Assert.Equal("class Final { }", await File.ReadAllTextAsync(fixture.SourcePath, ct));
        if (provider.ValidationTool == "run_process")
        {
            var launch = Assert.Single(process.Requests);
            Assert.True(Path.IsPathFullyQualified(launch.FileName));
            Assert.Equal(shell, Path.GetFileNameWithoutExtension(launch.FileName));
            Assert.Equal(fixture.Repository, launch.WorkingDirectory);
            Assert.Equal(shell == "pwsh" ? "-Command" : "-c", launch.Arguments[^2]);
            Assert.Contains(shell == "pwsh" ? "dotnet.exe'" : "/dotnet'", launch.Arguments[^1], OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            Assert.EndsWith(validation[validation.IndexOf(' ')..], launch.Arguments[^1], StringComparison.Ordinal);
        }
    }

    private sealed class RecordingValidationProcessManager : IProcessManager
    {
        public List<ProcessExecutionRequest> Requests { get; } = [];

        public IReadOnlyList<ActiveProcessInfo> ActiveProcesses => [];

        public Task<ProcessExecutionResult> RunAsync(ProcessExecutionRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(new ProcessExecutionResult(1, 0, "passed", string.Empty, false, false, false, TimeSpan.Zero));
        }
    }

    private sealed class ValidationOrderingProvider : IModelProvider
    {
        private readonly string _validation;
        private readonly bool _mixedBatch;

        public ValidationOrderingProvider(string validation, bool mixedBatch)
        {
            _validation = validation;
            _mixedBatch = mixedBatch;
        }

        public string ValidationTool => _validation.Contains(' ', StringComparison.Ordinal) ? "run_process" : _validation;

        public List<ModelStreamRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelChunk> StreamAsync(ModelStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            await Task.Yield();
            var round = Requests.Count - (_mixedBatch ? 1 : 0);
            if (_mixedBatch && round == 1)
            {
                Assert.Contains(request.Messages, message => message.IsError == true
                    && message.GetModelVisibleContent().Contains("Review completed compiler", StringComparison.Ordinal));
            }

            if (round is 2 or 3)
            {
                Assert.Contains(request.Messages, message => message.SectionId == "source-edit-feedback"
                    && message.GetModelVisibleContent().Contains($"Feedback for edit {round - 1}", StringComparison.Ordinal));
            }

            if (round <= 2)
            {
                var arguments = JsonSerializer.Serialize(new
                {
                    rationale = "Apply requested edit.",
                    mutations = new[] { new { type = "ReplaceText", relativePath = "Example.cs", expectedText = round <= 1 ? "Example" : "Changed", replacementText = round <= 1 ? "Changed" : "Final" } },
                });
                yield return new() { Output = new ToolRequestModelOutput("edit_source", arguments), FinishReason = ModelFinishReason.ToolCalls };
                if (round != 0)
                {
                    yield break;
                }
            }

            if (round is 0 or 3)
            {
                var arguments = ValidationTool == "run_process" ? JsonSerializer.Serialize(new { command = _validation })
                    : ValidationTool == "test_run_targeted" ? "{\"testId\":\"selected-test\"}" : "{\"targetPath\":\"Example.csproj\"}";
                yield return new() { Output = new ToolRequestModelOutput(ValidationTool, arguments), FinishReason = ModelFinishReason.ToolCalls };
                yield break;
            }

            Assert.Contains(request.Messages, message => message.Role == ModelMessageRole.Tool
                && message.ToolName == ValidationTool && message.IsError == false);
            yield return new() { Text = "Done.", FinishReason = ModelFinishReason.Stop };
        }
    }
}
