namespace Threadsmith.ModelTooling.Tests;

using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Models;
using Xunit;

/// <summary>Verifies archival preserves raw diagnostics across session boundaries.</summary>
public sealed class JsonlModelExchangeLogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"threadsmith-log-rotation-{Guid.NewGuid():N}");

    /// <summary>An unused log does not produce empty archives.</summary>
    [Fact]
    public async Task RotateAsync_MissingFile_DoesNotCreateLogOrArchive()
    {
        var log = new JsonlModelExchangeLog(Path.Combine(_directory, "raw.jsonl"));

        await log.RotateAsync();

        Assert.Empty(Directory.EnumerateFiles(_directory));
    }

    /// <summary>Repeated boundaries preserve prior contents and the requested active filename.</summary>
    [Theory]
    [InlineData("raw.jsonl", "raw_0.jsonl", "raw_1.jsonl")]
    [InlineData("raw-session_5.jsonl", "raw-session_5_0.jsonl", "raw-session_5_1.jsonl")]
    [InlineData("raw.log", "raw_0.log", "raw_1.log")]
    [InlineData("raw", "raw_0", "raw_1")]
    public async Task RotateAsync_RepeatedRotation_PreservesContentsAndOriginalWritePath(
        string name,
        string firstArchive,
        string secondArchive)
    {
        var path = Path.Combine(_directory, name);
        var log = new JsonlModelExchangeLog(path);
        await File.WriteAllTextAsync(path, "original\n");

        await log.RotateAsync();
        Assert.Equal("original\n", await File.ReadAllTextAsync(Path.Combine(_directory, firstArchive)));
        Assert.False(File.Exists(path));

        await log.AppendCompletionAsync(RunId.New(), 0, 1);
        var secondContents = await File.ReadAllTextAsync(path);
        await log.RotateAsync();
        await log.AppendCompletionAsync(RunId.New(), 1, 2);

        Assert.Equal("original\n", await File.ReadAllTextAsync(Path.Combine(_directory, firstArchive)));
        Assert.Equal(secondContents, await File.ReadAllTextAsync(Path.Combine(_directory, secondArchive)));
        using var current = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal(1, current.RootElement.GetProperty("ToolContinuationRound").GetInt32());
    }

    /// <summary>Suffix selection is numeric, unbounded, and specific to the active log.</summary>
    [Theory]
    [InlineData("10", "11")]
    [InlineData("92233720368547758081234567890", "92233720368547758081234567891")]
    public async Task RotateAsync_UsesHighestNumericSuffixForSameLog(string highest, string expected)
    {
        var path = Path.Combine(_directory, "raw.jsonl");
        var log = new JsonlModelExchangeLog(path);
        string[] existingNames = [
            "raw_0.jsonl", "raw_9.jsonl", $"raw_{highest}.jsonl", "other_999.jsonl",
            "raw_other_999.jsonl", "raw_-1.jsonl", "raw_12x.jsonl", "raw_500.txt",
        ];
        foreach (var name in existingNames)
        {
            await File.WriteAllTextAsync(Path.Combine(_directory, name), name);
        }

        await File.WriteAllTextAsync(path, "new archive");
        await log.RotateAsync();

        Assert.Equal("new archive", await File.ReadAllTextAsync(Path.Combine(_directory, $"raw_{expected}.jsonl")));
        foreach (var name in existingNames)
        {
            Assert.Equal(name, await File.ReadAllTextAsync(Path.Combine(_directory, name)));
        }
    }

    /// <summary>Cancellation preserves the source and permits later writes.</summary>
    [Fact]
    public async Task RotateAsync_Canceled_DoesNotMoveFileOrBlockSubsequentWrites()
    {
        var path = Path.Combine(_directory, "raw.jsonl");
        var log = new JsonlModelExchangeLog(path);
        await File.WriteAllTextAsync(path, "original\n");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => log.RotateAsync(cancellation.Token));

        Assert.Equal("original\n", await File.ReadAllTextAsync(path));
        Assert.False(File.Exists(Path.Combine(_directory, "raw_0.jsonl")));
        await log.AppendCompletionAsync(RunId.New(), 0, 1);
        Assert.Equal(2, (await File.ReadAllLinesAsync(path)).Length);
    }

    /// <summary>Host destination rejection preserves the source and releases the append gate.</summary>
    [Fact]
    public async Task RotateAsync_HostRejectsArchive_PreservesOriginalLogAndReleasesGate()
    {
        var path = Path.Combine(_directory, "raw.jsonl");
        var validatedPaths = new List<string>();
        using var cancellation = new CancellationTokenSource();
        var log = new JsonlModelExchangeLog(path, (candidate, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            validatedPaths.Add(candidate);
            return Task.FromException(new InvalidOperationException("Archive destination is not ignored."));
        });
        await File.WriteAllTextAsync(path, "original\n");

        await Assert.ThrowsAsync<InvalidOperationException>(() => log.RotateAsync(cancellation.Token));

        Assert.Equal(Path.Combine(_directory, "raw_0.jsonl"), Assert.Single(validatedPaths));
        Assert.Equal("original\n", await File.ReadAllTextAsync(path));
        Assert.False(File.Exists(Path.Combine(_directory, "raw_0.jsonl")));
        await log.AppendCompletionAsync(RunId.New(), 0, 1);
        Assert.Equal(2, (await File.ReadAllLinesAsync(path)).Length);
    }

    /// <summary>Concurrent appends and rotation retain every complete diagnostic entry.</summary>
    [Fact]
    public async Task RotateAsync_ConcurrentWritesAndRotations_PreserveEveryCompleteEntry()
    {
        var log = new JsonlModelExchangeLog(Path.Combine(_directory, "raw.jsonl"));
        using var start = new SemaphoreSlim(0);
        var writes = Enumerable.Range(0, 30).Select(async sequence =>
        {
            await start.WaitAsync();
            await log.AppendCompletionAsync(RunId.New(), 0, sequence);
        }).ToArray();
        var rotations = Enumerable.Range(0, 10).Select(async _ =>
        {
            await start.WaitAsync();
            await log.RotateAsync();
        }).ToArray();

        start.Release(writes.Length + rotations.Length);
        await Task.WhenAll(writes.Concat(rotations));

        var sequences = new List<int>();
        foreach (var path in Directory.EnumerateFiles(_directory))
        {
            foreach (var line in await File.ReadAllLinesAsync(path))
            {
                using var document = JsonDocument.Parse(line);
                sequences.Add(document.RootElement.GetProperty("Sequence").GetInt32());
            }
        }

        Assert.Equal(Enumerable.Range(0, 30), sequences.Order());
    }

    /// <summary>An occupied archive destination is skipped without overwriting it.</summary>
    [Fact]
    public async Task RotateAsync_ArchiveNameOccupiedByDirectory_UsesNextAvailableSuffix()
    {
        var path = Path.Combine(_directory, "raw.jsonl");
        var log = new JsonlModelExchangeLog(path);
        Directory.CreateDirectory(Path.Combine(_directory, "raw_0.jsonl"));
        await File.WriteAllTextAsync(path, "original");

        await log.RotateAsync();

        Assert.True(Directory.Exists(Path.Combine(_directory, "raw_0.jsonl")));
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(_directory, "raw_1.jsonl")));
    }

    /// <summary>Removes the isolated test log directory.</summary>
    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
