namespace Threadsmith.ModelTooling.Tests;

using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Tools;
using Xunit;

/// <summary>Model projections disclose information lost to configured output limits.</summary>
public sealed class ValidationProjectionLimitsTests
{
    /// <summary>Dropped omission reasons are counted and mark the model result as truncated.</summary>
    [Fact]
    public void PackageOmissionsDiscloseProjectionLoss()
    {
        var projection = new NativeValidationModelProjection(new ValidationResourceLimits { MaximumModelOmissions = 1 });
        var result = new NuGetDependencyHealthResult([], [], null, DateTimeOffset.UtcNow, false, false, false, [], ["first failure", "second failure"], false, default);
        using var json = JsonDocument.Parse(projection.Create(result));
        Assert.True(json.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(1, json.RootElement.GetProperty("omittedOmissions").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("omissions").GetArrayLength());
    }

    /// <summary>Output trimming is reported independently of diagnostic and subprocess truncation.</summary>
    [Theory]
    [InlineData(10, false)]
    [InlineData(11, true)]
    public void FailedValidationOutputDisclosesProjectionLoss(int length, bool truncated)
    {
        var projection = new NativeValidationModelProjection(new ValidationResourceLimits { MaximumModelOutputCharacters = 10 });
        var result = new ValidationToolResult("fixture", default, default, false, ".", [], [], new string('x', length), 1, TimeSpan.Zero, false, false);
        using var json = JsonDocument.Parse(projection.Create(result));
        Assert.Equal(truncated, json.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(10, json.RootElement.GetProperty("output").GetString()!.Length);
    }
}
