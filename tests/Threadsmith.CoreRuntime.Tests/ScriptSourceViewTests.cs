namespace Threadsmith.CoreRuntime.Tests;

using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Interaction.Agents;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Interaction.Themes;
using Threadsmith.Tui.TuiKit;
using TUIKit;
using TUIKit.Hosting;
using TUIKit.Input;
using Xunit;

/// <summary>Exercises bounded source display and exact source copy.</summary>
[Collection("TUIKit terminal")]
public sealed class ScriptSourceViewTests
{
    private static readonly int[] SourceViewerWidths = [100, 70, 100];

    /// <summary>Preview bounds count logical lines.</summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(20, false)]
    [InlineData(21, true)]
    public void PreviewUsesLogicalLines(int count, bool truncated)
    {
        var source = string.Join("\r\n", Enumerable.Range(1, count).Select(index => $"line {index}"));
        var preview = SourceDocument.Preview(source);
        Assert.Equal(truncated, preview.Truncated);
        Assert.True(preview.Text.Split("\r\n").Length <= 20);
        Assert.Equal(string.Join("\r\n", Enumerable.Range(1, Math.Min(count, 20)).Select(index => $"line {index}")), preview.Text);
    }

    /// <summary>Every display newline form is bounded before expansion and cannot evict output.</summary>
    [Theory]
    [InlineData("\r\n")]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\v")]
    [InlineData("\f")]
    [InlineData("\u0085")]
    [InlineData("\u2028")]
    [InlineData("\u2029")]
    public void PreviewBoundsAllDisplayNewlines(string separator)
    {
        var twenty = string.Join(separator, Enumerable.Repeat("// line", 20));
        Assert.False(SourceDocument.Preview(twenty).Truncated);
        Assert.True(SourceDocument.Preview(twenty + separator + "// last").Truncated);
        var item = Item(string.Join(separator, Enumerable.Repeat("x", 1100)));
        var view = new TranscriptView();
        view.Present(new([item]));
        Click(view, Render(view), "[+]");
        var screen = Render(view);
        Assert.Contains("Result", screen, StringComparison.Ordinal);
        Assert.Contains("[-] C# script", screen, StringComparison.Ordinal);
        Assert.Contains("Preview truncated", screen, StringComparison.Ordinal);
        Assert.Equal(0, view.Evicted);
    }

    /// <summary>Character bounds preserve surrogate pairs.</summary>
    [Theory]
    [InlineData(8192, false)]
    [InlineData(8193, true)]
    public void PreviewHasIndependentCharacterLimit(int length, bool truncated)
    {
        var preview = SourceDocument.Preview(new string('x', length));
        Assert.Equal(Math.Min(length, 8192), preview.Text.Length);
        Assert.Equal(truncated, preview.Truncated);
        Assert.Equal(8191, SourceDocument.Preview(new string('x', 8191) + "😀").Text.Length);
    }

    /// <summary>Actions keep their identity and reflow reuses highlighting.</summary>
    [Fact]
    public void CollapsedActionBindsEachInvocationAndHighlightingIsCached()
    {
        var highlighter = new CountingHighlighter();
        var view = new TranscriptView(highlighter: highlighter);
        var first = Item("first source");
        var second = Item("second source");
        var opened = new List<ToolSourceDetail>();
        view.SourceRequested = opened.Add;
        view.Present(new PresentationBatch([first, second]));
        var screen = Render(view);
        Assert.DoesNotContain("first source", screen, StringComparison.Ordinal);
        Assert.Equal(0, highlighter.Count);
        view.HandleKey(KeyEvent.Special(KeyCode.Down, KeyModifiers.Ctrl));
        view.HandleKey(KeyEvent.Special(KeyCode.Down, KeyModifiers.Ctrl));
        view.HandleKey(KeyEvent.Special(KeyCode.Enter));
        Assert.Same(first.SourceDetail, Assert.Single(opened));
        Click(view, Render(view), "[+]", firstMatch: false);
        Assert.Contains("second source", Render(view), StringComparison.Ordinal);
        Render(view, 70);
        Render(view, 100);
        Assert.Equal(1, highlighter.Count);
        Click(view, Render(view), "View full", firstMatch: false);
        Assert.Same(second.SourceDetail, opened[1]);
        Assert.DoesNotContain("first source", JsonSerializer.Serialize(first), StringComparison.Ordinal);
    }

