namespace Threadsmith.Tools;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Policies.Configuration;
using Threadsmith.Policies.Contracts;
using Threadsmith.Policies.Evaluation;

/// <summary>Prepares examples and evaluates supplied policy without execution or activation.</summary>
public sealed class ValidateCommandPolicyTool : Tool<CommandPolicyValidationRequest, CommandPolicyValidationResult>
{
    /// <summary>Maximum retained examples per request.</summary>
    internal const int MaximumExamples = 32;

    /// <summary>Maximum characters in one command.</summary>
    internal const int MaximumCommandCharacters = 8192;

    /// <summary>Maximum aggregate command UTF-8 bytes.</summary>
    internal const int MaximumCommandBytes = 64 * 1024;

    /// <summary>Maximum candidate UTF-8 bytes.</summary>
    internal const int MaximumCandidateBytes = 64 * 1024;

    /// <summary>Maximum actions returned across all examples.</summary>
    internal const int MaximumActions = 256;

    /// <summary>Maximum serialized result bytes.</summary>
    internal const int MaximumResultBytes = 256 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly IReadOnlyDictionary<string, string> NoEnvironmentAdditions = new Dictionary<string, string>();
    private static readonly string[] ScopeWarnings =
    [
        "IsolatedCandidateOnly",
        "ActivePoliciesAndSavedGrantsNotRead",
        "InstalledLayerIntegrationNotChecked",
        "NoExecutionActivationOrGrant",
        "ExamplesDoNotProveGeneralSafety",
        "ArgumentsHaveNoPositiveExactCommandMatcher",
        "DirectoryMatchersIncludeDescendantsAndDoNotConfineFileArguments",
        "ScriptPathsDoNotValidateScriptContents",
        "LiteralShellNamesDoNotBindFixedImplementations",
        "PathBindingsDoNotPinFileContents",
        "MachineConstraintsDoNotGrantOrdinaryPermission",
    ];

    /// <summary>Initializes a new instance of the <see cref="ValidateCommandPolicyTool"/> class.</summary>
    public ValidateCommandPolicyTool(IPromptLoader prompts)
    {
        ArgumentNullException.ThrowIfNull(prompts);
        Definition = ToolDefinitionFactory.Create<CommandPolicyValidationRequest, CommandPolicyValidationResult>(
            "validate_command_policy",
            () => prompts.Get(PromptFileNames.ToolValidateCommandPolicyDescription),
            ToolCategory.NativeSkillTools,
            RepositoryTrustLevel.UntrustedInspection,
            ApprovalLevel.None,
            ToolSideEffect.ReadOnly,
            TimeSpan.FromSeconds(30),
            MaximumResultBytes) with
        {
            DisplayName = "Validate command policy",
            EnabledByDefault = false,
        };
    }

    /// <inheritdoc />
    public override ToolDefinition Definition { get; }

    /// <inheritdoc />
    public override Task<ToolExecution<CommandPolicyValidationResult>> ExecuteAsync(
        CommandPolicyValidationRequest input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var result = Evaluate(input, Math.Min(context.MaximumOutputBytes ?? MaximumResultBytes, MaximumResultBytes), cancellationToken);
        return Task.FromResult(new ToolExecution<CommandPolicyValidationResult>(result, []));
    }

    /// <summary>Runs the single non-executing validation implementation.</summary>
    internal static CommandPolicyValidationResult Evaluate(
        CommandPolicyValidationRequest input,
        int maximumOutputBytes = MaximumResultBytes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRequest(input);
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(input.WorkingDirectory));
        var shell = Path.GetFileNameWithoutExtension(input.Shell).ToLowerInvariant();
        var examples = input.Examples.ToArray();
        var allowedExecutables = input.AllowedExecutables?.ToArray();
        var warnings = new List<string>(ScopeWarnings);
        var diagnostics = new List<string>();
        if (allowedExecutables is null)
        {
            warnings.Add("OuterExecutableEligibilityUnassessed");
        }

