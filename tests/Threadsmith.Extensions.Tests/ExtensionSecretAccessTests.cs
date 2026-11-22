namespace Threadsmith.Extensions.Tests;

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Extensions.Abstractions;
using Threadsmith.Extensions.Runtime;
using Threadsmith.Tools;
using Xunit;

/// <summary>Checks secret resolution, revocation, and output boundaries.</summary>
public static class ExtensionSecretAccessTests
{
    /// <summary>Readers pin a retired value only if it is resolved again during their lifetime.</summary>
    [Fact]
    public static void SecretProtection_DoesNotTransferRetiredValuesBetweenReaders()
    {
        var protection = new ExtensionSecretProtection();
        using var owner = protection.CreateScope();
        owner.Protect("retired");
        owner.Protect("renewed");
        using var first = protection.CreateScope(pinActiveValues: true);
        owner.Dispose();
        using var next = protection.CreateScope(pinActiveValues: true);
        using (var renewal = protection.CreateScope())
        {
            renewal.Protect("renewed");
        }

        first.Dispose();
        Assert.False(protection.Contains("retired"));
        Assert.True(protection.Contains("renewed"));
        next.Dispose();
        Assert.False(protection.Contains("renewed"));
    }

    /// <summary>Exercises trust, declarations and resolution through a loaded package.</summary>
    [Fact]
    public static async Task ManagerGrant_ResolvesOnlyAdmittedReferences()
    {
        var token = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory().FullName;
        var package = Directory.CreateDirectory(Path.Combine(root, "package")).FullName;
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var fixture = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../Fixtures/SettingsExtension/bin", configuration, "net10.0"));
        var resolver = new TestResolver();
        resolver.Result.SetResult(new SecretResolutionResult { Value = new SecretValue("fixture-value") });
        var events = new RecordingEventStream();
        var registry = new ToolRegistry([]);
        var leases = new InvocationLeaseAuthority(NullLogger.Instance);
        var capabilities = new CapabilityRegistry(leases, NullLogger<CapabilityRegistry>.Instance, registry);
        var trust = RepositoryTrustLevel.TrustedRead;
        var session = SessionId.New();
        var host = new ExtensionHost(
            events,
            NullLogger<ExtensionHost>.Instance,
            capabilityRegistry: capabilities,
            leaseAuthority: leases,
            repositoryPath: root,
            secrets: resolver,
            repositoryTrust: (_, _) => Task.FromResult(trust));
        try
        {
            foreach (var file in Directory.EnumerateFiles(fixture))
            {
                File.Copy(file, Path.Combine(package, Path.GetFileName(file)));
            }

            await File.WriteAllTextAsync(
                Path.Combine(package, "threadsmith.extension.json"),
                """{"Id":"threadsmith.tests.settings","Name":"Settings fixture","RequiredTrust":"FullyTrustedAutomation","Permissions":["secret-access","network-access"],"AccessTypes":["disk-read","disk-write"]}""",
                token);
            Directory.CreateDirectory(Path.Combine(root, ".threadsmith"));
            await File.WriteAllTextAsync(
                Path.Combine(root, ".threadsmith", "extensions.json"),
                """{"settings":{"threadsmith.tests.settings":{"reference":"secrets:test"}}}""",
                token);
            host.SetDiscoveryDirectory(package);
            IExtensionManager manager = host;
            var discovered = Assert.Single(await manager.DiscoverAsync(token));
            Assert.Equal(RepositoryTrustLevel.FullyTrustedAutomation, discovered.RequiredTrust);
            Assert.Equal(["disk-read", "disk-write", "network-access", "secret-access"], discovered.AccessTypes);
            Assert.Equal(discovered.AccessTypes, Assert.Single(manager.Summaries).AccessTypes);
            await Assert.ThrowsAsync<ExtensionIncompatibleException>(() => manager.LoadAsync("threadsmith.tests.settings", session, token));
            Assert.Empty(registry.AllDefinitions);
            trust = RepositoryTrustLevel.FullyTrustedAutomation;
            var enabled = await manager.LoadAsync("threadsmith.tests.settings", session, token);
            Assert.Equal(discovered.AccessTypes, enabled!.AccessTypes);
            var pipeline = new ToolInvocationPipeline(
                registry,
                new DefaultPolicyEngine(),
                new DenyApprovalPolicy(),
                events,
                new Threadsmith.Telemetry.SecretOutputSanitizer(),
                NullLogger<ToolInvocationPipeline>.Instance,
                timeProvider: new FakeTimeProvider(),
                prompts: TestPromptLoader.Instance);
            var request = new ToolInvocationRequest
            {
                Context = new() { RepositoryPath = root, RequestedBy = "test", TrustLevel = trust, ProhibitedPaths = ["private.fixture"], AllowedNetworkToolIds = [] },
                SessionId = session,
                RunId = RunId.New(),
                ToolId = "settings_view",
                ArgumentsJson = "{}",
            };
            var denied = await pipeline.InvokeAsync(request with { Context = request.Context with { TrustLevel = RepositoryTrustLevel.TrustedRead } }, token);
            Assert.False(denied.Succeeded);
            var undeclared = await pipeline.InvokeAsync(request with { ArgumentsJson = """{"reference":"secrets:other"}""" }, token);
            Assert.False(undeclared.Succeeded);
            Assert.Equal(0, resolver.Calls);
            var result = await pipeline.InvokeAsync(request, token);
            Assert.True(result.Succeeded, result.Error);
            using var output = JsonDocument.Parse(result.ResultJson!);
            Assert.Equal("fixture-value".Length, output.RootElement.GetProperty("length").GetInt32());
            Assert.Equal(1, resolver.Calls);
            Assert.Equal(SecretProviderTrust.UserOwned, resolver.Request!.MinimumTrust);
            Assert.Equal(root, resolver.Request.RepositoryRoot);
            var leaking = await pipeline.InvokeAsync(request with { ArgumentsJson = """{"returnSecret":true}""" }, token);
            Assert.False(leaking.Succeeded);
            Assert.DoesNotContain("fixture-value", JsonSerializer.Serialize(events.Published), StringComparison.Ordinal);
            Assert.DoesNotContain("fixture-value", JsonSerializer.Serialize(leaking), StringComparison.Ordinal);

            // Hold an admitted call while the shared replacement path publishes a new generation.
            var predecessor = Assert.Single(host.Generations);
            resolver.PauseNext();
            var admitted = pipeline.InvokeAsync(request, token);
            var paused = resolver.Paused.Task;
            try
            {
                await Task.WhenAny(admitted, paused).WaitAsync(token);
                if (!paused.IsCompletedSuccessfully)
                {
                    var earlyResult = await admitted;
                    Assert.Fail($"The invocation completed before reaching the resolver barrier ({earlyResult.ErrorClassification}): {earlyResult.Error}");
                }

                var successor = await host.LoadReplacementAsync(
                    predecessor,
                    new ExtensionLoadRequest
                    {
                        ExtensionDirectory = package,
                        ShadowStagingRoot = Path.GetDirectoryName(predecessor.StagingPath)!,
                        EffectiveTrust = trust,
                        SessionId = session,
                        Configuration = new Dictionary<string, string?> { ["reference"] = "secrets:successor" },
                    },
                    token);
                Assert.NotEqual(predecessor.GenerationId, successor.GenerationId);
                leases.BeginDraining(predecessor.GenerationId);
            }
            finally
            {
                resolver.Continue.TrySetResult(new SecretResolutionResult { Value = new SecretValue("old-generation") });
            }

            Assert.True((await admitted.WaitAsync(token)).Succeeded);
            Assert.True((await pipeline.InvokeAsync(request, token)).Succeeded);
            Assert.Equal("secrets:successor", resolver.Request!.Reference.CanonicalName);
        }
        finally
        {
            foreach (var generation in host.Generations)
            {
                await host.UnloadAsync(generation, session, token);
            }

            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A provider that ignores cancellation cannot return a credential after retirement.</summary>
    [Fact]
    public static async Task Revocation_DiscardsLateResolution()
    {
        var token = TestContext.Current.CancellationToken;
        var resolver = new TestResolver();
        using var retirement = new CancellationTokenSource();
        var protection = new ExtensionSecretProtection();
        var context = new ToolExecutionContext(
            ToolInvocationId.New(),
            SessionId.New(),
            RunId.New(),
            new ToolInvocationContext { RepositoryPath = ".", RequestedBy = "test" });
        using var access = new ExtensionSecretAccess(
            resolver,
            new HashSet<string> { "secrets:test" },
            context,
            protection,
            "fixture",
            retirement.Token,
            token);
        var pending = access.ResolveAsync("secrets:test", token);
        await resolver.Entered.Task.WaitAsync(token);
        await retirement.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(token));
        resolver.Result.SetResult(new SecretResolutionResult { Value = new SecretValue("discard-me") });
        Assert.False(protection.HasValues);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => access.ResolveAsync("secrets:test", token));
        Assert.Equal(1, resolver.Calls);

        var clock = new FakeTimeProvider();
        var deadlineContext = context with { TimeProvider = clock, ExecutionDeadline = clock.GetUtcNow().AddMinutes(1) };
        var late = new TestResolver();
        using var expiring = new ExtensionSecretAccess(late, new HashSet<string> { "secrets:test" }, deadlineContext, protection, "fixture", default, token);
        var expired = expiring.ResolveAsync("secrets:test", token);
        await late.Entered.Task.WaitAsync(token);
        clock.Advance(TimeSpan.FromMinutes(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => expired.WaitAsync(token));
        late.Result.SetResult(new SecretResolutionResult { Value = new SecretValue("too-late") });
        Assert.False(protection.HasValues);
    }

    /// <summary>Decoded output and retained extension loggers use the same generation protection.</summary>
    [Fact]
    public static void SecretOutput_ProtectsEscapedValuesAndRetainedLogger()
    {
        const string secret = "fixture-\"<&value";
        var protection = new ExtensionSecretProtection();
        var logger = new CaptureLogger();
        var retained = new ExtensionLogger(logger, protection);
        const string relaxedJson = """["fixture-\"<&value"]""";
        retained.Error(relaxedJson);
        Assert.Equal(relaxedJson, logger.Message);

        protection.Protect(secret);
        var json = JsonSerializer.Serialize(new[] { secret });
        using var document = JsonDocument.Parse(json);
        Assert.True(protection.Contains(document.RootElement));
        retained.Warning(json);
        Assert.Equal("[protected extension log]", logger.Message);
        retained.Error(relaxedJson);
        Assert.Equal("[protected extension log]", logger.Message);
        retained.Information($"Connection diagnostic: {relaxedJson}");
        Assert.Equal("[protected extension log]", logger.Message);
        retained.Information("Connected");
        Assert.Equal("Connected", logger.Message);
    }

    /// <summary>Published policy failures cannot disclose credentials from extension admission callbacks.</summary>
    [Theory]
    [InlineData("throw", "An extension admission callback failed.")]
    [InlineData("executable", "An extension admission claim contained a protected value.")]
    [InlineData("reference", "An extension admission claim contained a protected value.")]
    public static async Task AdmissionCallbacks_KeepResolvedCredentialsOutOfPolicyFailures(string claim, string reason)
    {
        var generation = new ExtensionGeneration(ExtensionId.New(), ExtensionGenerationId.New(), new() { Id = "fixture", Name = "fixture" }, null, null!, null, ".", ".");
        generation.SecretProtection.Protect(AdmissionTool.Credential);
        var clock = new FakeTimeProvider();
        var registry = new ToolRegistry([]);
        var proxy = new CapabilityProxy(new AdmissionTool(claim), CapabilityId.New(), generation, new InvocationLeaseAuthority(NullLogger.Instance, clock), NullLogger.Instance);
        registry.RegisterOrReplace(proxy, new ToolActivitySource(ToolActivitySourceKind.Extension, "fixture"));
        var events = new RecordingEventStream();
        var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new DenyApprovalPolicy(), events, new Threadsmith.Telemetry.SecretOutputSanitizer(), NullLogger<ToolInvocationPipeline>.Instance, timeProvider: clock, prompts: TestPromptLoader.Instance);
        var request = new ToolInvocationRequest
        {
            Context = new() { RepositoryPath = ".", RequestedBy = "test" },
            SessionId = SessionId.New(),
            RunId = RunId.New(),
            ToolId = proxy.Definition.Id,
            ArgumentsJson = "{}",
        };

        var result = await pipeline.InvokeAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(ToolErrorClassification.PolicyDenied, result.ErrorClassification);
        Assert.Equal(reason, result.Error);
        Assert.DoesNotContain(AdmissionTool.Credential, JsonSerializer.Serialize(events.Published.OfType<ToolInvocationCompleted>()), StringComparison.Ordinal);
    }

