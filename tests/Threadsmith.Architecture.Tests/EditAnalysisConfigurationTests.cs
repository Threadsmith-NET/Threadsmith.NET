namespace Threadsmith.Architecture.Tests;

using System.Text;
using Microsoft.Extensions.Configuration;
using Threadsmith.App;
using Threadsmith.Core;
using Xunit;

/// <summary>Exercises host-bound edit advisory configuration.</summary>
public sealed class EditAnalysisConfigurationTests
{
    /// <summary>Binding captures normalized policy until the next startup.</summary>
    [Fact]
    public void BoundOptionsNormalizeIdsReplaceDefaultsAndCaptureUntilRestart()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["limits:semantic:editAnalysis:compilerWarningIds:0"] = " cs4014 ",
            ["limits:semantic:editAnalysis:compilerWarningIds:1"] = "CS4014",
            ["limits:semantic:editAnalysis:excludedDiagnosticIds:0"] = " cs0246 ",
            ["limits:semantic:editAnalysis:excludedDiagnosticIds:1"] = "CS0246",
            ["limits:semantic:editAnalysis:excludedDiagnosticIds:2"] = "CA2016",
            ["limits:semantic:editAnalysis:maximumRetainedDiagnostics"] = "2",
            ["limits:semantic:editAnalysis:maximumReportedDiagnostics"] = "1",
            ["limits:semantic:editAnalysis:maximumCachedProjects"] = "1",
            ["limits:semantic:editAnalysis:maximumOmissions"] = "2",
            ["limits:semantic:editAnalysis:maximumDiagnosticMessageCharacters"] = "12",
            ["limits:semantic:editAnalysis:immediateObservationMilliseconds"] = "0",
            ["limits:semantic:editAnalysis:candidateLifetimeMilliseconds"] = "1234",
            ["limits:semantic:editAnalysis:finalFeedbackWaitMilliseconds"] = "4567",
            ["limits:semantic:editAnalysis:analyzers:diagnosticIds:0"] = " ca2016 ",
            ["limits:semantic:editAnalysis:analyzers:diagnosticIds:1"] = "CA2016",
            ["limits:semantic:editAnalysis:analyzers:budgetMilliseconds"] = "2345",
            ["limits:semantic:editAnalysis:analyzers:concurrentAnalysis"] = "true",
            ["limits:semantic:editAnalysis:analyzers:enabled"] = "true",
        }).Build();
        var options = HostFoundation.LoadOperationalLimits(configuration).Semantic.EditAnalysis;
        Assert.Equal(["CS4014"], options.CompilerWarningIds);
        Assert.Equal(["CA2016"], options.Analyzers.DiagnosticIds);
        Assert.Equal(2345, options.Analyzers.BudgetMilliseconds);
        Assert.True(options.Analyzers.ConcurrentAnalysis);
        Assert.True(options.Analyzers.Enabled);
        configuration["limits:semantic:editAnalysis:analyzers:diagnosticIds:0"] = "CA2012";
        Assert.Equal(["CA2016"], options.Analyzers.DiagnosticIds);
        Assert.Equal(["CS0246", "CA2016"], options.ExcludedDiagnosticIds);
        Assert.Equal(2, options.MaximumRetainedDiagnostics);
        Assert.Equal(1, options.MaximumReportedDiagnostics);
        Assert.Equal(1, options.MaximumCachedProjects);
        Assert.Equal(2, options.MaximumOmissions);
        Assert.Equal(12, options.MaximumDiagnosticMessageCharacters);
        Assert.Equal(0, options.ImmediateObservationMilliseconds);
        Assert.Equal(1234, options.CandidateLifetimeMilliseconds);
        Assert.Equal(4567, options.FinalFeedbackWaitMilliseconds);
        var defaults = HostFoundation.LoadOperationalLimits(new ConfigurationBuilder().Build()).Semantic.EditAnalysis;
        Assert.True(defaults.Analyzers.Enabled);
        Assert.Equal(Timeout.Infinite, defaults.FinalFeedbackWaitMilliseconds);
        Assert.Equal(Timeout.Infinite, defaults.CandidateLifetimeMilliseconds);
        Assert.Equal(0, defaults.MaximumRetainedDiagnostics);
        Assert.Equal(0, defaults.MaximumReportedDiagnostics);
        Assert.Equal(0, defaults.MaximumCachedProjects);
        var captured = new DiagnosticReportingPolicy(options);
        configuration["limits:semantic:editAnalysis:excludedDiagnosticIds:0"] = "CS4014";
        Assert.False(captured.ShouldReport("CS0246", DiagnosticSeverity.Error));
        Assert.True(captured.ShouldReport("CS4014", DiagnosticSeverity.Warning));
        Assert.False(new DiagnosticReportingPolicy(HostFoundation.LoadOperationalLimits(configuration).Semantic.EditAnalysis).ShouldReport("CS4014", DiagnosticSeverity.Error));
    }

    /// <summary>Explicit empty lists preserve their documented behavior.</summary>
    [Fact]
    public void EmptyArraysDisableWarningsAndFilterNothing()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("""
            {"limits":{"semantic":{"editAnalysis":{"compilerWarningIds":[],"excludedDiagnosticIds":[],"analyzers":{"diagnosticIds":[]}}}}}
            """));
        var options = HostFoundation.LoadOperationalLimits(new ConfigurationBuilder().AddJsonStream(stream).Build()).Semantic.EditAnalysis;
        Assert.Empty(options.CompilerWarningIds);
        Assert.Empty(options.ExcludedDiagnosticIds);
        Assert.Empty(options.Analyzers.DiagnosticIds);
        Assert.True(options.Analyzers.Enabled);
        Assert.False(options.Analyzers.ConcurrentAnalysis);
        var policy = new DiagnosticReportingPolicy(options);
        Assert.True(policy.ShouldReport("CS0246", DiagnosticSeverity.Error));
        Assert.False(policy.ShouldReport("CS4014", DiagnosticSeverity.Warning));
        Assert.Empty(HostFoundation.LoadOperationalLimits(new ConfigurationBuilder().Build()).Semantic.EditAnalysis.ExcludedDiagnosticIds);
    }

    /// <summary>Explicit opt-out survives startup binding and capture.</summary>
    [Fact]
    public void AnalyzerFeedbackCanBeDisabledExplicitly()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["limits:semantic:editAnalysis:analyzers:enabled"] = "false",
        }).Build();
        var options = HostFoundation.LoadOperationalLimits(configuration).Semantic.EditAnalysis;
        Assert.False(options.Analyzers.Enabled);
        Assert.False(options.Capture().Analyzers.Enabled);
    }

    /// <summary>Invalid or unknown options fail through the host binder.</summary>
    [Theory]
    [InlineData("unknownSetting", "1")]
    [InlineData("maximumRetainedDiagnostics", "-1")]
    [InlineData("maximumReportedDiagnostics", "-1")]
    [InlineData("candidateLifetimeMilliseconds", "0")]
    [InlineData("immediateObservationMilliseconds", "-1")]
    [InlineData("finalFeedbackWaitMilliseconds", "-2")]
    [InlineData("excludedDiagnosticIds:0", " ")]
    [InlineData("compilerWarningIds:0", "")]
    [InlineData("analyzers:diagnosticIds:0", " ")]
    [InlineData("analyzers:budgetMilliseconds", "0")]
    [InlineData("analyzers:budgetMilliseconds", "-1")]
    [InlineData("analyzers:unknown", "true")]
    public void InvalidNestedSettingsFailAtHostBinding(string setting, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["limits:semantic:editAnalysis:" + setting] = value,
        }).Build();
        Assert.ThrowsAny<Exception>(() => HostFoundation.LoadOperationalLimits(configuration));
    }
}
