namespace Threadsmith.Architecture.Tests;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.App;
using Threadsmith.Core;
using Threadsmith.DotNet;
using Threadsmith.Execution;
using Xunit;

public static partial class AppBootstrapTests
{
    /// <summary>Nondefault host-bound limits change actual compiler retention and edit projections.</summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(4, 3)]
    public static async Task BoundEditLimitsControlRealAnalysis(int retained, int reported)
    {
        var ct = TestContext.Current.CancellationToken;
        using var temporary = new TemporaryDirectory("edit-analysis-limits");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["limits:semantic:editAnalysis:maximumRetainedDiagnostics"] = retained.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["limits:semantic:editAnalysis:maximumReportedDiagnostics"] = reported.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["limits:semantic:editAnalysis:maximumDiagnosticMessageCharacters"] = "12",
        }).Build();
        var options = HostFoundation.LoadOperationalLimits(configuration);
        var project = Path.Combine(temporary.Root, "Example.csproj");
        const string original = "class Example { }";
        const string replacement = "class Example { MissingType Value; public string Text; public int M(string? text) => text.Length; }";
        await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><TreatWarningsAsErrors>false</TreatWarningsAsErrors></PropertyGroup></Project>", ct);
        await File.WriteAllTextAsync(Path.Combine(temporary.Root, "Example.cs"), original, ct);
        await using var events = new DomainEventStream();
        await using var engines = new SemanticEngineRegistry(events, NullLoggerFactory.Instance, TestPromptLoader.Instance, resourceLimits: options.Semantic);
        var command = new ApplySourceEditCommand(SessionId.New(), RunId.New(), WorkspaceId.New(), Guid.NewGuid(), new() { Rationale = "Check configured compiler output", Mutations = [] });
        await engines.LoadAsync(new(command.SessionId, command.WorkspaceId, temporary.Root, project, RepositoryTrustLevel.TrustedBuild), ct);
        var completed = new TaskCompletionSource<SourceEditAnalysis>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = events.Subscribe((item, _) =>
        {
            if (item is SemanticCheckCompleted { Analysis: { } analysis } && analysis.EffectId == command.EffectId)
            {
                completed.TrySetResult(analysis);
            }

            return Task.CompletedTask;
        });
        var beforeHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(original)));
        var afterHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(replacement)));
        var snapshot = new MutationEffectSnapshot(MutationSetId.New(), [], [new("Example.cs", beforeHash, afterHash) { FinalBytes = Encoding.UTF8.GetBytes(replacement) }]);
        Assert.NotNull(await engines.AnalyzeCandidateAsync(command, temporary.Root, snapshot, TimeSpan.Zero, ct));
        var result = await completed.Task.WaitAsync(ct);
        Assert.Equal(1, result.CurrentErrors);
        Assert.Equal(2, result.CurrentWarnings);
        Assert.Equal(reported, result.Diagnostics.Count);
        Assert.Equal(retained == 1, result.FindingRetentionTruncated);
        Assert.Equal(retained != 1, result.ErrorComparisonAvailable);
        Assert.Equal(DiagnosticSeverity.Error, result.Diagnostics[0].Severity);
        Assert.All(result.Diagnostics, diagnostic => Assert.InRange(diagnostic.Message.Length, 1, 12));
    }
}
