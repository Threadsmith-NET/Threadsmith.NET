namespace Threadsmith.Mutations.Tests;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Persistence;
using Threadsmith.Telemetry;
using Threadsmith.Workspaces;
using Xunit;

/// <summary>Exercises durable outcome classification with entirely fresh runtime objects.</summary>
public sealed class SourceEditRestartTests
{
    /// <summary>Fresh hosts classify complete and indeterminate byte states without invoking the writer.</summary>
    [Theory]
    [InlineData("before", SourceEditStatus.NotApplied)]
    [InlineData("after", SourceEditStatus.Applied)]
    [InlineData("partial", SourceEditStatus.RecoveryRequired)]
    [InlineData("without-bom", SourceEditStatus.RecoveryRequired)]
    public async Task FreshObjectsReconcileDurableEndpointBytesWithoutReplaying(string diskState, SourceEditStatus expectedStatus)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await RestartFixture.CreateAsync(ct);
        Dictionary<string, byte[]?> finalBytes;

        // Use the real writer, but retain unresolved intent by failing terminal recording.
        // This is not an abrupt-process-death simulation.
        await using (var original = await fixture.OpenAsync(failCompletion: true, ct))
        {
            Assert.Equal(0, original.Workspace.RetainedBaselineBytes);
            var applied = await original.Application.HandleAsync(fixture.Command, ct);
            Assert.Equal(SourceEditStatus.Applied, applied.Status);
            Assert.False(applied.DurableOutcomeRecorded);
            Assert.Equal(1, original.Commits.Count);
            var pending = Assert.Single(await original.Store.GetUnresolvedEffectsAsync(RepositoryIdentity.Create(fixture.Repository), ct));
            Assert.Equal(fixture.Command.EffectId, pending.EffectId);
            Assert.Null(pending.Receipt);
            finalBytes = await fixture.ReadStateAsync(ct);
            Assert.Equal(Encode(Encoding.UTF8, RestartFixture.AfterText), finalBytes["edit.cs"]);
            Assert.Equal(fixture.OriginalBytes["move.txt"], finalBytes["moved.txt"]);
            Assert.Null(finalBytes["delete.txt"]);
            Assert.Null(finalBytes["move.txt"]);
        }

        // Arrange endpoint states after disposing the old workspace. These arrangements
        // do not invoke product recovery or prove restoration of original bytes.
        if (diskState is "before" or "partial")
        {
            await fixture.WriteStateAsync(fixture.OriginalBytes, ct);
            if (diskState == "partial")
            {
                await File.WriteAllBytesAsync(fixture.PathOf("edit.cs"), finalBytes["edit.cs"]!, ct);
            }
        }
        else if (diskState == "without-bom")
        {
            await File.WriteAllBytesAsync(fixture.PathOf("edit.cs"), new UTF8Encoding(false).GetBytes(RestartFixture.AfterText), ct);
            Assert.Equal(RestartFixture.AfterText, await File.ReadAllTextAsync(fixture.PathOf("edit.cs"), ct));
            Assert.NotEqual(Hash(finalBytes["edit.cs"]!), Hash(await File.ReadAllBytesAsync(fixture.PathOf("edit.cs"), ct)));
        }

        var expectedDisk = await fixture.ReadStateAsync(ct);

        // A fresh application constructs a fresh journal. Store and artifact adapters
        // are recreated over the same durable locations, not reused.
        await using var restarted = await fixture.OpenAsync(failCompletion: false, ct);
        Assert.Equal(0, restarted.Workspace.RetainedBaselineBytes);
        var intent = Assert.IsType<MutationEffectRecord>(await restarted.Store.GetEffectAsync(fixture.Command.EffectId, ct));
        Assert.Null(intent.Receipt);
        var reference = Assert.IsType<ExecutionArtifactReference>(intent.SnapshotArtifact);
        var json = Assert.IsType<string>(await restarted.Artifacts.ReadAsync(reference, ct));
        var snapshot = Assert.IsType<MutationEffectSnapshot>(JsonSerializer.Deserialize<MutationEffectSnapshot>(json));
        Assert.Equal(intent.MutationSetId, snapshot.MutationSetId);
        Assert.Equal(5, snapshot.Endpoints.Count);
        Assert.Equal(4, snapshot.AppliedMutations.Count);
        foreach (var endpoint in snapshot.Endpoints)
        {
            Assert.Equal(HashOrAbsent(fixture.OriginalBytes[endpoint.RelativePath]), endpoint.BeforeSha256, ignoreCase: true);
            Assert.Equal(HashOrAbsent(finalBytes[endpoint.RelativePath]), endpoint.AfterSha256, ignoreCase: true);
            Assert.Null(endpoint.FinalBytes);
        }

