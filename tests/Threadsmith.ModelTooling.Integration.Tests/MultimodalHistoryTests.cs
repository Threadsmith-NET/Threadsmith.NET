namespace Threadsmith.ModelTooling.Integration.Tests;

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
            Id = $"image-{index}",
            ContentHash = new string('a', 64),
            MediaType = "image/png",
            ByteLength = byteLength,
            Width = 2,
            Height = 2,
            Source = "clipboard",
        };
        ConversationMessage Message(int sequence, RunId run, ConversationRole role, IReadOnlyList<ImageArtifact> images) => new()
        {
            Id = ConversationMessageId.New(),
            SessionId = session,
            RunId = run,
            Sequence = sequence,
            Role = role,
            Content = $"Message {sequence}",
            ContentHash = string.Empty,
            EstimatedTokens = 5,
            OccurredAt = DateTimeOffset.UtcNow,
            Images = images,
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
            Id = "fixture",
            Name = "fixture",
            BaseUri = new Uri("https://example.test/v1/"),
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
        using var client = new HttpClient(new CaptureHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        var provider = new ConfiguredModelProvider(client, catalog, (_, _) => Task.FromResult<string?>(null), timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
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
            SessionId = session,
            RunId = currentRun,
            RepositoryPath = fixture.Root,
            CurrentMessageId = currentMessage.Id,
            Task = new TaskSpecification("Describe current images", []),
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
            message.Role,
            message.SectionId,
            message.ToolCallId,
            message.ToolName,
            Content = CanonicalContent(message),
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
