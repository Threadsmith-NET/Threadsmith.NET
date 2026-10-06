namespace Threadsmith.Interaction.Presentation;

/// <summary>Loads the application-owned tips used only by local presentation.</summary>
internal static class TipsCatalog
{
    private static readonly Lazy<IReadOnlyList<string>> Loaded = new(() =>
        Array.AsReadOnly(File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "tips.txt"))
            .Where(line => !string.IsNullOrWhiteSpace(line)).ToArray()));

    /// <summary>Gets every nonempty line from the deployed tips file.</summary>
    internal static IReadOnlyList<string> All => Loaded.Value;

    /// <summary>Formats tips for local output without conversation admission.</summary>
    internal static string FormatBullets() => string.Concat(All.Select(tip => $"- {tip}\n"));
}
