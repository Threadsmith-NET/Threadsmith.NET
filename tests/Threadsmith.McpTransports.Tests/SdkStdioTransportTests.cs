namespace Threadsmith.McpTransports.Tests;

using Threadsmith.Core;
using Threadsmith.Mcp;
using Xunit;

/// <summary>Verifies stdio MCP option mapping and bounded message framing.</summary>
public sealed class SdkStdioTransportTests
{
    /// <summary>Prompt mapping honors both invocation and prompt-specific argument bounds.</summary>
    [Fact]
    public void PromptArguments_HonorInvocationCountAndNameLimits()
    {
        var mapping = new McpTransportMapping(new McpResourceLimits
        {
            MaximumPromptArguments = 10,
            MaximumArguments = 1,
            MaximumArgumentNameCharacters = 3,
        });
        Assert.Single(mapping.MapArguments(new Dictionary<string, string> { ["one"] = "value" }));
        Assert.Throws<InvalidOperationException>(() => mapping.MapArguments(new Dictionary<string, string>
        {
            ["one"] = "first",
            ["two"] = "second",
        }));
        Assert.Throws<InvalidOperationException>(() => mapping.MapArguments(new Dictionary<string, string> { ["long"] = "value" }));
    }

    /// <summary>The byte following the largest configured line is still rejected without counter overflow.</summary>
    [Fact]
    public void LineGuard_RejectsByteBeyondIntegerMaximum()
    {
        using var bounded = new McpBoundedLineReadStream(new MemoryStream([0x41]), int.MaxValue);

        // Seed only the already-consumed count; do not allocate or scan a 2 GiB fixture.
        var count = typeof(McpBoundedLineReadStream).GetField("_currentLineBytes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        count.SetValue(bounded, int.MaxValue);
        Assert.Throws<InvalidDataException>(() => bounded.ReadByte());
    }

    /// <summary>Host profiles map to isolated SDK stdio process options.</summary>
    [Fact]
    public void Profile_maps_to_scoped_stdio_options()
    {
        var profile = new McpConnectionProfile
        {
            Id = "stdio-test",
            DisplayName = "M9 stdio fixture",
            Transport = McpTransport.Stdio,
            Command = "dotnet",
            Arguments = ["server.dll"],
            WorkingDirectory = AppContext.BaseDirectory,
            Trust = McpTrustLevel.TrustedRead,
            DrainKillTimeout = TimeSpan.FromSeconds(2),
            AllowedCapabilities = [McpCapabilityKind.Tool],
        };

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
}
