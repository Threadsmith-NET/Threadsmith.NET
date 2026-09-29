namespace Threadsmith.ModelTooling.Tests;

using System.Collections.Concurrent;
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
public static class Plan117RoslynUpgradeTests
{
    /// <summary>C# 14, multi-targeting, linked input, and source-generated symbols flow through the established engine.</summary>
    [Fact]
    public static async Task SemanticEngine_Roslyn59_LoadsModernSyntaxAndGeneratedSymbols()
    {
        var root = FixtureRoot;
        var request = CreateLoadRequest(root);
        await using var events = new DomainEventStream();
        await using var registry = new SemanticEngineRegistry(events, NullLoggerFactory.Instance);
        var queries = new AdvancedSemanticQueryService(registry, TestPromptLoader.Instance);

        var load = await registry.LoadAsync(request, TestContext.Current.CancellationToken);
        var extensionProperty = await registry.FindSymbolsAsync(
            request.WorkspaceId,
            "UpperValue",
            TestContext.Current.CancellationToken);
        var extensionMethod = await registry.FindSymbolsAsync(
            request.WorkspaceId,
            "Repeat",
            TestContext.Current.CancellationToken);
        var staticExtension = await registry.FindSymbolsAsync(
            request.WorkspaceId,
            "Create",
            TestContext.Current.CancellationToken);
        var fieldBackedType = await registry.FindSymbolsAsync(
            request.WorkspaceId,
            "FieldBackedValue",
            TestContext.Current.CancellationToken);
        var generatedDocuments = await queries.QueryGeneratedCodeAsync(
            request.WorkspaceId,
            new GeneratedCodeQuery { IncludeContent = true },
            TestContext.Current.CancellationToken);
        var codeExplore = await queries.QueryCodeExploreAsync(
            request.WorkspaceId,
            new CodeExploreRequest
            {
                Query = "SmallSolution.Contracts.GeneratedByRoslyn59",
                ExactSymbolAnchors = ["T:SmallSolution.Contracts.GeneratedByRoslyn59"],
            },
            new FixtureSourceReader(root),
            TestContext.Current.CancellationToken);

        Assert.Equal(SemanticConfidenceLevel.FullSemantic, load.Confidence);
        Assert.Contains(load.Projects, project => project.Name == "Contracts"
            && project.TargetFrameworks.Contains("net10.0", StringComparer.Ordinal)
            && project.TargetFrameworks.Contains("net9.0", StringComparer.Ordinal));
        Assert.NotEmpty(extensionProperty);
        Assert.NotEmpty(extensionMethod);
        Assert.NotEmpty(staticExtension);
        Assert.NotEmpty(fieldBackedType);
        Assert.Contains(
            generatedDocuments.Documents,
            document => document.Origin == GeneratedCodeOrigin.SourceGenerator
                && document.Name.Contains("GeneratedByRoslyn59", StringComparison.Ordinal)
                && document.Content?.Contains("Roslyn 5.9", StringComparison.Ordinal) == true);
        Assert.Contains(
            codeExplore.ResolvedAnchors,
            resolution => resolution.Outcome == CodeExploreResolutionOutcome.Resolved
                && resolution.SelectedLocation?.IsGenerated == true);
        Assert.Contains(codeExplore.FileSections, section => section.IsGenerated);

        var property = extensionProperty[0];
        var references = await registry.FindReferencesAsync(
            request.WorkspaceId,
            property.Symbol.Id,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(references, reference => reference.Location.FilePath.EndsWith(
            Path.Combine("App", "Program.cs"),
            StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Generated source truncated by either source budget cannot advertise unresolved path continuations.</summary>
    [Theory]
    [InlineData(60, 1000)]
    [InlineData(1000, 60)]
    [InlineData(1, 1000)]
    [InlineData(1000, 1)]
    public static async Task CodeExplore_TruncatedGeneratedSource_SuppressesPathContinuations(
        int perFileBudget,
        int totalBudget)
    {
        var root = FixtureRoot;
        var request = CreateLoadRequest(root);
        await using var events = new DomainEventStream();
        await using var registry = new SemanticEngineRegistry(events, NullLoggerFactory.Instance);
        var queries = new AdvancedSemanticQueryService(registry, TestPromptLoader.Instance);
        await registry.LoadAsync(request, TestContext.Current.CancellationToken);

        var result = await queries.QueryCodeExploreAsync(
            request.WorkspaceId,
            new CodeExploreRequest
            {
                Query = "SmallSolution.Contracts.GeneratedByRoslyn59",
                ExactSymbolAnchors = ["T:SmallSolution.Contracts.GeneratedByRoslyn59"],
                Limits = new CodeExploreLimits
                {
                    MaximumPerFileSourceCharacters = perFileBudget,
                    MaximumSourceCharacters = totalBudget,
                },
            },
            new FixtureSourceReader(root),
            TestContext.Current.CancellationToken);

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

    /// <summary>A physical generated-looking source file retains ordinary path and drift enforcement.</summary>
    [Fact]
    public static async Task CodeExplore_PhysicalGeneratedLookingFile_DetectsDiskDrift()
    {
        var temporaryRoot = CopyFixtureToTemporaryRoot();
        try
        {
            await RemoveFixtureAnalyzerReferenceAsync(temporaryRoot);
            var sourcePath = Path.Combine(temporaryRoot, "Contracts", "PhysicalGenerated.g.cs");
            await File.WriteAllTextAsync(
                sourcePath,
                "namespace SmallSolution.Contracts; public sealed class PhysicalGenerated { public int Value => 1; }",
                TestContext.Current.CancellationToken);
            var request = CreateLoadRequest(temporaryRoot);
            await using var events = new DomainEventStream();
            await using var registry = new SemanticEngineRegistry(events, NullLoggerFactory.Instance);
            var queries = new AdvancedSemanticQueryService(registry, TestPromptLoader.Instance);
            var load = await registry.LoadAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(SemanticConfidenceLevel.FullSemantic, load.Confidence);

            await File.WriteAllTextAsync(
                sourcePath,
                "namespace SmallSolution.Contracts; public sealed class PhysicalGenerated { public int Value => 2; }",
                TestContext.Current.CancellationToken);
            var result = await queries.QueryCodeExploreAsync(
                request.WorkspaceId,
                new CodeExploreRequest
                {
                    Query = "SmallSolution.Contracts.PhysicalGenerated",
                    ExactSymbolAnchors = ["T:SmallSolution.Contracts.PhysicalGenerated"],
                },
                new FixtureSourceReader(temporaryRoot),
                TestContext.Current.CancellationToken);

            Assert.NotEmpty(result.FileSections);
            Assert.All(
                result.FileSections,
                section =>
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

    /// <summary>One aggregate measurement reports bounded phases and counts without repository paths.</summary>
    [Fact]
    public static async Task SemanticEngine_LoadMeasurement_IsAggregateAndPathFree()
    {
        var root = FixtureRoot;
        var logger = new RecordingLogger<SemanticEngine>();
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(events, logger);

        var load = await engine.LoadAsync(
            CreateLoadRequest(root),
            TestContext.Current.CancellationToken);

        Assert.True(
            load.Confidence == SemanticConfidenceLevel.FullSemantic,
            string.Join(Environment.NewLine, logger.Messages.Select(message => message.Text)));
        var measurement = Assert.Single(
            logger.Messages,
            message => message.Level == LogLevel.Information
                && message.Text.StartsWith("Semantic load completed:", StringComparison.Ordinal));
        Assert.Contains("evaluation", measurement.Text, StringComparison.Ordinal);
        Assert.Contains("confinement", measurement.Text, StringComparison.Ordinal);
        Assert.Contains("compilation", measurement.Text, StringComparison.Ordinal);
        Assert.Contains("projects expected", measurement.Text, StringComparison.Ordinal);
        Assert.Contains("workspace failures 0", measurement.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(root, measurement.Text, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(
            logger.Messages.Count(message => message.Level == LogLevel.Debug
                && message.Text.StartsWith("Slowest semantic project compilations:", StringComparison.Ordinal)),
            0,
            1);
    }

    /// <summary>Text-only completion and cancellation each emit exactly one classified load measurement.</summary>
    [Fact]
    public static async Task SemanticEngine_LoadMeasurement_ClassifiesTextOnlyAndCancellation()
    {
        var root = FixtureRoot;
        var textLogger = new RecordingLogger<SemanticEngine>();
        await using (var events = new DomainEventStream())
        await using (var engine = new SemanticEngine(events, textLogger))
        {
            var request = CreateLoadRequest(root) with { TrustLevel = RepositoryTrustLevel.TrustedRead };
            var load = await engine.LoadAsync(request, TestContext.Current.CancellationToken);

            Assert.Equal(SemanticConfidenceLevel.TextOnly, load.Confidence);
            Assert.Single(
                textLogger.Messages,
                message => message.Level == LogLevel.Information
                    && message.Text.StartsWith("Semantic load completed:", StringComparison.Ordinal));
        }

        var cancellationLogger = new RecordingLogger<SemanticEngine>();
        await using (var events = new DomainEventStream())
        await using (var engine = new SemanticEngine(events, cancellationLogger))
        using (var cancellation = new CancellationTokenSource())
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                engine.LoadAsync(CreateLoadRequest(root), cancellation.Token));

            Assert.Single(
                cancellationLogger.Messages,
                message => message.Level == LogLevel.Information
                    && message.Text.StartsWith("Semantic load cancelled:", StringComparison.Ordinal));
        }
    }

    /// <summary>A publication failure after state commit cannot dispose the workspace now owned by the engine.</summary>
    [Fact]
    public static async Task SemanticEngine_PostCommitPublicationFailure_RetainsUsableState()
    {
        await using var events = new DomainEventStream();
        await using var subscription = events.Subscribe((domainEvent, _) =>
            domainEvent is SemanticConfidenceChanged
                ? Task.FromException(new InvalidOperationException("Injected post-commit publication failure."))
                : Task.CompletedTask);
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.LoadAsync(CreateLoadRequest(FixtureRoot), TestContext.Current.CancellationToken));

        Assert.Equal(SemanticConfidenceLevel.FullSemantic, engine.Confidence);
        var symbols = await engine.FindSymbolsAsync(
            "UpperValue",
            TestContext.Current.CancellationToken);
        Assert.NotEmpty(symbols);
    }

    /// <summary>A valid target analyzer assembly whose analyzer cannot be created cannot retain full confidence.</summary>
    [Fact]
    public static async Task SemanticEngine_BrokenTargetAnalyzer_ReducesConfidence()
    {
        var temporaryRoot = CopyFixtureToTemporaryRoot();
        var analyzerPath = Path.Combine(
            AppContext.BaseDirectory,
            "plan117",
            "analyzer-failure",
            "Threadsmith.SemanticFixtures.Roslyn59.dll");
        var failureMarker = analyzerPath + ".fail-load";
        try
        {
            SetFixtureAnalyzerPath(temporaryRoot, analyzerPath);
            await File.WriteAllTextAsync(
                failureMarker,
                "trigger",
                TestContext.Current.CancellationToken);
            await using var events = new DomainEventStream();
            await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance);

            var load = await engine.LoadAsync(
                CreateLoadRequest(temporaryRoot),
                TestContext.Current.CancellationToken);

            Assert.NotEqual(SemanticConfidenceLevel.FullSemantic, load.Confidence);
            Assert.NotEmpty(load.Diagnostics);
        }
        finally
        {
            File.Delete(failureMarker);
            DeleteOwnedTemporaryRoot(temporaryRoot);
        }
    }

    /// <summary>Blocked analyzer and generator construction cannot prevent load or full-refresh cancellation.</summary>
    [Theory]
    [InlineData("analyzer", false)]
    [InlineData("generator", false)]
    [InlineData("analyzer", true)]
    [InlineData("generator", true)]
    public static async Task SemanticEngine_BlockedAnalyzerConstruction_CancelsWithoutPublishingState(
        string component,
        bool refresh)
    {
        var temporaryRoot = CopyFixtureToTemporaryRoot();
        var pipeName = "threadsmith-plan117-" + Guid.NewGuid().ToString("N");
        await using var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        await using var events = new DomainEventStream();
        var publishedEvents = new ConcurrentQueue<IDomainEvent>();
        await using var subscription = events.Subscribe((domainEvent, _) =>
        {
            publishedEvents.Enqueue(domainEvent);
            return Task.CompletedTask;
        });
        var logger = new RecordingLogger<SemanticEngine>();
        await using var engine = new SemanticEngine(events, logger);
        var request = CreateLoadRequest(temporaryRoot);
        var analyzerPath = Path.Combine(
            AppContext.BaseDirectory,
            "plan117",
            $"analyzer-blocking-{component}-{refresh}",
            "Threadsmith.SemanticFixtures.Roslyn59.dll");
        var markerPath = analyzerPath + ".block-" + component;
        try
        {
            // Arrange: a refresh starts with a usable workspace that must survive cancellation.
            if (refresh)
            {
                SetFixtureAnalyzerPath(temporaryRoot, Path.Combine(FixtureRoot, "Analyzers", "Threadsmith.SemanticFixtures.Roslyn59.dll"));
                var initialLoad = await engine.LoadAsync(request, TestContext.Current.CancellationToken);
                Assert.Equal(SemanticConfidenceLevel.FullSemantic, initialLoad.Confidence);
                publishedEvents.Clear();
            }

            // Loaded analyzer assemblies stay locked on Windows; keep them in test output, outside disposable inputs.
            Directory.CreateDirectory(Path.GetDirectoryName(analyzerPath)!);
            File.Copy(Path.Combine(FixtureRoot, "Analyzers", "Threadsmith.SemanticFixtures.Roslyn59.dll"), analyzerPath, overwrite: true);
            SetFixtureAnalyzerPath(temporaryRoot, analyzerPath);
            await File.WriteAllTextAsync(markerPath, pipeName, TestContext.Current.CancellationToken);

            // Act: cancel only after the real Roslyn constructor has entered its synchronous barrier.
            var operation = refresh
                ? engine.RefreshFullAsync(cancellation.Token)
                : engine.LoadAsync(request, cancellation.Token);
            await pipe.WaitForConnectionAsync(TestContext.Current.CancellationToken);
            await cancellation.CancelAsync();
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                operation.WaitAsync(TestContext.Current.CancellationToken));

            // Assert: cancellation completes while construction is blocked, without publishing replacement state.
            Assert.Equal(cancellation.Token, exception.CancellationToken);
            Assert.Empty(publishedEvents);
            Assert.Equal(
                refresh ? SemanticConfidenceLevel.FullSemantic : SemanticConfidenceLevel.None,
                engine.Confidence);
            Assert.Single(logger.Messages, message => message.Text.StartsWith("Semantic load cancelled:", StringComparison.Ordinal));
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
            File.Delete(markerPath);
            if (pipe.IsConnected)
            {
                byte[] release = [1];
                await pipe.WriteAsync(release, CancellationToken.None);
                var acknowledgement = new byte[1];
                Assert.Equal(1, await pipe.ReadAsync(acknowledgement, CancellationToken.None));
            }

            DeleteOwnedTemporaryRoot(temporaryRoot);
        }
    }

    /// <summary>A repository that requires an unavailable SDK cannot be reported as fully semantic.</summary>
    [Fact]
    public static async Task SemanticEngine_UnavailableRepositorySdk_ReducesConfidence()
    {
        var temporaryRoot = CopyFixtureToTemporaryRoot();
        try
        {
            await RemoveFixtureAnalyzerReferenceAsync(temporaryRoot);
            await File.WriteAllTextAsync(
                Path.Combine(temporaryRoot, "global.json"),
                """
                {
                  "sdk": {
                    "version": "10.0.999",
                    "rollForward": "disable",
                    "allowPrerelease": false
                  }
                }
                """,
                TestContext.Current.CancellationToken);
            var logger = new RecordingLogger<SemanticEngine>();
            await using var events = new DomainEventStream();
            await using var engine = new SemanticEngine(events, logger);

            var load = await engine.LoadAsync(
                CreateLoadRequest(temporaryRoot),
                TestContext.Current.CancellationToken);

            Assert.NotEqual(SemanticConfidenceLevel.FullSemantic, load.Confidence);
            Assert.NotEmpty(load.Diagnostics);
            Assert.Single(
                logger.Messages,
                message => message.Level == LogLevel.Information
                    && message.Text.StartsWith("Semantic load failed:", StringComparison.Ordinal));
        }
        finally
        {
            DeleteOwnedTemporaryRoot(temporaryRoot);
        }
    }

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
            await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance);

            var load = await engine.LoadAsync(
                CreateLoadRequest(temporaryRoot),
                TestContext.Current.CancellationToken);

            Assert.Equal(SemanticConfidenceLevel.FullSemantic, load.Confidence);
            Assert.Empty(load.Diagnostics);
        }
        finally
        {
            DeleteOwnedTemporaryRoot(temporaryRoot);
        }
    }

    /// <summary>Opt-in production-path phase evidence for the plan's fixture, project, and solution matrix.</summary>
    [Fact]
    [Trait("Category", "Performance")]
    public static async Task SemanticEngine_PerformanceEvidence_UsesProductionLoadPath()
    {
        var target = Environment.GetEnvironmentVariable("THREADSMITH_PLAN117_PERFORMANCE_TARGET");
        var repositoryRoot = Environment.GetEnvironmentVariable("THREADSMITH_PLAN117_REPOSITORY_ROOT");
        if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(repositoryRoot))
        {
            Assert.Skip("Set the Plan 117 performance target and repository root to collect phase evidence.");
        }

        var normalizedRepositoryRoot = Path.GetFullPath(repositoryRoot);
        (var loadRoot, var selection) = target switch
        {
            "fixture" => (FixtureRoot, Path.Combine(FixtureRoot, "SmallDotNetSolution.sln")),
            "app" => (normalizedRepositoryRoot, Path.Combine(normalizedRepositoryRoot, "src", "Threadsmith.App", "Threadsmith.App.csproj")),
            "solution" => (normalizedRepositoryRoot, Path.Combine(normalizedRepositoryRoot, "src", "Threadsmith.sln")),
            _ => throw new InvalidOperationException("Unknown Plan 117 performance target."),
        };

        for (var iteration = 1; iteration <= 3; iteration++)
        {
            var logger = new RecordingLogger<SemanticEngine>();
            await using var events = new DomainEventStream();
            await using var engine = new SemanticEngine(events, logger);
            var request = new SemanticLoadRequest(
                SessionId.New(),
                WorkspaceId.New(),
                loadRoot,
                selection,
                RepositoryTrustLevel.TrustedBuild);

            var load = await engine.LoadAsync(request, TestContext.Current.CancellationToken);

            var measurement = Assert.Single(
                logger.Messages,
                message => message.Level == LogLevel.Information
                    && message.Text.StartsWith("Semantic load ", StringComparison.Ordinal));
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"P117 target={target} iteration={iteration} {measurement.Text}");
            var expectedConfidence = target == "solution"
                ? SemanticConfidenceLevel.PartialCompilation
                : SemanticConfidenceLevel.FullSemantic;
            Assert.True(
                load.Confidence == expectedConfidence,
                string.Join(Environment.NewLine, load.Diagnostics.Concat(logger.Messages.Select(message => message.Text))));
            if (target != "solution")
            {
                Assert.Empty(load.Diagnostics);
            }
        }
    }

