namespace Threadsmith.Architecture.Tests;

using Threadsmith.App;
using Threadsmith.Core;
using Xunit;

public sealed partial class SecurityPolicyConfigurationTests
{
    /// <summary>Both shorter and longer tool preferences override the fallback.</summary>
    [Theory]
    [InlineData(60)]
    [InlineData(600)]
    public void ToolTimeoutOverridesGeneralDefault(int seconds)
    {
        File.WriteAllText(_paths.UserConfiguration, $$$$"""
            {"tools":{"runProcess":{"defaultTimeoutSeconds":{{{{seconds}}}},"maxTimeoutSeconds":900},"runtime":{"defaults":{"timeoutMilliseconds":120000} } } }
            """);
        var timeouts = Load().ToolTimeouts!;
        Assert.Equal(TimeSpan.FromSeconds(seconds), timeouts.Select("run_process", TimeSpan.FromSeconds(30)));
        Assert.Equal(TimeSpan.FromSeconds(120), timeouts.Select("other", TimeSpan.FromSeconds(30)));
        Assert.Equal(TimeSpan.FromSeconds(seconds), timeouts.Clamp("run_process", TimeSpan.FromSeconds(seconds)));
    }

    /// <summary>Runtime tool overrides beat legacy preferences, including infinite timeouts.</summary>
    [Fact]
    public void RuntimeToolPreferenceWinsOverLegacySettingAndGeneralDefault()
    {
        File.WriteAllText(_paths.UserConfiguration, """
            {"tools":{"runProcess":{"defaultTimeoutSeconds":600},"runtime":{"defaults":{"timeoutMilliseconds":120000},"byTool":[{"toolId":"run_process","timeoutMilliseconds":300000},{"toolId":"server:search","timeoutMilliseconds":-1}]}}}
            """);
        var timeouts = Load().ToolTimeouts!;
        Assert.Equal(TimeSpan.FromSeconds(300), timeouts.Select("RUN_PROCESS", TimeSpan.FromSeconds(30)));
        Assert.Equal(Timeout.InfiniteTimeSpan, timeouts.Select("server:search", TimeSpan.FromSeconds(30)));
    }

    /// <summary>Machine restrictions remain ceilings rather than fallback preferences.</summary>
    [Fact]
    public void EnforcedTimeoutsRemainSeparateAndBothCeilingsApply()
    {
        File.WriteAllText(_paths.UserConfiguration, """
            {"tools":{"runtime":{"defaults":{"timeoutMilliseconds":1000},"byTool":[{"toolId":"server:search","timeoutMilliseconds":-1}]}}}
            """);
        File.WriteAllText(_paths.MachineConfiguration, """
            {"enforced":{"tools":{"runtime":{"defaults":{"timeoutMilliseconds":9000},"byTool":[{"toolId":"server:search","timeoutMilliseconds":5000}]}}}}
            """);
        var timeouts = Load().ToolTimeouts!;
        Assert.Equal(Timeout.InfiniteTimeSpan, timeouts.Select("server:search", TimeSpan.FromSeconds(30)));
        Assert.Equal(TimeSpan.FromSeconds(5), timeouts.Clamp("server:search", Timeout.InfiniteTimeSpan));
        Assert.Equal(TimeSpan.FromSeconds(9), timeouts.Clamp("other", TimeSpan.FromSeconds(600)));
    }

    /// <summary>Adapter timeouts honor the supported launch environment above file preferences.</summary>
    [Fact]
    public void WebSearchTimeoutEnvironmentOverridesUserPreference()
    {
        const string key = "THREADSMITH_webSearch__provider__timeoutSeconds";
        var previous = Environment.GetEnvironmentVariable(key);
        try
        {
            File.WriteAllText(_paths.UserConfiguration, """{"webSearch":{"provider":{"timeoutSeconds":20}}}""");
            Environment.SetEnvironmentVariable(key, "90");
            Assert.Equal(TimeSpan.FromSeconds(90), Load().ToolTimeouts!.Select("web_search", TimeSpan.FromSeconds(10)));
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, previous);
        }
    }

    /// <summary>Validation timeouts retain ordinary repository, session, CLI and launch precedence.</summary>
    [Fact]
    public void ValidationTimeoutRetainsOrdinaryConfigurationPrecedence()
    {
        const string key = "THREADSMITH_limits__validation__timeoutMilliseconds";
        var previous = Environment.GetEnvironmentVariable(key);
        try
        {
            Environment.SetEnvironmentVariable(key, null);
            Assert.Empty(HostFoundation.CreateConfiguredToolTimeouts(ConfigurationBootstrap.Build([], _paths)));
            File.WriteAllText(_paths.UserConfiguration, """{"limits":{"validation":{"timeoutMilliseconds":30000}}}""");
            File.WriteAllText(_paths.RepositoryConfiguration, """{"limits":{"validation":{"timeoutMilliseconds":600000}}}""");
            Assert.Equal(TimeSpan.FromMinutes(10), HostFoundation.CreateConfiguredToolTimeouts(ConfigurationBootstrap.Build([], _paths))["dotnet_build"]);
            File.WriteAllText(_paths.SessionConfiguration, """{"limits":{"validation":{"timeoutMilliseconds":400000}}}""");
            Assert.Equal(TimeSpan.FromMilliseconds(400000), HostFoundation.CreateConfiguredToolTimeouts(ConfigurationBootstrap.Build([], _paths))["test_run_targeted"]);
            string[] args = ["--set:limits:validation:timeoutMilliseconds=500000"];
            Assert.Equal(TimeSpan.FromMilliseconds(500000), HostFoundation.CreateConfiguredToolTimeouts(ConfigurationBootstrap.Build(args, _paths))["dotnet_build"]);
            Environment.SetEnvironmentVariable(key, "700000");
            Assert.Equal(TimeSpan.FromMilliseconds(700000), HostFoundation.CreateConfiguredToolTimeouts(ConfigurationBootstrap.Build(args, _paths))["dotnet_build"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, previous);
        }
    }

    /// <summary>Timeout provenance affects policy identity even when numeric resource values match.</summary>
    [Fact]
    public void MissingTimeoutPreferencesRetainDeclaredFallbackAndEnforcementChangesFingerprint()
    {
        var initial = Load();
        Assert.Equal(TimeSpan.FromSeconds(42), initial.ToolTimeouts!.Select("other", TimeSpan.FromSeconds(42)));
        File.WriteAllText(_paths.MachineConfiguration, """{"tools":{"runtime":{"defaults":{"timeoutMilliseconds":9000}}}}""");
        var preference = Load();
        File.WriteAllText(_paths.MachineConfiguration, """{"enforced":{"tools":{"runtime":{"defaults":{"timeoutMilliseconds":9000}}}}}""");
        var enforced = Load();
        Assert.Equal(preference.ResourceLimits["tools:runtime:defaults:timeoutMilliseconds"], enforced.ResourceLimits["tools:runtime:defaults:timeoutMilliseconds"]);
        Assert.NotEqual(preference.Fingerprint, enforced.Fingerprint);
        Assert.Null(preference.ToolTimeouts!.Maximum);
        Assert.Null(enforced.ToolTimeouts!.Default);
    }
}
