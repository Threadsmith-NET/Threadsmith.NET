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
    private readonly ConcurrentDictionary<Uri, TcpListener> _reservations = new();

    /// <inheritdoc />
    public Uri ReserveRedirectUri(int requestedPort)
    {
        var listener = new TcpListener(IPAddress.Loopback, requestedPort);
        listener.Start();
        var reservedPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        var redirectUri = new Uri($"http://localhost:{reservedPort}/callback", UriKind.Absolute);
        if (!_reservations.TryAdd(redirectUri, listener))
        {
            listener.Stop();
            throw new InvalidOperationException("The OAuth callback redirect URI is already reserved.");
        }

        return redirectUri;
    }

    /// <inheritdoc />
    public async Task<Uri> WaitForCallbackAsync(Uri redirectUri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(redirectUri);
        if (!redirectUri.IsLoopback || redirectUri.Scheme != Uri.UriSchemeHttp || redirectUri.Port <= 0)
        {
            throw new InvalidOperationException("OAuth callback listeners require an explicit HTTP localhost port.");
        }

        if (!_reservations.TryRemove(redirectUri, out TcpListener? listener))
        {
            throw new InvalidOperationException("The OAuth callback redirect URI was not reserved by this listener.");
        }

        try
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken);
            await using NetworkStream stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(cancellationToken);
            var requestParts = requestLine?.Split(' ', 3) ?? [];
            if (requestParts.Length != 3
                || !string.Equals(requestParts[0], "GET", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The OAuth callback did not contain a valid HTTP GET request.");
            }

            string? header;
            do
            {
                header = await reader.ReadLineAsync(cancellationToken);
            }
            while (!string.IsNullOrEmpty(header));

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
            listener.Stop();
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
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? callbackUri))
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
        if (_tokens.Any(pair => pair.Key.StartsWith(RemovedPrefixMarker, StringComparison.Ordinal)
            && secretReference.StartsWith(pair.Key[RemovedPrefixMarker.Length..], StringComparison.Ordinal)))
        {
            return null;
        }

        return _tokens.TryGetValue(secretReference, out var value)
            ? NullIfEmpty(value)
            : await _secretStore.GetAsync("secrets:" + secretReference, cancellationToken);
    }

    /// <inheritdoc />
    public async Task SetAsync(string secretReference, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretReference);
        ArgumentNullException.ThrowIfNull(value);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Dictionary<string, string> modifiedTokens = _tokens.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal);
            foreach (var marker in modifiedTokens.Keys
                .Where(key => key.StartsWith(RemovedPrefixMarker, StringComparison.Ordinal)
                    && secretReference.StartsWith(key[RemovedPrefixMarker.Length..], StringComparison.Ordinal))
                .ToArray())
            {
                modifiedTokens.Remove(marker);
            }

            modifiedTokens[secretReference] = value;
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
            Dictionary<string, string> modifiedTokens = _tokens
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

        foreach (KeyValuePair<string, string> pair in values)
        {
            if (pair.Key.StartsWith("mcp:oauth:", StringComparison.Ordinal)
                || pair.Key.StartsWith(RemovedPrefixMarker + "mcp:oauth:", StringComparison.Ordinal))
            {
                _tokens[pair.Key] = pair.Value;
            }
        }
    }

    private static string? NullIfEmpty(string value)
    {
        return value.Length == 0 ? null : value;
    }
}
