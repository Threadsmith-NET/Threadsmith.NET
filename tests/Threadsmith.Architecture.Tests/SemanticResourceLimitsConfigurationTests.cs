namespace Threadsmith.Architecture.Tests;

using Microsoft.Extensions.Configuration;
using Threadsmith.App;
using Threadsmith.Core;
using Xunit;

/// <summary>Checks startup binding of the semantic file-read limit.</summary>
public static class SemanticResourceLimitsConfigurationTests
{
    /// <summary>Omitted configuration retains four read workers.</summary>
    [Fact]
    public static void FileReadLimitDefaultsToFour()
    {
        var limits = HostFoundation.LoadOperationalLimits(new ConfigurationBuilder().Build());
        Assert.Equal(4, limits.Semantic.MaximumConcurrentFileReads);
    }

    /// <summary>Host binding accepts overrides and captures them until the next startup.</summary>
    [Theory]
    [InlineData("1", 1)]
    [InlineData("7", 7)]
    public static void FileReadLimitIsCapturedUntilRestart(string value, int expected)
    {
        const string key = "limits:semantic:maximumConcurrentFileReads";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [key] = value,
        }).Build();
        var limits = HostFoundation.LoadOperationalLimits(configuration);
        configuration[key] = "3";
        Assert.Equal(expected, limits.Semantic.MaximumConcurrentFileReads);
        Assert.Equal(3, HostFoundation.LoadOperationalLimits(configuration).Semantic.MaximumConcurrentFileReads);
    }

    /// <summary>Both direct validation and host binding reject nonpositive counts.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public static void NonpositiveFileReadLimitIsRejected(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SemanticResourceLimits { MaximumConcurrentFileReads = value }.Validate());
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["limits:semantic:maximumConcurrentFileReads"] = value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }).Build();
        Assert.Throws<ArgumentOutOfRangeException>(() => HostFoundation.LoadOperationalLimits(configuration));
    }

    /// <summary>Malformed values and misspelled members fail rather than silently using defaults.</summary>
    [Theory]
    [InlineData("maximumConcurrentFileReads", "invalid")]
    [InlineData("maximumConcurrentFileRead", "4")]
    public static void MalformedOrUnknownFileReadSettingIsRejected(string member, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["limits:semantic:" + member] = value,
        }).Build();
        Assert.Throws<InvalidOperationException>(() => HostFoundation.LoadOperationalLimits(configuration));
    }
}
