namespace Threadsmith.Tools;

using Microsoft.Extensions.Configuration;
using Threadsmith.Core;

/// <summary>Model-visible presentation formats supported by the built-in code_explore tool.</summary>
public enum CodeExploreOutputFormat
{
    /// <summary>Return the host-owned structured JSON result.</summary>
    Structured,

    /// <summary>Return a bounded Markdown presentation derived from the structured result.</summary>
    Markdown,
}

/// <summary>Immutable code_explore output preferences effective for one host session.</summary>
public sealed record CodeExploreOutputSnapshot(
    SessionId SessionId,
    CodeExploreOutputFormat OutputFormat,
    bool InspectCodeExploreOutput);

/// <summary>Gets code_explore output preferences for one host session.</summary>
public sealed record GetCodeExploreOutputOptionsCommand(SessionId SessionId)
    : ICommand<CodeExploreOutputSnapshot>;

/// <summary>Sets the model-visible code_explore output format for one host session.</summary>
public sealed record SetCodeExploreOutputFormatCommand(
    SessionId SessionId,
    CodeExploreOutputFormat OutputFormat)
    : ICommand<CodeExploreOutputSnapshot>;

/// <summary>Sets whether TUI tool-completion blocks include code_explore output for one host session.</summary>
public sealed record SetCodeExploreOutputInspectionCommand(
    SessionId SessionId,
    bool InspectCodeExploreOutput)
    : ICommand<CodeExploreOutputSnapshot>;

/// <summary>Host-owned code_explore output options with session-scoped overrides.</summary>
public sealed class CodeExploreOutputOptions :
    ICommandHandler<GetCodeExploreOutputOptionsCommand, CodeExploreOutputSnapshot>,
    ICommandHandler<SetCodeExploreOutputFormatCommand, CodeExploreOutputSnapshot>,
    ICommandHandler<SetCodeExploreOutputInspectionCommand, CodeExploreOutputSnapshot>
{
    private readonly Lock _gate = new();
    private readonly CodeExploreOutputFormat _configuredOutputFormat;
    private readonly bool _configuredInspectCodeExploreOutput;
    private readonly Dictionary<SessionId, CodeExploreSessionOutputOverrides> _sessionOverrides = [];

    /// <summary>Initializes a new instance of the <see cref="CodeExploreOutputOptions" /> class.</summary>
    public CodeExploreOutputOptions(
        CodeExploreOutputFormat configuredOutputFormat = CodeExploreOutputFormat.Markdown,
        bool configuredInspectCodeExploreOutput = false)
    {
        if (!Enum.IsDefined(configuredOutputFormat))
        {
            throw new ArgumentOutOfRangeException(nameof(configuredOutputFormat));
        }

        _configuredOutputFormat = configuredOutputFormat;
        _configuredInspectCodeExploreOutput = configuredInspectCodeExploreOutput;
    }

    /// <summary>Gets the effective model-visible output format for one host session.</summary>
    public CodeExploreOutputFormat GetOutputFormat(SessionId sessionId)
    {
        return GetSnapshot(sessionId).OutputFormat;
    }

    /// <summary>Gets whether future TUI code_explore completion blocks should include output for one host session.</summary>
    public bool GetInspectCodeExploreOutput(SessionId sessionId)
    {
        return GetSnapshot(sessionId).InspectCodeExploreOutput;
    }

    /// <summary>Gets the effective code_explore output preferences for one host session.</summary>
    public CodeExploreOutputSnapshot GetSnapshot(SessionId sessionId)
    {
        lock (_gate)
        {
            return CreateSnapshot(sessionId, GetOverridesOrDefault(sessionId));
        }
    }

    /// <summary>Sets the session-only output format override.</summary>
    public CodeExploreOutputSnapshot SetSessionOutputFormat(
        SessionId sessionId,
        CodeExploreOutputFormat outputFormat)
    {
        ValidateSessionId(sessionId);
        if (!Enum.IsDefined(outputFormat))
        {
            throw new ArgumentOutOfRangeException(nameof(outputFormat));
        }

        lock (_gate)
        {
            var overrides = GetOverridesOrDefault(sessionId) with
            {
                OutputFormat = outputFormat,
            };
            _sessionOverrides[sessionId] = overrides;
            return CreateSnapshot(sessionId, overrides);
        }
    }

    /// <summary>Sets the session-only TUI inspection override.</summary>
    public CodeExploreOutputSnapshot SetSessionInspectCodeExploreOutput(
        SessionId sessionId,
        bool inspectCodeExploreOutput)
    {
        ValidateSessionId(sessionId);
        lock (_gate)
        {
            var overrides = GetOverridesOrDefault(sessionId) with
            {
                InspectCodeExploreOutput = inspectCodeExploreOutput,
            };
            _sessionOverrides[sessionId] = overrides;
            return CreateSnapshot(sessionId, overrides);
        }
    }

    /// <summary>Creates code_explore output options from layered normal configuration.</summary>
    public static CodeExploreOutputOptions FromConfiguration(IConfiguration? configuration)
    {
        var inspect = configuration?.GetValue("tools:codeExplore:inspectCodeExploreOutput", false) ?? false;
        return new CodeExploreOutputOptions(configuredInspectCodeExploreOutput: inspect);
    }

    /// <summary>Parses a bounded user/configuration output-format value.</summary>
    public static bool TryParseOutputFormat(
        string? value,
        out CodeExploreOutputFormat format)
    {
        if (string.Equals(value, "markdown", StringComparison.OrdinalIgnoreCase))
        {
            format = CodeExploreOutputFormat.Markdown;
            return true;
        }

        if (string.Equals(value, "structured", StringComparison.OrdinalIgnoreCase))
        {
            format = CodeExploreOutputFormat.Structured;
            return true;
        }

        format = CodeExploreOutputFormat.Structured;
        return false;
    }

    /// <inheritdoc />
    public Task<CodeExploreOutputSnapshot> HandleAsync(
        GetCodeExploreOutputOptionsCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSessionId(command.SessionId);
        return Task.FromResult(GetSnapshot(command.SessionId));
    }

    /// <inheritdoc />
    public Task<CodeExploreOutputSnapshot> HandleAsync(
        SetCodeExploreOutputFormatCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(SetSessionOutputFormat(command.SessionId, command.OutputFormat));
    }

    /// <inheritdoc />
    public Task<CodeExploreOutputSnapshot> HandleAsync(
        SetCodeExploreOutputInspectionCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(SetSessionInspectCodeExploreOutput(
            command.SessionId,
            command.InspectCodeExploreOutput));
    }

    private static void ValidateSessionId(SessionId sessionId)
    {
        if (sessionId == default)
        {
            throw new ArgumentException("A host session identity is required.", nameof(sessionId));
        }
    }

    private CodeExploreSessionOutputOverrides GetOverridesOrDefault(SessionId sessionId)
    {
        return _sessionOverrides.TryGetValue(sessionId, out var overrides)
            ? overrides
            : new CodeExploreSessionOutputOverrides(null, null);
    }

    private CodeExploreOutputSnapshot CreateSnapshot(
        SessionId sessionId,
        CodeExploreSessionOutputOverrides overrides)
    {
        return new CodeExploreOutputSnapshot(
            sessionId,
            overrides.OutputFormat ?? _configuredOutputFormat,
            overrides.InspectCodeExploreOutput ?? _configuredInspectCodeExploreOutput);
    }

    private sealed record CodeExploreSessionOutputOverrides(
        CodeExploreOutputFormat? OutputFormat,
        bool? InspectCodeExploreOutput);
}
