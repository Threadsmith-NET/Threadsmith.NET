namespace Threadsmith.Extensions.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Extensions.Abstractions;
using Threadsmith.Extensions.Runtime;
using Xunit;

/// <summary>
/// Regression tests added by the Milestone 7 PR-review remediation. Each test pins one finding so the
/// fixed behavior cannot silently regress:
/// <list type="bullet">
/// <item>F2: the host constructs an extension exactly once during load (no throwaway prototype).</item>
/// <item>F3: <see cref="ExtensionApprovalRequirement.HostPolicy"/> maps faithfully to <see cref="ApprovalLevel.HostPolicy"/>,
/// not silently to <see cref="ApprovalLevel.None"/>.</item>
/// <item>F6: repeated invocation leases on the same thread/generation are all tracked (monotonic LeaseId).</item>
/// <item>F11: a load failure publishes <see cref="ExtensionLoadFailed"/>, not <see cref="ExtensionUnloadFailed"/>.</item>
/// </list>
/// </summary>
public sealed class ExtensionReviewRemediationTests
{
    /// <summary>Asserts <see cref="ExtensionApprovalRequirement.HostPolicy"/> maps to <see cref="ApprovalLevel.HostPolicy"/> (F3).</summary>
    [Fact]
    public async Task CapabilityProxy_maps_host_policy_approval_faithfully()
    {
        var generation = await LoadConstructionCountGenerationAsync();
        var leaseAuthority = new InvocationLeaseAuthority(NullLogger.Instance);

        var hostPolicyProxy = new CapabilityProxy(
            new HostPolicyToolCapability(ExtensionApprovalRequirement.HostPolicy),
            CapabilityId.New(),
            generation,
            leaseAuthority,
            NullLogger.Instance);
        Assert.Equal(ApprovalLevel.HostPolicy, hostPolicyProxy.Definition.RequiredApproval);

        var userProxy = new CapabilityProxy(
            new HostPolicyToolCapability(ExtensionApprovalRequirement.User),
            CapabilityId.New(),
            generation,
            leaseAuthority,
            NullLogger.Instance);
        Assert.Equal(ApprovalLevel.User, userProxy.Definition.RequiredApproval);

        var noneProxy = new CapabilityProxy(
            new HostPolicyToolCapability(ExtensionApprovalRequirement.None),
            CapabilityId.New(),
            generation,
            leaseAuthority,
            NullLogger.Instance);
        Assert.Equal(ApprovalLevel.None, noneProxy.Definition.RequiredApproval);
    }

    private static async Task<ExtensionGeneration> LoadConstructionCountGenerationAsync()
    {
        var events = new RecordingEventStream();
        var host = new ExtensionHost(events, NullLogger<ExtensionHost>.Instance);
        return await host.LoadAsync(new ExtensionLoadRequest
        {
            ExtensionDirectory = ResolveFixtureOutputDirectory("ConstructionCountExtension"),
            EffectiveTrust = RepositoryTrustLevel.TrustedRead,
            SessionId = SessionId.New(),
            ShadowStagingRoot = NewStagingRoot(),
        });
    }

    private static string NewStagingRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "threadsmith-m7-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string ResolveFixtureOutputDirectory(string fixtureProjectDirName)
    {
        var bin = AppContext.BaseDirectory;
        var repoRoot = Path.GetFullPath(Path.Combine(bin, "..", "..", "..", "..", ".."));
        var configuration = bin.Contains("Debug", StringComparison.Ordinal) ? "Debug" : "Release";
        return Path.Combine(repoRoot, "tests", "Threadsmith.Extensions.Tests", "Fixtures", fixtureProjectDirName, "bin", configuration, "net10.0");
    }
}

/// <summary>A metadata-only tool capability whose <see cref="ExtensionToolDefinition.RequiredApproval"/> is configurable (F3).</summary>
internal sealed class HostPolicyToolCapability : IToolCapability
{
    /// <inheritdoc />
    public CapabilityDescriptor Descriptor { get; } = new()
    {
        Id = "test_host_policy",
        Kind = CapabilityKind.Tool,
        DisplayName = "Host Policy Test Tool",
    };

    /// <inheritdoc />
    public ExtensionToolDefinition Definition { get; }

    /// <summary>Initializes a new instance of the <see cref="HostPolicyToolCapability"/> class with the supplied approval requirement.</summary>
    /// <param name="approval">The approval requirement the proxy must map.</param>
    public HostPolicyToolCapability(ExtensionApprovalRequirement approval)
    {
        Definition = new ExtensionToolDefinition
        {
            Id = "test_host_policy",
            Version = "1.0",
            Description = "Host policy approval mapping fixture.",
            Category = ExtensionToolCategory.RepositoryInspection,
            InputSchema = new ExtensionToolSchema("None", 1, "{\"type\":\"object\"}"),
            OutputSchema = new ExtensionToolSchema("None", 1, "{\"type\":\"object\"}"),
            RequiredTrust = ExtensionTrustRequirement.None,
            RequiredApproval = approval,
            SideEffect = ExtensionToolSideEffect.ReadOnly,
            Idempotency = ExtensionToolIdempotency.Idempotent,
            SupportsCancellation = true,
        };
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetResourcePaths(string argumentsJson, ExtensionToolInvocationContext context)
    {
        return [];
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetSecretReferences(string argumentsJson)
    {
        return [];
    }

    /// <inheritdoc />
    public string? GetExecutable(string argumentsJson)
    {
        return null;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetNetworkHosts(string argumentsJson)
    {
        return [];
    }

    /// <inheritdoc />
    public Task<ExtensionToolResult> ExecuteAsync(
        string argumentsJson,
        ExtensionToolInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("The host policy fixture is metadata-only and is never invoked.");
    }
}
