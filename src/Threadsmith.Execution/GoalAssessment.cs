namespace Threadsmith.Execution;

using System.Text;
using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Models;

/// <summary>One bounded, chronological piece of goal evidence.</summary>
internal sealed record GoalEvidence(int Id, string Kind, string Text, bool Omitted, GoalReceipt? Receipt = null, ConversationMessageId? ArchivedAnswer = null);

/// <summary>Compact invocation facts independent of the clipped output excerpt.</summary>
internal sealed record GoalReceipt(Guid Invocation, string Tool, string Arguments, string Outcome, bool Succeeded, bool Check, string? Target = null, string? Filter = null)
{
    /// <summary>Kind of action confirmed by the typed tool adapter.</summary>
    public string? ActionKind { get; init; }

    /// <summary>Exact fully qualified name of the executed test when available.</summary>
    public string? ExecutedTest { get; init; }

    /// <summary>Limits of the source identity that the check actually observed.</summary>
    public string? SourceProof { get; init; }

    /// <summary>Whether this assessment can use the receipt only as historical evidence.</summary>
    public bool Historical { get; init; }

    /// <summary>Originating run for source applicability.</summary>
    internal RunId Run { get; init; }

    /// <summary>Workspace captured by the check owner.</summary>
    internal string? Workspace { get; init; }

    /// <summary>Baseline observed when the check started.</summary>
    internal DateTimeOffset? Baseline { get; init; }

    /// <summary>Repository revision observed by the check owner.</summary>
    internal string? Revision { get; init; }

    /// <summary>Whether execution could invalidate earlier source observations.</summary>
    internal bool CanChangeFiles { get; init; }

    /// <summary>Tests whether this observation can describe an action in the current run and workspace.</summary>
    internal bool IsHistorical(RunId currentRun, WorkspaceId? workspace, WorkspaceBaseline? baseline) =>
        Historical || (Outcome == "PolicyDenied" && Run != currentRun) || (Check && (Run != currentRun || Workspace != workspace?.Value.ToString()
            || Baseline != baseline?.CapturedAt || Revision != baseline?.GitRevision
            || (SourceProof is not null && (baseline is null || Baseline is null))));

    /// <summary>Indicates that a prior check cannot establish the post-edit state.</summary>
    internal bool MayHaveEdited => CanChangeFiles || (Tool == "edit_source" && (Succeeded || Outcome == "OutcomeUnknown"));
}

/// <summary>Evidence belonging to one live goal, independent of the model's compaction history.</summary>
internal sealed class GoalJournal
{
    private readonly Queue<GoalEvidence> _items = new();
    private readonly List<(GoalReceipt Receipt, GoalEvidence Entry)> _receipts = [];
    private readonly Lock _gate = new();
    private int _sequence;
    private bool _sensitive;

    /// <summary>Signals that this revision no longer owns the session goal.</summary>
    internal TaskCompletionSource Replaced { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Retains only recent model-visible text and labels incomplete excerpts.</summary>
    internal void Add(string kind, string text, bool sensitive, ConversationMessageId? archivedAnswer = null)
    {
        lock (_gate)
        {
            _sensitive |= sensitive;
            if (_items.Count == 16)
            {
                _items.Dequeue();
            }

            const int limit = 2048;
            _items.Enqueue(new GoalEvidence(++_sequence, kind, Excerpt(text, limit), text.Length > limit, ArchivedAnswer: text.Length is > limit and <= 8192 ? archivedAnswer : null));
        }
    }

    /// <summary>Retains action identity and outcome before optional evidence storage or user cancellation.</summary>
    internal void AddReceipt(GoalReceipt receipt, string excerpt, bool truncated)
    {
        lock (_gate)
        {
            _sensitive = true;
            if (_receipts.Count == 32)
            {
                var lastEdit = _receipts.FindLastIndex(item => item.Receipt.MayHaveEdited);
                var candidates = Enumerable.Range(0, _receipts.Count).Where(i => i != lastEdit || receipt.MayHaveEdited);
                var index = candidates.MinBy(i => Priority(_receipts[i].Receipt));
                if (Priority(receipt) < Priority(_receipts[index].Receipt))
                {
                    _sequence++;
                    return;
                }

                _receipts.RemoveAt(index);
            }

            const int limit = 1024;
            var text = Excerpt(excerpt, limit);
            _receipts.Add((receipt, new(++_sequence, $"tool:{receipt.Tool}:{receipt.Outcome}", text, truncated || excerpt.Length > limit, receipt)));
        }
    }

    /// <summary>Captures a stable evidence boundary for a single request.</summary>
    internal (GoalEvidence[] Evidence, bool Sensitive, bool Omitted) Capture()
    {
        lock (_gate)
        {
            return (_items.Concat(_receipts.Select(item => item.Entry)).OrderBy(item => item.Id).ToArray(), _sensitive, _sequence > _items.Count + _receipts.Count);
        }
    }

    /// <summary>Keeps both ends of a deliverable within the assessment allowance.</summary>
    internal static string Excerpt(string text, int limit)
    {
        if (text.Length <= limit)
        {
            return text;
        }

        const string separator = " … ";
        var head = (limit - separator.Length) / 2;
        var tail = text.Length - (limit - separator.Length - head);
        return string.Concat(
            text.AsSpan(0, char.IsHighSurrogate(text[head - 1]) ? head - 1 : head),
            separator,
            text.AsSpan(char.IsLowSurrogate(text[tail]) ? tail + 1 : tail));
    }

    private static int Priority(GoalReceipt receipt) => receipt.MayHaveEdited || (receipt.Check && receipt.Succeeded) ? 2 : receipt.Succeeded ? 1 : 0;
}

/// <summary>Validated model output. Only the foreground loop can apply it.</summary>
internal sealed record GoalVerdict(string Verdict, string Summary, string Reason, string NextStep)
{
    private static readonly string[] _sequenceSeparators = ["and then", "and", "then"];

