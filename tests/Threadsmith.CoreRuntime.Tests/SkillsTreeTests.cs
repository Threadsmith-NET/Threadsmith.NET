namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Interaction.Themes;
using Threadsmith.Tui.TuiKit;
using TUIKit;
using TUIKit.Input;
using TUIKit.Terminal;
using Xunit;

/// <summary>Verifies nested skill groups on the same immediate-action tree used for Tools.</summary>
[Collection("TUIKit terminal")]
public static class SkillsTreeTests
{
    /// <summary>Nested group mutations and verification target only concrete filtered members.</summary>
    [Fact]
    public static void NestedGroupsKeepFormatScopeAndFilteredActionsSeparate()
    {
        var modal = new ToggleModal(Request(), _ => CellStyle.Default, () => { }, () => { }) { SupportsActions = true };
        var cells = new CellBuffer(100, 28);
        modal.Render(new BufferSurface(cells));
        var rendered = TUIKit.Testing.Snapshot.ToText(cells);
        Assert.Contains("Native", rendered, StringComparison.Ordinal);
        Assert.Contains("Maintained", rendered, StringComparison.Ordinal);
        Assert.Contains("Repository", rendered, StringComparison.Ordinal);
        Assert.Contains("Claude", rendered, StringComparison.Ordinal);
        Assert.Equal(["review", "other", "repository"], modal.Members("group:Native").Select(option => option.Id));
        Assert.Equal(["review", "other"], modal.Members("group:Native/Maintained").Select(option => option.Id));
        Assert.Single(modal.ActionChoices("group:Native"));

        modal.HandlePaste("Review");
        Assert.Equal(["review", "repository"], modal.Members("group:Native").Select(option => option.Id));
        Assert.Equal(["review", "repository"], modal.ActionMembers("group:Native").Select(option => option.Id));
        modal.HandleKey(KeyEvent.Special(KeyCode.F3));
        Assert.True(modal.Changes.TryRead(out var verify));
        Assert.Equal("group:Native", verify.Id);
        Assert.True(verify.Actions);
        modal.CompleteChange();
        modal.Reconcile("review", new InteractionToggleResult(true, "Verified")
        {
            UpdatedOption = Request().Options[0] with { Label = "Review [Maintained] enabled", Enabled = true },
        });
        Assert.True(modal.Members("group:Native/Maintained")[0].Enabled);
        cells = new CellBuffer(100, 28);
        modal.Render(new BufferSurface(cells));
        Assert.Contains("Review [Maintained] enabled", TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
        modal.HandlePaste("missing");
        Assert.Empty(modal.ActionMembers("group:Native"));
        Assert.Empty(modal.Members("group:Native"));
        modal.HandleKey(KeyEvent.Special(KeyCode.F3));
        Assert.False(modal.Changes.TryRead(out _));
    }

    /// <summary>Status changes update cached tree rows and the next action's filtered scope together.</summary>
    [Fact]
    public static void MutableStateFilterRebuildsRowsAfterVerification()
    {
        var request = Request() with
        {
            Options = Request().Options.Select(option => option with { Label = option.Label + " [Unverified] disabled" }).ToArray(),
        };
        var modal = new ToggleModal(request, _ => CellStyle.Default, () => { }, () => { }) { SupportsActions = true };
        modal.HandlePaste("Unverified");
        var cells = new CellBuffer(100, 28);
        modal.Render(new BufferSurface(cells));
        Assert.Contains("Native", TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);

        foreach (var option in request.Options.Where(option => option.GroupPath[0] == "Native"))
        {
            modal.Reconcile(option.Id, new InteractionToggleResult(true)
            {
                UpdatedOption = option with { Label = option.Label.Replace("[Unverified] disabled", "[Maintained] enabled", StringComparison.Ordinal) },
            });
        }

        cells = new CellBuffer(100, 28);
        modal.Render(new BufferSurface(cells));
        var rendered = TUIKit.Testing.Snapshot.ToText(cells);
        Assert.DoesNotContain("Native", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Maintained", rendered, StringComparison.Ordinal);
        Assert.Contains("Claude", rendered, StringComparison.Ordinal);
        Assert.Empty(modal.ActionMembers("group:Native"));
        modal.HandleKey(KeyEvent.Special(KeyCode.F3));
        Assert.True(modal.Changes.TryRead(out var next));
        Assert.Equal("group:Claude", next.Id);
        Assert.Equal("claude", Assert.Single(modal.ActionMembers(next.Id)).Id);
    }

    /// <summary>Existing action dialogs stay leaf-only and delimiter characters cannot merge group identities.</summary>
    [Fact]
    public static void GroupActionsRequireOptInAndPathsAreUnambiguous()
    {
        var request = Request() with { AllowGroupActions = false };
        var modal = new ToggleModal(request, _ => CellStyle.Default, () => { }, () => { });
        Assert.Empty(modal.ActionChoices("group:Native"));
        Assert.Single(modal.ActionChoices("item:review"));
        request = new InteractionToggleRequest(
            "Groups",
            [
                new("flat", "One", "Native/Maintained", false),
                new("nested", "Two", "Native / Maintained", false) { GroupPath = ["Native", "Maintained"] },
            ]);
        modal = new ToggleModal(request, _ => CellStyle.Default, () => { }, () => { });
        Assert.Equal("flat", Assert.Single(modal.Members("group:Native%2FMaintained")).Id);
        Assert.Equal("nested", Assert.Single(modal.Members("group:Native/Maintained")).Id);
    }

    /// <summary>Native keyboard group verification stays open, cancels remaining targets, and allows later toggles.</summary>
    [Fact]
    public static async Task GroupVerificationCancelsAndKeepsParentModalUsable()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var backend = new HeadlessBackend(100, 28);
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], timeout.Cancel, backend);
        await surface.RunAsync(
            async token =>
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var actions = new List<string>();
            var toggles = new List<string>();
            var dialog = surface.SelectActionTogglesAsync(
                Request(),
                (id, enabled, _) =>
            {
                toggles.Add(id);
                if (id == "repository")
                {
                    changed.TrySetResult();
                }

                return Task.FromResult(new InteractionToggleResult(enabled));
            },
                async (id, action, operationToken) =>
            {
                actions.Add(id);
                Assert.Equal("verify", action);
                started.SetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, operationToken);
                }
                catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
                {
                }

                return new InteractionToggleResult(false, "Verification cancelled.");
            },
                token);
            await surface.PresentAsync(new PresentationBatch([]), token);
            backend.TakeOutput();
            backend.FeedInput("\u001bOR");
            await WaitForTextAsync("Verify");
            backend.FeedInput("\r");
            await started.Task.WaitAsync(token);
            backend.FeedInput("\u001b[27u");
            await WaitForTextAsync("Verification cancelled.");
            Assert.False(dialog.IsCompleted);
            Assert.Equal(["review"], actions);
            backend.FeedInput(" ");
            await changed.Task.WaitAsync(token);
            await WaitForTextAsync("Enabled 3/3");
            backend.FeedInput("\u001b[27u");
            await dialog;
            Assert.Equal(["review", "other", "repository"], toggles);

            async Task WaitForTextAsync(string text)
            {
                var output = string.Empty;
                while (!output.Contains(text, StringComparison.Ordinal))
                {
                    await Task.Delay(10, token);
                    output += backend.TakeOutput();
                }
            }
        },
            timeout.Token);
    }

    /// <summary>A queued dismissal between action selection and admission must prevent verification.</summary>
    [Fact]
    public static async Task DismissedParentDoesNotStartGroupVerification()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var backend = new HeadlessBackend(100, 28);
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], timeout.Cancel, backend);
        await surface.RunAsync(
            async token =>
        {
            var actions = new List<string>();
            var dialog = surface.SelectActionTogglesAsync(
                Request(),
                (_, enabled, _) => Task.FromResult(new InteractionToggleResult(enabled)),
                (id, _, _) =>
            {
                actions.Add(id);
                return Task.FromResult(new InteractionToggleResult(true));
            },
                token);
            await surface.PresentAsync(new PresentationBatch([]), token);
            backend.TakeOutput();
            backend.FeedInput("\u001bOR");
            var output = string.Empty;
            while (!output.Contains("Verify", StringComparison.Ordinal))
            {
                await Task.Delay(10, token);
                output += backend.TakeOutput();
            }

            backend.FeedInput("\r\u001b[27u");
            await dialog.WaitAsync(token);
            Assert.Empty(actions);
        },
            timeout.Token);
    }

    /// <summary>A rejected Claude skill cannot trap a large group in repeated enable requests.</summary>
    [Fact]
    public static async Task LargeClaudeGroupReportsPartialEnablementAndNextSpaceDisablesAll()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var backend = new HeadlessBackend(100, 28);
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], timeout.Cancel, backend);
        await surface.RunAsync(
            async token =>
        {
            var options = Enumerable.Range(0, 32).Select(index => new InteractionToggleOption(
                index.ToString(System.Globalization.CultureInfo.InvariantCulture), $"Skill {index}", "Claude / User", false)
            {
                GroupPath = ["Claude", "User"],
            }).ToArray();
            var changes = new List<(string Id, bool Enabled)>();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var dialog = surface.SelectTogglesAsync(
                new InteractionToggleRequest("Skills", options),
                async (id, enabled, operationToken) =>
            {
                changes.Add((id, enabled));
                if (id == "0" && enabled)
                {
                    await release.Task.WaitAsync(operationToken);
                }

                return new InteractionToggleResult(enabled && id != "31", id == "31" ? "Unsupported runtime behavior" : null);
            },
                token);
            await surface.PresentAsync(new PresentationBatch([]), token);
            backend.TakeOutput();
            backend.FeedInput(" ");
            await WaitForTextAsync("Enabling 0/32");
            backend.FeedInput(" ");
            await surface.PresentAsync(new PresentationBatch([]), token);
            release.SetResult();
            await WaitForTextAsync("Enabled 31/32; 1 not enabled");
            Assert.Equal(32, changes.Count);
            Assert.All(changes, item => Assert.True(item.Enabled));
            backend.FeedInput(" ");
            await WaitForTextAsync("Disabled 32/32");
            Assert.Equal(options.Select(option => option.Id), changes.Skip(32).Select(item => item.Id));
            Assert.All(changes.Skip(32), item => Assert.False(item.Enabled));
            backend.FeedInput("\u001b[27u");
            await dialog.WaitAsync(token);

            async Task WaitForTextAsync(string expected)
            {
                var output = string.Empty;
                while (!output.Contains(expected, StringComparison.Ordinal))
                {
                    await Task.Delay(10, token);
                    output += backend.TakeOutput();
                }
            }
        },
            timeout.Token);
    }

    /// <summary>Cancelling a batch keeps acknowledged states and admits a later disable request.</summary>
    [Fact]
    public static async Task GroupToggleCancellationPreservesCompletedChangesAndAllowsRetry()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var backend = new HeadlessBackend(100, 28);
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], timeout.Cancel, backend);
        await surface.RunAsync(
            async token =>
        {
            var changes = new List<(string Id, bool Enabled)>();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken inFlight = default;
            var dialog = surface.SelectTogglesAsync(
                Request(),
                async (id, enabled, operationToken) =>
            {
                changes.Add((id, enabled));
                if (id == "other" && enabled)
                {
                    inFlight = operationToken;
                    started.SetResult();
                    await release.Task.WaitAsync(operationToken);
                }

                return new InteractionToggleResult(enabled);
            },
                token);
            await surface.PresentAsync(new PresentationBatch([]), token);
            backend.TakeOutput();
            backend.FeedInput(" ");
            await started.Task.WaitAsync(token);
            backend.FeedInput("\u001b[27u");
            await WaitForTextAsync("Stopping after current item...");
            Assert.False(inFlight.IsCancellationRequested);
            release.SetResult();
            await WaitForTextAsync("Cancelled: Enabled 2/3; 1 not processed");
            Assert.False(dialog.IsCompleted);
            Assert.Equal([("review", true), ("other", true)], changes);
            backend.FeedInput(" ");
            await WaitForTextAsync("Disabled 3/3");
            Assert.Equal([("review", false), ("other", false), ("repository", false)], changes.Skip(2));
            backend.FeedInput("\u001b[27u");
            await dialog.WaitAsync(token);

            async Task WaitForTextAsync(string expected)
            {
                var output = string.Empty;
                while (!output.Contains(expected, StringComparison.Ordinal))
                {
                    await Task.Delay(10, token);
                    output += backend.TakeOutput();
                }
            }
        },
            timeout.Token);
    }

    /// <summary>Skills and tools use the same activity snapshot and retain other running operations on completion.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task SkillInvocationProgress_UsesSharedToolBlocksAndPreservesOtherActivities(bool fail)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var backend = new HeadlessBackend(120, 28);
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], timeout.Cancel, backend);
        await surface.RunAsync(
            async token =>
        {
            var activity = InteractionPresentationFormatter.CreateOperationActivity("SKILLS", "review", "Invocation: fixture", TimeProvider.System, true);
            var tool = InteractionPresentationFormatter.CreateOperationActivity("TOOLS", "read_file", "README.md", TimeProvider.System, true);
            await surface.PresentToolActivitiesAsync([activity, tool], token);
            var output = string.Empty;
            while (!output.Contains("Invocation: fixture", StringComparison.Ordinal) || !output.Contains("README.md", StringComparison.Ordinal))
            {
                await Task.Delay(10, token);
                output += backend.TakeOutput();
            }

            Assert.Contains("SKILLS: review - running", output, StringComparison.Ordinal);
            Assert.Contains("TOOLS: read_file - running", output, StringComparison.Ordinal);
            await surface.PresentToolActivitiesAsync([], token);
            await surface.PresentToolActivitiesAsync([tool], token);
            await surface.PresentAsync(new PresentationBatch([]), token);
            backend.TakeOutput();
            var completedText = InteractionPresentationFormatter.FormatOperationCompletion("SKILLS", "review", "Invocation: fixture", fail ? "failed" : "completed", null);
            var completedRole = fail ? PresentationTextRole.Warning : PresentationTextRole.Success;
            await surface.PresentAsync(new PresentationBatch([new PresentationTextItem([new(completedText, completedRole)])]), token);

            backend.TakeOutput();
            backend.Resize(121, 28);
            output = string.Empty;
            while (!output.Contains("TOOLS: read_file - running", StringComparison.Ordinal))
            {
                await Task.Delay(10, token);
                output += backend.TakeOutput();
            }

            Assert.DoesNotContain("SKILLS: review - running", output, StringComparison.Ordinal);

            await surface.PresentToolActivitiesAsync([], token);
        },
            timeout.Token);
    }

    private static InteractionToggleRequest Request() => new(
        "Skills - checked means enabled",
        [
            new("review", "Review", "Native / Maintained", false) { GroupPath = ["Native", "Maintained"], Actions = [new("verify", "Verify")] },
            new("other", "Other skill", "Native / Maintained", false) { GroupPath = ["Native", "Maintained"], Actions = [new("verify", "Verify")] },
            new("repository", "Review repository", "Native / Repository", false) { GroupPath = ["Native", "Repository"], Actions = [new("verify", "Verify")] },
            new("claude", "Review compatible", "Claude / Repository", false) { GroupPath = ["Claude", "Repository"], Actions = [new("verify", "Verify")] },
        ]) { AllowGroupActions = true };
}