        var parseStatus = input.PolicyJson is null ? "NotSupplied" : "Succeeded";
        var compileStatus = "NotAttempted";
        var candidateDigest = input.PolicyJson is null ? null
            : Convert.ToHexStringLower(SHA256.HashData(StrictUtf8.GetBytes(input.PolicyJson)));
        PolicyDocument? candidate = null;
        PolicySnapshot? snapshot = null;
        if (input.PolicyJson is not null)
        {
            try
            {
                candidate = PolicyJson.Parse(input.PolicyJson);
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException or NotSupportedException)
            {
                parseStatus = "Failed";
                diagnostics.Add(exception.Message[..Math.Min(exception.Message.Length, 2048)]);
            }

            if (candidate is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    snapshot = PolicyCompiler.Compile(
                        new PolicyDocument(),
                        input.Layer == "machine" ? candidate : null,
                        input.Layer == "user" ? candidate : null,
                        input.Platform,
                        shell);
                    compileStatus = "Succeeded";
                    if (!candidate.Platforms.TryGetValue(input.Platform, out var platformProfile)
                        || !platformProfile.Shells.ContainsKey(shell))
                    {
                        warnings.Add("NoExplicitSelectedShellProfile");
                    }
                }
                catch (Exception exception) when (exception is InvalidDataException or ArgumentException or JsonException or NotSupportedException)
                {
                    compileStatus = "Failed";
                    diagnostics.Add(exception.Message[..Math.Min(exception.Message.Length, 2048)]);
                }
            }
        }

        var results = new List<CommandPolicyValidationExampleResult>(examples.Length);
        var sharedActions = new Dictionary<string, string>(StringComparer.Ordinal);
        var sharedExpectationsDiffer = false;
        var actionCount = 0;
        var coverageStatus = "Complete";
        foreach (var example in examples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PreparedShellCommand prepared;
            try
            {
                prepared = ShellCommandPreparation.Prepare(example.Command, input.Shell, directory, NoEnvironmentAdditions);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or ArgumentException or InvalidOperationException or NotSupportedException)
            {
                results.Add(new(example.Id, example.Command, example.ExpectedOutcome, "Failed", exception is FileNotFoundException ? "NativeTargetUnavailable" : "PreparationUnsupportedOrUnresolved", null, false, null, null, "Unassessed", []));
                continue;
            }

            if (prepared.Actions.IsEmpty)
            {
                results.Add(new(example.Id, example.Command, example.ExpectedOutcome, "Failed", "NoCommandActions", null, false, null, null, "Unassessed", []));
                continue;
            }

            actionCount += prepared.Actions.Length;
            if (actionCount > MaximumActions)
            {
                coverageStatus = "ActionLimitExceeded";
                break;
            }

            var actions = new List<CommandPolicyValidationAction>(prepared.Actions.Length);
            var decisions = new List<PolicyDecision>(prepared.Actions.Length);
            foreach (var facts in prepared.Actions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bindingId = PolicyJson.Fingerprint(facts);
                var sharedIdentity = PolicyJson.Fingerprint(new
                {
                    facts.Platform,
                    facts.Shell,
                    facts.ExecutableName,
                    facts.ResolvedExecutable,
                    facts.CommandKind,
                    facts.WorkingDirectory,
                });
                if (sharedActions.TryGetValue(sharedIdentity, out var previousOutcome))
                {
                    sharedExpectationsDiffer |= previousOutcome != example.ExpectedOutcome;
                }
                else
                {
                    sharedActions.Add(sharedIdentity, example.ExpectedOutcome);
                }

                var decision = snapshot is null ? null : PolicyEvaluator.Evaluate(snapshot, facts);
                if (decision is not null)
                {
                    decisions.Add(decision);
                }

                actions.Add(new(bindingId, facts.ExecutableName, facts.ResolvedExecutable, facts.CommandKind, facts.Arguments.ToArray(), decision?.Outcome?.ToString(), decision?.ReasonCode ?? CandidateUnavailableReason(parseStatus, compileStatus)));
            }

            var complete = snapshot is null ? null : CommandPolicyCoordinator.Combine([.. decisions], mandatoryReview: false);
            var actualOutcome = complete?.Outcome?.ToString();
            var matched = actualOutcome == example.ExpectedOutcome
                && (example.ExpectedOutcome != nameof(PolicyOutcome.AlwaysDeny) || complete?.ReasonCode == PolicyReasons.ExplicitDenial);
            var outerExecutable = Path.GetFileNameWithoutExtension(prepared.Launch.FileName);
            var outerEligibility = allowedExecutables is null ? "Unassessed"
                : allowedExecutables.Contains(outerExecutable, StringComparer.OrdinalIgnoreCase) ? "Eligible" : "Ineligible";
            results.Add(new(example.Id, example.Command, example.ExpectedOutcome, "Succeeded", complete?.ReasonCode ?? CandidateUnavailableReason(parseStatus, compileStatus), actualOutcome, matched, outerExecutable, prepared.Launch.FileName, outerEligibility, actions));
        }

        if (sharedExpectationsDiffer)
        {
            warnings.Add("SharedActionsHaveDifferentCompleteCommandExpectations");
        }

        if (coverageStatus != "Complete")
        {
            results.Clear();
            warnings.Add("ActionCoverageLimitExceeded");
        }

        var exampleSetId = PolicyJson.Fingerprint(new
        {
            Version = 1,
            input.Layer,
            input.Platform,
            Shell = shell,
            RequestedShell = input.Shell,
            WorkingDirectory = directory,
            Examples = examples,
            AllowedExecutables = allowedExecutables,
            Bindings = results.Select(result => new
            {
                result.Id,
                result.PreparationStatus,
                result.OuterResolvedExecutable,
                Actions = result.Actions.Select(action => action.BindingId).ToArray(),
            }).ToArray(),
            CoverageStatus = coverageStatus,
        });
        var validated = parseStatus == "Succeeded" && compileStatus == "Succeeded" && coverageStatus == "Complete"
            && results.Count == examples.Length && results.All(result => result.PreparationStatus == "Succeeded" && result.Matched)
            && results.All(result => result.OuterEligibility != "Ineligible");
        var status = coverageStatus != "Complete" ? "CoverageIncomplete"
            : parseStatus == "Failed" ? "ParsingFailed"
            : compileStatus == "Failed" ? "CompilationFailed"
            : results.Any(result => result.PreparationStatus != "Succeeded") ? "PreparationFailed"
            : input.PolicyJson is null ? "PreparationOnly"
            : validated ? "ValidatedCandidate" : "OutcomeMismatch";
        var output = new CommandPolicyValidationResult(candidateDigest, exampleSetId, input.Platform, shell, directory, parseStatus, compileStatus, coverageStatus, status, validated, examples.Length, results, warnings, diagnostics);
        cancellationToken.ThrowIfCancellationRequested();
        return BoundOutput(output, maximumOutputBytes);
    }

    /// <inheritdoc />
    protected override void ValidateInput(CommandPolicyValidationRequest input) => ValidateRequest(input);

    private static string CandidateUnavailableReason(string parseStatus, string compileStatus) =>
        parseStatus == "NotSupplied" ? "CandidateNotSupplied"
        : parseStatus == "Failed" ? "CandidateParseFailed"
        : compileStatus == "Failed" ? "CandidateCompileFailed" : "CandidateUnavailable";

    private static CommandPolicyValidationResult BoundOutput(CommandPolicyValidationResult output, int maximumOutputBytes)
    {
        if (maximumOutputBytes > 0 && JsonSerializer.SerializeToUtf8Bytes(output).Length <= maximumOutputBytes)
        {
            return output;
        }

        var limited = output with
        {
            CoverageStatus = "OutputLimitExceeded",
            Status = "CoverageIncomplete",
            IsValidated = false,
            Results = [],
            Diagnostics = [],
            Warnings = [.. output.Warnings, "ResultsOmittedBecauseOutputLimitExceeded"],
        };
        if (maximumOutputBytes <= 0 || JsonSerializer.SerializeToUtf8Bytes(limited).Length > maximumOutputBytes)
        {
            throw new ToolExecutionException("CommandPolicyValidationOutputLimitExceeded", ToolErrorClassification.OutputLimitExceeded);
        }

        return limited;
    }

    private static void ValidateRequest(CommandPolicyValidationRequest input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var hostPlatform = OperatingSystem.IsWindows() ? CommandPolicyNames.Windows
            : OperatingSystem.IsMacOS() ? CommandPolicyNames.MacOS : CommandPolicyNames.Linux;
        Require(input.Layer is "machine" or "user", "InvalidCandidateLayer");
        Require(input.Platform == hostPlatform, "ValidationRequiresCurrentHostPlatform");
        ValidateText(input.Shell, 1024, "InvalidShell");
        ValidateText(input.WorkingDirectory, 4096, "InvalidWorkingDirectory");
        Require(
            Path.IsPathFullyQualified(input.WorkingDirectory) && Directory.Exists(input.WorkingDirectory),
            "WorkingDirectoryMustBeAnExistingAbsoluteDirectory");
        var shell = Path.GetFileNameWithoutExtension(input.Shell).ToLowerInvariant();
        Require(
            shell is CommandPolicyNames.WindowsPowerShell or CommandPolicyNames.PowerShell or CommandPolicyNames.Cmd
                or CommandPolicyNames.Bash or CommandPolicyNames.Sh or CommandPolicyNames.Zsh,
            "UnsupportedShell");
        Require(OperatingSystem.IsWindows() || shell is not (CommandPolicyNames.Cmd or CommandPolicyNames.WindowsPowerShell), "UnsupportedHostShellPair");
        Require(Path.IsPathFullyQualified(input.Shell) || input.Shell.IndexOfAny(['/', '\\', ':']) < 0, "ShellMustBeABareNameOrAbsolutePath");
        if (input.PolicyJson is not null)
        {
            ValidateText(input.PolicyJson, MaximumCandidateBytes, "InvalidCandidateJson");
            Require(Utf8Length(input.PolicyJson) <= MaximumCandidateBytes, "CandidateJsonTooLarge");
        }

        ArgumentNullException.ThrowIfNull(input.Examples);
        Require(input.Examples.Count is > 0 and <= MaximumExamples, "InvalidExampleCount");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var commandBytes = 0;
        foreach (var example in input.Examples)
        {
            ArgumentNullException.ThrowIfNull(example);
            ValidateText(example.Id, 64, "InvalidExampleId");
            Require(
                example.Id.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-') && ids.Add(example.Id),
                "InvalidOrDuplicateExampleId");
            ValidateText(example.Command, MaximumCommandCharacters, "InvalidExampleCommand");
            commandBytes += Utf8Length(example.Command);
            Require(commandBytes <= MaximumCommandBytes, "CommandTextTooLarge");
            Require(
                example.ExpectedOutcome is nameof(PolicyOutcome.Allow) or nameof(PolicyOutcome.AlwaysDeny)
                    or nameof(PolicyOutcome.ApprovalRequired) or nameof(PolicyOutcome.ApprovalRequiredSession)
                    or nameof(PolicyOutcome.ApprovalAlwaysRequired),
                "InvalidExpectedOutcome");
        }

        if (input.AllowedExecutables is { } allowed)
        {
            Require(allowed.Count <= 128, "TooManyOuterExecutables");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in allowed)
            {
                ValidateText(name, 128, "InvalidOuterExecutable");
                Require(
                    name is not ("." or "..")
                        && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')
                        && names.Add(name),
                    "InvalidOrDuplicateOuterExecutable");
            }
        }
    }

    private static void ValidateText(string value, int maximumCharacters, string reason) =>
        Require(!string.IsNullOrWhiteSpace(value) && value.Length <= maximumCharacters && !value.Contains('\0'), reason);

    private static int Utf8Length(string value)
    {
        try
        {
            return StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ToolArgumentValidationException("InvalidUtf8Input", exception);
        }
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition)
        {
            throw new ToolArgumentValidationException(reason);
        }
    }
}
