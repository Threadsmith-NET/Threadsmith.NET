namespace Threadsmith.ModelTooling.Tests;

using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Models.OpenAiCompatible;
using Threadsmith.Telemetry;
using Xunit;

public sealed partial class MultimodalTests
{
    /// <summary>Historical pixels yield to required images before real provider request preparation.</summary>
    [Theory]
    [InlineData(8, 1, 100, 0, 1)]
    [InlineData(8, 8, 100, 0, 8)]
    [InlineData(7, 1, 100, 0, 0)]
    [InlineData(8, 9, 100, 0, 8)]
    [InlineData(1, 5, 5242880, 0, 1)]
    [InlineData(4, 1, 5242880, 0, 1)]
    [InlineData(3, 1, 5242880, 0, 0)]
    [InlineData(8, 1, 100, 1, 2)]
    [InlineData(4, 1, 5242880, 1, 2)]
    public async Task HistoricalImageLimitsPreserveCurrentAttachments(
        int historyCount, int currentCount, int byteLength, int toolCount, int omittedCount)
    {
        await using var fixture = await Fixture.CreateAsync();
        var token = TestContext.Current.CancellationToken;
        var session = SessionId.New();
        var priorRun = RunId.New();
        var currentRun = RunId.New();
        ImageArtifact Image(int index) => new()
        {
            Id = $"image-{index}", ContentHash = new string('a', 64), MediaType = "image/png",
            ByteLength = byteLength, Width = 2, Height = 2, Source = "clipboard",
        };
        ConversationMessage Message(int sequence, RunId run, ConversationRole role, IReadOnlyList<ImageArtifact> images) => new()
        {
            Id = ConversationMessageId.New(), SessionId = session, RunId = run, Sequence = sequence,
            Role = role, Content = $"Message {sequence}", ContentHash = string.Empty,
            EstimatedTokens = 5, OccurredAt = DateTimeOffset.UtcNow, Images = images,
        };
        var historical = Enumerable.Range(0, historyCount).Select(Image).ToArray();
        var current = Enumerable.Range(100, currentCount).Select(Image).ToArray();
        var older = Message(1, priorRun, ConversationRole.User, historical[..1]);
        var newer = Message(3, RunId.New(), ConversationRole.User, historical[1..]);
        var currentMessage = Message(5, currentRun, ConversationRole.User, current);
        var snapshot = new ConversationStateSnapshot
        {
            SessionId = session,
            Messages = [older, Message(2, priorRun, ConversationRole.Assistant, []), newer,
                Message(4, newer.RunId, ConversationRole.Assistant, []), currentMessage],
        };
        var configuration = new OpenAiCompatibleProviderConfiguration
        {
            Id = "fixture", Name = "fixture", BaseUri = new Uri("https://example.test/v1/"),
            Models =
            [
                new OpenAiCompatibleModelConfiguration
                {
                Id = new ModelProfileId(Guid.NewGuid()), Name = "fixture", ModelId = "fixture",
                ContextWindow = 200000, MaximumOutputTokens = 4096,
                SupportedInputContentTypes = ModelContentTypes.Text | ModelContentTypes.Image,
                SensitiveDataPolicy = ModelSensitiveDataPolicy.Allowed,
                Capabilities = new ModelCapabilitySet { Streaming = true, ToolCalls = true },
                },
            ],
        };
        var catalog = new EffectiveModelProviderCatalog(
            new ModelProviderCatalogConfiguration { Providers = [configuration] },
            new ModelProviderRegistry([new OpenAiCompatibleProviderRegistration()]));
        using var client = new HttpClient(new CaptureHandler());
        var provider = new ConfiguredModelProvider(client, catalog, (_, _) => Task.FromResult<string?>(null));
        var sanitizer = new SecretOutputSanitizer();
        var assembler = new ContextAssembler(
            new EvidenceStore(fixture.Events, sanitizer),
            new TokenEstimator(),
            new ContextPolicy(),
            new PromptAppendLoader(sanitizer),
            sanitizer,
            fixture.Events,
            TestPromptLoader.Instance,
            new ContextAssemblerOptions { MaximumTokens = 200000 },
            modelResolver: new ModelResolver(catalog.ModelCatalog, new InMemoryModelPreferenceSnapshotProvider()),
            conversationStore: new ImageHistoryStore(snapshot),
            requestPreparationResolver: provider);
        var request = new ContextAssemblyRequest
        {
            SessionId = session, RunId = currentRun, RepositoryPath = fixture.Root,
            CurrentMessageId = currentMessage.Id, Task = new TaskSpecification("Describe current images", []),
            AdditionalMessages = toolCount == 0 ? [] :
            [
                new ModelMessage
                {
                    Role = ModelMessageRole.Assistant, SectionId = "tool-call", ToolCallId = "call-1", ToolName = "read_file",
                    Content = [new ModelContentPart { Content = "{}" }],
                },
                new ModelMessage
                {
                    Role = ModelMessageRole.Tool, SectionId = "tool-image-result", ToolCallId = "call-1", ToolName = "read_file",
                    Content = ModelImages.Append([], [Image(200)]),
                },
            ],
        };
        if (currentCount > ImageArtifactLimits.MaximumCount || (long)currentCount * byteLength > ImageArtifactLimits.MaximumRequestBytes)
        {
            await Assert.ThrowsAsync<ModelProviderException>(() => assembler.AssembleAsync(request, token));
            return;
        }

        var result = await assembler.AssembleAsync(request, token);
        Assert.NotNull(result.Messages);
        Assert.NotNull(result.Layout);
        string CanonicalContent(ModelMessage message) => message.Content.Any(part => part.IsModelVisible && part.Image is not null)
            ? JsonSerializer.Serialize(message.Content.Where(part => part.IsModelVisible)) : message.GetModelVisibleContent();
        var legacyMessages = result.Messages.Take(result.Layout.StablePrefixMessageCount).Select(message => new
        {
            message.Role, message.SectionId, message.ToolCallId, message.ToolName, Content = CanonicalContent(message),
        });
        var legacyEncoding = result.ProviderInstructions is { } instructions
            ? JsonSerializer.Serialize(new { ProviderInstructions = new { instructions.SectionId, instructions.Content }, Messages = legacyMessages })
            : JsonSerializer.Serialize(legacyMessages);
        Assert.Equal("sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(legacyEncoding))), result.Layout.StablePrefixDigest);
        var imageSegments = result.Messages.Select((message, index) => (message, index))
            .Where(pair => pair.message.Content.Any(part => part.IsModelVisible && part.Image is not null));
        foreach (var (message, index) in imageSegments)
        {
            var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalContent(message))));
            Assert.Equal(digest, result.Layout.Segments[index + (result.ProviderInstructions is null ? 0 : 1)].Digest);
        }

        var pixels = result.Messages.SelectMany(message => message.Content)
            .Where(part => part.Kind == ModelContentPartKind.Image).Select(part => part.Image!.Id).ToArray();
        Assert.Equal(
            historical.Skip(omittedCount).Concat(current).Select(image => image.Id)
                .Concat(toolCount == 0 ? [] : new[] { "image-200" }),
            pixels);
        var receipts = result.Messages.Where(message => message.SectionId == "recent-user")
            .SelectMany(message => message.Content).Where(part => part.Kind == ModelContentPartKind.Json)
            .Select(part => JsonDocument.Parse(part.Content)).ToArray();
        try
        {
            var omitted = receipts.Where(json => json.RootElement.TryGetProperty("omittedImagePixels", out _))
                .SelectMany(json => json.RootElement.GetProperty("omittedImagePixels").EnumerateArray())
                .Select(item => item.GetProperty("artifactId").GetString()).ToArray();
            Assert.Equal(historical.Take(omittedCount).Select(image => image.Id), omitted);
            Assert.Equal(historyCount, receipts.Where(json => json.RootElement.TryGetProperty("attachments", out _))
                .Sum(json => json.RootElement.GetProperty("attachments").GetArrayLength()));
        }
        finally
        {
            foreach (var receipt in receipts)
            {
                receipt.Dispose();
            }
        }

        Assert.Contains("Message 1", result.ModelInput, StringComparison.Ordinal);
        Assert.Contains("Message 3", result.ModelInput, StringComparison.Ordinal);
        Assert.Equal(omittedCount > 0, result.ModelInput.Contains("aggregate-image-limit", StringComparison.Ordinal));
        Assert.Equal(omittedCount > 0, result.Inspection.ConversationItems.Any(item => item.ImageDisposition?.Contains("aggregate image limits", StringComparison.Ordinal) == true));
        Assert.Equal(historyCount, snapshot.Messages.Sum(message => message.Sequence < 5 ? message.Images.Count : 0));
    }

    /// <summary>Tool results appended to frozen context force historical selection through the real conversation loop.</summary>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task ToolImageAfterFullHistoricalContextCompletes(bool currentAttachment, bool nativeReplay, bool pendingMemoryRefresh)
    {
        await using var fixture = await Fixture.CreateAsync();
        var token = TestContext.Current.CancellationToken;
        var provider = new ReadAfterHistoryProvider { NativeReplay = nativeReplay };
        var resolver = new ModelResolver(new ConfiguredModelCatalog([provider.Profile]), new InMemoryModelPreferenceSnapshotProvider());
        var application = fixture.CreateApplication(provider, 200000, resolver, pendingMemoryRefresh ? provider : null, pendingMemoryRefresh ? provider : null);
        var session = await application.HandleAsync(new CreateSessionCommand("image history"), token);
        var image = await fixture.Store.StoreImageAsync(new MemoryStream(Png), session, "clipboard", cancellationToken: token);
        var priorRun = RunId.New();
        foreach (var role in new[] { ConversationRole.User, ConversationRole.Assistant })
        {
            var archived = new ConversationMessage
            {
                Id = ConversationMessageId.New(), SessionId = session, RunId = priorRun,
                Role = role, Content = "Prior image turn", ContentHash = string.Empty, EstimatedTokens = 5,
                Sequence = 0, OccurredAt = DateTimeOffset.UtcNow,
                Images = role == ConversationRole.User ? Enumerable.Repeat(image, currentAttachment ? 7 : 8).ToArray() : [],
            };
            await fixture.Conversations.ArchiveMessageAsync(archived, token);
        }

        await File.WriteAllBytesAsync(Path.Combine(fixture.Root, "next.png"), Png, token);
        var submission = new SubmitRequestCommand(session, "Read next.png and describe it")
        {
            Images = currentAttachment ? [image] : [],
        };
        var run = await application.HandleAsync(submission, token);
        Assert.True(await application.HandleAsync(new WaitForRunCommand(run), token));
        Assert.Equal(2, provider.Requests.Count);
        Assert.All(provider.Requests, request => Assert.Equal(8, request.Messages.SelectMany(message => message.Content).Count(part => part.Kind == ModelContentPartKind.Image)));
        var second = provider.Requests[1];
        Assert.False(second.TransientState?.HasResponses);
        Assert.Contains(second.Messages, message => message.SectionId == "recent-user" && message.GetModelVisibleContent().Contains("aggregate-image-limit", StringComparison.Ordinal));
        Assert.Equal(currentAttachment ? 1 : 0, second.Messages.Single(message => message.SectionId == "current-user").Content.Count(part => part.Kind == ModelContentPartKind.Image));
        Assert.Contains(second.Messages, message => message.Role == ModelMessageRole.Tool && message.Content.Any(part => part.Kind == ModelContentPartKind.Image));
    }

    /// <summary>A full current draft leaves tool metadata visible without overflowing the next provider request.</summary>
    [Theory]
    [InlineData(8, false, false, false)]
    [InlineData(8, true, false, false)]
    [InlineData(8, true, true, false)]
    [InlineData(7, false, false, true)]
    [InlineData(7, true, true, true)]
    public async Task ToolImagesYieldToCurrentAttachments(int currentCount, bool nativeReplay, bool pendingMemoryRefresh, bool pdf)
    {
        await using var fixture = await Fixture.CreateAsync();
        var token = TestContext.Current.CancellationToken;
        var provider = new ReadAfterHistoryProvider
        {
            NativeReplay = nativeReplay,
            ExpectNativeReplay = nativeReplay,
            ToolArguments = pdf ? "{\"path\":\"next.pdf\",\"maximumPages\":2}" : "{\"path\":\"next.png\"}",
        };
        var resolver = new ModelResolver(new ConfiguredModelCatalog([provider.Profile]), new InMemoryModelPreferenceSnapshotProvider());
        var application = fixture.CreateApplication(provider, 200000, resolver, pendingMemoryRefresh ? provider : null, pendingMemoryRefresh ? provider : null);
        var session = await application.HandleAsync(new CreateSessionCommand("current images"), token);
        var image = await fixture.Store.StoreImageAsync(new MemoryStream(Png), session, "clipboard", cancellationToken: token);
        await File.WriteAllBytesAsync(Path.Combine(fixture.Root, pdf ? "next.pdf" : "next.png"), pdf ? CreatePdf() : Png, token);
        var submission = new SubmitRequestCommand(session, "Read the file and describe it")
        {
            Images = Enumerable.Repeat(image, currentCount).ToArray(),
        };
        var run = await application.HandleAsync(submission, token);
        Assert.True(await application.HandleAsync(new WaitForRunCommand(run), token));
        Assert.Equal(2, provider.Requests.Count);
        var second = provider.Requests[1];
        Assert.Equal(8, second.Messages.SelectMany(message => message.Content).Count(part => part.Kind == ModelContentPartKind.Image));
        Assert.Equal(currentCount, second.Messages.Single(message => message.SectionId == "current-user").Content.Count(part => part.Kind == ModelContentPartKind.Image));
        var tool = Assert.Single(second.Messages, message => message.Role == ModelMessageRole.Tool);
        Assert.Equal(pdf ? 1 : 0, tool.Content.Count(part => part.Kind == ModelContentPartKind.Image));
        Assert.Contains("aggregate-image-limit", tool.GetModelVisibleContent(), StringComparison.Ordinal);
        Assert.Contains("artifactId", tool.GetModelVisibleContent(), StringComparison.Ordinal);
        Assert.False(tool.IsError);
        ModelImages.Validate(second, provider.Profile);
    }

    private sealed class ReadAfterHistoryProvider : IModelProvider, IModelRequestPreparationResolver, IManagedRepositoryMemoryService, IRepositoryMemoryOptionsProvider
    {
        public ModelProfile Profile { get; } = new()
        {
            Id = new ModelProfileId(Guid.NewGuid()), Name = "fixture", Provider = "fixture", ModelId = "fixture",
            Endpoint = new Uri("https://example.test/v1/"), ContextWindow = 200000, MaximumOutputTokens = 4096,
            SupportedInputContentTypes = ModelContentTypes.Text | ModelContentTypes.Image,
            SensitiveDataPolicy = ModelSensitiveDataPolicy.Allowed,
            Capabilities = new ModelCapabilitySet { Streaming = true, ToolCalls = true, StructuredOutput = true },
        };

        public List<ModelStreamRequest> Requests { get; } = [];

        public bool NativeReplay { get; init; }

        public bool ExpectNativeReplay { get; init; }

        public string ToolArguments { get; init; } = "{\"path\":\"next.png\"}";

        private int MemoryVersion { get; set; } = 1;

        public RepositoryMemoryOptions CaptureCurrent() => new() { StandingPreferenceWarningThreshold = MemoryVersion };

        public RepositoryMemoryOptions Capture(string repositoryIdentity) => CaptureCurrent();

        public Task<RepositoryMemoryOperationResult> ExecuteAsync(RepositoryMemoryOperationRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<RepositoryMemoryReadSnapshot> GetSnapshotAsync(string repositoryIdentity, CancellationToken cancellationToken = default)
            => Task.FromResult(new RepositoryMemoryReadSnapshot(repositoryIdentity, 0, [], [], []));

        public Task<IReadOnlyList<RepositoryMemoryId>> EnforceCapacityAsync(string repositoryIdentity, RepositoryMemoryOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RepositoryMemoryId>>([]);

        public Task RecordInclusionsAsync(string repositoryIdentity, RunId runId, IReadOnlyList<RepositoryMemoryInclusion> inclusions, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public ModelStreamRequest Prepare(ModelStreamRequest request)
        {
            ModelImages.Validate(request, Profile);
            return request;
        }

        public async IAsyncEnumerable<ModelChunk> StreamAsync(ModelStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (Requests.Count == 1)
            {
                if (NativeReplay)
                {
                    yield return new ModelChunk
                    {
                        ResponseEnvelope = new ModelResponseReplayEnvelope(
                            new ModelReplayBinding
                            {
                                ProviderId = "fixture", ModelId = "fixture", ProfileId = Profile.Id,
                                RunId = request.RunId, ModelRound = request.ToolContinuationRound,
                                HistoryRewriteGeneration = request.HistoryRewriteGeneration,
                                CredentialGeneration = "fixture", ToolInventoryDigest = "fixture-tools",
                                InstructionDigest = "fixture-instructions", NormalizedRoundDigest = "fixture-round",
                            },
                            [1],
                            ["wire-1"],
                            retainedOutputTokens: 1),
                    };
                }

                MemoryVersion++;
                yield return new ModelChunk { Output = new ToolRequestModelOutput("read_file", ToolArguments) };
                yield return new ModelChunk { FinishReason = ModelFinishReason.ToolCalls };
            }
            else
            {
                Assert.Equal(ExpectNativeReplay, request.TransientState?.HasResponses ?? false);
                yield return new ModelChunk { Text = "Done.", FinishReason = ModelFinishReason.Stop };
            }
        }
    }

    private sealed class ImageHistoryStore(ConversationStateSnapshot snapshot) : IConversationStore
    {
        public Task<IReadOnlyList<ConversationMessage>> GetMessagesAsync(SessionId sessionId, IReadOnlyCollection<ConversationMessageId> messageIds, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ConversationMessage>>(snapshot.Messages.Where(message => message.SessionId == sessionId && messageIds.Contains(message.Id)).ToArray());

        public Task<ConversationStateSnapshot> GetSnapshotAsync(SessionId sessionId, bool includeBodies = true, ConversationHistoryWindow? historyWindow = null, CancellationToken cancellationToken = default)
            => Task.FromResult(snapshot);

        public Task<ConversationMessage> ArchiveMessageAsync(ConversationMessage message, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task SetModeAsync(SessionId sessionId, ConversationContextMode mode, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<int> RemoveMessageBodiesOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
