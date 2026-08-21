namespace Threadsmith.Extensions.Tests;

using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Extensions.Abstractions;
using Threadsmith.Extensions.Runtime;
using Xunit;

/// <summary>
/// Plan-66 acceptance: <see cref="ExtensionGeneration"/> capability views are cached live read-only
/// wrappers over the private mutable backing lists. Consumers cannot recover or mutate the backing
/// <see cref="List{T}"/>, while host-owned registration and unload clearing remain immediately
/// observable through previously captured views (Scenario AF; strategy §17.3, §17.14, §17.19).
/// </summary>
public sealed class ExtensionGenerationReadOnlyViewTests
{
    /// <summary>Asserts neither public capability view exposes the mutable backing <see cref="List{T}"/> type.</summary>
    [Fact]
    public async Task Capability_views_are_not_mutable_lists()
    {
        var generation = await LoadSampleGenerationAsync();

        Assert.False(
            generation.Tools is List<IToolCapability>,
            "Tools must not expose the backing List<T>.");
        Assert.False(
            generation.ModelPreferenceContributors is List<IModelPreferenceContributor>,
            "ModelPreferenceContributors must not expose the backing List<T>.");
        Assert.IsNotType<List<IToolCapability>>(generation.Tools);
        Assert.IsNotType<List<IModelPreferenceContributor>>(generation.ModelPreferenceContributors);
    }

    /// <summary>Asserts mutation through mutable collection interfaces is rejected with <see cref="NotSupportedException"/>.</summary>
    [Fact]
    public async Task Capability_views_reject_mutation_through_collection_interfaces()
    {
        var generation = await LoadSampleGenerationAsync();
        var tool = new ConfigurableToolCapability("view_reject_tool", "view_reject_tool");
        var contributor = new StubModelPreferenceContributor("view_reject_contributor");

        Assert.Throws<NotSupportedException>(
            () => ((ICollection<IToolCapability>)generation.Tools).Add(tool));
        Assert.Throws<NotSupportedException>(
            () => ((ICollection<IToolCapability>)generation.Tools).Remove(tool));
        Assert.Throws<NotSupportedException>(
            () => ((ICollection<IToolCapability>)generation.Tools).Clear());
        Assert.Throws<NotSupportedException>(
            () => ((ICollection<IModelPreferenceContributor>)generation.ModelPreferenceContributors).Add(contributor));
        Assert.Throws<NotSupportedException>(
            () => ((ICollection<IModelPreferenceContributor>)generation.ModelPreferenceContributors).Remove(contributor));
        Assert.Throws<NotSupportedException>(
            () => ((ICollection<IModelPreferenceContributor>)generation.ModelPreferenceContributors).Clear());

        // The backing store is unchanged after rejected mutations.
        Assert.Single(generation.Tools);
        Assert.Single(generation.ModelPreferenceContributors);
    }

    /// <summary>Asserts repeated property access returns the same cached wrapper instance.</summary>
    [Fact]
    public async Task Repeated_access_returns_the_same_cached_wrapper()
    {
        var generation = await LoadSampleGenerationAsync();

        var firstTools = generation.Tools;
        var secondTools = generation.Tools;
        var firstContributors = generation.ModelPreferenceContributors;
        var secondContributors = generation.ModelPreferenceContributors;

        Assert.Same(firstTools, secondTools);
        Assert.Same(firstContributors, secondContributors);
        Assert.IsType<ReadOnlyCollection<IToolCapability>>(firstTools);
        Assert.IsType<ReadOnlyCollection<IModelPreferenceContributor>>(firstContributors);
    }

    /// <summary>Asserts a previously captured tool view observes later host-owned registration in order.</summary>
    [Fact]
    public async Task Captured_tool_view_observes_later_host_owned_registration_in_order()
    {
        var generation = await LoadSampleGenerationAsync();
        var view = generation.Tools;
        var firstId = view[0].Definition.Id;
        var second = new ConfigurableToolCapability("view_added_tool", "view_added_tool");
        var third = new ConfigurableToolCapability("view_added_tool_2", "view_added_tool_2");

        generation.AddTool(second);
        generation.AddTool(third);

        Assert.Equal(3, view.Count);
        Assert.Equal(firstId, view[0].Definition.Id);
        Assert.Same(second, view[1]);
        Assert.Same(third, view[2]);
        // The public property still returns the same cached wrapper instance.
        Assert.Same(view, generation.Tools);
    }

