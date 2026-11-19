namespace Threadsmith.ModelTooling.Integration.Tests;

using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Models.OpenAiCompatible;
using Threadsmith.Models.OpenAiCompatible.OpenRouter;
using Threadsmith.Persistence;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

/// <summary>Exercises shared image storage, real tools, wire mapping and output finality.</summary>
public sealed partial class MultimodalTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAEElEQVR4nGP4z8AARAwQCgAf7gP9i18U1AAAAABJRU5ErkJggg==");

    /// <summary>Verifies the named multimodal contract for each supported case.</summary>
    [Theory]
    [InlineData("3")]
    [InlineData("\"3\"")]
    [InlineData("\"Image\"")]
    [InlineData("\"Text, Audio\"")]
    [InlineData("\"\"")]
    public void FlagsRejectInvalidValues(string json)
        => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ModelContentTypes>(json));

    /// <summary>Verifies the named multimodal contract through its production entry point.</summary>
    [Fact]
    public async Task ImagesAreScopedValidatedAndProtectedFromDeletion()
    {
        await using var fixture = await Fixture.CreateAsync();
        var token = TestContext.Current.CancellationToken;
        var session = SessionId.New();
        var image = await fixture.Store.StoreImageAsync(new MemoryStream(Png), session, "fixture", cancellationToken: token);
        Assert.Equal(("image/png", 2, 2), (image.MediaType, image.Width, image.Height));
        Assert.Equal(Png, (await fixture.Store.ReadImageAsync(image, session, token)).ToArray());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Store.ReadImageAsync(image, SessionId.New(), token));
        Assert.Null(await fixture.Store.ReadAsync(image.ContentHash, token));
        Assert.False(await fixture.Store.DeleteAsync(image.ContentHash, token));
        var corrupt = Png.ToArray();
        corrupt[40] ^= 1;
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.StoreImageAsync(new MemoryStream(corrupt), session, "bad", cancellationToken: token));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.StoreImageAsync(new MemoryStream(Png[..^1]), session, "bad", cancellationToken: token));
        var clone = SessionId.New();
        await fixture.Store.GrantImageAsync(image.Id, session, clone, token);
        await fixture.Store.ReleaseImageAsync(image.Id, session, token);
        Assert.False(await fixture.Store.DeleteAsync(image.ContentHash, token));
        await fixture.Store.ReleaseImageAsync(image.Id, clone, token);
        Assert.Empty(await fixture.Store.ListAsync(null, token));
        Assert.False(await fixture.Store.DeleteAsync(image.ContentHash, token));
    }

    /// <summary>A completed image is terminal; an incomplete response publishes no artifact reference.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedImageRespectsCompletionBoundary(bool incomplete)
    {
        await using var fixture = await Fixture.CreateAsync();
        var token = TestContext.Current.CancellationToken;
        var provider = new GenerateThenSaveProvider { StopAfterImage = true, Incomplete = incomplete };
        var application = fixture.CreateApplication(provider);
        var session = await application.HandleAsync(new CreateSessionCommand("generated"), token);
        var run = await application.HandleAsync(new SubmitRequestCommand(session, "Generate an image."), token);
        if (incomplete)
        {
            await Assert.ThrowsAsync<MalformedModelOutputException>(() => application.HandleAsync(new WaitForRunCommand(run), token));
        }
        else
        {
            Assert.True(await application.HandleAsync(new WaitForRunCommand(run), token));
        }

        Assert.Equal(1, provider.Rounds);
        var archive = await fixture.Conversations.GetSnapshotAsync(session, cancellationToken: token);
        Assert.Equal(incomplete ? 0 : 1, archive.Messages.Sum(message => message.Images.Count));
        Assert.False(File.Exists(Path.Combine(fixture.Root, ".inbox/generated.png")));
    }

    /// <summary>Image-only submission crosses archive and the real context assembler without losing pixels.</summary>
    [Fact]
    public async Task ImageOnlySubmissionUsesRealContextAssembler()
    {
        await using var fixture = await Fixture.CreateAsync();
        var token = TestContext.Current.CancellationToken;
        var provider = new CaptureModel();
        var application = fixture.CreateApplication(provider);
        var session = await application.HandleAsync(new CreateSessionCommand("image-only"), token);
        var image = await fixture.Store.StoreImageAsync(new MemoryStream(Png), session, "clipboard", cancellationToken: token);
        var run = await application.HandleAsync(new SubmitRequestCommand(session, string.Empty) { Images = [image] }, token);
        Assert.True(await application.HandleAsync(new WaitForRunCommand(run), token));
        Assert.Equal(image, Assert.Single(provider.Request!.Messages.Single(message => message.SectionId == "current-user").Content, part => part.Kind == ModelContentPartKind.Image).Image);
        Assert.True(provider.Request.WireEstimate!.WireInputTokens >= ModelImages.EstimateTokens(image));
    }

    /// <summary>Restored draft removal preserves archived ownership; retention honors live pins then releases bytes.</summary>
    [Fact]
    public async Task ArchivedOwnershipAndActiveLeaseSurviveDraftReleaseAndRetention()
    {
        await using var fixture = await Fixture.CreateAsync();
        var token = TestContext.Current.CancellationToken;
        var session = SessionId.New();
        var image = await fixture.Store.StoreImageAsync(new MemoryStream(Png), session, "clipboard", cancellationToken: token);
        await fixture.Conversations.ArchiveMessageAsync(
            new ConversationMessage
            {
                Id = ConversationMessageId.New(),
                SessionId = session,
                RunId = RunId.New(),
                Role = ConversationRole.User,
                Content = "image",
                Images = [image],
                OccurredAt = DateTimeOffset.UtcNow,
                Sequence = 0,
                ContentHash = string.Empty,
                EstimatedTokens = 0,
            },
            token);
        await fixture.Store.ReleaseImageAsync(image.Id, session, token);
        Assert.Equal(image, await fixture.Store.GetImageAsync(image.Id, session, token));
        using (await fixture.Store.PinSessionImagesAsync(session, token))
        {
            await fixture.Conversations.RemoveMessageBodiesOlderThanAsync(DateTimeOffset.UtcNow.AddDays(1), token);
            Assert.Equal(Png, (await fixture.Store.ReadImageAsync(image, session, token)).ToArray());
        }

        Assert.Equal(1, await fixture.Store.PruneImageReferencesAsync(DateTimeOffset.UtcNow.AddDays(1), token));
        Assert.True(await fixture.Store.DeleteAsync(image.ContentHash, token));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Store.GetImageAsync(image.Id, session, token));
    }

    /// <summary>The streamed caller bound, independent of an initial length observation, rejects growth before publication.</summary>
    [Fact]
    public async Task StreamedCallerLimitRejectsImageBeforePublication()
    {
        await using var fixture = await Fixture.CreateAsync();
        var token = TestContext.Current.CancellationToken;
        await using var stream = new MemoryStream(Png);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.StoreImageAsync(stream, SessionId.New(), "growing-file", maximumBytes: Png.Length - 1, cancellationToken: token));
        Assert.Empty(await fixture.Store.ListAsync(null, token));
    }

    /// <summary>Standalone artifact storage does not require the conversation schema for image cleanup.</summary>
    [Fact]
    public async Task StandaloneImageCleanupDoesNotRequireConversationSchema()
    {
        await using var fixture = await Fixture.CreateAsync(migrate: false);
        var token = TestContext.Current.CancellationToken;
        var session = SessionId.New();
        var image = await fixture.Store.StoreImageAsync(new MemoryStream(Png), session, "standalone", cancellationToken: token);
        await fixture.Store.ReleaseImageAsync(image.Id, session, token);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Store.GetImageAsync(image.Id, session, token));
        await fixture.Store.StoreImageAsync(new MemoryStream(Png), session, "standalone", cancellationToken: token);
        Assert.Equal(1, await fixture.Store.PruneImageReferencesAsync(DateTimeOffset.MaxValue, token));
        Assert.True(await fixture.Store.DeleteAsync(image.ContentHash, token));
    }

    /// <summary>Invalid or prohibited image requests fail before any artifact bytes are resolved.</summary>
    [Theory]
    [InlineData("text-only")]
    [InlineData("privacy")]
    [InlineData("count")]
    [InlineData("dimensions")]
    public async Task ImageAdmissionRejectsBeforeResolvingBytes(string failure)
    {
        var profile = new ModelProfile
        {
            Id = new ModelProfileId(Guid.NewGuid()),
            Name = "image",
            Provider = "fixture",
            ModelId = "fixture",
            Endpoint = new Uri("https://example.test/"),
            SupportedInputContentTypes = failure == "text-only" ? ModelContentTypes.Text : ModelContentTypes.Text | ModelContentTypes.Image,
            SensitiveDataPolicy = failure == "privacy" ? ModelSensitiveDataPolicy.Prohibited : ModelSensitiveDataPolicy.Allowed,
        };
        var image = new ImageArtifact
        {
            Id = "image",
            ContentHash = new string('a', 64),
            MediaType = "image/png",
            ByteLength = Png.Length,
            Width = failure == "dimensions" ? ImageArtifactLimits.MaximumDimension + 1 : 2,
            Height = 2,
            Source = "test",
        };
        var resolved = false;
        var request = new ModelStreamRequest
        {
            RunId = RunId.New(),
            Input = "inspect",
            Messages = [new ModelMessage { SectionId = "image", Role = ModelMessageRole.User, Content = ModelImages.Append([], Enumerable.Repeat(image, failure == "count" ? 9 : 1).ToArray()) }],
            ResolveImageAsync = (_, _) =>
            {
                resolved = true;
                return Task.FromResult<ReadOnlyMemory<byte>>(Png);
            },
        };
        await Assert.ThrowsAsync<ModelProviderException>(() => ModelImages.ResolveAsync(request, profile, TestContext.Current.CancellationToken));
        Assert.False(resolved);
    }

    private sealed class CaptureModel : IModelProvider
    {
        public ModelStreamRequest? Request { get; private set; }

        public async IAsyncEnumerable<ModelChunk> StreamAsync(ModelStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            yield return new ModelChunk { Text = "Observed image.", FinishReason = ModelFinishReason.Stop };
        }
    }

    private sealed class GenerateThenSaveProvider : IModelProvider
    {
        public int Rounds { get; private set; }

        public int ToolOrder { get; init; }

        public bool StopAfterImage { get; init; }

        public bool Incomplete { get; init; }

        public async IAsyncEnumerable<ModelChunk> StreamAsync(ModelStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            Rounds++;
            if (Rounds == 1)
            {
                if (ToolOrder == 1)
                {
                    yield return new ModelChunk { Output = new ToolRequestModelOutput("write_file", "{\"path\":\".inbox/intermediate.txt\",\"content\":\"intermediate\"}") };
                }

                var generated = new ModelChunk { GeneratedImage = new GeneratedModelImage { ResponseItemId = "response/image", Bytes = Png } };
                yield return generated;
                yield return generated;
                if (ToolOrder == 2)
                {
                    yield return new ModelChunk { Output = new ToolRequestModelOutput("write_file", "{\"path\":\".inbox/intermediate.txt\",\"content\":\"intermediate\"}") };
                }

                if (!Incomplete)
                {
                    yield return new ModelChunk { FinishReason = StopAfterImage ? ModelFinishReason.Stop : ModelFinishReason.ToolCalls };
                }
            }
            else if (Rounds == 2)
            {
                var metadata = request.Messages.Single(message => message.SectionId == "generated-image-artifacts").GetModelVisibleContent();
                using var json = JsonDocument.Parse(metadata);
                var id = json.RootElement.GetProperty("generatedImages")[0].GetProperty("Id").GetString();
                yield return new ModelChunk { Output = new ToolRequestModelOutput("write_file", JsonSerializer.Serialize(new { path = ".inbox/generated.png", artifactId = id })) };
                yield return new ModelChunk { FinishReason = ModelFinishReason.ToolCalls };
            }
            else
            {
                yield return new ModelChunk { Text = "Saved.", FinishReason = ModelFinishReason.Stop };
            }
        }
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var body = request.RequestUri?.AbsolutePath.EndsWith("/responses", StringComparison.Ordinal) == true
                ? "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[],\"usage\":{\"input_tokens\":1,\"output_tokens\":0}}}\n\n"
                : "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(string root, ArtifactStore store, SqliteConversationStore conversations, ToolLimits? limits)
        {
            Root = root;
            Store = store;
            Conversations = conversations;
            Events = new DomainEventStream();
            Invocation = new ToolInvocationContext { RepositoryPath = root, TrustLevel = RepositoryTrustLevel.TrustedRead, RequestedBy = "model" };
            var configuration = new ConfigurationBuilder().Build();
            var registry = new ToolRegistry(
                [
                    new ReadFileTool(TestPromptLoader.Instance, new SecretOutputSanitizer(), limits, artifacts: store),
                    new WriteFileTool(new WriteFileConfiguration(configuration, configuration, root), conversations, TestPromptLoader.Instance, artifacts: store),
                ],
                runtimeOptions: new ToolRuntimeOptions
                {
                    // Content tests follow runner cancellation, not production wall-clock deadlines.
                    Defaults = new ToolRuntimeOverride { TimeoutMilliseconds = -1 },
                });
            Pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new DenyApprovalPolicy(), Events, new SecretOutputSanitizer(), NullLogger<ToolInvocationPipeline>.Instance, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
        }

        public string Root { get; }

        public ArtifactStore Store { get; }

        public SqliteConversationStore Conversations { get; }

        public DomainEventStream Events { get; }

        public ToolInvocationContext Invocation { get; }

        public ToolInvocationPipeline Pipeline { get; }

        public static async Task<Fixture> CreateAsync(bool migrate = true, long maximumRetainedImageBytes = ArtifactStore.DefaultMaximumRetainedImageBytes, ToolLimits? limits = null)
        {
            var root = Directory.CreateDirectory(Path.Combine(TestTemporaryPath.PhysicalPath(), "threadsmith-images-" + Guid.NewGuid().ToString("N"))).FullName;
            var connection = $"Data Source={Path.Combine(root, "state.db")};Pooling=False";
            if (migrate)
            {
                await new MigrationRunner(connection, DefaultMigrations.All).RunAsync(TestContext.Current.CancellationToken);
            }

            var store = new ArtifactStore(connection, Path.Combine(root, "artifacts"), new SecretOutputSanitizer(), maximumRetainedImageBytes: maximumRetainedImageBytes);
            await store.InitializeAsync(TestContext.Current.CancellationToken);
            return new Fixture(root, store, new SqliteConversationStore(connection, store, new SecretOutputSanitizer()), limits);
        }

        public SessionApplication CreateApplication(IModelProvider provider, int maximumContextTokens = 32000, IModelResolver? modelResolver = null, IManagedRepositoryMemoryService? memories = null, IRepositoryMemoryOptionsProvider? memoryOptions = null)
        {
            var sanitizer = new SecretOutputSanitizer();
            var evidence = new EvidenceStore(Events, sanitizer);
            var assembler = new ContextAssembler(evidence, new TokenEstimator(), new ContextPolicy(), new PromptAppendLoader(sanitizer), sanitizer, Events, TestPromptLoader.Instance, new ContextAssemblerOptions { MaximumTokens = maximumContextTokens }, modelResolver: modelResolver, conversationStore: Conversations, requestPreparationResolver: provider as IModelRequestPreparationResolver);
            return new SessionApplication(
                Events,
                provider,
                new ExecutionBudget(new BudgetDimensions(500000, 100, TimeSpan.MaxValue)),
                sanitizer,
                NullLogger<SessionApplication>.Instance,
                Pipeline,
                (_, _) => Task.FromResult(Invocation),
                assembler,
                evidence,
                Pipeline.Registry,
                correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
                prompts: TestPromptLoader.Instance,
                conversationStore: Conversations,
                imageArtifacts: Store,
                repositoryMemories: memories,
                repositoryMemoryOptions: memoryOptions);
        }

        public Task<ToolInvocationResult> InvokeAsync(SessionId session, string tool, string arguments, CancellationToken cancellationToken)
            => Pipeline.InvokeAsync(new ToolInvocationRequest { SessionId = session, RunId = RunId.New(), ToolId = tool, ArgumentsJson = arguments, Context = Invocation }, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await Events.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (!Path.GetFileName(Root).StartsWith("threadsmith-images-", StringComparison.Ordinal)
                || !string.Equals(Path.GetDirectoryName(Root), TestTemporaryPath.PhysicalPath(), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Unexpected fixture cleanup root.");
            }

            Directory.Delete(Root, recursive: true);
        }
    }
}
