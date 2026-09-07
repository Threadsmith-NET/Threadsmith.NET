namespace Threadsmith.Tui.TuiKit;

using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Threadsmith.Interaction.Commands;
using TUIKit.Input;
using TUIKit.Widgets;

/// <summary>Projects the shared command metadata into the two retained discovery views.</summary>
internal sealed class TuiKitCommandDiscovery : ISuggestionProvider
{
    /// <summary>Bounds fuzzy search and its offscreen label rendering in UTF-16 units.</summary>
    internal const int MaximumTitleLength = 512;

    private readonly InteractiveCommandDescriptor[] _entries;
    private readonly FrozenDictionary<string, InteractiveCommandDescriptor> _byName;
    private readonly CommandRegistry _registry = new();

    /// <summary>Initializes a new instance of the <see cref="TuiKitCommandDiscovery"/> class.</summary>
    internal TuiKitCommandDiscovery(IEnumerable<InteractiveCommandDescriptor> entries, Action<string> complete)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(complete);
        _entries = [.. entries];
        _byName = _entries.ToFrozenDictionary(entry => entry.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _entries)
        {
            _registry.Add(new Command(
                entry.Name,
                PaletteTitle(entry),
                () => complete(entry.Name),
                category: "Threadsmith"));
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Suggest(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.StartsWith('/') || input.Any(char.IsWhiteSpace) || _byName.ContainsKey(input))
        {
            return Array.Empty<string>();
        }

        return Array.AsReadOnly(_entries
            .Where(entry => entry.Name.StartsWith(input, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Name)
            .ToArray());
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> SuggestAsync(string input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Suggest(input));
    }

    /// <summary>Creates a transient fuzzy view without exposing the mutable registry.</summary>
    internal FuzzyList<Command> BuildPalette()
    {
        return _registry.BuildPalette();
    }

    /// <summary>Resolves accepted identity without inspecting a display label or parsing arguments.</summary>
    internal bool TryGet(string name, [NotNullWhen(true)] out InteractiveCommandDescriptor? descriptor)
    {
        return _byName.TryGetValue(name, out descriptor);
    }

    private static string PaletteTitle(InteractiveCommandDescriptor entry)
    {
        var text = TranscriptView.Safe(entry.Name + " - " + entry.Description).ReplaceLineEndings(" ");
        var title = new StringBuilder();
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            if (title.Length + element.Length > MaximumTitleLength)
            {
                break;
            }

            title.Append(element);
        }

        return title.ToString();
    }
}
