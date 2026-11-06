namespace Threadsmith.ModelTooling.Tests;

using System.Collections.Frozen;
using System.Diagnostics;
using System.Globalization;
using Xunit;

/// <summary>Reports the isolated cost of membership snapshot construction and comparison.</summary>
public static class WatcherTopologyMeasurementTests
{
    /// <summary>Compares privately owned sets with the former frozen-set conversion.</summary>
    [Fact(Explicit = true)]
    [Trait("Category", "Performance")]
    public static void MeasureMembershipSnapshotConstructionAndComparison()
    {
        var output = TestContext.Current.TestOutputHelper!;
        output.WriteLine("Synthetic membership-only comparison; excludes directory enumeration, watchers and binding. Four snapshots and two equality checks per sample; nine samples after warmup.");
        foreach (var count in new[] { 1000, 20000 })
        {
            var paths = Enumerable.Range(0, count).Select(index => $"C:/repository/src/Project{index / 100}/File{index}.cs").ToArray();
            foreach (var comparer in new[] { StringComparer.Ordinal, StringComparer.OrdinalIgnoreCase })
            {
                var frozenTimes = new List<double>();
                var ownedTimes = new List<double>();
                var frozenBytes = new List<long>();
                var ownedBytes = new List<long>();
                for (var sample = 0; sample < 10; sample++)
                {
                    for (var pass = 0; pass < 2; pass++)
                    {
                        var freeze = (sample + pass) % 2 == 0;
                        var before = GC.GetAllocatedBytesForCurrentThread();
                        var start = Stopwatch.GetTimestamp();
                        var snapshots = new IReadOnlySet<string>[4];
                        for (var index = 0; index < snapshots.Length; index++)
                        {
                            var entries = new HashSet<string>(comparer);
                            foreach (var path in paths)
                            {
                                entries.Add(path);
                            }

                            snapshots[index] = freeze ? entries.ToFrozenSet(comparer) : entries;
                        }

                        var equal = snapshots[0].SetEquals(snapshots[1]) && snapshots[2].SetEquals(snapshots[3]);
                        var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                        Assert.True(equal);
                        if (sample > 0)
                        {
                            (freeze ? frozenTimes : ownedTimes).Add(elapsed);
                            (freeze ? frozenBytes : ownedBytes).Add(allocated);
                        }
                    }
                }

                frozenTimes.Sort();
                ownedTimes.Sort();
                frozenBytes.Sort();
                ownedBytes.Sort();
                output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"entries={count}; comparer={comparer}; frozen median={frozenTimes[4]:F3}ms range={frozenTimes[0]:F3}..{frozenTimes[^1]:F3}, bytes={frozenBytes[4]}; owned median={ownedTimes[4]:F3}ms range={ownedTimes[0]:F3}..{ownedTimes[^1]:F3}, bytes={ownedBytes[4]}"));
            }
        }
    }
}
