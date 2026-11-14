namespace Threadsmith.Skills;

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Threadsmith.Core;
using Threadsmith.Tools;

/// <summary>Checks the maintained authoring contract at the existing procedure boundaries.</summary>
internal sealed class CommandPolicyAuthoringPolicy
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly string? _directory;
    private readonly List<(string Command, string ExpectedOutcome)> _retainedExamples = [];
    private CommandPolicyValidationRequest? _request;
    private CommandPolicyValidationResult? _observation;

    /// <summary>Initializes a new instance of the <see cref="CommandPolicyAuthoringPolicy"/> class.</summary>
    internal CommandPolicyAuthoringPolicy(string inputJson, string? repositoryPath)
    {
        ValidateInput(inputJson);
        using var input = JsonDocument.Parse(inputJson);
        _directory = input.RootElement.TryGetProperty("workingDirectory", out var directory)
            ? directory.GetString() : repositoryPath;
        if (input.RootElement.TryGetProperty("examples", out var examples))
        {
            _retainedExamples.AddRange(ReadExamples(examples));
        }
    }

    /// <summary>Identifies the maintained data contract, not an alternate execution route.</summary>
    internal static bool Applies(SkillScope scope, string skillId) =>
        scope == SkillScope.Maintained && skillId == "command-policy-authoring";

    /// <summary>Gets the host platform used by the ordinary validator.</summary>
    internal static string HostPlatform => OperatingSystem.IsWindows() ? "windows"
        : OperatingSystem.IsMacOS() ? "macos" : "linux";

    /// <summary>Enforces nonblank values, absolute supplied paths, and aggregate text limits.</summary>
    internal static void ValidateInput(string json)
    {
        using var input = JsonDocument.Parse(json);
        ValidateStrings(input.RootElement);
        if (input.RootElement.TryGetProperty("workingDirectory", out var directory))
        {
            var path = directory.GetString();
            Require(
                path is not null && Path.IsPathFullyQualified(path) && Directory.Exists(path),
                "A supplied workingDirectory must be an existing absolute directory.");
        }

        if (input.RootElement.TryGetProperty("examples", out var examples))
        {
            Require(
                examples.EnumerateArray().Sum(example => Encoding.UTF8.GetByteCount(example.GetProperty("command").GetString() ?? string.Empty)) <= 64 * 1024,
                "Aggregate example command text exceeds 64 KiB UTF-8.");
        }
    }

    /// <summary>Supplies explicit host context before normal tool preflight and invocation.</summary>
    internal string BindArguments(string argumentsJson)
    {
        var arguments = JsonNode.Parse(argumentsJson)?.AsObject()
            ?? throw new InvalidDataException("Validator arguments must be an object.");
        Require(!string.IsNullOrWhiteSpace(_directory), "Working directory context is unavailable. Supply workingDirectory through skill input or report incomplete; do not guess a directory.");
        var directoryKey = arguments.Select(item => item.Key).SingleOrDefault(key => key.Equals("workingDirectory", StringComparison.OrdinalIgnoreCase)) ?? "workingDirectory";
        arguments[directoryKey] = _directory;

        var platformKey = arguments.Select(item => item.Key).SingleOrDefault(key => key.Equals("platform", StringComparison.OrdinalIgnoreCase)) ?? "platform";
        arguments[platformKey] = HostPlatform;
        return arguments.ToJsonString();
    }

    /// <summary>Keeps only the latest completed attempt; failures invalidate prior checks.</summary>
    internal void Observe(string argumentsJson, ToolInvocationResult? result)
    {
        _request = null;
        _observation = null;
        if (result is not { Succeeded: true, ResultJson: { } json })
        {
            return;
        }

        try
        {
            var request = JsonSerializer.Deserialize<CommandPolicyValidationRequest>(argumentsJson, JsonOptions);
            var observation = JsonSerializer.Deserialize<CommandPolicyValidationResult>(json, JsonOptions);
            if (request is null || observation is null || observation.Results is null || observation.Warnings is null
                || request.Examples is null || observation.Results.Count != request.Examples.Count
                || observation.ExampleCount != request.Examples.Count)
            {
                return;
            }

            // Sanitization or omitted results cannot turn altered evidence into a checked draft.
            for (var index = 0; index < request.Examples.Count; index++)
            {
                var expected = request.Examples[index];
                var actual = observation.Results[index];
                if (expected.Id != actual.Id || expected.Command != actual.Command || expected.ExpectedOutcome != actual.ExpectedOutcome)
                {
                    return;
                }
            }

            foreach (var example in request.Examples)
            {
                var retained = (example.Command, example.ExpectedOutcome);
                if (!_retainedExamples.Contains(retained))
                {
                    _retainedExamples.Add(retained);
                }
            }

            _request = request;
            _observation = observation;
        }
        catch (JsonException)
        {
            // A bounded error/coverage result is not validation evidence. The model can report incomplete.
        }
    }

    /// <summary>Accepts an exact replacement example set only from an answered user clarification.</summary>
    internal void ObserveAnswer(ToolInvocationResult result, Func<string, string> validateInput)
    {
        if (!result.Succeeded || result.ResultJson is not { } json)
        {
            return;
        }

        _request = null;
        _observation = null;
        var answer = JsonSerializer.Deserialize<UserQuestionResult>(json, JsonOptions);
        if (answer?.Status != UserQuestionStatus.Answered)
        {
            return;
        }

        foreach (var item in answer.Answers)
        {
            if (item.Text is not { } text)
            {
                continue;
            }

            try
            {
                using var revision = JsonDocument.Parse(text);
                if (revision.RootElement.ValueKind != JsonValueKind.Object
                    || !revision.RootElement.TryGetProperty("examples", out var examples))
                {
                    continue;
                }

                var input = JsonSerializer.Serialize(new { request = "Example revision", examples });
                _ = validateInput(input);
                ValidateInput(input);
                var retained = ReadExamples(examples).ToArray();
                _retainedExamples.Clear();
                _retainedExamples.AddRange(retained);
                _request = null;
                _observation = null;
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException)
            {
                // Ordinary prose and malformed revisions do not redefine the checked example set.
            }
        }
    }

    /// <summary>Rejects fabricated, obsolete, altered, or incomplete evidence through output correction.</summary>
    internal void ValidateOutput(string json)
    {
        using var document = JsonDocument.Parse(json);
        var output = document.RootElement;
        ValidateStrings(output);
        var validated = output.GetProperty("status").GetString() == "validated";
        var hasValidation = output.TryGetProperty("validation", out var validation);
        Require(!validated || hasValidation, "Validated output requires observed validator evidence.");
        if (!hasValidation)
        {
            Require(!validated, "An unchecked draft cannot be validated.");
            Require(
                _request?.PolicyJson is not { } checkedPolicy || _observation is null
                || (output.TryGetProperty("policyJson", out var draft) && draft.GetString() != checkedPolicy),
                "Preserve current validator evidence and warnings when returning the checked draft, including incomplete results.");
            return;
        }

        var request = _request;
        var observation = _observation;
        Require(request is not null && observation is not null, "No complete current validator observation supports this output.");
        if (request is null || observation is null)
        {
            return;
        }

        Require(
            output.TryGetProperty("policyJson", out var policy) && policy.GetString() == request.PolicyJson && request.PolicyJson is not null,
            "Returned policyJson must equal the exact latest checked candidate text.");
        Require(
            validation.GetProperty("candidateDigest").GetString() == observation.CandidateDigest
            && validation.GetProperty("exampleSetId").GetString() == observation.ExampleSetId,
            "Returned validation identities differ from the current tool observation.");
        var results = validation.GetProperty("results");
        Require(results.GetArrayLength() == observation.Results.Count, "All retained example results must be reported.");
        for (var index = 0; index < observation.Results.Count; index++)
        {
            var expected = observation.Results[index];
            var actual = results[index];
            var hasOutcome = actual.TryGetProperty("actualOutcome", out var outcome);
            Require(
                actual.GetProperty("command").GetString() == expected.Command
                && actual.GetProperty("expectedOutcome").GetString() == expected.ExpectedOutcome
                && hasOutcome == (expected.ActualOutcome is not null)
                && (!hasOutcome || outcome.GetString() == expected.ActualOutcome)
                && actual.GetProperty("matched").GetBoolean() == expected.Matched
                && (expected.ActualOutcome is not null || !expected.Matched),
                "Returned example facts differ from the completed validator observation.");
        }

        var warnings = output.GetProperty("warnings").EnumerateArray().Select(item => item.GetString()).ToArray();
        Require(
            observation.Warnings.All(warning => warnings.Contains(warning, StringComparer.Ordinal)),
            "Preserve observed scope and coverage warnings in the warnings array.");
        if (validated)
        {
            Require(
                observation.IsValidated && observation.ParseStatus == "Succeeded" && observation.CompileStatus == "Succeeded"
                && observation.CoverageStatus == "Complete" && observation.ExampleCount > 0
                && observation.Results.All(item => item.PreparationStatus == "Succeeded" && item.Matched
                    && item.OuterEligibility != "Ineligible" && (item.ExpectedOutcome != "AlwaysDeny" || item.ReasonCode == "PolicyExplicitDenial")),
                "The current observation is not a complete successful isolated candidate check.");
            var remaining = request.Examples.Select(item => (item.Command, item.ExpectedOutcome)).ToList();
            foreach (var retained in _retainedExamples)
            {
                Require(remaining.Remove(retained), "Validation must retain every agreed command and expected outcome; obtain an explicit user revision or report incomplete.");
            }

            Require(
                output.GetProperty("outstandingItems").GetArrayLength() == 0,
                "Validated output cannot contain outstanding items.");
        }
        else if (observation.Results.Any(item => item.ActualOutcome is null || !item.Matched))
        {
            Require(
                output.GetProperty("outstandingItems").GetArrayLength() > 0,
                "Incomplete checks require an outstanding-item explanation.");
        }
    }

    private static IEnumerable<(string Command, string ExpectedOutcome)> ReadExamples(JsonElement examples) =>
        examples.EnumerateArray().Select(example => (example.GetProperty("command").GetString() ?? string.Empty,
            example.GetProperty("expectedOutcome").GetString() ?? string.Empty));

    private static void ValidateStrings(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            Require(!string.IsNullOrWhiteSpace(value.GetString()), "Supplied strings must not be blank.");
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                ValidateStrings(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                ValidateStrings(item);
            }
        }
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition)
        {
            throw new InvalidDataException(reason);
        }
    }
}
