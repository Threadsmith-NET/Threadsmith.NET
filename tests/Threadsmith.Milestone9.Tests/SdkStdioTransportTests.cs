namespace Threadsmith.Milestone9.Tests;

using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Mcp;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

/// <summary>Verifies the SDK-backed stdio MCP transport against the in-repository server.</summary>
public sealed class SdkStdioTransportTests
{
    /// <summary>Host profiles map to isolated SDK stdio process options.</summary>
    [Fact]
    public void Profile_maps_to_scoped_stdio_options()
    {
        var profile = CreateProfile(["server.dll"]);

        var options = SdkStdioTransport.CreateOptions(
            profile,
            new Dictionary<string, string> { ["THREADSMITH_TEST"] = "value" });

        Assert.Equal("dotnet", options.Command);
        Assert.Equal(["server.dll"], options.Arguments);
        Assert.Equal(profile.WorkingDirectory, options.WorkingDirectory);
        Assert.False(options.InheritEnvironmentVariables);
        Assert.Equal("value", options.EnvironmentVariables?["THREADSMITH_TEST"]);
        Assert.Equal(profile.DrainKillTimeout, options.ShutdownTimeout);
    }

    /// <summary>An oversized stdio frame is rejected before the SDK can materialize its JSON.</summary>
    [Fact]
    public void Stdio_message_stream_rejects_oversized_line()
    {
        using var source = new MemoryStream(new byte[McpBoundedLineReadStream.MaximumLineBytes + 1]);
        using var bounded = new McpBoundedLineReadStream(source);
        var buffer = new byte[McpBoundedLineReadStream.MaximumLineBytes + 1];

        _ = Assert.Throws<InvalidDataException>(() => bounded.Read(buffer, 0, buffer.Length));
    }

    /// <summary>A real stdio server imports and invokes echo through the host-owned imported tool.</summary>
    [Fact]
    public async Task Real_stdio_server_connects_imports_invokes_and_disconnects()
    {
        string serverAssembly = GetServerAssemblyPath();
        Assert.True(File.Exists(serverAssembly), $"MCP test server fixture is unavailable at '{serverAssembly}'.");
        var adapter = CreateAdapter();
        var profile = CreateProfile([serverAssembly]);

        var connection = await adapter.ConnectAsync(profile);

        Assert.True(connection.Succeeded, connection.Status.Error);
        Assert.True(connection.Status.ProcessPresent);
        Assert.True(Assert.Single(adapter.GetConnections()).ProcessPresent);
        var tool = Assert.Single(connection.Tools);
        Assert.Equal("stdio-test:echo", tool.Definition.Id);
        object input = tool.DeserializeInput("{\"message\":\"hello-mcp\"}");
        var envelope = await tool.ExecuteAsync(
            input,
            new ToolExecutionContext(
                ToolInvocationId.New(),
                SessionId.New(),
                RunId.New(),
                CreateInvocationContext()),
            CancellationToken.None);
        var result = Assert.IsType<JsonElement>(envelope.Value);
        Assert.Equal("hello-mcp", result[0].GetString());

        await adapter.DisconnectAsync(profile.Id);
        Assert.Empty(adapter.GetConnections());
        await adapter.DisposeAsync();
    }

    /// <summary>SDK disposal kills a real server process that refuses to exit after stdin closes.</summary>
    [Fact]
    public async Task Hung_stdio_server_process_is_killed_within_drain_timeout()
    {
        string serverAssembly = GetServerAssemblyPath();
        string pidFile = Path.Combine(Path.GetTempPath(), $"threadsmith-mcp-{Guid.NewGuid():N}.pid");
        var adapter = CreateAdapter();
        var profile = CreateProfile(
            [serverAssembly, "--hang-on-shutdown", "--pid-file", pidFile],
            TimeSpan.FromMilliseconds(250));
        try
        {
            var connection = await adapter.ConnectAsync(profile);
            Assert.True(connection.Succeeded, connection.Status.Error);
            await WaitForFileAsync(pidFile, TimeSpan.FromSeconds(5));
            int processId = int.Parse(await File.ReadAllTextAsync(pidFile));
            var stopwatch = Stopwatch.StartNew();

            await adapter.DisconnectAsync(profile.Id);

            stopwatch.Stop();
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
            Assert.True(WaitForExit(processId, TimeSpan.FromSeconds(3)), $"MCP fixture process {processId} remained alive.");
        }
        finally
        {
            await adapter.DisposeAsync();
            File.Delete(pidFile);
        }
    }