    /// <summary>Selection and full copy use original text.</summary>
    [Theory]
    [InlineData("a\r\n\tb  \r世界😀\n")]
    [InlineData("[red]<tag>\u001b[31m\t  \n\n")]
    public void SourceSelectionAndFullCopyPreserveRawTextAfterReflow(string source)
    {
        string? copied = null;
        var modal = new ChoiceModal("script", [], sourceDetail: Item(source).SourceDetail)
        {
            CopyRequested = value =>
            {
                copied = value;
                return true;
            },
        };
        Render(modal);
        modal.HandleKey(KeyEvent.Char('a', KeyModifiers.Ctrl));
        modal.HandleKey(KeyEvent.Special(KeyCode.F6));
        Assert.Equal(source, copied);
        Render(modal, 55);
        modal.HandleKey(KeyEvent.Char('a', KeyModifiers.Ctrl));
        modal.HandleKey(KeyEvent.Special(KeyCode.F6));
        Assert.Equal(source, copied);
        modal.HandleKey(KeyEvent.Special(KeyCode.F4));
        Assert.Equal(source, copied);
        Assert.DoesNotContain('\u001b', Render(modal));
        modal.HandleKey(KeyEvent.Special(KeyCode.Escape));
        Assert.True(modal.IsClosed);
    }

    /// <summary>Full view has dedicated capacity and respects clipboard policy.</summary>
    [Fact]
    public void SourceModalRetainsMoreThanNormalTranscriptLinesAndReportsClipboardLimit()
    {
        var source = string.Concat(Enumerable.Repeat("// é\r\n", 1400)) + "// end";
        var copyCalls = 0;
        var modal = new ChoiceModal("script", [], new TuiResourceLimits { MaximumClipboardCopyBytes = 10 }, sourceDetail: Item(source).SourceDetail)
        {
            CopyRequested = _ =>
            {
                copyCalls++;
                return true;
            },
        };
        Render(modal);
        modal.HandleKey(KeyEvent.Special(KeyCode.End));
        Assert.Contains("// end", Render(modal), StringComparison.Ordinal);
        modal.HandleKey(KeyEvent.Special(KeyCode.F4));
        Assert.Equal(0, copyCalls);
        Assert.Contains("Copy blocked", Render(modal), StringComparison.Ordinal);
    }

    /// <summary>Failed highlighting does not change copy offsets.</summary>
    [Fact]
    public void FallbackAndOffsetMappingKeepSourceReadableAndExact()
    {
        const string source = "\tfirst\r\nsecond\rthird\n";
        var document = new SourceDocument(source, "csharp", new ThrowingHighlighter());
        Assert.Equal("    first\nsecond\nthird\n", document.DisplaySource);
        Assert.Equal(new SourceHighlightSpan(0, document.DisplaySource.Length, PresentationTextRole.MarkdownCode), Assert.Single(document.Ranges));
        Assert.Equal(source, document.CopyRange(0, document.DisplaySource.Length));
        Assert.Equal("\tfirst\r\n", document.CopyRange(0, 10));
        var unknown = new SourceDocument(source, "unknown", new TuiKitSourceHighlighter());
        Assert.Equal(document.DisplaySource, unknown.DisplaySource);
        var highlighted = new TuiKitSourceHighlighter().Highlight("class Example { string value = \"text\"; } // comment", "csharp");
        Assert.Contains(highlighted, range => range.Role == PresentationTextRole.Status);
        Assert.Contains(highlighted, range => range.Role == PresentationTextRole.Muted);
    }

