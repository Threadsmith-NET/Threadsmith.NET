namespace Threadsmith.ModelTooling.Integration.Tests;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.DotNet;
using Threadsmith.Execution;
using Xunit;

/// <summary>Verifies the Roslyn 5.9 and .NET 10.0.4xx semantic-toolchain compatibility contract.</summary>
public abstract class Plan117RoslynUpgradeTests
{
    private protected static string FixtureRoot => TestSemanticSolution.SourceRoot;

    private protected static SemanticLoadRequest CreateLoadRequest(string root)
    {
        return new SemanticLoadRequest(SessionId.New(), WorkspaceId.New(), root, Path.Combine(root, "SmallDotNetSolution.sln"), RepositoryTrustLevel.TrustedBuild);
    }

    private protected static string CopyFixtureToTemporaryRoot()
    {
        return TestSemanticSolution.CopyToTemporaryRoot("threadsmith-plan117");
    }

    private protected static async Task RemoveFixtureAnalyzerReferenceAsync(string root)
    {
        var contractsProject = Path.Combine(root, "Contracts", "Contracts.csproj");
        var projectText = await File.ReadAllTextAsync(contractsProject, TestContext.Current.CancellationToken);
        var project = XDocument.Parse(projectText);
        var analyzer = project.Descendants("Analyzer").Single(element => element.Attribute("Include")?.Value.EndsWith("Threadsmith.SemanticFixtures.Roslyn59.dll", StringComparison.OrdinalIgnoreCase) == true);
        analyzer.Remove();
        await File.WriteAllTextAsync(contractsProject, project.ToString(), TestContext.Current.CancellationToken);
    }

    private protected static void SetFixtureAnalyzerPath(string root, string analyzerPath)
    {
        var contractsProject = Path.Combine(root, "Contracts", "Contracts.csproj");
        var document = XDocument.Load(contractsProject);
        var analyzer = document.Descendants("Analyzer").Single(element => element.Attribute("Include")?.Value.EndsWith("Threadsmith.SemanticFixtures.Roslyn59.dll", StringComparison.OrdinalIgnoreCase) == true);
        analyzer.SetAttributeValue("Include", analyzerPath);
        document.Save(contractsProject);
    }

    private protected static void DeleteOwnedTemporaryRoot(string path)
    {
        var temporaryParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        var ownedRoot = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(ownedRoot), temporaryParent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(ownedRoot).StartsWith("threadsmith-plan117-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing cleanup outside the exact Plan 117 test directory.");
        }

        Directory.Delete(ownedRoot, recursive: true);
    }

    private protected sealed class OwnedMeasurementInputs : IDisposable
    {
        private readonly string _root;

        public OwnedMeasurementInputs(string root)
        {
            _root = root;
        }

        public void Dispose()
        {
            DeleteOwnedTemporaryRoot(_root);
        }
    }

    private protected sealed class RecordingLogger<TCategory> : ILogger<TCategory>
    {
        public ConcurrentQueue<LogMessage> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Enqueue(new LogMessage(logLevel, formatter(state, exception)));
        }
    }

    private protected sealed class FixtureSourceReader : ICodeExploreSourceReader
    {
        private readonly string _root;

        internal FixtureSourceReader(string root)
        {
            _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        }

        public bool IsPathAllowed(string path)
        {
            var normalized = Path.GetFullPath(path);
            return File.Exists(normalized) && normalized.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        public async Task<CodeExploreSourceText> ReadTextAsync(string path, int maximumBytes, CancellationToken cancellationToken = default)
        {
            if (!IsPathAllowed(path))
            {
                throw new UnauthorizedAccessException("The source path is outside the semantic fixture.");
            }

            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            if (bytes.Length > maximumBytes)
            {
                throw new InvalidOperationException("The semantic fixture source exceeds the read limit.");
            }

            return new CodeExploreSourceText(Path.GetFullPath(path), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes), Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }
    }

    private protected sealed record LogMessage(LogLevel Level, string Text);
}

