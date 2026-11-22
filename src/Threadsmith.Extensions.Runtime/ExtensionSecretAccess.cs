namespace Threadsmith.Extensions.Runtime;

using System.Text.Json;
using Threadsmith.Extensions.Abstractions;
using Threadsmith.Tools;

/// <summary>Tracks resolved credentials for the lifetime of a generation's output boundaries.</summary>
internal sealed class ExtensionSecretProtection
{
    private const string _protectedMessage = "[protected extension log]";
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>Registers a credential before its first reveal.</summary>
    internal void Protect(string value)
    {
        lock (_gate)
        {
            if (_values.ContainsKey(value))
            {
                return;
            }

            if (_values.Count >= 256)
            {
                throw new InvalidOperationException("ExtensionSecretProtectionCapacity");
            }

            _values.Add(value, JsonEncodedText.Encode(value).ToString());
        }
    }

    /// <summary>Indicates whether this generation has handled credentials.</summary>
    internal bool HasValues
    {
        get
        {
            lock (_gate)
            {
                return _values.Count > 0;
            }
        }
    }

    /// <summary>Checks output text for any credential registered by this generation.</summary>
    internal bool Contains(string? text)
    {
        lock (_gate)
        {
            return text is not null && _values.Any(value => text.Contains(value.Key, StringComparison.Ordinal) || text.Contains(value.Value, StringComparison.Ordinal));
        }
    }

    /// <summary>Removes exact protected values from extension diagnostics.</summary>
    internal string SafeMessage(string message)
    {
        return Contains(message) ? _protectedMessage : message;
    }

    /// <summary>Protects free-form logs, which may embed JSON using unknown escaping conventions.</summary>
    internal string SafeLogMessage(string message)
    {
        // JSON escapes require a backslash. Without a declared format, even a JSON fragment
        // embedded in ordinary text may encode a credential, so withhold ambiguous messages.
        return ContainsDiagnostic(message) ? _protectedMessage : message;
    }

    /// <summary>Checks free-form diagnostics without assuming a particular escaping convention.</summary>
    internal bool ContainsDiagnostic(string? text)
    {
        return text is not null && ((text.Contains('\\') && HasValues) || Contains(text));
    }

    /// <summary>Checks decoded property names and values before result serialization.</summary>
    internal bool Contains(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().Any(property => Contains(property.Name) || Contains(property.Value)),
            JsonValueKind.Array => element.EnumerateArray().Any(Contains),
            JsonValueKind.String => Contains(element.GetString()),
            _ => Contains(element.GetRawText()),
        };
    }
}

/// <summary>Binds resolver authority to one admitted execution and its retirement signal.</summary>
internal sealed class ExtensionSecretAccess : IExtensionSecretAccess, IDisposable
{
    private readonly ISecretResolver _resolver;
    private readonly IReadOnlySet<string> _references;
    private readonly ToolExecutionContext _context;
    private readonly ExtensionSecretProtection _protection;
    private readonly string _component;
    private readonly CancellationTokenSource? _deadline;
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationToken _revoked;

    /// <summary>Initializes a new instance of the <see cref="ExtensionSecretAccess"/> class.</summary>
    internal ExtensionSecretAccess(
        ISecretResolver resolver,
        IReadOnlySet<string> references,
        ToolExecutionContext context,
        ExtensionSecretProtection protection,
        string extensionId,
        CancellationToken retirement,
        CancellationToken cancellationToken)
    {
        _resolver = resolver;
        _references = references;
        _context = context;
        _protection = protection;
        _component = SecretResolutionRequest.CreateConfiguredComponentId("extension", extensionId);
        var expires = context.Deadline is { } admission && (context.ExecutionDeadline is null || admission < context.ExecutionDeadline)
            ? admission
            : context.ExecutionDeadline;
        if (expires is { } absolute)
        {
            var remaining = absolute - context.TimeProvider.GetUtcNow();
            _deadline = new CancellationTokenSource(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, context.TimeProvider);
        }

        _lifetime = _deadline is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, retirement)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, retirement, _deadline.Token);
        _revoked = _lifetime.Token;
    }

    /// <inheritdoc />
    public async Task<ExtensionSecretValue> ResolveAsync(string reference, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _revoked);
        Validate(linked.Token);
        if (!SecretReference.TryParse(reference, out var parsed) || parsed is null || !_references.Contains(parsed.CanonicalName))
        {
            throw new UnauthorizedAccessException("ExtensionSecretNotAdmitted");
        }

        var request = new SecretResolutionRequest
        {
            Reference = parsed,
            ComponentId = _component,
            RepositoryRoot = _context.Invocation.RepositoryPath,
            MinimumTrust = SecretProviderTrust.UserOwned,
            Purpose = "Extension tool execution",
        };
        var result = await _resolver.ResolveAsync(request, linked.Token).WaitAsync(linked.Token);
        Validate(linked.Token);
        if (result.Value is not { } value)
        {
            throw new InvalidOperationException("ExtensionSecretResolutionFailed");
        }

        var credential = value.Reveal();
        _protection.Protect(credential);
        Validate(linked.Token);
        return new ExtensionSecretValue(credential);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        _deadline?.Dispose();
    }

    private void Validate(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_context.Deadline <= _context.TimeProvider.GetUtcNow() || _context.ExecutionDeadline <= _context.TimeProvider.GetUtcNow())
        {
            throw new TimeoutException("ExtensionSecretDeadlineExpired");
        }

        if (_context.Invocation.SecurityPolicy is { } policy)
        {
            _context.Invocation.PolicyBinding?.Validate(policy);
        }
    }
}
