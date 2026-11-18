namespace Threadsmith.CoreRuntime.Tests;

using System.Reflection;
using System.Text;
using Microsoft.Extensions.Configuration;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Markdown;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Tui.TuiKit;
using TUIKit;
using TUIKit.Content;
using TUIKit.Input;
using TUIKit.Terminal;
using TUIKit.Testing;
using Xunit;

/// <summary>Exercises the single surface clipboard dispatch boundary with deterministic backend failures.</summary>
public static class TuiKitClipboardCopyTests
{
    /// <summary>Admission is based on active capability and complete UTF-8 bytes, never OS assumptions.</summary>
    [Theory]
    [InlineData(true, true, "😀", 4, true, "Sent to terminal clipboard")]
    [InlineData(true, true, "😀x", 4, false, "byte limit")]
    [InlineData(true, true, "", 4, false, "empty")]
    [InlineData(true, false, "x", 4, false, "unavailable")]
    [InlineData(false, true, "x", 4, false, "unavailable")]
    [InlineData(true, true, "malformed", 4, false, "invalid Unicode")]
    public static async Task Copy_AdmitsCompletePayloadOrNothing(bool interactive, bool capability, string text, int limit, bool expected, string notice)
    {
        using var backend = new CopyBackend(interactive, capability);
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], () => { }, backend, limits: new TuiResourceLimits { MaximumClipboardCopyBytes = limit }, deferWorkspace: true);

        if (text == "malformed")
        {
            text = new string((char)0xD800, 1);
        }

        Assert.Equal(expected, Copy(surface, text));
        Assert.Contains(notice, Notice(surface), StringComparison.Ordinal);
        Assert.Equal(expected ? ClipboardWriter.BuildSequence(text) : string.Empty, backend.Output);
        Assert.Equal(expected ? 1 : 0, backend.Flushes);
    }

    /// <summary>Neither a failed write nor a failed flush reports successful dispatch.</summary>
    [Theory]
    [InlineData("write")]
    [InlineData("flush")]
    public static async Task Copy_BackendFailure_DoesNotReportSuccess(string phase)
    {
        using var backend = new CopyBackend(true, true) { FailurePhase = phase };
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], () => { }, backend, deferWorkspace: true);
        Assert.False(Copy(surface, "body\r\n"));
        Assert.Equal("Terminal clipboard write failed", Notice(surface));
        Assert.DoesNotContain("Sent", Notice(surface), StringComparison.Ordinal);
    }

    /// <summary>The surface's own transcript invokes its clipboard callback without a terminal loop or output waits.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public static async Task FencedKeyboardAction_DispatchesThroughSurface(bool capability)
    {
        using var backend = new CopyBackend(true, capability);
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], () => { }, backend, deferWorkspace: true);
        const string source = "```text\r\nexact\t  \r\n```";
        var parsed = new MarkdownParser().Parse(source);
        var field = typeof(TuiKitSurface).GetField("_agentWorkspace", BindingFlags.Instance | BindingFlags.NonPublic);
        var workspace = Assert.IsType<Lazy<AgentViews>>(Assert.IsType<FieldInfo>(field, exactMatch: false).GetValue(surface)).Value;
        workspace.Present(new PresentationBatch([new PresentationMarkdownItem(Assert.IsType<MarkdownDocument>(parsed.Document), source, parsed.SafeSource, true)]));
        var view = workspace.Main.Transcript;
        var cells = new CellBuffer(40, 12);
        view.Render(new BufferSurface(cells));
        Assert.Equal(capability, Snapshot.ToText(cells).Contains("[Copy]", StringComparison.Ordinal));
        view.HandleKey(KeyEvent.Special(KeyCode.Down, KeyModifiers.Ctrl));
        view.HandleKey(KeyEvent.Special(KeyCode.Enter));
        Assert.Equal(capability ? ClipboardWriter.BuildSequence("exact\t  \r\n") : string.Empty, backend.Output);
        Assert.Equal(capability ? 1 : 0, backend.Flushes);
        if (capability)
        {
            Assert.Equal("Sent to terminal clipboard", Notice(surface));
        }
    }

    /// <summary>An ordinary configured theme can explicitly request ASCII fence glyphs.</summary>
    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public static void Theme_LoadsAsciiFencePreference(string value, bool expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["tui:themes:0:id"] = "fence-test",
            ["tui:themes:0:name"] = "Fence test",
            ["tui:themes:0:ui:fencedCodeAscii"] = value,
        }).Build();
        var (catalog, _) = TuiThemeConfigurationLoader.Load(configuration);
        Assert.Empty(catalog.Warnings);
        Assert.True(catalog.TryGet("fence-test", out var theme));
        Assert.Equal(expected, theme!.Ui.FencedCodeAscii);
    }

    private static bool Copy(TuiKitSurface surface, string text)
        => Assert.IsType<bool>(Assert.IsType<MethodInfo>(typeof(TuiKitSurface).GetMethod("Copy", BindingFlags.Instance | BindingFlags.NonPublic), exactMatch: false).Invoke(surface, [text]));

    private static string Notice(TuiKitSurface surface)
        => Assert.IsType<string>(Assert.IsType<FieldInfo>(typeof(TuiKitSurface).GetField("_notice", BindingFlags.Instance | BindingFlags.NonPublic), exactMatch: false).GetValue(surface));

    private sealed class CopyBackend(bool interactive, bool capability) : ITerminalBackend
    {
        private readonly StringBuilder _output = new();

        public string? FailurePhase { get; init; }

        public string Output => _output.ToString();

        public int Flushes { get; private set; }

        public TerminalCapabilities Capabilities { get; } = new(TerminalColorDepth.None, false, false, false, capability, false, false, false, false);

        public Size Size => new(80, 24);

        public bool IsInteractive => interactive;

        public void Start()
        {
        }

        public void Stop()
        {
        }

        public void Dispose()
        {
        }

        public int ReadInput(byte[] buffer, int offset, int count) => 0;

        public void Write(string data)
        {
            if (FailurePhase == "write")
            {
                throw new IOException("Injected clipboard write failure");
            }

            _output.Append(data);
        }

        public void Flush()
        {
            if (FailurePhase == "flush")
            {
                throw new IOException("Injected clipboard flush failure");
            }

            Flushes++;
        }
    }
}
