namespace Threadsmith.Mutations.Tests;

using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Threadsmith.Core;
using Threadsmith.Workspaces;
using Xunit;

/// <summary>Approval choices persist only in repository settings; session overrides never alter disk.</summary>
public sealed class ApprovalPolicyPersistenceTests
{
    /// <summary>Every non-session choice survives reload without modifying machine/user configuration or unrelated repository values.</summary>
    [Theory]
    [InlineData(false, "reviewAll")]
    [InlineData(false, "reviewRisky")]
    [InlineData(false, "trustPlan")]
    [InlineData(false, "alwaysTrustRepo")]
    [InlineData(true, "reviewAll")]
    [InlineData(true, "reviewRisky")]
    [InlineData(true, "autoApproveAllValid")]
    [InlineData(true, "alwaysTrustRepo")]
    public static async Task NonSessionChoice_RoundTripsOnlyThroughRepository(bool planning, string selected)
    {
        using var fixture = new Fixture();
        var original = JsonNode.Parse(await File.ReadAllTextAsync(fixture.ConfigPath)) as JsonObject
            ?? throw new InvalidOperationException("Missing fixture config.");
        var configuration = fixture.Load();

        if (planning)
        {
            var policy = Enum.Parse<PlanApprovalPolicy>(selected, ignoreCase: true);
            var service = new PlanApprovalPolicyService(configuration, fixture.ConfigPath);
            await service.SetPolicyAsync(policy);
            Assert.Equal(policy, new PlanApprovalPolicyService(fixture.Load(), fixture.ConfigPath).CurrentPolicy);
        }
        else
        {
            var policy = Enum.Parse<MutationApprovalPolicy>(selected, ignoreCase: true);
            var service = new MutationApprovalPolicyService(configuration, fixture.ConfigPath);
            await service.SetPolicyAsync(policy);
            Assert.Equal(policy, new MutationApprovalPolicyService(fixture.Load(), fixture.ConfigPath).CurrentPolicy);
        }

        var section = original[planning ? "Planning" : "Mutation"] as JsonObject
            ?? throw new InvalidOperationException("Missing fixture policy.");
        section["ApprovalPolicy"] = selected;
        if (planning)
        {
            section.Remove("approvalRepositoryIdentity");
        }

        Assert.True(JsonNode.DeepEquals(original, JsonNode.Parse(await File.ReadAllTextAsync(fixture.ConfigPath))));
        fixture.AssertOnlyRepositorySettingsWritten();
    }

    /// <summary>Session trust preserves an existing saved policy byte-for-byte and creates no missing configuration.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public static async Task SessionChoice_DoesNotWriteOrClearSavedPolicy(bool planning, bool existing)
    {
        using var fixture = new Fixture();
        if (!existing)
        {
            File.Delete(fixture.ConfigPath);
            Directory.Delete(Path.GetDirectoryName(fixture.ConfigPath) ?? throw new InvalidOperationException());
        }

        var before = existing ? await File.ReadAllBytesAsync(fixture.ConfigPath) : null;
        if (planning)
        {
            var service = new PlanApprovalPolicyService(fixture.Load(), fixture.ConfigPath);
            await service.SetPolicyAsync(PlanApprovalPolicy.TrustSession);
            Assert.Equal(PlanApprovalPolicy.TrustSession, service.CurrentPolicy);
            Assert.Equal(existing ? PlanApprovalPolicy.AlwaysTrustRepo : PlanApprovalPolicy.ReviewAll, new PlanApprovalPolicyService(fixture.Load(), fixture.ConfigPath).CurrentPolicy);
        }
        else
        {
            var service = new MutationApprovalPolicyService(fixture.Load(), fixture.ConfigPath);
            await service.SetPolicyAsync(MutationApprovalPolicy.TrustSession);
            Assert.Equal(MutationApprovalPolicy.TrustSession, service.CurrentPolicy);
            Assert.Equal(existing ? MutationApprovalPolicy.AlwaysTrustRepo : MutationApprovalPolicy.ReviewAll, new MutationApprovalPolicyService(fixture.Load(), fixture.ConfigPath).CurrentPolicy);
        }

        Assert.Equal(existing, File.Exists(fixture.ConfigPath));
        if (existing)
        {
            Assert.Equal(before, await File.ReadAllBytesAsync(fixture.ConfigPath));
        }
        else
        {
            Assert.False(Directory.Exists(Path.GetDirectoryName(fixture.ConfigPath)));
        }

        fixture.AssertOnlyRepositorySettingsWritten(existing);
    }

