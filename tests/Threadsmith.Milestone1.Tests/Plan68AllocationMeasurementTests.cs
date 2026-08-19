namespace Threadsmith.Milestone1.Tests;

using System.Globalization;
using System.Text;
using System.Text.Json;
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

        long smallBytes = MeasureFormat(small, 80);
        long typicalBytes = MeasureFormat(typical, 80);
        long boundedLargeBytes = MeasureFormat(boundedLarge, 60);

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
        string small = "Operation completed in 12ms.";
        string typical = "Loaded model gpt-4 with api_key=sk-AbCdEfGhIjKlMnOpQrStUv and token: Bearer abc123.\n"
            + "Connection string: Server=db;Password=hunter2;Integrated Security=true.";
        string boundedLarge = BuildBoundedLargeSanitizerInput();

        long smallBytes = MeasureSanitize(sanitizer, small);
        long typicalBytes = MeasureSanitize(sanitizer, typical);
        long boundedLargeBytes = MeasureSanitize(sanitizer, boundedLarge);

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
        string[] lines = BuildSseLines(64, 240);

        long baselineBytes = MeasureSseBaseline(lines);
        long spanBytes = MeasureSseSpan(lines);

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
        string[] lines = BuildSseLines(64, 240);

        long baselineBytes = MeasureSseFullBaseline(lines);
        long spanBytes = MeasureSseFullSpan(lines);

        Record("SseFullChunk (prep + JsonDocument.Parse)", [
            ("baseline substring+trim + parse (64 lines)", baselineBytes),
            ("span trim+toString + parse (64 lines)", spanBytes),
        ]);
        Assert.True(spanBytes < baselineBytes, "span full-chunk path must allocate less than baseline.");
    }

    private static long MeasureFormat(TuiMarkdownDocument document, int width)
    {
        // Warm up JIT and caches.
        for (int i = 0; i < 64; i++)
        {
            _ = TuiMarkdownLayout.Format(document, width);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        const int iterations = 256;
        for (int i = 0; i < iterations; i++)
        {
            _ = TuiMarkdownLayout.Format(document, width);
        }

        long after = GC.GetAllocatedBytesForCurrentThread();
        return (after - before) / iterations;
    }

    private static long MeasureSanitize(SecretOutputSanitizer sanitizer, string input)
    {
        for (int i = 0; i < 64; i++)
        {
            _ = sanitizer.Sanitize(input);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        const int iterations = 256;
        for (int i = 0; i < iterations; i++)
        {
            _ = sanitizer.Sanitize(input);
        }

        long after = GC.GetAllocatedBytesForCurrentThread();
        return (after - before) / iterations;
    }

    private static long MeasureSseBaseline(string[] lines)
    {
        for (int i = 0; i < 64; i++)
        {
            for (int j = 0; j < lines.Length; j++)
            {
                string payload = lines[j][5..].Trim();
                _ = payload.Length;
            }
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        const int iterations = 256;
        for (int i = 0; i < iterations; i++)
        {
            for (int j = 0; j < lines.Length; j++)
            {
                string payload = lines[j][5..].Trim();
                _ = payload.Length;
            }
        }

        long after = GC.GetAllocatedBytesForCurrentThread();
        return (after - before) / iterations;
    }

    private static long MeasureSseSpan(string[] lines)
    {
        for (int i = 0; i < 64; i++)
        {
            for (int j = 0; j < lines.Length; j++)
            {
                string payload = lines[j].AsSpan(5).Trim().ToString();
                _ = payload.Length;
            }
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        const int iterations = 256;
        for (int i = 0; i < iterations; i++)
        {
            for (int j = 0; j < lines.Length; j++)
            {
                string payload = lines[j].AsSpan(5).Trim().ToString();
                _ = payload.Length;
            }
        }

        long after = GC.GetAllocatedBytesForCurrentThread();
        return (after - before) / iterations;
    }

    private static long MeasureSseFullBaseline(string[] lines)
    {
        for (int i = 0; i < 64; i++)
        {
            for (int j = 0; j < lines.Length; j++)
            {
                string payload = lines[j][5..].Trim();
                using JsonDocument document = JsonDocument.Parse(payload);
                _ = document.RootElement.GetProperty("id").GetString();
            }
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        const int iterations = 128;
        for (int i = 0; i < iterations; i++)
        {
            for (int j = 0; j < lines.Length; j++)
            {
                string payload = lines[j][5..].Trim();
                using JsonDocument document = JsonDocument.Parse(payload);
                _ = document.RootElement.GetProperty("id").GetString();
            }
        }

        long after = GC.GetAllocatedBytesForCurrentThread();
        return (after - before) / iterations;
    }

    private static long MeasureSseFullSpan(string[] lines)
    {
        for (int i = 0; i < 64; i++)
        {
            for (int j = 0; j < lines.Length; j++)
            {
                string payload = lines[j].AsSpan(5).Trim().ToString();
                using JsonDocument document = JsonDocument.Parse(payload);
                _ = document.RootElement.GetProperty("id").GetString();
            }
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        const int iterations = 128;
        for (int i = 0; i < iterations; i++)
        {
            for (int j = 0; j < lines.Length; j++)
            {
                string payload = lines[j].AsSpan(5).Trim().ToString();
                using JsonDocument document = JsonDocument.Parse(payload);
                _ = document.RootElement.GetProperty("id").GetString();
            }
        }

        long after = GC.GetAllocatedBytesForCurrentThread();
        return (after - before) / iterations;
    }

    private static TuiMarkdownDocument BuildSmallDocument()
    {
        return new([new TuiMarkdownParagraph([new TuiMarkdownSpan("A short answer with a few words.")])]);
    }

    private static TuiMarkdownDocument BuildTypicalDocument()
    {
        return new([
                new TuiMarkdownHeading(1, [new TuiMarkdownSpan("Release notes")]),
            new TuiMarkdownParagraph([
                new TuiMarkdownSpan("This release adds "),
                new TuiMarkdownSpan("parallel tools", TuiMarkdownSpanStyle.Strong),
                new TuiMarkdownSpan(" and fixes several sanitizer edge cases."),
            ]),
            new TuiMarkdownList(false, 1, [
                new TuiMarkdownListItem(false, [new TuiMarkdownParagraph([new TuiMarkdownSpan("Concurrent sibling execution.")])]),
                new TuiMarkdownListItem(false, [new TuiMarkdownParagraph([new TuiMarkdownSpan("Stable canonical continuations.")])]),
                new TuiMarkdownListItem(false, [new TuiMarkdownParagraph([new TuiMarkdownSpan("Bounded drain and kill timeouts.")])]),
            ]),
            new TuiMarkdownCodeBlock("Console.WriteLine(\"safe\");\nvar x = 1 + 2;\n", "csharp"),
        ]);
    }

    private static TuiMarkdownDocument BuildBoundedLargeDocument()
    {
        // A long unbroken token (a 1200-char code run) forces many wrap iterations in
        // AppendWrappedSpans, plus a large code block with many lines.
        string longToken = new('x', 1200);
        var paragraph = new TuiMarkdownParagraph([
            new TuiMarkdownSpan("Prefix "),
            new TuiMarkdownSpan(longToken),
            new TuiMarkdownSpan(" suffix with more normal words that wrap across several lines."),
        ]);
        var codeLines = new StringBuilder();
        for (int i = 0; i < 200; i++)
        {
            codeLines.Append("var value").Append(i.ToString(CultureInfo.InvariantCulture)).Append(" = ").Append(i).Append(";\n");
        }

        return new TuiMarkdownDocument([paragraph, new TuiMarkdownCodeBlock(codeLines.ToString(), "csharp")]);
    }

    private static string BuildBoundedLargeSanitizerInput()
    {
        var builder = new StringBuilder();
        for (int i = 0; i < 40; i++)
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
        for (int i = 0; i < count; i++)
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
        foreach ((string name, long bytes) in measurements)
        {
            Console.WriteLine($"  {name}: {bytes} bytes/op");
        }
    }
}