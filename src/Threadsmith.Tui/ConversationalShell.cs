namespace Threadsmith.Tui;

using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Interaction.Coordination;
using Threadsmith.Interaction.Markdown;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Models;
using Threadsmith.Tools;

/// <summary>Constructs the current PrettyPrompt frontend and delegates coordination to Interaction.</summary>
public sealed class ConversationalShell
{
    private readonly InteractionCoordinator _coordinator;

    /// <summary>Initializes a new instance of the <see cref="ConversationalShell" /> class.</summary>
    public ConversationalShell(
        TuiPresenter presenter,
        IDomainEventStream events,
        ConfiguredModelCatalog? modelCatalog = null,
        ModelProfileId? activeProfileId = null,
        SessionModelPreferences? sessionPreferences = null,
        IExtensionManager? extensionManager = null,
        IConfiguration? configuration = null,
        SessionUsageProjection? sessionUsage = null,
        IToolStateManager? toolStateManager = null,
        IMutationApprovalPolicy? mutationApprovalPolicy = null,
        IPlanApprovalPolicy? planApprovalPolicy = null,
        bool activeModelSelectionAvailable = false,
        IClaudeSkillCompatibilityCatalog? claudeSkills = null,
        bool sessionLifecycleAvailable = false,
        IGitQueryService? gitQueries = null,
        WebFetchAuthorizationAuthority? webFetchAuthorization = null,
        DirectFetchApprovalPromptRouter? directFetchApprovalPrompt = null,
        string? userConfigurationPath = null,
        IReadOnlyList<MutationValidationStage>? validationStages = null,
        CodeExploreOutputOptions? codeExploreOutputOptions = null)
    {
        ArgumentNullException.ThrowIfNull(presenter);
        ArgumentNullException.ThrowIfNull(events);
        (var catalog, var defaultThemeId) = TuiThemeConfigurationLoader.Load(configuration);
        var themePreferences = new SessionThemePreferences(catalog, defaultThemeId);
        var displayOptions = TuiDisplayOptions.Load(configuration);
        var surface = new PrettyPromptConsoleSurface(themePreferences.ActiveTheme);
        var preferenceStore = string.IsNullOrWhiteSpace(userConfigurationPath)
            ? null
            : new UserConfigurationThemePreferenceStore(userConfigurationPath);
        var themeCommands = new ThemeCommandContribution(
            themePreferences,
            surface.SetThemeAsync,
            preferenceStore);
        var warnings = catalog.Warnings.Concat(displayOptions.Diagnostics).ToArray();
        _coordinator = new InteractionCoordinator(
            presenter,
            events,
            surface,
            modelCatalog,
            activeProfileId,
            sessionPreferences,
            extensionManager,
            sessionUsage,
            configuration?.GetValue("tui:footer:enabled", true) ?? true,
            toolStateManager,
            mutationApprovalPolicy,
            planApprovalPolicy,
            activeModelSelectionAvailable,
            claudeSkills,
            sessionLifecycleAvailable,
            displayOptions.ToInteractionOptions(),
            warnings,
            TimeProvider.System,
            gitQueries,
            webFetchAuthorization,
            directFetchApprovalPrompt,
            themeCommands,
            validationStages,
            codeExploreOutputOptions ?? CodeExploreOutputOptions.FromConfiguration(configuration));
        foreach (var warning in catalog.Warnings)
        {
            Debug.WriteLine(warning);
        }
    }

    /// <summary>Initializes a new instance of the <see cref="ConversationalShell" /> class.</summary>
    internal ConversationalShell(
        TuiPresenter presenter,
        IDomainEventStream events,
        IConsoleSurface surface,
        ConfiguredModelCatalog? modelCatalog = null,
        ModelProfileId? activeProfileId = null,
        SessionModelPreferences? sessionPreferences = null,
        IExtensionManager? extensionManager = null,
        SessionThemePreferences? themePreferences = null,
        SessionUsageProjection? sessionUsage = null,
        bool showSessionStatus = true,
        IToolStateManager? toolStateManager = null,
        IMutationApprovalPolicy? mutationApprovalPolicy = null,
        IPlanApprovalPolicy? planApprovalPolicy = null,
        bool activeModelSelectionAvailable = false,
        IClaudeSkillCompatibilityCatalog? claudeSkills = null,
        bool sessionLifecycleAvailable = false,
        TuiDisplayOptions? displayOptions = null,
        TimeProvider? timeProvider = null,
        IGitQueryService? gitQueries = null,
        WebFetchAuthorizationAuthority? webFetchAuthorization = null,
        DirectFetchApprovalPromptRouter? directFetchApprovalPrompt = null,
        IThemePreferenceStore? themePreferenceStore = null,
        IReadOnlyList<MutationValidationStage>? validationStages = null,
        CodeExploreOutputOptions? codeExploreOutputOptions = null)
    {
        ArgumentNullException.ThrowIfNull(presenter);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(surface);
        var effectiveThemes = themePreferences ?? new SessionThemePreferences(
            new ConfiguredThemeCatalog(BuiltInThemes.Create()),
            "system");
        var effectiveDisplayOptions = displayOptions ?? new TuiDisplayOptions();
        var themeCommands = new ThemeCommandContribution(
            effectiveThemes,
            surface.SetThemeAsync,
            themePreferenceStore);
        _coordinator = new InteractionCoordinator(
            presenter,
            events,
            surface,
            modelCatalog,
            activeProfileId,
            sessionPreferences,
            extensionManager,
            sessionUsage,
            showSessionStatus,
            toolStateManager,
            mutationApprovalPolicy,
            planApprovalPolicy,
            activeModelSelectionAvailable,
            claudeSkills,
            sessionLifecycleAvailable,
            effectiveDisplayOptions.ToInteractionOptions(),
            effectiveThemes.Catalog.Warnings.Concat(effectiveDisplayOptions.Diagnostics).ToArray(),
            timeProvider,
            gitQueries,
            webFetchAuthorization,
            directFetchApprovalPrompt,
            themeCommands,
            validationStages,
            codeExploreOutputOptions);
    }

