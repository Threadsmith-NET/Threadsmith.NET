namespace Threadsmith.Mutations.Tests;

using System.Diagnostics.Metrics;

internal sealed class TestRecoveryByteMeasurements : IDisposable
{
    private readonly AsyncLocal<bool> _scope = new() { Value = true };
    private readonly MeterListener _listener = new();
    private long _first;
    private long _refresh;

    public TestRecoveryByteMeasurements()
    {
        _listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Name == "threadsmith.workspace.recovery.bytes")
            {
                owner.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            if (!_scope.Value)
            {
                return;
            }

            foreach (var tag in tags)
            {
                if (tag.Key == "materialization")
                {
                    if (Equals(tag.Value, "first"))
                    {
                        Interlocked.Add(ref _first, value);
                    }
                    else if (Equals(tag.Value, "refresh"))
                    {
                        Interlocked.Add(ref _refresh, value);
                    }
                }
            }
        });
        _listener.Start();
    }

    public long First => Interlocked.Read(ref _first);

    public long Refresh => Interlocked.Read(ref _refresh);

    public void Dispose()
    {
        _scope.Value = false;
        _listener.Dispose();
    }
}
