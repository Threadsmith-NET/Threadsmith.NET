namespace Threadsmith.Skills;

using System.Text.Json;
using Threadsmith.Core;

/// <summary>Opens only compiled-recipe dependencies through native catalog, verification and content services.</summary>
public sealed class FocusedReviewPrivateResolver
{
    private readonly string _deploymentRoot;
    private readonly ISkillPackageVerifier _verifier;
    private readonly ISkillContentLoader _content;
    private readonly BoundedJsonSchemaValidator _schemas;
    private readonly SkillCatalogOptions _catalogOptions;

    /// <summary>Initializes a new instance of the <see cref="FocusedReviewPrivateResolver"/> class.</summary>
    public FocusedReviewPrivateResolver(
        string deploymentRoot,
        ISkillPackageVerifier verifier,
        ISkillContentLoader content,
        BoundedJsonSchemaValidator schemas,
        SkillCatalogOptions? catalogOptions = null)
    {
        _deploymentRoot = Path.GetFullPath(deploymentRoot);
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _content = content ?? throw new ArgumentNullException(nameof(content));
        _schemas = schemas ?? throw new ArgumentNullException(nameof(schemas));
        _catalogOptions = catalogOptions ?? new();
    }

    /// <summary>Matches the public entry by deployment, native identity, verification and the declared step.</summary>
    public bool Handles(
        SkillCatalogCandidate candidate,
        SkillWorkflowStep step)
    {
        return candidate.Enabled && candidate.Verification == SkillVerificationState.Maintained
            && candidate.Provenance.Scope == SkillScope.Maintained
            && candidate.Identity.PackageId == "threadsmith.review"
            && candidate.Identity.Publisher == "Threadsmith.NET" && candidate.Identity.Version == "1.0.0"
            && Path.GetFullPath(candidate.Provenance.PackageRoot).Equals(
                Path.Combine(_deploymentRoot, "MaintainedSkills", "review"),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            && step.StepId == "request-review" && step.Kind == SkillWorkflowStepKind.RequestReviews;
    }

    /// <summary>Verifies the exact compiled public entry before host source acquisition.</summary>
    public async Task VerifyEntryAsync(
        SkillCatalogCandidate candidate,
        SkillInvocationPlan plan,
        CancellationToken cancellationToken = default)
    {
        if (candidate.Identity != plan.Package || !Handles(candidate, candidate.Metadata.Workflow.Steps.Single())
            || FocusedReviewTargetCapture.Hash(await File.ReadAllBytesAsync(
                SkillPathPolicy.ResolveConfined(candidate.Provenance.PackageRoot, "skill.json"), cancellationToken)) != FocusedReviewRecipePin.PublicManifestSha256)
        {
            throw new UnauthorizedAccessException("The public review entry differs from the compiled host recipe.");
        }
    }

    /// <summary>Rechecks public identity and private integrity/revocation before dispatch or recovery.</summary>
    public async Task<IReadOnlyList<IFocusedReviewCompletionPolicy>> ResolveAsync(
        SkillCatalogCandidate candidate,
        SkillInvocationPlan plan,
        FocusedReviewTarget target,
        int generation,
        int maximumCorrections,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (candidate.Identity != plan.Package || !Handles(candidate, candidate.Metadata.Workflow.Steps.Single()))
            {
                throw new UnauthorizedAccessException("Unbound review workflow.");
            }

            var publicManifest = SkillPathPolicy.ResolveConfined(candidate.Provenance.PackageRoot, "skill.json");
            if (FocusedReviewTargetCapture.Hash(await File.ReadAllBytesAsync(publicManifest, cancellationToken)) != FocusedReviewRecipePin.PublicManifestSha256)
            {
                throw new InvalidDataException("Public package differs from the compiled recipe.");
            }

            var privateRoot = Path.Combine(_deploymentRoot, "ReviewSkills");
            var recipePath = SkillPathPolicy.ResolveConfined(privateRoot, "recipe.json");
            var recipeBytes = await File.ReadAllBytesAsync(recipePath, cancellationToken);
            if (FocusedReviewTargetCapture.Hash(recipeBytes) != FocusedReviewRecipePin.Sha256)
            {
                throw new InvalidDataException("Review recipe integrity mismatch.");
            }

            var recipe = JsonSerializer.Deserialize<Recipe>(
                recipeBytes,
                FocusedReviewInput.JsonOptions)
                ?? throw new InvalidDataException("Review recipe is unavailable.");
            if (recipe.Version != 1 || recipe.PublicManifestSha256 != FocusedReviewRecipePin.PublicManifestSha256 || recipe.Dependencies.Count != 4)
            {
                throw new InvalidDataException("Unsupported review recipe.");
            }

            var catalog = new SkillCatalog([new SkillCatalogSource(SkillScope.Maintained, privateRoot, "host-review-dependencies", IsMaintained: true)], _catalogOptions);
            var discovered = await catalog.RefreshAsync(cancellationToken);
            var results = new List<IFocusedReviewCompletionPolicy>();
            foreach (var dependency in recipe.Dependencies)
            {
                var expectedRoot = SkillPathPolicy.ResolveConfined(privateRoot, dependency.Directory);
                var rawManifest = await File.ReadAllBytesAsync(SkillPathPolicy.ResolveConfined(expectedRoot, "skill.json"), cancellationToken);
                if (FocusedReviewTargetCapture.Hash(rawManifest) != dependency.ManifestSha256)
                {
                    throw new InvalidDataException("Private package changed.");
                }

                var package = discovered.Candidates.Single(item => Path.GetFullPath(item.Provenance.PackageRoot) == expectedRoot);
                package = await _verifier.VerifyAsync(package, cancellationToken);
                if (!package.Enabled || package.Verification != SkillVerificationState.Maintained)
                {
                    throw new UnauthorizedAccessException("Private dependency is disabled or invalid.");
                }

                var step = package.Metadata.Workflow.Steps.Single(item => item.StepId == dependency.ProcedureId);
                if (step.Kind != SkillWorkflowStepKind.InvokeProcedure || step.InputSchemaAsset is null || step.OutputSchemaAsset is null)
                {
                    throw new InvalidDataException("The dependency is not an eligible native procedure.");
                }

                var segments = await _content.LoadAsync(package, step, plan.EffectiveBudget.ContentTokens, cancellationToken);
                var instructions = segments.Single(item => item.AssetPath == step.InstructionAsset);
                var schema = segments.Single(item => item.AssetPath == step.OutputSchemaAsset);
                var inputSchema = segments.Single(item => item.AssetPath == step.InputSchemaAsset);
                _ = _schemas.Validate(_schemas.Compile(inputSchema.Content), "{}");
                var binding = new FocusedReviewBinding(
                    plan.Request.InvocationId,
                    generation,
                    FocusedReviewRecipePin.Sha256,
                    package.Identity,
                    step.StepId,
                    instructions.Sha256,
                    schema.Sha256,
                    target.Identity,
                    Enum.Parse<AgentRole>(dependency.Role, ignoreCase: false));
                results.Add(
                    new FocusedReviewCompletionPolicy(
                    binding,
                    instructions.Content,
                    schema.Content,
                    _schemas,
                    target,
                    maximumCorrections,
                    plan.EffectiveBudget with
                    {
                        ModelTurns = Math.Min(plan.EffectiveBudget.ModelTurns, package.Metadata.Budget.ModelTurns * 4),
                        ToolCalls = Math.Min(plan.EffectiveBudget.ToolCalls, package.Metadata.Budget.ToolCalls * 4),
                        ReviewerFindings = Math.Min(plan.EffectiveBudget.ReviewerFindings, package.Metadata.Budget.ReviewerFindings * 4),
                    }));
            }

            return results;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
            or InvalidOperationException or JsonException or ArgumentException)
        {
            throw new InvalidOperationException("Focused review dependencies are unavailable, changed, disabled or incompatible. Verify the installed review capability and current skill policy.");
        }
    }

    private sealed record Recipe(int Version, string PublicManifestSha256, IReadOnlyList<Dependency> Dependencies);

    private sealed record Dependency(string Directory, string Role, string ManifestSha256, string ProcedureId);
}