    /// <summary>Gets the cited goal clauses with their proof selectors.</summary>
    internal GoalRequirement[] Requirements { get; init; } = [];

    /// <summary>A requested outcome and the evidence that supports it.</summary>
    internal sealed record GoalRequirement(string Clause, string Kind, string Tool, string Scope, int[] Evidence)
    {
        /// <summary>Separates an observed invocation from a claim about final source or built inputs.</summary>
        internal bool NeedsUnavailableSourceProof(GoalReceipt receipt) => receipt.Check
            && (Clause.Contains("current source", StringComparison.OrdinalIgnoreCase)
                || Clause.Contains("final source", StringComparison.OrdinalIgnoreCase)
                || Clause.Contains("latest source", StringComparison.OrdinalIgnoreCase)
                || Clause.Contains("current checkout", StringComparison.OrdinalIgnoreCase));

        /// <summary>Checks an invocation independently of the model's explanation.</summary>
        internal bool Matches(GoalReceipt receipt)
        {
            if (receipt.Tool != Tool || receipt.Historical || !receipt.Succeeded || Scope.Contains("[REDACTED]", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var instruction = Instruction(Clause);
            var words = instruction.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var targets = words.Select(word => word.Trim('`', '"', '\'', '.', ',', '(', ')')).Where(IsNamedTarget);
            if (targets.Any(target => !string.Equals(target, Scope, StringComparison.Ordinal)
                && target != receipt.Target && target != receipt.Filter && target != receipt.ExecutedTest))
            {
                return false;
            }

            var actions = SplitClauses(instruction)
                .Select(part => ExpectedAction(Instruction(part))).Where(action => action.HasValue).ToArray();
            if (actions.Length > 1)
            {
                return false;
            }

            var expected = actions.FirstOrDefault();
            if (expected is null && !words.Any(word => word.Trim('`', '"', '.', ',') == receipt.Tool))
            {
                return false;
            }

            if (expected is { } action && receipt.ActionKind != action.Kind
                && !(receipt.Tool == action.Tool && (receipt.Outcome == "PolicyDenied" || action.Kind is "read" or "search")))
            {
                return false;
            }

            // A single direct argument can be a lower-case target, e.g. "Build website".
            if (expected is not null && words.Length == 2 && !IsGenericTarget(words[1])
                && !string.Equals(words[1].Trim('`', '"', '.', ','), Scope, StringComparison.Ordinal))
            {
                return false;
            }

            if (Scope.Length == 0)
            {
                return !targets.Any();
            }

            if (!Clause.Contains(Scope, StringComparison.Ordinal))
            {
                return false;
            }

            if (receipt.Target is not null || receipt.Filter is not null || receipt.ExecutedTest is not null)
            {
                return Scope == receipt.Target || Scope == receipt.Filter || Scope == receipt.ExecutedTest;
            }

            try
            {
                using var args = JsonDocument.Parse(receipt.Arguments);
                return args.RootElement.ValueKind == JsonValueKind.Object && args.RootElement.EnumerateObject().Any(property =>
                    property.Name is "target" or "targetPath" or "project" or "path" or "filter" or "symbolId" or "command"
                    && property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() == Scope);
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }

    /// <summary>Validates an exact clause and its receipt selector against this assessment input.</summary>
    internal static GoalRequirement ParseRequirement(JsonElement item, string goal, GoalEvidence[] evidence)
    {
        var requirement = new GoalRequirement(item.GetProperty("clause").GetString() ?? string.Empty, item.GetProperty("kind").GetString() ?? string.Empty, item.GetProperty("tool").GetString() ?? string.Empty, item.GetProperty("scope").GetString() ?? string.Empty, [.. item.GetProperty("evidence").EnumerateArray().Select(value => value.GetInt32())]);
        if (string.IsNullOrWhiteSpace(requirement.Clause) || !goal.Contains(requirement.Clause, StringComparison.Ordinal)
            || requirement.Kind is not ("action" or "text") || (requirement.Kind == "action") == string.IsNullOrWhiteSpace(requirement.Tool)
            || requirement.Evidence.Any(id => !evidence.Any(entry => entry.Id == id)))
        {
            throw new InvalidDataException("Invalid goal requirement or action selector.");
        }

        return requirement;
    }

    /// <summary>Reads a complete text-only verdict without forwarding raw model output.</summary>
    internal static async Task<GoalVerdict> ReadAsync(IModelProvider provider, ModelStreamRequest request, string goal, GoalEvidence[] evidence, Action<ModelUsage> observeUsage, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        ModelFinishReason? finish = null;
        await foreach (var chunk in provider.StreamAsync(request, cancellationToken).WithCancellation(cancellationToken))
        {
            if (chunk.Usage is { } usage)
            {
                observeUsage(usage);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (chunk.Output is not null || chunk.GeneratedImage is not null || chunk.ResponseEnvelope is not null || chunk.Text?.Length > 16384 - text.Length)
            {
                throw new InvalidDataException("The goal assessor did not return a bounded text response.");
            }

            text.Append(chunk.Text);
            finish = chunk.FinishReason ?? finish;
        }

        if (finish != ModelFinishReason.Stop)
        {
            throw new InvalidDataException("The goal assessment did not finish normally.");
        }

        using var json = JsonDocument.Parse(text.ToString(), new JsonDocumentOptions { AllowDuplicateProperties = false, MaxDepth = 4 });
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 7 || root.GetProperty("version").GetInt32() != 2)
        {
            throw new InvalidDataException("Invalid goal assessment format.");
        }

        var result = new GoalVerdict(root.GetProperty("verdict").GetString() ?? string.Empty, root.GetProperty("summary").GetString() ?? string.Empty, root.GetProperty("reason").GetString() ?? string.Empty, root.GetProperty("nextStep").GetString() ?? string.Empty);
        var references = root.GetProperty("evidence");
        if (result.Verdict is not ("Continue" or "Met" or "PartiallyMet" or "CannotMeet")
            || string.IsNullOrWhiteSpace(result.Summary) || string.IsNullOrWhiteSpace(result.Reason)
            || ((result.Verdict == "Continue") == string.IsNullOrWhiteSpace(result.NextStep))
            || references.GetArrayLength() == 0 || references.GetArrayLength() > evidence.Length
            || references.EnumerateArray().Any(reference => !reference.TryGetInt32(out var id) || !evidence.Any(item => item.Id == id)))
        {
            throw new InvalidDataException("The goal assessor returned an unsupported verdict or evidence reference.");
        }

        var requirements = root.GetProperty("requirements").EnumerateArray().Select(item => ParseRequirement(item, goal, evidence)).ToArray();
        if (requirements.Length is < 1 or > 32)
        {
            throw new InvalidDataException("Missing or oversized goal requirement list.");
        }

        if (result.Verdict != "Continue")
        {
            var remaining = goal.AsSpan();
            foreach (var requirement in requirements)
            {
                var start = remaining.IndexOf(requirement.Clause, StringComparison.Ordinal);
                if (start < 0 || !IsSeparator(remaining[..start]))
                {
                    throw new InvalidDataException("The assessment did not account for the complete goal.");
                }

                remaining = remaining[(start + requirement.Clause.Length)..];
            }

            if (!IsSeparator(remaining))
            {
                throw new InvalidDataException("The assessment omitted a goal requirement.");
            }
        }

        var lastEdit = evidence.Where(entry => entry.Receipt is { MayHaveEdited: true }).Select(entry => entry.Id).DefaultIfEmpty().Max();
        foreach (var requirement in requirements)
        {
            var claimed = evidence.Where(entry => requirement.Evidence.Contains(entry.Id));
            var supported = requirement.Kind == "text"
                ? IsWritingRequest(requirement.Clause) && claimed.Any(entry => entry.Kind == "answer")
                : claimed.Any(entry => entry.Receipt is { } receipt && requirement.Matches(receipt)
                    && (!receipt.Check || (entry.Id >= lastEdit && !requirement.NeedsUnavailableSourceProof(receipt))));
            if ((result.Verdict == "Met" || requirement.Evidence.Length > 0) && !supported)
            {
                if (claimed.Any(entry => entry.Receipt is { Check: true } receipt && requirement.Matches(receipt with { Historical = false })))
                {
                    return new GoalVerdict("Continue", string.Empty, string.Empty, requirement.Clause)
                    {
                        Requirements = [requirement with { Evidence = [] }],
                    };
                }

                throw new InvalidDataException("The cited evidence does not confirm the required work on the current source.");
            }
        }

        if (result.Verdict is "PartiallyMet" or "CannotMeet")
        {
            var cited = references.EnumerateArray().Select(reference => reference.GetInt32()).ToHashSet();
            foreach (var missing in requirements.Where(requirement => requirement.Evidence.Length == 0))
            {
                var unavailable = missing.Kind == "action" && NeedsTestExecution(missing.Clause)
                    && evidence.Any(entry => entry.Kind == "no-working-tools" && cited.Contains(entry.Id));
                if (!unavailable && (missing.Kind != "action" || !evidence.Any(entry => cited.Contains(entry.Id)
                    && entry.Receipt is { Succeeded: false, Historical: false, Outcome: "PolicyDenied" } blocker
                    && missing.Matches(blocker with { Succeeded = true }))))
                {
                    throw new InvalidDataException("No matching action failure supports the claimed blocker.");
                }
            }
        }

        if ((result.Verdict == "Continue" && requirements.All(requirement => requirement.Evidence.Length != 0))
            || (result.Verdict == "CannotMeet" && requirements.Any(requirement => requirement.Evidence.Length != 0))
            || (result.Verdict == "PartiallyMet" && (!requirements.Any(requirement => requirement.Evidence.Length > 0) || requirements.All(requirement => requirement.Evidence.Length > 0))))
        {
            throw new InvalidDataException("The verdict conflicts with the supported requirements.");
        }

        return result with { Requirements = requirements };
    }

    private static bool IsNamedTarget(string word)
    {
        var value = word.Trim('`', '"', '\'', '.', ',', '(', ')');
        return value.Contains('/') || value.Contains('\\') || value.Contains('.')
            || (value.Length > 1 && value.Skip(1).Any(char.IsUpper));
    }

    private static bool IsWritingRequest(string clause)
    {
        var parts = SplitClauses(clause);
        if (parts.Length == 0 || parts.Any(part => ExpectedAction(Instruction(part)) is not null))
        {
            return false;
        }

        // An unrecognized imperative cannot be discharged by an assistant promise.
        var verb = Instruction(parts[0]).Split(' ', 2)[0].ToLowerInvariant();
        return verb is "display" or "show" or "write" or "explain" or "summarize" or "describe" or "identify" or "review"
            or "say" or "list" or "answer" or "tell" or "draft" or "what" or "why" or "how" or "give" or "return"
            or "translate" or "compare" or "calculate" or "assess" or "evaluate" or "compose" or "outline" or "name" or "report";
    }

    private static string[] SplitClauses(string clause)
    {
        foreach (var separator in _sequenceSeparators)
        {
            clause = clause.Replace($" {separator} ", ";", StringComparison.OrdinalIgnoreCase);
        }

        return clause.Split([';', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    private static string Instruction(string clause)
    {
        var instruction = clause.Trim();
        foreach (var prefix in new[] { "could you ", "can you ", "please " })
        {
            if (instruction.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                instruction = instruction[prefix.Length..].TrimStart();
            }
        }

        return instruction;
    }

    private static (string Kind, string Tool)? ExpectedAction(string instruction)
    {
        var verb = instruction.Split(' ', 2)[0].ToLowerInvariant();
        return verb switch
        {
            "test" => ("test", "test_run_targeted"),
            "run" or "execute" or "verify" or "ensure" or "check" when instruction.Contains("test", StringComparison.OrdinalIgnoreCase) => ("test", "test_run_targeted"),
            "run" or "execute" or "install" or "deploy" => ("process", "run_process"),
            "build" or "compile" => ("Build", "dotnet_build"),
            "edit" or "modify" or "update" or "patch" or "fix" or "delete" => ("edit", "edit_source"),
            "read" or "inspect" => ("read", "read_file"),
            "search" => ("search", "search"),
            "write" or "create" when instruction.Split(' ').Skip(1).Any(word => word.Contains('/') || word.Contains('\\')
                || (IsNamedTarget(word) && word.Contains('.'))) => ("edit", "edit_source"),
            _ => null,
        };
    }

    private static bool IsGenericTarget(string word) => word.Trim('.', ',').ToLowerInvariant() is
        "tests" or "it" or "this" or "everything" or "all";

    private static bool NeedsTestExecution(string clause) => ExpectedAction(Instruction(clause))?.Kind == "test";

    private static bool IsSeparator(ReadOnlySpan<char> text)
    {
        text = text.Trim();
        while (!text.IsEmpty && char.IsPunctuation(text[0]))
        {
            text = text[1..].TrimStart();
        }

        foreach (var separator in _sequenceSeparators)
        {
            if (text.Equals(separator, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return text.IsEmpty;
    }
}
