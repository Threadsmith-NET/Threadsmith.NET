using Xunit;

// Roslyn 5.6 uses one process-wide in-memory SQLite write cache on non-Windows,
// with synchronization scoped to each workspace. Independent workspace fixtures
// must not overlap; concurrency explicitly exercised inside a test is preserved.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