/// <summary>Exercises one independently owned semantic or model contract.</summary>
public sealed class Plan117RoslynUpgradeTestsCodeExplore_TruncatedGeneratedSource_SuppressesPathContinuations : Plan117RoslynUpgradeTests
{
    /// <summary>Generated source truncated by either source budget cannot advertise unresolved path continuations.</summary>
    [Theory]
    [InlineData(60, 1000)]
    [InlineData(1000, 60)]
    public static async Task CodeExplore_TruncatedGeneratedSource_SuppressesPathContinuations(int perFileBudget, int totalBudget)
    {
        using var fixture = new TestSemanticSolution();
        var root = fixture.Root;
        var request = CreateLoadRequest(root);
        await using var events = new DomainEventStream();
        await using var registry = new SemanticEngineRegistry(events, NullLoggerFactory.Instance, TestPromptLoader.Instance, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
        var queries = new AdvancedSemanticQueryService(registry, TestPromptLoader.Instance, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
        await registry.LoadAsync(request, TestContext.Current.CancellationToken);
        await registry.GetEngine(request.WorkspaceId).WaitForWarmAsync(TestContext.Current.CancellationToken);
        var result = await queries.QueryCodeExploreAsync(request.WorkspaceId, new CodeExploreRequest { Query = "SmallSolution.Contracts.GeneratedByRoslyn59", ExactSymbolAnchors = ["T:SmallSolution.Contracts.GeneratedByRoslyn59"], Limits = new CodeExploreLimits { MaximumPerFileSourceCharacters = perFileBudget, MaximumSourceCharacters = totalBudget, }, }, new FixtureSourceReader(root), cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEmpty(result.FileSections);
        Assert.All(result.FileSections, section =>
        {
            Assert.True(section.IsGenerated);
            Assert.NotEqual(CodeExploreSourceCompleteness.Complete, section.Source.Completeness);
            Assert.NotEmpty(section.Source.OmittedRanges);
            Assert.Null(section.Source.ContinuationAnchor);
        });
        Assert.DoesNotContain(result.ContinuationTargets, target => target.Kind == CodeExploreAnchorKind.Path);
    }
}

/// <summary>Exercises one independently owned semantic or model contract.</summary>
public sealed class Plan117RoslynUpgradeTestsCodeExplore_ExactIdentityCannotClaimAbsenceFromOmittedCandidateProjects : Plan117RoslynUpgradeTests
{
    /// <summary>Omitted projects prevent claims of exact absence or unique ownership while warming is blocked.</summary>
    [Theory]
    [InlineData("P:SmallSolution.Contracts.GeneratedByRoslyn59.Value", true)]
    [InlineData("Owner", false)]
    [InlineData("T:Decoy.Owner", true)]
    public static async Task CodeExplore_ExactIdentityCannotClaimAbsenceFromOmittedCandidateProjects(string anchor, bool symbolId)
    {
        var root = CopyFixtureToTemporaryRoot();
        var analyzerPath = Path.Combine(FixtureRoot, "Analyzers", "Threadsmith.SemanticFixtures.Roslyn59.dll");
        var markerPath = Path.Combine(root, "App", "generator.block");
        var pipeName = Guid.NewGuid().ToString("N")[..16];
        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var events = new DomainEventStream();
        await using var registry = new SemanticEngineRegistry(events, NullLoggerFactory.Instance, TestPromptLoader.Instance, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
        var released = false;
        try
        {
            var contractsPath = Path.Combine(root, "Contracts", "Contracts.csproj");
            var contracts = XDocument.Load(contractsPath);
            contracts.Descendants("Analyzer").Remove();
            contracts.Save(contractsPath);
            var appPath = Path.Combine(root, "App", "App.csproj");
            var app = XDocument.Load(appPath);
            app.Root!.Add(new XElement(
                "ItemGroup",
                new XElement("Analyzer", new XAttribute("Include", analyzerPath)),
                new XElement("AdditionalFiles", new XAttribute("Include", markerPath))));
            app.Save(appPath);
            await File.WriteAllTextAsync(Path.Combine(root, "Contracts", "Decoy.cs"), "namespace Decoy; public class Owner { public void Calculate() {} } public class GeneratedByRoslyn59 { public int Value => 1; } public class CS {}", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, "App", "Program.cs"), "namespace Wanted; public class Owner { public void Calculate() {} } public class CS {}", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(markerPath, pipeName, TestContext.Current.CancellationToken);
            var load = await registry.LoadForBindingAsync(CreateLoadRequest(root), TestContext.Current.CancellationToken);
            await registry.CompleteInitialPublicationAsync(load, true, TestContext.Current.CancellationToken);
            await TestAnalyzerBarrier.WaitForConnectionAsync(pipe, registry.GetEngine(load.WorkspaceId).WaitForWarmAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
            var queries = new AdvancedSemanticQueryService(registry, TestPromptLoader.Instance, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
            var beforeWarm = registry.GetEngine(load.WorkspaceId).CaptureCodeExploreReadinessSnapshot();
            var truncated = await queries.QueryCodeExploreAsync(load.WorkspaceId, new CodeExploreRequest { Query = "Contracts/Decoy.cs", PathAnchors = [new CodeExplorePathAnchor { Path = "Contracts/Decoy.cs", SelectionMode = CodeExplorePathSelectionMode.WholeFile }], Limits = new CodeExploreLimits { MaximumSourceCharacters = 1, MaximumPerFileSourceCharacters = 1 }, }, new FixtureSourceReader(root), cancellationToken: TestContext.Current.CancellationToken);
            var continuation = Assert.Single(truncated.ContinuationTargets);
            var request = new CodeExploreRequest
            {
                Query = "explain exact declaration",
                SymbolIds = symbolId ? [anchor] : [],
                ExactSymbolAnchors = symbolId ? [] : [anchor],
                Limits = new CodeExploreLimits
                {
                    MaximumAnchors = 1,
                },
            };
            using var queryCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var pendingQuery = queries.QueryCodeExploreAsync(load.WorkspaceId, request, new FixtureSourceReader(root), cancellationToken: queryCancellation.Token);
            Assert.False(pendingQuery.IsCompleted);
            await queryCancellation.CancelAsync();
#pragma warning disable VSTHRD003 // The test owns the query task and joins its explicitly cancelled waiter.

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendingQuery);
#pragma warning restore VSTHRD003
            await ReleaseGeneratorAsync();
            await registry.GetEngine(load.WorkspaceId).WaitForWarmAsync(TestContext.Current.CancellationToken);
            var afterWarm = registry.GetEngine(load.WorkspaceId).CaptureCodeExploreReadinessSnapshot();
            Assert.True(afterWarm.Generation > beforeWarm.Generation);
            Assert.Equal(beforeWarm.SourceGeneration, afterWarm.SourceGeneration);
            var replayed = await queries.QueryCodeExploreAsync(load.WorkspaceId, new CodeExploreRequest { Query = continuation.Anchor, PathAnchors = [new CodeExplorePathAnchor { Path = continuation.FilePath ?? continuation.Anchor, Line = continuation.StartLine, EndLine = continuation.EndLine, SelectionMode = continuation.SelectionMode ?? CodeExplorePathSelectionMode.ExactLineRange, ExpectedFileSha256 = continuation.ExpectedFileSha256, ExpectedWorkspaceGeneration = continuation.WorkspaceGeneration, },], }, new FixtureSourceReader(root), cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(truncated.WorkspaceGeneration, replayed.WorkspaceGeneration);
            Assert.NotEmpty(replayed.FileSections);
            Assert.All(replayed.FileSections, section => Assert.Equal(CodeExploreSourceCompleteness.Complete, section.Source.Completeness));
            var result = await queries.QueryCodeExploreAsync(load.WorkspaceId, request, new FixtureSourceReader(root), cancellationToken: TestContext.Current.CancellationToken);
            Assert.Contains(result.ResolvedAnchors, resolution => resolution.Outcome == (anchor == "Owner" ? CodeExploreResolutionOutcome.Ambiguous : CodeExploreResolutionOutcome.Resolved));
            Assert.NotEmpty(result.FileSections);
        }
        finally
        {
            try
            {
                await ReleaseGeneratorAsync();
            }
            finally
            {
                await registry.DisposeAsync();
                DeleteOwnedTemporaryRoot(root);
            }
        }

        async Task ReleaseGeneratorAsync()
        {
            if (released)
            {
                return;
            }

            released = true;
            File.Delete(markerPath);
            await TestAnalyzerBarrier.ReleaseAsync(pipe);
        }
    }
}

/// <summary>Exercises one independently owned semantic or model contract.</summary>
public sealed class Plan117RoslynUpgradeTestsCodeExplore_PhysicalGeneratedLookingFile_DetectsDiskDrift : Plan117RoslynUpgradeTests
{
    /// <summary>A physical generated-looking source file retains ordinary path and drift enforcement.</summary>
    [Fact]
    public static async Task CodeExplore_PhysicalGeneratedLookingFile_DetectsDiskDrift()
    {
        var temporaryRoot = CopyFixtureToTemporaryRoot();
        try
        {
            await RemoveFixtureAnalyzerReferenceAsync(temporaryRoot);
            var sourcePath = Path.Combine(temporaryRoot, "Contracts", "PhysicalGenerated.g.cs");
            await File.WriteAllTextAsync(sourcePath, "namespace SmallSolution.Contracts; public sealed class PhysicalGenerated { public int Value => 1; }", TestContext.Current.CancellationToken);
            var request = CreateLoadRequest(temporaryRoot);
            await using var events = new DomainEventStream();
            await using var registry = new SemanticEngineRegistry(events, NullLoggerFactory.Instance, TestPromptLoader.Instance, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
            var queries = new AdvancedSemanticQueryService(registry, TestPromptLoader.Instance, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
            var load = await registry.LoadAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(SemanticConfidenceLevel.PartialCompilation, load.Confidence);
            await registry.GetEngine(request.WorkspaceId).WaitForWarmAsync(TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(sourcePath, "namespace SmallSolution.Contracts; public sealed class PhysicalGenerated { public int Value => 2; }", TestContext.Current.CancellationToken);
            var result = await queries.QueryCodeExploreAsync(request.WorkspaceId, new CodeExploreRequest { Query = "SmallSolution.Contracts.PhysicalGenerated", ExactSymbolAnchors = ["T:SmallSolution.Contracts.PhysicalGenerated"], }, new FixtureSourceReader(temporaryRoot), cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotEmpty(result.FileSections);
            Assert.All(result.FileSections, section =>
            {
                Assert.Equal(CodeExploreSourceCompleteness.Drifted, section.Source.Completeness);
                Assert.Empty(section.Source.NumberedLines);
            });
        }
        finally
        {
            DeleteOwnedTemporaryRoot(temporaryRoot);
        }
    }
}

/// <summary>Exercises one independently owned semantic or model contract.</summary>
public sealed class Plan117RoslynUpgradeTestsSemanticEngine_LoadMeasurement_ClassifiesTextOnlyAndCancellation : Plan117RoslynUpgradeTests
{
    /// <summary>Text-only completion and cancellation each emit exactly one classified load measurement.</summary>
    [Fact]
    public static async Task SemanticEngine_LoadMeasurement_ClassifiesTextOnlyAndCancellation()
    {
        using var fixture = new TestSemanticSolution();
        var root = fixture.Root;
        var textLogger = new RecordingLogger<SemanticEngine>();
        await using (var events = new DomainEventStream())
        await using (var engine = new SemanticEngine(events, textLogger, TestPromptLoader.Instance, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow)))
        {
            var request = CreateLoadRequest(root) with
            {
                TrustLevel = RepositoryTrustLevel.TrustedRead,
            };
            var load = await engine.LoadAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(SemanticConfidenceLevel.TextOnly, load.Confidence);
            Assert.Single(textLogger.Messages, message => message.Level == LogLevel.Information && message.Text.StartsWith("Semantic load completed:", StringComparison.Ordinal));
        }

        var cancellationLogger = new RecordingLogger<SemanticEngine>();
        await using (var events = new DomainEventStream())
        await using (var engine = new SemanticEngine(events, cancellationLogger, TestPromptLoader.Instance, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow)))
        using (var cancellation = new CancellationTokenSource())
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.LoadAsync(CreateLoadRequest(root), cancellation.Token));
            Assert.Single(cancellationLogger.Messages, message => message.Level == LogLevel.Information && message.Text.StartsWith("Semantic load cancelled:", StringComparison.Ordinal));
        }
    }
}

/// <summary>Exercises one independently owned semantic or model contract.</summary>
public sealed class Plan117RoslynUpgradeTestsSemanticEngine_PostCommitPublicationFailure_RetainsUsableState : Plan117RoslynUpgradeTests
{
    /// <summary>A publication failure after state commit cannot dispose the workspace now owned by the engine.</summary>
    [Fact]
    public static async Task SemanticEngine_PostCommitPublicationFailure_RetainsUsableState()
    {
        using var fixture = new TestSemanticSolution();
        await using var events = new DomainEventStream();
        await using var subscription = events.Subscribe((domainEvent, _) => domainEvent is SemanticConfidenceChanged ? Task.FromException(new InvalidOperationException("Injected post-commit publication failure.")) : Task.CompletedTask);
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.LoadAsync(CreateLoadRequest(fixture.Root), TestContext.Current.CancellationToken));
        Assert.Equal(SemanticConfidenceLevel.PartialCompilation, engine.Confidence);
        Assert.NotEmpty(engine.CaptureAdvancedSnapshot().CompiledProjects);
    }
}

/// <summary>Exercises one independently owned semantic or model contract.</summary>
public sealed class Plan117RoslynUpgradeTestsSemanticEngine_BlockedAnalyzerConstruction_CancelsWithoutPublishingState : Plan117RoslynUpgradeTests
{
    /// <summary>Blocked analyzer and generator construction cannot prevent load or full-refresh cancellation.</summary>
    [Theory]
    [InlineData("analyzer", false, false)]
    [InlineData("generator", false, true)]
    [InlineData("generator", true, true)]
    public static async Task SemanticEngine_BlockedAnalyzerConstruction_CancelsWithoutPublishingState(string component, bool refresh, bool retireOwner)
    {
        var temporaryRoot = CopyFixtureToTemporaryRoot();
        var pipeName = Guid.NewGuid().ToString("N")[..16];
        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var events = new DomainEventStream();
        var publishedEvents = new ConcurrentQueue<IDomainEvent>();
        await using var subscription = events.Subscribe((domainEvent, _) =>
        {
            publishedEvents.Enqueue(domainEvent);
            return Task.CompletedTask;
        });
        var logger = new RecordingLogger<SemanticEngine>();
        var clock = new ObservedTimeProvider();
        await using var engine = new SemanticEngine(events, logger, TestPromptLoader.Instance, timeProvider: clock);
        var request = CreateLoadRequest(temporaryRoot);
        var analyzerPath = Path.Combine(temporaryRoot, "Contracts", "BlockingProbe.dll");
        try
        {
            // Arrange: a refresh starts with a usable workspace that must survive cancellation.
            if (refresh)
            {
                SetFixtureAnalyzerPath(temporaryRoot, Path.Combine(FixtureRoot, "Analyzers", "Threadsmith.SemanticFixtures.Roslyn59.dll"));
                var initialLoad = await engine.LoadAsync(request, TestContext.Current.CancellationToken);
                Assert.Equal(SemanticConfidenceLevel.PartialCompilation, initialLoad.Confidence);
                await engine.WaitForWarmAsync(TestContext.Current.CancellationToken);
                publishedEvents.Clear();
            }

            var originalInventory = engine.GetRefreshInventory();
            var bytes = TestAnalyzerBinary.EmitBlockingConstructor(component, pipeName, TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(analyzerPath, bytes, TestContext.Current.CancellationToken);
            SetFixtureAnalyzerPath(temporaryRoot, analyzerPath);
            // Act: cancel only after the real Roslyn constructor has entered its synchronous barrier.
            var operation = refresh ? engine.RefreshFullAsync(cancellation.Token) : engine.LoadAsync(request, cancellation.Token);
            await TestAnalyzerBarrier.WaitForConnectionAsync(pipe, operation, TestContext.Current.CancellationToken);
            if (retireOwner)
            {
                var retirement = engine.RetirePreparationAsync(retainCurrentPreparation: false, TestContext.Current.CancellationToken);
                await clock.WaitForTimerAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
                clock.Advance(TimeSpan.FromSeconds(2));
                var exception = await Assert.ThrowsAnyAsync<Exception>(() => operation.WaitAsync(TestContext.Current.CancellationToken));
                Assert.True(exception is InvalidOperationException or OperationCanceledException);
                await retirement;
            }
            else
            {
                await cancellation.CancelAsync();
                await clock.WaitForTimerAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
                clock.Advance(TimeSpan.FromSeconds(2));
                var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TestContext.Current.CancellationToken));
                Assert.Equal(cancellation.Token, exception.CancellationToken);
            }

            // Assert: cancellation completes while construction is blocked, without publishing replacement state.
            Assert.Empty(publishedEvents);
            Assert.Same(originalInventory, engine.GetRefreshInventory());
            Assert.Equal(refresh ? SemanticConfidenceLevel.FullSemantic : SemanticConfidenceLevel.None, engine.Confidence);
            Assert.Single(logger.Messages, message => message.Text.StartsWith("Semantic load cancelled:", StringComparison.Ordinal) || (retireOwner && message.Text.StartsWith("Semantic load failed:", StringComparison.Ordinal)));
            if (refresh)
            {
                Assert.NotEmpty(await engine.FindSymbolsAsync("UpperValue", TestContext.Current.CancellationToken));
            }
            else
            {
                Assert.Empty(engine.Projects);
            }
        }
        finally
        {
            // Release abandoned construction before removing its owned fixture inputs.
            try
            {
                await TestAnalyzerBarrier.ReleaseAsync(pipe);
            }
            finally
            {
                DeleteOwnedTemporaryRoot(temporaryRoot);
            }
        }
    }
}

/// <summary>Exercises one independently owned semantic or model contract.</summary>
public sealed class Plan117RoslynUpgradeTestsSemanticEngine_AvailableOlderRepositorySdk_LoadsSemantics : Plan117RoslynUpgradeTests
{
    /// <summary>An explicitly available older .NET 10 SDK remains loadable through the target host.</summary>
    [Fact]
    public static async Task SemanticEngine_AvailableOlderRepositorySdk_LoadsSemantics()
    {
        var olderSdk = Environment.GetEnvironmentVariable("THREADSMITH_PLAN117_OLDER_SDK_VERSION");
        if (string.IsNullOrWhiteSpace(olderSdk))
        {
            Assert.Skip("Set the installed older .NET 10 SDK version for the Plan 117 compatibility matrix.");
        }

        if (!Version.TryParse(olderSdk, out var parsedSdk) || parsedSdk.Major != 10)
        {
            throw new InvalidOperationException("The older SDK matrix value must be a .NET 10 version.");
        }

        var temporaryRoot = CopyFixtureToTemporaryRoot();
        try
        {
            await RemoveFixtureAnalyzerReferenceAsync(temporaryRoot);
            await File.WriteAllTextAsync(
                Path.Combine(temporaryRoot, "global.json"),
                $$"""
                {
                  "sdk": {
                    "version": "{{parsedSdk}}",
                    "rollForward": "disable",
                    "allowPrerelease": false
                  }
                }
                """,
                TestContext.Current.CancellationToken);
            await using var events = new DomainEventStream();
            await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
            var load = await engine.LoadAsync(CreateLoadRequest(temporaryRoot), TestContext.Current.CancellationToken);
            Assert.Equal(SemanticConfidenceLevel.PartialCompilation, load.Confidence);
            await engine.WaitForWarmAsync(TestContext.Current.CancellationToken);
            Assert.Equal(SemanticConfidenceLevel.FullSemantic, engine.Confidence);
            Assert.Empty(load.Diagnostics);
        }
        finally
        {
            DeleteOwnedTemporaryRoot(temporaryRoot);
        }
    }
}
