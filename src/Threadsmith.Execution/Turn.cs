namespace Threadsmith.Execution;

using Threadsmith.Core;

/// <summary>Dictionary-backed immutable baseline.</summary>
public sealed class BaselineSnapshot : IBaselineSnapshot
{
    private readonly IReadOnlyDictionary<string, string> _values;

    /// <summary>Initializes a new instance of the <see cref="BaselineSnapshot"/> class.</summary>
    public BaselineSnapshot(long revision = 0, IReadOnlyDictionary<string, string>? values = null)
    {
        Revision = revision;
        _values = values is null ? [] : new Dictionary<string, string>(values);
    }

    /// <inheritdoc />
    public long Revision { get; }

    /// <inheritdoc />
    public string? Get(string key)
    {
        return _values.GetValueOrDefault(key);
    }

    /// <summary>Gets the immutable values for copy-on-write staging.</summary>
    internal IReadOnlyDictionary<string, string> Values => _values;
}

/// <summary>Copy-on-write execution turn.</summary>
public sealed class Turn : ITurn, IStagingView
{
    private readonly Dictionary<string, string> _staging;
    private readonly HashSet<string> _invalidations = [];
    private bool _finished;

    /// <summary>Initializes a new instance of the <see cref="Turn"/> class.</summary>
    public Turn(BaselineSnapshot baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        Baseline = baseline;
        _staging = new Dictionary<string, string>(baseline.Values);
    }

    /// <inheritdoc />
    public IBaselineSnapshot Baseline { get; }

    /// <inheritdoc />
    public IStagingView Staging => this;

    /// <inheritdoc />
    public void Set(string key, string value)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _staging[key] = value;
    }

    /// <inheritdoc />
    public string? Get(string key)
    {
        return _staging.GetValueOrDefault(key);
    }

    /// <inheritdoc />
    public void Invalidate(string key)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _invalidations.Add(key);
    }

    /// <inheritdoc />
    public Task<IBaselineSnapshot> CommitAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_finished, this);
        foreach (var key in _invalidations)
        {
            _staging.Remove(key);
        }

        _finished = true;
        return Task.FromResult<IBaselineSnapshot>(new BaselineSnapshot(Baseline.Revision + 1, _staging));
    }

    /// <inheritdoc />
    public Task CancelAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _finished = true;
        _staging.Clear();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _finished = true;
        _staging.Clear();
        return ValueTask.CompletedTask;
    }
}
