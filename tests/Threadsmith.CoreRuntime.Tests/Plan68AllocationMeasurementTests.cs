namespace Threadsmith.CoreRuntime.Tests;

using System.Globalization;
using System.Text;
using System.Text.Json;
using Threadsmith.Interaction.Markdown;
using Threadsmith.Telemetry;
using Threadsmith.Tui;
using Xunit;

/// <summary>
/// Plan-68 evidence harness: measures warm steady-state allocations for representative small,
/// typical, and bounded-large inputs across the named AR-05 candidate paths (TUI Markdown layout,
/// secret-output sanitization, and SSE payload preparation). Results are written to a temp file so
/// the implementing agent can record reproducible baselines and compare before/after dispositions.
/// This is NOT a wall-clock CI gate; it reports allocation bytes per operation over repeated samples.
/// </summary>
public sealed class Plan68AllocationMeasurementTests
{
    /// <summary>Measures TuiMarkdownLayout.Format allocations across representative document sizes.</summary>
    [Fact]
    public void Measure_TuiMarkdownLayout_Format_Allocations()
    {
        var small = BuildSmallDocument();
        var typical = BuildTypicalDocument();
        var boundedLarge = BuildBoundedLargeDocument();

        var smallBytes = MeasureFormat(small, 80);
        var typicalBytes = MeasureFormat(typical, 80);
        var boundedLargeBytes = MeasureFormat(boundedLarge, 60);

        Record("TuiMarkdownLayout.Format", [
            ("small", smallBytes),
            ("typical", typicalBytes),
            ("bounded-large", boundedLargeBytes),
        ]);
        Assert.True(boundedLargeBytes < 600_000, $"bounded-large layout allocations regressed: {boundedLargeBytes} bytes/op");
    }

    /// <summary>Measures SecretOutputSanitizer.Sanitize allocations across representative input sizes.</summary>
    [Fact]
    public void Measure_SecretOutputSanitizer_Sanitize_Allocations()
    {
        var sanitizer = new SecretOutputSanitizer();
        var small = "Operation completed in 12ms.";
        var typical = "Loaded model gpt-4 with api_key=sk-AbCdEfGhIjKlMnOpQrStUv and token: Bearer abc123.\n"
            + "Connection string: Server=db;Password=hunter2;Integrated Security=true.";
        var boundedLarge = BuildBoundedLargeSanitizerInput();

        var smallBytes = MeasureSanitize(sanitizer, small);
        var typicalBytes = MeasureSanitize(sanitizer, typical);
        var boundedLargeBytes = MeasureSanitize(sanitizer, boundedLarge);

        Record("SecretOutputSanitizer.Sanitize", [
            ("small", smallBytes),
            ("typical", typicalBytes),
            ("bounded-large", boundedLargeBytes),
        ]);
        Assert.True(boundedLargeBytes < 120_000, $"bounded-large sanitizer allocations regressed: {boundedLargeBytes} bytes/op");
    }

    /// <summary>Measures SSE payload-preparation allocations: baseline substring+trim vs span-trim+toString.</summary>
    [Fact]
    public void Measure_SsePayloadPrep_Allocations()
    {
        var lines = BuildSseLines(64, 240);

        var baselineBytes = MeasureSseBaseline(lines);
        var spanBytes = MeasureSseSpan(lines);

        Record("SsePayloadPrep", [
            ("baseline substring+trim (64 lines)", baselineBytes),
            ("span trim+toString (64 lines)", spanBytes),
        ]);
        Assert.True(spanBytes < baselineBytes, "span payload prep must allocate less than baseline substring+trim.");
    }

    /// <summary>Measures the full SSE chunk path (payload prep + JSON parse) to test payload-prep materiality.</summary>
    [Fact]
    public void Measure_SseFullChunk_Allocations()
    {
        var lines = BuildSseLines(64, 240);

        var baselineBytes = MeasureSseFullBaseline(lines);
        var spanBytes = MeasureSseFullSpan(lines);

        Record("SseFullChunk (prep + JsonDocument.Parse)", [
            ("baseline substring+trim + parse (64 lines)", baselineBytes),
            ("span trim+toString + parse (64 lines)", spanBytes),
        ]);
        Assert.True(spanBytes < baselineBytes, "span full-chunk path must allocate less than baseline.");
    }

