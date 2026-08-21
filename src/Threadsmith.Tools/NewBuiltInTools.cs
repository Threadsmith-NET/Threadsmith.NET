namespace Threadsmith.Tools;

using System.Reflection;
using System.Text.Json;
using Threadsmith.Core;

/// <summary>Empty input for the current date/time tool.</summary>
public sealed record DateTimeInput;

/// <summary>Current UTC and local clock values with timezone metadata.</summary>
public sealed record DateTimeOutput(
    string UtcNow,
    string LocalNow,
    string TimeZoneId,
    string OffsetFromUtc);

/// <summary>Returns the current UTC and host-local date/time.</summary>
public sealed class DateTimeTool : Tool<DateTimeInput, DateTimeOutput>
{
    private static readonly ToolDefinition _definition = ToolDefinitionFactory
        .Create<DateTimeInput, DateTimeOutput>(
            "datetime",
            "Returns the current UTC and local date/time with timezone information.",
            ToolCategory.SystemInformation,
            RepositoryTrustLevel.UntrustedInspection,
            ApprovalLevel.None,
            ToolSideEffect.ReadOnly,
            TimeSpan.FromSeconds(2),
            8 * 1024) with
    {
        DisplayName = "Date/Time",
    };

    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new instance of the <see cref="DateTimeTool"/> class.</summary>
    public DateTimeTool(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public override ToolDefinition Definition => _definition;

    /// <inheritdoc />
    public override Task<ToolExecution<DateTimeOutput>> ExecuteAsync(
        DateTimeInput input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var utcNow = _timeProvider.GetUtcNow();
        var timezone = TimeZoneInfo.Local;
        var localNow = TimeZoneInfo.ConvertTime(utcNow, timezone);
        var output = new DateTimeOutput(
            utcNow.ToString("O"),
            localNow.ToString("O"),
            timezone.Id,
            localNow.Offset.ToString("c"));
        return Task.FromResult(new ToolExecution<DateTimeOutput>(output, []));
    }

    /// <inheritdoc />
    protected override void ValidateInput(DateTimeInput input)
    {
    }
}

/// <summary>Supported C# script input forms.</summary>
public enum ScriptKind
{
    /// <summary>A C# expression whose value is returned.</summary>
    Expression,

    /// <summary>A C# statement sequence whose final return value is returned.</summary>
    Statement,
}

/// <summary>Input for bounded isolated C# script execution.</summary>
public sealed record CSharpScriptInput
{
    /// <summary>C# expression or statement sequence.</summary>
    public required string Code { get; init; }

