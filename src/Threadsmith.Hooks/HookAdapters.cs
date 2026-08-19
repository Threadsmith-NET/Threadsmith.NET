namespace Threadsmith.Hooks;

using System.Net;
using System.Text;
using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Tools;

/// <summary>Invokes JSON-over-standard-stream executable handlers through the tracked process manager.</summary>
public sealed class ExecutableHookAdapter : IHookHandlerAdapter
{
    private readonly IProcessManager _processManager;
    private readonly string _workingDirectory;

    /// <summary>Initializes a new instance of the <see cref="ExecutableHookAdapter"/> class.</summary>
    public ExecutableHookAdapter(IProcessManager processManager, string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(processManager);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        _processManager = processManager;
        _workingDirectory = Path.GetFullPath(workingDirectory);
    }

    /// <inheritdoc />
    public HookAdapterKind Kind => HookAdapterKind.Executable;

    /// <inheritdoc />
    public async Task<HookHandlerResult> InvokeAsync(
        HookHandlerDescriptor descriptor,
        HookInvocationEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.SecretReferences.Count > 0)
        {
            return new HookFailureResult("unsupported-secret-binding", "Executable hook secret bindings require an explicit named environment/stdin mapping.");
        }

        var inputBytes = JsonSerializer.SerializeToUtf8Bytes(envelope);
        if (inputBytes.Length > descriptor.Limits.MaximumInputBytes)
        {
            return new HookFailureResult("input-too-large", "The hook envelope exceeded the configured input bound.");
        }

        ProcessExecutionResult process = await _processManager.RunAsync(
            new ProcessExecutionRequest
            {
                ToolInvocationId = new ToolInvocationId(envelope.InvocationId.Value),
                RunId = envelope.RunId ?? default,
                FileName = descriptor.Target,
                WorkingDirectory = _workingDirectory,
                Timeout = descriptor.Limits.Timeout,
                MaximumOutputCharacters = descriptor.Limits.MaximumOutputBytes,
                StandardInput = Encoding.UTF8.GetString(inputBytes),
                Origin = ProcessRequestOrigin.Host,
            },
            cancellationToken);
        if (process.TimedOut)
        {
            throw new OperationCanceledException("Executable hook timed out.", cancellationToken);
        }

        if (process.StandardOutputTruncated || Encoding.UTF8.GetByteCount(process.StandardOutput) > descriptor.Limits.MaximumOutputBytes)
        {
            return new HookFailureResult("output-too-large", "The executable hook response exceeded its output bound.");
        }

        if (process.ExitCode != 0)
        {
            return new HookFailureResult("process-failed", "The executable hook exited unsuccessfully.");
        }

        return DeserializeResult(process.StandardOutput);
    }

    /// <summary>Deserializes one closed, versioned hook response.</summary>
    internal static HookHandlerResult DeserializeResult(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<HookHandlerResult>(json)
            ?? throw new InvalidDataException("The hook response was empty or malformed.");
    }
}

/// <summary>Invokes bounded HTTPS JSON hook endpoints without automatic redirects.</summary>
public sealed class HttpHookAdapter : IHookHandlerAdapter
{
    private readonly HttpClient _httpClient;
    private readonly ISecretResolver? _secretResolver;

    /// <summary>Initializes a new instance of the <see cref="HttpHookAdapter"/> class.</summary>
    public HttpHookAdapter(HttpClient httpClient, ISecretResolver? secretResolver = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
        _secretResolver = secretResolver;
    }

    /// <summary>Initializes a new instance of the <see cref="HttpHookAdapter"/> class for legacy hosts and tests.</summary>
    public HttpHookAdapter(HttpClient httpClient, ISecretStore secretStore)
        : this(httpClient, new LegacySecretStoreResolver(secretStore))
    {
    }

    /// <inheritdoc />
    public HookAdapterKind Kind => HookAdapterKind.Http;

