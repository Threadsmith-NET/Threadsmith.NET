namespace Threadsmith.Interaction.Themes;

using System.Text.Json;
using Threadsmith.Interaction.Commands;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Presentation;

/// <summary>Handles the fixed presentation-local theme command.</summary>
internal sealed class ThemeCommandContribution : IFrontendCommandContribution
{
    private const string CancelOptionId = "threadsmith:theme:cancel";

    private readonly Func<ConfiguredTheme, CancellationToken, Task> _applyTheme;
    private readonly IThemePreferenceStore? _preferenceStore;
    private readonly SessionThemePreferences _preferences;

    /// <summary>Initializes a new instance of the <see cref="ThemeCommandContribution" /> class.</summary>
    internal ThemeCommandContribution(
        SessionThemePreferences preferences,
        Func<ConfiguredTheme, CancellationToken, Task> applyTheme,
        IThemePreferenceStore? preferenceStore = null)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(applyTheme);
        _preferences = preferences;
        _applyTheme = applyTheme;
        _preferenceStore = preferenceStore;
    }

    /// <inheritdoc />
    public async Task<FrontendCommandOutcome> HandleAsync(
        InteractiveCommandInvocation invocation,
        IInteractionSurface surface,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(surface);
        if (!string.Equals(invocation.Descriptor.Name, "/theme", StringComparison.OrdinalIgnoreCase))
        {
            return FrontendCommandOutcome.NotHandled;
        }

        var selectedId = invocation.Argument;
        if (string.Equals(selectedId, "current", StringComparison.OrdinalIgnoreCase))
        {
            var current = _preferences.ActiveTheme;
            await WriteAsync(
                surface,
                $"Current theme: {current.Theme.Id} ({current.Name}).{Environment.NewLine}",
                PresentationTextRole.Status,
                cancellationToken);
            return FrontendCommandOutcome.Handled;
        }

        if (string.IsNullOrWhiteSpace(selectedId))
        {
            var active = _preferences.ActiveTheme;
            var options = _preferences.Catalog.Themes
                .Select(theme => new InteractionSelectionOption(
                    theme.Theme.Id,
                    $"{theme.Name} ({theme.Theme.Id}){(ReferenceEquals(theme, active) ? " [active]" : string.Empty)}"))
                .Append(new InteractionSelectionOption(CancelOptionId, "Cancel"))
                .ToArray();
            var selection = await surface.SelectAsync(
                new InteractionSelectionRequest("Theme (Up/Down, Enter):", options),
                cancellationToken);
            if (selection.IsCancelled
                || selection.SelectedOptionId is null
                || string.Equals(selection.SelectedOptionId, CancelOptionId, StringComparison.Ordinal))
            {
                await WriteAsync(surface, "Theme unchanged.\n", PresentationTextRole.Status, cancellationToken);
                return FrontendCommandOutcome.Handled;
            }

            selectedId = selection.SelectedOptionId;
        }

        var safeId = selectedId.Length <= 40
            && selectedId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
        if (!safeId
            || !_preferences.Catalog.TryGet(selectedId, out var selectedTheme)
            || selectedTheme is null)
        {
            var displayId = safeId ? $": {selectedId}" : " id";
            await WriteAsync(
                surface,
                $"Unknown theme{displayId}. Enter /theme to list themes.{Environment.NewLine}",
                PresentationTextRole.Error,
                cancellationToken);
            return FrontendCommandOutcome.Handled;
        }

        if (_preferenceStore is not null)
        {
            try
            {
                await _preferenceStore.SetDefaultThemeAsync(selectedTheme.Theme.Id, cancellationToken);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
            {
                await WriteAsync(
                    surface,
                    $"Theme could not be saved; selection unchanged.{Environment.NewLine}",
                    PresentationTextRole.Error,
                    cancellationToken);
                return FrontendCommandOutcome.Handled;
            }
        }

        _ = _preferences.TrySelect(selectedTheme.Theme.Id, out _);
        await _applyTheme(selectedTheme, cancellationToken);
        var persistence = _preferenceStore is null ? string.Empty : " and saved as the default";
        await WriteAsync(
            surface,
            $"Theme changed to {selectedTheme.Name} ({selectedTheme.Theme.Id}){persistence}.{Environment.NewLine}",
            PresentationTextRole.Status,
            cancellationToken);
        return FrontendCommandOutcome.Handled;
    }

    private static Task WriteAsync(
        IInteractionSurface surface,
        string text,
        PresentationTextRole role,
        CancellationToken cancellationToken)
    {
        return surface.PresentAsync(
            new PresentationBatch([new PresentationTextItem([new PresentationTextSegment(text, role)])]),
            cancellationToken);
    }
}
