namespace Threadsmith.ContextCaching.Tests;

using System.Collections;
using System.Security.Cryptography;
using System.Text;
using Threadsmith.Context;
using Threadsmith.Core;
using Xunit;

/// <summary>Plan 90 deployed prompt loader, cache, and template acceptance coverage.</summary>
public sealed class DeployedPromptLoaderTests
{
    /// <summary>The internal production limits retain the public deployed prompt byte contracts.</summary>
    [Fact]
    public void DeployedPromptLoadLimits_Production_PreservesExactDefaults()
    {
        var limits = DeployedPromptLoadLimits.Production;

        Assert.Equal(128 * 1024, DeployedPromptLoader.MaximumFileBytes);
        Assert.Equal(4 * 1024 * 1024, DeployedPromptLoader.MaximumCatalogBytes);
        Assert.Equal(DeployedPromptLoader.MaximumFileBytes, limits.MaximumFileBytes);
        Assert.Equal(DeployedPromptLoader.MaximumCatalogBytes, limits.MaximumCatalogBytes);
    }

    /// <summary>Invalid prompt load limits fail before any filesystem access.</summary>
    [Fact]
    public void DeployedPromptLoadLimits_InvalidValues_FailFast()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeployedPromptLoadLimits(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeployedPromptLoadLimits(1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeployedPromptLoadLimits(2, 1));
    }

    /// <summary>The shared in-memory consumer fixture preserves production one-pass token semantics.</summary>
    [Fact]
    public void TestPromptLoader_Render_DoesNotRecursivelyInterpretTokenValues()
    {
        var rendered = TestPromptLoader.Instance.Render(
            PromptFileNames.CorrectionToolBatchPreflightFailed,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Ordinal"] = "1",
                ["Tool"] = "{{Reason}}",
                ["Reason"] = "safe",
            });