    /// <summary>A cancelled or failed write leaves the live policy and saved bytes unchanged.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public static async Task FailedSave_DoesNotChangeEffectivePolicy(bool planning, bool cancelled)
    {
        using var fixture = new Fixture();
        var configuration = fixture.Load();
        if (!cancelled)
        {
            await File.WriteAllTextAsync(fixture.ConfigPath, "[]");
        }

        var before = await File.ReadAllBytesAsync(fixture.ConfigPath);
        var cancellationToken = new CancellationToken(cancelled);
        if (planning)
        {
            var service = new PlanApprovalPolicyService(configuration, fixture.ConfigPath);
            await Assert.ThrowsAnyAsync<Exception>(() => service.SetPolicyAsync(PlanApprovalPolicy.ReviewAll, cancellationToken));
            Assert.Equal(PlanApprovalPolicy.AlwaysTrustRepo, service.CurrentPolicy);
        }
        else
        {
            var service = new MutationApprovalPolicyService(configuration, fixture.ConfigPath);
            await Assert.ThrowsAnyAsync<Exception>(() => service.SetPolicyAsync(MutationApprovalPolicy.ReviewAll, cancellationToken));
            Assert.Equal(MutationApprovalPolicy.AlwaysTrustRepo, service.CurrentPolicy);
        }

        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.ConfigPath));
        fixture.AssertOnlyRepositorySettingsWritten();
    }

    /// <summary>Opening another repository redirects writes and discards only the prior in-memory session override.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task RepositorySwitch_RebindsSavedPolicyAndWriteTarget(bool planning)
    {
        using var first = new Fixture();
        using var second = new Fixture();
        var before = await File.ReadAllBytesAsync(first.ConfigPath);
        if (planning)
        {
            var service = new PlanApprovalPolicyService(first.Load(), first.ConfigPath);
            await service.SetPolicyAsync(PlanApprovalPolicy.TrustSession);
            await service.BindRepositoryAsync(second.Repository);
            Assert.Equal(PlanApprovalPolicy.AlwaysTrustRepo, service.CurrentPolicy);
            await service.SetPolicyAsync(PlanApprovalPolicy.ReviewRisky);
            Assert.Equal(PlanApprovalPolicy.ReviewRisky, new PlanApprovalPolicyService(second.Load(), second.ConfigPath).CurrentPolicy);
            await service.BindRepositoryAsync(first.Repository);
            Assert.Equal(PlanApprovalPolicy.AlwaysTrustRepo, service.CurrentPolicy);
        }
        else
        {
            var service = new MutationApprovalPolicyService(first.Load(), first.ConfigPath);
            await service.SetPolicyAsync(MutationApprovalPolicy.TrustSession);
            await service.BindRepositoryAsync(second.Repository);
            Assert.Equal(MutationApprovalPolicy.AlwaysTrustRepo, service.CurrentPolicy);
            await service.SetPolicyAsync(MutationApprovalPolicy.TrustPlan);
            Assert.Equal(MutationApprovalPolicy.TrustPlan, new MutationApprovalPolicyService(second.Load(), second.ConfigPath).CurrentPolicy);
            await service.BindRepositoryAsync(first.Repository);
            Assert.Equal(MutationApprovalPolicy.AlwaysTrustRepo, service.CurrentPolicy);
        }

        Assert.Equal(before, await File.ReadAllBytesAsync(first.ConfigPath));
        first.AssertOnlyRepositorySettingsWritten();
        second.AssertOnlyRepositorySettingsWritten();
    }

    private sealed class Fixture : IDisposable
    {
        private const string HostConfig = "{\"planning\":{\"approvalPolicy\":\"reviewAll\"},\"mutation\":{\"approvalPolicy\":\"reviewAll\"}}";
        private readonly string _machinePath;
        private readonly string _userPath;

        public Fixture()
        {
            Root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "threadsmith-policy-tests-" + Guid.NewGuid().ToString("N"))).FullName;
            Repository = Directory.CreateDirectory(Path.Combine(Root, "repo")).FullName;
            ConfigPath = Path.Combine(Directory.CreateDirectory(Path.Combine(Repository, ".threadsmith")).FullName, "config.json");
            _machinePath = Path.Combine(Root, "machine.json");
            _userPath = Path.Combine(Root, "user.json");
            File.WriteAllText(_machinePath, HostConfig);
            File.WriteAllText(_userPath, HostConfig);
            File.WriteAllText(ConfigPath, "{\"Planning\":{\"ApprovalPolicy\":\"alwaysTrustRepo\",\"approvalRepositoryIdentity\":\"legacy\",\"Note\":42},\"Mutation\":{\"ApprovalPolicy\":\"alwaysTrustRepo\",\"LargeDiffThreshold\":50},\"unrelated\":true}");
        }

        public string Root { get; }

        public string Repository { get; }

        public string ConfigPath { get; }

        public IConfigurationRoot Load() => new ConfigurationBuilder().AddJsonFile(_machinePath).AddJsonFile(_userPath).AddJsonFile(ConfigPath, optional: true).Build();

        public void AssertOnlyRepositorySettingsWritten(bool repositoryConfigExists = true)
        {
            Assert.Equal(HostConfig, File.ReadAllText(_machinePath));
            Assert.Equal(HostConfig, File.ReadAllText(_userPath));
            Assert.Equal(repositoryConfigExists ? 3 : 2, Directory.GetFiles(Root, "*", SearchOption.AllDirectories).Length);
        }

        public void Dispose()
        {
            if (!Path.GetFileName(Root).StartsWith("threadsmith-policy-tests-", StringComparison.Ordinal)
                || !string.Equals(Path.GetDirectoryName(Root), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Unexpected fixture cleanup root.");
            }

            Directory.Delete(Root, recursive: true);
        }
    }
}
