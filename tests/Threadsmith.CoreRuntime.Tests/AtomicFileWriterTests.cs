namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Core;
using Xunit;

/// <summary>Checks publication ordering, preservation, and sibling temporary-file cleanup.</summary>
public sealed class AtomicFileWriterTests
{
    /// <summary>Validation runs before creation, after creation, and after the complete temporary file closes.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WritePublishesCompleteContent(bool existing)
    {
        using var temporary = new TemporaryDirectory();
        var parent = Path.Combine(temporary.Path, "new");
        var destination = Path.Combine(parent, "catalog.json");
        var token = TestContext.Current.CancellationToken;
        var bytes = "replacement"u8.ToArray();
        if (existing)
        {
            Directory.CreateDirectory(parent);
            await File.WriteAllTextAsync(destination, "previous", token);
        }

        var validations = 0;
        void Validate()
        {
            validations++;
            Assert.Equal(existing || validations > 1, Directory.Exists(parent));
            if (validations == 3)
            {
                var pending = Assert.Single(Directory.GetFiles(parent, "*.tmp"));
                // Reading with exclusive sharing also proves the writer has closed its stream.
                using var stream = new FileStream(pending, FileMode.Open, FileAccess.Read, FileShare.None);
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                Assert.Equal(bytes, memory.ToArray());
                Assert.Equal(existing, File.Exists(destination));
            }
        }

        await AtomicFileWriter.WriteAsync(destination, bytes, overwrite: true, Validate, token);

        Assert.Equal(3, validations);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(destination, token));
        Assert.Empty(Directory.GetFiles(parent, "*.tmp"));
    }

    /// <summary>Each validation failure preserves existing bytes and cleans any temporary file.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ValidationFailurePreservesPreviousContent(int failureAt)
    {
        using var temporary = new TemporaryDirectory();
        var destination = Path.Combine(temporary.Path, "catalog.json");
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(destination, "previous", token);
        var validations = 0;
        var failure = new IOException("Controlled validation failure.");

        void Validate()
        {
            if (++validations == failureAt)
            {
                throw failure;
            }
        }

        var observed = await Assert.ThrowsAsync<IOException>(() => AtomicFileWriter.WriteAsync(
            destination, "replacement"u8.ToArray(), overwrite: true, Validate, token));

        Assert.Same(failure, observed);
        Assert.Equal(failureAt, validations);
        Assert.Equal("previous", await File.ReadAllTextAsync(destination, token));
        Assert.Empty(Directory.GetFiles(temporary.Path, "*.tmp"));
    }

    /// <summary>Initial path rejection does not create the destination's parent.</summary>
    [Fact]
    public async Task InitialValidationFailureDoesNotCreateDirectory()
    {
        using var temporary = new TemporaryDirectory();
        var parent = Path.Combine(temporary.Path, "absent");

        await Assert.ThrowsAsync<IOException>(() => AtomicFileWriter.WriteAsync(
            Path.Combine(parent, "catalog.json"),
            "replacement"u8.ToArray(),
            overwrite: true,
            static () => throw new IOException("Rejected path."),
            TestContext.Current.CancellationToken));

        Assert.False(Directory.Exists(parent));
    }

    /// <summary>Cancellation before writing or after directory creation leaves the previous catalog intact.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellationPreservesPreviousContent(bool initiallyCanceled)
    {
        using var temporary = new TemporaryDirectory();
        var destination = Path.Combine(temporary.Path, "catalog.json");
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(destination, "previous", token);
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (initiallyCanceled)
        {
            await canceled.CancelAsync();
        }

        var validations = 0;
        void Validate()
        {
            if (++validations == 2)
            {
                canceled.Cancel();
            }
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AtomicFileWriter.WriteAsync(
            destination, "replacement"u8.ToArray(), overwrite: true, Validate, canceled.Token));

        Assert.Equal(2, validations);
        Assert.Equal("previous", await File.ReadAllTextAsync(destination, token));
        Assert.Empty(Directory.GetFiles(temporary.Path, "*.tmp"));
    }

    /// <summary>A refused replacement removes the fully written temporary file.</summary>
    [Fact]
    public async Task ReplacementFailureCleansTemporaryFile()
    {
        using var temporary = new TemporaryDirectory();
        var destination = Path.Combine(temporary.Path, "catalog.json");
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(destination, "previous", token);
        var validations = 0;

        await Assert.ThrowsAsync<IOException>(() => AtomicFileWriter.WriteAsync(
            destination, "replacement"u8.ToArray(), overwrite: false, () => validations++, token));

        Assert.Equal(3, validations);
        Assert.Equal("previous", await File.ReadAllTextAsync(destination, token));
        Assert.Empty(Directory.GetFiles(temporary.Path, "*.tmp"));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(AppContext.BaseDirectory, "atomic-writer-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
