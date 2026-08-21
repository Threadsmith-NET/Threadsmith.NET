namespace Threadsmith.Mcp;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Tools;

/// <summary>Presents authorization in the system browser.</summary>
public sealed class SystemBrowserLauncher : IBrowserLauncher
{
    /// <inheritdoc />
    public Task LaunchAsync(Uri authorizationUri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorizationUri);
        cancellationToken.ThrowIfCancellationRequested();
        if (authorizationUri.Scheme != Uri.UriSchemeHttp
            && authorizationUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("OAuth authorization requires an HTTP or HTTPS URI.");
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = authorizationUri.AbsoluteUri,
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "The system browser could not be opened. Run headlessly to use copy-and-paste OAuth authorization.",
                exception);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Presents an authorization URI for copy-and-paste headless authentication.</summary>
public sealed class ConsoleBrowserLauncher : IBrowserLauncher
{
    private readonly TextWriter _output;

    /// <summary>Initializes a new instance of the <see cref="ConsoleBrowserLauncher"/> class.</summary>
    public ConsoleBrowserLauncher(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
    }

    /// <inheritdoc />
    public async Task LaunchAsync(Uri authorizationUri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorizationUri);
        cancellationToken.ThrowIfCancellationRequested();
        await _output.WriteLineAsync("Open this OAuth authorization URL in a browser:");
        await _output.WriteLineAsync(authorizationUri.AbsoluteUri);
        await _output.FlushAsync(cancellationToken);
    }
}

/// <summary>Receives OAuth callbacks from a localhost-only HTTP listener.</summary>
public sealed class LoopbackOAuthCallbackListener : IOAuthCallbackListener
{
    private const int MaximumHeaderBytes = 32 * 1024;
    private const int MaximumHeaderCount = 64;
    private const int MaximumLineBytes = 8 * 1024;
    private readonly ConcurrentDictionary<Uri, LoopbackReservation> _reservations = new();

    /// <inheritdoc />
    public Uri ReserveRedirectUri(int requestedPort)
    {
        var ipv4Listener = new TcpListener(IPAddress.Loopback, requestedPort);
        ipv4Listener.Start();
        var reservedPort = ((IPEndPoint)ipv4Listener.LocalEndpoint).Port;
        TcpListener? ipv6Listener = null;
        try
        {
            if (Socket.OSSupportsIPv6)
            {
                ipv6Listener = new TcpListener(IPAddress.IPv6Loopback, reservedPort);
                ipv6Listener.Start();
            }

            var redirectUri = new Uri($"http://localhost:{reservedPort}/callback", UriKind.Absolute);
            var reservation = new LoopbackReservation(ipv4Listener, ipv6Listener);
            if (!_reservations.TryAdd(redirectUri, reservation))
            {
                reservation.Stop();
                throw new InvalidOperationException("The OAuth callback redirect URI is already reserved.");
            }

            return redirectUri;
        }
        catch
        {
            ipv6Listener?.Stop();
            ipv4Listener.Stop();
            throw;
        }
    }

    /// <inheritdoc />
    public void ReleaseRedirectUri(Uri redirectUri)
    {
        ArgumentNullException.ThrowIfNull(redirectUri);
        if (_reservations.TryRemove(redirectUri, out var reservation))
        {
            reservation.Stop();
        }
    }