    /// <summary>Asserts a previously captured contributor view observes later host-owned registration in order.</summary>
    [Fact]
    public async Task Captured_contributor_view_observes_later_host_owned_registration_in_order()
    {
        var generation = await LoadSampleGenerationAsync();
        var view = generation.ModelPreferenceContributors;
        var firstId = view[0].Descriptor.Id;
        var second = new StubModelPreferenceContributor("view_added_contributor");
        var third = new StubModelPreferenceContributor("view_added_contributor_2");

        generation.AddModelPreferenceContributor(second);
        generation.AddModelPreferenceContributor(third);

        Assert.Equal(3, view.Count);
        Assert.Equal(firstId, view[0].Descriptor.Id);
        Assert.Same(second, view[1]);
        Assert.Same(third, view[2]);
        Assert.Same(view, generation.ModelPreferenceContributors);
    }

    /// <summary>Asserts <see cref="ExtensionGeneration.ClearCapabilities"/> empties already captured views.</summary>
    [Fact]
    public async Task ClearCapabilities_empties_already_captured_views()
    {
        var generation = await LoadSampleGenerationAsync();
        var toolsView = generation.Tools;
        var contributorsView = generation.ModelPreferenceContributors;
        Assert.NotEmpty(toolsView);
        Assert.NotEmpty(contributorsView);

        generation.ClearCapabilities();

        Assert.Empty(toolsView);
        Assert.Empty(contributorsView);
        Assert.Empty(generation.Tools);
        Assert.Empty(generation.ModelPreferenceContributors);
        // The wrapper instances are stable after clearing; no snapshot was substituted.
        Assert.Same(toolsView, generation.Tools);
        Assert.Same(contributorsView, generation.ModelPreferenceContributors);
    }

    private static async Task<ExtensionGeneration> LoadSampleGenerationAsync()
    {
        var stagingRoot = Path.Combine(
            Path.GetTempPath(),
            "threadsmith-m7-view-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingRoot);
        var events = new RecordingEventStream();
        var host = new ExtensionHost(events, NullLogger<ExtensionHost>.Instance);
        return await host.LoadAsync(new ExtensionLoadRequest
        {
            ExtensionDirectory = ResolveSampleOutputDirectory(),
            EffectiveTrust = RepositoryTrustLevel.TrustedRead,
            SessionId = SessionId.New(),
            ShadowStagingRoot = Path.Combine(stagingRoot, "sample"),
        });
    }

    private static string ResolveSampleOutputDirectory()
    {
        var bin = AppContext.BaseDirectory;
        var repoRoot = Path.GetFullPath(Path.Combine(bin, "..", "..", "..", "..", ".."));
        var configuration = bin.Contains("Debug", StringComparison.Ordinal) ? "Debug" : "Release";
        return Path.Combine(repoRoot, "samples", "extensions", "MinimalToolExtension", "bin", configuration, "net10.0");
    }
}

/// <summary>A minimal <see cref="IModelPreferenceContributor"/> stub for read-only view tests.</summary>
internal sealed class StubModelPreferenceContributor : IModelPreferenceContributor
{
    /// <summary>Initializes a new instance of the <see cref="StubModelPreferenceContributor"/> class.</summary>
    public StubModelPreferenceContributor(string id)
    {
        Descriptor = new CapabilityDescriptor
        {
            Id = id,
            Kind = CapabilityKind.ModelPreference,
            DisplayName = id,
        };
    }

    /// <inheritdoc />
    public CapabilityDescriptor Descriptor { get; }

    /// <inheritdoc />
    public Task<IReadOnlyList<ExtensionModelPreferenceHint>> GetHintsAsync(
        string workloadName,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<ExtensionModelPreferenceHint>>([]);
    }
}