    /// <summary>Retention and mismatched identities cannot expose stale source.</summary>
    [Fact]
    public void RetentionDropsSourceWithoutReadingSavedPathsOrResurrectingActions()
    {
        var item = Item(new string('x', 1000));
        var view = new TranscriptView(new TuiResourceLimits { MaximumTranscriptBytes = 256 });
        view.Present(new PresentationBatch([item]));
        Assert.Contains("source unavailable", Render(view), StringComparison.Ordinal);
        Assert.DoesNotContain("View full", Render(view), StringComparison.Ordinal);
        view.Present(new PresentationBatch([]) { ReplaceOutput = true });
        Assert.DoesNotContain("C# script", Render(view), StringComparison.Ordinal);
        var completion = new ToolInvocationCompleted(SessionId.New(), DateTimeOffset.UtcNow, ToolInvocationId.New(), true) { SourceDetail = item.SourceDetail };
        Assert.Null(InteractionEventSegments.GetSourceDetail(completion));
    }

    /// <summary>Evicting a source attachment must not remove later diagnostic output.</summary>
    [Fact]
    public void SourceEvictionPreservesFollowingDiagnosticWithoutReprojection()
    {
        var view = new TranscriptView(new TuiResourceLimits { MaximumTranscriptBytes = 256 });
        view.Present(new([Item(new string('x', 200))]));
        Render(view);
        view.Present(new([new PresentationTextItem([new("Keep diagnostic\n", PresentationTextRole.Warning)])]));

        // Evict the source item by retained bytes, not its newer diagnostic or projected rows.
        view.Present(new([new PresentationTextItem([new(new string('y', 60), PresentationTextRole.Default)])]));

        Assert.Contains("Keep diagnostic", view.Lines);
        Assert.DoesNotContain(view.Lines, line => line.Contains("View full", StringComparison.Ordinal));
        Assert.Contains("Keep diagnostic", Render(view), StringComparison.Ordinal);
    }

    /// <summary>Normalized previews respect byte, logical-line and continued-chunk budgets.</summary>
    [Theory]
    [InlineData(256, 64, 16384, true)]
    [InlineData(4096, 12, 16384, false)]
    [InlineData(4096, 5, 16384, false)]
    [InlineData(4096, 12, 32, true)]
    public void OversizedPreviewPreservesResultAndControls(int bytes, int lines, int lineCharacters, bool tabs)
    {
        var source = tabs ? "//" + new string('\t', 100) + "\n42"
            : string.Join('\n', Enumerable.Repeat("// line", 20));
        var view = new TranscriptView(new TuiResourceLimits
        {
            MaximumTranscriptBytes = bytes,
            MaximumTranscriptLines = lines,
            MaximumTranscriptLineCharacters = lineCharacters,
        });
        var item = Item(source);
        ToolSourceDetail? opened = null;
        view.SourceRequested = detail => opened = detail;
        view.Present(new([item]));

        Click(view, Render(view), "[+]");

        foreach (var width in new[] { 110, 70, 110 })
        {
            var screen = Render(view, width);
            Assert.Contains("Result", screen, StringComparison.Ordinal);
            Assert.Contains("[-] C# script", screen, StringComparison.Ordinal);
            Assert.Contains("Preview omitted", screen, StringComparison.Ordinal);
            Assert.Contains(view.Lines, line => line.StartsWith("View full", StringComparison.Ordinal));
            Assert.Equal(0, view.Evicted);
        }

        Click(view, Render(view), "View full");
        Assert.Same(item.SourceDetail, opened);
        Click(view, Render(view), "[-]");
        Assert.Contains("[+] C# script", Render(view), StringComparison.Ordinal);
    }

    /// <summary>The minimum omission marker uses no more bytes or rows than collapsed controls.</summary>
    [Fact]
    public void TightByteBudgetUsesExistingHeaderForOmission()
    {
        var view = new TranscriptView(new TuiResourceLimits { MaximumTranscriptBytes = 256 });
        var item = Item(string.Join('\n', Enumerable.Repeat("x", 20))) with
        {
            Segments = [new("Result" + new string('r', 204) + "\n", PresentationTextRole.Success)],
        };
        ToolSourceDetail? opened = null;
        view.SourceRequested = detail => opened = detail;
        view.Present(new([item]));
        Click(view, Render(view), "[+]");

        foreach (var width in new[] { 110, 70, 110 })
        {
            var screen = Render(view, width);
            Assert.Contains("Result", screen, StringComparison.Ordinal);
            Assert.Contains("[!] C# script", screen, StringComparison.Ordinal);
            Assert.Equal(0, view.Evicted);
        }

        Click(view, Render(view), "View full");
        Assert.Same(item.SourceDetail, opened);
        Click(view, Render(view), "[!]");
        Assert.Contains("[+] C# script", Render(view), StringComparison.Ordinal);
    }