    /// <summary>Input form: <c>expression</c> or <c>statement</c>.</summary>
    public string Kind { get; init; } = "expression";
}

/// <summary>Bounded C# script outcome.</summary>
public sealed record CSharpScriptOutput(
    bool Success,
    string? Output,
    string? Error,
    int ExecutionMs,
    bool IsTruncated);

/// <summary>Executes C# in a fresh isolated worker process.</summary>
public interface ICSharpScriptEngine
{
    /// <summary>Executes one expression or statement sequence.</summary>
    Task<CSharpScriptOutput> ExecuteAsync(
        string code,
        ScriptKind kind,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>Tracked-process adapter for the Roslyn scripting worker.</summary>
public sealed class CSharpScriptEngine : ICSharpScriptEngine
{
    private const int _maximumConfiguredOutputBytes = 1024 * 1024;
    private const int _maximumConfiguredTimeoutMilliseconds = 30000;
    private const int _minimumConfiguredOutputBytes = 256;
    private const int _minimumConfiguredTimeoutMilliseconds = 100;
    private readonly IProcessManager _processManager;
    private readonly IToolConfig _toolConfig;
    private readonly string _workerPath;

    /// <summary>Initializes a new instance of the <see cref="CSharpScriptEngine"/> class.</summary>
    public CSharpScriptEngine(
        IProcessManager processManager,
        IToolConfig toolConfig,
        string workerPath)
    {
        ArgumentNullException.ThrowIfNull(processManager);
        ArgumentNullException.ThrowIfNull(toolConfig);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerPath);
        _processManager = processManager;
        _toolConfig = toolConfig;
        _workerPath = Path.GetFullPath(workerPath);
    }

    /// <inheritdoc />
    public async Task<CSharpScriptOutput> ExecuteAsync(
        string code,
        ScriptKind kind,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentNullException.ThrowIfNull(context);
        var timeoutMilliseconds = _toolConfig.Get("csharp_script", "timeout_ms", 5000);
        var maximumOutputBytes = _toolConfig.Get("csharp_script", "max_output_bytes", 65536);
        var allowedAssemblies = _toolConfig.Get(
            "csharp_script",
            "allowed_assemblies",
            "System.Linq,System.Collections,System.Collections.Generic");
        if (allowedAssemblies.Length > 4096)
        {
            throw new InvalidOperationException("csharp_script allowed_assemblies is limited to 4096 characters.");
        }

        if (timeoutMilliseconds is < _minimumConfiguredTimeoutMilliseconds or > _maximumConfiguredTimeoutMilliseconds)
        {
            throw new InvalidOperationException(
                $"csharp_script timeout_ms must be between {_minimumConfiguredTimeoutMilliseconds} and {_maximumConfiguredTimeoutMilliseconds}.");
        }

        if (maximumOutputBytes is < _minimumConfiguredOutputBytes or > _maximumConfiguredOutputBytes)
        {
            throw new InvalidOperationException(
                $"csharp_script max_output_bytes must be between {_minimumConfiguredOutputBytes} and {_maximumConfiguredOutputBytes}.");
        }

        if (!File.Exists(_workerPath))
        {
            throw new FileNotFoundException("The C# scripting worker is unavailable.", _workerPath);
        }

        var workerRequest = new ScriptWorkerRequest
        {
            Code = code,
            Kind = kind.ToString(),
            MaximumOutputBytes = maximumOutputBytes,
            AllowedAssemblies = allowedAssemblies
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        };
        var requestJson = JsonSerializer.Serialize(workerRequest);
        var launchAppHost = !string.Equals(
            Path.GetExtension(_workerPath),
            ".dll",
            StringComparison.OrdinalIgnoreCase);
        var entryAssemblyPath = Assembly.GetEntryAssembly()?.Location
            ?? throw new InvalidOperationException("The host entry assembly path is unavailable.");
        IReadOnlyList<string> arguments = launchAppHost
            ? []
            :
            [
                "exec",
                "--runtimeconfig",
                Path.ChangeExtension(entryAssemblyPath, ".runtimeconfig.json"),
                "--depsfile",
                Path.ChangeExtension(entryAssemblyPath, ".deps.json"),
                _workerPath,
            ];
        var result = await _processManager.RunAsync(
            new ProcessExecutionRequest
            {
                ToolInvocationId = context.ToolInvocationId,
                RunId = context.RunId,
                FileName = launchAppHost ? _workerPath : "dotnet",
                Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(_workerPath)
                    ?? throw new InvalidOperationException("The scripting worker path has no parent directory."),
                Timeout = TimeSpan.FromMilliseconds(timeoutMilliseconds),
                MaximumOutputCharacters = checked((maximumOutputBytes * 6) + 4096),
                StandardInput = requestJson,
                Origin = ProcessRequestOrigin.Host,
            },
            cancellationToken);
        if (result.TimedOut)
        {
            return new CSharpScriptOutput(
                false,
                null,
                $"Script execution exceeded {timeoutMilliseconds} ms and the worker was terminated.",
                (int)Math.Min(result.Duration.TotalMilliseconds, int.MaxValue),
                false);
        }

        if (result.ExitCode != 0 || result.StandardOutputTruncated)
        {
            var error = string.IsNullOrWhiteSpace(result.StandardError)
                ? $"The C# scripting worker exited with code {result.ExitCode}."
                : result.StandardError;
            return new CSharpScriptOutput(
                false,
                null,
                error,
                (int)Math.Min(result.Duration.TotalMilliseconds, int.MaxValue),
                result.StandardOutputTruncated || result.StandardErrorTruncated);
        }

        try
        {
            return JsonSerializer.Deserialize<CSharpScriptOutput>(
                result.StandardOutput,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("The C# scripting worker returned an empty result.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The C# scripting worker returned malformed output.", exception);
        }
    }

    private sealed record ScriptWorkerRequest
    {
        public required string Code { get; init; }

        public required string Kind { get; init; }

        public int MaximumOutputBytes { get; init; }

        public required IReadOnlyList<string> AllowedAssemblies { get; init; }
    }
}

/// <summary>Runs a bounded C# expression or statement sequence in an isolated worker.</summary>
public sealed class CSharpScriptTool : Tool<CSharpScriptInput, CSharpScriptOutput>
{
    private static readonly ToolDefinition _definition = ToolDefinitionFactory
        .Create<CSharpScriptInput, CSharpScriptOutput>(
            "csharp_script",
            "Compiles and executes bounded C# in an isolated worker process.",
            ToolCategory.CodeExecution,
            RepositoryTrustLevel.FullyTrustedAutomation,
            ApprovalLevel.None,
            ToolSideEffect.ExecutesCode,
            TimeSpan.FromSeconds(35),
            7 * 1024 * 1024) with
    {
        DisplayName = "C# Script",
        EnabledByDefault = false,
        Idempotency = ToolIdempotency.NonIdempotent,
    };

    private readonly ICSharpScriptEngine _engine;

    /// <summary>Initializes a new instance of the <see cref="CSharpScriptTool"/> class.</summary>
    public CSharpScriptTool(ICSharpScriptEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
    }

    /// <inheritdoc />
    public override ToolDefinition Definition => _definition;

    /// <inheritdoc />
    public override async Task<ToolExecution<CSharpScriptOutput>> ExecuteAsync(
        CSharpScriptInput input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var kind = Enum.Parse<ScriptKind>(input.Kind, ignoreCase: true);
        var output = await _engine.ExecuteAsync(
            input.Code,
            kind,
            context,
            cancellationToken);
        return new ToolExecution<CSharpScriptOutput>(output, [], output.IsTruncated);
    }

    /// <inheritdoc />
    protected override void ValidateInput(CSharpScriptInput input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Code);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Kind);
        if (input.Code.Length > 64 * 1024)
        {
            throw new ToolArgumentValidationException("code is limited to 65536 characters.");
        }

        if (!Enum.TryParse(input.Kind, ignoreCase: true, out ScriptKind kind)
            || !Enum.IsDefined(kind))
        {
            throw new ToolArgumentValidationException("kind must be 'expression' or 'statement'.");
        }
    }

    /// <inheritdoc />
    protected override string? GetExecutable(CSharpScriptInput input)
    {
        return "dotnet";
    }
}
