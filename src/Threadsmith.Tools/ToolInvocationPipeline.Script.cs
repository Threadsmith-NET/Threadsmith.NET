namespace Threadsmith.Tools;

using Threadsmith.Core;

/// <summary>Caller-owned state shared with nested tool admission for one model round.</summary>
internal sealed record ScriptCaller(IConversationToolSnapshotStore? Snapshots, ToolCallHistory History, Action? Reserve = null, Action<ToolInvocationResult>? Charge = null, Action<string>? Accepted = null, Func<string, string>? Normalize = null, Func<ToolInvocationResult, IEnumerable<ToolProvenanceSource>>? ReadSources = null);

/// <summary>Host-only accounting performed after a nested reply fits the exchange budget.</summary>
internal sealed record PendingScriptReply(ToolInvocationResult Result, Action Commit)
    : ScriptToolReply(Result.Succeeded, Result.ResultJson, Result.IsTruncated, Result.ErrorClassification.ToString(), Result.Error);

public sealed partial class ToolInvocationPipeline
{
    /// <summary>Checks the current caller policy and registration identity for nested script calls.</summary>
    internal bool IsScriptCallable(ToolRegistration registration, ToolInvocationContext context)
    {
        var definition = registration.Tool.Definition;
        return registration.Source.Kind == ToolActivitySourceKind.BuiltIn
            && definition.VisibleToScriptingHost
            && definition.Id != "csharp_script"
            && (!definition.RequiresWorkspace || context.WorkspaceId is not null)
            && IsNormallyEnabled(registration)
            && ConversationToolAvailability.IsAdvertised(definition, context);
    }

    /// <summary>Checks that the captured outer script remains available to this caller.</summary>
    internal bool IsScriptHostAvailable(ToolRegistration registration, ToolInvocationContext context) =>
        registration.Implementation is CSharpScriptTool
        && context.Scratchpad is { IsActive: true, RootPath: not null }
        && IsNormallyEnabled(registration, context)
        && ConversationToolAvailability.IsAdvertised(registration.Tool.Definition, context);

    private bool IsNormallyEnabled(ToolRegistration expected, ToolInvocationContext? context = null)
    {
        try
        {
            var id = expected.Tool.Definition.Id;
            return ToolRegistrationIdentity.Matches(context is null ? _registry.GetRegistration(id) : _registry.GetRegistration(id, context), expected);
        }
        catch (KeyNotFoundException)
        {
            return false;
        }
    }

    private Dictionary<string, Func<string, CancellationToken, Task<ScriptToolReply>>> CreateScriptHandlers(ToolInvocationRequest parent, List<ToolProvenanceSource> sources)
    {
        var handlers = new Dictionary<string, Func<string, CancellationToken, Task<ScriptToolReply>>>(StringComparer.Ordinal);
        var context = parent.Context;
        if (context.ScriptCaller is not { Snapshots: { } snapshots } caller || context.ModelVisibleToolSnapshotId is not { } snapshot)
        {
            return handlers;
        }

        IReadOnlyList<ToolRegistration> saved;
        try
        {
            saved = snapshots.Resolve(snapshot, parent.SessionId, parent.RunId);
        }
        catch (InvalidOperationException)
        {
            return handlers;
        }

        foreach (var registration in saved)
        {
            var definition = ToolRuntimePolicy.ApplyCeiling(registration.Tool, context.SecurityPolicy).Definition;
            var id = definition.Id;
            if (!IsScriptCallable(registration, context))
            {
                continue;
            }

            handlers.Add(id, async (arguments, token) =>
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    _ = snapshots.Resolve(snapshot, parent.SessionId, parent.RunId);
                    if (!IsNormallyEnabled(registration) || !ConversationToolAvailability.IsAdvertised(definition, context))
                    {
                        return new(false, null, false, "Denied", "Script tool is no longer available.");
                    }

                    caller.Reserve?.Invoke();
                    var key = Guid.NewGuid().ToString("N");
                    var request = parent with
                    {
                        ToolId = id,
                        ArgumentsJson = arguments,
                        ExpectedRegistration = registration,
                        InvocationKey = key,
                        Completion = null,
                        Context = context with { IsScriptOrigin = true, ScriptCaller = null, ScriptHandlers = null },
                    };
                    var preflight = PreflightBatch([new ToolBatchRequest(0, key, request)]);
                    if (!preflight.Succeeded || preflight.Preparation is not { } prepared)
                    {
                        return new(false, null, false, preflight.ErrorClassification.ToString(), preflight.SafeReason);
                    }

                    var normalized = caller.Normalize?.Invoke(arguments) ?? arguments;
                    lock (caller.History)
                    {
                        if (!caller.History.TryAdd(definition, normalized))
                        {
                            return new(false, null, false, "Denied", "Identical tool invocation already admitted.");
                        }

                        caller.Accepted?.Invoke(id);
                    }

                    var result = (await InvokePreparedBatchAsync(prepared, token)).Single().Result;
                    token.ThrowIfCancellationRequested();
                    lock (caller.History)
                    {
                        caller.History.ObserveResult(definition, normalized, result);
                    }

                    return new PendingScriptReply(result, () =>
                    {
                        caller.Charge?.Invoke(result);
                        if (result.Succeeded && caller.ReadSources is { } readSources)
                        {
                            sources.AddRange(readSources(result));
                        }
                    });
                }
                catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException or KeyNotFoundException)
                {
                    token.ThrowIfCancellationRequested();
                    return new(false, null, false, "Denied", "The call or result exceeded the caller's authority or budget.");
                }
            });
        }

        return handlers;
    }
}
