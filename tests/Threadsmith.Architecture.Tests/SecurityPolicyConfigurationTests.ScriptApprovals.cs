namespace Threadsmith.Architecture.Tests;

using System.Text.Json;
using System.Text.Json.Nodes;
using Threadsmith.App;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Policies.Configuration;
using Threadsmith.Policies.Contracts;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public sealed partial class SecurityPolicyConfigurationTests
{
    /// <summary>Script ceilings accept literal filenames, not paths, patterns or command text.</summary>
    [Theory]
    [InlineData("deployed.ps1", true)]
    [InlineData("deployed.sh", true)]
    [InlineData("naïve's script.PS1", true)]
    [InlineData("fixture script.sh", true)]
    [InlineData("program name.exe", false)]
    [InlineData("*.ps1", false)]
    [InlineData("script?.sh", false)]
    [InlineData("/tmp/script.sh", false)]
    [InlineData("C:\\scripts\\script.ps1", false)]
    [InlineData(".\\script.ps1", false)]
    [InlineData("my\nscript.ps1", false)]
    [InlineData("my\tscript.sh", false)]
    public void ScriptExecutableCeilingsAcceptOnlyLiteralBasenames(string name, bool allowed)
    {
        var document = JsonSerializer.Serialize(new { tools = new { allowedExecutables = new[] { name } } });
        File.WriteAllText(_paths.UserConfiguration, document);

        if (allowed)
        {
            Assert.Equal(name, Assert.Single(Load().AllowedExecutables));
        }
        else
        {
            Assert.Throws<InvalidDataException>(Load);
        }
    }

    /// <summary>Persistent script approval saves and replays only the exact prepared invocation.</summary>
    [Fact]
    public async Task AlwaysScriptApprovalDoesNotFollowShadowsArgumentsOrInterpreterForms()
    {
        // Arrange: retain the production configuration publication and binding lifecycle.
        var ct = TestContext.Current.CancellationToken;
        var (owner, binding, root, platform, shell) = await CreateApprovalBindingAsync(ct);
        var interpreter = OperatingSystem.IsWindows() ? "pwsh" : "sh";
        var name = OperatingSystem.IsWindows() ? "fixture script.ps1" : "fixture script.sh";
        var document = new PolicyDocument
        {
            Common = new PolicyProfile
            {
                Rules =
                [
                    new PolicyRule
                    {
                        Id = "fixture-script-interpreter",
                        Policy = PolicyOutcome.Allow,
                        Matchers = [new PolicyMatcher { Type = PolicyMatcherType.Executable, Value = interpreter }],
                    },
                    new PolicyRule
                    {
                        Id = "fixture-script-approval",
                        Policy = PolicyOutcome.ApprovalRequired,
                        Matchers = [new PolicyMatcher { Type = PolicyMatcherType.Executable, Value = name }],
                    },
                ],
            },
        };
        root["tools"]!["allowedExecutables"] = new JsonArray(shell, interpreter, name);
        root["tools"]!["runProcess"]!["commandPolicy"] = JsonNode.Parse(PolicyJson.Serialize(document));
        await File.WriteAllTextAsync(_paths.UserConfiguration, root.ToJsonString(), ct);
        await Assert.IsType<RepositoryScopedBindingCoordinator>(owner).BindRepositoryAsync(_paths.RepositoryRoot, ct);
        var script = Path.Combine(_paths.RepositoryRoot, name);
        await File.WriteAllTextAsync(script, "fixture", ct);
        var shadow = Directory.CreateDirectory(Path.Combine(_paths.RepositoryRoot, "shadow")).FullName;
        var shadowScript = Path.Combine(shadow, name);
        await File.WriteAllTextAsync(shadowScript, "shadow", ct);
        var approvals = new CommandPolicyCoordinator();
        binding.AuthorityInvalidated += approvals.InvalidateAuthority;
        approvals.AttachConfiguration(new PolicyConfigurationService(owner), () => binding.Current);
        var prompts = 0;
        var completions = new List<CommandApprovalCompletion>();
        using var handler = approvals.Attach(
            (request, token) =>
            {
                token.ThrowIfCancellationRequested();
                prompts++;
                Assert.Contains(PolicyApprovalChoice.Always, request.Choices);
                return Task.FromResult(new PolicyApprovalResponse(request.RequestId, prompts == 1 ? PolicyApprovalChoice.Always : PolicyApprovalChoice.Deny));
            },
            (completion, token) =>
            {
                token.ThrowIfCancellationRequested();
                completions.Add(completion);
                return Task.CompletedTask;
            });
        await using var events = new DomainEventStream();
        var (_, pipeline, manager, context) = CreateApprovalRuntime(binding, approvals, events, shell);
        var command = "\"" + script.Replace('\\', '/') + "\"";
        var request = new ToolInvocationRequest
        {
            SessionId = SessionId.New(),
            RunId = RunId.New(),
            ToolId = "run_process",
            ArgumentsJson = JsonSerializer.Serialize(new RunProcessInput { Command = command, WorkingDirectory = _paths.RepositoryRoot }),
            Context = context,
        };

        // Act: save, revoke live grants, and replay through the published snapshot.
        var first = await pipeline.InvokeAsync(request, ct);
        Assert.True(first.Succeeded, first.Error);
        var completion = Assert.Single(completions);
        Assert.True(completion.ContinuationApproved);
        Assert.NotNull(completion.Receipt);
        Assert.True(completion.Receipt.DiskPublished);
        Assert.True(completion.Receipt.Activated);
        var saved = JsonNode.Parse(await File.ReadAllTextAsync(_paths.UserConfiguration, ct))!;
        Assert.Single(saved["tools"]!["runProcess"]!["commandPolicy"]!["platforms"]![platform]!["shells"]![shell]!["alwaysAllow"]!.AsArray());
        approvals.Reset();
        var policy = binding.Current;
        request = request with { Context = context with { SecurityPolicy = policy, AllowedExecutables = policy.AllowedExecutables } };
        var replay = await pipeline.InvokeAsync(request, ct);
        Assert.True(replay.Succeeded, replay.Error);
        Assert.Equal(1, prompts);

        foreach (var variant in new[]
        {
            "\"" + shadowScript.Replace('\\', '/') + "\"",
            command + " changed",
            interpreter + (OperatingSystem.IsWindows() ? " -File " : " -- ") + command,
        })
        {
            var changed = request with { ArgumentsJson = JsonSerializer.Serialize(new RunProcessInput { Command = variant, WorkingDirectory = _paths.RepositoryRoot }) };
            var denied = await pipeline.InvokeAsync(changed, ct);
            Assert.False(denied.Succeeded);
            Assert.Equal(ToolErrorClassification.ApprovalDenied, denied.ErrorClassification);
        }

        // Assert: no changed invocation consumed the persisted grant or reached execution.
        Assert.Equal(4, prompts);
        Assert.Equal(2, manager.Launches);
        Assert.Equal("unchanged", saved["fixture"]!.GetValue<string>());
    }
}
