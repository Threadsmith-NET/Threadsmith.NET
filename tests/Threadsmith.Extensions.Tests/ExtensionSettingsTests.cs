namespace Threadsmith.Extensions.Tests;

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Extensions.Abstractions;
using Threadsmith.Extensions.Runtime;
using Threadsmith.Tools;
using Xunit;

/// <summary>Exercises bounded settings through the production load and replacement paths.</summary>
public sealed class ExtensionSettingsTests : IAsyncLifetime
{
    private const string ExtensionId = "threadsmith.tests.settings";
    private readonly List<ExtensionHost> _hosts = [];
    private readonly ITestOutputHelper _output;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "threadsmith-settings-tests", Guid.NewGuid().ToString("N"));

    /// <summary>Initializes a new instance of the <see cref="ExtensionSettingsTests"/> class.</summary>
    public ExtensionSettingsTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>Manual loading selects only the stable descriptor's section and repository.</summary>
    [Fact]
    public async Task Manager_load_receives_only_its_settings_and_repository_identity()
    {
        WriteSettings("""{"endpoint":"https://example.invalid","credential":"secrets:fixture.password","optional":null}""");
        var host = CreateHost();

        var summary = await ((IExtensionManager)host).LoadAsync(ExtensionId, SessionId.New(), TestContext.Current.CancellationToken);
        var result = await ReadSettingsAsync(host);

        Assert.NotNull(summary);
        Assert.Equal(_root, result.GetProperty("repositoryPath").GetString());
        var settings = result.GetProperty("settings");
        Assert.Equal(3, settings.EnumerateObject().Count());
        Assert.Equal("secrets:fixture.password", settings.GetProperty("credential").GetString());
        Assert.Equal(JsonValueKind.Null, settings.GetProperty("optional").ValueKind);
        Assert.False(settings.TryGetProperty("otherExtensionOnly", out _));
    }

    /// <summary>Missing optional data preserves an empty configuration view.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("{\"settings\":{\"another.extension\":{\"value\":\"other\"}}}")]
    public async Task Missing_file_or_section_keeps_empty_settings(string? json)
    {
        if (json is not null)
        {
            WriteFile(json);
        }

        var host = CreateHost();
        await host.LoadAsync(Request(), TestContext.Current.CancellationToken);
        var result = await ReadSettingsAsync(host);

        Assert.Empty(result.GetProperty("settings").EnumerateObject());
    }

    /// <summary>Caller mutations cannot change captured settings, and empty overrides remain explicit.</summary>
    [Fact]
    public async Task Explicit_settings_are_copied_before_first_await_and_an_empty_override_is_respected()
    {
        WriteSettings("""{"source":"repository"}""");
        var supplied = new Dictionary<string, string?> { ["source"] = "original" };
        var barrier = new DiscoveryBarrier();
        var host = CreateHost(barrier);
        var loading = host.LoadAsync(Request() with { Configuration = supplied }, TestContext.Current.CancellationToken);
        await barrier.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        supplied["source"] = "mutated";
        barrier.Release.TrySetResult();
        await loading;

        Assert.Equal("original", (await ReadSettingsAsync(host)).GetProperty("settings").GetProperty("source").GetString());
        supplied["source"] = "mutated again";
        Assert.Equal("original", (await ReadSettingsAsync(host)).GetProperty("settings").GetProperty("source").GetString());

        var emptyHost = CreateHost();
        await emptyHost.LoadAsync(Request() with { Configuration = new Dictionary<string, string?>() }, TestContext.Current.CancellationToken);
        Assert.Empty((await ReadSettingsAsync(emptyHost)).GetProperty("settings").EnumerateObject());
    }

    /// <summary>Replacement selects a new view without changing the retained predecessor.</summary>
    [Fact]
    public async Task Replacement_publishes_a_new_snapshot_and_retained_old_calls_keep_the_old_view()
    {
        WriteSettings("""{"value":"first"}""");
        var host = CreateHost();
        var current = await host.LoadAsync(Request(), TestContext.Current.CancellationToken);
        var oldProxy = host.Capabilities.Get("settings_view")!.ToolProxy!;
        WriteSettings("""{"value":"second"}""");

        var successor = await host.LoadReplacementAsync(current, Request(), TestContext.Current.CancellationToken);

        Assert.NotEqual(current.GenerationId, successor.GenerationId);
        Assert.Equal("second", (await ReadSettingsAsync(host)).GetProperty("settings").GetProperty("value").GetString());
        Assert.Equal("first", (await ReadSettingsAsync(oldProxy)).GetProperty("settings").GetProperty("value").GetString());
        WriteSettings("""{"value":"third"}""");
        Assert.Equal("second", (await ReadSettingsAsync(host)).GetProperty("settings").GetProperty("value").GetString());
    }

    /// <summary>Explicit load after retirement captures fresh repository settings.</summary>
    [Fact]
    public async Task Manual_reload_after_unload_captures_fresh_settings()
    {
        WriteSettings("""{"value":"first"}""");
        var host = CreateHost();
        var manager = (IExtensionManager)host;
        await manager.LoadAsync(ExtensionId, SessionId.New(), TestContext.Current.CancellationToken);
        var previous = Assert.Single(host.Generations);
        await host.UnloadAsync(previous, SessionId.New(), TestContext.Current.CancellationToken);
        WriteSettings("""{"value":"reloaded"}""");

        var summary = await manager.LoadAsync(ExtensionId, SessionId.New(), TestContext.Current.CancellationToken);

        Assert.NotNull(summary);
        Assert.NotEqual(previous.GenerationId, summary.GenerationId);
        Assert.Equal("reloaded", (await ReadSettingsAsync(host)).GetProperty("settings").GetProperty("value").GetString());
    }

    /// <summary>A cancelled retirement remains reachable through the normal manager unload path.</summary>
    [Fact]
    public async Task Manager_can_retry_cancelled_deactivation_then_load_fresh_settings()
    {
        WriteSettings("""{"value":"first","holdFirstDeactivation":"true"}""");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var loggerFactory = new CancellationLoggerFactory(cancellation)
        {
            CancellationMessage = "SettingsFixtureDeactivationWaiting",
        };
        var host = CreateHost(loggerFactory: loggerFactory);
        var manager = (IExtensionManager)host;
        await manager.LoadAsync(ExtensionId, SessionId.New(), TestContext.Current.CancellationToken);
        var previous = Assert.Single(host.Generations);
        loggerFactory.Enabled = true;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.UnloadAsync(ExtensionId, SessionId.New(), cancellation.Token));
        Assert.Equal(ExtensionLifecycleState.Deactivating, previous.State);
        var pending = Assert.Single(await host.DiscoverAsync(TestContext.Current.CancellationToken));
        Assert.Equal(ExtensionId, pending.ExtensionId);
        Assert.True(pending.IsLoaded);
        Assert.Equal(previous.GenerationId, pending.GenerationId);
        Assert.Equal(nameof(ExtensionLifecycleState.Deactivating), pending.State);
        await manager.UnloadAsync(ExtensionId, SessionId.New(), TestContext.Current.CancellationToken);
        await loggerFactory.Deactivated.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Empty(host.Capabilities.Registrations);
        WriteSettings("""{"value":"fresh"}""");
        var loaded = await manager.LoadAsync(ExtensionId, SessionId.New(), TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        Assert.NotEqual(previous.GenerationId, loaded.GenerationId);
        Assert.Equal("fresh", (await ReadSettingsAsync(host)).GetProperty("settings").GetProperty("value").GetString());
    }

    /// <summary>Invalid successor settings cannot change the active registration.</summary>
    [Fact]
    public async Task Invalid_replacement_leaves_the_old_generation_and_registration_unchanged()
    {
        WriteSettings("""{"value":"first"}""");
        var host = CreateHost();
        var current = await host.LoadAsync(Request(), TestContext.Current.CancellationToken);
        var oldRegistration = host.Capabilities.Get("settings_view");
        WriteFile("""{"settings":{"threadsmith.tests.settings":{"value":42}}}""");
        var replacer = new HotReplacementCoordinator(host, host.Capabilities, NullLogger<HotReplacementCoordinator>.Instance);

        var exception = await Assert.ThrowsAsync<ExtensionLoadException>(() => replacer.ReplaceAsync(
            current, Request(), SessionId.New(), TestContext.Current.CancellationToken));

        Assert.Equal(ExtensionSelectionConfig.InvalidSettingsDiagnostic, exception.Message);
        Assert.Same(current, Assert.Single(host.Generations));
        Assert.Same(oldRegistration, host.Capabilities.Get("settings_view"));
        Assert.Equal("first", (await ReadSettingsAsync(host)).GetProperty("settings").GetProperty("value").GetString());
    }

    /// <summary>Cancellation is checked after a non-cooperative activation completes.</summary>
    [Fact]
    public async Task Cancellation_ignored_by_activation_cannot_publish_replacement_settings()
    {
        WriteSettings("""{"value":"first"}""");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var loggerFactory = new CancellationLoggerFactory(cancellation);
        var host = CreateHost(loggerFactory: loggerFactory);
        var current = await host.LoadAsync(Request(), TestContext.Current.CancellationToken);
        var oldRegistration = host.Capabilities.Get("settings_view");
        WriteSettings("""{"value":"second","startWorker":"true"}""");
        loggerFactory.Enabled = true;
        var replacer = new HotReplacementCoordinator(host, host.Capabilities, NullLogger<HotReplacementCoordinator>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replacer.ReplaceAsync(
            current, Request(), SessionId.New(), cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);
        await loggerFactory.WorkerStopped.Task.WaitAsync(TestContext.Current.CancellationToken);
        await loggerFactory.Deactivated.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Same(current, Assert.Single(host.Generations));
        Assert.Same(oldRegistration, host.Capabilities.Get("settings_view"));
        Assert.Equal("first", (await ReadSettingsAsync(host)).GetProperty("settings").GetProperty("value").GetString());
    }

    /// <summary>A request's explicit repository identity selects its own settings source.</summary>
    [Fact]
    public async Task Explicit_repository_identity_cannot_reuse_the_hosts_other_repository_settings()
    {
        WriteSettings("""{"value":"first repository"}""");
        var otherRoot = Path.Combine(_root, "other-repository");
        var host = CreateHost();

        await host.LoadAsync(Request() with { RepositoryPath = otherRoot }, TestContext.Current.CancellationToken);
        var result = await ReadSettingsAsync(host, otherRoot);

        Assert.Equal(otherRoot, result.GetProperty("repositoryPath").GetString());
        Assert.Empty(result.GetProperty("settings").EnumerateObject());
    }

    /// <summary>A live repository transition cannot reuse startup discovery, settings, or proxies.</summary>
    [Fact]
    public async Task Repository_transition_fails_closed_for_stale_manual_loading_and_proxy_execution()
    {
        WriteSettings("""{"value":"first repository"}""");
        var binding = new SessionPolicyBinding();
        var policy = new EffectiveSessionPolicy
        {
            BindingId = Guid.NewGuid(),
            Generation = 1,
            RepositoryPath = _root,
            Fingerprint = "first",
        };
        binding.Publish(policy);
        var host = CreateHost(policyBinding: binding);
        await ((IExtensionManager)host).LoadAsync(ExtensionId, SessionId.New(), TestContext.Current.CancellationToken);
        var proxy = host.Capabilities.Get("settings_view")!.ToolProxy!;
        var otherRoot = Path.Combine(_root, "other-repository");
        binding.Publish(policy with { Generation = 2, RepositoryPath = otherRoot, Fingerprint = "second" });

        var discoveryFailure = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.DiscoverAsync(TestContext.Current.CancellationToken));
        var loadFailure = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ((IExtensionManager)host).LoadAsync(
            ExtensionId, SessionId.New(), TestContext.Current.CancellationToken));
        var proxyFailure = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ReadSettingsAsync(proxy, otherRoot));

        Assert.StartsWith("ExtensionRepositoryStale:", discoveryFailure.Message);
        Assert.StartsWith("ExtensionRepositoryStale:", loadFailure.Message);
        Assert.StartsWith("ExtensionRepositoryStale:", proxyFailure.Message);
        // Already admitted old-repository execution retains its own immutable view.
        Assert.Equal("first repository", (await ReadSettingsAsync(proxy)).GetProperty("settings").GetProperty("value").GetString());
    }

    /// <summary>Malformed data fails explicit loading without exposing parser or configuration details.</summary>
    [Theory]
    [InlineData("{not-json")]
    [InlineData("null")]
    [InlineData("{\"autoLoad\":null}")]
    [InlineData("{\"settings\":null}")]
    [InlineData("{\"settings\":[]}")]
    [InlineData("{\"settings\":{\"threadsmith.tests.settings\":null}}")]
    [InlineData("{\"settings\":{\"threadsmith.tests.settings\":{\"private\":42}}}")]
    [InlineData("{\"settings\":{\"x\":{},\"x\":{}}}")]
    [InlineData("{\"settings\":{\"x\":{\"k\":\"first\",\"k\":\"second\"}}}")]
    [InlineData("{\"settings\":{},\"Settings\":{}}")]
    [InlineData("{\"settings\":{\" \":{}}}")]
    [InlineData("{\"settings\":{\"x\":{\" \":\"private\"}}}")]
    public async Task Invalid_data_fails_explicit_load_safely_but_startup_keeps_no_autoload(string json)
    {
        WriteFile(json);
        var events = new RecordingEventStream();
        var host = CreateHost(events);

        var exception = await Assert.ThrowsAsync<ExtensionLoadException>(() => host.LoadAsync(Request(), TestContext.Current.CancellationToken));

        Assert.Equal(ExtensionSelectionConfig.InvalidSettingsDiagnostic, exception.Message);
        Assert.Null(exception.InnerException);
        Assert.Empty(host.Generations);
        Assert.Empty(host.Capabilities.Registrations);
        Assert.Empty(ExtensionSelectionConfig.LoadOrDefault(ConfigPath).AutoLoad);
        Assert.All(events.Published.OfType<ExtensionLoadFailed>(), failure => Assert.Equal(exception.Message, failure.Reason));
    }

    /// <summary>Section, key, value and aggregate limits reject overflow at the owning parser.</summary>
    [Fact]
    public void All_count_and_length_limits_are_enforced_and_boundary_values_are_accepted()
    {
        var valid = new Dictionary<string, string?>
        {
            [new string('k', ExtensionSelectionConfig.MaximumNameLength)] = new('v', ExtensionSelectionConfig.MaximumValueLength),
        };
        WriteSettings(JsonSerializer.Serialize(valid));
        Assert.Single(ExtensionSelectionConfig.Load(ConfigPath).Settings[ExtensionId]);

        valid[new string('z', ExtensionSelectionConfig.MaximumNameLength + 1)] = "value";
        AssertInvalidSettings(valid);
        valid = new Dictionary<string, string?> { ["k"] = new('v', ExtensionSelectionConfig.MaximumValueLength + 1) };
        AssertInvalidSettings(valid);
        valid = Enumerable.Range(0, ExtensionSelectionConfig.MaximumKeys).ToDictionary(index => index.ToString(), _ => (string?)null);
        WriteSettings(JsonSerializer.Serialize(valid));
        Assert.Equal(ExtensionSelectionConfig.MaximumKeys, ExtensionSelectionConfig.Load(ConfigPath).Settings[ExtensionId].Count);
        valid["overflow"] = null;
        AssertInvalidSettings(valid);

        var sections = Enumerable.Range(0, ExtensionSelectionConfig.MaximumSections)
            .ToDictionary(index => index.ToString(), _ => new Dictionary<string, string?>());
        WriteFile(JsonSerializer.Serialize(new { settings = sections }));
        Assert.Equal(ExtensionSelectionConfig.MaximumSections, ExtensionSelectionConfig.Load(ConfigPath).Settings.Count);
        sections["overflow"] = [];
        WriteFile(JsonSerializer.Serialize(new { settings = sections }));
        AssertSafeFileFailure();
        WriteFile(JsonSerializer.Serialize(new { settings = new Dictionary<string, object> { [new string('x', 257)] = new { } } }));
        AssertSafeFileFailure();

        var fullSection = Enumerable.Range(0, 8).ToDictionary(index => index.ToString(), _ => (string?)new string('v', 8192));
        AssertInvalidSettings(fullSection);
        Assert.Throws<ExtensionLoadException>(() => ExtensionSelectionConfig.Snapshot(fullSection));
    }

    /// <summary>The reader bounds bytes and depth while accepting a UTF-8 BOM.</summary>
    [Fact]
    public void File_byte_limit_depth_and_utf8_bom_are_checked()
    {
        WriteFile("{}" + new string(' ', ExtensionSelectionConfig.MaximumFileBytes - 2));
        Assert.Empty(ExtensionSelectionConfig.Load(ConfigPath).Settings);
        File.AppendAllText(ConfigPath, " ");
        AssertSafeFileFailure();
        WriteFile("{\"unknown\":" + new string('[', 8) + "0" + new string(']', 8) + "}");
        AssertSafeFileFailure();
        File.WriteAllText(ConfigPath, "{}", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Assert.Empty(ExtensionSelectionConfig.Load(ConfigPath).Settings);
    }

    /// <summary>The activation facade owns an independent ordinal snapshot.</summary>
    [Fact]
    public void Facade_copies_values_and_uses_ordinal_keys()
    {
        var supplied = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["Key"] = "original" };
        var configuration = new ExtensionConfiguration(supplied);
        supplied["Key"] = "mutated";
        supplied["another"] = "new";

        Assert.Equal("original", configuration["Key"]);
        Assert.Null(configuration["key"]);
        Assert.Equal("Key", Assert.Single(configuration.Keys));
    }

    /// <summary>Committed delivery deadlines retain authoritative activation despite a stalled subscriber.</summary>
    [Fact]
    public async Task Stalled_activation_subscriber_does_not_roll_back_committed_generation()
    {
        await using var events = new Threadsmith.Execution.DomainEventStream(TimeSpan.FromMilliseconds(10));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = events.Subscribe(async (domainEvent, token) =>
        {
            if (domainEvent is ExtensionActivated)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
        });
        var host = CreateHost(events);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        try
        {
            var loading = host.LoadAsync(Request(), cancellation.Token);
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            await cancellation.CancelAsync();
            var generation = await loading.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(ExtensionLifecycleState.Active, generation.State);
            Assert.Same(generation, Assert.Single(host.Generations));
            Assert.NotNull(host.Capabilities.Get("settings_view"));
        }
        finally
        {
            release.TrySetResult();
            // Fixture teardown needs the stream to remain available for unload events.
            foreach (var generation in host.Generations)
            {
                await host.UnloadAsync(generation, SessionId.New(), TestContext.Current.CancellationToken);
            }

            _hosts.Remove(host);
        }
    }

    /// <summary>Incomplete rollback is reported safely without replacing the load failure.</summary>
    [Fact]
    public async Task Failed_rollback_warns_without_replacing_original_cancellation()
    {
        WriteSettings("""{"throwOnDeactivation":"true"}""");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var loggerFactory = new CancellationLoggerFactory(cancellation) { Enabled = true };
        var host = CreateHost(loggerFactory: loggerFactory);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.LoadAsync(Request(), cancellation.Token));

        Assert.Contains(loggerFactory.Warnings, warning => warning.StartsWith("ExtensionActivationRollbackIncomplete:", StringComparison.Ordinal));
        Assert.DoesNotContain(loggerFactory.Warnings, warning => warning.Contains("private-cleanup-detail", StringComparison.Ordinal));
        Assert.Empty(host.Generations);
        Assert.Empty(host.Capabilities.Registrations);
    }

    /// <summary>Every settings section is validated before any staging side effects.</summary>
    [Fact]
    public async Task Invalid_unselected_settings_fail_before_staging()
    {
        WriteFile("""{"settings":{"another.extension":{"invalid":42}}}""");
        var host = CreateHost();
        var request = Request();

        await Assert.ThrowsAsync<ExtensionLoadException>(() => host.LoadAsync(request, TestContext.Current.CancellationToken));

        Assert.False(Directory.Exists(request.ShadowStagingRoot));
    }

    /// <summary>Activation defers dictionary creation until the descriptor is known.</summary>
    [Fact]
    public void Deferred_settings_materialize_only_the_selected_section()
    {
        WriteSettings("""{"value":"selected"}""");
        var selection = ExtensionSelectionConfig.Load(ConfigPath, deferSettings: true);

        Assert.Empty(selection.Settings);
        var selected = selection.GetSettings(ExtensionId);
        Assert.Equal("selected", selected["value"]);
        Assert.Single(selected);
        Assert.Empty(selection.GetSettings("absent"));
    }

    /// <inheritdoc />
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            foreach (var generation in host.Generations)
            {
                var result = await host.UnloadAsync(generation, SessionId.New(), CancellationToken.None);
                if (result.Outcome == UnloadOutcome.UnloadBlocked)
                {
                    _output.WriteLine("Runtime reported UnloadBlocked; mapped fixture staging retained at {0}", generation.StagingPath);
                }
            }
        }

        _hosts.Clear();

        // Configuration data is test-owned and must be deleted. Mapped assembly staging is owned
        // by the normal runtime path and its UnloadBlocked outcome is reported separately above.
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        Assert.False(Directory.Exists(_root), "The settings fixture temporary directory must be removed.");
    }

    private string ConfigPath => Path.Combine(_root, ".threadsmith", "extensions.json");

    private void WriteFile(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, json, new UTF8Encoding(false));
    }

    private void WriteSettings(string section)
    {
        WriteFile("{\"settings\":{\"threadsmith.tests.settings\":" + section + ",\"another.extension\":{\"otherExtensionOnly\":\"hidden\"}}}");
    }

    private ExtensionHost CreateHost(IDomainEventStream? events = null, ILoggerFactory? loggerFactory = null, SessionPolicyBinding? policyBinding = null)
    {
        var host = new ExtensionHost(
            events ?? new RecordingEventStream(),
            loggerFactory?.CreateLogger<ExtensionHost>() ?? NullLogger<ExtensionHost>.Instance,
            extensionLoggerFactory: loggerFactory,
            repositoryPath: _root,
            policyBinding: policyBinding);
        host.SetDiscoveryDirectory(FixtureOutputDirectory());
        _hosts.Add(host);
        return host;
    }

    private static ExtensionLoadRequest Request()
    {
        return new ExtensionLoadRequest
        {
            ExtensionDirectory = FixtureOutputDirectory(),
            EffectiveTrust = RepositoryTrustLevel.TrustedRead,
            SessionId = SessionId.New(),
            ShadowStagingRoot = Path.Combine(Path.GetTempPath(), "threadsmith-extension-settings-staging", Guid.NewGuid().ToString("N")),
        };
    }

    private static string FixtureOutputDirectory()
    {
        var bin = AppContext.BaseDirectory;
        var root = Path.GetFullPath(Path.Combine(bin, "..", "..", "..", "..", ".."));
        var configuration = bin.Contains("Debug", StringComparison.Ordinal) ? "Debug" : "Release";
        return Path.Combine(root, "tests", "Threadsmith.Extensions.Tests", "Fixtures", "SettingsExtension", "bin", configuration, "net10.0");
    }

    private Task<JsonElement> ReadSettingsAsync(ExtensionHost host, string? repositoryPath = null) => ReadSettingsAsync(host.Capabilities.Get("settings_view")!.ToolProxy!, repositoryPath);

    private async Task<JsonElement> ReadSettingsAsync(ITool proxy, string? repositoryPath = null)
    {
        var context = new ToolExecutionContext(
            ToolInvocationId.New(),
            SessionId.New(),
            RunId.New(),
            new ToolInvocationContext { RepositoryPath = repositoryPath ?? _root, RequestedBy = "test" });
        var result = await proxy.ExecuteAsync(
            proxy.DeserializeInput("{}"),
            context,
            TestContext.Current.CancellationToken);
        return Assert.IsType<JsonElement>(result.Value);
    }

    private void AssertInvalidSettings(IReadOnlyDictionary<string, string?> values)
    {
        WriteSettings(JsonSerializer.Serialize(values));
        AssertSafeFileFailure();
    }

    private void AssertSafeFileFailure()
    {
        var exception = Assert.Throws<ExtensionLoadException>(() => ExtensionSelectionConfig.Load(ConfigPath));
        Assert.Equal(ExtensionSelectionConfig.InvalidSettingsDiagnostic, exception.Message);
        Assert.Null(exception.InnerException);
    }

    private sealed class DiscoveryBarrier : IDomainEventStream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task PublishAsync(IDomainEvent domainEvent, CancellationToken cancellationToken = default)
        {
            if (domainEvent is ExtensionDiscovered)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
        }

        public IDomainEventSubscription Subscribe(Func<IDomainEvent, CancellationToken, Task> handler, int capacity = 256) => throw new NotSupportedException();

        public Task PublishCommittedBatchAsync(IReadOnlyList<IDomainEvent> domainEvents, Func<bool> tryCommit, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            tryCommit();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CancellationLoggerFactory : ILoggerFactory, ILogger
    {
        private readonly CancellationTokenSource _cancellation;

        public CancellationLoggerFactory(CancellationTokenSource cancellation) => _cancellation = cancellation;

        public bool Enabled { get; set; }

        public string CancellationMessage { get; init; } = "SettingsFixtureReady";

        public TaskCompletionSource WorkerStopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Deactivated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Warnings { get; } = [];

        public ILogger CreateLogger(string categoryName) => this;

        public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(message);
            }

            if (Enabled && message == CancellationMessage)
            {
                _cancellation.Cancel();
            }
            else if (message == "SettingsFixtureWorkerStopped")
            {
                WorkerStopped.TrySetResult();
            }
            else if (message == "SettingsFixtureDeactivated")
            {
                Deactivated.TrySetResult();
            }
        }

        public void Dispose()
        {
        }
    }
}