    /// <summary>Free-form error and provenance fields cannot bypass protection using relaxed JSON escaping.</summary>
    [Theory]
    [InlineData("error")]
    [InlineData("kind")]
    [InlineData("identifier")]
    [InlineData("range")]
    public static async Task SecretOutput_ProtectsEscapedErrorAndProvenance(string field)
    {
        const string credential = "fixture-\"<&value";
        var resolver = new TestResolver();
        resolver.Result.SetResult(new SecretResolutionResult { Value = new SecretValue(credential) });
        var generation = new ExtensionGeneration(ExtensionId.New(), ExtensionGenerationId.New(), new() { Id = "fixture", Name = "fixture" }, null, null!, null, ".", ".")
        {
            SecretResolver = resolver,
        };
        var clock = new FakeTimeProvider();
        var registry = new ToolRegistry([]);
        var proxy = new CapabilityProxy(new DiagnosticTool(field), CapabilityId.New(), generation, new InvocationLeaseAuthority(NullLogger.Instance, clock), NullLogger.Instance);
        registry.RegisterOrReplace(proxy, new ToolActivitySource(ToolActivitySourceKind.Extension, "fixture"));
        var events = new RecordingEventStream();
        var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new DenyApprovalPolicy(), events, new Threadsmith.Telemetry.SecretOutputSanitizer(), NullLogger<ToolInvocationPipeline>.Instance, timeProvider: clock, prompts: TestPromptLoader.Instance);
        var request = new ToolInvocationRequest
        {
            Context = new() { RepositoryPath = ".", RequestedBy = "test", TrustLevel = RepositoryTrustLevel.FullyTrustedAutomation },
            SessionId = SessionId.New(),
            RunId = RunId.New(),
            ToolId = proxy.Definition.Id,
            ArgumentsJson = "{}",
        };