        var recovered = await restarted.Application.HandleAsync(fixture.Command, ct);
        var replay = await restarted.Application.HandleAsync(fixture.Command, ct);

        Assert.Equal(expectedStatus, recovered.Status);
        Assert.Equal(expectedStatus, replay.Status);
        Assert.Equal(intent.MutationSetId, recovered.MutationSetId);
        Assert.True(recovered.DurableOutcomeRecorded);
        Assert.True(replay.DurableOutcomeRecorded);
        Assert.Equal(0, restarted.Commits.Count);
        Assert.Equal(0, restarted.Workspace.RetainedBaselineBytes);
        var recorded = Assert.IsType<MutationEffectRecord>(await restarted.Store.GetEffectAsync(fixture.Command.EffectId, ct));
        Assert.Equal(expectedStatus, recorded.Receipt?.Status);
        var unresolved = await restarted.Store.GetUnresolvedEffectsAsync(RepositoryIdentity.Create(fixture.Repository), ct);
        if (expectedStatus == SourceEditStatus.RecoveryRequired)
        {
            Assert.Equal(fixture.Command.EffectId, Assert.Single(unresolved).EffectId);
            var next = fixture.Command with { EffectId = Guid.NewGuid() };
            await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.Application.HandleAsync(next, ct));
            Assert.Null(await restarted.Store.GetEffectAsync(next.EffectId, ct));
            Assert.Equal(0, restarted.Commits.Count);
            Assert.Equal(0, restarted.Workspace.RetainedBaselineBytes);
        }
        else
        {
            Assert.Empty(unresolved);
        }

        var actualDisk = await fixture.ReadStateAsync(ct);
        foreach (var path in expectedDisk.Keys)
        {
            Assert.Equal(expectedDisk[path], actualDisk[path]);
        }
    }

    private static byte[] Encode(Encoding encoding, string text)
    {
        return [.. encoding.GetPreamble(), .. encoding.GetBytes(text)];
    }

    private static string Hash(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static string? HashOrAbsent(byte[]? bytes)
    {
        return bytes is null ? null : Hash(bytes);
    }

    private sealed class RestartFixture : IAsyncDisposable
    {
        public const string AfterText = "class Changed { }\r\n";
        private readonly string _root;

        private RestartFixture(string root)
        {
            _root = root;
            OriginalBytes = new Dictionary<string, byte[]?>
            {
                ["edit.cs"] = Encode(Encoding.UTF8, "class Café { }\r\n"),
                ["delete.txt"] = Encode(Encoding.Unicode, "delete Ω\r\n"),
                ["move.txt"] = Encode(Encoding.BigEndianUnicode, "move Ω\r\n"),
                ["moved.txt"] = null,
                ["created.txt"] = null,
            };
            Command = new(
                SessionId.New(),
                RunId.New(),
                WorkspaceId.New(),
                Guid.NewGuid(),
                new MutationProposalSet
                {
                    Rationale = "Exercise a durable multi-endpoint edit.",
                    Mutations =
                    [
                        new ReplaceTextMutationProposal { RelativePath = "edit.cs", ExpectedText = "Café", ReplacementText = "Changed" },
                        new DeleteFileMutationProposal { RelativePath = "delete.txt" },
                        new MoveFileMutationProposal { RelativePath = "move.txt", DestinationRelativePath = "moved.txt" },
                        new CreateFileMutationProposal { RelativePath = "created.txt", Content = new() { Text = "created\r\n" } },
                    ],
                },
                false);
        }

        public string Repository => Path.Combine(_root, "repo");

        public ApplySourceEditCommand Command { get; }

        public IReadOnlyDictionary<string, byte[]?> OriginalBytes { get; }

        private string ConnectionString => $"Data Source={Path.Combine(_root, "state.db")};Pooling=False";

        public static async Task<RestartFixture> CreateAsync(CancellationToken ct)
        {
            var root = Directory.CreateTempSubdirectory("threadsmith-source-edit-restart-").FullName;
            var fixture = new RestartFixture(root);
            Directory.CreateDirectory(fixture.Repository);
            await fixture.WriteStateAsync(fixture.OriginalBytes, ct);
            await new MigrationRunner(fixture.ConnectionString, DefaultMigrations.All).RunAsync(ct);
            await new SqliteEventStore(fixture.ConnectionString).InitializeAsync(ct);
            return fixture;
        }

        public string PathOf(string relativePath)
        {
            return Path.Combine(Repository, relativePath);
        }

        public async Task<RestartHost> OpenAsync(bool failCompletion, CancellationToken ct)
        {
            var state = await ReadStateAsync(ct);
            var baseline = new WorkspaceBaseline(
                Command.WorkspaceId,
                Repository,
                DateTimeOffset.UtcNow,
                state.Where(item => item.Value is not null)
                    .Select(item => new WorkspaceFileHash(item.Key, Hash(item.Value!), item.Value!.LongLength))
                    .ToArray(),
                TrustLevel: RepositoryTrustLevel.TrustedMutation);
            var events = new DomainEventStream();
            var policy = new MutationApprovalPolicyService();
            await policy.SetPolicyAsync(MutationApprovalPolicy.TrustSession, ct);
            var workspaces = new TransactionalWorkspaceCoordinator(events, mutationApprovalPolicy: policy);
            await workspaces.RegisterBaselineAsync(baseline, null, null, deferRecovery: true, ct);
            var store = new ExecutionCheckpointStore(ConnectionString);
            var artifacts = new ExecutionArtifactPublisher(new ArtifactStore(ConnectionString, Path.Combine(_root, "artifacts"), new SecretOutputSanitizer()));
            return new RestartHost(Command.WorkspaceId, events, workspaces, store, artifacts, failCompletion);
        }

        public async Task<Dictionary<string, byte[]?>> ReadStateAsync(CancellationToken ct)
        {
            var state = new Dictionary<string, byte[]?>();
            foreach (var path in OriginalBytes.Keys)
            {
                var fullPath = PathOf(path);
                state[path] = File.Exists(fullPath) ? await File.ReadAllBytesAsync(fullPath, ct) : null;
            }

            return state;
        }

        public async Task WriteStateAsync(IReadOnlyDictionary<string, byte[]?> state, CancellationToken ct)
        {
            foreach (var (path, bytes) in state)
            {
                ct.ThrowIfCancellationRequested();
                if (bytes is null)
                {
                    File.Delete(PathOf(path));
                }
                else
                {
                    await File.WriteAllBytesAsync(PathOf(path), bytes, ct);
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            Directory.Delete(_root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RestartHost : IAsyncDisposable
    {
        private readonly DomainEventStream _events;
        private readonly TransactionalWorkspaceCoordinator _workspaces;

        public RestartHost(WorkspaceId workspaceId, DomainEventStream events, TransactionalWorkspaceCoordinator workspaces, ExecutionCheckpointStore store, IExecutionArtifactPublisher artifacts, bool failCompletion)
        {
            _events = events;
            _workspaces = workspaces;
            Store = store;
            Artifacts = artifacts;
            Workspace = Assert.IsType<TransactionalWorkspace>(workspaces.GetWorkspace(workspaceId));
            Commits = new CountingCommits(workspaces);
            Application = new(
                workspaces,
                Commits,
                workspaces,
                store,
                failCompletion ? new FailingCompletionStore(store) : store,
                artifacts,
                events,
                TestPromptLoader.Instance,
                new SecretOutputSanitizer(),
                reviews: workspaces,
                previews: workspaces);
        }

        public ExecutionCheckpointStore Store { get; }

        public IExecutionArtifactPublisher Artifacts { get; }

        public TransactionalWorkspace Workspace { get; }

        public CountingCommits Commits { get; }

        public SourceEditApplication Application { get; }

        public async ValueTask DisposeAsync()
        {
            await _workspaces.DisposeAsync();
            await _events.DisposeAsync();
        }
    }

    private sealed class FailingCompletionStore : IMutationEffectStore
    {
        private readonly IMutationEffectStore _store;

        public FailingCompletionStore(IMutationEffectStore store)
        {
            _store = store;
        }

        public Task<bool> TryBeginEffectAsync(MutationEffectRecord record, CancellationToken cancellationToken = default)
        {
            return _store.TryBeginEffectAsync(record, cancellationToken);
        }

        public Task<MutationEffectRecord?> GetEffectAsync(Guid effectId, CancellationToken cancellationToken = default)
        {
            return _store.GetEffectAsync(effectId, cancellationToken);
        }

        public Task CompleteEffectAsync(Guid effectId, SourceEditReceipt receipt, CancellationToken cancellationToken = default)
        {
            throw new IOException("Simulated interruption of outcome recording.");
        }

        public Task<IReadOnlyList<MutationEffectRecord>> GetUnresolvedEffectsAsync(string repositoryIdentity, CancellationToken cancellationToken = default)
        {
            return _store.GetUnresolvedEffectsAsync(repositoryIdentity, cancellationToken);
        }
    }

    private sealed class CountingCommits : ICommandHandler<CommitMutationSetCommand, MutationCommitResult>
    {
        private readonly TransactionalWorkspaceCoordinator _workspaces;

        public CountingCommits(TransactionalWorkspaceCoordinator workspaces)
        {
            _workspaces = workspaces;
        }

        public int Count { get; private set; }

        public Task<MutationCommitResult> HandleAsync(CommitMutationSetCommand command, CancellationToken cancellationToken = default)
        {
            Count++;
            return _workspaces.HandleAsync(command, cancellationToken);
        }
    }
}
