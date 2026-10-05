namespace Threadsmith.Tools;

using Microsoft.Extensions.Configuration;
using Threadsmith.Core;

/// <summary>Binds ordinary layered memory settings without carrying overrides across repositories.</summary>
public sealed class RepositoryMemoryConfiguration : IRepositoryMemoryOptionsProvider
{
    /// <summary>The sole configuration namespace for repository memories.</summary>
    public const string SectionName = "tools:config:memories";

    private readonly RepositoryMemoryOptions _fallback;
    private Snapshot _current;

    /// <summary>Initializes a new instance of the <see cref="RepositoryMemoryConfiguration"/> class.</summary>
    public RepositoryMemoryConfiguration(IConfiguration configuration, IConfiguration fallbackConfiguration, string repositoryPath)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(fallbackConfiguration);
        var identity = RepositoryIdentity.Create(repositoryPath);
        _fallback = Read(fallbackConfiguration, new RepositoryMemoryOptions());
        _current = new Snapshot(identity, Read(configuration, _fallback));
    }

    /// <inheritdoc />
    public RepositoryMemoryOptions CaptureCurrent()
    {
        return Volatile.Read(ref _current).Options;
    }

    /// <inheritdoc />
    public RepositoryMemoryOptions Capture(string repositoryIdentity)
    {
        var snapshot = Volatile.Read(ref _current);
        if (!string.Equals(snapshot.Identity, repositoryIdentity, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Memory settings belong to another repository. Reopen the repository before retrying.");
        }

        return snapshot.Options;
    }

    /// <summary>Rebinds repository overrides after validating the complete effective snapshot.</summary>
    public void BindRepository(string repositoryPath, IConfiguration repositoryConfiguration)
    {
        ArgumentNullException.ThrowIfNull(repositoryConfiguration);
        var identity = RepositoryIdentity.Create(repositoryPath);
        var options = Read(repositoryConfiguration, _fallback);
        Volatile.Write(ref _current, new Snapshot(identity, options));
    }

    /// <summary>Validates effective options without changing the active repository snapshot.</summary>
    public RepositoryMemoryOptions ReadRepositoryOptions(IConfiguration repositoryConfiguration)
    {
        ArgumentNullException.ThrowIfNull(repositoryConfiguration);
        return Read(repositoryConfiguration, _fallback);
    }

    /// <summary>Publishes a previously prepared immutable snapshot after repository persistence commits.</summary>
    public void BindRepository(string repositoryPath, RepositoryMemoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var identity = RepositoryIdentity.Create(repositoryPath);
        Volatile.Write(ref _current, new Snapshot(identity, options));
    }

    private static RepositoryMemoryOptions Read(IConfiguration configuration, RepositoryMemoryOptions fallback)
    {
        foreach (var key in new[] { "MaxRepoMemoriesInContext", "SemanticMinimum", "RerankerEnabled", "RerankerCandidateLimit", "MaximumQueryCharacters", "ConceptRecall", "Lexical", "Reconciliation:CandidateLimit", "Reconciliation:Concepts:FuzzyEnabled", "Reconciliation:Concepts:FuzzyMaximumDistance" })
        {
            if (configuration.GetSection($"{SectionName}:{key}").Exists())
            {
                throw new ArgumentException($"Memory setting '{key}' has moved. Use the Recall and Reconciliation blocks; each owns SemanticMinimum, RerankerEnabled, RerankerCandidateLimit, Concepts, Fuzzy and Lexical settings. Recall:MaximumResults replaces MaxRepoMemoriesInContext.");
            }
        }

        var recallLexical = ReadLexical(configuration, "Recall", fallback.Lexical);
        var reconciliationLexical = ReadLexical(configuration, "Reconciliation", fallback.ReconciliationLexical);
        var options = new RepositoryMemoryOptions
        {
            Lexical = recallLexical,
            ReconciliationLexical = reconciliationLexical,
            ReconciliationRerankerEnabled = configuration.GetValue($"{SectionName}:Reconciliation:RerankerEnabled", fallback.ReconciliationRerankerEnabled),
            ReconciliationConceptsEnabled = configuration.GetValue($"{SectionName}:Reconciliation:Concepts:Enabled", fallback.ReconciliationConceptsEnabled),
            ReconciliationConceptCandidateLimit = configuration.GetValue($"{SectionName}:Reconciliation:Concepts:CandidateLimit", fallback.ReconciliationConceptCandidateLimit),
            ReconciliationConceptFuzzyEnabled = reconciliationLexical.FuzzyEnabled,
            ReconciliationConceptFuzzyMaximumDistance = reconciliationLexical.FuzzyMaximumDistance,
            ReconciliationEnabled = configuration.GetValue($"{SectionName}:Reconciliation:Enabled", fallback.ReconciliationEnabled),
            ReconciliationSemanticMinimum = configuration.GetValue($"{SectionName}:Reconciliation:SemanticMinimum", fallback.ReconciliationSemanticMinimum),
            ReconciliationCandidateLimit = configuration.GetValue($"{SectionName}:Reconciliation:RerankerCandidateLimit", fallback.ReconciliationCandidateLimit),
            ConceptFuzzyEnabled = recallLexical.FuzzyEnabled,
            ConceptFuzzyMaximumDistance = recallLexical.FuzzyMaximumDistance,
            ConceptCandidateLimit = configuration.GetValue($"{SectionName}:Recall:Concepts:CandidateLimit", fallback.ConceptCandidateLimit),
            ConceptRecallEnabled = configuration.GetValue($"{SectionName}:Recall:Concepts:Enabled", fallback.ConceptRecallEnabled),
            MaximumTextCharacters = configuration.GetValue($"{SectionName}:MaximumTextCharacters", fallback.MaximumTextCharacters),
            MaximumQueryCharacters = configuration.GetValue($"{SectionName}:Recall:MaximumQueryCharacters", fallback.MaximumQueryCharacters),
            MaximumQueryTerms = configuration.GetValue($"{SectionName}:MaximumQueryTerms", fallback.MaximumQueryTerms),
            MaximumCacheEntries = configuration.GetValue($"{SectionName}:MaximumCacheEntries", fallback.MaximumCacheEntries),
            MaximumDiagnostics = configuration.GetValue($"{SectionName}:MaximumDiagnostics", fallback.MaximumDiagnostics),
            MaximumListBytes = configuration.GetValue($"{SectionName}:MaximumListBytes", fallback.MaximumListBytes),
            MaxNumberOfRepoMemories = configuration.GetValue($"{SectionName}:MaxNumberOfRepoMemories", fallback.MaxNumberOfRepoMemories),
            MaxRepoMemoriesInContext = configuration.GetValue($"{SectionName}:Recall:MaximumResults", fallback.MaxRepoMemoriesInContext),
            SemanticMinimum = configuration.GetValue($"{SectionName}:Recall:SemanticMinimum", fallback.SemanticMinimum),
            RerankerEnabled = configuration.GetValue($"{SectionName}:Recall:RerankerEnabled", fallback.RerankerEnabled),
            RerankerCandidateLimit = configuration.GetValue($"{SectionName}:Recall:RerankerCandidateLimit", fallback.RerankerCandidateLimit),
            StandingPreferenceWarningThreshold = configuration.GetValue(
                $"{SectionName}:standingPreferenceWarningThreshold",
                fallback.StandingPreferenceWarningThreshold),
        };
        options.Validate();
        return options;
    }

    private static RepositoryMemoryLexicalOptions ReadLexical(IConfiguration configuration, string scenario, RepositoryMemoryLexicalOptions fallback)
    {
        return new()
        {
            FuzzyEnabled = configuration.GetValue($"{SectionName}:{scenario}:Fuzzy:Enabled", fallback.FuzzyEnabled),
            FuzzyMaximumDistance = configuration.GetValue($"{SectionName}:{scenario}:Fuzzy:MaximumDistance", fallback.FuzzyMaximumDistance),
            MaximumExpansionsPerTerm = configuration.GetValue($"{SectionName}:{scenario}:Lexical:MaximumExpansionsPerTerm", fallback.MaximumExpansionsPerTerm),
            MaximumExpansions = configuration.GetValue($"{SectionName}:{scenario}:Lexical:MaximumExpansions", fallback.MaximumExpansions),
        };
    }

    private sealed record Snapshot(string Identity, RepositoryMemoryOptions Options);
}