    /// <summary>Runs the interactive conversation until exit or cancellation.</summary>
    public Task RunAsync(
        string? repositoryPath = null,
        RepositoryTrustLevel? requestedTrust = null,
        string? requestedSolutionPath = null,
        string modelStatus = "Scripted demo (offline)",
        bool? repositoryConfigurationDirectoryExistedAtStartup = null,
        CancellationToken cancellationToken = default)
    {
        return _coordinator.RunAsync(
            repositoryPath,
            requestedTrust,
            requestedSolutionPath,
            modelStatus,
            repositoryConfigurationDirectoryExistedAtStartup,
            cancellationToken);
    }

    /// <summary>Handles a compatibility fetch-authorization command.</summary>
    internal Task HandleFetchAuthorizationCommandAsync(
        string commandText,
        string? repositoryRoot,
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        return _coordinator.HandleFetchAuthorizationCommandAsync(
            commandText,
            repositoryRoot,
            sessionId,
            cancellationToken);
    }

    /// <summary>Writes output while preserving the shutdown fallback behavior.</summary>
    internal static async Task WriteOutputWithCancellationFallbackAsync(
        IConsoleSurface surface,
        IReadOnlyList<PresentationItem> output,
        CancellationToken cancellationToken)
    {
        await InteractionCoordinator.WriteOutputWithCancellationFallbackAsync(
            new InteractionSessionSurface(surface),
            output,
            cancellationToken);
    }

    /// <summary>Flushes a buffered final answer during shutdown.</summary>
    internal static PresentationItem? FlushFinalAnswerForShutdown(ModelAnswerCollector collector)
    {
        return InteractionCoordinator.FlushFinalAnswerForShutdown(collector);
    }

    /// <summary>Formats the start of post-apply validation.</summary>
    internal static IReadOnlyList<PresentationTextSegment>? FormatPostApplyValidationStartSegments(
        IReadOnlyList<MutationValidationStage> stages)
    {
        return InteractionCoordinator.FormatPostApplyValidationStartSegments(stages);
    }

    /// <summary>Formats a post-apply validation result.</summary>
    internal static (string Message, PresentationTextRole Role) FormatPostApplyValidationResult(
        ExecutionCheckpointPhase phase,
        string suffix)
    {
        return InteractionCoordinator.FormatPostApplyValidationResult(phase, suffix);
    }

    /// <summary>Resolves the current repository branch when available.</summary>
    internal static Task<string?> ResolveCurrentBranchAsync(
        IGitQueryService? gitQueries,
        string repositoryPath,
        bool repositoryIsOpen,
        CancellationToken cancellationToken)
    {
        return InteractionCoordinator.ResolveCurrentBranchAsync(
            gitQueries,
            repositoryPath,
            repositoryIsOpen,
            cancellationToken);
    }

    /// <summary>Formats active-turn compaction activity.</summary>
    internal static string FormatActiveTurnCompactionActivity(ActiveTurnCompactionStarted started)
    {
        return InteractionCoordinator.FormatActiveTurnCompactionActivity(started);
    }

    /// <summary>Formats active-turn compaction completion.</summary>
    internal static string FormatActiveTurnCompactionCompletion(
        ActiveTurnCompactionCompleted completed,
        bool showOperationDuration = true)
    {
        return InteractionCoordinator.FormatActiveTurnCompactionCompletion(
            completed,
            showOperationDuration);
    }

    /// <summary>Formats active-turn compaction inspection.</summary>
    internal static string FormatActiveTurnInspection(
        ActiveTurnCompactionInspectionProjection? activeTurn)
    {
        return InteractionCoordinator.FormatActiveTurnInspection(activeTurn);
    }
}
