namespace Threadsmith.ModelTooling.Tests;

using System.Text;
using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public static partial class ToolRuntimeTests
{
    /// <summary>Redaction preserves line positions and is stable across subsequent shared sanitation.</summary>
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public static void Sanitizer_MultilineCredentialsPreserveLines(string newline)
    {
        var source = string.Join(newline, "before", "-----BEGIN PRIVATE KEY-----", "synthetic-key-material", "-----END PRIVATE KEY-----", "apiKey: \"synthetic-first", "synthetic-second\"", "Server=fixture;Password=synthetic-third", "synthetic-fourth;", "after");
        var sanitizer = new SecretOutputSanitizer();
        var result = sanitizer.Sanitize(source);

        Assert.DoesNotContain("synthetic", result, StringComparison.Ordinal);
        Assert.Equal(source.Count(c => c == '\n'), result.Count(c => c == '\n'));
        Assert.Equal(source.Count(c => c == '\r'), result.Count(c => c == '\r'));
        Assert.Equal(result, sanitizer.Sanitize(result));
        Assert.Equal("after", result.Split(newline)[^1]);
    }

    /// <summary>Unquoted authorization values preserve line positions without requiring quoted credentials elsewhere.</summary>
    [Theory]
    [InlineData("Bearer", "\n")]
    [InlineData("Bearer", "\r\n")]
    [InlineData("Basic", "\n")]
    [InlineData("Basic", "\r\n")]
    public static void Sanitizer_MultilineAuthorizationPreservesLines(string scheme, string newline)
    {
        var source = $"authorization: {scheme}{newline}  synthetic-credential{newline}next: value";
        var sanitizer = new SecretOutputSanitizer();
        var result = sanitizer.Sanitize(source);

        Assert.Equal($"authorization: [REDACTED]{newline}{newline}next: value", result);
        Assert.Equal(result, sanitizer.Sanitize(result));
    }

    /// <summary>Both read modes remove complete credentials before selecting a page and retain safe configuration.</summary>
    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 3)]
    [InlineData(true, 1)]
    public static async Task ReadFile_SanitizesBeforePaging(bool snapshot, int startLine)
    {
        var root = CreateTemporaryDirectory();
        try
        {
            const string source = "setting: enabled\n-----BEGIN PRIVATE KEY-----\nsynthetic-private-value\n-----END PRIVATE KEY-----\napiKey: \"synthetic-start\nsynthetic-end\"\nafter: readable\n";
            await File.WriteAllTextAsync(Path.Combine(root, "config.yml"), source, new UTF8Encoding(false));
            await using var events = new DomainEventStream();
            var pipeline = CreatePipeline(events, [new ReadFileTool(TestPromptLoader.Instance, new SecretOutputSanitizer(), new ToolLimits { ReadFileMaximumContentBytes = snapshot ? 32 : 2048 })]);
            var offset = 0;
            var collected = new StringBuilder();
            do
            {
                var arguments = JsonSerializer.Serialize(new { path = "config.yml", snapshot, snapshotOffset = offset, startLine });
                var result = await pipeline.InvokeAsync(CreateBatchRequest(0, "sanitized-read", "read_file", CreateContext(root) with { TrustLevel = RepositoryTrustLevel.TrustedRead }, arguments).Invocation);
                Assert.True(result.Succeeded, result.Error);
                Assert.DoesNotContain("synthetic", result.ResultJson, StringComparison.Ordinal);
                var output = JsonSerializer.Deserialize<ReadFileOutput>(result.ResultJson!);
                Assert.NotNull(output);
                if (!snapshot)
                {
                    Assert.Equal(7, output.TotalLines);
                    Assert.Contains("after: readable", output.Lines);
                    break;
                }

                Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(source))), output.ContentDigest);
                collected.Append(output.Content);
                offset = output.NextSnapshotOffset ?? -1;
            }
            while (offset >= 0);

            if (snapshot)
            {
                Assert.Contains("after: readable", collected.ToString(), StringComparison.Ordinal);
                Assert.Equal(7, collected.ToString().Count(c => c == '\n'));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