    /// <inheritdoc />
    public async Task<Uri> WaitForCallbackAsync(Uri redirectUri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(redirectUri);
        if (!redirectUri.IsLoopback || redirectUri.Scheme != Uri.UriSchemeHttp || redirectUri.Port <= 0)
        {
            throw new InvalidOperationException("OAuth callback listeners require an explicit HTTP localhost port.");
        }

        var reservation = TakeOrRenewReservation(redirectUri);

        try
        {
            using var client = await reservation.AcceptTcpClientAsync(cancellationToken);
            await using var stream = client.GetStream();

            var requestLine = await ReadAsciiLineAsync(stream, MaximumLineBytes, cancellationToken);
            var requestParts = requestLine?.Split(' ', 3) ?? [];

            if (requestParts.Length != 3
                || !string.Equals(requestParts[0], "GET", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The OAuth callback did not contain a valid HTTP GET request.");
            }

            var headerBytes = 0;
            for (var headerCount = 0; headerCount < MaximumHeaderCount; headerCount++)
            {
                var header = await ReadAsciiLineAsync(stream, MaximumLineBytes, cancellationToken)
                    ?? throw new InvalidOperationException(
                        "The OAuth callback ended before its HTTP headers were complete.");

                headerBytes += Encoding.ASCII.GetByteCount(header) + 2;
                if (headerBytes > MaximumHeaderBytes)
                {
                    throw new InvalidOperationException("The OAuth callback HTTP headers exceed the host bound.");
                }

                if (header.Length == 0)
                {
                    break;
                }

                if (headerCount == MaximumHeaderCount - 1)
                {
                    throw new InvalidOperationException("The OAuth callback contains too many HTTP headers.");
                }
            }

            var callbackUri = new Uri(redirectUri, requestParts[1]);
            var body = Encoding.UTF8.GetBytes(
                "<!doctype html><html><body><h1>Authentication complete</h1><p>You may close this window.</p></body></html>");
            var responseHeaders = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(responseHeaders, cancellationToken);
            await stream.WriteAsync(body, cancellationToken);
            return callbackUri;
        }
        finally
        {
            reservation.Stop();
        }
    }

    private LoopbackReservation TakeOrRenewReservation(Uri redirectUri)
    {
        if (_reservations.TryRemove(redirectUri, out var reservation))
        {
            return reservation;
        }

        // A live transport can authorize more than once. The first callback consumes its startup
        // reservation, so a later authorization must reacquire the same registered redirect URI.
        var renewedUri = ReserveRedirectUri(redirectUri.Port);
        if (renewedUri != redirectUri || !_reservations.TryRemove(renewedUri, out reservation))
        {
            ReleaseRedirectUri(renewedUri);
            throw new InvalidOperationException("The OAuth callback redirect URI could not be renewed.");
        }

        return reservation;
    }

    private static async Task<string?> ReadAsciiLineAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[maximumBytes];
        var count = 0;
        var singleByte = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(singleByte, cancellationToken);
            if (read == 0)
            {
                return count == 0 ? null : Encoding.ASCII.GetString(buffer, 0, count);
            }

            if (singleByte[0] == (byte)'\n')
            {
                if (count > 0 && buffer[count - 1] == (byte)'\r')
                {
                    count--;
                }

                return Encoding.ASCII.GetString(buffer, 0, count);
            }

            if (count == buffer.Length)
            {
                throw new InvalidOperationException("The OAuth callback contains an overlong HTTP line.");
            }

            buffer[count++] = singleByte[0];
        }
    }

    private sealed class LoopbackReservation
    {
        private readonly TcpListener[] _listeners;

        public LoopbackReservation(TcpListener ipv4Listener, TcpListener? ipv6Listener)
        {
            _listeners = ipv6Listener is null ? [ipv4Listener] : [ipv4Listener, ipv6Listener];
        }

        public async Task<TcpClient> AcceptTcpClientAsync(CancellationToken cancellationToken)
        {
            Task<TcpClient>[] pending =
            [
                .. _listeners.Select(listener => listener.AcceptTcpClientAsync(cancellationToken).AsTask()),
            ];

            var completed = await Task.WhenAny(pending);
            var acceptedClient = await completed;
            Stop();

            foreach (var task in pending)
            {
                if (task != completed)
                {
                    ObserveAndDispose(task);
                }
            }

            return acceptedClient;
        }

        public void Stop()
        {
            foreach (var listener in _listeners)
            {
                listener.Stop();
            }
        }

        private static void ObserveAndDispose(Task<TcpClient> task)
        {
            _ = task.ContinueWith(
                static completed =>
                {
                    if (completed.Status == TaskStatus.RanToCompletion)
                    {
                        completed.Result.Dispose();
                    }
                    else
                    {
                        _ = completed.Exception;
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}

/// <summary>Reads a complete pasted callback URI for headless OAuth authorization.</summary>
public sealed class ConsoleOAuthCallbackListener : IOAuthCallbackListener
{
    private readonly TextReader _input;
    private readonly TextWriter _output;

    /// <summary>Initializes a new instance of the <see cref="ConsoleOAuthCallbackListener"/> class.</summary>
    public ConsoleOAuthCallbackListener(TextReader input, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        _input = input;
        _output = output;
    }

    /// <inheritdoc />
    public Uri ReserveRedirectUri(int requestedPort)
    {
        if (requestedPort != 0)
        {
            return new Uri($"http://localhost:{requestedPort}/callback", UriKind.Absolute);
        }

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var selectedPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            return new Uri($"http://localhost:{selectedPort}/callback", UriKind.Absolute);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <inheritdoc />
    public async Task<Uri> WaitForCallbackAsync(Uri redirectUri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(redirectUri);

        await _output.WriteLineAsync("Paste the complete OAuth callback URL:");
        await _output.FlushAsync(cancellationToken);

        var value = await _input.ReadLineAsync(cancellationToken);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var callbackUri))
        {
            throw new InvalidOperationException("A valid absolute OAuth callback URL is required.");
        }

        return callbackUri;
    }
}

/// <summary>Adapts the existing host secret store to the OAuth token-cache contract.</summary>
public sealed class McpOAuthSecretStore : IMcpOAuthTokenStore
{
    private const string RemovedPrefixMarker = "threadsmith:removed-prefix:";
    private static readonly string[] LegacyGrantSuffixes =
    [
        "accessToken",
        "refreshToken",
        "obtainedAt",
        "expiresAt",
        "tokenType",
        "scope",
        "authorizationServer",
        "clientId",
        "clientSecret",
        "tokenEndpointAuthMethod",
        "redirectUri",
    ];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ISecretStore _secretStore;
    private readonly string _cachePath;
    private readonly ILogger<McpOAuthSecretStore> _logger;
    private ConcurrentDictionary<string, string> _tokens = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="McpOAuthSecretStore"/> class.</summary>
    public McpOAuthSecretStore(
        ISecretStore secretStore,
        string? cachePath = null,
        ILogger<McpOAuthSecretStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(secretStore);
        _secretStore = secretStore;
        _cachePath = cachePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".threadsmith",
            "mcp-oauth-tokens.json");
        _logger = logger ?? NullLogger<McpOAuthSecretStore>.Instance;
        LoadExisting();
    }

    /// <inheritdoc />
    public async Task<string?> GetAsync(string secretReference, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretReference);
        cancellationToken.ThrowIfCancellationRequested();

        var tokens = _tokens;

        if (IsRemovedByPrefix(tokens, secretReference))
        {
            return null;
        }

        return tokens.TryGetValue(secretReference, out var value)
            ? NullIfEmpty(value)
            : await _secretStore.GetAsync("secrets:" + secretReference, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, string>> GetSnapshotAsync(
        string secretReferencePrefix,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretReferencePrefix);
        cancellationToken.ThrowIfCancellationRequested();
        var tokens = _tokens;
        var snapshot = tokens
            .Where(pair => pair.Key.StartsWith(secretReferencePrefix, StringComparison.Ordinal))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (snapshot.Count > 0 || IsRemovedByPrefix(tokens, secretReferencePrefix))
        {
            return snapshot;
        }

        // The compatibility store has only field-level reads, so it cannot itself provide a
        // point-in-time grant. Perform a stable double-read while local mutations are serialized,
        // then atomically migrate the entire legacy grant. Never use fallback values to fill gaps
        // in a partial local grant.
        await _gate.WaitAsync(cancellationToken);
        try
        {
            tokens = _tokens;
            snapshot = tokens
                .Where(pair => pair.Key.StartsWith(secretReferencePrefix, StringComparison.Ordinal))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            if (snapshot.Count > 0 || IsRemovedByPrefix(tokens, secretReferencePrefix))
            {
                return snapshot;
            }

            var first = await ReadLegacyGrantAsync(secretReferencePrefix, cancellationToken);
            var second = await ReadLegacyGrantAsync(secretReferencePrefix, cancellationToken);
            if (!HaveSameValues(first, second))
            {
                throw new InvalidOperationException(
                    "The compatibility OAuth credential source changed while it was being migrated; retry the operation.");
            }

            if (second.Count == 0)
            {
                return second;
            }

            var migratedTokens = tokens.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal);
            foreach (var pair in second)
            {
                migratedTokens[pair.Key] = pair.Value;
            }

            await PersistLockedAsync(migratedTokens, cancellationToken);
            Interlocked.Exchange(
                ref _tokens,
                new ConcurrentDictionary<string, string>(migratedTokens, StringComparer.Ordinal));
            return second;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SetAsync(string secretReference, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretReference);
        ArgumentNullException.ThrowIfNull(value);
        await ApplyAsync(
            new McpOAuthTokenStoreMutation
            {
                Values = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [secretReference] = value,
                },
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task ApplyAsync(
        McpOAuthTokenStoreMutation mutation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        foreach (var pair in mutation.Values)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pair.Key);
            ArgumentNullException.ThrowIfNull(pair.Value);
        }

        foreach (var reference in mutation.RemovedReferences)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        }

        foreach (var prefix in mutation.RemovedPrefixes)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var modifiedTokens = _tokens.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal);

            foreach (var reference in mutation.RemovedReferences)
            {
                modifiedTokens.Remove(reference);
            }

            foreach (var prefix in mutation.RemovedPrefixes)
            {
                foreach (var key in modifiedTokens.Keys
                    .Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
                    .ToArray())
                {
                    modifiedTokens.Remove(key);
                }
            }

            foreach (var pair in mutation.Values)
            {
                foreach (var marker in modifiedTokens.Keys
                    .Where(key => key.StartsWith(RemovedPrefixMarker, StringComparison.Ordinal)
                        && pair.Key.StartsWith(key[RemovedPrefixMarker.Length..], StringComparison.Ordinal))
                    .ToArray())
                {
                    modifiedTokens.Remove(marker);
                }

                modifiedTokens[pair.Key] = pair.Value;
            }

            await PersistLockedAsync(modifiedTokens, cancellationToken);
            Interlocked.Exchange(
                ref _tokens,
                new ConcurrentDictionary<string, string>(modifiedTokens, StringComparer.Ordinal));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task RemovePrefixAsync(
        string secretReferencePrefix,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretReferencePrefix);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var modifiedTokens = _tokens
                .Where(pair => !pair.Key.StartsWith(secretReferencePrefix, StringComparison.Ordinal))
                .ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.Ordinal);
            modifiedTokens[RemovedPrefixMarker + secretReferencePrefix] = string.Empty;
            await PersistLockedAsync(modifiedTokens, cancellationToken);
            Interlocked.Exchange(
                ref _tokens,
                new ConcurrentDictionary<string, string>(modifiedTokens, StringComparer.Ordinal));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task PersistLockedAsync(
        IReadOnlyDictionary<string, string> tokens,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_cachePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("The MCP OAuth token-cache path has no parent directory.");
        }

        Directory.CreateDirectory(directory);
        var temporaryPath = _cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var payload = JsonSerializer.SerializeToUtf8Bytes(tokens);
        try
        {
            var fileOptions = new FileStreamOptions
            {
                Access = FileAccess.Write,
                Mode = FileMode.CreateNew,
                Options = FileOptions.Asynchronous,
                Share = FileShare.None,
            };
            if (!OperatingSystem.IsWindows())
            {
                fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            await using (var stream = new FileStream(temporaryPath, fileOptions))
            {
                await stream.WriteAsync(payload, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, _cachePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private void LoadExisting()
    {
        if (!File.Exists(_cachePath))
        {
            return;
        }

        Dictionary<string, string>? values;
        try
        {
            values = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(_cachePath));
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                exception,
                "The optional MCP OAuth token cache at {CachePath} could not be loaded; cached authentication will be ignored.",
                _cachePath);
            return;
        }

        if (values is null)
        {
            return;
        }

        foreach (var pair in values)
        {
            if (pair.Key.StartsWith("mcp:oauth:", StringComparison.Ordinal)
                || pair.Key.StartsWith(RemovedPrefixMarker + "mcp:oauth:", StringComparison.Ordinal))
            {
                _tokens[pair.Key] = pair.Value;
            }
        }
    }

    private static bool IsRemovedByPrefix(
        IReadOnlyDictionary<string, string> tokens,
        string secretReference)
    {
        return tokens.Keys.Any(key => key.StartsWith(RemovedPrefixMarker, StringComparison.Ordinal)
            && secretReference.StartsWith(key[RemovedPrefixMarker.Length..], StringComparison.Ordinal));
    }

    private async Task<IReadOnlyDictionary<string, string>> ReadLegacyGrantAsync(
        string secretReferencePrefix,
        CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var suffix in LegacyGrantSuffixes)
        {
            var secretReference = secretReferencePrefix + suffix;
            var value = await _secretStore.GetAsync("secrets:" + secretReference, cancellationToken);
            if (value is not null)
            {
                values[secretReference] = value;
            }
        }

        return values;
    }

    private static bool HaveSameValues(
        IReadOnlyDictionary<string, string> first,
        IReadOnlyDictionary<string, string> second)
    {
        return first.Count == second.Count
            && first.All(pair => second.TryGetValue(pair.Key, out var value)
                && string.Equals(pair.Value, value, StringComparison.Ordinal));
    }

    private static string? NullIfEmpty(string value)
    {
        return value.Length == 0 ? null : value;
    }
}