    private static long MeasureFormat(MarkdownDocument document, int width)
    {
        // Warm up JIT and caches.
        for (var i = 0; i < 64; i++)
        {
            _ = TuiMarkdownLayout.Format(document, width);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        const int iterations = 256;
        for (var i = 0; i < iterations; i++)
        {
            _ = TuiMarkdownLayout.Format(document, width);
        }

        var after = GC.GetAllocatedBytesForCurrentThread();
        return (after - before) / iterations;
    }

    private static long MeasureSanitize(SecretOutputSanitizer sanitizer, string input)
    {
        for (var i = 0; i < 64; i++)
        {
            _ = sanitizer.Sanitize(input);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        const int iterations = 256;
        for (var i = 0; i < iterations; i++)
        {
            _ = sanitizer.Sanitize(input);
        }

        var after = GC.GetAllocatedBytesForCurrentThread();
        return (after - before) / iterations;
    }

    private static long MeasureSseBaseline(string[] lines)
    {
        for (var i = 0; i < 64; i++)
        {
            for (var j = 0; j < lines.Length; j++)
            {
                var payload = lines[j][5..].Trim();
                _ = payload.Length;
            }
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        const int iterations = 256;
        for (var i = 0; i < iterations; i++)
        {
            for (var j = 0; j < lines.Length; j++)
            {
                var payload = lines[j][5..].Trim();
                _ = payload.Length;
            }
        }

        var after = GC.GetAllocatedBytesForCurrentThread();
        return (after - before) / iterations;
    }

    private static long MeasureSseSpan(string[] lines)
    {
        for (var i = 0; i < 64; i++)
        {
            for (var j = 0; j < lines.Length; j++)
            {
                var payload = lines[j].AsSpan(5).Trim().ToString();
                _ = payload.Length;
            }
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        const int iterations = 256;
        for (var i = 0; i < iterations; i++)
        {
            for (var j = 0; j < lines.Length; j++)
            {
                var payload = lines[j].AsSpan(5).Trim().ToString();
                _ = payload.Length;
            }
        }

        var after = GC.GetAllocatedBytesForCurrentThread();
        return (after - before) / iterations;
    }

    private static long MeasureSseFullBaseline(string[] lines)
    {
        for (var i = 0; i < 64; i++)
        {
            for (var j = 0; j < lines.Length; j++)
            {
                var payload = lines[j][5..].Trim();
                using var document = JsonDocument.Parse(payload);
                _ = document.RootElement.GetProperty("id").GetString();
            }
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        const int iterations = 128;
        for (var i = 0; i < iterations; i++)
        {
            for (var j = 0; j < lines.Length; j++)
            {
                var payload = lines[j][5..].Trim();
                using var document = JsonDocument.Parse(payload);
                _ = document.RootElement.GetProperty("id").GetString();
            }
        }

        var after = GC.GetAllocatedBytesForCurrentThread();
        return (after - before) / iterations;
    }

    private static long MeasureSseFullSpan(string[] lines)
    {
        for (var i = 0; i < 64; i++)
        {
            for (var j = 0; j < lines.Length; j++)
            {
                var payload = lines[j].AsSpan(5).Trim().ToString();
                using var document = JsonDocument.Parse(payload);
                _ = document.RootElement.GetProperty("id").GetString();
            }
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        const int iterations = 128;
        for (var i = 0; i < iterations; i++)
        {
            for (var j = 0; j < lines.Length; j++)
            {
                var payload = lines[j].AsSpan(5).Trim().ToString();
                using var document = JsonDocument.Parse(payload);
                _ = document.RootElement.GetProperty("id").GetString();
            }
        }

        var after = GC.GetAllocatedBytesForCurrentThread();
        return (after - before) / iterations;
    }

    private static MarkdownDocument BuildSmallDocument()
    {
        return new([new MarkdownParagraph([new MarkdownSpan("A short answer with a few words.")])]);
    }

    private static MarkdownDocument BuildTypicalDocument()
    {
        return new([
                new MarkdownHeading(1, [new MarkdownSpan("Release notes")]),
            new MarkdownParagraph([
                new MarkdownSpan("This release adds "),
                new MarkdownSpan("parallel tools", MarkdownSpanStyle.Strong),
                new MarkdownSpan(" and fixes several sanitizer edge cases."),
            ]),
            new MarkdownList(false, 1, [
                new MarkdownListItem(false, [new MarkdownParagraph([new MarkdownSpan("Concurrent sibling execution.")])]),
                new MarkdownListItem(false, [new MarkdownParagraph([new MarkdownSpan("Stable canonical continuations.")])]),
                new MarkdownListItem(false, [new MarkdownParagraph([new MarkdownSpan("Bounded drain and kill timeouts.")])]),
            ]),
            new MarkdownCodeBlock("Console.WriteLine(\"safe\");\nvar x = 1 + 2;\n", "csharp"),
        ]);
    }

    private static MarkdownDocument BuildBoundedLargeDocument()
    {
        // A long unbroken token (a 1200-char code run) forces many wrap iterations in
        // AppendWrappedSpans, plus a large code block with many lines.
        string longToken = new('x', 1200);
        var paragraph = new MarkdownParagraph([
            new MarkdownSpan("Prefix "),
            new MarkdownSpan(longToken),
            new MarkdownSpan(" suffix with more normal words that wrap across several lines."),
        ]);
        var codeLines = new StringBuilder();
        for (var i = 0; i < 200; i++)
        {
            codeLines.Append("var value").Append(i.ToString(CultureInfo.InvariantCulture)).Append(" = ").Append(i).Append(";\n");
        }

        return new MarkdownDocument([paragraph, new MarkdownCodeBlock(codeLines.ToString(), "csharp")]);
    }

    private static string BuildBoundedLargeSanitizerInput()
    {
        var builder = new StringBuilder();
        for (var i = 0; i < 40; i++)
        {
            builder.Append("Line ").Append(i).Append(": api_key=sk-AbCdEfGhIjKlMnOpQrStUv ");
            builder.Append("token: Bearer abc123def456; password=hunter2; ");
            builder.Append("https://user:secretpass@example.com/path; \u0007control\u0007\n");
        }

        return builder.ToString();
    }

    private static string[] BuildSseLines(int count, int payloadLength)
    {
        var lines = new string[count];
        string payloadBody = new('a', Math.Max(0, payloadLength - 20));
        for (var i = 0; i < count; i++)
        {
            // Mimic OpenAI-compatible SSE: "data: { ... }\n" with leading spaces that require trimming.
            lines[i] = "data:   {\"id\":\"chunk-" + i.ToString(CultureInfo.InvariantCulture) + "\",\"payload\":\"" + payloadBody + "\"}";
        }

        return lines;
    }

    private static void Record(string label, (string Name, long Bytes)[] measurements)
    {
        Console.WriteLine(new string('=', 70));
        Console.WriteLine(label);
        foreach ((var name, var bytes) in measurements)
        {
            Console.WriteLine($"  {name}: {bytes} bytes/op");
        }
    }
}