    private static string FixtureRoot => Path.Combine(
        AppContext.BaseDirectory,
        "fixtures",
        "semantic",
        "SmallDotNetSolution");

    private static SemanticLoadRequest CreateLoadRequest(string root)
    {
        return new SemanticLoadRequest(
            SessionId.New(),
            WorkspaceId.New(),
            root,
            Path.Combine(root, "SmallDotNetSolution.sln"),
            RepositoryTrustLevel.TrustedBuild);
    }

    private static string CopyFixtureToTemporaryRoot()
    {
        var temporaryParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        var destination = Path.Combine(temporaryParent, $"threadsmith-plan117-{Guid.NewGuid():N}");
        Directory.CreateDirectory(destination);
        foreach (var sourcePath in Directory.EnumerateFiles(FixtureRoot, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(FixtureRoot, sourcePath);
            var destinationPath = Path.Combine(destination, relativePath);
            Directory.CreateDirectory(
                Path.GetDirectoryName(destinationPath)
                    ?? throw new InvalidOperationException("Fixture destination has no parent."));
            File.Copy(sourcePath, destinationPath);
        }

        return destination;
    }

    private static async Task RemoveFixtureAnalyzerReferenceAsync(string root)
    {
        var contractsProject = Path.Combine(root, "Contracts", "Contracts.csproj");
        var contractsProjectText = await File.ReadAllTextAsync(
            contractsProject,
            TestContext.Current.CancellationToken);
        const string analyzerReference = "    <Analyzer Include=\"..\\Analyzers\\Threadsmith.SemanticFixtures.Roslyn59.dll\" />";
        var withoutAnalyzer = contractsProjectText
            .Replace(analyzerReference + "\r\n", string.Empty, StringComparison.Ordinal)
            .Replace(analyzerReference + "\n", string.Empty, StringComparison.Ordinal);
        if (string.Equals(withoutAnalyzer, contractsProjectText, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The Plan 117 fixture analyzer reference was not found.");
        }

        await File.WriteAllTextAsync(
            contractsProject,
            withoutAnalyzer,
            TestContext.Current.CancellationToken);
    }

    private static void SetFixtureAnalyzerPath(string root, string analyzerPath)
    {
        var contractsProject = Path.Combine(root, "Contracts", "Contracts.csproj");
        var document = XDocument.Load(contractsProject);
        var analyzer = document
            .Descendants("Analyzer")
            .Single(element => element.Attribute("Include")?.Value.EndsWith(
                "Threadsmith.SemanticFixtures.Roslyn59.dll",
                StringComparison.OrdinalIgnoreCase) == true);
        analyzer.SetAttributeValue("Include", analyzerPath);
        document.Save(contractsProject);
    }

    private static void DeleteOwnedTemporaryRoot(string path)
    {
        var temporaryParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        var ownedRoot = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(ownedRoot), temporaryParent, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(ownedRoot).StartsWith("threadsmith-plan117-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing cleanup outside the exact Plan 117 test directory.");
        }

        Directory.Delete(ownedRoot, recursive: true);
    }

    private sealed class RecordingLogger<TCategory> : ILogger<TCategory>
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

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Enqueue(new LogMessage(logLevel, formatter(state, exception)));
        }
    }

    private sealed class FixtureSourceReader : ICodeExploreSourceReader
    {
        private readonly string _root;

        internal FixtureSourceReader(string root)
        {
            _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        }

        public bool IsPathAllowed(string path)
        {
            var normalized = Path.GetFullPath(path);
            return File.Exists(normalized)
                && normalized.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        public async Task<CodeExploreSourceText> ReadTextAsync(
            string path,
            int maximumBytes,
            CancellationToken cancellationToken = default)
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

            return new CodeExploreSourceText(
                Path.GetFullPath(path),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes),
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }
    }

    private sealed record LogMessage(LogLevel Level, string Text);
}