    /// <inheritdoc />
    public async Task<HookHandlerResult> InvokeAsync(
        HookHandlerDescriptor descriptor,
        HookInvocationEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(envelope);
        if (!Uri.TryCreate(descriptor.Target, UriKind.Absolute, out Uri? target)
            || (target.Scheme != Uri.UriSchemeHttps
                && !(target.Scheme == Uri.UriSchemeHttp && IPAddress.TryParse(target.Host, out IPAddress? address) && IPAddress.IsLoopback(address))))
        {
            return new HookFailureResult("endpoint-policy", "HTTP hook endpoints require HTTPS, except for literal loopback development endpoints.");
        }

        var body = JsonSerializer.SerializeToUtf8Bytes(envelope);
        if (body.Length > descriptor.Limits.MaximumInputBytes)
        {
            return new HookFailureResult("input-too-large", "The hook envelope exceeded the configured input bound.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, target)
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new("application/json");
        if (envelope.SecretReferences.Count > 0)
        {
            if (_secretResolver is null || envelope.SecretReferences.Count != 1)
            {
                return new HookFailureResult("secret-scope-unavailable", "HTTP hook authentication requires one explicitly scoped secret reference.");
            }

            if (!SecretReference.TryParse(envelope.SecretReferences[0], out SecretReference? reference) || reference is null)
            {
                return new HookFailureResult("secret-reference-invalid", "The scoped hook credential reference is invalid.");
            }

            var resolutionRequest = new SecretResolutionRequest
            {
                Reference = reference,
                ComponentId = "hooks:http",
                Purpose = "authenticate a trusted managed HTTP hook",
                MinimumTrust = SecretProviderTrust.UserOwned,
            };
            SecretResolutionResult resolution = await _secretResolver.ResolveAsync(resolutionRequest, cancellationToken);
            if (!resolution.Succeeded)
            {
                return new HookFailureResult(
                    "secret-unavailable",
                    $"The scoped hook credential is unavailable ({resolution.Failure}).");
            }

            request.Headers.Authorization = new("Bearer", resolution.Value?.Reveal());
        }

        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            return new HookFailureResult("redirect-denied", "HTTP hook redirects are not followed.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var transient = response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
                || (int)response.StatusCode >= 500;
            return new HookFailureResult("http-failure", "The HTTP hook endpoint returned an unsuccessful status.", transient);
        }

        if (response.Content.Headers.ContentLength > descriptor.Limits.MaximumOutputBytes)
        {
            return new HookFailureResult("output-too-large", "The HTTP hook response exceeded its output bound.");
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var bounded = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (bounded.Length + read > descriptor.Limits.MaximumOutputBytes)
            {
                return new HookFailureResult("output-too-large", "The HTTP hook response exceeded its output bound.");
            }

            await bounded.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return ExecutableHookAdapter.DeserializeResult(Encoding.UTF8.GetString(bounded.ToArray()));
    }
}

/// <summary>Adapts an already-connected MCP capability through a host-provided policy-governed invoker.</summary>
public sealed class McpHookAdapter : IHookHandlerAdapter
{
    private readonly Func<HookHandlerDescriptor, HookInvocationEnvelope, CancellationToken, Task<HookHandlerResult>> _invoke;

    /// <summary>Initializes a new instance of the <see cref="McpHookAdapter"/> class.</summary>
    public McpHookAdapter(Func<HookHandlerDescriptor, HookInvocationEnvelope, CancellationToken, Task<HookHandlerResult>> invoke)
    {
        ArgumentNullException.ThrowIfNull(invoke);
        _invoke = invoke;
    }

    /// <inheritdoc />
    public HookAdapterKind Kind => HookAdapterKind.Mcp;

    /// <inheritdoc />
    public Task<HookHandlerResult> InvokeAsync(HookHandlerDescriptor descriptor, HookInvocationEnvelope envelope, CancellationToken cancellationToken = default)
    {
        return _invoke(descriptor, envelope, cancellationToken);
    }
}

/// <summary>Adapts a leased extension lifecycle capability through a host-provided registry invoker.</summary>
public sealed class ExtensionHookAdapter : IHookHandlerAdapter
{
    private readonly Func<HookHandlerDescriptor, HookInvocationEnvelope, CancellationToken, Task<HookHandlerResult>> _invoke;

    /// <summary>Initializes a new instance of the <see cref="ExtensionHookAdapter"/> class.</summary>
    public ExtensionHookAdapter(Func<HookHandlerDescriptor, HookInvocationEnvelope, CancellationToken, Task<HookHandlerResult>> invoke)
    {
        ArgumentNullException.ThrowIfNull(invoke);
        _invoke = invoke;
    }

    /// <inheritdoc />
    public HookAdapterKind Kind => HookAdapterKind.Extension;

    /// <inheritdoc />
    public Task<HookHandlerResult> InvokeAsync(HookHandlerDescriptor descriptor, HookInvocationEnvelope envelope, CancellationToken cancellationToken = default)
    {
        return _invoke(descriptor, envelope, cancellationToken);
    }
}
