namespace Threadsmith.Architecture.Tests;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Threadsmith.App;
using Threadsmith.Core;
using Threadsmith.Models;
using Xunit;

public static partial class AppBootstrapTests
{
    /// <summary>The explicit CLI option archives an existing raw log before the composed provider writes to it.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public static async Task ModelComposition_raw_log_option_rotates_existing_file_before_provider_writes(bool enabled)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TemporaryDirectory("raw-log-startup");
        var repository = temporary.GetPath("repository");
        Directory.CreateDirectory(repository);
        var path = temporary.GetPath("review-run.jsonl");
        const string priorContents = "{\"previousLaunch\":true}\n";
        await File.WriteAllTextAsync(path, priorContents, cancellationToken);
        string[] arguments = enabled
            ? ["--repository", repository, "--raw-model-log", path]
            : ["--repository", repository];
        var parsed = CommandLineParser.Parse(arguments);
        Assert.Null(parsed.Error);
        var options = Assert.IsType<CommandLineOptions>(parsed.Options);
        IConfiguration configuration = new ConfigurationBuilder().Build();
        using var loggerFactory = LoggerFactory.Create(_ => { });

        using var models = await ModelComposition.CreateAsync(
            configuration,
            CreatePaths(repository),
            new CapturingSecretResolver(),
            loggerFactory,
            options.RawModelLogPath,
            trustedConfiguration: configuration);

        var archive = temporary.GetPath("review-run_0.jsonl");
        if (enabled)
        {
            Assert.NotNull(models.RawModelLog);
            Assert.Equal(priorContents, await File.ReadAllTextAsync(archive, cancellationToken));
            Assert.False(File.Exists(path));
        }
        else
        {
            Assert.Null(models.RawModelLog);
            Assert.False(File.Exists(archive));
        }

        await foreach (var chunk in models.Provider.StreamAsync(
            new ModelStreamRequest { RunId = RunId.New(), Input = "offline startup verification" }, cancellationToken))
        {
            Assert.NotNull(chunk);
        }

        var activeContents = await File.ReadAllTextAsync(path, cancellationToken);
        if (enabled)
        {
            Assert.Contains("offline startup verification", activeContents, StringComparison.Ordinal);
            Assert.DoesNotContain("previousLaunch", activeContents, StringComparison.Ordinal);
            Assert.Equal(priorContents, await File.ReadAllTextAsync(archive, cancellationToken));
        }
        else
        {
            Assert.Equal(priorContents, activeContents);
        }
    }
}
