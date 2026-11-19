namespace Threadsmith.Tools;

using System.Text;
using Threadsmith.Core;

/// <summary>Caller-owned state shared with nested tool admission for one model round.</summary>
internal sealed record ScriptCaller(IConversationToolSnapshotStore? Snapshots, ToolCallHistory History, Action? Reserve = null, Action<ToolInvocationResult>? Charge = null, Action<string>? Accepted = null, Func<string, string>? Normalize = null, Func<ToolInvocationResult, IEnumerable<ToolProvenanceSource>>? ReadSources = null);

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

    private bool IsNormallyEnabled(ToolRegistration expected)
    {
        try
        {
            return ToolRegistrationIdentity.Matches(_registry.GetRegistration(expected.Tool.Definition.Id), expected);
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

        var remaining = ScriptWorkerProtocol.ResultBytes;
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

                    var size = Encoding.UTF8.GetByteCount(result.ResultJson ?? string.Empty) + (long)Encoding.UTF8.GetByteCount(result.Error ?? string.Empty);
                    if (size > remaining)
                    {
                        return new(false, null, true, "OutputLimitExceeded", "Script result budget exhausted.");
                    }

                    caller.Charge?.Invoke(result);
                    remaining -= (int)size;
                    if (result.Succeeded && id == "read_file" && caller.ReadSources is { } readSources)
                    {
                        sources.AddRange(readSources(result));
                    }

                    return new(result.Succeeded, result.ResultJson, result.IsTruncated, result.ErrorClassification.ToString(), result.Error);
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
