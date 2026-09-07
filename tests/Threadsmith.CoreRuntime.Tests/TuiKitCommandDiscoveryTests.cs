namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Interaction.Commands;
using Threadsmith.Tui.TuiKit;
using TUIKit;
using TUIKit.Input;
using TUIKit.Widgets;
using Xunit;

/// <summary>Checks the pinned toolkit APIs and a single catalog feeding both discovery surfaces.</summary>
public static class TuiKitCommandDiscoveryTests
{
    /// <summary>Both views retain the shared catalog's ordering and canonical identities.</summary>
    [Fact]
    public static void SharedCatalogAppearsInOrderWithExactIdentity()
    {
        var discovery = new TuiKitCommandDiscovery(InteractiveCommandCatalog.All, _ => Assert.Fail("Discovery executed a handler."));
        var palette = discovery.BuildPalette();
        Assert.Equal(InteractiveCommandCatalog.All.Count, palette.MatchCount);
        Assert.Equal(InteractiveCommandCatalog.All.Select(entry => entry.Name), discovery.Suggest("/"));

        foreach (var descriptor in InteractiveCommandCatalog.All)
        {
            Assert.Equal(descriptor.Name, palette.SelectedItem?.Id);
            Assert.True(discovery.TryGet(descriptor.Name.ToUpperInvariant(), out var resolved));
            Assert.Same(descriptor, resolved);
            Assert.Empty(discovery.Suggest(descriptor.Name));
            Assert.Empty(discovery.Suggest(descriptor.Name.ToUpperInvariant()));
            palette.HandleKey(KeyEvent.Special(KeyCode.Down));
        }
    }

    /// <summary>New metadata becomes discoverable without changing frontend command logic.</summary>
    [Fact]
    public static void SyntheticCommandFlowsThroughBothViewsAndRequestsOnlyLocalCompletion()
    {
        var entry = new InteractiveCommandDescriptor("/fixture", "/fixture [argument]", "Unique constellation operation");
        var source = new List<InteractiveCommandDescriptor>(InteractiveCommandCatalog.All) { entry };
        string? completed = null;
        var discovery = new TuiKitCommandDiscovery(source, value => completed = value);
        source.Clear();

        var palette = discovery.BuildPalette();
        palette.Query = "cnstltn";
        var selected = Assert.IsType<Command>(palette.SelectedItem);
        Assert.Equal(entry.Name, selected.Id);
        Assert.Null(completed);
        selected.Handler();
        Assert.Equal(entry.Name, completed);
        Assert.Equal([entry.Name], discovery.Suggest("/FIX"));
        Assert.True(discovery.TryGet(entry.Name, out var resolved));
        Assert.Equal(entry.Usage, resolved?.Usage);

        palette.Query = "unmatchable query";
        Assert.Null(palette.SelectedItem);
        Assert.Equal(0, palette.MatchCount);
    }

    /// <summary>Case variants cannot represent independent identities.</summary>
    [Fact]
    public static void CatalogRejectsCaseInsensitiveDuplicateIdentities()
    {
        InteractiveCommandDescriptor[] entries = [new("/fixture", "first", "first"), new("/FIXTURE", "second", "second")];
        Assert.Throws<ArgumentException>(() => new TuiKitCommandDiscovery(entries, _ => { }));
    }

    /// <summary>Suggestion providers do not interpret prose or command arguments.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("help")]
    [InlineData(" /h")]
    [InlineData("/help arg")]
    [InlineData("/h\n")]
    [InlineData("/h\t")]
    [InlineData("/unknown-fixture")]
    public static void IneligiblePrefixesHaveNoSuggestions(string input)
    {
        var discovery = new TuiKitCommandDiscovery(InteractiveCommandCatalog.All, _ => { });
        Assert.Empty(discovery.Suggest(input));
    }

    /// <summary>Sources and consumers cannot mutate discovery state, and cancellation is observed.</summary>
    [Fact]
    public static async Task ProviderIsImmutableOrderedAndObservesCancellation()
    {
        InteractiveCommandDescriptor[] entries = [new("/zz", "zz", "z"), new("/za", "za", "a")];
        var discovery = new TuiKitCommandDiscovery(entries, _ => { });
        entries[0] = new("/other", "other", "other");

        var suggestions = await discovery.SuggestAsync("/Z", TestContext.Current.CancellationToken);
        Assert.Equal(["/zz", "/za"], suggestions);
        var collection = Assert.IsAssignableFrom<IList<string>>(suggestions);
        Assert.True(collection.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => collection[0] = "/changed");
        Assert.Equal(suggestions, await discovery.SuggestAsync("/z", TestContext.Current.CancellationToken));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => discovery.SuggestAsync("/z", cancelled.Token));
    }

    /// <summary>The pinned package renders both views and accepts a canonical name through public APIs.</summary>
    [Fact]
    public static void PublicAutocompleteAndPaletteRetainTypedSelection()
    {
        var discovery = new TuiKitCommandDiscovery(InteractiveCommandCatalog.All, _ => Assert.Fail("Rendering executed a command."));
        var overlay = new AutocompleteOverlay(discovery) { MaxRows = 6 };
        string? accepted = null;
        overlay.Accepted += value => accepted = value;
        overlay.SetInput("/rea");
        var cells = new CellBuffer(40, 12);
        overlay.RenderAt(new BufferSurface(cells), 0, 11);
        Assert.Contains("/reasoning", TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
        Assert.True(overlay.HandleKey(KeyEvent.Special(KeyCode.Tab)));
        Assert.Equal("/reasoning", accepted);
        Assert.False(overlay.IsVisible);

        var palette = discovery.BuildPalette();
        palette.Query = "reasoning effort";
        Assert.Equal(accepted, palette.SelectedItem?.Id);
        palette.Render(new BufferSurface(cells));
        Assert.Contains("/reasoning", TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
    }
}
