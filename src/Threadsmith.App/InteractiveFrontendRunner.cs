namespace Threadsmith.App;

using Microsoft.Extensions.Configuration;
using Threadsmith.Core;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Coordination;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Tui.TuiKit;
using Threadsmith.Workspaces;
using TUIKit.Terminal;

/// <summary>Composes one selected frontend over the same interactive coordinator.</summary>
internal static class InteractiveFrontendRunner
{
    /// <summary>Owns the single terminal from its first emitted frame through host cleanup.</summary>
    internal static async Task<int> RunStartupAsync(
        Func<TuiKitSurface, SemanticStartupProgress, CancellationToken, Task<int>> initialize,
        CancellationTokenSource processCancellation,
        ITerminalBackend? backend = null,
        SemanticStartupProgress? progress = null)
    {
        try
        {
            var result = 1;
            string? failureDetail = null;
            await using (var surface = new TuiKitSurface(BuiltInThemes.Create()[0], processCancellation.Cancel, backend, deferWorkspace: true))
            {
                progress ??= new SemanticStartupProgress();
                using var observation = progress.ObserveProcess();
                await surface.RunAsync(
                    async token =>
                {
                    await using var terminalCancellation = token.Register(processCancellation.Cancel);
                    await surface.BeginProcessStartupAsync(progress, () => observation.Snapshot, token);
                    progress.MarkFirstFrame();
                    try
                    {
                        result = await initialize(surface, progress, token);
                        if (result is not 0 and not 130)
                        {
                            failureDetail = await surface.AcknowledgeStartupFailureAsync(string.Empty, token);
                        }
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException && progress.ReadyTimestamp is null)
                    {
                        failureDetail = await surface.AcknowledgeStartupFailureAsync(Program.FormatErrorDetails(exception), token);
                        result = 1;
                    }
                },
                    processCancellation.Token);
                processCancellation.Token.ThrowIfCancellationRequested();
            }

            if (failureDetail is not null)
            {
                // Preserve the complete bounded diagnostic after the alternate screen is restored.
                await Console.Error.WriteLineAsync(failureDetail);
            }

            return result;
        }
        catch (UnsupportedTerminalException exception)
        {
            await Console.Error.WriteLineAsync(exception.Message);
            return 2;
        }
    }

    /// <summary>Starts the selected frontend over identical shared interaction services.</summary>
    internal static Task RunAsync(ShellRunContext context, CancellationTokenSource processCancellation)
    {
        var (catalog, defaultId) = TuiThemeConfigurationLoader.Load(context.Configuration);
        var themes = new SessionThemePreferences(catalog, defaultId);
        var display = TuiDisplayOptions.Load(context.Configuration);
        if (context.CommandLine.InteractiveFrontend != InteractiveFrontendKind.TuiKit)
        {
            throw new InvalidOperationException("Interactive startup requires a selected frontend.");
        }

        return RunTuiKitAsync(context, themes, display, processCancellation);
    }

    private static async Task RunTuiKitAsync(ShellRunContext context, SessionThemePreferences themes, TuiDisplayOptions display, CancellationTokenSource processCancellation)
    {
        var surface = context.EarlySurface ?? throw new InvalidOperationException("Interactive startup requires the early terminal surface.");
        await surface.AttachStartupServicesAsync(context.ImageArtifacts, processCancellation.Token);
        var coordinator = CreateCoordinator(context, themes, display, surface, surface.SetThemeAsync);
        var trusted = ConfigurationBootstrap.BuildTrusted(context.Paths);
        using var trustedLifetime = trusted as IDisposable;
        surface.QuestionBellEnabled = trusted["tui:questions:audio"]?.Equals("bell", StringComparison.OrdinalIgnoreCase) == true;
        context.Applications.UserQuestions.Attach(coordinator.PresentUserQuestionAsync);
        try
        {
            await RunCoordinatorAsync(coordinator, context, processCancellation.Token);
        }
        finally
        {
            context.Applications.UserQuestions.Detach();
        }
    }

    private static InteractionCoordinator CreateCoordinator(
        ShellRunContext context,
        SessionThemePreferences themes,
        TuiDisplayOptions display,
        IInteractionSurface surface,
        Func<ConfiguredTheme, CancellationToken, Task> applyTheme)
    {
        var extensionHost = context.ExtensionHost ?? throw new InvalidOperationException("Interactive startup requires the extension host.");
        var names = AgentNameConfiguration.Load(context.Configuration);
        var terminalWarnings = surface is TuiKitSurface tuiKit ? tuiKit.ThemeDiagnostics : [];
        var themeCommands = new ThemeCommandContribution(themes, applyTheme, new UserConfigurationThemePreferenceStore(context.Paths.UserConfiguration, context.MaximumUserConfigurationBytes));
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
            context.Models.ActiveModels is not null,
            context.Applications.ClaudeSkillCatalog,
            sessionLifecycleAvailable: true,
            displayOptions: display.ToInteractionOptions(),
            displayWarnings: themes.Catalog.Warnings.Concat(display.Diagnostics).Concat(context.Applications.StartupDisplayWarnings).Concat(names.Warnings).Concat(terminalWarnings).ToArray(),
            gitQueries: new GitQueryService(HostFoundation.LoadOperationalLimits(context.Configuration).Git),
            webFetchAuthorization: context.WebFetchAuthorization,
            directFetchApprovalPrompt: context.DirectFetchApprovalPrompt,
            frontendCommands: themeCommands,
            codeExploreOutputOptions: context.CodeExploreOutputOptions,
            standingPreferenceWarningThreshold: context.Configuration.GetValue(
                "tools:config:memories:standingPreferenceWarningThreshold",
                3),
            standingPreferenceWarningThresholdProvider: repositoryIdentity => context.Applications.MemoryOptions
                .Capture(repositoryIdentity)
                .StandingPreferenceWarningThreshold,
            agentDisplay: context.Applications.AgentDisplay,
            agentNames: names.Catalog,
            semanticStartupProgress: context.SemanticStartupProgress);
    }

    private static Task RunCoordinatorAsync(InteractionCoordinator coordinator, ShellRunContext context, CancellationToken cancellationToken)
    {
        return coordinator.RunAsync(
        context.Paths.RepositoryRoot,
        context.CommandLine.RequestedTrust,
        context.CommandLine.RequestedSolution,
        context.Models.Status,
        context.Paths.RepositoryConfigurationDirectoryExistedAtStartup,
        cancellationToken);
    }
}
