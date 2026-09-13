namespace Threadsmith.Skills;

using System.Text.RegularExpressions;
using Threadsmith.Core;

/// <summary>Validates bounded declarative requirements, budgets, workflows, and agent templates.</summary>
internal static partial class SkillManifestValidator
{
    private static readonly HashSet<string> ProhibitedDirectTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "invoke_skill",
        "run_process",
        "web_search",
        "csharp_script",
        "propose_mutations",
        "propose_plan",
    };

    /// <summary>Validates declared host, tool, trust, approval, and model requirements.</summary>
    internal static void ValidateRequirements(SkillRequirementSet requirements, SkillCatalogOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        options ??= new();
        ValidateVersion(requirements.MinimumHostVersion, "minimum host version");
        ValidateVersion(requirements.MaximumHostVersion, "maximum host version");
        if (SemanticVersionComparer.Instance.Compare(
            requirements.MinimumHostVersion,
            requirements.MaximumHostVersion) > 0)
        {
            throw new InvalidDataException("Skill host version range is inverted.");
        }

        ValidateIds(requirements.RequiredTools, "required tools", options.MaximumRequiredTools, options.MaximumIdentifierCharacters);
        ValidateIds(requirements.OptionalTools, "optional tools", options.MaximumRequiredTools, options.MaximumIdentifierCharacters);
        if (requirements.RequiredTools.Intersect(
            requirements.OptionalTools,
            StringComparer.OrdinalIgnoreCase).Any())
        {
            throw new InvalidDataException("A tool cannot be both required and optional.");
        }

        if (requirements.RequiredTools.Concat(requirements.OptionalTools).Any(tool =>
            ProhibitedDirectTools.Contains(tool)
            || tool.Contains("mutation", StringComparison.OrdinalIgnoreCase)
            || tool.Contains("write", StringComparison.OrdinalIgnoreCase)
            || tool.Contains("apply", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                "Skills cannot declare nested-skill, direct process/network/script, planning, or mutation tools.");
        }

        if (requirements.ToolContractVersions.Count > options.MaximumContractVersions
            || requirements.ToolContractVersions.Any(pair =>
                string.IsNullOrWhiteSpace(pair.Key)
                || string.IsNullOrWhiteSpace(pair.Value)
                || pair.Key.Length > options.MaximumIdentifierCharacters
                || pair.Value.Length > options.MaximumVersionCharacters))
        {
            throw new InvalidDataException("Skill tool contract requirements exceed their bounds.");
        }

        if (requirements.ApprovalCategories.Count > options.MaximumApprovalCategories
            || requirements.ApprovalCategories.Any(item =>
                string.IsNullOrWhiteSpace(item) || item.Length > options.MaximumIdentifierCharacters))
        {
            throw new InvalidDataException("Skill approval disclosures exceed their bounds.");
        }

        var model = requirements.Model;
        if (model.MinimumContextWindow < 0
            || model.Workloads.Count > options.MaximumWorkloads
            || model.Workloads.Any(item => string.IsNullOrWhiteSpace(item) || item.Length > options.MaximumVersionCharacters)
            || model.AllowedProfiles.Count > options.MaximumModelProfiles
            || model.DeniedProfiles.Count > options.MaximumModelProfiles
            || model.AllowedProfiles.Intersect(model.DeniedProfiles).Any())
        {
            throw new InvalidDataException("Skill model requirements exceed their bounds or conflict.");
        }
    }

    /// <summary>Validates a finite configured skill budget and its internal relationships.</summary>
    internal static void ValidateBudget(SkillBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        if (budget.ContentTokens < 1
            || budget.WorkflowSteps < 1
            || budget.ModelTurns < 0
            || budget.ToolCalls < 0
            || budget.Mutations < 0
            || budget.ValidationAttempts < 0
            || budget.DelegatedChildren < 0
            || budget.ParallelChildren < 0
            || budget.ParallelChildren > budget.DelegatedChildren
            || budget.Worktrees < 0
            || budget.ReviewerFindings < 0
            || budget.WallTime <= TimeSpan.Zero
            || budget.WallTime.TotalMilliseconds > uint.MaxValue - 1d)
        {
            throw new InvalidDataException("Skill budget values or relationships are invalid.");
        }
    }

    /// <summary>Validates the closed bounded acyclic workflow graph.</summary>
    internal static void ValidateWorkflow(
        SkillWorkflowDefinition workflow,
        IReadOnlyList<SkillAssetMetadata> assets)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        if (workflow.SchemaVersion != 1
            || string.IsNullOrWhiteSpace(workflow.WorkflowId)
            || workflow.Steps.Count < 1)
        {
            throw new InvalidDataException("Skill workflow identity, schema, or size is invalid.");
        }

        var steps = new Dictionary<string, SkillWorkflowStep>(StringComparer.Ordinal);
        var assetPaths = assets.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var step in workflow.Steps)
        {
            if (string.IsNullOrWhiteSpace(step.StepId)
                || !steps.TryAdd(step.StepId, step)
                || step.MaximumIterations < 1
                || step.DependsOn.Count != step.DependsOn.Distinct(StringComparer.Ordinal).Count())
            {
                throw new InvalidDataException("Skill workflow step identity, loop, or dependencies are invalid.");
            }

            ValidateOptionalAsset(step.InstructionAsset, assetPaths);
            ValidateOptionalAsset(step.InputSchemaAsset, assetPaths);
            ValidateOptionalAsset(step.OutputSchemaAsset, assetPaths);
            if ((step.Kind is SkillWorkflowStepKind.InvokeProcedure
                    or SkillWorkflowStepKind.CollectEvidence
                    or SkillWorkflowStepKind.Summarize)
                && (step.InstructionAsset is null || step.OutputSchemaAsset is null))
            {
                throw new InvalidDataException(
                    "Model-backed workflow steps require instruction and output-schema assets.");
            }

            if (step.HostAction is not null && !ActionMatchesStep(step.Kind, step.HostAction.Value))
            {
                throw new InvalidDataException("Workflow step declares an incompatible host action.");
            }

            if ((step.HostAction is not null || ActionMatchesStepKind(step.Kind))
                && step.MaximumIterations != 1)
            {
                throw new InvalidDataException("Host-action workflow steps cannot repeat.");
            }
        }

        foreach (var step in workflow.Steps)
        {
            if (step.DependsOn.Any(id => id == step.StepId || !steps.ContainsKey(id)))
            {
                throw new InvalidDataException("Workflow dependencies must refer to distinct existing steps.");
            }
        }

        DetectCycles(steps);
    }

    /// <summary>Validates aggregate workflow iterations against package budgets.</summary>
    internal static void ValidateWorkflowBudget(
        SkillWorkflowDefinition workflow,
        SkillBudget budget)
    {
        var iterations = workflow.Steps.Sum(item => (long)item.MaximumIterations);
        var modelTurns = workflow.Steps
            .Where(item => item.Kind is SkillWorkflowStepKind.InvokeProcedure
                or SkillWorkflowStepKind.CollectEvidence
                or SkillWorkflowStepKind.Summarize)
            .Sum(item => (long)item.MaximumIterations);
        if (iterations > budget.WorkflowSteps || modelTurns > budget.ModelTurns)
        {
            throw new InvalidDataException("Skill workflow iterations exceed the declared aggregate budget.");
        }
    }

    /// <summary>Validates bounded Plan-38 templates against package assets and budgets.</summary>
    internal static void ValidateAgents(
        IReadOnlyList<SkillAgentTemplate> agents,
        IReadOnlyList<SkillAssetMetadata> assets,
        SkillBudget budget)
    {
        ArgumentNullException.ThrowIfNull(agents);
        if (agents.Sum(item => (long)item.MaximumChildren) > budget.DelegatedChildren)
        {
            throw new InvalidDataException("Skill agent templates exceed the delegation budget.");
        }

        var paths = assets.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var agent in agents)
        {
            if (agent.MaximumChildren < 1 || !paths.Contains(agent.OutputSchemaPath))
            {
                throw new InvalidDataException("Skill agent template is invalid or references a missing schema.");
            }

            var child = agent.Budget;
            if (child.ModelTokens < 0 || child.ToolCalls < 0 || child.EvidenceItems < 0
                || child.Files < 0 || child.Bytes < 0 || child.Mutations < 0
                || child.Processes < 0 || child.Builds < 0 || child.Tests < 0
                || child.Corrections < 0 || child.WallTime <= TimeSpan.Zero)
            {
                throw new InvalidDataException("Skill agent budget must be finite and non-negative.");
            }
        }
    }

    private static void DetectCycles(IReadOnlyDictionary<string, SkillWorkflowStep> steps)
    {
        var remainingDependencies = steps.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.DependsOn.Count,
            StringComparer.Ordinal);
        var dependents = steps.Keys.ToDictionary(
            id => id,
            _ => new List<string>(),
            StringComparer.Ordinal);
        foreach (var step in steps.Values)
        {
            foreach (var dependency in step.DependsOn)
            {
                dependents[dependency].Add(step.StepId);
            }
        }

        var ready = new Queue<string>(remainingDependencies.Where(pair => pair.Value == 0).Select(pair => pair.Key));
        var visited = 0;
        while (ready.TryDequeue(out var id))
        {
            visited++;
            foreach (var dependent in dependents[id])
            {
                if (--remainingDependencies[dependent] == 0)
                {
                    ready.Enqueue(dependent);
                }
            }
        }

        if (visited != steps.Count)
        {
            throw new InvalidDataException("Skill workflow graph contains a cycle.");
        }
    }

    private static bool ActionMatchesStepKind(SkillWorkflowStepKind step)
    {
        return step is SkillWorkflowStepKind.ProposePlan
            or SkillWorkflowStepKind.ExecuteApprovedPlan
            or SkillWorkflowStepKind.ProposeDelegation
            or SkillWorkflowStepKind.RequestReviews
            or SkillWorkflowStepKind.Validate
            or SkillWorkflowStepKind.AskUserInput;
    }

    private static bool ActionMatchesStep(SkillWorkflowStepKind step, SkillHostActionKind action)
    {
        return (step, action) switch
        {
            (SkillWorkflowStepKind.ProposePlan, SkillHostActionKind.ProposePlan) => true,
            (SkillWorkflowStepKind.ExecuteApprovedPlan, SkillHostActionKind.ExecuteApprovedPlan) => true,
            (SkillWorkflowStepKind.ProposeDelegation, SkillHostActionKind.ProposeDelegation) => true,
            (SkillWorkflowStepKind.Validate, SkillHostActionKind.Validate) => true,
            (SkillWorkflowStepKind.AskUserInput, SkillHostActionKind.AskUserInput) => true,
            _ => false,
        };
    }

    private static void ValidateOptionalAsset(string? path, IReadOnlySet<string> assets)
    {
        if (path is null)
        {
            return;
        }

        SkillPathPolicy.ValidateRelativePath(path);
        if (!assets.Contains(path))
        {
            throw new InvalidDataException($"Skill workflow references undeclared asset '{path}'.");
        }
    }

    private static void ValidateIds(IReadOnlyList<string> values, string name, int maximum, int maximumCharacters)
    {
        if (values.Count > maximum
            || values.Count != values.Distinct(StringComparer.OrdinalIgnoreCase).Count()
            || values.Any(item => string.IsNullOrWhiteSpace(item) || item.Length > maximumCharacters))
        {
            throw new InvalidDataException($"Skill {name} exceed bounds or contain duplicates.");
        }
    }

    private static void ValidateVersion(string value, string name)
    {
        if (!SemanticVersionRegex().IsMatch(value))
        {
            throw new InvalidDataException($"Skill {name} is not a semantic version.");
        }
    }

    [GeneratedRegex("^[0-9]+\\.[0-9]+\\.[0-9]+(?:-[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SemanticVersionRegex();
}

/// <summary>Cross-platform package path confinement policy.</summary>
internal static class SkillPathPolicy
{
    /// <summary>Rejects rooted, traversing, alternate-stream, and ambiguous asset paths.</summary>
    internal static void ValidateRelativePath(string path, int maximumCharacters = int.MaxValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Length > maximumCharacters || Path.IsPathRooted(path) || path.Contains(':', StringComparison.Ordinal))
        {
            throw new InvalidDataException("Skill asset path must be bounded and package-relative.");
        }

        var normalized = path.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0
            || segments.Any(segment => segment is "." or ".." || segment.EndsWith(' ') || segment.EndsWith('.')))
        {
            throw new InvalidDataException("Skill asset path contains an unsafe segment.");
        }
    }

    /// <summary>Resolves a package-relative path without crossing links or the package root.</summary>
    internal static string ResolveConfined(string root, string relativePath)
    {
        ValidateRelativePath(relativePath);
        var fullRoot = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var prefix = Path.TrimEndingDirectorySeparator(fullRoot) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, comparison))
        {
            throw new UnauthorizedAccessException("Skill path escapes its package root.");
        }

        var current = fullRoot;
        foreach (var segment in relativePath.Replace('\\', '/').Split('/'))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException("Skill package paths cannot traverse links or reparse points.");
            }
        }

        return candidate;
    }
}
