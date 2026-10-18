namespace Threadsmith.ModelTooling.Tests;

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
    public void NamedFlagsRoundTrip()
    {
        const ModelContentTypes flags = ModelContentTypes.Text | ModelContentTypes.Image;
        Assert.Equal(flags, JsonSerializer.Deserialize<ModelContentTypes>(JsonSerializer.Serialize(flags)));
    }

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

    /// <summary>Verifies the named multimodal contract through its production entry point.</summary>
    [Fact]
    public async Task ReadAndWriteUseRealPipelineAndPreserveBytes()
    {
        await using var fixture = await Fixture.CreateAsync();
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllBytesAsync(Path.Combine(fixture.Root, "red.png"), Png, token);
        var session = SessionId.New();
        var result = await fixture.InvokeAsync(session, "read_file", "{\"path\":\"red.png\"}", token);
        Assert.True(result.Succeeded, result.Error);
        var image = Assert.Single(result.Images);
        Assert.DoesNotContain(Convert.ToBase64String(Png), result.ResultJson ?? string.Empty, StringComparison.Ordinal);
        var save = await fixture.InvokeAsync(session, "write_file", JsonSerializer.Serialize(new { path = ".inbox/red.png", artifactId = image.Id }), token);
        Assert.True(save.Succeeded, save.Error);
        Assert.Equal(Png, await File.ReadAllBytesAsync(Path.Combine(fixture.Root, ".inbox/red.png"), token));
        foreach (var path in new[] { ".inbox/wrong.jpg", "outside.png", ".inbox/source.cs" })
        {
            var denied = await fixture.InvokeAsync(session, "write_file", JsonSerializer.Serialize(new { path, artifactId = image.Id }), token);
            Assert.False(denied.Succeeded);
        }

        var foreign = await fixture.InvokeAsync(SessionId.New(), "write_file", JsonSerializer.Serialize(new { path = ".inbox/stolen.png", artifactId = image.Id }), token);
        Assert.False(foreign.Succeeded);
        var lines = await fixture.InvokeAsync(session, "read_file", "{\"path\":\"red.png\",\"startLine\":2}", token);
        Assert.False(lines.Succeeded);
    }

    /// <summary>Verifies the named multimodal contract for each supported case.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatWireCarriesActualPixelsAndToolIdentity(bool toolResult)
    {
        await using var fixture = await Fixture.CreateAsync();
        var token = TestContext.Current.CancellationToken;
        var session = SessionId.New();
        var image = await fixture.Store.StoreImageAsync(new MemoryStream(Png), session, "fixture", cancellationToken: token);
        var handler = new CaptureHandler();
        using var client = new HttpClient(handler);
        var configuration = new OpenAiCompatibleProviderConfiguration
        {
            Id = "fixture",
            Name = "fixture",
            BaseUri = new Uri("https://example.test/v1/"),
            Models =
            [
                new OpenAiCompatibleModelConfiguration
                {
                Id = new ModelProfileId(Guid.NewGuid()), Name = "fixture", ModelId = "fixture", ContextWindow = 100000,
                MaximumOutputTokens = 4096, SupportedInputContentTypes = ModelContentTypes.Text | ModelContentTypes.Image,
                SensitiveDataPolicy = ModelSensitiveDataPolicy.Allowed, Capabilities = new ModelCapabilitySet { Streaming = true, ToolCalls = true },
                },
            ],
        };
        var registration = new OpenAiCompatibleProviderRegistration();
        var catalog = new EffectiveModelProviderCatalog(new ModelProviderCatalogConfiguration { Providers = [configuration] }, new ModelProviderRegistry([registration]));
        var provider = new ConfiguredModelProvider(client, catalog, (_, _) => Task.FromResult<string?>(null), images: fixture.Store);
        var imageMessage = new ModelMessage
        {
            Role = toolResult ? ModelMessageRole.Tool : ModelMessageRole.User,
            SectionId = "image",
            ToolCallId = toolResult ? "call_1" : null,
            ToolName = toolResult ? "read_file" : null,
            Content = ModelImages.Append([new ModelContentPart { Content = "Describe the pixels" }, new ModelContentPart { Kind = ModelContentPartKind.Json, Content = JsonSerializer.Serialize(new { attachments = new[] { new { artifactId = image.Id } } }) }], [image]),
        };
        var request = new ModelStreamRequest
        {
            RunId = RunId.New(),
            ImageSessionId = session,
            Input = "image",
            Messages = toolResult ? [new ModelMessage { Role = ModelMessageRole.Assistant, SectionId = "call", ToolName = "read_file", ToolCallId = "call_1", Content = [new ModelContentPart { Content = "{}" }] }, imageMessage] : [imageMessage],
        };
        await foreach (var chunk in provider.StreamAsync(request, token))
        {
            Assert.Null(chunk.Output);
        }

        Assert.Contains(Convert.ToBase64String(Png), handler.Body, StringComparison.Ordinal);
        Assert.Contains(image.Id, handler.Body, StringComparison.Ordinal);
        if (toolResult)
        {
            using var wire = JsonDocument.Parse(handler.Body);
            Assert.Equal("call_1", wire.RootElement.GetProperty("messages")[1].GetProperty("tool_call_id").GetString());
            Assert.Equal("user", wire.RootElement.GetProperty("messages")[2].GetProperty("role").GetString());
        }

        var estimate = ModelWireEstimator.Estimate(request.Messages, [], ToolTransportMode.Native, 0, 4096);
        Assert.True(estimate.WireInputTokens > 2000);
        Assert.DoesNotContain(Convert.ToBase64String(Png), JsonSerializer.Serialize(request), StringComparison.Ordinal);
    }

    /// <summary>Verifies the named multimodal contract through its production entry point.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task GeneratedImageContinuesToModelIssuedWriteBeforeCompletion(int toolOrder)
    {
        await using var fixture = await Fixture.CreateAsync();
        var provider = new GenerateThenSaveProvider { ToolOrder = toolOrder };
        var application = fixture.CreateApplication(provider);
        var token = TestContext.Current.CancellationToken;
        var session = await application.HandleAsync(new CreateSessionCommand("image"), token);
        var run = await application.HandleAsync(new SubmitRequestCommand(session, "Generate and save an image to .inbox/generated.png"), token);
        Assert.True(await application.HandleAsync(new WaitForRunCommand(run), token));
        Assert.Equal(3, provider.Rounds);
        Assert.Equal(Png, await File.ReadAllBytesAsync(Path.Combine(fixture.Root, ".inbox/generated.png"), token));
        var archived = await fixture.Conversations.GetSnapshotAsync(session, cancellationToken: token);
        Assert.Contains(archived.Messages, message => message.Images.Count == 1);
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

    /// <summary>Complete binary signatures do not reject ordinary prose using format names.</summary>
    [Theory]
    [InlineData("GIF support is documented here.")]
    [InlineData("RIFF format is documented here.")]
    public async Task FormatNameTextRemainsReadable(string content)
    {
        await using var fixture = await Fixture.CreateAsync();
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "format.txt"), content, token);
        var result = await fixture.InvokeAsync(SessionId.New(), "read_file", "{\"path\":\"format.txt\"}", token);
        Assert.True(result.Succeeded, result.Error);
        Assert.Contains(content, result.ResultJson ?? string.Empty, StringComparison.Ordinal);
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

    /// <summary>Pasted images can be exported using only model-visible metadata in current or historical turns.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClipboardImageMetadataEnablesModelIssuedExport(bool historical)
    {
        await using var fixture = await Fixture.CreateAsync();
        var token = TestContext.Current.CancellationToken;
        var provider = new SaveAttachmentProvider { Historical = historical };
        var application = fixture.CreateApplication(provider);
        var session = await application.HandleAsync(new CreateSessionCommand("clipboard-export"), token);
        var first = await fixture.Store.StoreImageAsync(new MemoryStream(Png), session, "clipboard", cancellationToken: token);
        var second = await fixture.Store.StoreImageAsync(new MemoryStream(Png), session, "clipboard", cancellationToken: token);
        var run = await application.HandleAsync(new SubmitRequestCommand(session, historical ? "Remember these images." : "Save the second image to .inbox/pasted.png.") { Images = [first, second] }, token);
        Assert.True(await application.HandleAsync(new WaitForRunCommand(run), token));
        if (historical)
        {
            run = await application.HandleAsync(new SubmitRequestCommand(session, "Save the second image from my previous message to .inbox/pasted.png."), token);
            Assert.True(await application.HandleAsync(new WaitForRunCommand(run), token));
        }

        Assert.Equal(second.Id, provider.SavedArtifactId);
        Assert.Equal(Png, await File.ReadAllBytesAsync(Path.Combine(fixture.Root, ".inbox/pasted.png"), token));
        var message = Assert.Single(provider.ExportRequest!.Messages, item => item.SectionId == (historical ? "recent-user" : "current-user"));
        Assert.Equal(new[] { first, second }, message.Content.Where(part => part.Kind == ModelContentPartKind.Image).Select(part => part.Image));
        using var metadata = JsonDocument.Parse(Assert.Single(message.Content, part => part.Kind == ModelContentPartKind.Json).Content);
        var attachments = metadata.RootElement.GetProperty("attachments");
        Assert.Equal(2, attachments.GetArrayLength());
        Assert.Equal(first.Id, attachments[0].GetProperty("artifactId").GetString());
        Assert.Equal(second.Id, attachments[1].GetProperty("artifactId").GetString());
        Assert.Equal("image/png", attachments[1].GetProperty("mediaType").GetString());
        Assert.Equal(second.Width, attachments[1].GetProperty("width").GetInt32());
        Assert.Equal(second.Height, attachments[1].GetProperty("height").GetInt32());
        Assert.Equal(Png.Length, attachments[1].GetProperty("byteLength").GetInt32());
        Assert.DoesNotContain(Convert.ToBase64String(Png), message.GetModelVisibleContent(), StringComparison.Ordinal);
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

    /// <summary>Binary classification preserves the established BOM-aware text reader.</summary>
    [Theory]
    [InlineData("auto")]
    [InlineData("text")]
    public async Task Utf16ReadRemainsText(string mode)
    {
        await using var fixture = await Fixture.CreateAsync();
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "unicode.txt"), "hello unicode", Encoding.Unicode, token);
        var result = await fixture.InvokeAsync(SessionId.New(), "read_file", JsonSerializer.Serialize(new { path = "unicode.txt", mode }), token);
        Assert.True(result.Succeeded, result.Error);
        Assert.Contains("hello unicode", result.ResultJson ?? string.Empty, StringComparison.Ordinal);
        Assert.Empty(result.Images);
    }

    /// <summary>Knowing a sibling image ID does not authorize a child's write operation.</summary>
    [Fact]
    public async Task ChildExportRequiresAdmittedReference()
    {
        await using var fixture = await Fixture.CreateAsync();
        var token = TestContext.Current.CancellationToken;
        var session = SessionId.New();
        var image = await fixture.Store.StoreImageAsync(new MemoryStream(Png), session, "parent", cancellationToken: token);
        var request = new ToolInvocationRequest
        {
            SessionId = session,
            RunId = RunId.New(),
            ToolId = "write_file",
            ArgumentsJson = JsonSerializer.Serialize(new { path = ".inbox/child.png", artifactId = image.Id }),
            Context = fixture.Invocation with { IsChildAgent = true },
        };
        Assert.False((await fixture.Pipeline.InvokeAsync(request, token)).Succeeded);
        Assert.True((await fixture.Pipeline.InvokeAsync(request with { Context = request.Context with { AdmittedImageIds = new HashSet<string> { image.Id } } }, token)).Succeeded);
    }

    /// <summary>High-detail admission covers the largest documented compatible tile schedule.</summary>
    [Fact]
    public void HighDetailMiniImageCostIsNotUnderestimated()
        => Assert.True(ModelImages.EstimateTokens(new ImageArtifact { Id = "test", ContentHash = "test", MediaType = "image/png", ByteLength = 100, Width = 512, Height = 512, Source = "test" }) >= 8500);

    /// <summary>The streamed caller bound, independent of an initial length observation, rejects growth before publication.</summary>
    [Fact]
    public async Task StreamedCallerLimitRejectsImageBeforePublication()
    {
        await using var fixture = await Fixture.CreateAsync();
        var token = TestContext.Current.CancellationToken;
        using var stream = new MemoryStream(Png);
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

    /// <summary>Atomic session cloning copies archived image grants and omits unrelated draft grants.</summary>
    [Fact]
    public async Task SessionCloneCopiesOnlyArchivedImageOwnership()
    {
        await using var fixture = await Fixture.CreateAsync();
        var token = TestContext.Current.CancellationToken;
        var source = SessionId.New();
        var destination = SessionId.New();
        var archived = await fixture.Store.StoreImageAsync(new MemoryStream(Png), source, "archived", cancellationToken: token);
        var draft = await fixture.Store.StoreImageAsync(new MemoryStream(Png), source, "draft", cancellationToken: token);
        await fixture.Conversations.ArchiveMessageAsync(
            new ConversationMessage
            {
                Id = ConversationMessageId.New(),
                SessionId = source,
                RunId = RunId.New(),
                Role = ConversationRole.User,
                Content = "image",
                Images = [archived],
                OccurredAt = DateTimeOffset.UtcNow,
                Sequence = 0,
                ContentHash = string.Empty,
                EstimatedTokens = 0,
            },
            token);
        var lifecycle = new SqliteSessionLifecycleStore($"Data Source={Path.Combine(fixture.Root, "state.db")};Pooling=False");
        await lifecycle.CloneAsync(
            source,
            new SessionCatalogEntry
        {
            SessionId = destination,
            CloneSourceSessionId = source,
            RepositoryIdentity = "repo",
            RepositoryDisplayName = "repo",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            State = SessionLifecycleState.Idle,
        },
            new SessionDurableUsage(0, 0, false, false, false),
            token);
        var clone = await fixture.Conversations.GetSnapshotAsync(destination, cancellationToken: token);
        Assert.Equal(archived, Assert.Single(Assert.Single(clone.Messages).Images));
        Assert.Equal(Png, (await fixture.Store.ReadImageAsync(archived, destination, token)).ToArray());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Store.GetImageAsync(draft.Id, destination, token));
        Assert.Single(await fixture.Store.ListAsync(null, token));
    }

    /// <summary>Opt-in maximum-pixel PNG measurement records decoder latency and process allocation without a flaky timing gate.</summary>
    [Fact]
    public async Task MeasureMaximumPixelImageAdmission()
    {
        var path = Environment.GetEnvironmentVariable("THREADSMITH_IMAGE_BENCHMARK_FILE");
        if (string.IsNullOrWhiteSpace(path))
        {
            Assert.Skip("Set THREADSMITH_IMAGE_BENCHMARK_FILE to the deterministic 4096x4096 PNG fixture.");
        }

        await using var fixture = await Fixture.CreateAsync();
        var token = TestContext.Current.CancellationToken;
        await using var stream = File.OpenRead(path);
        var allocated = GC.GetTotalAllocatedBytes(true);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var image = await fixture.Store.StoreImageAsync(stream, SessionId.New(), "maximum-pixel-fixture", cancellationToken: token);
        watch.Stop();
        Assert.Equal(ImageArtifactLimits.MaximumPixels, image.Width * image.Height);
        TestContext.Current.TestOutputHelper?.WriteLine($"Encoded={image.ByteLength} bytes; elapsed={watch.Elapsed.TotalMilliseconds:F0} ms; process allocated delta={GC.GetTotalAllocatedBytes(true) - allocated} bytes.");
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

    private sealed class SaveAttachmentProvider : IModelProvider
    {
        private int _rounds;

        public bool Historical { get; init; }

        public string? SavedArtifactId { get; private set; }

        public ModelStreamRequest? ExportRequest { get; private set; }

        public async IAsyncEnumerable<ModelChunk> StreamAsync(ModelStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            _rounds++;
            if (_rounds == (Historical ? 2 : 1))
            {
                ExportRequest = request;
                var message = Assert.Single(request.Messages, item => item.SectionId == (Historical ? "recent-user" : "current-user"));
                using var metadata = JsonDocument.Parse(Assert.Single(message.Content, part => part.Kind == ModelContentPartKind.Json).Content);
                SavedArtifactId = metadata.RootElement.GetProperty("attachments")[1].GetProperty("artifactId").GetString();
                yield return new ModelChunk { Output = new ToolRequestModelOutput("write_file", JsonSerializer.Serialize(new { path = ".inbox/pasted.png", artifactId = SavedArtifactId })) };
                yield return new ModelChunk { FinishReason = ModelFinishReason.ToolCalls };
            }
            else
            {
                yield return new ModelChunk { Text = "Done.", FinishReason = ModelFinishReason.Stop };
            }
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
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: [DONE]\n", Encoding.UTF8, "text/event-stream") };
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
            var registry = new ToolRegistry([
                new ReadFileTool(TestPromptLoader.Instance, new SecretOutputSanitizer(), limits, artifacts: store),
                new WriteFileTool(new WriteFileConfiguration(configuration, configuration, root), conversations, TestPromptLoader.Instance, artifacts: store),
            ]);
            Pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new DenyApprovalPolicy(), Events, new SecretOutputSanitizer(), NullLogger<ToolInvocationPipeline>.Instance);
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
                new ExecutionBudget(new BudgetDimensions(500000, 100, TimeSpan.FromMinutes(5))),
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
