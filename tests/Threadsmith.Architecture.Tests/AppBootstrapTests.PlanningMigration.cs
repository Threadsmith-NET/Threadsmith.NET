namespace Threadsmith.Architecture.Tests;

using Microsoft.Extensions.Configuration;
using Threadsmith.App;
using Threadsmith.Core;
using Xunit;

public static partial class AppBootstrapTests
{
    /// <summary>Nested and flattened retired values are removed without changing unrelated settings.</summary>
    [Theory]
    [InlineData("{\"planning\":{\"approvalPolicy\":\"invalid\",\"keep\":1},\"mutation\":{\"approvalPolicy\":\"reviewAll\"}}")]
    [InlineData("{\"mutation\":{\"approvalPolicy\":\"reviewAll\"},\"PLANNING:APPROVALPOLICY\":false,}")]
    [InlineData("{\"planning\":{\"approvalPolicy\":{},\"incrementalPlans\":{\"enabled\":true,\"targetSteps\":-1,\"keep\":1}},\"mutation\":{\"approvalPolicy\":\"reviewAll\"}}")]
    [InlineData("{\"limits\":{\"plan\":{\"maximumSteps\":\"invalid\",},\"workspace\":{\"maximumMutations\":12}},\"mutation\":{\"approvalPolicy\":\"reviewAll\"}}")]
    [InlineData("{\"execution:mutationBatching:targetFiles\":-1,\"execution:maxPlanningToolRounds\":\"invalid\",\"mutation\":{\"approvalPolicy\":\"reviewAll\"}}")]
    public static async Task PlanningMigration_RemovesOnlyRetiredKeysAndIsIdempotent(string original)
    {
        using var temporary = new TemporaryDirectory("planning-migration");
        var path = temporary.GetPath("config.json");
        await File.WriteAllTextAsync(path, original, TestContext.Current.CancellationToken);
        await RetiredPlanningConfiguration.MigrateFileAsync(path, TestContext.Current.CancellationToken);
        var first = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        Assert.NotEqual(original, first);
        var configuration = new ConfigurationBuilder().AddJsonFile(path).Build();
        Assert.Null(RetiredPlanningConfiguration.GetWarning(configuration));
        Assert.Equal("reviewAll", configuration["mutation:approvalPolicy"]);
        if (original.Contains("keep", StringComparison.Ordinal))
        {
            Assert.Contains("\"keep\":1", first, StringComparison.Ordinal);
        }

        var limits = HostFoundation.LoadOperationalLimits(configuration);
        Assert.Equal(original.Contains("maximumMutations", StringComparison.Ordinal) ? 12 : 100, limits.Workspace.MaximumMutations);
        await RetiredPlanningConfiguration.MigrateFileAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(first, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(temporary.Root));
    }

    /// <summary>Removing the last property preserves the rest of the document verbatim.</summary>
    [Fact]
    public static async Task PlanningMigration_PreservesUnrelatedFormattingAndComments()
    {
        using var temporary = new TemporaryDirectory("planning-format");
        var path = temporary.GetPath("config.json");
        const string retained = "  // retained setting\n  \"mutation\" : { \"approvalPolicy\" : \"alwaysTrustRepo\" }";
        await File.WriteAllTextAsync(path, "{\n" + retained + ",\n  \"planning:approvalPolicy\":\"autoApproveAllValid\"\n}", TestContext.Current.CancellationToken);
        await RetiredPlanningConfiguration.MigrateFileAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal("{\n" + retained + "\n  \n}", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    /// <summary>Comments adjacent to first, middle and last obsolete properties survive removal.</summary>
    [Theory]
    [InlineData("{\"planning:approvalPolicy\":0, /* retained, rationale */ \"keep\":1}")]
    [InlineData("{\"before\":1,\"planning:approvalPolicy\":0, /* retained, rationale */ \"keep\":1}")]
    [InlineData("{\"keep\":1, /* retained, rationale */ \"planning:approvalPolicy\":0,}")]
    public static async Task PlanningMigration_PreservesAdjacentComments(string original)
    {
        using var temporary = new TemporaryDirectory("planning-comments");
        var path = temporary.GetPath("config.json");
        await File.WriteAllTextAsync(path, original, TestContext.Current.CancellationToken);
        await RetiredPlanningConfiguration.MigrateFileAsync(path, TestContext.Current.CancellationToken);
        var updated = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        Assert.Contains("/* retained, rationale */", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("planning:approvalPolicy", updated, StringComparison.Ordinal);
        Assert.Equal("1", new ConfigurationBuilder().AddJsonFile(path).Build()["keep"]);
    }

    /// <summary>Migration exceptions do not turn strict binding into an unknown-key bypass.</summary>
    [Fact]
    public static void PlanningMigration_UnknownPlanLimitsStillFailStrictBinding()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["limits:plan:maximumSteps"] = "invalid",
            ["limits:plan:unknownSetting"] = "1",
        }).Build();
        Assert.Throws<InvalidOperationException>(() => HostFoundation.LoadOperationalLimits(configuration));
    }

    /// <summary>Old policies and invalid plan bounds remain inert in unwritable and external layers.</summary>
    [Fact]
    public static async Task PlanningMigration_ReadOnlyAndExternalValuesCannotRestorePlanAdmission()
    {
        using var temporary = new TemporaryDirectory("planning-read-only");
        var path = temporary.GetPath("config.json");
        const string original = "{\"planning\":{\"approvalPolicy\":\"not-a-policy\"},\"limits:plan:maximumSteps\":-1,\"mutation:approvalPolicy\":\"reviewAll\"}";
        await File.WriteAllTextAsync(path, original, TestContext.Current.CancellationToken);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            await RetiredPlanningConfiguration.MigrateFileAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(original, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
            var configuration = new ConfigurationBuilder().AddJsonFile(path).AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["planning:approvalPolicy"] = "autoApproveAllValid",
                ["execution:maxPlanningToolRounds"] = "invalid",
            }).Build();
            Assert.NotNull(RetiredPlanningConfiguration.GetWarning(configuration));
            Assert.Equal(new WorkspaceResourceLimits(), HostFoundation.LoadOperationalLimits(configuration).Workspace);
            Assert.Equal("reviewAll", configuration["mutation:approvalPolicy"]);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    /// <summary>A cancelled migration cannot alter the original file.</summary>
    [Fact]
    public static async Task PlanningMigration_CancellationBeforeWritePreservesFile()
    {
        using var temporary = new TemporaryDirectory("planning-cancel");
        var path = temporary.GetPath("config.json");
        const string original = "{\"planning:approvalPolicy\":\"autoApproveAllValid\"}";
        await File.WriteAllTextAsync(path, original, TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RetiredPlanningConfiguration.MigrateFileAsync(path, cancellation.Token));
        Assert.Equal(original, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }
}