        var result = await pipeline.InvokeAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(1, resolver.Calls);
        Assert.Equal(ToolErrorClassification.ExecutionFailure, result.ErrorClassification);
        Assert.Equal(field == "error" ? "[protected extension log]" : "ExtensionToolExecutionFailed", result.Error);
        Assert.Empty(result.Sources);
        var completed = Assert.Single(events.Published.OfType<ToolInvocationCompleted>());
        Assert.Equal(result.Error, completed.Error);
        Assert.DoesNotContain(credential, result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(DiagnosticTool.Escaped, result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(DiagnosticTool.Escaped, completed.ResultJson ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>Rotation forgets inactive values while overlapping owners and retained logs remain protected.</summary>
    [Fact]
    public static async Task SecretProtection_RetiresInvocationRegistrations()
    {
        var protection = new ExtensionSecretProtection();
        var logger = new CaptureLogger();
        var retained = new ExtensionLogger(logger, protection);
        var context = new ToolExecutionContext(
            ToolInvocationId.New(),
            SessionId.New(),
            RunId.New(),
            new ToolInvocationContext { RepositoryPath = ".", RequestedBy = "test" });
        var references = new HashSet<string> { "secrets:test" };
        var token = TestContext.Current.CancellationToken;
        for (var index = 0; index < 257; index++)
        {
            var credential = $"rotating-fixture-{index}";
            var resolver = new TestResolver();
            resolver.Result.SetResult(new SecretResolutionResult { Value = new SecretValue(credential) });
            var first = new ExtensionSecretAccess(resolver, references, context, protection, "fixture", default, token);
            var second = new ExtensionSecretAccess(resolver, references, context, protection, "fixture", default, token);
            using (first)
            using (second)
            {
                await first.ResolveAsync("secrets:test", token);
                await first.ResolveAsync("secrets:test", token); // One owner despite repeated resolution.
                await second.ResolveAsync("secrets:test", token);
                using var boundary = protection.CreateScope(pinActiveValues: true);
                first.Revoke();
                Assert.True(protection.Contains(credential));
                first.Dispose();
                Assert.True(protection.Contains(credential));
                second.Dispose();
                Assert.True(protection.Contains(credential)); // Callback/output reader still owns it.
            }

            Assert.False(protection.Contains(credential));
            retained.Information(credential); // A retained logger cannot reveal a forgotten value.
            Assert.Equal("[protected extension log]", logger.Message);
        }

        // A contributor can overlap the invoking tool. Force the invocation to retire first,
        // then complete either its hint projection or its diagnostic exception.
        foreach (var fail in new[] { false, true })
        {
            const string credential = "overlapping-contributor-value";
            var generation = new ExtensionGeneration(ExtensionId.New(), ExtensionGenerationId.New(), new() { Id = "fixture", Name = "fixture" }, null, null!, null, ".", ".");
            var contributor = new PausedContributor();
            var registry = new CapabilityRegistry(new InvocationLeaseAuthority(NullLogger.Instance), NullLogger<CapabilityRegistry>.Instance);
            registry.RegisterModelPreferenceContributor(generation, contributor);
            var aggregate = new ModelPreferenceAggregator(registry, logger);
            using var owner = generation.SecretProtection.CreateScope();
            if (!fail)
            {
                owner.Protect(credential);
            }

            var pending = aggregate.GetHintsAsync("General", token);
            IReadOnlyList<ExtensionModelPreferenceHint> hints = [];
            try
            {
                await contributor.Entered.Task.WaitAsync(token);
                if (fail)
                {
                    owner.Protect(credential); // The callback began before credential resolution.
                }

                owner.Dispose();
                Assert.True(generation.SecretProtection.Contains(credential));
            }
            finally
            {
                if (fail)
                {
                    contributor.Result.TrySetException(new InvalidOperationException(credential));
                }
                else
                {
                    contributor.Result.TrySetResult([new() { WorkloadName = "General", PreferredProfileName = "fast", Rationale = credential }]);
                }

                hints = await pending;
            }

            Assert.Empty(hints);
            Assert.False(generation.SecretProtection.Contains(credential));
            if (fail)
            {
                Assert.DoesNotContain(credential, logger.Message, StringComparison.Ordinal);
                Assert.Contains("[protected extension log]", logger.Message, StringComparison.Ordinal);
            }
        }
    }

    private sealed class PausedContributor : IModelPreferenceContributor
    {
        public CapabilityDescriptor Descriptor { get; } = new() { Id = "paused_contributor", Kind = CapabilityKind.ModelPreference };

        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<IReadOnlyList<ExtensionModelPreferenceHint>> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<ExtensionModelPreferenceHint>> GetHintsAsync(string workloadName, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            return Result.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class DiagnosticTool(string field) : IToolCapability
    {
        internal const string Escaped = """["fixture-\"<&value"]""";

        public CapabilityDescriptor Descriptor { get; } = new() { Id = "fixture_diagnostic", Kind = CapabilityKind.Tool };

        public ExtensionToolDefinition Definition { get; } = new()
        {
            Id = "fixture_diagnostic",
            Version = "1",
            Description = "Fixture diagnostic output",
            InputSchema = new("input", 1, "{}"),
            OutputSchema = new("output", 1, "{}"),
        };

        public string? GetExecutable(string argumentsJson) => null;

        public IReadOnlyList<string> GetSecretReferences(string argumentsJson) => ["secrets:test"];

        public async Task<ExtensionToolResult> ExecuteAsync(string argumentsJson, ExtensionToolInvocationContext context, CancellationToken cancellationToken = default)
        {
            await context.Secrets!.ResolveAsync("secrets:test", cancellationToken);
            var source = new ExtensionProvenanceSource(
                field == "kind" ? Escaped : "fixture",
                field == "identifier" ? Escaped : "fixture",
                field == "range" ? Escaped : null);
            return new ExtensionToolResult
            {
                Succeeded = false,
                ResultJson = "{}",
                Error = field == "error" ? Escaped : "Fixture failure",
                Sources = field == "error" ? [] : [source],
            };
        }
    }

    private sealed class AdmissionTool(string claim) : IToolCapability
    {
        internal const string Credential = "fixture-private-value";

        public CapabilityDescriptor Descriptor { get; } = new() { Id = "fixture_admission", Kind = CapabilityKind.Tool };

        public ExtensionToolDefinition Definition { get; } = new()
        {
            Id = "fixture_admission",
            Version = "1",
            Description = "Fixture admission claims",
            InputSchema = new("input", 1, "{}"),
            OutputSchema = new("output", 1, "{}"),
        };

        public string? GetExecutable(string argumentsJson) => claim switch
        {
            "throw" => throw new ArgumentException(Credential),
            "executable" => Credential,
            _ => null,
        };

        public IReadOnlyList<string> GetSecretReferences(string argumentsJson) => claim == "reference" ? [$"secrets:{Credential}"] : [];

        public Task<ExtensionToolResult> ExecuteAsync(string argumentsJson, ExtensionToolInvocationContext context, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("The fixture must be denied before execution.");
    }

    private sealed class TestResolver : ISecretResolver
    {
        private int _pauseNext;

        internal TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<SecretResolutionResult> Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<SecretResolutionResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int Calls { get; private set; }

        internal SecretResolutionRequest? Request { get; private set; }

        public Task<SecretResolutionResult> ResolveAsync(SecretResolutionRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            Request = request;
            Entered.TrySetResult();
            if (Interlocked.Exchange(ref _pauseNext, 0) != 0)
            {
                Paused.TrySetResult();
                return Continue.Task.WaitAsync(TestContext.Current.CancellationToken);
            }

            return Result.Task.WaitAsync(TestContext.Current.CancellationToken);
        }

        internal void PauseNext() => Interlocked.Exchange(ref _pauseNext, 1);
    }

    private sealed class CaptureLogger : ILogger<ModelPreferenceAggregator>
    {
        internal string? Message { get; private set; }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Message = formatter(state, exception);
        }
    }
}