        Assert.Equal("Call 1 ({{Reason}}) failed preflight: safe", rendered);
    }

    /// <summary>Platform rendering normalizes template framing without rewriting token content.</summary>
    [Fact]
    public void PromptAssetRenderer_NormalizesOnlyTemplateLineEndings()
    {
        var prompts = TestPromptLoader.Instance.WithPrompt(
            PromptFileNames.CorrectionToolBatchPreflightFailed,
            "start\n{{Reason}}\nend");

        var rendered = PromptAssetRenderer.RenderWithPlatformLineEndings(
            prompts,
            PromptFileNames.CorrectionToolBatchPreflightFailed,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Ordinal"] = "unused",
                ["Tool"] = "unused",
                ["Reason"] = "value\nkept",
            });

        Assert.Equal(
            "start" + Environment.NewLine + "value\nkept" + Environment.NewLine + "end",
            rendered);
    }

    private static readonly UTF8Encoding Utf8WithoutBom = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>The complete declared catalog loads with exact content and safe deterministic metadata.</summary>
    [Fact]
    public async Task LoadAsync_CompleteCatalog_PreservesExactBytesAndDigests()
    {
        using var catalog = TemporaryPromptCatalog.CreateCompleteCatalog();
        const string exactContent = "first line\r\nsecond line\n{ordinary-braces}";
        catalog.WriteText(PromptFileNames.SystemSystemPrompt, exactContent);

        var loader = await DeployedPromptLoader.LoadAsync(catalog.BaseDirectory);

        var expectedBytes = Utf8WithoutBom.GetBytes(exactContent);
        var expectedDigest = ComputeDigest(expectedBytes);
        var metadata = Assert.Single(
            loader.Assets,
            asset => string.Equals(asset.FileName, PromptFileNames.SystemSystemPrompt, StringComparison.Ordinal));
        Assert.Equal(PromptAssetCatalog.All.Count, loader.Assets.Count);
        Assert.Equal(
            PromptAssetCatalog.All.Select(definition => definition.FileName).Order(StringComparer.Ordinal),
            loader.Assets.Select(asset => asset.FileName));
        Assert.Equal(exactContent, loader.Get(PromptFileNames.SystemSystemPrompt));
        Assert.Equal(expectedBytes.Length, metadata.Bytes);
        Assert.Equal(expectedDigest, metadata.Digest);
        Assert.Equal(catalog.TotalBytes, loader.TotalBytes);
        Assert.Equal(ComputeCatalogDigest(loader.Assets), loader.CatalogDigest);
    }

    /// <summary>Repeated and concurrent access remains cache-only after the deployed files change.</summary>
    [Fact]
    public async Task GetAndRender_AfterInitialization_UseOnlyImmutableCache()
    {
        var definition = PromptAssetCatalog.Get(PromptFileNames.ToolRunProcessDescription);
        using var catalog = TemporaryPromptCatalog.Create(definition);
        var original = TemporaryPromptCatalog.CreateValidContent(definition);
        var values = CreateRequiredValues(definition, "cached-{{ShellLanguage}}-value");
        var loader = await catalog.LoadAsync();
        var expectedRendered = ReplaceMarkersOnce(original, values);

        catalog.WriteText(definition.FileName, "changed-on-disk");
        Directory.Delete(catalog.PromptRoot, recursive: true);

        const int readerCount = 6;
        var readersEntered = 0;
        var allReadersEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReaders = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, readerCount)
            .Select(async index =>
            {
                if (Interlocked.Increment(ref readersEntered) == readerCount)
                {
                    allReadersEntered.TrySetResult();
                }

                await releaseReaders.Task.WaitAsync(TimeSpan.FromSeconds(5));
                return index % 2 == 0
                    ? loader.Get(definition.FileName)
                    : loader.Render(definition.FileName, values);
            })
            .ToArray();

        try
        {
            await allReadersEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            releaseReaders.TrySetResult();
        }

        var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.All(
            results.Where((_, index) => index % 2 == 0),
            result => Assert.Equal(original, result));
        Assert.All(
            results.Where((_, index) => index % 2 != 0),
            result => Assert.Equal(expectedRendered, result));
    }

    /// <summary>Rendering requires every required token and substitutes token values only once.</summary>
    [Fact]
    public async Task Render_RequiredTokens_AreStrictAndNonRecursive()
    {
        var definition = PromptAssetCatalog.Get(PromptFileNames.ToolRunProcessDescription);
        using var catalog = TemporaryPromptCatalog.Create(definition);
        catalog.WriteText(
            definition.FileName,
            "before {ordinary} {{ShellLanguage}} after {{ShellLanguage}}");
        var loader = await catalog.LoadAsync();

        var missing = Assert.Throws<ArgumentException>(() =>
            loader.Render(definition.FileName, new Dictionary<string, string>(StringComparer.Ordinal)));
        var rendered = loader.Render(
            definition.FileName,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ShellLanguage"] = "{{ShellLanguage}} and {{UnknownMarker}}",
            });

        Assert.Contains("Required prompt token", missing.Message, StringComparison.Ordinal);
        Assert.Equal(
            "before {ordinary} {{ShellLanguage}} and {{UnknownMarker}} after {{ShellLanguage}} and {{UnknownMarker}}",
            rendered);
    }

    /// <summary>Declared optional block tokens may be omitted or supplied without leaving a marker behind.</summary>
    [Fact]
    public async Task Render_OptionalTokens_MayBeOmittedOrSupplied()
    {
        var definition = PromptAssetCatalog.Get(PromptFileNames.ToolDelegateAgentsFinding);
        using var catalog = TemporaryPromptCatalog.Create(definition);
        Assert.Equal(
            ["FilePathBlock", "SymbolBlock", "UncertaintyBlock"],
            definition.OptionalTokens.Order(StringComparer.Ordinal));
        catalog.WriteText(
            definition.FileName,
            "{{AssignmentId}}{{FilePathBlock}}{{SymbolBlock}}{{Title}}{{Evidence}}{{Confidence}}{{UncertaintyBlock}}");
        var loader = await catalog.LoadAsync();
        var required = CreateRequiredValues(definition, "required");

        var omitted = loader.Render(definition.FileName, required);
        var supplied = new Dictionary<string, string>(required, StringComparer.Ordinal)
        {
            ["FilePathBlock"] = "file",
            ["SymbolBlock"] = "symbol",
            ["UncertaintyBlock"] = "uncertain",
        };

        Assert.Equal("requiredrequiredrequiredrequired", omitted);
        Assert.Equal("requiredfilesymbolrequiredrequiredrequireduncertain", loader.Render(definition.FileName, supplied));
    }

    /// <summary>Undeclared token input is rejected without disclosing its value.</summary>
    [Fact]
    public async Task Render_UnknownToken_RejectsWithoutValueDisclosure()
    {
        var definition = PromptAssetCatalog.Get(PromptFileNames.ToolRunProcessDescription);
        using var catalog = TemporaryPromptCatalog.Create(definition);
        var loader = await catalog.LoadAsync();
        var tokens = CreateRequiredValues(definition, "ordinary");
        tokens["UnexpectedToken"] = "secret-token-value";

        var exception = Assert.Throws<ArgumentException>(() => loader.Render(definition.FileName, tokens));

        Assert.Contains("not declared", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token-value", exception.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Unknown and malformed markers fail catalog initialization with safe diagnostics.</summary>
    [Theory]
    [InlineData("{{UnknownMarker}}", "token-contract")]
    [InlineData("{{Broken", "invalid-token-marker")]
    [InlineData("{{Bad-Name}}", "invalid-token-marker")]
    public async Task LoadAsync_UnresolvedOrMalformedMarker_FailsSafely(
        string marker,
        string expectedCategory)
    {
        var definition = PromptAssetCatalog.Get(PromptFileNames.SystemSystemPrompt);
        using var catalog = TemporaryPromptCatalog.Create(definition);
        const string secretBody = "secret-body-value";
        catalog.WriteText(definition.FileName, secretBody + marker);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.LoadAsync());

        Assert.Contains(expectedCategory, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secretBody, exception.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A template missing one of its declared required markers fails atomically.</summary>
    [Fact]
    public async Task LoadAsync_MissingRequiredMarker_FailsSafely()
    {
        var definition = PromptAssetCatalog.Get(PromptFileNames.ToolRunProcessDescription);
        using var catalog = TemporaryPromptCatalog.Create(definition);
        catalog.WriteText(definition.FileName, "no declared marker appears here");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.LoadAsync());

        Assert.Contains("token-contract", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("no declared marker appears here", exception.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A missing prompt root fails before any catalog can be returned.</summary>
    [Fact]
    public async Task LoadAsync_MissingRoot_FailsSafely()
    {
        using var directory = TemporaryDirectory.Create();
        var definition = PromptAssetCatalog.Get(PromptFileNames.SystemSystemPrompt);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DeployedPromptLoader.LoadAsync(
                directory.Path,
                [definition],
                DeployedPromptLoadLimits.Production));

        Assert.Contains("directory is missing", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(directory.Path, exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A missing declared prompt file fails with a safe category and no content.</summary>
    [Fact]
    public async Task LoadAsync_MissingFile_FailsSafely()
    {
        var definition = PromptAssetCatalog.Get(PromptFileNames.SystemSystemPrompt);
        using var catalog = TemporaryPromptCatalog.Create(definition);
        File.Delete(catalog.GetAssetPath(definition.FileName));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.LoadAsync());

        Assert.Contains("(missing)", exception.Message, StringComparison.Ordinal);
        Assert.Contains(definition.FileName, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(catalog.BaseDirectory, exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            catalog.GetAssetPath(definition.FileName),
            exception.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An inaccessible declared prompt file fails with a safe category.</summary>
    [Fact]
    public async Task LoadAsync_UnreadableFile_FailsSafely()
    {
        var definition = PromptAssetCatalog.Get(PromptFileNames.SystemSystemPrompt);
        using var catalog = TemporaryPromptCatalog.Create(definition);
        await using var exclusive = new FileStream(
            catalog.GetAssetPath(definition.FileName),
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.LoadAsync());

        Assert.Contains("(unreadable)", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(catalog.BaseDirectory, exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            catalog.GetAssetPath(definition.FileName),
            exception.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Malformed UTF-8 fails closed without copying bytes into diagnostics.</summary>
    [Fact]
    public async Task LoadAsync_InvalidUtf8_FailsSafely()
    {
        var definition = PromptAssetCatalog.Get(PromptFileNames.SystemSystemPrompt);
        using var catalog = TemporaryPromptCatalog.Create(definition);
        catalog.WriteBytes(definition.FileName, [0x73, 0x65, 0x63, 0x72, 0x65, 0x74, 0xc3, 0x28]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.LoadAsync());

        Assert.Contains("(invalid-utf8)", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", exception.ToString(), StringComparison.Ordinal);
    }

    /// <summary>NUL content fails closed without copying the prompt body into diagnostics.</summary>
    [Fact]
    public async Task LoadAsync_NulContent_FailsSafely()
    {
        var definition = PromptAssetCatalog.Get(PromptFileNames.SystemSystemPrompt);
        using var catalog = TemporaryPromptCatalog.Create(definition);
        const string secretBody = "secret-before\0secret-after";
        catalog.WriteText(definition.FileName, secretBody);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.LoadAsync());

        Assert.Contains("(nul-content)", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-before", exception.ToString(), StringComparison.Ordinal);
    }

    /// <summary>One prompt cannot exceed the code-owned per-file byte bound.</summary>
    [Fact]
    public async Task LoadAsync_PerFileOverflow_FailsSafely()
    {
        var definition = PromptAssetCatalog.Get(PromptFileNames.SystemSystemPrompt);
        using var catalog = TemporaryPromptCatalog.Create(definition);
        var limits = new DeployedPromptLoadLimits(maximumFileBytes: 16, maximumCatalogBytes: 32);
        catalog.WriteBytes(definition.FileName, new byte[limits.MaximumFileBytes + 1]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.LoadAsync(limits));

        Assert.Contains("(file-size-limit)", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>The required catalog cannot exceed its aggregate byte bound.</summary>
    [Fact]
    public async Task LoadAsync_AggregateOverflow_FailsSafely()
    {
        PromptAssetDefinition[] definitions =
        [
            PromptAssetCatalog.Get(PromptFileNames.SystemSystemPrompt),
            PromptAssetCatalog.Get(PromptFileNames.SystemPhaseEvidenceCollection),
        ];
        Assert.All(definitions, definition =>
        {
            Assert.Empty(definition.RequiredTokens);
            Assert.Empty(definition.OptionalTokens);
        });
        using var catalog = TemporaryPromptCatalog.Create(definitions);
        catalog.WriteText(definitions[0].FileName, "first-small-prompt");
        catalog.WriteText(definitions[1].FileName, "second-small-prompt");
        var fileBytes = definitions
            .Select(definition => checked((int)new FileInfo(catalog.GetAssetPath(definition.FileName)).Length))
            .ToArray();
        var limits = new DeployedPromptLoadLimits(
            maximumFileBytes: fileBytes.Max(),
            maximumCatalogBytes: fileBytes.Sum() - 1);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.LoadAsync(limits));

        Assert.Contains("(catalog-size-limit)", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Case-distinct physical Markdown names are rejected on case-sensitive filesystems.</summary>
    [Fact]
    public async Task LoadAsync_CaseInsensitivePhysicalCollision_FailsSafely()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            Assert.Skip("The host filesystem does not support distinct case-only names.");
        }

        var definition = PromptAssetCatalog.Get(PromptFileNames.SystemSystemPrompt);
        using var catalog = TemporaryPromptCatalog.Create(definition);
        catalog.WriteText(definition.FileName.ToUpperInvariant(), "collision");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.LoadAsync());

        Assert.Contains("case-insensitive filename collision", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Unknown physical files do not join the runtime catalog or become addressable.</summary>
    [Fact]
    public async Task LoadAsync_UnknownExtraFile_IgnoresAndCannotAddressIt()
    {
        var definition = PromptAssetCatalog.Get(PromptFileNames.SystemSystemPrompt);
        using var catalog = TemporaryPromptCatalog.Create(definition);
        const string unknownName = "Unknown-Extra.md";
        catalog.WriteText(unknownName, "unknown-extra-body");

        var loader = await catalog.LoadAsync();
        var exception = Assert.Throws<ArgumentException>(() => loader.Get(unknownName));

        Assert.Single(loader.Assets);
        Assert.DoesNotContain(loader.Assets, asset =>
            string.Equals(asset.FileName, unknownName, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("not declared", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("unknown-extra-body", exception.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Unknown lookups do not probe the filesystem after initialization.</summary>
    [Fact]
    public async Task Get_UnknownNameAfterRootRemoval_RejectsWithoutFileSystemProbe()
    {
        var definition = PromptAssetCatalog.Get(PromptFileNames.SystemSystemPrompt);
        using var catalog = TemporaryPromptCatalog.Create(definition);
        var loader = await catalog.LoadAsync();
        Directory.Delete(catalog.PromptRoot, recursive: true);

        var exception = Assert.Throws<ArgumentException>(() => loader.Get("Unknown-Lookup.md"));

        Assert.Contains("not declared", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>A pre-cancelled initialization publishes no partial loader and a later clean load succeeds.</summary>
    [Fact]
    public async Task LoadAsync_Cancelled_DoesNotPublishPartialCatalog()
    {
        var definition = PromptAssetCatalog.Get(PromptFileNames.SystemSystemPrompt);
        using var catalog = TemporaryPromptCatalog.Create(definition);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            catalog.LoadAsync(cancellationToken: cancellation.Token));
        var loader = await catalog.LoadAsync();

        Assert.Single(loader.Assets);
    }

    /// <summary>A linked prompt file is rejected before its external target can be read.</summary>
    [Fact]
    public async Task LoadAsync_SymbolicLinkFile_FailsSafelyWhenSupported()
    {
        var definition = PromptAssetCatalog.Get(PromptFileNames.SystemSystemPrompt);
        using var catalog = TemporaryPromptCatalog.Create(definition);
        using var outside = TemporaryDirectory.Create();
        var linkPath = catalog.GetAssetPath(definition.FileName);
        var targetPath = Path.Combine(outside.Path, "external.md");
        await File.WriteAllTextAsync(targetPath, "external-secret", Utf8WithoutBom);
        File.Delete(linkPath);
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or PlatformNotSupportedException)
        {
            Assert.Skip($"Symbolic-link creation is unavailable: {exception.GetType().Name}.");
        }

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.LoadAsync());

        Assert.Contains("(non-ordinary-file)", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("external-secret", failure.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A linked prompt root is rejected before any external catalog can be read.</summary>
    [Fact]
    public async Task LoadAsync_SymbolicLinkRoot_FailsSafelyWhenSupported()
    {
        using var baseDirectory = TemporaryDirectory.Create();
        var definition = PromptAssetCatalog.Get(PromptFileNames.SystemSystemPrompt);
        using var externalCatalog = TemporaryPromptCatalog.Create(definition);
        var linkPath = Path.Combine(baseDirectory.Path, "prompts");
        try
        {
            Directory.CreateSymbolicLink(linkPath, externalCatalog.PromptRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or PlatformNotSupportedException)
        {
            Assert.Skip($"Symbolic-link creation is unavailable: {exception.GetType().Name}.");
        }

        try
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                DeployedPromptLoader.LoadAsync(
                    baseDirectory.Path,
                    [definition],
                    DeployedPromptLoadLimits.Production));
            Assert.Contains("cannot be a reparse point", failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(baseDirectory.Path, failure.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(externalCatalog.PromptRoot, failure.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(linkPath);
        }
    }

    /// <summary>Every code-owned filename is a confined flat Markdown child name.</summary>
    [Fact]
    public void CatalogNames_AreFlatAndTraversalFree()
    {
        Assert.All(PromptAssetCatalog.All, definition =>
        {
            Assert.Equal(definition.FileName, Path.GetFileName(definition.FileName));
            Assert.False(Path.IsPathRooted(definition.FileName));
            Assert.DoesNotContain("..", definition.FileName, StringComparison.Ordinal);
            Assert.EndsWith(".md", definition.FileName, StringComparison.Ordinal);
        });
    }

    private static string ComputeCatalogDigest(IReadOnlyList<LoadedPromptAssetMetadata> metadata)
    {
        var canonical = string.Join(
            "\n",
            metadata.Select(asset => $"{asset.FileName}\0{asset.Bytes}\0{asset.Digest}"));
        return ComputeDigest(Encoding.UTF8.GetBytes(canonical));
    }

    private static string ComputeDigest(ReadOnlySpan<byte> bytes)
    {
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static Dictionary<string, string> CreateRequiredValues(
        PromptAssetDefinition definition,
        string value)
    {
        return definition.RequiredTokens.ToDictionary(token => token, _ => value, StringComparer.Ordinal);
    }

    private static string ReplaceMarkersOnce(string content, IReadOnlyDictionary<string, string> values)
    {
        foreach (var value in values)
        {
            content = content.Replace("{{" + value.Key + "}}", value.Value, StringComparison.Ordinal);
        }

        return content;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"threadsmith-prompt-loader-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class TemporaryPromptCatalog : IDisposable
    {
        private readonly IReadOnlyList<PromptAssetDefinition> _definitions;
        private readonly TemporaryDirectory _directory;

        private TemporaryPromptCatalog(
            TemporaryDirectory directory,
            IReadOnlyList<PromptAssetDefinition> definitions)
        {
            _directory = directory;
            _definitions = definitions;
            PromptRoot = Path.Combine(directory.Path, "prompts");
            Directory.CreateDirectory(PromptRoot);
        }

        public string BaseDirectory => _directory.Path;

        public string PromptRoot { get; }

        public long TotalBytes => _definitions.Sum(definition =>
            new FileInfo(GetAssetPath(definition.FileName)).Length);

        public static TemporaryPromptCatalog Create(params PromptAssetDefinition[] definitions)
        {
            ArgumentNullException.ThrowIfNull(definitions);
            var catalog = new TemporaryPromptCatalog(TemporaryDirectory.Create(), [.. definitions]);
            foreach (var definition in definitions)
            {
                catalog.WriteText(definition.FileName, CreateValidContent(definition));
            }

            return catalog;
        }

        public static TemporaryPromptCatalog CreateCompleteCatalog()
        {
            return Create([.. PromptAssetCatalog.All]);
        }

        public static string CreateRequiredMarkers(PromptAssetDefinition definition)
        {
            return string.Concat(definition.RequiredTokens.Order(StringComparer.Ordinal).Select(token => "{{" + token + "}}"));
        }

        public static string CreateValidContent(PromptAssetDefinition definition)
        {
            var required = CreateRequiredMarkers(definition);
            var optional = string.Concat(
                definition.OptionalTokens.Order(StringComparer.Ordinal).Select(token => "{{" + token + "}}"));
            return $"asset:{definition.FileName}\r\n{{ordinary}}{required}{optional}";
        }

        public string GetAssetPath(string fileName)
        {
            return Path.Combine(PromptRoot, fileName);
        }

        public Task<DeployedPromptLoader> LoadAsync(
            DeployedPromptLoadLimits? limits = null,
            CancellationToken cancellationToken = default)
        {
            return DeployedPromptLoader.LoadAsync(
                BaseDirectory,
                _definitions,
                limits ?? DeployedPromptLoadLimits.Production,
                cancellationToken);
        }

        public void WriteBytes(string fileName, byte[] content)
        {
            File.WriteAllBytes(GetAssetPath(fileName), content);
        }

        public void WriteText(string fileName, string content)
        {
            WriteBytes(fileName, Utf8WithoutBom.GetBytes(content));
        }

        public void Dispose()
        {
            _directory.Dispose();
        }
    }
}