    /// <summary>A shortened stop deadline replaces the SDK's original timeout and terminates the process tree.</summary>
    [Fact]
    public async Task StopAsync_RemainingDeadlineOverridesOriginalSdkShutdownTimeout()
    {
        string serverAssembly = GetServerAssemblyPath();
        string pidFile = Path.Combine(Path.GetTempPath(), $"threadsmith-mcp-{Guid.NewGuid():N}.pid");
        var transport = new SdkStdioTransport(
            new SecretOutputSanitizer(),
            NullLoggerFactory.Instance);
        var profile = CreateProfile(
            [serverAssembly, "--hang-on-shutdown", "--pid-file", pidFile],
            TimeSpan.FromSeconds(5));
        try
        {
            _ = await transport.StartAsync(profile, new Dictionary<string, string>());
            await WaitForFileAsync(pidFile, TimeSpan.FromSeconds(5));
            int processId = int.Parse(await File.ReadAllTextAsync(pidFile));
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            var stopwatch = Stopwatch.StartNew();

            _ = await transport.StopAsync(TimeSpan.FromMilliseconds(200), deadline.Token);

            stopwatch.Stop();
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
            Assert.True(
                WaitForExit(processId, TimeSpan.FromMilliseconds(500)),
                $"MCP fixture process {processId} survived the remaining shutdown deadline.");
        }
        finally
        {
            await transport.DisposeAsync();
            File.Delete(pidFile);
        }
    }

    /// <summary>A failed SDK handshake disposes the local transport and terminates its child process.</summary>
    [Fact]
    public async Task Failed_stdio_handshake_terminates_spawned_process()
    {
        string serverAssembly = GetServerAssemblyPath();
        string pidFile = Path.Combine(Path.GetTempPath(), $"threadsmith-mcp-{Guid.NewGuid():N}.pid");
        var transport = new SdkStdioTransport(
            new SecretOutputSanitizer(),
            NullLoggerFactory.Instance);
        var profile = CreateProfile(
            [serverAssembly, "--skip-handshake", "--pid-file", pidFile],
            TimeSpan.FromMilliseconds(250)) with
        {
            StartupTimeout = TimeSpan.FromMilliseconds(250),
        };
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => transport.StartAsync(profile, new Dictionary<string, string>()));
            await WaitForFileAsync(pidFile, TimeSpan.FromSeconds(5));
            int processId = int.Parse(await File.ReadAllTextAsync(pidFile));

            Assert.True(WaitForExit(processId, TimeSpan.FromSeconds(3)), $"MCP fixture process {processId} remained alive.");
        }
        finally
        {
            await transport.DisposeAsync();
            File.Delete(pidFile);
        }
    }

    private static McpAdapter CreateAdapter()
    {
        var sanitizer = new SecretOutputSanitizer();
        return new McpAdapter(
            _ => new SdkStdioTransport(sanitizer, NullLoggerFactory.Instance),
            new EmptySecretStore(),
            sanitizer,
            NullLogger<McpAdapter>.Instance);
    }

    private static ToolInvocationContext CreateInvocationContext()
    {
        return new()
        {
            RepositoryPath = FindRepositoryRoot(),
            RequestedBy = "milestone9-test",
        };
    }

    private static McpConnectionProfile CreateProfile(
        IReadOnlyList<string> arguments,
        TimeSpan? drainKillTimeout = null)
    {
        return new()
        {
            Id = "stdio-test",
            DisplayName = "M9 stdio fixture",
            Transport = McpTransport.Stdio,
            Command = "dotnet",
            Arguments = arguments,
            WorkingDirectory = FindRepositoryRoot(),
            Trust = McpTrustLevel.TrustedRead,
            StartupTimeout = TimeSpan.FromSeconds(10),
            RequestTimeout = TimeSpan.FromSeconds(10),
            DrainKillTimeout = drainKillTimeout ?? TimeSpan.FromSeconds(2),
            AllowedCapabilities = [McpCapabilityKind.Tool],
        };
    }

    private static string GetServerAssemblyPath()
    {
        return Path.Combine(
                FindRepositoryRoot(),
                "tests",
                "Threadsmith.Mcp.TestServer",
                "bin",
                "Debug",
                "net10.0",
                "Threadsmith.Mcp.TestServer.dll");
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "Threadsmith.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the Threadsmith repository root.");
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!File.Exists(path))
        {
            await Task.Delay(25, cancellation.Token);
        }
    }

    private static bool WaitForExit(int processId, TimeSpan timeout)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return process.WaitForExit((int)timeout.TotalMilliseconds);
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private sealed class EmptySecretStore : ISecretStore
    {
        public Task<string?> GetAsync(string secretReference, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>(null);
        }
    }
}