    /// <summary>Many source controls retain their own rows across disclosure and width changes.</summary>
    [Fact]
    public void ManySourceInvocationsSurviveReprojectionAndReuseCachedHighlighting()
    {
        var highlighter = new CountingHighlighter();
        var view = new TranscriptView(highlighter: highlighter);
        var items = Enumerable.Range(0, 80).Select(index => Item($"// source {index}")).ToArray();
        view.Present(new(items));
        Render(view);
        view.HandleKey(KeyEvent.Special(KeyCode.Home));
        Click(view, Render(view), "[+]");

        foreach (var width in new[] { 70, 110, 80 })
        {
            Render(view, width);
            Assert.Equal(items.Length, view.Lines.Count(line => line == "View full script…"));
            Assert.Equal(items.Length, view.Lines.Count(line => line.Contains("C# script", StringComparison.Ordinal)));
            Assert.Contains("// source 0", view.Lines);
            Assert.Equal(0, view.Evicted);
        }

        Assert.Equal(1, highlighter.Count);
    }

    /// <summary>Batched styles track normalized offsets across continued chunks and logical newlines.</summary>
    [Fact]
    public void BatchedSourceStylesSurviveChunkingAndResize()
    {
        var styles = new TuiKitStyles(BuiltInThemes.Create()[0], false);
        var view = new TranscriptView(new TuiResourceLimits { MaximumTranscriptLineCharacters = 4 })
        {
            ResolveStyle = styles.Resolve,
        };
        view.PresentSource(new SourceDocument("\tclass\r\n42", "csharp", new TuiKitSourceHighlighter()));
        foreach (var width in new[] { 80, 60, 80 })
        {
            var cells = new CellBuffer(width, 12);
            view.Render(new BufferSurface(cells));
            Assert.Equal(["    ", "clas", "s", "42"], view.Lines);
            Assert.Equal(styles.Resolve(PresentationTextRole.Status).Foreground, cells.Get(0, 1).Style.Foreground);
            Assert.Equal(styles.Resolve(PresentationTextRole.Status).Foreground, cells.Get(0, 2).Style.Foreground);
            Assert.Equal(styles.Resolve(PresentationTextRole.Warning).Foreground, cells.Get(0, 3).Style.Foreground);
        }
    }

    /// <summary>Token-dense source uses bounded existing chunks and remains exact across width changes.</summary>
    [Fact]
    public void NearLimitTokenDenseSourceSurvivesChunkingAndReflow()
    {
        var source = "var values = new[]{" + string.Concat(Enumerable.Repeat("1,", 32740)) + "0};";
        Assert.InRange(source.Length, 65000, ToolSourceDetail.MaximumCharacters);
        string? copied = null;
        var modal = new ChoiceModal("script", [], sourceDetail: Item(source).SourceDetail)
        {
            CopyRequested = text =>
            {
                copied = text;
                return true;
            },
        };
        Render(modal);
        Render(modal, 70);
        modal.HandleKey(KeyEvent.Special(KeyCode.End));
        Assert.Contains("0};", Render(modal, 70), StringComparison.Ordinal);
        modal.HandleKey(KeyEvent.Char('a', KeyModifiers.Ctrl));
        modal.HandleKey(KeyEvent.Special(KeyCode.F6));
        Assert.Equal(source, copied);
        modal.HandleKey(KeyEvent.Special(KeyCode.F4));
        Assert.Equal(source, copied);
    }

    /// <summary>Pointer selection uses the modal's content coordinates.</summary>
    [Fact]
    public void ModalMouseSelectionCopiesOriginalRange()
    {
        string? copied = null;
        var modal = new ChoiceModal("script", [], sourceDetail: Item("\talpha\r\nbeta").SourceDetail)
        {
            CopyRequested = value =>
            {
                copied = value;
                return true;
            },
        };
        var rows = Render(modal).Split('\n');
        var y = Array.FindIndex(rows, row => row.Contains("alpha", StringComparison.Ordinal));
        var x = rows[y].IndexOf("alpha", StringComparison.Ordinal);
        modal.HandleMouse(new(MouseEventKind.Press, MouseButton.Left, x, y, KeyModifiers.None, 1));
        modal.HandleMouse(new(MouseEventKind.Release, MouseButton.Left, x + 5, y, KeyModifiers.None, 1));
        modal.HandleKey(KeyEvent.Special(KeyCode.F6));
        Assert.Equal("alpha", copied);
    }

