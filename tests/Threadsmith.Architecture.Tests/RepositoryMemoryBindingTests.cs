namespace Threadsmith.Architecture.Tests;

using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Threadsmith.App;
using Threadsmith.Core;
using Threadsmith.Mcp;
using Threadsmith.Persistence;
using Threadsmith.Tools;
using Threadsmith.Workspaces;
using Xunit;

/// <summary>Failed repository opens preserve target memory content and defer destructive migrations.</summary>
public static class RepositoryMemoryBindingTests
{
    /// <summary>A late rebind failure preserves both current and legacy target notes; a successful retry applies the cap.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task Failed_open_preserves_memories_until_successful_retry(bool legacy)
    {
        await using var fixture = await BindingFixture.CreateAsync(legacy);
        using var router = new RepositoryBoundMemoryStore(fixture.InitialConnection, fixture.InitialRoot);
        var configuration = new ConfigurationBuilder().Build();
        var options = new RepositoryMemoryConfiguration(configuration, configuration, fixture.InitialRoot);
        var configPath = Path.Combine(fixture.InitialRoot, ".threadsmith", "config.json");
        var toolState = new ToolStateManager(
            [],
            configuration,
            configPath,
            userConsentPath: Path.Combine(fixture.Root, "consent.json"),
            mcpApprovalPath: Path.Combine(fixture.Root, "mcp.json"));
        var mcp = new FailingRebindManager();
        var coordinator = new RepositoryScopedBindingCoordinator(
            fixture.InitialRoot,
            toolState,
            null,
            new MutationApprovalPolicyService(configuration, configPath),
            new PlanApprovalPolicyService(configuration, configPath),
            new RepositorySecretProvider(fixture.InitialRoot),
            mcp,
            router,
            options,
            _ => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["tools:config:memories:MaxNumberOfRepoMemories"] = "1",
            }).Build());

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.BindRepositoryAsync(fixture.TargetRoot, TestContext.Current.CancellationToken));

        Assert.Equal("Injected late repository binding failure.", failure.Message);
        Assert.Equal(legacy ? 9L : 11L, await fixture.ScalarAsync("SELECT max(version) FROM schema_version;"));
        Assert.Equal(2L, await fixture.ScalarAsync(legacy
            ? "SELECT count(*) FROM repository_memory;"
            : "SELECT count(*) FROM managed_memories;"));
        Assert.Empty(Directory.EnumerateFiles(fixture.TargetRoot, "*.backup", SearchOption.AllDirectories));
        var initialIdentity = RepositoryIdentity.Create(fixture.InitialRoot);
        Assert.Empty((await router.GetSnapshotAsync(initialIdentity, [], TestContext.Current.CancellationToken)).Entries);
        Assert.Equal(20, options.Capture(initialIdentity).MaxNumberOfRepoMemories);

        await coordinator.BindRepositoryAsync(fixture.TargetRoot, TestContext.Current.CancellationToken);

        var targetIdentity = RepositoryIdentity.Create(fixture.TargetRoot);
        Assert.Single((await router.GetSnapshotAsync(targetIdentity, [], TestContext.Current.CancellationToken)).Entries);
        Assert.Equal(1, options.Capture(targetIdentity).MaxNumberOfRepoMemories);
        Assert.Equal(11L, await fixture.ScalarAsync("SELECT max(version) FROM schema_version;"));
        if (legacy)
        {
            Assert.Single(Directory.EnumerateFiles(fixture.TargetRoot, "*.backup", SearchOption.AllDirectories));
        }
    }

    private sealed class FailingRebindManager : IMcpManager
    {
        private bool _failNext = true;

        public Task AutoConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RebindRepositoryAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_failNext)
            {
                _failNext = false;
                throw new InvalidOperationException("Injected late repository binding failure.");
            }

            return Task.CompletedTask;
        }

        public Task<McpManagementResult> ExecuteAsync(McpManagementRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BindingFixture : IAsyncDisposable
    {
        private BindingFixture(string root)
        {
            Root = root;
            InitialRoot = Path.Combine(root, "initial");
            TargetRoot = Path.Combine(root, "target");
            InitialConnection = Connection(InitialRoot);
        }

        internal string Root { get; }

        internal string InitialRoot { get; }

        internal string TargetRoot { get; }

        internal string InitialConnection { get; }

        public ValueTask DisposeAsync()
        {
            var expectedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            var normalized = Path.GetFullPath(Root);
            if (Path.GetDirectoryName(normalized) != expectedParent
                || !Path.GetFileName(normalized).StartsWith("threadsmith-memory-bind-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Refusing to remove an unowned binding fixture.");
            }

            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(TargetRoot, ".threadsmith", "threadsmith.db") }.ToString()))
            {
                SqliteConnection.ClearPool(connection);
            }

            Directory.Delete(normalized, recursive: true);
            return ValueTask.CompletedTask;
        }

        internal static async Task<BindingFixture> CreateAsync(bool legacy)
        {
            var fixture = new BindingFixture(Path.Combine(Path.GetTempPath(), "threadsmith-memory-bind-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(Path.Combine(fixture.InitialRoot, ".threadsmith"));
            Directory.CreateDirectory(Path.Combine(fixture.TargetRoot, ".threadsmith"));
            await new SqliteEventStore(fixture.InitialConnection).InitializeAsync();
            await new MigrationRunner(fixture.InitialConnection, DefaultMigrations.All).RunAsync();
            var targetConnection = Connection(fixture.TargetRoot);
            await new SqliteEventStore(targetConnection).InitializeAsync();
            await new MigrationRunner(targetConnection, legacy ? DefaultMigrations.All.Take(10) : DefaultMigrations.All).RunAsync();
            var identity = RepositoryIdentity.Create(fixture.TargetRoot);
            for (var index = 0; index < 2; index++)
            {
                if (legacy)
                {
                    await using var connection = new SqliteConnection(targetConnection);
                    await connection.OpenAsync();
                    await using var command = connection.CreateCommand();
                    command.CommandText = """
                        INSERT INTO repository_memory(memory_id, repository_identity, kind, authority, validity, sensitivity,
                            content, content_hash, created_at, updated_at, schema_version)
                        VALUES($id, $repo, 0, $authority, $validity, 0, $text, '', $now, $now, 1);
                        INSERT INTO repository_memory_sources(memory_id, source_kind, source_id, ordinal)
                        VALUES($id, $source, 'explicit-fixture', 0);
                        """;
                    command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
                    command.Parameters.AddWithValue("$repo", identity);
                    command.Parameters.AddWithValue("$authority", (int)RepositoryMemoryAuthority.UserAuthored);
                    command.Parameters.AddWithValue("$validity", (int)RepositoryMemoryValidity.Active);
                    command.Parameters.AddWithValue("$source", (int)RepositoryMemorySourceKind.UserCommand);
                    command.Parameters.AddWithValue("$text", "Preserve explicit note " + index);
                    command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                    await command.ExecuteNonQueryAsync();
                }
                else
                {
                    await new SqliteManagedRepositoryMemoryStore(targetConnection).AddAsync(
                        identity,
                        new RepositoryMemoryWrite { Text = "Preserve explicit note " + index, Origin = RepositoryMemoryOrigin.Manual },
                        new TextEmbeddingModelDescriptor("fixture", 1, 256),
                        new TextEmbeddingResult(new float[] { 1 }, 5, false),
                        new RepositoryMemoryOptions());
                }
            }

            return fixture;
        }

        internal async Task<long> ScalarAsync(string sql)
        {
            await using var connection = new SqliteConnection(Connection(TargetRoot));
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }

        private static string Connection(string repositoryRoot)
            => new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(repositoryRoot, ".threadsmith", "threadsmith.db"),
                Pooling = false,
            }.ToString();
    }
}
