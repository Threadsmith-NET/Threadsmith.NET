namespace Threadsmith.Mcp.TestServer;

using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        bool hangOnShutdown = args.Contains("--hang-on-shutdown", StringComparer.Ordinal);
        int pidFileIndex = Array.IndexOf(args, "--pid-file");
        if (pidFileIndex >= 0 && pidFileIndex + 1 < args.Length)
        {
            await File.WriteAllTextAsync(args[pidFileIndex + 1], Environment.ProcessId.ToString());
        }

        if (args.Contains("--invalid-handshake", StringComparer.Ordinal))
        {
            // Fail only after the process has started and published its PID. A very short
            // client timeout can kill a slow-starting process before the fixture runs at all.
            var requestLine = await Console.In.ReadLineAsync();
            using var request = JsonDocument.Parse(requestLine ?? throw new InvalidOperationException("Missing initialize request."));
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = request.RootElement.GetProperty("id"),
                error = new { code = -32603, message = "Fixture rejects initialization." },
            }));
            await Console.Out.FlushAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None);
            return;
        }

        HostApplicationBuilder builder = Host.CreateApplicationBuilder([]);
        builder.Logging.ClearProviders();
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<EchoTools>()
            .WithResources<TestResources>()
            .WithPrompts<TestPrompts>();

        using IHost host = builder.Build();
        await host.RunAsync();
        if (hangOnShutdown)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None);
        }
    }
}

[McpServerToolType]
internal sealed class EchoTools
{
    [McpServerTool(Name = "echo")]
    [Description("Echoes the supplied message.")]
    public static string Echo([Description("The message to echo.")] string message) => message;
}

[McpServerResourceType]
internal sealed class TestResources
{
    [McpServerResource(UriTemplate = "threadsmith://fixture/status", Name = "fixture-status", MimeType = "text/plain")]
    [Description("Returns bounded fixture status text.")]
    public static TextResourceContents GetStatus()
    {
        return new TextResourceContents
        {
            Uri = "threadsmith://fixture/status",
            MimeType = "text/plain",
            Text = "fixture-ready",
        };
    }

    [McpServerResource(UriTemplate = "threadsmith://fixture/{name}", Name = "fixture-by-name", MimeType = "text/plain")]
    [Description("Returns one named bounded fixture resource.")]
    public static TextResourceContents GetNamed(string name)
    {
        return new TextResourceContents
        {
            Uri = $"threadsmith://fixture/{name}",
            MimeType = "text/plain",
            Text = $"fixture:{name}",
        };
    }
}

[McpServerPromptType]
internal sealed class TestPrompts
{
    [McpServerPrompt(Name = "review_fixture")]
    [Description("Returns untrusted fixture review material.")]
    public static PromptMessage ReviewFixture(
        [Description("The fixture name.")] string name)
    {
        return new PromptMessage
        {
            Role = Role.User,
            Content = new TextContentBlock { Text = $"Review fixture {name}." },
        };
    }
}