    /// <summary>Clipboard policy counts UTF-8 bytes, not source characters.</summary>
    [Theory]
    [InlineData('x', 65536, true)]
    [InlineData('é', 32768, true)]
    [InlineData('é', 32769, false)]
    public void FullCopyHonorsUtf8Boundary(char character, int length, bool allowed)
    {
        var source = new string(character, length);
        string? copied = null;
        var modal = new ChoiceModal("script", [], sourceDetail: Item(source).SourceDetail)
        {
            CopyRequested = value =>
            {
                copied = value;
                return true;
            },
        };
        modal.HandleKey(KeyEvent.Special(KeyCode.F4));
        Assert.Equal(allowed ? source : null, copied);
        if (!allowed)
        {
            Assert.Contains("Copy blocked", Render(modal), StringComparison.Ordinal);
        }
    }

    /// <summary>MAIN and child output use the same invocation-bound action.</summary>
    [Fact]
    public void ChildAndMainViewsOpenTheirOwnSource()
    {
        var opened = new List<ToolSourceDetail>();
        var views = new AgentViews(_ => CellStyle.Default, sourceRequested: opened.Add);
        var session = SessionId.New();
        views.Attach(session);
        var target = new AgentPresentationTarget(session, DelegationId.New(), AgentAssignmentId.New(), RunId.New(), 1);
        views.Update(new(target, "child", AgentRole.Explorer, AgentRunStatus.Running, 1));
        var main = Item("main");
        var child = Item("child");
        views.Present(new([main]));
        views.Present(new([child]) { Target = target });
        var childView = views.Ordered.Single(view => view.Snapshot is not null);
        Click(childView.Transcript, Render(childView.Transcript), "View full");
        Click(views.Main.Transcript, Render(views.Main.Transcript), "View full");
        Assert.Equal([child.SourceDetail!, main.SourceDetail!], opened);
    }

