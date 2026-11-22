namespace Threadsmith.Extensions.Runtime;

using System.Text.Json;
using Threadsmith.Extensions.Abstractions;
using Threadsmith.Tools;

/// <summary>Tracks resolved credentials for the lifetime of a generation's output boundaries.</summary>
internal sealed class ExtensionSecretProtection
{
    private const string _protectedMessage = "[protected extension log]";
    private readonly Dictionary<string, ProtectedValue> _values = new(StringComparer.Ordinal);
    private readonly HashSet<Scope> _readers = [];
    private readonly Lock _gate = new();
    private bool _retired;

    /// <summary>Registers a credential before its first reveal.</summary>
    internal void Protect(string value)
    {
        lock (_gate)
        {
            if (_values.TryGetValue(value, out var current))
            {
                _values[value] = current with { Owners = current.Owners + 1 };
            }
            else
            {
                if (_values.Count >= 256)
                {
                    throw new InvalidOperationException("ExtensionSecretProtectionCapacity");
                }

                _values.Add(value, new ProtectedValue(JsonEncodedText.Encode(value).ToString(), 1, 0));
            }

            foreach (var reader in _readers)
            {
                reader.Pin(value);
            }
        }
    }

    /// <summary>Indicates whether this generation has handled credentials.</summary>
    internal bool HasValues
    {
        get
        {
            lock (_gate)
            {
                return _retired || _values.Count > 0;
            }
        }
    }

    /// <summary>Checks output text for any credential registered by this generation.</summary>
    internal bool Contains(string? text)
    {
        lock (_gate)
        {
            return text is not null && _values.Any(value => text.Contains(value.Key, StringComparison.Ordinal) || text.Contains(value.Value.Encoded, StringComparison.Ordinal));
        }
    }

    /// <summary>Protects free-form contributor projections, including retained components after retirement.</summary>
    internal string SafeMessage(string message)
    {
        return SafeLogMessage(message);
    }

    /// <summary>Protects free-form logs, which may embed JSON using unknown escaping conventions.</summary>
    internal string SafeLogMessage(string message)
    {
        // JSON escapes require a backslash. Without a declared format, even a JSON fragment
        // embedded in ordinary text may encode a credential, so withhold ambiguous messages.
        lock (_gate)
        {
            // Retained generation loggers can outlive every invocation. Once values have been
            // forgotten, withhold their free-form text instead of retaining credential history.
            return _retired || ContainsDiagnostic(message) ? _protectedMessage : message;
        }
    }

    /// <summary>Checks free-form diagnostics without assuming a particular escaping convention.</summary>
    internal bool ContainsDiagnostic(string? text)
    {
        return text is not null && ((text.Contains('\\') && HasValues) || Contains(text));
    }

    /// <summary>Creates an owner, optionally pinning current and newly registered values until a callback finishes.</summary>
    internal Scope CreateScope(bool pinActiveValues = false)
    {
        lock (_gate)
        {
            var scope = new Scope(this);
            if (pinActiveValues)
            {
                foreach (var value in _values.Where(pair => pair.Value.Owners > 0).Select(pair => pair.Key).ToArray())
                {
                    scope.Pin(value);
                }

                _readers.Add(scope);
            }

            return scope;
        }
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

    /// <summary>Encoded active credential and its invocation/boundary owner count.</summary>
    private sealed record ProtectedValue(string Encoded, int Owners, int Pins);

    /// <summary>Keeps each distinct credential alive until its owning output boundary has finished.</summary>
    internal sealed class Scope : IDisposable
    {
        private readonly ExtensionSecretProtection _owner;
        private readonly HashSet<string> _values = new(StringComparer.Ordinal);
        private readonly HashSet<string> _pins = new(StringComparer.Ordinal);
        private bool _disposed;

        /// <summary>Initializes a new instance of the <see cref="Scope"/> class.</summary>
        internal Scope(ExtensionSecretProtection owner)
        {
            _owner = owner;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            lock (_owner._gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _owner._readers.Remove(this);
                foreach (var value in _values)
                {
                    var current = _owner._values[value];
                    Release(value, current with { Owners = current.Owners - 1 });
                }

                foreach (var value in _pins)
                {
                    var current = _owner._values[value];
                    Release(value, current with { Pins = current.Pins - 1 });
                }

                _values.Clear();
                _pins.Clear();
            }
        }

        /// <summary>Pins an overlapping credential under the owner's lock without registering a new invocation.</summary>
        internal void Pin(string value)
        {
            if (_pins.Add(value))
            {
                var current = _owner._values[value];
                _owner._values[value] = current with { Pins = current.Pins + 1 };
            }
        }

        /// <summary>Registers before reveal, atomically with respect to scope closure.</summary>
        internal void Protect(string value)
        {
            lock (_owner._gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_values.Contains(value))
                {
                    _owner.Protect(value);
                    _values.Add(value);
                }
            }
        }

        private void Release(string value, ProtectedValue remaining)
        {
            if (remaining.Owners == 0 && remaining.Pins == 0)
            {
                _owner._retired = true;
                _owner._values.Remove(value);
            }
            else
            {
                _owner._values[value] = remaining;
            }
        }
    }
}

/// <summary>Binds resolver authority to one admitted execution and its retirement signal.</summary>
internal sealed class ExtensionSecretAccess : IExtensionSecretAccess, IDisposable
{
    private readonly ISecretResolver _resolver;
    private readonly IReadOnlySet<string> _references;
    private readonly ToolExecutionContext _context;
    private readonly ExtensionSecretProtection.Scope _protection;
    private readonly string _component;
    private readonly CancellationTokenSource? _deadline;
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationToken _revoked;
    private int _disposed;

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
        _protection = protection.CreateScope();
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Revoke();
        _lifetime.Dispose();
        _deadline?.Dispose();
        _protection.Dispose();
    }

    /// <summary>Ends resolver authority while retaining credentials through result/catch sanitization.</summary>
    internal void Revoke()
    {
        _lifetime.Cancel();
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
