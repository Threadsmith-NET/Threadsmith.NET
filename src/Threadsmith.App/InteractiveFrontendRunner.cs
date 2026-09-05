namespace Threadsmith.App;

using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Coordination;
using Threadsmith.Tui;
using Threadsmith.Tui.TuiKit;
using Threadsmith.Workspaces;

/// <summary>Composes one selected frontend over the same interactive coordinator.</summary>
internal static class InteractiveFrontendRunner
{
    /// <summary>Starts the selected frontend over identical shared interaction services.</summary>
    internal static Task RunAsync(ShellRunContext context, CancellationTokenSource processCancellation)
    {
        var (catalog, defaultId) = TuiThemeConfigurationLoader.Load(context.Configuration);
        var themes = new SessionThemePreferences(catalog, defaultId);
        var display = TuiDisplayOptions.Load(context.Configuration);
        if (context.CommandLine.InteractiveFrontend == InteractiveFrontendKind.TuiKit)
        {
            return RunTuiKitAsync(context, themes, display, processCancellation);
        }

        if (context.CommandLine.InteractiveFrontend != InteractiveFrontendKind.Original)
        {
            throw new InvalidOperationException("Interactive startup requires a selected frontend.");
        }

        var surface = new PrettyPromptConsoleSurface(themes.ActiveTheme);
        return RunCoordinatorAsync(CreateCoordinator(context, themes, display, surface, surface.SetThemeAsync), context, processCancellation.Token);
    }

    // Keep backend construction out of the PrettyPrompt/headless JIT and initialization paths.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task RunTuiKitAsync(ShellRunContext context, SessionThemePreferences themes, TuiDisplayOptions display, CancellationTokenSource processCancellation)
    {
        await using var surface = new TuiKitSurface(themes.ActiveTheme, processCancellation.Cancel);
        var coordinator = CreateCoordinator(context, themes, display, surface, surface.SetThemeAsync);
        await surface.RunAsync(token => RunCoordinatorAsync(coordinator, context, token), processCancellation.Token);
    }

    private static InteractionCoordinator CreateCoordinator(
        ShellRunContext context,
        SessionThemePreferences themes,
        TuiDisplayOptions display,
        IInteractionSurface surface,
        Func<ConfiguredTheme, CancellationToken, Task> applyTheme)
    {
        var extensionHost = context.ExtensionHost ?? throw new InvalidOperationException("Interactive startup requires the extension host.");
        var themeCommands = new ThemeCommandContribution(themes, applyTheme, new UserConfigurationThemePreferenceStore(context.Paths.UserConfiguration));
        return new InteractionCoordinator(
            new InteractionPresenter(context.Dispatcher, context.Projections),
            context.Events,
            surface,
            context.Models.Catalog,
            context.Applications.EffectiveStartupProfileId,
            context.Applications.SessionModelPreferences,
            extensionHost,
            context.Applications.SessionUsage,
            context.Configuration.GetValue("tui:footer:enabled", true),
            context.ToolStateManager,
            context.Applications.MutationApprovalPolicy,
            context.Applications.PlanApprovalPolicy,
            context.Models.ActiveModels is not null,
            context.Applications.ClaudeSkillCatalog,
            sessionLifecycleAvailable: true,
            displayOptions: display.ToInteractionOptions(),
            displayWarnings: themes.Catalog.Warnings.Concat(display.Diagnostics).ToArray(),
            gitQueries: new GitQueryService(),
            webFetchAuthorization: context.WebFetchAuthorization,
            directFetchApprovalPrompt: context.DirectFetchApprovalPrompt,
            frontendCommands: themeCommands,
            validationStages: context.Applications.ValidationStages,
            codeExploreOutputOptions: context.CodeExploreOutputOptions);
    }

    private static Task RunCoordinatorAsync(InteractionCoordinator coordinator, ShellRunContext context, CancellationToken cancellationToken) => coordinator.RunAsync(
        context.Paths.RepositoryRoot,
        context.CommandLine.RequestedTrust,
        context.CommandLine.RequestedSolution,
        context.Models.Status,
        context.Paths.RepositoryConfigurationDirectoryExistedAtStartup,
        cancellationToken);
}