    /// <summary>The utility modal preserves the draft and uses the existing clipboard transport.</summary>
    [Fact]
    public async Task SurfaceOpensSourceAndRestoresComposer()
    {
        const string source = "// full-source-copy\r\n\t42";
        using var backend = new ObservedBackend(100, 30);
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], () => throw new InvalidOperationException("Unexpected interrupt"), backend);
        await surface.RunAsync(
            async token =>
            {
                await surface.PresentAsync(new([Item(source)]), token);
                var input = surface.ReadComposerAsync(new ComposerRequest("ordinary"), token);
                await surface.PresentAsync(new([]), token);
                backend.FeedInput("draft\u001b[18~\u001b[1;5B\u001b[1;5B\r\u001bOS\u001b[27u\u001b[18~\r");
                Assert.Equal("draft", (await input).Text);
            },
            TestContext.Current.CancellationToken);
        Assert.Contains(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(source)), backend.TakeOutput(), StringComparison.Ordinal);
    }

    /// <summary>The full-source action keeps modal chrome and uses the themed output background for code.</summary>
    [Theory]
    [InlineData("black")]
    [InlineData("blue")]
    [InlineData("default-theme")]
    public async Task SurfaceSourceViewerUsesOutputBackgroundInsideStandardModal(string outputBackground)
    {
        // Arrange: make inherited modal and output backgrounds visibly different.
        var theme = outputBackground == "default-theme" ? BuiltInThemes.CreateDefault() : new ConfiguredTheme(
            "Source modal test",
            new TuiTheme(
                "source-modal-test",
                [
                    new(PresentationTextRole.Default, new(TuiColor.Parse("green"), TuiColor.Parse("magenta"))),
                    new(PresentationTextRole.OutputStreamPaneRole, new(Background: TuiColor.Parse(outputBackground))),
                    new(PresentationTextRole.Status, new(TuiColor.Parse("yellow"), TuiColor.Parse("red"))),
                ]),
            TuiThemeUi.Default,
            false);
        var suppress = TuiThemeResolver.ShouldSuppressStyles(false, Environment.GetEnvironmentVariable("NO_COLOR"), Environment.GetEnvironmentVariable("TERM"));
        var styles = new TuiKitStyles(theme, suppress);
        using var backend = new ObservedBackend(100, 30);
        await using var surface = new TuiKitSurface(theme, () => throw new InvalidOperationException("Unexpected interrupt"), backend);
        var application = (TuiApplication)typeof(TuiKitSurface).GetField("_app", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(surface)!;
        await surface.RunAsync(
            async token =>
            {
                const string source = "new[] { 4, 7, 100 }.Average()\n\nvar text = \"value\"; // modal source";
                await surface.PresentAsync(new([Item(source)]), token);
                var input = surface.ReadComposerAsync(new ComposerRequest("ordinary"), token);
                await surface.PresentAsync(new([]), token);

                // Act through the real transcript action, then snapshot on the UI owner.
                backend.FeedInput("\u001b[18~\u001b[1;5B\u001b[1;5B\r");
                _ = await backend.WaitForOutputAsync("F4 Copy full script", token);
                foreach (var width in SourceViewerWidths)
                {
                    var snapshot = new TaskCompletionSource<CellBuffer>(TaskCreationOptions.RunContinuationsAsynchronously);
                    application.Post(() =>
                    {
                        var frame = new CellBuffer(width, 30);
                        application.Modals.Top?.Render(new BufferSurface(frame));
                        snapshot.SetResult(frame);
                    });
                    var cells = await snapshot.Task.WaitAsync(token);

                    // Assert frame, padding and hint retain the standard modal appearance.
                    var bounds = ModalFrame.Bounds(new(width, 30), expandHeight: true, preferredSize: new(90, 24));
                    Assert.Equal(styles.Resolve(PresentationTextRole.Default), cells.Get(bounds.X, bounds.Y).Style);
                    Assert.Equal(styles.Resolve(PresentationTextRole.Default), cells.Get(bounds.X + 1, bounds.Y + 1).Style);
                    var rows = TUIKit.Testing.Snapshot.ToText(cells).Split('\n');
                    var hintY = Array.FindIndex(rows, row => row.Contains("F4 Copy full script", StringComparison.Ordinal));
                    Assert.True(hintY >= 0);
                    Assert.Equal(styles.Resolve(PresentationTextRole.Status), cells.Get(bounds.X + 2, hintY).Style);
                    Assert.Contains("// modal source", string.Join('\n', rows), StringComparison.Ordinal);

                    // Every source cell, including tokens, spaces, blank lines and unused area,
                    // uses the output-pane background while retaining syntax foregrounds.
                    var content = WorkspaceLayout.ModalContent(new(bounds.Width, bounds.Height));
                    var sourceX = bounds.X + content.X;
                    var body = WorkspaceLayout.ModalBody(new(content.Width, content.Height), spaceAfterSubtitle: true);
                    var sourceY = bounds.Y + content.Y + body.Y;
                    var background = styles.Resolve(PresentationTextRole.OutputStreamPaneRole).Background;
                    Assert.True(string.IsNullOrWhiteSpace(rows[sourceY - 1].Substring(sourceX, content.Width)));
                    for (var y = sourceY; y < sourceY + body.Height; y++)
                    {
                        for (var x = sourceX; x < sourceX + content.Width; x++)
                        {
                            Assert.Equal(background, cells.Get(x, y).Style.Background);
                        }
                    }

                    Assert.Equal(styles.Resolve(PresentationTextRole.Status).Foreground, cells.Get(sourceX, sourceY).Style.Foreground);
                    Assert.Equal(styles.Resolve(PresentationTextRole.MarkdownCode).Foreground, cells.Get(sourceX + 3, sourceY).Style.Foreground);
                    Assert.Equal(styles.Resolve(PresentationTextRole.Warning).Foreground, cells.Get(sourceX + 8, sourceY).Style.Foreground);
                    Assert.Equal(styles.Resolve(PresentationTextRole.Success).Foreground, cells.Get(sourceX + 11, sourceY + 2).Style.Foreground);
                    Assert.Equal(styles.Resolve(PresentationTextRole.Muted).Foreground, cells.Get(sourceX + 20, sourceY + 2).Style.Foreground);
                }

                backend.FeedInput("\u001b[27u\u001b[18~\r");
                _ = await input;
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>Source keeps Skills heading and footer styles, with a blank row before the code.</summary>
    [Theory]
    [InlineData(110, 40, false)]
    [InlineData(55, 16, false)]
    [InlineData(40, 12, false)]
    [InlineData(110, 40, true)]
    public void SourceViewerMatchesSkillsShell(int width, int height, bool suppress)
    {
        // Arrange
        var styles = new TuiKitStyles(BuiltInThemes.CreateDefault(), suppress);
        var skills = new ToggleModal(new("Skills", []), styles.Resolve, () => { }, () => { });
        var source = new ChoiceModal("C# script", [], sourceDetail: Item("// source").SourceDetail)
        {
            ResolveStyle = styles.Resolve,
        };
        var skillsCells = new CellBuffer(width, height);
        var sourceCells = new CellBuffer(width, height);

        // Act
        skills.Render(new BufferSurface(skillsCells));
        source.Render(new BufferSurface(sourceCells));

        // Assert
        var bounds = ModalFrame.Bounds(new(width, height), expandHeight: true, preferredSize: new(90, 24));
        var inner = WorkspaceLayout.ModalContent(new(bounds.Width, bounds.Height));
        var skillsBounds = ModalFrame.Bounds(new(width, height));
        var skillsInner = WorkspaceLayout.ModalContent(new(skillsBounds.Width, skillsBounds.Height));
        var skillsX = skillsBounds.X + skillsInner.X;
        var skillsY = skillsBounds.Y + skillsInner.Y;
        var x = bounds.X + inner.X;
        var y = bounds.Y + inner.Y;
        var rows = TUIKit.Testing.Snapshot.ToText(sourceCells).Split('\n');
        Assert.StartsWith("C# script", rows[y][x..], StringComparison.Ordinal);
        Assert.True(string.IsNullOrWhiteSpace(rows[y + 1].Substring(x, inner.Width)));
        Assert.StartsWith("Read-only C# source", rows[y + 2][x..], StringComparison.Ordinal);
        Assert.True(string.IsNullOrWhiteSpace(rows[y + 3].Substring(x, inner.Width)));
        Assert.StartsWith("// source", rows[y + 4][x..], StringComparison.Ordinal);
        Assert.StartsWith("F4 Copy full script", rows[y + inner.Height - 2][x..], StringComparison.Ordinal);
        Assert.StartsWith("Arrows/PgUp/PgDn", rows[y + inner.Height - 1][x..], StringComparison.Ordinal);
        Assert.Equal(styles.Resolve(PresentationTextRole.SelectionPrompt), sourceCells.Get(x, y).Style);
        foreach (var row in new[] { 0, 2, -2, -1 })
        {
            var skillsRow = skillsY + (row < 0 ? skillsInner.Height + row : row);
            var sourceRow = y + (row < 0 ? inner.Height + row : row);
            Assert.Equal(skillsCells.Get(skillsX, skillsRow).Style, sourceCells.Get(x, sourceRow).Style);
        }

        for (var row = bounds.Y; row < bounds.Y + bounds.Height; row++)
        {
            Assert.Equal(styles.Resolve(PresentationTextRole.Default), sourceCells.Get(bounds.X, row).Style);
            Assert.Equal(styles.Resolve(PresentationTextRole.Default), sourceCells.Get(bounds.X + bounds.Width - 1, row).Style);
        }
    }

    /// <summary>Overflow stays inside the body and remains reachable with keyboard and wheel.</summary>
    [Theory]
    [InlineData(110, 40)]
    [InlineData(55, 16)]
    [InlineData(40, 12)]
    public void SourceViewerScrollsWithinSharedBody(int width, int height)
    {
        // Arrange
        var source = string.Join('\n', Enumerable.Range(0, 150).Select(index => $"// source {index:D3}"));
        var modal = new ChoiceModal("C# script", [], sourceDetail: Item(source).SourceDetail);
        var initial = Render(modal, width, height);
        Assert.Contains("// source 000", initial, StringComparison.Ordinal);
        Assert.DoesNotContain("// source 149", initial, StringComparison.Ordinal);

        // Act and assert each scroll operation at its rendered completion boundary.
        modal.HandleKey(KeyEvent.Special(KeyCode.Down));
        Assert.DoesNotContain("// source 000", Render(modal, width, height), StringComparison.Ordinal);
        modal.HandleKey(KeyEvent.Special(KeyCode.Up));
        Assert.Equal(initial, Render(modal, width, height));
        modal.HandleKey(KeyEvent.Special(KeyCode.PageDown));
        Assert.DoesNotContain("// source 000", Render(modal, width, height), StringComparison.Ordinal);
        modal.HandleKey(KeyEvent.Special(KeyCode.PageUp));
        Assert.Equal(initial, Render(modal, width, height));
        modal.HandleKey(KeyEvent.Special(KeyCode.End));
        Assert.Contains("// source 149", Render(modal, width, height), StringComparison.Ordinal);

        var bounds = ModalFrame.Bounds(new(width, height), expandHeight: true, preferredSize: new(90, 24));
        var inner = WorkspaceLayout.ModalContent(new(bounds.Width, bounds.Height));
        var body = WorkspaceLayout.ModalBody(new(inner.Width, inner.Height), spaceAfterSubtitle: true);
        var x = bounds.X + inner.X;
        var y = bounds.Y + inner.Y + body.Y;
        modal.HandleMouse(new(MouseEventKind.Wheel, MouseButton.WheelUp, x, y, KeyModifiers.None, 0));
        Assert.DoesNotContain("// source 149", Render(modal, width, height), StringComparison.Ordinal);
        modal.HandleMouse(new(MouseEventKind.Wheel, MouseButton.WheelDown, x, y, KeyModifiers.None, 0));
        var end = Render(modal, width, height).Split('\n');
        Assert.Contains("// source 149", string.Join('\n', end), StringComparison.Ordinal);
        var start = initial.Split('\n');
        for (var row = 0; row < height; row++)
        {
            if (row < y || row >= y + body.Height)
            {
                Assert.Equal(start[row], end[row]);
            }
        }

        modal.HandleKey(KeyEvent.Special(KeyCode.Home));
        Assert.Equal(initial, Render(modal, width, height));
    }

    private static PresentationTextItem Item(string source)
    {
        return new([new("Result\n", PresentationTextRole.Success)])
        {
            SourceDetail = new ToolSourceDetail
            {
                ToolInvocationId = ToolInvocationId.New(),
                Language = "csharp",
                Availability = ToolSourceAvailability.Available,
                Source = source,
            },
        };
    }

    private static string Render(TranscriptView view, int width = 110)
    {
        var buffer = new CellBuffer(width, 40);
        view.Render(new BufferSurface(buffer));
        return TUIKit.Testing.Snapshot.ToText(buffer);
    }

    private static string Render(ChoiceModal modal, int width = 110, int height = 40)
    {
        var buffer = new CellBuffer(width, height);
        modal.Render(new BufferSurface(buffer));
        return TUIKit.Testing.Snapshot.ToText(buffer);
    }

    private static void Click(TranscriptView view, string screen, string label, bool firstMatch = true)
    {
        var rows = screen.Split('\n');
        var index = firstMatch ? Array.FindIndex(rows, row => row.Contains(label, StringComparison.Ordinal))
            : Array.FindLastIndex(rows, row => row.Contains(label, StringComparison.Ordinal));
        Assert.True(index >= 0, screen);
        view.HandleMouse(new(MouseEventKind.Press, MouseButton.Left, 1, index, KeyModifiers.None, 1));
    }

    private sealed class CountingHighlighter : ISourceHighlighter
    {
        public int Count { get; private set; }

        public IReadOnlyList<SourceHighlightSpan> Highlight(string source, string language)
        {
            Count++;
            return [];
        }
    }

    private sealed class ThrowingHighlighter : ISourceHighlighter
    {
        public IReadOnlyList<SourceHighlightSpan> Highlight(string source, string language)
        {
            throw new InvalidOperationException("unsupported display");
        }
    }
}
