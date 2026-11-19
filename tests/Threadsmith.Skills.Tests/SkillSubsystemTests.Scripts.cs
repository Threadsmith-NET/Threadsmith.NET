namespace Threadsmith.Skills.Tests;

using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public sealed partial class SkillSubsystemTests
{
    /// <summary>Discovery validates multiple script declarations without reading source.</summary>
    [Fact]
    public static async Task Scripts_DiscoveryAcceptsMultipleDeclarationsWithoutReadingBodies()
    {
        using var package = TemporaryPackage.CopyMaintained("fix-analyzer-warnings");
        AddScriptAsset(package, "scripts/first.csx", Encoding.UTF8.GetBytes("1 + 2"));
        AddScriptAsset(package, "scripts/second.csx", Encoding.UTF8.GetBytes("3 + 4"));
        Directory.Delete(Path.Combine(package.PackageRoot, "scripts"), recursive: true);
        var snapshot = await package.CreateCatalog(SkillScope.User).RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, Assert.Single(snapshot.Candidates).Metadata.Assets.Count(asset => asset.Kind == "script"));
    }

    /// <summary>Only flat native .csx resources are admitted.</summary>
    [Theory]
    [InlineData("scripts/helper.cs", "script")]
    [InlineData("scripts/helper.ps1", "script")]
    [InlineData("scripts/helper.py", "script")]
    [InlineData("scripts/nested/helper.csx", "script")]
    [InlineData("helper.csx", "script")]
    [InlineData("references/helper.csx", "reference")]
    [InlineData("scripts/helper.csx", "reference")]
    [InlineData("scripts/.hidden.csx", "script")]
    [InlineData("scripts/helper name.csx", "script")]
    public static async Task Scripts_DiscoveryRejectsUnsupportedMetadata(string path, string kind)
    {
        using var package = TemporaryPackage.CopyMaintained("fix-analyzer-warnings");
        AddScriptAsset(package, path, Encoding.UTF8.GetBytes("1"), kind);
        await Assert.ThrowsAsync<InvalidDataException>(() => package.CreateCatalog(SkillScope.User).RefreshAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>Script names cannot collide on case-insensitive filesystems.</summary>
    [Fact]
    public static async Task Scripts_DiscoveryRejectsCaseInsensitiveDuplicateNames()
    {
        using var package = TemporaryPackage.CopyMaintained("fix-analyzer-warnings");
        AddScriptAsset(package, "scripts/first.csx", Encoding.UTF8.GetBytes("1"));
        AddScriptAsset(package, "scripts/FIRST.csx", Encoding.UTF8.GetBytes("1"));
        await Assert.ThrowsAsync<InvalidDataException>(() => package.CreateCatalog(SkillScope.User).RefreshAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>Staging preserves source bytes and does not rewrite session copies.</summary>
    [Fact]
    public static async Task Scripts_ExternalCopiesPreserveBytesAndReuseWithoutOverwrite()
    {
        using var package = TemporaryPackage.CopyMaintained("fix-analyzer-warnings");
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("// café\r\n1 + 2  \r\n")).ToArray();
        var asset = AddScriptAsset(package, "scripts/helper.csx", bytes);
        var context = ScriptContext(package);
        var plan = PermissionPlan() with { ScriptAssets = [new(package.PackageRoot, asset)] };
        var token = TestContext.Current.CancellationToken;

        var first = Assert.Single(await SkillScriptAssets.PrepareAsync(plan, context, token));
        var destination = Path.GetFullPath(first.ScriptPath, context.RepositoryPath);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(destination, token));
        File.SetAttributes(destination, FileAttributes.ReadOnly);
        var repeated = plan with { Request = plan.Request with { InvocationId = SkillInvocationId.New() } };
        var second = Assert.Single(await SkillScriptAssets.PrepareAsync(repeated, context, token));

        Assert.Equal(first, second);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(destination, token));
        Assert.Equal(FileAttributes.ReadOnly, File.GetAttributes(destination) & FileAttributes.ReadOnly);
        Assert.StartsWith(".threadsmith/.scratchpad/skill-scripts/", first.ScriptPath, StringComparison.Ordinal);
        Assert.Equal("helper.csx", first.Name);
    }

    /// <summary>Concurrent callers share copies without package or session collisions.</summary>
    [Fact]
    public static async Task Scripts_IsolatePackagesAndSessionsAndAllowConcurrentPreparation()
    {
        using var package = TemporaryPackage.CopyMaintained("fix-analyzer-warnings");
        var asset = AddScriptAsset(package, "scripts/helper.csx", Encoding.UTF8.GetBytes("1"));
        var context = ScriptContext(package);
        var plan = PermissionPlan() with { ScriptAssets = [new(package.PackageRoot, asset)] };
        var token = TestContext.Current.CancellationToken;
        var concurrent = await Task.WhenAll(
            SkillScriptAssets.PrepareAsync(plan, context, token),
            SkillScriptAssets.PrepareAsync(plan, context, token));
        Assert.Equal(Assert.Single(concurrent[0]), Assert.Single(concurrent[1]));
        var otherPackage = plan with { Package = plan.Package with { Digest = new("sha256", new string('b', 64)) } };
        var otherSession = plan with { Request = plan.Request with { SessionId = SessionId.New() } };
        var maps = new[]
        {
            Assert.Single(concurrent[0]),
            Assert.Single(await SkillScriptAssets.PrepareAsync(otherPackage, context, token)),
            Assert.Single(await SkillScriptAssets.PrepareAsync(otherSession, context, token)),
        };
        Assert.Equal(3, maps.Select(map => map.ScriptPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>Reuse rejects tampering while missing transient files can be recreated.</summary>
    [Fact]
    public static async Task Scripts_TamperedCopyFailsWithoutOverwriteAndMissingCopyIsRecreated()
    {
        using var package = TemporaryPackage.CopyMaintained("fix-analyzer-warnings");
        var asset = AddScriptAsset(package, "scripts/helper.csx", Encoding.UTF8.GetBytes("1"));
        var context = ScriptContext(package);
        var plan = PermissionPlan() with { ScriptAssets = [new(package.PackageRoot, asset)] };
        var token = TestContext.Current.CancellationToken;
        var map = Assert.Single(await SkillScriptAssets.PrepareAsync(plan, context, token));
        var destination = Path.GetFullPath(map.ScriptPath, context.RepositoryPath);
        await File.WriteAllTextAsync(destination, "2", token);
        await Assert.ThrowsAsync<InvalidDataException>(() => SkillScriptAssets.PrepareAsync(plan, context, token));
        Assert.Equal("2", await File.ReadAllTextAsync(destination, token));
        File.Delete(destination);
        Assert.Equal(map, Assert.Single(await SkillScriptAssets.PrepareAsync(plan, context, token)));
        Assert.Equal("1", await File.ReadAllTextAsync(destination, token));
    }

    /// <summary>Integrity and UTF-8 checks run before publication.</summary>
    [Theory]
    [InlineData("length")]
    [InlineData("hash")]
    [InlineData("utf8")]
    public static async Task Scripts_RejectChangedOrInvalidSourceBeforePublishing(string scenario)
    {
        using var package = TemporaryPackage.CopyMaintained("fix-analyzer-warnings");
        var bytes = Encoding.UTF8.GetBytes("1");
        if (scenario == "utf8")
        {
            bytes = [0xFF];
        }

        var asset = AddScriptAsset(package, "scripts/helper.csx", bytes);
        if (scenario != "utf8")
        {
            await File.WriteAllTextAsync(Path.Combine(package.PackageRoot, asset.Path), scenario == "hash" ? "2" : "22", TestContext.Current.CancellationToken);
        }

        var context = ScriptContext(package);
        var plan = PermissionPlan() with { ScriptAssets = [new(package.PackageRoot, asset)] };
        if (scenario == "utf8")
        {
            await Assert.ThrowsAsync<DecoderFallbackException>(() => SkillScriptAssets.PrepareAsync(plan, context, TestContext.Current.CancellationToken));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => SkillScriptAssets.PrepareAsync(plan, context, TestContext.Current.CancellationToken));
        }

        Assert.False(Directory.Exists(context.Scratchpad.RootPath));
    }

    /// <summary>External sources cannot bypass scratchpad or repository source confinement.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task Scripts_ExternalStagingRequiresInRepositoryScratchpad(bool externalScratchpad)
    {
        using var package = TemporaryPackage.CopyMaintained("fix-analyzer-warnings");
        var asset = AddScriptAsset(package, "scripts/helper.csx", Encoding.UTF8.GetBytes("1"));
        var context = ScriptContext(package);
        context = context with
        {
            Scratchpad = externalScratchpad
                ? context.Scratchpad with { RootPath = Path.Combine(package.Root, "outside-scratchpad") }
                : ScratchpadSessionCapability.Disabled(ScratchpadDisabledReason.NotConfigured),
        };
        var plan = PermissionPlan() with { ScriptAssets = [new(package.PackageRoot, asset)] };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => SkillScriptAssets.PrepareAsync(plan, context, TestContext.Current.CancellationToken));
    }

    /// <summary>Inherited exclusions and cancellation prevent scratchpad publication.</summary>
    [Fact]
    public static async Task Scripts_ProhibitionsAndCancellationPreventStaging()
    {
        using var package = TemporaryPackage.CopyMaintained("fix-analyzer-warnings");
        var asset = AddScriptAsset(package, "scripts/helper.csx", Encoding.UTF8.GetBytes("1"));
        var context = ScriptContext(package);
        var plan = PermissionPlan() with { ScriptAssets = [new(package.PackageRoot, asset)] };
        var prohibited = context with { ProhibitedPaths = ["**/helper.csx"] };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => SkillScriptAssets.PrepareAsync(plan, prohibited, TestContext.Current.CancellationToken));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SkillScriptAssets.PrepareAsync(plan, context, cancellation.Token));
        Assert.False(Directory.Exists(context.Scratchpad.RootPath));
    }

    /// <summary>Repository packages keep their ordinary repository-relative source paths.</summary>
    [Fact]
    public static async Task Scripts_RepositorySourceUsesRelativePathWithoutScratchpad()
    {
        using var package = TemporaryPackage.CopyMaintained("fix-analyzer-warnings");
        var asset = AddScriptAsset(package, "scripts/helper.csx", Encoding.UTF8.GetBytes("1"));
        var context = ScriptContext(package) with
        {
            RepositoryPath = package.Root,
            ApprovedRoots = [package.Root],
            Scratchpad = ScratchpadSessionCapability.Disabled(ScratchpadDisabledReason.NotConfigured),
        };
        var plan = PermissionPlan() with { ScriptAssets = [new(package.PackageRoot, asset)] };
        var map = Assert.Single(await SkillScriptAssets.PrepareAsync(plan, context, TestContext.Current.CancellationToken));
        Assert.Equal("fix-analyzer-warnings/scripts/helper.csx", map.ScriptPath);
        Assert.False(Directory.Exists(Path.Combine(package.Root, ".threadsmith")));
    }

    /// <summary>Successful preparation shares paths without rereads, while authority and invocation boundaries remain live.</summary>
    [Fact]
    public static async Task Scripts_WorkflowCacheReusesMappingButRechecksAuthorityAndNewInvocations()
    {
        using var package = TemporaryPackage.CopyMaintained("fix-analyzer-warnings");
        var asset = AddScriptAsset(package, "scripts/helper.csx", Encoding.UTF8.GetBytes("1"));
        var context = ScriptContext(package);
        var plan = PermissionPlan() with { ScriptAssets = [new(package.PackageRoot, asset)] };
        var token = TestContext.Current.CancellationToken;
        var first = await SkillScriptAssets.PrepareForWorkflowAsync(plan, context, token);
        var destination = Path.GetFullPath(Assert.Single(first).ScriptPath, context.RepositoryPath);
        await using (var locked = new FileStream(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var concurrent = await Task.WhenAll(
                SkillScriptAssets.PrepareForWorkflowAsync(plan with { EffectiveBudget = new() }, context, token),
                SkillScriptAssets.PrepareForWorkflowAsync(plan, context, token));
            Assert.All(concurrent, paths => Assert.Same(first, paths));
        }

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => SkillScriptAssets.PrepareForWorkflowAsync(
            plan, context with { ProhibitedPaths = ["**/helper.csx"] }, token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => SkillScriptAssets.PrepareForWorkflowAsync(
            plan with { Package = plan.Package with { Digest = new("sha256", new string('b', 64)) } }, context, token));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SkillScriptAssets.PrepareForWorkflowAsync(plan, context, cancelled.Token));
        await File.WriteAllTextAsync(destination, "2", token);
        await Assert.ThrowsAsync<InvalidDataException>(() => SkillScriptAssets.PrepareForWorkflowAsync(
            plan with { Request = plan.Request with { InvocationId = SkillInvocationId.New() } }, context, token));
    }

    /// <summary>Cached paths never permit changed package bytes to reach the existing script engine.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task Scripts_CachedMappingChecksIntegrityOnActualExecutionSnapshot(bool repositorySource)
    {
        using var package = TemporaryPackage.CopyMaintained("fix-analyzer-warnings");
        var asset = AddScriptAsset(package, "scripts/helper.csx", Encoding.UTF8.GetBytes("1"));
        var context = ScriptContext(package);
        if (repositorySource)
        {
            context = context with { RepositoryPath = package.Root, ApprovedRoots = [package.Root] };
        }

        var plan = PermissionPlan() with { ScriptAssets = [new(package.PackageRoot, asset)] };
        var token = TestContext.Current.CancellationToken;
        var paths = await SkillScriptAssets.PrepareForWorkflowAsync(plan, context, token);
        var selected = Assert.Single(paths);
        var destination = Path.GetFullPath(selected.ScriptPath, context.RepositoryPath);
        await File.WriteAllTextAsync(destination, "2", token);
        Assert.Same(paths, await SkillScriptAssets.PrepareForWorkflowAsync(plan, context, token));
        context = context with { ScriptSourceIntegrity = SkillScriptAssets.BindIntegrity(plan, paths, context) };
        var tool = new CSharpScriptTool(new RejectScriptEngine(), TestPromptLoader.Instance);
        var execution = new ToolExecutionContext(ToolInvocationId.New(), plan.Request.SessionId, plan.Request.RunId, context);

        var failure = await Assert.ThrowsAsync<ToolArgumentValidationException>(() => tool.ExecuteAsync(new() { ScriptPath = selected.ScriptPath }, execution, token));

        Assert.Contains("package integrity", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Aggregate admission occurs before source I/O or scratchpad publication.</summary>
    [Fact]
    public static async Task Scripts_AggregateLimitRejectsBeforeAnyReadOrPublication()
    {
        using var package = TemporaryPackage.CopyMaintained("fix-analyzer-warnings");
        var asset = AddScriptAsset(package, "scripts/helper.csx", Encoding.UTF8.GetBytes("1"));
        Directory.Delete(Path.Combine(package.PackageRoot, "scripts"), recursive: true);
        var context = ScriptContext(package);
        var plan = PermissionPlan() with
        {
            ScriptAssets = [new(package.PackageRoot, asset with { Bytes = SkillScriptAssets.MaximumPreparationBytes }), new(package.PackageRoot, asset)],
        };

        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => SkillScriptAssets.PrepareAsync(plan, context, TestContext.Current.CancellationToken));

        Assert.Contains("aggregate", failure.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(context.Scratchpad.RootPath));
    }

    /// <summary>Effective limits count decoded characters and exclude BOM bytes from the saved source limit.</summary>
    [Theory]
    [InlineData("1", 1, 1, true)]
    [InlineData("12", 1, 2, false)]
    [InlineData("é", 1, 2, true)]
    [InlineData("é", 1, 1, false)]
    public static async Task Scripts_EffectiveLimitsRespectUtf8AndBom(string source, int characters, int bytes, bool accepted)
    {
        using var package = TemporaryPackage.CopyMaintained("fix-analyzer-warnings");
        var content = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(source)).ToArray();
        var asset = AddScriptAsset(package, "scripts/helper.csx", content);
        var context = ScriptContext(package);
        context = context with
        {
            SecurityPolicy = new EffectiveSessionPolicy
            {
                BindingId = Guid.NewGuid(),
                Generation = 1,
                RepositoryPath = context.RepositoryPath,
                Fingerprint = "test",
                ResourceLimits = new Dictionary<string, int>
                {
                    ["tools:config:csharp_script:max_code_characters"] = characters,
                    ["tools:writeFile:maxContentBytes"] = bytes,
                }.ToImmutableDictionary(),
            },
        };
        var plan = PermissionPlan() with { ScriptAssets = [new(package.PackageRoot, asset)] };
        if (accepted)
        {
            Assert.Single(await SkillScriptAssets.PrepareAsync(plan, context, TestContext.Current.CancellationToken));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => SkillScriptAssets.PrepareAsync(plan, context, TestContext.Current.CancellationToken));
            Assert.False(Directory.Exists(context.Scratchpad.RootPath));
        }
    }

    private sealed class RejectScriptEngine : ICSharpScriptEngine
    {
        public Task<CSharpScriptOutput> ExecuteAsync(string code, ScriptKind kind, ToolExecutionContext context, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Changed packaged source must not reach the engine.");
    }

    private static ToolInvocationContext ScriptContext(TemporaryPackage package)
    {
        var repository = Path.Combine(package.Root, "repository");
        Directory.CreateDirectory(repository);
        return PermissionContext() with
        {
            RepositoryPath = repository,
            ApprovedRoots = [repository],
            Scratchpad = new ScratchpadSessionCapability
            {
                IsActive = true,
                RootPath = Path.Combine(repository, ".threadsmith", ".scratchpad"),
                IsInsideRepository = true,
                RepositoryIdentity = repository,
                Generation = 1,
            },
        };
    }

    private static SkillAssetMetadata AddScriptAsset(TemporaryPackage package, string path, byte[] bytes, string kind = "script")
    {
        var asset = new SkillAssetMetadata
        {
            Path = path,
            Bytes = bytes.Length,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            Kind = kind,
        };
        var destination = Path.Combine(package.PackageRoot, path);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllBytes(destination, bytes);
        var manifestPath = Path.Combine(package.PackageRoot, "skill.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        manifest["assets"]!.AsArray().Add(new JsonObject
        {
            ["path"] = asset.Path,
            ["bytes"] = asset.Bytes,
            ["sha256"] = asset.Sha256,
            ["required"] = asset.Required,
            ["kind"] = asset.Kind,
        });
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        return asset;
    }
}
